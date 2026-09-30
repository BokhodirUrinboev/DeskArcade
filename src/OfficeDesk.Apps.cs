using System;
using System.Diagnostics;
using System.Linq;
using DeskArcade.Office;

namespace DeskArcade;

/// <summary>
/// Where the day went (off by default): once a minute at the computer, the name of the program in front (never a
/// window title), summed per day on the end-of-day card and saved as CSV for a timesheet from Today at work. Kept
/// for 30 days in apps.json on this PC.
/// </summary>
public sealed partial class OfficeDesk
{
    readonly AppTime _apps = AppTime.Load();
    int _appSeconds;

    /// <summary>Counts seconds at the computer; each full minute notes the program in front.</summary>
    void StepApps(bool atComputer)
    {
        if (!S.TrackApps || !atComputer) return;
        if (++_appSeconds < 60) return;
        _appSeconds = 0;
        if (ForegroundApp() is string app) _apps.AddMinute(DateOnly.FromDateTime(DateTime.Now), app);
    }

    /// <summary>The program in front, by the name it is summed under; null when the platform cannot tell.</summary>
    public string? ForegroundApp()
    {
        int? pid;
        try { pid = _w.Desktop.ForegroundProcessId(); }
        catch (Exception) { return null; }
        if (pid is not int id) return null;
        try
        {
            using var process = Process.GetProcessById(id);
            string name = process.ProcessName;
            string? path = null;
            if (OperatingSystem.IsMacOS())
            {
                try { path = process.MainModule?.FileName; }
                catch (Exception) { path = null; } // not ours to look into
            }
            return AppTime.Friendly(name, path);
        }
        catch (Exception)
        {
            return null; // gone already
        }
    }

    /// <summary>A program's name as shown: "browser" and "terminal" in the interface language.</summary>
    public static string AppName(string key) => key switch
    {
        AppTime.Browser => L.T("browser"),
        AppTime.Terminal => L.T("terminal"),
        _ => key,
    };

    /// <summary>"Code 4:10, browser 1:55, Teams 1:20, other 0:40", or null when nothing was noted that day.</summary>
    public string? AppsLine(DateOnly day)
    {
        var top = _apps.Top(day, 4);
        if (top.Count == 0) return null;
        return string.Join(", ", top.Select(a => (a.App == null ? L.T("other programs") : AppName(a.App)) + " " + AppTime.HoursMinutes(a.Minutes)));
    }

    /// <summary>Every day kept, as CSV for a timesheet.</summary>
    public string AppsCsv() => _apps.Csv(AppName);

    public bool HasAppTime => _apps.Days.Any();

    public void SetTrackApps(bool on)
    {
        S.TrackApps = on;
        _w.SaveSettings();
        _appSeconds = 0;
        if (on) Say(L.T("Noting where the day goes"), L.T("the program in front, once a minute · never a window title"), Blue, null);
        else _apps.Save();
        _w.RefreshTray();
    }
}
