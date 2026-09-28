using System;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>
/// A kite on a string (overlay DIPs, y down). The wind, as the kite feels it (the wind less its own speed), pushes it
/// downwind and lifts it square to that; gravity pulls it down; the string, held at the anchor, only ever pulls. A taut
/// string is what keeps the kite up: tied down, it cannot run with the wind, so the air keeps flowing past and lifting
/// it. Pulling the anchor away from the kite makes it climb; slack lets it sink. While the string is taut, more line
/// runs out while the kite stands high, up to <see cref="MaxLine"/>. UI-free, so the flight can be tested.
/// </summary>
public sealed class KiteFlight
{
    public const double Gravity = 150, Lift = 0.065, Drag = 0.03, StartLine = 220, LineOut = 15, PayOutAbove = 45, MaxSpeed = 900, TensionFull = 200;

    Vec2 _anchor;

    /// <summary>A kite launched from <paramref name="anchor"/>: up and downwind on a taut, short line.</summary>
    public KiteFlight(Vec2 anchor, double maxLine)
    {
        _anchor = anchor;
        MaxLine = Math.Max(StartLine, maxLine);
        Line = StartLine;
        Pos = anchor + new Vec2(StartLine * 0.7, -StartLine * 0.7);
    }

    public Vec2 Pos { get; private set; }
    public Vec2 Vel { get; private set; }
    public double Line { get; private set; }
    public double MaxLine { get; set; }
    public bool Taut { get; private set; }
    public Vec2 Anchor => _anchor;

    /// <summary>How hard the string pulls, 0 (slack) to 1 (<see cref="TensionFull"/> or more), smoothed: the sag of the line, the kite's tilt.</summary>
    public double Tension { get; private set; }

    /// <param name="wind">Horizontal wind in DIPs a second, to the right when positive.</param>
    /// <param name="anchor">Where the string is held now (it may have moved since the last step).</param>
    public void Step(double dt, double wind, Vec2 anchor)
    {
        if (dt <= 0) return;
        var anchorVel = (anchor - _anchor) / dt;
        _anchor = anchor;
        var u = new Vec2(wind, 0) - Vel; // the wind as the kite feels it
        double s = u.Length;
        var n = new Vec2(-u.Y, u.X);
        if (n.Y > 0) n = -n; // the kite is angled to be lifted, whichever way the air comes
        var force = u * (Drag * s) + n * (Lift * s) + new Vec2(0, Gravity);
        Vel += force * dt;
        if (Vel.Length > MaxSpeed) Vel = Vel * (MaxSpeed / Vel.Length);
        Pos += Vel * dt;

        var d = Pos - anchor;
        double len = d.Length, pull = 0;
        Taut = len >= Line - 2;
        if (len > 1e-6)
        {
            var dir = d / len;
            if (Taut) pull = Vec2.Dot(force, dir); // what the air and gravity pull along the line: the string holds it
            if (len > Line)
            {
                Pos = anchor + dir * Line;
                double away = Vec2.Dot(Vel - anchorVel, dir);
                if (away > 0) Vel -= dir * away; // the string stops it running away from the hand
            }
        }
        Tension = Math.Clamp(Tension + (Math.Clamp(pull / TensionFull, 0, 1) - Tension) * Math.Min(1, dt * 5), 0, 1);
        if (Taut && Line < MaxLine && Elevation > PayOutAbove) Line = Math.Min(MaxLine, Line + LineOut * dt); // a kite standing high takes more line
    }

    /// <summary>Keeps the kite inside the box (walls and ceiling stop it; the floor is the game's to judge).</summary>
    public void Confine(double left, double right, double top)
    {
        var p = Pos;
        var v = Vel;
        if (p.X < left) { p = new Vec2(left, p.Y); v = new Vec2(Math.Max(0, v.X), v.Y); }
        if (p.X > right) { p = new Vec2(right, p.Y); v = new Vec2(Math.Min(0, v.X), v.Y); }
        if (p.Y < top) { p = new Vec2(p.X, top); v = new Vec2(v.X, Math.Max(0, v.Y)); }
        Pos = p;
        Vel = v;
    }

    /// <summary>The angle of the string from the hand to the kite, in degrees above the horizontal (downwind positive).</summary>
    public double Elevation => Math.Atan2(_anchor.Y - Pos.Y, Pos.X - _anchor.X) * 180 / Math.PI;
}

/// <summary>
/// The desk fan's wind: a breeze that swells and fades, with a gust now and then (the kite climbs) and a lull (it sags,
/// and wants pulling). Seeded, so a flight's weather can be replayed in a test.
/// </summary>
public sealed class KiteWind
{
    public const double Base = 85, Swell = 18, GustExtra = 55, LullDrop = 45;

    readonly Random _rng;
    double _t, _next, _eventT, _eventLen;

    public KiteWind(Random rng)
    {
        _rng = rng;
        _next = 4 + rng.NextDouble() * 3;
    }

    /// <summary>+1 during a gust, -1 during a lull, 0 otherwise.</summary>
    public int Event { get; private set; }

    /// <summary>The wind now, in DIPs a second.</summary>
    public double Speed { get; private set; } = Base;

    public double Step(double dt)
    {
        _t += dt;
        if (Event != 0)
        {
            _eventT += dt;
            if (_eventT >= _eventLen)
            {
                Event = 0;
                _next = 3.5 + _rng.NextDouble() * 4.5;
            }
        }
        else if ((_next -= dt) <= 0)
        {
            Event = _rng.NextDouble() < 0.6 ? 1 : -1;
            _eventT = 0;
            _eventLen = Event > 0 ? 1.6 + _rng.NextDouble() : 2 + _rng.NextDouble();
        }
        double shape = Event == 0 ? 0 : Math.Sin(Math.PI * Math.Clamp(_eventT / _eventLen, 0, 1)); // rises and falls away
        Speed = Base + Swell * Math.Sin(_t * 0.7) + (Event > 0 ? GustExtra : -LullDrop) * shape * Math.Abs(Event);
        return Speed;
    }
}

/// <summary>
/// One flight: a minute in the air, clouds worth <see cref="CloudPoints"/> and stars worth <see cref="StarPoints"/>,
/// and every fifth catch without a crash in between pays a <see cref="StreakBonus"/>.
/// </summary>
public sealed class KiteRound
{
    public const double Seconds = 60;
    public const int CloudPoints = 10, StarPoints = 25, StreakBonus = 20, StreakEvery = 5;

    public double Time { get; private set; }
    public int Score { get; private set; }
    public int Clouds { get; private set; }
    public int Stars { get; private set; }
    public int Crashes { get; private set; }
    public int Streak { get; private set; }
    public bool Over => Time >= Seconds;
    public double Left => Math.Max(0, Seconds - Time);

    public void Tick(double dt)
    {
        if (!Over) Time = Math.Min(Seconds, Time + dt);
    }

    /// <summary>A cloud or a star caught: the points it paid, with the streak bonus when it completes one.</summary>
    public (int Points, bool StreakDone) Catch(bool star)
    {
        if (Over) return (0, false);
        if (star) Stars++;
        else Clouds++;
        Streak++;
        int points = star ? StarPoints : CloudPoints;
        bool streak = Streak % StreakEvery == 0;
        if (streak) points += StreakBonus;
        Score += points;
        return (points, streak);
    }

    public void Crash()
    {
        if (Over) return;
        Crashes++;
        Streak = 0;
    }
}
