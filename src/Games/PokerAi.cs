using System;
using System.Diagnostics;
using System.Linq;

namespace DeskArcade.Games;

/// <summary>
/// What a computer player knows when it has to act: its own cards, the board, the chips, and what the others did.
/// A copy, so the Expert can think about it on another thread while the table carries on.
/// </summary>
public sealed record PokerSpot(
    int[] Hole, int[] Board, int Street, int Pot, int ToCall, int Stack, int Bet, int CurrentBet, int MinRaiseTo, int MaxRaiseTo,
    bool CanRaise, int BigBlind, double[] Ranges, int ToActAfter, bool InPosition, bool Aggressor, int RaisesThisStreet,
    int OpponentRaises, int Live)
{
    /// <summary>The players still in the hand besides this one.</summary>
    public int Opponents => Ranges.Length;
}

/// <summary>A poker move: "fold", "check", "call", "raise" (to <see cref="Amount"/>) or "allin".</summary>
public readonly record struct PokerMove(string Kind, int Amount = 0);

/// <summary>
/// The computer players, at four levels (tray → CPU difficulty):
/// <list type="bullet">
/// <item>Easy plays loosely and predictably: it calls a lot, bets only with a strong made hand, always the minimum, and never bluffs.</item>
/// <item>Medium weighs its hand's chance against random hands (a quick Monte Carlo) against the pot odds.</item>
/// <item>Hard adds position (tighter early, looser last to act), reads raises as strength, and bluffs now and then:
/// continuation bets, stabs in position, semi-bluffs with draws and the odd river bluff.</item>
/// <item>Expert opens by position, estimates each opponent's range from how they have bet this hand, and works out its
/// chance against those ranges by Monte Carlo within a small time budget, then bets for value, bluffs in balance and
/// calls by the pot odds.</item>
/// </list>
/// </summary>
public static class PokerAi
{
    /// <summary>How long the Expert thinks at most at the table, and how many deals it looks at.</summary>
    public const int ExpertMillis = 150, ExpertIterations = 6000;

    /// <summary>What <paramref name="seat"/> knows right now, including a range for each opponent still in the hand.</summary>
    public static PokerSpot SpotFor(PokerRules r, int seat)
    {
        int street = Math.Min((int)r.Phase, 3);
        var opponents = Enumerable.Range(0, r.Players).Where(s => s != seat && !r.Folded[s]).ToArray();
        return new PokerSpot(
            r.Hole[seat].ToArray(), r.Board.ToArray(), street, r.Pot, r.Owed(seat), r.Stacks[seat], r.Bets[seat], r.CurrentBet,
            r.MinRaiseTo(seat), r.MaxRaiseTo(seat), r.CanRaise(seat), r.BigBlind, opponents.Select(o => RangeOf(r, o, seat)).ToArray(),
            r.ToActAfter(seat), r.InPosition(seat), street > 0 && r.Raises[seat][street - 1] > 0,
            Enumerable.Range(0, r.Players).Sum(s => r.Raises[s][street]),
            opponents.Sum(o => r.Raises[o].Sum()), r.Live);
    }

    /// <summary>
    /// The share of starting hands an opponent is likely to hold, from how they have bet this hand: a raise before the
    /// flop narrows it to the best 15 %, a re-raise to 5 %, a call to about a third; every bet or raise after the flop
    /// narrows it further, and every call a little.
    /// </summary>
    static double RangeOf(PokerRules r, int o, int me)
    {
        int preRaises = r.Raises[o][0], preCalls = r.Calls[o][0];
        bool raised = Enumerable.Range(0, r.Players).Any(s => s != o && r.Raises[s][0] > 0);
        double range = preRaises >= 2 ? 0.05 : preRaises == 1 ? 0.16 : preCalls > 0 ? raised ? 0.3 : 0.55 : 1.0;
        for (int st = 1; st <= Math.Min((int)r.Phase, 3); st++)
        {
            for (int k = 0; k < r.Raises[o][st]; k++) range *= 0.6;
            for (int k = 0; k < r.Calls[o][st]; k++) range *= 0.85;
        }
        if (r.AllIn[o]) range = Math.Min(range, 0.3);
        return Math.Clamp(range, 0.03, 1.0);
    }

