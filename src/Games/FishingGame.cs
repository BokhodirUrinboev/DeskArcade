using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>
/// Fishing: a pond along the bottom of the screen, a rod on a little pier at its left end. Drag back from the
/// rod tip and let go to cast; a fish comes over, nibbles, then pulls the bobber under — click right then to
/// hook it (too soon spooks it, too late and it steals the bait). Then hold to reel and let go before the
/// tension bar hits red, or the line snaps. Two-minute rounds, started by the first cast (see FishFight.cs).
/// </summary>
public sealed class FishingGame : MiniGame
{
    const double Step = 1.0 / 240, RoundSeconds = 120, RodLen = 150, RodAngle = -58, TipReach = 30, ReelReach = 30;
    const double BobberReach = 26, BobR = 6, MaxPull = 160, MinPower = 0.08, LandTime = 0.6, LandHold = 0.4, ReelTime = 0.5;
    const double WaveStep = 24, RippleLife = 0.9, BarH = 100, Hang = 36, PulseAt = 0.7;

    static readonly Color Gold = Color.FromRgb(255, 209, 102);
    static readonly Color[] Drops = { Color.FromRgb(200, 230, 255), Colors.White, Color.FromRgb(140, 200, 240) };
    static readonly Color[] GoldBurst = { Gold, Color.FromRgb(255, 240, 180), Colors.White };
    static readonly IBrush Wood = Art.Brush("#8B5A2B"), WoodDark = Art.Brush("#4E3116"), Post = Art.Brush("#5E3B1C");
    static readonly IBrush Reed = Art.Brush("#5E8C3A"), Cattail = Art.Brush("#6B4226");

    enum State { Ready, Aiming, Flying, Waiting, Fighting, Landing, Reeling }
    enum Mode { Wander, Approach, AtBait, Flee, Hooked, Landing }

    sealed class Fish
    {
        public required Sprite Sprite;
        public required Species Species;
        public double Kg, Len, Face = 1, Speed, Rest, FleeT;
        public Vec2 Pos, Goal;
        public Mode Mode;
    }

    sealed class Ripple
    {
        public required Ellipse El;
        public required TranslateTransform Tr;
        public Vec2 P;
        public double Age = RippleLife, Size;
    }

    readonly Canvas _back = new() { IsHitTestVisible = false };
    readonly Path _water = new() { IsHitTestVisible = false, Fill = WaterBrush() };
    readonly Canvas _fishLayer = new() { IsHitTestVisible = false };
    readonly Path _surfaceLine = new() { IsHitTestVisible = false, Stroke = Art.Brush(150, 225, 240, 255), StrokeThickness = 1.5 };
    readonly Canvas _rippleLayer = new() { IsHitTestVisible = false };
    readonly Canvas _front = new() { IsHitTestVisible = false };
    readonly Path _line = new() { IsHitTestVisible = false, Stroke = Art.Brush(170, 240, 240, 240), StrokeThickness = 1 };
    readonly Path _rod = new() { IsHitTestVisible = false, Stroke = Art.Brush("#3B2A1A"), StrokeThickness = 3, StrokeLineCap = PenLineCap.Round };
    readonly Path _handle = new() { IsHitTestVisible = false, Stroke = Art.Brush("#C89B63"), StrokeThickness = 5.5, StrokeLineCap = PenLineCap.Round };
    Sprite _reelSprite = MakeReel();
    Sprite _bobber = MakeBobber();
    readonly Canvas _guide = new() { IsHitTestVisible = false };
    readonly Ellipse[] _dots = new Ellipse[10];
    readonly Canvas _bar = new() { IsHitTestVisible = false, IsVisible = false };
    readonly Rectangle _barFill = new() { Width = 8, RadiusX = 2, RadiusY = 2 };
    readonly List<Fish> _fish = new();
    readonly List<Ripple> _ripples = new();
    readonly IBrush?[] _barBrushes = new IBrush?[3];

    State _state;
    bool _active, _reeling, _baitGone, _demoWillMiss, _barColorBlind;
    double _time, _acc, _roundLeft, _lastWave = -1, _lastClick = -1, _lastPlop = -1;
    double _depth, _surface, _waterL, _waterR, _deckY, _pierEnd, _castMin, _castMax, _shoreX;
    double _power, _flyT, _flyTime, _interestIn, _waitT, _landT, _reelT, _reelSpin;
    int _score, _caught, _shownSecond = -1, _barZone = -1, _demoPause;
    Vec2 _butt, _tip, _reelAt, _bob, _pressAt, _flyFrom, _landAt, _reelFrom, _landFrom;
    Fish? _suitor;
    BiteSchedule? _bite;
    FishFight? _fight;

    public FishingGame(IGameHost host) : base(host)
    {
        for (int i = 0; i < _dots.Length; i++)
        {
            _dots[i] = new Ellipse
            {
                Width = 5, Height = 5, Fill = Brushes.White, IsVisible = false,
                RenderTransformOrigin = RelativePoint.TopLeft, RenderTransform = new TranslateTransform(),
            };
            _guide.Children.Add(_dots[i]);
        }
        for (int i = 0; i < 8; i++)
        {
            var tr = new TranslateTransform();
            var el = new Ellipse
            {
                Stroke = Art.Brush(200, 230, 245, 255), StrokeThickness = 1.3, IsVisible = false, IsHitTestVisible = false,
                RenderTransformOrigin = RelativePoint.TopLeft, RenderTransform = tr,
            };
            _rippleLayer.Children.Add(el);
            _ripples.Add(new Ripple { El = el, Tr = tr });
        }
        _bar.Children.Add(new Rectangle
        {
            Width = 12, Height = BarH, RadiusX = 3, RadiusY = 3, Fill = Art.Brush(150, 20, 24, 32), Stroke = Art.Brush(140, 255, 255, 255), StrokeThickness = 1,
        });
        _bar.Children.Add(Art.At(_barFill, 2, BarH - 2));

        Layer.Children.Add(_back);
        Layer.Children.Add(_water);
        Layer.Children.Add(_fishLayer);
        Layer.Children.Add(_surfaceLine);
        Layer.Children.Add(_rippleLayer);
        Layer.Children.Add(_front);
        Layer.Children.Add(_line);
        Layer.Children.Add(_handle);
        Layer.Children.Add(_rod);
        Layer.Children.Add(_reelSprite);
        Layer.Children.Add(_bobber);
        Layer.Children.Add(_guide);
        Layer.Children.Add(_bar);
    }

