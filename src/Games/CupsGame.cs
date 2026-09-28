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
/// Ping-Pong Cups: six cups of water stand in a row on one of your window tops (on the taskbar when no window has room)
/// and ride along if you drag the window. Grab the ball in the throw zone on the left, flick it and let go: it bounces
/// off rims, cup sides, window tops and the taskbar, and drops into a cup for ten; a ball that bounced on the way is a
/// bounce shot, worth twenty and a second cup. Clear the rack and the balls you have left pay five each, and a new rack
/// goes up elsewhere. Ten balls make a round (see <see cref="CupsRound"/>): a race against the computer or a co-worker
/// over the LAN.
/// </summary>
public sealed class CupsGame : MiniGame
{
    const double BallR = 7, ZoneShare = 0.3, ThrowTimeout = 5, MaxFlick = 2600;

    readonly BallBody _ball = new(BallR) { Gravity = 1500, Restitution = 0.8, WallRestitution = 0.8, AirDrag = 0.22, RollFriction = 2.2 };
    readonly Canvas _ballEl = new() { IsHitTestVisible = false };
    readonly Canvas _cupsLayer = new() { IsHitTestVisible = false };
    readonly Canvas _zone = new() { IsHitTestVisible = false };
    readonly Dictionary<Cup, Canvas> _cupEls = new();
    readonly Queue<(Vec2 P, double T)> _trail = new();
    CupsRound _round = new();
    Vec2 _start, _prev;
    double _t, _flightT;
    bool _held, _flying, _bounced, _racing, _demo;
    int _demoWait;

    public CupsGame(IGameHost host) : base(host)
    {
        Layer.Children.Add(_zone);
        Layer.Children.Add(_cupsLayer);
        Layer.Children.Add(_ballEl);
        DrawBall();
    }

