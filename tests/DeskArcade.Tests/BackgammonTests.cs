using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using DeskArcade.Games;
using Xunit;

namespace DeskArcade.Tests;

public class BackgammonTests
{
    static Dictionary<int, int> P(params (int Point, int N)[] at) => at.ToDictionary(x => x.Point, x => x.N);

    static int Total(BackgammonRules g, int side) =>
        Enumerable.Range(1, 24).Sum(r => g.Count(side, r)) + g.OnBar(side) + g.BorneOff(side);

    [Fact]
    public void TheGameStartsWithFifteenCheckersEachAnd167Pips()
    {
        var g = new BackgammonRules();
        for (int side = 0; side < 2; side++)
        {
            Assert.Equal(15, Total(g, side));
            Assert.Equal(167, g.Pips(side));
        }
        Assert.Equal(2, g.Count(1, 24));
        Assert.Equal(-2, g[0]); // side 1's 24-point is absolute point 0
        Assert.Equal(2, g[23]);
    }

    [Fact]
    public void AnOpeningRollHasItsKnownMoves()
    {
        var g = new BackgammonRules();
        Assert.True(g.Roll(3, 1));
        Assert.Equal(new[] { 3, 1 }, g.Dice);
        var steps = g.LegalSteps();
        Assert.Contains(new BgStep(8, 5, 3), steps);
        Assert.Contains(new BgStep(6, 5, 1), steps); // either die first
        Assert.Contains(new BgStep(24, 21, 3), steps);
        Assert.Contains(new BgStep(24, 23, 1), steps);
        Assert.Contains(g.Plays(), p => p.Count == 2 && p.Contains(new BgStep(8, 5, 3)) && p.Contains(new BgStep(6, 5, 1)));
    }

    [Fact]
    public void AClosedPointCannotBeLandedOn()
    {
        // side 1 holds its 19 (my 6): my 8 cannot move 2
        var g = BackgammonRules.Setup(0, P((8, 1), (3, 14)), P((19, 2), (1, 13)));
        g.Roll(2, 1);
        Assert.DoesNotContain(new BgStep(8, 6, 2), g.LegalSteps());
        Assert.Contains(new BgStep(8, 7, 1), g.LegalSteps());
    }

    [Fact]
    public void ALoneCheckerIsHitAndMustComeInFirst()
    {
        var g = BackgammonRules.Setup(0, P((10, 1), (6, 14)), P((20, 1), (1, 14))); // their blot on their 20 = my 5
        g.Roll(5, 2);
        var r = g.Play(new BgStep(10, 5, 5));
        Assert.NotNull(r);
        Assert.True(r!.Value.Hit);
        Assert.Equal(1, g.OnBar(1));
        g.Play(g.LegalSteps()[0]); // the 2
        Assert.Equal(1, g.Turn);
        g.Roll(6, 4);
        Assert.All(g.LegalSteps(), s => Assert.Equal(BackgammonRules.BarSpot, s.From));
        // their 6-entry lands on their 19 = my 6, which I hold: only the 4 comes in
        Assert.Equal(new[] { new BgStep(25, 21, 4) }, g.LegalSteps());
    }

    [Fact]
    public void ABlockedBarCheckerLosesTheTurn()
    {
        var g = BackgammonRules.Setup(1, P((20, 1)), P((1, 2), (2, 2), (3, 2), (4, 2), (5, 2), (6, 2), (7, 3)), ownBar: 1);
        Assert.False(g.Roll(6, 3)); // my whole home board is closed to them
        Assert.Equal(0, g.Turn);
        Assert.False(g.Rolled);
    }

    [Fact]
    public void BothDiceMustBeUsedWhenTheyCanBe()
    {
        // 13 -> 7 with the 6 leaves the 5 nowhere to go (my 2 is closed); the other ways use both dice
        var g = BackgammonRules.Setup(0, P((13, 1), (7, 1), (1, 13)), P((23, 2), (2, 13)));
        g.Roll(6, 5);
        Assert.All(g.Plays(), p => Assert.Equal(2, p.Count));
        Assert.DoesNotContain(new BgStep(13, 7, 6), g.LegalSteps());
        Assert.Contains(new BgStep(13, 8, 5), g.LegalSteps());
        Assert.Contains(new BgStep(7, 1, 6), g.LegalSteps());
    }

    [Fact]
    public void WhenOnlyOneDieFitsItMustBeTheLarger()
    {
        // one checker far back; either die moves it, but then the other would land on my closed 15: the 6 must be played
        var g = BackgammonRules.Setup(0, P((24, 1), (1, 14)), P((10, 2), (2, 13)));
        g.Roll(6, 3);
        Assert.Equal(new[] { new BgStep(24, 18, 6) }, g.LegalSteps());
    }