    /// <summary>
    /// A move for <paramref name="spot"/> at <paramref name="level"/> 1–4. The Monte Carlo levels look at no more than
    /// <paramref name="iterations"/> deals (0: each level's own number) and, for the Expert, stop at
    /// <paramref name="millis"/>; the move is always legal for the spot.
    /// </summary>
    public static PokerMove Decide(PokerSpot spot, int level, Random rng, int iterations = 0, int millis = ExpertMillis)
    {
        var move = level switch
        {
            <= 1 => Easy(spot),
            2 => Medium(spot, rng, iterations > 0 ? iterations : 250),
            3 => Hard(spot, rng, iterations > 0 ? iterations : 400),
            _ => Expert(spot, rng, iterations > 0 ? iterations : ExpertIterations, millis),
        };
        return Legal(spot, move);
    }

    /// <summary>A move the table will take: a raise that can't be made becomes a call or a check, a check that can't be made a fold.</summary>
    static PokerMove Legal(PokerSpot s, PokerMove m)
    {
        if (m.Kind == "raise")
        {
            if (!s.CanRaise) return s.ToCall > 0 ? new("call") : new("check");
            int to = Math.Clamp(m.Amount, s.MinRaiseTo, s.MaxRaiseTo);
            return to >= s.MaxRaiseTo ? new("allin") : new("raise", to);
        }
        if (m.Kind == "check" && s.ToCall > 0) return new("fold");
        if (m.Kind == "call" && s.ToCall <= 0) return new("check");
        if (m.Kind == "fold" && s.ToCall <= 0) return new("check"); // never fold when checking is free
        return m;
    }

    /// <summary>A raise to <paramref name="fraction"/> of the pot (after calling) on top of the bet to call; all in when nearly there.</summary>
    static PokerMove Bet(PokerSpot s, double fraction)
    {
        int to = s.CurrentBet + (int)Math.Round(fraction * (s.Pot + s.ToCall));
        to = Math.Clamp(to, s.MinRaiseTo, s.MaxRaiseTo);
        if (to >= s.MaxRaiseTo * 0.85) to = s.MaxRaiseTo; // what would be left isn't worth keeping
        return new("raise", to);
    }

    static PokerMove CheckOrFold(PokerSpot s) => s.ToCall > 0 ? new("fold") : new("check");

    static double PotOdds(PokerSpot s) => s.ToCall <= 0 ? 0 : (double)s.ToCall / (s.Pot + s.ToCall);

    // ------------------------------------------------------------------ Easy

    static PokerMove Easy(PokerSpot s)
    {
        if (s.Street == 0)
        {
            double pct = PokerHand.PreflopPercentile(s.Hole[0], s.Hole[1]);
            if (pct <= 0.06 && s.RaisesThisStreet < 2) return new("raise", s.MinRaiseTo);
            if (s.ToCall == 0) return new("check");
            return s.ToCall <= s.Stack * 0.2 || pct <= 0.25 ? new("call") : new("fold");
        }
        var cat = PokerHand.CategoryOf(PokerHand.Evaluate(s.Hole.Concat(s.Board).ToArray()));
        if (cat >= PokerHand.Category.TwoPair && s.RaisesThisStreet < 2)
            return s.ToCall == 0 ? new("raise", s.CurrentBet + Math.Max(s.BigBlind, s.Pot / 2)) : new("raise", s.MinRaiseTo);
        if (s.ToCall == 0) return new("check");
        if (cat >= PokerHand.Category.Pair && s.ToCall <= s.Stack * 0.5) return new("call");
        return s.ToCall * 3 <= s.Pot ? new("call") : new("fold");
    }

    // ------------------------------------------------------------------ Medium

    static PokerMove Medium(PokerSpot s, Random rng, int iterations)
    {
        double eq = PokerHand.Equity(s.Hole, s.Board, Enumerable.Repeat(1.0, s.Opponents).ToArray(), rng, iterations, 0, out _);
        double fair = 1.0 / (s.Opponents + 1);
        double strong = fair + (1 - fair) * 0.35, monster = fair + (1 - fair) * 0.6;
        if (eq >= strong && s.RaisesThisStreet < 3) return Bet(s, eq >= monster ? 1.0 : 0.7);
        if (s.ToCall == 0) return new("check");
        return eq >= PotOdds(s) + 0.04 ? new("call") : new("fold");
    }

    // ------------------------------------------------------------------ Hard

