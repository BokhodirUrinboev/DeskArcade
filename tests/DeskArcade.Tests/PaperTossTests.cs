using System;
using DeskArcade.Engine;
using DeskArcade.Games;
using Xunit;

namespace DeskArcade.Tests;

public class PaperTossTests
{
    const double H = PaperFlight.Step;

    /// <summary>Flies the paper against the bin (no screen edges) and reports whether it went in, and cleanly.</summary>
    static (bool In, bool Touched) Throw(Vec2 from, Vec2 v, double wind, Vec2 bin)
    {
        var paper = new BallBody(PaperFlight.R) { Pos = from, Vel = v };
        bool touched = false;
        for (int i = 0; i < 5 / H; i++)
        {
            PaperFlight.Fly(ref paper.Pos, ref paper.Vel, wind, H);
            var hit = PaperFlight.Collide(paper, bin);
            touched |= hit.Rim > 0 || hit.Wall > 0;
            if (PaperFlight.IsIn(paper.Pos, paper.Vel, bin)) return (true, touched);
            if (paper.Vel.Y > 0 && paper.Pos.Y > bin.Y + 400) break; // fell past it
        }
        return (false, touched);
    }

    public static TheoryData<double, double, double, double> Setups()
    {
        var data = new TheoryData<double, double, double, double>();
        // from x, bin x, bin y (the floor is at 1000), wind
        foreach (double wind in new[] { -PaperFlight.MaxWind, -3.5, -1, 0, 1.5, 3.5, PaperFlight.MaxWind })
        {
            data.Add(130, 900, 1000, wind);   // across the taskbar
            data.Add(1790, 500, 1000, wind);  // the other way
            data.Add(130, 1500, 1000, wind);  // far
            data.Add(130, 700, 520, wind);    // up on a window top
            data.Add(1790, 1200, 380, wind);  // high window top, close by
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Setups))]
    public void TheSolvedThrowLandsInTheBin(double fromX, double binX, double binY, double wind)
    {
        var from = new Vec2(fromX, 1000 - PaperFlight.R);
        var bin = new Vec2(binX, binY);
        Assert.True(PaperFlight.SolveThrow(from, PaperFlight.Opening(bin), wind, out var v, ceiling: 0), "no throw found");
        Assert.True(v.Length <= PaperFlight.MaxThrow);
        var (inside, touched) = Throw(from, v, wind, bin);
        Assert.True(inside, $"missed with v={v}");
        // a bin high up close by can be grazed on the way up; one on the floor is a clean swish
        if (binY == 1000) Assert.False(touched, "the aimed throw should be a swish");
    }

    [Fact]
    public void TheSolverKeepsTheFlightUnderTheCeiling()
    {
        var from = new Vec2(130, 1000 - PaperFlight.R);
        var target = PaperFlight.Opening(new Vec2(1400, 1000));
        Assert.True(PaperFlight.SolveThrow(from, target, 0, out var free), "no throw found without a ceiling");
        double apex = from.Y;
        for (Vec2 p0 = from, v0 = free; v0.Y < 0;) { PaperFlight.Fly(ref p0, ref v0, 0, H); apex = Math.Min(apex, p0.Y); }

        // a ceiling lower than that arc leaves no throw steep enough to drop in
        Assert.False(PaperFlight.SolveThrow(from, target, 0, out _, ceiling: apex - PaperFlight.R + 80));

        double ceiling = apex - PaperFlight.R - 2;
        Assert.True(PaperFlight.SolveThrow(from, target, 0, out var v, ceiling: ceiling), "no throw found under the ceiling");
        Vec2 p = from, vel = v;
        for (int i = 0; i < 4 / H && (vel.Y < 0 || p.Y < target.Y); i++)
        {
            PaperFlight.Fly(ref p, ref vel, 0, H);
            Assert.True(p.Y - PaperFlight.R >= ceiling - 0.5, $"hit the ceiling at {p}");
        }
    }

    [Fact]
    public void AnOutOfReachBinHasNoSolution()
    {
        var from = new Vec2(0, 1000 - PaperFlight.R);
        Assert.False(PaperFlight.SolveThrow(from, PaperFlight.Opening(new Vec2(6000, 1000)), 0, out _));
    }

    [Fact]
    public void WindDeflectsTheFlight()
    {
        double XAfterOneSecond(double wind)
        {
            Vec2 p = new(0, 0), v = new(800, -900);
            for (int i = 0; i < 1 / H; i++) PaperFlight.Fly(ref p, ref v, wind, H);
            return p.X;
        }
        double calm = XAfterOneSecond(0);
        Assert.True(XAfterOneSecond(3) - calm > 60, "a tailwind should carry it noticeably further");
        Assert.True(calm - XAfterOneSecond(-3) > 60, "a headwind should hold it back noticeably");
        Assert.True(XAfterOneSecond(PaperFlight.MaxWind) > XAfterOneSecond(3));
    }

    [Fact]
    public void DragSlowsThePaper()
    {
        Vec2 p = new(0, 0), v = new(1000, 0);
        for (int i = 0; i < 1 / H; i++) PaperFlight.Fly(ref p, ref v, 0, H);
        Assert.InRange(v.X, 250, 450); // high drag: about a third of the speed is left after a second
        Assert.True(p.X < 750, "drag should shorten the flight compared with 1000 px in a vacuum");
    }

    [Fact]
    public void APaperDroppedOntoTheRimBouncesOut()
    {
        var bin = new Vec2(1000, 1000);
        var lip = PaperFlight.LipLeft(bin);
        var paper = new BallBody(PaperFlight.R) { Pos = lip + new Vec2(-6, -60) };
        bool rim = false;
        for (int i = 0; i < 2 / H; i++)
        {
            PaperFlight.Fly(ref paper.Pos, ref paper.Vel, 0, H);
            rim |= PaperFlight.Collide(paper, bin).Rim > 0;
            Assert.False(PaperFlight.IsIn(paper.Pos, paper.Vel, bin));
        }
        Assert.True(rim);
        Assert.True(paper.Pos.X < lip.X, "it should fall off the outside");
    }

    [Fact]
    public void OnlyAFallingPaperInsideTheBinCounts()
    {
        var bin = new Vec2(500, 1000);
        var inside = new Vec2(500, 1000 - PaperFlight.BinH / 2);
        Assert.True(PaperFlight.IsIn(inside, new Vec2(0, 300), bin));
        Assert.False(PaperFlight.IsIn(inside, new Vec2(0, -300), bin));                         // still rising
        Assert.False(PaperFlight.IsIn(new Vec2(500, 1000 - PaperFlight.BinH - 10), new Vec2(0, 300), bin)); // above the rim
        Assert.False(PaperFlight.IsIn(new Vec2(500 + PaperFlight.BinTopW / 2 + 5, inside.Y), new Vec2(0, 300), bin)); // beside it
    }

    [Fact]
    public void ScoringAndWindRules()
    {
        Assert.Equal(1, PaperFlight.Points(swish: false));
        Assert.Equal(2, PaperFlight.Points(swish: true));
        Assert.True(PaperFlight.WindLimit(0) < PaperFlight.WindLimit(3));
        Assert.Equal(PaperFlight.MaxWind, PaperFlight.WindLimit(50));
    }

    [Fact]
    public void AShakyDemoHandScoresOftenButNotAlways()
    {
        var rng = new Random(11);
        var from = new Vec2(130, 1000 - PaperFlight.R);
        int made = 0;
        const int throws = 200;
        for (int i = 0; i < throws; i++)
        {
            var bin = new Vec2(600 + rng.NextDouble() * 900, 1000);
            double wind = (rng.NextDouble() * 2 - 1) * PaperFlight.MaxWind;
            var target = PaperFlight.Opening(bin) + new Vec2((rng.NextDouble() - 0.5) * 12, 0);
            Assert.True(PaperFlight.SolveThrow(from, target, wind, out var v, ceiling: 0));
            if (Throw(from, v * (1 + (rng.NextDouble() - 0.5) * 0.035), wind, bin).In) made++;
        }
        Assert.InRange(made, throws * 0.45, throws * 0.95);
    }
}
