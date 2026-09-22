using System;
using System.Collections.Generic;
using Avalonia;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>
/// The Pinball table without any UI: the ball, two flippers (rotating tapered capsules with an angular
/// velocity), the guide rails from the side walls down to the flipper pivots, the slingshot kickers above
/// the flippers, the pop bumpers, the rollover lanes at the top and one-way window-top ledges. It keeps the
/// score and the lane multiplier. <see cref="PinballGame"/> draws it and runs the game; tests drive it directly.
/// </summary>
public sealed class PinballTable
{
    public const double Gravity = 1200, Step = 1.0 / 600, MaxSpeed = 3200;
    public const double BumperKick = 950, SlingKick = 850;
    public const int BumperPoints = 100, SlingPoints = 10, LanePoints = 100, LanesBonus = 1000, MaxMultiplier = 5;
    const double FlipUpSpeed = 24, FlipDownSpeed = 14, DownAngle = 0.5, UpAngle = -0.5;
    const double FlipperBounce = 0.25, RailBounce = 0.3, WallBounce = 0.55, PostBounce = 0.5, LedgeBounce = 0.55;
    const double RestSpeed = 60, SlingMin = 50, RollDrag = 0.6, ShedAccel = 500, StuckAfter = 4;
    const double RailR = 5, SlingR = 5, PostR = 6;

    public enum Hit { Bumper, Sling, Rollover, LanesDone, Wall, Flipper, Drain }

    /// <summary>A flipper: a tapered capsule turning about its pivot. Side is +1 for the left one (tip toward +x), −1 for the right.</summary>
    public sealed class Flipper
    {
        public Vec2 Pivot;
        public double Length, BaseR, TipR, Side, Angle = DownAngle, Omega;
        /// <summary>The button is held: the flipper swings up and stays there.</summary>
        public bool Up;

        public Vec2 Dir => new(Side * Math.Cos(Angle), Math.Sin(Angle));
        public Vec2 Tip => Pivot + Dir * Length;
        public bool Moving => Omega != 0 || Angle != (Up ? UpAngle : DownAngle);
        public bool Raised => Angle <= UpAngle + 1e-9;

        /// <summary>Velocity of the flipper surface at <paramref name="p"/> (angular velocity × radius).</summary>
        public Vec2 VelocityAt(Vec2 p)
        {
            Vec2 d = p - Pivot;
            return new Vec2(-d.Y, d.X) * (Side * Omega);
        }
    }

    public readonly record struct Segment(Vec2 A, Vec2 B, double Radius, double Bounce, double Kick, int Sling);

    public sealed class Bumper
    {
        public Vec2 Center;
        public double Radius;
        public bool Window;
    }

    public readonly record struct Ledge(double Y, double X1, double X2);

    readonly Random _rng;
    readonly bool[] _inLane = new bool[3];
    double _acc, _slowT;
    bool _predicting;

    public PinballTable(Rect arena, Rect avoid = default, Random? rng = null)
    {
        _rng = rng ?? new Random();
        Build(arena, avoid);
    }

    public Rect Arena { get; private set; }
    public double BallR { get; private set; }
    public Vec2 Ball, BallVel;
    public bool InPlay { get; set; }

    public Flipper Left { get; } = new() { Side = 1 };
    public Flipper Right { get; } = new() { Side = -1 };
    public List<Segment> Segments { get; } = new();
    public List<Vec2> Posts { get; } = new();
    public List<Bumper> Bumpers { get; } = new();
    public List<Ledge> Ledges { get; } = new();
    public Vec2[] Lanes { get; } = new Vec2[3];
    public bool[] LaneLit { get; } = new bool[3];
    public double LaneR { get; private set; }
    public double PostRadius => PostR;
    /// <summary>The slingshot triangles, left then right (three corners each).</summary>
    public Vec2[][] Slings { get; } = { new Vec2[3], new Vec2[3] };
    /// <summary>Where the ball waits to be served, above the right flipper.</summary>
    public Vec2 ServeSpot { get; private set; }
    /// <summary>Window tops lower than this are left out: they would crowd the flippers.</summary>
    public double LedgeMaxY { get; private set; }

