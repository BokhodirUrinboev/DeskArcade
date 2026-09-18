using System;
using Avalonia.Controls;
using Avalonia.Media;

namespace DeskArcade.Engine;

/// <summary>
/// The other LAN player's ball or arrow, drawn faded with their name above it. It stays while position
/// updates arrive and fades out once they stop.
/// </summary>
public sealed class Ghost
{
    const double Fade = 1.5, Opacity = 0.6;

    readonly Sprite _sprite;
    readonly TextBlock _label = new()
    {
        FontFamily = Fx.Font, FontSize = 13, FontWeight = FontWeight.Bold, Foreground = Brushes.White, IsHitTestVisible = false,
    };
    readonly double _labelUp;
    double _seen = Fade;

    /// <param name="labelUp">How far above the position the name sits.</param>
    public Ghost(Sprite sprite, double labelUp)
    {
        _sprite = sprite;
        _labelUp = labelUp;
        _sprite.IsHitTestVisible = false;
        _sprite.IsVisible = _label.IsVisible = false;
    }

    public void AddTo(Canvas layer)
    {
        layer.Children.Add(_sprite);
        layer.Children.Add(_label);
    }

    public void Show(Vec2 at, double angle, string name)
    {
        _sprite.Set(at, angle);
        _label.Text = name;
        Canvas.SetLeft(_label, at.X - 20);
        Canvas.SetTop(_label, at.Y - _labelUp - 18);
        _seen = 0;
    }

    /// <summary>Advances the fade; returns true while the ghost is on screen.</summary>
    public bool Update(double dt)
    {
        _seen += dt;
        bool shown = _seen < Fade;
        _sprite.IsVisible = _label.IsVisible = shown;
        if (shown) _sprite.Opacity = _label.Opacity = Opacity * Math.Min(1, (Fade - _seen) / 0.5);
        return shown;
    }

    public void Hide()
    {
        _seen = Fade;
        _sprite.IsVisible = _label.IsVisible = false;
    }
}
