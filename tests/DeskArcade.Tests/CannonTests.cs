using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using DeskArcade.Engine;
using DeskArcade.Games;
using Xunit;

namespace DeskArcade.Tests;

public class CannonTests
{
    const double S = 24, Floor = 1040;
    static readonly Rect Arena = new(0, 0, 1920, Floor);

    /// <summary>A game on an empty desk: both castles on the taskbar, 1500-odd pixels apart.</summary>
    static CannonRules Game(int starter = 0)
    {
        var g = new CannonRules(starter);
        g.Castles[0].Place(115, Floor, S);
        g.Castles[1].Place(Arena.Right - 115 - Castle.Cols * S, Floor, S);
        return g;
    }

    static CannonField Empty => new(Arena, Array.Empty<Rect>());

    static double MaxSpeed(Castle c) => CannonRules.MaxSpeedFor(c.FullPivot.Y, Arena.Top);

    static Impact Shoot(CannonRules g, int side, double angle, double power, double wind, CannonField? field = null) =>
        Flight.Fly(CannonRules.Launch(g.Castles[side], angle, power, MaxSpeed(g.Castles[side])), wind, field ?? Empty, g.Castles);

    // ------------------------------------------------------------------ ballistics

    [Fact]
    public void WithoutWindTheBallFollowsTheParabola()
    {
        var f = new Flight(new Vec2(100, 900), new Vec2(400, -600), 7);
        var start = f.Pos;
        var v = f.Vel;
        var huge = new CannonField(new Rect(-1e5, -1e5, 2e5, 2e5), Array.Empty<Rect>());
        for (int i = 0; i < 240; i++) f.Advance(Flight.Step, 0, huge, Array.Empty<Castle>());
        var exact = start + v * 1.0 + new Vec2(0, CannonRules.Gravity) * 0.5;
        Assert.Equal(exact.X, f.Pos.X, 6);
        Assert.InRange(f.Pos.Y - exact.Y, 0, 4); // the fixed step lands a hair below the exact curve
    }

    [Fact]
    public void WindPushesTheBallSidewaysByHalfWindTimesTimeSquared()
    {
        var huge = new CannonField(new Rect(-1e5, -1e5, 2e5, 2e5), Array.Empty<Rect>());
        var calm = new Flight(new Vec2(0, 0), new Vec2(300, -500), 7);
        var windy = new Flight(new Vec2(0, 0), new Vec2(300, -500), 7);
        for (int i = 0; i < 480; i++)
        {
            calm.Advance(Flight.Step, 0, huge, Array.Empty<Castle>());
            windy.Advance(Flight.Step, 120, huge, Array.Empty<Castle>());
        }
        Assert.Equal(0.5 * 120 * 2 * 2, windy.Pos.X - calm.Pos.X, 0); // two seconds of wind: 240 px, give or take the step
        Assert.Equal(calm.Pos.Y, windy.Pos.Y, 6); // the wind only blows sideways
    }

    [Fact]
    public void AHeadwindShortensAShotAndATailwindStretchesIt()
    {
        var g = Game();
        double Land(double wind) => Flight.XAtHeight(CannonRules.Launch(g.Castles[0], 45, 0.6, MaxSpeed(g.Castles[0])), wind, Floor - 10, Arena);
        Assert.True(Land(-100) < Land(0));
        Assert.True(Land(100) > Land(0));
    }

    [Fact]
    public void EachCannonFiresTowardTheOtherCastleAndAnglesStayInRange()
    {
        Assert.True(CannonRules.Direction(1, 30).X > 0);
        Assert.True(CannonRules.Direction(-1, 30).X < 0);
        Assert.True(CannonRules.Direction(1, 30).Y < 0); // up
        Assert.Equal(CannonRules.Direction(1, CannonRules.MaxAngle), CannonRules.Direction(1, 170));
        Assert.Equal(CannonRules.Direction(-1, 0), CannonRules.Direction(-1, -20));
        Assert.True(CannonRules.SpeedFor(1, 1000) > CannonRules.SpeedFor(0, 1000));
        Assert.Equal(1000, CannonRules.SpeedFor(5, 1000), 6);
    }

