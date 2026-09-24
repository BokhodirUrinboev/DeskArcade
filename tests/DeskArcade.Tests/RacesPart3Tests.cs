using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using DeskArcade.Engine;
using DeskArcade.Games;
using Xunit;

namespace DeskArcade.Tests;

/// <summary>Races, part 3: the UI-free pieces of Pool, Memory, Code Breaker, Solitaire and Interns as race games.</summary>
public class RacesPart3Tests
{
    // ------------------------------------------------------------------ race scores

    [Theory]
    [InlineData(1, false, 1)]
    [InlineData(4, false, 4)]
    [InlineData(10, false, 10)] // cracked on the very last guess
    [InlineData(10, true, 11)]  // got away: one more than the board holds
    public void CodeBreakerRoundCountsGuessesAndAFailedCodeAsEleven(int guesses, bool lost, int expected) =>
        Assert.Equal(expected, CodeBreakerGame.RaceScoreFor(guesses, lost));

    [Fact]
    public void CodeBreakerFailedCodeIsWorseThanAnyCrack()
    {
        int failed = CodeBreakerGame.RaceScoreFor(CodeBreakerRules.MaxGuesses, true);
        for (int g = 1; g <= CodeBreakerRules.MaxGuesses; g++) Assert.True(CodeBreakerGame.RaceScoreFor(g, false) < failed);
    }

    [Theory]
    [InlineData(0, 10, 0)]
    [InlineData(7, 10, 7)]
    [InlineData(20, 10, 10)] // a best from a bigger level never asks more than this level lets out
    [InlineData(-3, 10, 0)]
    public void InternsRivalReferenceIsCappedByTheLevel(long best, int total, int expected) =>
        Assert.Equal(expected, InternsGame.RivalReference(best, total));

    [Fact]
    public void InternsRoundLastsLongerWithMoreInterns()
    {
        Assert.True(InternsGame.RoundSeconds(10) < InternsGame.RoundSeconds(24));
        Assert.InRange(InternsGame.RoundSeconds(10), 30, 180);
    }

    // ------------------------------------------------------------------ memory flips

    [Fact]
    public void FlipIsFullWidthAtBothEndsAndEdgeOnHalfway()
    {
        Assert.Equal(1, MemoryGame.FlipWidth(0), 9);
        Assert.Equal(1, MemoryGame.FlipWidth(1), 9);
        Assert.Equal(0.02, MemoryGame.FlipWidth(0.5), 9);
        Assert.True(MemoryGame.FlipWidth(0.25) > MemoryGame.FlipWidth(0.4));
        Assert.Equal(MemoryGame.FlipWidth(0.3), MemoryGame.FlipWidth(0.7), 9);
    }

    [Fact]
    public void FarSideShowsFromHalfway()
    {
        Assert.False(MemoryGame.FlipShowsFarSide(0.49));
        Assert.True(MemoryGame.FlipShowsFarSide(0.5));
        Assert.True(MemoryGame.FlipShowsFarSide(1));
    }

    [Fact]
    public void AFreshFlipStartsAtZeroAndTakesTheWholeTime()
    {
        var (start, seconds) = MemoryGame.FlipPlan(null);
        Assert.Equal(0, start);
        Assert.Equal(MemoryGame.FlipTime, seconds);
    }

    [Theory]
    [InlineData(0.1)]
    [InlineData(0.3)]
    [InlineData(0.45)]
    [InlineData(0.6)]
    [InlineData(0.9)]
    public void TurningBackMidFlipKeepsTheWidthAndOnlyTakesTheTimeSpent(double inFlight)
    {
        var (start, seconds) = MemoryGame.FlipPlan(inFlight);
        Assert.Equal(MemoryGame.FlipWidth(inFlight), MemoryGame.FlipWidth(start), 9);
        Assert.Equal(MemoryGame.FlipTime * inFlight, seconds, 9);
        Assert.InRange(start, 0, 1);
        // it now shows the other side of what the interrupted flip had reached
        Assert.NotEqual(MemoryGame.FlipShowsFarSide(inFlight), MemoryGame.FlipShowsFarSide(start));
    }

    [Fact]
    public void CardsDealInOneAfterAnother()
    {
        Assert.Equal(0, MemoryGame.DealDelay(0));
        for (int i = 1; i < 24; i++) Assert.True(MemoryGame.DealDelay(i) > MemoryGame.DealDelay(i - 1));
        Assert.True(MemoryGame.DealDelay(23) < 2, "the whole deal is over in a couple of seconds");
    }

    // ------------------------------------------------------------------ code breaker pins and reveal

    [Fact]
    public void PinsPopInOneByOne()
    {
        Assert.True(CodeBreakerGame.PinDelay(0) > 0);
        for (int k = 1; k < CodeBreakerRules.Pegs; k++) Assert.True(CodeBreakerGame.PinDelay(k) > CodeBreakerGame.PinDelay(k - 1));
    }

