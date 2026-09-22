using System;
using DeskArcade.Engine;
using DeskArcade.Games;
using Xunit;

namespace DeskArcade.Tests;

public class DartsBoardTests
{
    const double R = 170; // one unit per millimetre

    static Segment At(double mm, double clockwiseDeg)
    {
        double a = clockwiseDeg * Math.PI / 180;
        return DartsRules.Score(new Vec2(Math.Sin(a) * mm, -Math.Cos(a) * mm), R);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(90, 6)]
    [InlineData(180, 3)]
    [InlineData(270, 11)]
    [InlineData(18, 1)]
    [InlineData(-18, 5)]
    [InlineData(8.9, 20)]
    [InlineData(9.1, 1)]
    public void NumbersSitWhereTheyDoOnARealBoard(double deg, int number)
    {
        var s = At(130, deg);
        Assert.Equal(number, s.Number);
        Assert.Equal(1, s.Multiplier);
    }

    [Fact]
    public void TheRingsFollowTheRegulationRadii()
    {
        Assert.Equal(Segment.Bull, At(0, 0));
        Assert.Equal(Segment.Bull, At(6.3, 40));
        Assert.Equal(Segment.OuterBull, At(6.5, 40));
        Assert.Equal(Segment.OuterBull, At(15.8, 200));
        Assert.Equal(Segment.Single(20), At(16.1, 0));
        Assert.Equal(Segment.Single(20), At(98.8, 0));
        Assert.Equal(Segment.Treble(20), At(99.2, 0));
        Assert.Equal(Segment.Treble(20), At(106.8, 0));
        Assert.Equal(Segment.Single(20), At(107.2, 0));
        Assert.Equal(Segment.Single(3), At(161.8, 180));
        Assert.Equal(Segment.Double(3), At(162.2, 180));
        Assert.Equal(Segment.Double(3), At(169.8, 180));
        Assert.Equal(Segment.Miss, At(170.2, 180));
        Assert.Equal(Segment.Miss, At(400, 90));
    }

    [Fact]
    public void ScoresScaleWithTheBoard()
    {
        Assert.Equal(Segment.Treble(6), DartsRules.Score(new Vec2(103.0 / 170 * 0.5, 0), 0.5));
        Assert.Equal(60, Segment.Treble(20).Points);
        Assert.Equal(50, Segment.Bull.Points);
        Assert.True(Segment.Bull.IsDouble);
        Assert.False(Segment.OuterBull.IsDouble);
    }

    [Fact]
    public void EveryAimPointScoresItsOwnSegment()
    {
        foreach (int n in DartsRules.Order)
            foreach (var s in new[] { Segment.Single(n), Segment.Double(n), Segment.Treble(n) })
                Assert.Equal(s, DartsRules.Score(DartsRules.AimPoint(s, R), R));
        Assert.Equal(Segment.Bull, DartsRules.Score(DartsRules.AimPoint(Segment.Bull, R), R));
        Assert.Equal(Segment.OuterBull, DartsRules.Score(DartsRules.AimPoint(Segment.OuterBull, R), R));
    }
}

public class X01Tests
{
    static X01 Leg(int remaining)
    {
        var leg = new X01(remaining);
        return leg;
    }

    [Fact]
    public void OvershootingIsABustBackToTheStartOfTheTurn()
    {
        var leg = Leg(50);
        Assert.False(leg.Throw(Segment.Single(20)).Bust); // 30 left
        var r = leg.Throw(Segment.Treble(20));
        Assert.True(r.Bust);
        Assert.True(r.TurnOver);
        Assert.Equal(50, leg.Remaining);
        Assert.Equal(0, leg.DartsInTurn);
        Assert.Equal(2, leg.DartsUsed);
    }

    [Fact]
    public void LeavingOneIsABust()
    {
        var leg = Leg(41);
        var r = leg.Throw(Segment.Double(20));
        Assert.True(r.Bust);
        Assert.Equal(41, leg.Remaining);
    }

    [Fact]
    public void FinishingOnASingleIsABust()
    {
        var leg = Leg(20);
        var r = leg.Throw(Segment.Single(20));
        Assert.True(r.Bust);
        Assert.False(leg.Finished);
        Assert.Equal(20, leg.Remaining);
    }

