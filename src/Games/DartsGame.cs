using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>
/// Darts: a dartboard hangs on the screen. Press on it and hold to aim: the reticle settles, then starts to
/// wobble the longer you hold, so let go in the sweet spot. Play 501, three darts a turn, and finish on a double
/// (or the bull) in as few darts as you can; going below zero, down to 1 or out on a single is a bust.
/// </summary>
public sealed class DartsGame : MiniGame
{
    const double SurroundK = 1.24, HitMargin = 14, FlightTime = 0.18, PullDelay = 0.9, PullTime = 0.22;
    const double SettleTime = 1.2, LandSigma = 0.012, DemoSigma = 0.05, MaxLand = 1.2;

    static readonly Color Gold = Color.FromRgb(255, 209, 102);
    static readonly Color Black = Color.FromRgb(27, 27, 27), Cream = Color.FromRgb(241, 227, 192);
    static readonly Color Red = Color.FromRgb(215, 38, 61), Green = Color.FromRgb(30, 140, 69);

    sealed class Dart
    {
        public required Sprite Sprite;
        public Vec2 Offset; // from the board centre, in board radii, so the dart stays put when the board moves or scales
    }

    readonly X01 _leg = new();
    readonly Sprite _board = new() { IsHitTestVisible = false };
    readonly Canvas _dartLayer = new() { IsHitTestVisible = false };
    readonly Sprite _reticle;
    readonly Control _reticleWarn;
    readonly Dart[] _darts = new Dart[X01.DartsPerTurn];

    Vec2 _centre, _summonAt, _aim, _phase1, _phase2, _demoPoint;
    double _r, _builtR = -1, _time, _holdT, _flyT, _pullIn = -1, _pullT = -1, _lastThud = -1, _demoHold;
    bool _builtColorBlind, _summoned, _aiming, _flying, _demoAim;
    int _thrown, _demoWait;

    public DartsGame(IGameHost host) : base(host)
    {
        _reticle = MakeReticle(out _reticleWarn);
        Layer.Children.Add(_board);
        Layer.Children.Add(_dartLayer);
        Layer.Children.Add(_reticle);
        _reticle.IsVisible = false;
    }

