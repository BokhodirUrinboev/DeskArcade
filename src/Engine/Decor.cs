using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace DeskArcade.Engine;

/// <summary>
/// The particles of a theme's decor, without any drawing, so the rules can be tested: snowflakes drift down and sit
/// on window tops and the taskbar for a moment, leaves tumble, petals float, bubbles rise, fireflies blink, stars
/// twinkle, embers rise and confetti flutters. New particles appear only while <c>active</c> (the overlay is drawing
/// frames for a game anyway); once it is not, the ones on screen fade within <see cref="FadeOut"/> seconds and the
/// model goes quiet, so an idle desktop stays idle. Nothing leaves <see cref="Box"/>; at most <see cref="Max"/> live.
/// </summary>
public sealed class DecorModel
{
    public const int Max = 40;
    /// <summary>How long the last particles take to go once the overlay has nothing else to draw.</summary>
    public const double FadeOut = 1.2;

    public struct Particle
    {
        public bool Alive, Settled;
        public Vec2 P, V;
        public double Age, Life, Size, Angle, Spin, Phase, Rest;
        /// <summary>Which of the palette's colours, and which of the kind's shapes.</summary>
        public int Tint, Variant;
        /// <summary>How solid it is drawn this frame, 0 to 1.</summary>
        public double Alpha;
    }

    readonly Particle[] _parts = new Particle[Max];
    readonly Random _rng;
    double _spawn;

    public DecorModel(Random? rng = null) => _rng = rng ?? new Random();

    public Decor Kind { get; private set; }

    /// <summary>The closed box the particles live in (the arena).</summary>
    public Rect Box { get; set; } = new(0, 0, 1920, 1040);

    /// <summary>How many are alive after the last update.</summary>
    public int Count { get; private set; }

    public bool Busy => Count > 0;

    public Particle this[int i] => _parts[i];

    /// <summary>Switches to <paramref name="kind"/> and drops every particle.</summary>
    public void Reset(Decor kind)
    {
        Kind = kind;
        Array.Clear(_parts);
        Count = 0;
        _spawn = 0;
    }

    /// <summary>How many appear per second on a typical screen while active.</summary>
    static double Rate(Decor kind) => kind switch
    {
        Decor.Snow => 2.8, Decor.Leaves => 1.8, Decor.Petals => 2.2, Decor.Bubbles => 1.8, Decor.Fireflies => 2.5,
        Decor.Stars => 5, Decor.Embers => 6, Decor.Confetti => 3.5, _ => 0,
    };

    /// <param name="active">The overlay is drawing frames for something else: only then do new particles appear.</param>
    /// <param name="platforms">Window tops the snow can settle on.</param>
    /// <returns>True while anything is on screen.</returns>
    public bool Update(double dt, bool active, IReadOnlyList<Platform>? platforms = null)
    {
        if (Kind == Decor.None)
        {
            if (Count > 0) Reset(Decor.None);
            return false;
        }
        dt = Math.Clamp(dt, 0, 0.1);
        if (active && Box.Width > 40 && Box.Height > 40)
        {
            _spawn += Rate(Kind) * Math.Clamp(Box.Width / 1600, 0.5, 1.5) * dt;
            while (_spawn >= 1)
            {
                _spawn -= 1;
                Spawn();
            }
        }
        else _spawn = 0;

        int count = 0;
        for (int i = 0; i < _parts.Length; i++)
        {
            ref var p = ref _parts[i];
            if (!p.Alive) continue;
            p.Age += dt;
            if (!active) p.Life = Math.Min(p.Life, p.Age + FadeOut);
            Move(ref p, dt, platforms);
            if (!p.Alive || p.Age >= p.Life)
            {
                p.Alive = false;
                continue;
            }
            Keep(ref p);
            double fadeIn = Math.Min(1, p.Age / 0.5), fadeOut = Math.Min(1, (p.Life - p.Age) / 0.8);
            p.Alpha = Math.Clamp(Shine(in p) * fadeIn * fadeOut, 0, 1);
            count++;
        }
        Count = count;
        return count > 0;
    }

