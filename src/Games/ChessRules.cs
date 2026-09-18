using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DeskArcade.Games;

/// <summary>
/// Chess rules, free of UI. Squares are 0..63 row-major from the top-left (a8 = 0, h1 = 63). White (+1)
/// starts on rows 6–7 and moves up. Pieces: 1 pawn, 2 knight, 3 bishop, 4 rook, 5 queen, 6 king; the sign
/// is the side. Covers castling, en passant, promotion (always to a queen), check, mate, stalemate, the
/// 50-move rule and bare-minor-piece draws. Threefold repetition is not tracked.
/// </summary>
public sealed class ChessRules : IBoardRules, ILeveledRules
{
    public const int Pawn = 1, Knight = 2, Bishop = 3, Rook = 4, Queen = 5, King = 6;
    const int WhiteShort = 1, WhiteLong = 2, BlackShort = 4, BlackLong = 8;

    public sbyte[] Board { get; } = new sbyte[64];
    public int Turn { get; private set; } = 1;
    public int Ply { get; private set; }
    /// <summary>Castling rights as bits: 1 white short, 2 white long, 4 black short, 8 black long.</summary>
    public int Castle { get; private set; } = 15;
    /// <summary>The square a pawn just skipped over (en passant target), or −1.</summary>
    public int EnPassant { get; private set; } = -1;
    /// <summary>Plies since the last capture or pawn move.</summary>
    public int HalfMoves { get; private set; }

    List<int[]>? _legal;

    public static ChessRules New()
    {
        var c = new ChessRules();
        sbyte[] back = { Rook, Knight, Bishop, Queen, King, Bishop, Knight, Rook };
        for (int col = 0; col < 8; col++)
        {
            c.Board[col] = (sbyte)-back[col];
            c.Board[8 + col] = -Pawn;
            c.Board[48 + col] = Pawn;
            c.Board[56 + col] = back[col];
        }
        return c;
    }

    /// <summary>"e2" → 52.</summary>
    public static int Sq(string name) => (8 - (name[1] - '0')) * 8 + (name[0] - 'a');

    public int Count(int side) => Board.Count(p => Math.Sign(p) == side);

    public bool InCheck(int side) => Array.IndexOf(Board, (sbyte)(King * side)) is int k && k >= 0 && Attacked(k, -side);

    public int Alert => InCheck(Turn) ? Array.IndexOf(Board, (sbyte)(King * Turn)) : -1;

    public int Result =>
        LegalMoves().Count == 0 ? (InCheck(Turn) ? -Turn : 2)
        : HalfMoves >= 100 || BareMaterial() ? 2
        : 0;

    /// <summary>"stalemate", "fifty" or "material" once <see cref="Result"/> is a draw.</summary>
    public string DrawKind => LegalMoves().Count == 0 ? "stalemate" : HalfMoves >= 100 ? "fifty" : "material";

    bool BareMaterial()
    {
        int minors = 0;
        foreach (sbyte p in Board)
        {
            int k = Math.Abs(p);
            if (k is Pawn or Rook or Queen) return false;
            if (k is Knight or Bishop) minors++;
        }
        return minors <= 1;
    }

    // ------------------------------------------------------------------ move generation

    public List<int[]> LegalMoves()
    {
        if (_legal != null) return _legal;
        var legal = new List<int[]>();
        foreach (var m in Pseudo(capturesOnly: false))
        {
            var next = Clone();
            next.Move(m);
            if (!next.InCheck(Turn)) legal.Add(m);
        }
        AddCastling(legal);
        return _legal = legal;
    }

