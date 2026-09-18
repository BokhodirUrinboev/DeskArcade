using System;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace DeskArcade.Engine;

/// <summary>
/// A positioned element whose local origin is its center. Children added to <see cref="Rotor"/>
/// rotate; children added to the sprite itself do not.
/// </summary>
public class Sprite : Canvas
{
    readonly TranslateTransform _tr = new();
    readonly ScaleTransform _sc = new();
    readonly RotateTransform _rot = new();

    public Canvas Rotor { get; } = new();

    public Sprite()
    {
        RenderTransformOrigin = RelativePoint.TopLeft;
        RenderTransform = new TransformGroup { Children = { _sc, _tr } };
        Rotor.RenderTransformOrigin = RelativePoint.TopLeft;
        Rotor.RenderTransform = _rot;
        Children.Add(Rotor);
    }

    public void Set(Vec2 p, double angleDeg = double.NaN)
    {
        if (_tr.X != p.X) _tr.X = p.X;
        if (_tr.Y != p.Y) _tr.Y = p.Y;
        if (!double.IsNaN(angleDeg) && _rot.Angle != angleDeg) _rot.Angle = angleDeg;
    }

    public double Scale
    {
        get => _sc.ScaleX;
        set
        {
            if (_sc.ScaleX == value && _sc.ScaleY == value) return;
            _sc.ScaleX = value;
            _sc.ScaleY = value;
        }
    }

    public double FlipX
    {
        set { if (_sc.ScaleX != value) _sc.ScaleX = value; }
    }
}

public static class Art
{
    /// <summary>Colour-blind mode: <see cref="Safe"/> swaps greens for sky blue and reds for vermillion (Okabe–Ito).</summary>
    public static bool ColorBlind { get; set; }

