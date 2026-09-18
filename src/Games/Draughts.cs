using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DeskArcade.Games;

/// <summary>
/// Russian draughts (shashki), free of any UI so they can be unit-tested and shared by both ends of a LAN
/// game. Squares are 0..63, row-major from the top-left. Side +1 (white) starts on rows 5–7, moves up and
/// moves first; side −1 starts on rows 0–2 and moves down.
/// <list type="bullet">
/// <item>Men step one square diagonally forward, but capture forward and backward.</item>
/// <item>Kings fly: they move any distance along a free diagonal, and capture a piece at any distance,
/// landing on any free square beyond it.</item>
/// <item>Capturing is compulsory and a capture continues while it can, but among several captures the
/// player may choose any (not necessarily the one that takes the most).</item>
/// <item>A man that reaches the far row during a capture is crowned at once and goes on capturing as a king.</item>
/// <item>Captured pieces are removed only after the move ("Turkish strike"): no piece can be jumped twice,
/// and a captured piece still blocks the way.</item>
/// </list>
/// A side with no legal move loses. Fifteen moves each with no capture and no man moving is a draw.
/// </summary>
public sealed class Draughts : IBoardRules, ILeveledRules
{
    /// <summary>0 empty, ±1 man, ±2 king; the sign is the side.</summary>
    public sbyte[] Board { get; } = new sbyte[64];
    public int Turn { get; private set; } = 1;
    public int Ply { get; private set; }
    /// <summary>Plies since the last capture or man move; <see cref="DrawPlies"/> of them is a draw.</summary>
    public int Quiet { get; private set; }
    public const int DrawPlies = 30;
    public bool IsDraw => Quiet >= DrawPlies;

    public static Draughts New()
    {
        var d = new Draughts();
        for (int i = 0; i < 64; i++)
        {
            if (!Dark(i)) continue;
            int r = i / 8;
            if (r <= 2) d.Board[i] = -1;
            else if (r >= 5) d.Board[i] = 1;
        }
        return d;
    }

    public static bool Dark(int sq) => (sq / 8 + sq % 8) % 2 == 1;

    public int Count(int side) => Board.Count(p => Math.Sign(p) == side);

    /// <summary>+1 or −1 once the side to move is stuck, else 0.</summary>
    public int Winner => LegalMoves().Count == 0 ? -Turn : 0;

    public int Result => Winner is int w and not 0 ? w : IsDraw ? 2 : 0;

    public int Alert => -1;

    /// <summary>Every legal move for the side to move, each as a path: [from, landing, landing...].</summary>
    public List<int[]> LegalMoves()
    {
        var captures = new List<int[]>();
        var steps = new List<int[]>();
        for (int sq = 0; sq < 64; sq++)
        {
            sbyte p = Board[sq];
            if (Math.Sign(p) != Turn) continue;
            Jumps(sq, p, new List<int> { sq }, new HashSet<int>(), captures);
            if (captures.Count > 0) continue;
            foreach (var (dr, dc) in All)
            {
                if (Math.Abs(p) == 1 && dr != Forward(p)) continue; // men step forward only
                for (int dist = 1; At(sq, dist * dr, dist * dc) is int to && Board[to] == 0; dist++)
                {
                    steps.Add(new[] { sq, to });
                    if (Math.Abs(p) == 1) break; // a man steps one square; a king flies
                }
            }
        }
        return captures.Count > 0 ? captures.Distinct(PathComparer.Instance).ToList() : steps;
    }