    void Spawn()
    {
        int slot = -1;
        for (int i = 0; i < _parts.Length; i++)
        {
            if (_parts[i].Alive) continue;
            slot = i;
            break;
        }
        if (slot < 0) return;
        double w = Box.Width, h = Box.Height, r = _rng.NextDouble();
        var p = new Particle
        {
            Alive = true, Life = 30, Phase = _rng.NextDouble() * Math.PI * 2, Tint = _rng.Next(1000), Variant = _rng.Next(1000),
        };
        switch (Kind)
        {
            case Decor.Snow:
                p.Size = 3 + r * 4;
                p.V = new Vec2(0, 40 + _rng.NextDouble() * 50);
                break;
            case Decor.Leaves:
                p.Size = 8 + r * 6;
                p.V = new Vec2(0, 60 + _rng.NextDouble() * 50);
                p.Spin = (60 + _rng.NextDouble() * 140) * (_rng.Next(2) == 0 ? 1 : -1);
                p.Angle = _rng.NextDouble() * 360;
                break;
            case Decor.Petals:
                p.Size = 5 + r * 4;
                p.V = new Vec2(0, 50 + _rng.NextDouble() * 40);
                p.Spin = (30 + _rng.NextDouble() * 80) * (_rng.Next(2) == 0 ? 1 : -1);
                p.Angle = _rng.NextDouble() * 360;
                break;
            case Decor.Bubbles:
                p.Size = 6 + r * 10;
                p.V = new Vec2(0, -(35 + p.Size * 3));
                break;
            case Decor.Fireflies:
                p.Size = 3 + r * 1.5;
                p.V = new Vec2((_rng.NextDouble() - 0.5) * 40, (_rng.NextDouble() - 0.5) * 24);
                p.Life = 5 + _rng.NextDouble() * 4;
                break;
            case Decor.Stars:
                p.Size = 2 + r * 2 + (_rng.Next(6) == 0 ? 2 : 0);
                p.V = new Vec2((_rng.NextDouble() - 0.5) * 4, (_rng.NextDouble() - 0.5) * 4);
                p.Life = 3 + _rng.NextDouble() * 4;
                break;
            case Decor.Embers:
                p.Size = 2 + r * 3;
                p.V = new Vec2(0, -(80 + _rng.NextDouble() * 90));
                p.Life = 2.5 + _rng.NextDouble() * 2.5;
                break;
            case Decor.Confetti:
                p.Size = 6 + r * 4;
                p.V = new Vec2(0, 90 + _rng.NextDouble() * 80);
                p.Spin = (200 + _rng.NextDouble() * 300) * (_rng.Next(2) == 0 ? 1 : -1);
                p.Angle = _rng.NextDouble() * 360;
                break;
        }
        double half = p.Size / 2, x = Box.Left + half + _rng.NextDouble() * Math.Max(1, w - p.Size);
        p.P = Kind switch
        {
            Decor.Bubbles or Decor.Embers => new Vec2(x, Box.Bottom - half - 1),
            Decor.Fireflies => new Vec2(x, Box.Top + h * 0.3 + _rng.NextDouble() * h * 0.65),
            Decor.Stars => new Vec2(x, Box.Top + half + _rng.NextDouble() * h * 0.7),
            _ => new Vec2(x, Box.Top + half + 1),
        };
        _parts[slot] = p;
    }

