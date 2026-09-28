using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>
/// Snakes on Windows: a snake sleeps curled up on the taskbar; click it and it wakes and slides across the desktop after
/// your cursor, turning in curves (see <see cref="SnakeRules"/>). Apples sit on your window tops, riding along when a
/// window is dragged, or lie about the desktop; each one makes the snake longer and a little faster, and now and then a
/// golden apple shines for a few seconds. The screen's edges turn it back; its own tail is the only thing that ends the
/// game. The mouse stays yours meanwhile: the snake only follows the pointer, and clicks go to your windows. A game is a
/// round, raced against the computer or a co-worker over the LAN. Ctrl+Alt+B moves the sleeping snake to the cursor.
/// </summary>
public sealed class SnakeGame : MiniGame
{
    const double AppleR = 12, GoldenLife = 7, NestW = 56, NestH = 30, AppleSize = 1.25;
    const int Apples = 3, GoldenEvery = 6;

    sealed class Apple
    {
        public required Canvas El;
        public Vec2 At;
        public bool Golden;
        public IntPtr On; // the window it sits on, if any
        public double Age;
    }

    readonly Polyline _body = new() { StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round, IsHitTestVisible = false };
    readonly Polyline _stripe = new() { StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round, IsHitTestVisible = false };
    readonly Canvas _head = new() { IsHitTestVisible = false };
    readonly RotateTransform _headTurn = new();
    readonly Path _tongue;
    readonly Canvas _nest = new() { IsHitTestVisible = false };
    readonly Canvas _apples = new() { IsHitTestVisible = false };
    readonly List<Apple> _list = new();
    SnakeRules? _rules;
    bool _playing, _racing, _demo, _over;
    int _eatenForGolden, _demoIdle;
    double _t, _spawnT, _nestX = double.NaN, _scale = 1;
    Rect _nestRect;

    public SnakeGame(IGameHost host) : base(host)
    {
        _tongue = Art.PathOf("M11,0 L20,0 L24,-3 M20,0 L24,3", null, Art.Brush("#E0304A"), 1.6);
        Layer.Children.Add(_apples);
        Layer.Children.Add(_body);
        Layer.Children.Add(_stripe);
        _head.RenderTransform = _headTurn;
        Layer.Children.Add(_head);
        Layer.Children.Add(_nest);
        ThemeChanged();
    }

