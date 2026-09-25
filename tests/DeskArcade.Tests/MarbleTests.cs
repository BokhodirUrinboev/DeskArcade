using System;
using System.Collections.Generic;
using Avalonia;
using DeskArcade.Engine;
using DeskArcade.Games;
using Xunit;
using static DeskArcade.Games.MarbleRules;

namespace DeskArcade.Tests;

public class MarbleTests
{
    static readonly Rect Arena = new(0, 0, 1920, 1040);
    static readonly List<Engine.Platform> NoWindows = new();

    /// <summary>A course with the drop point and the cup where the test wants them.</summary>
    static MarbleRules Course(double dropX, double cupX, double dropY = 70)
    {
        var g = new MarbleRules(Arena);
        g.NewRound(new Random(1));
        g.Drop = new Vec2(dropX, dropY);
        g.CupX = cupX;
        g.Rest();
        return g;
    }

    /// <summary>Steps the marble until the run ends (or <paramref name="seconds"/> pass); returns how it ended.</summary>
    static MarbleOutcome Run(MarbleRules g, IReadOnlyList<Engine.Platform> tops, double seconds = 30, Action<MarbleEvents>? each = null)
    {
        for (double t = 0; t < seconds; t += Step)
        {
            var ev = g.Advance(tops);
            each?.Invoke(ev);
            if (ev.Outcome != MarbleOutcome.None) return ev.Outcome;
        }
        return MarbleOutcome.None;
    }

    [Fact]
    public void AMarbleDroppedOntoTheBareTaskbarComesToRestBelowTheFunnelAndMisses()
    {
        var g = Course(400, 1400);
        Assert.True(g.Release());
        Assert.False(g.Release()); // one marble at a time
        Assert.Equal(MarbleOutcome.Miss, Run(g, NoWindows));
        Assert.False(g.Running);
        Assert.Equal(400, g.Pos.X, 0);
        Assert.Equal(Arena.Bottom - R, g.Pos.Y, 3);
        Assert.Equal(MarbleOutcome.None, g.Advance(NoWindows).Outcome); // at rest, a step does nothing
    }

    [Fact]
    public void AMarbleDroppedOverTheCupLandsInIt()
    {
        var g = Course(900, 900);
        g.Release();
        Assert.Equal(MarbleOutcome.Cup, Run(g, NoWindows));
        Assert.Equal(900, g.Pos.X, 3);
        Assert.False(g.Running);
    }

    [Fact]
    public void AMarbleLandsOnAWindowTopInsteadOfFallingThrough()
    {
        var g = Course(500, 1500);
        var tops = new List<Engine.Platform> { new((IntPtr)1, 600, 300, 900) };
        g.Release();
        Assert.Equal(MarbleOutcome.Miss, Run(g, tops)); // straight down onto a level top: nothing to roll it anywhere
        Assert.Equal(600 - R, g.Pos.Y, 3);
        Assert.Equal(600, g.SurfaceBelow(g.Drop, tops));
        Assert.Equal(Arena.Bottom, g.SurfaceBelow(new Vec2(1000, 70), tops));
    }

    [Fact]
    public void AMarbleRollsAlongAWindowTopWithTheSpeedItBrought()
    {
        var g = Course(500, 1500);
        var tops = new List<Engine.Platform> { new((IntPtr)1, 600, 100, 1800) };
        g.Release();
        g.Pos = new Vec2(300, 600 - R);
        g.Vel = new Vec2(500, 0);
        double lastX = g.Pos.X;
        for (int i = 0; i < 120; i++) // half a second
        {
            g.Advance(tops);
            Assert.True(g.Pos.X > lastX, "the marble stopped rolling");
            lastX = g.Pos.X;
            Assert.True(g.Grounded);
            Assert.Equal(600 - R, g.Pos.Y, 3);
        }
        Assert.InRange(g.Vel.X, 100, 499); // friction slows it, but it keeps rolling
        Assert.NotEqual(0, g.Angle); // and it turns as it rolls
        // it rolls on until friction stops it, about v / RollFriction further on
        Assert.Equal(MarbleOutcome.Miss, Run(g, tops));
        Assert.InRange(g.Pos.X, lastX + 150, lastX + 500);
    }

    [Fact]
    public void AMarbleRollsOffTheEndOfAWindowTopAndFalls()
    {
        var g = Course(500, 100);
        var tops = new List<Engine.Platform> { new((IntPtr)1, 600, 100, 700) };
        g.Release();
        g.Pos = new Vec2(600, 600 - R);
        g.Vel = new Vec2(500, 0);
        bool fell = false;
        Run(g, tops, 3, _ => fell |= g.Pos.Y > 600 + R);
        Assert.True(fell, "the marble never left the window top");
        Assert.True(g.Pos.X > 700);
        Assert.Equal(Arena.Bottom - R, g.Pos.Y, 3); // it ended on the taskbar
    }

