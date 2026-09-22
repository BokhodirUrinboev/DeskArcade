using System;
using System.Collections.Generic;
using Avalonia;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>A round body on a <see cref="DiscTable"/>: a pool ball, a bowling ball or a pin seen from above.</summary>
public sealed class Disc
{
    public Vec2 Pos, Vel;
    public double R = 10, Mass = 1;
    /// <summary>Multiplies the table's rolling friction and damping: sliding pins stop sooner than a rolling ball.</summary>
    public double Drag = 1;
    /// <summary>Off the table (potted, swept away): no longer moves or collides.</summary>
    public bool Sunk;
    /// <summary>Still moves and hits the cushions, but passes through other discs (a bowling ball in the gutter).</summary>
    public bool Ghost;
    /// <summary>Whatever the game wants to hang on it (the ball number, the pin index).</summary>
    public int Tag;

    public bool Still => Sunk || Vel.X == 0 && Vel.Y == 0;
}

/// <summary>
/// Top-down disc physics with no gravity, shared by Pool and Bowling: rolling friction, elastic
/// disc-disc collisions, cushions along the edges of a rectangle and optional round pockets. It runs in
/// fixed sub-steps short enough that the fastest disc moves less than the smallest radius per step, so
/// nothing tunnels through anything.
/// </summary>
public sealed class DiscTable
{
    public const double Step = 1.0 / 600;
    /// <summary>At 1/600 s a step this is 6 px per step, under every radius the games use.</summary>
    public const double MaxSpeed = 3600;

    public readonly record struct Pocket(Vec2 Pos, double R);

    public Rect Bounds { get; set; }
    public List<Disc> Discs { get; } = new();
    public List<Pocket> Pockets { get; } = new();

    /// <summary>Disc-disc bounciness (pool balls ~0.95).</summary>
    public double Restitution { get; set; } = 0.95;
    /// <summary>Disc-cushion bounciness.</summary>
    public double CushionRestitution { get; set; } = 0.75;
    /// <summary>Constant rolling deceleration, px/s².</summary>
    public double Friction { get; set; } = 90;
    /// <summary>Extra linear slow-down, 1/s: fast balls lose speed a little faster than slow ones.</summary>
    public double Damping { get; set; } = 0.25;

    /// <summary>Two discs touched: the discs and the closing speed.</summary>
    public event Action<Disc, Disc, double>? Collided;
    /// <summary>A disc bounced off a cushion, with the speed it hit at.</summary>
    public event Action<Disc, double>? Cushion;
    /// <summary>A disc dropped into a pocket (index into <see cref="Pockets"/>).</summary>
    public event Action<Disc, int>? Sunk;

    double _acc;

    public bool AllStill
    {
        get
        {
            foreach (var d in Discs)
                if (!d.Still) return false;
            return true;
        }
    }

    public Disc Add(Vec2 pos, double r, double mass = 1, int tag = 0)
    {
        var d = new Disc { Pos = pos, R = r, Mass = mass, Tag = tag };
        Discs.Add(d);
        return d;
    }

    /// <summary>Runs whole sub-steps for <paramref name="dt"/> seconds; a long stall is not caught up in one go.</summary>
    public void Advance(double dt)
    {
        _acc = Math.Min(_acc + dt, 0.1);
        while (_acc >= Step)
        {
            _acc -= Step;
            StepOnce(Step);
        }
    }

    public void StepOnce(double h)
    {
        var discs = Discs;
        for (int i = 0; i < discs.Count; i++)
        {
            var d = discs[i];
            if (d.Sunk || d.Still) continue;
            double speed = d.Vel.Length;
            if (speed > MaxSpeed)
            {
                d.Vel *= MaxSpeed / speed;
                speed = MaxSpeed;
            }
            double next = speed - (Friction + Damping * speed) * d.Drag * h;
            if (next <= 0.5) d.Vel = default;
            else d.Vel *= next / speed;
            d.Pos += d.Vel * h;
        }

        for (int i = 0; i < discs.Count; i++)
        {
            var a = discs[i];
            if (a.Sunk || a.Ghost) continue;
            for (int j = i + 1; j < discs.Count; j++)
            {
                var b = discs[j];
                if (b.Sunk || b.Ghost) continue;
                Collide(a, b);
            }
        }

        for (int i = 0; i < discs.Count; i++)
        {
            var d = discs[i];
            if (!d.Sunk) Edges(d);
        }
    }