    public int Score, Multiplier = 1;

    /// <summary>Something worth a sound or a flash: kind, index (bumper, lane, 0 left / 1 right), where, impact speed.</summary>
    public event Action<Hit, int, Vec2, double>? Event;

    public bool FlippersMoving => Left.Moving || Right.Moving;

    // ------------------------------------------------------------------ layout

    /// <summary>Lays the table out over the whole arena, keeping the bumpers and lanes clear of <paramref name="avoid"/>.</summary>
    public void Build(Rect arena, Rect avoid = default)
    {
        Arena = arena;
        var a = arena;
        double k = Math.Clamp(a.Height / 1000, 0.75, 1.5);
        double cx = a.Center.X;
        BallR = Math.Clamp(Math.Round(12.5 * k), 11, 16);
        double len = Math.Clamp(125 * k, 110, 140);
        double gapHalf = BallR + 10 + 12 * k; // the ball passes between the lowered tips with room to spare

        foreach (var f in new[] { Left, Right })
        {
            f.Length = len;
            f.BaseR = Math.Clamp(12 * k, 11, 15);
            f.TipR = Math.Clamp(8 * k, 8, 11);
            f.Angle = f.Up ? UpAngle : DownAngle;
            f.Omega = 0;
        }
        double pivotY = a.Bottom - len * Math.Sin(DownAngle) - 45 * k;
        double reach = len * Math.Cos(DownAngle);
        Left.Pivot = new Vec2(cx - gapHalf - reach, pivotY);
        Right.Pivot = new Vec2(cx + gapHalf + reach, pivotY);

        Segments.Clear();
        Posts.Clear();
        Bumpers.RemoveAll(b => !b.Window);

        // guide rails: from each side wall down to the flipper pivot, so anything that falls funnels to the flippers
        double run = Left.Pivot.X - a.Left;
        double slope = Math.Min(Math.Tan(22 * Math.PI / 180), a.Height * 0.33 / Math.Max(1, run));
        double wallY = pivotY - run * slope;
        var railL = new Vec2(a.Left, wallY);
        var railR = new Vec2(a.Right, wallY);
        Segments.Add(new Segment(railL, Left.Pivot, RailR, RailBounce, 0, -1));
        Segments.Add(new Segment(railR, Right.Pivot, RailR, RailBounce, 0, -1));

        // slingshots: a triangle above each flipper, with an inlane underneath so a ball on the rail rolls on to the flipper
        double slingTop = double.MaxValue;
        for (int side = 0; side < 2; side++)
        {
            var f = side == 0 ? Left : Right;
            var wall = side == 0 ? railL : railR;
            Vec2 u = (f.Pivot - wall).Normalized();
            Vec2 n = new(u.Y, -u.X);
            if (n.Y > 0) n = -n;
            double off = 2 * BallR + RailR + SlingR + 8;
            Vec2 b1 = f.Pivot - u * (len * 0.5) + n * off;
            Vec2 b2 = f.Pivot - u * (len * 1.5) + n * off;
            Vec2 top = b2 + n * (len * 0.75);
            Slings[side][0] = b1;
            Slings[side][1] = b2;
            Slings[side][2] = top;
            Segments.Add(new Segment(b1, b2, SlingR, PostBounce, 0, -1));
            Segments.Add(new Segment(b2, top, SlingR, PostBounce, 0, -1));
            Segments.Add(new Segment(top, b1, SlingR, PostBounce, SlingKick, side)); // the kicking face looks at the middle
            slingTop = Math.Min(slingTop, top.Y);
        }
        LedgeMaxY = Math.Min(pivotY - 2.2 * len, slingTop - 30);

        // three pop bumpers in a triangle and three rollover lanes above them, shifted sideways if the HUD is in the way
        double sp = 105 * k, br = Math.Round(28 * k), laneSp = 72 * k;
        double by = a.Top + a.Height * 0.45, laneY = Math.Max(a.Top + 70 * k, a.Top + a.Height * 0.13);
        double shift = 0;
        for (int i = 0; i < 12; i++)
        {
            double s = (i % 2 == 0 ? 1 : -1) * ((i + 1) / 2) * 80;
            var cluster = new Rect(cx + s - Math.Max(sp, laneSp * 1.5) - br - 20, laneY - 30, 2 * (Math.Max(sp, laneSp * 1.5) + br + 20), by + sp + br - laneY + 50);
            shift = s;
            if (avoid.Width <= 0 || !cluster.Intersects(avoid.Inflate(10))) break;
        }
        double bx = cx + shift;
        Bumpers.InsertRange(0, new[] // the table's own bumpers come first, window ones after
        {
            new Bumper { Center = new Vec2(bx - sp * 0.6, by - sp * 0.35), Radius = br },
            new Bumper { Center = new Vec2(bx + sp * 0.6, by - sp * 0.35), Radius = br },
            new Bumper { Center = new Vec2(bx, by + sp * 0.6), Radius = br },
        });
        LaneR = Math.Max(16, BallR + 4);
        for (int i = 0; i < 3; i++) Lanes[i] = new Vec2(bx + (i - 1) * laneSp, laneY);
        for (int i = 0; i < 4; i++) Posts.Add(new Vec2(bx + (i - 1.5) * laneSp, laneY));

        ServeSpot = new Vec2(Right.Pivot.X - len * 0.15 * Math.Cos(DownAngle), pivotY - 70 * k);
    }

