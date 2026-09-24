using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace DeskArcade.Engine;

/// <summary>Floating score text and simple particles, drawn above the games.</summary>
public sealed class Fx
{
    sealed class Pop
    {
        public required Control El;
        public required TranslateTransform Tr;
        public required ScaleTransform Sc;
        public Vec2 P;
        public double Age, Life;
    }

    sealed class Particle
    {
        public required Ellipse El;
        public required TranslateTransform Tr;
        public Vec2 P;
        public Vec2 V;
        public double Age, Life, Size, Gravity;
    }

    sealed class Ring
    {
        public required Ellipse El;
        public required TranslateTransform Tr;
        public Vec2 P;
        public double Age, Life, From, To;
    }

    const int MaxParticles = 160;

    readonly List<Pop> _pops = new();
    readonly List<Ring> _rings = new();
    readonly List<Particle> _parts = new();
    readonly Stack<Particle> _pool = new();
    readonly Dictionary<Color, IBrush> _brushes = new();

    /// <summary>Segoe UI on Windows, Ubuntu/Cantarell/Noto on Linux.</summary>
    public static readonly FontFamily Font = new("Segoe UI, SF Pro Text, Helvetica Neue, Ubuntu, Cantarell, Noto Sans, DejaVu Sans");

    public Canvas Layer { get; } = new() { IsHitTestVisible = false };

    /// <summary>Tweens that belong to the overlay rather than to one game (a game's entrance, the scoreboard).</summary>
    public Anims Anims { get; } = new();

    /// <summary>Reduced motion: no particle bursts or trails, and popups appear in place without bouncing or drifting.</summary>
    public static bool ReducedMotion { get; set; }

    /// <summary>Visible area; popups are kept inside it.</summary>
    public Rect Bounds { get; set; } = new(0, 0, 1920, 1080);

    public void Popup(Vec2 p, string text, Color color, double size = 30, double life = 1.2, string? sub = null)
    {
        var panel = new StackPanel { IsHitTestVisible = false };
        color = Art.Safe(color);
        panel.Children.Add(Outlined(text, color, size));
        if (sub != null) panel.Children.Add(Outlined(sub, Colors.White, size * 0.45));

        panel.Measure(Size.Infinity);
        var ds = panel.DesiredSize;
        var sc = new ScaleTransform(0.4, 0.4);
        var tr = new TranslateTransform();
        panel.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        panel.RenderTransform = new TransformGroup { Children = { sc, tr } };
        Layer.Children.Add(panel);

        double halfW = ds.Width / 2, halfH = ds.Height / 2;
        p.X = Math.Clamp(p.X, Bounds.Left + halfW + 8, Math.Max(Bounds.Left + halfW + 8, Bounds.Right - halfW - 8));
        p.Y = Math.Max(p.Y, Bounds.Top + halfH + 8);
        _pops.Add(new Pop { El = panel, Tr = tr, Sc = sc, P = new Vec2(p.X - halfW, p.Y - halfH), Life = life });
    }

    /// <summary>
    /// A ring that grows from <paramref name="from"/> to <paramref name="to"/> pixels across and fades out:
    /// marks where something happened (the rival's clicks in a LAN race), without covering the game.
    /// </summary>
    public void Marker(Vec2 p, Color color, double from = 10, double to = 46, double life = 0.9)
    {
        var tr = new TranslateTransform();
        var el = new Ellipse
        {
            Stroke = Art.Brush(Art.Safe(color)), StrokeThickness = 3, IsHitTestVisible = false,
            RenderTransform = tr, RenderTransformOrigin = RelativePoint.TopLeft,
        };
        Layer.Children.Add(el);
        var ring = new Ring { El = el, Tr = tr, P = p, Life = life, From = from, To = ReducedMotion ? from : to };
        _rings.Add(ring);
        Place(ring, 0);
    }

    static void Place(Ring ring, double k)
    {
        double d = ring.From + (ring.To - ring.From) * EaseOut(k);
        ring.El.Width = ring.El.Height = d;
        ring.Tr.X = ring.P.X - d / 2;
        ring.Tr.Y = ring.P.Y - d / 2;
        ring.El.Opacity = 0.9 * (1 - k);
    }

    static Grid Outlined(string text, Color color, double size)
    {
        var g = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
        var shadow = new SolidColorBrush(Color.FromArgb(200, 10, 10, 20));
        foreach (var (dx, dy) in new[] { (-2, 0), (2, 0), (0, -2), (0, 2), (2, 3) })
        {
            g.Children.Add(new TextBlock
            {
                Text = text, FontFamily = Font, FontWeight = FontWeight.Black, FontSize = size,
                Foreground = shadow, RenderTransform = new TranslateTransform(dx, dy),
            });
        }
        g.Children.Add(new TextBlock
        {
            Text = text, FontFamily = Font, FontWeight = FontWeight.Black, FontSize = size,
            Foreground = new SolidColorBrush(color),
        });
        return g;
    }

