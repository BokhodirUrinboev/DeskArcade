using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using DeskArcade.Engine;
using DeskArcade.Net;

namespace DeskArcade.Games;

/// <summary>
/// Asteroids: a little ship waits on the taskbar; click it and it lifts off and follows your cursor across the desktop,
/// with a little lag, turning to face where it flies. Rocks drift in from the edges and bounce around the screen; click
/// a rock and the ship fires at it: a big rock splits in two, a medium one in two small ones, and a small one is dust.
/// A rock touching the ship costs a life (three), and the ship blinks while it gets clear. Clear the screen for the
/// next, bigger wave. Only the rocks and the parked ship take the mouse; everything else still clicks through to your
/// windows. Alone, a game is a race against the computer; over the LAN it is co-op: two ships in one field of rocks,
/// sharing five lives and the score (see the co-op part).
/// </summary>
public sealed partial class AsteroidsGame : MiniGame
{
    const double FollowSpring = 7, MaxShipSpeed = 460, PadW = 60, PadH = 40;

    sealed class RockView
    {
        public required Canvas El;
        public required RotateTransform Turn;
        public required TranslateTransform Move;
        public Vec2 Pos; // last drawn, for the guest's dust when one disappears
        public int Size;
    }

    sealed class ShipView
    {
        public required Canvas El;
        public required RotateTransform Turn;
        public required Path Flame;
        public double Angle = -Math.PI / 2;
        public Vec2 Last;
    }

    readonly Canvas _rocksLayer = new() { IsHitTestVisible = false };
    readonly Canvas _shotsLayer = new() { IsHitTestVisible = false };
    readonly Dictionary<int, RockView> _rocks = new();
    readonly List<Ellipse> _shotPool = new();
    readonly ShipView _me, _mate;
    readonly Canvas _pad = new() { IsHitTestVisible = false };
    AsteroidsRules? _rules;
    Vec2 _myPos, _myVel;
    bool _playing, _racing, _demo, _over;
    double _padX = double.NaN, _demoFire;
    int _lastWave, _demoIdle;
    Rect _padRect;

    public AsteroidsGame(IGameHost host) : base(host)
    {
        Layer.Children.Add(_rocksLayer);
        Layer.Children.Add(_shotsLayer);
        _mate = NewShip();
        _me = NewShip();
        Layer.Children.Add(_pad);
        CoopSetup();
        ThemeChanged();
    }

    public override string Id => "asteroids";
    public override string Title => "Asteroids";