    [Fact]
    public void TheStrongestShotStraightUpTurnsBelowTheTopOfTheScreen()
    {
        var g = Game();
        var c = g.Castles[0];
        var path = new List<Vec2>();
        Flight.Fly(CannonRules.Launch(c, CannonRules.MaxAngle, 1, MaxSpeed(c)), 0, Empty, g.Castles, path);
        Assert.True(path.Min(p => p.Y) > Arena.Top, "the ball left through the ceiling");
    }

    [Fact]
    public void AWindowBetweenTheCastlesStopsAFlatShot()
    {
        var g = Game();
        var window = new Rect(800, Floor - 300, 300, 300);
        var impact = Shoot(g, 0, 10, 0.9, 0, new CannonField(Arena, new[] { window }));
        Assert.Equal(ImpactKind.Window, impact.Kind);
        Assert.InRange(impact.At.X, window.Left - 10, window.Right + 10);
    }

    [Fact]
    public void AWeakShotLandsOnTheFloorAndAWildOneLeavesAtTheSide()
    {
        var g = Game();
        var weak = Shoot(g, 0, 30, 0, 0);
        Assert.Equal(ImpactKind.Ground, weak.Kind);
        Assert.Equal(-1, weak.Castle);
        // a strong tailwind carries a high ball past the rival and off the right edge
        var wild = Shoot(g, 0, 60, 1, CannonRules.MaxWind * 3);
        Assert.Equal(ImpactKind.Out, wild.Kind);
    }

    // ------------------------------------------------------------------ castles

    [Fact]
    public void AWholeCastleHasItsKeepFlagAndCannonWhereTheyBelong()
    {
        var g = Game();
        var left = g.Castles[0];
        var right = g.Castles[1];
        Assert.Equal(Castle.WholeCount, left.Standing);
        Assert.False(left.FlagDown);
        // the keep is second from the back, the cannon on the front wall: mirrored on the right-hand castle
        Assert.True(left.BlockRect(Castle.IndexOf(Castle.KeepCol, 0)).X < left.CannonPivot.X);
        Assert.True(right.BlockRect(Castle.IndexOf(Castle.KeepCol, 0)).X > right.CannonPivot.X);
        Assert.Equal(Floor - S, left.BlockRect(Castle.IndexOf(0, 0)).Y);
        var keepTop = left.BlockRect(Castle.IndexOf(Castle.KeepCol, Castle.FullHeights[Castle.KeepCol] - 1));
        Assert.Equal(keepTop.Top, left.FlagFoot.Y, 6);
        Assert.Equal(keepTop.Center.X, left.FlagFoot.X, 6);
        Assert.False(left.Has(Castle.IndexOf(2, 3))); // above the front wall is open sky
    }

    [Fact]
    public void AHitCracksABlockAndASecondKnocksItOutWithEverythingAboveIt()
    {
        var c = Game().Castles[1];
        int tower = Castle.IndexOf(0, 1); // the back tower's second block
        Assert.Equal(0, c.Hit(tower));
        Assert.True(c.Cracked(tower));
        Assert.Equal(Castle.WholeCount, c.Standing);
        Assert.Equal(4, c.Hit(tower)); // it and the three above it
        Assert.Equal(1, c.Height(0));
        Assert.False(c.Has(Castle.IndexOf(0, 4)));
        Assert.False(c.Cracked(tower));
        Assert.Equal(0, c.Hit(Castle.IndexOf(0, 3))); // already gone
        Assert.Equal(Castle.WholeCount - 4, c.Standing);
        Assert.False(c.FlagDown); // the keep still stands
        c.Knock(Castle.IndexOf(Castle.KeepCol, 5));
        Assert.True(c.FlagDown); // the top of the keep carried the flag
    }

