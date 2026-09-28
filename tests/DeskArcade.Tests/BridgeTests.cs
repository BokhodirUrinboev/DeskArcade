using System;
using System.Linq;
using DeskArcade.Games;
using Xunit;

namespace DeskArcade.Tests;

public class BridgeTests
{
    static RopeBridge Bridge() => new(400, 760, 36, new Random(1), from: 150);

    static void Run(RopeBridge b, double seconds, Action? each = null)
    {
        for (double t = 0; t < seconds; t += 1 / 60.0)
        {
            each?.Invoke();
            b.Step(1 / 60.0);
        }
    }

    [Fact]
    public void TheGapIsCutIntoPlankPlaces()
    {
        var b = Bridge();
        Assert.Equal(10, b.Slots);
        Assert.Equal(36, b.SlotW, 6);
        Assert.Equal(-1, b.SlotAt(399));
        Assert.Equal(0, b.SlotAt(400));
        Assert.Equal(9, b.SlotAt(759));
        Assert.Equal(-1, b.SlotAt(760));
        Assert.All(Enumerable.Range(0, b.Slots), s => Assert.False(b.HasPlank(s)));
    }

    [Fact]
    public void AnInternWithNoPlankUnderfootFalls()
    {
        var b = Bridge();
        var events = Enumerable.Range(0, 60 * 12).SelectMany(_ => b.Step(1 / 60.0)).ToList();
        Assert.Contains(events, e => e.Kind == "fell" && e.Slot == 0);
        Assert.True(b.Lost >= 1);
        Assert.Equal(0, b.Saved);
    }

    [Fact]
    public void AFullBridgeGetsThemAcross()
    {
        var b = Bridge();
        for (int s = 0; s < b.Slots; s++)
        {
            if (b.Pile == 0) Run(b, RopeBridge.RefillEvery + 0.01);
            Assert.True(b.Lay(s, wear: 99));
        }
        Run(b, 30);
        Assert.True(b.Saved >= 3, $"{b.Saved} saved");
        Assert.Equal(0, b.Lost);
    }

    [Fact]
    public void AWornPlankSnapsOnTheNextCrossing()
    {
        var b = Bridge();
        for (int s = 0; s < b.Slots; s++)
        {
            if (b.Pile == 0) Run(b, RopeBridge.RefillEvery + 0.01);
            b.Lay(s, wear: s == 4 ? 1 : 99);
        }
        // the first intern wears it down to nothing, the second goes through it
        var events = Enumerable.Range(0, 60 * 30).SelectMany(_ => b.Step(1 / 60.0)).ToList();
        Assert.Contains(events, e => e.Kind == "snap" && e.Slot == 4);
        Assert.False(b.HasPlank(4));
        Assert.True(b.Saved >= 1);
    }

    [Fact]
    public void APlankCannotBePulledFromUnderSomeone()
    {
        var b = Bridge();
        for (int s = 0; s < b.Slots; s++)
        {
            if (b.Pile == 0) Run(b, RopeBridge.RefillEvery + 0.01);
            b.Lay(s, wear: 99);
        }
        Run(b, RopeBridge.RefillEvery * 3);
        var walker = b.Walkers.First(w => !w.Gone);
        while (walker.Slot < 0) b.Step(1 / 60.0);
        Assert.False(b.Lay(walker.Slot));
    }

    [Fact]
    public void ThePileRefillsUpToItsLimit()
    {
        var b = Bridge();
        Assert.Equal(RopeBridge.PileMax, b.Pile);
        b.Lay(0);
        b.Lay(1);
        Assert.Equal(RopeBridge.PileMax - 2, b.Pile);
        Run(b, RopeBridge.RefillEvery * 5);
        Assert.Equal(RopeBridge.PileMax, b.Pile);
        Assert.Equal(2, b.Laid);
    }

    [Fact]
    public void ARoundIsFifteenInternsAndKeepingTheBridgeMendedSavesThem()
    {
        var b = Bridge();
        // a keeper who fills any empty or worn-out place that nobody stands on
        for (double t = 0; t < 120 && !b.Over; t += 1 / 60.0)
        {
            for (int s = 0; s < b.Slots; s++)
                if (b.Wear(s) <= 0 && b.Pile > 0) b.Lay(s);
            b.Step(1 / 60.0);
        }
        Assert.True(b.Over);
        Assert.Equal(RopeBridge.RoundInterns, b.Saved + b.Lost);
        Assert.True(b.Saved >= 13, $"{b.Saved} saved");
    }
}