    void Collide(Disc a, Disc b)
    {
        double dx = b.Pos.X - a.Pos.X, dy = b.Pos.Y - a.Pos.Y, min = a.R + b.R;
        double d2 = dx * dx + dy * dy;
        if (d2 >= min * min) return;
        double dist = Math.Sqrt(d2);
        var n = dist < 1e-9 ? new Vec2(1, 0) : new Vec2(dx / dist, dy / dist);
        double ia = 1 / a.Mass, ib = 1 / b.Mass;

        // push them apart by mass, so a heavy ball barely gives way to a pin
        double push = (min - dist) / (ia + ib);
        a.Pos -= n * (push * ia);
        b.Pos += n * (push * ib);

        double vn = Vec2.Dot(b.Vel - a.Vel, n);
        if (vn >= 0) return;
        double j = -(1 + Restitution) * vn / (ia + ib);
        a.Vel -= n * (j * ia);
        b.Vel += n * (j * ib);
        Collided?.Invoke(a, b, -vn);
    }

    void Edges(Disc d)
    {
        var box = Bounds;
        double r = d.R;
        bool outside = d.Pos.X - r < box.Left || d.Pos.X + r > box.Right || d.Pos.Y - r < box.Top || d.Pos.Y + r > box.Bottom;

        for (int k = 0; k < Pockets.Count; k++)
        {
            var p = Pockets[k];
            double dist = (d.Pos - p.Pos).Length;
            bool inMouth = dist < p.R + r;
            // the centre over the hole drops it; so does crossing the cushion line in the pocket's mouth
            bool beyond = d.Pos.X < box.Left || d.Pos.X > box.Right || d.Pos.Y < box.Top || d.Pos.Y > box.Bottom;
            if (dist < p.R || inMouth && beyond)
            {
                d.Sunk = true;
                d.Vel = default;
                Sunk?.Invoke(d, k);
                return;
            }
            if (inMouth) return; // the cushion is open next to a pocket
        }
        if (!outside) return;

        double hit = 0;
        if (d.Pos.X - r < box.Left)
        {
            d.Pos.X = box.Left + r;
            if (d.Vel.X < 0) { hit = Math.Max(hit, -d.Vel.X); d.Vel.X = -d.Vel.X * CushionRestitution; }
        }
        else if (d.Pos.X + r > box.Right)
        {
            d.Pos.X = box.Right - r;
            if (d.Vel.X > 0) { hit = Math.Max(hit, d.Vel.X); d.Vel.X = -d.Vel.X * CushionRestitution; }
        }
        if (d.Pos.Y - r < box.Top)
        {
            d.Pos.Y = box.Top + r;
            if (d.Vel.Y < 0) { hit = Math.Max(hit, -d.Vel.Y); d.Vel.Y = -d.Vel.Y * CushionRestitution; }
        }
        else if (d.Pos.Y + r > box.Bottom)
        {
            d.Pos.Y = box.Bottom - r;
            if (d.Vel.Y > 0) { hit = Math.Max(hit, d.Vel.Y); d.Vel.Y = -d.Vel.Y * CushionRestitution; }
        }
        if (hit > 0) Cushion?.Invoke(d, hit);
    }

    // ------------------------------------------------------------------ geometry for aiming

    /// <summary>
    /// Slides a circle of radius <paramref name="r"/> from <paramref name="from"/> along the unit vector
    /// <paramref name="dir"/> and returns how far it goes before touching a disc (null if it touches none).
    /// </summary>
    public double? Cast(Vec2 from, Vec2 dir, double r, Disc? ignore, out Disc? hit, Disc? ignore2 = null)
    {
        hit = null;
        double best = double.MaxValue;
        foreach (var d in Discs)
        {
            if (d.Sunk || d == ignore || d == ignore2) continue;
            var c = d.Pos - from;
            double b = Vec2.Dot(c, dir), rr = r + d.R;
            double disc = b * b - (c.LengthSquared - rr * rr);
            if (disc < 0) continue;
            double t = b - Math.Sqrt(disc);
            if (t < -0.5 || t >= best) continue;
            best = Math.Max(0, t);
            hit = d;
        }
        return hit == null ? null : best;
    }