    void Move(ref Particle p, double dt, IReadOnlyList<Platform>? platforms)
    {
        double half = p.Size / 2;
        switch (Kind)
        {
            case Decor.Snow:
            case Decor.Leaves:
            case Decor.Petals:
                if (p.Settled)
                {
                    // sits where it landed for a moment, then melts or blows away
                    p.Rest += dt;
                    double stay = Kind == Decor.Snow ? 1.0 + p.Phase / (Math.PI * 2) * 1.5 : 0.6 + p.Phase / (Math.PI * 2) * 0.8;
                    if (p.Rest > stay) p.Life = Math.Min(p.Life, p.Age + 0.8);
                    return;
                }
                p.V.X = Kind switch
                {
                    Decor.Snow => Math.Sin(p.Age * 1.3 + p.Phase) * 18 + 8,
                    Decor.Leaves => Math.Sin(p.Age * 1.1 + p.Phase) * 55,
                    _ => Math.Sin(p.Age * 1.6 + p.Phase) * 35,
                };
                double prevBottom = p.P.Y + half;
                p.P += p.V * dt;
                p.Angle += p.Spin * dt;
                Land(ref p, prevBottom, platforms);
                break;
            case Decor.Bubbles:
                p.V.X = Math.Sin(p.Age * 2.2 + p.Phase) * 14;
                p.P += p.V * dt;
                if (p.P.Y - half <= Box.Top) p.Alive = false; // pops at the ceiling
                break;
            case Decor.Fireflies:
                p.V += new Vec2((_rng.NextDouble() - 0.5) * 60 * dt, (_rng.NextDouble() - 0.5) * 40 * dt);
                if (p.V.Length > 34) p.V = p.V.Normalized() * 34;
                p.P += p.V * dt;
                Bounce(ref p);
                break;
            case Decor.Stars:
                p.P += p.V * dt;
                Bounce(ref p);
                break;
            case Decor.Embers:
                p.V.X = Math.Sin(p.Age * 5 + p.Phase) * 25;
                p.P += p.V * dt;
                if (p.P.Y - half <= Box.Top) p.Alive = false;
                break;
            case Decor.Confetti:
                p.V.X = Math.Sin(p.Age * 4 + p.Phase) * 60;
                p.P += p.V * dt;
                p.Angle += p.Spin * dt;
                if (p.P.Y + half >= Box.Bottom) p.Alive = false;
                break;
        }
    }

    /// <summary>A falling particle that crossed a window top or reached the floor settles on it.</summary>
    void Land(ref Particle p, double prevBottom, IReadOnlyList<Platform>? platforms)
    {
        double half = p.Size / 2, bottom = p.P.Y + half;
        if (platforms != null)
        {
            foreach (var pl in platforms)
            {
                if (p.P.X < pl.X1 || p.P.X > pl.X2 || prevBottom > pl.Y + 1 || bottom < pl.Y) continue;
                Settle(ref p, pl.Y - half);
                return;
            }
        }
        if (bottom >= Box.Bottom) Settle(ref p, Box.Bottom - half);
    }

    static void Settle(ref Particle p, double y)
    {
        p.P.Y = y;
        p.V = default;
        p.Spin = 0;
        p.Settled = true;
        p.Rest = 0;
    }

    /// <summary>Wanderers turn back at the walls.</summary>
    void Bounce(ref Particle p)
    {
        double half = p.Size / 2;
        if (p.P.X - half < Box.Left || p.P.X + half > Box.Right) p.V.X = -p.V.X;
        if (p.P.Y - half < Box.Top || p.P.Y + half > Box.Bottom) p.V.Y = -p.V.Y;
    }

    /// <summary>Whatever moved it, it stays inside the box.</summary>
    void Keep(ref Particle p)
    {
        double half = Math.Min(p.Size / 2, Math.Min(Box.Width, Box.Height) / 2);
        p.P.X = Math.Clamp(p.P.X, Box.Left + half, Box.Right - half);
        p.P.Y = Math.Clamp(p.P.Y, Box.Top + half, Box.Bottom - half);
    }

    /// <summary>How bright the kind is at this moment of its life: fireflies blink, stars twinkle, embers cool.</summary>
    double Shine(in Particle p) => Kind switch
    {
        Decor.Fireflies => Math.Pow(Math.Max(0, Math.Sin(p.Age * 1.7 + p.Phase)), 3),
        Decor.Stars => 0.35 + 0.65 * Math.Abs(Math.Sin(p.Age * (1.5 + p.Variant % 3 * 0.4) + p.Phase)),
        Decor.Embers => 1 - p.Age / p.Life * 0.6,
        Decor.Snow => 0.9,
        Decor.Bubbles => 0.85,
        _ => 0.95,
    };
}

