using System;

namespace DeskArcade.Engine;

public struct Impacts
{
    public double Floor;     // strongest floor/platform impact speed this step
    public double Wall;      // strongest wall impact speed
    public bool TouchedGround;
}

/// <summary>Circle physics shared by the ball games: gravity, walls, floor, window-top platforms.</summary>
public sealed class BallBody
{
    public Vec2 Pos;
    public Vec2 Vel;
    public double R;
    public double Angle;          // degrees, for rendering
    public double Spin;           // degrees/second while airborne
    public double Gravity = 2000;
    public double Restitution = 0.72;
    public double WallRestitution = 0.78;
    public double AirDrag = 0.06;
    public double RollFriction = 1.4;

    public bool Grounded { get; private set; }
    public IntPtr GroundHwnd { get; private set; }
    public bool Asleep { get; private set; }

    double _still;
    int _seenGeneration = -1;

    public BallBody(double r) => R = r;

    public void Wake()
    {
        Asleep = false;
        _still = 0;
    }

    public void Place(Vec2 p, Vec2 v = default)
    {
        Pos = p;
        Vel = v;
        Grounded = false;
        GroundHwnd = IntPtr.Zero;
        Wake();
    }

    public void Step(double dt, IGameHost host, ref Impacts imp)
    {
        var plats = host.Platforms;
        if (plats.Generation != _seenGeneration)
        {
            _seenGeneration = plats.Generation;
            if (GroundHwnd != IntPtr.Zero)
            {
                Pos += plats.DeltaOf(GroundHwnd); // ride along with the window
                Wake();
            }
        }
        if (Asleep) return;

        var arena = host.Arena;
        double prevBottom = Pos.Y + R;
        Vel.Y += Gravity * dt;
        Vel *= 1 - AirDrag * dt;
        Pos += Vel * dt;
        Grounded = false;

        if (Pos.X - R < arena.Left)
        {
            Pos.X = arena.Left + R;
            if (Vel.X < 0) { imp.Wall = Math.Max(imp.Wall, -Vel.X); Vel.X = -Vel.X * WallRestitution; }
        }
        else if (Pos.X + R > arena.Right)
        {
            Pos.X = arena.Right - R;
            if (Vel.X > 0) { imp.Wall = Math.Max(imp.Wall, Vel.X); Vel.X = -Vel.X * WallRestitution; }
        }
        if (Pos.Y - R < arena.Top) // closed box: the top of the screen is a ceiling
        {
            Pos.Y = arena.Top + R;
            if (Vel.Y < 0) { imp.Wall = Math.Max(imp.Wall, -Vel.Y); Vel.Y = -Vel.Y * WallRestitution; }
        }

        double groundY = double.NaN;
        IntPtr groundHwnd = IntPtr.Zero;
        if (Vel.Y >= 0 && plats.FindLanding(Pos.X, prevBottom, Pos.Y + R, out var plat))
        {
            groundY = plat.Y;
            groundHwnd = plat.Hwnd;
        }
        else if (Pos.Y + R >= arena.Bottom)
        {
            groundY = arena.Bottom;
        }

        if (!double.IsNaN(groundY))
        {
            Pos.Y = groundY - R;
            if (Vel.Y > 0)
            {
                imp.Floor = Math.Max(imp.Floor, Vel.Y);
                Vel.Y = -Vel.Y * Restitution;
                if (Math.Abs(Vel.Y) < 90) Vel.Y = 0;
            }
            Grounded = true;
            GroundHwnd = groundHwnd;
            imp.TouchedGround = true;
        }

        if (Grounded)
        {
            Vel.X *= 1 - Math.Min(1, RollFriction * dt);
            Spin = Vel.X / R * 180 / Math.PI;
        }
        Angle = (Angle + Spin * dt) % 360;

        if (Grounded && Vel.Length < 10)
        {
            _still += dt;
            if (_still > 0.35) { Asleep = true; Vel = default; Spin = 0; }
        }
        else
        {
            _still = 0;
        }
    }

    /// <summary>Bounce off a static round collider (rim lip, star, ...). Returns impact speed or 0.</summary>
    public double CollidePoint(Vec2 c, double cr, double restitution)
    {
        Vec2 d = Pos - c;
        double min = R + cr;
        double dist = d.Length;
        if (dist >= min || dist < 1e-6) return 0;
        Vec2 n = d / dist;
        Pos = c + n * min;
        double vn = Vec2.Dot(Vel, n);
        if (vn >= 0) return 0;
        Vel -= n * ((1 + restitution) * vn);
        Vec2 t = new(-n.Y, n.X);
        double vt = Vec2.Dot(Vel, t);
        Vel -= t * (0.08 * vt); // a little tangential friction
        Spin += vt * 0.6;
        return -vn;
    }

    /// <summary>Bounce off a capsule (segment with thickness), e.g. a backboard.</summary>
    public double CollideSegment(Vec2 a, Vec2 b, double thickness, double restitution)
    {
        Vec2 ab = b - a;
        double len2 = ab.LengthSquared;
        double t = len2 < 1e-9 ? 0 : Math.Clamp(Vec2.Dot(Pos - a, ab) / len2, 0, 1);
        return CollidePoint(a + ab * t, thickness, restitution);
    }
}