    void AddCastling(List<int[]> into)
    {
        int home = Turn > 0 ? 60 : 4;
        if (Board[home] != King * Turn || Attacked(home, -Turn)) return;
        int shortBit = Turn > 0 ? WhiteShort : BlackShort, longBit = Turn > 0 ? WhiteLong : BlackLong;
        if ((Castle & shortBit) != 0 && Board[home + 3] == Rook * Turn && Board[home + 1] == 0 && Board[home + 2] == 0 &&
            !Attacked(home + 1, -Turn) && !Attacked(home + 2, -Turn))
            into.Add(new[] { home, home + 2 });
        if ((Castle & longBit) != 0 && Board[home - 4] == Rook * Turn && Board[home - 1] == 0 && Board[home - 2] == 0 && Board[home - 3] == 0 &&
            !Attacked(home - 1, -Turn) && !Attacked(home - 2, -Turn))
            into.Add(new[] { home, home - 2 });
    }

    static readonly (int, int)[] KnightSteps = { (-2, -1), (-2, 1), (-1, -2), (-1, 2), (1, -2), (1, 2), (2, -1), (2, 1) };
    static readonly (int, int)[] Diagonal = { (-1, -1), (-1, 1), (1, -1), (1, 1) };
    static readonly (int, int)[] Straight = { (-1, 0), (1, 0), (0, -1), (0, 1) };
    static readonly (int, int)[] AllDirs = Diagonal.Concat(Straight).ToArray();

    IEnumerable<int[]> Pseudo(bool capturesOnly)
    {
        for (int sq = 0; sq < 64; sq++)
        {
            sbyte p = Board[sq];
            if (Math.Sign(p) != Turn) continue;
            int r = sq / 8, c = sq % 8;
            switch (Math.Abs(p))
            {
                case Pawn:
                    int dr = -Turn;
                    if (!capturesOnly && On(r + dr, c) && Board[(r + dr) * 8 + c] == 0)
                    {
                        yield return new[] { sq, (r + dr) * 8 + c };
                        int start = Turn > 0 ? 6 : 1;
                        if (r == start && Board[(r + 2 * dr) * 8 + c] == 0) yield return new[] { sq, (r + 2 * dr) * 8 + c };
                    }
                    foreach (int dc in new[] { -1, 1 })
                    {
                        if (!On(r + dr, c + dc)) continue;
                        int to = (r + dr) * 8 + c + dc;
                        if (Math.Sign(Board[to]) == -Turn || to == EnPassant) yield return new[] { sq, to };
                    }
                    break;
                case Knight:
                    foreach (var m in Steps(sq, KnightSteps, capturesOnly)) yield return m;
                    break;
                case King:
                    foreach (var m in Steps(sq, AllDirs, capturesOnly)) yield return m;
                    break;
                case Bishop:
                    foreach (var m in Slides(sq, Diagonal, capturesOnly)) yield return m;
                    break;
                case Rook:
                    foreach (var m in Slides(sq, Straight, capturesOnly)) yield return m;
                    break;
                case Queen:
                    foreach (var m in Slides(sq, AllDirs, capturesOnly)) yield return m;
                    break;
            }
        }
    }

    IEnumerable<int[]> Steps(int sq, (int, int)[] steps, bool capturesOnly)
    {
        int r = sq / 8, c = sq % 8;
        foreach (var (dr, dc) in steps)
        {
            if (!On(r + dr, c + dc)) continue;
            int to = (r + dr) * 8 + c + dc;
            int s = Math.Sign(Board[to]);
            if (s == -Turn || (s == 0 && !capturesOnly)) yield return new[] { sq, to };
        }
    }

    IEnumerable<int[]> Slides(int sq, (int, int)[] dirs, bool capturesOnly)
    {
        int r0 = sq / 8, c0 = sq % 8;
        foreach (var (dr, dc) in dirs)
            for (int r = r0 + dr, c = c0 + dc; On(r, c); r += dr, c += dc)
            {
                int to = r * 8 + c, s = Math.Sign(Board[to]);
                if (s == Turn) break;
                if (s == 0 && capturesOnly) continue;
                yield return new[] { sq, to };
                if (s != 0) break;
            }
    }

