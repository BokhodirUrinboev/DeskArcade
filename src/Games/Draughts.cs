using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DeskArcade.Games;

/// <summary>
/// English draughts (checkers) rules, free of any UI so they can be unit-tested and shared by both ends
/// of a LAN game. Squares are 0..63, row-major from the top-left. Side +1 starts on rows 5–7 and moves up;
/// side −1 starts on rows 0–2 and moves down. Men move and capture forward only; kings move one step in
/// any diagonal direction. Captures are compulsory, a capture continues while it can, and reaching the far
/// row crowns a man and ends the move. A side with no legal move loses. Forty moves each with no
/// capture and no man moving is a draw.
/// </summary>
public sealed class Draughts : IBoardRules
{
    /// <summary>0 empty, ±1 man, ±2 king; the sign is the side.</summary>
    public sbyte[] Board { get; } = new sbyte[64];
    public int Turn { get; private set; } = 1;
    public int Ply { get; private set; }
    /// <summary>Plies since the last capture or man move; <see cref="DrawPlies"/> of them is a draw.</summary>
    public int Quiet { get; private set; }
    public const int DrawPlies = 80;
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
            foreach (var (dr, dc) in Dirs(p))
                if (At(sq, dr, dc) is int to && Board[to] == 0) steps.Add(new[] { sq, to });
        }
        return captures.Count > 0 ? captures : steps;
    }

    void Jumps(int from, sbyte p, List<int> path, HashSet<int> taken, List<int[]> into)
    {
        bool any = false;
        foreach (var (dr, dc) in Dirs(p))
        {
            if (At(from, dr, dc) is not int over || At(from, 2 * dr, 2 * dc) is not int to) continue;
            if (Math.Sign(Board[over]) != -Math.Sign(p) || taken.Contains(over)) continue;
            if (Board[to] != 0 && to != path[0]) continue;
            any = true;
            path.Add(to);
            taken.Add(over);
            if (Math.Abs(p) == 1 && CrownRow(to, p)) into.Add(path.ToArray()); // crowning ends the move
            else Jumps(to, p, path, taken, into);
            path.RemoveAt(path.Count - 1);
            taken.Remove(over);
        }
        if (!any && path.Count > 1) into.Add(path.ToArray());
    }

    /// <summary>True if <paramref name="path"/> is one of <see cref="LegalMoves"/>.</summary>
    public bool IsLegal(int[] path) => LegalMoves().Any(m => m.SequenceEqual(path));

    /// <summary>Plays a move from <see cref="LegalMoves"/>; returns the squares of captured pieces.</summary>
    public List<int> Apply(int[] path)
    {
        var captured = new List<int>();
        sbyte p = Board[path[0]];
        Board[path[0]] = 0;
        for (int i = 1; i < path.Length; i++)
        {
            int a = path[i - 1], b = path[i];
            if (Math.Abs(a / 8 - b / 8) == 2)
            {
                int over = (a + b) / 2;
                Board[over] = 0;
                captured.Add(over);
            }
        }
        int end = path[^1];
        Quiet = captured.Count > 0 || Math.Abs(p) == 1 ? 0 : Quiet + 1;
        if (Math.Abs(p) == 1 && CrownRow(end, p)) p = (sbyte)(2 * p);
        Board[end] = p;
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

    /// <summary>Material from the side to move's point of view; men are worth a bit more as they advance.</summary>
    double Evaluate()
    {
        double s = 0;
        for (int i = 0; i < 64; i++)
        {
            sbyte p = Board[i];
            if (p == 0) continue;
            int side = Math.Sign(p);
            double v = Math.Abs(p) == 2 ? 1.6 : 1 + 0.03 * (side > 0 ? 7 - i / 8 : i / 8);
            s += side == Turn ? v : -v;
        }
        return s;
    }

    // ------------------------------------------------------------------ geometry

    static readonly (int, int)[] Up = { (-1, -1), (-1, 1) }, Down = { (1, -1), (1, 1) }, All = { (-1, -1), (-1, 1), (1, -1), (1, 1) };

    static (int, int)[] Dirs(sbyte p) => Math.Abs(p) == 2 ? All : p > 0 ? Up : Down;

    static bool CrownRow(int sq, sbyte p) => p > 0 ? sq / 8 == 0 : sq / 8 == 7;

    static int? At(int sq, int dr, int dc)
    {
        int r = sq / 8 + dr, c = sq % 8 + dc;
        return r is >= 0 and < 8 && c is >= 0 and < 8 ? r * 8 + c : null;
    }
}
