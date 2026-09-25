using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using DeskArcade.Engine;
using static DeskArcade.Games.SheepRules;

namespace DeskArcade.Games;

/// <summary>
/// Sheep Herding (see <see cref="SheepRules"/>): a flock grazes on your window tops and along the taskbar, and the
/// cursor is the sheepdog. Sheep run from the dog, and a scared sheep sets its neighbours off, so the flock can be
/// driven; they hop off the ends of window tops and jump up onto low ones as they flee. Get every sheep into the pen
/// by the screen's edge before the clock runs out: ten points a sheep, and five for every second left once they are
/// all in. Click to bark, which scares the sheep nearby harder (the dog needs a moment between barks), and watch for
/// the stray that bolts. Click the pen to start; each round cleared has a bigger flock and a shorter clock. A round is
/// a race against the computer or a co-worker. Only the pen and the sheep (and, mid-round, the ground around them)
/// take the mouse.
/// </summary>
public sealed class SheepGame : MiniGame
{
    const double FenceH = 34, PostEvery = 22, GateW = 30, HitReach = 80, SheepHit = 22, DogLift = 14;
    const double BannerIn = 0.45, BannerHold = 2.4, BannerOut = 0.5;

    /// <summary>A fair round for a decent player: five sheep in with about twenty-five seconds to spare.</summary>
    public const int FairRound = 180;
    /// <summary>About how long such a round lasts, in seconds.</summary>
    public const double TypicalRoundSeconds = 65;

    static readonly Color Gold = Color.FromRgb(255, 209, 102);
    static readonly Color Wool = Color.FromRgb(246, 243, 236);
    static readonly Color WoolShade = Color.FromRgb(200, 192, 178);
    static readonly Color Face = Color.FromRgb(58, 50, 46);
    static readonly Color Wood = Color.FromRgb(139, 94, 52);
    static readonly Color Straw = Color.FromRgb(232, 205, 120);
    static readonly Color Coat = Color.FromRgb(30, 30, 36);
    static readonly Color Alarm = Color.FromRgb(255, 92, 92);

    enum Phase { Ready, Playing, Result }

    sealed class Look
    {
        public required Canvas Holder;
        public required ScaleTransform Flip, Stretch;
        public required TranslateTransform Move, Head;
        public required Control LegsA, LegsB, Alarm;
    }

    readonly Canvas _penLayer = new() { IsHitTestVisible = false };
    readonly Canvas _flockLayer = new() { IsHitTestVisible = false };
    readonly Canvas _dogLayer = new() { IsHitTestVisible = false };
    readonly Canvas _banners = new() { IsHitTestVisible = false };
    readonly Dictionary<int, Look> _looks = new();

    SheepRules? _rules;
    Phase _phase;
    int _round = 1, _seenGen = -1, _shownSecond = -1;
    bool _passed, _racing, _penRight = true, _drawnColorBlind;
    double _time, _baaIn, _resultFor, _dogFace = 1, _dogStep;
    Rect _arenaAtRound;
    Vec2 _lastPointer;
    List<Ledge> _windows = new();
    Sprite? _dog;
    Control? _dogLegsA, _dogLegsB, _dogMouth;

    public SheepGame(IGameHost host) : base(host)
    {
        Layer.Children.Add(_penLayer);
        Layer.Children.Add(_flockLayer);
        Layer.Children.Add(_dogLayer);
        Layer.Children.Add(_banners);
        BuildDog();
    }

