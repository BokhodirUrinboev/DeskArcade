using System;
using System.Collections.Generic;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>
/// Where a dart landed: the number (1–20, 25 for either bull, 0 for a miss), the multiplier (0 miss, 1 single,
/// 2 double, 3 treble) and the points. The bull (50) counts as a double, so it can finish a leg.
/// </summary>
public readonly record struct Segment(int Number, int Multiplier, int Points)
{
    public static readonly Segment Miss = new(0, 0, 0);
    public static readonly Segment OuterBull = new(25, 1, 25);
    public static readonly Segment Bull = new(25, 2, 50);

    public static Segment Single(int n) => new(n, 1, n);
    public static Segment Double(int n) => new(n, 2, 2 * n);
    public static Segment Treble(int n) => new(n, 3, 3 * n);

    public bool IsDouble => Multiplier == 2;
    public bool IsBull => Number == 25;

    /// <summary>Darts notation: "T20", "D16", "5", "25", "BULL", or "" for a miss.</summary>
    public override string ToString() => Multiplier switch
    {
        0 => "",
        _ when Points == 50 => "BULL",
        _ when Number == 25 => "25",
        3 => "T" + Number,
        2 => "D" + Number,
        _ => Number.ToString(),
    };
}

/// <summary>
/// The dartboard and the 501 checkout table, without any UI. Ring radii are the regulation millimetres over the
/// 170 mm double ring, so they scale with any board size; angles run clockwise from the top (20) in screen
/// coordinates (y down).
/// </summary>
public static class DartsRules
{
    public const double Mm = 170;
    public const double BullR = 6.35 / Mm, OuterBullR = 15.9 / Mm;
    public const double TrebleIn = 99 / Mm, TrebleOut = 107 / Mm, DoubleIn = 162 / Mm, DoubleOut = 1;

    /// <summary>The numbers clockwise from the top.</summary>
    public static readonly int[] Order = { 20, 1, 18, 4, 13, 6, 10, 15, 2, 17, 3, 19, 7, 16, 8, 11, 14, 9, 12, 5 };

    /// <summary>What a dart <paramref name="offset"/> from the centre of a board with this double-ring radius scores.</summary>
    public static Segment Score(Vec2 offset, double boardRadius)
    {
        double r = offset.Length / boardRadius;
        if (r <= BullR) return Segment.Bull;
        if (r <= OuterBullR) return Segment.OuterBull;
        if (r > DoubleOut) return Segment.Miss;

        double deg = Math.Atan2(offset.X, -offset.Y) * 180 / Math.PI; // 0 at the top, clockwise
        int n = Order[(int)Math.Floor((deg + 9 + 360) / 18) % 20];
        if (r >= TrebleIn && r <= TrebleOut) return Segment.Treble(n);
        if (r >= DoubleIn) return Segment.Double(n);
        return Segment.Single(n);
    }

    /// <summary>Clockwise angle from the top (degrees) of the middle of a number's wedge.</summary>
    public static double AngleOf(int number) => 18 * Array.IndexOf(Order, number);

    /// <summary>The middle of a segment, as an offset from the board centre: where a player aims for it.</summary>
    public static Vec2 AimPoint(Segment s, double boardRadius)
    {
        if (s.Multiplier == 0) return new Vec2(0, -boardRadius * 1.1);
        if (s.Points == 50) return default;
        double r = s.IsBull ? (BullR + OuterBullR) / 2
            : s.Multiplier == 3 ? (TrebleIn + TrebleOut) / 2
            : s.Multiplier == 2 ? (DoubleIn + DoubleOut) / 2
            : (TrebleOut + DoubleIn) / 2; // the big single between the rings is the easy one to hit
        if (s.IsBull) return new Vec2(0, -r * boardRadius);
        double a = AngleOf(s.Number) * Math.PI / 180;
        return new Vec2(Math.Sin(a), -Math.Cos(a)) * (r * boardRadius);
    }

    /// <summary>A normally distributed offset (Box–Muller) with this standard deviation on each axis.</summary>
    public static Vec2 Scatter(Random rng, double sigma)
    {
        double u = 1 - rng.NextDouble(), v = rng.NextDouble();
        double m = Math.Sqrt(-2 * Math.Log(u)) * sigma;
        return new Vec2(Math.Cos(2 * Math.PI * v) * m, Math.Sin(2 * Math.PI * v) * m);
    }

    // ------------------------------------------------------------------ checkouts

    static readonly Segment[] Setups = BuildSetups();
    static readonly Segment[] Finishes = BuildFinishes();
    static readonly Segment[]?[,] Table = new Segment[]?[171, 4];
    static readonly bool[,] Known = new bool[171, 4];

    // listed from the most to the least common, so equal routes resolve the usual way
    static Segment[] BuildSetups()
    {
        var list = new List<Segment> { Segment.Treble(20), Segment.Treble(19) };
        for (int n = 20; n >= 1; n--) list.Add(Segment.Single(n));
        for (int n = 18; n >= 1; n--) list.Add(Segment.Treble(n));
        list.Add(Segment.OuterBull);
        list.Add(Segment.Bull);
        for (int n = 20; n >= 1; n--) list.Add(Segment.Double(n));
        return list.ToArray();
    }

