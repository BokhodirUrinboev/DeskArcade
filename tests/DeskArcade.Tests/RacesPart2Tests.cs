using System;
using System.Linq;
using DeskArcade.Engine;
using DeskArcade.Games;
using Xunit;

namespace DeskArcade.Tests;

public class DartsRaceTests
{
    static double[] Samples => Enumerable.Range(0, 201).Select(i => i / 200.0).ToArray();

    [Fact]
    public void AFinishedLegCountsTheDartsItTook()
    {
        Assert.Equal(18, DartsRace.Score(18, gaveUp: false));
        Assert.Equal(0, DartsRace.Score(-3, gaveUp: false));
    }

    [Fact]
    public void AGivenUpLegCountsAsALongOne()
    {
        Assert.Equal(DartsRace.GivenUp, DartsRace.Score(12, gaveUp: true));
        Assert.Equal(120, DartsRace.Score(120, gaveUp: true)); // already past it: no better for giving up
        Assert.True(DartsRace.Score(12, gaveUp: true) > DartsRace.Score(60, gaveUp: false));
    }

    [Fact]
    public void GivingUpLosesToTheComputerAtEveryLevel()
    {
        var rng = new Random(7);
        for (int level = 1; level <= MiniGame.LevelNames.Length; level++)
        {
            for (int i = 0; i < 20; i++)
            {
                var cpu = new CpuRival(level, 27, lowerIsBetter: true, 80, rng);
                Assert.True(cpu.Target < DartsRace.Score(cpu.Target, gaveUp: true));
            }
        }
    }

    [Fact]
    public void TheDartLeavesBigShrinksInTheAirAndSettlesToItsSize()
    {
        Assert.Equal(2.2, DartsRace.Scale(0), 6);
        Assert.Equal(1, DartsRace.Scale(1), 6);
        var sizes = Samples.Select(DartsRace.Scale).ToArray();
        double smallest = sizes.Min();
        Assert.True(smallest < 1);
        Assert.True(smallest >= 0.8 - 1e-9);
        Assert.True(Array.IndexOf(sizes, smallest) > sizes.Length / 2); // the far point comes late in the flight
    }

    [Fact]
    public void TheArcRisesAndComesBackDown()
    {
        Assert.Equal(0, DartsRace.Lift(0), 6);
        Assert.Equal(1, DartsRace.Lift(0.5), 6);
        Assert.Equal(0, DartsRace.Lift(1), 6);
        Assert.True(DartsRace.Pitch(0) < 0);
        Assert.Equal(0, DartsRace.Pitch(1), 6);
    }

    [Fact]
    public void TheThudQuiversBothWaysAndDiesDown()
    {
        Assert.Equal(0, DartsRace.Thud(0), 6);
        Assert.Equal(0, DartsRace.Thud(1), 6);
        var angles = Samples.Select(DartsRace.Thud).ToArray();
        Assert.True(angles.Any(a => a > 1) && angles.Any(a => a < -1));
        Assert.True(angles.All(a => Math.Abs(a) <= 9));
        double early = angles.Take(angles.Length / 3).Max(Math.Abs), late = angles.Skip(2 * angles.Length / 3).Max(Math.Abs);
        Assert.True(early > late);
    }
}

public class ClayMathsTests
{
    [Fact]
    public void TheRecoilKicksEarlyAndSettles()
    {
        Assert.Equal(0, ClayMaths.Recoil(0), 6);
        Assert.Equal(1, ClayMaths.Recoil(1.0 / 3), 6);
        Assert.Equal(0, ClayMaths.Recoil(1), 6);
        Assert.True(ClayMaths.Recoil(0.9) < 0.1);
        Assert.True(Enumerable.Range(0, 101).Select(i => ClayMaths.Recoil(i / 100.0)).All(v => v is >= 0 and <= 1 + 1e-9));
        Assert.Equal(0, ClayMaths.Recoil(2), 6); // clamped
    }