    public override string Id => "snake";
    public override string Title => "Snakes on Windows";

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        var c = Art.Safe(Themes.Current.Mine);
        s.Rotor.Children.Add(Art.PathOf("M-9,7 C-12,0 -4,-2 0,2 C4,6 10,4 8,-3", null, Art.Brush(c), 4.5));
        s.Rotor.Children.Add(Art.Circle(8, -4, 3.2, Art.Brush(Art.Blend(c, Colors.Black, 0.3))));
        s.Rotor.Children.Add(Art.Circle(-8, -6, 3, Art.Brush("#E0304A")));
        return s;
    }

    public override HudInfo Hud
    {
        get
        {
            long best = Host.Stats.Get("snake.best");
            string line = _playing ? L.F("Apples {0} · steer with the cursor · don't cross your tail", _rules!.Eaten)
                : _over ? L.F("Bitten! {0} apples · click the snake to play again", _rules?.Eaten ?? 0)
                : L.T("Click the sleeping snake · it follows your cursor");
            return new HudInfo((_rules?.Score ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture), line, best > 0 ? L.F("Best {0}", best) : L.T("Best —"));
        }
    }

    // ------------------------------------------------------------------ races

    public override bool SupportsLan => true;
    public override (int Score, bool Active)? Race => (_rules?.Score ?? 0, _racing);
    public override int RaceBaseline => 150;
    public override int RaceBest => (int)Host.Stats.Get("snake.best");
    public override double RaceSeconds => 80;

    public override void StartRace()
    {
        if (!_racing) Start();
    }

    // ------------------------------------------------------------------ layout

    public override void Layout()
    {
        var a = Host.Arena;
        _scale = Clamp(a.Height / 1000, 0.85, 1.3);
        if (double.IsNaN(_nestX) || _nestX < a.Left || _nestX > a.Right) _nestX = a.Left + a.Width * 0.2;
        _nestX = Clamp(_nestX, a.Left + 60, a.Right - 60);
        _nestRect = new Rect(_nestX - NestW / 2 * _scale, a.Bottom - NestH * _scale - 6, NestW * _scale, NestH * _scale + 6);
        Canvas.SetLeft(_nest, _nestX);
        Canvas.SetTop(_nest, a.Bottom);
        _nest.RenderTransform = new ScaleTransform(_scale, _scale);
        _nest.RenderTransformOrigin = RelativePoint.TopLeft;
        _nest.IsVisible = !_playing;
        if (_rules != null) _rules.Box = a;
        Host.HudChanged();
    }

    public override void Summon(Vec2 p)
    {
        if (_playing) return;
        _nestX = p.X;
        Layout();
    }

    public override void Deactivate() => Anims.Finish();

    // ------------------------------------------------------------------ input

    public override void CollectHitShapes(List<HitShape> into)
    {
        if (!_playing) into.Add(HitShape.Box(_nestRect.Inflate(6)));
    }

    public override bool PointerDown(Vec2 p, bool right)
    {
        if (!_playing && _nestRect.Inflate(6).Contains(p.ToPoint())) Start();
        return false;
    }

    void Start()
    {
        var a = Host.Arena;
        foreach (var apple in _list) _apples.Children.Remove(apple.El);
        _list.Clear();
        _rules = new SnakeRules(new Vec2(_nestX, a.Bottom - 70 * _scale), -Math.PI / 2, a);
        _playing = true;
        _over = false;
        _eatenForGolden = 0;
        _spawnT = 0;
        _body.Opacity = _stripe.Opacity = _head.Opacity = 1;
        _body.IsVisible = _stripe.IsVisible = _head.IsVisible = true;
        _nest.IsVisible = false;
        for (int i = 0; i < Apples; i++) SpawnApple(golden: false);
        if (!_racing)
        {
            _racing = true;
            Host.RoundStarted();
        }
        Host.Sound.Play("hiss", 0.25, 1.4);
        Draw();
        Host.HudChanged();
        Host.Wake();
    }

    void GameOver()
    {
        var r = _rules!;
        _playing = false;
        _over = true;
        long before = Host.Stats.Get("snake.best");
        Host.Stats.Max("snake.best", r.Score);
        Host.Stats.Max("snake.run", r.Eaten);
        if (_racing)
        {
            _racing = false;
            Host.RoundEnded(r.Score);
        }
        bool best = r.Score > before && r.Score > 0;
        Host.Fx.Burst(r.Head, new[] { Art.Safe(Themes.Current.Mine), Colors.White, Color.FromRgb(224, 48, 74) }, 26, 360, 600, 5, 0.8);
        Host.Fx.Popup(r.Head - new Vec2(0, 40), best ? L.T("NEW BEST!") : L.T("BITTEN!"), best ? Themes.Themed(Themes.ClassicGold) : Colors.White, 40, 2.4,
            L.F("{0} apples · {1} points", r.Eaten, r.Score));
        Host.Sound.Play(best ? "best" : "hiss", best ? 0.8 : 0.45);
        Anims.Add(0.9, k => _body.Opacity = _stripe.Opacity = _head.Opacity = 1 - k, Ease.OutQuad, () =>
        {
            _body.IsVisible = _stripe.IsVisible = _head.IsVisible = false;
            _nest.IsVisible = true;
        });
        Host.HudChanged();
    }

    // ------------------------------------------------------------------ the snake

    public override bool Update(double dt)
    {
        bool anim = Anims.Update(dt);
        if (!_playing || _rules is not { } r) return anim;
        _t += dt;
        r.Box = Host.Arena;
        if (!r.Step(dt, _demo ? DemoTarget() : Host.Pointer))
        {
            GameOver();
            return true;
        }
        for (int i = _list.Count - 1; i >= 0; i--)
        {
            var apple = _list[i];
            apple.Age += dt;
            if (apple.On != IntPtr.Zero) apple.At += Host.Platforms.DeltaOf(apple.On); // rides along with its window
            if (r.TryEat(apple.At, AppleR * _scale, apple.Golden)) Eat(apple);
            else if (apple.Golden && apple.Age > GoldenLife) Remove(apple, fade: true);
            else Place(apple);
        }
        if (_list.Count(x => !x.Golden) < Apples && (_spawnT -= dt) <= 0)
        {
            SpawnApple(golden: false);
            _spawnT = 0.4;
        }
        Draw();
        return true;
    }

    void Eat(Apple apple)
    {
        var r = _rules!;
        Remove(apple, fade: false);
        Host.Stats.Add("snake.apples");
        if (apple.Golden) Host.Stats.Add("snake.golden");
        int points = apple.Golden ? SnakeRules.GoldenPoints : SnakeRules.ApplePoints;
        Host.Fx.Popup(apple.At - new Vec2(0, 18), $"+{points}", apple.Golden ? Themes.Themed(Themes.ClassicGold) : Colors.White, apple.Golden ? 24 : 18, 0.8);
        Host.Fx.Burst(apple.At, apple.Golden ? new[] { Themes.Current.Gold, Colors.White } : new[] { Color.FromRgb(214, 48, 49), Color.FromRgb(90, 180, 80) }, 10, 200, 300, 4, 0.5);
        Host.Sound.Play(apple.Golden ? "star" : "pop", apple.Golden ? 0.6 : 0.45, apple.Golden ? 1 : 0.75);
        Host.ShareAction(apple.At, points);
        if (!apple.Golden && ++_eatenForGolden % GoldenEvery == 0) SpawnApple(golden: true);
        Host.HudChanged();
    }

    /// <summary>
    /// A new apple: on a window top when there is one with room (and it rides along with it), otherwise somewhere on the
    /// desktop; never under the scoreboard or right by the snake.
    /// </summary>
    void SpawnApple(bool golden)
    {
        var a = Host.Arena;
        var hud = Host.HudBounds.Inflate(20);
        var tops = Host.Platforms.Items.Where(p => p.X2 - p.X1 > 80 && p.Y > a.Top + 60 && p.Y < a.Bottom - 40).ToList();
        for (int attempt = 0; attempt < 20; attempt++)
        {
            Vec2 at;
            IntPtr on = IntPtr.Zero;
            if (tops.Count > 0 && Rng.NextDouble() < 0.6)
            {
                var p = tops[Rng.Next(tops.Count)];
                at = new Vec2(p.X1 + 30 + Rng.NextDouble() * (p.X2 - p.X1 - 60), p.Y - AppleR * _scale - 2);
                on = p.Hwnd;
            }
            else at = new Vec2(a.Left + 60 + Rng.NextDouble() * (a.Width - 120), a.Top + 60 + Rng.NextDouble() * (a.Height - 140));
            if (hud.Contains(at.ToPoint()) || (_rules?.Near(at, 70 * _scale) ?? false) || _list.Any(o => (o.At - at).Length < 90)) continue;
            var apple = new Apple { El = DrawApple(golden), At = at, Golden = golden, On = on };
            _list.Add(apple);
            _apples.Children.Add(apple.El);
            Place(apple);
            apple.El.Opacity = 0;
            Anims.Add(0.35, k =>
            {
                apple.El.Opacity = k;
                apple.El.RenderTransform = new ScaleTransform(AppleSize * _scale * Ease.OutBack(k), AppleSize * _scale * Ease.OutBack(k));
            }, Ease.Linear);
            return;
        }
    }

    void Remove(Apple apple, bool fade)
    {
        _list.Remove(apple);
        if (!fade)
        {
            _apples.Children.Remove(apple.El);
            return;
        }
        Anims.Add(0.4, k => apple.El.Opacity = 1 - k, Ease.OutQuad, () => _apples.Children.Remove(apple.El));
    }

    static void Place(Apple apple)
    {
        Canvas.SetLeft(apple.El, apple.At.X);
        Canvas.SetTop(apple.El, apple.At.Y);
    }

    // ------------------------------------------------------------------ demo

    public override void DemoTick()
    {
        _demo = true;
        if (_playing) return;
        if (++_demoIdle > 10)
        {
            _demoIdle = 0;
            Start();
        }
    }

    /// <summary>The demo player heads for the nearest apple.</summary>
    Vec2 DemoTarget()
    {
        var head = _rules!.Head;
        var apple = _list.OrderBy(x => (x.At - head).Length).FirstOrDefault();
        return apple?.At ?? Host.Arena.Center;
    }

    // ------------------------------------------------------------------ drawing

    void Draw()
    {
        if (_rules is not { } r) return;
        var points = new List<Point>(r.Path.Count);
        foreach (var p in r.Path) points.Add(p.ToPoint());
        _body.Points = points;
        _stripe.Points = points;
        _body.StrokeThickness = SnakeRules.Radius * 2;
        _stripe.StrokeThickness = SnakeRules.Radius * 0.7;
        Canvas.SetLeft(_head, r.Head.X);
        Canvas.SetTop(_head, r.Head.Y);
        _headTurn.Angle = r.Heading * 180 / Math.PI;
        _tongue.IsVisible = _t % 1.6 < 0.25; // a flick now and then
    }

    public override void ThemeChanged()
    {
        var mine = Art.Safe(Themes.Current.Mine);
        _body.Stroke = Art.Brush(mine);
        _stripe.Stroke = Art.Brush(Art.Blend(mine, Colors.White, 0.35));
        _stripe.StrokeDashArray = new AvaloniaList<double> { 1.2, 2.2 };
        _head.Children.Clear();
        _head.Children.Add(_tongue);
        _head.Children.Add(Art.At(new Ellipse { Width = 26, Height = 22, Fill = Art.Brush(Art.Blend(mine, Colors.Black, 0.2)) }, -12, -11));
        foreach (double y in new[] { -5.5, 5.5 })
        {
            _head.Children.Add(Art.Circle(5, y, 3.6, Brushes.White));
            _head.Children.Add(Art.Circle(6.2, y, 1.8, Art.Brush("#15171D")));
        }
        DrawNest(mine);
    }

    /// <summary>The snake asleep on the taskbar: three coils, the head resting on top with its eyes shut.</summary>
    void DrawNest(Color c)
    {
        _nest.Children.Clear();
        var dark = Art.Brush(Art.Blend(c, Colors.Black, 0.35));
        _nest.Children.Add(Art.At(new Ellipse { Width = 56, Height = 16, Fill = Art.Brush(c), Stroke = dark, StrokeThickness = 1.5 }, -28, -16));
        _nest.Children.Add(Art.At(new Ellipse { Width = 44, Height = 14, Fill = Art.Brush(Art.Blend(c, Colors.White, 0.12)), Stroke = dark, StrokeThickness = 1.5 }, -22, -25));
        _nest.Children.Add(Art.At(new Ellipse { Width = 30, Height = 12, Fill = Art.Brush(c), Stroke = dark, StrokeThickness = 1.5 }, -15, -33));
        _nest.Children.Add(Art.At(new Ellipse { Width = 18, Height = 14, Fill = Art.Brush(Art.Blend(c, Colors.Black, 0.2)), Stroke = dark, StrokeThickness = 1.2 }, 6, -38));
        _nest.Children.Add(Art.PathOf("M10,-32 Q12,-30 14,-32 M16,-32 Q18,-30 20,-32", null, Art.Brush("#15171D"), 1.2));
        var zz = new TextBlock { Text = "z z", FontFamily = Fx.Font, FontSize = 12, FontWeight = FontWeight.Bold, Foreground = Art.Brush(Themes.Current.HudFront), IsHitTestVisible = false };
        _nest.Children.Add(Art.At(zz, 22, -58));
    }

    static Canvas DrawApple(bool golden)
    {
        var c = new Canvas { IsHitTestVisible = false };
        var skin = golden ? Color.FromRgb(255, 200, 40) : Art.Safe(Color.FromRgb(214, 48, 49));
        c.Children.Add(Art.PathOf("M0,-6 C-5,-11 -13,-9 -12,0 C-11,8 -5,12 0,9 C5,12 11,8 12,0 C13,-9 5,-11 0,-6 Z",
            Art.Brush(skin), Art.Brush(Art.Blend(skin, Colors.Black, 0.4)), 1.2));
        c.Children.Add(Art.PathOf("M0,-6 Q1,-11 3,-13", null, Art.Brush("#6B3E1E"), 2));
        c.Children.Add(Art.PathOf("M2,-10 Q8,-15 11,-10 Q6,-7 2,-10 Z", Art.Brush(Art.Safe(Color.FromRgb(60, 170, 80)))));
        c.Children.Add(Art.At(new Ellipse { Width = 4, Height = 6, Fill = Art.Brush(120, 255, 255, 255) }, -8, -4));
        if (golden) c.Children.Add(Art.PathOf(Art.StarPath(10, -12, 5, 2), Art.Brush("#FFF3C4")));
        return c;
    }
}