    public void Burst(Vec2 p, Color[] colors, int count, double speed, double gravity = 900, double size = 6, double life = 0.8)
    {
        for (int i = 0; i < count; i++)
        {
            double a = Random.Shared.NextDouble() * Math.PI * 2;
            double s = speed * (0.35 + Random.Shared.NextDouble() * 0.65);
            Spawn(p, new Vec2(Math.Cos(a) * s, Math.Sin(a) * s - speed * 0.3),
                colors[i % colors.Length], size * (0.6 + Random.Shared.NextDouble() * 0.8),
                life * (0.7 + Random.Shared.NextDouble() * 0.6), gravity);
        }
    }

    public void Spawn(Vec2 p, Vec2 v, Color c, double size, double life, double gravity)
    {
        if (ReducedMotion) return;
        c = Art.Safe(c);
        if (_parts.Count >= MaxParticles) return;
        Particle part;
        if (_pool.Count > 0)
        {
            part = _pool.Pop();
            part.El.IsVisible = true;
        }
        else
        {
            var tr = new TranslateTransform();
            part = new Particle
            {
                El = new Ellipse { RenderTransform = tr, RenderTransformOrigin = RelativePoint.TopLeft, IsHitTestVisible = false },
                Tr = tr,
            };
            Layer.Children.Add(part.El);
        }
        if (!_brushes.TryGetValue(c, out var brush)) _brushes[c] = brush = Art.Brush(c);
        part.El.Fill = brush;
        part.El.Width = part.El.Height = size;
        part.P = p;
        part.V = v;
        part.Age = 0;
        part.Life = life;
        part.Size = size;
        part.Gravity = gravity;
        _parts.Add(part);
    }

    public bool Update(double dt)
    {
        bool animating = Anims.Update(dt);
        for (int i = _pops.Count - 1; i >= 0; i--)
        {
            var pop = _pops[i];
            pop.Age += dt;
            double k = pop.Age / pop.Life;
            if (k >= 1)
            {
                Layer.Children.Remove(pop.El);
                _pops.RemoveAt(i);
                continue;
            }
            double scale = ReducedMotion ? 1 : 0.4 + 0.6 * EaseOutBack(Math.Min(1, pop.Age / 0.15));
            pop.Sc.ScaleX = pop.Sc.ScaleY = scale;
            pop.Tr.X = pop.P.X;
            pop.Tr.Y = ReducedMotion ? pop.P.Y : pop.P.Y - 60 * EaseOut(k);
            pop.El.Opacity = k < 0.7 ? 1 : 1 - (k - 0.7) / 0.3;
        }

        for (int i = _parts.Count - 1; i >= 0; i--)
        {
            var part = _parts[i];
            part.Age += dt;
            if (part.Age >= part.Life)
            {
                part.El.IsVisible = false;
                _parts.RemoveAt(i);
                _pool.Push(part);
                continue;
            }
            part.V.Y += part.Gravity * dt;
            part.P += part.V * dt;
            part.Tr.X = part.P.X - part.Size / 2;
            part.Tr.Y = part.P.Y - part.Size / 2;
            part.El.Opacity = 1 - part.Age / part.Life;
        }

        for (int i = _rings.Count - 1; i >= 0; i--)
        {
            var ring = _rings[i];
            ring.Age += dt;
            if (ring.Age >= ring.Life)
            {
                Layer.Children.Remove(ring.El);
                _rings.RemoveAt(i);
                continue;
            }
            Place(ring, ring.Age / ring.Life);
        }

        return _pops.Count > 0 || _parts.Count > 0 || _rings.Count > 0 || animating;
    }

    public void Clear()
    {
        foreach (var ring in _rings) Layer.Children.Remove(ring.El);
        _rings.Clear();
        foreach (var pop in _pops) Layer.Children.Remove(pop.El);
        _pops.Clear();
        foreach (var part in _parts)
        {
            part.El.IsVisible = false;
            _pool.Push(part);
        }
        _parts.Clear();
    }

    static double EaseOut(double t) => 1 - (1 - t) * (1 - t);

    static double EaseOutBack(double t)
    {
        const double c = 1.9;
        return 1 + (c + 1) * Math.Pow(t - 1, 3) + c * Math.Pow(t - 1, 2);
    }
}
