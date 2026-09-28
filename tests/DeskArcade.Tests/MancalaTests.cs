using System;
using System.Diagnostics;
using System.Linq;
using DeskArcade.Games;
using Xunit;

namespace DeskArcade.Tests;

public class MancalaTests
{
    static int Seeds(MancalaRules g) => Enumerable.Range(0, 14).Sum(i => g[i]);

    [Fact]
    public void ItStartsWithFourSeedsInEveryPit()
    {
        var g = new MancalaRules();
        Assert.Equal(MancalaRules.Total, Seeds(g));
        Assert.Equal(0, g.Score(0));
        Assert.Equal(0, g.Score(1));
        Assert.Equal(0, g.Turn);
        Assert.Equal(Enumerable.Range(0, 6), g.LegalPits());
    }

    [Fact]
    public void SowingDropsOneSeedInEachPitThatFollows()
    {
        var g = new MancalaRules();
        var s = g.Play(0)!;
        Assert.Equal(new[] { 1, 2, 3, 4 }, s.Drops);
        Assert.Equal(0, g[0]);
        Assert.Equal(5, g[4]);
        Assert.Equal(1, g.Turn);
        Assert.Equal(MancalaRules.Total, Seeds(g));
    }

    [Fact]
    public void TheLastSeedInYourStorePlaysAgain()
    {
        var g = new MancalaRules();
        var s = g.Play(2)!; // 4 seeds from pit 2: 3, 4, 5 and the store
        Assert.True(s.Again);
        Assert.Equal(0, g.Turn);
        Assert.Equal(1, g.Score(0));
    }

    [Fact]
    public void SowingSkipsTheOtherPlayersStore()
    {
        var g = new MancalaRules();
        g.Play(5);   // side 0: 4 seeds into its store and three of side 1's pits
        var s = g.Play(5)!; // side 1's last pit (index 12): 5 seeds into its store and round side 0's pits
        Assert.DoesNotContain(MancalaRules.Store(0), s.Drops);
        Assert.Equal(MancalaRules.Total, Seeds(g));
    }

    [Fact]
    public void ALastSeedInAnEmptyPitOfYoursCapturesTheOppositePit()
    {
        var g = new MancalaRules();
        g.Play(4);          // 0: pit 4 → 5, store, 7, 8; now side 1
        g.Play(0);          // 1: pit 7 (5 seeds) → 8..12; now side 0
        var s = g.Play(0)!; // 0: pit 0 (4 seeds) → 1, 2, 3, 4; pit 4 was emptied: capture its opposite (8)
        Assert.Equal(8, s.CapturedFrom);
        Assert.True(s.Captured > 1);
        Assert.Equal(0, g[4]);
        Assert.Equal(0, g[8]);
        Assert.Equal(MancalaRules.Total, Seeds(g));
    }

    [Fact]
    public void AnEmptyPitOrAFinishedGameTakesNoMove()
    {
        var g = new MancalaRules();
        g.Play(0);
        g.Play(0);
        Assert.Null(g.Play(0)); // side 0's pit 0 is empty now
        Assert.Null(g.Play(9));
    }

    [Fact]
    public void WhenOneSideRunsOutTheOtherKeepsItsSeedsAndTheGameEnds()
    {
        var rng = new Random(3);
        for (int game = 0; game < 50; game++)
        {
            var g = new MancalaRules();
            int moves = 0;
            while (!g.Over && moves++ < 500)
            {
                var legal = g.LegalPits().ToList();
                g.Play(legal[rng.Next(legal.Count)]);
                Assert.Equal(MancalaRules.Total, Seeds(g));
            }
            Assert.True(g.Over);
            Assert.Equal(MancalaRules.Total, g.Score(0) + g.Score(1)); // every seed ends in a store
            Assert.Equal(g.Score(0) > g.Score(1) ? 0 : g.Score(1) > g.Score(0) ? 1 : -1, g.Winner);
            Assert.Empty(g.LegalPits());
        }
    }

    [Fact]
    public void TheComputerTakesAFreeTurnWhenItCan()
    {
        var g = new MancalaRules();
        Assert.Equal(2, g.BestPit(1, new Random(1))); // only pit 2 lands in the store
    }

    [Fact]
    public void StrongerLevelsBeatWeakerOnes()
    {
        var rng = new Random(9);
        int deepWins = 0, shallowWins = 0;
        for (int game = 0; game < 20; game++)
        {
            var g = new MancalaRules();
            int deepSide = game % 2;
            while (!g.Over) g.Play(g.BestPit(g.Turn == deepSide ? 6 : 1, rng));
            if (g.Winner == deepSide) deepWins++;
            else if (g.Winner >= 0) shallowWins++;
        }
        Assert.True(deepWins > shallowWins * 2, $"deep {deepWins}, shallow {shallowWins}");
    }

    [Fact]
    public void TheExpertThinksQuickly()
    {
        var g = new MancalaRules();
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 6 && !g.Over; i++) g.Play(g.BestPit(MancalaRules.Depths[^1], new Random(i)));
        Assert.True(watch.ElapsedMilliseconds < 900, $"{watch.ElapsedMilliseconds} ms for six expert moves"); // it thinks on the UI thread
    }

    [Fact]
    public void OppositePitsFaceEachOther()
    {
        Assert.Equal(12, MancalaRules.Opposite(0));
        Assert.Equal(7, MancalaRules.Opposite(5));
        Assert.Equal(0, MancalaRules.SideOf(5));
        Assert.Equal(1, MancalaRules.SideOf(7));
        Assert.Equal(9, MancalaRules.PitOf(1, 2));
    }
}