    /// <summary>
    /// Extends a capture from <paramref name="from"/> in every possible way. <paramref name="taken"/> holds the
    /// pieces already captured on this move: they stay on the board until the move ends, so they can't be
    /// jumped again and still block the way.
    /// </summary>
    void Jumps(int from, sbyte p, List<int> path, HashSet<int> taken, List<int[]> into)
    {
        bool any = false, king = Math.Abs(p) == 2;
        foreach (var (dr, dc) in All)
        {
            // walk to the first piece on this diagonal (a man only looks at the next square)
            int dist = 1;
            int? over = null;
            while (At(from, dist * dr, dist * dc) is int sq)
            {
                if (!IsEmpty(sq, path[0], taken))
                {
                    over = sq;
                    break;
                }
                if (!king) break;
                dist++;
            }
            if (over is not int victim || taken.Contains(victim) || Math.Sign(Board[victim]) != -Math.Sign(p)) continue;

            // land on the free square behind it; a king may land on any of the free squares behind it, but if
            // it can go on capturing from some of them, it must land on one of those
            var landings = new List<int>();
            for (int beyond = dist + 1; At(from, beyond * dr, beyond * dc) is int to && IsEmpty(to, path[0], taken); beyond++)
            {
                landings.Add(to);
                if (!king) break;
            }
            if (landings.Count == 0) continue;
            any = true;
            taken.Add(victim);
            if (king && landings.Count > 1)
            {
                var onward = landings.Where(to => CanCapture(to, p, path[0], taken)).ToList();
                if (onward.Count > 0) landings = onward;
            }
            foreach (int to in landings)
            {
                path.Add(to);
                // a man reaching the far row is crowned there and goes on capturing as a king
                sbyte next = !king && CrownRow(to, p) ? (sbyte)(2 * p) : p;
                Jumps(to, next, path, taken, into);
                path.RemoveAt(path.Count - 1);
            }
            taken.Remove(victim);
        }
        if (!any && path.Count > 1) into.Add(path.ToArray());
    }

    /// <summary>Whether a king of <paramref name="p"/>'s side standing on <paramref name="from"/> could capture something next.</summary>
    bool CanCapture(int from, sbyte p, int start, HashSet<int> taken)
    {
        foreach (var (dr, dc) in All)
        {
            int dist = 1;
            while (At(from, dist * dr, dist * dc) is int sq && IsEmpty(sq, start, taken)) dist++;
            if (At(from, dist * dr, dist * dc) is not int victim || taken.Contains(victim) || Math.Sign(Board[victim]) != -Math.Sign(p)) continue;
            if (At(from, (dist + 1) * dr, (dist + 1) * dc) is int land && IsEmpty(land, start, taken)) return true;
        }
        return false;
    }

    /// <summary>Free for the moving piece: empty, or the square it started from. A captured piece is not free.</summary>
    bool IsEmpty(int sq, int start, HashSet<int> taken) => !taken.Contains(sq) && (Board[sq] == 0 || sq == start);

    /// <summary>True if <paramref name="path"/> is one of <see cref="LegalMoves"/>.</summary>
    public bool IsLegal(int[] path) => LegalMoves().Any(m => m.SequenceEqual(path));

    /// <summary>Plays a move from <see cref="LegalMoves"/>; returns the squares of captured pieces.</summary>
    public List<int> Apply(int[] path)
    {
        var captured = new List<int>();
        sbyte p = Board[path[0]];
        int side = Math.Sign(p);
        Board[path[0]] = 0;
        bool crowned = false;
        for (int i = 1; i < path.Length; i++)
        {
            int a = path[i - 1], b = path[i];
            int dr = Math.Sign(b / 8 - a / 8), dc = Math.Sign(b % 8 - a % 8);
            for (int sq = a + dr * 8 + dc; sq != b; sq += dr * 8 + dc)
                if (Math.Sign(Board[sq]) == -side && !captured.Contains(sq)) captured.Add(sq);
            crowned |= Math.Abs(p) == 1 && CrownRow(b, p);
        }
        foreach (int sq in captured) Board[sq] = 0; // Turkish strike: everything taken leaves together
        Quiet = captured.Count > 0 || Math.Abs(p) == 1 ? 0 : Quiet + 1;
        if (crowned) p = (sbyte)(2 * side);
        Board[path[^1]] = p;
        Turn = -Turn;
        Ply++;
        return captured;
    }

