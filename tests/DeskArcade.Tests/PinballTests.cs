using System;
using System.Collections.Generic;
using Avalonia;
using DeskArcade.Engine;
using DeskArcade.Games;
using Xunit;

namespace DeskArcade.Tests;

public class PinballTableTests
{
    static readonly Rect Arena = new(0, 0, 1920, 1040);

    static PinballTable NewTable(int seed = 1) => new(Arena, default, new Random(seed));

    static Vec2 UpNormal(PinballTable.Flipper f)
    {
        var n = new Vec2(-f.Dir.Y, f.Dir.X);
        return n.Y > 0 ? -n : n;
    }

    /// <summary>A ball resting on top of flipper <paramref name="f"/> at <paramref name="along"/> of its length.</summary>
    static void RestOn(PinballTable t, PinballTable.Flipper f, double along)
    {
        double r = f.BaseR + (f.TipR - f.BaseR) * along;
        t.Ball = f.Pivot + f.Dir * (f.Length * along) + UpNormal(f) * (t.BallR + r + 0.5);
        t.BallVel = default;
        t.InPlay = true;
    }

    static double Above(PinballTable t, PinballTable.Flipper f)
    {
        var c = PinballTable.Closest(f.Pivot, f.Tip, t.Ball, out _);
        return Vec2.Dot(t.Ball - c, UpNormal(f));
    }

    [Fact]
    public void ABallOnARaisedFlipperStaysCradled()
    {
        var t = NewTable();
        t.Left.Up = true;
        t.Advance(0.2);
        Assert.True(t.Left.Raised);
        RestOn(t, t.Left, 0.5);
        for (int i = 0; i < 60 * 4; i++) t.Advance(1.0 / 60);

        var f = t.Left;
        Assert.True(t.InPlay, "the cradled ball drained");
        Assert.True(t.BallVel.Length < 80, $"still moving at {t.BallVel.Length:0} px/s");
        var c = PinballTable.Closest(f.Pivot, f.Tip, t.Ball, out _);
        Assert.True((t.Ball - c).Length < t.BallR + f.BaseR + 3, "the ball left the flipper");
        Assert.True(t.Ball.Y < f.Pivot.Y && t.Ball.X > f.Pivot.X - f.Length && t.Ball.X < f.Tip.X, $"ball at {t.Ball} is not in the cradle");
    }

    [Theory]
    [InlineData(0.45)]
    [InlineData(0.75)]
    public void AFlipLaunchesABallRestingOnTheLoweredFlipper(double along)
    {
        var t = NewTable();
        RestOn(t, t.Left, along);
        t.Left.Up = true;
        double fastestUp = 0;
        for (int i = 0; i < 60; i++)
        {
            t.Advance(PinballTable.Step);
            fastestUp = Math.Max(fastestUp, -t.BallVel.Y);
        }
        Assert.True(fastestUp > 1300, $"launched at only {fastestUp:0} px/s");
        Assert.True(t.BallVel.Length <= PinballTable.MaxSpeed + 1e-6);
    }

    [Fact]
    public void ALoweredFlipperLetsTheBallRollOffIntoTheDrain()
    {
        var t = NewTable();
        RestOn(t, t.Right, 0.3);
        bool drained = false;
        t.Event += (kind, _, _, _) => drained |= kind == PinballTable.Hit.Drain;
        for (int i = 0; i < 60 * 3 && !drained; i++) t.Advance(1.0 / 60);
        Assert.True(drained);
    }

    [Fact]
    public void ABallDroppedInTheMiddleOfTheDrainGapFallsThroughUntouched()
    {
        var t = NewTable();
        var events = new List<PinballTable.Hit>();
        t.Event += (kind, _, _, _) => events.Add(kind);
        t.Ball = new Vec2(Arena.Center.X, t.Left.Pivot.Y - 200);
        t.BallVel = default;
        t.InPlay = true;
        double sideways = 0;
        for (int i = 0; i < 600 * 3 && t.InPlay; i++)
        {
            t.StepOnce(PinballTable.Step);
            sideways = Math.Max(sideways, Math.Abs(t.BallVel.X));
        }
        Assert.False(t.InPlay);
        Assert.Equal(new[] { PinballTable.Hit.Drain }, events);
        Assert.Equal(0, sideways);
    }

