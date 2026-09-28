using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>
/// Kite: a desk fan on the taskbar blows across the screen. Click the kite lying beside it and it goes up on a string
/// held by your cursor (see <see cref="KiteFlight"/>): keep the line taut and the wind holds it up; pull away from it
/// to climb, and in a lull pull, or it sinks. Fly through the clouds drifting by for ten points, and the stars that
/// twinkle high up for twenty-five; every fifth catch in a row pays a bonus. Birds, the taskbar and the tops of
/// windows (dropping onto one) bring the kite down, and it goes up again from your hand. A flight is a minute: a race
/// against the computer or a co-worker over the LAN.
/// </summary>
public sealed class KiteGame : MiniGame
{
    const double KiteW = 38, KiteH = 52, CatchR = 20, BirdR = 20, TailLink = 9, FanX = 58, DownFor = 1.3;
    const int TailLinks = 10, MaxClouds = 4;
    static readonly Color[] Panels = { Color.FromRgb(232, 74, 95), Color.FromRgb(77, 163, 255), Color.FromRgb(255, 209, 102), Color.FromRgb(61, 220, 132) };

    sealed class Thing
    {
        public required Canvas El;
        public Vec2 At;
        public double R, Speed, Age, Life;
        public bool Star, Bird;
    }

    readonly Canvas _sky = new() { IsHitTestVisible = false };
    readonly Canvas _streaks = new() { IsHitTestVisible = false };
    readonly Path _string = new() { StrokeThickness = 1.3, IsHitTestVisible = false };
    readonly Path _tail = new() { StrokeThickness = 1.6, IsHitTestVisible = false };
    readonly Canvas _bows = new() { IsHitTestVisible = false };
    readonly Canvas _kite = new() { IsHitTestVisible = false };
    readonly RotateTransform _kiteTurn = new();
    readonly Canvas _fan = new() { IsHitTestVisible = false };
    readonly RotateTransform _blades = new();
    readonly Ellipse _hand = new() { Width = 12, Height = 12, IsHitTestVisible = false };
    readonly List<Thing> _things = new();
    readonly List<(Rectangle El, double Y, double X, double Len)> _gusts = new();
    readonly Vec2[] _tailPts = new Vec2[TailLinks], _tailOld = new Vec2[TailLinks];
    readonly Random _weather = new();
    KiteWind _wind;
    KiteFlight? _flight;
    KiteRound _round = new();
    bool _flying, _racing, _demo, _down, _rehold;
    double _t, _downT, _cloudT, _birdT, _starT, _spin, _wait, _lean;
    int _shownSecond = -1;
    Vec2 _rest, _kitePos, _fall, _demoAnchor, _prevAnchor;
    Anims.Tween? _glide; // the kite gliding home after a flight

    public KiteGame(IGameHost host) : base(host)
    {
        _wind = new KiteWind(_weather);
        foreach (var c in new Control[] { _streaks, _sky, _fan, _string, _tail, _bows, _kite, _hand }) Layer.Children.Add(c);
        _kite.RenderTransform = _kiteTurn;
        _string.Stroke = Art.Brush(200, 240, 240, 240);
        DrawKite();
        ThemeChanged();
    }