    bool LanOn => Host.Lan.Connected;
    bool IsGuest => LanOn && Host.Lan.Role == LanRole.Guest;

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        s.Rotor.Children.Add(Art.PathOf(RockPath(7, 8), Art.Brush("#8C8F99"), Art.Brush("#4A4D57"), 1));
        s.Rotor.Children.Add(Art.PathOf("M-2,10 L2,2 L6,10 L2,8 Z", Art.Brush(Art.Safe(Themes.Current.Mine))));
        return s;
    }

    public override HudInfo Hud
    {
        get
        {
            long best = Host.Stats.Get("asteroids.best");
            var (score, wave, lives, playing) = Shown;
            string line = playing
                ? LanOn ? L.F("Wave {0} · lives {1} · with {2}", wave, lives, Host.Lan.PeerName) : L.F("Wave {0} · lives {1} · click a rock to fire", wave, lives)
                : _over ? L.F("Game over · wave {0} · click your ship to fly again", wave)
                : IsGuest ? L.F("Click your ship to ask {0} for a game", Host.Lan.PeerName)
                : L.T("Click your ship to launch · it follows the cursor, click rocks to fire");
            return new HudInfo(score.ToString(System.Globalization.CultureInfo.InvariantCulture), line, best > 0 ? L.F("Best {0}", best) : L.T("Best —"));
        }
    }

    /// <summary>What the scoreboard shows: this game's numbers, or the host's in a co-op game on the guest's screen.</summary>
    (int Score, int Wave, int Lives, bool Playing) Shown => IsGuest ? (_state.Score, _state.Wave, _state.Lives, _state.Playing)
        : (_rules?.Score ?? 0, _rules?.Wave ?? 0, _rules?.Lives ?? 0, _playing);

    public override bool SupportsLan => true;

    /// <summary>A race alone; over the LAN the co-op game has its own messages, so race mode stays out of it.</summary>
    public override (int Score, bool Active)? Race => LanOn ? null : (_rules?.Score ?? 0, _racing);
    public override int RaceBaseline => 1200;
    public override int RaceBest => (int)Host.Stats.Get("asteroids.best");
    public override double RaceSeconds => 120;
    public override Opponent? Opponent => LanOn ? new Opponent(Host.Lan.PeerName, false, 0, null, Teammate: true) : null;

    public override void StartRace()
    {
        if (!_racing) StartGame();
    }

    // ------------------------------------------------------------------ layout

    public override void Layout()
    {
        var a = Host.Arena;
        if (double.IsNaN(_padX) || _padX < a.Left || _padX > a.Right) _padX = a.Center.X + a.Width * 0.18;
        _padX = Clamp(_padX, a.Left + 60, a.Right - 60);
        _padRect = new Rect(_padX - PadW / 2, a.Bottom - PadH, PadW, PadH);
        Canvas.SetLeft(_pad, _padX);
        Canvas.SetTop(_pad, a.Bottom);
        if (_rules != null) _rules.Box = a;
        CoopCheckSession();
        ShowPad();
        Host.HudChanged();
    }

    void ShowPad() => _pad.IsVisible = !(_playing || (IsGuest && _state.Playing));

    public override void Summon(Vec2 p)
    {
        if (_playing) return;
        _padX = p.X;
        Layout();
    }

    public override void Deactivate() => Anims.Finish();

    // ------------------------------------------------------------------ input

    public override void CollectHitShapes(List<HitShape> into)
    {
        if (_pad.IsVisible) into.Add(HitShape.Box(_padRect.Inflate(6)));
        foreach (var r in _rocks.Values) into.Add(HitShape.Circle(r.Pos, AsteroidsRules.RadiusOf(r.Size) + 6));
    }

    public override bool PointerDown(Vec2 p, bool right)
    {
        if (_pad.IsVisible && _padRect.Inflate(6).Contains(p.ToPoint()))
        {
            if (IsGuest) CoopAskGame();
            else StartGame();
            return false;
        }
        if (!(_playing || (IsGuest && _state.Playing))) return false;
        if (IsGuest) CoopFire(p);
        else if (_rules!.Fire(0, p)) Host.Sound.Play("pin-flipper", 0.3, 1.6);
        return false;
    }

    void StartGame()
    {
        var a = Host.Arena;
        _rules = new AsteroidsRules(a, LanOn ? 2 : 1, Rng);
        _myPos = new Vec2(_padX, a.Bottom - PadH);
        _myVel = default;
        _rules.Ships[0].Pos = _myPos;
        if (LanOn) _rules.Ships[1].Pos = _matePos;
        _playing = true;
        _over = false;
        _lastWave = 0;
        foreach (var r in _rocks.Values) _rocksLayer.Children.Remove(r.El);
        _rocks.Clear();
        if (LanOn) Host.Stats.Add("asteroids.coop");
        else if (!_racing)
        {
            _racing = true;
            Host.RoundStarted();
        }
        ShowPad();
        Host.Sound.Play("whoosh", 0.4, 0.8);
        Host.HudChanged();
        Host.Wake();
    }

    void GameOver()
    {
        var r = _rules!;
        _playing = false;
        _over = true;
        _endT = LanOn ? 2 : 0; // the final field goes out a little longer, so the guest sees the end
        long before = Host.Stats.Get("asteroids.best");
        Host.Stats.Max("asteroids.best", r.Score);
        Host.Stats.Max("asteroids.wave", r.Wave);
        if (_racing)
        {
            _racing = false;
            Host.RoundEnded(r.Score);
        }
        ShowResult(r.Score, r.Wave, r.Score > before && r.Score > 0);
        ShowPad();
        Host.HudChanged();
    }

    void ShowResult(int score, int wave, bool best)
    {
        var at = new Vec2(Host.Arena.Center.X, Host.Arena.Top + Host.Arena.Height * 0.32);
        Host.Fx.Popup(at, best ? L.T("NEW BEST!") : L.T("GAME OVER"), best ? Themes.Themed(Themes.ClassicGold) : Colors.White, 44, 2.6,
            L.F("wave {0} · {1} points", wave, score));
        if (best) Host.Fx.Burst(at, Themes.Current.Confetti, 44, 540, 700, 7, 1.1);
        Host.Sound.Play(best ? "best" : "buzzer", best ? 0.8 : 0.4);
    }

    // ------------------------------------------------------------------ the flight

    public override bool Update(double dt)
    {
        bool anim = Anims.Update(dt);
        bool flying = _playing || (IsGuest && _state.Playing);
        if (flying) Fly(dt);
        if (!IsGuest && _playing && _rules is { } r)
        {
            r.Box = Host.Arena;
            r.Ships[0].Pos = _myPos;
            if (LanOn) r.Ships[1].Pos = _matePos;
            if (_demo) DemoFire(dt);
            r.Step(dt);
            foreach (var (at, size, owner) in r.Broken) Dust(at, size, owner);
            foreach (int ship in r.ShipsHit) ShipHit(ship == 0);
            if (r.Broken.Count > 0) Host.HudChanged();
            if (r.Wave != _lastWave)
            {
                _lastWave = r.Wave;
                Host.Stats.Max("asteroids.wave", r.Wave);
                if (r.Wave > 1)
                {
                    Host.Fx.Popup(new Vec2(Host.Arena.Center.X, Host.Arena.Top + Host.Arena.Height * 0.3), L.F("WAVE {0}", r.Wave), Themes.Themed(Themes.ClassicGold), 44, 1.6);
                    Host.Sound.Play("fire", 0.5);
                }
                Host.HudChanged();
            }
            DrawRocks(r.Rocks.Select(x => (x.Id, x.Pos, x.Size, x.Angle)));
            DrawShots(r.Shots.Select(s => s.Pos));
            DrawShip(_me, _myPos, r.Ships[0].Invulnerable, true);
            DrawShip(_mate, _matePos, LanOn ? r.Ships[1].Invulnerable : 0, LanOn);
            if (r.Over) GameOver();
        }
        bool coop = CoopUpdate(dt);
        return anim || flying || coop;
    }

    /// <summary>The local ship chases the cursor on a spring, up to a top speed, and turns to face where it flies.</summary>
    void Fly(double dt)
    {
        var target = _demo ? DemoTarget() : Host.Pointer;
        _myVel = (target - _myPos) * FollowSpring;
        if (_myVel.Length > MaxShipSpeed) _myVel = _myVel.Normalized() * MaxShipSpeed;
        _myPos += _myVel * dt;
        var a = Host.Arena;
        _myPos = new Vec2(Clamp(_myPos.X, a.Left + 16, a.Right - 16), Clamp(_myPos.Y, a.Top + 16, a.Bottom - 16));
    }

    /// <param name="owner">The ship whose shot broke it: 0 ours, 1 the co-worker's; -1 when a ship flew into it.</param>
    void Dust(Vec2 at, int size, int owner)
    {
        var grey = new[] { Color.FromRgb(160, 164, 176), Color.FromRgb(110, 114, 126), owner == 1 ? Themes.Current.Rival : Themes.Current.Mine };
        Host.Fx.Burst(at, grey, 6 + size * 5, 120 + size * 60, 0, 3 + size, 0.5 + size * 0.1);
        Host.Sound.Play(size >= 3 ? "thunk" : "pop", 0.3 + size * 0.08, size >= 3 ? 0.7 : 1.3 - size * 0.15);
        if (owner == 0 && !IsGuest) Host.Stats.Add("asteroids.rocks"); // no marker for the co-worker: in co-op they see the rock go
    }

    void ShipHit(bool mine)
    {
        var at = mine ? _myPos : _matePos;
        Host.Fx.Burst(at, new[] { Colors.White, Color.FromRgb(255, 170, 60), Color.FromRgb(255, 90, 60) }, 22, 320, 0, 4, 0.6);
        Host.Sound.Play("buzzer", 0.35);
        Host.HudChanged();
    }

    // ------------------------------------------------------------------ demo

    public override void DemoTick()
    {
        _demo = true;
        if (_playing || (IsGuest && _state.Playing)) return;
        if (++_demoIdle > 10)
        {
            _demoIdle = 0;
            if (IsGuest) CoopAskGame();
            else StartGame();
        }
    }

    /// <summary>The demo pilot keeps away from the nearest rock, in the lower middle of the screen.</summary>
    Vec2 DemoTarget()
    {
        var a = Host.Arena;
        var home = new Vec2(a.Center.X + Math.Sin(Environment.TickCount64 / 1500.0) * a.Width * 0.25, a.Bottom - a.Height * 0.25);
        var near = _rocks.Values.OrderBy(r => (r.Pos - _myPos).Length).FirstOrDefault();
        if (near == null || (near.Pos - _myPos).Length > 220) return home;
        return _myPos + (_myPos - near.Pos).Normalized() * 160;
    }

    void DemoFire(double dt)
    {
        if ((_demoFire -= dt) > 0 || _rules!.Rocks.Count == 0) return;
        _demoFire = 0.35;
        var rock = _rules.Rocks.OrderBy(r => (r.Pos - _myPos).Length).First();
        PointerDown(rock.Pos, false);
    }

    // ------------------------------------------------------------------ drawing

    ShipView NewShip()
    {
        var turn = new RotateTransform();
        var el = new Canvas { IsHitTestVisible = false, RenderTransform = turn, IsVisible = false };
        var flame = Art.PathOf("M-12,-5 L-22,0 L-12,5 Z", Art.Brush("#FFB020"));
        el.Children.Add(flame);
        Layer.Children.Add(el);
        return new ShipView { El = el, Turn = turn, Flame = flame };
    }

    static void PaintShip(ShipView ship, Color c)
    {
        var flame = ship.Flame;
        ship.El.Children.Clear();
        ship.El.Children.Add(flame);
        ship.El.Children.Add(Art.PathOf("M16,0 L-12,-11 L-7,0 L-12,11 Z", Art.Brush(c), Art.Brush(Art.Blend(c, Colors.Black, 0.45)), 1.5));
        ship.El.Children.Add(Art.PathOf("M8,0 L-1,-4 L-1,4 Z", Art.Brush(Color.FromArgb(220, 200, 230, 255))));
    }

    void DrawShip(ShipView ship, Vec2 pos, double invulnerable, bool shown)
    {
        ship.El.IsVisible = shown;
        if (!shown) return;
        var moved = pos - ship.Last;
        if (moved.Length > 0.6) ship.Angle = Math.Atan2(moved.Y, moved.X);
        ship.Flame.IsVisible = moved.Length > 1.5;
        ship.Last = pos;
        Canvas.SetLeft(ship.El, pos.X);
        Canvas.SetTop(ship.El, pos.Y);
        ship.Turn.Angle = ship.Angle * 180 / Math.PI;
        ship.El.Opacity = invulnerable > 0 && (int)(invulnerable * 8) % 2 == 0 ? 0.35 : 1; // blinks while it gets clear
    }

    void DrawRocks(IEnumerable<(int Id, Vec2 Pos, int Size, double Angle)> rocks)
    {
        var seen = new HashSet<int>();
        foreach (var (id, pos, size, angle) in rocks)
        {
            seen.Add(id);
            if (!_rocks.TryGetValue(id, out var view))
            {
                var turn = new RotateTransform();
                var move = new TranslateTransform();
                var el = DrawRock(AsteroidsRules.RadiusOf(size), id);
                el.RenderTransform = new TransformGroup { Children = { turn, move } };
                el.RenderTransformOrigin = RelativePoint.TopLeft;
                view = new RockView { El = el, Turn = turn, Move = move, Size = size };
                _rocks[id] = view;
                _rocksLayer.Children.Add(el);
            }
            view.Pos = pos;
            view.Move.X = pos.X;
            view.Move.Y = pos.Y;
            view.Turn.Angle = angle * 180 / Math.PI;
        }
        foreach (var gone in _rocks.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _rocksLayer.Children.Remove(_rocks[gone].El);
            _rocks.Remove(gone);
        }
    }

    void DrawShots(IEnumerable<Vec2> shots)
    {
        int i = 0;
        foreach (var p in shots)
        {
            if (i == _shotPool.Count)
            {
                var dot = new Ellipse { Width = 6, Height = 6, Fill = Art.Brush(Themes.Current.Gold), IsHitTestVisible = false };
                _shotPool.Add(dot);
                _shotsLayer.Children.Add(dot);
            }
            var e = _shotPool[i++];
            e.IsVisible = true;
            Canvas.SetLeft(e, p.X - 3);
            Canvas.SetTop(e, p.Y - 3);
        }
        for (; i < _shotPool.Count; i++) _shotPool[i].IsVisible = false;
    }

    /// <summary>A rock: its jagged outline, a lit edge and two or three craters, all from its id, so it looks the same on every screen.</summary>
    static Canvas DrawRock(double r, int seed)
    {
        var c = new Canvas { IsHitTestVisible = false };
        c.Children.Add(Art.PathOf(RockPath(r, seed), Art.Brush("#7C808C"), Art.Brush("#3E414B"), 2));
        var rng = new Random(seed * 31 + 5);
        int craters = r > 20 ? 3 : 1;
        for (int i = 0; i < craters; i++)
        {
            double a = rng.NextDouble() * Math.PI * 2, d = r * (0.15 + rng.NextDouble() * 0.35), size = r * (0.18 + rng.NextDouble() * 0.14);
            c.Children.Add(Art.At(new Ellipse { Width = size * 2, Height = size * 1.7, Fill = Art.Brush("#5E626E"), Stroke = Art.Brush("#8E929E"), StrokeThickness = 1 },
                Math.Cos(a) * d - size, Math.Sin(a) * d - size * 0.85));
        }
        return c;
    }

    /// <summary>A jagged rock outline of radius <paramref name="r"/>, the same for the same <paramref name="seed"/> on every screen.</summary>
    static string RockPath(double r, int seed)
    {
        var rng = new Random(seed * 7919 + 17);
        int n = 11;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < n; i++)
        {
            double a = i * Math.PI * 2 / n, k = r * (0.72 + rng.NextDouble() * 0.3);
            sb.Append(i == 0 ? 'M' : 'L').Append(Art.F(Math.Cos(a) * k)).Append(',').Append(Art.F(Math.Sin(a) * k)).Append(' ');
        }
        return sb.Append('Z').ToString();
    }

    public override void ThemeChanged()
    {
        PaintShip(_me, Art.Safe(Themes.Current.Mine));
        PaintShip(_mate, Art.Safe(Themes.Current.Rival));
        _pad.Children.Clear();
        _pad.Children.Add(Art.At(new Rectangle { Width = 60, Height = 6, RadiusX = 3, RadiusY = 3, Fill = Art.Brush("#3A3E4A") }, -30, -6));
        var parked = Art.PathOf("M0,-38 L-11,-10 L0,-15 L11,-10 Z", Art.Brush(Art.Safe(Themes.Current.Mine)), Art.Brush(Art.Blend(Art.Safe(Themes.Current.Mine), Colors.Black, 0.45)), 1.5);
        _pad.Children.Add(parked);
        _pad.Children.Add(Art.PathOf("M0,-30 L-4,-21 L4,-21 Z", Art.Brush(Color.FromArgb(220, 200, 230, 255))));
        foreach (var dot in _shotPool) dot.Fill = Art.Brush(Themes.Current.Gold);
    }
}