    [Theory]
    [InlineData(1, 3)] // cracked low, hit high: it gives way from the crack
    [InlineData(3, 1)] // cracked high, hit low: from the new hit
    public void ASecondHitAnywhereInACrackedColumnBringsItDownFromTheLowerHit(int first, int second)
    {
        var c = Game().Castles[0];
        Assert.Equal(0, c.Hit(Castle.IndexOf(0, first)));
        Assert.Equal(0, c.Hit(Castle.IndexOf(3, 0))); // a crack in another column is its own business
        Assert.Equal(Castle.IndexOf(0, first), c.CrackIn(0));
        Assert.Equal(4, c.Hit(Castle.IndexOf(0, second)));
        Assert.Equal(1, c.Height(0));
        Assert.Equal(-1, c.CrackIn(0));
        Assert.Equal(Castle.IndexOf(3, 0), c.CrackIn(3));
    }

    [Fact]
    public void AShotAtTheFrontWallCracksTheBlockItHits()
    {
        var g = Game();
        var rival = g.Castles[1];
        // aim with the computer's own solver at the rival's front wall, just under the cannon
        var target = rival.BlockRect(Castle.IndexOf(Castle.CannonCol, 1));
        double power = CannonCpu.SolvePower(g.Castles[0], 35, target.Center.X, target.Center.Y, 0, MaxSpeed(g.Castles[0]), Arena);
        Assert.InRange(power, 0.01, 0.99); // within reach
        var impact = Shoot(g, 0, 35, power, 0);
        Assert.Equal(ImpactKind.Block, impact.Kind);
        Assert.Equal(1, impact.Castle);
        Assert.Equal(0, g.Land(0, impact.Castle, impact.Block, 0));
        Assert.True(rival.Cracked(impact.Block));
        Assert.Equal(Castle.WholeCount, rival.Standing);
        // the rival misses, and the same shot again knocks the cracked block out
        g.Land(1, -1, -1, 0);
        var again = Shoot(g, 0, 35, power, 0);
        Assert.Equal(impact.Block, again.Block);
        int fell = g.Land(0, again.Castle, again.Block, 0);
        Assert.True(fell >= 1);
        Assert.Equal(fell, g.Knocked[0]);
        Assert.Equal(Castle.WholeCount - fell, rival.Standing);
    }

    [Fact]
    public void KnockingABlockOutOfTheKeepDownsTheFlagAndWinsTheGame()
    {
        var g = Game();
        int keep = Castle.IndexOf(Castle.KeepCol, 3);
        Assert.Equal(0, g.Land(0, 1, keep, 0)); // cracked
        Assert.False(g.Over);
        g.Land(1, -1, -1, 0);
        Assert.Equal(3, g.Land(0, 1, keep, 0));
        Assert.True(g.Castles[1].FlagDown);
        Assert.True(g.Over);
        Assert.Equal(0, g.Winner);
        Assert.Equal(2, g.ShotsBy[0]);
        Assert.Equal(0, g.Land(1, 0, Castle.IndexOf(Castle.KeepCol, 0), 0)); // nobody shoots once it is over
        Assert.False(g.Castles[0].Cracked(Castle.IndexOf(Castle.KeepCol, 0)));
    }

    [Fact]
    public void ABallFliesThroughTheFlagAndStrikesTheKeepUnderIt()
    {
        var g = Game();
        var castle = g.Castles[1];
        // dropped straight down the flagpole from above the flag
        var foot = castle.FlagFoot;
        var drop = new Flight(new Vec2(foot.X, foot.Y - (Castle.FlagHeight + 1) * S), new Vec2(0, 50), CannonRules.BallRadius(S));
        var impact = Flight.Fly(drop, 0, Empty, g.Castles);
        Assert.Equal(ImpactKind.Block, impact.Kind);
        Assert.Equal(Castle.IndexOf(Castle.KeepCol, Castle.FullHeights[Castle.KeepCol] - 1), impact.Block);
    }

