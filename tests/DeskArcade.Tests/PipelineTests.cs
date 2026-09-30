using System;
using System.Collections.Generic;
using System.Linq;
using DeskArcade.Games;
using Xunit;
using static DeskArcade.Games.PipelineRules;

namespace DeskArcade.Tests;

public class PipelineTests
{
    [Fact]
    public void PiecesExitTheWayTheyAreOpen()
    {
        Assert.Equal(Side.West, Exit(Pipe.Horizontal, Side.East));
        Assert.Null(Exit(Pipe.Horizontal, Side.North));
        Assert.Equal(Side.East, Exit(Pipe.NorthEast, Side.North));
        Assert.Equal(Side.North, Exit(Pipe.NorthEast, Side.East));
        Assert.Null(Exit(Pipe.NorthEast, Side.South));
        Assert.Equal(Side.South, Exit(Pipe.Cross, Side.North)); // straight through, both ways
        Assert.Equal(Side.West, Exit(Pipe.Cross, Side.East));
        Assert.Equal(Pipe.SouthWest, Joining(Side.West, Side.South));
        Assert.Equal(Pipe.Vertical, Joining(Side.South, Side.North));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(12)]
    [InlineData(30)]
    public void EveryLevelHasARouteAndGrowsHarder(int level)
    {
        for (int seed = 0; seed < 40; seed++)
        {
            var r = new PipelineRules(level, seed);
            Assert.NotNull(r.ShortestPath());
            Assert.Equal(QueueLength, r.Queue.Count);
            Assert.False(r.Blocked(r.Start.X, r.Start.Y));
            Assert.False(r.Blocked(r.End.X, r.End.Y));
        }
        Assert.True(CountdownFor(level + 1) <= CountdownFor(level));
        Assert.True(FlowStepFor(level + 1) <= FlowStepFor(level));
        Assert.True(ObstaclesFor(level + 1) >= ObstaclesFor(level));
    }

    [Fact]
    public void TheSameSeedMakesTheSameLevel()
    {
        var a = new PipelineRules(4, 1234);
        var b = new PipelineRules(4, 1234);
        Assert.Equal(a.Start, b.Start);
        Assert.Equal(a.End, b.End);
        Assert.Equal(a.Queue, b.Queue);
        for (int x = 0; x < Width; x++)
            for (int y = 0; y < Height; y++)
                Assert.Equal(a.Blocked(x, y), b.Blocked(x, y));
    }

    [Fact]
    public void LayingTakesTheFrontOfTheQueueAndReplacingCostsTime()
    {
        var r = new PipelineRules(1, 7);
        var (x, y) = FreeCell(r);
        var front = r.Queue[0];
        var second = r.Queue[1];
        Assert.Equal("placed", r.Place(x, y)?.Kind);
        Assert.Equal(front.Pipe, r.PipeAt(x, y));
        Assert.Equal(second, r.Queue[0]);
        Assert.Equal(QueueLength, r.Queue.Count);
        double before = r.Countdown;
        Assert.Equal("replaced", r.Place(x, y)?.Kind);
        Assert.Equal(before - ReplaceCost, r.Countdown, 6);
        Assert.Null(r.Place(r.Start.X, r.Start.Y)); // not on the commit
        Assert.Null(r.Place(-1, 0));
    }

    [Fact]
    public void AnEmptyGridLeaksWhenTheCountdownEnds()
    {
        var r = new PipelineRules(1, 3);
        var events = r.Step(CountdownFor(1) + FlowStepFor(1) + 0.01);
        Assert.Contains(events, e => e.Kind == "flow");
        Assert.Contains(events, e => e.Kind == "leak");
        Assert.Equal(Phase.Leaked, r.State);
        Assert.True(r.Over);
    }

