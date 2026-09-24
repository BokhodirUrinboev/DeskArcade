using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskArcade;

public sealed class Settings
{
    public string Game { get; set; } = "hoops";
    public bool Sound { get; set; } = true;
    public double Volume { get; set; } = 0.6;
    public bool Platforms { get; set; } = true;
    public bool ClaudeNotify { get; set; } = true;
    /// <summary>Show the overlay when Claude starts working.</summary>
    public bool ClaudeAutoShow { get; set; }
    /// <summary>Hide the overlay when Claude finishes or needs you.</summary>
    public bool ClaudeAutoHide { get; set; }
    /// <summary>Freeze the current game when Claude finishes or needs you; a click on the game resumes it.</summary>
    public bool ClaudePause { get; set; }
    /// <summary>"auto" (follow the system), "en", "uz" or "ru".</summary>
    public string Language { get; set; } = "auto";
    public bool CheckForUpdates { get; set; } = true;
    public DateTime? LastUpdateCheck { get; set; }
    public string? MonitorName { get; set; }

    public double? HoopX { get; set; }
    public double? HoopY { get; set; }
    public double? BowX { get; set; }
    public double? BowY { get; set; }
    public double? PlinkoX { get; set; }
    public double? PlinkoY { get; set; }
    public double? HudX { get; set; }
    public double? HudY { get; set; }
    /// <summary>Where movable boards, tables and wells were dragged to, per game id: x and y from the arena's top-left corner.</summary>
    public Dictionary<string, double[]> Positions { get; set; } = new();

    public int BestHoopsStreak { get; set; }
    public int BestHoopsScore { get; set; }
    public int BestArchery { get; set; }
    public int BestJuggle { get; set; }
    /// <summary>Best 9-hole round relative to par (lower is better); null until a round is finished.</summary>
    public int? BestGolf { get; set; }
    public int BestBugs { get; set; }
    public int BestCans { get; set; }
    public int BestBricks { get; set; }
    public int BestBubbles { get; set; }
    public int HockeyWins { get; set; }
    public bool FirstRun { get; set; } = true;
    public bool ReducedMotion { get; set; }
    public bool ColorBlind { get; set; }
    /// <summary>Colours for mallets, paddles and balls: a theme id from <see cref="Engine.Themes"/>, or "seasonal".</summary>
    public string Theme { get; set; } = "classic";
    /// <summary>
    /// CPU level per game id (1 Easy … 4 Expert); a game not listed starts at its own default (Easy for board games,
    /// Medium elsewhere). Stored under the name it had when only board games had levels, so older files still load.
    /// </summary>
    [JsonPropertyName("BoardLevels")]
    public Dictionary<string, int> Levels { get; set; } = new();
    /// <summary>Race the computer in round-based games when nobody is on the LAN: a computer rival plays a round alongside.</summary>
    public bool CpuRival { get; set; } = true;
    /// <summary>Snow, leaves, petals and the like drifting over the desktop while a game moves (tray → Theme → Theme decorations).</summary>
    public bool ThemeDecor { get; set; } = true;
    /// <summary>Remind the player to take a break after this many minutes of play; 0 turns it off.</summary>
    public int BreakMinutes { get; set; }
    /// <summary>When Claude finishes while you were playing, say "back to work" instead of "your turn".</summary>
    public bool BackToWork { get; set; }
    /// <summary>Post today's scores to the office leaderboard on the local network (opt-in: it sends the user name).</summary>
    public bool ShareLeaderboard { get; set; }
    /// <summary>The desktop pet: one of <see cref="Games.PetGame.Kinds"/> ("cat" by default).</summary>
    public string PetKind { get; set; } = "cat";
    /// <summary>A <see cref="DeskArcade.ShortcutModifiers"/> name and three letters, for show/hide, next game and bring to cursor.</summary>
    public string ShortcutModifiers { get; set; } = "CtrlAlt";
    public string ShortcutKeys { get; set; } = "GNB";

    // daily challenge (see Daily)
    public string? DailyDate { get; set; }
    public long DailyBase { get; set; }
    public bool DailyDone { get; set; }
    public string? DailyLastDone { get; set; }
    public int DailyStreak { get; set; }

    /// <summary>%APPDATA%\DeskArcade on Windows, ~/.config/DeskArcade on Linux (suffixed for --profile runs).</summary>
    public static string DataDirectory
    {
        get
        {
            // Without SpecialFolderOption.Create, Linux returns "" while ~/.config doesn't exist yet.
            string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create);
            if (string.IsNullOrEmpty(root))
                root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(root, Program.Profile.Length == 0 ? "DeskArcade" : "DeskArcade-" + Program.Profile);
        }
    }

    static string FilePath => Path.Combine(DataDirectory, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { /* corrupt file: start fresh */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort */ }
    }

    public void ResetPositions()
    {
        HoopX = HoopY = BowX = BowY = HudX = HudY = PlinkoX = PlinkoY = null;
        Positions.Clear();
    }

    public void ResetScores()
    {
        BestHoopsStreak = BestHoopsScore = BestArchery = BestJuggle = BestBugs = BestCans = 0;
        BestBricks = BestBubbles = HockeyWins = 0;
        BestGolf = null;
    }
}
