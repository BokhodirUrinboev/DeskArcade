using System;
using System.Collections.Generic;
using System.Linq;
using static DeskArcade.Games.HeartsRules;

namespace DeskArcade.Games;

/// <summary>
/// The computer players for Hearts, at four levels (tray → CPU difficulty). They only use what their seat could know:
/// their own hand, the cards they passed, and every card played so far.
/// <list type="bullet">
/// <item>Easy plays any card the rules allow, and passes three at random.</item>
/// <item>Medium passes its most dangerous cards, ducks under the trick when it can, throws the queen and high hearts
/// away when it can't follow, and leads its lowest card.</item>
/// <item>Hard counts cards: it remembers who has run out of a suit, passes to empty a short suit, keeps a queen that
/// enough low spades protect, leads low spades to smoke the queen out, avoids leading into a void, and takes a clean
/// trick with its highest card when it plays last.</item>
/// <item>Expert also counts which higher cards are still out to pick the safest lead, throws from its shortest suit,
/// never feeds points to a player going for the moon, and stops a moon shot by taking a point itself.</item>
/// </list>
/// </summary>
public static class HeartsAi
{
    /// <summary>What a seat knows: the cards played, who has shown out of which suit, and who may be shooting the moon.</summary>
    sealed class Read
    {
        public readonly bool[] Gone = new bool[52];
        public readonly bool[,] Void = new bool[Players, 4];
        public readonly int Shooter = -1;
        public readonly bool QueenOut; // not played yet and not in my hand

        public Read(HeartsRules r, int me)
        {
            int ledTrick = -1, led = -1;
            foreach (var (seat, card, trick) in r.History)
            {
                Gone[card] = true;
                if (trick != ledTrick)
                {
                    ledTrick = trick;
                    led = Suit(card);
                }
                else if (Suit(card) != led) Void[seat, led] = true;
            }
            QueenOut = !Gone[QueenOfSpades] && !r.Hands[me].Contains(QueenOfSpades);
            // one opponent has taken every point so far, and plenty of them: a moon shot is on
            var takers = Enumerable.Range(0, Players).Where(s => r.Taken[s] > 0).ToList();
            if (takers.Count == 1 && takers[0] != me && (r.Taken[takers[0]] >= 13 || r.Taken[takers[0]] >= 5 && r.TrickNumber >= 5))
                Shooter = takers[0];
        }

        /// <summary>Unplayed cards of <paramref name="card"/>'s suit above it that aren't in <paramref name="hand"/>.</summary>
        public int HigherOut(int card, IReadOnlyList<int> hand)
        {
            int n = 0;
            for (int rank = Rank(card) + 1; rank <= 14; rank++)
            {
                int c = PokerHand.Card(rank, Suit(card));
                if (!Gone[c] && !hand.Contains(c)) n++;
            }
            return n;
        }
    }

    // ------------------------------------------------------------------ passing

    /// <summary>The three cards <paramref name="seat"/> passes this hand.</summary>
    public static int[] ChoosePass(HeartsRules r, int seat, int level, Random rng)
    {
        var hand = r.Hands[seat];
        if (level <= 1) return hand.OrderBy(_ => rng.Next()).Take(3).ToArray();
        int lowSpades = hand.Count(c => Suit(c) == Spades && Rank(c) < 12);
        bool queen = hand.Contains(QueenOfSpades);
        int keepQueen = level >= 4 ? 4 : 3; // low spades enough to protect the queen
        double Danger(int c)
        {
            int rank = Rank(c), suit = Suit(c);
            if (level == 2) return c == QueenOfSpades ? 100 : suit == Spades && rank > 12 ? 90 + rank : suit == Hearts ? 20 + rank : rank;
            if (c == QueenOfSpades) return lowSpades >= keepQueen ? 5 : 100;
            if (suit == Spades && rank > 12) return lowSpades >= keepQueen && !queen ? 30 + rank : 80 + rank;
            if (suit == Spades) return rank * 0.3; // low spades protect: keep them
            if (suit == Hearts) return 26 + rank;
            int count = hand.Count(x => Suit(x) == suit);
            return rank + (count <= 3 ? 20 - count * 2 : 0); // empty a short suit
        }
        return hand.OrderByDescending(Danger).ThenBy(_ => rng.Next()).Take(3).ToArray();
    }

    // ------------------------------------------------------------------ playing

    /// <summary>The card <paramref name="seat"/> plays; it is always one the rules allow.</summary>
    public static int ChoosePlay(HeartsRules r, int seat, int level, Random rng)
    {
        var legal = r.LegalCards(seat);
        if (legal.Count == 1 || level <= 1) return legal[rng.Next(legal.Count)];
        var k = new Read(r, seat);
        int led = r.LedSuit;
        if (led < 0) return Lead(r, seat, legal, k, level);
        return Suit(legal[0]) == led ? Follow(r, seat, legal, k, level) : Discard(r, seat, legal, k, level);
    }

    static int Lowest(IEnumerable<int> cards) => cards.OrderBy(Rank).ThenBy(c => c).First();
    static int Highest(IEnumerable<int> cards) => cards.OrderByDescending(Rank).ThenBy(c => c).First();

