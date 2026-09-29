using System;
using System.Collections.Generic;
using System.Linq;

namespace DeskArcade.Office;

/// <summary>What the overlay does about a meeting: warn ahead, pause the game a minute before, say it has started.</summary>
public enum MeetingCue { Warn, Soon, Start }

/// <summary>
/// The meetings of the next day or so, and which heads-ups are due: a warning <c>warnMinutes</c> before, "a minute to
/// go" (the game pauses then) and "starting now". Each is given once per occurrence, however often the calendar is
/// read again; one that is late (Desk Arcade started half a minute before a meeting) gives only the latest cue that
/// still applies.
/// </summary>
public sealed class MeetingWatch
{
    /// <summary>How long after its start a meeting still gets its "starting now".</summary>
    public static readonly TimeSpan StartGrace = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan SoonBefore = TimeSpan.FromMinutes(1);

    readonly HashSet<string> _given = new();
    List<Meeting> _meetings = new();

    public IReadOnlyList<Meeting> Meetings => _meetings;

    /// <summary>A fresh read of the calendar. Heads-ups already given stay given.</summary>
    public void Update(IEnumerable<Meeting> meetings) =>
        _meetings = meetings.OrderBy(m => m.Start).ThenBy(m => m.Title, StringComparer.Ordinal).ToList();

    /// <summary>The meeting going on now (started, not over), or null.</summary>
    public Meeting? Current(DateTime nowUtc) =>
        _meetings.LastOrDefault(m => m.Start <= nowUtc && (m.End > m.Start ? nowUtc < m.End : nowUtc < m.Start + StartGrace));

    /// <summary>The next meeting that has not started yet, or null.</summary>
    public Meeting? Next(DateTime nowUtc) => _meetings.FirstOrDefault(m => m.Start > nowUtc);

    /// <summary>The meetings still ahead today (local time), for the At work window.</summary>
    public IEnumerable<Meeting> LaterToday(DateTime nowUtc, TimeZoneInfo local)
    {
        var today = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, local).Date;
        return _meetings.Where(m => m.End > nowUtc || m.Start > nowUtc)
            .Where(m => TimeZoneInfo.ConvertTimeFromUtc(m.Start, local).Date == today);
    }

    /// <summary>The cues due at <paramref name="nowUtc"/>; <paramref name="warnMinutes"/> 0 gives only "a minute to go" and "starting now".</summary>
    public List<(MeetingCue Cue, Meeting Meeting)> Step(DateTime nowUtc, int warnMinutes)
    {
        var due = new List<(MeetingCue, Meeting)>();
        foreach (var m in _meetings)
        {
            if (m.Start > nowUtc + TimeSpan.FromMinutes(Math.Max(warnMinutes, 1)) + TimeSpan.FromSeconds(1)) break;
            MeetingCue? cue = null;
            if (nowUtc >= m.Start)
            {
                if (nowUtc < m.Start + StartGrace) cue = MeetingCue.Start;
            }
            else if (nowUtc >= m.Start - SoonBefore) cue = MeetingCue.Soon;
            else if (warnMinutes > 0 && nowUtc >= m.Start - TimeSpan.FromMinutes(warnMinutes)) cue = MeetingCue.Warn;
            if (cue is not MeetingCue c || _given.Contains(Id(c, m))) continue;
            // a later cue stands in for the earlier ones it overtook
            for (var earlier = MeetingCue.Warn; earlier <= c; earlier++) _given.Add(Id(earlier, m));
            due.Add((c, m));
        }
        if (_given.Count > 2000) _given.Clear(); // weeks of meetings: the old keys are long past
        return due;
    }

    static string Id(MeetingCue cue, Meeting m) => (int)cue + "|" + m.Key;

    /// <summary>"in 4:59", "in 1 h 05", for the scoreboard chip.</summary>
    public static string Until(TimeSpan left)
    {
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        return left.TotalHours >= 1 ? $"{(int)left.TotalHours}:{left.Minutes:00}:{left.Seconds:00}" : $"{(int)left.TotalMinutes}:{left.Seconds:00}";
    }
}
