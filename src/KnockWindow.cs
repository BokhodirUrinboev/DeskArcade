using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using DeskArcade.Engine;
using DeskArcade.Net;
using DeskArcade.Office;

namespace DeskArcade;

/// <summary>
/// Types a knock for a co-worker: one line of what it is about ("about the API, 5 min"). Opened from At work → Knock
/// on…; the overlay never takes the keyboard, so the line is typed here.
/// </summary>
public sealed class KnockWindow : Window
{
    static KnockWindow? _open;

    readonly OfficeDesk _desk;
    readonly Coworker _to;
    readonly TextBox _text = new() { MaxLength = OfficeInvites.MaxText, AcceptsReturn = false, TextWrapping = TextWrapping.NoWrap };

    public static void ShowFor(OverlayWindow overlay, OfficeDesk desk, Coworker to)
    {
        _open?.Close();
        _open = new KnockWindow(overlay, desk, to);
        _open.Closed += (_, _) => _open = null;
        _open.Show();
        _open.Activate();
    }

    KnockWindow(OverlayWindow overlay, OfficeDesk desk, Coworker to)
    {
        _desk = desk;
        _to = to;
        Title = L.F("Knock on {0}", to.Name);
        Width = 400;
        Height = 230;
        CanResize = false;
        Topmost = true;
        RequestedThemeVariant = ThemeVariant.Dark;
        Background = Art.Brush("#16181F");
        Icon = overlay.Icon;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _text.Watermark = L.T("about the API, 5 min");
        var knock = new Button { Content = L.T("Knock"), IsDefault = true };
        knock.Click += (_, _) => Send();
        var cancel = new Button { Content = L.T("Cancel"), IsCancel = true };
        cancel.Click += (_, _) => Close();

        var panel = new StackPanel { Margin = new Thickness(20, 16), Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = Title, FontSize = 20, FontWeight = FontWeight.Bold, Foreground = Brushes.White });
        panel.Children.Add(new TextBlock
        {
            Text = desk.CoworkerLine(to) + " · " + (to.State is PresenceState.Focusing or PresenceState.Meeting
                ? L.T("they will see it at their break") : L.T("they will see it now")),
            Foreground = Art.Brush("#AAB3C0"), TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(_text);
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { knock, cancel } });
        Content = panel;
        Opened += (_, _) => _text.Focus();
    }

    void Send()
    {
        string text = (_text.Text ?? "").Trim();
        if (text.Length == 0)
        {
            _text.Focus();
            return;
        }
        if (_desk.KnockOn(_to.Id, text)) Close();
    }
}
