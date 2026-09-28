using System;
using System.Linq;
using DeskArcade.Engine;
using DeskArcade.Games;
using Xunit;

namespace DeskArcade.Tests;

public class CurlingTests
{
    static Vec2 Toward(Vec2 target, double speed)
    {
        var d = (target - CurlingRules.Hack).Normalized();
        return d * speed;
    }

    static void ThrowAndSettle(CurlingRules g, Vec2 v)
    {
        Assert.True(g.Throw(v));
        g.Finish();
        Assert.False(g.Moving);
    }

    [Fact]
    public void ADrawWeightStopsOnTheButton()
    {
        var g = new CurlingRules();
        double speed = g.DrawSpeed((CurlingRules.Button - CurlingRules.Hack).Length);
        ThrowAndSettle(g, Toward(CurlingRules.Button, speed));
        var stone = g.Stones.Single();
        Assert.False(stone.Sunk);
        Assert.True((stone.Pos - CurlingRules.Button).Length < 6, $"stopped at {stone.Pos}");
    }

    [Fact]
    public void AStoneShortOfTheHogLineIsOutOfPlay()
    {
        var g = new CurlingRules();
        ThrowAndSettle(g, Toward(CurlingRules.Button, 120));
        Assert.True(g.Stones.Single().Sunk);
    }

    [Fact]
    public void AStoneThroughTheHouseIsOutOfPlay()
    {
        var g = new CurlingRules();
        ThrowAndSettle(g, Toward(CurlingRules.Button, CurlingRules.MaxSpeed));
        Assert.True(g.Stones.Single().Sunk);
    }

    [Fact]
    public void AStoneAgainstASideBoardIsOutOfPlay()
    {
        var g = new CurlingRules();
        ThrowAndSettle(g, new Vec2(250, 120)); // steered into the far board
        Assert.True(g.Stones.Single().Sunk);
    }

    [Fact]
    public void SidesTakeTurnsAndSweepingSlidesFurther()
    {
        var plain = new CurlingRules();
        var swept = new CurlingRules();
        double speed = plain.DrawSpeed(700);
        ThrowAndSettle(plain, new Vec2(speed, 0));
        Assert.True(swept.Throw(new Vec2(speed, 0)));
        swept.Sweep(true);
        swept.Finish();
        Assert.True(swept.Stones[0].Pos.X > plain.Stones[0].Pos.X + 40);
        Assert.Equal(1, plain.Turn);
        Assert.Equal(1, plain.Thrown);
    }

    [Fact]
    public void AThrowIsRefusedWhileAStoneSlides()
    {
        var g = new CurlingRules();
        Assert.True(g.Throw(new Vec2(300, 0)));
        Assert.False(g.Throw(new Vec2(300, 0)));
    }

    [Fact]
    public void TheNearestSideScoresEachStoneInsideTheOthersBest()
    {
        var g = new CurlingRules();
        var b = CurlingRules.Button;
        g.Sync(new[]
        {
            (b.X + 5, b.Y, 0, false), (b.X - 25, b.Y, 0, false), (b.X, b.Y + 40, 1, false), (b.X + 50, b.Y - 5, 0, false),
            (b.X + 200, b.Y, 1, false), // nowhere near the house
        });
        Assert.Equal((0, 2), g.Count()); // the third is farther than side 1's best
    }

    [Fact]
    public void NoStoneInTheHouseIsABlankEnd()
    {
        var g = new CurlingRules();
        g.Sync(new[] { (CurlingRules.Button.X - 150, CurlingRules.Button.Y, 0, false) });
        Assert.Equal((-1, 0), g.Count());
    }

    [Fact]
    public void AGameIsThreeEndsOfEightStones()
    {
        var g = new CurlingRules();
        var rng = new Random(3);
        int throws = 0;
        while (!g.Over && throws < 100)
        {
            ThrowAndSettle(g, g.CpuThrow(3, rng));
            throws++;
        }
        Assert.True(g.Over);
        Assert.Equal(CurlingRules.Ends * CurlingRules.StonesPerSide * 2, throws);
        Assert.Equal(g.Score(0) > g.Score(1) ? 0 : g.Score(1) > g.Score(0) ? 1 : -1, g.Winner);
    }

    [Fact]
    public void TheSideThatScoredThrowsSecondNextEnd()
    {
        var g = new CurlingRules();
        var rng = new Random(11);
        for (int i = 0; i < CurlingRules.StonesPerSide * 2; i++) ThrowAndSettle(g, g.CpuThrow(4, rng));
        Assert.Equal(2, g.End);
        var (side, _) = g.LastEnd;
        if (side >= 0) Assert.Equal(1 - side, g.Turn);
        Assert.Empty(g.Stones); // the ice is cleared for the new end
        Assert.NotEmpty(g.EndStones);
    }

    [Fact]
    public void BetterComputersLandNearerTheButton()
    {
        double Mean(int level)
        {
            var rng = new Random(level * 7);
            double total = 0;
            for (int i = 0; i < 40; i++)
            {
                var g = new CurlingRules();
                ThrowAndSettle(g, g.CpuThrow(level, rng));
                var s = g.Stones[0];
                total += s.Sunk ? 300 : (s.Pos - CurlingRules.Button).Length;
            }
            return total / 40;
        }
        Assert.True(Mean(4) < Mean(1));
    }

    [Fact]
    public void ARemoteThrowWaitsForTheThrowersResult()
    {
        var g = new CurlingRules();
        Assert.True(g.Throw(new Vec2(300, 0), remote: true));
        for (int i = 0; i < 5000; i++) g.Step(0.01);
        Assert.True(g.Moving); // stopped here, but the thrower's screen has the last word
        g.SettleFrom(new[] { (CurlingRules.Button.X, CurlingRules.Button.Y, 0, false) });
        Assert.False(g.Moving);
        Assert.Equal(1, g.Thrown);
        Assert.Equal(1, g.Turn);
        Assert.Equal(CurlingRules.Button, g.Stones.Single().Pos);
    }
}
