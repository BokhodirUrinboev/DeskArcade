using System;
using System.Collections.Generic;
using Avalonia;
using DeskArcade.Engine;

namespace DeskArcade.Games;

public enum MarblePieceKind { Ramp, Bumper }

/// <summary>A piece the player placed: a ramp (a tilted bar the marble rolls along) or a bumper (a post that kicks it away).</summary>
public sealed class MarblePiece
{
    public MarblePieceKind Kind;
    public Vec2 Pos;
    /// <summary>A ramp's tilt in degrees, 0 = level, positive = down to the right (screen y grows downwards).</summary>
    public double Angle;

    public MarblePiece(MarblePieceKind kind, Vec2 pos, double angle = 0)
    {
        Kind = kind;
        Pos = pos;
        Angle = angle;
    }

    public Vec2 Dir => new(Math.Cos(Angle * Math.PI / 180), Math.Sin(Angle * Math.PI / 180));
    /// <summary>A ramp's two ends; <see cref="B"/> carries the knob that turns it.</summary>
    public Vec2 A => Pos - Dir * MarbleRules.RampHalf;
    public Vec2 B => Pos + Dir * MarbleRules.RampHalf;
}

public enum MarbleOutcome { None, Cup, Miss }

/// <summary>What one physics step did, for the sounds and the flashes.</summary>
public struct MarbleEvents
{
    public double Floor;     // strongest landing speed on a window top or the taskbar
    public double Wall;      // strongest wall impact
    public int Piece;        // the piece hit hardest this step, or −1
    public double PieceSpeed;
    public bool LippedOut;   // over the cup, but too fast to drop in
    public MarbleOutcome Outcome;
}

/// <summary>
/// Marble Run, free of UI. A course is a drop point near the top of the screen and a cup sunk into the taskbar
/// somewhere else. Before a release the player may place up to <see cref="Budget"/> pieces: ramps, which the
/// marble rolls along, and bumpers, which kick it away. Released, the marble falls, lands on window tops (one-way
/// platforms, as in the other ball games), rolls on with the momentum it brought, drops off their edges, bounces
/// off the pieces and the screen's sides, and drops into the cup when it passes over the mouth slowly enough. It
/// misses when it comes to rest anywhere else (or wanders too long). A sunk marble scores <see cref="SinkPoints"/>,
/// plus <see cref="SparePoints"/> for each piece left unused, less <see cref="MissPenalty"/> for each earlier miss
/// on the course; after <see cref="Tries"/> misses the course is lost. A round is <see cref="Courses"/> courses.
/// </summary>
public sealed class MarbleRules
{
    public const double R = 9, Gravity = 1400, MaxSpeed = 1600, AirDrag = 0.02, Restitution = 0.35, WallRestitution = 0.6;
    /// <summary>Rolling friction on window tops and the taskbar, per second: a marble rolling at v comes to rest about v / RollFriction further on.</summary>
    public const double RollFriction = 1.2;
    public const double RampHalf = 55, RampThick = 4, RampBounce = 0.2, RampFriction = 0.35;
    public const double BumperR = 16, BumperKick = 520;
    public const double CupHalf = 20, CatchSpeed = 700;
    public const double StillSpeed = 14, StillTime = 0.5, MaxRun = 25, Step = 1.0 / 240;
    public const int Budget = 3, Courses = 5, Tries = 3, SinkPoints = 100, SparePoints = 25, MissPenalty = 25;

    /// <summary>The most a round can score: every course sunk at the first release without a piece.</summary>
    public const int MaxRound = Courses * (SinkPoints + SparePoints * Budget);

    readonly List<MarblePiece> _pieces = new();
    double _still;

    public MarbleRules(Rect arena) => Arena = arena;

