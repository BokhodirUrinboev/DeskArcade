using System;
using System.Collections.Generic;
using System.Linq;
using DeskArcade.Engine;
using DeskArcade.Games;
using Xunit;
using static DeskArcade.Games.SheepRules;

namespace DeskArcade.Tests;

public class SheepTests
{
    const double Floor = 800;
    static readonly Ledge[] NoWindows = Array.Empty<Ledge>();

    /// <summary>A 1000-wide screen with the pen at 820–980 on the floor, its gate on the left, the round on and no strays.</summary>
    static SheepRules World(bool gateLeft = true, double penX1 = 820, double penX2 = 980, int seed = 1) =>
        new(0, 1000, Floor, penX1, penX2, gateLeft, 90, new Random(seed)) { Running = true, Strays = false };

    static List<(Sheep Sheep, Event Event)> Run(SheepRules w, double seconds, (double, double)? dog = null, IReadOnlyList<Ledge>? windows = null, double dt = 1 / 60.0)
    {
        var events = new List<(Sheep, Event)>();
        for (double t = 0; t < seconds; t += dt) events.AddRange(w.Step(dt, dog, windows ?? NoWindows));
        return events;
    }

    // ------------------------------------------------------------------ the dog

    [Fact]
    public void SheepRunAwayFromTheDog()
    {
        var w = World();
        var s = w.Add(400, Floor);
        Run(w, 0.5, dog: (340, Floor - 10));
        Assert.True(s.X > 420, $"it only got to {s.X:0}");
        Assert.Equal(State.Fleeing, s.State);
        Assert.Equal(1, s.Dir);

        var other = World();
        var t = other.Add(400, Floor);
        Run(other, 0.5, dog: (460, Floor - 10)); // from the other side it runs the other way
        Assert.True(t.X < 380);
        Assert.Equal(-1, t.Dir);
    }

    [Fact]
    public void ADistantDogIsNoBother()
    {
        var w = World();
        var s = w.Add(400, Floor);
        var states = new HashSet<State>();
        for (int i = 0; i < 120; i++)
        {
            w.Step(1 / 60.0, (400 + FearRadius + 30, Floor - 10), NoWindows);
            states.Add(s.State);
            Assert.Equal(0, s.Fear);
        }
        Assert.DoesNotContain(State.Fleeing, states);
        Assert.InRange(s.X, 400 - WalkSpeed * 2.5, 400 + WalkSpeed * 2.5); // at most an amble
    }

    [Fact]
    public void TheCloserTheDogTheFasterTheyRun()
    {
        var near = World();
        var a = near.Add(400, Floor);
        Run(near, 0.4, dog: (370, Floor - 10));
        var far = World();
        var b = far.Add(400, Floor);
        Run(far, 0.4, dog: (400 - FearRadius + 20, Floor - 10));
        Assert.True(a.Vx > b.Vx + 30, $"near {a.Vx:0} vs far {b.Vx:0}");
    }

    [Fact]
    public void ABarkScaresHarderThanTheDogAndNeedsABreather()
    {
        var dogged = World();
        var a = dogged.Add(400, Floor);
        Run(dogged, 0.6, dog: (340, Floor - 10));

        var barked = World();
        var b = barked.Add(400, Floor);
        var outOfReach = barked.Add(340 + BarkRadius + 60, Floor);
        Assert.True(barked.TryBark(340, Floor - 10, out int scared));
        Assert.Equal(1, scared);
        Assert.False(barked.TryBark(340, Floor - 10, out _)); // still out of breath
        Run(barked, 0.6);
        Assert.True(b.X > a.X + 15, $"barked at {b.X:0}, dogged {a.X:0}");
        Assert.Equal(0, outOfReach.Panic);

        Run(barked, BarkCooldown);
        Assert.True(barked.BarkReady);
        Assert.True(barked.TryBark(500, Floor - 10, out _));
    }

