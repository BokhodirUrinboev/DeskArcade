using System;
using System.Collections.Generic;
using Avalonia;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>
/// Snakes on Windows' snake: it slides across the desktop at a steady speed and turns toward the cursor, but only so
/// fast, so it swings round in curves rather than snapping to it; near the cursor it runs straight on, so a cursor left
/// still makes it loop rather than coil up. At the screen's edges it turns to slide along them. Its body is the path its
/// head left behind, as long as the snake; an apple makes it longer and a little faster, and the head touching any part
/// of the body past its neck ends the game. UI-free (the game draws it), so it can be tested.
/// </summary>
public sealed class SnakeRules
{
    public const double Radius = 9, StartLength = 130, GrowPerApple = 28, GoldenGrowth = 2;
    public const double StartSpeed = 165, SpeedPerApple = 2.5, MaxSpeed = 330, TurnRate = 4.2, CalmRadius = 30;
    public const int ApplePoints = 10, GoldenPoints = 50;

    /// <summary>How far down the body the head can't bite: the neck bends with the head.</summary>
    public const double Neck = Radius * 5;

    const double Spacing = 3; // path points closer than this are merged

    readonly List<Vec2> _path = new(); // the head first

    public SnakeRules(Vec2 start, double heading, Rect box)
    {
        Box = box;
        Heading = heading;
        _path.Add(start);
        // start stretched out behind the head, so it has a body from the first frame
        var back = new Vec2(-Math.Cos(heading), -Math.Sin(heading));
        for (double d = Spacing; d <= StartLength; d += Spacing) _path.Add(start + back * d);
    }

    public Rect Box { get; set; }
    public IReadOnlyList<Vec2> Path => _path;
    public Vec2 Head => _path[0];
    public double Heading { get; private set; }
    public double Length { get; private set; } = StartLength;
    public int Eaten { get; private set; }
    public int GoldenEaten { get; private set; }
    public int Score { get; private set; }
    public bool Dead { get; private set; }

    public double Speed => Math.Min(MaxSpeed, StartSpeed + SpeedPerApple * Eaten);

    /// <summary>Turns <paramref name="heading"/> toward <paramref name="wanted"/> by at most <paramref name="maxStep"/>, the short way round.</summary>
    public static double Turn(double heading, double wanted, double maxStep)
    {
        double diff = Math.IEEERemainder(wanted - heading, 2 * Math.PI);
        return heading + Math.Clamp(diff, -maxStep, maxStep);
    }

    /// <summary>Moves the snake for <paramref name="dt"/> seconds toward <paramref name="target"/>; false once it has bitten itself.</summary>
    public bool Step(double dt, Vec2 target)
    {
        if (Dead) return false;
        var head = Head;
        var to = target - head;
        if (to.Length > CalmRadius) Heading = Turn(Heading, Math.Atan2(to.Y, to.X), TurnRate * dt);
        var next = head + new Vec2(Math.Cos(Heading), Math.Sin(Heading)) * (Speed * dt);

        // at the screen's edges it turns to slide along them (back the way it came would be into its own body); head-on,
        // toward the side the cursor is on
        double left = Box.Left + Radius, right = Box.Right - Radius, top = Box.Top + Radius, bottom = Box.Bottom - Radius;
        if (next.X < left || next.X > right)
        {
            double along = Math.Abs(Math.Sin(Heading)) > 0.2 ? Math.Sin(Heading) : target.Y - head.Y;
            Heading = along >= 0 ? Math.PI / 2 : -Math.PI / 2;
            next.X = Math.Clamp(next.X, left, Math.Max(left, right));
        }
        if (next.Y < top || next.Y > bottom)
        {
            double along = Math.Abs(Math.Cos(Heading)) > 0.2 ? Math.Cos(Heading) : target.X - head.X;
            Heading = along >= 0 ? 0 : Math.PI;
            next.Y = Math.Clamp(next.Y, top, Math.Max(top, bottom));
        }

        // a new point once the head is a spacing away from the last one kept; until then the head point slides forward
        if (_path.Count >= 2 && (next - _path[1]).Length < Spacing) _path[0] = next;
        else _path.Insert(0, next);
        Trim();
        if (BitesItself()) Dead = true;
        return !Dead;
    }

    /// <summary>Eats an apple at <paramref name="apple"/> if the head reaches it: longer, faster, and the points.</summary>
    public bool TryEat(Vec2 apple, double appleRadius, bool golden)
    {
        if (Dead || (apple - Head).Length > Radius + appleRadius) return false;
        Eaten++;
        if (golden) GoldenEaten++;
        Length += GrowPerApple * (golden ? GoldenGrowth : 1);
        Score += golden ? GoldenPoints : ApplePoints;
        return true;
    }

    /// <summary>Whether any part of the body past the neck is within <paramref name="clearance"/> of <paramref name="p"/>.</summary>
    public bool Near(Vec2 p, double clearance)
    {
        foreach (var q in _path)
            if ((q - p).Length < clearance) return true;
        return false;
    }

    bool BitesItself()
    {
        var head = Head;
        double along = 0, reach = Radius * 1.6;
        for (int i = 1; i < _path.Count; i++)
        {
            along += (_path[i] - _path[i - 1]).Length;
            if (along > Neck && (_path[i] - head).Length < reach) return true;
        }
        return false;
    }

    /// <summary>Cuts the path to the snake's length, the last point moved to exactly where the tail ends.</summary>
    void Trim()
    {
        double along = 0;
        for (int i = 1; i < _path.Count; i++)
        {
            double seg = (_path[i] - _path[i - 1]).Length;
            if (along + seg < Length)
            {
                along += seg;
                continue;
            }
            _path[i] = _path[i - 1] + (_path[i] - _path[i - 1]) * ((Length - along) / Math.Max(seg, 1e-9));
            _path.RemoveRange(i + 1, _path.Count - i - 1);
            return;
        }
    }
}
