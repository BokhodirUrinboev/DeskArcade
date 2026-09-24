using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>
/// Paper Toss: grab the crumpled paper ball in the corner and flick it into the wastebasket, which stands on a
/// window top or on the far side of the taskbar. A desk fan at the edge of the screen blows a new wind every throw,
/// stronger the longer the streak. Every basket scores (a swish scores double) and moves the bin; one miss ends the run.
/// </summary>
public sealed class PaperTossGame : MiniGame
{
    const double R = PaperFlight.R, Step = PaperFlight.Step, Reach = R + 14, MinThrow = 250, ThrowTimeout = 5;
    const double SpotInset = 130, ZoneHalfW = 100, ZoneH = 280, FanInset = 34, FanHub = 52, MinBinDist = 380, BinMargin = 110;
    const double FanSpinAfterWind = 1.2, FanSpinDown = 0.8;

    /// <summary>A fair run for a decent player: five or six baskets before the miss, a swish or two among them.</summary>
    public const int FairRound = 6;
    /// <summary>About how long such a run takes, in seconds.</summary>
    public const double TypicalRoundSeconds = 30;

    static readonly Color Gold = Color.FromRgb(255, 209, 102);
    static readonly Color[] Confetti = { Gold, Colors.White, Color.FromRgb(142, 201, 240), Color.FromRgb(6, 214, 160) };
    static readonly IBrush WindBrush = Art.Brush("#8EC9F0");

    readonly BallBody _paper = new(R) { Gravity = 0, AirDrag = 0, Restitution = 0.3, WallRestitution = 0.4, RollFriction = 5 };
    readonly Sprite _paperSprite = new() { IsHitTestVisible = false };
    readonly Path _crumple = new()
    {
        Stroke = Art.Brush("#A8A499"), StrokeThickness = 1, StrokeJoin = PenLineJoin.Round, Fill = PaperFill(),
    };
    readonly Path _creases = new()
    {
        Stroke = Art.Brush(170, 150, 146, 134), StrokeThickness = 0.9, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
    };
    readonly Sprite _binBack = MakeBinBack(), _binFront = MakeBinFront();
    readonly Sprite _fan = new() { IsHitTestVisible = false };
    readonly Sprite _blades = MakeBlades();
    readonly Path _windArrow = new() { Stroke = WindBrush, StrokeThickness = 3, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round, Width = 44, Height = 16 };
    readonly TextBlock _windText = new()
    {
        FontFamily = Fx.Font, FontWeight = FontWeight.Bold, FontSize = 14, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center,
    };
    readonly Border _windTag;
    readonly Rectangle _zone = new()
    {
        Stroke = Art.Brush(80, 255, 255, 255), StrokeThickness = 1.5, StrokeDashArray = new AvaloniaList<double> { 4, 4 },
        RadiusX = 8, RadiusY = 8, IsVisible = false, IsHitTestVisible = false,
    };
    readonly List<(double t, Vec2 p)> _trail = new();
    readonly Dictionary<string, double> _lastSound = new();

    Vec2 _spot, _bin, _grabOffset;
    IntPtr _binHwnd;
    int _binGen = -1, _score, _streak;
    double _wind, _time, _acc, _sinceThrow, _stillT, _resetIn = -1, _bladeAngle, _bladeSpin, _binAngle;
    Anims.Tween? _spinTween, _rockTween;
    long _bestBefore;
    bool _placed, _holding, _inFlight, _touched, _scored, _runOver, _onLeft, _fanLeft;

    public PaperTossGame(IGameHost host) : base(host)
    {
        _paperSprite.Children.Insert(0, Art.Circle(2, 4, R * 0.9, Art.Brush(50, 0, 0, 0)));
        _paperSprite.Rotor.Children.Add(_crumple);
        _paperSprite.Rotor.Children.Add(_creases);
        Crumple();
        BuildFan();

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(_windArrow);
        row.Children.Add(_windText);
        _windTag = new Border
        {
            Background = Art.Brush(170, 22, 27, 36), CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 4),
            Child = row, IsHitTestVisible = false,
        };

