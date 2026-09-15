using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>
/// Bug Squash: bugs crawl along the taskbar and the tops of your windows. Click them before they fly
/// away. Quick successive squashes build a combo; ladybugs are features, not bugs.
/// </summary>
public sealed class BugsGame : MiniGame
{
    const double RoundSeconds = 30, HitR = 26, Gravity = 1500, BodyLift = 11, ComboWindow = 1.2;

    static readonly string[] BugNames =
    {
        "NullReference", "off-by-one", "race condition", "memory leak", "typo", "flaky test", "merge conflict",
        "undefined", "CORS error", "timeout", "deadlock", "stack overflow", "missing ;", "404", "heisenbug",
        "works on my machine",
    };
    static readonly Color Gold = Color.FromRgb(255, 209, 102);
    static readonly Color[] Goo = { Color.FromRgb(123, 192, 67), Color.FromRgb(180, 220, 90), Color.FromRgb(90, 150, 50) };

    enum Kind { Beetle, Speedy, Golden, Ladybug }

    sealed class Bug
    {
        public required Sprite Sprite;
        public required Control LegsA;
        public required Control LegsB;
        public required Control Wings;
        public Kind Kind;
        public Vec2 Pos; // feet, on the surface
        public double Dir = 1, Speed, Vy, Age, Life, LegT, EscapeT;
        public IntPtr Hwnd;
        public int SeenGen = -1;
        public bool Falling, Escaping, Sleeping;
    }

    sealed class Splat
    {
        public required Sprite Sprite;
        public double Age;
    }

    readonly Canvas _splatLayer = new() { IsHitTestVisible = false };
    readonly Canvas _bugLayer = new() { IsHitTestVisible = false };
    readonly List<Bug> _bugs = new();
    readonly List<Splat> _splats = new();

    bool _roundActive;
    double _roundLeft, _spawnIn, _time, _lastSquash = -10, _sleeperIn = -1;
    int _score, _squashed, _escaped, _combo, _shownSecond = -1;

    public BugsGame(IGameHost host) : base(host)
    {
        Layer.Children.Add(_splatLayer);
        Layer.Children.Add(_bugLayer);
    }

    public override string Id => "bugs";
    public override string Title => "Bug Squash";

    public override Sprite CreateIcon()
    {
        var icon = MakeBug(Kind.Beetle, out _, out var legsB, out _);
        legsB.IsVisible = false;
        icon.Scale = 0.72;
        return icon;
    }

    public override HudInfo Hud => new(
        _score.ToString(),
        _roundActive
            ? _combo > 1
                ? L.F("{0}s left · squashed {1} · combo ×{2}", Math.Max(0, (int)Math.Ceiling(_roundLeft)), _squashed, Math.Min(_combo, 4))
                : L.F("{0}s left · squashed {1}", Math.Max(0, (int)Math.Ceiling(_roundLeft)), _squashed)
            : L.T("Squash the sleeping bug to start · spare the ladybugs"),
        L.F("Best {0}", Host.Settings.BestBugs));

    // ------------------------------------------------------------------ round flow

    public override void Layout()
    {
        var a = Host.Arena;
        foreach (var b in _bugs)
        {
            b.Pos.X = Clamp(b.Pos.X, a.Left + 6, a.Right - 6);
            if (b.Pos.Y > a.Bottom) b.Pos.Y = a.Bottom;
            Draw(b);
        }
        if (!_roundActive && _sleeperIn < 0 && !_bugs.Any(b => b.Sleeping)) SpawnSleeper();
        Host.HudChanged();
    }

    void SpawnSleeper()
    {
        var a = Host.Arena;
        var bug = NewBug(Kind.Beetle);
        bug.Sleeping = true;
        bug.Pos = new Vec2(a.Left + a.Width * 0.35, a.Bottom);
        bug.Sprite.Children.Add(Art.At(new TextBlock
        {
            Text = "z z", FontFamily = Fx.Font, FontSize = 12, FontWeight = FontWeight.Bold, Foreground = Art.Brush(200, 230, 235, 245),
        }, 6, -34));
        bug.LegsB.IsVisible = false;
        Draw(bug);
    }

