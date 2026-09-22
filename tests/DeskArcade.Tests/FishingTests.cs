using System;
using System.Linq;
using DeskArcade.Games;
using Xunit;

namespace DeskArcade.Tests;

public class FishFightTests
{
    const double Dt = 1.0 / 120, Line = 500;

    static FishFight Fight(string id, bool heaviest, int seed)
    {
        var s = FishFight.ById(id);
        return new FishFight(s, heaviest ? s.MaxKg : s.MinKg, Line, new Random(seed), Line * 1.3);
    }

    /// <summary>Runs the fight to its end (or two minutes) with the given reel strategy.</summary>
    static FishFight Run(FishFight f, Func<FishFight, bool> reel) => Run(f, reel, out _);

    static FishFight Run(FishFight f, Func<FishFight, bool> reel, out double seconds)
    {
        int i = 0;
        for (; i < 120 * 120 && !f.Over; i++) f.Step(Dt, reel(f));
        seconds = i * Dt;
        return f;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void HoldingTheReelOnABigCatfishSnapsTheLine(int seed)
    {
        var f = Run(Fight("catfish", heaviest: true, seed), _ => true);
        Assert.True(f.Snapped);
        Assert.False(f.Landed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void PulsingTheReelLandsTheSameCatfish(int seed)
    {
        var f = Run(Fight("catfish", heaviest: true, seed), x => x.Tension < 0.7, out double seconds);
        Assert.True(f.Landed, $"still {f.Distance:0} px out, tension {f.Tension:0.00}");
        Assert.False(f.Snapped);
        Assert.InRange(seconds, 4, 25); // a real fight, but not a whole round
    }

    [Fact]
    public void TheBigCatfishHasToBeReeledFarSlowerThanAPerch()
    {
        Run(Fight("catfish", heaviest: true, 1), x => x.Tension < 0.7, out double catfish);
        Run(Fight("perch", heaviest: false, 1), _ => true, out double perch);
        Assert.True(catfish > perch * 1.5, $"catfish {catfish:0.0}s vs perch {perch:0.0}s");
    }

    [Fact]
    public void PulsingAtTheDemosTenTimesASecondStillLandsIt()
    {
        // the demo decides once every 0.1 s, so the reel overshoots the threshold a little
        for (int seed = 1; seed <= 5; seed++)
        {
            var f = Fight("catfish", heaviest: true, seed);
            bool reel = true;
            for (int i = 0; i < 120 * 120 && !f.Over; i++)
            {
                if (i % 12 == 0) reel = f.Tension < 0.7;
                f.Step(Dt, reel);
            }
            Assert.True(f.Landed, $"seed {seed}");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void ASmallPerchCanBeLandedByJustHolding(int seed)
    {
        var f = Run(Fight("perch", heaviest: true, seed), _ => true);
        Assert.True(f.Landed);
        Assert.True(f.Tension < FishFight.SnapAt);
    }

    [Fact]
    public void LettingGoLetsTheFishTakeLineButNeverMoreThanTheMax()
    {
        var f = Fight("pike", heaviest: true, 1);
        for (int i = 0; i < 120 * 60; i++) f.Step(Dt, false);
        Assert.False(f.Over);
        Assert.Equal(f.MaxDistance, f.Distance, 3);
        Assert.True(f.Tension < 0.7);
    }

    [Fact]
    public void HeavierFishAreWorthMoreAndPointsNeverDropBelowTheBase()
    {
        foreach (var s in FishFight.Table)
        {
            Assert.Equal(s.Points, FishFight.PointsFor(s, s.MinKg));
            Assert.Equal(s.Points * 2, FishFight.PointsFor(s, s.MaxKg));
            Assert.True(FishFight.PointsFor(s, (s.MinKg + s.MaxKg) / 2) > s.Points);
        }
    }

    [Fact]
    public void WeightsStayInRangeAndGoldenTroutAreRare()
    {
        var rng = new Random(42);
        int golden = 0;
        for (int i = 0; i < 5000; i++)
        {
            var s = FishFight.Pick(rng);
            if (s.Golden) golden++;
            double kg = FishFight.RollKg(s, rng);
            Assert.InRange(kg, s.MinKg - 0.05, s.MaxKg + 0.05);
        }
        Assert.InRange(golden, 100, 350); // about 4%
    }
}

public class FishingSoundTests
{
    [Theory]
    [InlineData("splash")]
    [InlineData("plop")]
    [InlineData("reel")]
    public void FishingClipsAreSynthesizedAudibleAndUnclipped(string name)
    {
        using var sound = new Sound(new DeskArcade.Platform.NullPlatform());
        var clip = sound.Samples(name);
        Assert.True(clip != null, $"no clip named {name}");
        Assert.True(clip!.Length > 1000, $"{name} is too short");
        Assert.All(clip, x => Assert.True(float.IsFinite(x)));
        Assert.InRange(clip.Max(Math.Abs), 0.2f, 0.8f);
    }
}

public class BiteScheduleTests
{
    const double Dt = 1.0 / 120;

    static BiteSchedule WithNibbles(int seed = 1)
    {
        for (; ; seed++)
        {
            var b = new BiteSchedule(new Random(seed));
            if (b.NibbleTimes.Count > 0) return b;
        }
    }

    static void AdvanceTo(BiteSchedule b, double t)
    {
        while (b.T < t) b.Advance(Math.Min(Dt, t - b.T + 1e-9));
    }

    [Fact]
    public void ClickingDuringANibbleSpooksTheFish()
    {
        var b = WithNibbles();
        AdvanceTo(b, b.NibbleTimes[0] + BiteSchedule.NibbleTime / 2);
        Assert.True(b.Dip > 0);
        Assert.Equal(BiteClick.Spooked, b.Click());
    }

    [Fact]
    public void TheBiteWindowHooksAndLateIsTooLate()
    {
        for (int seed = 1; seed <= 20; seed++)
        {
            var early = new BiteSchedule(new Random(seed));
            AdvanceTo(early, early.BiteAt - 0.05);
            Assert.Equal(BiteClick.Spooked, early.Click());

            var onTime = new BiteSchedule(new Random(seed));
            AdvanceTo(onTime, onTime.BiteAt + 0.02);
            Assert.True(onTime.Biting);
            Assert.Equal(1, onTime.Dip);
            Assert.Equal(BiteClick.Hooked, onTime.Click());

            var lastMoment = new BiteSchedule(new Random(seed));
            AdvanceTo(lastMoment, lastMoment.BiteAt + BiteSchedule.Window - 0.02);
            Assert.Equal(BiteClick.Hooked, lastMoment.Click());

            var late = new BiteSchedule(new Random(seed));
            AdvanceTo(late, late.BiteAt + BiteSchedule.Window + 0.02);
            Assert.True(late.Missed);
            Assert.Equal(BiteClick.TooLate, late.Click());
        }
    }

    [Fact]
    public void EventsComeInOrderNibblesThenTheBiteThenTheMiss()
    {
        for (int seed = 1; seed <= 20; seed++)
        {
            var b = new BiteSchedule(new Random(seed));
            Assert.InRange(b.NibbleTimes.Count, 0, 3);
            var events = Enumerable.Range(0, 120 * 8).Select(_ => b.Advance(Dt)).Where(e => e != BiteEvent.None).ToList();
            var expected = Enumerable.Repeat(BiteEvent.Nibble, b.NibbleTimes.Count).Append(BiteEvent.Bite).Append(BiteEvent.Missed);
            Assert.Equal(expected, events);
        }
    }
}