    [Fact]
    public void ADoubleFinishesTheLeg()
    {
        var leg = new X01();
        for (int turn = 0; turn < 2; turn++)
            for (int d = 0; d < 3; d++) leg.Throw(Segment.Treble(20)); // 141 left after two 180s
        Assert.Equal(141, leg.Remaining);
        Assert.False(leg.Throw(Segment.Treble(20)).Checkout);
        Assert.False(leg.Throw(Segment.Treble(19)).Checkout);
        var r = leg.Throw(Segment.Double(12));
        Assert.True(r.Checkout);
        Assert.True(leg.Finished);
        Assert.Equal(9, leg.DartsUsed);
        Assert.Equal(0, leg.Remaining);
        Assert.Throws<InvalidOperationException>(() => leg.Throw(Segment.Single(1)));
    }

    [Fact]
    public void TheBullCountsAsADoubleButTheOuterBullDoesNot()
    {
        var leg = Leg(50);
        Assert.True(leg.Throw(Segment.Bull).Checkout);

        var outer = Leg(25);
        Assert.True(outer.Throw(Segment.OuterBull).Bust);
    }

    [Fact]
    public void ThreeTreble20sMakeA180AndEndTheTurn()
    {
        var leg = new X01();
        leg.Throw(Segment.Treble(20));
        leg.Throw(Segment.Treble(20));
        var r = leg.Throw(Segment.Treble(20));
        Assert.True(r.TurnOver);
        Assert.Equal(180, r.TurnTotal);
        Assert.Equal(321, leg.TurnStart);
    }

    [Fact]
    public void NewLegResetsEverything()
    {
        var leg = Leg(2);
        leg.Throw(Segment.Double(1));
        leg.NewLeg();
        Assert.Equal(2, leg.Remaining);
        Assert.False(leg.Finished);
        Assert.Equal(0, leg.DartsUsed);
    }
}

public class CheckoutTests
{
    static string? Route(int remaining, int darts = 3) =>
        DartsRules.Checkout(remaining, darts) is { } r ? DartsRules.Describe(r) : null;

    [Theory]
    [InlineData(170, "T20 T20 BULL")]
    [InlineData(40, "D20")]
    [InlineData(32, "D16")]
    [InlineData(50, "BULL")]
    [InlineData(3, "1 D1")]
    [InlineData(121, "T20 11 BULL")]
    [InlineData(100, "T20 D20")]
    public void SuggestsTheUsualRoute(int remaining, string route) => Assert.Equal(route, Route(remaining));

    [Theory]
    [InlineData(169)]
    [InlineData(168)]
    [InlineData(166)]
    [InlineData(1)]
    [InlineData(171)]
    public void SomeScoresHaveNoCheckout(int remaining) => Assert.Null(Route(remaining));

    [Fact]
    public void TheDartsLeftInTheTurnLimitTheRoute()
    {
        Assert.Null(Route(100, 1));
        Assert.Equal("T20 D20", Route(100, 2));
        Assert.Null(Route(170, 2));
    }

    [Fact]
    public void EverySuggestedRouteReallyFinishes()
    {
        for (int n = 2; n <= 170; n++)
        {
            var route = DartsRules.Checkout(n);
            if (route == null) continue;
            var leg = new X01(n);
            DartResult last = default;
            foreach (var s in route) last = leg.Throw(s);
            Assert.True(last.Checkout, $"{n}: {DartsRules.Describe(route)}");
        }
    }

    [Fact]
    public void TheDemoFinishesLegsInAReasonableNumberOfDarts()
    {
        var rng = new Random(7);
        const double r = 170, sigma = 0.05 * r + 0.012 * r;
        int total = 0;
        for (int legNo = 0; legNo < 30; legNo++)
        {
            var leg = new X01();
            while (!leg.Finished)
            {
                Assert.True(leg.DartsUsed < 200, "the demo never finishes");
                var target = DartsRules.DemoTarget(leg.Remaining, leg.DartsLeft);
                var hit = DartsRules.AimPoint(target, r) + DartsRules.Scatter(rng, sigma);
                leg.Throw(DartsRules.Score(hit, r));
            }
            total += leg.DartsUsed;
        }
        double average = total / 30.0;
        Assert.InRange(average, 12, 60);
    }
}