    [Fact]
    public void AFullRouteDeploysAndScoresPiecesBadgesAndTheLevel()
    {
        var r = new PipelineRules(2, 11);
        var placed = Solve(r, secondsPerPiece: 0, hurry: true);
        Assert.Equal(Phase.Deployed, r.State);
        Assert.Equal(r.Path.Count, r.Flowed);
        int badges = r.Path.Count(c => r.BadgeAt(c.X, c.Y) != Badge.None);
        int crossings = r.Path.Count - r.Path.Select(c => (c.X, c.Y)).Distinct().Count();
        Assert.Equal((r.Path.Count - crossings) * PipePoints + badges * BadgePoints + crossings * CrossPoints + DeployPoints * 2, r.Score);
        Assert.True(placed >= r.ShortestPath()!.Count);
    }

    [Theory]
    [InlineData(1, 0.8)]
    [InlineData(4, 0.8)]
    [InlineData(8, 0.6)]
    [InlineData(15, 0.45)]
    [InlineData(25, 0.35)]
    public void EveryLevelCanBeSolvedWithTheQueueItDeals(int level, double secondsPerPiece)
    {
        int solved = 0;
        const int Seeds = 60;
        for (int seed = 0; seed < Seeds; seed++)
        {
            var r = new PipelineRules(level, seed * 31 + level);
            Solve(r, secondsPerPiece, hurry: false);
            if (r.State == Phase.Deployed) solved++;
        }
        Assert.Equal(Seeds, solved);
    }

    [Fact]
    public void HurryingEndsTheCountdownAndRushesTheFlow()
    {
        var r = new PipelineRules(1, 5);
        r.Hurry();
        Assert.Equal(0, r.Countdown);
        Assert.Equal(HurryStep, r.FlowStep);
        r.Step(0.01);
        Assert.Equal(Phase.Flowing, r.State);
    }

    static (int X, int Y) FreeCell(PipelineRules r)
    {
        var route = r.ShortestPath()!.ToHashSet();
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                if (r.CanPlace(x, y) && !route.Contains((x, y))) return (x, y);
        throw new InvalidOperationException("no free cell");
    }

    /// <summary>
    /// A player that works the queue as it comes: each piece goes where the route needs that piece (a cross stands in for a
    /// straight), the cells nearest the commit first; a piece nothing needs goes on a cell off the route. Laying one takes
    /// <paramref name="secondsPerPiece"/> of game time. Returns how many pieces it laid.
    /// </summary>
    static int Solve(PipelineRules r, double secondsPerPiece, bool hurry)
    {
        var route = r.ShortestPath()!;
        var need = new List<(int X, int Y, Pipe Pipe)>();
        for (int i = 0; i < route.Count; i++)
        {
            var prev = i == 0 ? r.Start : route[i - 1];
            var next = i == route.Count - 1 ? r.End : route[i + 1];
            need.Add((route[i].X, route[i].Y, Joining(SideTowards(route[i], prev), SideTowards(route[i], next))));
        }
        var onRoute = route.ToHashSet();
        var dumps = new Queue<(int X, int Y)>();
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                if (r.CanPlace(x, y) && !onRoute.Contains((x, y))) dumps.Enqueue((x, y));
        int placed = 0;
        while (!r.Over && need.Count > 0)
        {
            var (pipe, _) = r.Queue[0];
            int k = need.FindIndex(n => n.Pipe == pipe || pipe == Pipe.Cross && n.Pipe is Pipe.Horizontal or Pipe.Vertical);
            if (k >= 0 && r.CanPlace(need[k].X, need[k].Y))
            {
                r.Place(need[k].X, need[k].Y);
                need.RemoveAt(k);
            }
            else
            {
                if (dumps.Count == 0) break;
                var d = dumps.Dequeue();
                r.Place(d.X, d.Y);
                dumps.Enqueue(d); // a dump cell takes another piece later (a replacement, which costs time)
            }
            placed++;
            if (secondsPerPiece > 0) r.Step(secondsPerPiece);
        }
        if (hurry) r.Hurry();
        for (int i = 0; i < 10_000 && !r.Over; i++) r.Step(0.05);
        return placed;
    }

    static Side SideTowards((int X, int Y) from, (int X, int Y) to) =>
        to.X > from.X ? Side.East : to.X < from.X ? Side.West : to.Y > from.Y ? Side.South : Side.North;
}