    /// <summary>True if any piece of <paramref name="by"/> attacks <paramref name="sq"/>.</summary>
    public bool Attacked(int sq, int by)
    {
        int r = sq / 8, c = sq % 8;
        int pr = r + by; // an attacking pawn sits one row behind, from its own point of view
        foreach (int dc in new[] { -1, 1 })
            if (On(pr, c + dc) && Board[pr * 8 + c + dc] == Pawn * by) return true;
        foreach (var (dr, dc) in KnightSteps)
            if (On(r + dr, c + dc) && Board[(r + dr) * 8 + c + dc] == Knight * by) return true;
        foreach (var (dr, dc) in AllDirs)
            if (On(r + dr, c + dc) && Board[(r + dr) * 8 + c + dc] == King * by) return true;
        return Ray(r, c, Diagonal, by, Bishop) || Ray(r, c, Straight, by, Rook);
    }

    bool Ray(int r0, int c0, (int, int)[] dirs, int by, int slider)
    {
        foreach (var (dr, dc) in dirs)
            for (int r = r0 + dr, c = c0 + dc; On(r, c); r += dr, c += dc)
            {
                sbyte p = Board[r * 8 + c];
                if (p == 0) continue;
                if (p == slider * by || p == Queen * by) return true;
                break;
            }
        return false;
    }

    static bool On(int r, int c) => r is >= 0 and < 8 && c is >= 0 and < 8;

    // ------------------------------------------------------------------ making moves

    public List<int> Apply(int[] move)
    {
        var captured = Move(move);
        Ply++;
        return captured;
    }

    /// <summary>Plays a (pseudo-legal) move and passes the turn.</summary>
    List<int> Move(int[] move)
    {
        int from = move[0], to = move[1];
        sbyte p = Board[from];
        int kind = Math.Abs(p), side = Math.Sign(p);
        var captured = new List<int>();
        if (Board[to] != 0) captured.Add(to);
        if (kind == Pawn && to == EnPassant && Board[to] == 0)
        {
            int victim = to + 8 * side;
            Board[victim] = 0;
            captured.Add(victim);
        }
        if (kind == King && Math.Abs(to - from) == 2)
        {
            int rookFrom = to > from ? to + 1 : to - 2, rookTo = to > from ? to - 1 : to + 1;
            Board[rookTo] = Board[rookFrom];
            Board[rookFrom] = 0;
        }
        Board[to] = kind == Pawn && (to / 8 == 0 || to / 8 == 7) ? (sbyte)(Queen * side) : p;
        Board[from] = 0;

        if (kind == King) Castle &= side > 0 ? ~(WhiteShort | WhiteLong) : ~(BlackShort | BlackLong);
        foreach (int sq in new[] { from, to })
            Castle &= sq switch { 63 => ~WhiteShort, 56 => ~WhiteLong, 7 => ~BlackShort, 0 => ~BlackLong, _ => ~0 };
        EnPassant = kind == Pawn && Math.Abs(to - from) == 16 ? (from + to) / 2 : -1;
        HalfMoves = kind == Pawn || captured.Count > 0 ? 0 : HalfMoves + 1;
        Turn = -Turn;
        _legal = null;
        return captured;
    }

    public ChessRules Clone()
    {
        var c = new ChessRules { Turn = Turn, Ply = Ply, Castle = Castle, EnPassant = EnPassant, HalfMoves = HalfMoves };
        Array.Copy(Board, c.Board, 64);
        return c;
    }

    // ------------------------------------------------------------------ network form

    const string Letters = ".PNBRQK";

    public string Encode()
    {
        var sb = new StringBuilder(80);
        foreach (sbyte p in Board)
        {
            char ch = Letters[Math.Abs(p)];
            sb.Append(p < 0 ? char.ToLowerInvariant(ch) : ch);
        }
        return sb.Append(Turn > 0 ? "|w|" : "|b|").Append(Castle).Append('|').Append(EnPassant).Append('|')
            .Append(HalfMoves).Append('|').Append(Ply).ToString();
    }

