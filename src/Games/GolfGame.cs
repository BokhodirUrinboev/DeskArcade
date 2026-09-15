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
/// Mini golf across the desktop: drag back from the ball to putt along the taskbar and window tops
/// into the cup. Nine holes a round; each hole starts where the last one was sunk.
/// </summary>
public sealed class GolfGame : MiniGame
{
    const double R = 11, Step = 1.0 / 240, Reach = 34;
    const double MaxPull = 170, MinPull = 12, MaxSpeed = 1750, SinkSpeed = 430, CupHalf = 14;
    const int HolesPerRound = 9;

    static readonly Color Gold = Color.FromRgb(255, 209, 102);
    static readonly Color[] Confetti = { Gold, Color.FromRgb(6, 214, 160), Color.FromRgb(239, 71, 111), Colors.White };

    readonly BallBody _ball = new(R) { Gravity = 1800, Restitution = 0.32, WallRestitution = 0.5, AirDrag = 0.05, RollFriction = 0.85 };
    readonly Sprite _ballSprite = MakeBall(R);
    readonly Sprite _flag = new();
    readonly Path _cloth = new() { Fill = Art.Brush("#E63946") };
    readonly Sprite _ring = new() { IsHitTestVisible = false };
    readonly Canvas _guide = new() { IsHitTestVisible = false };
    readonly Ellipse[] _dots = new Ellipse[10];
    readonly Dictionary<string, double> _lastSound = new();

    Vec2 _cup;
    IntPtr _cupHwnd;
    int _cupGen = -1;
    int _hole, _strokes, _roundStrokes, _parDone, _par;
    bool _placed, _aiming, _sinking, _holeScored, _roundOver;
    Vec2 _pull, _sinkFrom;
    double _time, _acc, _sinkT;

    public GolfGame(IGameHost host) : base(host)
    {
        _flag.Children.Insert(0, Art.At(new Ellipse { Width = CupHalf * 2 + 4, Height = 8, Fill = Art.Brush("#101214") }, -CupHalf - 2, -4));
        _flag.Rotor.Children.Add(Art.PathOf("M0,-2 L0,-64", null, Art.Brush("#E9ECEF"), 2.2));
        _flag.Rotor.Children.Add(_cloth);
        _flag.Children.Add(Art.At(new Ellipse { Width = CupHalf * 2 + 4, Height = 8, Stroke = Art.Brush(110, 255, 255, 255), StrokeThickness = 1 }, -CupHalf - 2, -4));
        _flag.IsHitTestVisible = false;

        var ring = Art.Circle(0, 0, Reach, Art.Brush(10, 255, 255, 255), Art.Brush(80, 255, 255, 255), 1.5);
        ring.StrokeDashArray = new AvaloniaList<double> { 3, 5 };
        _ring.Children.Add(ring);

        for (int i = 0; i < _dots.Length; i++)
        {
            _dots[i] = new Ellipse
            {
                Width = 5, Height = 5, Fill = Brushes.White, IsVisible = false,
                RenderTransformOrigin = RelativePoint.TopLeft, RenderTransform = new TranslateTransform(),
            };
            _guide.Children.Add(_dots[i]);
        }

        Layer.Children.Add(_flag);
        Layer.Children.Add(_ring);
        Layer.Children.Add(_guide);
        Layer.Children.Add(_ballSprite);
        UpdateCloth(0);
    }