    /// <summary>The closed box the marble lives in: walls left and right, a ceiling, the taskbar as the floor.</summary>
    public Rect Arena { get; set; }
    public Vec2 Drop { get; set; }
    public double CupX { get; set; }
    public double CupY => Arena.Bottom;
    public IReadOnlyList<MarblePiece> Pieces => _pieces;
    public int PiecesLeft => Budget - _pieces.Count;

    public Vec2 Pos, Vel;
    /// <summary>Degrees the marble has turned, for drawing it roll.</summary>
    public double Angle;
    public bool Running { get; private set; }
    public bool Grounded { get; private set; }
    public double RunTime { get; private set; }

    /// <summary>The course being played, 1 to <see cref="Courses"/>; 0 before the first round.</summary>
    public int Course { get; private set; }
    /// <summary>Misses so far on this course.</summary>
    public int Misses { get; private set; }
    public int Score { get; private set; }
    public int Sunk { get; private set; }
    /// <summary>Courses in a row sunk at the first release (it carries on into the next round).</summary>
    public int Streak { get; private set; }
    public bool RoundOver => Course > Courses;

    // ------------------------------------------------------------------ courses

    /// <summary>A fresh round: the score goes back to 0 and the first course is laid out.</summary>
    public void NewRound(Random rng, Rect avoid = default)
    {
        Score = 0;
        Sunk = 0;
        Course = 1;
        Plan(rng, avoid);
    }

    /// <summary>
    /// Lays out a course: the drop point somewhere along the top, clear of <paramref name="avoid"/> (the scoreboard),
    /// and the cup on the taskbar a fair way off to one side, so the marble has to be steered there.
    /// </summary>
    public void Plan(Random rng, Rect avoid = default)
    {
        var a = Arena;
        double margin = Math.Min(90, a.Width / 6), y = a.Top + Math.Min(70, a.Height / 8);
        double x = a.Center.X;
        for (int i = 0; i < 30; i++)
        {
            x = a.Left + margin + rng.NextDouble() * Math.Max(1, a.Width - margin * 2);
            if (!avoid.Inflate(60).Contains(new Point(x, y))) break;
        }
        Drop = new Vec2(x, y);
        double gap = Math.Min(320, a.Width * 0.25), cupMargin = Math.Min(60, a.Width / 8);
        double cup = a.Center.X;
        for (int i = 0; i < 30; i++)
        {
            cup = a.Left + cupMargin + rng.NextDouble() * Math.Max(1, a.Width - cupMargin * 2);
            if (Math.Abs(cup - x) >= gap && !avoid.Inflate(20).Contains(new Point(cup, a.Bottom - 30))) break;
        }
        CupX = cup;
        Misses = 0;
        _pieces.Clear();
        Rest();
    }

    /// <summary>After a sunk or lost course: on to the next one, or the round is over.</summary>
    public void NextCourse(Random rng, Rect avoid = default)
    {
        Course++;
        Misses = 0;
        if (RoundOver)
        {
            _pieces.Clear();
            Rest();
            return;
        }
        Plan(rng, avoid);
    }

    /// <summary>Points for sinking the marble with <paramref name="piecesUsed"/> pieces after <paramref name="misses"/> misses.</summary>
    public static int CourseScore(int piecesUsed, int misses) =>
        SinkPoints + SparePoints * Math.Max(0, Budget - piecesUsed) - MissPenalty * Math.Min(misses, Tries - 1);

    /// <summary>The marble is in the cup: scores the course (the streak grows only on a first release).</summary>
    public int Sink()
    {
        int points = CourseScore(_pieces.Count, Misses);
        Score += points;
        Sunk++;
        Streak = Misses == 0 ? Streak + 1 : 0;
        return points;
    }

    /// <summary>
    /// The marble came to rest outside the cup (it stays where it stopped until <see cref="Rest"/> brings it back). True
    /// when that was the last try and the course is lost.
    /// </summary>
    public bool Miss()
    {
        Misses++;
        Streak = 0;
        return Misses >= Tries;
    }

    // ------------------------------------------------------------------ pieces