    public override string Id => "sheep";
    public override string Title => "Sheep Herding";

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        var holder = new Canvas { RenderTransform = new ScaleTransform(0.62, 0.62) };
        DrawSheep(holder, out _, out _, out _, out _);
        s.Rotor.Children.Add(Art.At(holder, -2, 9));
        return s;
    }

    public override HudInfo Hud
    {
        get
        {
            var r = _rules;
            string line = r == null ? "" : _phase switch
            {
                Phase.Ready => L.F("Round {0} · {1} sheep · click the pen to start", _round, r.Total),
                Phase.Playing => L.F("Round {0} · penned {1}/{2} · {3}s left", _round, r.Penned, r.Total, (int)Math.Ceiling(r.TimeLeft)),
                _ => _passed ? L.F("Round {0} done · click the pen for the next one", _round)
                    : L.F("{0} of {1} penned · click the pen to try again", r.Penned, r.Total),
            };
            return new HudInfo((r?.Score ?? 0).ToString(CultureInfo.InvariantCulture), line, L.F("Best {0}", Host.Stats.Get("sheep.best")));
        }
    }

    // ------------------------------------------------------------------ races

    public override bool SupportsLan => true;
    public override (int Score, bool Active)? Race => (_rules?.Score ?? 0, _racing);
    public override int RaceBaseline => FairRound;
    public override int RaceBest => (int)Host.Stats.Get("sheep.best");
    public override double RaceSeconds => TypicalRoundSeconds;
    public override int RaceMax => _rules is { } r ? RoundScore(r.Total, true, r.Seconds) : int.MaxValue;

    /// <summary>The rival's round began: this one starts too (the next round, after a result).</summary>
    public override void StartRace()
    {
        if (_racing) return;
        Start();
    }

    void EndRace(int score)
    {
        if (!_racing) return;
        _racing = false;
        Host.RoundEnded(score);
    }

    // ------------------------------------------------------------------ rounds

    public override void Layout()
    {
        var a = Host.Arena;
        // a new round when there is none, or the screen changed size under this one
        if (_rules == null || Math.Abs(a.Width - _arenaAtRound.Width) > 2 || Math.Abs(a.Bottom - _arenaAtRound.Bottom) > 2) NewRound();
        else if (_drawnColorBlind != Art.ColorBlind) ThemeChanged(); // the overlay calls Layout when colour-blind mode is toggled
        else
        {
            DrawPen();
            DrawFlock();
        }
        Host.HudChanged();
    }

    public override void Deactivate()
    {
        Anims.Finish();
        if (_dog != null) _dog.IsVisible = false;
    }

    public override void ThemeChanged()
    {
        _drawnColorBlind = Art.ColorBlind;
        BuildDog();
        if (_rules == null) return;
        foreach (var look in _looks.Values) _flockLayer.Children.Remove(look.Holder);
        _looks.Clear();
        DrawPen();
        DrawFlock();
    }

    /// <summary>
    /// Lays out round <see cref="_round"/>: the pen against the left or right edge of the floor with its gate facing
    /// the middle, clear of the scoreboard, and the flock scattered over window tops and the floor away from it. A
    /// round given up partway counts for the race with what it had.
    /// </summary>
    void NewRound()
    {
        if (_rules is { } old) EndRace(old.Score);
        var a = Host.Arena;
        _arenaAtRound = a;
        int n = FlockSize(_round);
        double w = PenWidth(n);
        var hud = Host.HudBounds.Inflate(10);
        Rect PenAt(bool right) => new(right ? a.Right - w - 4 : a.Left + 4, a.Bottom - FenceH - 40, w, FenceH + 40);
        bool right = _penRight;
        if (PenAt(right).Intersects(hud) && !PenAt(!right).Intersects(hud)) right = !right;
        double x1 = PenAt(right).X;
        var r = new SheepRules(a.Left, a.Right, a.Bottom, x1, x1 + w, gateLeft: right, RoundSeconds(_round), Rng);
        _rules = r;

        var tops = Host.Platforms.Items.Where(p => p.X2 - p.X1 >= 70 && p.Y > a.Top + 60 && p.Y < a.Bottom - 50 &&
            !new Rect(p.X1, p.Y - 40, p.X2 - p.X1, 40).Intersects(hud)).ToList();
        for (int i = 0; i < n; i++)
        {
            if (tops.Count > 0 && Rng.NextDouble() < 0.55)
            {
                var p = tops[Rng.Next(tops.Count)];
                r.Add(p.X1 + 20 + Rng.NextDouble() * (p.X2 - p.X1 - 40), p.Y, p.Hwnd);
                continue;
            }
            double x = 0;
            for (int tries = 0; tries < 12; tries++)
            {
                x = a.Left + 30 + Rng.NextDouble() * Math.Max(1, a.Width - 60);
                if (Math.Abs(x - r.PenMid) > w / 2 + 140) break;
            }
            r.Add(x, a.Bottom);
        }

        _phase = Phase.Ready;
        _passed = false;
        _shownSecond = -1;
        _resultFor = 0;
        _seenGen = Host.Platforms.Generation;
        _windows = Windows();
        _drawnColorBlind = Art.ColorBlind;
        foreach (var look in _looks.Values) _flockLayer.Children.Remove(look.Holder);
        _looks.Clear();
        DrawPen();
        DrawFlock();
        Host.HudChanged();
        Host.Wake();
    }

    void Start()
    {
        if (_rules == null) return;
        if (_phase == Phase.Result)
        {
            if (_passed) _round++;
            NewRound();
        }
        if (_phase != Phase.Ready || _rules is not { } r) return;
        _phase = Phase.Playing;
        r.Running = true;
        _racing = true;
        _baaIn = 1.5;
        _lastPointer = Host.Pointer;
        Host.RoundStarted();
        Banner(L.T("GO!"), L.F("Round {0} · {1} sheep · {2} seconds", _round, r.Total, (int)r.Seconds), Gold);
        Host.Sound.Play("whoosh", 0.35, 0.9);
        Baa(r.Flock[Rng.Next(r.Total)], 0.4);
        DrawPen();
        Host.HudChanged();
        Host.Wake();
    }

    void EndRound()
    {
        var r = _rules!;
        _phase = Phase.Result;
        r.Running = false;
        _passed = r.AllIn;
        int score = r.Score, spare = (int)Math.Ceiling(r.TimeLeft);
        bool best = score > Host.Stats.Get("sheep.best");
        Host.Stats.Max("sheep.best", score);
        var at = new Vec2(Host.Arena.Center.X, Host.Arena.Top + Host.Arena.Height * 0.2);
        if (_passed)
        {
            Host.Stats.Max("sheep.round", _round);
            Host.Stats.Max("sheep.spare", spare);
            Banner(best ? L.T("NEW BEST!") : L.T("ALL PENNED!"), L.F("{0} points · {1}s to spare", score, spare), Gold);
            Host.Fx.Burst(at, Themes.Current.Confetti, 40, 520, 700, 7, 1.0);
            Host.Sound.Play("best", 0.8);
        }
        else
        {
            Banner(best ? L.T("NEW BEST!") : L.T("TIME!"), L.F("{0} of {1} penned · {2} points", r.Penned, r.Total, score), best ? Gold : Colors.White);
            Host.Sound.Play("buzzer", 0.4);
        }
        EndRace(score);
        if (_dog != null) _dog.IsVisible = false;
        DrawPen();
        Host.HudChanged();
    }

    // ------------------------------------------------------------------ frame

    public override bool Update(double dt)
    {
        if (_rules is not { } r) return false;
        _time += dt;
        bool windowsMoved = FollowWindows(r);
        bool anim = Anims.Update(dt);
        dt = Math.Min(dt, 0.05);
        if (_phase != Phase.Playing)
        {
            if (_phase == Phase.Result) _resultFor += dt;
            // between rounds the flock stands still; it only settles when a window under it moves or goes
            if (!windowsMoved && !r.Moving) return anim;
            Handle(r.Step(dt, null, _windows));
            DrawFlock();
            return true;
        }

        var p = Host.Pointer;
        Handle(r.Step(dt, (p.X, p.Y), _windows));
        DrawFlock();
        DrawDog(p, dt);
        if ((_baaIn -= dt) <= 0)
        {
            _baaIn = 3 + Rng.NextDouble() * 4;
            var free = r.Flock.Where(s => s.Free).ToList();
            if (free.Count > 0) Baa(free[Rng.Next(free.Count)], 0.2);
        }
        int second = (int)Math.Ceiling(r.TimeLeft);
        if (second != _shownSecond)
        {
            _shownSecond = second;
            if (second is > 0 and <= 5) Host.Sound.Play("thunk", 0.2, 1.6); // the last seconds tick
            Host.HudChanged();
        }
        if (r.Over) EndRound();
        return true;
    }

    /// <summary>Carries the sheep on a window that moved; true when the window tops changed at all.</summary>
    bool FollowWindows(SheepRules r)
    {
        var plats = Host.Platforms;
        if (plats.Generation == _seenGen) return false;
        _seenGen = plats.Generation;
        foreach (var h in plats.Items.Select(p => p.Hwnd).Distinct())
        {
            var d = plats.DeltaOf(h);
            if (d != default) r.Carry(h, d.X, d.Y);
        }
        _windows = Windows();
        return true;
    }

    List<Ledge> Windows() => Host.Platforms.Items.Select(p => new Ledge(p.Y, p.X1, p.X2, p.Hwnd)).ToList();

    void Handle(List<(Sheep Sheep, Event Event)> events)
    {
        foreach (var (s, e) in events)
        {
            var at = new Vec2(s.X, s.Y - BodyLift);
            switch (e)
            {
                case Event.Penned:
                    Host.Stats.Add("sheep.penned");
                    Host.ShareAction(at, PerSheep);
                    Host.Fx.Popup(at - new Vec2(0, 28), $"+{PerSheep}", Gold, 18, 0.8);
                    Host.Fx.Burst(new Vec2(s.X, s.Y - 4), new[] { Straw, Wool }, 8, 160, 500, 4, 0.5);
                    Baa(s, 0.45);
                    DrawPen();
                    Host.HudChanged();
                    break;
                case Event.Bolted:
                    Host.Fx.Popup(at - new Vec2(0, 30), "!", Alarm, 22, 0.8);
                    Baa(s, 0.4);
                    break;
                case Event.Jumped:
                    if (!Fx.ReducedMotion && _looks.TryGetValue(s.Id, out var look)) Boing(look);
                    break;
                case Event.Landed:
                    Host.Sound.Play("thunk", 0.15, 1.4);
                    break;
            }
        }
    }

    void Baa(Sheep s, double vol) => Host.Sound.Play(s.Id % 3 == 2 ? "baa2" : "baa", vol, 0.92 + (s.Id % 5) * 0.04 + Rng.NextDouble() * 0.06);

    // ------------------------------------------------------------------ input

    Rect PenBox => _rules is { } r ? new Rect(r.PenX1 - 6, r.Floor - FenceH - 34, r.PenX2 - r.PenX1 + 12, FenceH + 36) : default;

    public override void CollectHitShapes(List<HitShape> into)
    {
        if (_rules is not { } r) return;
        into.Add(HitShape.Box(PenBox));
        foreach (var s in r.Flock)
        {
            if (_phase == Phase.Playing && !s.Free) continue;
            into.Add(HitShape.Circle(new Vec2(s.X, s.Y - BodyLift), _phase == Phase.Playing ? HitReach : SheepHit));
        }
    }

    public override bool PointerDown(Vec2 p, bool right)
    {
        if (_rules == null) return false;
        if (_phase == Phase.Playing) Bark(p);
        else Start(); // the pen or a sheep: the round begins
        return false;
    }

    /// <summary>Woof: the sheep near the dog run for it. Nothing while the dog gets its breath back.</summary>
    void Bark(Vec2 p)
    {
        var r = _rules!;
        if (!r.TryBark(p.X, p.Y, out int scared)) return;
        Host.Stats.Add("sheep.barks");
        Host.Sound.Play("bark1", 0.55, 1.05);
        Host.Fx.Marker(p, Gold, 16, BarkRadius * 2, 0.45);
        Host.Fx.Popup(p - new Vec2(0, 40), L.T("Woof!"), Colors.White, 18, 0.6);
        if (_dogMouth is { } mouth)
        {
            mouth.IsVisible = true;
            Anims.After(0.3, () => mouth.IsVisible = false);
        }
        if (scared > 0)
        {
            var near = r.Flock.Where(s => s.Free && s.Panic > 0).ToList();
            Baa(near[Rng.Next(near.Count)], 0.35);
        }
    }

    /// <summary>Moves the pen to the side of the screen nearer the cursor (a new flock too), between rounds.</summary>
    public override void Summon(Vec2 p)
    {
        if (_phase == Phase.Playing) return;
        _penRight = p.X > Host.Arena.Center.X;
        NewRound();
    }

    // ------------------------------------------------------------------ demo

    /// <summary>Barks from behind the sheep farthest from the pen, so it runs toward the gate.</summary>
    public override void DemoTick()
    {
        if (_rules is not { } r) return;
        if (_phase != Phase.Playing)
        {
            if (_phase == Phase.Ready || _resultFor > 3) Start();
            return;
        }
        if (!r.BarkReady) return;
        var s = r.Flock.Where(x => x.Free && x.State != State.Airborne).OrderByDescending(x => Math.Abs(x.X - r.PenMid)).FirstOrDefault();
        if (s == null) return;
        Bark(new Vec2(s.X - Math.Sign(r.PenMid - s.X) * 40, s.Y - BodyLift));
    }

    // ------------------------------------------------------------------ motion

    /// <summary>A jumping sheep stretches as it takes off and settles back.</summary>
    void Boing(Look look)
    {
        Anims.Add(0.35, k =>
        {
            double p = Ease.Pulse(k);
            look.Stretch.ScaleY = 1 + 0.18 * p;
            look.Stretch.ScaleX = 1 - 0.1 * p;
        }, Ease.Linear);
    }

    /// <summary>A banner across the top of the screen: it springs in, stays a while and fades.</summary>
    void Banner(string title, string sub, Color color)
    {
        var a = Host.Arena;
        var panel = new StackPanel { IsHitTestVisible = false };
        panel.Children.Add(new TextBlock
        {
            Text = title, FontFamily = Fx.Font, FontSize = 34, FontWeight = FontWeight.Black, Foreground = Art.Brush(Art.Safe(Themes.Themed(color))),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = sub, FontFamily = Fx.Font, FontSize = 15, FontWeight = FontWeight.Bold, Foreground = Art.Brush("#E6EAF2"),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        var scale = new ScaleTransform(Fx.ReducedMotion ? 1 : 0.6, Fx.ReducedMotion ? 1 : 0.6);
        var border = new Border
        {
            Background = Art.Brush(235, 20, 24, 34), BorderBrush = Art.Brush(Art.Safe(Themes.Themed(color))), BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(14), Padding = new Thickness(28, 12, 28, 14), Child = panel, IsHitTestVisible = false,
            RenderTransformOrigin = RelativePoint.Center, RenderTransform = scale, Opacity = 0,
        };
        border.Measure(Size.Infinity);
        var ds = border.DesiredSize;
        _banners.Children.Add(Art.At(border, a.Center.X - ds.Width / 2, a.Top + a.Height * 0.2 - ds.Height / 2));
        Anims.Add(BannerIn, k =>
        {
            border.Opacity = Math.Min(1, k * 1.5);
            if (!Fx.ReducedMotion) scale.ScaleX = scale.ScaleY = 0.6 + 0.4 * k;
        }, Ease.OutBack);
        Anims.Add(BannerOut, k =>
        {
            border.Opacity = 1 - k;
            if (!Fx.ReducedMotion) scale.ScaleX = scale.ScaleY = 1 + 0.1 * k;
        }, Ease.InQuad, () => _banners.Children.Remove(border), BannerHold);
        Host.Wake();
    }

    // ------------------------------------------------------------------ drawing

    /// <summary>
    /// The pen, behind the flock: straw on the ground, a back fence with two rails, a post at each end and the gate
    /// swung open on the side facing the middle; a count and, between rounds, what a click does.
    /// </summary>
    void DrawPen()
    {
        _penLayer.Children.Clear();
        if (_rules is not { } r) return;
        var th = Themes.Current;
        var wood = th.BoardFrame is { } frame && Luma(frame) > 40 ? frame : Wood;
        var woodBrush = Art.Brush(wood);
        var dark = Art.Brush(Art.Blend(wood, Colors.Black, 0.35));
        double x1 = r.PenX1, x2 = r.PenX2, y = r.Floor;
        _penLayer.Children.Add(Art.At(new Rectangle { Width = x2 - x1, Height = 6, RadiusX = 3, RadiusY = 3, Fill = Art.Brush(Straw) }, x1, y - 5));
        foreach (double ry in new[] { FenceH * 0.4, FenceH * 0.8 })
            _penLayer.Children.Add(Art.At(new Rectangle { Width = x2 - x1, Height = 4, RadiusX = 1.5, RadiusY = 1.5, Fill = woodBrush, Stroke = dark, StrokeThickness = 0.8, Opacity = 0.9 }, x1, y - ry - 2));
        int posts = Math.Max(2, (int)Math.Round((x2 - x1) / PostEvery));
        for (int i = 0; i <= posts; i++)
        {
            double px = x1 + (x2 - x1) * i / posts;
            bool end = i == 0 || i == posts;
            double pw = end ? 6 : 4, ph = end ? FenceH + 6 : FenceH;
            _penLayer.Children.Add(Art.At(new Rectangle { Width = pw, Height = ph, RadiusX = 1.5, RadiusY = 1.5, Fill = woodBrush, Stroke = dark, StrokeThickness = 0.8 }, px - pw / 2, y - ph));
        }
        // the gate, swung open outward from its post; shut once the whole flock is in
        double gx = r.GateLeft ? x1 : x2, dir = r.GateLeft ? -1 : 1;
        double reach = r.AllIn ? 0 : GateW;
        string F(double v) => Art.F(v);
        string gate = r.AllIn
            ? $"M{F(gx)},{F(y - FenceH * 0.4)} L{F(gx - dir * 3)},{F(y - FenceH * 0.4)} M{F(gx)},{F(y - FenceH * 0.8)} L{F(gx - dir * 3)},{F(y - FenceH * 0.8)}"
            : $"M{F(gx)},{F(y - FenceH * 0.8)} L{F(gx + dir * reach)},{F(y - FenceH * 0.72)} L{F(gx + dir * reach)},{F(y - FenceH * 0.1)} L{F(gx)},{F(y - FenceH * 0.25)} " +
              $"M{F(gx)},{F(y - FenceH * 0.52)} L{F(gx + dir * reach)},{F(y - FenceH * 0.42)} M{F(gx + dir * reach * 0.5)},{F(y - FenceH * 0.76)} L{F(gx + dir * reach * 0.5)},{F(y - FenceH * 0.17)}";
        _penLayer.Children.Add(Art.PathOf(gate, null, woodBrush, 3.2));
        // a little flag in the theme's accent on the closed end
        double fx = r.GateLeft ? x2 - 3 : x1 + 3, fy = y - FenceH - 6;
        _penLayer.Children.Add(Art.PathOf($"M{F(fx)},{F(fy)} L{F(fx)},{F(fy - 16)}", null, dark, 1.5));
        _penLayer.Children.Add(Art.PathOf($"M{F(fx)},{F(fy - 16)} L{F(fx - dir * 13)},{F(fy - 12)} L{F(fx)},{F(fy - 8)} Z", Art.Brush(th.Accent)));

        // the count sits on a dark plate so it reads over any wallpaper
        var count = new Border
        {
            Background = Art.Brush(200, 18, 20, 28), CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 0, 7, 1), IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = $"{r.Penned}/{r.Total}", FontFamily = Fx.Font, FontSize = 14, FontWeight = FontWeight.Black,
                Foreground = Art.Brush(r.AllIn ? th.Gold : Colors.White),
            },
        };
        count.Measure(Size.Infinity);
        _penLayer.Children.Add(Art.At(count, (x1 + x2) / 2 - count.DesiredSize.Width / 2, y - FenceH - 26));
        if (_phase == Phase.Playing) return;
        var prompt = new TextBlock
        {
            Text = _phase == Phase.Ready ? L.T("click the pen to start") : _passed ? L.T("click for the next round") : L.T("click to try again"),
            FontFamily = Fx.Font, FontSize = 12, FontWeight = FontWeight.Bold, Foreground = Art.Brush(th.Gold),
        };
        prompt.Measure(Size.Infinity);
        double tx = Clamp((x1 + x2) / 2 - prompt.DesiredSize.Width / 2, Host.Arena.Left + 6, Host.Arena.Right - prompt.DesiredSize.Width - 6);
        _penLayer.Children.Add(Art.At(prompt, tx, y - FenceH - 44));
    }

    static double Luma(Color c) => 0.3 * c.R + 0.59 * c.G + 0.11 * c.B;

    void DrawFlock()
    {
        if (_rules is not { } r) return;
        foreach (var s in r.Flock)
        {
            if (!_looks.TryGetValue(s.Id, out var look)) _looks[s.Id] = look = NewLook();
            bool moving = s.State is State.Walking or State.Fleeing;
            double bob = moving && !Fx.ReducedMotion && s.State == State.Fleeing ? -Math.Abs(Math.Sin(_time * 16 + s.Id)) * 3 : 0;
            look.Move.X = s.X;
            look.Move.Y = s.Y + bob;
            look.Flip.ScaleX = s.Dir < 0 ? -1 : 1;
            bool step = moving && (int)((_time + s.Id * 0.13) * (s.State == State.Fleeing ? 12 : 5)) % 2 == 1;
            look.LegsA.IsVisible = !step && s.State != State.Airborne;
            look.LegsB.IsVisible = step || s.State == State.Airborne;
            // head down to graze; up and alert otherwise
            look.Head.X = s.State == State.Grazing ? 2 : 0;
            look.Head.Y = s.State == State.Grazing ? 6 : 0;
            look.Alarm.IsVisible = s.Free && s.Panic > 0;
        }
    }

    Look NewLook()
    {
        var holder = new Canvas { IsHitTestVisible = false };
        var body = new Canvas();
        DrawSheep(body, out var legsA, out var legsB, out var head, out var alarm);
        var stretch = new ScaleTransform(1, 1);
        body.RenderTransform = stretch; // around the feet at the origin
        body.RenderTransformOrigin = RelativePoint.TopLeft;
        holder.Children.Add(body);
        var flip = new ScaleTransform(1, 1);
        var move = new TranslateTransform();
        holder.RenderTransform = new TransformGroup { Children = { flip, move } };
        _flockLayer.Children.Add(holder);
        return new Look { Holder = holder, Flip = flip, Stretch = stretch, Move = move, Head = head, LegsA = legsA, LegsB = legsB, Alarm = alarm };
    }

    /// <summary>A sheep around its feet at (0, 0), facing right: a cloud of wool, a dark face, a bell in the theme's accent.</summary>
    static void DrawSheep(Canvas into, out Control legsA, out Control legsB, out TranslateTransform headMove, out Control alarm)
    {
        var face = Art.Brush(Face);
        legsA = Art.PathOf("M-8,-8 L-8,0 M-4,-8 L-4,0 M5,-8 L5,0 M9,-8 L9,0", null, face, 2.4);
        legsB = Art.PathOf("M-8,-8 L-11,-1 M-4,-8 L-1,-1 M5,-8 L2,-1 M9,-8 L12,-1", null, face, 2.4);
        legsB.IsVisible = false;
        into.Children.Add(legsA);
        into.Children.Add(legsB);
        var wool = Art.Brush(Wool);
        var shade = Art.Brush(WoolShade);
        into.Children.Add(Art.At(new Ellipse { Width = 30, Height = 18, Fill = wool, Stroke = shade, StrokeThickness = 1 }, -15, -23));
        foreach (var (x, y, rr) in new[] { (-12.0, -17.0, 5.5), (-6.0, -22.0, 6.0), (1.0, -23.0, 6.0), (8.0, -21.0, 5.5), (12.0, -15.0, 4.5), (-15.0, -13.0, 3.5) })
            into.Children.Add(Art.Circle(x, y, rr, wool, shade, 0.8));
        into.Children.Add(Art.At(new Ellipse { Width = 26, Height = 12, Fill = wool }, -13, -21)); // hides the puffs' inner edges

        var head = new Canvas();
        headMove = new TranslateTransform();
        head.RenderTransform = headMove;
        head.Children.Add(Art.At(new Ellipse { Width = 7, Height = 3.5, Fill = face, RenderTransform = new RotateTransform(-25) }, 10, -21));
        head.Children.Add(Art.At(new Ellipse { Width = 10, Height = 13, Fill = face }, 13, -23));
        head.Children.Add(Art.Circle(16, -23, 3.5, wool, shade, 0.6)); // the topknot
        head.Children.Add(Art.Circle(19.5, -18, 1.5, Brushes.White));
        head.Children.Add(Art.Circle(20, -18, 0.7, Art.Brush("#111111")));
        into.Children.Add(head);
        into.Children.Add(Art.Circle(13, -10, 2.2, Art.Brush(Themes.Current.Accent), Art.Brush(80, 0, 0, 0), 0.6));

        alarm = Art.At(new TextBlock
        {
            Text = "!", FontFamily = Fx.Font, FontSize = 15, FontWeight = FontWeight.Black, Foreground = Art.Brush(Art.Safe(Alarm)), IsVisible = false,
        }, -2, -48);
        into.Children.Add(alarm);
    }

    // ------------------------------------------------------------------ the dog

    /// <summary>The sheepdog at the cursor: a black-and-white collie with a bandana in the theme's own colour.</summary>
    void BuildDog()
    {
        _dogLayer.Children.Clear();
        var dog = new Sprite { IsHitTestVisible = false, IsVisible = _phase == Phase.Playing };
        var c = dog.Rotor.Children;
        var coat = Art.Brush(Coat);
        var white = Brushes.White;
        _dogLegsA = Art.PathOf("M-9,3 L-10,12 M-5,3 L-4,12 M6,3 L5,12 M10,3 L11,12", null, coat, 2.6);
        _dogLegsB = Art.PathOf("M-9,3 L-13,11 M-5,3 L-1,11 M6,3 L2,11 M10,3 L14,11", null, coat, 2.6);
        _dogLegsB.IsVisible = false;
        c.Add(_dogLegsA);
        c.Add(_dogLegsB);
        c.Add(Art.PathOf("M-12,-2 Q-20,-4 -21,-12", null, coat, 4));
        c.Add(Art.Circle(-21, -12, 2.4, white));
        c.Add(Art.At(new Ellipse { Width = 28, Height = 13, Fill = coat }, -14, -6));
        c.Add(Art.At(new Ellipse { Width = 10, Height = 10, Fill = white }, 6, -3));
        c.Add(Art.Circle(15, -8, 7, coat));
        c.Add(Art.PathOf("M15,-15 L16,-6 L21,-4 L22,-7 Z", white)); // the white blaze down the face
        c.Add(Art.PathOf("M9,-13 L11,-20 L15,-14 Z", coat));
        c.Add(Art.Circle(17.5, -10, 1.3, Art.Brush("#111111")));
        c.Add(Art.Circle(22.5, -6.5, 1.6, Art.Brush("#111111")));
        c.Add(Art.PathOf("M9,-3 L18,-2 L13,4 Z", Art.Brush(Themes.Current.Mine), Art.Brush(80, 0, 0, 0), 0.6));
        _dogMouth = Art.PathOf("M18,-3 Q22,1 24,-3 Z", Art.Brush("#D94B5B"));
        _dogMouth.IsVisible = false;
        c.Add(_dogMouth);
        _dog = dog;
        _dogLayer.Children.Add(dog);
    }

    /// <summary>The dog runs along with the pointer, facing the way it goes.</summary>
    void DrawDog(Vec2 p, double dt)
    {
        if (_dog is not { } dog) return;
        dog.IsVisible = true;
        double dx = p.X - _lastPointer.X, speed = (p - _lastPointer).Length / Math.Max(dt, 1e-3);
        _lastPointer = p;
        if (Math.Abs(dx) > 0.5) _dogFace = Math.Sign(dx);
        bool running = speed > 40;
        if (running) _dogStep += dt * 12;
        bool step = running && (int)_dogStep % 2 == 1;
        if (_dogLegsA != null) _dogLegsA.IsVisible = !step;
        if (_dogLegsB != null) _dogLegsB.IsVisible = step;
        dog.Set(new Vec2(p.X, p.Y + DogLift));
        dog.FlipX = _dogFace;
    }
}
