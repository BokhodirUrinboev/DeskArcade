using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace DeskArcade.Games;

/// <summary>
/// Reversi (Othello) rules, free of UI. 8×8, black (+1) moves first from the usual four discs in the middle. A move
/// is [square]: it must outflank at least one line of the other side's discs, and every outflanked disc flips. A side
/// with no move passes (the turn simply stays with the other side and <see cref="Passed"/> says who passed); when
/// neither side can move the game is over and the most discs wins.
/// </summary>
public sealed class ReversiRules : IBoardRules, ILeveledRules
{
    public const int N = 8;
    /// <summary>A search score beyond any evaluation: a finished game, plus its disc margin.</summary>
    const int Won = 100000;
    /// <summary>The computer player stops deepening its search after this long, to keep the overlay responsive.</summary>
    public const int ThinkMs = 100;

    static readonly (int Dr, int Dc)[] Dirs = { (-1, -1), (-1, 0), (-1, 1), (0, -1), (0, 1), (1, -1), (1, 0), (1, 1) };

    /// <summary>
    /// What each square is worth to hold: corners most, the squares that give a corner away (next to it) least,
    /// edges a little. The usual table from Othello programs.
    /// </summary>
    static readonly int[] Weights =
    {
        100, -20, 10,  5,  5, 10, -20, 100,
        -20, -50, -2, -2, -2, -2, -50, -20,
         10,  -2,  1,  1,  1,  1,  -2,  10,
          5,  -2,  1,  0,  0,  1,  -2,   5,
          5,  -2,  1,  0,  0,  1,  -2,   5,
         10,  -2,  1,  1,  1,  1,  -2,  10,
        -20, -50, -2, -2, -2, -2, -50, -20,
        100, -20, 10,  5,  5, 10, -20, 100,
    };

    public sbyte[] Board { get; } = new sbyte[N * N];
    public int Turn { get; private set; } = 1;
    public int Ply { get; private set; }
    /// <summary>The side that had no move and passed after the last move, or 0.</summary>
    public int Passed { get; private set; }
    public int Alert => -1;

    int _result;

    public static ReversiRules New()
    {
        var r = new ReversiRules();
        r.Board[3 * N + 3] = r.Board[4 * N + 4] = 1;
        r.Board[3 * N + 4] = r.Board[4 * N + 3] = -1;
        return r;
    }

    public int Result => _result;

    public int Count(int side) => Board.Count(p => p == side);

    public static int Sq(int row, int col) => row * N + col;

    // ------------------------------------------------------------------ moves

    public List<int[]> LegalMoves()
    {
        var moves = new List<int[]>();
        if (_result != 0) return moves;
        foreach (int sq in Moves(Board, Turn)) moves.Add(new[] { sq });
        return moves;
    }

    /// <summary>The squares <paramref name="side"/> may play on <paramref name="board"/>, top-left first.</summary>
    public static List<int> Moves(sbyte[] board, int side)
    {
        var list = new List<int>();
        for (int sq = 0; sq < board.Length; sq++)
            if (board[sq] == 0 && Outflanks(board, sq, side)) list.Add(sq);
        return list;
    }

    static bool Outflanks(sbyte[] board, int sq, int side)
    {
        int r0 = sq / N, c0 = sq % N;
        foreach (var (dr, dc) in Dirs)
        {
            int r = r0 + dr, c = c0 + dc, n = 0;
            while (r >= 0 && r < N && c >= 0 && c < N && board[r * N + c] == -side)
            {
                r += dr;
                c += dc;
                n++;
            }
            if (n > 0 && r >= 0 && r < N && c >= 0 && c < N && board[r * N + c] == side) return true;
        }
        return false;
    }

    /// <summary>The discs a disc of <paramref name="side"/> on <paramref name="sq"/> would flip, nearest first along each line.</summary>
    public static List<int> Flips(sbyte[] board, int sq, int side)
    {
        var flips = new List<int>();
        if (board[sq] != 0) return flips;
        int r0 = sq / N, c0 = sq % N;
        foreach (var (dr, dc) in Dirs)
        {
            int r = r0 + dr, c = c0 + dc, n = 0;
            while (r >= 0 && r < N && c >= 0 && c < N && board[r * N + c] == -side)
            {
                r += dr;
                c += dc;
                n++;
            }
            if (n == 0 || r < 0 || r >= N || c < 0 || c >= N || board[r * N + c] != side) continue;
            for (int k = 1; k <= n; k++) flips.Add((r0 + dr * k) * N + c0 + dc * k);
        }
        return flips;
    }

    /// <summary>Plays a legal move; returns the discs it flipped.</summary>
    public List<int> Apply(int[] move)
    {
        int sq = move[0];
        var flips = Flips(Board, sq, Turn);
        Board[sq] = (sbyte)Turn;
        foreach (int f in flips) Board[f] = (sbyte)Turn;
        Ply++;
        Advance(-Turn);
        return flips;
    }

    /// <summary>Hands the turn to <paramref name="next"/>, or back when it has no move; ends the game when neither side has one.</summary>
    void Advance(int next)
    {
        Passed = 0;
        _result = 0;
        if (Moves(Board, next).Count > 0) Turn = next;
        else if (Moves(Board, -next).Count > 0)
        {
            Turn = -next;
            Passed = next;
        }
        else
        {
            Turn = next;
            int diff = Count(1) - Count(-1);
            _result = diff > 0 ? 1 : diff < 0 ? -1 : 2;
        }
    }