    public bool CanPlace => !Running && _pieces.Count < Budget;

    public MarblePiece? Place(MarblePieceKind kind, Vec2 at, double angle = 0)
    {
        if (!CanPlace) return null;
        var p = new MarblePiece(kind, Keep(at), angle);
        _pieces.Add(p);
        return p;
    }

    public bool Remove(MarblePiece p) => !Running && _pieces.Remove(p);

    /// <summary>Where a piece may stand: inside the arena and off the taskbar.</summary>
    public Vec2 Keep(Vec2 at)
    {
        var a = Arena;
        return new Vec2(Math.Clamp(at.X, a.Left + 10, Math.Max(a.Left + 10, a.Right - 10)), Math.Clamp(at.Y, a.Top + 10, Math.Max(a.Top + 10, a.Bottom - 24)));
    }

    /// <summary>
    /// A ramp that sends the marble toward the cup: just under the drop point, tilted down toward the cup's side. A
    /// starting point for the demo; whether it gets there depends on the windows in the way.
    /// </summary>
    public (Vec2 Pos, double Angle) SuggestRamp(double tilt = 24)
    {
        int side = CupX >= Drop.X ? 1 : -1;
        return (Keep(Drop + new Vec2(side * RampHalf * 0.6, 110)), side * tilt);
    }

    // ------------------------------------------------------------------ the run

    /// <summary>The marble waits in the drop point.</summary>
    public void Rest()
    {
        Running = false;
        Grounded = false;
        Pos = Drop;
        Vel = default;
        RunTime = 0;
        _still = 0;
    }

    /// <summary>Lets the marble go from the drop point; false while one is already running or the round is over.</summary>
    public bool Release()
    {
        if (Running || RoundOver) return false;
        Rest();
        Vel = new Vec2(0, 40);
        Running = true;
        return true;
    }

    /// <summary>The first thing below <paramref name="from"/>: a window top or the taskbar (for the drop point's plumb line).</summary>
    public double SurfaceBelow(Vec2 from, IReadOnlyList<Engine.Platform> tops)
    {
        double best = Arena.Bottom;
        foreach (var p in tops)
            if (from.X >= p.X1 && from.X <= p.X2 && p.Y > from.Y + R && p.Y < best) best = p.Y;
        return best;
    }

