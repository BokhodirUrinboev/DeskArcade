using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using DeskArcade.Engine;
using DeskArcade.Net;

namespace DeskArcade;

/// <summary>
/// The chat with the co-worker on the LAN link (see <see cref="ChatHub"/>): the reactions along the top, what was said
/// (yours on the right with a tick once it arrived, theirs on the left), and a box to write in; Enter sends, Esc closes.
/// One window at a time. Nothing in it is saved.
/// </summary>
public sealed class ChatWindow : Window
{
    static ChatWindow? _open;

    readonly ChatHub _hub;
    readonly StackPanel _lines = new() { Spacing = 6, Margin = new Thickness(0, 4) };
    readonly ScrollViewer _scroll;
    readonly TextBox _box = new() { MaxLength = ChatLink.MaxLength, MinWidth = 200, AcceptsReturn = false };
    readonly Button _send = new() { Content = L.T("Send") };
    readonly TextBlock _title, _status;
    readonly CheckBox _on = new();
    readonly StackPanel _reactions = new() { Orientation = Orientation.Horizontal, Spacing = 6 };

    public static bool IsOpen => _open != null;

    public static void ShowFor(OverlayWindow overlay, ChatHub hub)
    {
        if (_open != null)
        {
            _open.Activate();
            _open._box.Focus();
            return;
        }
        _open = new ChatWindow(overlay, hub);
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    ChatWindow(OverlayWindow overlay, ChatHub hub)
    {
        _hub = hub;
        Width = 420;
        Height = 560;
        MinWidth = 340;
        MinHeight = 380;
        Topmost = true; // the overlay is topmost too
        RequestedThemeVariant = ThemeVariant.Dark;
        Background = Art.Brush("#16181F");
        Icon = overlay.Icon;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _title = Text("", 20, "#FFFFFF", FontWeight.Bold);
        _status = Text("", 12, "#AAB3C0");
        foreach (var r in ReactionArt.All)
        {
            var reaction = r;
            var icon = ReactionArt.Icon(r);
            icon.RenderTransform = new ScaleTransform(0.72, 0.72);
            var holder = new Canvas { Width = 30, Height = 30 };
            Canvas.SetLeft(icon, 15);
            Canvas.SetTop(icon, 15);
            holder.Children.Add(icon);
            var button = new Button { Content = holder, Padding = new Thickness(3) };
            ToolTip.SetTip(button, ReactionArt.Name(r));
            button.Click += (_, _) => _hub.React(reaction);
            _reactions.Children.Add(button);
        }
        _send.Click += (_, _) => SendTyped();
        _box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                SendTyped();
            }
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
        _on.IsCheckedChanged += (_, _) =>
        {
            if (_on.IsChecked is bool on && on != _hub.Enabled) _hub.SetEnabled(on);
        };

        _scroll = new ScrollViewer { Content = _lines, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        var input = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_send, Dock.Right);
        _send.Margin = new Thickness(8, 0, 0, 0);
        input.Children.Add(_send);
        input.Children.Add(_box);

        var top = new StackPanel { Spacing = 8 };
        top.Children.Add(_title);
        top.Children.Add(Text(L.T("Only between the two of you, over your local network. Nothing is saved."), 12, "#AAB3C0"));
        top.Children.Add(_reactions);
        var bottom = new StackPanel { Spacing = 8 };
        bottom.Children.Add(input);
        bottom.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _on, _status } });
        var root = new DockPanel { Margin = new Thickness(18, 14) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        top.Margin = new Thickness(0, 0, 0, 10);
        bottom.Margin = new Thickness(0, 10, 0, 0);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(_scroll);
        Content = root;

        _hub.Changed += Refresh;
        Closed += (_, _) => _hub.Changed -= Refresh;
        Opened += (_, _) => _box.Focus();
        Refresh();
    }

    void SendTyped()
    {
        switch (_hub.Send(_box.Text ?? ""))
        {
            case ChatSend.Sent:
                _box.Text = "";
                break;
            case ChatSend.TooSoon or ChatSend.TooMany:
                _status.Text = L.T("A moment — the last messages are still on their way");
                break;
        }
        _box.Focus();
    }

    void Refresh()
    {
        bool linked = _hub.Connected;
        Title = linked ? L.F("Chat with {0}", _hub.Peer) : L.T("Chat");
        _title.Text = Title;
        _box.IsEnabled = _send.IsEnabled = _reactions.IsEnabled = linked;
        _box.Watermark = linked ? L.F("Message {0}…", _hub.Peer) : "";
        _on.Content = L.T("Chat is on");
        _on.IsChecked = _hub.Enabled;
        _status.Text = linked ? "" : L.T("Not connected — pair up with Play over LAN first");

        _lines.Children.Clear();
        foreach (var line in _hub.Lines) _lines.Children.Add(Bubble(line));
        Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    Control Bubble(ChatHub.Line line)
    {
        if (line.State == ChatHub.State.Note) return Text(line.Text, 12, "#FFD166");
        var t = Themes.Current;
        var text = new TextBlock { Text = line.Text, TextWrapping = TextWrapping.Wrap, FontSize = 14, Foreground = Brushes.White, MaxWidth = 290 };
        var panel = new StackPanel { Spacing = 2, Children = { text } };
        if (line.Mine)
        {
            string mark = line.State switch
            {
                ChatHub.State.Delivered => "✓",
                ChatHub.State.NotDelivered => L.T("not delivered"),
                _ => L.T("sending…"),
            };
            panel.Children.Add(new TextBlock
            {
                Text = mark, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Right,
                Foreground = Art.Brush(line.State == ChatHub.State.NotDelivered ? "#FF8A8A" : "#D6E4FF"),
            });
        }
        return new Border
        {
            Child = panel,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 6),
            Background = Art.Brush(line.Mine ? Art.Blend(Art.Safe(t.Mine), Colors.Black, 0.35) : Color.FromRgb(46, 50, 62)),
            HorizontalAlignment = line.Mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        };
    }

    static TextBlock Text(string text, double size, string color, FontWeight weight = FontWeight.Normal) =>
        new() { Text = text, FontSize = size, FontWeight = weight, Foreground = Art.Brush(color), TextWrapping = TextWrapping.Wrap };
}
