using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DeskArcade.Dev;

namespace DeskArcade;

/// <summary>
/// Your year at the desk: from 15 December, once a year, a card of the year so far, to keep or share as a picture. The
/// waits and the rivalries are the year's own; counters that are only kept as totals (focus blocks, water) count from
/// the snapshot taken at the start of the year, or from the start when there is none yet (the first year).
/// </summary>
public static class YearInReview
{
    /// <summary>The counters snapshotted at the turn of the year, so the next card can say what the year added.</summary>
    public static readonly string[] Counters = { "work.focus", "work.water", "claude.done", "task.done", "lan.wins", "daily.done" };

    public const int FromDay = 15;

    /// <summary>Whether the card is due: on or after 15 December, and not shown yet this year.</summary>
    public static bool Due(DateTime nowLocal, int? shownYear) => nowLocal.Month == 12 && nowLocal.Day >= FromDay && shownYear != nowLocal.Year;

    /// <summary>Whether the menu offers the card: from 15 December to the end of January (the year just gone, then).</summary>
    public static bool InSeason(DateTime nowLocal) => nowLocal.Month == 12 && nowLocal.Day >= FromDay || nowLocal.Month == 1;

    /// <summary>The year the card is about: this one in December, last year in January.</summary>
    public static int YearOf(DateTime nowLocal) => nowLocal.Month == 1 ? nowLocal.Year - 1 : nowLocal.Year;

    /// <summary>
    /// The lines of the card for <paramref name="year"/>, each only when there is something to say.
    /// <paramref name="counter"/> reads a stats counter's total, <paramref name="yearBase"/> the snapshot from the start
    /// of the year, <paramref name="secondsPlayed"/> the time played in a game (by id, with its title).
    /// </summary>
    public static List<string> Lines(int year, Func<string, long> counter, IReadOnlyDictionary<string, long>? yearBase, WaitLog waits, Rivalries rivals,
        IEnumerable<(string Id, string Title, double Seconds)> games, DateTime? petAdopted, int achievements, int achievementsTotal, DateTime nowUtc)
    {
        var lines = new List<string>();
        var from = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Local).ToUniversalTime();
        var to = new DateTime(year + 1, 1, 1, 0, 0, 0, DateTimeKind.Local).ToUniversalTime();
        var yearWaits = waits.Between(from, to).ToList();

        double played = yearWaits.Sum(w => w.PlayedSeconds), waited = yearWaits.Sum(w => w.Seconds);
        if (yearWaits.Count > 0)
            lines.Add(L.F("Waited on builds, tests, CI and agents: {0} over {1} waits, and played {2} of it", Long(waited), yearWaits.Count, Long(played)));
        if (yearWaits.Where(w => w.Kind is WaitKind.Command or WaitKind.Lane or WaitKind.Ci).OrderByDescending(w => w.Seconds).FirstOrDefault() is { } longest)
            lines.Add(L.F("The longest wait: {0}, {1}", longest.Label, Long(longest.Seconds)));

        if (games.Where(g => g.Seconds >= 60).OrderByDescending(g => g.Seconds).FirstOrDefault() is { Id: not null } favourite)
            lines.Add(L.F("Favourite game: {0}, {1} played", favourite.Title, Long(favourite.Seconds)));

        var yearResults = rivals.Results.Where(m => m.AtUtc >= from && m.AtUtc < to).ToList();
        if (yearResults.GroupBy(m => m.Opponent, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).FirstOrDefault() is { } rival)
            lines.Add(L.F("Best rival: {0}, {1}–{2} in {3} games", rival.First().Opponent, rival.Count(m => m.Outcome > 0), rival.Count(m => m.Outcome < 0), rival.Count()));

        long Year(string key) => Math.Max(0, counter(key) - (yearBase != null && yearBase.TryGetValue(key, out long b) ? b : 0));
        long focus = Year("work.focus"), water = Year("work.water"), claude = Year("claude.done"), daily = Year("daily.done");
        if (focus > 0 || water > 0) lines.Add(L.F("Focus blocks: {0} · glasses of water: {1}", focus, water));
        if (claude > 0) lines.Add(L.F("Claude finished {0} times while you played", claude));
        if (daily > 0) lines.Add(L.F("Daily challenges done: {0}", daily));
        if (petAdopted is DateTime adopted && adopted <= nowUtc)
            lines.Add(L.F("Your pet has kept you company for {0} days", (int)(nowUtc - adopted).TotalDays));
        lines.Add(L.F("Achievements: {0} of {1}", achievements, achievementsTotal));
        return lines;
    }

    /// <summary>"3 h 20 min", "12 min" or "45 s".</summary>
    public static string Long(double seconds)
    {
        if (seconds < 60) return L.F("{0} s", (int)Math.Round(seconds));
        int minutes = (int)(seconds / 60);
        return minutes >= 60 ? L.F("{0} h {1} min", minutes / 60, minutes % 60) : L.F("{0} min", minutes);
    }

    /// <summary>The year's title, "Your 2026 at the desk".</summary>
    public static string Title(int year) => L.F("Your {0} at the desk", year.ToString(CultureInfo.InvariantCulture));
}
