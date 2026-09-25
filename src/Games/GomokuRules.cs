using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace DeskArcade.Games;

/// <summary>
/// Gomoku rules, free of UI: freestyle, on the 15×15 intersections. Black (+1) moves first; a move is [point]. Five
/// or more stones in a row (across, down or diagonal) win; a full board is a draw.
/// </summary>
public sealed class GomokuRules : IBoardRules, ILeveledRules
{
    public const int N = 15, Need = 5;
    /// <summary>The computer player stops searching after this long, to keep the overlay responsive.</summary>
    public const int ThinkMs = 100;

    static readonly (int Dr, int Dc)[] Dirs = { (0, 1), (1, 0), (1, 1), (1, -1) };

    // what one line through a point is worth to the side that plays there (see Shape)
    const int Five = 1_000_000, OpenFour = 100_000, Four = 12_000, OpenThree = 10_000, Three = 1_000, OpenTwo = 400, Two = 60, One = 8;

    public sbyte[] Board { get; } = new sbyte[N * N];
    public int Turn { get; private set; } = 1;
    public int Ply { get; private set; }
    public int Alert => -1;
    /// <summary>The side with five in a row, or 0.</summary>
    public int Winner { get; private set; }
    /// <summary>The stones of the run that won, from one end to the other; null while nobody has won.</summary>
    public int[]? WinLine { get; private set; }

    public static int Sq(int row, int col) => row * N + col;

    public int Result => Winner != 0 ? Winner : Ply >= Board.Length ? 2 : 0;

    public int Count(int side) => Board.Count(p => p == side);

    public List<int[]> LegalMoves()
    {
        var moves = new List<int[]>();
        if (Result != 0) return moves;
        for (int sq = 0; sq < Board.Length; sq++)
            if (Board[sq] == 0) moves.Add(new[] { sq });
        return moves;
    }

    public List<int> Apply(int[] move)
    {
        int sq = move[0];
        Board[sq] = (sbyte)Turn;
        if (RunThrough(Board, sq) is { } run)
        {
            Winner = Turn;
            WinLine = run;
        }
        Turn = -Turn;
        Ply++;
        return new List<int>();
    }

    /// <summary>The longest run of five or more through <paramref name="sq"/> in any direction, or null.</summary>
    public static int[]? RunThrough(sbyte[] board, int sq)
    {
        int side = board[sq];
        if (side == 0) return null;
        foreach (var (dr, dc) in Dirs)
        {
            var run = new List<int> { sq };
            for (int sign = -1; sign <= 1; sign += 2)
            {
                int r = sq / N + dr * sign, c = sq % N + dc * sign;
                while (r >= 0 && r < N && c >= 0 && c < N && board[r * N + c] == side)
                {
                    if (sign < 0) run.Insert(0, r * N + c);
                    else run.Add(r * N + c);
                    r += dr * sign;
                    c += dc * sign;
                }
            }
            if (run.Count >= Need) return run.ToArray();
        }
        return null;
    }

    public string Encode()
    {
        var sb = new StringBuilder(Board.Length + 12);
        foreach (sbyte p in Board) sb.Append(p > 0 ? 'x' : p < 0 ? 'o' : '.');
        return sb.Append(Turn > 0 ? "|x|" : "|o|").Append(Ply).ToString();
    }

    public static GomokuRules? Decode(string text)
    {
        var f = text.Split('|');
        if (f.Length != 3 || f[0].Length != N * N || !int.TryParse(f[2], out int ply) || ply < 0) return null;
        var d = new GomokuRules { Turn = f[1] == "o" ? -1 : 1, Ply = ply };
        for (int i = 0; i < d.Board.Length; i++) d.Board[i] = (sbyte)(f[0][i] == 'x' ? 1 : f[0][i] == 'o' ? -1 : 0);
        for (int sq = 0; sq < d.Board.Length && d.Winner == 0; sq++)
            if (RunThrough(d.Board, sq) is { } run)
            {
                d.Winner = d.Board[sq];
                d.WinLine = run;
            }
        return d;
    }

    // ------------------------------------------------------------------ computer player

    public int[] BestMove(Random rng) => BestMove(rng, 1);