    /// <summary>The top of the rail (or the flipper line) at <paramref name="x"/>: the ball lives above it.</summary>
    public double RailYAt(double x)
    {
        var a = Arena;
        if (x <= Left.Pivot.X)
        {
            double t = (x - a.Left) / Math.Max(1, Left.Pivot.X - a.Left);
            return Segments[0].A.Y + (Left.Pivot.Y - Segments[0].A.Y) * Math.Clamp(t, 0, 1) - RailR;
        }
        if (x >= Right.Pivot.X)
        {
            double t = (a.Right - x) / Math.Max(1, a.Right - Right.Pivot.X);
            return Segments[1].A.Y + (Right.Pivot.Y - Segments[1].A.Y) * Math.Clamp(t, 0, 1) - RailR;
        }
        return Left.Pivot.Y;
    }

    /// <summary>Window tops the ball can bounce on: high enough, and not spanning wall to wall (the ball could never leave it).</summary>
    public void SetLedges(IReadOnlyList<Engine.Platform> tops)
    {
        Ledges.Clear();
        double clear = 2 * BallR + 4;
        foreach (var p in tops)
        {
            if (p.Y > LedgeMaxY || p.Y < Arena.Top + 40) continue;
            if (p.X1 - Arena.Left < clear && Arena.Right - p.X2 < clear) continue;
            if (p.Y > RailYAt(p.X1) - clear && p.Y > RailYAt(p.X2) - clear) continue;
            Ledges.Add(new Ledge(p.Y, p.X1, p.X2));
        }
    }

    // ------------------------------------------------------------------ game flow

