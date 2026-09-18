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
/// Lists the Desk Arcade hosts on the local network (refreshed every couple of seconds) and joins the one
/// you click, or an address you type when the network blocks broadcasts. Opened from the tray menu.
/// </summary>
public sealed class LobbyWindow : Window
{
    static LobbyWindow? _open;

    readonly OverlayWindow _overlay;
    readonly StackPanel _hosts = new() { Spacing = 6 };
    readonly TextBox _address = new() { Watermark = "192.168.1.20", MinWidth = 220 };
    readonly TextBlock _status;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    bool _searching;

    public static void ShowFor(OverlayWindow overlay)
    {
        if (_open != null)
        {
            _open.Activate();
            return;
        }
        _open = new LobbyWindow(overlay);
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    LobbyWindow(OverlayWindow overlay)
    {
        _overlay = overlay;
        Title = L.T("Find games");
        Width = 440;
        Height = 420;
        MinWidth = 360;
        MinHeight = 300;
        Topmost = true; // the overlay is topmost too
        RequestedThemeVariant = ThemeVariant.Dark;
        Background = Art.Brush("#16181F");
        Icon = overlay.Icon;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _status = Text(L.T("Looking for games on your network…"), 12, "#AAB3C0");
        var join = new Button { Content = L.T("Join") };
        join.Click += (_, _) => JoinTyped();
        _address.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) JoinTyped();
        };

        var panel = new StackPanel { Margin = new Thickness(22, 18), Spacing = 10 };
        panel.Children.Add(Text(L.T("Find games"), 22, "#FFFFFF", FontWeight.Bold));
        panel.Children.Add(_status);
        panel.Children.Add(_hosts);
        panel.Children.Add(Text(L.T("Or join by address (ask the host for their IP):"), 12, "#AAB3C0"));
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _address, join } });
        Content = new ScrollViewer { Content = panel };

        _timer.Tick += (_, _) => Search();
        Opened += (_, _) =>
        {
            Search();
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    async void Search()
    {
        if (_searching) return;
        _searching = true;
        try
        {
            var found = await LanLink.FindHosts(TimeSpan.FromSeconds(0.8));
            _hosts.Children.Clear();
            foreach (var h in found.OrderBy(h => h.Name))
            {
                string game = _overlay.Games.FirstOrDefault(g => g.Id == h.GameId) is { } g ? L.T(g.Title) : h.GameId;
                var button = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    IsEnabled = !h.Busy,
                    Content = h.Busy ? L.F("{0} · {1} · already playing", h.Name, game) : L.F("{0} · {1}", h.Name, game),
                };
                var address = h.Address;
                button.Click += (_, _) => Join(address);
                _hosts.Children.Add(button);
            }
            _status.Text = found.Count == 0
                ? L.T("No games yet. Ask your co-worker to pick Play over LAN → Host a game.")
                : L.T("Click a game to join it:");
        }
        catch (Exception)
        {
            _status.Text = L.T("Couldn't search the network.");
        }
        finally
        {
            _searching = false;
        }
    }

    void JoinTyped()
    {
        if (LanLink.ParseAddress(_address.Text ?? "") is { } address) Join(address);
        else _status.Text = L.T("That doesn't look like an address, e.g. 192.168.1.20");
    }

    void Join(System.Net.IPEndPoint address)
    {
        _overlay.JoinLan(address);
        Close();
    }

    static TextBlock Text(string text, double size, string color, FontWeight weight = FontWeight.Normal) =>
        new() { Text = text, FontSize = size, FontWeight = weight, Foreground = Art.Brush(color), TextWrapping = TextWrapping.Wrap };
}
