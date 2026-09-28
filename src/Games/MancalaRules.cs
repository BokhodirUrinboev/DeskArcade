using System;
using System.Collections.Generic;
using System.Linq;

namespace DeskArcade.Games;

/// <summary>
/// Mancala (Kalah, six pits a side, four seeds in each): on your turn pick up all the seeds of one of your pits and sow
/// them one by one into the pits that follow, counterclockwise, into your own store but past the other player's. A last
/// seed that lands in your store earns another turn; one that lands in an empty pit of yours, opposite seeds, captures
/// them both into your store. When one side's pits are all empty the other side keeps what is left in theirs, and the
/// fuller store wins. Pits 0–5 are side 0's (left to right), 6 its store, 7–12 side 1's and 13 its store. UI-free, so
/// the rules and the computer player can be tested.
/// </summary>
public sealed class MancalaRules
{
    public const int Pits = 6, Start = 4, Total = Pits * 2 * Start;

    readonly int[] _b = new int[14];

    public MancalaRules()
    {
        for (int i = 0; i < 14; i++) _b[i] = i is 6 or 13 ? 0 : Start;
    }

    MancalaRules(int[] board, int turn)
    {
        Array.Copy(board, _b, 14);
        Turn = turn;
    }

    /// <summary>The side to move: 0 or 1.</summary>
    public int Turn { get; private set; }

    /// <summary>Moves played, for keeping two screens in step.</summary>
    public int Ply { get; private set; }

    public bool Over { get; private set; }

    /// <summary>The winner once <see cref="Over"/>: 0, 1, or -1 for a draw.</summary>
    public int Winner => !Over ? -1 : _b[6] > _b[13] ? 0 : _b[13] > _b[6] ? 1 : -1;

    public int this[int i] => _b[i];

    public static int Store(int side) => side == 0 ? 6 : 13;

    /// <summary>The board index of <paramref name="side"/>'s <paramref name="pit"/>-th pit (0–5).</summary>
    public static int PitOf(int side, int pit) => side * 7 + pit;

    public static int Opposite(int i) => 12 - i;

    public static int SideOf(int i) => i < 7 ? 0 : 1;

    public int Score(int side) => _b[Store(side)];

    public MancalaRules Clone() => new(_b, Turn) { Ply = Ply, Over = Over };

    public IEnumerable<int> LegalPits() => Enumerable.Range(0, Pits).Where(p => !Over && _b[PitOf(Turn, p)] > 0);

    /// <summary>What one move did, for the animation: every pit a seed landed in, in order, and a capture if there was one.</summary>
    public sealed record Sowing(int From, List<int> Drops, int CapturedFrom, int Captured, bool Again, bool Ended);

    /// <summary>Plays the current side's <paramref name="pit"/> (0–5); null if it is empty or the game is over.</summary>
    public Sowing? Play(int pit)
    {
        if (Over || pit < 0 || pit >= Pits) return null;
        int side = Turn, from = PitOf(side, pit), seeds = _b[from];
        if (seeds == 0) return null;
        _b[from] = 0;
        var drops = new List<int>(seeds);
        int at = from;
        while (seeds > 0)
        {
            at = (at + 1) % 14;
            if (at == Store(1 - side)) continue; // never into the other player's store
            _b[at]++;
            drops.Add(at);
            seeds--;
        }
        int capturedFrom = -1, captured = 0;
        if (SideOf(at) == side && at != Store(side) && _b[at] == 1 && _b[Opposite(at)] > 0)
        {
            capturedFrom = Opposite(at);
            captured = _b[capturedFrom] + 1;
            _b[Store(side)] += captured;
            _b[capturedFrom] = 0;
            _b[at] = 0;
        }
        bool again = at == Store(side);
        Ply++;
        bool ended = SweepIfDone();
        if (!ended && !again) Turn = 1 - side;
        return new Sowing(from, drops, capturedFrom, captured, again && !ended, ended);
    }

    /// <summary>When a side has no seeds left in its pits, the other side's go to their own store and the game is over.</summary>
    bool SweepIfDone()
    {
        bool empty0 = Enumerable.Range(0, Pits).All(p => _b[PitOf(0, p)] == 0);
        bool empty1 = Enumerable.Range(0, Pits).All(p => _b[PitOf(1, p)] == 0);
        if (!empty0 && !empty1) return false;
        for (int side = 0; side < 2; side++)
            for (int p = 0; p < Pits; p++)
            {
                int i = PitOf(side, p);
                _b[Store(side)] += _b[i];
                _b[i] = 0;
            }
        Over = true;
        return true;
    }

