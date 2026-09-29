using System;
using System.Globalization;
using Avalonia;

namespace DeskArcade.Office;

/// <summary>The end-of-day notes: time to wrap up, and an hour later, if still at the computer, a nudge to go home.</summary>
public enum DayCue { WrapUp, StillHere }

/// <summary>
/// The end of the working day (tray → At work → End of day). At the time set, someone who is at the computer and has
/// been for half an hour today gets the day's summary and a "time to wrap up"; an hour later, still there, a gentle
/// "still here?". Each comes once a day, and not at all on a day spent elsewhere.
/// </summary>
public sealed class EndOfDay
{
    public const double MinActiveSeconds = 30 * 60;
    public static readonly TimeSpan NudgeAfter = TimeSpan.FromHours(1);

    /// <summary>The local days (yyyy-MM-dd) each cue was last given on, kept in the settings.</summary>
    public string? WrappedOn { get; set; }
    public string? NudgedOn { get; set; }

    /// <summary>"18:00" → 18:00; anything else → null (off).</summary>
    public static TimeOnly? ParseTime(string? text) =>
        text != null && TimeOnly.TryParseExact(text.Trim(), "H:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

    public static string FormatTime(TimeOnly t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <param name="nowLocal">The local time now.</param>
    /// <param name="end">The end of the working day, or null when the reminder is off.</param>
    /// <param name="atComputer">Keyboard or mouse used in the last few minutes.</param>
    /// <param name="activeToday">Seconds at the computer today.</param>
    public DayCue? Step(DateTime nowLocal, TimeOnly? end, bool atComputer, double activeToday)
    {
        if (end is not TimeOnly e || !atComputer) return null;
        string today = nowLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var endToday = nowLocal.Date + e.ToTimeSpan();
        if (nowLocal < endToday) return null;
        if (WrappedOn != today)
        {
            if (activeToday < MinActiveSeconds) return null;
            WrappedOn = today;
            return DayCue.WrapUp;
        }
        if (NudgedOn != today && nowLocal >= endToday + NudgeAfter)
        {
            NudgedOn = today;
            return DayCue.StillHere;
        }
        return null;
    }

    /// <summary>"7 h 42 min", "25 min", in English; the overlay formats its own for translation.</summary>
    public static (int Hours, int Minutes) Split(double seconds)
    {
        int total = (int)Math.Max(0, seconds / 60);
        return (total / 60, total % 60);
    }
}

/// <summary>A timer from tray → At work → Timer, or "deskarcade --timer 10m Tea".</summary>
public sealed record DeskTimer(string Label, DateTime Due);

/// <summary>Durations typed for a timer.</summary>
public static class Durations
{
    /// <summary>
    /// "10" (minutes), "10m", "10 min", "90s", "1h", "1h30m", "1:30" (hours and minutes), "1.5h"; null for anything
    /// else, for nothing at all, and for more than a day.
    /// </summary>
    public static TimeSpan? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string s = text.Trim().ToLowerInvariant().Replace(" ", "");
        TimeSpan? result = null;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double minutes))
        {
            if (!double.IsFinite(minutes) || minutes <= 0 || minutes > 1440) return null;
            result = TimeSpan.FromMinutes(minutes);
        }
        else if (s.Contains(':'))
        {
            var parts = s.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int h) &&
                int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int m) && m < 60 && h <= 24)
                result = new TimeSpan(h, m, 0);
        }
        else
        {
            var total = TimeSpan.Zero;
            int i = 0;
            bool any = false;
            while (i < s.Length)
            {
                int start = i;
                while (i < s.Length && (char.IsAsciiDigit(s[i]) || s[i] == '.')) i++;
                if (start == i || !double.TryParse(s[start..i], NumberStyles.Float, CultureInfo.InvariantCulture, out double n)) return null;
                if (!double.IsFinite(n) || n > 100_000) return null; // before TimeSpan sees it: huge numbers overflow
                int unitStart = i;
                while (i < s.Length && char.IsAsciiLetter(s[i])) i++;
                TimeSpan? part = s[unitStart..i] switch
                {
                    "h" or "hr" or "hrs" or "hour" or "hours" => TimeSpan.FromHours(n),
                    "m" or "min" or "mins" or "minute" or "minutes" or "" => TimeSpan.FromMinutes(n),
                    "s" or "sec" or "secs" or "second" or "seconds" => TimeSpan.FromSeconds(n),
                    _ => null,
                };
                if (part is not TimeSpan p) return null;
                total += p;
                any = true;
            }
            if (any) result = total;
        }
        return result is TimeSpan t && t > TimeSpan.Zero && t <= TimeSpan.FromDays(1) ? t : null;
    }
}

/// <summary>Whether a window fills a monitor, as a full-screen app, a slide show or a video does.</summary>
public static class FullScreen
{
    /// <summary>True when <paramref name="window"/> covers all of <paramref name="monitor"/>, give or take a couple of pixels.</summary>
    public static bool Covers(PixelRect window, PixelRect monitor, int slack = 2) =>
        monitor.Width > 0 && monitor.Height > 0 &&
        window.X <= monitor.X + slack && window.Y <= monitor.Y + slack &&
        window.Right >= monitor.Right - slack && window.Bottom >= monitor.Bottom - slack;
}
