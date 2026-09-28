using System.Linq;
using DeskArcade.Engine;
using DeskArcade.Games;
using Xunit;

namespace DeskArcade.Tests;

public class CupsTests
{
    static CupsRound Racked()
    {
        var r = new CupsRound();
        r.Rack(800, 900);
        return r;
    }

    [Fact]
    public void ARackIsSixCupsInARowStandingOnTheSurface()
    {
        var r = Racked();
        Assert.Equal(CupsRound.RackSize, r.Standing);
        Assert.All(r.Cups, c => Assert.Equal(900 - Cup.Height, c.RimY));
        var xs = r.Cups.Select(c => c.X).ToList();
        Assert.Equal(800, xs.Average(), 6);
        Assert.All(xs.Zip(xs.Skip(1)), p => Assert.Equal(CupsRound.Spacing, p.Second - p.First, 6));
    }

    [Fact]
    public void ABallDropsInOnlyThroughTheMouthGoingDown()
    {
        var cup = new Cup { X = 500, RimY = 800 };
        Assert.True(cup.Catches(new Vec2(500, 790), new Vec2(502, 806), 7));
        Assert.False(cup.Catches(new Vec2(500, 806), new Vec2(500, 790), 7)); // going up
        Assert.False(cup.Catches(new Vec2(500 + Cup.TopHalf, 790), new Vec2(500 + Cup.TopHalf, 806), 7)); // on the lip
        Assert.False(cup.Catches(new Vec2(560, 790), new Vec2(560, 806), 7)); // beside it
        cup.Standing = false;
        Assert.False(cup.Catches(new Vec2(500, 790), new Vec2(502, 806), 7)); // it has come off
    }

    [Fact]
    public void ACupScoresTenAndComesOff()
    {
        var r = Racked();
        r.Throw();
        var (points, off, cleared) = r.Sink(r.Cups[2], bounced: false);
        Assert.Equal(CupsRound.CupPoints, points);
        Assert.Single(off);
        Assert.False(cleared);
        Assert.Equal(5, r.Standing);
        Assert.Equal(1, r.Sunk);
    }

    [Fact]
    public void ABounceShotScoresTwentyAndTakesTheNearestCupToo()
    {
        var r = Racked();
        r.Throw();
        var (points, off, _) = r.Sink(r.Cups[2], bounced: true);
        Assert.Equal(CupsRound.BouncePoints, points);
        Assert.Equal(2, off.Count);
        Assert.Equal(CupsRound.Spacing, System.Math.Abs(off[1].X - off[0].X), 6);
        Assert.Equal(4, r.Standing);
    }

    [Fact]
    public void AClearedRackPaysForTheBallsLeft()
    {
        var r = Racked();
        (int, System.Collections.Generic.List<Cup>, bool) last = default;
        foreach (var cup in r.Cups.ToList())
        {
            r.Throw();
            last = r.Sink(cup, bounced: false);
        }
        Assert.True(last.Item3);
        Assert.Equal(CupsRound.CupPoints + (CupsRound.Balls - 6) * CupsRound.SpareBall, last.Item1);
        Assert.Equal(6, r.RackThrows);
        r.Rack(300, 1000);
        Assert.Equal(2, r.Racks);
        Assert.Equal(0, r.RackThrows);
        Assert.Equal(6, r.Standing);
    }

    [Fact]
    public void TenBallsMakeARound()
    {
        var r = Racked();
        for (int i = 0; i < CupsRound.Balls; i++) Assert.True(r.Throw());
        Assert.True(r.Over);
        Assert.False(r.Throw());
        Assert.Equal(CupsRound.Balls, r.Throws);
    }

    [Fact]
    public void SinkingACupThatIsDownDoesNothing()
    {
        var r = Racked();
        r.Sink(r.Cups[0], false);
        var (points, off, _) = r.Sink(r.Cups[0], false);
        Assert.Equal(0, points);
        Assert.Empty(off);
    }
}