    public override string Id => "golf";
    public override string Title => "Mini Golf";

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        s.Rotor.Children.Add(Art.PathOf("M-3,9 L-3,-10", null, Art.Brush("#E9ECEF"), 1.6));
        s.Rotor.Children.Add(Art.PathOf("M-3,-10 L8,-6 L-3,-2 Z", Art.Brush("#E63946")));
        s.Rotor.Children.Add(Art.Circle(5, 6, 4, Brushes.White, Art.Brush("#8A9099"), 0.8));
        return s;
    }

    public override HudInfo Hud => new(
        _roundStrokes.ToString(),
        _roundOver
            ? $"Round done · {Rel(_roundStrokes - _parDone)} vs par · putt to play again"
            : $"Hole {_hole}/{HolesPerRound} · par {_par} · stroke {_strokes}",
        Host.Settings.BestGolf is int best ? $"Best {Rel(best)}" : "Best —");

    static string Rel(int d) => d == 0 ? "E" : d > 0 ? $"+{d}" : $"−{-d}";

    bool BallReady => !_sinking && (_ball.Asleep || (_ball.Grounded && _ball.Vel.Length < 25));

    void Changed() => Host.HudChanged();

    // ------------------------------------------------------------------ course

    public override void Layout()
    {
        var a = Host.Arena;
        if (!_placed)
        {
            _ball.Place(new Vec2(a.Left + a.Width * 0.15, a.Bottom - R));
            _placed = true;
            NewRound();
        }
        else
        {
            if (_ball.Pos.X < a.Left || _ball.Pos.X > a.Right || _ball.Pos.Y > a.Bottom) _ball.Place(new Vec2(a.Left + a.Width * 0.15, a.Bottom - R));
            if (_cup.X < a.Left || _cup.X > a.Right || _cup.Y > a.Bottom + 1) PlaceCup();
        }
        Draw();
        Changed();
    }

    public override void Deactivate()
    {
        _aiming = false;
        HideGuide();
    }

    void NewRound()
    {
        _hole = 0;
        _roundStrokes = 0;
        _parDone = 0;
        _roundOver = false;
        NextHole();
    }

    void NextHole()
    {
        _hole++;
        _strokes = 0;
        PlaceCup();
        double dist = Math.Abs(_cup.X - _ball.Pos.X);
        int par = dist < 450 ? 2 : dist < 1000 ? 3 : 4;
        if (_cup.Y < _ball.Pos.Y + R - 60) par++; // uphill onto a window
        _par = Math.Clamp(par, 2, 5);
        Changed();
    }

    void PlaceCup()
    {
        var a = Host.Arena;
        var surfaces = Host.Platforms.Items.Where(p => p.X2 - p.X1 >= 160).ToList();
        var hud = Host.HudBounds.Inflate(40);
        for (int tries = 0; tries < 40; tries++)
        {
            Vec2 c;
            IntPtr hwnd = IntPtr.Zero;
            if (surfaces.Count > 0 && Rng.NextDouble() < 0.45)
            {
                var p = surfaces[Rng.Next(surfaces.Count)];
                c = new Vec2(p.X1 + 40 + Rng.NextDouble() * (p.X2 - p.X1 - 80), p.Y);
                hwnd = p.Hwnd;
            }
            else
            {
                c = new Vec2(a.Left + 60 + Rng.NextDouble() * (a.Width - 120), a.Bottom);
            }
            if (tries < 35 && Math.Abs(c.X - _ball.Pos.X) < 280) continue;
            if (hud.Contains(new Point(c.X, c.Y - 40))) continue;
            _cup = c;
            _cupHwnd = hwnd;
            _cupGen = Host.Platforms.Generation;
            return;
        }
        _cup = new Vec2(_ball.Pos.X < a.Center.X ? a.Right - 120 : a.Left + 120, a.Bottom);
        _cupHwnd = IntPtr.Zero;
    }

    /// <summary>A cup on a window top rides along with the window, or drops to the taskbar if the window goes.</summary>
    void TrackCup()
    {
        var plats = Host.Platforms;
        if (plats.Generation == _cupGen) return;
        _cupGen = plats.Generation;
        if (_cupHwnd == IntPtr.Zero) return;
        _cup += plats.DeltaOf(_cupHwnd);
        bool supported = plats.Items.Any(p => p.Hwnd == _cupHwnd && _cup.X >= p.X1 + 4 && _cup.X <= p.X2 - 4 && Math.Abs(p.Y - _cup.Y) < 3);
        if (supported) return;
        var a = Host.Arena;
        _cupHwnd = IntPtr.Zero;
        _cup = new Vec2(Clamp(_cup.X, a.Left + 40, a.Right - 40), a.Bottom);
    }

    // ------------------------------------------------------------------ input

    public override void CollectHitShapes(List<HitShape> into) => into.Add(HitShape.Circle(_ball.Pos, Reach));

    public override bool PointerDown(Vec2 p, bool right)
    {
        if ((p - _ball.Pos).Length > Reach || !BallReady) return false;
        if (_roundOver) NewRound();
        _aiming = true;
        _pull = default;
        return true;
    }

    public override void PointerUp(Vec2 p)
    {
        if (!_aiming) return;
        _aiming = false;
        HideGuide();
        double len = _pull.Length;
        if (len >= MinPull) Putt(-_pull / len, SpeedFor(len));
    }

    static double SpeedFor(double pull) => MaxSpeed * Math.Pow(Math.Min(pull, MaxPull) / MaxPull, 1.35);

    void Putt(Vec2 dir, double speed)
    {
        _ball.Place(_ball.Pos + new Vec2(0, -0.5), dir * speed);
        _ball.Spin = dir.X * speed * 0.8;
        _strokes++;
        _roundStrokes++;
        Host.Sound.Play("kick", 0.45 + 0.4 * Math.Min(1, speed / MaxSpeed), 1.9);
        Changed();
    }

    public override void Summon(Vec2 p)
    {
        if (_sinking) return;
        var a = Host.Arena;
        _ball.Place(new Vec2(Clamp(p.X, a.Left + R, a.Right - R), Clamp(p.Y, a.Top + R, a.Bottom - R)));
        if (_roundOver) return;
        _strokes++;
        _roundStrokes++;
        Host.Fx.Popup(_ball.Pos - new Vec2(0, 40), "+1", Color.FromRgb(255, 150, 150), 24, 1.0, "penalty drop");
        Changed();
    }

    // ------------------------------------------------------------------ simulation

    public override bool Update(double dt)
    {
        _time += dt;
        bool busy = _aiming || _sinking;
        if (_aiming)
        {
            var pull = Host.Pointer - _ball.Pos;
            if (pull.Length > MaxPull) pull *= MaxPull / pull.Length;
            _pull = pull;
            UpdateGuide();
        }

        TrackCup();
        if (_sinking)
        {
            AnimateSink(dt);
        }
        else
        {
            _acc += dt;
            while (_acc >= Step)
            {
                _acc -= Step;
                SimStep(Step);
            }
            busy |= !_ball.Asleep;
        }

        if (busy) UpdateCloth(_time);
        Draw();
        return busy;
    }

    void SimStep(double h)
    {
        var imp = new Impacts();
        _ball.Step(h, Host, ref imp);
        if (imp.Floor > 180) PlayThrottled("bounce", Math.Min(0.5, imp.Floor / 2500), 1.8);
        if (imp.Wall > 180) PlayThrottled("bounce", Math.Min(0.4, imp.Wall / 2500), 2.0);
        if (_ball.Asleep || _roundOver) return;

        bool onCupSurface = Math.Abs(_ball.Pos.Y + R - _cup.Y) < 4;
        if (!onCupSurface || Math.Abs(_ball.Pos.X - _cup.X) >= CupHalf - 3) return;
        if (_ball.Vel.Length < SinkSpeed)
        {
            _sinking = true;
            _holeScored = false;
            _sinkT = 0;
            _sinkFrom = _ball.Pos;
            Host.Sound.Play("star", 0.7);
        }
        else if (_ball.Grounded && _ball.Vel.Y >= 0)
        {
            _ball.Vel.Y = -Math.Min(240, Math.Abs(_ball.Vel.X) * 0.2); // too fast: lips out
            PlayThrottled("rim", 0.35, 1.6);
        }
    }

    void AnimateSink(double dt)
    {
        _sinkT += dt;
        double k = Math.Min(1, _sinkT / 0.3);
        _ball.Pos = _sinkFrom + (new Vec2(_cup.X, _cup.Y - 2) - _sinkFrom) * k;
        _ballSprite.Scale = 1 - 0.75 * k;
        if (_sinkT >= 0.3 && !_holeScored)
        {
            _holeScored = true;
            HoleComplete();
        }
        if (_sinkT < 1.1) return;

        _sinking = false;
        _ballSprite.Scale = 1;
        _ball.Place(new Vec2(_cup.X, _cup.Y - R - 1)); // the next tee is where this cup was
        if (_hole >= HolesPerRound) EndRound();
        else NextHole();
    }

    void HoleComplete()
    {
        int diff = _strokes - _par;
        _parDone += _par;
        string label = _strokes == 1 ? "HOLE IN ONE!" : diff <= -2 ? "EAGLE!" : diff == -1 ? "BIRDIE!" : diff == 0 ? "PAR" :
            diff == 1 ? "BOGEY" : diff == 2 ? "DOUBLE BOGEY" : $"+{diff}";
        bool great = _strokes == 1 || diff < 0;
        var color = great ? Gold : diff == 0 ? Colors.White : Color.FromRgb(255, 160, 160);
        Host.Fx.Popup(_cup - new Vec2(0, 95), label, color, _strokes == 1 ? 40 : 32, 1.4,
            $"{_strokes} stroke{(_strokes == 1 ? "" : "s")} · par {_par}");
        if (great)
        {
            Host.Fx.Burst(_cup - new Vec2(0, 20), Confetti, 30, 420, 700, 6, 1.0);
            Host.Sound.Play(_strokes == 1 ? "best" : "score", 0.8);
        }
        Changed();
    }

    void EndRound()
    {
        _roundOver = true;
        int rel = _roundStrokes - _parDone;
        var s = Host.Settings;
        bool best = s.BestGolf is not int previous || rel < previous;
        if (best)
        {
            s.BestGolf = rel;
            Host.SaveSettings();
        }
        var a = Host.Arena;
        var at = new Vec2(a.Center.X, a.Top + a.Height * 0.3);
        Host.Fx.Popup(at, best ? "NEW BEST ROUND!" : "ROUND COMPLETE", best ? Gold : Colors.White, 38, 2.4,
            $"{_roundStrokes} strokes · {Rel(rel)} vs par");
        if (best)
        {
            Host.Fx.Burst(at, Confetti, 40, 520, 700, 7, 1.1);
            Host.Sound.Play("best", 0.8);
        }
        else
        {
            Host.Sound.Play("buzzer", 0.3);
        }
        Changed();
    }

    // ------------------------------------------------------------------ visuals

    void Draw()
    {
        _ballSprite.Set(_ball.Pos, _ball.Angle);
        _flag.Set(_cup);
        _ring.Set(_ball.Pos);
        _ring.IsVisible = BallReady;
    }

    void UpdateCloth(double t)
    {
        double wave = Math.Sin(t * 5) * 3;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(1, -64), true);
            ctx.QuadraticBezierTo(new Point(12, -61 + wave), new Point(28, -56 + wave * 0.6));
            ctx.LineTo(new Point(1, -46));
            ctx.EndFigure(true);
        }
        _cloth.Data = g;
    }

    void UpdateGuide()
    {
        double len = _pull.Length;
        if (len < MinPull)
        {
            HideGuide();
            return;
        }
        var v = -_pull / len * SpeedFor(len);
        var start = _ball.Pos;
        for (int i = 0; i < _dots.Length; i++)
        {
            double t = (i + 1) * 0.045;
            var p = start + v * t + new Vec2(0, _ball.Gravity * 0.5 * t * t);
            if (p.Y > start.Y) p.Y = start.Y; // a ground putt rolls along the ground
            var tr = (TranslateTransform)_dots[i].RenderTransform!;
            tr.X = p.X - 2.5;
            tr.Y = p.Y - 2.5;
            _dots[i].Opacity = 0.8 * (1 - (double)i / _dots.Length);
            _dots[i].IsVisible = true;
        }
    }

    void HideGuide()
    {
        foreach (var d in _dots) d.IsVisible = false;
    }

    void PlayThrottled(string name, double vol, double pitch = 1)
    {
        if (_lastSound.TryGetValue(name, out double t) && _time - t < 0.08) return;
        _lastSound[name] = _time;
        Host.Sound.Play(name, vol, pitch);
    }

    static Sprite MakeBall(double r)
    {
        var s = new Sprite { IsHitTestVisible = false };
        s.Children.Insert(0, Art.Circle(1.5, 3, r, Art.Brush(55, 0, 0, 0)));
        var body = new RadialGradientBrush { GradientOrigin = new RelativePoint(0.35, 0.3, RelativeUnit.Relative) };
        body.GradientStops.Add(new GradientStop(Colors.White, 0));
        body.GradientStops.Add(new GradientStop(Color.FromRgb(0xE3, 0xE6, 0xEA), 0.7));
        body.GradientStops.Add(new GradientStop(Color.FromRgb(0xAE, 0xB4, 0xBC), 1));
        s.Rotor.Children.Add(Art.Circle(0, 0, r, body, Art.Brush("#8A9099"), 1));
        foreach (var (x, y) in new[] { (-4.0, -3.0), (3.0, -5.0), (5.0, 2.0), (-2.0, 4.0), (0.5, -0.5), (-6.0, 2.0) })
            s.Rotor.Children.Add(Art.Circle(x * r / 11, y * r / 11, r * 0.09, Art.Brush(70, 90, 100, 110)));
        return s;
    }

    public override void DemoTick()
    {
        if (!BallReady || _aiming) return;
        if (_roundOver) NewRound();
        if (Math.Abs(_ball.Pos.Y + R - _cup.Y) < 4)
        {
            double dx = _cup.X - _ball.Pos.X;
            double speed = Math.Min(MaxSpeed, Math.Abs(dx) * _ball.RollFriction + 40 + Rng.NextDouble() * 30);
            Putt(new Vec2(Math.Sign(dx), 0), speed);
        }
        else
        {
            double T = 0.9 + Rng.NextDouble() * 0.2;
            var v = new Vec2((_cup.X - _ball.Pos.X) / T, (_cup.Y - R - _ball.Pos.Y - 0.5 * _ball.Gravity * T * T) / T);
            Putt(v.Normalized(), Math.Min(2400, v.Length));
        }
    }
}
