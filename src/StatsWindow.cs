using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using DeskArcade.Engine;

namespace DeskArcade;

/// <summary>Time played, best scores and achievements. A normal window, opened from the tray menu.</summary>
public sealed class StatsWindow : Window
{
    static StatsWindow? _open;

    public static void ShowFor(OverlayWindow overlay)
    {
        if (_open != null)
        {
            _open.Activate();
            return;
        }
        _open = new StatsWindow(overlay);
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    StatsWindow(OverlayWindow overlay)
    {
        Title = L.T("Stats & achievements");
        Width = 580;
        Height = 700;
        MinWidth = 420;
        MinHeight = 360;
        Topmost = true; // the overlay is topmost too; without this the window would sit under the games
        RequestedThemeVariant = ThemeVariant.Dark;
        Background = Art.Brush("#16181F");
        Icon = overlay.Icon;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var stats = overlay.Stats;
        var panel = new StackPanel { Margin = new Thickness(22, 18), Spacing = 4 };
        panel.Children.Add(Text(L.T("Stats & achievements"), 22, FontWeight.Bold, "#FFFFFF"));
        panel.Children.Add(Text(
            L.F("Time played {0} · achievements {1}/{2}", Duration(stats.TotalSeconds), stats.UnlockedCount, Achievements.All.Length),
            13, FontWeight.Normal, "#AAB3C0"));
        panel.Children.Add(Text(overlay.DailyLine, 13, FontWeight.SemiBold, "#FFD166"));

        panel.Children.Add(Section(L.T("Games")));
        foreach (var game in overlay.Games)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("36,*,Auto,90"), Margin = new Thickness(0, 3) };
            var iconHost = new Canvas { Width = 28, Height = 28, VerticalAlignment = VerticalAlignment.Center };
            var icon = game.CreateIcon();
            icon.Set(new Vec2(14, 14));
            iconHost.Children.Add(icon);
            row.Children.Add(iconHost);
            AddCell(row, 1, Text(L.T(game.Title), 14, FontWeight.SemiBold, "#FFFFFF"));
            AddCell(row, 2, Text(SafeBest(game), 12, FontWeight.Normal, "#FFD166"));
            var time = Text(Duration(stats.SecondsPlayed(game.Id)), 12, FontWeight.Normal, "#AAB3C0");
            time.HorizontalAlignment = HorizontalAlignment.Right;
            AddCell(row, 3, time);
            panel.Children.Add(row);
        }

        var groups = new[] { "general" }.Concat(overlay.Games.Select(g => g.Id));
        foreach (string groupId in groups)
        {
            var list = Achievements.All.Where(a => a.GameId == groupId).ToList();
            if (list.Count == 0) continue;
            string heading = groupId == "general" ? L.T("General") : L.T(overlay.Games.First(g => g.Id == groupId).Title);
            panel.Children.Add(Section(heading));
            foreach (var a in list) panel.Children.Add(AchievementRow(a, stats));
        }

        Content = new ScrollViewer { Content = panel };
    }

    static Control AchievementRow(Achievement a, Stats stats)
    {
        bool unlocked = stats.IsUnlocked(a.Id);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("26,*,Auto"), Margin = new Thickness(0, 3) };
        row.Children.Add(new Ellipse
        {
            Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center,
            Fill = unlocked ? Art.Brush("#FFD166") : Brushes.Transparent,
            Stroke = Art.Brush(unlocked ? "#FFD166" : "#5A6270"), StrokeThickness = 2,
        });

        var text = new StackPanel();
        text.Children.Add(Text(L.T(a.Title), 14, FontWeight.SemiBold, unlocked ? "#FFFFFF" : "#C9D1DC"));
        text.Children.Add(Text(L.T(a.Description), 12, FontWeight.Normal, "#8D97A5"));
        AddCell(row, 1, text);

        string status = unlocked && stats.UnlockedAt(a.Id) is DateTime at
            ? at.ToLocalTime().ToString("yyyy-MM-dd")
            : $"{Math.Min(stats.Get(a.Counter), a.Target)}/{a.Target}";
        var right = Text(status, 12, FontWeight.Normal, unlocked ? "#FFD166" : "#8D97A5");
        right.VerticalAlignment = VerticalAlignment.Center;
        AddCell(row, 2, right);
        return row;
    }

    static string SafeBest(MiniGame game)
    {
        try { return game.Hud.Best; }
        catch { return ""; }
    }

    static string Duration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? L.F("{0}h {1}m", (int)t.TotalHours, t.Minutes) : L.F("{0}m", (int)t.TotalMinutes);
    }

    static void AddCell(Grid grid, int column, Control child)
    {
        Grid.SetColumn(child, column);
        grid.Children.Add(child);
    }

    static TextBlock Section(string title) => new()
    {
        Text = title.ToUpperInvariant(), FontFamily = Fx.Font, FontSize = 11, FontWeight = FontWeight.Bold,
        Foreground = Art.Brush("#7F8A99"), Margin = new Thickness(0, 16, 0, 4),
    };

    static TextBlock Text(string text, double size, FontWeight weight, string color) => new()
    {
        Text = text, FontFamily = Fx.Font, FontSize = size, FontWeight = weight, Foreground = Art.Brush(color),
        TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
    };
}