    /// <summary>Launches the waiting ball up toward a rollover lane.</summary>
    public void Serve()
    {
        Ball = ServeSpot;
        double apex = Lanes[1].Y - 25;
        double vy = -Math.Sqrt(2 * Gravity * Math.Max(50, Ball.Y - apex));
        double flight = -vy / Gravity;
        // aim at a lane whose arc clears the bumpers, so a serve reaches the top like a plunger shot
        int first = _rng.Next(3);
        double jitter = (_rng.NextDouble() - 0.5) * 30;
        double vx = (Lanes[first].X + jitter - Ball.X) / flight;
        for (int i = 0; i < 3; i++)
        {
            double tryVx = (Lanes[(first + i) % 3].X + jitter - Ball.X) / flight;
            if (!ArcHitsBumper(Ball, new Vec2(tryVx, vy), flight)) { vx = tryVx; break; }
        }
        BallVel = new Vec2(vx, vy);
        InPlay = true;
        _slowT = 0;
    }

    bool ArcHitsBumper(Vec2 p, Vec2 v, double flight)
    {
        for (double t = 0; t <= flight; t += 1.0 / 120)
        {
            var at = p + v * t + new Vec2(0, 0.5 * Gravity * t * t);
            foreach (var b in Bumpers)
                if ((at - b.Center).Length < b.Radius + BallR + 2) return true;
        }
        return false;
    }

    /// <summary>A new ball: lane lights off and the multiplier back to ×1.</summary>
    public void ResetBall()
    {
        Multiplier = 1;
        Array.Clear(LaneLit);
        Array.Clear(_inLane);
        InPlay = false;
        Ball = ServeSpot;
        BallVel = default;
    }

    void Award(int points) => Score += points * Multiplier;

    // ------------------------------------------------------------------ simulation

    public void Advance(double dt)
    {
        _acc += dt;
        while (_acc >= Step)
        {
            _acc -= Step;
            StepOnce(Step);
        }
    }

    public void StepOnce(double h)
    {
        MoveFlipper(Left, h);
        MoveFlipper(Right, h);
        if (!InPlay) return;

        double prevBottom = Ball.Y + BallR;
        BallVel.Y += Gravity * h;
        Ball += BallVel * h;
        var a = Arena;

        // closed box: side walls and the ceiling bounce; the floor is the drain
        if (Ball.X - BallR < a.Left) { Ball.X = a.Left + BallR; if (BallVel.X < 0) WallHit(ref BallVel.X); }
        else if (Ball.X + BallR > a.Right) { Ball.X = a.Right - BallR; if (BallVel.X > 0) WallHit(ref BallVel.X); }
        if (Ball.Y - BallR < a.Top) { Ball.Y = a.Top + BallR; if (BallVel.Y < 0) WallHit(ref BallVel.Y); }

        foreach (var s in Segments)
        {
            Vec2 c = Closest(s.A, s.B, Ball, out _);
            double hit = Collide(c, s.Radius, default, s.Bounce, s.Kick, s.Kick > 0 ? SlingMin : double.MaxValue, h, out bool kicked);
            if (kicked)
            {
                Award(SlingPoints);
                Raise(Hit.Sling, s.Sling, c, hit);
            }
            else if (hit > 150) Raise(Hit.Wall, -1, c, hit);
        }
        foreach (var p in Posts)
        {
            double hit = Collide(p, PostR, default, PostBounce, 0, double.MaxValue, h, out _);
            if (hit > 150) Raise(Hit.Wall, -1, p, hit);
        }
        for (int i = 0; i < Bumpers.Count; i++)
        {
            var b = Bumpers[i];
            // a pop bumper fires at any touch, so a ball can never come to rest on one
            double hit = Collide(b.Center, b.Radius, default, PostBounce, BumperKick, 0, h, out bool kicked);
            if (!kicked) continue;
            Award(BumperPoints);
            Raise(Hit.Bumper, i, b.Center + (Ball - b.Center).Normalized() * b.Radius, hit);
        }
        HitFlipper(Left, h);
        HitFlipper(Right, h);
        bool onLedge = HitLedges(prevBottom, h);

        double speed = BallVel.Length;
        if (speed > MaxSpeed) BallVel *= MaxSpeed / speed;

        CheckLanes();

        if (Ball.Y + BallR >= a.Bottom)
        {
            Ball.Y = a.Bottom - BallR;
            InPlay = false;
            BallVel = default;
            Raise(Hit.Drain, -1, Ball, 0);
            return;
        }

        // a ball parked somewhere odd (on a post, a slingshot corner) gets a little shove after a while
        if (!_predicting)
        {
            bool holding = Left.Up || Right.Up;
            if (speed < 30 && !holding && !onLedge) _slowT += h;
            else _slowT = 0;
            if (_slowT > StuckAfter)
            {
                _slowT = 0;
                BallVel = new Vec2((_rng.NextDouble() - 0.5) * 500, -420);
            }
        }
    }

