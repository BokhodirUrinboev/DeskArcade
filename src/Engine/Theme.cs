using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Media;

namespace DeskArcade.Engine;

/// <summary>The light particles a theme drifts over the desktop while a game is moving (see <see cref="DecorModel"/>).</summary>
public enum Decor { None, Snow, Leaves, Petals, Bubbles, Fireflies, Stars, Confetti, Embers }

/// <summary>
/// A colour set for the whole overlay, picked in tray → Theme. The first roles are the pieces players own: the
/// mallets, paddles and puck (Air Hockey, Pong), the basketball and the golf ball, the table lines and the
/// celebration confetti. The rest dress everything shared: <see cref="Accent"/> for buttons, selection and the
/// scoreboard's border; <see cref="Gold"/> for the score and best text and the gold of popups; <see cref="HudBack"/>
/// and <see cref="HudFront"/> for the scoreboard; the board squares and frame, the card tables' felt and the card
/// backs (null keeps each game's own colours, as Classic does); <see cref="Ink"/> for the dark panels (grips, the race
/// label); the <see cref="Decor"/> drifting over the desktop and a one-line <see cref="Mood"/> shown when it is picked.
/// </summary>
public sealed record Theme(
    string Id, string Name, Color Mine, Color Rival, Color Puck, Color PuckRim, Color Line,
    Color Ball, Color BallSeam, Color GolfBall, Color[] Confetti,
    Color Accent, Color Gold, Color HudBack, Color HudFront,
    Color? BoardLight, Color? BoardDark, Color? BoardFrame, Color? Felt, Color? CardBack,
    Color Ink, Decor Decor, string Mood);

public static class Themes
{
    /// <summary>The gold the games write their popups in; <see cref="Themed"/> turns it into the current theme's gold.</summary>
    public static readonly Color ClassicGold = Color.FromRgb(255, 209, 102);

    static Color Hex(string hex) => Color.Parse(hex);

    public static readonly Theme Classic = new("classic", "Classic",
        Color.FromRgb(77, 163, 255), Color.FromRgb(255, 92, 108), Hex("#1D2129"), Hex("#AEB6C2"), Colors.White,
        Color.FromRgb(0xEE, 0x7A, 0x1C), Hex("#3A1805"), Colors.White,
        new[] { Color.FromRgb(255, 209, 102), Color.FromRgb(77, 163, 255), Color.FromRgb(255, 92, 108), Colors.White },
        Accent: Color.FromRgb(77, 163, 255), Gold: ClassicGold, HudBack: Hex("#12141C"), HudFront: Colors.White,
        BoardLight: null, BoardDark: null, BoardFrame: null, Felt: null, CardBack: null,
        Ink: Hex("#12141C"), Decor: Decor.None, Mood: "the desk as you know it");

    public static readonly Theme Neon = new("neon", "Neon",
        Color.FromRgb(0, 229, 255), Color.FromRgb(255, 64, 200), Hex("#14002B"), Hex("#B6FF3B"), Color.FromRgb(0, 229, 255),
        Color.FromRgb(255, 107, 0), Hex("#2A0033"), Hex("#D4FF4F"),
        new[] { Color.FromRgb(0, 229, 255), Color.FromRgb(255, 64, 200), Color.FromRgb(182, 255, 59), Colors.White },
        Accent: Hex("#00E5FF"), Gold: Hex("#B6FF3B"), HudBack: Hex("#14002B"), HudFront: Hex("#F4F0FF"),
        BoardLight: Hex("#3B2A66"), BoardDark: Hex("#1A0F33"), BoardFrame: Hex("#FF40C8"), Felt: Hex("#1A0F33"), CardBack: Hex("#FF40C8"),
        Ink: Hex("#14002B"), Decor: Decor.Confetti, Mood: "lights on, volume up");

