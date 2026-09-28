using System;
using System.Linq;
using Avalonia;
using DeskArcade.Engine;
using DeskArcade.Games;
using Xunit;
using Xunit.Abstractions;

namespace DeskArcade.Tests;

public class PaperPlaneTests
{
    static readonly Rect Box = new(0, 0, 1920, 1040);
    static readonly Vec2 Launch = new(80, 900);
    readonly ITestOutputHelper _out;

    public PaperPlaneTests(ITestOutputHelper output) => _out = output;

    static PaperPlaneFlight Fly(Vec2 v, params PlaneTop[] tops)
    {
        var f = new PaperPlaneFlight(Launch, v, Box, tops);
        f.Finish();
        return f;
    }

    static Vec2 At(double degreesUp, double speed) =>
        new(Math.Cos(degreesUp * Math.PI / 180) * speed, -Math.Sin(degreesUp * Math.PI / 180) * speed);

    [Fact]
    public void AGlideGoesAFewTimesAsFarAsItDrops()
    {
        var f = new PaperPlaneFlight(new Vec2(80, 840), new Vec2(PaperPlaneFlight.TrimSpeed, 0), Box, Array.Empty<PlaneTop>());
        f.Finish();
        double drop = Box.Bottom - 840;
        _out.WriteLine($"glide {f.Distance:0} px from {drop:0} px up, {f.Landed}");
        Assert.Equal(PlaneLanding.Floor, f.Landed);
        Assert.InRange(f.Distance, drop * 3, drop * 6.5);
    }

    [Fact]
    public void AGoodThrowCrossesMostOfTheScreen()
    {
        var best = Enumerable.Range(0, 12).Select(i => Fly(At(i * 5, 560))).Max(f => f.Distance);
        _out.WriteLine($"best of the angles: {best:0} px");
        Assert.InRange(best, 700, 1700); // across most of the screen, but not every throw to the far wall
    }

    [Fact]
    public void AThrowStraightUpStallsAndComesDownShort()
    {
        var steep = Fly(At(80, 600));
        var good = Enumerable.Range(0, 8).Select(i => Fly(At(i * 5, 560))).Max(f => f.Distance);
        _out.WriteLine($"steep {steep.Distance:0}, good {good:0}");
        Assert.True(steep.Distance < good * 0.5);
    }

    [Fact]
    public void WarmAirAboveAWindowCarriesItFurther()
    {
        var plain = Fly(At(10, 420));
        var lifted = Fly(At(10, 420), new PlaneTop(700, 1500, 1010));
        _out.WriteLine($"plain {plain.Distance:0}, over a thermal {lifted.Distance:0}");
        Assert.True(lifted.Distance > plain.Distance + 50);
        Assert.NotEqual(PlaneLanding.Window, plain.Landed);
    }

    [Fact]
    public void ItLandsOnAWindowTopItComesDownOnto()
    {
        var f = Fly(At(20, 440), new PlaneTop(250, 1900, 840));
        Assert.Equal(PlaneLanding.Window, f.Landed);
        Assert.Equal(840, f.Pos.Y);
    }

    [Fact]
    public void AWallStopsItDeadAndScoresHalf()
    {
        var f = new PaperPlaneFlight(new Vec2(1800, 500), new Vec2(700, -50), Box, Array.Empty<PlaneTop>());
        f.Finish();
        Assert.Equal(PlaneLanding.Crash, f.Landed);
        int full = PaperPlaneScore.Metres(f.Distance, 1);
        Assert.Equal(full / 2, PaperPlaneScore.Points(f, 1, (0, 0)));
    }

    [Fact]
    public void TheCeilingKnocksItBackDown()
    {
        var f = new PaperPlaneFlight(new Vec2(80, 60), At(60, 700), Box, Array.Empty<PlaneTop>());
        for (int i = 0; i < 200; i++)
        {
            f.Advance(1.0 / 60);
            Assert.True(f.Pos.Y >= Box.Top);
        }
    }

    [Fact]
    public void ALandingOnTheStripScoresABonus()
    {
        var f = Fly(At(15, 520));
        Assert.Equal(PlaneLanding.Floor, f.Landed);
        int plain = PaperPlaneScore.Points(f, 1, (0, 0));
        int onStrip = PaperPlaneScore.Points(f, 1, (f.Pos.X - 20, f.Pos.X + 20));
        Assert.Equal(plain + PaperPlaneScore.StripBonus, onStrip);
        Assert.Equal(PaperPlaneScore.Metres(f.Distance, 1), plain);
    }

    [Fact]
    public void ALaunchIsCappedAtTheTopSpeed()
    {
        var f = new PaperPlaneFlight(Launch, new Vec2(5000, 0), Box, Array.Empty<PlaneTop>());
        Assert.Equal(PaperPlaneFlight.MaxLaunch, f.Vel.Length, 6);
    }

    [Fact]
    public void MetresScaleWithTheOverlay() => Assert.Equal(50, PaperPlaneScore.Metres(1000, 2));
}
