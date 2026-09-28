using System;
using System.Linq;
using DeskArcade.Engine;
using DeskArcade.Games;
using Xunit;

namespace DeskArcade.Tests;

public class KiteTests
{
    static readonly Vec2 Hand = new(200, 900);

    static KiteFlight Fly(double seconds, double wind, double maxLine = 600, Func<double, Vec2>? hand = null)
    {
        var f = new KiteFlight(Hand, maxLine);
        for (double t = 0; t < seconds; t += 1 / 120.0) f.Step(1 / 120.0, wind, hand?.Invoke(t) ?? Hand);
        return f;
    }

    [Fact]
    public void InAFairWindTheKiteStandsHighDownwindOnATautLine()
    {
        var f = Fly(10, KiteWind.Base);
        Assert.True(f.Taut);
        Assert.True(f.Tension > 0.9, $"tension {f.Tension:0.00}");
        Assert.True(f.Pos.X > Hand.X, "downwind of the hand");
        Assert.InRange(f.Elevation, 40, 75);
        Assert.Equal(f.Line, (f.Pos - Hand).Length, 0);
    }

    [Fact]
    public void AStrongerWindFliesItSteeper()
    {
        Assert.True(Fly(8, 140).Elevation > Fly(8, 70).Elevation + 3);
    }

    [Fact]
    public void LineRunsOutWhileTautUpToTheMaximum()
    {
        var f = Fly(4, KiteWind.Base);
        Assert.True(f.Line > KiteFlight.StartLine + 30, $"line {f.Line:0}");
        var g = Fly(40, KiteWind.Base, maxLine: 320);
        Assert.Equal(320, g.Line, 6);
    }

    [Fact]
    public void WithoutWindItFalls()
    {
        var f = new KiteFlight(Hand, 600);
        double y0 = f.Pos.Y;
        for (int i = 0; i < 120; i++) f.Step(1 / 120.0, 0, Hand);
        Assert.True(f.Pos.Y > y0 + 20);
    }

    [Fact]
    public void PullingTheLineMakesItClimb()
    {
        // a light wind leaves the kite low; walking the hand back into the wind lifts it
        var still = Fly(8, 55, maxLine: 300);
        var pulled = Fly(8, 55, maxLine: 300, hand: t => t < 5 ? Hand : Hand + new Vec2(-Math.Min(3, t - 5) * 80, 0));
        Assert.True(pulled.Pos.Y < still.Pos.Y - 20, $"pulled {pulled.Pos.Y:0} vs still {still.Pos.Y:0}");
    }

    [Fact]
    public void RunningWithTheWindLetsItDrop()
    {
        var still = Fly(8, KiteWind.Base, maxLine: 300);
        var running = Fly(8, KiteWind.Base, maxLine: 300, hand: t => t < 5 ? Hand : Hand + new Vec2(Math.Min(3, t - 5) * 110, 0));
        Assert.True(running.Pos.Y > still.Pos.Y + 20, $"running {running.Pos.Y:0} vs still {still.Pos.Y:0}");
    }

    [Theory]
    [InlineData(700, 450)]
    [InlineData(1200, 300)]
    [InlineData(500, 650)]
    public void AHandCanSteerTheKiteOntoACloud(double x, double y)
    {
        // a player who keeps the hand where the kite, flying as it does now, would be over the cloud
        var target = new Vec2(x, y);
        var hand = Hand;
        var f = new KiteFlight(hand, 600);
        var wind = new KiteWind(new Random(9));
        double closest = double.MaxValue;
        for (double t = 0; t < 12; t += 1 / 60.0)
        {
            var goal = target - (f.Pos - f.Anchor);
            goal = new Vec2(Math.Clamp(goal.X, 10, 1900), Math.Clamp(goal.Y, 430, 1030));
            var d = goal - hand;
            double step = 300 / 60.0;
            hand = d.Length <= step ? goal : hand + d * (step / d.Length);
            f.Step(1 / 60.0, wind.Step(1 / 60.0), hand);
            closest = Math.Min(closest, (f.Pos - target).Length);
        }
        Assert.True(closest < 40, $"closest {closest:0}");
    }

    [Fact]
    public void TheStringNeverStretches()
    {
        var f = new KiteFlight(Hand, 400);
        var rng = new Random(3);
        var hand = Hand;
        for (int i = 0; i < 2000; i++)
        {
            hand += new Vec2(rng.NextDouble() * 20 - 10, rng.NextDouble() * 20 - 10);
            f.Step(1 / 120.0, 60 + rng.NextDouble() * 100, hand);
            Assert.True((f.Pos - hand).Length <= f.Line + 1e-6);
        }
    }

    [Fact]
    public void TheBoxStopsIt()
    {
        var f = Fly(10, 200);
        f.Confine(0, 250, 700);
        Assert.True(f.Pos.X <= 250 && f.Pos.Y >= 700);
        Assert.True(f.Vel.X <= 0 && f.Vel.Y >= 0);
    }

    [Fact]
    public void TheWindGustsAndLullsAroundItsBase()
    {
        var w = new KiteWind(new Random(5));
        var speeds = Enumerable.Range(0, 60 * 60).Select(_ => w.Step(1 / 60.0)).ToList();
        Assert.InRange(speeds.Average(), KiteWind.Base - 15, KiteWind.Base + 15);
        Assert.Contains(speeds, s => s > KiteWind.Base + 40); // a gust
        Assert.Contains(speeds, s => s < KiteWind.Base - 30); // a lull
        Assert.All(speeds, s => Assert.True(s > 0));
    }

    [Fact]
    public void CatchesScoreAndEveryFifthInARowPaysABonus()
    {
        var r = new KiteRound();
        Assert.Equal((KiteRound.CloudPoints, false), r.Catch(star: false));
        Assert.Equal((KiteRound.StarPoints, false), r.Catch(star: true));
        r.Catch(false);
        r.Catch(false);
        Assert.Equal((KiteRound.CloudPoints + KiteRound.StreakBonus, true), r.Catch(false));
        Assert.Equal(4, r.Clouds);
        Assert.Equal(1, r.Stars);
        r.Crash();
        Assert.Equal(0, r.Streak);
        for (int i = 0; i < 4; i++) Assert.False(r.Catch(false).StreakDone);
        Assert.True(r.Catch(false).StreakDone);
    }

    [Fact]
    public void AFlightIsAMinute()
    {
        var r = new KiteRound();
        r.Tick(59.5);
        Assert.False(r.Over);
        r.Tick(1);
        Assert.True(r.Over);
        Assert.Equal(0, r.Left);
        Assert.Equal((0, false), r.Catch(false));
    }
}
