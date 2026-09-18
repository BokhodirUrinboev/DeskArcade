using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using DeskArcade.Engine;
using DeskArcade.Net;

namespace DeskArcade;

/// <summary>
/// The office leaderboard: today's best scores of everyone on the local network who shares theirs
/// (<see cref="OfficeBoard"/>), one ranking per category. Sharing is opt-in and is switched here or in the
/// tray. Opened from the tray menu.
/// </summary>
public sealed class LeaderboardWindow : Window
{
    const int ShowTop = 5;

    static LeaderboardWindow? _open;

    readonly OverlayWindow _overlay;
    readonly StackPanel _board = new() { Spacing = 14 };
    readonly TextBlock _status;
    readonly Button _share = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };

    public static void ShowFor(OverlayWindow overlay)
    {
        if (_open != null)
        {
            _open.Activate();
            return;
        }
        _open = new LeaderboardWindow(overlay);
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    LeaderboardWindow(OverlayWindow overlay)
    {
        _overlay = overlay;
        Title = L.T("Office leaderboard");
        Width = 460;
        Height = 620;
        MinWidth = 360;
        MinHeight = 320;
        Topmost = true; // the overlay is topmost too
        RequestedThemeVariant = ThemeVariant.Dark;
        Background = Art.Brush("#16181F");
        Icon = overlay.Icon;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _status = Text("", 12, "#AAB3C0");
        _share.Click += (_, _) =>
        {
            _overlay.SetShareLeaderboard(!_overlay.Settings.ShareLeaderboard);
            Refresh();
        };

        var panel = new StackPanel { Margin = new Thickness(22, 18), Spacing = 10 };
        panel.Children.Add(Text(L.T("Office leaderboard · today"), 22, "#FFFFFF", FontWeight.Bold));
        panel.Children.Add(_status);
        panel.Children.Add(_share);
        panel.Children.Add(_board);
        Content = new ScrollViewer { Content = panel };

        _timer.Tick += (_, _) => Refresh();
        Opened += (_, _) =>
        {
            Refresh();
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    void Refresh()
    {
        bool sharing = _overlay.Settings.ShareLeaderboard;
        _share.Content = sharing ? L.T("Stop sharing my scores") : L.T("Share my scores on the local network");
        _board.Children.Clear();
        if (!sharing)
        {
            _status.Text = L.T("Sharing is off. Turn it on to post your scores and see everyone else's. It sends your user name and today's scores to the other computers on your local network, and nothing else.");
            return;
        }

        var entries = _overlay.BoardEntries();
        _status.Text = entries.Count > 1
            ? L.F("{0} players sharing today · updates every 20 seconds", entries.Count)
            : L.T("Only you so far. Co-workers show up here once they share their scores too.");
        foreach (var (key, title, _) in OfficeBoard.Categories)
        {
            var ranked = OfficeBoard.Rank(entries, key);
            if (ranked.Count == 0) continue;
            _board.Children.Add(Text(L.T(title), 15, "#FFD166", FontWeight.Bold));
            int place = 0;
            foreach (var (id, name, score) in ranked.Take(ShowTop))
            {
                place++;
                bool mine = id == OfficeBoard.InstanceId;
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("28,*,Auto") };
                row.Children.Add(Text($"{place}.", 13, "#AAB3C0"));
                var who = Text(mine ? L.F("{0} (you)", name) : name, 13, mine ? "#4DA3FF" : "#FFFFFF", mine ? FontWeight.Bold : FontWeight.Normal);
                Grid.SetColumn(who, 1);
                row.Children.Add(who);
                var value = Text(score.ToString(), 13, "#FFFFFF", FontWeight.Bold);
                Grid.SetColumn(value, 2);
                row.Children.Add(value);
                _board.Children.Add(row);
            }
        }
        if (_board.Children.Count == 0) _board.Children.Add(Text(L.T("No scores yet today. Go play something!"), 13, "#AAB3C0"));
    }

    static TextBlock Text(string text, double size, string color, FontWeight weight = FontWeight.Normal) =>
        new() { Text = text, FontSize = size, FontWeight = weight, Foreground = Art.Brush(color), TextWrapping = TextWrapping.Wrap };
}