/// <summary>
/// Draws a <see cref="DecorModel"/> on one canvas above the games and below the scoreboard: a pool of small
/// shapes in the theme's colours, moved with transforms only. Off with reduced motion and the "Theme decorations"
/// toggle; the current game's motion decides when new particles appear (see the model).
/// </summary>
public sealed class DecorLayer
{
    sealed class Slot
    {
        public required Canvas El;
        public required TranslateTransform Move;
        public required RotateTransform Turn;
        public required ScaleTransform Grow;
    }

    /// <summary>Shapes are drawn this big and scaled to the particle's size.</summary>
    const double Unit = 10;

    readonly DecorModel _model = new();
    readonly Slot[] _slots = new Slot[DecorModel.Max];
    string _built = "";

    public Canvas Layer { get; } = new() { IsHitTestVisible = false };

    public DecorModel Model => _model;

    /// <param name="active">Something else is moving on the overlay this frame.</param>
    /// <returns>True while particles are on screen (the frame loop should go on).</returns>
    public bool Update(double dt, bool active, Theme theme, Rect box, IReadOnlyList<Platform> platforms)
    {
        var kind = Themes.DecorEnabled && !Fx.ReducedMotion ? theme.Decor : Decor.None;
        if (theme.Id != _built)
        {
            _built = theme.Id;
            _model.Reset(kind);
            Build(theme);
        }
        else if (kind != _model.Kind) _model.Reset(kind);
        _model.Box = box;
        bool busy = _model.Update(dt, active, platforms);
        for (int i = 0; i < _slots.Length; i++)
        {
            var slot = _slots[i];
            if (slot == null) continue;
            var p = _model[i];
            if (!p.Alive)
            {
                if (slot.El.IsVisible) slot.El.IsVisible = false;
                continue;
            }
            slot.Move.X = p.P.X;
            slot.Move.Y = p.P.Y;
            slot.Turn.Angle = p.Angle;
            slot.Grow.ScaleX = slot.Grow.ScaleY = p.Size / Unit;
            slot.El.Opacity = p.Alpha;
            if (!slot.El.IsVisible) slot.El.IsVisible = true;
        }
        return busy;
    }

    /// <summary>One shape per pool slot, for the theme's decor and in its colours.</summary>
    void Build(Theme theme)
    {
        Layer.Children.Clear();
        var rng = new Random(theme.Id.GetHashCode());
        for (int i = 0; i < _slots.Length; i++)
        {
            var move = new TranslateTransform();
            var turn = new RotateTransform();
            var grow = new ScaleTransform(1, 1);
            var el = new Canvas
            {
                IsHitTestVisible = false, IsVisible = false, RenderTransformOrigin = RelativePoint.TopLeft,
                RenderTransform = new TransformGroup { Children = { grow, turn, move } },
            };
            if (theme.Decor != Decor.None) Shape(el, theme, i, rng);
            Layer.Children.Add(el);
            _slots[i] = new Slot { El = el, Move = move, Turn = turn, Grow = grow };
        }
    }

