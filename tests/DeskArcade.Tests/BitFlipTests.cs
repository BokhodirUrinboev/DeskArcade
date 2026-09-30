using System;
using System.Linq;
using DeskArcade.Games;
using Xunit;

namespace DeskArcade.Tests;

public class BitFlipTests
{
    /// <summary>A game with its first number in the well.</summary>
    static BitFlipRules Started(int seed = 3)
    {
        var r = new BitFlipRules(new Random(seed));
        r.Step(1);
        Assert.NotNull(r.Lowest);
        return r;
    }

    /// <summary>Flips the bits that make the lowest number; returns what cleared.</summary>
    static BitTarget? Make(BitFlipRules r)
    {
        BitTarget? cleared = null;
        foreach (int b in BitFlipRules.Differences(r.Value, r.Lowest!.Value, r.Bits).ToList()) cleared = r.Flip(b);
        return cleared;
    }

    [Fact]
    public void FlippingTheBitsOfTheLowestNumberClearsItAndResetsTheBits()
    {
        var r = Started();
        var low = r.Lowest!;
        var cleared = Make(r);
        Assert.Same(low, cleared);
        Assert.Equal(0, r.Value);
        Assert.Equal(1, r.Cleared);
        Assert.True(r.Score > 0);
        Assert.DoesNotContain(low, r.Targets);
    }

    [Fact]
    public void AFlipTogglesOneBitAndOnlyTheBitsTheLevelHas()
    {
        var r = new BitFlipRules(new Random(1));
        r.Flip(2);
        Assert.Equal(4, r.Value);
        r.Flip(0);
        Assert.Equal(5, r.Value);
        r.Flip(2);
        Assert.Equal(1, r.Value);
        Assert.Null(r.Flip(r.Bits)); // no such bit at level 1
        Assert.Equal(1, r.Value);
        r.Reset();
        Assert.Equal(0, r.Value);
    }

    [Fact]
    public void ANumberThatLandsCostsALifeAndThreeEndTheGame()
    {
        var r = Started();
        int landed = 0;
        for (int i = 0; i < 400 && !r.Over; i++) landed += r.Step(0.25).Count;
        Assert.True(r.Over);
        Assert.Equal(BitFlipRules.Lives, landed);
        Assert.Equal(0, r.LivesLeft);
        Assert.Empty(r.Step(1)); // nothing moves after the end
    }

    [Fact]
    public void TargetsAreNeverZeroNeverTooBigAndNeverTwiceInTheWell()
    {
        var r = new BitFlipRules(new Random(9));
        for (int i = 0; i < 600 && !r.Over; i++)
        {
            r.Step(0.2);
            Assert.All(r.Targets, t => Assert.InRange(t.Value, 1, (1 << r.Bits) - 1));
            Assert.Equal(r.Targets.Count, r.Targets.Select(t => t.Value).Distinct().Count());
            if (i % 3 == 0 && r.Lowest != null) Make(r);
        }
    }

    [Fact]
    public void LevelsGoUpEveryFewClearedFromFourBitsToSixteenWithHexOnTheWay()
    {
        Assert.Equal(4, BitFlipRules.Levels[0].Bits);
        Assert.Equal(16, BitFlipRules.Levels[^1].Bits);
        Assert.Equal(0, BitFlipRules.Levels.TakeWhile(l => l.Bits < 8).Sum(l => l.HexShare)); // hex only once there are eight bits
        for (int i = 1; i < BitFlipRules.Levels.Count; i++)
        {
            Assert.True(BitFlipRules.Levels[i].Bits >= BitFlipRules.Levels[i - 1].Bits);
            Assert.Equal(i + 1, BitFlipRules.Levels[i].Number);
        }

        // a perfect player climbs every level
        var r = new BitFlipRules(new Random(4));
        for (int i = 0; i < 20000 && r.Level < BitFlipRules.Levels.Count; i++)
        {
            r.Step(0.05);
            if (r.Lowest != null) Make(r);
        }
        Assert.Equal(BitFlipRules.Levels.Count, r.Level);
        Assert.Equal(16, r.Bits);
        Assert.Equal(BitFlipRules.Lives, r.LivesLeft);
        Assert.Equal((BitFlipRules.Levels.Count - 1) * BitFlipRules.PerLevel, r.Cleared);
    }

    [Fact]
    public void EveryLevelLeavesTimeToFlipAllItsBits()
    {
        // at worst every bit flips; a quick player manages about three a second, and several numbers share the well
        foreach (var level in BitFlipRules.Levels)
        {
            Assert.True(level.FallSeconds >= level.Bits / 3.0 + 3, $"level {level.Number}");
            Assert.True(level.Every >= level.Bits / 5.0, $"level {level.Number}");
        }
    }

    [Theory]
    [InlineData(1, false, 1.0, 10)]
    [InlineData(1, false, 0.0, 20)]
    [InlineData(3, false, 0.5, 30)]
    [InlineData(5, true, 1.0, 45)]
    public void PointsGrowWithTheLevelHexAndCatchingItHigh(int level, bool hex, double y, int points) =>
        Assert.Equal(points, BitFlipRules.Points(level, hex, y));

    [Theory]
    [InlineData(42, false, 8, "42")]
    [InlineData(42, true, 8, "0x2A")]
    [InlineData(5, true, 12, "0x005")]
    [InlineData(65535, true, 16, "0xFFFF")]
    public void NumbersReadInDecimalOrHexWithAllTheirDigits(int value, bool hex, int bits, string text) =>
        Assert.Equal(text, new BitTarget { Value = value, Hex = hex }.Text(bits));

    [Fact]
    public void TheSameSeedDealsTheSameNumbers()
    {
        var a = new BitFlipRules(new Random(77));
        var b = new BitFlipRules(new Random(77));
        for (int i = 0; i < 60; i++)
        {
            a.Step(0.3);
            b.Step(0.3);
        }
        Assert.Equal(a.Targets.Select(t => (t.Value, t.Hex)), b.Targets.Select(t => (t.Value, t.Hex)));
    }
}
