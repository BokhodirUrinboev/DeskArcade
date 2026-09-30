using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskArcade;

/// <summary>
/// A sticky note on the desktop: its text, where it sits (from the arena's top-left corner) and, if it has one, when it
/// reminds. Notes stay in settings.json on this PC.
/// </summary>
public sealed class StickyNote
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Text { get; set; } = "";
    public DateTime? Due { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
}

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
    /// <summary>The pet sits with you in the other games and joins in (tray → Pet → Pet keeps me company in games); off by default.</summary>
    public bool PetCompany { get; set; }
    /// <summary>Remind the player to take a break after this many minutes of play; 0 turns it off.</summary>
    public int BreakMinutes { get; set; }
    /// <summary>When Claude finishes while you were playing, say "back to work" instead of "your turn".</summary>
    public bool BackToWork { get; set; }
    /// <summary>Post today's scores to the office leaderboard on the local network (opt-in: it sends the user name).</summary>
    public bool ShareLeaderboard { get; set; }
    /// <summary>The desktop pet: one of <see cref="Games.PetGame.Kinds"/> ("cat" by default).</summary>
    public string PetKind { get; set; } = "cat";
    /// <summary>What Typing Race and Word Rain give you to type: "auto" (the interface language), "en", "ru", "uz" or "code".</summary>
    public string TypingText { get; set; } = "auto";
    /// <summary>Chat with the co-worker on the LAN link: off until the chat is first opened (see <see cref="ChatHub"/>).</summary>
    public bool LanChat { get; set; }
    /// <summary>The Bingo of Work card in play (see <see cref="Games.BingoCard.Save"/>); null deals a new one.</summary>
    public string? Bingo { get; set; }
    /// <summary>The pet's own voice level: 0 off, 1 quiet, 2 normal, 3 loud (see <see cref="Games.PetLife.VolumeFactor"/>).</summary>
    public int PetVolume { get; set; } = Games.PetLife.DefaultVolume;
    /// <summary>When each kind of pet was first adopted (its age in the stats window, and part of growing up).</summary>
    public Dictionary<string, DateTime> PetAdopted { get; set; } = new();
    /// <summary>
    /// Naps per app the pet slept on the windows of, by process name (never window titles): the favourite is where it
    /// goes back to nap. A handful of entries at most (see <see cref="Games.PetLife.RecordNap"/>).
    /// </summary>
    public Dictionary<string, int> PetNapSpots { get; set; } = new();
    /// <summary>A <see cref="DeskArcade.ShortcutModifiers"/> name and three letters, for show/hide, next game and bring to cursor.</summary>
    public string ShortcutModifiers { get; set; } = "CtrlAlt";
    public string ShortcutKeys { get; set; } = "GNB";

    // at work (see OfficeDesk)

    /// <summary>Step aside while a full-screen app, a slide show or Windows presentation settings are on (on by default).</summary>
    public bool HideWhenFullScreen { get; set; } = true;
    /// <summary>The calendar meetings are read from: an .ics link (https, http or webcal) or an .ics file; null is off.</summary>
    public string? CalendarUrl { get; set; }
    /// <summary>Minutes before a meeting for the heads-up; 0 leaves only "a minute to go" and "starting now".</summary>
    public int MeetingWarnMinutes { get; set; } = 5;
    /// <summary>Pause the game a minute before a meeting.</summary>
    public bool MeetingPause { get; set; } = true;
    /// <summary>Hide the overlay while a meeting is on.</summary>
    public bool MeetingHide { get; set; }
    /// <summary>The 20-20-20 eye breaks.</summary>
    public bool EyeBreaks { get; set; }
    /// <summary>A stretch after this many minutes at the computer; 0 is off.</summary>
    public int StretchMinutes { get; set; }
    /// <summary>A glass of water after this many minutes at the computer; 0 is off.</summary>
    public int WaterMinutes { get; set; }
    public int FocusMinutes { get; set; } = 25;
    public int FocusBreakMinutes { get; set; } = 5;
    /// <summary>After a focus break, start the next block by itself.</summary>
    public bool FocusAuto { get; set; }
    /// <summary>Hold chat, reactions and invites while a focus block runs, and show them at the break.</summary>
    public bool FocusQuiet { get; set; } = true;
    /// <summary>The end of the working day ("18:00"), or null for no end-of-day note.</summary>
    public string? WorkEnd { get; set; }
    /// <summary>The days (yyyy-MM-dd) the end-of-day wrap-up and the "still here?" were last shown.</summary>
    public string? WorkEndWrapped { get; set; }
    public string? WorkEndNudged { get; set; }
    /// <summary>Coffee, lunch and walk invites with co-workers on the local network (opt-in: it sends the user name).</summary>
    public bool OfficeInvites { get; set; }
    /// <summary>Chime when a browser download finishes in the Downloads folder.</summary>
    public bool WatchDownloads { get; set; }
    /// <summary>The folder watched for downloads; null is the usual Downloads folder.</summary>
    public string? DownloadsFolder { get; set; }
    /// <summary>Timers still running, so a restart keeps them.</summary>
    public List<Office.DeskTimer> Timers { get; set; } = new();
    /// <summary>The sticky notes on the desktop.</summary>
    public List<StickyNote> Notes { get; set; } = new();

    // the programmer's day (see OfficeDesk.Morning.cs, OfficeDesk.Evening.cs)

    /// <summary>The morning card on the first activity of each working day: standup notes, room to focus, today's three.</summary>
    public bool MorningCard { get; set; } = true;
    /// <summary>The day (yyyy-MM-dd) the morning card was last shown, and the Monday of the week the wait report was last offered in.</summary>
    public string? MorningShown { get; set; }
    public string? WaitReportWeek { get; set; }
    /// <summary>A focus block accepted on the morning card, waiting to start (UTC), and its length in minutes.</summary>
    public DateTime? FocusAt { get; set; }
    public int FocusAtMinutes { get; set; }
    /// <summary>Today's three, under the scoreboard.</summary>
    public Office.TodaysThree Three { get; set; } = new();
    /// <summary>The end-of-day card lists repos with work not committed, pushed or stashed, half an hour earlier on Fridays.</summary>
    public bool GitBeforeYouGo { get; set; } = true;
    /// <summary>The last day (yyyy-MM-dd) the repos were all committed and pushed at the end of the day.</summary>
    public string? GitCleanOn { get; set; }
    /// <summary>Where the day went: the program in front, noted once a minute (off by default).</summary>
    public bool TrackApps { get; set; }

    // for programmers (see Dev/)

    /// <summary>
    /// Git repository folders the player added (tray → Coding agents &amp; CI → Add a repo folder…): CI and pull
    /// requests are followed for them, and the standup notes and "git before you go" read them. Empty is off.
    /// </summary>
    public List<string> Repos { get; set; } = new();

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
