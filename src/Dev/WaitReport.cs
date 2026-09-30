using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace DeskArcade.Dev;

/// <summary>How many waits, how long they took in all, and how much of that was spent playing.</summary>
public readonly record struct WaitSum(int Count, double Seconds, double Played)
{
    public static WaitSum Of(IEnumerable<WaitEntry> entries)
    {
        int count = 0;
        double seconds = 0, played = 0;
        foreach (var e in entries)
        {
            count++;
            seconds += e.Seconds;
            played += e.PlayedSeconds;
        }
        return new WaitSum(count, seconds, played);
    }
}

/// <summary>A command's runs in the week: how many and how long on average.</summary>
public sealed record CommandAverage(string Label, int Runs, double AverageSeconds);

/// <summary>A command that got slower: its average in the week against its average in the four weeks before.</summary>
public sealed record CommandTrend(string Label, int Runs, double AverageSeconds, int EarlierRuns, double EarlierAverage)
{
    /// <summary>+0.4 for 40 % slower.</summary>
    public double Change => EarlierAverage > 0 ? AverageSeconds / EarlierAverage - 1 : 0;

    public int Percent => (int)Math.Round(Change * 100, MidpointRounding.AwayFromZero);
}

/// <summary>
/// The wait report: for a week, how long builds and tests (commands run with arcade, status lanes), CI, coding agents
/// and downloads kept you waiting, how much of it you played, the slowest commands, and the ones getting slower (the
/// week's average against the four weeks before it). Built from <see cref="WaitLog"/>; a card each week, a page in
/// the stats window, and text to copy for the team's retro.
/// </summary>
public sealed class WaitReport
{
    public const int Days = 7, EarlierWeeks = 4, TopSlowest = 5, TopSlower = 3;
    /// <summary>A trend needs this many runs in the week and in the weeks before.</summary>
    public const int MinRuns = 2;
    /// <summary>Slower means at least 10 % and 5 seconds slower on average: less is noise.</summary>
    public const double MinChange = 0.10, MinSlowerSeconds = 5;

    public DateTime FromUtc { get; private init; }
    public DateTime ToUtc { get; private init; }
    /// <summary>Commands run with arcade (--while) and status lanes: builds, tests, scripts.</summary>
    public WaitSum Builds { get; private init; }
    public WaitSum Ci { get; private init; }
    public WaitSum Agents { get; private init; }
    public WaitSum Downloads { get; private init; }
    public WaitSum All { get; private init; }
    public IReadOnlyList<CommandAverage> Slowest { get; private init; } = Array.Empty<CommandAverage>();
    public IReadOnlyList<CommandTrend> Slower { get; private init; } = Array.Empty<CommandTrend>();

    public bool Empty => All.Count == 0;

    /// <summary>The share of the waiting spent playing, 0 to 1.</summary>
    public double PlayedShare => All.Seconds > 0 ? Math.Clamp(All.Played / All.Seconds, 0, 1) : 0;

    static bool IsCommand(WaitKind k) => k is WaitKind.Command or WaitKind.Lane;

    /// <summary>The report for the <see cref="Days"/> days up to (not including) <paramref name="toUtc"/>.</summary>
    public static WaitReport Build(IEnumerable<WaitEntry> entries, DateTime toUtc)
    {
        var from = toUtc.AddDays(-Days);
        var earlierFrom = from.AddDays(-Days * EarlierWeeks);
        var all = entries.ToList();
        var week = all.Where(e => e.StartUtc >= from && e.StartUtc < toUtc).ToList();
        var earlier = all.Where(e => e.StartUtc >= earlierFrom && e.StartUtc < from).ToList();

        var slowest = week.Where(e => IsCommand(e.Kind))
            .GroupBy(e => e.Label, StringComparer.OrdinalIgnoreCase)
            .Select(g => new CommandAverage(g.First().Label, g.Count(), g.Average(e => e.Seconds)))
            .OrderByDescending(c => c.AverageSeconds).ThenBy(c => c.Label, StringComparer.Ordinal)
            .Take(TopSlowest).ToList();

        // commands, lanes and CI runs can all get slower; each label is compared with itself
        var before = earlier.Where(e => IsCommand(e.Kind) || e.Kind == WaitKind.Ci)
            .GroupBy(e => e.Label, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (Runs: g.Count(), Average: g.Average(e => e.Seconds)), StringComparer.OrdinalIgnoreCase);
        var slower = week.Where(e => IsCommand(e.Kind) || e.Kind == WaitKind.Ci)
            .GroupBy(e => e.Label, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= MinRuns && before.TryGetValue(g.Key, out var b) && b.Runs >= MinRuns)
            .Select(g =>
            {
                var b = before[g.Key];
                return new CommandTrend(g.First().Label, g.Count(), g.Average(e => e.Seconds), b.Runs, b.Average);
            })
            .Where(t => t.Change >= MinChange && t.AverageSeconds - t.EarlierAverage >= MinSlowerSeconds)
            .OrderByDescending(t => t.Change).ThenBy(t => t.Label, StringComparer.Ordinal)
            .Take(TopSlower).ToList();

        return new WaitReport
        {
            FromUtc = from,
            ToUtc = toUtc,
            Builds = WaitSum.Of(week.Where(e => IsCommand(e.Kind))),
            Ci = WaitSum.Of(week.Where(e => e.Kind == WaitKind.Ci)),
            Agents = WaitSum.Of(week.Where(e => e.Kind == WaitKind.Agent)),
            Downloads = WaitSum.Of(week.Where(e => e.Kind == WaitKind.Download)),
            All = WaitSum.Of(week),
            Slowest = slowest,
            Slower = slower,
        };
    }

