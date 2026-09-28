using System;
using System.Linq;
using DeskArcade.Games;
using Xunit;

namespace DeskArcade.Tests;

public class DominoTests
{
    static DominoRules Dealt(int seed = 1, int opener = 0)
    {
        var g = new DominoRules();
        g.NewMatch();
        g.Deal(seed, opener);
        return g;
    }

    static int Tiles(DominoRules g) => g.Hand(0).Count + g.Hand(1).Count + g.Boneyard + g.Line.Count;

    [Fact]
    public void ADoubleSixSetHas28TilesAnd168Pips()
    {
        Assert.Equal(28, Domino.Set.Count);
        Assert.Equal(28, Domino.Set.Distinct().Count());
        Assert.Equal(168, Domino.Set.Sum(t => t.Pips));
        Assert.Equal(new Domino(2, 5), new Domino(5, 2));
    }

    [Fact]
    public void EachTakesSevenAndTheRestIsTheBoneyard()
    {
        var g = Dealt();
        Assert.Equal(7, g.Hand(0).Count);
        Assert.Equal(7, g.Hand(1).Count);
        Assert.Equal(14, g.Boneyard);
        Assert.Equal(28, g.Hand(0).Concat(g.Hand(1)).Distinct().Count() + g.Boneyard);
        var again = Dealt();
        Assert.Equal(g.Hand(0), again.Hand(0)); // the same seed deals the same on both screens
    }

    [Fact]
    public void TilesMustMatchAnOpenEnd()
    {
        var g = Dealt();
        var first = g.Hand(0)[0];
        Assert.True(g.Play(first, atLeft: true));
        Assert.Equal(first.A, g.LeftEnd);
        Assert.Equal(first.B, g.RightEnd);
        Assert.Equal(1, g.Turn);
        var wrong = g.Hand(1).FirstOrDefault(t => !t.Has(g.LeftEnd) && !t.Has(g.RightEnd));
        if (wrong != default) Assert.False(g.Play(wrong, atLeft: true));
        Assert.False(g.Play(g.Hand(0)[0], atLeft: true)); // not their tile, not their turn
    }

    [Fact]
    public void TheLineGrowsBothWaysWithMatchingHalves()
    {
        var rng = new Random(3);
        var g = Dealt(5);
        for (int i = 0; i < 12 && !g.RoundOver; i++)
        {
            if (g.MustDraw) g.Draw();
            else if (g.MustPass) g.Pass();
            else
            {
                var t = g.Playable(g.Turn).First();
                var (l, _) = g.Fits(t);
                g.Play(t, l);
            }
            for (int k = 1; k < g.Line.Count; k++) Assert.Equal(g.Line[k - 1].R, g.Line[k].L);
            Assert.Equal(28, Tiles(g));
        }
        Assert.InRange(g.FirstIndex, 0, Math.Max(0, g.Line.Count - 1));
    }

    [Fact]
    public void YouDrawOnlyWhenNothingFitsAndPassOnlyWhenTheBoneyardIsEmpty()
    {
        var g = Dealt(2);
        g.Play(g.Hand(0)[0], true);
        if (g.CanPlay(1))
        {
            Assert.False(g.MustDraw);
            Assert.Null(g.Draw());
            Assert.False(g.Pass());
        }
        // play on until someone must draw
        for (int guard = 0; guard < 100 && !g.RoundOver && !g.MustDraw; guard++)
        {
            var t = g.Playable(g.Turn).FirstOrDefault();
            if (t == default) break;
            g.Play(t, g.Fits(t).Left);
        }
        if (g.MustDraw)
        {
            int before = g.Hand(g.Turn).Count, yard = g.Boneyard;
            Assert.NotNull(g.Draw());
            Assert.Equal(before + 1, g.Hand(g.Turn).Count);
            Assert.Equal(yard - 1, g.Boneyard);
            Assert.False(g.Pass());
        }
    }

    [Fact]
    public void GoingOutScoresTheOtherHand()
    {
        var rng = new Random(7);
        for (int seed = 0; seed < 40; seed++)
        {
            var g = Dealt(seed);
            while (!g.RoundOver)
            {
                if (g.MustDraw) g.Draw();
                else if (g.MustPass) g.Pass();
                else
                {
                    var m = g.BestMove(1, rng)!.Value;
                    Assert.True(g.Play(m.Tile, m.AtLeft));
                }
            }
            Assert.Equal(28, Tiles(g));
            if (g.WentOut)
            {
                Assert.Empty(g.Hand(g.RoundWinner));
                Assert.Equal(g.HandPips(1 - g.RoundWinner), g.RoundPoints);
                Assert.Equal(g.RoundPoints, g.Score(g.RoundWinner));
            }
            else
            {
                Assert.Equal(0, g.Boneyard); // blocked only once the boneyard is empty
                if (g.RoundWinner >= 0) Assert.True(g.HandPips(g.RoundWinner) < g.HandPips(1 - g.RoundWinner));
            }
        }
    }

    [Fact]
    public void AMatchGoesToFifty()
    {
        var rng = new Random(9);
        var g = new DominoRules();
        g.NewMatch();
        int rounds = 0;
        while (!g.MatchOver && rounds < 60)
        {
            g.Deal(rng.Next(), rounds % 2);
            rounds++;
            while (!g.RoundOver)
            {
                if (g.MustDraw) g.Draw();
                else if (g.MustPass) g.Pass();
                else
                {
                    var m = g.BestMove(2, rng)!.Value;
                    g.Play(m.Tile, m.AtLeft);
                }
            }
        }
        Assert.True(g.MatchOver);
        Assert.True(g.Score(g.MatchWinner) >= DominoRules.MatchTo);
        Assert.Equal(rounds, g.Round);
    }

    [Fact]
    public void TheHardComputerBeatsRandomPlay()
    {
        var rng = new Random(13);
        int cpuPoints = 0, randomPoints = 0;
        for (int round = 0; round < 60; round++)
        {
            var g = Dealt(rng.Next(), round % 2);
            while (!g.RoundOver)
            {
                if (g.MustDraw) g.Draw();
                else if (g.MustPass) g.Pass();
                else
                {
                    var m = g.BestMove(g.Turn == 0 ? 3 : 1, rng)!.Value;
                    g.Play(m.Tile, m.AtLeft);
                }
            }
            cpuPoints += g.Score(0);
            randomPoints += g.Score(1);
        }
        Assert.True(cpuPoints > randomPoints * 1.2, $"{cpuPoints} vs {randomPoints}");
    }

    [Fact]
    public void TheExpertAlwaysHasALegalMoveWhenATileFits()
    {
        var g = Dealt(21);
        var rng = new Random(1);
        int guard = 0;
        while (!g.RoundOver && guard++ < 200)
        {
            if (g.MustDraw) g.Draw();
            else if (g.MustPass) g.Pass();
            else
            {
                var m = g.BestMove(4, rng);
                Assert.NotNull(m);
                Assert.True(g.Play(m!.Value.Tile, m.Value.AtLeft));
            }
        }
        Assert.True(g.RoundOver);
    }
}
