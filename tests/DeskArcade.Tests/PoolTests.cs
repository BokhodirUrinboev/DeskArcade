using System;
using Avalonia;
using DeskArcade.Engine;
using DeskArcade.Games;
using Xunit;

namespace DeskArcade.Tests;

public class DiscTableTests
{
    static DiscTable Frictionless() => new() { Bounds = new Rect(0, 0, 2000, 1000), Friction = 0, Damping = 0, Restitution = 0.95 };

    /// <summary>A pool table like the game's: 1000 x 500 of felt, four corner and two middle pockets.</summary>
    static DiscTable PoolTable(double r = 12)
    {
        var t = new DiscTable { Bounds = new Rect(100, 100, 1000, 500), Friction = 110, Damping = 0.3, Restitution = 0.95, CushionRestitution = 0.75 };
        foreach (var (x, y) in new[] { (100 - r * 0.5, 100 - r * 0.5), (1100 + r * 0.5, 100 - r * 0.5), (100 - r * 0.5, 600 + r * 0.5), (1100 + r * 0.5, 600 + r * 0.5) })
            t.Pockets.Add(new DiscTable.Pocket(new Vec2(x, y), r * 2));
        t.Pockets.Add(new DiscTable.Pocket(new Vec2(600, 100 - r * 0.9), r * 1.75));
        t.Pockets.Add(new DiscTable.Pocket(new Vec2(600, 600 + r * 0.9), r * 1.75));
        return t;
    }

    static void Run(DiscTable t, double seconds)
    {
        for (double s = 0; s < seconds; s += 1.0 / 60) t.Advance(1.0 / 60);
    }

    [Fact]
    public void AHeadOnHitOfEqualBallsStopsTheCueBallAndSendsTheOtherOn()
    {
        var t = Frictionless();
        var cue = t.Add(new Vec2(500, 500), 12);
        var ball = t.Add(new Vec2(600, 500), 12);
        cue.Vel = new Vec2(1000, 0);
        double impact = 0;
        t.Collided += (_, _, speed) => impact = Math.Max(impact, speed);
        Run(t, 0.2);
        Assert.InRange(impact, 950, 1050);
        Assert.True(cue.Vel.Length < 50, $"the cue ball kept {cue.Vel.Length:0} px/s");
        Assert.InRange(ball.Vel.X, 940, 1000);
        Assert.InRange(Math.Abs(ball.Vel.Y), 0, 1e-6);
    }

    [Fact]
    public void MomentumIsConservedInAGlancingHit()
    {
        var t = Frictionless();
        var a = t.Add(new Vec2(400, 500), 11.5, mass: 4);
        var b = t.Add(new Vec2(520, 512), 6.5, mass: 1);
        a.Vel = new Vec2(900, 30);
        b.Vel = new Vec2(-120, 0);
        var before = a.Vel * a.Mass + b.Vel * b.Mass;
        int hits = 0;
        t.Collided += (_, _, _) => hits++;
        Run(t, 0.25);
        var after = a.Vel * a.Mass + b.Vel * b.Mass;
        Assert.True(hits > 0);
        Assert.True((after - before).Length < 1e-6, $"momentum {before} became {after}");
        Assert.True(b.Vel.X > 500, "the light pin flies off down the lane");
    }

    [Fact]
    public void ABallRolledAtAPocketSinks()
    {
        var t = PoolTable();
        var ball = t.Add(new Vec2(400, 400), 12);
        var corner = t.Pockets[0].Pos;
        ball.Vel = (corner - ball.Pos).Normalized() * t.SpeedToTravel((corner - ball.Pos).Length, 150);
        int sunkIn = -1;
        t.Sunk += (d, k) => sunkIn = k;
        Run(t, 4);
        Assert.True(ball.Sunk);
        Assert.Equal(0, sunkIn);
    }

    [Fact]
    public void ABallAlongTheRailRollsPastTheMiddlePocket()
    {
        var t = PoolTable();
        var ball = t.Add(new Vec2(300, 100 + 12), 12); // touching the top cushion
        ball.Vel = new Vec2(900, 0);
        Run(t, 0.8);
        Assert.False(ball.Sunk);
        Assert.True(ball.Pos.X > 700);
    }

    [Fact]
    public void FrictionBringsEverythingToRestInsideTheTable()
    {
        var t = PoolTable();
        var rng = new Random(5);
        for (int i = 0; i < 16; i++)
        {
            var d = t.Add(new Vec2(150 + i % 8 * 110, 200 + i / 8 * 200), 12);
            d.Vel = new Vec2(rng.NextDouble() * 4000 - 2000, rng.NextDouble() * 4000 - 2000);
        }
        for (int frame = 0; frame < 60 * 60 && !t.AllStill; frame++) t.Advance(1.0 / 60);
        Assert.True(t.AllStill);
        foreach (var d in t.Discs)
            Assert.True(d.Sunk || t.Bounds.Inflate(0.01).Contains(new Point(d.Pos.X, d.Pos.Y)), $"a ball ended off the table at {d.Pos}");
    }

