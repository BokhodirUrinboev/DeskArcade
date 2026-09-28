using System;
using System.Linq;
using Avalonia;
using DeskArcade.Engine;
using DeskArcade.Games;
using Xunit;

namespace DeskArcade.Tests;

public class AsteroidsTests
{
    static readonly Rect Box = new(0, 0, 1920, 1040);

    static AsteroidsRules Game(int ships = 1, int seed = 1)
    {
        var g = new AsteroidsRules(Box, ships, new Random(seed));
        foreach (var s in g.Ships) s.Pos = new Vec2(960, 900);
        g.Step(0.01); // the first wave comes in
        return g;
    }

    /// <summary>Flies the ship close to <paramref name="rock"/>, fires with a lead, and runs until a rock breaks (or a second passes).</summary>
    static void ShootAt(AsteroidsRules g, Rock rock, int ship = 0)
    {
        var s = g.Ships[ship];
        s.Invulnerable = 99; // keep the test about the shot
        int before = g.Destroyed;
        for (int i = 0; i < 100; i++)
        {
            var below = rock.Pos + new Vec2(0, rock.Radius + 90);
            s.Pos = new Vec2(Math.Clamp(below.X, 20, Box.Right - 20), Math.Clamp(below.Y, 20, Box.Bottom - 20));
            var aim = rock.Pos + rock.Vel * ((rock.Pos - s.Pos).Length / AsteroidsRules.ShotSpeed);
            if (g.Fire(ship, aim)) break;
            g.Step(0.01); // the gun is still cooling
        }
        for (int i = 0; i < 120 && g.Destroyed == before; i++) g.Step(1.0 / 120);
    }

    [Fact]
    public void TheFirstWaveComesInFromTheEdgesAwayFromTheShip()
    {
        var g = Game();
        Assert.Equal(1, g.Wave);
        Assert.Equal(3, g.Rocks.Count);
        Assert.All(g.Rocks, r => Assert.Equal(3, r.Size));
        Assert.All(g.Rocks, r => Assert.True((r.Pos - g.Ships[0].Pos).Length > 200));
        Assert.Equal(AsteroidsRules.StartLives, g.Lives);
    }

    [Fact]
    public void CoopHasTwoShipsAndMoreLives()
    {
        var g = Game(ships: 2);
        Assert.Equal(2, g.Ships.Length);
        Assert.Equal(AsteroidsRules.CoopLives, g.Lives);
    }

    [Fact]
    public void RocksStayInsideTheScreen()
    {
        var g = Game();
        g.Ships[0].Invulnerable = 1000;
        for (int i = 0; i < 60 * 30; i++)
        {
            g.Step(1.0 / 60);
            Assert.All(g.Rocks, r => Assert.True(Box.Inflate(1).Contains(r.Pos.ToPoint())));
        }
    }

    [Fact]
    public void AShotSplitsABigRockIntoTwoMediumOnes()
    {
        var g = Game();
        var big = g.Rocks[0];
        ShootAt(g, big);
        Assert.DoesNotContain(big, g.Rocks);
        Assert.Equal(2, g.Rocks.Count(r => r.Size == 2));
        Assert.Equal(AsteroidsRules.PointsOf(3), g.Score);
    }

    [Fact]
    public void SmallRocksAreDustAndScoreTheMost()
    {
        Assert.True(AsteroidsRules.PointsOf(1) > AsteroidsRules.PointsOf(2));
        Assert.True(AsteroidsRules.PointsOf(2) > AsteroidsRules.PointsOf(3));
        var g = Game();
        for (int round = 0; round < 60 && g.Rocks.Count > 0 && g.Wave == 1; round++) ShootAt(g, g.Rocks.OrderBy(r => r.Size).First());
        Assert.True(g.Destroyed >= 3 + 6 + 12 || g.Wave > 1, $"destroyed {g.Destroyed}");
    }

    [Fact]
    public void TheGunCoolsBetweenShots()
    {
        var g = Game();
        Assert.True(g.Fire(0, new Vec2(0, 0)));
        Assert.False(g.Fire(0, new Vec2(0, 0)));
        g.Step(AsteroidsRules.FireEvery + 0.01);
        Assert.True(g.Fire(0, new Vec2(0, 0)));
        Assert.False(g.Fire(5, new Vec2(0, 0))); // no such ship
    }

    [Fact]
    public void ShotsFadeAndLeaveTheScreen()
    {
        var g = Game();
        g.Ships[0].Invulnerable = 99;
        g.Fire(0, g.Ships[0].Pos + new Vec2(0, -500));
        Assert.Single(g.Shots);
        for (int i = 0; i < 200; i++) g.Step(1.0 / 60);
        Assert.Empty(g.Shots);
    }

    [Fact]
    public void ARockOnTheShipCostsALifeAndAMomentToGetClear()
    {
        var g = Game();
        g.Ships[0].Invulnerable = 0;
        g.Ships[0].Pos = g.Rocks[0].Pos;
        g.Step(0.001);
        Assert.Equal(AsteroidsRules.StartLives - 1, g.Lives);
        Assert.Equal(new[] { 0 }, g.ShipsHit);
        Assert.True(g.Ships[0].Invulnerable > 0);
        Assert.Equal(0, g.Score); // a crash scores nothing
        g.Ships[0].Pos = g.Rocks[0].Pos;
        g.Step(0.001);
        Assert.Equal(AsteroidsRules.StartLives - 1, g.Lives); // still blinking: no second hit
    }

    [Fact]
    public void OutOfLivesTheGameIsOver()
    {
        var g = Game();
        for (int i = 0; i < AsteroidsRules.StartLives; i++)
        {
            g.Ships[0].Invulnerable = 0;
            g.Ships[0].Pos = g.Rocks[0].Pos;
            g.Step(0.001);
        }
        Assert.True(g.Over);
        Assert.False(g.Fire(0, Vec2Zero));
    }

    static readonly Vec2 Vec2Zero = new(0, 0);

    [Fact]
    public void ClearingAWaveBringsABiggerFasterOneAfterABreather()
    {
        var g = Game();
        double speed = g.Rocks.Average(r => r.Vel.Length);
        while (g.Rocks.Count > 0) ShootAt(g, g.Rocks[0]);
        Assert.Equal(1, g.Wave);
        g.Step(AsteroidsRules.Breather / 2);
        Assert.Empty(g.Rocks); // still the breather
        g.Step(AsteroidsRules.Breather);
        Assert.Equal(2, g.Wave);
        Assert.Equal(4, g.Rocks.Count);
        Assert.True(g.Rocks.Average(r => r.Vel.Length) > speed * 0.9);
    }

    [Fact]
    public void TheCoWorkersShotsScoreForTheTeam()
    {
        var g = Game(ships: 2);
        g.Ships[1].Pos = new Vec2(300, 900);
        ShootAt(g, g.Rocks[0], ship: 1);
        Assert.Equal(AsteroidsRules.PointsOf(3), g.Score);
    }

    [Fact]
    public void ThereAreNeverTooManyRocks()
    {
        var g = Game();
        g.Ships[0].Invulnerable = 1e9;
        for (int i = 0; i < 2000 && g.Rocks.Count > 0; i++)
        {
            var r = g.Rocks[i % g.Rocks.Count];
            g.Fire(0, r.Pos);
            g.Step(1.0 / 30);
            Assert.True(g.Rocks.Count <= AsteroidsRules.MaxRocks);
        }
    }
}
