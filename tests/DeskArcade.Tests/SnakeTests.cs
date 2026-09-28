using System;
using System.Linq;
using Avalonia;
using DeskArcade.Engine;
using DeskArcade.Games;
using Xunit;

namespace DeskArcade.Tests;

public class SnakeTests
{
    static readonly Rect Box = new(0, 0, 1920, 1040);

    static SnakeRules Snake(double x = 960, double y = 520, double heading = 0) => new(new Vec2(x, y), heading, Box);

    static double PathLength(SnakeRules s)
    {
        double total = 0;
        for (int i = 1; i < s.Path.Count; i++) total += (s.Path[i] - s.Path[i - 1]).Length;
        return total;
    }

    [Fact]
    public void ItStartsStretchedOutBehindItsHead()
    {
        var s = Snake(heading: 0);
        Assert.InRange(PathLength(s), SnakeRules.StartLength - 3, SnakeRules.StartLength);
        Assert.True(s.Path[^1].X < s.Head.X); // heading right, the tail trails to the left
    }

    [Theory]
    [InlineData(0, 1, 0.1, 0.1)]
    [InlineData(0, -1, 0.1, -0.1)]
    [InlineData(3.0, -3.0, 0.1, 3.1)]  // across ±π the short way, not all the way round
    [InlineData(0, 0.05, 0.1, 0.05)]
    public void ItTurnsTheShortWayAndOnlySoFast(double heading, double wanted, double step, double expected) =>
        Assert.Equal(expected, SnakeRules.Turn(heading, wanted, step), 6);

    [Fact]
    public void ItMovesAtItsSpeedAndSteersTowardTheCursor()
    {
        var s = Snake(heading: 0);
        var start = s.Head;
        Assert.True(s.Step(0.1, new Vec2(960, 100))); // the cursor straight above
        Assert.InRange((s.Head - start).Length, SnakeRules.StartSpeed * 0.1 - 0.5, SnakeRules.StartSpeed * 0.1 + 0.5);
        Assert.True(s.Heading < 0); // turned upward, but only a little
        Assert.True(s.Heading > -SnakeRules.TurnRate * 0.1 - 1e-9);
    }

    [Fact]
    public void RightByTheCursorItRunsStraightOn()
    {
        var s = Snake(heading: 0);
        double heading = s.Heading;
        s.Step(0.05, s.Head + new Vec2(0, SnakeRules.CalmRadius / 2));
        Assert.Equal(heading, s.Heading);
    }

    [Fact]
    public void AtTheEdgesItSlidesAlongThem()
    {
        var s = Snake(x: 1900, y: 520, heading: 0);
        for (int i = 0; i < 20; i++) s.Step(0.05, new Vec2(3000, 520)); // the cursor "beyond" the right edge
        Assert.True(s.Head.X <= Box.Right - SnakeRules.Radius);
        Assert.False(s.Dead);
    }

    [Fact]
    public void AnAppleMakesItLongerFasterAndScores()
    {
        var s = Snake();
        Assert.False(s.TryEat(s.Head + new Vec2(100, 0), 10, golden: false));
        Assert.True(s.TryEat(s.Head + new Vec2(12, 0), 10, golden: false));
        Assert.Equal(SnakeRules.StartLength + SnakeRules.GrowPerApple, s.Length);
        Assert.Equal(SnakeRules.StartSpeed + SnakeRules.SpeedPerApple, s.Speed);
        Assert.True(s.TryEat(s.Head, 10, golden: true));
        Assert.Equal(SnakeRules.ApplePoints + SnakeRules.GoldenPoints, s.Score);
        Assert.Equal(1, s.GoldenEaten);
        Assert.Equal(SnakeRules.StartLength + SnakeRules.GrowPerApple * (1 + SnakeRules.GoldenGrowth), s.Length);
    }

    [Fact]
    public void TheBodyGrowsToItsNewLengthAndNoFurther()
    {
        var s = Snake(x: 300, heading: 0);
        for (int i = 0; i < 4; i++) s.TryEat(s.Head, 10, golden: false);
        for (int i = 0; i < 200; i++) s.Step(1.0 / 60, s.Head + new Vec2(500, 0));
        Assert.InRange(PathLength(s), s.Length - 4, s.Length + 4);
    }

    [Fact]
    public void ItsSpeedTopsOut()
    {
        var s = Snake();
        for (int i = 0; i < 500; i++) s.TryEat(s.Head, 10, golden: false);
        Assert.Equal(SnakeRules.MaxSpeed, s.Speed);
    }

    [Fact]
    public void BitingItsTailEndsTheGame()
    {
        // a long snake circling a point just outside the calm radius curls into its own body
        var s = Snake(heading: 0);
        for (int i = 0; i < 12; i++) s.TryEat(s.Head, 10, golden: false);
        var pivot = s.Head + new Vec2(0, 40);
        bool alive = true;
        for (int i = 0; i < 600 && alive; i++) alive = s.Step(1.0 / 60, pivot);
        Assert.True(s.Dead);
        Assert.False(s.Step(0.1, pivot)); // and stays so
    }

    [Fact]
    public void AShortSnakeCanTurnWithoutBitingItsNeck()
    {
        var s = Snake(heading: 0);
        for (int i = 0; i < 120; i++) s.Step(1.0 / 60, s.Head + new Vec2(-300, 10)); // a U-turn
        Assert.False(s.Dead);
    }

    [Fact]
    public void ACursorLeftStillMakesItLoopRatherThanCoil()
    {
        // the start length is short enough that its loops round a still cursor never close on the body
        var s = Snake(heading: 0);
        var still = new Vec2(1100, 520);
        for (int i = 0; i < 60 * 20; i++) s.Step(1.0 / 60, still);
        Assert.False(s.Dead);
        Assert.True((s.Head - still).Length < 300);
    }

    [Fact]
    public void NearFindsTheBody()
    {
        var s = Snake(heading: 0);
        Assert.True(s.Near(s.Path[^1], 5));
        Assert.False(s.Near(new Vec2(10, 10), 50));
    }
}
