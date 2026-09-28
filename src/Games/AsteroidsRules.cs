using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>A rock in Asteroids: big (3), medium (2) or small (1). Its id also seeds its jagged outline, the same on every screen.</summary>
public sealed class Rock
{
    public required int Id { get; init; }
    public required int Size { get; init; }
    public Vec2 Pos;
    public Vec2 Vel;
    public double Spin, Angle;
    public double Radius => AsteroidsRules.RadiusOf(Size);
}

public sealed class Shot
{
    public Vec2 Pos, Vel;
    public double Life;
    public int Owner; // the ship that fired it
}

public sealed class Ship
{
    public Vec2 Pos;
    public double Invulnerable; // seconds of blinking left after a hit (and at the start)
    public bool Active = true;
}

/// <summary>
/// Asteroids' rules, for one ship or two flying together: rocks drift across the screen and bounce off its edges,
/// shots fly straight and fade, a shot splits a big rock into two medium ones and a medium into two small (a small one
/// is gone), and a rock touching a ship costs the team a life and gives that ship a moment to get clear. When the last
/// rock is gone the next wave comes in, with more and faster rocks, always from the edges away from the ships. Scores
/// go up as rocks get smaller. UI-free and seeded, so it can be tested; in a co-op game the host runs it for both ships.
/// </summary>
public sealed class AsteroidsRules
{
    public const int StartLives = 3, CoopLives = 5, MaxRocks = 40;
    public const double ShotSpeed = 900, ShotLife = 1.1, SafeTime = 2.2, ShipRadius = 14, FireEvery = 0.16, Breather = 1.6;

    readonly Random _rng;
    readonly List<Rock> _rocks = new();
    readonly List<Shot> _shots = new();
    readonly double[] _lastShot;
    int _nextId;
    double _calm; // seconds since the last rock went: a short breather before the next wave

    public AsteroidsRules(Rect box, int ships, Random rng)
    {
        Box = box;
        _rng = rng;
        Ships = Enumerable.Range(0, Math.Clamp(ships, 1, 2)).Select(_ => new Ship { Invulnerable = SafeTime }).ToArray();
        _lastShot = new double[Ships.Length];
        Array.Fill(_lastShot, double.NegativeInfinity);
        Lives = Ships.Length > 1 ? CoopLives : StartLives;
    }

    public Rect Box { get; set; }
    public Ship[] Ships { get; }
    public IReadOnlyList<Rock> Rocks => _rocks;
    public IReadOnlyList<Shot> Shots => _shots;
    public int Wave { get; private set; }
    public int Score { get; private set; }
    public int Lives { get; private set; }
    public int Destroyed { get; private set; }
    public bool Over => Lives <= 0;
    public double Time { get; private set; }

    /// <summary>What happened in the last <see cref="Step"/>: rocks hit (where, how big, by which ship) and ships hit.</summary>
    public List<(Vec2 At, int Size, int Owner)> Broken { get; } = new();
    public List<int> ShipsHit { get; } = new();

    public static double RadiusOf(int size) => size switch { 3 => 46, 2 => 26, _ => 14 };
    public static int PointsOf(int size) => size switch { 3 => 20, 2 => 50, _ => 100 };

    /// <summary>The next wave: three big rocks, one more each wave (up to ten), faster each time, from the edges.</summary>
    public void NextWave()
    {
        Wave++;
        int count = Math.Min(10, 2 + Wave);
        double speed = 55 * (1 + 0.1 * (Wave - 1));
        for (int i = 0; i < count; i++) Add(3, EdgeSpot(), RandomVelocity(speed));
    }

    /// <summary>Ship <paramref name="ship"/> fires toward <paramref name="target"/>; false while its gun is still cooling.</summary>
    public bool Fire(int ship, Vec2 target)
    {
        if (Over || ship < 0 || ship >= Ships.Length || !Ships[ship].Active || Time - _lastShot[ship] < FireEvery) return false;
        var from = Ships[ship].Pos;
        var dir = (target - from).Normalized();
        if (dir.LengthSquared < 0.5) dir = new Vec2(0, -1);
        _lastShot[ship] = Time;
        _shots.Add(new Shot { Pos = from + dir * (ShipRadius + 4), Vel = dir * ShotSpeed, Life = ShotLife, Owner = ship });
        return true;
    }

