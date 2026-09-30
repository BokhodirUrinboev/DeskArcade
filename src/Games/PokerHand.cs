using System;
using System.Collections.Generic;
using System.Linq;

namespace DeskArcade.Games;

/// <summary>
/// Poker hands, free of UI: the value of the best five of five to seven cards, which five those are, the hand's name,
/// how good two hole cards are before the flop, and a hand's chance against other hands (Monte Carlo). Cards are 0–51:
/// the suit is card / 13 (♠ 0, ♣ 1, ♦ 2, ♥ 3, as <see cref="DurakGame.CardFace(int, int, double, double)"/> draws them)
/// and the rank card % 13 + 2, from the two (2) to the ace (14).
/// </summary>
public static class PokerHand
{
    public enum Category { HighCard, Pair, TwoPair, Trips, Straight, Flush, FullHouse, Quads, StraightFlush }

    public const int Deck = 52;

    public static int Suit(int card) => card / 13;
    public static int Rank(int card) => card % 13 + 2;
    public static int Card(int rank, int suit) => suit * 13 + rank - 2;

    /// <summary>A card from text such as "As", "Td", "9h" or "2c" (for tests and tables of known hands).</summary>
    public static int Parse(string text)
    {
        text = text.Trim();
        int rank = "23456789TJQKA".IndexOf(char.ToUpperInvariant(text[0])) + 2;
        int suit = "scdh".IndexOf(char.ToLowerInvariant(text[^1]));
        if (rank < 2 || suit < 0 || text.Length != 2) throw new FormatException("not a card: " + text);
        return Card(rank, suit);
    }

    /// <summary>Several cards from text separated by spaces: "As Ks Qs Js Ts".</summary>
    public static int[] ParseMany(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Parse).ToArray();

    /// <summary>The category of a value from <see cref="Evaluate(ReadOnlySpan{int})"/>.</summary>
    public static Category CategoryOf(int value) => (Category)(value >> 20);

    /// <summary>The ranks a value was built from, most important first (the pair, then the kickers...).</summary>
    public static int[] RanksOf(int value) => new[] { (value >> 16) & 15, (value >> 12) & 15, (value >> 8) & 15, (value >> 4) & 15, value & 15 };

    static int Make(Category c, int r1 = 0, int r2 = 0, int r3 = 0, int r4 = 0, int r5 = 0) =>
        (int)c << 20 | r1 << 16 | r2 << 12 | r3 << 8 | r4 << 4 | r5;

    public static int Evaluate(IReadOnlyList<int> cards)
    {
        Span<int> span = stackalloc int[cards.Count];
        for (int i = 0; i < cards.Count; i++) span[i] = cards[i];
        return Evaluate(span);
    }

    /// <summary>
    /// The value of the best five of these five to seven cards: a bigger value is a better hand, and equal values tie.
    /// The category sits in the high bits and the deciding ranks below it, most important first.
    /// </summary>
    public static int Evaluate(ReadOnlySpan<int> cards)
    {
        Span<int> counts = stackalloc int[15];
        Span<int> suitMask = stackalloc int[4];
        Span<int> suitCount = stackalloc int[4];
        int rankMask = 0;
        foreach (int c in cards)
        {
            int r = c % 13 + 2, s = c / 13;
            counts[r]++;
            suitMask[s] |= 1 << r;
            suitCount[s]++;
            rankMask |= 1 << r;
        }
        // with seven cards a flush rules out four of a kind and a full house, so it can be settled first
        for (int s = 0; s < 4; s++)
        {
            if (suitCount[s] < 5) continue;
            int high = StraightHigh(suitMask[s]);
            if (high > 0) return Make(Category.StraightFlush, high);
            Span<int> top = stackalloc int[5];
            TopRanks(suitMask[s], top, 5);
            return Make(Category.Flush, top[0], top[1], top[2], top[3], top[4]);
        }
        int quad = 0, trip1 = 0, trip2 = 0, pair1 = 0, pair2 = 0, pair3 = 0;
        for (int r = 14; r >= 2; r--)
        {
            switch (counts[r])
            {
                case 4: quad = r; break;
                case 3:
                    if (trip1 == 0) trip1 = r;
                    else if (trip2 == 0) trip2 = r;
                    break;
                case 2:
                    if (pair1 == 0) pair1 = r;
                    else if (pair2 == 0) pair2 = r;
                    else if (pair3 == 0) pair3 = r;
                    break;
            }
        }
        if (quad > 0) return Make(Category.Quads, quad, HighestExcept(rankMask, quad));
        if (trip1 > 0 && (trip2 > 0 || pair1 > 0)) return Make(Category.FullHouse, trip1, Math.Max(trip2, pair1));
        int straight = StraightHigh(rankMask);
        if (straight > 0) return Make(Category.Straight, straight);
        if (trip1 > 0)
        {
            int k1 = HighestExcept(rankMask, trip1), k2 = HighestExcept(rankMask, trip1, k1);
            return Make(Category.Trips, trip1, k1, k2);
        }
        if (pair2 > 0) return Make(Category.TwoPair, pair1, pair2, HighestExcept(rankMask, pair1, pair2));
        if (pair1 > 0)
        {
            int k1 = HighestExcept(rankMask, pair1), k2 = HighestExcept(rankMask, pair1, k1), k3 = HighestExcept(rankMask, pair1, k1, k2);
            return Make(Category.Pair, pair1, k1, k2, k3);
        }
        Span<int> high5 = stackalloc int[5];
        TopRanks(rankMask, high5, 5);
        return Make(Category.HighCard, high5[0], high5[1], high5[2], high5[3], high5[4]);
    }