    /// <summary>
    /// The CPU's move. <paramref name="depth"/> 0 (Easy) extends its own lines, or plays at random next to the stones
    /// already down; 1 (Medium) scores every point for the threats it makes and the threats it blocks (fours and open
    /// threes above all) and plays the best; 2 and more (Hard, Expert) also look that many moves ahead among the ten
    /// best-scored points, for <see cref="ThinkMs"/> at most.
    /// </summary>
    public int[] BestMove(Random rng, int depth)
    {
        if (Result != 0) return Array.Empty<int>();
        var near = Candidates(Board);
        if (near.Count == 0) return new[] { Sq(N / 2, N / 2) };
        if (depth <= 0)
        {
            if (rng.NextDouble() < 0.5) return new[] { near[rng.Next(near.Count)] };
            return new[] { near.OrderByDescending(sq => Attack(Board, sq, Turn)).ThenBy(_ => rng.Next()).First() };
        }
        var ranked = Ranked(Board, Turn, near, rng);
        if (depth == 1) return new[] { ranked[0] };
        // a five now, or else the point that stops theirs, needs no search
        foreach (int side in new[] { Turn, -Turn })
            if (ranked.FirstOrDefault(sq => Attack(Board, sq, side) >= Five, -1) is int must and >= 0) return new[] { must };

        var clock = Stopwatch.StartNew();
        int best = ranked[0];
        var root = ranked.Take(10).ToList();
        for (int d = 2; d <= depth; d++)
        {
            int pick = root[0];
            double alpha = double.NegativeInfinity;
            bool cut = false;
            foreach (int sq in root)
            {
                Board[sq] = (sbyte)Turn;
                double score = RunThrough(Board, sq) != null ? Five : -Negamax(-Turn, d - 1, double.NegativeInfinity, -alpha, clock);
                Board[sq] = 0;
                if (clock.ElapsedMilliseconds > ThinkMs)
                {
                    cut = true;
                    break;
                }
                if (score > alpha)
                {
                    alpha = score;
                    pick = sq;
                }
            }
            if (cut) break;
            best = pick;
            if (alpha >= Five) break;
        }
        return new[] { best };
    }

    /// <summary>The value of the board for <paramref name="side"/>, to move, looking among its best ten points.</summary>
    double Negamax(int side, int depth, double alpha, double beta, Stopwatch clock)
    {
        var near = Candidates(Board);
        if (near.Count == 0) return 0;
        if (depth <= 0 || clock.ElapsedMilliseconds > ThinkMs)
        {
            double value = Evaluate(Board, side, out bool mustBlock);
            // with no four of theirs to answer, a point that makes an open four is a win in two
            if (Math.Abs(value) < Five && !mustBlock && near.Any(sq => Attack(Board, sq, side) >= OpenFour)) return Five / 2;
            return value;
        }
        var ranked = Ranked(Board, side, near, null);
        foreach (int sq in ranked.Take(10))
        {
            Board[sq] = (sbyte)side;
            double score = RunThrough(Board, sq) != null ? Five : -Negamax(-side, depth - 1, -beta, -alpha, clock);
            Board[sq] = 0;
            if (score > alpha) alpha = score;
            if (alpha >= beta) break;
        }
        return alpha;
    }

    static readonly int[] WindowWorth = { 0, 1, 10, 100, 1000 };

    /// <summary>
    /// The board for <paramref name="side"/>, to move: every five-point window along every line that holds stones of
    /// one side only counts for that side, more the fuller it is. A window of four is a five next move: ours wins at
    /// once, and two of theirs on different points can't both be blocked; <paramref name="mustBlock"/> says they have
    /// one to answer.
    /// </summary>
    public static double Evaluate(sbyte[] board, int side, out bool mustBlock)
    {
        double mine = 0, theirs = 0;
        int theirFive = -1;
        bool theirTwo = false;
        for (int sq = 0; sq < board.Length; sq++)
        {
            int r0 = sq / N, c0 = sq % N;
            foreach (var (dr, dc) in Dirs)
            {
                int r4 = r0 + dr * (Need - 1), c4 = c0 + dc * (Need - 1);
                if (r4 < 0 || r4 >= N || c4 < 0 || c4 >= N) continue;
                int own = 0, other = 0, gap = -1;
                for (int k = 0; k < Need; k++)
                {
                    int at = (r0 + dr * k) * N + c0 + dc * k, p = board[at] * side;
                    if (p > 0) own++;
                    else if (p < 0) other++;
                    else gap = at;
                }
                if (own > 0 && other > 0) continue;
                if (own == Need - 1)
                {
                    mustBlock = false;
                    return Five;
                }
                if (other == Need - 1)
                {
                    if (theirFive >= 0 && theirFive != gap) theirTwo = true;
                    theirFive = gap;
                }
                mine += WindowWorth[Math.Min(own, Need - 1)];
                theirs += WindowWorth[Math.Min(other, Need - 1)];
            }
        }
        mustBlock = theirFive >= 0;
        return theirTwo ? -Five : mine - theirs;
    }

