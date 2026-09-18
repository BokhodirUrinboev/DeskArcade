using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;

namespace DeskArcade.Engine;

/// <summary>
/// A colour set for the pieces players own: the mallets, paddles and puck (Air Hockey, Pong), the
/// basketball and the golf ball, the table lines and the celebration confetti. Picked in tray → Theme.
/// </summary>
public sealed record Theme(
    string Id, string Name, Color Mine, Color Rival, Color Puck, Color PuckRim, Color Line,
    Color Ball, Color BallSeam, Color GolfBall, Color[] Confetti);

public static class Themes
{
    public static readonly Theme Classic = new("classic", "Classic",
        Color.FromRgb(77, 163, 255), Color.FromRgb(255, 92, 108), Color.Parse("#1D2129"), Color.Parse("#AEB6C2"), Colors.White,
        Color.FromRgb(0xEE, 0x7A, 0x1C), Color.Parse("#3A1805"), Colors.White,
        new[] { Color.FromRgb(255, 209, 102), Color.FromRgb(77, 163, 255), Color.FromRgb(255, 92, 108), Colors.White });

    public static readonly Theme Neon = new("neon", "Neon",
        Color.FromRgb(0, 229, 255), Color.FromRgb(255, 64, 200), Color.Parse("#14002B"), Color.Parse("#B6FF3B"), Color.FromRgb(0, 229, 255),
        Color.FromRgb(255, 107, 0), Color.Parse("#2A0033"), Color.Parse("#D4FF4F"),
        new[] { Color.FromRgb(0, 229, 255), Color.FromRgb(255, 64, 200), Color.FromRgb(182, 255, 59), Colors.White });

    public static readonly Theme Retro = new("retro", "Retro",
        Color.FromRgb(120, 220, 120), Color.FromRgb(240, 200, 80), Color.Parse("#F5F0DC"), Color.Parse("#5B5B5B"), Color.FromRgb(200, 255, 200),
        Color.FromRgb(0xD9, 0xA0, 0x66), Color.Parse("#5A3A1A"), Color.Parse("#F5F0DC"),
        new[] { Color.FromRgb(120, 220, 120), Color.FromRgb(240, 200, 80), Color.FromRgb(245, 240, 220) });

    public static readonly Theme Halloween = new("halloween", "Halloween",
        Color.FromRgb(160, 90, 230), Color.FromRgb(255, 140, 30), Color.Parse("#111111"), Color.Parse("#FF8C1E"), Color.FromRgb(255, 140, 30),
        Color.FromRgb(0xFF, 0x75, 0x18), Color.Parse("#2B1100"), Color.Parse("#EDEDED"),
        new[] { Color.FromRgb(255, 140, 30), Color.FromRgb(160, 90, 230), Color.FromRgb(60, 60, 60), Colors.White });

    public static readonly Theme Winter = new("winter", "Winter",
        Color.FromRgb(120, 200, 255), Color.FromRgb(220, 40, 60), Color.Parse("#E8F4FF"), Color.Parse("#7FA7C9"), Colors.White,
        Color.FromRgb(0xEA, 0xF4, 0xFF), Color.Parse("#9DB8D0"), Colors.White,
        new[] { Colors.White, Color.FromRgb(120, 200, 255), Color.FromRgb(220, 40, 60), Color.FromRgb(200, 230, 255) });

    public static IReadOnlyList<Theme> All { get; } = new[] { Classic, Neon, Retro, Halloween, Winter };

    /// <summary>The theme in use, set by <see cref="Apply"/>.</summary>
    public static Theme Current { get; private set; } = Classic;

    /// <summary>Tray choices: every theme plus "seasonal", which follows the calendar.</summary>
    public static IEnumerable<(string Id, string Name)> Choices =>
        new[] { ("seasonal", "Seasonal") }.Concat(All.Select(t => (t.Id, t.Name)));

    /// <summary>Resolves a setting ("seasonal" or a theme id) for a date: Halloween in October, Winter in December and January.</summary>
    public static Theme Resolve(string? setting, DateTime today)
    {
        if (setting == "seasonal")
            return today.Month switch { 10 => Halloween, 12 or 1 => Winter, _ => Classic };
        return All.FirstOrDefault(t => t.Id == setting) ?? Classic;
    }

    /// <summary>Makes <paramref name="setting"/> current; true if the theme actually changed.</summary>
    public static bool Apply(string? setting, DateTime today)
    {
        var next = Resolve(setting, today);
        if (next == Current) return false;
        Current = next;
        return true;
    }
}
