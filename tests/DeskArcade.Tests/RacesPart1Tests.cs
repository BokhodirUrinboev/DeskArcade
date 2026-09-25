using System;
using System.Linq;
using DeskArcade.Engine;
using DeskArcade.Games;
using Xunit;

namespace DeskArcade.Tests;

public class JuggleRaceTests
{
    [Fact]
    public void KickSendsTheBallUpAndAwayFromTheClick()
    {
        var left = JuggleGame.KickVelocity(-1, 0, 0);   // clicked on the ball's right edge: it goes left
        var right = JuggleGame.KickVelocity(1, 0, 0);
        var centre = JuggleGame.KickVelocity(0, 0, 0);
        Assert.True(left.X < 0 && right.X > 0);
        Assert.Equal(0, centre.X, 9);
        Assert.True(left.Y < 0 && right.Y < 0 && centre.Y < 0);
        Assert.True(-centre.Y > -left.Y, "a clean kick under the middle goes highest");
    }

    [Fact]
    public void KickKeepsALittleOfTheSidewaysSpeedAndClampsTheOffset()
    {
        var v = JuggleGame.KickVelocity(0, 500, 0);
        Assert.Equal(100, v.X, 9);
        Assert.Equal(JuggleGame.KickVelocity(1, 0, 0).X, JuggleGame.KickVelocity(3, 0, 0).X, 9);
        Assert.Equal(JuggleGame.KickVelocity(0, 0, 0).Y - 60, JuggleGame.KickVelocity(0, 0, 60).Y, 9);
    }

    [Fact]
    public void BaselineIsWithinReachOfADecentRun()
    {
        Assert.InRange(JuggleGame.Baseline, 10, 30);
        Assert.InRange(JuggleGame.RunSeconds, 10, 40);
    }
}

public class BugsRaceTests
{
    [Fact]
    public void ScurryRocksTheBodyEachWayOncePerStrideAndIsLevelAtTheStep()
    {
        Assert.Equal(0, BugsGame.ScurryTilt(0), 9);
        Assert.Equal(0, BugsGame.ScurryTilt(1), 9);
        Assert.Equal(3, BugsGame.ScurryTilt(0.25), 9);
        Assert.Equal(-3, BugsGame.ScurryTilt(0.75), 9);
        for (double t = 0; t <= 5; t += 0.05) Assert.InRange(BugsGame.ScurryTilt(t), -3, 3);
    }

    [Fact]
    public void BaselineFitsAThirtySecondRound()
    {
        Assert.InRange(BugsGame.Baseline, 25, 60);
    }
}

public class CansRaceTests
{
    [Theory]
    [InlineData(1, 3, 3)]
    [InlineData(2, 3, 3)]
    [InlineData(3, 4, 3)]
    [InlineData(4, 4, 3)]
    [InlineData(5, 5, 4)]
    [InlineData(9, 5, 4)]
    public void StacksGrowEveryTwoClearsUpToFiveRowsWithAFourthBallForTheBiggest(int stack, int rows, int balls)
    {
        Assert.Equal((rows, balls), CansGame.StackShape(stack));
    }

    [Fact]
    public void PyramidStandsOnTheShelfCentredWithOneCanOnTop()
    {
        var homes = CansGame.PyramidHomes(4, 500, 300);
        Assert.Equal(10, homes.Count); // 4 + 3 + 2 + 1
        Assert.Equal(4, homes.Take(4).Count(h => h.Y == homes[0].Y)); // the bottom row comes first
        Assert.True(homes[0].Y < 300 && homes[0].Y > 300 - 40, "the bottom row rests on the shelf");
        Assert.Equal(500, homes.Take(4).Average(h => h.X), 9);
        var top = homes[^1];
        Assert.Equal(500, top.X, 9);
        Assert.True(top.Y < homes[0].Y - 100, "the top can sits three rows up");
        Assert.Equal(1, homes.Count(h => h.Y == top.Y));
    }