    [Fact]
    public void KnockingDownYourOwnFlagLosesTheGame()
    {
        var g = Game();
        int keep = Castle.IndexOf(Castle.KeepCol, 0);
        g.Land(0, 0, keep, 0);
        g.Land(1, -1, -1, 0);
        g.Land(0, 0, keep, 0);
        Assert.Equal(1, g.Winner);
        Assert.Equal(0, g.Knocked[0]); // your own blocks don't count as knocked
    }

    // ------------------------------------------------------------------ turns

    [Fact]
    public void TurnsAlternateAndTheWindChangesWithEveryShot()
    {
        var g = Game(starter: 1);
        Assert.Equal(1, g.Turn);
        Assert.Equal(0, g.Wind); // every game opens calm
        Assert.Equal(0, g.Land(0, -1, 0, 50)); // not your turn: ignored
        Assert.Equal(0, g.Shots);
        g.Land(1, -1, 0, 50);
        Assert.Equal(0, g.Turn);
        Assert.Equal(50, g.Wind);
        g.Land(0, 1, Castle.IndexOf(3, 2), -999);
        Assert.Equal(1, g.Turn);
        Assert.Equal(-CannonRules.MaxWind, g.Wind); // clamped
        Assert.Equal(2, g.Shots);
        Assert.Equal(new[] { 1, 1 }, g.ShotsBy);
        Assert.False(g.Over);
    }

    [Fact]
    public void RandomWindsStayInRangeAndAreWholeNumbers()
    {
        var rng = new Random(3);
        for (int i = 0; i < 500; i++)
        {
            double w = CannonRules.RandomWind(rng);
            Assert.InRange(w, -CannonRules.MaxWind, CannonRules.MaxWind);
            Assert.Equal(Math.Round(w), w);
        }
        Assert.Equal("calm", CannonRules.WindText(3));
        Assert.Equal("→ 2.0", CannonRules.WindText(60));
        Assert.Equal("← 5.0", CannonRules.WindText(-150));
    }

    // ------------------------------------------------------------------ the computer

    [Fact]
    public void ThePowerSolverBringsTheBallDownOnItsMark()
    {
        var g = Game();
        var mine = g.Castles[0];
        var target = CannonCpu.TargetOf(g.Castles[1]);
        foreach (double wind in new[] { -120.0, 0, 90 })
        {
            double p = CannonCpu.SolvePower(mine, 45, target.X, target.Y, wind, MaxSpeed(mine), Arena);
            double x = Flight.XAtHeight(CannonRules.Launch(mine, 45, p, MaxSpeed(mine)), wind, target.Y, Arena);
            Assert.InRange(x - target.X, -2, 2);
        }
    }

