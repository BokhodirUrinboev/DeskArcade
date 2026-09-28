using System;
using System.Linq;
using DeskArcade.Games;
using Xunit;

namespace DeskArcade.Tests;

public class BlackjackTests
{
    /// <summary>A card of the given rank (1 ace … 13 king) in spades.</summary>
    static int C(int rank) => rank - 1;

    [Theory]
    [InlineData(new[] { 1, 13 }, 21, true)]       // ace and king: blackjack, soft
    [InlineData(new[] { 1, 1, 9 }, 21, true)]     // two aces: 11 + 1 + 9
    [InlineData(new[] { 1, 6, 10 }, 17, false)]   // the ace falls back to 1
    [InlineData(new[] { 12, 11, 2 }, 22, false)]  // faces count 10
    [InlineData(new[] { 1, 6 }, 17, true)]        // soft 17
    [InlineData(new[] { 5, 5 }, 10, false)]
    public void HandsAreCountedWithAcesAsOneOrEleven(int[] ranks, int total, bool soft)
    {
        var (t, s) = BlackjackRules.Value(ranks.Select(C));
        Assert.Equal(total, t);
        Assert.Equal(soft, s);
    }

    [Fact]
    public void OnlyATwoCardTwentyOneIsABlackjack()
    {
        Assert.True(BlackjackRules.IsBlackjack(new[] { C(1), C(10) }));
        Assert.False(BlackjackRules.IsBlackjack(new[] { C(7), C(7), C(7) }));
    }

    [Fact]
    public void ARoundIsTenHandsFromAHundredChips()
    {
        var rng = new Random(2);
        var g = new BlackjackRules(rng);
        Assert.Equal(BlackjackRules.StartChips, g.Chips);
        int hands = 0;
        while (g.Phase != BlackjackPhase.RoundOver && hands < 50)
        {
            Assert.True(g.Deal(5));
            hands++;
            while (g.Phase == BlackjackPhase.Playing)
                switch (BlackjackRules.Advice(g.Player, g.Dealer[0], g.CanDouble))
                {
                    case 'D': g.Double(); break;
                    case 'S': g.Stand(); break;
                    default: g.Hit(); break;
                }
            Assert.NotEqual(BlackjackOutcome.None, g.Outcome);
        }
        Assert.True(hands == BlackjackRules.Hands || g.Chips < BlackjackRules.Bets[0]);
        Assert.False(g.Deal(5)); // over until a new round
        g.NewRound();
        Assert.Equal(BlackjackRules.StartChips, g.Chips);
        Assert.True(g.Deal(5));
    }

    [Fact]
    public void PayoutsMatchTheOutcome()
    {
        for (int seed = 0; seed < 400; seed++)
        {
            var g = new BlackjackRules(new Random(seed));
            int before = g.Chips;
            g.Deal(10);
            while (g.Phase == BlackjackPhase.Playing) g.Stand();
            int expected = g.Outcome switch
            {
                BlackjackOutcome.Blackjack => 15,
                BlackjackOutcome.Win => 10,
                BlackjackOutcome.Push => 0,
                _ => -10,
            };
            Assert.Equal(expected, g.Paid);
            Assert.Equal(before + expected, g.Chips);
        }
    }

    [Fact]
    public void TheDealerDrawsToSeventeenAndHitsASoftSeventeen()
    {
        for (int seed = 0; seed < 300; seed++)
        {
            var g = new BlackjackRules(new Random(seed));
            g.Deal(5);
            if (g.Phase != BlackjackPhase.Playing) continue; // a blackjack settled it
            g.Stand();
            var (total, soft) = BlackjackRules.Value(g.Dealer);
            Assert.True(total >= 17, $"dealer stopped on {total}");
            Assert.False(total == 17 && soft, "dealer stood on a soft 17");
            if (total <= 21)
            {
                var (before, softBefore) = BlackjackRules.Value(g.Dealer.Take(g.Dealer.Count - 1));
                if (g.Dealer.Count > 2) Assert.True(before < 17 || before == 17 && softBefore); // it only drew when it had to
            }
        }
    }

    [Fact]
    public void BustingLosesAtOnceWithoutTheDealerDrawing()
    {
        for (int seed = 0; seed < 300; seed++)
        {
            var g = new BlackjackRules(new Random(seed));
            g.Deal(5);
            if (g.Phase != BlackjackPhase.Playing) continue;
            while (g.Phase == BlackjackPhase.Playing) g.Hit();
            if (BlackjackRules.Value(g.Player).Total <= 21) continue; // hit 21 and stood
            Assert.Equal(BlackjackOutcome.Bust, g.Outcome);
            Assert.Equal(2, g.Dealer.Count);
            return;
        }
        Assert.Fail("no bust in 300 deals");
    }

    [Fact]
    public void DoublingTakesOneCardForTwiceTheBet()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var g = new BlackjackRules(new Random(seed));
            g.Deal(10);
            if (!g.CanDouble) continue;
            Assert.True(g.Double());
            Assert.Equal(3, g.Player.Count);
            Assert.Equal(20, g.Bet);
            Assert.True(g.Doubled);
            Assert.NotEqual(BlackjackPhase.Playing, g.Phase);
            Assert.Equal(g.Outcome switch { BlackjackOutcome.Win => 20, BlackjackOutcome.Push => 0, _ => -20 }, g.Paid);
            return;
        }
        Assert.Fail("never could double");
    }

    [Fact]
    public void ABetBeyondTheChipsIsRefused()
    {
        var g = new BlackjackRules(new Random(1));
        Assert.False(g.Deal(0));
        Assert.False(g.Deal(BlackjackRules.StartChips + 1));
        Assert.False(g.Hit()); // nothing dealt yet
        Assert.False(g.Stand());
    }

    [Fact]
    public void TheShoeIsTwoDecksAndRefillsWhenLow()
    {
        var g = new BlackjackRules(new Random(4));
        Assert.Equal(52 * BlackjackRules.Decks, g.ShoeLeft);
        for (int round = 0; round < 20; round++)
        {
            g.NewRound();
            while (g.Phase != BlackjackPhase.RoundOver)
            {
                g.Deal(5);
                while (g.Phase == BlackjackPhase.Playing) g.Stand();
                Assert.True(g.ShoeLeft >= 0);
            }
        }
    }

    [Theory]
    [InlineData(new[] { 5, 6 }, 6, true, 'D')]    // 11 against a 6: double
    [InlineData(new[] { 10, 7 }, 10, true, 'S')]  // hard 17: stand
    [InlineData(new[] { 10, 3 }, 5, true, 'S')]   // 13 against a 5: stand
    [InlineData(new[] { 10, 3 }, 9, true, 'H')]   // 13 against a 9: hit
    [InlineData(new[] { 1, 6 }, 9, true, 'H')]    // soft 17: hit
    [InlineData(new[] { 1, 7 }, 9, true, 'S')]    // soft 18: stand
    public void TheDemoPlaysASimpleBasicStrategy(int[] ranks, int up, bool canDouble, char advice) =>
        Assert.Equal(advice, BlackjackRules.Advice(ranks.Select(C).ToList(), C(up), canDouble));
}
