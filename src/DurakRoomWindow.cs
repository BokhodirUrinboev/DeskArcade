using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using DeskArcade.Engine;
using DeskArcade.Games;
using DeskArcade.Net;

namespace DeskArcade;

/// <summary>
/// Sets up a Durak game with co-workers: create a room (its code appears for the others to type), or join
/// one from the list of rooms on the network or by its code, optionally at an IP address when the network
/// blocks broadcasts. The host picks how many seats to play with; computer players fill the empty ones.
/// </summary>
public sealed class DurakRoomWindow : Window
{
    static DurakRoomWindow? _open;

    readonly DurakGame _durak;
    readonly OverlayWindow _overlay;
    readonly StackPanel _rooms = new() { Spacing = 6 };
    readonly StackPanel _players = new() { Spacing = 4 };
    readonly TextBlock _status, _code;
    readonly TextBox _codeBox = new() { Watermark = "ABCD", MinWidth = 120, MaxLength = 8 };
    readonly TextBox _addressBox = new() { Watermark = L.T("IP address (optional)"), MinWidth = 180 };
    readonly ComboBox _seats = new() { MinWidth = 90 };
    readonly Button _start = new() { Content = L.T("Start the game") };
    readonly StackPanel _hostPanel, _joinPanel;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    bool _searching;
    int _tick;

    public static void ShowFor(OverlayWindow overlay, DurakGame durak)
    {
        if (_open != null)
        {
            _open.Activate();
            return;
        }
        _open = new DurakRoomWindow(overlay, durak);
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    DurakRoomWindow(OverlayWindow overlay, DurakGame durak)
    {
        _overlay = overlay;
        _durak = durak;
        Title = L.T("Durak with co-workers");
        Width = 480;
        Height = 620;
        MinWidth = 380;
        MinHeight = 400;
        Topmost = true; // the overlay is topmost too
        RequestedThemeVariant = ThemeVariant.Dark;
        Background = Art.Brush("#16181F");
        Icon = overlay.Icon;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _status = Text("", 12, "#AAB3C0");
        _code = Text("", 40, "#FFD166", FontWeight.Black);
        for (int n = 2; n <= RoomLink.MaxSeats; n++) _seats.Items.Add(L.F("{0} players", n));
        _seats.SelectedIndex = RoomLink.MaxSeats - 2;

        var create = new Button { Content = L.T("Create a room") };
        create.Click += (_, _) =>
        {
            _durak.HostRoom();
            Refresh();
        };
        _start.Click += (_, _) =>
        {
            _durak.StartRoom(_seats.SelectedIndex + 2);
            _overlay.SwitchGame(_durak.Id);
            Close();
        };
        var join = new Button { Content = L.T("Join") };
        join.Click += (_, _) => JoinTyped();
        _codeBox.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) JoinTyped();
        };
        var leave = new Button { Content = L.T("Leave the room") };
        leave.Click += (_, _) =>
        {
            _durak.LeaveRoom();
            Refresh();
        };