    public static readonly Theme Retro = new("retro", "Retro",
        Color.FromRgb(120, 220, 120), Color.FromRgb(240, 200, 80), Hex("#F5F0DC"), Hex("#5B5B5B"), Color.FromRgb(200, 255, 200),
        Color.FromRgb(0xD9, 0xA0, 0x66), Hex("#5A3A1A"), Hex("#F5F0DC"),
        new[] { Color.FromRgb(120, 220, 120), Color.FromRgb(240, 200, 80), Color.FromRgb(245, 240, 220) },
        Accent: Hex("#78DC78"), Gold: Hex("#F0C850"), HudBack: Hex("#2B2A22"), HudFront: Hex("#F5F0DC"),
        BoardLight: Hex("#E9DFC0"), BoardDark: Hex("#8B6D3F"), BoardFrame: Hex("#4A3A22"), Felt: Hex("#3F6B3F"), CardBack: Hex("#B5433A"),
        Ink: Hex("#2B2A22"), Decor: Decor.Stars, Mood: "insert coin");

    public static readonly Theme Halloween = new("halloween", "Halloween",
        Color.FromRgb(160, 90, 230), Color.FromRgb(255, 140, 30), Hex("#111111"), Hex("#FF8C1E"), Color.FromRgb(255, 140, 30),
        Color.FromRgb(0xFF, 0x75, 0x18), Hex("#2B1100"), Hex("#EDEDED"),
        new[] { Color.FromRgb(255, 140, 30), Color.FromRgb(160, 90, 230), Color.FromRgb(60, 60, 60), Colors.White },
        Accent: Hex("#FF8C1E"), Gold: Hex("#FFC94A"), HudBack: Hex("#120A1C"), HudFront: Hex("#FFE9CC"),
        BoardLight: Hex("#4A3660"), BoardDark: Hex("#241733"), BoardFrame: Hex("#7A3E10"), Felt: Hex("#2A1740"), CardBack: Hex("#FF8C1E"),
        Ink: Hex("#120A1C"), Decor: Decor.Embers, Mood: "boo");

    public static readonly Theme Winter = new("winter", "Winter",
        Color.FromRgb(120, 200, 255), Color.FromRgb(220, 40, 60), Hex("#E8F4FF"), Hex("#7FA7C9"), Colors.White,
        Color.FromRgb(0xEA, 0xF4, 0xFF), Hex("#9DB8D0"), Colors.White,
        new[] { Colors.White, Color.FromRgb(120, 200, 255), Color.FromRgb(220, 40, 60), Color.FromRgb(200, 230, 255) },
        Accent: Hex("#78C8FF"), Gold: Hex("#F9D66B"), HudBack: Hex("#0E2238"), HudFront: Hex("#F2F8FF"),
        BoardLight: Hex("#E8F4FF"), BoardDark: Hex("#7FA7C9"), BoardFrame: Hex("#2B4A6B"), Felt: Hex("#1E4A6E"), CardBack: Hex("#DC283C"),
        Ink: Hex("#0E2238"), Decor: Decor.Snow, Mood: "mind the snow");

    public static readonly Theme Ocean = new("ocean", "Ocean",
        Hex("#33C1FF"), Hex("#FF7A59"), Hex("#0B2A4A"), Hex("#7FDBFF"), Hex("#BFEFFF"),
        Hex("#FF7A59"), Hex("#4A1A0A"), Hex("#F2FBFF"),
        new[] { Hex("#33C1FF"), Hex("#7FDBFF"), Colors.White, Hex("#FF7A59") },
        Accent: Hex("#33C1FF"), Gold: Hex("#FFC857"), HudBack: Hex("#06223A"), HudFront: Hex("#E6F7FF"),
        BoardLight: Hex("#BFE3F2"), BoardDark: Hex("#2A6F97"), BoardFrame: Hex("#0B2A4A"), Felt: Hex("#0F4C75"), CardBack: Hex("#1B6CA8"),
        Ink: Hex("#06223A"), Decor: Decor.Bubbles, Mood: "deep breath");