    static int Lead(HeartsRules r, int seat, List<int> legal, Read k, int level)
    {
        var hand = r.Hands[seat];
        if (level >= 4 && k.Shooter >= 0)
        {
            // a heart nobody can beat takes a point and ends the moon shot
            var winner = legal.Where(c => Suit(c) == Hearts && k.HigherOut(c, hand) == 0).ToList();
            if (winner.Count > 0) return Highest(winner);
        }
        var safe = legal.Where(c => c != QueenOfSpades).ToList();
        if (safe.Count == 0) return legal[0];
        if (level == 2) return Lowest(safe.Where(c => Suit(c) != Hearts).DefaultIfEmpty(Lowest(safe)));

        // smoke the queen out with spades below her, when she is still out and we hold no ace or king of spades
        var spades = safe.Where(c => Suit(c) == Spades).ToList();
        if (k.QueenOut && spades.Count > 0 && spades.All(c => Rank(c) < 12)) return Highest(spades);

        bool bigSpades = hand.Any(c => Suit(c) == Spades && Rank(c) > 12);
        double Risk(int c)
        {
            int suit = Suit(c);
            double risk = Rank(c);
            // an opponent who has shown out of the suit throws points on it
            for (int s = 1; s < Players; s++)
                if (k.Void[(seat + s) % Players, suit]) risk += 30;
            if (suit == Spades && k.QueenOut && Rank(c) > 12) risk += 60;
            if (suit == Spades && bigSpades && k.QueenOut) risk += 8;
            if (suit == Hearts) risk += 6;
            if (level >= 4) risk -= 3 * k.HigherOut(c, hand); // more higher cards out: somebody else takes it
            return risk;
        }
        return safe.OrderBy(Risk).ThenBy(c => c).First();
    }

    static int Follow(HeartsRules r, int seat, List<int> legal, Read k, int level)
    {
        int led = r.LedSuit;
        int winning = r.Trick.Where(c => c >= 0 && Suit(c) == led).OrderByDescending(Rank).First();
        int winnerSeat = Array.IndexOf(r.Trick, winning);
        int points = r.Trick.Where(c => c >= 0).Sum(PointsOf);
        int after = r.Trick.Count(c => c < 0) - 1;
        bool last = after == 0;

        // the queen goes under a higher spade
        if (led == Spades && legal.Contains(QueenOfSpades) && Rank(winning) > 12) return QueenOfSpades;

        if (level >= 4 && k.Shooter >= 0 && winnerSeat == k.Shooter)
        {
            // take the trick from the shooter if it holds a point, or will
            var over = legal.Where(c => Rank(c) > Rank(winning) && c != QueenOfSpades).ToList();
            if (over.Count > 0 && (points > 0 || led == Hearts || !last)) return Lowest(over);
        }

        var under = legal.Where(c => Rank(c) < Rank(winning)).ToList();
        if (under.Count > 0) return Highest(under); // duck as high as possible

        var noQueen = legal.Where(c => c != QueenOfSpades).ToList();
        if (noQueen.Count == 0) return legal[0];
        if (last) return Highest(noQueen); // it's ours anyway: shed the highest
        if (level == 2) return points == 0 ? Highest(noQueen) : Lowest(noQueen);
        bool danger = points > 0 || (led == Spades && k.QueenOut);
        for (int s = 1; s <= after; s++)
            if (k.Void[(seat + s) % Players, led]) danger = true; // someone after us can throw points
        return danger ? Lowest(noQueen) : Highest(noQueen);
    }

    static int Discard(HeartsRules r, int seat, List<int> legal, Read k, int level)
    {
        var hand = r.Hands[seat];
        int led = r.LedSuit;
        int winning = r.Trick.Where(c => c >= 0 && Suit(c) == led).OrderByDescending(Rank).First();
        bool shooterTakes = level >= 4 && k.Shooter >= 0 && Array.IndexOf(r.Trick, winning) == k.Shooter;
        if (shooterTakes)
        {
            // never feed a moon shot: throw something harmless
            var clean = legal.Where(c => PointsOf(c) == 0).ToList();
            if (clean.Count > 0) return Highest(clean);
        }
        if (legal.Contains(QueenOfSpades)) return QueenOfSpades;
        var bigSpades = legal.Where(c => Suit(c) == Spades && Rank(c) > 12).ToList();
        if (bigSpades.Count > 0 && (level == 2 || k.QueenOut)) return Highest(bigSpades);
        var hearts = legal.Where(c => Suit(c) == Hearts).ToList();
        if (hearts.Count > 0 && (level < 4 || hearts.Any(h => k.HigherOut(h, hand) < 3))) return Highest(hearts);
        if (level >= 4)
        {
            // from the shortest suit, to run out of it
            var bySuit = legal.GroupBy(Suit).OrderBy(g => hand.Count(c => Suit(c) == g.Key)).ThenByDescending(g => g.Max(Rank));
            return Highest(bySuit.First());
        }
        return Highest(legal);
    }
}