    [Fact]
    public void RevealFlipsTheCapAwayThenTheCodePegOut()
    {
        var (cap0, peg0) = CodeBreakerGame.RevealWidths(0);
        Assert.Equal(1, cap0, 9);
        Assert.Equal(0, peg0, 9);
        var (cap1, peg1) = CodeBreakerGame.RevealWidths(1);
        Assert.Equal(0, cap1, 9);
        Assert.Equal(1, peg1, 9);
        double lastCap = 1;
        for (double t = 0.05; t < 0.5; t += 0.05)
        {
            var (cap, peg) = CodeBreakerGame.RevealWidths(t);
            Assert.True(cap < lastCap && cap > 0, "the cap turns edge-on over the first half");
            Assert.Equal(0, peg);
            lastCap = cap;
        }
        double lastPeg = 0;
        for (double t = 0.55; t <= 1; t += 0.05)
        {
            var (cap, peg) = CodeBreakerGame.RevealWidths(t);
            Assert.Equal(0, cap);
            Assert.True(peg > lastPeg, "the peg turns out over the second half");
            lastPeg = peg;
        }
    }

    // ------------------------------------------------------------------ solitaire deal and cascade

    [Fact]
    public void DealIndexFollowsTheRowsOfTheDeal()
    {
        Assert.Equal(0, SolitaireGame.DealIndex(0, 0));
        Assert.Equal(6, SolitaireGame.DealIndex(6, 0));
        Assert.Equal(7, SolitaireGame.DealIndex(1, 1));
        Assert.Equal(27, SolitaireGame.DealIndex(6, 6));
        var all = new List<int>();
        for (int pile = 0; pile < SolitaireRules.Piles; pile++)
            for (int i = 0; i <= pile; i++) all.Add(SolitaireGame.DealIndex(pile, i));
        Assert.Equal(Enumerable.Range(0, 28), all.OrderBy(n => n));
        for (int i = 1; i < 24; i++) Assert.True(SolitaireGame.DealDelay(i) > SolitaireGame.DealDelay(i - 1));
    }

    public static IEnumerable<object[]> CascadeThrows() => new[]
    {
        new object[] { 300.0, 200.0, -150.0 }, new object[] { 900.0, -250.0, -300.0 }, new object[] { 1500.0, 60.0, 100.0 },
        new object[] { 100.0, -400.0, -20.0 }, new object[] { 1800.0, 380.0, -280.0 },
    };

    [Theory]
    [MemberData(nameof(CascadeThrows))]
    public void CascadePathKeepsTheWholeCardInsideTheBox(double startX, double vx, double vy)
    {
        var box = new Rect(0, 30, 1920, 1010);
        const double w = 64, h = 88;
        var path = SolitaireCascade.Path(new Vec2(startX, 60), new Vec2(vx, vy), w, h, box, 1500, 0.75, 2.2, 1 / 60.0);
        Assert.True(path.Count > 100);
        foreach (var p in path)
        {
            Assert.InRange(p.X, box.Left, box.Right - w);
            Assert.InRange(p.Y, box.Top, box.Bottom - h);
        }
        Assert.Contains(path, p => Math.Abs(p.Y - (box.Bottom - h)) < 0.001); // it reaches the floor and bounces
        Assert.Equal((int)Math.Ceiling(2.2 / (1 / 60.0)) + 1, path.Count, 1.0);
    }

    [Fact]
    public void CascadeStartsInsideEvenWhenThrownFromOutside()
    {
        var box = new Rect(100, 100, 500, 400);
        var path = SolitaireCascade.Path(new Vec2(-50, 900), new Vec2(50, 50), 64, 88, box, 1500, 0.75, 1, 1 / 60.0);
        Assert.Equal(100, path[0].X);
        Assert.Equal(box.Bottom - 88, path[0].Y);
        foreach (var p in path)
        {
            Assert.InRange(p.X, box.Left, box.Right - 64);
            Assert.InRange(p.Y, box.Top, box.Bottom - 88);
        }
    }

    [Fact]
    public void CascadeLosesHeightWithEveryBounce()
    {
        var box = new Rect(0, 0, 1000, 800);
        var path = SolitaireCascade.Path(new Vec2(100, 0), new Vec2(0, 0), 64, 88, box, 1500, 0.6, 4, 1 / 120.0);
        // the highest point after each landing is lower than the one before
        var peaks = new List<double>();
        double floor = box.Bottom - 88, best = double.MaxValue;
        bool airborne = false;
        foreach (var p in path)
        {
            if (p.Y < floor - 0.001)
            {
                airborne = true;
                best = Math.Min(best, p.Y);
            }
            else if (airborne)
            {
                peaks.Add(best);
                best = double.MaxValue;
                airborne = false;
            }
        }
        Assert.True(peaks.Count >= 3);
        for (int i = 1; i < peaks.Count; i++) Assert.True(peaks[i] > peaks[i - 1], "each bounce is lower (y grows downwards)");
    }
}