    /// <summary>The top card of the best straight in a set of ranks (5 for the wheel, A-2-3-4-5), or 0 when there is none.</summary>
    static int StraightHigh(int mask)
    {
        if ((mask & 1 << 14) != 0) mask |= 1 << 1; // the ace plays low too
        for (int high = 14; high >= 5; high--)
            if ((mask >> (high - 4) & 0x1F) == 0x1F) return high;
        return 0;
    }

    static void TopRanks(int mask, Span<int> into, int n)
    {
        int k = 0;
        for (int r = 14; r >= 2 && k < n; r--)
            if ((mask & 1 << r) != 0) into[k++] = r;
    }

    static int HighestExcept(int mask, int a, int b = 0, int c = 0)
    {
        for (int r = 14; r >= 2; r--)
            if ((mask & 1 << r) != 0 && r != a && r != b && r != c) return r;
        return 0;
    }

    /// <summary>The five cards that make the best hand of these five to seven, in the order they read (the pair first...).</summary>
    public static int[] BestFive(IReadOnlyList<int> cards)
    {
        if (cards.Count < 5) return cards.ToArray();
        int best = -1;
        int[] five = Array.Empty<int>();
        Span<int> pick = stackalloc int[5];
        int n = cards.Count;
        for (int a = 0; a < n; a++)
            for (int b = a + 1; b < n; b++)
                for (int c = b + 1; c < n; c++)
                    for (int d = c + 1; d < n; d++)
                        for (int e = d + 1; e < n; e++)
                        {
                            pick[0] = cards[a]; pick[1] = cards[b]; pick[2] = cards[c]; pick[3] = cards[d]; pick[4] = cards[e];
                            int v = Evaluate(pick);
                            if (v <= best) continue;
                            best = v;
                            five = pick.ToArray();
                        }
        // the cards that make the hand first (by how many of their rank there are), then by rank
        var ranks = five.GroupBy(Rank).ToDictionary(g => g.Key, g => g.Count());
        var cat = CategoryOf(best);
        bool wheel = cat is Category.Straight or Category.StraightFlush && RanksOf(best)[0] == 5;
        return five.OrderByDescending(c => ranks[Rank(c)]).ThenByDescending(c => wheel && Rank(c) == 14 ? 1 : Rank(c)).ToArray();
    }

    // ------------------------------------------------------------------ names

    static readonly string[] RankSingular = { "", "", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "jack", "queen", "king", "ace" };
    static readonly string[] RankPlural = { "", "", "twos", "threes", "fours", "fives", "sixes", "sevens", "eights", "nines", "tens", "jacks", "queens", "kings", "aces" };