    [Fact]
    public void DoublesPlayFourTimes()
    {
        var g = new BackgammonRules();
        g.Roll(3, 3);
        Assert.Equal(4, g.Dice.Count);
        Assert.All(g.Plays(), p => Assert.Equal(4, p.Count));
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(0, g.Turn);
            g.Play(g.LegalSteps()[0]);
        }
        Assert.Equal(1, g.Turn);
    }

    [Fact]
    public void BearingOffTakesExactOrTheFurthestWithAHigherDie()
    {
        var g = BackgammonRules.Setup(0, P((4, 2), (2, 1)), P((20, 15)));
        Assert.Equal(12, g.BorneOff(0));
        g.Roll(6, 1);
        var steps = g.LegalSteps();
        Assert.Contains(new BgStep(4, 0, 6), steps); // 6 from the furthest checker, on 4
        Assert.DoesNotContain(new BgStep(2, 0, 6), steps); // not while one stands further out
        Assert.Contains(new BgStep(2, 1, 1), steps);

        var h = BackgammonRules.Setup(0, P((8, 1), (2, 13)), P((20, 15)));
        h.Roll(2, 1);
        Assert.DoesNotContain(h.LegalSteps(), s => s.To == BackgammonRules.OffSpot); // one is not home yet
    }

    [Fact]
    public void AllOffWinsAndAGammonCountsDouble()
    {
        var g = BackgammonRules.Setup(0, P((1, 1)), P((10, 15)));
        g.Roll(2, 1);
        var r = g.Play(g.LegalSteps().First(s => s.To == 0));
        Assert.True(r!.Value.Won);
        Assert.True(g.Over);
        Assert.Equal(0, g.Winner);
        Assert.Equal(2, g.WinKind); // they bore off none, but none are in my home

        var b = BackgammonRules.Setup(0, P((1, 1)), P((10, 14), (20, 1)));
        b.Roll(1, 1);
        b.Play(b.LegalSteps()[0]);
        Assert.Equal(3, b.WinKind); // one of theirs is still in my home board
    }

    [Fact]
    public void BearingOffWithOneDieOrBothIsAPlayOfTwo()
    {
        // 6-1 with checkers on 2 and 1: the 1 (2 -> 1) and then the 6 (off) lands where the 6 alone would, in two steps
        var g = BackgammonRules.Setup(0, P((2, 1), (1, 1)), P((10, 15)));
        g.Roll(6, 1);
        Assert.NotEmpty(g.Plays());
        Assert.All(g.Plays(), p => Assert.Equal(2, p.Count));
        foreach (var s in g.BestPlay(4, new Random(1))) Assert.NotNull(g.Play(s));
        Assert.True(g.Over);
    }

    [Fact]
    public void RandomGamesKeepTheCheckersAndAlwaysEnd()
    {
        var rng = new Random(11);
        for (int game = 0; game < 30; game++)
        {
            var g = new BackgammonRules();
            int guard = 0;
            while (!g.Over && guard++ < 2000)
            {
                if (!g.Rolled && !g.Roll(rng.Next(1, 7), rng.Next(1, 7))) continue;
                var steps = g.LegalSteps();
                Assert.NotEmpty(steps);
                Assert.NotNull(g.Play(steps[rng.Next(steps.Count)]));
                Assert.Equal(15, Total(g, 0));
                Assert.Equal(15, Total(g, 1));
            }
            Assert.True(g.Over, "a game ended");
            Assert.InRange(g.WinKind, 1, 3);
        }
    }

    [Fact]
    public void EveryPlayCanBeStepped()
    {
        var rng = new Random(4);
        var g = new BackgammonRules();
        for (int turn = 0; turn < 40 && !g.Over; turn++)
        {
            if (!g.Roll(rng.Next(1, 7), rng.Next(1, 7))) continue;
            var play = g.BestPlay(3, rng);
            int side = g.Turn;
            foreach (var s in play)
            {
                Assert.Equal(side, g.Turn);
                Assert.NotNull(g.Play(s));
            }
            Assert.True(g.Over || g.Turn != side);
        }
    }

    [Fact]
    public void TheHardComputerBeatsRandomPlay()
    {
        var rng = new Random(21);
        int wins = 0;
        var watch = Stopwatch.StartNew();
        for (int game = 0; game < 12; game++)
        {
            var g = new BackgammonRules();
            int cpu = game % 2, guard = 0;
            while (!g.Over)
            {
                Assert.True(guard++ < 3000, "the game got stuck");
                if (!g.Roll(rng.Next(1, 7), rng.Next(1, 7))) continue;
                var play = g.Turn == cpu ? g.BestPlay(3, rng) : g.Plays()[rng.Next(g.Plays().Count)];
                foreach (var s in play) Assert.NotNull(g.Play(s));
            }
            if (g.Winner == cpu) wins++;
        }
        Assert.True(wins >= 10, $"{wins} of 12");
        Assert.True(watch.ElapsedMilliseconds < 8000, $"{watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void ThinkingOverDoublesIsQuick()
    {
        var g = new BackgammonRules();
        var watch = Stopwatch.StartNew();
        g.Roll(1, 1);
        g.BestPlay(4, new Random(1));
        g = new BackgammonRules();
        g.Roll(2, 2);
        g.BestPlay(4, new Random(1));
        Assert.True(watch.ElapsedMilliseconds < 400, $"{watch.ElapsedMilliseconds} ms"); // it thinks on the UI thread
    }
}