    /// <summary>The report for the week before the one <paramref name="todayLocal"/> is in (Monday to Sunday).</summary>
    public static WaitReport LastWeek(IEnumerable<WaitEntry> entries, DateOnly todayLocal, TimeZoneInfo zone)
    {
        var monday = Office.WorkDays.WeekOf(todayLocal);
        var end = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(monday.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified), zone);
        return Build(entries, end);
    }

    // ------------------------------------------------------------------ as text

    /// <summary>"4:10" or "1:02:03".</summary>
    public static string Clock(double seconds) => Hud.FormatWait(TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds))));

    /// <summary>"22.09–28.09", the week in local dates.</summary>
    public string Week(TimeZoneInfo zone)
    {
        var first = TimeZoneInfo.ConvertTimeFromUtc(FromUtc, zone);
        var last = TimeZoneInfo.ConvertTimeFromUtc(ToUtc.AddSeconds(-1), zone);
        return first.ToString("dd.MM", CultureInfo.InvariantCulture) + "–" + last.ToString("dd.MM", CultureInfo.InvariantCulture);
    }

    /// <summary>"dotnet test: 4:10 on average, up 40 % in a month".</summary>
    public static string TrendLine(CommandTrend t) => L.F("{0}: {1} on average, up {2} % in a month", t.Label, Clock(t.AverageSeconds), t.Percent);

    public static string SlowLine(CommandAverage c) =>
        c.Runs == 1 ? L.F("{0}: {1}, once", c.Label, Clock(c.AverageSeconds)) : L.F("{0}: {1} on average, {2} runs", c.Label, Clock(c.AverageSeconds), c.Runs);

    /// <summary>The waiting summed up by kind: "Builds and tests: 1 h 40 min (42)", one line each, the empty ones left out.</summary>
    public List<string> SumLines()
    {
        var lines = new List<string>();
        if (Builds.Count > 0) lines.Add(L.F("Builds and tests: {0} ({1})", OfficeDesk.Duration(Builds.Seconds), Builds.Count));
        if (Ci.Count > 0) lines.Add(L.F("CI runs: {0} ({1})", OfficeDesk.Duration(Ci.Seconds), Ci.Count));
        if (Agents.Count > 0) lines.Add(L.F("Coding agents: {0} ({1})", OfficeDesk.Duration(Agents.Seconds), Agents.Count));
        if (Downloads.Count > 0) lines.Add(L.F("Downloads and other waits: {0} ({1})", OfficeDesk.Duration(Downloads.Seconds), Downloads.Count));
        return lines;
    }

    /// <summary>"Played while waiting: 48 min (25 %)".</summary>
    public string PlayedLine() =>
        L.F("Played while waiting: {0} ({1} %)", OfficeDesk.Duration(All.Played), (int)Math.Round(PlayedShare * 100, MidpointRounding.AwayFromZero));

    /// <summary>The whole report as plain text, for the clipboard.</summary>
    public string Text(TimeZoneInfo zone)
    {
        var sb = new StringBuilder();
        sb.Append(L.F("Wait report · {0}", Week(zone))).Append('\n');
        if (Empty)
        {
            sb.Append(L.T("Nothing waited on")).Append('\n');
            return sb.ToString();
        }
        sb.Append(L.F("Waited {0} in all", OfficeDesk.Duration(All.Seconds))).Append('\n');
        foreach (string line in SumLines()) sb.Append("  ").Append(line).Append('\n');
        sb.Append(PlayedLine()).Append('\n');
        if (Slowest.Count > 0)
        {
            sb.Append(L.T("Slowest")).Append(":\n");
            foreach (var c in Slowest) sb.Append("  - ").Append(SlowLine(c)).Append('\n');
        }
        if (Slower.Count > 0)
        {
            sb.Append(L.T("Getting slower")).Append(":\n");
            foreach (var t in Slower) sb.Append("  - ").Append(TrendLine(t)).Append('\n');
        }
        return sb.ToString();
    }
}
