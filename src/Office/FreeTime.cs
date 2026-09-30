using System;
using System.Collections.Generic;
using System.Linq;

namespace DeskArcade.Office;

/// <summary>A stretch of the working day with no meeting in it, in UTC.</summary>
public readonly record struct FreeStretch(DateTime Start, DateTime End)
{
    public TimeSpan Length => End - Start;
}

/// <summary>
/// Room to focus: the free stretches between meetings for the rest of the working day, and the longest one worth a
/// focus block. Overlapping and back-to-back meetings count as one busy stretch; a meeting that fills the day leaves
/// no room at all.
/// </summary>
public static class FreeTime
{
    /// <summary>The shortest stretch worth offering as a focus block.</summary>
    public static readonly TimeSpan FocusWorthy = TimeSpan.FromMinutes(25);

    /// <summary>The shortest stretch worth listing in Today at work.</summary>
    public static readonly TimeSpan Listable = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The free stretches from <paramref name="fromUtc"/> to <paramref name="untilUtc"/> between
    /// <paramref name="meetings"/>, in order, each at least <paramref name="minimum"/> long (a minute by default).
    /// A meeting with no end blocks nothing; one still going at <paramref name="fromUtc"/> blocks until it ends.
    /// </summary>
    public static List<FreeStretch> Between(IEnumerable<Meeting> meetings, DateTime fromUtc, DateTime untilUtc, TimeSpan? minimum = null)
    {
        var shortest = minimum ?? TimeSpan.FromMinutes(1);
        var free = new List<FreeStretch>();
        if (untilUtc <= fromUtc) return free;
        var busy = meetings
            .Where(m => m.End > m.Start && m.End > fromUtc && m.Start < untilUtc)
            .Select(m => (Start: m.Start < fromUtc ? fromUtc : m.Start, End: m.End > untilUtc ? untilUtc : m.End))
            .OrderBy(b => b.Start)
            .ToList();
        var cursor = fromUtc;
        foreach (var (start, end) in busy)
        {
            if (start > cursor && start - cursor >= shortest) free.Add(new FreeStretch(cursor, start));
            if (end > cursor) cursor = end; // overlapping and back-to-back meetings run together
        }
        if (untilUtc > cursor && untilUtc - cursor >= shortest) free.Add(new FreeStretch(cursor, untilUtc));
        return free;
    }

    /// <summary>The longest free stretch of at least <see cref="FocusWorthy"/> (the earlier one on a tie), or null.</summary>
    public static FreeStretch? Longest(IEnumerable<Meeting> meetings, DateTime fromUtc, DateTime untilUtc)
    {
        FreeStretch? best = null;
        foreach (var s in Between(meetings, fromUtc, untilUtc, FocusWorthy))
            if (best is not FreeStretch b || s.Length > b.Length) best = s;
        return best;
    }

    /// <summary>
    /// How long a focus block in <paramref name="stretch"/> should be: the usual length when it fits, otherwise the
    /// longest of 25, 50 and 90 minutes that does.
    /// </summary>
    public static int FocusMinutesFor(FreeStretch stretch, int usual)
    {
        double room = stretch.Length.TotalMinutes;
        if (usual > 0 && usual <= room) return usual;
        int best = 25;
        foreach (int m in new[] { 25, 50, 90 })
            if (m <= room) best = m;
        return best;
    }
}