    /// <summary>A rank as the cards print it: 2–10, J, Q, K, A.</summary>
    public static string RankLetter(int rank) => rank switch { 14 => "A", 13 => "K", 12 => "Q", 11 => "J", _ => rank.ToString(System.Globalization.CultureInfo.InvariantCulture) };

    /// <summary>A rank's name for the hand names ("king", or "kings" with <paramref name="plural"/>), translated.</summary>
    public static string RankName(int rank, bool plural) => L.T(plural ? RankPlural[rank] : RankSingular[rank]);

    /// <summary>The hand a value stands for, as a player would say it: "Pair of kings", "Full house, sevens over fours", "Straight, J high".</summary>
    public static string Describe(int value)
    {
        var r = RanksOf(value);
        return CategoryOf(value) switch
        {
            Category.HighCard => L.F("High card, {0}", RankName(r[0], false)),
            Category.Pair => L.F("Pair of {0}", RankName(r[0], true)),
            Category.TwoPair => L.F("Two pair, {0} and {1}", RankName(r[0], true), RankName(r[1], true)),
            Category.Trips => L.F("Three of a kind, {0}", RankName(r[0], true)),
            Category.Straight => L.F("Straight, {0} high", RankLetter(r[0])),
            Category.Flush => L.F("Flush, {0} high", RankLetter(r[0])),
            Category.FullHouse => L.F("Full house, {0} over {1}", RankName(r[0], true), RankName(r[1], true)),
            Category.Quads => L.F("Four of a kind, {0}", RankName(r[0], true)),
            _ => r[0] == 14 ? L.T("Royal flush") : L.F("Straight flush, {0} high", RankLetter(r[0])),
        };
    }

    /// <summary>What two hole cards are called before the flop: "Pair of kings", "Ace and king, suited".</summary>
    public static string DescribeHole(int a, int b)
    {
        int hi = Math.Max(Rank(a), Rank(b)), lo = Math.Min(Rank(a), Rank(b));
        if (hi == lo) return L.F("Pair of {0}", RankName(hi, true));
        string name = L.F("{0} and {1}", Capital(RankName(hi, false)), RankName(lo, false));
        return Suit(a) == Suit(b) ? L.F("{0}, suited", name) : name;
    }

    static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // ------------------------------------------------------------------ before the flop

    static double[]? _preflop;

    /// <summary>
    /// How strong two hole cards are before the flop, as the share of all 1,326 starting hands that are at least as good:
    /// about 0.005 for a pair of aces, 1 for seven-two offsuit. Ranked by the Chen formula.
    /// </summary>
    public static double PreflopPercentile(int a, int b)
    {
        _preflop ??= BuildPreflop();
        return _preflop[PreflopKey(a, b)];
    }

    static int PreflopKey(int a, int b)
    {
        int hi = Math.Max(Rank(a), Rank(b)), lo = Math.Min(Rank(a), Rank(b));
        return ((hi - 2) * 13 + (lo - 2)) * 2 + (Suit(a) == Suit(b) && hi != lo ? 1 : 0);
    }

    /// <summary>The Chen formula's score for two hole cards (−1.5 to 20).</summary>
    public static double ChenScore(int a, int b)
    {
        int hi = Math.Max(Rank(a), Rank(b)), lo = Math.Min(Rank(a), Rank(b));
        static double Points(int r) => r switch { 14 => 10, 13 => 8, 12 => 7, 11 => 6, _ => r / 2.0 };
        if (hi == lo) return Math.Max(5, Points(hi) * 2);
        double score = Points(hi);
        if (Suit(a) == Suit(b)) score += 2;
        int gap = hi - lo - 1;
        score -= gap switch { 0 => 0, 1 => 1, 2 => 2, 3 => 4, _ => 5 };
        if (gap <= 1 && hi < 12) score += 1;
        return Math.Ceiling(score);
    }

