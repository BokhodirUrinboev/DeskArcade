using System;
using System.Collections.Generic;
using System.Linq;
using DeskArcade.Games;
using Xunit;
using static DeskArcade.Games.InternsWorld;

namespace DeskArcade.Tests;

public class InternsTests
{
    const double Floor = 800;
    static readonly Ledge[] NoWindows = Array.Empty<Ledge>();

    /// <summary>A 1000-wide screen with the exit at <paramref name="exitX"/> and the trapdoor just above the floor at <paramref name="spawnX"/>.</summary>
    static InternsWorld World(double spawnX = 100, double exitX = 600, int total = 1, (double, double)[]? pits = null,
        int umbrellas = 0, int blockers = 0, int builders = 0, double spawnY = Floor - 20) =>
        new(0, 1000, Floor, pits ?? Array.Empty<(double, double)>(), spawnX, spawnY, default, 1, exitX, total, total,
            umbrellas, blockers, builders) { Open = true };

    static void Run(InternsWorld w, double seconds, IReadOnlyList<Ledge>? windows = null, double dt = 1 / 60.0)
    {
        for (double t = 0; t < seconds; t += dt) w.Step(dt, windows ?? NoWindows);
    }

    [Fact]
    public void AnInternWalksToTheExitAndIsSaved()
    {
        var w = World();
        Run(w, 16);
        Assert.Equal(1, w.Saved);
        Assert.True(w.Finished);
    }

    [Fact]
    public void TheyTurnRoundAtTheEdgeOfTheScreen()
    {
        var w = World(spawnX: 900, exitX: 500); // they start walking right, away from the exit
        Run(w, 1);
        Assert.Equal(1, w.Interns[0].Dir);
        Run(w, 4); // 100 px to the edge takes about three seconds
        Assert.Equal(-1, w.Interns[0].Dir);
        Run(w, 30);
        Assert.Equal(1, w.Saved);
    }

    [Fact]
    public void AManholeSwallowsThem()
    {
        var w = World(pits: new[] { (200.0, 250.0) });
        Run(w, 10);
        Assert.Equal(1, w.Lost);
        Assert.False(w.Interns[0].Splat);
    }

    [Fact]
    public void AStaircaseBridgesAManhole()
    {
        var w = World(pits: new[] { (200.0, 250.0) }, builders: 1);
        Run(w, 1);
        var intern = w.Interns[0];
        Run(w, (180 - intern.X) / WalkSpeed); // walk up to just short of the hole
        Assert.Equal(State.Walking, intern.State);
        Assert.True(w.Assign(intern, Tool.Builder));
        Run(w, StairSteps * BrickEvery + 0.2);
        Assert.Equal(StairSteps, w.Bricks.Count);
        Assert.Equal(Floor - StairSteps * BrickRise, w.Bricks[^1].Y);
        Run(w, 20);
        Assert.Equal(1, w.Saved); // over the top, a short drop past the hole, on to the exit
    }

    [Fact]
    public void AHighFallKillsWithoutAnUmbrella()
    {
        var high = World(spawnY: Floor - 300);
        Run(high, 4);
        Assert.Equal(1, high.Lost);
        Assert.True(high.Interns[0].Splat);

        var safe = World(spawnY: Floor - 300, umbrellas: 1);
        Run(safe, 1 / 30.0);
        Assert.True(safe.Assign(safe.Interns[0], Tool.Umbrella));
        Run(safe, 20);
        Assert.Equal(1, safe.Saved);
    }

    [Fact]
    public void TheyWalkAlongAWindowTopAndFallOffItsEnd()
    {
        var window = new[] { new Ledge(Floor - 80, 50, 300, 42) };
        var w = World(spawnY: Floor - 100); // lands on the window, 80 above the floor: a safe drop
        Run(w, 1, window);
        Assert.Equal(State.Walking, w.Interns[0].State);
        Assert.Equal(Floor - 80, w.Interns[0].Y);
        Assert.Equal((IntPtr)42, w.Interns[0].On);
        Run(w, 20, window);
        Assert.Equal(1, w.Saved);
    }

    [Fact]
    public void TheyRideAWindowThatMoves()
    {
        var window = new[] { new Ledge(Floor - 80, 50, 600, 7) };
        var w = World(spawnY: Floor - 100);
        Run(w, 1, window);
        double x = w.Interns[0].X;
        w.Carry(7, 30, -10);
        Assert.Equal(x + 30, w.Interns[0].X, 3);
        Assert.Equal(Floor - 90, w.Interns[0].Y, 3);
    }

    [Fact]
    public void ABlockerTurnsTheOthersRoundUntilReleased()
    {
        var w = World(total: 2, blockers: 1, exitX: 700);
        Run(w, 1);
        var first = w.Interns[0];
        Run(w, 3);
        Assert.True(w.Assign(first, Tool.Blocker));
        Run(w, 6);
        var second = w.Interns[1];
        Assert.True(second.X < first.X, "the second intern got past the blocker");
        Assert.True(w.OnlyBlockersLeft || second.State == State.Walking);
        Assert.True(w.Assign(first, Tool.Umbrella)); // any click lets a blocker go
        Assert.Equal(State.Walking, first.State);
        Run(w, 40);
        Assert.Equal(2, w.Saved);
    }

    [Fact]
    public void ToolsRunOut()
    {
        var w = World(total: 2, umbrellas: 1);
        Run(w, 3);
        Assert.True(w.Assign(w.Interns[0], Tool.Umbrella));
        Assert.False(w.Assign(w.Interns[1], Tool.Umbrella));
        Assert.False(w.Assign(w.Interns[1], Tool.Builder));
        Assert.Equal(0, w.Count(Tool.Umbrella));
    }

    [Fact]
    public void ReleasesEveryoneOneAtATime()
    {
        var w = World(total: 5);
        Run(w, SpawnEvery * 2.5);
        Assert.Equal(3, w.Released);
        Run(w, 60);
        Assert.Equal(5, w.Saved);
        Assert.True(w.Finished);
    }
}