    [Fact]
    public void NothingTunnelsAtTheTopSpeed()
    {
        var t = Frictionless();
        var ball = t.Add(new Vec2(100, 500), 11.5, mass: 4);
        var pin = t.Add(new Vec2(1200, 503), 6.5);
        ball.Vel = new Vec2(DiscTable.MaxSpeed * 2, 0); // capped to the top speed
        bool hit = false;
        t.Collided += (_, _, _) => hit = true;
        for (int frame = 0; frame < 60 && !hit; frame++)
        {
            t.Advance(1.0 / 60);
            Assert.True(ball.Pos.X < pin.Pos.X, "the ball passed through the pin");
        }
        Assert.True(hit);
        Assert.True(pin.Vel.X > DiscTable.MaxSpeed * 0.9);

        // and a thin wall of cushion holds it too
        var t2 = Frictionless();
        var fast = t2.Add(new Vec2(1000, 500), 6.5);
        fast.Vel = new Vec2(DiscTable.MaxSpeed, -DiscTable.MaxSpeed);
        for (int frame = 0; frame < 120; frame++)
        {
            t2.Advance(1.0 / 60);
            Assert.True(t2.Bounds.Contains(new Point(fast.Pos.X, fast.Pos.Y)));
        }
    }

    [Fact]
    public void SpeedToTravelMatchesHowFarABallRolls()
    {
        var t = PoolTable();
        var ball = t.Add(new Vec2(200, 350), 12);
        ball.Vel = new Vec2(t.SpeedToTravel(700, 0), 0);
        Run(t, 20);
        Assert.True(ball.Still);
        Assert.InRange(ball.Pos.X - 200, 690, 710);
    }

    [Fact]
    public void TheCastFindsTheFirstBallAndTheGhostBallTouchesIt()
    {
        var t = PoolTable();
        var cue = t.Add(new Vec2(300, 350), 12);
        var near = t.Add(new Vec2(600, 360), 12);
        t.Add(new Vec2(800, 350), 12);
        var dist = t.Cast(cue.Pos, new Vec2(1, 0), cue.R, cue, out var hit);
        Assert.Same(near, hit);
        Assert.NotNull(dist);
        var ghost = cue.Pos + new Vec2(1, 0) * dist!.Value;
        Assert.InRange((ghost - near.Pos).Length, 23.9, 24.1);
        Assert.Equal(1100 - 12 - 300, t.CastToCushion(cue.Pos, new Vec2(1, 0), 12), 6);
    }

    [Fact]
    public void ThePlannedPotGoesIn()
    {
        var t = PoolTable();
        var cue = t.Add(new Vec2(350, 420), 12, tag: 0);
        var ball = t.Add(new Vec2(800, 250), 12, tag: 1);
        var shot = t.PlanPot(cue, 2600);
        Assert.NotNull(shot);
        Assert.Same(ball, shot!.Value.Target);
        int sunkIn = -1;
        t.Sunk += (d, k) => { if (d == ball) sunkIn = k; };
        cue.Vel = shot.Value.Dir * shot.Value.Speed;
        Run(t, 10);
        Assert.True(ball.Sunk, $"the ball stopped at {ball.Pos}");
        Assert.Equal(shot.Value.Pocket, sunkIn);
    }

    [Fact]
    public void ThePlannerSkipsABlockedPot()
    {
        var t = PoolTable();
        var cue = t.Add(new Vec2(300, 350), 12);
        var target = t.Add(new Vec2(700, 350), 12);
        t.Add(new Vec2(500, 350), 12); // right in the way of the cue ball
        var shot = t.PlanPot(cue, 2600);
        Assert.True(shot == null || shot.Value.Target != target || Math.Abs(shot.Value.Dir.Y) > 0.05);
    }

    [Fact]
    public void ShootingThePlannedPotsClearsSeveralBalls()
    {
        var t = PoolTable();
        var rng = new Random(11);
        var cue = t.Add(new Vec2(350, 350), 12);
        for (int i = 0; i < 6; i++) t.Add(new Vec2(250 + rng.NextDouble() * 800, 160 + rng.NextDouble() * 380), 12, tag: i + 1);
        int potted = 0;
        t.Sunk += (d, _) => { if (d != cue) potted++; };
        for (int shotNo = 0; shotNo < 30 && potted < 6; shotNo++)
        {
            if (cue.Sunk)
            {
                cue.Sunk = false;
                cue.Pos = new Vec2(350, 350);
            }
            if (t.PlanPot(cue, 2600) is DiscTable.Shot shot) cue.Vel = shot.Dir * shot.Speed * 1.15;
            else cue.Vel = new Vec2(1500, (rng.NextDouble() - 0.5) * 900);
            for (int frame = 0; frame < 60 * 20 && !t.AllStill; frame++) t.Advance(1.0 / 60);
        }
        Assert.True(potted >= 4, $"only {potted} of 6 went in");
    }
}
