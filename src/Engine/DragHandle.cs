using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace DeskArcade.Engine;

/// <summary>
/// A grip above a board, table or well: drag it to move the game, and the place is kept in the settings per game
/// (tray → Reset positions forgets it). Games that had a right-drag keep it and route it through here too. The game
/// owns its origin; the handle draws the grip, tests presses and turns pointer positions into a new origin.
/// </summary>
public sealed class DragHandle
{
    public const double Height = 22;

    readonly IGameHost _host;
    readonly string _id;
    readonly Border _grip;
    readonly TextBlock _label;
    Rect _rect;
    Vec2 _offset;
    double _width;
    string _painted = "";

    public DragHandle(IGameHost host, string id, string title)
    {
        _host = host;
        _id = id;
        _label = new TextBlock
        {
            FontFamily = Fx.Font, FontSize = 11, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center,
        };
        _grip = new Border
        {
            Height = Height, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(9, 0, 10, 1),
            Child = _label, IsHitTestVisible = false, IsVisible = false, Cursor = new Cursor(StandardCursorType.SizeAll),
        };
        Paint();
        Retitle(title);
    }

    /// <summary>The grip in the theme's colours: its ink for the bar, the accent for the rim. Called again when the theme changes.</summary>
    void Paint()
    {
        var t = Themes.Current;
        _painted = t.Id;
        _grip.Background = Art.Brush(Color.FromArgb(205, t.Ink.R, t.Ink.G, t.Ink.B));
        _grip.BorderBrush = Art.Brush(Color.FromArgb(110, t.Accent.R, t.Accent.G, t.Accent.B));
        _label.Foreground = Art.Brush(Art.Blend(t.HudFront, t.HudBack, 0.15));
    }

    /// <summary>The grip, to add to the game's layer above the board.</summary>
    public Control Visual => _grip;

    public bool Dragging { get; private set; }

    /// <summary>Where the grip is (empty while hidden).</summary>
    public Rect Rect => _rect;

    public HitShape Hit => HitShape.Box(_rect.Inflate(4));

    public void Retitle(string title)
    {
        _label.Text = "⋮⋮  " + L.T(title);
        _width = 0;
    }

    /// <summary>Puts the grip along the top edge of <paramref name="panel"/>: just above it, or inside its top-left corner when there is no room above.</summary>
    public void Show(Rect panel)
    {
        if (_painted != Themes.Current.Id) Paint(); // every game lays out again after a theme change
        if (_width <= 0)
        {
            _grip.Measure(Size.Infinity);
            _width = Math.Max(60, Math.Ceiling(_grip.DesiredSize.Width));
        }
        double y = panel.Top - Height - 6;
        if (y < _host.Arena.Top + 2) y = panel.Top + 6;
        _rect = new Rect(panel.Left, y, _width, Height);
        Canvas.SetLeft(_grip, _rect.X);
        Canvas.SetTop(_grip, _rect.Y);
        _grip.IsVisible = true;
    }

    public void Hide()
    {
        _grip.IsVisible = false;
        _rect = default;
    }

    public bool Contains(Vec2 p) => _grip.IsVisible && _rect.Inflate(4).Contains(p.ToPoint());

    /// <summary>
    /// Starts a drag if <paramref name="p"/> is on the grip (or wherever it is, with <paramref name="anywhere"/>, for a
    /// right-drag on the board itself). <paramref name="origin"/> is the game's origin at that moment.
    /// </summary>
    public bool Begin(Vec2 p, Vec2 origin, bool anywhere = false)
    {
        if (!anywhere && !Contains(p)) return false;
        Dragging = true;
        _offset = origin - p;
        return true;
    }

    /// <summary>The origin for the pointer's position, keeping a panel of <paramref name="panel"/> size inside the arena.</summary>
    public Vec2 Move(Vec2 pointer, Size panel)
    {
        var a = _host.Arena;
        var o = pointer + _offset;
        double top = a.Top + Height + 10;
        return new Vec2(
            Math.Clamp(o.X, a.Left + 4, Math.Max(a.Left + 4, a.Right - panel.Width - 4)),
            Math.Clamp(o.Y, top, Math.Max(top, a.Bottom - panel.Height - 4)));
    }

    /// <summary>Ends a drag and remembers where the game now sits.</summary>
    public void End(Vec2 origin)
    {
        if (!Dragging) return;
        Dragging = false;
        Save(origin);
    }

    /// <summary>Ends a drag without remembering anything (the press was cut off).</summary>
    public void Cancel() => Dragging = false;

    public void Save(Vec2 origin)
    {
        var a = _host.Arena;
        _host.Settings.Positions[_id] = new[] { origin.X - a.Left, origin.Y - a.Top };
        _host.SaveSettings();
    }

    /// <summary>The remembered origin in arena coordinates, or null when the game was never moved (or positions were reset).</summary>
    public Vec2? Saved()
    {
        if (!_host.Settings.Positions.TryGetValue(_id, out var p) || p is not { Length: 2 }) return null;
        var a = _host.Arena;
        return new Vec2(a.Left + p[0], a.Top + p[1]);
    }
}
