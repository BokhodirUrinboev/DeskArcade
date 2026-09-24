using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DeskArcade.Engine;
using Xunit;

namespace DeskArcade.Tests;

public class EaseTests
{
    public static IEnumerable<object[]> Curves() => new Func<double, double>[]
    {
        Ease.Linear, Ease.InQuad, Ease.OutQuad, Ease.InOutQuad, Ease.InCubic, Ease.OutCubic, Ease.InOutCubic,
        Ease.OutBack, Ease.OutElastic, Ease.OutBounce,
    }.Select(f => new object[] { f });

    [Theory]
    [MemberData(nameof(Curves))]
    public void EveryCurveStartsAtZeroAndEndsAtOne(Func<double, double> curve)
    {
        Assert.Equal(0, curve(0), 9);
        Assert.Equal(1, curve(1), 9);
    }

    [Fact]
    public void PulseGoesUpAndBackDown()
    {
        Assert.Equal(0, Ease.Pulse(0), 9);
        Assert.Equal(1, Ease.Pulse(0.5), 9);
        Assert.Equal(0, Ease.Pulse(1), 9);
    }
}

public class AnimsTests
{
    [Fact]
    public void TweenRunsToOneThenCallsDone()
    {
        Fx.ReducedMotion = false;
        var anims = new Anims();
        var seen = new List<double>();
        bool done = false;
        anims.Add(1.0, seen.Add, Ease.Linear, () => done = true);

        Assert.True(anims.Update(0.25));
        Assert.True(anims.Update(0.25));
        Assert.False(done);
        Assert.False(anims.Update(0.6)); // past the end: the final frame is exactly 1, then done
        Assert.True(done);
        Assert.Equal(new[] { 0.25, 0.5, 1.0 }, seen);
        Assert.False(anims.Busy);
    }

    [Fact]
    public void DelayIsHonouredBeforeTheTweenStarts()
    {
        Fx.ReducedMotion = false;
        var anims = new Anims();
        var seen = new List<double>();
        anims.Add(1.0, seen.Add, Ease.Linear, delay: 0.5);
        anims.Update(0.25);
        Assert.Empty(seen);
        anims.Update(0.5); // 0.25 into the delay's end, so the tween has run 0.25
        Assert.Equal(new[] { 0.25 }, seen);
    }

    [Fact]
    public void ReducedMotionJumpsToTheEndButKeepsDelays()
    {
        Fx.ReducedMotion = true;
        try
        {
            var anims = new Anims();
            var seen = new List<double>();
            bool fired = false;
            anims.Add(2.0, seen.Add);
            anims.After(1.0, () => fired = true);
            anims.Update(0.01);
            Assert.Equal(new[] { 1.0 }, seen);
            Assert.False(fired);
            anims.Update(1.0);
            Assert.True(fired);
        }
        finally
        {
            Fx.ReducedMotion = false;
        }
    }

    [Fact]
    public void DoneMayStartAnotherTweenAndCancelDropsQuietly()
    {
        Fx.ReducedMotion = false;
        var anims = new Anims();
        int second = 0, cancelled = 0;
        anims.Add(0.1, _ => { }, done: () => anims.Add(0.1, _ => second++));
        var t = anims.Add(0.1, _ => cancelled++);
        t.Cancel();
        anims.Update(0.2);
        Assert.Equal(0, cancelled);
        Assert.True(anims.Busy);
        anims.Update(0.2);
        Assert.True(second > 0);
        Assert.False(anims.Busy);
    }

    [Fact]
    public void FinishEndsEverythingAtOnce()
    {
        Fx.ReducedMotion = false;
        var anims = new Anims();
        double last = -1;
        int done = 0;
        anims.Add(5, k => last = k, done: () => done++);
        anims.Add(5, k => last = k, done: () => done++);
        anims.Finish();
        Assert.Equal(1, last);
        Assert.Equal(2, done);
        Assert.False(anims.Busy);
    }
}

public class CpuRivalTests
{
    static double Average(int level, int reference, bool lower, int seed = 1)
    {
        var rng = new Random(seed);
        double sum = 0;
        for (int i = 0; i < 200; i++)
        {
            var cpu = new CpuRival(level, reference, lower, 30, rng);
            while (!cpu.Done) cpu.Tick(0.25);
            sum += cpu.Score;
        }
        return sum / 200;
    }

    [Fact]
    public void HigherLevelsScoreMoreWhenHigherIsBetter()
    {
        double easy = Average(1, 100, false), medium = Average(2, 100, false), hard = Average(3, 100, false), expert = Average(4, 100, false);
        Assert.True(easy < medium && medium < hard && hard < expert, $"{easy} {medium} {hard} {expert}");
        Assert.InRange(hard, 80, 105);   // about the reference, minus the off days
        Assert.InRange(expert, 105, 135);
    }

    [Fact]
    public void HigherLevelsFinishInFewerWhenLowerIsBetter()
    {
        double easy = Average(1, 20, true), expert = Average(4, 20, true);
        Assert.True(easy > expert, $"{easy} {expert}");
        Assert.InRange(expert, 14, 20);
        Assert.InRange(easy, 25, 35);
    }

    [Fact]
    public void ScoreClimbsThenStopsWhenFinished()
    {
        var cpu = new CpuRival(3, 100, false, 20, new Random(3));
        int last = 0;
        bool changed = false;
        for (int i = 0; i < 20; i++) // 10 s of a round that lasts 17 to 23
        {
            changed |= cpu.Tick(0.5);
            Assert.True(cpu.Score >= last);
            last = cpu.Score;
        }
        Assert.True(changed);
        Assert.False(cpu.Done);
        cpu.Finish();
        Assert.True(cpu.Done);
        Assert.False(cpu.Tick(1));
        Assert.Equal(last, cpu.Score);
    }

    [Fact]
    public void FewerIsBetterKnowsItsCountFromTheStart()
    {
        var cpu = new CpuRival(2, 20, true, 20, new Random(5));
        Assert.Equal(cpu.Target, cpu.Score);
        Assert.True(cpu.Target >= 1);
        cpu.Tick(100);
        Assert.True(cpu.Done);
        Assert.Equal(cpu.Target, cpu.Score);
    }
}

public class SettingsTests
{
    [Fact]
    public void LevelsStillLoadFromTheOldBoardLevelsName()
    {
        var s = JsonSerializer.Deserialize<Settings>("""{"BoardLevels":{"chess":3},"Positions":{"chess":[10,20]}}""")!;
        Assert.Equal(3, s.Levels["chess"]);
        Assert.Equal(new[] { 10.0, 20.0 }, s.Positions["chess"]);
        string json = JsonSerializer.Serialize(s);
        Assert.Contains("\"BoardLevels\"", json);
        Assert.DoesNotContain("\"Levels\"", json);
        s.ResetPositions();
        Assert.Empty(s.Positions);
    }
}