    [Fact]
    public void AMarbleComingUpFromBelowPassesThroughAWindowTop()
    {
        var g = Course(500, 1500);
        var tops = new List<Engine.Platform> { new((IntPtr)1, 600, 100, 900) };
        g.Release();
        g.Pos = new Vec2(500, 700);
        g.Vel = new Vec2(0, -900);
        for (int i = 0; i < 60; i++) g.Advance(tops);
        Assert.True(g.Pos.Y < 600 - R, "the window top stopped a marble from below");
    }

    [Fact]
    public void ABumperThrowsTheMarbleOff()
    {
        var g = Course(500, 1500);
        Assert.NotNull(g.Place(MarblePieceKind.Bumper, new Vec2(510, 400)));
        g.Release();
        int hits = 0;
        double fastest = 0;
        Run(g, NoWindows, 1.0, ev =>
        {
            if (ev.Piece == 0) hits++;
            fastest = Math.Max(fastest, ev.PieceSpeed);
        });
        Assert.True(hits > 0, "the marble missed the bumper");
        Assert.True(g.Pos.X < 500, "a marble hitting the bumper's left side goes off to the left");
    }

    [Fact]
    public void ABumperKicksAtLeastItsKickSpeed()
    {
        var g = Course(500, 1500);
        g.Place(MarblePieceKind.Bumper, new Vec2(500, 400));
        g.Release();
        g.Pos = new Vec2(500, 400 - BumperR - R + 1);
        g.Vel = new Vec2(0, 30);
        g.Advance(NoWindows);
        Assert.True(-g.Vel.Y >= BumperKick * 0.95, $"kicked up at only {-g.Vel.Y}");
    }

    [Fact]
    public void ARampTurnsAFallingMarbleDownhillAlongIt()
    {
        var g = Course(500, 1500);
        g.Place(MarblePieceKind.Ramp, new Vec2(520, 300), 25); // down to the right
        g.Release();
        bool touched = false;
        Run(g, NoWindows, 1.0, ev => touched |= ev.Piece == 0);
        Assert.True(touched, "the marble missed the ramp");
        Assert.True(g.Vel.X > 150, $"the ramp did not send it right (vx {g.Vel.X})");
        Assert.True(g.Pos.X > 575, $"still at {g.Pos.X}"); // off the ramp's lower end
    }

    [Fact]
    public void ALevelRampHoldsTheMarbleAndItMisses()
    {
        var g = Course(500, 1500);
        g.Place(MarblePieceKind.Ramp, new Vec2(500, 300), 0);
        g.Release();
        Assert.Equal(MarbleOutcome.Miss, Run(g, NoWindows));
        Assert.Equal(300 - RampThick - R, g.Pos.Y, 0);
    }

    [Fact]
    public void TheSuggestedRampSteersTheMarbleTowardTheCup()
    {
        foreach (var (drop, cup) in new[] { (400.0, 1500.0), (1500.0, 300.0) })
        {
            var g = Course(drop, cup);
            var (pos, angle) = g.SuggestRamp();
            g.Place(MarblePieceKind.Ramp, pos, angle);
            g.Release();
            Run(g, NoWindows);
            Assert.True(Math.Abs(g.Pos.X - cup) < Math.Abs(drop - cup) - 150, $"from {drop} toward {cup} it stopped at {g.Pos.X}");
        }
    }

    [Fact]
    public void AMarbleRollingSlowlyOverTheCupDropsInButAFastOneSkimsPast()
    {
        var slow = Course(500, 1000);
        slow.Release();
        slow.Pos = new Vec2(800, Arena.Bottom - R);
        slow.Vel = new Vec2(400, 0);
        Assert.Equal(MarbleOutcome.Cup, Run(slow, NoWindows));

        var fast = Course(500, 1000);
        fast.Release();
        fast.Pos = new Vec2(900, Arena.Bottom - R);
        fast.Vel = new Vec2(CatchSpeed + 300, 0);
        bool lipped = false;
        for (int i = 0; i < 60; i++) lipped |= fast.Advance(NoWindows).LippedOut;
        Assert.True(lipped);
        Assert.True(fast.Running, "a marble too fast for the cup dropped in anyway");
        Assert.True(fast.Pos.X > 1000 + CupHalf);
    }

    [Fact]
    public void TheWallsTurnTheMarbleBack()
    {
        var g = Course(500, 1000);
        g.Release();
        g.Pos = new Vec2(Arena.Right - 40, Arena.Bottom - R);
        g.Vel = new Vec2(800, 0);
        double wall = 0;
        for (int i = 0; i < 60; i++) wall = Math.Max(wall, g.Advance(NoWindows).Wall);
        Assert.True(wall > 0);
        Assert.True(g.Vel.X < 0);
        Assert.True(g.Pos.X <= Arena.Right - R);
    }

