using System;
using System.Linq;
using DeskArcade.Games;
using Xunit;

namespace DeskArcade.Tests;

public class JengaTests
{
    static JengaTower Tower(int layers = JengaTower.StartLayers) => new(new Random(1), layers);

    [Fact]
    public void AFreshTowerStandsWithRoomToSway()
    {
        var t = Tower();
        Assert.Equal(12, t.Height);
        Assert.True(t.TopComplete);
        Assert.Equal(-1, t.FailingLayer());
        Assert.InRange(t.SwayRoom(), 1.0, 4);
    }

    [Fact]
    public void TheTopLayersCannotBeTaken()
    {
        var t = Tower();
        Assert.False(t.Takeable(t.Top, 1)); // the top layer
        Assert.True(t.Takeable(t.Top - 1, 1));
        Assert.True(t.Take(0, 1));
        Assert.False(t.Takeable(1, 1)); // one block at a time
        Assert.True(t.Place(0)); // a new, half-built top layer
        Assert.Equal(13, t.Height);
        Assert.False(t.TopComplete);
        Assert.False(t.Takeable(t.Top - 1, 1)); // the complete layer under a half-built top is out of bounds too
        Assert.True(t.Takeable(t.Top - 2, 1));
    }

    [Fact]
    public void TheTopFillsBeforeANewLayerStarts()
    {
        var t = Tower();
        t.Take(2, 1);
        Assert.Equal(new[] { 0, 1, 2 }, t.OpenSlots());
        t.Place(2);
        t.Take(3, 1);
        Assert.Equal(new[] { 0, 1 }, t.OpenSlots());
        Assert.False(t.Place(2)); // taken already
        Assert.True(t.Place(0));
        Assert.Equal(13, t.Height);
        Assert.Equal(2, t.Moved);
    }

    [Fact]
    public void TakingTheMiddleIsSafeAndLeavingOneSideBlockIsNot()
    {
        var t = Tower();
        t.Take(3, 1);
        t.Place(1);
        Assert.False(t.Check());
        var u = Tower();
        u.Take(3, 1);
        u.Place(1);
        u.Take(3, 2); // only the left block of layer 3 is left: the weight above is centred over nothing
        Assert.True(u.FailingLayer() == 3);
        Assert.True(u.Check());
        Assert.True(u.Fallen);
    }

    [Fact]
    public void TwoSideBlocksHoldLikeAFullLayer()
    {
        var t = Tower();
        t.Take(4, 1);
        t.Place(0);
        Assert.Equal(-1, t.FailingLayer());
        Assert.True(t.SwayRoom() > 0.5);
    }

    [Fact]
    public void AWeakTowerHasLessRoomToSway()
    {
        var t = Tower();
        double full = t.SwayRoom();
        t.Take(2, 0); // layer 2 now spans blocks 1-2 only
        t.Place(1);
        Assert.True(t.SwayRoom() < full);
    }

    [Fact]
    public void AnOffCentreTopPullsTheWeightOver()
    {
        // blocks laid only on the left of new layers pull the centre of mass left over thinned layers below
        var t = Tower();
        t.Take(5, 2);
        t.Place(0);
        t.Take(6, 2);
        Assert.True(t.Place(1)); // the new top is [x, x, _]: its weight sits left of centre
        Assert.Equal(-1, t.FailingLayer());
        Assert.True(t.SwayRoom() < Tower().SwayRoom());
    }

    [Fact]
    public void ABlockLeftWithFewerNeighboursSitsTighter()
    {
        var t = Tower();
        double before = t.Tightness(5, 0);
        t.Take(5, 1); // the middle goes: the two side blocks now share the weight
        Assert.True(t.Tightness(5, 0) > before);
        Assert.All(Enumerable.Range(0, t.Height), l => Assert.All(Enumerable.Range(0, 3), s => Assert.InRange(t.Tightness(l, s), 0, 1)));
    }

    [Fact]
    public void AStrongSwayBringsItDown()
    {
        var t = Tower();
        Assert.Equal(-1, t.FailingLayer(0.5));
        Assert.NotEqual(-1, t.FailingLayer(4));
        Assert.True(t.Check(4));
    }

    [Fact]
    public void CarefulPlayGoesOnForManyMoves()
    {
        // take middles first, then sides from layers whose middle stays, always laying blocks in the middle first
        var t = Tower();
        var rng = new Random(3);
        for (int move = 0; move < 30 && !t.Fallen; move++)
        {
            var options = Enumerable.Range(0, t.Height).SelectMany(l => Enumerable.Range(0, 3).Select(s => (l, s)))
                .Where(x => t.Takeable(x.l, x.s))
                .Where(x =>
                {
                    var copy = t.Clone();
                    copy.Take(x.l, x.s);
                    return copy.FailingLayer() < 0;
                }).ToList();
            if (options.Count == 0) break;
            var (l, s) = options[rng.Next(options.Count)];
            t.Take(l, s);
            var open = t.OpenSlots();
            t.Place(open.Contains(1) ? 1 : open[0]);
            t.Check();
        }
        Assert.True(t.Moved >= 15, $"{t.Moved} moves");
    }
}