    [Fact]
    public void TheyRunOutOfPanicAndCalmDown()
    {
        var w = World();
        var s = w.Add(300, Floor);
        w.TryBark(250, Floor - 10, out _);
        Run(w, PanicSeconds + 2);
        Assert.Equal(0, s.Panic);
        Assert.NotEqual(State.Fleeing, s.State);
        Assert.True(Math.Abs(s.Vx) <= WalkSpeed + 1);
    }

    // ------------------------------------------------------------------ flocking

    [Fact]
    public void FrightSpreadsToTheNeighbours()
    {
        var w = World();
        var a = w.Add(400, Floor);
        var b = w.Add(480, Floor); // out of the dog's reach, but next to a
        var dog = (250.0, Floor - 10);
        Assert.True(Math.Sqrt((480 - 250) * (480 - 250) + 2.0 * 2) > FearRadius);
        Run(w, 0.6, dog: dog);
        Assert.Equal(0, b.Fear);
        Assert.True(b.Vx > WalkSpeed, $"the neighbour stood still ({b.Vx:0})");
        Assert.True(b.X > 490);
        Assert.True(a.X > 420);
    }

    [Fact]
    public void SheepDoNotStandInEachOther()
    {
        var w = World();
        var a = w.Add(500, Floor);
        var b = w.Add(502, Floor);
        Run(w, 3);
        Assert.True(Math.Abs(a.X - b.X) > Personal * 0.6, $"still {Math.Abs(a.X - b.X):0.0} apart");
    }

    [Fact]
    public void AStragglerAmblesBackToTheFlock()
    {
        int closer = 0;
        for (int seed = 1; seed <= 10; seed++)
        {
            var w = World(seed: seed);
            var lone = w.Add(380, Floor);
            foreach (double x in new[] { 590.0, 600, 610, 620 }) w.Add(x, Floor);
            Run(w, 25);
            double middle = w.Flock.Skip(1).Average(s => s.X);
            if (Math.Abs(middle - lone.X) < 170) closer++;
        }
        Assert.True(closer >= 7, $"only {closer} of 10 stragglers rejoined");
    }

    // ------------------------------------------------------------------ window tops

    [Fact]
    public void AScaredSheepHopsOffTheEndOfAWindowTop()
    {
        var w = World();
        var top = new[] { new Ledge(500, 300, 500, (IntPtr)42) };
        var s = w.Add(470, 500, (IntPtr)42);
        var events = Run(w, 2.5, dog: (400, 490), windows: top);
        Assert.Contains(events, e => e.Sheep == s && e.Event == Event.Hopped);
        Assert.Contains(events, e => e.Sheep == s && e.Event == Event.Landed);
        Assert.Equal(Floor, s.Y);
        Assert.Equal(IntPtr.Zero, s.On);
    }

    [Fact]
    public void ACalmSheepTurnsRoundAtTheEdge()
    {
        var w = World();
        var top = new[] { new Ledge(500, 300, 380, (IntPtr)42) };
        var s = w.Add(360, 500, (IntPtr)42);
        for (int i = 0; i < 60 * 40; i++)
        {
            w.Step(1 / 60.0, null, top);
            Assert.Equal(500, s.Y);
            Assert.InRange(s.X, 300, 380);
        }
    }

    [Fact]
    public void AFrightenedSheepJumpsUpOntoALowWindowTop()
    {
        var w = World();
        var low = new[] { new Ledge(Floor - 70, 340, 700, (IntPtr)7) };
        var s = w.Add(300, Floor);
        var events = new List<(Sheep Sheep, Event Event)>();
        bool up = false;
        for (int i = 0; i < 90 && !up; i++)
        {
            events.AddRange(w.Step(1 / 60.0, (250, Floor - 10), low));
            up = s.State != State.Airborne && s.Y == Floor - 70;
        }
        Assert.Contains(events, e => e.Event == Event.Jumped);
        Assert.True(up, $"it is at {s.X:0},{s.Y:0}");
        Assert.Equal((IntPtr)7, s.On);
    }