    [Fact]
    public void PiecesComeOutOfABudgetOfThreeAndOnlyBetweenRuns()
    {
        var g = Course(500, 1500);
        Assert.Equal(Budget, g.PiecesLeft);
        var first = g.Place(MarblePieceKind.Ramp, new Vec2(600, 400));
        Assert.NotNull(g.Place(MarblePieceKind.Bumper, new Vec2(700, 400)));
        Assert.NotNull(g.Place(MarblePieceKind.Ramp, new Vec2(800, 400)));
        Assert.Null(g.Place(MarblePieceKind.Bumper, new Vec2(900, 400)));
        Assert.Equal(0, g.PiecesLeft);
        Assert.True(g.Remove(first!));
        Assert.Equal(1, g.PiecesLeft);

        g.Release();
        Assert.False(g.CanPlace);
        Assert.Null(g.Place(MarblePieceKind.Bumper, new Vec2(900, 400)));
        Assert.False(g.Remove(g.Pieces[0]));
    }

    [Fact]
    public void PiecesStayOnTheScreenAndOffTheTaskbar()
    {
        var g = Course(500, 1500);
        var p = g.Place(MarblePieceKind.Bumper, new Vec2(-50, 5000))!;
        Assert.InRange(p.Pos.X, Arena.Left, Arena.Right);
        Assert.True(p.Pos.Y < Arena.Bottom - 20);
    }

    [Fact]
    public void ACourseScoresMoreForFewerPiecesAndFewerMisses()
    {
        Assert.Equal(175, CourseScore(0, 0));
        Assert.Equal(100, CourseScore(3, 0));
        Assert.Equal(125, CourseScore(1, 1));
        Assert.Equal(50, CourseScore(3, Tries - 1)); // the least a sunk marble can score
        for (int used = 0; used < Budget; used++) Assert.True(CourseScore(used, 0) > CourseScore(used + 1, 0));
        for (int misses = 0; misses < Tries - 1; misses++) Assert.True(CourseScore(0, misses) > CourseScore(0, misses + 1));
        Assert.Equal(MaxRound, Courses * CourseScore(0, 0));
    }

    [Fact]
    public void SinkingScoresTheCourseAndGrowsTheStreakOnlyAtTheFirstRelease()
    {
        var rng = new Random(3);
        var g = new MarbleRules(Arena);
        g.NewRound(rng);
        g.Place(MarblePieceKind.Ramp, new Vec2(600, 400));
        Assert.Equal(150, g.Sink());
        Assert.Equal(1, g.Streak);
        g.NextCourse(rng);
        Assert.Equal(2, g.Course);
        Assert.Empty(g.Pieces); // every course starts with a full tray

        Assert.False(g.Miss());
        Assert.Equal(0, g.Streak);
        Assert.Equal(150, g.Sink()); // 175 less one miss
        Assert.Equal(0, g.Streak);
        Assert.Equal(300, g.Score);
        Assert.Equal(2, g.Sunk);
    }

    [Fact]
    public void ThreeMissesLoseTheCourseAndFiveCoursesMakeARound()
    {
        var rng = new Random(4);
        var g = new MarbleRules(Arena);
        g.NewRound(rng);
        for (int course = 1; course <= Courses; course++)
        {
            Assert.Equal(course, g.Course);
            Assert.False(g.RoundOver);
            for (int miss = 1; miss < Tries; miss++) Assert.False(g.Miss());
            Assert.True(g.Miss());
            g.NextCourse(rng);
            Assert.Equal(0, g.Misses);
        }
        Assert.True(g.RoundOver);
        Assert.False(g.Release());
        Assert.Equal(0, g.Score);

        g.NewRound(rng);
        Assert.Equal(1, g.Course);
        Assert.False(g.RoundOver);
    }

    [Fact]
    public void CoursesPutTheCupWellAwayFromTheDropAndTheFunnelClearOfTheScoreboard()
    {
        var hud = new Rect(760, 0, 400, 120);
        var rng = new Random(5);
        var g = new MarbleRules(Arena);
        for (int i = 0; i < 200; i++)
        {
            g.Plan(rng, hud);
            Assert.InRange(g.Drop.X, Arena.Left + 40, Arena.Right - 40);
            Assert.InRange(g.CupX, Arena.Left + CupHalf, Arena.Right - CupHalf);
            Assert.True(Math.Abs(g.CupX - g.Drop.X) >= 300, $"cup at {g.CupX}, drop at {g.Drop.X}");
            Assert.False(hud.Inflate(30).Contains(g.Drop.ToPoint()), $"the funnel at {g.Drop} is under the scoreboard");
            Assert.Equal(g.Drop, g.Pos); // the marble waits in the funnel
        }
    }

    [Fact]
    public void TheRaceIsTunedToTheScoring()
    {
        Assert.InRange(MarbleGame.FairRound, Courses * CourseScore(Budget, 1), MaxRound - 1);
        Assert.InRange(MarbleGame.TypicalRoundSeconds, 10, 600);
    }
}