    public override string Id => "kite";
    public override string Title => "Kite";

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        s.Rotor.Children.Add(Art.PathOf("M0,-11 L0,-3 L-8,-3 Z", Art.Brush(Panels[0])));
        s.Rotor.Children.Add(Art.PathOf("M0,-11 L8,-3 L0,-3 Z", Art.Brush(Panels[1])));
        s.Rotor.Children.Add(Art.PathOf("M-8,-3 L0,-3 L0,11 Z", Art.Brush(Panels[2])));
        s.Rotor.Children.Add(Art.PathOf("M8,-3 L0,11 L0,-3 Z", Art.Brush(Panels[3])));
        s.Rotor.Children.Add(Art.PathOf("M0,11 Q-3,14 1,16 T-1,21", null, Art.Brush("#20232C"), 1.2));
        return s;
    }

    public override HudInfo Hud
    {
        get
        {
            long best = Host.Stats.Get("kite.best");
            string line = _flying ? L.F("{0}s · clouds {1} · pull the string to climb", (int)Math.Ceiling(_round.Left), _round.Clouds)
                : _round.Over ? L.F("Flight over · {0} clouds · click the kite to fly again", _round.Clouds)
                : L.T("Click the kite to fly it — the string follows your cursor");
            return new HudInfo(_round.Score.ToString(CultureInfo.InvariantCulture), line, best > 0 ? L.F("Best {0}", best) : L.T("Best —"));
        }
    }

    // ------------------------------------------------------------------ races

    public override bool SupportsLan => true;
    public override (int Score, bool Active)? Race => (_round.Score, _racing);
    public override int RaceBaseline => 150;
    public override int RaceBest => (int)Host.Stats.Get("kite.best");
    public override double RaceSeconds => KiteRound.Seconds;

    public override void StartRace()
    {
        if (_racing) return;
        if (_flying || _round.Time > 0) NewRound();
        Launch();
    }

    void BeginRound()
    {
        if (_racing) return;
        _racing = true;
        Host.RoundStarted();
    }

    void NewRound()
    {
        _round = new KiteRound();
        _flying = _down = false;
        _flight = null;
        Host.HudChanged();
    }

    // ------------------------------------------------------------------ layout

    double MaxLine => Math.Min(Host.Arena.Height * 0.72, 640);

    public override void Layout()
    {
        var a = Host.Arena;
        _rest = new Vec2(a.Left + FanX + 120, a.Bottom);
        if (_flight != null) _flight.MaxLine = MaxLine;
        DrawFan();
        DrawGusts();
        if (!_flying) Rest();
        Host.HudChanged();
    }

    public override void Activate()
    {
        base.Activate();
        _rehold = true; // the cursor may be anywhere now: take the string up from there without a yank
    }

    public override void Summon(Vec2 p) { }

    public override void Deactivate() => Anims.Finish();

    /// <summary>The kite lies on the taskbar beside the fan, its tail along the floor.</summary>
    void Rest()
    {
        _kitePos = _rest + new Vec2(0, -KiteH * 0.55);
        _kiteTurn.Angle = -24;
        for (int i = 0; i < TailLinks; i++) _tailPts[i] = _tailOld[i] = new Vec2(_rest.X - 6 - i * TailLink * 0.95, _rest.Y - 2);
        _string.IsVisible = _hand.IsVisible = false;
        DrawAll();
    }

    // ------------------------------------------------------------------ input

    public override void CollectHitShapes(List<HitShape> into)
    {
        if (!_flying) into.Add(HitShape.Circle(_kitePos, 34));
    }

    public override bool PointerDown(Vec2 p, bool right)
    {
        if (_flying) return false;
        if (_round.Over || _round.Time > 0) NewRound();
        Launch();
        return false;
    }

    Vec2 Anchor()
    {
        var a = Host.Arena;
        var p = _demo ? _demoAnchor : Host.Pointer;
        return new Vec2(Clamp(p.X, a.Left + 10, a.Right - 10), Clamp(p.Y, a.Top + a.Height * 0.4, a.Bottom - 6));
    }

    void Launch()
    {
        var a = Host.Arena;
        _glide?.Cancel(); // clicked on its way home: its landing must not put the string away mid-flight
        _glide = null;
        if (_round.Time == 0 && !_flying)
        {
            Host.Stats.Add("kite.flights");
            BeginRound();
            _cloudT = 0;
            _birdT = 5 + _weather.NextDouble() * 3;
            _starT = 7 + _weather.NextDouble() * 4;
            ClearSky();
            for (int i = 0; i < 3; i++) SpawnCloud(a.Left + a.Width * (0.3 + 0.22 * i));
        }
        if (_demo) _demoAnchor = new Vec2(_kitePos.X, a.Bottom - 20);
        _prevAnchor = Anchor();
        _flight = new KiteFlight(_prevAnchor, MaxLine);
        _flying = true;
        _down = false;
        _rehold = false;
        _lean = -20;
        for (int i = 0; i < TailLinks; i++) _tailPts[i] = _tailOld[i] = _flight.Pos + new Vec2(0, KiteH * 0.55 + i * TailLink);
        _string.IsVisible = _hand.IsVisible = true;
        Host.Sound.Play("whoosh", 0.3, 0.8);
        Host.HudChanged();
        Host.Wake();
    }

    // ------------------------------------------------------------------ flight

    public override bool Update(double dt)
    {
        bool anim = Anims.Update(dt);
        if (!_flying) return anim || _demo;
        _t += dt;
        var a = Host.Arena;
        double wind = _wind.Step(dt);
        _blades.Angle += wind * dt * 9;
        MoveGusts(dt, wind);
        _round.Tick(dt);
        if (_down) Fall(dt);
        else Fly(dt, wind);
        MoveSky(dt, wind);
        StepTail(dt, wind);
        DrawAll();
        int second = (int)Math.Ceiling(_round.Left);
        if (second != _shownSecond)
        {
            _shownSecond = second;
            Host.HudChanged();
        }
        if (_round.Over) FlightOver();
        return true;
    }

    void Fly(double dt, double wind)
    {
        var f = _flight!;
        var a = Host.Arena;
        var anchor = Anchor();
        if (_rehold)
        {
            _rehold = false;
            _prevAnchor = anchor;
            f.Step(1e-4, wind, anchor);
        }
        if (_demo) DemoSteer(dt);
        var prev = f.Pos;
        f.Step(dt / 2, wind, (_prevAnchor + anchor) / 2);
        f.Step(dt / 2, wind, anchor);
        f.Confine(a.Left + 16, a.Right - 16, a.Top + 16);
        _prevAnchor = anchor;
        _kitePos = f.Pos;
        // on a taut line the nose leans upwind with the line's angle; a slack kite swings with its own motion
        double lean = f.Tension * -f.Elevation * 0.45 + (1 - f.Tension) * Clamp(f.Vel.X * 0.1, -40, 40);
        _lean += (lean - _lean) * Math.Min(1, dt * 6);
        _kiteTurn.Angle = Clamp(_lean + Math.Sin(_t * 5.3) * (3 + 3 * (1 - f.Tension)), -70, 70);

        if (f.Pos.Y > a.Bottom - 14) Crash(L.T("It hit the ground"));
        else if (f.Vel.Y > 0 && Host.Platforms.FindCrossing(prev + new Vec2(0, KiteH * 0.5), f.Pos + new Vec2(0, KiteH * 0.5), out _, out _))
            Crash(L.T("Snagged on a window"));
        else if (_things.FirstOrDefault(t => t.Bird && (t.At - f.Pos).Length < BirdR) is { } bird)
        {
            Crash(L.T("A bird flew into it"));
            Scatter(bird);
        }
        else
        {
            foreach (var t in _things.Where(t => !t.Bird && (t.At - f.Pos).Length < t.R * 0.9 + CatchR).ToList()) Caught(t);
        }
    }

    void Caught(Thing t)
    {
        var (points, streak) = _round.Catch(t.Star);
        Host.Stats.Add(t.Star ? "kite.stars" : "kite.clouds");
        var gold = Themes.Themed(Themes.ClassicGold);
        Host.Fx.Burst(t.At, t.Star ? new[] { gold, Colors.White } : new[] { Colors.White, Color.FromRgb(220, 235, 255) }, t.Star ? 18 : 14, 240, 60, 5, 0.7);
        Host.Fx.Popup(t.At - new Vec2(0, 26), "+" + points.ToString(CultureInfo.InvariantCulture), gold, t.Star ? 26 : 22, 1.0,
            streak ? L.F("{0} in a row!", _round.Streak) : null);
        Host.Sound.Play(t.Star ? "star" : "pop", t.Star ? 0.5 : 0.35, t.Star ? 1.2 : 0.8);
        Host.ShareAction(t.At, points);
        Remove(t, fade: false);
        Host.HudChanged();
    }

    void Crash(string why)
    {
        var f = _flight!;
        _round.Crash();
        Host.Stats.Add("kite.crashes");
        _down = true;
        _downT = 0;
        _fall = f.Vel * 0.4;
        _spin = (f.Vel.X >= 0 ? 1 : -1) * 420;
        Host.Fx.Popup(f.Pos - new Vec2(0, 34), why, Color.FromRgb(255, 140, 150), 20, 1.4);
        Host.Sound.Play("thunk", 0.45, 0.9);
        Host.HudChanged();
    }

    /// <summary>A downed kite tumbles to the floor; then it goes up again from the hand.</summary>
    void Fall(double dt)
    {
        var a = Host.Arena;
        _downT += dt;
        _fall += new Vec2(0, 420 * dt);
        _kitePos += _fall * dt;
        if (_kitePos.Y > a.Bottom - 10)
        {
            _kitePos = new Vec2(_kitePos.X, a.Bottom - 10);
            _fall = default;
            _spin = 0;
        }
        _kiteTurn.Angle += _spin * dt;
        if (_downT >= DownFor && !_round.Over) Launch();
    }

    void FlightOver()
    {
        _flying = _down = false;
        long before = Host.Stats.Get("kite.best");
        Host.Stats.Max("kite.best", _round.Score);
        if (_round.Crashes == 0 && _round.Clouds + _round.Stars > 0) Host.Stats.Add("kite.clean");
        if (_racing)
        {
            _racing = false;
            Host.RoundEnded(_round.Score);
        }
        bool best = _round.Score > before && _round.Score > 0;
        var at = new Vec2(Host.Arena.Center.X, Host.Arena.Top + Host.Arena.Height * 0.3);
        Host.Fx.Popup(at, best ? L.T("NEW BEST!") : L.T("FLIGHT OVER"), best ? Themes.Themed(Themes.ClassicGold) : Colors.White, 40, 2.4,
            L.F("{0} clouds · {1} stars · {2} points", _round.Clouds, _round.Stars, _round.Score));
        if (best) Host.Fx.Burst(at, Themes.Current.Confetti, 40, 520, 700, 7, 1.1);
        Host.Sound.Play(best ? "best" : "score", 0.6);
        // the kite glides home to the taskbar
        var from = _kitePos;
        double fromAngle = _kiteTurn.Angle;
        var home = _rest + new Vec2(0, -KiteH * 0.55);
        _string.IsVisible = _hand.IsVisible = false;
        _glide = Anims.Add(1.1, k =>
        {
            _kitePos = from + (home - from) * k + new Vec2(0, -60 * Math.Sin(Math.PI * k));
            _kiteTurn.Angle = fromAngle + (-24 - fromAngle) * k;
            DrawAll();
        }, Ease.InOutQuad, Rest);
        _wait = 3;
        Host.HudChanged();
    }

    // ------------------------------------------------------------------ the sky

    void MoveSky(double dt, double wind)
    {
        var a = Host.Arena;
        if (_things.Count(t => !t.Bird && !t.Star) < MaxClouds && (_cloudT -= dt) <= 0)
        {
            SpawnCloud(null);
            _cloudT = 1.8 + _weather.NextDouble() * 1.6;
        }
        if ((_birdT -= dt) <= 0)
        {
            SpawnBird();
            _birdT = 6 + _weather.NextDouble() * 5;
        }
        if ((_starT -= dt) <= 0)
        {
            SpawnStar();
            _starT = 9 + _weather.NextDouble() * 5;
        }
        foreach (var t in _things.ToList())
        {
            t.Age += dt;
            if (t.Bird)
            {
                t.At += new Vec2(-t.Speed * dt, Math.Sin(t.Age * 3) * 12 * dt);
                ((ScaleTransform)t.El.RenderTransform!).ScaleY = Math.Sin(t.Age * 12) >= 0 ? 1 : -0.6; // flap
                if (t.At.X < a.Left - 40) Remove(t, fade: false);
            }
            else if (t.Star)
            {
                double pulse = 1 + 0.15 * Math.Sin(t.Age * 7);
                var s = (ScaleTransform)t.El.RenderTransform!;
                s.ScaleX = s.ScaleY = pulse;
                if (t.Age > t.Life) Remove(t, fade: true);
            }
            else
            {
                t.At += new Vec2(wind * 0.35 * dt, 0);
                if (t.At.X > a.Right + t.R * 1.6) Remove(t, fade: false);
            }
            Canvas.SetLeft(t.El, t.At.X);
            Canvas.SetTop(t.El, t.At.Y);
        }
    }

    void SpawnCloud(double? x)
    {
        var a = Host.Arena;
        double r = 24 + _weather.NextDouble() * 14;
        var el = new Canvas { IsHitTestVisible = false };
        var puff = Art.Brush(225, 255, 255, 255);
        var shade = Art.Brush(120, 200, 215, 235);
        el.Children.Add(Art.Circle(-r * 0.2, r * 0.25, r * 0.95, shade));
        el.Children.Add(Art.Circle(-r * 0.75, r * 0.15, r * 0.6, puff));
        el.Children.Add(Art.Circle(r * 0.7, r * 0.2, r * 0.62, puff));
        el.Children.Add(Art.Circle(0, -r * 0.1, r * 0.8, puff));
        el.Children.Add(Art.Circle(r * 0.25, -r * 0.35, r * 0.55, puff));
        var t = new Thing { El = el, R = r, At = new Vec2(x ?? a.Left - r * 1.5, a.Top + a.Height * (0.08 + 0.5 * _weather.NextDouble())) };
        Add(t);
    }

    void SpawnStar()
    {
        var a = Host.Arena;
        var el = new Canvas { IsHitTestVisible = false, RenderTransform = new ScaleTransform(1, 1) };
        el.Children.Add(Art.Circle(0, 0, 18, Art.Brush(60, 255, 209, 102)));
        el.Children.Add(Art.PathOf(Art.StarPath(0, 0, 13, 5.5), Art.Brush(Themes.Themed(Themes.ClassicGold)), Art.Brush("#8A6A1C"), 1));
        var t = new Thing
        {
            El = el, R = 16, Star = true, Life = 7,
            At = new Vec2(a.Left + a.Width * (0.25 + 0.6 * _weather.NextDouble()), a.Top + a.Height * (0.06 + 0.2 * _weather.NextDouble())),
        };
        Add(t);
        Anims.Add(0.4, k => el.Opacity = k);
    }

    void SpawnBird()
    {
        var a = Host.Arena;
        var el = new Canvas { IsHitTestVisible = false, RenderTransform = new ScaleTransform(1, 1) };
        el.Children.Add(Art.PathOf("M-12,-2 Q-6,-9 0,0 Q6,-9 12,-2", null, Art.Brush("#2B2F3A"), 2.4));
        el.Children.Add(Art.Circle(0, 0, 2.6, Art.Brush("#2B2F3A")));
        var t = new Thing
        {
            El = el, Bird = true, R = BirdR, Speed = 110 + _weather.NextDouble() * 50,
            At = new Vec2(a.Right + 30, a.Top + a.Height * (0.1 + 0.45 * _weather.NextDouble())),
        };
        Add(t);
    }

    void Add(Thing t)
    {
        _things.Add(t);
        _sky.Children.Add(t.El);
        Canvas.SetLeft(t.El, t.At.X);
        Canvas.SetTop(t.El, t.At.Y);
    }

    void Remove(Thing t, bool fade)
    {
        if (!_things.Remove(t)) return;
        if (!fade)
        {
            _sky.Children.Remove(t.El);
            return;
        }
        Anims.Add(0.4, k => t.El.Opacity = 1 - k, Ease.Linear, () => _sky.Children.Remove(t.El));
    }

    /// <summary>A bird that hit the kite flaps off upward, startled.</summary>
    void Scatter(Thing bird)
    {
        _things.Remove(bird);
        var from = bird.At;
        Anims.Add(0.8, k =>
        {
            Canvas.SetLeft(bird.El, from.X - 120 * k);
            Canvas.SetTop(bird.El, from.Y - 140 * k);
            bird.El.Opacity = 1 - k;
        }, Ease.OutQuad, () => _sky.Children.Remove(bird.El));
    }

    void ClearSky()
    {
        foreach (var t in _things) _sky.Children.Remove(t.El);
        _things.Clear();
    }

    // ------------------------------------------------------------------ the tail

    /// <summary>A rope of bows hanging from the kite's foot, blown by the wind (Verlet links).</summary>
    void StepTail(double dt, double wind)
    {
        double turn = _kiteTurn.Angle * Math.PI / 180;
        _tailPts[0] = _kitePos + new Vec2(-Math.Sin(turn) * KiteH * 0.55, Math.Cos(turn) * KiteH * 0.55);
        var push = new Vec2(wind * 1.6, 160) * (dt * dt);
        for (int i = 1; i < TailLinks; i++)
        {
            var p = _tailPts[i];
            _tailPts[i] = p + (p - _tailOld[i]) * 0.94 + push;
            _tailOld[i] = p;
        }
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 1; i < TailLinks; i++)
            {
                var d = _tailPts[i] - _tailPts[i - 1];
                double len = d.Length;
                if (len > 1e-6) _tailPts[i] = _tailPts[i - 1] + d * (TailLink / len);
            }
        }
    }

    // ------------------------------------------------------------------ demo

    public override void DemoTick()
    {
        _demo = true;
        if (_flying) return;
        if ((_wait -= 0.15) <= 0)
        {
            if (_round.Over || _round.Time > 0) NewRound();
            Launch();
        }
    }

    /// <summary>The demo walks its hand under the nearest cloud (upwind of it, as the kite flies downwind of the hand).</summary>
    void DemoSteer(double dt)
    {
        var f = _flight!;
        var a = Host.Arena;
        // the clouds still ahead of the kite (a cloud just in from the upwind edge would pull the hand into the corner)
        var target = _things.Where(t => !t.Bird && t.At.X > f.Pos.X - 80 && t.At.X < a.Right - 60).OrderBy(t => (t.At - f.Pos).Length).FirstOrDefault();
        // where the hand should be for the kite, as it flies now, to reach the cloud
        double gx = target == null ? a.Left + a.Width * 0.3 : target.At.X - (f.Pos.X - f.Anchor.X);
        double gy = target == null ? a.Bottom - 40 : target.At.Y + (f.Anchor.Y - f.Pos.Y);
        var goal = new Vec2(Clamp(gx, a.Left + 20, a.Right - 20), Clamp(gy, a.Top + a.Height * 0.4, a.Bottom - 20));
        bool sagging = f.Tension < 0.3 && f.Elevation < 40;
        if (sagging) goal = _demoAnchor + new Vec2(-30, 20); // a low, sagging kite wants pulling
        goal = new Vec2(Clamp(goal.X, a.Left + 20, a.Right - 20), Clamp(goal.Y, a.Top + a.Height * 0.4, a.Bottom - 20));
        var d = goal - _demoAnchor;
        double step = (sagging ? 90 : 65) * dt; // slower than the wind, or the kite sinks (or trails behind the hand)
        _demoAnchor = d.Length <= step ? goal : _demoAnchor + d * (step / d.Length);
    }

    // ------------------------------------------------------------------ drawing

    void DrawAll()
    {
        Canvas.SetLeft(_kite, _kitePos.X);
        Canvas.SetTop(_kite, _kitePos.Y);
        if (_flying && _flight != null)
        {
            var hand = _flight.Anchor;
            var mid = (hand + _kitePos) / 2 + new Vec2(0, 4 + 46 * (1 - _flight.Tension));
            _string.Data = Geometry.Parse($"M{Art.F(hand.X)},{Art.F(hand.Y)} Q{Art.F(mid.X)},{Art.F(mid.Y)} {Art.F(_kitePos.X)},{Art.F(_kitePos.Y)}");
            Canvas.SetLeft(_hand, hand.X - 6);
            Canvas.SetTop(_hand, hand.Y - 6);
        }
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < TailLinks; i++) sb.Append(i == 0 ? 'M' : 'L').Append(Art.F(_tailPts[i].X)).Append(',').Append(Art.F(_tailPts[i].Y)).Append(' ');
        _tail.Data = Geometry.Parse(sb.ToString());
        for (int i = 0; i < _bows.Children.Count; i++)
        {
            var p = _tailPts[Math.Min(TailLinks - 1, 2 + i * 3)];
            Canvas.SetLeft(_bows.Children[i], p.X);
            Canvas.SetTop(_bows.Children[i], p.Y);
        }
    }

    /// <summary>A diamond kite of four coloured panels on two spars, drawn round its bridle point.</summary>
    void DrawKite()
    {
        _kite.Children.Clear();
        double top = -KiteH * 0.45, mid = -KiteH * 0.12, bottom = KiteH * 0.55, w = KiteW / 2;
        string P(double x, double y) => Art.F(x) + "," + Art.F(y);
        var rim = Art.Brush("#20232C");
        _kite.Children.Add(Art.PathOf($"M{P(0, top)} L{P(-w, mid)} L{P(0, mid)} Z", Art.Brush(Art.Safe(Panels[0]))));
        _kite.Children.Add(Art.PathOf($"M{P(0, top)} L{P(w, mid)} L{P(0, mid)} Z", Art.Brush(Art.Safe(Panels[1]))));
        _kite.Children.Add(Art.PathOf($"M{P(-w, mid)} L{P(0, bottom)} L{P(0, mid)} Z", Art.Brush(Art.Safe(Panels[2]))));
        _kite.Children.Add(Art.PathOf($"M{P(w, mid)} L{P(0, bottom)} L{P(0, mid)} Z", Art.Brush(Art.Safe(Panels[3]))));
        _kite.Children.Add(Art.PathOf($"M{P(0, top)} L{P(w, mid)} L{P(0, bottom)} L{P(-w, mid)} Z", null, rim, 1.4));
        _kite.Children.Add(Art.PathOf($"M{P(0, top)} L{P(0, bottom)} M{P(-w, mid)} L{P(w, mid)}", null, Art.Brush(170, 60, 40, 30), 1.2));
    }

    void DrawFan()
    {
        var a = Host.Arena;
        _fan.Children.Clear();
        double cx = a.Left + FanX, cy = a.Bottom - 46;
        var metal = Art.Brush("#C9CED8");
        var dark = Art.Brush("#4A505C");
        _fan.Children.Add(Art.PathOf($"M{Art.F(cx - 4)},{Art.F(cy + 20)} L{Art.F(cx + 4)},{Art.F(cy + 20)} L{Art.F(cx + 3)},{Art.F(a.Bottom - 6)} L{Art.F(cx - 3)},{Art.F(a.Bottom - 6)} Z", dark));
        _fan.Children.Add(Art.At(new Rectangle { Width = 44, Height = 7, RadiusX = 3.5, RadiusY = 3.5, Fill = dark }, cx - 22, a.Bottom - 7));
        var blades = new Canvas { RenderTransform = _blades, IsHitTestVisible = false };
        for (int i = 0; i < 3; i++)
        {
            var blade = Art.At(new Ellipse { Width = 14, Height = 22, Fill = Art.Brush(210, 120, 190, 255) }, -7, -22);
            blade.RenderTransform = new RotateTransform(i * 120);
            blade.RenderTransformOrigin = new RelativePoint(0.5, 1, RelativeUnit.Relative);
            blades.Children.Add(blade);
        }
        blades.Children.Add(Art.Circle(0, 0, 4, dark));
        _fan.Children.Add(Art.At(blades, cx, cy));
        _fan.Children.Add(Art.Circle(cx, cy, 25, null, metal, 1.6));
        _fan.Children.Add(Art.PathOf($"M{Art.F(cx - 25)},{Art.F(cy)} L{Art.F(cx + 25)},{Art.F(cy)} M{Art.F(cx)},{Art.F(cy - 25)} L{Art.F(cx)},{Art.F(cy + 25)}", null, Art.Brush(120, 201, 206, 216), 0.8));
    }

    /// <summary>Faint streaks of moving air, brighter in a gust.</summary>
    void DrawGusts()
    {
        var a = Host.Arena;
        _streaks.Children.Clear();
        _gusts.Clear();
        for (int i = 0; i < 7; i++)
        {
            double len = 40 + _weather.NextDouble() * 60;
            var el = new Rectangle { Width = len, Height = 1.6, RadiusX = 0.8, RadiusY = 0.8, Fill = Art.Brush(255, 255, 255, 255), Opacity = 0, IsHitTestVisible = false };
            _streaks.Children.Add(el);
            _gusts.Add((el, a.Top + a.Height * (0.1 + 0.75 * _weather.NextDouble()), a.Left + a.Width * _weather.NextDouble(), len));
        }
    }

    void MoveGusts(double dt, double wind)
    {
        var a = Host.Arena;
        double glow = Math.Clamp((wind - 70) / 90, 0.05, 0.4);
        for (int i = 0; i < _gusts.Count; i++)
        {
            var (el, y, x, len) = _gusts[i];
            x += wind * 2.2 * dt;
            if (x > a.Right) x = a.Left - len;
            _gusts[i] = (el, y, x, len);
            el.Opacity = glow;
            Canvas.SetLeft(el, x);
            Canvas.SetTop(el, y);
        }
    }

    public override void ThemeChanged()
    {
        var t = Themes.Current;
        _hand.Fill = Art.Brush(t.Mine);
        _hand.Stroke = Brushes.White;
        _hand.StrokeThickness = 1.5;
        _tail.Stroke = Art.Brush(210, 240, 240, 240);
        _bows.Children.Clear();
        for (int i = 0; i < 3; i++)
        {
            var c = t.Confetti[i % t.Confetti.Length];
            _bows.Children.Add(Art.PathOf("M0,0 L-6,-4 L-6,4 Z M0,0 L6,-4 L6,4 Z", Art.Brush(c)));
        }
        DrawAll();
    }
}