    /// <summary>
    /// Two shots in a row from a fresh gunner in a calm: how far past the keep each one came down through the keep's
    /// height (measured in the open, since a block it hits would cut the error short).
    /// </summary>
    static (double First, double Second) TwoShots(int level, int seed)
    {
        var g = Game();
        var cpu = new CannonCpu(level, new Random(seed));
        var mine = g.Castles[1];
        var target = CannonCpu.TargetOf(g.Castles[0]);
        double Shot()
        {
            var (angle, power) = cpu.Aim(mine, g.Castles[0], 0, MaxSpeed(mine), Empty, g.Castles);
            double x = Flight.XAtHeight(CannonRules.Launch(mine, angle, power, MaxSpeed(mine)), 0, target.Y, Arena);
            double off = CannonCpu.LongBy(mine, target, new Impact(ImpactKind.Ground, new Vec2(x, target.Y)));
            cpu.Learn(off, blocked: false);
            return off;
        }
        double first = Shot();
        return (first, Shot());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void TheComputerCorrectsItsAimAfterAMiss(int level)
    {
        var shots = Enumerable.Range(1, 60).Select(seed => TwoShots(level, seed)).ToList();
        double first = shots.Average(s => Math.Abs(s.First)), second = shots.Average(s => Math.Abs(s.Second));
        Assert.True(second < first * 0.75, $"level {level}: first {first:0} px off, second {second:0} px off");
    }

    [Fact]
    public void HigherLevelsAimCloserFromTheFirstShot()
    {
        double Off(int level) => Enumerable.Range(1, 60).Average(seed => Math.Abs(TwoShots(level, seed).First));
        Assert.True(Off(1) > Off(2));
        Assert.True(Off(2) > Off(3));
        Assert.True(Off(3) > Off(4));
    }

    [Fact]
    public void AComputerStoppedByAWindowLobsHigherNextTime()
    {
        var g = Game();
        var mine = g.Castles[1];
        var target = CannonCpu.TargetOf(g.Castles[0]);
        var cpu = new CannonCpu(1, new Random(5)); // Easy doesn't look for windows itself
        var tower = new CannonField(Arena, new[] { new Rect(900, 200, 120, Floor - 200) });
        var (angle, power) = cpu.Aim(mine, g.Castles[0], 0, MaxSpeed(mine), tower, g.Castles);
        var impact = Flight.Fly(CannonRules.Launch(mine, angle, power, MaxSpeed(mine)), 0, tower, g.Castles);
        Assert.Equal(ImpactKind.Window, impact.Kind);
        Assert.True(CannonCpu.Blocked(mine, g.Castles[0], target, impact));
        cpu.Learn(CannonCpu.LongBy(mine, target, impact), blocked: true);
        var again = cpu.Aim(mine, g.Castles[0], 0, MaxSpeed(mine), tower, g.Castles);
        Assert.True(again.Angle > angle + 4);
    }

    [Fact]
    public void TheHigherLevelsLookForAWayOverAWindow()
    {
        var g = Game();
        var mine = g.Castles[1];
        var wall = new CannonField(Arena, new[] { new Rect(900, 520, 120, Floor - 520) });
        int cleared = 0;
        for (int seed = 1; seed <= 20; seed++)
        {
            var cpu = new CannonCpu(4, new Random(seed));
            var (angle, power) = cpu.Aim(mine, g.Castles[0], 0, MaxSpeed(mine), wall, g.Castles);
            var impact = Flight.Fly(CannonRules.Launch(mine, angle, power, MaxSpeed(mine)), 0, wall, g.Castles);
            if (impact.Kind != ImpactKind.Window) cleared++;
        }
        Assert.True(cleared >= 18, $"only {cleared} of 20 shots got over the window");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void TwoComputersFinishAGameInTheWind(int level)
    {
        var rng = new Random(11 * level);
        var g = Game();
        var cpus = new[] { new CannonCpu(level, rng), new CannonCpu(level, rng) };
        for (int shot = 0; shot < 80 && !g.Over; shot++)
        {
            int side = g.Turn;
            var mine = g.Castles[side];
            var theirs = g.Castles[1 - side];
            var target = CannonCpu.TargetOf(theirs);
            var (angle, power) = cpus[side].Aim(mine, theirs, g.Wind, MaxSpeed(mine), Empty, g.Castles);
            var impact = Flight.Fly(CannonRules.Launch(mine, angle, power, MaxSpeed(mine)), g.Wind, Empty, g.Castles);
            cpus[side].Learn(CannonCpu.LongBy(mine, target, impact), CannonCpu.Blocked(mine, theirs, target, impact));
            g.Land(side, impact.Castle, impact.Block, CannonRules.RandomWind(rng));
        }
        Assert.True(g.Over, $"level {level}: no flag down after {g.Shots} shots");
    }

    // ------------------------------------------------------------------ the LAN

    [Theory]
    [InlineData(1, -1, 0)]   // a miss
    [InlineData(1, 1, 7)]    // their shot hit their own castle
    [InlineData(0, 1, 13)]   // our shot hit their castle
    [InlineData(0, 0, 29)]   // our shot came down on our own castle
    public void AShotReadsTheSameFromTheOtherScreen(int shooter, int castle, int block)
    {
        // the shooter's screen sends it; on the other screen the shooter is side 1, so the castles swap
        string body = CannonRules.ShotMessage(4, shooter, castle, block, -73);
        Assert.True(CannonRules.TryReadShot(body, out int shot, out int theirCastle, out int theirBlock, out double wind));
        Assert.Equal(4, shot);
        Assert.Equal(block, theirBlock);
        Assert.Equal(-73, wind);
        int expected = castle < 0 ? -1 : castle == shooter ? 1 : 0;
        Assert.Equal(expected, theirCastle);
    }

    [Fact]
    public void BothScreensLoseTheSameBlocks()
    {
        var mine = Game();
        var theirs = Game(starter: 1); // the co-worker's screen: they are side 0 there, we are side 1
        // our shot cracks their wall, theirs misses, ours knocks the cracked block out: each sent and read on the other side
        var shots = new[] { (ByUs: true, Castle: 1, Block: Castle.IndexOf(2, 1)), (false, -1, -1), (true, 1, Castle.IndexOf(2, 1)) };
        foreach (var (byUs, castle, block) in shots)
        {
            // every screen has its own player on side 0, so the shooter's screen numbers the castles its own way
            var (from, to) = byUs ? (mine, theirs) : (theirs, mine);
            int local = castle < 0 ? -1 : byUs ? castle : 1 - castle;
            double next = 40 + from.Shots;
            string body = CannonRules.ShotMessage(from.Shots, 0, local, block, next);
            from.Land(0, local, block, next);
            Assert.True(CannonRules.TryReadShot(body, out int shot, out int c, out int b, out double wind));
            Assert.Equal(to.Shots, shot);
            to.Land(1, c, b, wind);
        }
        Assert.Equal(mine.Castles[1].Standing, theirs.Castles[0].Standing);
        Assert.Equal(mine.Castles[0].Standing, theirs.Castles[1].Standing);
        Assert.Equal(Castle.WholeCount - 2, mine.Castles[1].Standing);
        Assert.Equal(mine.Wind, theirs.Wind);
        Assert.Equal(mine.Shots, theirs.Shots);
        Assert.Equal(1, mine.Turn);
        Assert.Equal(0, theirs.Turn); // their turn on both screens
    }

    [Theory]
    [InlineData("sh|1|2|3|0")]      // no such castle
    [InlineData("sh|1|1|99|0")]     // no such block
    [InlineData("sh|1|0|-1|0")]     // a hit on a castle, but no block
    [InlineData("sh|x|1|3|0")]
    [InlineData("sh|1|1|3")]
    [InlineData("ng|3")]
    public void AGarbledShotIsRefused(string body) =>
        Assert.False(CannonRules.TryReadShot(body, out _, out _, out _, out _));

    [Fact]
    public void TheGhostBallFliesBetweenTheSameTwoCannonsOnTheOtherScreen()
    {
        var from = new Vec2(200, 900);
        var to = new Vec2(1700, 950);
        var ball = new Vec2(950, 500);
        var (u, v) = CannonRules.GhostOut(ball, from, to, 1000);
        Assert.Equal(0.5, u, 6);
        // on the other screen the shooter's cannon is on the right: halfway across, as high above it
        var theirFrom = new Vec2(1600, 800);
        var theirTo = new Vec2(300, 800);
        var at = CannonRules.GhostIn(u, v, theirFrom, theirTo, 1000);
        Assert.Equal(950, at.X, 6);
        Assert.Equal(400, at.Y, 6);
        var back = CannonRules.GhostIn(u, v, from, to, 1000);
        Assert.Equal(ball.X, back.X, 6);
        Assert.Equal(ball.Y, back.Y, 6);
    }
}