    public override string Id => "fishing";
    public override string Title => "Fishing";

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        s.Rotor.Children.Add(Art.PathOf("M-10,5 Q-5,2 0,5 T10,5", null, Art.Brush("#8FD3FF"), 1.6));
        s.Rotor.Children.Add(Art.PathOf("M0,-6 L0,-11", null, Art.Brush("#E9ECEF"), 1.4));
        s.Rotor.Children.Add(Art.PathOf("M-6,0 A6,6 0 0 1 6,0 Z", Art.Brush(Themes.Current.Mine)));
        s.Rotor.Children.Add(Art.PathOf("M-6,0 A6,6 0 0 0 6,0 Z", Brushes.White));
        s.Rotor.Children.Add(Art.Circle(0, 0, 6, null, Art.Brush("#1C1F26"), 1));
        return s;
    }

    int SecondsLeft => (int)Math.Ceiling(Math.Max(0, _roundLeft));

    public override HudInfo Hud => new(
        _score.ToString(),
        _active ? L.F("{0}s left · caught {1}", SecondsLeft, _caught) : L.T("Drag back from the rod tip and let go to cast"),
        L.F("Best {0}", Host.Stats.Get("fishing.best")));

    static string NameOf(Species s) => s.Id switch
    {
        "carp" => L.T("Carp"),
        "pike" => L.T("Pike"),
        "catfish" => L.T("Catfish"),
        "golden" => L.T("Golden Trout"),
        _ => L.T("Perch"),
    };

    // ------------------------------------------------------------------ layout

    public override void Layout()
    {
        var a = Host.Arena;
        _depth = Clamp(a.Height * 0.11, 100, 120);
        _surface = a.Bottom - _depth;
        _waterL = a.Left + 70;
        _waterR = a.Right - 24;
        _deckY = _surface - 12;
        _pierEnd = _waterL + 100;
        _castMin = _pierEnd + 40;
        _castMax = Math.Max(_castMin + 40, _waterR - 40);
        _butt = new Vec2(a.Left + 70, _deckY - 3);
        _shoreX = (_butt + Polar(RodLen, RodAngle)).X;
        BuildScenery();

        int want = (int)Clamp((_waterR - _waterL) / 300, 3, 8);
        while (_fish.Count < want) AddFish(RandomSpot());
        for (int i = _fish.Count - 1; i >= 0 && _fish.Count > want; i--)
            if (_fish[i] != _suitor) RemoveFish(_fish[i]);
        foreach (var f in _fish)
        {
            if (f.Mode is Mode.Hooked or Mode.Landing) continue;
            f.Pos = ClampToWater(f.Pos, f.Len);
            f.Goal = ClampToWater(f.Goal, f.Len);
        }

        // a changed screen mid-cast: keep the bobber on the new pond
        _landAt = new Vec2(Clamp(_landAt.X, _castMin, _castMax), _surface);
        if (_state is State.Waiting or State.Reeling) _bob = new Vec2(Clamp(_bob.X, _castMin, _castMax), _surface);
        DrawWater(_time);
        Draw();
        Host.HudChanged();
    }

    public override void Deactivate()
    {
        _active = false; // an unfinished round is dropped, like Whack-a-Bug's
        _state = State.Ready;
        _fight = null;
        _bite = null;
        _suitor = null;
        _reeling = _baitGone = false;
        foreach (var f in _fish)
            if (f.Mode != Mode.Wander) SetWander(f);
        foreach (var r in _ripples)
        {
            r.Age = RippleLife;
            r.El.IsVisible = false;
        }
        HideGuide();
        _acc = 0;
    }

    Vec2 ClampToWater(Vec2 p, double len) => new(
        Clamp(p.X, _waterL + 30 + len / 2, Math.Max(_waterL + 31 + len / 2, _waterR - 20 - len / 2)),
        Clamp(p.Y, _surface + 16, Math.Max(_surface + 17, Host.Arena.Bottom - 12)));

    Vec2 RandomSpot() => new(_waterL + 40 + Rng.NextDouble() * Math.Max(1, _waterR - _waterL - 80), _surface + 18 + Rng.NextDouble() * Math.Max(1, _depth - 32));

    void AddFish(Vec2 at)
    {
        var s = FishFight.Pick(Rng);
        double kg = FishFight.RollKg(s, Rng);
        double len = s.Length * (0.8 + 0.45 * FishFight.SizeOf(s, kg));
        var f = new Fish { Sprite = MakeFish(s, len), Species = s, Kg = kg, Len = len, Face = Rng.NextDouble() < 0.5 ? -1 : 1 };
        f.Pos = ClampToWater(at, len);
        SetWander(f);
        f.Rest = Rng.NextDouble() * 2;
        _fishLayer.Children.Add(f.Sprite);
        _fish.Add(f);
        DrawFish(f);
    }

    void RemoveFish(Fish f)
    {
        _fishLayer.Children.Remove(f.Sprite);
        _fish.Remove(f);
    }

    void SetWander(Fish f)
    {
        f.Mode = Mode.Wander;
        f.Speed = 22 + Rng.NextDouble() * 22;
        f.Goal = ClampToWater(RandomSpot(), f.Len);
        f.Sprite.Opacity = f.Species.Golden ? 0.7 : 0.42;
    }

    /// <summary>Darts off away from the bobber, then goes back to wandering.</summary>
    void Flee(Fish f)
    {
        double dir = f.Pos.X < _bob.X ? -1 : 1;
        f.Mode = Mode.Flee;
        f.FleeT = 1.5;
        f.Speed = 150;
        f.Goal = ClampToWater(new Vec2(f.Pos.X + dir * 300, f.Pos.Y + 20), f.Len);
        f.Sprite.Opacity = f.Species.Golden ? 0.7 : 0.42;
    }

    void BuildScenery()
    {
        var a = Host.Arena;
        _back.Children.Clear();
        _front.Children.Clear();
        string F(double v) => Art.F(v);

        // the bank under the pier; the translucent water laps over its edge
        _back.Children.Add(Art.PathOf(
            $"M{F(a.Left)},{F(a.Bottom)} L{F(a.Left)},{F(_surface - 10)} Q{F(_waterL - 20)},{F(_surface - 9)} {F(_waterL + 16)},{F(_surface + 6)} L{F(_waterL + 46)},{F(a.Bottom)} Z",
            Art.Brush(235, 74, 104, 52), Art.Brush(200, 48, 72, 34), 1));
        _back.Children.Add(Art.PathOf(
            $"M{F(a.Left + 8)},{F(_surface - 10)} l-2,-7 M{F(a.Left + 14)},{F(_surface - 10)} l1,-8 M{F(a.Left + 30)},{F(_surface - 10)} l-1,-6 M{F(a.Left + 36)},{F(_surface - 10)} l2,-7",
            null, Reed, 1.4));

        // reeds stand in the water, so the water in front of them softens their stems
        var reeds = new System.Text.StringBuilder();
        void Stem(double x, double top, double lean) =>
            reeds.Append($"M{F(x)},{F(a.Bottom - 4)} Q{F(x + lean * 0.3)},{F(_surface - 10)} {F(x + lean)},{F(top)} ");
        for (int i = 0; i < 6; i++)
        {
            double x = _waterR - 30 + i * 7, top = _surface - 36 - (i * 37 % 4) * 12;
            Stem(x, top, (i % 2 == 0 ? 1 : -1) * (3 + i));
            if (i % 2 == 0) _back.Children.Add(Art.At(new Ellipse { Width = 5, Height = 15, Fill = Cattail }, x + (3 + i) - 2.5, top + 5));
        }
        for (int i = 0; i < 3; i++) Stem(_waterL + 4 + i * 8, _surface - 30 - i * 10, -4 + i * 3);
        _back.Children.Add(Art.PathOf(reeds.ToString(), null, Reed, 2.2));

        // lily pads floating near the right end
        double[] pads = { _waterR - 70, _waterR - 118, _waterR - 176 };
        for (int i = 0; i < pads.Length; i++)
        {
            if (pads[i] < _castMin + 80) continue;
            double rx = 15 - i * 2, ry = rx * 0.32, cx = pads[i], cy = _surface + 1;
            _front.Children.Add(Art.PathOf(
                $"M{F(cx)},{F(cy)} L{F(cx + rx * 0.94)},{F(cy + ry * 0.34)} A{F(rx)},{F(ry)} 0 1 1 {F(cx + rx * 0.94)},{F(cy - ry * 0.34)} Z",
                Art.Brush(220, 76, 140, 58), Art.Brush(220, 47, 94, 36), 1));
            if (i == 0) _front.Children.Add(Art.Circle(cx - rx * 0.3, cy - 2, 3.2, Art.Brush("#F4A7C0"), Art.Brush("#C2708F"), 0.8));
        }

        // the pier: posts in the water, a plank deck and the rod holder
        double deckL = a.Left + 10;
        _front.Children.Add(Art.At(new Rectangle { Width = 6, Height = Math.Max(0, a.Bottom - 4 - _deckY - 7), Fill = Post }, _pierEnd - 14, _deckY + 7));
        _front.Children.Add(Art.At(new Rectangle { Width = 6, Height = Math.Max(0, a.Bottom - 4 - _deckY - 7), Fill = Post }, _waterL + 20, _deckY + 7));
        _front.Children.Add(Art.At(new Rectangle { Width = _pierEnd - deckL, Height = 7, RadiusX = 1.5, RadiusY = 1.5, Fill = Wood, Stroke = WoodDark, StrokeThickness = 1 }, deckL, _deckY));
        var planks = new System.Text.StringBuilder();
        for (double x = deckL + 14; x < _pierEnd - 4; x += 14) planks.Append($"M{F(x)},{F(_deckY + 1)} L{F(x)},{F(_deckY + 6)} ");
        _front.Children.Add(Art.PathOf(planks.ToString(), null, WoodDark, 1));
        _front.Children.Add(Art.At(new Rectangle { Width = 7, Height = 13, RadiusX = 1.5, RadiusY = 1.5, Fill = Art.Brush("#2B2F36") }, _butt.X - 3.5, _deckY - 13));
        // a bucket for the catch
        _front.Children.Add(Art.PathOf(
            $"M{F(a.Left + 20)},{F(_deckY - 16)} L{F(a.Left + 38)},{F(_deckY - 16)} L{F(a.Left + 36)},{F(_deckY)} L{F(a.Left + 22)},{F(_deckY)} Z",
            Art.Brush("#8A96A3"), Art.Brush("#4A525C"), 1));

        var handle = new StreamGeometry();
        using (var ctx = handle.Open())
        {
            ctx.BeginFigure((_butt + Polar(-8, RodAngle)).ToPoint(), false);
            ctx.LineTo((_butt + Polar(26, RodAngle)).ToPoint());
            ctx.EndFigure(false);
        }
        _handle.Data = handle;
        _reelAt = _butt + Polar(16, RodAngle) + Polar(9, RodAngle + 90);
        _bar.RenderTransform = new TranslateTransform(_butt.X - 50, _deckY - 26 - BarH);
    }

    // ------------------------------------------------------------------ round flow

    public override bool SupportsLan => true;
    public override (int Score, bool Active)? Race => (_score, _active);

    public override void StartRace()
    {
        if (!_active) StartRound();
    }

    void StartRound()
    {
        _active = true;
        Host.RoundStarted();
        _roundLeft = RoundSeconds;
        _score = _caught = 0;
        _shownSecond = -1;
        Host.Sound.Play("fire", 0.5);
        Host.HudChanged();
    }

    void EndRound()
    {
        _active = false;
        Host.RoundEnded(_score);
        if (_state == State.Fighting && _suitor != null)
        {
            Flee(_suitor); // time's up: it gets away with the hook
            _fight = null;
            _suitor = null;
            _state = State.Waiting;
            _reeling = false;
        }
        if (_state == State.Waiting) ReelIn();

        long before = Host.Stats.Get("fishing.best");
        Host.Stats.Max("fishing.best", _score);
        bool best = _score > before;
        var a = Host.Arena;
        var at = new Vec2(a.Center.X, a.Top + a.Height * 0.3);
        Host.Fx.Popup(at, best ? L.T("NEW BEST!") : L.T("TIME!"), best ? Gold : Colors.White, 38, 2.4,
            L.F("{0} points · {1} caught", _score, _caught));
        if (best)
        {
            Host.Fx.Burst(at, new[] { Gold, Colors.White, Color.FromRgb(6, 214, 160) }, 40, 520, 700, 7, 1.1);
            Host.Sound.Play("best", 0.8);
        }
        else
        {
            Host.Sound.Play("done", 0.5);
        }
        _demoPause = 25;
        Host.HudChanged();
    }

    // ------------------------------------------------------------------ input

    public override void CollectHitShapes(List<HitShape> into)
    {
        into.Add(HitShape.Circle(_tip, TipReach));
        into.Add(HitShape.Circle(_reelAt, ReelReach));
        if (_state is State.Waiting or State.Fighting) into.Add(HitShape.Circle(_bob, BobberReach));
    }

    public override bool PointerDown(Vec2 p, bool right)
    {
        switch (_state)
        {
            case State.Ready:
                if ((p - _tip).Length > TipReach && (p - _reelAt).Length > ReelReach) return false;
                _state = State.Aiming;
                _pressAt = p;
                _power = 0;
                return true;
            case State.Waiting:
                if (!Strike()) return false;
                _reeling = true; // the click that sets the hook keeps reeling while it is held
                return true;
            case State.Fighting:
                _reeling = true;
                return true;
            default:
                return false;
        }
    }

    public override void PointerUp(Vec2 p)
    {
        if (_state == State.Aiming)
        {
            HideGuide();
            if (_power >= MinPower) Cast(_power);
            else _state = State.Ready;
        }
        _reeling = false;
        Host.Wake();
    }

    /// <summary>Casts to where the cursor is, if it is over the pond.</summary>
    public override void Summon(Vec2 p)
    {
        if (_state != State.Ready) return;
        Cast(Clamp((p.X - _castMin) / Math.Max(1, _castMax - _castMin), 0, 1));
    }

    void Cast(double power)
    {
        _flyFrom = _bob;
        _landAt = new Vec2(_castMin + (_castMax - _castMin) * power, _surface);
        _flyT = 0;
        _flyTime = 0.45 + 0.35 * power;
        _state = State.Flying;
        Host.Sound.Play("whoosh", 0.45 + 0.35 * power, 1.25 - 0.35 * power);
        if (!_active) StartRound();
        Host.Wake();
    }

    void Land()
    {
        _bob = _landAt;
        _state = State.Waiting;
        _suitor = null;
        _bite = null;
        _baitGone = false;
        _waitT = 0;
        _interestIn = 1.2 + Rng.NextDouble() * 2.5;
        Host.Sound.Play("splash", 0.45, 1.3);
        AddRipple(_bob, 36);
        Host.Fx.Burst(_bob, Drops, 8, 170, 900, 4, 0.45);
        if (!_active) ReelIn(); // the round ended while it flew
    }

    /// <summary>A click while the bobber is out: hooks a biting fish, spooks a nibbling one, otherwise reels in.</summary>
    bool Strike()
    {
        if (_suitor is { Mode: Mode.AtBait } && _bite != null)
        {
            switch (_bite.Click())
            {
                case BiteClick.Hooked:
                    Hook(_suitor);
                    return true;
                case BiteClick.Spooked:
                    Spook();
                    return false;
            }
        }
        ReelIn();
        return false;
    }

    void Spook()
    {
        var f = _suitor!;
        Host.Fx.Popup(_bob - new Vec2(0, 44), L.T("Too soon!"), Color.FromRgb(255, 190, 120), 24, 1.2, L.T("you spooked it"));
        Host.Sound.Play("plop", 0.4, 0.7);
        AddRipple(f.Pos with { Y = _surface }, 30);
        Flee(f);
        _suitor = null;
        _bite = null;
        _interestIn = 2 + Rng.NextDouble() * 2;
        Host.ShareAction(_bob, 0);
    }

    void Missed()
    {
        var f = _suitor!;
        Host.Fx.Popup(_bob - new Vec2(0, 44), L.T("Too late!"), Color.FromRgb(255, 130, 130), 26, 1.4, L.T("it stole the bait · reel in"));
        Host.Sound.Play("buzzer", 0.3);
        Flee(f);
        _suitor = null;
        _bite = null;
        _baitGone = true;
        Host.ShareAction(_bob, 0);
    }

    void ReelIn()
    {
        if (_state != State.Waiting) return;
        if (_suitor != null && _suitor.Mode is Mode.Approach or Mode.AtBait) SetWander(_suitor);
        _suitor = null;
        _bite = null;
        _state = State.Reeling;
        _reelFrom = _bob;
        _reelT = 0;
        Host.Wake();
    }

    void Hook(Fish f)
    {
        f.Mode = Mode.Hooked;
        f.Face = 1;
        f.Sprite.Opacity = 0.6;
        _bite = null;
        _state = State.Fighting;
        double dist = Math.Max(20, f.Pos.X - _shoreX);
        _fight = new FishFight(f.Species, f.Kg, dist, Rng, _castMax + 40 - _shoreX);
        Host.Sound.Play("kick", 0.5, 1.4);
        Host.Sound.Play("splash", 0.4, 1.6);
        AddRipple(_bob, 40);
        Host.Fx.Burst(_bob, Drops, 10, 220, 900, 4, 0.5);
    }

    void Snap()
    {
        var f = _suitor!;
        Host.Sound.Play("twang", 0.7, 1.7);
        Host.Fx.Popup(_bob - new Vec2(0, 50), L.T("Line snapped!"), Color.FromRgb(255, 92, 108), 28, 1.5, L.T("it got away"));
        AddRipple(_bob, 44);
        Flee(f);
        f.Speed = 220;
        _suitor = null;
        _fight = null;
        _reeling = false;
        _state = State.Ready; // a fresh float comes with the new line
        Host.ShareAction(_bob, 0);
    }

    void Catch()
    {
        var f = _suitor!;
        int pts = FishFight.PointsFor(f.Species, f.Kg);
        _score += pts;
        _caught++;
        Host.Stats.Add("fishing.caught");
        if (f.Species.Golden) Host.Stats.Add("fishing.golden");
        Host.Stats.Max("fishing.heaviest", (long)Math.Round(f.Kg * 1000));
        Host.ShareAction(f.Pos, pts);

        _fight = null;
        _reeling = false;
        _state = State.Landing;
        _landT = 0;
        _landFrom = f.Pos;
        f.Mode = Mode.Landing;
        f.Sprite.Opacity = 1;

        string kg = f.Kg.ToString("0.0", CultureInfo.InvariantCulture);
        Host.Fx.Popup(_tip + new Vec2(80, -30), L.F("{0} · {1} kg", NameOf(f.Species), kg), f.Species.Golden ? Gold : Colors.White, 26, 1.8,
            L.F("+{0} points", pts));
        Host.Sound.Play("splash", 0.5, 1.1);
        Host.Fx.Burst(f.Pos with { Y = _surface }, Drops, 12, 260, 900, 4, 0.55);
        if (f.Species.Golden)
        {
            Host.Fx.Burst(_tip + new Vec2(0, 30), GoldBurst, 30, 420, 700, 6, 1.0);
            Host.Sound.Play("best", 0.8);
        }
        else
        {
            Host.Sound.Play("score", 0.6);
        }
        Host.HudChanged();
    }

    // ------------------------------------------------------------------ simulation

    public override bool Update(double dt)
    {
        _time += dt;
        if (_state == State.Aiming)
        {
            var pull = _pressAt - Host.Pointer;
            _power = pull.X > 0 ? Clamp(pull.Length / MaxPull, 0, 1) : 0; // only a drag away from the pond
        }

        _acc += dt;
        while (_acc >= Step)
        {
            _acc -= Step;
            Tick(Step);
        }

        if (_active && SecondsLeft != _shownSecond)
        {
            _shownSecond = SecondsLeft;
            Host.HudChanged();
            if (_shownSecond is > 0 and <= 3) Host.Sound.Play("rim", 0.25, 2.5);
        }
        if (_active && _time - _lastWave >= 1.0 / 30)
        {
            _lastWave = _time;
            DrawWater(_time);
        }

        bool ripples = false;
        foreach (var r in _ripples)
        {
            if (r.Age >= RippleLife) continue;
            ripples = true;
            DrawRipple(r);
        }
        Draw();

        bool busy = _active || ripples || _state is State.Aiming or State.Flying or State.Landing or State.Reeling;
        if (!busy) _acc = 0;
        return busy;
    }

    void Tick(double h)
    {
        if (_active && (_roundLeft -= h) <= 0) EndRound();

        switch (_state)
        {
            case State.Flying:
                _flyT += h;
                _bob = ArcAt(_flyFrom, _landAt, Math.Min(1, _flyT / _flyTime));
                if (_flyT >= _flyTime) Land();
                break;
            case State.Waiting:
                TickWaiting(h);
                break;
            case State.Fighting:
                TickFight(h);
                break;
            case State.Landing:
                TickLanding(h);
                break;
            case State.Reeling:
                _reelT += h;
                _reelSpin += h * 900;
                _bob = _reelFrom + (HangPos - _reelFrom) * Smooth(Math.Min(1, _reelT / ReelTime));
                if (_time - _lastClick > 0.05)
                {
                    _lastClick = _time;
                    Host.Sound.Play("reel", 0.3, 1.1);
                }
                if (_reelT >= ReelTime) _state = State.Ready;
                break;
        }

        if (_active)
            foreach (var f in _fish) Swim(f, h);
        foreach (var r in _ripples) r.Age += h;
    }

    Vec2 HangPos => _tip + new Vec2(0, Hang);

    void TickWaiting(double h)
    {
        _waitT += h;
        if (_suitor == null && !_baitGone && (_interestIn -= h) <= 0) PickSuitor();
        if (_bite != null && _suitor != null)
        {
            switch (_bite.Advance(h))
            {
                case BiteEvent.Nibble:
                    if (_time - _lastPlop > 0.1) Host.Sound.Play("plop", 0.35, 0.9 + Rng.NextDouble() * 0.3);
                    _lastPlop = _time;
                    AddRipple(_bob, 22);
                    break;
                case BiteEvent.Bite:
                    Host.Sound.Play("splash", 0.55, 1.25);
                    AddRipple(_bob, 42);
                    Host.Fx.Burst(_bob, Drops, 8, 200, 900, 4, 0.45);
                    _demoWillMiss = Rng.NextDouble() < 0.15;
                    break;
                case BiteEvent.Missed:
                    Missed();
                    break;
            }
        }
        double dip = _bite?.Dip ?? 0;
        _bob = new Vec2(_bob.X, WaveY(_bob.X, _time) - 1 + (_active ? Math.Sin(_time * 3) * 1.2 : 0) + dip * 12);
    }

    void PickSuitor()
    {
        Fish? best = null;
        double nearest = double.MaxValue;
        foreach (var f in _fish)
        {
            if (f.Mode != Mode.Wander) continue;
            double d = Math.Abs(f.Pos.X - _bob.X) + (Rng.NextDouble() * 120); // not always the very closest
            if (d < nearest)
            {
                nearest = d;
                best = f;
            }
        }
        if (best == null)
        {
            _interestIn = 1;
            return;
        }
        _suitor = best;
        best.Mode = Mode.Approach;
        best.Speed = 55 + Rng.NextDouble() * 25;
    }

    void TickFight(double h)
    {
        var fight = _fight!;
        var f = _suitor!;
        fight.Step(h, _reeling);
        if (_reeling)
        {
            _reelSpin += h * 720;
            if (_time - _lastClick > 0.07 - 0.03 * fight.Tension)
            {
                _lastClick = _time;
                Host.Sound.Play("reel", 0.3, 0.85 + 0.35 * fight.Tension);
            }
        }
        double thrash = fight.Surging ? Math.Sin(_time * 22) * 3 : Math.Sin(_time * 5) * 1.2;
        f.Pos = new Vec2(_shoreX + fight.Distance, _surface + 12 + Math.Min(1, fight.Distance / 200) * 16 + thrash);
        f.Face = 1; // pulling away from the pier
        _bob = new Vec2(f.Pos.X - f.Len * 0.2, WaveY(f.Pos.X, _time) + 2 + 6 * fight.Tension);
        if (fight.Surging && Rng.NextDouble() < h * 5)
            Host.Fx.Burst(new Vec2(f.Pos.X, _surface), Drops, 4, 160, 900, 3.5, 0.4);

        if (fight.Snapped) Snap();
        else if (fight.Landed) Catch();
    }

    void TickLanding(double h)
    {
        var f = _suitor!;
        _landT += h;
        double k = Smooth(Math.Min(1, _landT / LandTime));
        f.Pos = _landFrom + (_tip + new Vec2(0, 10 + f.Len * 0.5) - _landFrom) * k;
        f.Face = 1;
        _bob = _tip + new Vec2(0, 4);
        if (_landT < LandTime + LandHold) return;
        RemoveFish(f);
        _suitor = null;
        _state = State.Ready;
        // a new fish swims in from the far end
        AddFish(new Vec2(_waterR - 30, _surface + 20 + Rng.NextDouble() * Math.Max(1, _depth - 36)));
    }

    void Swim(Fish f, double h)
    {
        switch (f.Mode)
        {
            case Mode.Hooked:
            case Mode.Landing:
                return;
            case Mode.Approach:
            {
                double side = f.Pos.X < _bob.X ? -1 : 1;
                f.Goal = new Vec2(_bob.X + side * f.Len * 0.5, _surface + 10 + f.Len * 0.12);
                if (MoveToward(f, h, 1.5))
                {
                    f.Mode = Mode.AtBait;
                    _bite = new BiteSchedule(Rng);
                }
                return;
            }
            case Mode.AtBait:
            {
                // lunges at the bait as it nibbles
                double side = f.Face > 0 ? -1 : 1;
                double dip = _bite?.Dip ?? 0;
                f.Pos = new Vec2(_bob.X + side * f.Len * (0.5 - 0.2 * Math.Min(1, dip)), _surface + 10 + f.Len * 0.12 + Math.Sin(_time * 4) * 1);
                return;
            }
            case Mode.Flee:
                f.FleeT -= h;
                if (MoveToward(f, h, 6) || f.FleeT <= 0) SetWander(f);
                return;
        }

        if (f.Rest > 0)
        {
            f.Rest -= h;
            f.Pos.Y += Math.Sin(_time * 1.3 + f.Len) * 2 * h; // hanging in the water, fins going
            return;
        }
        if (MoveToward(f, h, 4))
        {
            f.Rest = 0.5 + Rng.NextDouble() * 2.2;
            f.Goal = ClampToWater(RandomSpot(), f.Len);
        }
    }

    /// <summary>Turns toward the goal, swims there (slowed while turning); true on arrival.</summary>
    static bool MoveToward(Fish f, double h, double close)
    {
        var d = f.Goal - f.Pos;
        double dist = d.Length;
        if (dist <= close) return true;
        if (Math.Abs(d.X) > 2) f.Face += (Math.Sign(d.X) - f.Face) * Math.Min(1, 5 * h);
        double along = Math.Max(0.15, Math.Abs(f.Face));
        double step = Math.Min(dist, f.Speed * along * h);
        f.Pos += d / dist * step;
        return false;
    }

    // ------------------------------------------------------------------ visuals

    static Vec2 Polar(double r, double deg)
    {
        var (x, y) = Art.Polar(r, deg);
        return new Vec2(x, y);
    }

    static double Smooth(double k) => k * k * (3 - 2 * k);

    double WaveY(double x, double t) => _surface + Math.Sin(x * 0.021 + t * 1.7) * 1.6 + Math.Sin(x * 0.053 - t * 2.4) * 0.9;

    Vec2 ArcAt(Vec2 from, Vec2 to, double k)
    {
        double height = Math.Min(60 + 0.2 * Math.Abs(to.X - from.X), Math.Max(0, Math.Min(from.Y, to.Y) - Host.Arena.Top - 20));
        var p = from + (to - from) * k;
        p.Y -= 4 * height * k * (1 - k);
        return p;
    }

    void DrawWater(double t)
    {
        double bottom = Host.Arena.Bottom;
        var fill = new StreamGeometry();
        var top = new StreamGeometry();
        using (var f = fill.Open())
        using (var s = top.Open())
        {
            f.BeginFigure(new Point(_waterL + 26, bottom), true);
            for (double x = _waterL; ; x += WaveStep)
            {
                double xx = Math.Min(x, _waterR);
                var p = new Point(xx, WaveY(xx, t));
                f.LineTo(p);
                if (x == _waterL) s.BeginFigure(p, false);
                else s.LineTo(p);
                if (xx >= _waterR) break;
            }
            f.LineTo(new Point(_waterR - 18, bottom));
            f.EndFigure(true);
            s.EndFigure(false);
        }
        _water.Data = fill;
        _surfaceLine.Data = top;
    }

    void Draw()
    {
        // the rod bends back while aiming and toward the fish under tension
        double ang = RodAngle, bend = 0;
        if (_state == State.Aiming) bend = -30 * _power;
        else if (_state == State.Fighting && _fight != null) bend = 30 * _fight.Tension;
        ang += bend;
        _tip = _butt + Polar(RodLen * (1 - 0.04 * Math.Abs(bend) / 30), ang);
        var ctrl = _butt + Polar(RodLen * 0.55, RodAngle + bend * 0.25);
        var rod = new StreamGeometry();
        using (var ctx = rod.Open())
        {
            ctx.BeginFigure(_butt.ToPoint(), false);
            ctx.QuadraticBezierTo(ctrl.ToPoint(), _tip.ToPoint());
            ctx.EndFigure(false);
        }
        _rod.Data = rod;
        _reelSprite.Set(_reelAt, _reelSpin % 360);

        if (_state is State.Ready or State.Aiming) _bob = HangPos;
        _bobber.Set(_bob);
        _bobber.Opacity = _state == State.Waiting && (_bite?.Dip ?? 0) >= 1 ? 0.45 : 1;

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            var stick = _bob + new Vec2(0, -BobR - 5);
            ctx.BeginFigure(_tip.ToPoint(), false);
            double sag = _state switch
            {
                State.Waiting or State.Reeling => 22,
                State.Flying => 8,
                State.Fighting => 22 * (1 - (_fight?.Tension ?? 0)),
                _ => 0,
            };
            var mid = (_tip + stick) / 2 + new Vec2(0, sag);
            ctx.QuadraticBezierTo(mid.ToPoint(), stick.ToPoint());
            ctx.EndFigure(false);
            if (_suitor != null && _state is State.Fighting or State.Landing)
            {
                var head = _suitor.Pos + (_state == State.Landing ? new Vec2(0, -_suitor.Len * 0.5) : new Vec2(-_suitor.Len * 0.3, 0));
                ctx.BeginFigure((_bob + new Vec2(0, BobR)).ToPoint(), false);
                ctx.LineTo(head.ToPoint());
                ctx.EndFigure(false);
            }
        }
        _line.Data = line;

        foreach (var f in _fish) DrawFish(f);
        DrawBar();
        DrawGuide();
    }

    void DrawFish(Fish f)
    {
        double angle = 0;
        if (f.Mode == Mode.Landing) angle = -90 * Smooth(Math.Min(1, _landT / LandTime)); // hangs from the line by its mouth
        f.Sprite.Set(f.Pos, angle);
        f.Sprite.FlipX = Math.Abs(f.Face) >= 0.1 ? f.Face : f.Face < 0 ? -0.1 : 0.1; // turning: seen edge-on for a moment
    }

    void DrawBar()
    {
        bool show = _state == State.Fighting && _fight != null;
        _bar.IsVisible = show;
        if (!show) return;
        double t = _fight!.Tension;
        int zone = t < 0.55 ? 0 : t < 0.8 ? 1 : 2;
        if (Art.ColorBlind != _barColorBlind)
        {
            _barColorBlind = Art.ColorBlind;
            Array.Clear(_barBrushes);
            _barZone = -1;
        }
        if (zone != _barZone)
        {
            _barZone = zone;
            _barBrushes[zone] ??= Art.Brush(Art.Safe(zone switch
            {
                0 => Color.FromRgb(61, 220, 132),
                1 => Color.FromRgb(255, 209, 102),
                _ => Color.FromRgb(255, 92, 108),
            }));
            _barFill.Fill = _barBrushes[zone];
        }
        double height = (BarH - 4) * Math.Clamp(t, 0, 1);
        _barFill.Height = height;
        Canvas.SetTop(_barFill, BarH - 2 - height);
    }

    void DrawGuide()
    {
        if (_state != State.Aiming || _power < MinPower)
        {
            HideGuide();
            return;
        }
        var to = new Vec2(_castMin + (_castMax - _castMin) * _power, _surface);
        for (int i = 0; i < _dots.Length; i++)
        {
            var p = ArcAt(_bob, to, (i + 1.0) / _dots.Length);
            var tr = (TranslateTransform)_dots[i].RenderTransform!;
            tr.X = p.X - 2.5;
            tr.Y = p.Y - 2.5;
            _dots[i].Opacity = 0.75 * (1 - 0.6 * i / _dots.Length);
            _dots[i].IsVisible = true;
        }
    }

    void HideGuide()
    {
        foreach (var d in _dots) d.IsVisible = false;
    }

    void AddRipple(Vec2 p, double size)
    {
        foreach (var r in _ripples)
        {
            if (r.Age < RippleLife) continue;
            r.P = new Vec2(p.X, _surface + 1);
            r.Age = 0;
            r.Size = size;
            r.El.IsVisible = true;
            DrawRipple(r);
            return;
        }
    }

    void DrawRipple(Ripple r)
    {
        double k = Math.Min(1, r.Age / RippleLife);
        if (k >= 1)
        {
            r.El.IsVisible = false;
            return;
        }
        double w = 8 + r.Size * (1 - (1 - k) * (1 - k)), hh = w * 0.28;
        r.El.Width = w;
        r.El.Height = hh;
        r.Tr.X = r.P.X - w / 2;
        r.Tr.Y = r.P.Y - hh / 2;
        r.El.Opacity = 0.8 * (1 - k);
    }

    public override void ThemeChanged()
    {
        var bobber = MakeBobber();
        Layer.Children[Layer.Children.IndexOf(_bobber)] = bobber;
        _bobber = bobber;
        var reel = MakeReel();
        Layer.Children[Layer.Children.IndexOf(_reelSprite)] = reel;
        _reelSprite = reel;
        Draw();
    }

    static IBrush WaterBrush()
    {
        var b = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        };
        b.GradientStops.Add(new GradientStop(Color.FromArgb(120, 80, 165, 215), 0));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(180, 26, 78, 138), 1));
        return b;
    }

    static Color ColorOf(Species s) => s.Id switch
    {
        "carp" => Color.FromRgb(196, 146, 66),
        "pike" => Color.FromRgb(104, 134, 82),
        "catfish" => Color.FromRgb(58, 60, 72),
        "golden" => Color.FromRgb(255, 204, 64),
        _ => Color.FromRgb(126, 164, 88),
    };

    /// <summary>A side-on fish facing +x, centered on the origin.</summary>
    static Sprite MakeFish(Species s, double len)
    {
        var c = ColorOf(s);
        var fill = Art.Brush(c);
        double h = len * (s.Id == "pike" ? 0.26 : s.Id == "catfish" ? 0.3 : 0.38);
        string F(double v) => Art.F(v);
        var sprite = new Sprite { IsHitTestVisible = false };
        var r = sprite.Rotor.Children;
        r.Add(Art.PathOf($"M{F(-len * 0.3)},0 L{F(-len * 0.5)},{F(-h * 0.55)} Q{F(-len * 0.43)},0 {F(-len * 0.5)},{F(h * 0.55)} Z", fill));
        r.Add(Art.PathOf($"M{F(-len * 0.12)},{F(-h * 0.4)} L{F(len * 0.0)},{F(-h * 0.78)} L{F(len * 0.12)},{F(-h * 0.42)} Z", fill));
        r.Add(Art.At(new Ellipse { Width = len * 0.82, Height = h, Fill = fill }, -len * 0.36, -h / 2));
        if (s.Id == "catfish")
            r.Add(Art.PathOf($"M{F(len * 0.42)},{F(-h * 0.1)} Q{F(len * 0.54)},{F(-h * 0.2)} {F(len * 0.58)},{F(h * 0.25)} M{F(len * 0.42)},{F(h * 0.12)} Q{F(len * 0.5)},{F(h * 0.3)} {F(len * 0.5)},{F(h * 0.6)}",
                null, fill, 1));
        r.Add(Art.Circle(len * 0.3, -h * 0.12, Math.Max(1, len * 0.03), Art.Brush(Art.Blend(c, Colors.Black, 0.6))));
        return sprite;
    }

    static Sprite MakeBobber()
    {
        var s = new Sprite { IsHitTestVisible = false };
        s.Rotor.Children.Add(Art.PathOf($"M0,{Art.F(-BobR)} L0,{Art.F(-BobR - 5)}", null, Art.Brush("#E9ECEF"), 1.4));
        s.Rotor.Children.Add(Art.PathOf($"M{Art.F(-BobR)},0 A{Art.F(BobR)},{Art.F(BobR)} 0 0 1 {Art.F(BobR)},0 Z", Art.Brush(Themes.Current.Mine)));
        s.Rotor.Children.Add(Art.PathOf($"M{Art.F(-BobR)},0 A{Art.F(BobR)},{Art.F(BobR)} 0 0 0 {Art.F(BobR)},0 Z", Brushes.White));
        s.Rotor.Children.Add(Art.Circle(0, 0, BobR, null, Art.Brush("#1C1F26"), 1));
        return s;
    }

    static Sprite MakeReel()
    {
        var s = new Sprite { IsHitTestVisible = false };
        var ink = Art.Brush("#1C1F26");
        s.Rotor.Children.Add(Art.Circle(0, 0, 7, Art.Brush(Themes.Current.Mine), ink, 1.2));
        s.Rotor.Children.Add(Art.Circle(0, 0, 2.2, ink));
        s.Rotor.Children.Add(Art.PathOf("M0,0 L0,-9", null, ink, 1.6));
        s.Rotor.Children.Add(Art.Circle(0, -9, 2, Art.Brush("#E9ECEF"), ink, 0.8));
        return s;
    }

    // ------------------------------------------------------------------ demo

    public override void DemoTick()
    {
        if (_demoPause > 0)
        {
            _demoPause--;
            return;
        }
        switch (_state)
        {
            case State.Ready:
                if (Rng.NextDouble() < 0.3) Cast(0.25 + Rng.NextDouble() * 0.75);
                break;
            case State.Waiting:
                if (_baitGone)
                {
                    if (Rng.NextDouble() < 0.3) ReelIn();
                }
                else if (_bite is { Biting: true } && !_demoWillMiss)
                {
                    if (Rng.NextDouble() < 0.6 && Strike()) _reeling = true;
                }
                else if (_suitor == null && _waitT > 9)
                {
                    ReelIn();
                }
                break;
            case State.Fighting:
                _reeling = _fight!.Tension < PulseAt; // pulse: reel until the bar turns yellow
                break;
        }
    }
}