        Layer.Children.Add(_zone);
        Layer.Children.Add(_fan);
        Layer.Children.Add(_windTag);
        Layer.Children.Add(_binBack);
        Layer.Children.Add(_paperSprite);
        Layer.Children.Add(_binFront);
    }

    public override string Id => "toss";
    public override string Title => "Paper Toss";

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        s.Children.Add(Art.PathOf("M-8,-2 L8,-2 L6,10 L-6,10 Z", Art.Brush(150, 40, 46, 54), Art.Brush("#9AA3AD"), 1.5));
        s.Children.Add(Art.PathOf("M-4,-2 L-3,10 M0,-2 L0,10 M4,-2 L3,10", null, Art.Brush("#9AA3AD"), 1));
        s.Children.Add(Art.PathOf("M-5,-12 L-1,-15 L4,-14 L6,-9 L3,-5 L-2,-5 L-6,-8 Z", Art.Brush("#F3F1EA"), Art.Brush("#A8A499"), 1));
        return s;
    }

    public override HudInfo Hud => new(
        _score.ToString(CultureInfo.InvariantCulture),
        _streak > 0 ? L.F("Streak {0} · mind the wind", _streak) : L.T("Grab the paper ball and flick it into the bin"),
        L.F("Best {0}", Host.Stats.Get("toss.best")));

    bool Resting => !_inFlight && !_holding && _resetIn < 0 && (_paper.Asleep || (_paper.Grounded && _paper.Vel.Length < 30));

    // ------------------------------------------------------------------ game flow

    public override void Layout()
    {
        var a = Host.Arena;
        if (!_placed)
        {
            _placed = true;
            _onLeft = Rng.NextDouble() < 0.5;
            SetSpot();
            PlaceBin();
            ResetPaper();
        }
        else
        {
            SetSpot();
            if (_binHwnd == IntPtr.Zero) _bin.Y = a.Bottom;
            bool fits = _bin.X >= a.Left + BinMargin && _bin.X <= a.Right - BinMargin && _bin.Y - PaperFlight.BinH > a.Top + 60;
            if (!fits || !Supported() || Math.Abs(_bin.X - _spot.X) < MinBinDist) PlaceBin();
            else DrawBin(); // the floor may have moved under it
            if (!_inFlight && !_holding && _resetIn < 0) _paper.Place(_spot);
            PlaceFan();
        }
        Draw();
        Host.HudChanged();
    }

    public override void Deactivate()
    {
        _holding = false;
        _zone.IsVisible = false;
        Anims.Finish();
        if (_resetIn >= 0)
        {
            _resetIn = -1;
            NextThrow();
        }
        else if (_inFlight)
        {
            ResetPaper(); // a throw cut short by switching games does not count
        }
    }

    void SetSpot()
    {
        var a = Host.Arena;
        _spot = new Vec2(_onLeft ? a.Left + SpotInset : a.Right - SpotInset, a.Bottom - R);
        Canvas.SetLeft(_zone, _spot.X - ZoneHalfW - R);
        Canvas.SetTop(_zone, a.Bottom - ZoneH - R);
        _zone.Width = (ZoneHalfW + R) * 2;
        _zone.Height = ZoneH + R;
    }

    /// <summary>Back at the throw spot, freshly crumpled, with a new wind.</summary>
    void ResetPaper()
    {
        _inFlight = _scored = _touched = false;
        _paper.Place(_spot);
        _paper.Angle = Rng.NextDouble() * 360;
        Crumple();
        RollWind();
        Host.HudChanged();
    }

    void RollWind()
    {
        double max = PaperFlight.WindLimit(_streak);
        _wind = Math.Round((Rng.NextDouble() * 2 - 1) * max, 1);
        if (_wind != 0) _fanLeft = _wind > 0; // the fan blows away from its own edge
        PlaceFan();
        SpinFan();
    }

    /// <summary>After a basket the bin moves; after a miss a new run starts, maybe from the other corner.</summary>
    void NextThrow()
    {
        if (_runOver)
        {
            _runOver = false;
            _score = _streak = 0;
            _onLeft = Rng.NextDouble() < 0.5;
            SetSpot();
        }
        PlaceBin();
        ResetPaper();
        Draw();
    }

    void Basket()
    {
        _scored = true;
        _inFlight = false;
        bool swish = !_touched;
        int pts = PaperFlight.Points(swish);
        if (_score == 0) _bestBefore = Host.Stats.Get("toss.best"); // the record this run has to beat
        _streak++;
        _score += pts;
        Host.Stats.Add("toss.baskets");
        if (swish) Host.Stats.Add("toss.swishes");
        Host.Stats.Max("toss.run", _score);
        Host.Stats.Max("toss.best", _score); // saved as it grows, so quitting mid-run keeps it

        var at = new Vec2(_bin.X, _bin.Y - PaperFlight.BinH - 50);
        Host.ShareAction(PaperFlight.Opening(_bin), pts);
        Host.Fx.Popup(at, swish ? L.T("Swish!") : L.T("In!"), swish ? Gold : Colors.White, swish ? 34 : 30, 1.2, $"+{pts}");
        if (swish)
        {
            SwishFlash();
            Host.Fx.Burst(at + new Vec2(0, 30), Confetti, 18, 360, 700, 5, 0.8);
        }
        Host.Sound.Play(swish ? "swish" : "score", swish ? 0.8 : 0.5);
        if (swish) Host.Sound.Play("score", 0.45);
        _resetIn = 1.0;
        Host.HudChanged();
    }

    void Miss()
    {
        _inFlight = false;
        _runOver = true;
        _inRun = false;
        Host.ShareAction(_paper.Pos, 0);
        Host.RoundEnded(_score);
        long before = _score > 0 ? _bestBefore : Host.Stats.Get("toss.best");
        bool best = _score > before;

        var at = _paper.Pos - new Vec2(0, 90);
        if (best)
        {
            Host.Fx.Popup(at, L.T("NEW BEST!"), Gold, 32, 1.8, L.F("{0} points", _score));
            Host.Fx.Burst(at, Confetti, 30, 450, 700, 6, 1.0);
            Host.Sound.Play("best", 0.8);
        }
        else
        {
            Host.Fx.Popup(at, L.T("Missed!"), Color.FromRgb(255, 130, 130), 28, 1.5, L.F("{0} points", _score)); // Popup makes it colour-blind safe
            Host.Sound.Play("buzzer", 0.4);
        }
        _resetIn = 1.4;
        Host.HudChanged();
    }

    // ------------------------------------------------------------------ the bin and the fan

    /// <summary>On a suitable window top when there is one (most of the time), otherwise on the far side of the taskbar.</summary>
    void PlaceBin()
    {
        var a = Host.Arena;
        var hud = Host.HudBounds.Inflate(20);
        int dir = _onLeft ? 1 : -1;
        double reach = Math.Min(1500, a.Width - SpotInset - BinMargin);
        var tops = new List<(Vec2 at, IntPtr hwnd)>();
        foreach (var p in Host.Platforms.Items)
        {
            if (p.X2 - p.X1 < PaperFlight.BinTopW + 30 || p.Y < a.Top + PaperFlight.BinH + 220 || p.Y > a.Bottom - 60) continue;
            double lo = Math.Max(p.X1 + PaperFlight.BinTopW / 2 + 8, a.Left + BinMargin);
            double hi = Math.Min(p.X2 - PaperFlight.BinTopW / 2 - 8, a.Right - BinMargin);
            if (dir > 0) lo = Math.Max(lo, _spot.X + MinBinDist);
            else hi = Math.Min(hi, _spot.X - MinBinDist);
            if (hi < lo) continue;
            var at = new Vec2(lo + Rng.NextDouble() * (hi - lo), p.Y);
            if (!BinArea(at).Intersects(hud) && Reachable(at)) tops.Add((at, p.Hwnd));
        }
        if (tops.Count > 0 && Rng.NextDouble() < 0.75)
        {
            var (at, hwnd) = tops[Rng.Next(tops.Count)];
            SetBin(at, hwnd);
            return;
        }

        Vec2 floor = default;
        for (int tries = 0; tries < 12; tries++)
        {
            double dist = MinBinDist + Rng.NextDouble() * Math.Max(0, reach - MinBinDist);
            floor = new Vec2(Clamp(_spot.X + dir * dist, a.Left + BinMargin, a.Right - BinMargin), a.Bottom);
            if (!BinArea(floor).Intersects(hud) && Reachable(floor)) break;
        }
        SetBin(floor, IntPtr.Zero);
    }

    Rect BinArea(Vec2 at) => new(at.X - PaperFlight.BinTopW / 2 - 10, at.Y - PaperFlight.BinH - 40, PaperFlight.BinTopW + 20, PaperFlight.BinH + 40);

    /// <summary>A bin is fair if a throw from the spot can sink it in the strongest wind either way.</summary>
    bool Reachable(Vec2 at)
    {
        double wind = PaperFlight.WindLimit(_streak), top = Host.Arena.Top;
        var target = PaperFlight.Opening(at);
        return PaperFlight.SolveThrow(_spot, target, wind, out _, PaperFlight.MaxThrow * 0.85, top)
               && PaperFlight.SolveThrow(_spot, target, -wind, out _, PaperFlight.MaxThrow * 0.85, top);
    }

    void SetBin(Vec2 at, IntPtr hwnd)
    {
        _bin = at;
        _binHwnd = hwnd;
        _binGen = Host.Platforms.Generation;
        DrawBin();
    }

    bool Supported()
    {
        if (_binHwnd == IntPtr.Zero) return true;
        foreach (var p in Host.Platforms.Items)
            if (p.Hwnd == _binHwnd && Math.Abs(p.Y - _bin.Y) < 3 && _bin.X >= p.X1 + 10 && _bin.X <= p.X2 - 10) return true;
        return false;
    }

    /// <summary>A bin on a window rides along with it, and finds a new place when the window closes or is covered.</summary>
    void TrackBin()
    {
        var plats = Host.Platforms;
        if (plats.Generation == _binGen) return;
        _binGen = plats.Generation;
        if (_binHwnd == IntPtr.Zero) return;
        var d = plats.DeltaOf(_binHwnd);
        _bin += d;
        if (_scored)
        {
            _paper.Pos += d; // the paper inside travels with it
            _paper.Wake();
        }
        var a = Host.Arena;
        if (Supported() && _bin.X >= a.Left + PaperFlight.BinTopW / 2 && _bin.X <= a.Right - PaperFlight.BinTopW / 2) DrawBin();
        else PlaceBin();
    }

    void PlaceFan()
    {
        var a = Host.Arena;
        double x = _fanLeft ? a.Left + FanInset : a.Right - FanInset;
        _fan.Set(new Vec2(x, a.Bottom));

        double strength = Math.Abs(_wind);
        _windText.Text = L.F("Wind {0}", strength.ToString("0.0", CultureInfo.InvariantCulture));
        _windArrow.IsVisible = strength > 0;
        double len = 14 + strength * 4, x0 = _wind > 0 ? 2 : 42, x1 = _wind > 0 ? x0 + len : x0 - len, back = _wind > 0 ? -6 : 6;
        string F(double v) => Art.F(v);
        _windArrow.Data = Geometry.Parse($"M{F(x0)},8 L{F(x1)},8 M{F(x1 + back)},2 L{F(x1)},8 L{F(x1 + back)},14");

        _windTag.Measure(Size.Infinity);
        var size = _windTag.DesiredSize;
        Canvas.SetLeft(_windTag, Clamp(x - size.Width / 2, a.Left + 6, Math.Max(a.Left + 6, a.Right - size.Width - 6)));
        Canvas.SetTop(_windTag, a.Bottom - FanHub - 30 - size.Height);
    }

    // ------------------------------------------------------------------ input

    public override void CollectHitShapes(List<HitShape> into)
    {
        if (!_inFlight && _resetIn < 0) into.Add(HitShape.Circle(_paper.Pos, Reach));
    }

    public override bool PointerDown(Vec2 p, bool right)
    {
        if (!Resting || (p - _paper.Pos).Length > Reach) return false;
        _holding = true;
        _grabOffset = _paper.Pos - p;
        _trail.Clear();
        _zone.IsVisible = true;
        Host.Sound.Play("rustle", 0.25, 1.2);
        return true;
    }

    public override void PointerUp(Vec2 p)
    {
        if (!_holding) return;
        _holding = false;
        _zone.IsVisible = false;
        var v = ThrowVelocity();
        if (v.Length < MinThrow) _paper.Place(_paper.Pos, v); // just set down: not a throw
        else Throw(v);
    }

    public override void PointerCancel()
    {
        if (!_holding) return;
        _holding = false; // cut off mid-flick: set it down where it is, no throw
        _zone.IsVisible = false;
        _paper.Place(_paper.Pos);
    }

    /// <summary>A LAN race is one run: it starts with the first throw and ends at the first miss.</summary>
    public override bool SupportsLan => true;
    public override (int Score, bool Active)? Race => (_score, _inRun);
    public override int RaceBaseline => FairRound;
    public override int RaceBest => (int)Host.Stats.Get("toss.best");
    public override double RaceSeconds => TypicalRoundSeconds;

    public override void StartRace()
    {
        if (_inRun) return;
        if (_runOver) NextThrow(); // the last run's miss is still settling: start fresh now
        _inRun = true;
        Host.RoundStarted();
    }

    bool _inRun;

    void Throw(Vec2 v)
    {
        if (!_inRun)
        {
            _inRun = true;
            Host.RoundStarted();
        }
        _paper.Place(_paper.Pos, v);
        _paper.Spin = (Rng.NextDouble() - 0.5) * 500 - v.X * 0.15;
        _inFlight = true;
        _scored = _touched = false;
        _sinceThrow = _stillT = 0;
        Host.Sound.Play("rustle", 0.55, 0.95 + Rng.NextDouble() * 0.15);
        Host.Sound.Play("whoosh", Math.Min(1, v.Length / 3000) * 0.35, 1.2);
        Host.HudChanged();
    }

    Vec2 ClampToZone(Vec2 p)
    {
        var a = Host.Arena;
        p.X = Clamp(Clamp(p.X, _spot.X - ZoneHalfW, _spot.X + ZoneHalfW), a.Left + R, a.Right - R);
        p.Y = Clamp(p.Y, a.Bottom - ZoneH, a.Bottom - R);
        return p;
    }

    Vec2 ThrowVelocity()
    {
        if (_trail.Count < 2) return default;
        var last = _trail[^1];
        var first = _trail[0];
        foreach (var s in _trail)
        {
            if (last.t - s.t <= 0.07)
            {
                first = s;
                break;
            }
        }
        double dt = last.t - first.t;
        if (dt < 0.008) return default;
        Vec2 v = (last.p - first.p) / dt;
        return v.Length > PaperFlight.MaxThrow ? v * (PaperFlight.MaxThrow / v.Length) : v;
    }

    public override void Summon(Vec2 p)
    {
        if (!Resting) return;
        _paper.Place(ClampToZone(p));
    }

    // ------------------------------------------------------------------ simulation

    public override bool Update(double dt)
    {
        _time += dt;
        bool busy = _holding;

        if (_holding)
        {
            var target = ClampToZone(Host.Pointer + _grabOffset);
            _paper.Pos = target;
            _paper.Vel = default;
            _trail.Add((_time, target));
            while (_trail.Count > 0 && _time - _trail[0].t > 0.15) _trail.RemoveAt(0);
        }

        TrackBin();

        _acc = Math.Min(_acc + dt, 0.1); // after a long idle wait, don't replay the gap
        while (_acc >= Step)
        {
            _acc -= Step;
            SimStep(Step);
        }

        if (_inFlight)
        {
            busy = true;
            _sinceThrow += dt;
            _stillT = _paper.Vel.Length < 20 ? _stillT + dt : 0;
            if ((_paper.Asleep && _sinceThrow > 0.3) || _stillT > 0.5 || _sinceThrow > ThrowTimeout) Miss();
        }

        // the fan runs at the wind's speed: while the paper flies, and for a moment after a new wind is called
        double spin = _inFlight ? 1 : _bladeSpin;
        if (spin > 0 && !Fx.ReducedMotion && _wind != 0)
        {
            _bladeAngle = (_bladeAngle + dt * spin * (120 + Math.Abs(_wind) * 160) * (_fanLeft ? 1 : -1)) % 360;
            _blades.Set(new Vec2(0, -FanHub), _bladeAngle);
        }

        if (_resetIn >= 0)
        {
            busy = true;
            if ((_resetIn -= dt) < 0)
            {
                _resetIn = -1;
                NextThrow();
            }
        }

        busy |= !_paper.Asleep || Anims.Update(dt);
        Draw();
        return busy;
    }

    void SimStep(double h)
    {
        if (_holding) return;
        // the fan only blows on a thrown ball in the air; asleep, nothing moves
        if (!_paper.Asleep) PaperFlight.Accelerate(ref _paper.Vel, _inFlight && !_paper.Grounded ? _wind : 0, h);
        var imp = new Impacts();
        _paper.Step(h, Host, ref imp);
        var hit = PaperFlight.Collide(_paper, _bin);

        if (hit.Rim > 30)
        {
            if (_inFlight) _touched = true;
            Rock();
            PlayThrottled("rim", Math.Min(0.7, hit.Rim / 900), 1.25 + Rng.NextDouble() * 0.15);
        }
        if (hit.Wall > 30)
        {
            if (_inFlight) _touched = true;
            PlayThrottled("rim", Math.Min(0.4, hit.Wall / 1400), 0.8);
        }
        if (hit.Bottom > 80) PlayThrottled("rustle", Math.Min(0.35, hit.Bottom / 2000), 0.8);
        if (imp.Floor > 250) PlayThrottled("rustle", Math.Min(0.35, imp.Floor / 3000), 1.3);

        if (_inFlight && PaperFlight.IsIn(_paper.Pos, _paper.Vel, _bin)) Basket();
    }

    // ------------------------------------------------------------------ visuals

    void Draw()
    {
        _paperSprite.Set(_paper.Pos, _paper.Angle);
        // in the air the ball crumples and uncrumples a little, like paper does
        _paperSprite.Scale = _inFlight && !Fx.ReducedMotion ? 1 + 0.07 * Math.Sin(_sinceThrow * 16) : 1;
    }

    void DrawBin()
    {
        _binBack.Set(_bin, _binAngle);
        _binFront.Set(_bin, _binAngle);
    }

    // ------------------------------------------------------------------ animation

    /// <summary>The blades run at the wind's speed for a moment after a new wind is called, then wind down.</summary>
    void SpinFan()
    {
        _spinTween?.Cancel();
        _bladeSpin = 1;
        _spinTween = Anims.Add(FanSpinDown, k => _bladeSpin = 1 - k, Ease.OutQuad, () =>
        {
            _bladeSpin = 0;
            _spinTween = null;
        }, FanSpinAfterWind);
    }

    /// <summary>A rim hit rocks the bin on its base, away from the side that was struck.</summary>
    void Rock()
    {
        double dir = _paper.Pos.X < _bin.X ? 1 : -1;
        _rockTween?.Cancel();
        _rockTween = Anims.Add(0.5, k =>
        {
            _binAngle = dir * 9 * Math.Sin(k * Math.PI * 3) * (1 - k);
            DrawBin();
        }, Ease.Linear, () =>
        {
            _binAngle = 0;
            DrawBin();
            _rockTween = null;
        });
    }

    /// <summary>A swish: a gold ring bursts out of the bin's opening.</summary>
    void SwishFlash()
    {
        var opening = PaperFlight.Opening(_bin);
        var ring = new Ellipse { Stroke = Art.Brush(Gold), StrokeThickness = 3, Fill = Art.Brush(60, 255, 209, 102), IsHitTestVisible = false };
        Layer.Children.Add(ring);
        Anims.Add(0.45, k =>
        {
            double w = (PaperFlight.BinTopW + 10) * (1 + 0.8 * k), h = 16 * (1 + 0.8 * k);
            ring.Width = w;
            ring.Height = h;
            Canvas.SetLeft(ring, opening.X - w / 2);
            Canvas.SetTop(ring, opening.Y - 2 - h / 2);
            ring.Opacity = 1 - k;
        }, Ease.OutCubic, () => Layer.Children.Remove(ring));
    }

    void PlayThrottled(string name, double vol, double pitch = 1)
    {
        if (_lastSound.TryGetValue(name, out double t) && _time - t < 0.06) return;
        _lastSound[name] = _time;
        Host.Sound.Play(name, vol, pitch);
    }

    /// <summary>A new crumple: an irregular outline and a few creases, different every time.</summary>
    void Crumple()
    {
        string F(double v) => Art.F(v);
        const int corners = 11;
        var outline = new StringBuilder();
        for (int i = 0; i < corners; i++)
        {
            var (x, y) = Art.Polar(R * (0.8 + Rng.NextDouble() * 0.24), (i + (Rng.NextDouble() - 0.5) * 0.6) * 360.0 / corners);
            outline.Append(i == 0 ? 'M' : 'L').Append(F(x)).Append(',').Append(F(y)).Append(' ');
        }
        _crumple.Data = Geometry.Parse(outline.Append('Z').ToString());

        var creases = new StringBuilder();
        for (int i = 0; i < 4; i++)
        {
            var (x1, y1) = Art.Polar(R * 0.75, Rng.NextDouble() * 360);
            var (x2, y2) = Art.Polar(R * 0.3 * Rng.NextDouble(), Rng.NextDouble() * 360);
            var (x3, y3) = Art.Polar(R * (0.4 + Rng.NextDouble() * 0.3), Rng.NextDouble() * 360);
            creases.Append($"M{F(x1)},{F(y1)} L{F(x2)},{F(y2)} L{F(x3)},{F(y3)} ");
        }
        _creases.Data = Geometry.Parse(creases.ToString());
    }

    static RadialGradientBrush PaperFill()
    {
        var brush = new RadialGradientBrush { GradientOrigin = new RelativePoint(0.35, 0.3, RelativeUnit.Relative) };
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xFD, 0xFC, 0xF7), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xEC, 0xE9, 0xDF), 0.6));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xC9, 0xC5, 0xB8), 1));
        return brush;
    }

    // The bin is drawn around its base (the middle of its bottom): the dark inside and the far half of the rim
    // go behind the paper, the wire mesh and the near half of the rim in front of it.

    static Sprite MakeBinBack()
    {
        double t = PaperFlight.BinTopW / 2, b = PaperFlight.BinBottomW / 2, h = PaperFlight.BinH;
        string F(double v) => Art.F(v);
        var s = new Sprite { IsHitTestVisible = false };
        s.Rotor.Children.Add(Art.PathOf($"M{F(-t)},{F(-h)} L{F(t)},{F(-h)} L{F(b)},0 L{F(-b)},0 Z", Art.Brush(150, 40, 46, 54)));
        s.Rotor.Children.Add(Art.PathOf($"M{F(-t)},{F(-h)} A{F(t)},5 0 0 1 {F(t)},{F(-h)}", null, Art.Brush("#8A939E"), 3.5));
        return s;
    }

    static Sprite MakeBinFront()
    {
        double t = PaperFlight.BinTopW / 2, b = PaperFlight.BinBottomW / 2, h = PaperFlight.BinH;
        string F(double v) => Art.F(v);
        var mesh = new StringBuilder();
        const int wires = 8;
        for (int i = 1; i < wires; i++)
        {
            double u = (double)i / wires;
            mesh.Append($"M{F(-t + 2 * t * u)},{F(-h)} L{F(-b + 2 * b * u)},0 ");
        }
        foreach (double k in new[] { 0.33, 0.66 })
        {
            double half = t + (b - t) * k;
            mesh.Append($"M{F(-half)},{F(-h + h * k)} L{F(half)},{F(-h + h * k)} ");
        }
        var s = new Sprite { IsHitTestVisible = false };
        s.Rotor.Children.Add(Art.PathOf(mesh.ToString(), null, Art.Brush(200, 154, 163, 173), 1.2));
        s.Rotor.Children.Add(Art.PathOf($"M{F(-t)},{F(-h)} L{F(-b)},0 M{F(t)},{F(-h)} L{F(b)},0", null, Art.Brush("#6E7782"), PaperFlight.WallThick));
        s.Rotor.Children.Add(Art.At(new Rectangle { Width = b * 2, Height = 5, RadiusX = 1.5, RadiusY = 1.5, Fill = Art.Brush("#6E7782") }, -b, -5));
        s.Rotor.Children.Add(Art.PathOf($"M{F(-t)},{F(-h)} A{F(t)},5 0 0 0 {F(t)},{F(-h)}", null, Art.Brush("#C9D0D8"), 3.5));
        s.Rotor.Children.Add(Art.Circle(-t, -h, PaperFlight.LipR, Art.Brush("#DDE3EA")));
        s.Rotor.Children.Add(Art.Circle(t, -h, PaperFlight.LipR, Art.Brush("#DDE3EA")));
        return s;
    }

    /// <summary>A little desk fan standing on the taskbar, drawn around the middle of its foot.</summary>
    void BuildFan()
    {
        var dark = Art.Brush("#3B4452");
        _fan.Children.Add(Art.At(new Rectangle { Width = 40, Height = 7, RadiusX = 3, RadiusY = 3, Fill = dark }, -20, -7));
        _fan.Children.Add(Art.At(new Rectangle { Width = 5, Height = 26, Fill = Art.Brush("#56606E") }, -2.5, -33));
        _fan.Children.Add(Art.Circle(0, -FanHub, 19, Art.Brush(60, 142, 201, 240)));
        _blades.Set(new Vec2(0, -FanHub), 0);
        _fan.Children.Add(_blades);
        _fan.Children.Add(Art.Circle(0, -FanHub, 3.5, dark));
        _fan.Children.Add(Art.Circle(0, -FanHub, 19, null, Art.Brush("#56606E"), 2));
        string y = Art.F(-FanHub);
        _fan.Children.Add(Art.PathOf($"M-19,{y} L19,{y} M0,{Art.F(-FanHub - 19)} L0,{Art.F(-FanHub + 19)}", null, Art.Brush(140, 86, 96, 110), 1));
    }

    static Sprite MakeBlades()
    {
        var s = new Sprite { IsHitTestVisible = false };
        for (int i = 0; i < 3; i++)
        {
            var blade = Art.PathOf("M0,0 C6,-4 9,-15 0,-17 C-8,-15 -6,-4 0,0 Z", Art.Brush("#8EC9F0"), Art.Brush("#5B8DB0"), 0.8);
            blade.RenderTransform = new RotateTransform(i * 120);
            blade.RenderTransformOrigin = RelativePoint.TopLeft;
            s.Rotor.Children.Add(blade);
        }
        return s;
    }

    // ------------------------------------------------------------------ demo

    public override void DemoTick()
    {
        if (!Resting) return;
        var target = PaperFlight.Opening(_bin) + new Vec2((Rng.NextDouble() - 0.5) * 12, 0);
        if (!PaperFlight.SolveThrow(_paper.Pos, target, _wind, out var v, PaperFlight.MaxThrow, Host.Arena.Top))
            v = new Vec2(_bin.X > _paper.Pos.X ? 1400 : -1400, -1400);
        Throw(v * (1 + (Rng.NextDouble() - 0.5) * 0.035)); // a slightly shaky hand, so it misses now and then
    }
}