    public void Step(double dt)
    {
        Broken.Clear();
        ShipsHit.Clear();
        if (Over) return;
        Time += dt;
        if (_rocks.Count == 0 && (Wave == 0 || (_calm += dt) >= Breather))
        {
            _calm = 0;
            NextWave();
        }

        foreach (var r in _rocks)
        {
            r.Pos += r.Vel * dt;
            r.Angle += r.Spin * dt;
            Bounce(ref r.Pos, ref r.Vel, r.Radius);
        }
        for (int i = _shots.Count - 1; i >= 0; i--)
        {
            var s = _shots[i];
            s.Pos += s.Vel * dt;
            s.Life -= dt;
            if (s.Life <= 0 || !Box.Contains(s.Pos.ToPoint())) _shots.RemoveAt(i);
        }

        // shots against rocks
        for (int i = _shots.Count - 1; i >= 0; i--)
        {
            var s = _shots[i];
            var hit = _rocks.FirstOrDefault(r => (r.Pos - s.Pos).Length < r.Radius + 3);
            if (hit == null) continue;
            _shots.RemoveAt(i);
            Break(hit, s.Owner, s.Vel);
        }

        // rocks against ships
        for (int k = 0; k < Ships.Length; k++)
        {
            var ship = Ships[k];
            if (!ship.Active) continue;
            if (ship.Invulnerable > 0)
            {
                ship.Invulnerable = Math.Max(0, ship.Invulnerable - dt);
                continue;
            }
            var hit = _rocks.FirstOrDefault(r => (r.Pos - ship.Pos).Length < r.Radius + ShipRadius * 0.8);
            if (hit == null) continue;
            ShipsHit.Add(k);
            Lives = Math.Max(0, Lives - 1);
            ship.Invulnerable = SafeTime;
            Break(hit, -1, hit.Pos - ship.Pos);
            if (Over) return;
        }
    }

    /// <summary>A rock hit: the points (for a shot), and two smaller rocks flying apart across the line of the hit.</summary>
    void Break(Rock rock, int owner, Vec2 push)
    {
        _rocks.Remove(rock);
        Broken.Add((rock.Pos, rock.Size, owner));
        if (owner >= 0)
        {
            Score += PointsOf(rock.Size);
            Destroyed++;
        }
        if (rock.Size <= 1) return;
        var across = new Vec2(-push.Y, push.X).Normalized();
        double speed = rock.Vel.Length * 1.35 + 30;
        for (int side = -1; side <= 1; side += 2)
        {
            if (_rocks.Count >= MaxRocks) break;
            var vel = (across * side + rock.Vel.Normalized() * 0.4).Normalized() * speed;
            Add(rock.Size - 1, rock.Pos + across * side * RadiusOf(rock.Size - 1) * 0.6, vel);
        }
    }

    Rock Add(int size, Vec2 at, Vec2 vel)
    {
        var r = new Rock { Id = ++_nextId, Size = size, Pos = at, Vel = vel, Spin = (_rng.NextDouble() - 0.5) * 1.6, Angle = _rng.NextDouble() * Math.PI * 2 };
        _rocks.Add(r);
        return r;
    }

    void Bounce(ref Vec2 p, ref Vec2 v, double r)
    {
        if (p.X < Box.Left + r && v.X < 0 || p.X > Box.Right - r && v.X > 0) v.X = -v.X;
        if (p.Y < Box.Top + r && v.Y < 0 || p.Y > Box.Bottom - r && v.Y > 0) v.Y = -v.Y;
        p = new Vec2(Math.Clamp(p.X, Box.Left + r, Math.Max(Box.Left + r, Box.Right - r)), Math.Clamp(p.Y, Box.Top + r, Math.Max(Box.Top + r, Box.Bottom - r)));
    }

    /// <summary>A point just inside an edge, as far from the ships as a few tries can find.</summary>
    Vec2 EdgeSpot()
    {
        Vec2 best = default;
        double bestGap = -1;
        for (int tries = 0; tries < 8; tries++)
        {
            double m = RadiusOf(3) + 2;
            var p = _rng.Next(4) switch
            {
                0 => new Vec2(Box.Left + m, Box.Top + m + _rng.NextDouble() * Math.Max(1, Box.Height - 2 * m)),
                1 => new Vec2(Box.Right - m, Box.Top + m + _rng.NextDouble() * Math.Max(1, Box.Height - 2 * m)),
                2 => new Vec2(Box.Left + m + _rng.NextDouble() * Math.Max(1, Box.Width - 2 * m), Box.Top + m),
                _ => new Vec2(Box.Left + m + _rng.NextDouble() * Math.Max(1, Box.Width - 2 * m), Box.Bottom - m),
            };
            double gap = Ships.Where(s => s.Active).Select(s => (s.Pos - p).Length).DefaultIfEmpty(double.MaxValue).Min();
            if (gap > bestGap)
            {
                bestGap = gap;
                best = p;
            }
        }
        return best;
    }

    Vec2 RandomVelocity(double speed)
    {
        double a = _rng.NextDouble() * Math.PI * 2;
        return new Vec2(Math.Cos(a), Math.Sin(a)) * speed * (0.75 + _rng.NextDouble() * 0.5);
    }
}