    static PokerMove Hard(PokerSpot s, Random rng, int iterations)
    {
        double eq = PokerHand.Equity(s.Hole, s.Board, Enumerable.Repeat(1.0, s.Opponents).ToArray(), rng, iterations, 0, out _);
        eq *= Math.Pow(0.9, Math.Min(4, s.OpponentRaises)); // a raise says something
        double fair = 1.0 / (s.Opponents + 1);
        double strong = fair + (1 - fair) * 0.32, monster = fair + (1 - fair) * 0.6;
        if (s.InPosition) strong -= 0.03;
        if (s.Street == 0 && s.ToActAfter >= 3) strong += 0.04; // early position: tighter
        double odds = PotOdds(s);
        if (eq >= strong && s.RaisesThisStreet < 3)
        {
            double spr = (double)s.Stack / Math.Max(1, s.Pot);
            return eq >= monster && spr < 1.5 ? new("raise", s.MaxRaiseTo) : Bet(s, 0.55 + rng.NextDouble() * 0.45);
        }
        if (s.ToCall == 0)
        {
            if (s.CanRaise && s.Street > 0)
            {
                if (s.Aggressor && s.Street == 1 && s.Opponents <= 2 && rng.NextDouble() < 0.6) return Bet(s, 0.5); // continuation bet
                if (s.InPosition && s.Opponents == 1 && rng.NextDouble() < 0.3) return Bet(s, 0.6);
                if (s.Street == 3 && s.InPosition && eq < 0.2 && rng.NextDouble() < 0.15) return Bet(s, 0.75);
            }
            return new("check");
        }
        if (s.Street is 1 or 2 && s.CanRaise && eq >= odds && eq < odds + 0.15 && rng.NextDouble() < 0.15) return Bet(s, 0.7); // semi-bluff
        double implied = s.Street is 1 or 2 ? 0.03 : 0;
        return eq + implied >= odds + 0.02 ? new("call") : new("fold");
    }

    // ------------------------------------------------------------------ Expert

    static PokerMove Expert(PokerSpot s, Random rng, int iterations, int millis)
    {
        long until = Stopwatch.GetTimestamp() + (long)(millis * (Stopwatch.Frequency / 1000.0));
        double odds = PotOdds(s);
        if (s.Street == 0)
        {
            double pct = PokerHand.PreflopPercentile(s.Hole[0], s.Hole[1]);
            bool unopened = s.RaisesThisStreet == 0;
            if (unopened)
            {
                // open-raise by position: wider the fewer players are left to act, widest heads-up
                double open = s.Live == 2 ? 0.6 : s.ToActAfter switch { 0 => 0.5, 1 => 0.42, 2 => 0.3, _ => 0.2 };
                int limped = Math.Max(0, s.Pot - s.BigBlind * 3 / 2); // a big blind more for everyone who just called
                if (pct <= open) return new("raise", (int)(s.BigBlind * (s.Live == 2 ? 2.5 : 3)) + limped);
                if (s.ToCall == 0) return new("check");
                return pct <= open + 0.15 && s.ToCall <= s.BigBlind / 2 ? new("call") : new("fold");
            }
        }
        double eq = PokerHand.Equity(s.Hole, s.Board, s.Ranges, rng, iterations, until, out _);
        double fair = 1.0 / (s.Opponents + 1);
        double value = fair + (1 - fair) * 0.28, nuts = fair + (1 - fair) * 0.62;
        double spr = (double)s.Stack / Math.Max(1, s.Pot);
        if (eq >= value && s.RaisesThisStreet < 3)
        {
            if (eq >= nuts && spr < 2) return new("raise", s.MaxRaiseTo);
            if (s.Street == 0) return Bet(s, 0.8 + rng.NextDouble() * 0.4); // a re-raise before the flop
            return Bet(s, eq >= nuts ? 0.9 : 0.6 + rng.NextDouble() * 0.2);
        }
        if (s.ToCall == 0)
        {
            if (s.CanRaise && s.Street > 0)
            {
                double bluff = s.Aggressor && s.Street == 1 ? 0.55 : s.InPosition && s.Opponents == 1 ? 0.35 : s.Opponents == 1 ? 0.15 : 0.05;
                if (s.Street == 3) bluff *= eq < 0.25 ? 0.7 : 0.2; // river bluffs with hands that can't win a showdown
                if (rng.NextDouble() < bluff) return Bet(s, 0.5 + rng.NextDouble() * 0.25);
            }
            return new("check");
        }
        if (s.Street is 1 or 2 && s.CanRaise && eq >= 0.3 && eq < value && s.Opponents == 1 && rng.NextDouble() < 0.2) return Bet(s, 0.75); // semi-bluff
        double implied = s.Street is 1 or 2 && spr > 3 ? 0.03 : 0;
        return eq + implied >= odds ? new("call") : new("fold");
    }
}