    public override string Id => "darts";
    public override string Title => "Darts";

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        s.Children.Add(Art.Circle(0, 0, 10, Art.Brush(Black), Art.Brush("#3A3A3A"), 1));
        s.Children.Add(Art.Circle(0, 0, 8, null, Art.Brush(Art.Safe(Red)), 1.6));
        s.Children.Add(Art.Circle(0, 0, 5, Art.Brush(Cream)));
        s.Children.Add(Art.Circle(0, 0, 3.4, null, Art.Brush(Art.Safe(Green)), 1.4));
        s.Children.Add(Art.Circle(0, 0, 1.4, Art.Brush(Art.Safe(Red))));
        s.Children.Add(Art.PathOf("M1,-1 L9,-9", null, Art.Brush("#B8C0CA"), 1.8));
        s.Children.Add(Art.PathOf("M8,-8 L12,-7 L9,-9 L7,-12 Z", Art.Brush(Themes.Current.Mine)));
        return s;
    }

    public override HudInfo Hud
    {
        get
        {
            long best = Host.Stats.Get("darts.best");
            return new HudInfo(_leg.Remaining.ToString(), HudLine(), best > 0 ? L.F("Best {0} darts", best) : L.T("Best —"));
        }
    }

    string HudLine()
    {
        if (_leg.Finished) return L.F("Game shot in {0} darts · click the board for a new leg", _leg.DartsUsed);
        var route = DartsRules.Checkout(_leg.Remaining, _leg.DartsLeft);
        if (route != null) return L.F("{0}: {1}", _leg.Remaining, string.Join(" ", route.Select(Name)));
        if (_leg.DartsUsed == 0) return L.T("Hold on the board to aim, let go to throw · finish on a double");
        return L.F("Dart {0} of 3 · {1} this turn", _leg.DartsInTurn + 1, _leg.TurnStart - _leg.Remaining);
    }

    /// <summary>A segment as players call it; the notation (T20, D16) is the same in every language.</summary>
    static string Name(Segment s) => s.Multiplier == 0 ? L.T("Miss") : s.Points == 50 ? L.T("BULL") : s.ToString();

    // ------------------------------------------------------------------ layout

    public override void Layout()
    {
        var a = Host.Arena;
        double r = Clamp(Math.Min(a.Width, a.Height) * 0.2, 140, 200);
        r = Math.Max(30, Math.Min(r, (Math.Min(a.Width, a.Height) - 20) / (2 * SurroundK))); // tiny arenas still fit
        double outer = r * SurroundK;

        Vec2 c;
        if (_summoned)
        {
            c = _summonAt;
        }
        else
        {
            // right-centre, clear of the scoreboard: below it if there is room, otherwise to its left
            c = new Vec2(a.Right - outer - Math.Max(40, a.Width * 0.06), a.Center.Y);
            var hud = Host.HudBounds.Inflate(16);
            if (hud.Width > 0 && BoxOf(c, outer).Intersects(hud))
            {
                if (hud.Bottom + outer * 2 <= a.Bottom - 8) c.Y = hud.Bottom + outer;
                else c.X = hud.Left - outer;
            }
        }
        c.X = Clamp(c.X, a.Left + outer + 4, Math.Max(a.Left + outer + 4, a.Right - outer - 4));
        c.Y = Clamp(c.Y, a.Top + outer + 4, Math.Max(a.Top + outer + 4, a.Bottom - outer - 4));
        _centre = c;
        _r = r;

        if (r != _builtR || Art.ColorBlind != _builtColorBlind)
        {
            BuildBoard();
            BuildDarts();
        }
        _board.Set(_centre);
        PlaceDarts();
        Host.HudChanged();
    }

    static Rect BoxOf(Vec2 c, double r) => new(c.X - r, c.Y - r, r * 2, r * 2);

    public override void ThemeChanged() => BuildDarts();

    public override void Deactivate()
    {
        // an unfinished throw is called off; the leg itself carries on next time
        if (_flying) _darts[--_thrown].Sprite.IsVisible = false; // not scored yet: it lands only in Land()
        _aiming = _flying = _demoAim = false;
        _reticle.IsVisible = false;
        if (_pullIn > 0 || _pullT >= 0) ClearDarts();
    }

    // ------------------------------------------------------------------ input

    public override void CollectHitShapes(List<HitShape> into) => into.Add(HitShape.Circle(_centre, _r * SurroundK + HitMargin));

    public override bool PointerDown(Vec2 p, bool right)
    {
        if (right || (p - _centre).Length > _r * SurroundK + HitMargin) return false;
        if (_demoAim || _flying) return false;
        if (_leg.Finished)
        {
            NewLeg();
            return false;
        }
        if (_pullIn > 0 || _pullT >= 0 || _thrown >= X01.DartsPerTurn) ClearDarts(); // no need to wait for the pull
        StartAim();
        return true;
    }

    public override void PointerUp(Vec2 p)
    {
        if (_aiming && !_demoAim) Release();
    }

    public override void PointerCancel()
    {
        if (!_aiming || _demoAim) return;
        _aiming = false; // cut off mid-aim: no dart thrown
        _reticle.IsVisible = false;
    }

    void StartAim()
    {
        _aiming = true;
        _holdT = 0;
        _phase1 = new Vec2(Rng.NextDouble() * 6.3, Rng.NextDouble() * 6.3);
        _phase2 = new Vec2(Rng.NextDouble() * 6.3, Rng.NextDouble() * 6.3);
        _aim = (_demoAim ? _demoPoint : Host.Pointer) + Wobble(0);
        _reticle.Set(_aim);
        _reticleWarn.Opacity = 0;
        _reticle.IsVisible = true;
    }

    /// <summary>
    /// The hand's tremor: a smooth sum of sines. It settles quickly, stays small for a while, then grows the longer
    /// the throw is held, so there is a sweet spot and waiting forever doesn't pay.
    /// </summary>
    Vec2 Wobble(double t)
    {
        double late = Math.Max(0, t - SettleTime);
        double amp = _r * Math.Min(0.28, 0.012 + 0.022 * Math.Exp(-t * 5) + 0.07 * Math.Pow(late, 1.5));
        double x = Math.Sin(t * 2.3 + _phase1.X) + 0.6 * Math.Sin(t * 3.9 + _phase2.X) + 0.3 * Math.Sin(t * 7.1 + _phase1.Y);
        double y = Math.Sin(t * 2.9 + _phase1.Y) + 0.6 * Math.Sin(t * 4.7 + _phase2.Y) + 0.3 * Math.Sin(t * 6.3 + _phase2.X);
        return new Vec2(x, y) * (amp / 1.9);
    }

    void Release()
    {
        _aiming = _demoAim = false;
        _reticle.IsVisible = false;
        var off = (_aim - _centre) / _r + DartsRules.Scatter(Rng, LandSigma);
        if (off.Length > MaxLand) off *= MaxLand / off.Length; // it sticks in the surround at worst
        var dart = _darts[_thrown++];
        dart.Offset = off;
        dart.Sprite.Opacity = 0.6;
        dart.Sprite.Scale = 2.4;
        dart.Sprite.IsVisible = true;
        _flying = true;
        _flyT = 0;
        Host.Sound.Play("whoosh", 0.25, 1.6);
        DrawFlight(0);
    }

    void Land()
    {
        _flying = false;
        var dart = _darts[_thrown - 1];
        dart.Sprite.Scale = 1;
        dart.Sprite.Opacity = 1;
        var at = _centre + dart.Offset * _r;
        dart.Sprite.Set(at);

        var res = _leg.Throw(DartsRules.Score(dart.Offset, 1));
        var seg = res.Segment;
        Host.Stats.Add("darts.darts");
        if (seg.Multiplier == 3) Host.Stats.Add("darts.trebles");
        if (seg.IsBull) Host.Stats.Add("darts.bulls");

        if (_time - _lastThud > 0.06)
        {
            _lastThud = _time;
            Host.Sound.Play("thunk", seg.Multiplier == 0 ? 0.45 : 0.7, 1.7 + Rng.NextDouble() * 0.2);
        }
        if (seg.Multiplier == 3 || seg.IsBull) Host.Sound.Play("score", 0.5);
        Color col = seg.Multiplier == 0 ? Color.FromRgb(170, 176, 186) : seg.Multiplier == 3 || seg.IsBull ? Gold : seg.IsDouble ? Color.FromRgb(120, 230, 150) : Colors.White;
        Host.Fx.Popup(at - new Vec2(0, 34), Name(seg), col, seg.Multiplier >= 2 ? 28 : 24, 0.9);

        var top = _centre - new Vec2(0, _r * 0.55);
        if (res.Bust)
        {
            Host.Fx.Popup(top, L.T("BUST"), Color.FromRgb(255, 100, 100), 38, 1.6, L.F("back to {0}", _leg.Remaining));
            Host.Sound.Play("buzzer", 0.45);
        }
        else if (res.Checkout)
        {
            GameShot(top);
        }
        else if (res.TurnOver && res.TurnTotal == 180)
        {
            Host.Stats.Add("darts.180s");
            Host.Fx.Popup(top, "180!", Gold, 46, 2.0);
            Host.Fx.Burst(top, Themes.Current.Confetti, 36, 480, 700, 6, 1.0);
            Host.Sound.Play("star", 0.8);
        }
        if (res.TurnOver) _pullIn = res.Checkout ? PullDelay * 2 : PullDelay;
        Host.HudChanged();
    }

    void GameShot(Vec2 at)
    {
        int darts = _leg.DartsUsed;
        long before = Host.Stats.Get("darts.best");
        Host.Stats.Add("darts.legs");
        Host.Stats.Min("darts.best", darts);
        bool best = before == 0 || darts < before;
        Host.Fx.Popup(at, L.T("GAME SHOT!"), Gold, 42, 2.6, best ? L.F("{0} darts · new best!", darts) : L.F("{0} darts", darts));
        Host.Fx.Burst(at, Themes.Current.Confetti, 44, 540, 700, 7, 1.1);
        Host.Sound.Play("best", 0.8);
    }

    void NewLeg()
    {
        ClearDarts();
        _leg.NewLeg();
        Host.Fx.Popup(_centre - new Vec2(0, _r * 0.55), "501", Colors.White, 34, 1.2, L.T("game on!"));
        Host.Sound.Play("attention", 0.4);
        Host.HudChanged();
    }

    void ClearDarts()
    {
        foreach (var d in _darts)
        {
            d.Sprite.IsVisible = false;
            d.Sprite.Opacity = 1;
            d.Sprite.Scale = 1;
        }
        _thrown = 0;
        _pullIn = _pullT = -1;
    }

    public override void Summon(Vec2 p)
    {
        _summoned = true;
        _summonAt = p;
        Layout();
    }

    // ------------------------------------------------------------------ animation

    public override bool Update(double dt)
    {
        _time += dt;
        if (_aiming)
        {
            _holdT += dt;
            _aim = (_demoAim ? _demoPoint : Host.Pointer) + Wobble(_holdT);
            _reticle.Set(_aim);
            _reticleWarn.Opacity = Math.Clamp((_holdT - SettleTime) / 0.8, 0, 1); // the ring turns orange past the sweet spot
            if (_demoAim && _holdT >= _demoHold) Release();
        }

        if (_flying)
        {
            _flyT += dt;
            double k = Math.Min(1, _flyT / FlightTime);
            if (k >= 1) Land();
            else DrawFlight(k);
        }

        if (_pullIn > 0 && (_pullIn -= dt) <= 0)
        {
            _pullIn = -1;
            _pullT = 0;
        }
        if (_pullT >= 0)
        {
            _pullT += dt;
            double k = _pullT / PullTime;
            if (k >= 1)
            {
                ClearDarts();
                Host.HudChanged();
            }
            else
            {
                for (int i = 0; i < _thrown; i++)
                {
                    _darts[i].Sprite.Opacity = 1 - k;
                    _darts[i].Sprite.Scale = 1 + 0.4 * k; // pulled back out toward the player
                }
            }
        }
        return _aiming || _flying || _pullIn > 0 || _pullT >= 0;
    }

    void DrawFlight(double k)
    {
        var dart = _darts[_thrown - 1];
        double e = k * k;
        var land = _centre + dart.Offset * _r;
        // it comes from the thrower's hand, a little below and to the right, and shrinks into the board
        dart.Sprite.Set(land + new Vec2(_r * 0.12, _r * 0.35) * (1 - e));
        dart.Sprite.Scale = 2.4 - 1.4 * e;
        dart.Sprite.Opacity = 0.6 + 0.4 * k;
    }

    void PlaceDarts()
    {
        foreach (var d in _darts) d.Sprite.Set(_centre + d.Offset * _r);
    }

    // ------------------------------------------------------------------ visuals

    void BuildBoard()
    {
        _builtR = _r;
        _builtColorBlind = Art.ColorBlind;
        double r = _r, outer = r * SurroundK;
        var parts = _board.Rotor.Children;
        parts.Clear();

        parts.Add(Art.Circle(4, 7, outer, Art.Brush(70, 0, 0, 0)));
        parts.Add(Art.Circle(0, 0, outer, Art.Brush("#141414"), Art.Brush("#2E2E2E"), 2));

        var dark = new StringBuilder();
        var light = new StringBuilder();
        var red = new StringBuilder();
        var green = new StringBuilder();
        for (int i = 0; i < 20; i++)
        {
            double a0 = 18 * i - 9, a1 = 18 * i + 9;
            bool even = i % 2 == 0; // 20 is a black wedge with red rings
            var single = even ? dark : light;
            var ring = even ? red : green;
            Sector(single, r * DartsRules.OuterBullR, r * DartsRules.TrebleIn, a0, a1);
            Sector(ring, r * DartsRules.TrebleIn, r * DartsRules.TrebleOut, a0, a1);
            Sector(single, r * DartsRules.TrebleOut, r * DartsRules.DoubleIn, a0, a1);
            Sector(ring, r * DartsRules.DoubleIn, r * DartsRules.DoubleOut, a0, a1);
        }
        parts.Add(Art.PathOf(dark.ToString(), Art.Brush(Black)));
        parts.Add(Art.PathOf(light.ToString(), Art.Brush(Cream)));
        parts.Add(Art.PathOf(red.ToString(), Art.Brush(Art.Safe(Red))));
        parts.Add(Art.PathOf(green.ToString(), Art.Brush(Art.Safe(Green))));
        parts.Add(Art.Circle(0, 0, r * DartsRules.OuterBullR, Art.Brush(Art.Safe(Green))));
        parts.Add(Art.Circle(0, 0, r * DartsRules.BullR, Art.Brush(Art.Safe(Red))));

        // the spider: wire rings and spokes
        var wire = Art.Brush(210, 196, 200, 206);
        double thick = Math.Max(0.8, r / 170);
        var spokes = new StringBuilder();
        for (int i = 0; i < 20; i++)
        {
            var (x0, y0) = OnBoard(r * DartsRules.OuterBullR, 18 * i + 9);
            var (x1, y1) = OnBoard(r, 18 * i + 9);
            spokes.Append($"M{Art.F(x0)},{Art.F(y0)} L{Art.F(x1)},{Art.F(y1)} ");
        }
        parts.Add(Art.PathOf(spokes.ToString(), null, wire, thick));
        foreach (double k in new[] { DartsRules.BullR, DartsRules.OuterBullR, DartsRules.TrebleIn, DartsRules.TrebleOut, DartsRules.DoubleIn, DartsRules.DoubleOut })
            parts.Add(Art.Circle(0, 0, r * k, null, wire, thick));
        parts.Add(Art.Circle(0, 0, r * 1.205, null, wire, thick * 1.6)); // the number ring

        var ink = Art.Brush("#F4F1EA");
        for (int i = 0; i < 20; i++)
        {
            var text = new TextBlock
            {
                Text = DartsRules.Order[i].ToString(), FontFamily = Fx.Font, FontWeight = FontWeight.Bold,
                FontSize = r * 0.105, Foreground = ink,
            };
            text.Measure(Size.Infinity);
            var (x, y) = OnBoard(r * 1.105, 18 * i);
            parts.Add(Art.At(text, x - text.DesiredSize.Width / 2, y - text.DesiredSize.Height / 2));
        }
    }

    /// <summary>Screen offset for a radius and a clockwise angle from the top.</summary>
    static (double x, double y) OnBoard(double r, double deg)
    {
        double a = deg * Math.PI / 180;
        return (Math.Sin(a) * r, -Math.Cos(a) * r);
    }

    static void Sector(StringBuilder sb, double r0, double r1, double a0, double a1)
    {
        var (ox0, oy0) = OnBoard(r1, a0);
        var (ox1, oy1) = OnBoard(r1, a1);
        var (ix1, iy1) = OnBoard(r0, a1);
        var (ix0, iy0) = OnBoard(r0, a0);
        sb.Append($"M{Art.F(ox0)},{Art.F(oy0)} A{Art.F(r1)},{Art.F(r1)} 0 0 1 {Art.F(ox1)},{Art.F(oy1)} ")
          .Append($"L{Art.F(ix1)},{Art.F(iy1)} A{Art.F(r0)},{Art.F(r0)} 0 0 0 {Art.F(ix0)},{Art.F(iy0)} Z ");
    }

    void BuildDarts()
    {
        double len = Math.Max(24, _r * 0.3);
        for (int i = 0; i < _darts.Length; i++)
        {
            var old = _darts[i];
            var sprite = MakeDart(len);
            if (old != null)
            {
                _dartLayer.Children.Remove(old.Sprite);
                sprite.IsVisible = old.Sprite.IsVisible;
                sprite.Opacity = old.Sprite.Opacity;
                sprite.Scale = old.Sprite.Scale;
            }
            else
            {
                sprite.IsVisible = false;
            }
            _dartLayer.Children.Add(sprite);
            _darts[i] = new Dart { Sprite = sprite, Offset = old?.Offset ?? default };
        }
        PlaceDarts();
    }

    /// <summary>A dart stuck in the board, seen from a little below and to the right: the tip is the origin.</summary>
    static Sprite MakeDart(double len)
    {
        var s = new Sprite { IsHitTestVisible = false };
        var dir = new Vec2(0.5, 0.866);
        var n = new Vec2(-dir.Y, dir.X);
        string P(Vec2 v) => $"{Art.F(v.X)},{Art.F(v.Y)}";
        var barrelEnd = dir * (len * 0.38);
        var shaftEnd = dir * (len * 0.74);
        var tail = dir * len;
        s.Rotor.Children.Add(Art.PathOf($"M{P(new Vec2(3, 5))} L{P(tail + new Vec2(6, 9))}", null, Art.Brush(60, 0, 0, 0), len * 0.07));
        s.Rotor.Children.Add(Art.PathOf($"M0,0 L{P(dir * (len * 0.1))}", null, Art.Brush("#D8DDE3"), len * 0.035));
        s.Rotor.Children.Add(Art.PathOf($"M{P(dir * (len * 0.1))} L{P(barrelEnd)}", null, Art.Brush("#8E97A2"), len * 0.1));
        s.Rotor.Children.Add(Art.PathOf($"M{P(dir * (len * 0.16))} L{P(dir * (len * 0.2))} M{P(dir * (len * 0.26))} L{P(dir * (len * 0.3))}",
            null, Art.Brush("#5D6570"), len * 0.1)); // grip rings
        s.Rotor.Children.Add(Art.PathOf($"M{P(barrelEnd)} L{P(shaftEnd)}", null, Art.Brush("#2B2F36"), len * 0.05));
        var flight = Themes.Current.Mine;
        double w = len * 0.2;
        s.Rotor.Children.Add(Art.PathOf(
            $"M{P(shaftEnd)} L{P(tail + n * w)} L{P(tail + dir * (len * 0.06))} Z M{P(shaftEnd)} L{P(tail - n * w)} L{P(tail + dir * (len * 0.06))} Z",
            Art.Brush(flight), Art.Brush(Art.Blend(flight, Colors.Black, 0.45)), 1));
        return s;
    }

    static Sprite MakeReticle(out Control warn)
    {
        var s = new Sprite { IsHitTestVisible = false };
        var shadow = Art.Brush(170, 10, 10, 20);
        s.Children.Add(Art.Circle(0, 0, 11, null, shadow, 4));
        s.Children.Add(Art.PathOf("M-17,0 L-6,0 M6,0 L17,0 M0,-17 L0,-6 M0,6 L0,17", null, shadow, 3.6));
        s.Children.Add(Art.Circle(0, 0, 11, null, Brushes.White, 2));
        var ring = Art.Circle(0, 0, 11, null, Art.Brush("#FF9F1C"), 2.4);
        s.Children.Add(ring);
        s.Children.Add(Art.PathOf("M-17,0 L-6,0 M6,0 L17,0 M0,-17 L0,-6 M0,6 L0,17", null, Brushes.White, 1.6));
        s.Children.Add(Art.Circle(0, 0, 1.6, Brushes.White));
        warn = ring;
        return s;
    }

    // ------------------------------------------------------------------ demo

    public override void DemoTick()
    {
        if (_aiming || _flying) return;
        if (_leg.Finished)
        {
            if (++_demoWait > 25)
            {
                _demoWait = 0;
                NewLeg();
            }
            return;
        }
        if (_pullIn > 0 || _pullT >= 0) return;
        if (_thrown >= X01.DartsPerTurn) ClearDarts();
        if (++_demoWait < 4) return;
        _demoWait = 0;

        var target = DartsRules.DemoTarget(_leg.Remaining, _leg.DartsLeft);
        _demoPoint = _centre + DartsRules.AimPoint(target, _r) + DartsRules.Scatter(Rng, DemoSigma * _r);
        _demoHold = 0.5 + Rng.NextDouble() * 0.6; // inside the sweet spot, like someone who knows the game
        _demoAim = true;
        StartAim();
    }
}