    static Segment[] BuildFinishes()
    {
        var list = new List<Segment> { Segment.Double(16), Segment.Double(20), Segment.Double(8), Segment.Bull };
        for (int n = 20; n >= 1; n--)
            if (n is not (16 or 20 or 8)) list.Add(Segment.Double(n));
        return list.ToArray();
    }

    /// <summary>How awkward a dart is to set up a finish with: trebles 20 and 19 and the big singles are the usual ones.</summary>
    static double SetupCost(Segment s) => s.Multiplier switch
    {
        3 when s.Number == 20 => 0,
        3 when s.Number == 19 => 1,
        3 when s.Number is 18 or 17 => 3,
        3 => 4,
        1 when s.IsBull => 3,
        1 => 1.5,
        _ => 5, // a double or the bull just to set up
    };

    static double FinishCost(Segment s) =>
        s.Points == 50 || s.Number is 16 or 20 or 8 ? 0 : s.Number is 10 or 12 or 18 or 4 ? 1 : 2;

    /// <summary>
    /// The easiest way to finish <paramref name="remaining"/> with at most <paramref name="darts"/> darts, ending on a
    /// double: the fewest darts first, then the most common route. Null when it can't be done (e.g. 169).
    /// </summary>
    public static IReadOnlyList<Segment>? Checkout(int remaining, int darts = 3)
    {
        if (remaining < 2 || remaining > 170 || darts < 1) return null;
        darts = Math.Min(darts, 3);
        lock (Table)
        {
            if (!Known[remaining, darts])
            {
                Table[remaining, darts] = Search(remaining, darts);
                Known[remaining, darts] = true;
            }
            return Table[remaining, darts];
        }
    }

    static Segment[]? Search(int remaining, int darts)
    {
        foreach (var f in Finishes)
            if (f.Points == remaining) return new[] { f };
        if (darts < 2) return null;

        Segment[]? best = null;
        double bestCost = double.MaxValue;
        foreach (var a in Setups)
        {
            int rest = remaining - a.Points;
            if (rest < 2) continue;
            foreach (var f in Finishes)
            {
                if (f.Points != rest) continue;
                double cost = SetupCost(a) + FinishCost(f);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = new[] { a, f };
                }
            }
        }
        if (best != null || darts < 3) return best;

        foreach (var a in Setups)
        {
            foreach (var b in Setups)
            {
                int rest = remaining - a.Points - b.Points;
                if (rest < 2) continue;
                foreach (var f in Finishes)
                {
                    if (f.Points != rest) continue;
                    double cost = SetupCost(a) + SetupCost(b) + FinishCost(f);
                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        best = new[] { a, b, f };
                    }
                }
            }
        }
        return best;
    }

    /// <summary>Checkout as text, e.g. "T20 11 BULL".</summary>
    public static string Describe(IReadOnlyList<Segment> route) => string.Join(" ", route);

    /// <summary>
    /// What the self-playing demo aims at: the first dart of a checkout when there is one, otherwise the treble 20,
    /// or whatever leaves a finish for the next turn without busting.
    /// </summary>
    public static Segment DemoTarget(int remaining, int dartsLeft)
    {
        var route = Checkout(remaining, dartsLeft);
        if (route != null) return route[0];
        foreach (var s in Setups)
        {
            if (s.Multiplier == 2) continue;
            int rest = remaining - s.Points;
            if (rest >= 2 && (rest > 170 || Checkout(rest) != null)) return s;
        }
        return Segment.Single(1);
    }
}

/// <summary>The outcome of one dart in a game of <see cref="X01"/>.</summary>
public readonly record struct DartResult(Segment Segment, bool Bust, bool Checkout, bool TurnOver, int TurnTotal);

/// <summary>
/// A leg of 501, double out, three darts a turn. A bust (below 0, left on 1, or 0 not on a double) puts the score
/// back to what it was at the start of the turn and ends the turn.
/// </summary>
public sealed class X01
{
    public const int DartsPerTurn = 3;

    public X01(int start = 501)
    {
        Start = start;
        NewLeg();
    }

    public int Start { get; }
    public int Remaining { get; private set; }
    public int TurnStart { get; private set; }
    /// <summary>Darts thrown in the current turn (0–2 while it is running).</summary>
    public int DartsInTurn { get; private set; }
    /// <summary>Darts thrown in the whole leg, busted ones included.</summary>
    public int DartsUsed { get; private set; }
    public bool Finished { get; private set; }
    public int DartsLeft => DartsPerTurn - DartsInTurn;

    public void NewLeg()
    {
        Remaining = TurnStart = Start;
        DartsInTurn = DartsUsed = 0;
        Finished = false;
    }

    public DartResult Throw(Segment s)
    {
        if (Finished) throw new InvalidOperationException("The leg is over.");
        DartsUsed++;
        DartsInTurn++;
        int left = Remaining - s.Points;
        if (left < 0 || left == 1 || (left == 0 && !s.IsDouble))
        {
            Remaining = TurnStart;
            EndTurn();
            return new DartResult(s, true, false, true, 0);
        }

        Remaining = left;
        int total = TurnStart - Remaining;
        if (left == 0)
        {
            Finished = true;
            EndTurn();
            return new DartResult(s, false, true, true, total);
        }
        if (DartsInTurn < DartsPerTurn) return new DartResult(s, false, false, false, total);
        EndTurn();
        return new DartResult(s, false, false, true, total);
    }

    void EndTurn()
    {
        TurnStart = Remaining;
        DartsInTurn = 0;
    }
}