    [Fact]
    public void NoJumpOntoAWindowTooHighAbove()
    {
        var w = World();
        var high = new[] { new Ledge(Floor - JumpUp - 40, 340, 700, (IntPtr)7) };
        var s = w.Add(300, Floor);
        var events = Run(w, 1, dog: (250, Floor - 10), windows: high);
        Assert.DoesNotContain(events, e => e.Event == Event.Jumped);
        Assert.Equal(Floor, s.Y);
    }

    [Fact]
    public void AWindowThatGoesDropsItsSheepAndAMovingOneCarriesThem()
    {
        var w = World();
        var top = new[] { new Ledge(500, 300, 500, (IntPtr)42) };
        var s = w.Add(400, 500, (IntPtr)42);
        w.Running = false; // standing still, so only the window moves it
        w.Step(1 / 60.0, null, top);
        w.Carry((IntPtr)42, 30, -10);
        Assert.Equal(430, s.X, 3);
        Assert.Equal(490, s.Y, 3);

        Run(w, 1.5); // the window closed
        Assert.Equal(Floor, s.Y);
        Assert.NotEqual(State.Airborne, s.State);
    }

    // ------------------------------------------------------------------ the pen

    [Fact]
    public void ASheepThatWalksThroughTheGateIsPennedAndStaysIn()
    {
        var w = World();
        var s = w.Add(760, Floor);
        var events = Run(w, 1.5, dog: (700, Floor - 10));
        Assert.Contains(events, e => e.Sheep == s && e.Event == Event.Penned);
        Assert.Equal(State.Penned, s.State);
        Assert.Equal(1, w.Penned);

        // the dog in the pen and a bark at its gate change nothing
        w.TryBark(900, Floor - 10, out int scared);
        Assert.Equal(0, scared);
        for (int i = 0; i < 60 * 10; i++)
        {
            w.Step(1 / 60.0, (s.X - 10, Floor - 10), NoWindows);
            Assert.Equal(State.Penned, s.State);
            Assert.True(w.InPen(s.X), $"it got out to {s.X:0}");
        }
    }

    [Fact]
    public void TheClosedSideOfThePenIsAFence()
    {
        var w = World(gateLeft: false); // the gate faces right, so the left side is fenced
        var s = w.Add(700, Floor);
        var events = Run(w, 2, dog: (640, Floor - 10));
        Assert.DoesNotContain(events, e => e.Event == Event.Penned);
        Assert.Equal(State.Fleeing, s.State);
        Assert.InRange(s.X, 700, 820 - FenceGap + 0.01);

        var mirror = World(gateLeft: true, penX1: 20, penX2: 180); // the pen on the left, fenced on its right
        var t = mirror.Add(300, Floor);
        Run(mirror, 2, dog: (360, Floor - 10));
        Assert.NotEqual(State.Penned, t.State);
        Assert.InRange(t.X, 180 + FenceGap - 0.01, 300);
    }

    [Fact]
    public void ASheepDroppingIntoThePenFromAboveIsPenned()
    {
        var w = World();
        var top = new[] { new Ledge(650, 700, 850, (IntPtr)3) };
        var s = w.Add(820, 650, (IntPtr)3);
        var events = Run(w, 2, dog: (760, 640), windows: top);
        Assert.Contains(events, e => e.Sheep == s && e.Event == Event.Hopped);
        Assert.Equal(State.Penned, s.State);
        Assert.True(w.InPen(s.X));
    }

    // ------------------------------------------------------------------ the clock and the score

    [Fact]
    public void TheClockRunsOnlyWhileTheRoundIsOn()
    {
        var w = World();
        w.Add(300, Floor);
        w.Running = false;
        Run(w, 5, dog: (260, Floor - 10));
        Assert.Equal(90, w.TimeLeft, 6);
        Assert.Equal(300, w.Flock[0].X, 6); // and the dog is ignored

        w.Running = true;
        Run(w, 10);
        Assert.Equal(80, w.TimeLeft, 1);
        Assert.False(w.Over);
        Run(w, 81);
        Assert.Equal(0, w.TimeLeft);
        Assert.True(w.Over);
        Assert.False(w.AllIn);
        Assert.Equal(0, w.Score);
    }

