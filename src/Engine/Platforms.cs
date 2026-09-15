using System;
using System.Collections.Generic;
using Avalonia;

namespace DeskArcade.Engine;

public readonly record struct Platform(IntPtr Hwnd, double Y, double X1, double X2);

/// <summary>
/// Turns the visible top edges of other application windows into one-way platforms, so balls
/// can land on (and ride along with) e.g. the terminal window. The OS layer supplies the windows.
/// </summary>
public sealed class Platforms
{
    List<Platform> _items = new();
    Dictionary<IntPtr, Vec2> _origins = new();
    readonly Dictionary<IntPtr, Vec2> _deltas = new();

    public IReadOnlyList<Platform> Items => _items;
    public bool Enabled { get; set; } = true;
    /// <summary>Increments whenever the platform set changes.</summary>
    public int Generation { get; private set; }

    /// <param name="windows">Application windows in overlay DIPs, topmost first.</param>
    public bool Refresh(IReadOnlyList<(IntPtr Id, Rect Bounds)> windows, Rect arena)
    {
        var items = new List<Platform>();
        var origins = new Dictionary<IntPtr, Vec2>();
        if (Enabled)
        {
            for (int i = 0; i < windows.Count; i++)
            {
                var (h, r) = windows[i];
                origins[h] = new Vec2(r.X, r.Y);
                double y = r.Y;
                if (y < arena.Top + 40 || y > arena.Bottom - 30) continue;

                var segs = new List<(double a, double b)> { (Math.Max(r.Left, arena.Left), Math.Min(r.Right, arena.Right)) };
                for (int j = 0; j < i && segs.Count > 0; j++)
                {
                    var o = windows[j].Bounds;
                    if (o.Top < y - 1 && o.Bottom > y) segs = Subtract(segs, o.Left, o.Right);
                }
                foreach (var (a, b) in segs)
                    if (b - a >= 40) items.Add(new Platform(h, y, a, b));
            }
        }

        _deltas.Clear();
        foreach (var (h, p) in origins)
        {
            if (!_origins.TryGetValue(h, out var old)) continue;
            double moved = (p - old).Length;
            if (moved > 0.5 && moved < 500) _deltas[h] = p - old;
        }
        _origins = origins;

        bool changed = !SameAs(items);
        _items = items;
        if (changed) Generation++;
        return changed;
    }

    /// <summary>How far a window moved during the latest refresh.</summary>
    public Vec2 DeltaOf(IntPtr hwnd) => _deltas.TryGetValue(hwnd, out var d) ? d : default;

    /// <summary>One-way landing test for a falling circle whose bottom moved from prevBottom to bottom.</summary>
    public bool FindLanding(double x, double prevBottom, double bottom, out Platform hit)
    {
        hit = default;
        if (!Enabled) return false;
        double best = double.MaxValue;
        foreach (var p in _items)
        {
            if (x < p.X1 || x > p.X2) continue;
            if (prevBottom <= p.Y + 2 && bottom >= p.Y && p.Y < best)
            {
                best = p.Y;
                hit = p;
            }
        }
        return best < double.MaxValue;
    }

    /// <summary>First platform crossed from above by the segment a→b (used for arrows).</summary>
    public bool FindCrossing(Vec2 a, Vec2 b, out Platform hit, out Vec2 at)
    {
        hit = default;
        at = default;
        if (!Enabled || b.Y <= a.Y) return false;
        double bestT = double.MaxValue;
        foreach (var p in _items)
        {
            if (a.Y > p.Y || b.Y < p.Y) continue;
            double t = (p.Y - a.Y) / (b.Y - a.Y);
            double x = a.X + (b.X - a.X) * t;
            if (x < p.X1 || x > p.X2 || t >= bestT) continue;
            bestT = t;
            hit = p;
            at = new Vec2(x, p.Y);
        }
        return bestT < double.MaxValue;
    }

    bool SameAs(List<Platform> other)
    {
        if (other.Count != _items.Count) return false;
        for (int i = 0; i < other.Count; i++)
        {
            var a = other[i];
            var b = _items[i];
            if (a.Hwnd != b.Hwnd || Math.Abs(a.Y - b.Y) > 0.5 || Math.Abs(a.X1 - b.X1) > 0.5 || Math.Abs(a.X2 - b.X2) > 0.5)
                return false;
        }
        return true;
    }

    static List<(double, double)> Subtract(List<(double a, double b)> segs, double cutA, double cutB)
    {
        var result = new List<(double, double)>(segs.Count + 1);
        foreach (var (a, b) in segs)
        {
            if (cutB <= a || cutA >= b) { result.Add((a, b)); continue; }
            if (cutA > a) result.Add((a, cutA));
            if (cutB < b) result.Add((cutB, b));
        }
        return result;
    }
}