    static double[] BuildPreflop()
    {
        // every class of starting hand, weighted by how many ways it can be dealt, strongest first
        var classes = new List<(int Key, double Score, int Combos)>();
        for (int hi = 2; hi <= 14; hi++)
            for (int lo = 2; lo <= hi; lo++)
            {
                if (hi == lo) classes.Add((PreflopKey(Card(hi, 0), Card(lo, 1)), ChenScore(Card(hi, 0), Card(lo, 1)) + hi * 0.01, 6));
                else
                {
                    classes.Add((PreflopKey(Card(hi, 0), Card(lo, 0)), ChenScore(Card(hi, 0), Card(lo, 0)) + (hi * 15 + lo) * 0.0001, 4));
                    classes.Add((PreflopKey(Card(hi, 0), Card(lo, 1)), ChenScore(Card(hi, 0), Card(lo, 1)) + (hi * 15 + lo) * 0.0001, 12));
                }
            }
        var table = new double[13 * 13 * 2];
        double seen = 0;
        foreach (var c in classes.OrderByDescending(c => c.Score))
        {
            seen += c.Combos;
            table[c.Key] = seen / 1326.0;
        }
        return table;
    }

    // ------------------------------------------------------------------ equity

    /// <summary>
    /// The share of the pot <paramref name="hole"/> can expect against <paramref name="ranges"/>.Length opponents once
    /// the board is complete (a split counts its share), by dealing out random hands: each opponent's two cards come
    /// from the best <c>ranges[i]</c> share of starting hands (1 = any two cards). Stops after
    /// <paramref name="iterations"/> deals, or when <paramref name="until"/> (Stopwatch ticks) comes, whichever is first;
    /// <paramref name="done"/> says how many deals it managed.
    /// </summary>
    public static double Equity(IReadOnlyList<int> hole, IReadOnlyList<int> board, IReadOnlyList<double> ranges, Random rng, int iterations,
        long until, out int done)
    {
        done = 0;
        int opponents = ranges.Count;
        if (opponents == 0) return 1;
        ulong fixedMask = 0;
        foreach (int c in hole) fixedMask |= 1UL << c;
        foreach (int c in board) fixedMask |= 1UL << c;
        Span<int> mine = stackalloc int[7];
        Span<int> theirs = stackalloc int[7];
        Span<int> full = stackalloc int[5];
        Span<int> opp = stackalloc int[opponents * 2];
        double total = 0;
        for (int it = 0; it < iterations; it++)
        {
            if (it > 0 && (it & 31) == 0 && until > 0 && System.Diagnostics.Stopwatch.GetTimestamp() >= until) break;
            ulong used = fixedMask;
            for (int o = 0; o < opponents; o++)
            {
                double range = ranges[o];
                int a = 0, b = 0;
                for (int tries = 0; tries < 30; tries++)
                {
                    a = Draw(rng, ref used);
                    b = Draw(rng, ref used);
                    if (range >= 0.999 || tries == 29 || PreflopPercentile(a, b) <= range) break;
                    used &= ~(1UL << a | 1UL << b); // outside their range: put them back and deal again
                }
                opp[o * 2] = a;
                opp[o * 2 + 1] = b;
            }
            for (int i = 0; i < 5; i++) full[i] = i < board.Count ? board[i] : Draw(rng, ref used);
            mine[0] = hole[0];
            mine[1] = hole[1];
            full.CopyTo(mine[2..]);
            int me = Evaluate(mine);
            int ties = 0;
            bool beaten = false;
            full.CopyTo(theirs[2..]);
            for (int o = 0; o < opponents; o++)
            {
                theirs[0] = opp[o * 2];
                theirs[1] = opp[o * 2 + 1];
                int v = Evaluate(theirs);
                if (v > me)
                {
                    beaten = true;
                    break;
                }
                if (v == me) ties++;
            }
            if (!beaten) total += 1.0 / (ties + 1);
            done++;
        }
        return done == 0 ? 0 : total / done;
    }

    /// <summary>Equity against <paramref name="opponents"/> random hands, <paramref name="iterations"/> deals (the beginner's hint).</summary>
    public static double Equity(IReadOnlyList<int> hole, IReadOnlyList<int> board, int opponents, Random rng, int iterations) =>
        Equity(hole, board, Enumerable.Repeat(1.0, opponents).ToArray(), rng, iterations, 0, out _);

    static int Draw(Random rng, ref ulong used)
    {
        while (true)
        {
            int c = rng.Next(Deck);
            ulong bit = 1UL << c;
            if ((used & bit) != 0) continue;
            used |= bit;
            return c;
        }
    }
}