        _hostPanel = new StackPanel { Spacing = 8 };
        _hostPanel.Children.Add(Text(L.T("Your room code — tell it to your co-workers:"), 13, "#AAB3C0"));
        _hostPanel.Children.Add(_code);
        _hostPanel.Children.Add(_players);
        _hostPanel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { Text(L.T("Play with"), 13, "#FFFFFF"), _seats, Text(L.T("(computer players fill empty seats)"), 12, "#AAB3C0") },
        });
        _hostPanel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _start, leave } });

        _joinPanel = new StackPanel { Spacing = 8 };
        _joinPanel.Children.Add(Text(L.T("Rooms on your network:"), 13, "#AAB3C0"));
        _joinPanel.Children.Add(_rooms);
        _joinPanel.Children.Add(Text(L.T("Or type a room code (and the host's IP if the room doesn't show up):"), 12, "#AAB3C0"));
        _joinPanel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _codeBox, _addressBox, join } });
        _joinPanel.Children.Add(new Separator());
        _joinPanel.Children.Add(Text(L.T("Or host the game yourself:"), 13, "#AAB3C0"));
        _joinPanel.Children.Add(create);

        var panel = new StackPanel { Margin = new Thickness(22, 18), Spacing = 10 };
        panel.Children.Add(Text(L.T("Durak with co-workers"), 22, "#FFFFFF", FontWeight.Bold));
        panel.Children.Add(Text(L.T("Two to four players on the same network. Everyone needs Desk Arcade 1.6 or newer."), 12, "#AAB3C0"));
        panel.Children.Add(_status);
        panel.Children.Add(_hostPanel);
        panel.Children.Add(_joinPanel);
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
        var room = _durak.Room;
        bool hosting = room.State == RoomState.Hosting;
        _hostPanel.IsVisible = hosting;
        _joinPanel.IsVisible = !hosting && room.State is RoomState.Off or RoomState.Lost;
        _status.Text = room.State switch
        {
            RoomState.Joining => L.F("Looking for room {0}…", room.Code),
            RoomState.Joined => L.F("You're in room {0} · waiting for the host to start. You can close this window.", room.Code),
            RoomState.Lost => room.Refusal switch
            {
                "full" => L.T("That room is full."),
                "started" => L.T("That game has already started."),
                "notfound" => L.T("No room with that code answered. Check the code, or add the host's IP address."),
                _ => L.T("The room closed."),
            },
            _ => "",
        };
        if (hosting)
        {
            _code.Text = room.Code;
            _players.Children.Clear();
            foreach (var s in room.Seats())
                _players.Children.Add(Text((s.Seat == 0 ? L.F("{0} (you, host)", s.Name) : s.Name) + (s.Connected ? "" : " · " + L.T("disconnected")), 14, s.Connected ? "#FFFFFF" : "#777777"));
            _start.IsEnabled = !_durak.Playing;
        }
        if (_joinPanel.IsVisible && _tick++ % 2 == 0) Search();
    }

    async void Search()
    {
        if (_searching) return;
        _searching = true;
        try
        {
            var found = await RoomLink.FindRooms(TimeSpan.FromSeconds(0.7));
            _rooms.Children.Clear();
            foreach (var r in found)
            {
                var button = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = r.Open && r.Players < r.Seats,
                    Content = L.F("{0} · {1} · {2}/{3} players", r.Code, r.Host, r.Players, r.Seats) + (r.Open ? "" : " · " + L.T("playing")),
                };
                var info = r;
                button.Click += (_, _) => Join(info.Code, info.Address);
                _rooms.Children.Add(button);
            }
            if (found.Count == 0) _rooms.Children.Add(Text(L.T("No rooms yet."), 12, "#777777"));
        }
        catch (Exception)
        {
            _rooms.Children.Clear();
        }
        finally
        {
            _searching = false;
        }
    }

    void JoinTyped()
    {
        string code = RoomLink.CleanCode(_codeBox.Text ?? "");
        if (code.Length == 0) return;
        System.Net.IPEndPoint? address = null;
        if (!string.IsNullOrWhiteSpace(_addressBox.Text))
        {
            address = LanLink.ParseAddress(_addressBox.Text);
            if (address == null)
            {
                _status.Text = L.T("That doesn't look like an address, e.g. 192.168.1.20");
                return;
            }
        }
        Join(code, address);
    }

    void Join(string code, System.Net.IPEndPoint? address)
    {
        _durak.JoinRoom(code, address);
        _overlay.SwitchGame(_durak.Id);
        Refresh();
    }

    static TextBlock Text(string text, double size, string color, FontWeight weight = FontWeight.Normal) =>
        new() { Text = text, FontSize = size, FontWeight = weight, Foreground = Art.Brush(color), TextWrapping = TextWrapping.Wrap };
}
