using System;
using System.Linq;
using DeskArcade.Games;
using Xunit;

namespace DeskArcade.Tests;

public class MemoryRulesTests
{
    static MemoryRules Deal(int seed = 1) => new(new Random(seed));

    /// <summary>Two different cards with the same face, and one card with another face.</summary>
    static (int a, int b, int other) Pick(MemoryRules m)
    {
        int a = 0;
        int b = Enumerable.Range(1, m.Count - 1).First(k => m.FaceOf(k) == m.FaceOf(a));
        int other = Enumerable.Range(1, m.Count - 1).First(k => m.FaceOf(k) != m.FaceOf(a));
        return (a, b, other);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(123)]
    public void EveryFaceAppearsExactlyTwice(int seed)
    {
        var m = Deal(seed);
        Assert.Equal(24, m.Count);
        var counts = Enumerable.Range(0, m.Count).GroupBy(m.FaceOf).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(12, counts.Count);
        Assert.All(counts.Values, c => Assert.Equal(2, c));
    }

    [Fact]
    public void TheShuffleDependsOnTheSeed()
    {
        var a = Deal(1);
        var b = Deal(2);
        Assert.NotEqual(Enumerable.Range(0, 24).Select(a.FaceOf), Enumerable.Range(0, 24).Select(b.FaceOf));
        var again = Deal(1);
        Assert.Equal(Enumerable.Range(0, 24).Select(a.FaceOf), Enumerable.Range(0, 24).Select(again.FaceOf));
    }

    [Fact]
    public void AMatchStaysUp()
    {
        var m = Deal();
        var (a, b, _) = Pick(m);
        Assert.Equal(MemoryRules.FlipResult.First, m.Flip(a));
        Assert.Equal(MemoryRules.FlipResult.Match, m.Flip(b));
        Assert.Equal(MemoryRules.Card.Matched, m.StateOf(a));
        Assert.Equal(MemoryRules.Card.Matched, m.StateOf(b));
        Assert.Equal(1, m.Found);
        Assert.False(m.Settle()); // nothing to turn back
        Assert.Equal(MemoryRules.Card.Matched, m.StateOf(a));
    }

    [Fact]
    public void AMismatchFlipsBack()
    {
        var m = Deal();
        var (a, _, other) = Pick(m);
        m.Flip(a);
        Assert.Equal(MemoryRules.FlipResult.Mismatch, m.Flip(other));
        Assert.Equal(MemoryRules.Card.Up, m.StateOf(a)); // both still shown during the pause
        Assert.Equal(MemoryRules.Card.Up, m.StateOf(other));
        Assert.True(m.Settle());
        Assert.Equal(MemoryRules.Card.Down, m.StateOf(a));
        Assert.Equal(MemoryRules.Card.Down, m.StateOf(other));
        Assert.Equal(0, m.Found);
        Assert.Equal(-1, m.Open);
    }

    [Fact]
    public void ThreeQuickClicksDoNotRevealThreeCards()
    {
        var m = Deal();
        var (a, _, other) = Pick(m);
        int third = Enumerable.Range(0, m.Count).First(k => k != a && k != other);
        m.Flip(a);
        m.Flip(other);
        Assert.Equal(MemoryRules.FlipResult.Ignored, m.Flip(third));
        Assert.Equal(2, Enumerable.Range(0, m.Count).Count(k => m.StateOf(k) != MemoryRules.Card.Down));
        Assert.Equal(1, m.Moves);
    }

    [Fact]
    public void ClickingAFaceUpCardDoesNothing()
    {
        var m = Deal();
        var (a, b, _) = Pick(m);
        m.Flip(a);
        Assert.Equal(MemoryRules.FlipResult.Ignored, m.Flip(a)); // the same card twice is not a pair
        Assert.Equal(a, m.Open);
        Assert.Equal(0, m.Moves);
        m.Flip(b);
        Assert.Equal(MemoryRules.FlipResult.Ignored, m.Flip(a)); // nor is a matched one
        Assert.Equal(-1, m.Open);
        Assert.Equal(1, m.Moves);
    }

    [Fact]
    public void MovesCountPairsOfFlips()
    {
        var m = Deal();
        var (a, b, other) = Pick(m);
        m.Flip(a);
        Assert.Equal(0, m.Moves); // one card is half a move
        m.Flip(other);
        Assert.Equal(1, m.Moves);
        m.Settle();
        m.Flip(a);
        m.Flip(b);
        Assert.Equal(2, m.Moves);
    }

    [Fact]
    public void FindingEveryPairWins()
    {
        var m = Deal(5);
        var byFace = Enumerable.Range(0, m.Count).GroupBy(m.FaceOf).Select(g => g.ToArray()).ToList();
        for (int i = 0; i < byFace.Count; i++)
        {
            Assert.False(m.Won);
            m.Flip(byFace[i][0]);
            Assert.Equal(MemoryRules.FlipResult.Match, m.Flip(byFace[i][1]));
        }
        Assert.True(m.Won);
        Assert.Equal(12, m.Moves); // a perfect game
        Assert.Equal(MemoryRules.FlipResult.Ignored, m.Flip(0));
    }
}
