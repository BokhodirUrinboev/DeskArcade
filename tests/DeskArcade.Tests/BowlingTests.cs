using System;
using System.Linq;
using Avalonia;
using DeskArcade.Games;
using Xunit;

namespace DeskArcade.Tests;

public class BowlingScoreTests
{
    static BowlingScore Bowl(params int[] rolls)
    {
        var s = new BowlingScore();
        foreach (int r in rolls) s.Roll(r);
        return s;
    }

    [Fact]
    public void TwelveStrikesIsAPerfect300()
    {
        var s = Bowl(Enumerable.Repeat(10, 12).ToArray());
        Assert.True(s.GameOver);
        Assert.Equal(300, s.Total);
        Assert.Equal(300, s.RunningTotals()[9]);
        Assert.Equal(new[] { "X", "X", "X" }, s.Marks(9));
        Assert.Equal(new[] { "", "X" }, s.Marks(0));
    }

    [Fact]
    public void AllSparesOfFiveMake150()
    {
        var s = Bowl(Enumerable.Repeat(5, 21).ToArray());
        Assert.True(s.GameOver);
        Assert.Equal(150, s.Total);
        Assert.Equal(new[] { "5", "/" }, s.Marks(3));
        Assert.Equal(new[] { "5", "/", "5" }, s.Marks(9));
    }

    [Fact]
    public void AllGuttersScoreNothingAndEndAfterTwentyBalls()
    {
        var s = new BowlingScore();
        for (int i = 0; i < 19; i++)
        {
            s.Roll(0);
            Assert.False(s.GameOver);
        }
        s.Roll(0);
        Assert.True(s.GameOver);
        Assert.Equal(0, s.Total);
        Assert.Equal(new[] { "-", "-" }, s.Marks(4));
    }

    [Fact]
    public void AMixedGameScoresFrameByFrame()
    {
        // X 7/ 9- X -8 8/ -6 X X X81
        var s = Bowl(10, 7, 3, 9, 0, 10, 0, 8, 8, 2, 0, 6, 10, 10, 10, 8, 1);
        Assert.True(s.GameOver);
        Assert.Equal(new int?[] { 20, 39, 48, 66, 74, 84, 90, 120, 148, 167 }, s.RunningTotals());
        Assert.Equal(167, s.Total);
        Assert.Equal(new[] { "7", "/" }, s.Marks(1));
        Assert.Equal(new[] { "9", "-" }, s.Marks(2));
        Assert.Equal(new[] { "-", "8" }, s.Marks(4));
        Assert.Equal(new[] { "X", "8", "1" }, s.Marks(9));
    }

    [Fact]
    public void AStrikeWaitsForItsTwoBonusBalls()
    {
        var s = Bowl(10);
        Assert.Null(s.RunningTotals()[0]);
        Assert.Equal(1, s.Frame);
        Assert.Equal(0, s.Ball);
        s.Roll(3);
        Assert.Null(s.RunningTotals()[0]);
        Assert.Equal(1, s.Ball);
        Assert.Equal(7, s.PinsStanding);
        s.Roll(4);
        Assert.Equal(new int?[] { 17, 24 }, s.RunningTotals().Take(2).ToArray());
    }

    [Fact]
    public void ATenthFrameStrikeEarnsTwoBonusBalls()
    {
        var s = Bowl(Enumerable.Repeat(0, 18).ToArray());
        s.Roll(10);
        Assert.Equal((9, 1, 10), (s.Frame, s.Ball, s.PinsStanding)); // a fresh rack after the strike
        s.Roll(3);
        Assert.Equal((2, 7), (s.Ball, s.PinsStanding)); // the third ball faces what the second one left
        Assert.False(s.GameOver);
        s.Roll(7);
        Assert.True(s.GameOver);
        Assert.Equal(20, s.Total);
        Assert.Equal(new[] { "X", "3", "/" }, s.Marks(9));
    }

    [Fact]
    public void ATenthFrameSpareEarnsOneBonusBall()
    {
        var s = Bowl(Enumerable.Repeat(0, 18).ToArray());
        s.Roll(7);
        s.Roll(3);
        Assert.False(s.GameOver);
        Assert.Equal((2, 10), (s.Ball, s.PinsStanding));
        s.Roll(5);
        Assert.True(s.GameOver);
        Assert.Equal(15, s.Total);
        Assert.Equal(21, s.Rolls.Count);
    }