    static void Shape(Canvas into, Theme theme, int i, Random rng)
    {
        var palette = theme.Confetti;
        var tint = palette[rng.Next(palette.Length)];
        const double u = Unit, h = Unit / 2;
        string F(double v) => Art.F(v);
        switch (theme.Decor)
        {
            case Decor.Snow:
                // an icy rim, so the flakes still read over a light wallpaper
                if (i % 3 == 0)
                {
                    into.Children.Add(Art.PathOf($"M0,{F(-h)} L0,{F(h)} M{F(-h * 0.87)},{F(-h * 0.5)} L{F(h * 0.87)},{F(h * 0.5)} M{F(-h * 0.87)},{F(h * 0.5)} L{F(h * 0.87)},{F(-h * 0.5)}",
                        null, Art.Brush(150, 125, 167, 201), 2.6));
                    into.Children.Add(Art.PathOf($"M0,{F(-h)} L0,{F(h)} M{F(-h * 0.87)},{F(-h * 0.5)} L{F(h * 0.87)},{F(h * 0.5)} M{F(-h * 0.87)},{F(h * 0.5)} L{F(h * 0.87)},{F(-h * 0.5)}",
                        null, Art.Brush(240, 255, 255, 255), 1.4));
                }
                else into.Children.Add(Art.Circle(0, 0, h * 0.8, Art.Brush(230, 255, 255, 255), Art.Brush(150, 125, 167, 201), 0.9));
                break;
            case Decor.Leaves:
                into.Children.Add(Art.PathOf($"M0,{F(-h)} C{F(h * 0.9)},{F(-h * 0.7)} {F(h)},{F(h * 0.3)} 0,{F(h)} C{F(-h)},{F(h * 0.3)} {F(-h * 0.9)},{F(-h * 0.7)} 0,{F(-h)} Z",
                    Art.Brush(tint), Art.Brush(Art.Blend(tint, Colors.Black, 0.35)), 0.8));
                into.Children.Add(Art.PathOf($"M0,{F(-h * 0.8)} L0,{F(h * 0.8)}", null, Art.Brush(Art.Blend(tint, Colors.Black, 0.35)), 0.7));
                break;
            case Decor.Petals:
                into.Children.Add(Art.PathOf($"M0,{F(-h)} C{F(h * 0.6)},{F(-h * 0.8)} {F(h * 0.6)},{F(h * 0.6)} 0,{F(h)} C{F(-h * 0.6)},{F(h * 0.6)} {F(-h * 0.6)},{F(-h * 0.8)} 0,{F(-h)} Z",
                    Art.Brush(Color.FromArgb(225, tint.R, tint.G, tint.B))));
                break;
            case Decor.Bubbles:
                into.Children.Add(Art.Circle(0, 0, h, Art.Brush(Color.FromArgb(38, theme.Accent.R, theme.Accent.G, theme.Accent.B)), Art.Brush(150, 255, 255, 255), 1));
                into.Children.Add(Art.Circle(-h * 0.35, -h * 0.35, h * 0.2, Art.Brush(200, 255, 255, 255)));
                break;
            case Decor.Fireflies:
                into.Children.Add(Art.Circle(0, 0, u, Art.Brush(Color.FromArgb(55, theme.Gold.R, theme.Gold.G, theme.Gold.B))));
                into.Children.Add(Art.Circle(0, 0, h * 0.7, Art.Brush(theme.Gold)));
                break;
            case Decor.Stars:
                var star = i % 4 == 0 ? theme.Gold : i % 4 == 1 ? tint : Colors.White;
                into.Children.Add(Art.PathOf($"M0,{F(-h)} L{F(h * 0.25)},{F(-h * 0.25)} L{F(h)},0 L{F(h * 0.25)},{F(h * 0.25)} L0,{F(h)} L{F(-h * 0.25)},{F(h * 0.25)} L{F(-h)},0 L{F(-h * 0.25)},{F(-h * 0.25)} Z",
                    Art.Brush(star)));
                break;
            case Decor.Embers:
                var ember = i % 3 == 0 ? theme.Gold : tint;
                into.Children.Add(Art.Circle(0, 0, u * 0.8, Art.Brush(Color.FromArgb(60, ember.R, ember.G, ember.B))));
                into.Children.Add(Art.Circle(0, 0, h * 0.7, Art.Brush(ember)));
                break;
            case Decor.Confetti:
                into.Children.Add(Art.At(new Rectangle { Width = u, Height = h, Fill = Art.Brush(tint), RadiusX = 1, RadiusY = 1 }, -h, -h / 2));
                break;
        }
    }
}
