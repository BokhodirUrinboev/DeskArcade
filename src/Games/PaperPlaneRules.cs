using System;
using System.Collections.Generic;
using Avalonia;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>A window top the plane can land on and whose warm air lifts it (a thermal).</summary>
public readonly record struct PlaneTop(double X1, double X2, double Y);

public enum PlaneLanding { Flying, Floor, Window, Crash }

/// <summary>
/// A paper plane's flight: a little glider. Lift pushes it at right angles to its path, growing with the square of its
/// speed, drag slows it, gravity pulls it down; so thrown flat and fast it climbs and swoops, thrown too steep it
/// stalls (climbing past 45°, or nearly stopped, its wing gives little lift) and drops, and at its trim speed it glides
/// about four and a half lengths forward for one down. Warm air rises above the
/// window tops and lifts it; it lands on a window top it comes down onto, or on the floor (the taskbar), and a wall
/// stops it dead. UI-free, so the flight can be tested.
/// </summary>
public sealed class PaperPlaneFlight
{
    public const double Gravity = 320, TrimSpeed = 200, GlideRatio = 4.5, MaxLaunch = 440, ThermalLift = 230, ThermalHeight = 220;
    const double Lift = Gravity / (TrimSpeed * TrimSpeed), Drag = Lift / GlideRatio, Step = 1.0 / 240;

    readonly IReadOnlyList<PlaneTop> _tops;
    double _acc;

    public PaperPlaneFlight(Vec2 from, Vec2 velocity, Rect box, IReadOnlyList<PlaneTop> tops)
    {
        Pos = Start = from;
        Vel = velocity.Length > MaxLaunch ? velocity * (MaxLaunch / velocity.Length) : velocity;
        Box = box;
        _tops = tops;
    }

    public Vec2 Start { get; }
    public Vec2 Pos { get; private set; }
    public Vec2 Vel { get; private set; }
    public Rect Box { get; }
    public PlaneLanding Landed { get; private set; }
    public double Time { get; private set; }
    public bool InThermal { get; private set; }

    /// <summary>The plane's nose angle: along its path.</summary>
    public double Angle => Math.Atan2(Vel.Y, Vel.X);

    /// <summary>How far it has come from the launch, left to right.</summary>
    public double Distance => Math.Max(0, Pos.X - Start.X);

    /// <summary>Advances the flight by <paramref name="dt"/>; false once it has landed.</summary>
    public bool Advance(double dt)
    {
        if (Landed != PlaneLanding.Flying) return false;
        _acc += Math.Min(dt, 0.1);
        while (_acc >= Step && Landed == PlaneLanding.Flying)
        {
            _acc -= Step;
            StepOnce(Step);
        }
        return Landed == PlaneLanding.Flying;
    }

    /// <summary>Flies it to the end (tests, and the demo's aim).</summary>
    public void Finish()
    {
        for (int i = 0; i < 240 * 60 && Landed == PlaneLanding.Flying; i++) StepOnce(Step);
    }

    void StepOnce(double h)
    {
        Time += h;
        var v = Vel;
        double speed = v.Length;
        var acc = new Vec2(0, Gravity);
        if (speed > 1)
        {
            var dir = v / speed;
            var up = dir.X >= 0 ? new Vec2(dir.Y, -dir.X) : new Vec2(-dir.Y, dir.X); // square to the path, on the upper side
            bool stalled = v.X < 30 || -v.Y > Math.Abs(v.X); // nose up past 45°, or barely moving forward
            acc += up * (Lift * speed * speed * (stalled ? 0.25 : 1)) - dir * (Drag * speed * speed);
        }
        InThermal = false;
        foreach (var t in _tops)
        {
            double above = t.Y - Pos.Y;
            if (Pos.X < t.X1 || Pos.X > t.X2 || above < 0 || above > ThermalHeight) continue;
            acc += new Vec2(0, -ThermalLift * (1 - above / ThermalHeight)); // strongest just above the window
            InThermal = true;
        }
        var prev = Pos;
        Vel += acc * h;
        Pos += Vel * h;

        // a window top it comes down onto
        foreach (var t in _tops)
            if (Vel.Y > 0 && prev.Y <= t.Y && Pos.Y >= t.Y && Pos.X >= t.X1 && Pos.X <= t.X2)
            {
                Land(new Vec2(Pos.X, t.Y), PlaneLanding.Window);
                return;
            }
        if (Pos.Y >= Box.Bottom)
        {
            Land(new Vec2(Pos.X, Box.Bottom), PlaneLanding.Floor);
            return;
        }
        if (Pos.Y < Box.Top)
        {
            Pos = new Vec2(Pos.X, Box.Top);
            Vel = new Vec2(Vel.X, Math.Abs(Vel.Y) * 0.3); // the ceiling knocks it back down
        }
        if (Pos.X > Box.Right || Pos.X < Box.Left)
        {
            Land(new Vec2(Math.Clamp(Pos.X, Box.Left, Box.Right), Pos.Y), PlaneLanding.Crash);
        }
    }

    void Land(Vec2 at, PlaneLanding how)
    {
        Pos = at;
        Vel = default;
        Landed = how;
    }
}

/// <summary>
/// Paper Planes' scoring: a round is three throws; a throw scores its distance in metres (ten pixels to the metre at the
/// overlay's normal size), and a landing on the strip painted at the far end of the taskbar adds a bonus. A plane that
/// hits a wall scores half.
/// </summary>
public static class PaperPlaneScore
{
    public const int Throws = 3, StripBonus = 25;
    public const double PixelsPerMetre = 10;

    public static int Metres(double pixels, double scale) => (int)Math.Round(pixels / (PixelsPerMetre * Math.Max(0.1, scale)));

    public static int Points(PaperPlaneFlight f, double scale, (double X1, double X2) strip)
    {
        int m = Metres(f.Distance, scale);
        if (f.Landed == PlaneLanding.Crash) return m / 2;
        bool onStrip = f.Landed == PlaneLanding.Floor && f.Pos.X >= strip.X1 && f.Pos.X <= strip.X2;
        return m + (onStrip ? StripBonus : 0);
    }
}