    [Theory]
    [InlineData(false, 0.5)]
    [InlineData(true, 0.5)]
    [InlineData(false, 0.9)]
    [InlineData(true, 0.9)]
    public void AMaxSpeedBallNeverTunnelsThroughAFlipper(bool flipping, double along)
    {
        var t = NewTable();
        var f = t.Left;
        var up = UpNormal(f);
        t.Ball = f.Pivot + f.Dir * (f.Length * along) + up * 150;
        t.BallVel = up * -PinballTable.MaxSpeed;
        t.InPlay = true;
        f.Up = flipping;
        for (int i = 0; i < 600 / 5 && t.InPlay; i++)
        {
            t.StepOnce(PinballTable.Step);
            var c = PinballTable.Closest(f.Pivot, f.Tip, t.Ball, out double s);
            // glancing off past the round tip is fine; being on the underside within its length is not
            Assert.True(s >= 1 || Above(t, f) > 0, $"step {i}: the ball got under the flipper: ball {t.Ball} v {t.BallVel} s {s:0.00} d {(t.Ball - c).Length:0.0} angle {f.Angle:0.00} tip {f.Tip}");
        }
        Assert.True(t.BallVel.Y < 0, "the ball should have bounced back up");
    }

    [Fact]
    public void ABumperSendsTheBallAwayAtLeastAtTheKickSpeed()
    {
        var t = NewTable();
        var bumper = t.Bumpers[0];
        double kickedAt = -1;
        t.Event += (kind, i, _, _) =>
        {
            if (kind == PinballTable.Hit.Bumper && i == 0 && kickedAt < 0) kickedAt = t.BallVel.Length;
        };
        t.Ball = bumper.Center - new Vec2(bumper.Radius + t.BallR + 30, 0);
        t.BallVel = new Vec2(200, 0);
        t.InPlay = true;
        int before = t.Score;
        for (int i = 0; i < 600 && kickedAt < 0; i++) t.StepOnce(PinballTable.Step);
        Assert.True(kickedAt >= PinballTable.BumperKick - 1e-6, $"kicked at {kickedAt:0} px/s");
        Assert.Equal(before + PinballTable.BumperPoints, t.Score);
        Assert.True((t.Ball - bumper.Center).Length >= bumper.Radius + t.BallR - 0.01);
    }

    [Fact]
    public void AllThreeLanesLitDoubleTheScoreAndResetTheLights()
    {
        var t = NewTable();
        t.InPlay = true;
        for (int i = 0; i < 3; i++)
        {
            t.Ball = t.Lanes[i];
            t.BallVel = new Vec2(0, -300);
            t.StepOnce(PinballTable.Step);
            t.Ball = t.Lanes[i] + new Vec2(0, -60); // leave the lane again
            t.StepOnce(PinballTable.Step);
        }
        Assert.Equal(2, t.Multiplier);
        Assert.Equal(3 * PinballTable.LanePoints + PinballTable.LanesBonus, t.Score);
        Assert.All(t.LaneLit, lit => Assert.False(lit));

        t.Ball = t.Lanes[0];
        t.StepOnce(PinballTable.Step);
        Assert.Equal(3 * PinballTable.LanePoints + PinballTable.LanesBonus + 2 * PinballTable.LanePoints, t.Score);
    }

    [Fact]
    public void AWindowTopIsAOneWayLedge()
    {
        var t = NewTable();
        t.SetLedges(new[] { new Engine.Platform((IntPtr)1, 500, 300, 700) });
        Assert.Single(t.Ledges);

        // from below it passes straight through
        t.Ball = new Vec2(450, 560);
        t.BallVel = new Vec2(0, -900);
        t.InPlay = true;
        for (int i = 0; i < 60; i++) t.StepOnce(PinballTable.Step);
        Assert.True(t.Ball.Y < 500 - t.BallR);

        // from above it lands and rolls off an end instead of parking there
        t.BallVel = new Vec2(0, 200);
        bool landed = false;
        for (int i = 0; i < 600 * 4; i++)
        {
            t.StepOnce(PinballTable.Step);
            landed |= Math.Abs(t.Ball.Y + t.BallR - 500) < 0.5;
            if (t.Ball.Y > 520) break;
        }
        Assert.True(landed);
        Assert.True(t.Ball.Y > 520, "the ball stayed on the window top");
    }

    [Fact]
    public void AWindowTopFromWallToWallIsIgnored()
    {
        var t = NewTable();
        t.SetLedges(new[] { new Engine.Platform((IntPtr)1, 500, 0, 1920), new Engine.Platform((IntPtr)2, t.LedgeMaxY + 40, 300, 700) });
        Assert.Empty(t.Ledges);
    }