    void StartRound()
    {
        _roundActive = true;
        _roundLeft = RoundSeconds;
        _score = _squashed = _escaped = _combo = 0;
        _spawnIn = 0.3;
        _shownSecond = -1;
        Host.Sound.Play("fire", 0.6);
        Host.HudChanged();
    }

    void EndRound()
    {
        _roundActive = false;
        foreach (var b in _bugs.Where(b => !b.Escaping).ToList())
        {
            b.Escaping = true; // everyone flies home; doesn't count as escaped
            b.EscapeT = 0;
        }

        Host.Stats.Max("bugs.round", _score);
        var s = Host.Settings;
        bool best = _score > s.BestBugs;
        if (best)
        {
            s.BestBugs = _score;
            Host.SaveSettings();
        }
        var a = Host.Arena;
        var at = new Vec2(a.Center.X, a.Top + a.Height * 0.3);
        Host.Fx.Popup(at, best ? L.T("NEW BEST!") : L.T("TIME!"), best ? Gold : Colors.White, 38, 2.4,
            L.F("{0} points · {1} squashed · {2} got away", _score, _squashed, _escaped));
        if (best)
        {
            Host.Fx.Burst(at, new[] { Gold, Colors.White, Color.FromRgb(6, 214, 160) }, 40, 520, 700, 7, 1.1);
            Host.Sound.Play("best", 0.8);
        }
        else
        {
            Host.Sound.Play("buzzer", 0.4);
        }
        _sleeperIn = 2.5;
        Host.HudChanged();
    }

    void Spawn()
    {
        if (_bugs.Count(b => !b.Escaping) >= 9) return;
        double roll = Rng.NextDouble();
        var kind = roll < 0.12 ? Kind.Ladybug : roll < 0.2 ? Kind.Golden : roll < 0.42 ? Kind.Speedy : Kind.Beetle;
        var bug = NewBug(kind);
        var a = Host.Arena;
        var tops = Host.Platforms.Items.Where(p => p.X2 - p.X1 >= 120 && p.Y > a.Top + 60).ToList();
        bool fromLeft = Rng.NextDouble() < 0.5;
        if (tops.Count > 0 && Rng.NextDouble() < 0.6)
        {
            var p = tops[Rng.Next(tops.Count)];
            bug.Pos = new Vec2(fromLeft ? p.X1 + 6 : p.X2 - 6, p.Y); // crawls out from the window edge
            bug.Hwnd = p.Hwnd;
            bug.SeenGen = Host.Platforms.Generation;
        }
        else
        {
            bug.Pos = new Vec2(fromLeft ? a.Left + 8 : a.Right - 8, a.Bottom);
        }
        bug.Dir = fromLeft ? 1 : -1;
        double urgency = 1 + (RoundSeconds - _roundLeft) / RoundSeconds * 0.5;
        bug.Speed = urgency * kind switch
        {
            Kind.Speedy => 180 + Rng.NextDouble() * 80,
            Kind.Golden => 250 + Rng.NextDouble() * 40,
            Kind.Ladybug => 55 + Rng.NextDouble() * 35,
            _ => 70 + Rng.NextDouble() * 50,
        };
        bug.Life = kind == Kind.Golden ? 3.5 : 5.5 + Rng.NextDouble() * 3;
        Draw(bug);
    }

    Bug NewBug(Kind kind)
    {
        var sprite = MakeBug(kind, out var legsA, out var legsB, out var wings);
        var bug = new Bug { Sprite = sprite, LegsA = legsA, LegsB = legsB, Wings = wings, Kind = kind };
        _bugLayer.Children.Add(sprite);
        _bugs.Add(bug);
        return bug;
    }

    void Remove(Bug b)
    {
        _bugLayer.Children.Remove(b.Sprite);
        _bugs.Remove(b);
    }

    // ------------------------------------------------------------------ input

    static Vec2 BodyCenter(Bug b) => new(b.Pos.X, b.Pos.Y - BodyLift);

    public override void CollectHitShapes(List<HitShape> into)
    {
        foreach (var b in _bugs)
            if (!b.Escaping) into.Add(HitShape.Circle(BodyCenter(b), HitR));
    }

    public override bool PointerDown(Vec2 p, bool right)
    {
        Bug? target = null;
        double nearest = HitR;
        foreach (var b in _bugs)
        {
            if (b.Escaping) continue;
            double d = (BodyCenter(b) - p).Length;
            if (d <= nearest)
            {
                nearest = d;
                target = b;
            }
        }
        if (target != null) Squash(target);
        return false;
    }

