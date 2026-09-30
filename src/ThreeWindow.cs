using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using DeskArcade.Engine;
using DeskArcade.Office;

namespace DeskArcade;

/// <summary>
/// Today's three, typed: up to three things to get done today, one per line, with what carried over from last time
/// already in. Opened from the morning card or At work → My day → Today's three…; the overlay itself never takes the
/// keyboard.
/// </summary>
public sealed class ThreeWindow : Window
{
    static ThreeWindow? _open;

    readonly OfficeDesk _desk;
    readonly TextBox[] _lines = new TextBox[TodaysThree.Max];

    public static void ShowFor(OverlayWindow overlay, OfficeDesk desk)
    {
        _open?.Close();
        _open = new ThreeWindow(overlay, desk);
        _open.Closed += (_, _) => _open = null;
        _open.Show();
        _open.Activate();
    }

    ThreeWindow(OverlayWindow overlay, OfficeDesk desk)
    {
        _desk = desk;
        Title = L.T("Today's three");
        Width = 400;
        Height = 290;
        CanResize = false;
        Topmost = true;
        RequestedThemeVariant = ThemeVariant.Dark;
        Background = Art.Brush("#16181F");
        Icon = overlay.Icon;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var panel = new StackPanel { Margin = new Thickness(20, 16), Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = Title, FontSize = 20, FontWeight = FontWeight.Bold, Foreground = Brushes.White });
        panel.Children.Add(new TextBlock
        {
            Text = L.T("Up to three things to get done today. They sit under the scoreboard; click one when it's done."),
            FontSize = 12, Foreground = Art.Brush("#AAB3C0"), TextWrapping = TextWrapping.Wrap,
        });
        var items = desk.Three.Items;
        string[] examples = { L.T("Finish the report"), L.T("Review the pull request"), L.T("Call the supplier") };
        for (int i = 0; i < _lines.Length; i++)
        {
            _lines[i] = new TextBox { MaxLength = TodaysThree.MaxLength, Watermark = $"{i + 1}. {examples[i]}", Text = i < items.Count ? items[i].Text : "" };
            panel.Children.Add(_lines[i]);
        }

        var save = new Button { Content = L.T("Save"), IsDefault = true };
        save.Click += (_, _) =>
        {
            _desk.SetThree(_lines.Select(l => l.Text));
            Close();
        };
        var cancel = new Button { Content = L.T("Cancel"), IsCancel = true };
        cancel.Click += (_, _) => Close();
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { save, cancel } });
        Content = panel;
        Opened += (_, _) => _lines.FirstOrDefault(l => string.IsNullOrEmpty(l.Text))?.Focus();
    }
}