    static void MoveFlipper(Flipper f, double h)
    {
        double target = f.Up ? UpAngle : DownAngle;
        double max = (f.Up ? FlipUpSpeed : FlipDownSpeed) * h;
        double prev = f.Angle, diff = target - f.Angle;
        f.Angle = Math.Abs(diff) <= max ? target : f.Angle + Math.Sign(diff) * max;
        f.Omega = (f.Angle - prev) / h;
    }

    void WallHit(ref double v)
    {
        if (Math.Abs(v) > 150) Raise(Hit.Wall, -1, Ball, Math.Abs(v));
        v = -v * WallBounce;
    }

    public static Vec2 Closest(Vec2 a, Vec2 b, Vec2 p, out double t)
    {
        Vec2 ab = b - a;
        double len2 = ab.LengthSquared;
        t = len2 < 1e-9 ? 0 : Math.Clamp(Vec2.Dot(p - a, ab) / len2, 0, 1);
        return a + ab * t;
    }

    /// <summary>
    /// Ball against a round surface point <paramref name="c"/> of radius <paramref name="r"/> moving at
    /// <paramref name="surfaceVel"/>: pushes the ball out and bounces it relative to the surface, so a moving
    /// flipper hands its speed to the ball. A kicker sends the ball off at least at <paramref name="kick"/> when
    /// it comes in faster than <paramref name="kickMin"/>. Returns the impact speed (0 for none).
    /// </summary>
    double Collide(Vec2 c, double r, Vec2 surfaceVel, double bounce, double kick, double kickMin, double h, out bool kicked)
    {
        kicked = false;
        Vec2 d = Ball - c;
        double min = BallR + r, dist2 = d.LengthSquared;
        if (dist2 >= min * min) return 0;
        double dist = Math.Sqrt(dist2);
        Vec2 n = dist < 1e-9 ? new Vec2(0, -1) : d / dist;
        Ball = c + n * min;

        Vec2 rel = BallVel - surfaceVel;
        double vn = Vec2.Dot(rel, n);
        if (vn >= 0) return 0;
        double outN = -vn < RestSpeed ? 0 : -vn * bounce; // no micro-bounces: a resting ball stays put
        if (kick > 0 && -vn > kickMin)
        {
            outN = Math.Max(outN, kick);
            kicked = true;
        }
        rel += n * (outN - vn);
        Vec2 t = new(-n.Y, n.X);
        rel -= t * (Vec2.Dot(rel, t) * Math.Min(1, RollDrag * h)); // a little rolling drag
        BallVel = rel + surfaceVel;
        return -vn;
    }

    void HitFlipper(Flipper f, double h)
    {
        Vec2 c = Closest(f.Pivot, f.Tip, Ball, out double t);
        double r = f.BaseR + (f.TipR - f.BaseR) * t; // tapered toward the tip
        double hit = Collide(c, r, f.VelocityAt(c), FlipperBounce, 0, double.MaxValue, h, out _);
        if (hit > 200) Raise(Hit.Flipper, f == Left ? 0 : 1, c, hit);
    }

