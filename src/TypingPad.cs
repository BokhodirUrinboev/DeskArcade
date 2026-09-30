using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using DeskArcade.Engine;

namespace DeskArcade;

/// <summary>
/// The typing games' keyboard. The overlay never takes the keyboard (so the terminal underneath keeps it), which a
/// typing game can't live with; so while one is being typed into, this small window sits under it and takes the keys
/// instead, passing them to the game (<see cref="IKeySink"/>). It is an ordinary window: clicking elsewhere gives the
/// keyboard back (the game pauses) and clicking it takes it again. The game closes it when the typing is over.
/// Characters come from text input, after the keyboard layout, so a Russian or Uzbek layout types its own letters.
/// </summary>
public sealed class TypingPad : Window
{
    const double PadW = 360, PadH = 50;

    readonly OverlayWindow _overlay;
    readonly TextBlock _title, _hint;
    readonly Border _frame;
    IKeySink? _sink;

    public TypingPad(OverlayWindow overlay)
    {
        _overlay = overlay;
        Title = "Desk Arcade";
        Icon = overlay.Icon;
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true; // above the overlay, which is topmost too
        Width = PadW;
        Height = PadH;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Focusable = true;

        _title = new TextBlock { FontFamily = Fx.Font, FontSize = 13, FontWeight = FontWeight.Bold };
        _hint = new TextBlock { FontFamily = Fx.Font, FontSize = 11 };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1, Children = { _title, _hint } };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(12, 0), Children = { KeyboardIcon(), text } };
        _frame = new Border { BorderThickness = new Thickness(1.5), Child = row };
        Content = _frame;

        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AddHandler(TextInputEvent, OnTextInput, RoutingStrategies.Tunnel);
        Activated += (_, _) =>
        {
            Paint();
            _overlay.Wake();
        };
        Deactivated += (_, _) =>
        {
            Paint();
            _sink?.KeyboardLost();
            _overlay.Wake();
        };
        Closed += (_, _) =>
        {
            var sink = _sink;
            _sink = null;
            sink?.KeyboardLost();
            _overlay.Wake();
        };
    }

    public IKeySink? Sink => _sink;

    /// <summary>Hands the keyboard to <paramref name="sink"/>: the pad goes under <paramref name="near"/> (overlay DIPs) and takes the focus.</summary>
    public void Attach(IKeySink sink, Rect near, string title)
    {
        if (_sink != null && _sink != sink) _sink.KeyboardLost();
        _sink = sink;
        _title.Text = title;
        Place(near);
        Paint();
        if (!IsVisible) Show();
        Activate();
        Focus();
    }

    /// <summary>
    /// Centred under the game's panel, on the screen rather than the play area, so under a game standing on the floor it
    /// goes over the taskbar, where it hides nothing of the game; above the panel when the screen has no room below.
    /// </summary>
    void Place(Rect near)
    {
        var top = _overlay.PointToScreen(near.TopLeft);
        var bottom = _overlay.PointToScreen(near.BottomRight);
        var screen = _overlay.Screens.ScreenFromPoint(top) ?? _overlay.Screens.Primary;
        double scaling = screen?.Scaling ?? 1;
        var bounds = screen?.Bounds ?? new PixelRect(top.X - 2000, top.Y - 2000, 4000, 4000);
        int w = (int)Math.Ceiling(PadW * scaling), h = (int)Math.Ceiling(PadH * scaling), gap = (int)(10 * scaling);
        int x = Math.Clamp((top.X + bottom.X - w) / 2, bounds.X + 4, Math.Max(bounds.X + 4, bounds.Right - w - 4));
        int y = Math.Min(bottom.Y + gap, bounds.Bottom - h);
        if (y < bottom.Y - 12 * scaling) y = Math.Max(bounds.Y + 4, top.Y - h - gap); // no room below: above instead
        Position = new PixelPoint(x, y);
    }

    void Paint()
    {
        var t = Themes.Current;
        bool on = IsActive;
        Background = Art.Brush(Art.Blend(t.Ink, Colors.Black, 0.15));
        _frame.BorderBrush = Art.Brush(on ? t.Accent : Art.Blend(t.Accent, t.Ink, 0.6));
        _title.Foreground = Art.Brush(t.HudFront);
        _hint.Foreground = Art.Brush(on ? Art.Blend(t.HudFront, t.Ink, 0.35) : t.Gold);
        _hint.Text = on ? L.T("Your keys go to the game here · Esc stops") : L.T("Click here to keep typing");
    }

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_sink == null) return;
        if (_sink is IChordSink chords && KeyChord.From(e.Key, e.KeyModifiers) is { } chord && chords.WantsChord(chord))
        {
            e.Handled = true;
            chords.ChordPressed(chord);
            _overlay.Wake();
            return;
        }
        TypingKey? key = e.Key switch
        {
            Key.Back when e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Alt) => TypingKey.WordBackspace,
            Key.Back => TypingKey.Backspace,
            Key.Enter => TypingKey.Enter,
            Key.Escape => TypingKey.Escape,
            _ => null,
        };
        if (key is { } k)
        {
            e.Handled = true;
            _sink.KeyPressed(k);
            _overlay.Wake();
        }
        else if (e.Key == Key.Tab) e.Handled = true; // no focus to move to
    }

    void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (_sink == null || string.IsNullOrEmpty(e.Text)) return;
        e.Handled = true;
        string text = Printable(e.Text);
        if (text.Length == 0) return;
        _sink.TextTyped(text);
        _overlay.Wake();
    }

    /// <summary>Drops control characters (some platforms send Enter or Backspace as text too; those arrive as keys).</summary>
    public static string Printable(string text)
    {
        var chars = new System.Text.StringBuilder(text.Length);
        foreach (char c in text)
            if (c >= ' ' && c != '\u007f') chars.Append(c);
        return chars.ToString();
    }

    static Control KeyboardIcon()
    {
        var c = new Canvas { Width = 30, Height = 20, VerticalAlignment = VerticalAlignment.Center };
        var t = Themes.Current;
        c.Children.Add(Art.At(new Rectangle { Width = 30, Height = 20, RadiusX = 3, RadiusY = 3, Fill = Art.Brush(Art.Blend(t.HudFront, t.Ink, 0.75)) }, 0, 0));
        var key = Art.Brush(Art.Blend(t.HudFront, t.Ink, 0.2));
        for (int row = 0; row < 2; row++)
            for (int col = 0; col < 5; col++)
                c.Children.Add(Art.At(new Rectangle { Width = 4, Height = 4, RadiusX = 1, RadiusY = 1, Fill = key }, 2.5 + col * 5.3 + row * 1.5, 3 + row * 5.5));
        c.Children.Add(Art.At(new Rectangle { Width = 16, Height = 3.5, RadiusX = 1, RadiusY = 1, Fill = key }, 7, 14));
        return c;
    }
}