    /// <summary>
    /// A colour that stays distinct with red–green colour blindness when <see cref="ColorBlind"/> is on:
    /// greens become sky blue, reds vermillion; everything else is unchanged.
    /// </summary>
    public static Color Safe(Color c)
    {
        if (!ColorBlind) return c;
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        if (d < 0.25 || max < 0.25) return c; // greys and dark colours keep their look
        double hue = max == r ? 60 * (((g - b) / d + 6) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        if (hue is >= 80 and <= 170) return Color.FromArgb(c.A, 86, 180, 233);      // green → sky blue
        if (hue is < 15 or > 340) return Color.FromArgb(c.A, 213, 94, 0);           // red → vermillion
        return c;
    }

    public static IBrush Brush(byte a, byte r, byte g, byte b) => new ImmutableSolidColorBrush(Color.FromArgb(a, r, g, b));
    public static IBrush Brush(string hex) => new ImmutableSolidColorBrush(Color.Parse(hex));
    public static IBrush Brush(Color c) => new ImmutableSolidColorBrush(c);

    public static T At<T>(T el, double x, double y) where T : Control
    {
        Canvas.SetLeft(el, x);
        Canvas.SetTop(el, y);
        return el;
    }

    public static Ellipse Circle(double cx, double cy, double r, IBrush? fill, IBrush? stroke = null, double thick = 0) =>
        At(new Ellipse { Width = r * 2, Height = r * 2, Fill = fill, Stroke = stroke, StrokeThickness = thick }, cx - r, cy - r);

    public static Path PathOf(string data, IBrush? fill, IBrush? stroke = null, double thick = 0) => new()
    {
        Data = Geometry.Parse(data),
        Fill = fill,
        Stroke = stroke,
        StrokeThickness = thick,
        StrokeLineCap = PenLineCap.Round,
        StrokeJoin = PenLineJoin.Round,
    };

    public static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    static RadialGradientBrush Radial(double ox, double oy, params (Color c, double at)[] stops)
    {
        var brush = new RadialGradientBrush
        {
            GradientOrigin = new RelativePoint(ox, oy, RelativeUnit.Relative),
            Center = new RelativePoint(0.45, 0.42, RelativeUnit.Relative),
        };
        foreach (var (c, at) in stops) brush.GradientStops.Add(new GradientStop(c, at));
        return brush;
    }

    static Ellipse Highlight(double r, byte alpha) => At(new Ellipse
    {
        Width = r * 0.7, Height = r * 0.42, Fill = Brush(alpha, 255, 255, 255), RenderTransform = new RotateTransform(-30),
    }, -r * 0.62, -r * 0.62);

    public static Sprite Basketball(double r)
    {
        var s = new Sprite();
        s.Children.Insert(0, Circle(2, 4, r, Brush(60, 0, 0, 0))); // soft shadow
        s.Rotor.Children.Add(Circle(0, 0, r,
            Radial(0.35, 0.3, (Color.FromRgb(0xFF, 0xB8, 0x62), 0), (Color.FromRgb(0xEE, 0x7A, 0x1C), 0.5), (Color.FromRgb(0xA9, 0x45, 0x0B), 1)),
            Brush("#5A2505"), 1.5));

        double k = r * 0.72;
        var seams = PathOf(
            $"M0,{F(-r)} L0,{F(r)} M{F(-r)},0 L{F(r)},0 " +
            $"M{F(-k)},{F(-k)} Q{F(-r * 0.22)},0 {F(-k)},{F(k)} " +
            $"M{F(k)},{F(-k)} Q{F(r * 0.22)},0 {F(k)},{F(k)}",
            null, Brush("#3A1805"), Math.Max(1.2, r * 0.075));
        seams.Clip = new EllipseGeometry(new Rect(-r + 0.5, -r + 0.5, r * 2 - 1, r * 2 - 1));
        s.Rotor.Children.Add(seams);

        s.Children.Add(Highlight(r, 70));
        return s;
    }

    public static Sprite SoccerBall(double r)
    {
        var s = new Sprite();
        s.Children.Insert(0, Circle(2, 4, r, Brush(60, 0, 0, 0)));
        s.Rotor.Children.Add(Circle(0, 0, r,
            Radial(0.35, 0.3, (Colors.White, 0), (Color.FromRgb(0xE9, 0xEC, 0xF1), 0.6), (Color.FromRgb(0xA8, 0xB0, 0xBC), 1)),
            Brush("#1C1F26"), 1.5));

        var ink = Brush("#1C1F26");
        var pattern = new Canvas { Clip = new EllipseGeometry(new Rect(-r + 0.8, -r + 0.8, r * 2 - 1.6, r * 2 - 1.6)) };
        pattern.Children.Add(PathOf(Pentagon(0, 0, r * 0.34, -90), ink));
        var lines = new StringBuilder();
        for (int i = 0; i < 5; i++)
        {
            double a = -90 + 72 * i;
            var (vx, vy) = Polar(r * 0.34, a);
            var (px, py) = Polar(r * 0.62, a);
            var (qx, qy) = Polar(r * 0.8, a + 36);
            var (nx, ny) = Polar(r * 0.62, a + 72);
            lines.Append($"M{F(vx)},{F(vy)} L{F(px)},{F(py)} L{F(qx)},{F(qy)} L{F(nx)},{F(ny)} ");
            var (cx, cy) = Polar(r * 1.02, a);
            pattern.Children.Add(PathOf(Pentagon(cx, cy, r * 0.3, a + 90), ink));
        }
        pattern.Children.Add(PathOf(lines.ToString(), null, ink, Math.Max(1, r * 0.05)));
        s.Rotor.Children.Add(pattern);

        s.Children.Add(Highlight(r, 90));
        return s;
    }

    public static Sprite Balloon(double r, Color color)
    {
        var s = new Sprite();
        s.Rotor.Children.Add(PathOf($"M0,{F(r * 1.15)} Q{F(r * 0.3)},{F(r * 1.8)} {F(-r * 0.1)},{F(r * 2.6)} T0,{F(r * 3.6)}", null, Brush(160, 230, 230, 230), 1.2));
        s.Rotor.Children.Add(PathOf($"M{F(-r * 0.16)},{F(r * 1.24)} L{F(r * 0.16)},{F(r * 1.24)} L0,{F(r * 1.08)} Z", Brush(Blend(color, Colors.Black, 0.3))));
        s.Rotor.Children.Add(At(new Ellipse
        {
            Width = r * 2, Height = r * 2.3,
            Fill = Radial(0.35, 0.3, (Blend(color, Colors.White, 0.45), 0), (color, 0.55), (Blend(color, Colors.Black, 0.35), 1)),
        }, -r, -r * 1.15));
        s.Rotor.Children.Add(At(new Ellipse { Width = r * 0.45, Height = r * 0.7, Fill = Brush(110, 255, 255, 255), RenderTransform = new RotateTransform(25) }, -r * 0.55, -r * 0.8));
        return s;
    }

    public static Sprite Star(double r)
    {
        var s = new Sprite();
        s.Children.Insert(0, Circle(0, 0, r * 1.35, Brush(50, 255, 214, 64)));
        s.Rotor.Children.Add(PathOf(StarPath(0, 0, r, r * 0.45), Brush("#FFD23F"), Brush("#B7791F"), 1.5));
        return s;
    }

    public static string Pentagon(double cx, double cy, double r, double startDeg)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 5; i++)
        {
            var (x, y) = Polar(r, startDeg + 72 * i);
            sb.Append(i == 0 ? 'M' : 'L').Append(F(cx + x)).Append(',').Append(F(cy + y)).Append(' ');
        }
        return sb.Append('Z').ToString();
    }

    public static string StarPath(double cx, double cy, double r, double inner)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 10; i++)
        {
            var (x, y) = Polar(i % 2 == 0 ? r : inner, -90 + 36 * i);
            sb.Append(i == 0 ? 'M' : 'L').Append(F(cx + x)).Append(',').Append(F(cy + y)).Append(' ');
        }
        return sb.Append('Z').ToString();
    }

    public static (double x, double y) Polar(double r, double deg)
    {
        double a = deg * Math.PI / 180;
        return (Math.Cos(a) * r, Math.Sin(a) * r);
    }

    public static Color Blend(Color a, Color b, double t) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}