    public string Encode()
    {
        var sb = new StringBuilder(Board.Length + 16);
        foreach (sbyte p in Board) sb.Append(p > 0 ? 'x' : p < 0 ? 'o' : '.');
        return sb.Append(Turn > 0 ? "|x|" : "|o|").Append(Ply).Append('|').Append(Passed > 0 ? 'x' : Passed < 0 ? 'o' : '-').ToString();
    }

    public static ReversiRules? Decode(string text)
    {
        var f = text.Split('|');
        if (f.Length != 4 || f[0].Length != N * N || !int.TryParse(f[2], out int ply) || ply < 0) return null;
        var d = new ReversiRules { Ply = ply };
        for (int i = 0; i < d.Board.Length; i++) d.Board[i] = (sbyte)(f[0][i] == 'x' ? 1 : f[0][i] == 'o' ? -1 : 0);
        int turn = f[1] == "o" ? -1 : 1;
        d.Advance(turn); // works out the result, and a turn that can't be taken, from the board itself
        if (d._result == 0 && d.Turn == turn) d.Passed = f[3] == "x" ? 1 : f[3] == "o" ? -1 : 0;
        return d;
    }

    // ------------------------------------------------------------------ computer player

    /// <summary>A middling player, for the demo mode: greedy with the square weights.</summary>
    public int[] BestMove(Random rng) => BestMove(rng, 1);

    /// <summary>
    /// The CPU's move. <paramref name="depth"/> 0 (Easy) plays at random, leaning towards moves that flip more;
    /// 1 (Medium) is greedy: the most discs, with corners and edges worth more and the squares that give a corner
    /// away worth less; 2 and more (Hard, Expert) search that many moves ahead with alpha-beta, weighing the
    /// squares held and the moves each side has left, and stop deepening after <see cref="ThinkMs"/>.
    /// </summary>
    public int[] BestMove(Random rng, int depth)
    {
        var moves = Moves(Board, Turn);
        if (moves.Count == 0) return Array.Empty<int>();
        if (depth <= 0)
        {
            // a raffle where every disc a move flips is another ticket
            var tickets = moves.Select(m => 1 + Flips(Board, m, Turn).Count).ToArray();
            int draw = rng.Next(tickets.Sum());
            for (int i = 0; i < moves.Count; i++)
                if ((draw -= tickets[i]) < 0) return new[] { moves[i] };
            return new[] { moves[^1] };
        }
        if (depth == 1)
            return new[] { moves.OrderByDescending(m => Flips(Board, m, Turn).Count + Weights[m] / 4.0).ThenBy(_ => rng.Next()).First() };
        return new[] { Search(moves.OrderBy(_ => rng.Next()).ToList(), depth) };
    }

    /// <summary>Iterative deepening from the root: each finished depth orders the next, and the clock can only cut a depth short.</summary>
    int Search(List<int> moves, int depth)
    {
        var clock = Stopwatch.StartNew();
        int best = moves[0];
        var order = moves.OrderByDescending(m => Weights[m]).ToList();
        for (int d = 1; d <= depth; d++)
        {
            int pick = order[0], alpha = -int.MaxValue;
            var scores = new Dictionary<int, int>();
            bool cut = false;
            foreach (int m in order)
            {
                var b = Played(Board, m, Turn);
                int score = -Negamax(b, -Turn, d - 1, -int.MaxValue, -alpha, clock, false);
                if (clock.ElapsedMilliseconds > ThinkMs && d > 1)
                {
                    cut = true;
                    break;
                }
                scores[m] = score;
                if (score > alpha)
                {
                    alpha = score;
                    pick = m;
                }
            }
            if (cut) break;
            best = pick;
            order = order.OrderByDescending(m => scores.TryGetValue(m, out int s) ? s : int.MinValue).ToList();
            if (Math.Abs(alpha) >= Won) break; // the outcome is settled
        }
        return best;
    }

    static sbyte[] Played(sbyte[] board, int sq, int side)
    {
        var b = (sbyte[])board.Clone();
        b[sq] = (sbyte)side;
        foreach (int f in Flips(board, sq, side)) b[f] = (sbyte)side;
        return b;
    }

    /// <summary>The value of <paramref name="board"/> for <paramref name="side"/>, to move. A pass costs no depth.</summary>
    static int Negamax(sbyte[] board, int side, int depth, int alpha, int beta, Stopwatch clock, bool passed)
    {
        var moves = Moves(board, side);
        if (moves.Count == 0)
        {
            if (passed) return Final(board, side); // neither side can move
            return -Negamax(board, -side, depth, -beta, -alpha, clock, true);
        }
        if (depth <= 0 || clock.ElapsedMilliseconds > ThinkMs) return Evaluate(board, side, moves.Count);
        foreach (int m in moves.OrderByDescending(m => Weights[m]))
        {
            int score = -Negamax(Played(board, m, side), -side, depth - 1, -beta, -alpha, clock, false);
            if (score > alpha) alpha = score;
            if (alpha >= beta) break;
        }
        return alpha;
    }

    /// <summary>A finished game: a win outweighs any position, and a bigger margin is better.</summary>
    static int Final(sbyte[] board, int side)
    {
        int diff = 0;
        foreach (sbyte p in board) diff += p * side;
        return diff > 0 ? Won + diff : diff < 0 ? -Won + diff : 0;
    }

    /// <summary>The squares held (by their weights) plus mobility: having more moves than the other side.</summary>
    static int Evaluate(sbyte[] board, int side, int myMoves)
    {
        int s = 0;
        for (int sq = 0; sq < board.Length; sq++) s += board[sq] * side * Weights[sq];
        int theirMoves = Moves(board, -side).Count;
        return s + 5 * (myMoves - theirMoves);
    }
}
