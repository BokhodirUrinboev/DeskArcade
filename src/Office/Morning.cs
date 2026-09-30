using System;
using System.Globalization;

namespace DeskArcade.Office;

/// <summary>Working days (Monday to Friday) and the days the morning card looks back on.</summary>
public static class WorkDays
{
    public static bool IsWorkingDay(DateOnly day) => day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

    /// <summary>The working day before <paramref name="today"/>: yesterday, or Friday on a Monday (and at a weekend).</summary>
    public static DateOnly LastWorkingDay(DateOnly today)
    {
        var d = today.AddDays(-1);
        while (!IsWorkingDay(d)) d = d.AddDays(-1);
        return d;
    }

    /// <summary>
    /// What the standup notes cover: from the start of the last working day to the start of today, local time, so a
    /// Monday reads Friday and whatever was done at the weekend.
    /// </summary>
    public static (DateTime FromLocal, DateTime ToLocal) StandupRange(DateOnly today) =>
        (LastWorkingDay(today).ToDateTime(TimeOnly.MinValue), today.ToDateTime(TimeOnly.MinValue));

    /// <summary>The Monday of the week <paramref name="day"/> is in.</summary>
    public static DateOnly WeekOf(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    public static string Key(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>
/// The morning card: once on each working day, at the first sign of someone at the computer from five in the morning
/// until an hour before the end of the working day (18:00 when none is set), and never during a presentation, a
/// meeting or a focus block (it waits for the end). On the first morning of a week it also offers the wait report.
/// </summary>
public sealed class MorningCue
{
    public static readonly TimeOnly Earliest = new(5, 0);
    public static readonly TimeOnly DefaultEnd = new(18, 0);

    /// <summary>The local day (yyyy-MM-dd) the card was last shown, kept in the settings.</summary>
    public string? ShownOn { get; set; }
    /// <summary>The Monday (yyyy-MM-dd) of the last week the wait report was offered in.</summary>
    public string? ReportWeek { get; set; }

    /// <param name="nowLocal">The local time now.</param>
    /// <param name="atComputer">Keyboard or mouse used in the last few minutes.</param>
    /// <param name="busy">A presentation, a meeting or a focus block is on.</param>
    /// <param name="end">The end of the working day, or null when none is set.</param>
    /// <returns>True when the card is due now; it then counts as shown today.</returns>
    public bool Step(DateTime nowLocal, bool atComputer, bool busy, TimeOnly? end)
    {
        if (!atComputer || busy) return false;
        var today = DateOnly.FromDateTime(nowLocal);
        if (!WorkDays.IsWorkingDay(today) || ShownOn == WorkDays.Key(today)) return false;
        var time = TimeOnly.FromDateTime(nowLocal);
        var last = (end ?? DefaultEnd).AddHours(-1);
        if (time < Earliest || time >= last) return false;
        ShownOn = WorkDays.Key(today);
        return true;
    }

    /// <summary>True on the first morning card of a week; the report then counts as offered for that week.</summary>
    public bool TakeWeeklyReport(DateOnly today)
    {
        string week = WorkDays.Key(WorkDays.WeekOf(today));
        if (ReportWeek == week) return false;
        ReportWeek = week;
        return true;
    }
}