    public static ChessRules? Decode(string text)
    {
        var f = text.Split('|');
        if (f.Length != 6 || f[0].Length != 64 || !int.TryParse(f[2], out int castle) || !int.TryParse(f[3], out int ep) ||
            !int.TryParse(f[4], out int half) || !int.TryParse(f[5], out int ply)) return null;
        var c = new ChessRules { Turn = f[1] == "b" ? -1 : 1, Castle = castle, EnPassant = ep, HalfMoves = half, Ply = ply };
        for (int i = 0; i < 64; i++)
        {
            int kind = Letters.IndexOf(char.ToUpperInvariant(f[0][i]));
            c.Board[i] = (sbyte)(kind <= 0 ? 0 : char.IsLower(f[0][i]) ? -kind : kind);
        }
        return c;
    }

    // ------------------------------------------------------------------ computer player

    static readonly int[] Value = { 0, 100, 320, 330, 500, 900, 0 };

    public int[] BestMove(Random rng) => BestMove(rng, 3);

    public int[] BestMove(Random rng, int depth)
    {
        int[] pick = LegalMoves()[0];
        double best = double.NegativeInfinity;
        foreach (var m in Ordered(LegalMoves().OrderBy(_ => rng.Next()).ToList()))
        {
            var next = Clone();
            next.Apply(m);
            // a move that can't beat the best comes back as exactly -best, so only a strictly better one wins;
            // the shuffle above picks among equals
            double score = -next.Search(depth - 1, -1e9, -best);
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
        if (depth <= 0) return Quiesce(alpha, beta, 4);
        var moves = LegalMoves();
        if (moves.Count == 0) return InCheck(Turn) ? -100000 - depth : 0; // mating sooner scores higher
        foreach (var m in Ordered(moves))
        {
            var next = Clone();
            next.Apply(m);
            alpha = Math.Max(alpha, -next.Search(depth - 1, -beta, -alpha));
            if (alpha >= beta) break;
        }
        return alpha;
    }

    /// <summary>Keep playing captures past the horizon, so the CPU doesn't hang a piece to a recapture.</summary>
    double Quiesce(double alpha, double beta, int left)
    {
        double stand = Evaluate();
        if (stand >= beta || left == 0) return stand;
        alpha = Math.Max(alpha, stand);
        foreach (var m in Ordered(Pseudo(capturesOnly: true)))
        {
            var next = Clone();
            next.Move(m);
            if (next.InCheck(Turn)) continue;
            alpha = Math.Max(alpha, -next.Quiesce(-beta, -alpha, left - 1));
            if (alpha >= beta) break;
        }
        return alpha;
    }

    /// <summary>Captures of valuable pieces by cheap ones first, which makes the search prune much more.</summary>
    IEnumerable<int[]> Ordered(IEnumerable<int[]> moves) =>
        moves.OrderByDescending(m => Board[m[1]] == 0 ? 0 : Value[Math.Abs(Board[m[1]])] * 10 - Value[Math.Abs(Board[m[0]])]);

    /// <summary>Material plus a small bonus for central pieces and advanced pawns, from the side to move's view.</summary>
    double Evaluate()
    {
        double s = 0;
        for (int i = 0; i < 64; i++)
        {
            sbyte p = Board[i];
            if (p == 0) continue;
            int kind = Math.Abs(p), side = Math.Sign(p), r = i / 8, c = i % 8;
            double center = 3.5 - Math.Max(Math.Abs(r - 3.5), Math.Abs(c - 3.5)); // 0 at the rim, 3 in the middle
            double v = Value[kind] + kind switch
            {
                Pawn => 6 * (side > 0 ? 6 - r : r - 1),
                Knight or Bishop => 8 * center,
                Queen => 2 * center,
                _ => 0,
            };
            s += side == Turn ? v : -v;
        }
        return s;
    }
}