    [Fact]
    public void AnOpenTenthFrameHasNoBonusBall()
    {
        var s = Bowl(Enumerable.Repeat(0, 18).ToArray());
        s.Roll(3);
        s.Roll(4);
        Assert.True(s.GameOver);
        Assert.Equal(7, s.Total);
        Assert.Throws<InvalidOperationException>(() => s.Roll(5));
    }

    [Fact]
    public void TheSecondBallCanOnlyKnockDownThePinsStillStanding()
    {
        var s = Bowl(7);
        Assert.Equal(3, s.PinsStanding);
        s.Roll(9); // only three are left to hit
        Assert.Equal(new[] { 7, 3 }, s.Rolls);
        Assert.Equal(new[] { "7", "/" }, s.Marks(0));
        Assert.Equal(10, s.PinsStanding);
    }
    [Fact]
    public void ZeroThenTenIsASpareNotAStrike()
    {
        var s = new BowlingScore();
        Assert.Equal(BowlingScore.Kind.Open, s.Roll(0));
        Assert.False(s.FreshRack);
        Assert.Equal(10, s.PinsStanding);
        Assert.Equal(BowlingScore.Kind.Spare, s.Roll(10));
        Assert.Equal(new[] { "-", "/" }, s.Marks(0));
        s.Roll(4);
        s.Roll(0);
        Assert.Equal(14, s.RunningTotals()[0]); // 10 plus the next ball only
    }

    [Fact]
    public void ATenthFrameSpareAfterAZeroIsMarkedAsASpare()
    {
        var s = Bowl(Enumerable.Repeat(0, 18).ToArray());
        Assert.Equal(BowlingScore.Kind.Open, s.Roll(0));
        Assert.Equal(BowlingScore.Kind.Spare, s.Roll(10));
        Assert.True(s.FreshRack);
        Assert.Equal(BowlingScore.Kind.Strike, s.Roll(10)); // the bonus ball meets a fresh rack
        Assert.Equal(new[] { "-", "/", "X" }, s.Marks(9));
        Assert.Equal(20, s.Total);
    }

    [Fact]
    public void ATenthFrameStrikeThenZeroThenTenEndsWithASpare()
    {
        var s = Bowl(Enumerable.Repeat(0, 18).ToArray());
        Assert.Equal(BowlingScore.Kind.Strike, s.Roll(10));
        Assert.Equal(BowlingScore.Kind.Open, s.Roll(0));
        Assert.False(s.FreshRack);
        Assert.Equal(BowlingScore.Kind.Spare, s.Roll(10));
        Assert.Equal(new[] { "X", "-", "/" }, s.Marks(9));
        Assert.True(s.GameOver);
    }

    [Fact]
    public void StrikesAreOnlyTenOnAFreshRack()
    {
        // X X then a 10th of X 0 10: three strikes in a row, not four
        var s = Bowl(Enumerable.Repeat(0, 14).ToArray());
        var kinds = new[] { 10, 10, 10, 0, 10 }.Select(s.Roll).ToArray();
        Assert.Equal(new[] { BowlingScore.Kind.Strike, BowlingScore.Kind.Strike, BowlingScore.Kind.Strike, BowlingScore.Kind.Open, BowlingScore.Kind.Spare }, kinds);
    }
}

public class BowlingLaneTests
{
    [Theory]
    [InlineData(1280)]
    [InlineData(1366)]
    [InlineData(1920)]
    [InlineData(3840)]
    public void AFullPowerDragFitsLeftOfTheBallOnAnyScreen(double width)
    {
        var arena = new Rect(0, 0, width, 700);
        var (left, length) = BowlingGame.LaneSpan(arena);
        Assert.True(left + BowlingGame.StartInset - BowlingGame.MaxPull >= arena.Left + 20, $"only {left + BowlingGame.StartInset:0} px for the drag");
        Assert.True(left + length <= arena.Right - 10);
        Assert.True(length >= 900);
    }
}