    void Squash(Bug b)
    {
        var at = BodyCenter(b);
        bool starts = b.Sleeping && !_roundActive;
        Remove(b);
        AddSplat(at, b.Kind == Kind.Ladybug ? Color.FromRgb(215, 38, 61) : Goo[0]);
        if (starts) StartRound();

        if (b.Kind == Kind.Ladybug)
        {
            _score = Math.Max(0, _score - 5);
            _combo = 0;
            Host.Fx.Popup(at - new Vec2(0, 40), "−5", Color.FromRgb(255, 110, 110), 30, 1.3, L.T("that was a feature!"));
            Host.Sound.Play("buzzer", 0.35);
        }
        else
        {
            _combo = _time - _lastSquash < ComboWindow ? _combo + 1 : 1;
            _lastSquash = _time;
            int mult = Math.Min(_combo, 4);
            int pts = (b.Kind == Kind.Golden ? 5 : b.Kind == Kind.Speedy ? 2 : 1) * mult;
            _score += pts;
            _squashed++;
            Host.Stats.Add("bugs.squashed");
            Host.Stats.Max("bugs.combo", mult);
            Host.Fx.Popup(at - new Vec2(0, 36), mult > 1 ? $"+{pts}  ×{mult}" : $"+{pts}", b.Kind == Kind.Golden ? Gold : Colors.White,
                b.Kind == Kind.Golden ? 32 : 26, 1.0, BugNames[Rng.Next(BugNames.Length)]);
            Host.Fx.Burst(at, Goo, 12, 260, 700, 5, 0.5);
            Host.Sound.Play("pop", 0.8, 0.8 + Rng.NextDouble() * 0.4);
            if (mult >= 3) Host.Sound.Play("score", 0.5);
        }
        Host.HudChanged();
    }

    public override void Summon(Vec2 p)
    {
        var sleeper = _bugs.FirstOrDefault(b => b.Sleeping);
        if (sleeper == null) return;
        var a = Host.Arena;
        sleeper.Pos = new Vec2(Clamp(p.X, a.Left + 20, a.Right - 20), a.Bottom);
        sleeper.Hwnd = IntPtr.Zero;
        Draw(sleeper);
    }

    // ------------------------------------------------------------------ simulation

    public override bool Update(double dt)
    {
        _time += dt;
        bool busy = false;

        if (_roundActive)
        {
            busy = true;
            _roundLeft -= dt;
            int second = (int)Math.Ceiling(Math.Max(0, _roundLeft));
            if (second != _shownSecond)
            {
                _shownSecond = second;
                Host.HudChanged();
            }
            if (_roundLeft <= 0)
            {
                EndRound();
            }
            else if ((_spawnIn -= dt) <= 0)
            {
                Spawn();
                double progress = 1 - _roundLeft / RoundSeconds;
                _spawnIn = 1.0 - 0.6 * progress + Rng.NextDouble() * 0.25;
            }
        }
        else if (_sleeperIn > 0)
        {
            busy = true;
            if ((_sleeperIn -= dt) <= 0)
            {
                _sleeperIn = -1;
                SpawnSleeper();
            }
        }

        for (int i = _bugs.Count - 1; i >= 0; i--)
        {
            var b = _bugs[i];
            if (b.Sleeping) continue;
            busy = true;
            b.Age += dt;

            if (b.Escaping)
            {
                b.EscapeT += dt;
                b.Pos.Y -= 260 * dt;
                b.Pos.X += b.Dir * 60 * dt;
                b.Sprite.Opacity = Math.Max(0, 1 - b.EscapeT / 0.9);
                b.LegsA.IsVisible = b.LegsB.IsVisible = false;
                b.Wings.IsVisible = (int)(b.EscapeT * 30) % 2 == 0;
                if (b.EscapeT >= 0.9) Remove(b);
                else Draw(b, -20);
                continue;
            }

            if (_roundActive && b.Age >= b.Life)
            {
                b.Escaping = true;
                b.EscapeT = 0;
                if (b.Kind != Kind.Ladybug)
                {
                    _escaped++;
                    _combo = 0;
                }
                continue;
            }

            Walk(b, dt);
            b.LegT += dt * b.Speed / 40;
            bool phase = (int)(b.LegT * 6) % 2 == 0;
            b.LegsA.IsVisible = phase && !b.Falling;
            b.LegsB.IsVisible = !phase && !b.Falling;
            Draw(b);
        }

        for (int i = _splats.Count - 1; i >= 0; i--)
        {
            var s = _splats[i];
            s.Age += dt;
            s.Sprite.Opacity = Math.Max(0, 1 - s.Age / 1.6);
            if (s.Age >= 1.6)
            {
                _splatLayer.Children.Remove(s.Sprite);
                _splats.RemoveAt(i);
            }
            busy = true;
        }
        return busy;
    }