    public Draughts Clone()
    {
        var d = new Draughts { Turn = Turn, Ply = Ply, Quiet = Quiet };
        Array.Copy(Board, d.Board, 64);
        return d;
    }

    /// <summary>A compact text form for the network: 64 of ".wWbB", the side to move, the ply and the quiet count.</summary>
    public string Encode()
    {
        var sb = new StringBuilder(70);
        foreach (sbyte p in Board) sb.Append(p switch { 1 => 'w', 2 => 'W', -1 => 'b', -2 => 'B', _ => '.' });
        return sb.Append(Turn > 0 ? "|w|" : "|b|").Append(Ply).Append('|').Append(Quiet).ToString();
    }

    public static Draughts? Decode(string text)
    {
        var f = text.Split('|');
        if (f.Length is < 3 or > 4 || f[0].Length != 64 || !int.TryParse(f[2], out int ply)) return null;
        var d = new Draughts { Turn = f[1] == "b" ? -1 : 1, Ply = ply, Quiet = f.Length == 4 && int.TryParse(f[3], out int q) ? q : 0 };
        for (int i = 0; i < 64; i++) d.Board[i] = f[0][i] switch { 'w' => 1, 'W' => 2, 'b' => -1, 'B' => -2, _ => 0 };
        return d;
    }

    // ------------------------------------------------------------------ computer player

    /// <summary>A short look-ahead on material, with a little randomness among equal moves.</summary>
    public int[] BestMove(Random rng) => BestMove(rng, 4);

    public int[] BestMove(Random rng, int depth)
    {
        var moves = LegalMoves();
        double best = double.NegativeInfinity;
        int[] pick = moves[0];
        foreach (var m in moves.OrderBy(_ => rng.Next()))
        {
            var next = Clone();
            next.Apply(m);
            double score = -next.Search(depth - 1, double.NegativeInfinity, double.PositiveInfinity);
            if (score > best)
            {
                best = score;
                pick = m;
            }
        }
        return pick;
    }

    double Search(int depth, double alpha, double beta)
    {
        var moves = LegalMoves();
        if (moves.Count == 0) return -1000 - depth; // losing sooner is worse
        if (depth <= 0) return Evaluate();
        foreach (var m in moves)
        {
            var next = Clone();
            next.Apply(m);
            alpha = Math.Max(alpha, -next.Search(depth - 1, -beta, -alpha));
            if (alpha >= beta) break;
        }
        return alpha;
    }

    /// <summary>Material from the side to move's point of view; men are worth a bit more as they advance, and a flying king is worth a lot.</summary>
    double Evaluate()
    {
        double s = 0;
        for (int i = 0; i < 64; i++)
        {
            sbyte p = Board[i];
            if (p == 0) continue;
            int side = Math.Sign(p);
            double v = Math.Abs(p) == 2 ? 3 : 1 + 0.03 * (side > 0 ? 7 - i / 8 : i / 8);
            s += side == Turn ? v : -v;
        }
        return s;
    }

    // ------------------------------------------------------------------ geometry

    static readonly (int, int)[] All = { (-1, -1), (-1, 1), (1, -1), (1, 1) };

    static int Forward(sbyte p) => p > 0 ? -1 : 1;

    static bool CrownRow(int sq, sbyte p) => p > 0 ? sq / 8 == 0 : sq / 8 == 7;

    static int? At(int sq, int dr, int dc)
    {
        int r = sq / 8 + dr, c = sq % 8 + dc;
        return r is >= 0 and < 8 && c is >= 0 and < 8 ? r * 8 + c : null;
    }

    /// <summary>Two capture routes through the same squares are the same move.</summary>
    sealed class PathComparer : IEqualityComparer<int[]>
    {
        public static readonly PathComparer Instance = new();
        public bool Equals(int[]? a, int[]? b) => a != null && b != null && a.SequenceEqual(b);
        public int GetHashCode(int[] path) => path.Aggregate(17, (h, sq) => h * 31 + sq);
    }
}