    /// <summary>How far a circle of radius <paramref name="r"/> goes along <paramref name="dir"/> before it meets a cushion.</summary>
    public double CastToCushion(Vec2 from, Vec2 dir, double r)
    {
        var box = Bounds;
        double t = double.MaxValue;
        if (dir.X > 1e-9) t = Math.Min(t, (box.Right - r - from.X) / dir.X);
        else if (dir.X < -1e-9) t = Math.Min(t, (box.Left + r - from.X) / dir.X);
        if (dir.Y > 1e-9) t = Math.Min(t, (box.Bottom - r - from.Y) / dir.Y);
        else if (dir.Y < -1e-9) t = Math.Min(t, (box.Top + r - from.Y) / dir.Y);
        return Math.Max(0, t);
    }

    /// <summary>How far a disc rolls while slowing from <paramref name="v0"/> to <paramref name="v1"/>.</summary>
    public double RollDistance(double v0, double v1, double drag = 1)
    {
        double f = Friction * drag, k = Damping * drag;
        if (v0 <= v1) return 0;
        if (k < 1e-9) return (v0 * v0 - v1 * v1) / (2 * f);
        // dv/dt = −(f + k·v), so ds = v dv / −(f + k·v)
        return (v0 - v1) / k - f / (k * k) * Math.Log((f + k * v0) / (f + k * v1));
    }

    /// <summary>The launch speed that still has <paramref name="arrive"/> left after rolling <paramref name="distance"/>.</summary>
    public double SpeedToTravel(double distance, double arrive, double drag = 1)
    {
        double lo = arrive, hi = MaxSpeed;
        if (RollDistance(hi, arrive, drag) < distance) return hi;
        for (int i = 0; i < 50; i++)
        {
            double mid = (lo + hi) / 2;
            if (RollDistance(mid, arrive, drag) < distance) lo = mid;
            else hi = mid;
        }
        return hi;
    }

    /// <summary>A planned pot: aim the cue ball along <see cref="Dir"/> at <see cref="Speed"/> to put <see cref="Target"/> in pocket <see cref="Pocket"/>.</summary>
    public readonly record struct Shot(Vec2 Dir, double Speed, Disc Target, int Pocket, double Ease);

    /// <summary>
    /// The easiest pot on the table by ghost-ball aiming: for each ball and pocket, the cue ball has to reach
    /// the spot touching the ball on the far side from the pocket. Both paths must be clear; straighter and
    /// shorter shots are easier. Null when there is no clear pot at all.
    /// </summary>
    public Shot? PlanPot(Disc cue, double maxSpeed)
    {
        Shot? best = null;
        foreach (var ball in Discs)
        {
            if (ball == cue || ball.Sunk) continue;
            for (int k = 0; k < Pockets.Count; k++)
            {
                var pocket = Pockets[k];
                var toPocket = pocket.Pos - ball.Pos;
                double d2 = toPocket.Length;
                if (d2 < 1) continue;
                var pd = toPocket / d2;
                var ghost = ball.Pos - pd * (ball.R + cue.R);
                var toGhost = ghost - cue.Pos;
                double d1 = toGhost.Length;
                if (d1 < 1) continue;
                var aim = toGhost / d1;
                double cut = Vec2.Dot(aim, pd);
                if (cut < 0.35) continue; // thinner than ~70°: too hard to judge

                // the cue ball must reach this ball first, and the ball must have a clear run to the pocket
                if (Cast(cue.Pos, aim, cue.R, cue, out var first) is double t1 && (first != ball || t1 < d1 - 2)) continue;
                if (Cast(ball.Pos, pd, ball.R, ball, out _, cue) is double t2 && t2 < d2 - pocket.R) continue;

                // how fast the object ball leaves: (1+e)/2 of the cue ball's speed along the line of centres
                double objectSpeed = SpeedToTravel(Math.Max(0, d2 - pocket.R * 0.5), 140);
                double contact = objectSpeed / (cut * (1 + Restitution) / 2);
                double speed = SpeedToTravel(d1, contact);
                if (speed > maxSpeed) continue;
                double ease = cut * cut / (1 + (d1 + d2) / 600);
                if (best is not Shot b || ease > b.Ease) best = new Shot(aim, speed, ball, k, ease);
            }
        }
        return best;
    }
}