    /// <summary>The points within two of a stone: the only ones worth thinking about.</summary>
    static List<int> Candidates(sbyte[] board)
    {
        var list = new List<int>();
        for (int sq = 0; sq < board.Length; sq++)
        {
            if (board[sq] != 0) continue;
            int r0 = sq / N, c0 = sq % N;
            bool close = false;
            for (int r = Math.Max(0, r0 - 2); r <= Math.Min(N - 1, r0 + 2) && !close; r++)
                for (int c = Math.Max(0, c0 - 2); c <= Math.Min(N - 1, c0 + 2) && !close; c++)
                    close = board[r * N + c] != 0;
            if (close) list.Add(sq);
        }
        return list;
    }

    /// <summary>The points best first: what playing there makes for <paramref name="side"/>, plus most of what it blocks.</summary>
    static List<int> Ranked(sbyte[] board, int side, List<int> near, Random? rng)
    {
        var scored = near.Select(sq => (sq, score: Attack(board, sq, side) + 0.9 * Attack(board, sq, -side), tie: rng?.Next() ?? 0)).ToList();
        return scored.OrderByDescending(s => s.score).ThenBy(s => s.tie).Select(s => s.sq).ToList();
    }

    /// <summary>What a stone of <paramref name="side"/> on the empty point <paramref name="sq"/> would make, summed over the four lines through it.</summary>
    public static double Attack(sbyte[] board, int sq, int side)
    {
        double total = 0;
        foreach (var (dr, dc) in Dirs) total += Shape(board, sq, side, dr, dc);
        return total;
    }

    /// <summary>
    /// The shape one line makes with a stone of <paramref name="side"/> on <paramref name="sq"/>, from the five-point
    /// windows along the line that contain it and hold none of the other side's stones: a full window is five; the
    /// points that would fill a window of four tell a four (one such point) from an open four (two); two or more
    /// windows of three make an open three, one a closed three, and so on down.
    /// </summary>
    static int Shape(sbyte[] board, int sq, int side, int dr, int dc)
    {
        int r0 = sq / N, c0 = sq % N;
        int best = 0, threes = 0, twos = 0;
        var fillers = new HashSet<int>();
        for (int start = -(Need - 1); start <= 0; start++)
        {
            int mine = 1, gap = -1, gaps = 0;
            bool blocked = false;
            for (int k = start; k < start + Need && !blocked; k++)
            {
                int r = r0 + dr * k, c = c0 + dc * k;
                if (r < 0 || r >= N || c < 0 || c >= N) blocked = true;
                else if (k == 0) continue;
                else if (board[r * N + c] == side) mine++;
                else if (board[r * N + c] != 0) blocked = true;
                else
                {
                    gap = r * N + c;
                    gaps++;
                }
            }
            if (blocked) continue;
            best = Math.Max(best, mine);
            if (mine == 4 && gaps == 1) fillers.Add(gap);
            if (mine == 3) threes++;
            if (mine == 2) twos++;
        }
        if (best >= 5) return Five;
        if (fillers.Count >= 2) return OpenFour;
        if (fillers.Count == 1) return Four;
        if (threes >= 2) return OpenThree;
        if (threes == 1) return Three;
        if (twos >= 2) return OpenTwo;
        if (twos == 1) return Two;
        return best == 1 ? One : 0;
    }
}