    [Fact]
    public void TheWholeFlockInStopsTheClockAndScoresTheTimeLeft()
    {
        var w = World();
        var a = w.Add(790, Floor);
        var b = w.Add(720, Floor);
        for (int i = 0; i < 120 && a.State != State.Penned; i++) w.Step(1 / 60.0, (680, Floor - 10), NoWindows);
        Assert.Equal(State.Penned, a.State);
        Assert.NotEqual(State.Penned, b.State);
        Assert.Equal(10, w.Score); // one in: ten points, no time bonus yet
        Run(w, 3, dog: (680, Floor - 10));
        Assert.True(w.AllIn);
        Assert.True(w.Over);
        double left = w.TimeLeft;
        Run(w, 5);
        Assert.Equal(left, w.TimeLeft); // frozen
        Assert.Equal(2 * PerSheep + (int)Math.Ceiling(left) * PerSecond, w.Score);
        Assert.Equal(State.Penned, b.State);
    }

    [Fact]
    public void RoundScoreCountsSheepAndSecondsLeft()
    {
        Assert.Equal(30, RoundScore(3, false, 20));
        Assert.Equal(50 + 13 * 5, RoundScore(5, true, 12.2));
        Assert.Equal(50, RoundScore(5, true, 0));
        Assert.Equal(0, RoundScore(0, false, 50));
    }

    [Fact]
    public void RoundsGetBiggerFlocksAndShorterClocks()
    {
        Assert.Equal(5, FlockSize(1));
        Assert.Equal(90, RoundSeconds(1));
        for (int round = 1; round < 12; round++)
        {
            Assert.True(FlockSize(round + 1) >= FlockSize(round));
            Assert.True(RoundSeconds(round + 1) <= RoundSeconds(round));
            Assert.True(PenWidth(FlockSize(round)) >= FlockSize(round) * 18);
        }
        Assert.Equal(MaxFlock, FlockSize(20));
        Assert.Equal(45, RoundSeconds(20));
    }

    [Fact]
    public void AStrayBoltsAwayFromThePen()
    {
        var w = World();
        w.Strays = true;
        var s = w.Add(500, Floor);
        var events = Run(w, 16);
        var bolt = events.FirstOrDefault(e => e.Event == Event.Bolted);
        Assert.NotNull(bolt.Sheep);
        Assert.Equal(-1, bolt.Sheep.PanicDir); // the pen is to the right
    }

    [Fact]
    public void TheRaceBaselineIsARoundOneScoreWithTimeToSpare()
    {
        Assert.InRange(SheepGame.FairRound, RoundScore(5, false, 0) + 1, RoundScore(5, true, RoundSeconds(1)) - 1);
        var rival = new CpuRival(3, SheepGame.FairRound, false, SheepGame.TypicalRoundSeconds, new Random(7));
        double elapsed = 0;
        while (!rival.Done && elapsed < SheepGame.TypicalRoundSeconds * 1.3)
        {
            rival.Tick(0.25);
            elapsed += 0.25;
        }
        Assert.True(rival.Done);
    }

    [Theory]
    [InlineData("baa")]
    [InlineData("baa2")]
    [InlineData("bark1")]
    public void SheepClipsAreSynthesizedAudibleAndUnclipped(string name)
    {
        using var sound = new Sound(new DeskArcade.Platform.NullPlatform());
        var clip = sound.Samples(name);
        Assert.True(clip != null, $"no clip named {name}");
        Assert.True(clip!.Length > 1000, $"{name} is too short");
        Assert.All(clip, x => Assert.True(float.IsFinite(x)));
        Assert.InRange(clip.Max(Math.Abs), 0.2f, 0.8f);
    }
}