    void Walk(Bug b, double dt)
    {
        var plats = Host.Platforms;
        var a = Host.Arena;
        if (b.Hwnd != IntPtr.Zero && b.SeenGen != plats.Generation)
        {
            b.SeenGen = plats.Generation;
            b.Pos += plats.DeltaOf(b.Hwnd); // ride along with the window
        }

        if (b.Falling)
        {
            double prev = b.Pos.Y;
            b.Vy += Gravity * dt;
            b.Pos.Y += b.Vy * dt;
            b.Pos.X = Clamp(b.Pos.X + b.Dir * b.Speed * 0.3 * dt, a.Left + 6, a.Right - 6);
            if (b.Vy > 0 && plats.FindLanding(b.Pos.X, prev, b.Pos.Y, out var landing))
            {
                b.Pos.Y = landing.Y;
                b.Hwnd = landing.Hwnd;
                b.SeenGen = plats.Generation;
                b.Falling = false;
                b.Vy = 0;
            }
            else if (b.Pos.Y >= a.Bottom)
            {
                b.Pos.Y = a.Bottom;
                b.Hwnd = IntPtr.Zero;
                b.Falling = false;
                b.Vy = 0;
            }
            return;
        }

        b.Pos.X += b.Dir * b.Speed * dt;
        if (b.Hwnd == IntPtr.Zero)
        {
            b.Pos.Y = a.Bottom;
            if (b.Pos.X < a.Left + 6) { b.Pos.X = a.Left + 6; b.Dir = 1; }
            else if (b.Pos.X > a.Right - 6) { b.Pos.X = a.Right - 6; b.Dir = -1; }
            return;
        }

        DeskArcade.Engine.Platform? surface = null;
        foreach (var p in plats.Items)
        {
            if (p.Hwnd == b.Hwnd && Math.Abs(p.Y - b.Pos.Y) < 4 && b.Pos.X >= p.X1 - 8 && b.Pos.X <= p.X2 + 8)
            {
                surface = p;
                break;
            }
        }
        if (surface is not DeskArcade.Engine.Platform top)
        {
            b.Falling = true; // the window moved away or closed
            b.Vy = 0;
            return;
        }
        b.Pos.Y = top.Y;
        if (b.Pos.X >= top.X1 && b.Pos.X <= top.X2) return;
        if (Rng.NextDouble() < 0.35)
        {
            b.Falling = true; // hop off the edge
            b.Vy = -120;
        }
        else
        {
            b.Pos.X = Clamp(b.Pos.X, top.X1, top.X2);
            b.Dir = -b.Dir;
        }
    }

    // ------------------------------------------------------------------ visuals

    void Draw(Bug b, double angle = 0)
    {
        b.Sprite.Set(BodyCenter(b), angle);
        b.Sprite.FlipX = b.Dir;
    }

    void AddSplat(Vec2 at, Color color)
    {
        var s = new Sprite { IsHitTestVisible = false };
        var fill = Art.Brush(Color.FromArgb(200, color.R, color.G, color.B));
        s.Rotor.Children.Add(Art.PathOf(Art.StarPath(0, 0, 17, 8), fill));
        for (int i = 0; i < 4; i++)
        {
            var (x, y) = Art.Polar(20 + Rng.NextDouble() * 8, Rng.NextDouble() * 360);
            s.Rotor.Children.Add(Art.Circle(x, y, 2 + Rng.NextDouble() * 2, fill));
        }
        s.Set(at, Rng.NextDouble() * 360);
        _splatLayer.Children.Add(s);
        _splats.Add(new Splat { Sprite = s });
    }