    /// <summary>Window tops: one-way ledges the ball bounces on from above, sloped so it rolls off an open end.</summary>
    bool HitLedges(double prevBottom, double h)
    {
        if (BallVel.Y < 0) return false;
        double bottom = Ball.Y + BallR;
        foreach (var l in Ledges)
        {
            if (Ball.X < l.X1 || Ball.X > l.X2 || prevBottom > l.Y + 2 || bottom < l.Y) continue;
            Ball.Y = l.Y - BallR;
            if (BallVel.Y > 150) Raise(Hit.Wall, -1, new Vec2(Ball.X, l.Y), BallVel.Y);
            BallVel.Y = BallVel.Y < RestSpeed ? 0 : -BallVel.Y * LedgeBounce;
            double clear = 2 * BallR + 4;
            bool leftOpen = l.X1 - Arena.Left >= clear, rightOpen = Arena.Right - l.X2 >= clear;
            double dir = leftOpen && (!rightOpen || Ball.X - l.X1 < l.X2 - Ball.X) ? -1 : 1;
            BallVel.X += dir * ShedAccel * h;
            return true;
        }
        return false;
    }

    void CheckLanes()
    {
        for (int i = 0; i < 3; i++)
        {
            bool inside = (Ball - Lanes[i]).LengthSquared < LaneR * LaneR;
            if (inside && !_inLane[i] && !_predicting)
            {
                bool wasLit = LaneLit[i];
                LaneLit[i] = true;
                Award(LanePoints);
                Raise(Hit.Rollover, i, Lanes[i], wasLit ? 0 : 1);
                if (LaneLit[0] && LaneLit[1] && LaneLit[2])
                {
                    Array.Clear(LaneLit);
                    Award(LanesBonus);
                    Multiplier = Math.Min(MaxMultiplier, Multiplier + 1);
                    Raise(Hit.LanesDone, Multiplier, Lanes[1], 0);
                }
            }
            _inLane[i] = inside;
        }
    }

    void Raise(Hit kind, int index, Vec2 at, double speed)
    {
        if (!_predicting) Event?.Invoke(kind, index, at, speed);
    }

    // ------------------------------------------------------------------ autoplay

    /// <summary>
    /// Looks ahead up to <paramref name="horizon"/> seconds (flippers as they are now) for the ball reaching the
    /// sweet spot of a lowered flipper. Returns 0 (left) or 1 (right) and when, or −1 if neither. Leaves the table unchanged.
    /// </summary>
    public int PredictFlip(double horizon, out double at)
    {
        at = 0;
        if (!InPlay) return -1;
        Vec2 ball = Ball, vel = BallVel;
        double acc = _acc, slow = _slowT;
        var inLane = (bool[])_inLane.Clone();
        var saved = (Left.Angle, Left.Omega, Right.Angle, Right.Omega);
        int score = Score, mult = Multiplier;
        _predicting = true;
        int found = -1;
        try
        {
            for (double t = 0; t <= horizon && InPlay; t += Step)
            {
                for (int i = 0; i < 2 && found < 0; i++)
                {
                    var f = i == 0 ? Left : Right;
                    if (f.Up) continue;
                    Vec2 c = Closest(f.Pivot, f.Tip, Ball, out double s);
                    Vec2 up = new(-f.Dir.Y, f.Dir.X); // flipper normal, pointing up
                    if (up.Y > 0) up = -up;
                    if (s is >= 0.3 and <= 0.95 && (Ball - c).Length < BallR + f.BaseR + 8 && Vec2.Dot(Ball - c, up) > 0)
                    {
                        found = i;
                        at = t;
                    }
                }
                if (found >= 0) break;
                StepOnce(Step);
            }
        }
        finally
        {
            _predicting = false;
            Ball = ball;
            BallVel = vel;
            InPlay = true;
            _acc = acc;
            _slowT = slow;
            Array.Copy(inLane, _inLane, 3);
            (Left.Angle, Left.Omega, Right.Angle, Right.Omega) = saved;
            Score = score;
            Multiplier = mult;
        }
        return found;
    }
}