    [Fact]
    public void CansDropInOneAfterAnother()
    {
        Assert.Equal(0, CansGame.DropDelay(0), 9);
        for (int i = 1; i < 15; i++) Assert.True(CansGame.DropDelay(i) > CansGame.DropDelay(i - 1));
        Assert.True(CansGame.DropDelay(14) < 1.5, "even the biggest stack is up within a moment");
    }

    [Fact]
    public void WobbleRocksBothWaysAndDiesOut()
    {
        Assert.Equal(0, CansGame.WobbleAngle(0), 9);
        Assert.Equal(0, CansGame.WobbleAngle(1), 9);
        Assert.True(CansGame.WobbleAngle(0.125) > 4);
        Assert.True(CansGame.WobbleAngle(0.375) < -3);
        Assert.True(Math.Abs(CansGame.WobbleAngle(0.875)) < Math.Abs(CansGame.WobbleAngle(0.125)), "each swing is smaller than the last");
    }

    [Fact]
    public void BaselineIsAboutTwoStacksAndABit()
    {
        Assert.InRange(CansGame.Baseline, 20, 45);
        Assert.InRange(CansGame.GameSeconds, 20, 90);
    }
}

public class BricksRaceTests
{
    [Fact]
    public void WallsGetTallerEachLevelUpToEightRows()
    {
        Assert.Equal(4, BricksGame.WallRows(1));
        Assert.Equal(5, BricksGame.WallRows(2));
        Assert.Equal(8, BricksGame.WallRows(5));
        Assert.Equal(8, BricksGame.WallRows(20));
    }

    [Fact]
    public void WallsFitTheScreenWithAtLeastFourColumns()
    {
        Assert.Equal(16, BricksGame.WallColumns(1920));
        Assert.Equal(16, BricksGame.WallColumns(2560)); // no wider than 1180 px
        Assert.Equal(13, BricksGame.WallColumns(1024)); // 944 px of wall
        Assert.Equal(4, BricksGame.WallColumns(200));
    }

    [Fact]
    public void BricksFallInRowByRowRipplingAcrossEachRow()
    {
        Assert.Equal(0, BricksGame.FallDelay(0, 0), 9);
        Assert.True(BricksGame.FallDelay(0, 15) < BricksGame.FallDelay(1, 0), "a whole row starts before the next row begins");
        Assert.True(BricksGame.FallDelay(2, 3) > BricksGame.FallDelay(2, 2));
        Assert.True(BricksGame.FallDelay(7, 15) < 1.2, "the tallest wall is in place within a moment");
    }

    [Fact]
    public void ClearWaveRollsLeftToRightInUnderASecond()
    {
        Assert.Equal(0, BricksGame.WaveDelay(0), 9);
        for (int c = 1; c < 16; c++) Assert.True(BricksGame.WaveDelay(c) > BricksGame.WaveDelay(c - 1));
        Assert.True(BricksGame.WaveDelay(15) < 1);
    }

    [Fact]
    public void BaselineIsAboutOneWall()
    {
        Assert.InRange(BricksGame.Baseline, 100, 250);
        Assert.InRange(BricksGame.GameSeconds, 30, 120);
    }
}

public class RaceRivalTargetsTests
{
    /// <summary>The computer rival aims at the better of the game's baseline and the player's best, scaled by its level.</summary>
    [Theory]
    [InlineData(JuggleGame.Baseline)]
    [InlineData(BugsGame.Baseline)]
    [InlineData(CansGame.Baseline)]
    [InlineData(BricksGame.Baseline)]
    public void EveryBaselineGivesTheRivalASensibleTarget(int baseline)
    {
        var rng = new Random(7);
        var easy = new CpuRival(1, baseline, false, 30, rng);
        var expert = new CpuRival(4, baseline, false, 30, rng);
        Assert.InRange(easy.Target, 1, baseline);
        Assert.True(expert.Target > easy.Target);
        Assert.InRange(expert.Target, baseline * 0.9, baseline * 1.6);
    }

    [Fact]
    public void ABetterPersonalBestRaisesTheBar()
    {
        int reference = Math.Max(BugsGame.Baseline, 90); // the player's best round beats the baseline
        var cpu = new CpuRival(3, reference, false, 30, new Random(1));
        Assert.True(cpu.Target > BugsGame.Baseline);
    }
}
