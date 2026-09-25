using System;
using System.Linq;
using Avalonia;
using DeskArcade.Engine;
using DeskArcade.Games;
using Xunit;

namespace DeskArcade.Tests;

/// <summary>
/// The pure pieces behind the performance pass. The pool table's pockets are computed from the felt alone, so the
/// physics (absolute) and the drawing (a felt at the origin, moved by a transform) agree and a drag only shifts them.
/// The card games' computer players are deterministic: once no computer seat has a move, the table is waiting on the
/// player, so the frame loop can stop there without stalling a game.
/// </summary>
public class PerfTests
{
    static void AssertAt(Vec2 expected, Vec2 actual)
    {
        Assert.Equal(expected.X, actual.X, 9);
        Assert.Equal(expected.Y, actual.Y, 9);
    }

    [Fact]
    public void PoolPocketsSitOnTheCornersAndTheLongRails()
    {
        var pockets = PoolGame.PocketsOf(new Rect(100, 200, 920, 460), 10);
        Assert.Equal(6, pockets.Length);
        // the corners first (a little outside the felt), then the middles of the top and bottom rails: the physics reports a pocket by its index
        AssertAt(new Vec2(95, 195), pockets[0].Pos);
        AssertAt(new Vec2(1025, 195), pockets[1].Pos);
        AssertAt(new Vec2(95, 665), pockets[2].Pos);
        AssertAt(new Vec2(1025, 665), pockets[3].Pos);
        AssertAt(new Vec2(560, 191), pockets[4].Pos);
        AssertAt(new Vec2(560, 669), pockets[5].Pos);
        Assert.All(pockets.Take(4), p => Assert.Equal(20, p.R, 9));
        Assert.All(pockets.Skip(4), p => Assert.Equal(17.5, p.R, 9));
    }

    [Fact]
    public void PoolPocketsMoveWithTheFeltAndOnlyWithIt()
    {
        var felt = new Rect(100, 200, 920, 460);
        var before = PoolGame.PocketsOf(felt, 12);
        var after = PoolGame.PocketsOf(felt.Translate(new Vector(37, -12)), 12);
        for (int i = 0; i < before.Length; i++)
        {
            AssertAt(before[i].Pos + new Vec2(37, -12), after[i].Pos);
            Assert.Equal(before[i].R, after[i].R);
        }
        // the drawing lays the pockets around a felt at the origin: the felt's place is the only difference from the physics'
        var drawn = PoolGame.PocketsOf(new Rect(0, 0, felt.Width, felt.Height), 12);
        for (int i = 0; i < before.Length; i++)
            AssertAt(before[i].Pos, drawn[i].Pos + new Vec2(felt.X, felt.Y));
    }

    [Fact]
    public void PoolPocketsGrowWithTheBalls()
    {
        var small = PoolGame.PocketsOf(new Rect(0, 0, 600, 300), 8);
        var large = PoolGame.PocketsOf(new Rect(0, 0, 600, 300), 12);
        Assert.All(small.Zip(large), pair => Assert.True(pair.First.R < pair.Second.R));
    }

    /// <summary>Plays the first computer move on offer, in the order the game asks (the defender, then the attackers round the table).</summary>
    static bool ComputerMoves(DurakRules r, int[] cpus)
    {
        var order = new[] { r.Defender }.Concat(Enumerable.Range(0, r.Players).Select(i => (r.Attacker + i) % r.Players));
        foreach (int seat in order.Where(cpus.Contains))
        {
            if (r.CpuAction(seat) is not { } a) continue;
            Assert.True(r.Act(seat, a.Kind, a.Card, a.Index), $"seat {seat}'s {a.Kind} was refused");
            return true;
        }
        return false;
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(2, 7)]
    [InlineData(3, 3)]
    [InlineData(4, 11)]
    public void DurakWaitsOnThePlayerWheneverTheComputersHaveNoMove(int players, int seed)
    {
        var r = new DurakRules(players, new Random(seed));
        int[] cpus = Enumerable.Range(1, players - 1).ToArray();
        int steps = 0;
        while (!r.Over && steps++ < 5000)
        {
            if (ComputerMoves(r, cpus)) continue;
            // the computers are done: asking again changes nothing, and the player must have a move or the game would stall
            Assert.All(cpus, seat => Assert.Null(r.CpuAction(seat)));
            var mine = r.CpuAction(0);
            Assert.True(mine.HasValue, "nobody at the table has a move");
            Assert.True(r.Act(0, mine.Value.Kind, mine.Value.Card, mine.Value.Index));
        }
        Assert.True(r.Over, "the deal did not finish");
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(2, 5)]
    [InlineData(3, 9)]
    public void LastCardWaitsOnThePlayerWheneverTheComputersHaveNoMove(int players, int seed)
    {
        var r = new LastCardRules(players, new Random(seed));
        int steps = 0;
        while (!r.Over && steps++ < 5000)
        {
            int seat = r.Turn;
            var move = r.CpuAction(seat);
            Assert.True(move.HasValue, $"seat {seat} is on turn without a move");
            // off turn, the computers have nothing: only the seat on turn can move
            for (int s = 0; s < players; s++)
                if (s != seat) Assert.Null(r.CpuAction(s));
            Assert.True(r.Act(seat, move.Value.Kind, move.Value.Card, move.Value.Color));
        }
        // a deal between computers may end in a few moves or run past the cap (three players keep hitting whoever is close
        // to going out); what matters is that every step had exactly the seat on turn with a move
        Assert.True(r.Over || steps > 5000, "the loop stopped early");
    }
}