    /// <summary>Side view of a bug facing +x, body centered on the origin.</summary>
    static Sprite MakeBug(Kind kind, out Control legsA, out Control legsB, out Control wings)
    {
        (Color shell, Color dark, double len, double hgt) = kind switch
        {
            Kind.Ladybug => (Color.FromRgb(215, 38, 61), Color.FromRgb(25, 25, 25), 24.0, 16.0),
            Kind.Golden => (Color.FromRgb(232, 184, 31), Color.FromRgb(120, 80, 10), 24.0, 15.0),
            Kind.Speedy => (Color.FromRgb(43, 90, 140), Color.FromRgb(20, 35, 60), 28.0, 12.0),
            _ => (Color.FromRgb(70, 84, 56), Color.FromRgb(30, 34, 26), 26.0, 16.0),
        };
        string F(double v) => Art.F(v);
        double foot = hgt / 2 + 6;
        var s = new Sprite { IsHitTestVisible = false };
        var legBrush = Art.Brush(dark);

        legsA = Art.PathOf($"M-8,3 L-12,{F(foot)} M0,3 L2,{F(foot)} M8,3 L12,{F(foot)}", null, legBrush, 1.6);
        legsB = Art.PathOf($"M-8,3 L-5,{F(foot)} M0,3 L-3,{F(foot)} M8,3 L5,{F(foot)}", null, legBrush, 1.6);
        s.Rotor.Children.Add(legsA);
        s.Rotor.Children.Add(legsB);

        var body = new RadialGradientBrush { GradientOrigin = new RelativePoint(0.4, 0.2, RelativeUnit.Relative) };
        body.GradientStops.Add(new GradientStop(Art.Blend(shell, Colors.White, 0.35), 0));
        body.GradientStops.Add(new GradientStop(shell, 0.55));
        body.GradientStops.Add(new GradientStop(Art.Blend(shell, Colors.Black, 0.35), 1));
        s.Rotor.Children.Add(Art.At(new Ellipse { Width = len, Height = hgt, Fill = body, Stroke = legBrush, StrokeThickness = 1 }, -len / 2, -hgt / 2));
        s.Rotor.Children.Add(Art.PathOf($"M{F(-len / 2 + 3)},0 Q0,{F(-hgt / 2 - 2)} {F(len / 2 - 3)},0", null, Art.Brush(Art.Blend(shell, Colors.Black, 0.45)), 1));

        if (kind == Kind.Ladybug)
            foreach (var (x, y) in new[] { (-5.0, -3.0), (2.0, -4.0), (-1.0, 2.0) })
                s.Rotor.Children.Add(Art.Circle(x, y, 2.2, legBrush));
        if (kind == Kind.Golden)
            s.Rotor.Children.Add(Art.At(new Ellipse { Width = 9, Height = 4, Fill = Art.Brush(150, 255, 255, 255) }, -6, -hgt / 2 + 2));

        s.Rotor.Children.Add(Art.Circle(len / 2 + 3, 1, hgt * 0.32, legBrush));
        s.Rotor.Children.Add(Art.Circle(len / 2 + 5, -1, 1.3, Brushes.White));
        s.Rotor.Children.Add(Art.PathOf($"M{F(len / 2 + 5)},-3 Q{F(len / 2 + 12)},-12 {F(len / 2 + 16)},-9", null, legBrush, 1.2));

        var wingCanvas = new Canvas { IsVisible = false };
        wingCanvas.Children.Add(Art.At(new Ellipse { Width = 22, Height = 10, Fill = Art.Brush(140, 220, 235, 255), RenderTransform = new RotateTransform(-25) }, -16, -hgt / 2 - 10));
        wingCanvas.Children.Add(Art.At(new Ellipse { Width = 18, Height = 8, Fill = Art.Brush(110, 220, 235, 255), RenderTransform = new RotateTransform(-45) }, -8, -hgt / 2 - 12));
        s.Rotor.Children.Add(wingCanvas);
        wings = wingCanvas;
        return s;
    }

    public override void DemoTick()
    {
        var targets = _bugs.Where(b => !b.Escaping && b.Kind != Kind.Ladybug).ToList();
        if (targets.Count == 0) return;
        if (!_roundActive || Rng.NextDouble() < 0.45) Squash(targets[Rng.Next(targets.Count)]);
    }
}