    // ------------------------------------------------------------------ the computer

    /// <summary>How far ahead the computer looks at each level (Easy looks one move ahead and slips now and then).</summary>
    public static readonly int[] Depths = { 1, 3, 6, 8 };

    /// <summary>The computer's pick for the side to move: a search <paramref name="depth"/> moves deep, ties broken at random.</summary>
    public int BestPit(int depth, Random rng)
    {
        int side = Turn, best = -1;
        double bestValue = double.NegativeInfinity;
        Span<int> next = stackalloc int[14];
        foreach (int p in LegalPits().OrderBy(_ => rng.Next()))
        {
            _b.CopyTo(next);
            int after = Sow(next, side, p);
            // a free turn is still ours, and does not use up the depth; only a better move than the best so far matters
            double v = Search(next, after, after == side ? depth : depth - 1, bestValue, double.PositiveInfinity, side);
            if (v > bestValue)
            {
                bestValue = v;
                best = p;
            }
        }
        return best;
    }

    /// <summary>
    /// Alpha-beta over bare boards, which it copies on the stack: no allocations, so Expert's eight moves stay quick on the
    /// UI thread. Moves that earn a free turn are tried first, as they are most often the best and cut the rest short.
    /// </summary>
    static double Search(ReadOnlySpan<int> b, int turn, int depth, double alpha, double beta, int me)
    {
        if (turn < 0 || depth <= 0) return Value(b, turn < 0, me);
        bool mine = turn == me;
        double best = mine ? double.NegativeInfinity : double.PositiveInfinity;
        Span<int> next = stackalloc int[14];
        for (int pass = 0; pass < 2; pass++)
        {
            for (int p = Pits - 1; p >= 0; p--)
            {
                int seeds = b[turn * 7 + p];
                if (seeds == 0 || (seeds == Pits - p) != (pass == 0)) continue; // pass 0: the free turns
                b.CopyTo(next);
                int after = Sow(next, turn, p);
                double v = Search(next, after, after == turn ? depth : depth - 1, alpha, beta, me);
                if (mine)
                {
                    best = Math.Max(best, v);
                    alpha = Math.Max(alpha, v);
                }
                else
                {
                    best = Math.Min(best, v);
                    beta = Math.Min(beta, v);
                }
                if (beta <= alpha) return best;
            }
        }
        return best;
    }

    /// <summary>
    /// <see cref="Play"/> on a bare board, for the search: sows <paramref name="side"/>'s <paramref name="pit"/> and
    /// returns the side to move next, or -1 once the game is over (the last seeds swept into their stores).
    /// </summary>
    static int Sow(Span<int> b, int side, int pit)
    {
        int from = side * 7 + pit, seeds = b[from], store = side == 0 ? 6 : 13, skip = side == 0 ? 13 : 6, at = from;
        b[from] = 0;
        while (seeds > 0)
        {
            at = at == 13 ? 0 : at + 1;
            if (at == skip) continue;
            b[at]++;
            seeds--;
        }
        if (at != store && (at < 7 ? 0 : 1) == side && b[at] == 1 && b[12 - at] > 0)
        {
            b[store] += b[12 - at] + 1;
            b[12 - at] = 0;
            b[at] = 0;
        }
        int left0 = 0, left1 = 0;
        for (int p = 0; p < Pits; p++)
        {
            left0 += b[p];
            left1 += b[7 + p];
        }
        if (left0 > 0 && left1 > 0) return at == store ? side : 1 - side;
        for (int p = 0; p < Pits; p++) b[p] = b[7 + p] = 0;
        b[6] += left0;
        b[13] += left1;
        return -1;
    }

    /// <summary>The store difference, with a little weight for seeds still on this side of the board.</summary>
    static double Value(ReadOnlySpan<int> b, bool over, int me)
    {
        double stores = b[Store(me)] - b[Store(1 - me)];
        if (over) return stores * 100;
        int side = 0;
        for (int p = 0; p < Pits; p++) side += b[PitOf(me, p)] - b[PitOf(1 - me, p)];
        return stores + side * 0.1;
    }
}