    [Fact]
    public void ShardsStayInTheBoxAndComeToRestOnTheFloor()
    {
        var pos = new Vec2(50, 50);
        var vel = new Vec2(-400, -300);
        double spin = 900;
        const double dt = 1.0 / 120;
        for (int i = 0; i < 4 * 120; i++)
        {
            ClayMaths.StepShard(ref pos, ref vel, ref spin, dt, 0, 0, 200, 100);
            Assert.InRange(pos.X, 0, 200);
            Assert.InRange(pos.Y, 0, 100);
        }
        Assert.Equal(98, pos.Y, 3); // settled just above the floor
        Assert.True(Math.Abs(vel.Y) < 60);
        Assert.True(Math.Abs(spin) < 900);
    }

    [Fact]
    public void AShardIsSolidForHalfItsLifeThenFades()
    {
        Assert.Equal(1, ClayMaths.ShardOpacity(0), 6);
        Assert.Equal(1, ClayMaths.ShardOpacity(0.5), 6);
        Assert.Equal(0.5, ClayMaths.ShardOpacity(0.75), 6);
        Assert.Equal(0, ClayMaths.ShardOpacity(1), 6);
        Assert.Equal(0, ClayMaths.ShardOpacity(1.5), 6);
    }
}

public class SlingshotMathsTests
{
    static double[] Samples => Enumerable.Range(1, 199).Select(i => i / 200.0).ToArray();

    [Fact]
    public void TheBandSnapsForwardFirstAndDiesDown()
    {
        Assert.Equal(0, SlingshotMaths.BandWobble(0), 6);
        Assert.Equal(0, SlingshotMaths.BandWobble(1), 6);
        var offsets = Samples.Select(SlingshotMaths.BandWobble).ToArray();
        Assert.True(offsets[0] > 0); // forward, along the shot
        Assert.True(offsets.Any(o => o < -0.5)); // and back past the rest
        Assert.True(offsets.All(o => Math.Abs(o) <= 9));
        Assert.True(offsets.Take(offsets.Length / 2).Max(Math.Abs) > offsets.Skip(offsets.Length / 2).Max(Math.Abs));
    }

    [Fact]
    public void TheHopIsTwoJumpsTheSecondSmaller()
    {
        Assert.Equal(0, SlingshotMaths.Hop(0), 6);
        Assert.Equal(0, SlingshotMaths.Hop(0.5), 6);
        Assert.Equal(0, SlingshotMaths.Hop(1), 6);
        Assert.True(Samples.Select(SlingshotMaths.Hop).All(h => h >= 0));
        Assert.True(SlingshotMaths.Hop(0.25) > SlingshotMaths.Hop(0.75));
        Assert.True(SlingshotMaths.Hop(0.75) > 0);
    }

    [Fact]
    public void HigherStoreysStartHigherUp()
    {
        Assert.Equal(150, SlingshotMaths.DropHeight(0), 6);
        Assert.True(SlingshotMaths.DropHeight(3) > SlingshotMaths.DropHeight(1));
        Assert.Equal(SlingshotMaths.DropHeight(0), SlingshotMaths.DropHeight(-1), 6);
    }
}

public class PlinkoPlacementTests
{
    [Fact]
    public void TheGripsPlaceWinsOverTheOldSettings()
    {
        var restored = PlinkoPlacement.Restore(new Vec2(300, 400), 100, 200, 10, 20);
        Assert.Equal(new Vec2(300, 400), restored);
    }

    [Fact]
    public void TheOldSettingsAreReadRelativeToTheArena()
    {
        Assert.Equal(new Vec2(110, 220), PlinkoPlacement.Restore(null, 100, 200, 10, 20));
    }

    [Fact]
    public void NothingSavedMeansNoPlace()
    {
        Assert.Null(PlinkoPlacement.Restore(null, null, null, 0, 0));
        Assert.Null(PlinkoPlacement.Restore(null, 5, null, 0, 0));
        Assert.Null(PlinkoPlacement.Restore(null, null, 5, 0, 0));
    }

    [Fact]
    public void ABoardLetGoNearTheFloorStandsOnIt()
    {
        Assert.True(PlinkoPlacement.StandsOnFloor(1000, 1000));
        Assert.True(PlinkoPlacement.StandsOnFloor(1000, 1027));
        Assert.False(PlinkoPlacement.StandsOnFloor(1000, 1028));
        Assert.False(PlinkoPlacement.StandsOnFloor(500, 1000));
    }
}