    public static readonly Theme Forest = new("forest", "Forest",
        Hex("#6CC24A"), Hex("#D98E3A"), Hex("#2B1B0E"), Hex("#A3C36B"), Hex("#D7E8C0"),
        Hex("#D98E3A"), Hex("#3A1805"), Hex("#F2F0E6"),
        new[] { Hex("#6CC24A"), Hex("#A3C36B"), Hex("#D98E3A"), Hex("#FFE066") },
        Accent: Hex("#6CC24A"), Gold: Hex("#FFE066"), HudBack: Hex("#14211A"), HudFront: Hex("#EAF5E4"),
        BoardLight: Hex("#C9D9A8"), BoardDark: Hex("#5E7A3A"), BoardFrame: Hex("#3B2A1A"), Felt: Hex("#1F5A3A"), CardBack: Hex("#5C3A1E"),
        Ink: Hex("#14211A"), Decor: Decor.Fireflies, Mood: "quiet under the trees");

    public static readonly Theme Sunset = new("sunset", "Sunset",
        Hex("#FF8C42"), Hex("#8E44AD"), Hex("#2D0F3A"), Hex("#FFB27A"), Hex("#FFD6B0"),
        Hex("#FF6B35"), Hex("#4A1A0A"), Hex("#FFF3E6"),
        new[] { Hex("#FF8C42"), Hex("#FF5E78"), Hex("#8E44AD"), Hex("#FFD166") },
        Accent: Hex("#FF8C42"), Gold: Hex("#FFD166"), HudBack: Hex("#2D0F3A"), HudFront: Hex("#FFF1E0"),
        BoardLight: Hex("#FFD1A8"), BoardDark: Hex("#A54A6A"), BoardFrame: Hex("#3F1A4A"), Felt: Hex("#4A1E5A"), CardBack: Hex("#FF5E78"),
        Ink: Hex("#2D0F3A"), Decor: Decor.Embers, Mood: "golden hour");

    public static readonly Theme Candy = new("candy", "Candy",
        Hex("#FF7EB6"), Hex("#5FE0C0"), Hex("#7A2E5A"), Hex("#FFD6EA"), Colors.White,
        Hex("#FF9ECF"), Hex("#7A2E5A"), Colors.White,
        new[] { Hex("#FF7EB6"), Hex("#5FE0C0"), Hex("#FFF3A0"), Colors.White },
        Accent: Hex("#FF7EB6"), Gold: Hex("#FFE27A"), HudBack: Hex("#4A2140"), HudFront: Hex("#FFF0F6"),
        BoardLight: Hex("#FFE4F1"), BoardDark: Hex("#BDEFE3"), BoardFrame: Hex("#8E4A78"), Felt: Hex("#4FB59E"), CardBack: Hex("#FF7EB6"),
        Ink: Hex("#4A2140"), Decor: Decor.Petals, Mood: "sweet");

    public static readonly Theme Mono = new("mono", "Mono",
        Colors.White, Hex("#E5322D"), Colors.Black, Colors.White, Colors.White,
        Hex("#F2F2F2"), Colors.Black, Colors.White,
        new[] { Colors.White, Hex("#E5322D"), Hex("#BFBFBF") },
        Accent: Hex("#E5322D"), Gold: Hex("#FF4A45"), HudBack: Colors.Black, HudFront: Hex("#F2F2F2"),
        BoardLight: Hex("#E8E8E8"), BoardDark: Hex("#3A3A3A"), BoardFrame: Colors.Black, Felt: Hex("#1C1C1C"), CardBack: Hex("#E5322D"),
        Ink: Colors.Black, Decor: Decor.None, Mood: "just the game");

    public static readonly Theme Spring = new("spring", "Spring",
        Hex("#6ED07A"), Hex("#FF8FB1"), Hex("#2F4F2F"), Hex("#C8F0C0"), Hex("#EFFFE8"),
        Hex("#FFB347"), Hex("#5A2A0A"), Colors.White,
        new[] { Hex("#FFB7D5"), Colors.White, Hex("#B6F0B0"), Hex("#FFF3A0") },
        Accent: Hex("#6ED07A"), Gold: Hex("#FFE066"), HudBack: Hex("#1E3A28"), HudFront: Hex("#F0FFF0"),
        BoardLight: Hex("#EAF7D8"), BoardDark: Hex("#8FCB7A"), BoardFrame: Hex("#4E7A3A"), Felt: Hex("#3C8D5A"), CardBack: Hex("#FF8FB1"),
        Ink: Hex("#1E3A28"), Decor: Decor.Petals, Mood: "fresh start");

