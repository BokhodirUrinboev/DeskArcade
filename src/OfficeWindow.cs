using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using DeskArcade.Engine;
using DeskArcade.Office;

namespace DeskArcade;

/// <summary>
/// At work…: today at the computer, the meetings still ahead today, and the settings that need typing: the calendar
/// link or file, the end of the working day and the Downloads folder. Opened from the tray or the ☰ menu.
/// </summary>
public sealed class OfficeWindow : Window
{
    static OfficeWindow? _open;

    static readonly int[] WarnChoices = { 0, 1, 2, 5, 10, 15 };

    readonly OfficeDesk _desk;
    readonly TextBox _calendar = new() { MinWidth = 300 };
    readonly TextBlock _calendarStatus;
    readonly ComboBox _warn = new() { MinWidth = 180 };
    readonly CheckBox _pause = new(), _hide = new();
    readonly ComboBox _end = new() { MinWidth = 140 };
    readonly TextBox _downloads = new() { MinWidth = 300 };
    readonly CheckBox _watch = new();

    public static void ShowFor(OverlayWindow overlay, OfficeDesk desk)
    {
        if (_open != null)
        {
            _open.Activate();
            return;
        }
        _open = new OfficeWindow(overlay, desk);
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    OfficeWindow(OverlayWindow overlay, OfficeDesk desk)
    {
        _desk = desk;
        var s = overlay.Settings;
        Title = L.T("At work");
        Width = 560;
        Height = 700;
        MinWidth = 440;
        MinHeight = 420;
        Topmost = true;
        RequestedThemeVariant = ThemeVariant.Dark;
        Background = Art.Brush("#16181F");
        Icon = overlay.Icon;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var panel = new StackPanel { Margin = new Thickness(22, 18), Spacing = 8 };
        panel.Children.Add(Text(L.T("At work"), 22, FontWeight.Bold, "#FFFFFF"));
        panel.Children.Add(Text(desk.StatusLine, 13, FontWeight.SemiBold, "#FFD166"));

        panel.Children.Add(Section(L.T("Today")));
        foreach (string line in desk.DaySummary()) panel.Children.Add(Text(line, 13, FontWeight.Normal, "#E6EAF0"));
        var later = desk.MeetingsLaterToday.Take(6).ToList();
        if (later.Count > 0)
        {
            panel.Children.Add(Text(L.T("Meetings still today:"), 13, FontWeight.SemiBold, "#AAB3C0"));
            foreach (var m in later)
            {
                string at = TimeZoneInfo.ConvertTimeFromUtc(m.Start, TimeZoneInfo.Local).ToString("HH:mm", CultureInfo.InvariantCulture);
                var line = Text($"{at}  {(m.Title.Length > 0 ? m.Title : L.T("Meeting"))}", 13, FontWeight.Normal, "#E6EAF0");
                if (m.JoinUrl == null) panel.Children.Add(line);
                else
                {
                    // a call: its Join button here too
                    var join = new Button { Content = L.T("Join"), Padding = new Thickness(10, 2), FontSize = 12 };
                    join.Click += (_, _) => desk.JoinMeeting(m);
                    panel.Children.Add(Row(line, join));
                }
            }
        }

        // --- meetings
        panel.Children.Add(Section(L.T("Meetings")));
        panel.Children.Add(Text(L.T("Your calendar's .ics link or file. Outlook: Settings → Calendar → Shared calendars → Publish a calendar → ICS. Google Calendar: Settings → your calendar → Secret address in iCal format. It stays on this PC."), 12, FontWeight.Normal, "#AAB3C0"));
        _calendar.Text = s.CalendarUrl ?? "";
        _calendar.Watermark = "https://outlook.office365.com/owa/calendar/…/calendar.ics";
        var browse = new Button { Content = L.T("File…") };
        browse.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = L.T("Pick a calendar file"), AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType(L.T("Calendar")) { Patterns = new[] { "*.ics" } }, FilePickerFileTypes.All },
            });
            if (files.Count > 0 && files[0].TryGetLocalPath() is string path) _calendar.Text = path;
        };
        panel.Children.Add(Row(_calendar, browse));
        _calendarStatus = Text(desk.CalendarProblem is string problem ? L.F("Last read failed: {0}", problem) : "", 12, FontWeight.Normal, "#AAB3C0");
        var check = new Button { Content = L.T("Check") };
        check.Click += async (_, _) =>
        {
            string where = _calendar.Text ?? "";
            if (string.IsNullOrWhiteSpace(where))
            {
                _calendarStatus.Text = L.T("Paste a link or pick a file first.");
                return;
            }
            check.IsEnabled = false;
            _calendarStatus.Text = L.T("Reading…");
            try
            {
                var (ok, message) = await _desk.TryCalendarAsync(where);
                _calendarStatus.Text = message;
                _calendarStatus.Foreground = Art.Brush(ok ? "#3DDC84" : "#FF6B6B");
            }
            finally
            {
                check.IsEnabled = true;
            }
        };
        var useIt = new Button { Content = L.T("Use this calendar") };
        useIt.Click += (_, _) =>
        {
            _desk.SetCalendar(_calendar.Text);
            _calendarStatus.Text = string.IsNullOrWhiteSpace(_calendar.Text) ? L.T("Meetings are off.") : L.T("Saved. Meetings are read every 15 minutes.");
            _calendarStatus.Foreground = Art.Brush("#AAB3C0");
        };
        var off = new Button { Content = L.T("Turn off") };
        off.Click += (_, _) =>
        {
            _calendar.Text = "";
            _desk.SetCalendar(null);
            _calendarStatus.Text = L.T("Meetings are off.");
        };
        panel.Children.Add(Row(check, useIt, off));
        panel.Children.Add(_calendarStatus);

        foreach (int minutes in WarnChoices)
            _warn.Items.Add(new ComboBoxItem { Content = minutes == 0 ? L.T("Only a minute before") : L.F("{0} minutes before", minutes), Tag = minutes });
        _warn.SelectedIndex = Math.Max(0, Array.IndexOf(WarnChoices, s.MeetingWarnMinutes));
        _warn.SelectionChanged += (_, _) =>
        {
            if (_warn.SelectedItem is ComboBoxItem { Tag: int m }) _desk.SetMeetingWarn(m);
        };
        panel.Children.Add(Row(Text(L.T("Heads-up"), 13, FontWeight.Normal, "#E6EAF0"), _warn));
        _pause.Content = L.T("Pause the game a minute before");
        _pause.IsChecked = s.MeetingPause;
        _pause.IsCheckedChanged += (_, _) => _desk.SetMeetingPause(_pause.IsChecked == true);
        _hide.Content = L.T("Hide the overlay during meetings");
        _hide.IsChecked = s.MeetingHide;
        _hide.IsCheckedChanged += (_, _) => _desk.SetMeetingHide(_hide.IsChecked == true);
        panel.Children.Add(_pause);
        panel.Children.Add(_hide);

        // --- end of the day
        panel.Children.Add(Section(L.T("End of the day")));
        _end.Items.Add(new ComboBoxItem { Content = L.T("Off"), Tag = "" });
        foreach (string time in OfficeDesk.EndTimes) _end.Items.Add(new ComboBoxItem { Content = time, Tag = time });
        _end.SelectedIndex = Math.Max(0, Array.IndexOf(OfficeDesk.EndTimes, s.WorkEnd ?? "") + 1);
        _end.SelectionChanged += (_, _) =>
        {
            if (_end.SelectedItem is ComboBoxItem { Tag: string t }) _desk.SetWorkEnd(t.Length == 0 ? null : t);
        };
        panel.Children.Add(Row(Text(L.T("My working day ends at"), 13, FontWeight.Normal, "#E6EAF0"), _end));
        panel.Children.Add(Text(L.T("Then you get the day's summary; an hour later, still at the computer, a nudge to go home."), 12, FontWeight.Normal, "#AAB3C0"));

        // --- downloads
        panel.Children.Add(Section(L.T("Downloads")));
        _watch.Content = L.T("Tell me when downloads finish");
        _watch.IsChecked = s.WatchDownloads;
        _watch.IsCheckedChanged += (_, _) => _desk.SetWatchDownloads(_watch.IsChecked == true);
        panel.Children.Add(_watch);
        _downloads.Text = s.DownloadsFolder ?? "";
        _downloads.Watermark = desk.DownloadsFolder;
        var pickFolder = new Button { Content = L.T("Folder…") };
        pickFolder.Click += async (_, _) =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = L.T("Pick the Downloads folder"), AllowMultiple = false });
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is string path)
            {
                _downloads.Text = path;
                _desk.SetDownloadsFolder(path);
            }
        };
        _downloads.LostFocus += (_, _) => _desk.SetDownloadsFolder(_downloads.Text);
        panel.Children.Add(Row(_downloads, pickFolder));

        panel.Children.Add(Section(L.T("From a terminal")));
        foreach (string example in new[]
        {
            "deskarcade --timer 10m Tea",
            "deskarcade --note \"Call the bank back\"",
            "deskarcade --wait-pid 4242",
            "deskarcade --wait-file ~/Downloads/build.zip",
            "deskarcade --wait-url http://localhost:8080/health",
            "deskarcade --focus 50",
            "arcade gh run watch --exit-status",
        })
            panel.Children.Add(new TextBlock { Text = example, FontFamily = new FontFamily("Cascadia Mono, Consolas, DejaVu Sans Mono, Menlo, monospace"), FontSize = 12, Foreground = Art.Brush("#C9D1DC") });

        Content = new ScrollViewer { Content = panel };
    }

    static StackPanel Row(params Control[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var c in children)
        {
            c.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(c);
        }
        return row;
    }

    static TextBlock Section(string title) => new()
    {
        Text = title.ToUpperInvariant(), FontFamily = Fx.Font, FontSize = 11, FontWeight = FontWeight.Bold,
        Foreground = Art.Brush("#7F8A99"), Margin = new Thickness(0, 14, 0, 2),
    };

    static TextBlock Text(string text, double size, FontWeight weight, string color) => new()
    {
        Text = text, FontFamily = Fx.Font, FontSize = size, FontWeight = weight, Foreground = Art.Brush(color), TextWrapping = TextWrapping.Wrap,
    };
}