    public override string Id => "cups";
    public override string Title => "Ping-Pong Cups";

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        s.Rotor.Children.Add(Art.PathOf("M-9,-6 L9,-6 L6,9 L-6,9 Z", Art.Brush("#D8333F"), Art.Brush("#8E1B26"), 1));
        s.Rotor.Children.Add(Art.At(new Rectangle { Width = 18, Height = 2.5, Fill = Brushes.White }, -9, -7));
        s.Rotor.Children.Add(Art.Circle(5, -11, 3, Brushes.White, Art.Brush("#B8BCC6"), 0.8));
        return s;
    }

    public override HudInfo Hud
    {
        get
        {
            long best = Host.Stats.Get("cups.best");
            string line = _round.Over && !_flying ? L.F("Round over · {0} cups · click the ball for another round", _round.Sunk)
                : L.F("Balls {0} · cups {1} · flick the ball into a cup", _round.BallsLeft, _round.Standing);
            return new HudInfo(_round.Score.ToString(System.Globalization.CultureInfo.InvariantCulture), line, best > 0 ? L.F("Best {0}", best) : L.T("Best —"));
        }
    }

    // ------------------------------------------------------------------ races

    public override bool SupportsLan => true;
    public override (int Score, bool Active)? Race => (_round.Score, _racing);
    public override int RaceBaseline => 70;
    public override int RaceBest => (int)Host.Stats.Get("cups.best");
    public override double RaceSeconds => 60;

    public override void StartRace()
    {
        if (_racing) return;
        if (_round.Throws > 0) NewRound();
        BeginRound();
    }

    void BeginRound()
    {
        if (_racing) return;
        _racing = true;
        Host.RoundStarted();
    }

    void NewRound()
    {
        _round = new CupsRound();
        NewRack();
        ResetBall();
        Host.HudChanged();
    }

    // ------------------------------------------------------------------ layout

    double ZoneRight => Host.Arena.Left + Host.Arena.Width * ZoneShare;

    public override void Layout()
    {
        var a = Host.Arena;
        _start = new Vec2(a.Left + 110, a.Bottom - BallR);
        DrawZone();
        if (_round.Cups.Count == 0) NewRack();
        if (!_flying && !_held) ResetBall();
        DrawCups();
        Host.HudChanged();
    }

    public override void Summon(Vec2 p) { }

    public override void Deactivate() => _held = false;

    /// <summary>A new rack: on a window top wide enough, away from the throw zone and not too high, else on the taskbar's far end.</summary>
    void NewRack()
    {
        var a = Host.Arena;
        double need = CupsRound.Spacing * CupsRound.RackSize + 20;
        var hud = Host.HudBounds.Inflate(20);
        var tops = Host.Platforms.Items.Where(p => p.X2 - p.X1 > need && p.X1 > ZoneRight && p.Y > a.Top + a.Height * 0.35 && p.Y < a.Bottom - 60).ToList();
        var old = _round.Cups.FirstOrDefault()?.X;
        var pick = tops.OrderBy(_ => Rng.Next()).FirstOrDefault(p => old == null || Math.Abs((p.X1 + p.X2) / 2 - old.Value) > 50);
        foreach (var el in _cupEls.Values) _cupsLayer.Children.Remove(el);
        _cupEls.Clear();
        if (pick.X2 > pick.X1)
        {
            double x = pick.X1 + need / 2 + Rng.NextDouble() * (pick.X2 - pick.X1 - need);
            _round.Rack(x, pick.Y, pick.Hwnd);
        }
        else
        {
            double x = a.Right - need / 2 - 40 - Rng.NextDouble() * a.Width * 0.25;
            if (hud.Left < x + need / 2 && hud.Bottom > a.Bottom - 80) x = hud.Left - need / 2 - 20;
            _round.Rack(x, a.Bottom);
        }
        DrawCups();
    }

    void ResetBall()
    {
        _flying = _held = _bounced = false;
        _ball.Place(_start);
        _ball.Spin = 0;
        PlaceBall();
        _ballEl.IsVisible = !_round.Over;
    }

    // ------------------------------------------------------------------ input

    public override void CollectHitShapes(List<HitShape> into)
    {
        if (!_flying) into.Add(HitShape.Circle(_ball.Pos, BallR + 22));
    }

    public override bool PointerDown(Vec2 p, bool right)
    {
        if (_flying) return false;
        if (_round.Over)
        {
            NewRound();
            return false;
        }
        _held = true;
        _trail.Clear();
        return true;
    }

    public override void PointerUp(Vec2 p)
    {
        if (!_held) return;
        _held = false;
        // the flick: how fast the pointer moved over the last tenth of a second
        var old = _trail.Count > 0 ? _trail.Peek() : (P: p, T: _t);
        double dt = Math.Max(0.016, _t - old.T);
        var v = (p - old.P) / dt;
        if (v.Length < 150)
        {
            ResetBall(); // put down, not thrown
            return;
        }
        if (v.Length > MaxFlick) v = v * (MaxFlick / v.Length);
        Launch(v);
    }

    public override void PointerCancel()
    {
        if (_held) ResetBall();
    }

    void Launch(Vec2 v)
    {
        if (!_round.Throw()) return;
        if (_round.Throws == 1 && !_racing) BeginRound();
        _ball.Place(_ball.Pos, v);
        _flying = true;
        _bounced = false;
        _flightT = 0;
        _prev = _ball.Pos;
        Host.Stats.Add("cups.throws");
        Host.Sound.Play("whoosh", 0.25, 1.6);
        Host.HudChanged();
        Host.Wake();
    }

    // ------------------------------------------------------------------ the ball

    public override bool Update(double dt)
    {
        _t += dt;
        RideCups();
        if (_held)
        {
            var a = Host.Arena;
            var p = Host.Pointer;
            _ball.Pos = new Vec2(Clamp(p.X, a.Left + BallR, ZoneRight), Clamp(p.Y, a.Top + BallR, a.Bottom - BallR));
            _trail.Enqueue((_ball.Pos, _t));
            while (_trail.Count > 0 && _t - _trail.Peek().T > 0.1) _trail.Dequeue();
            PlaceBall();
            return true;
        }
        if (_flying) Fly(dt);
        else if (_demo && !_round.Over && _demoWait-- <= 0) DemoThrow();
        return _flying || Anims.Update(dt) || _demo;
    }

    void Fly(double dt)
    {
        _flightT += dt;
        var imp = new Impacts();
        _prev = _ball.Pos;
        _ball.Step(dt, Host, ref imp);
        if (imp.TouchedGround && imp.Floor > 60)
        {
            _bounced = true;
            Host.Sound.Play("bounce", Math.Clamp(imp.Floor / 1200, 0.1, 0.5), 2.2);
        }
        foreach (var cup in _round.Cups.Where(c => c.Standing).ToList())
        {
            if (cup.Catches(_prev, _ball.Pos, BallR))
            {
                Sunk(cup);
                return;
            }
            var (l, r) = cup.Lips;
            double hit = _ball.CollidePoint(l, Cup.Lip, 0.6) + _ball.CollidePoint(r, Cup.Lip, 0.6);
            hit += _ball.CollideSegment(l, new Vec2(cup.X - Cup.BottomHalf, cup.RimY + Cup.Height), 1.5, 0.55);
            hit += _ball.CollideSegment(r, new Vec2(cup.X + Cup.BottomHalf, cup.RimY + Cup.Height), 1.5, 0.55);
            if (hit > 40) Host.Sound.Play("click", Math.Clamp(hit / 900, 0.1, 0.45), 1.8);
        }
        PlaceBall();
        if (_ball.Asleep || _flightT > ThrowTimeout) Missed();
    }

    void Sunk(Cup cup)
    {
        var (points, off, cleared) = _round.Sink(cup, _bounced);
        Host.Stats.Add("cups.sunk", off.Count);
        if (_bounced) Host.Stats.Add("cups.bounce");
        var at = new Vec2(cup.X, cup.RimY);
        Host.Fx.Burst(at, new[] { Color.FromRgb(120, 190, 255), Colors.White }, 16, 260, 700, 4, 0.6);
        Host.Fx.Popup(at - new Vec2(0, 30), _bounced ? L.F("BOUNCE SHOT! +{0}", points) : $"+{points}", Themes.Themed(Themes.ClassicGold), _bounced ? 26 : 22, 1.1);
        Host.Sound.Play("splash", 0.45, 1.5);
        Host.ShareAction(at, points);
        foreach (var c in off) KnockOff(c);
        if (cleared)
        {
            if (_round.RackThrows <= CupsRound.RackSize) Host.Stats.Add("cups.clean");
            Host.Fx.Popup(new Vec2(Host.Arena.Center.X, Host.Arena.Top + Host.Arena.Height * 0.3), L.T("RACK CLEARED!"), Themes.Themed(Themes.ClassicGold), 40, 1.8,
                _round.BallsLeft > 0 ? L.F("+{0} for the balls left · a new rack", _round.BallsLeft * CupsRound.SpareBall) : null);
            Host.Sound.Play("best", 0.6);
            Anims.After(1.0, NewRack);
        }
        EndThrow();
    }

    void Missed()
    {
        Host.Sound.Play("board", 0.2, 1.6);
        EndThrow();
    }

    void EndThrow()
    {
        _flying = false;
        if (_round.Over) RoundOver();
        Anims.After(0.4, ResetBall);
        Host.HudChanged();
    }

    void RoundOver()
    {
        long before = Host.Stats.Get("cups.best");
        Host.Stats.Max("cups.best", _round.Score);
        if (_racing)
        {
            _racing = false;
            Host.RoundEnded(_round.Score);
        }
        bool best = _round.Score > before && _round.Score > 0;
        var at = new Vec2(Host.Arena.Center.X, Host.Arena.Top + Host.Arena.Height * 0.32);
        Host.Fx.Popup(at, best ? L.T("NEW BEST!") : L.T("ROUND OVER"), best ? Themes.Themed(Themes.ClassicGold) : Colors.White, 40, 2.4, L.F("{0} cups · {1} points", _round.Sunk, _round.Score));
        if (best) Host.Fx.Burst(at, Themes.Current.Confetti, 40, 520, 700, 7, 1.1);
        Host.Sound.Play(best ? "best" : "score", 0.6);
        _demoWait = 40;
    }

    /// <summary>Cups on a window ride along when it is dragged.</summary>
    void RideCups()
    {
        foreach (var cup in _round.Cups)
        {
            if (cup.On == IntPtr.Zero) continue;
            var d = Host.Platforms.DeltaOf(cup.On);
            if (d.X == 0 && d.Y == 0) continue;
            cup.X += d.X;
            cup.RimY += d.Y;
            if (_cupEls.TryGetValue(cup, out var el)) PlaceCup(cup, el);
        }
    }

    void KnockOff(Cup cup)
    {
        if (!_cupEls.Remove(cup, out var el)) return;
        var move = new TranslateTransform();
        el.RenderTransform = move;
        Anims.Add(0.45, k =>
        {
            move.Y = 30 * k;
            el.Opacity = 1 - k;
        }, Ease.InQuad, () => _cupsLayer.Children.Remove(el));
    }

    // ------------------------------------------------------------------ demo

    public override void DemoTick()
    {
        _demo = true;
        if (_round.Over && !_flying && _demoWait-- <= 0) NewRound();
    }

    /// <summary>The demo aims a lob at the nearest cup (a ballistic guess, a little off now and then).</summary>
    void DemoThrow()
    {
        _demoWait = 45;
        var cup = _round.Cups.Where(c => c.Standing).OrderBy(c => c.X).FirstOrDefault();
        if (cup == null) return;
        var from = _start + new Vec2(0, -40);
        _ball.Place(from);
        double T = 1.0 + Rng.NextDouble() * 0.2, g = _ball.Gravity;
        var target = new Vec2(cup.X + (Rng.NextDouble() - 0.5) * 16, cup.RimY - 2);
        double drag = 1 + _ball.AirDrag * T / 2; // a rough allowance for the air
        Launch(new Vec2((target.X - from.X) / T * drag, ((target.Y - from.Y) - 0.5 * g * T * T) / T * drag));
    }

    // ------------------------------------------------------------------ drawing

    void PlaceBall()
    {
        Canvas.SetLeft(_ballEl, _ball.Pos.X);
        Canvas.SetTop(_ballEl, _ball.Pos.Y);
    }

    void DrawBall()
    {
        _ballEl.Children.Clear();
        _ballEl.Children.Add(Art.Circle(1, 1.5, BallR, Art.Brush(60, 0, 0, 0)));
        _ballEl.Children.Add(Art.Circle(0, 0, BallR, Brushes.White, Art.Brush("#B8BCC6"), 1));
        _ballEl.Children.Add(Art.Circle(-2, -2, 2, Art.Brush(150, 255, 255, 255)));
    }

    void DrawZone()
    {
        _zone.Children.Clear();
        var a = Host.Arena;
        double x = ZoneRight;
        _zone.Children.Add(Art.PathOf($"M{Art.F(x)},{Art.F(a.Bottom - 120)} L{Art.F(x)},{Art.F(a.Bottom)}", null, Art.Brush(90, 255, 255, 255), 1.5));
        ((Path)_zone.Children[0]).StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 4 };
        _zone.Children.Add(Art.At(new Rectangle { Width = 30, Height = 6, RadiusX = 3, RadiusY = 3, Fill = Art.Brush("#6B4A2B") }, _start.X - 15, a.Bottom - 3));
    }

    void DrawCups()
    {
        foreach (var cup in _round.Cups.Where(c => c.Standing))
        {
            if (!_cupEls.TryGetValue(cup, out var el))
            {
                el = CupArt();
                _cupEls[cup] = el;
                _cupsLayer.Children.Add(el);
            }
            PlaceCup(cup, el);
        }
    }

    static void PlaceCup(Cup cup, Canvas el)
    {
        Canvas.SetLeft(el, cup.X);
        Canvas.SetTop(el, cup.RimY);
    }

    /// <summary>A red party cup with a white rim and water inside, drawn from its rim's centre down.</summary>
    static Canvas CupArt()
    {
        var c = new Canvas { IsHitTestVisible = false };
        double t = Cup.TopHalf, b = Cup.BottomHalf, h = Cup.Height;
        c.Children.Add(Art.PathOf($"M{Art.F(-t)},0 L{Art.F(t)},0 L{Art.F(b)},{Art.F(h)} L{Art.F(-b)},{Art.F(h)} Z", Art.Brush(Art.Safe(Color.FromRgb(216, 51, 63))), Art.Brush("#8E1B26"), 1.2));
        c.Children.Add(Art.PathOf($"M{Art.F(-t + 3)},{Art.F(h * 0.45)} L{Art.F(t - 3)},{Art.F(h * 0.45)}", null, Art.Brush(80, 255, 255, 255), 1));
        c.Children.Add(Art.At(new Ellipse { Width = t * 2 - 4, Height = 6, Fill = Art.Brush("#7FC4FF") }, -t + 2, 1));
        c.Children.Add(Art.At(new Rectangle { Width = t * 2 + 2, Height = 3, RadiusX = 1.5, RadiusY = 1.5, Fill = Brushes.White }, -t - 1, -1.5));
        return c;
    }
}