    /// <summary>One fixed physics step of <see cref="Step"/> seconds (or <paramref name="h"/>).</summary>
    public MarbleEvents Advance(IReadOnlyList<Engine.Platform> tops, double h = Step)
    {
        var ev = new MarbleEvents { Piece = -1 };
        if (!Running) return ev;
        RunTime += h;
        var a = Arena;
        double prevBottom = Pos.Y + R;
        Vel.Y += Gravity * h;
        Vel *= 1 - AirDrag * h;
        double speed = Vel.Length;
        if (speed > MaxSpeed) Vel *= MaxSpeed / speed;
        Pos += Vel * h;
        Grounded = false;
        bool touching = false;

        // the screen's sides and top
        if (Pos.X - R < a.Left)
        {
            Pos.X = a.Left + R;
            if (Vel.X < 0) { ev.Wall = -Vel.X; Vel.X = -Vel.X * WallRestitution; }
        }
        else if (Pos.X + R > a.Right)
        {
            Pos.X = a.Right - R;
            if (Vel.X > 0) { ev.Wall = Vel.X; Vel.X = -Vel.X * WallRestitution; }
        }
        if (Pos.Y - R < a.Top)
        {
            Pos.Y = a.Top + R;
            if (Vel.Y < 0) { ev.Wall = Math.Max(ev.Wall, -Vel.Y); Vel.Y = -Vel.Y * WallRestitution; }
        }

        // the placed pieces
        for (int i = 0; i < _pieces.Count; i++)
        {
            var p = _pieces[i];
            double hit = p.Kind == MarblePieceKind.Ramp ? HitRamp(p, h, ref touching) : HitBumper(p);
            if (hit > ev.PieceSpeed)
            {
                ev.PieceSpeed = hit;
                ev.Piece = i;
            }
        }

        // window tops (landing from above only) and the taskbar
        double groundY = double.NaN;
        if (Vel.Y >= 0)
        {
            double best = double.MaxValue;
            foreach (var p in tops)
                if (Pos.X >= p.X1 && Pos.X <= p.X2 && prevBottom <= p.Y + 2 && Pos.Y + R >= p.Y && p.Y < best) best = p.Y;
            if (best < double.MaxValue) groundY = best;
        }
        if (double.IsNaN(groundY) && Pos.Y + R >= a.Bottom)
        {
            groundY = a.Bottom;
            // the cup's mouth: a marble passing over it slowly enough drops in
            if (Math.Abs(Pos.X - CupX) <= CupHalf - R * 0.4)
            {
                if (Math.Abs(Vel.X) < CatchSpeed)
                {
                    Pos = new Vec2(CupX, a.Bottom - R * 0.4);
                    Vel = default;
                    Running = false;
                    ev.Outcome = MarbleOutcome.Cup;
                    return ev;
                }
                ev.LippedOut = true;
            }
        }
        if (!double.IsNaN(groundY))
        {
            Pos.Y = groundY - R;
            if (Vel.Y > 0)
            {
                ev.Floor = Vel.Y;
                Vel.Y = -Vel.Y * Restitution;
                if (Math.Abs(Vel.Y) < 90) Vel.Y = 0;
            }
            Grounded = true;
            touching = true;
            Vel.X *= 1 - Math.Min(1, RollFriction * h);
        }

        Angle = (Angle + Vel.X / R * 180 / Math.PI * h) % 360;

        // at rest somewhere other than the cup, or wandering for too long: a miss
        if (touching && Vel.Length < StillSpeed) _still += h;
        else _still = 0;
        if (_still >= StillTime || RunTime >= MaxRun)
        {
            Running = false;
            ev.Outcome = MarbleOutcome.Miss;
        }
        return ev;
    }

    /// <summary>A ramp is a bar with rounded ends: the marble bounces a little, then rolls along it.</summary>
    double HitRamp(MarblePiece p, double h, ref bool touching)
    {
        Vec2 a = p.A, ab = p.B - a;
        double t = Math.Clamp(Vec2.Dot(Pos - a, ab) / ab.LengthSquared, 0, 1);
        Vec2 c = a + ab * t, d = Pos - c;
        double min = R + RampThick, dist = d.Length;
        if (dist >= min || dist < 1e-6) return 0;
        Vec2 n = d / dist;
        Pos = c + n * min;
        touching = true;
        double vn = Vec2.Dot(Vel, n);
        if (vn >= 0) return 0;
        Vel -= n * ((1 + (-vn > 80 ? RampBounce : 0)) * vn); // a resting marble just stays on the bar
        Vec2 tan = new(-n.Y, n.X);
        Vel -= tan * (Vec2.Dot(Vel, tan) * Math.Min(1, RampFriction * h));
        return -vn;
    }

    /// <summary>A bumper throws the marble off at no less than <see cref="BumperKick"/>.</summary>
    double HitBumper(MarblePiece p)
    {
        Vec2 d = Pos - p.Pos;
        double min = R + BumperR, dist = d.Length;
        if (dist >= min) return 0;
        Vec2 n = dist < 1e-6 ? new Vec2(0, -1) : d / dist;
        Pos = p.Pos + n * min;
        double vn = Vec2.Dot(Vel, n);
        if (vn >= 0 && dist >= 1e-6) return 0;
        Vel -= n * (2 * vn);
        double outward = Vec2.Dot(Vel, n);
        if (outward < BumperKick) Vel += n * (BumperKick - outward);
        return Math.Max(-vn, 1);
    }
}