    [Fact]
    public void TheAutoplayerSeesTheBallComingAndLeavesTheTableAsItWas()
    {
        var t = NewTable();
        var f = t.Right;
        t.Ball = f.Pivot + f.Dir * (f.Length * 0.6) + new Vec2(0, -120);
        t.BallVel = default;
        t.InPlay = true;
        var ball = t.Ball;
        int side = t.PredictFlip(0.5, out double at);
        Assert.Equal(1, side);
        Assert.InRange(at, 0.1, 0.5);
        Assert.Equal(ball, t.Ball);
        Assert.Equal(default, t.BallVel);
        Assert.True(t.InPlay);
    }

    /// <summary>What --demo does: look ahead every 150 ms, flip just before the ball arrives, hold briefly.</summary>
    static (double life, int bumpers, int score) Autoplay(Rect arena, int seed, int balls)
    {
        var t = new PinballTable(arena, default, new Random(seed));
        var rng = new Random(seed + 100);
        int bumpers = 0, drains = 0;
        t.Event += (kind, _, _, _) =>
        {
            if (kind == PinballTable.Hit.Bumper) bumpers++;
            if (kind == PinballTable.Hit.Drain) drains++;
        };
        const double dt = 1.0 / 60;
        double time = 0;
        double[] flipAt = { -1, -1 }, releaseAt = { -1, -1 };
        t.ResetBall();
        t.Serve();
        for (int frame = 0; drains < balls && frame < 60 * 60 * 10; frame++)
        {
            if (!t.InPlay) t.Serve();
            if (frame % 9 == 0)
            {
                int side = t.PredictFlip(0.4, out double at);
                if (side >= 0 && flipAt[side] < 0 && releaseAt[side] < 0)
                    flipAt[side] = time + Math.Max(0, at - 0.03 + (rng.NextDouble() - 0.5) * 0.05);
            }
            for (int i = 0; i < 2; i++)
            {
                var f = i == 0 ? t.Left : t.Right;
                if (flipAt[i] >= 0 && time >= flipAt[i]) { flipAt[i] = -1; f.Up = true; releaseAt[i] = time + 0.2; }
                else if (releaseAt[i] >= 0 && time >= releaseAt[i]) { releaseAt[i] = -1; f.Up = false; }
            }
            t.Advance(dt);
            time += dt;
        }
        return (time / Math.Max(1, drains), bumpers, t.Score);
    }

    [Fact]
    public void TheAutoplayerKeepsTheBallAliveAndHitsBumpers()
    {
        foreach (var arena in new[] { Arena, new Rect(0, 0, 1280, 680), new Rect(0, 0, 2560, 1400) })
        {
            var (life, bumpers, score) = Autoplay(arena, 3, 6);
            Assert.True(life > 8, $"{arena}: a ball only lasted {life:0.0}s");
            Assert.True(bumpers > 10, $"{arena}: only {bumpers} bumper hits");
            Assert.True(score > 0);
        }
    }

    [Fact]
    public void AServedBallRisesToTheLanes()
    {
        var t = NewTable(5);
        t.ResetBall();
        t.Serve();
        double highest = t.Ball.Y;
        for (int i = 0; i < 60 * 2 && t.InPlay; i++)
        {
            t.Advance(1.0 / 60);
            highest = Math.Min(highest, t.Ball.Y);
        }
        Assert.True(highest < t.Lanes[0].Y + 40, $"the serve only reached y={highest:0}");
    }

    [Fact]
    public void TheTableFitsSmallAndLargeScreens()
    {
        foreach (var arena in new[] { new Rect(0, 0, 1280, 680), new Rect(0, 0, 3840, 2100) })
        {
            var t = new PinballTable(arena, default, new Random(1));
            Assert.InRange(t.Left.Length, 110, 140);
            Assert.True(t.Right.Pivot.X - t.Left.Pivot.X > 2 * t.Left.Length);
            Assert.True(t.Lanes[0].Y > arena.Top && t.Left.Tip.Y < arena.Bottom);
            double slingTop = Math.Min(t.Slings[0][2].Y, t.Slings[1][2].Y);
            Assert.True(t.Bumpers[0].Center.Y > t.Lanes[0].Y && t.Bumpers[2].Center.Y + t.Bumpers[2].Radius < slingTop);
            Assert.True(t.Segments[0].A.Y > arena.Top + arena.Height * 0.5, "the rails start too high");
        }
    }
}