    public static readonly Theme Autumn = new("autumn", "Autumn",
        Hex("#E07A2E"), Hex("#8B2E2E"), Hex("#3A1F0E"), Hex("#E8B86D"), Hex("#F5E6C8"),
        Hex("#D96C2A"), Hex("#3A1805"), Hex("#FFF8E6"),
        new[] { Hex("#E07A2E"), Hex("#C94E2E"), Hex("#F2C14E"), Hex("#8B5A2B") },
        Accent: Hex("#E07A2E"), Gold: Hex("#F2C14E"), HudBack: Hex("#2A1608"), HudFront: Hex("#FFF1DC"),
        BoardLight: Hex("#F2D9A6"), BoardDark: Hex("#A0522D"), BoardFrame: Hex("#4A2A10"), Felt: Hex("#7A2E1E"), CardBack: Hex("#B5461E"),
        Ink: Hex("#2A1608"), Decor: Decor.Leaves, Mood: "crunchy leaves");

    public static readonly Theme Midnight = new("midnight", "Midnight",
        Hex("#7C9CFF"), Hex("#FF6B9D"), Hex("#05060F"), Hex("#7C9CFF"), Hex("#A9B8FF"),
        Hex("#B76E3A"), Hex("#1A0A05"), Hex("#E8ECFF"),
        new[] { Colors.White, Hex("#7C9CFF"), Hex("#C3CCFF"), Hex("#FFE9A8") },
        Accent: Hex("#7C9CFF"), Gold: Hex("#FFE9A8"), HudBack: Hex("#05060F"), HudFront: Hex("#E8ECFF"),
        BoardLight: Hex("#34395F"), BoardDark: Hex("#171A33"), BoardFrame: Hex("#0B0C1E"), Felt: Hex("#0F1233"), CardBack: Hex("#2C3A8C"),
        Ink: Hex("#05060F"), Decor: Decor.Stars, Mood: "lights out");

    public static IReadOnlyList<Theme> All { get; } = new[]
    {
        Classic, Neon, Retro, Halloween, Winter, Ocean, Forest, Sunset, Candy, Mono, Spring, Autumn, Midnight,
    };

    /// <summary>The theme in use, set by <see cref="Apply"/>.</summary>
    public static Theme Current { get; private set; } = Classic;

    /// <summary>Tray choices: every theme plus "seasonal", which follows the calendar.</summary>
    public static IEnumerable<(string Id, string Name)> Choices =>
        new[] { ("seasonal", "Seasonal") }.Concat(All.Select(t => (t.Id, t.Name)));

    /// <summary>
    /// The theme the calendar picks for a date: Spring from March to May, Ocean over the summer, Autumn in September
    /// and October (Halloween in its last week, the 24th to the 31st), Forest in November, Winter in December and
    /// January and Midnight in February.
    /// </summary>
    public static Theme Seasonal(DateTime today) => today.Month switch
    {
        3 or 4 or 5 => Spring,
        6 or 7 or 8 => Ocean,
        10 when today.Day >= 24 => Halloween,
        9 or 10 => Autumn,
        11 => Forest,
        12 or 1 => Winter,
        _ => Midnight,
    };

    /// <summary>Resolves a setting ("seasonal" or a theme id) for a date; an unknown id is Classic.</summary>
    public static Theme Resolve(string? setting, DateTime today)
    {
        if (setting == "seasonal") return Seasonal(today);
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

    /// <summary>The colour a game asked for, in the current theme: the classic gold becomes the theme's gold, anything else stays.</summary>
    public static Color Themed(Color c) => c == ClassicGold ? Current.Gold : c;

    // The decorations toggle lives beside the settings file as a marker (present = off), so it needs no field in
    // Settings; it is read once and kept.

    /// <summary>Whether the theme's decorations drift over the desktop; the overlay keeps it in the settings (Settings.ThemeDecor).</summary>
    public static bool DecorEnabled { get; set; } = true;
}
