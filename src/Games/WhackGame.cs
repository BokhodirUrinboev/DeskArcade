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
/// Whack-a-Bug: bugs peek over the tops of your windows, up from the taskbar and in from the sides of the
/// screen for a moment. Whack them before they duck back; quick hits build a combo. Ladybugs are features.
/// </summary>
public sealed class WhackGame : MiniGame, IPetPlayground
{
    /// <summary>A round is 30 seconds.</summary>
    public const double RoundSeconds = 30;
    /// <summary>A fair round for a decent player: two dozen whacks with a few combos among them.</summary>
    public const int FairRound = 50;
    const double Step = 1.0 / 240, Slide = 0.15, StayStart = 1.1, StayEnd = 0.5, Settle = 0.12;
    const double ComboWindow = 0.8, Depth = 60, HitR = 22, SquashTime = 0.3, SpinOffTime = 0.55, StarGravity = 700;
    const double MeterW = 84, MeterH = 14;
    const int MaxCombo = 3, MaxVisible = 3, LadybugPenalty = 5;

    static readonly Color Gold = Color.FromRgb(255, 209, 102);
    static readonly IBrush[] StarFills = { Art.Brush("#FFD166"), Art.Brush("#FFF0AA"), Art.Brush("#FFFFFF") };
    static readonly IBrush StarStroke = Art.Brush("#B7791F");

    enum Kind { Normal, Fast, Golden, Ladybug }
    enum Edge { Window, Taskbar, Left, Right }

    sealed class Bug
    {
        public required Canvas Holder; // clipped at the edge, so the part still behind it stays hidden
        public required Sprite Sprite;
        public required ScaleTransform Squash;
        public required Control EyesOpen;
        public required Control EyesDizzy;
        public required Control EyesAsleep;
        public Control? Zzz;
        public Kind Kind;
        public Edge Edge;
        public Vec2 Anchor; // where the bug's base meets the edge
        public Vec2 Out;    // unit vector pointing out of the edge, toward the visible side
        public Rect ClipBox;
        public IntPtr Hwnd;
        public int SeenGen = -1;
        public double T, Stay, WhackT = -1, WhackFrac;
        public bool Sleeping, Leaving, Flying;
    }

    sealed class Spark
    {
        public required Sprite Sprite;
        public Vec2 Pos, Vel;
        public double Age, Life, Spin, Angle;
    }

    readonly Canvas _bugLayer = new() { IsHitTestVisible = false };
    readonly Canvas _sparkLayer = new() { IsHitTestVisible = false };
    readonly List<Bug> _bugs = new();
    readonly List<Spark> _sparks = new();
    readonly Canvas _meter = new() { IsHitTestVisible = false, IsVisible = false, Opacity = 0 };
    readonly Rectangle[] _meterCells = new Rectangle[MaxCombo];
    readonly ScaleTransform _meterScale = new();
    Anims.Tween? _meterTween, _meterFade;

    bool _active;
    double _acc, _time, _roundLeft, _spawnIn, _lastHit = -10, _lastSound = -10, _sleeperIn = -1, _meterLevel;
    int _score, _hits, _combo, _shownSecond = -1;

    public WhackGame(IGameHost host) : base(host)
    {
        Layer.Children.Add(_bugLayer);
        Layer.Children.Add(_sparkLayer);
        BuildMeter();
        Layer.Children.Add(_meter);
    }

    public override string Id => "whack";
    public override string Title => "Whack-a-Bug";

    public override Sprite CreateIcon()
    {
        var bug = MakeBug(Kind.Normal, out _, out _, out _, out _);
        bug.Scale = 0.36;
        bug.Set(new Vec2(0, 9.5)); // the art stands on its base; center it on the origin
        var icon = new Sprite();
        icon.Children.Add(bug);
        return icon;
    }

    int SecondsLeft => (int)Math.Ceiling(Math.Max(0, _roundLeft));
    double Progress => Math.Clamp(1 - _roundLeft / RoundSeconds, 0, 1);

    public override HudInfo Hud => new(
        _score.ToString(),
        _active ? L.F("{0}s left · whacked {1}", SecondsLeft, _hits) : L.T("Whack the sleepy bug to start · spare the ladybugs"),
        L.F("Best {0}", Host.Stats.Get("whack.best")));

    // ------------------------------------------------------------------ round flow

    public override void Layout()
    {
        var a = Host.Arena;
        foreach (var b in _bugs)
        {
            switch (b.Edge)
            {
                case Edge.Taskbar: b.Anchor = new Vec2(Clamp(b.Anchor.X, a.Left + 30, a.Right - 30), a.Bottom); break;
                case Edge.Left: b.Anchor = new Vec2(a.Left, Clamp(b.Anchor.Y, a.Top + 30, a.Bottom - 30)); break;
                case Edge.Right: b.Anchor = new Vec2(a.Right, Clamp(b.Anchor.Y, a.Top + 30, a.Bottom - 30)); break;
                default: b.Anchor.X = Clamp(b.Anchor.X, a.Left + 30, a.Right - 30); break;
            }
            UpdateClip(b);
            Draw(b);
        }
        if (!_active && _sleeperIn < 0 && !_bugs.Any(b => b.Sleeping)) SpawnSleeper();
        PlaceMeter();
        Host.HudChanged();
    }

    public override void Deactivate()
    {
        Anims.Clear();
        foreach (var b in _bugs) _bugLayer.Children.Remove(b.Holder);
        _bugs.Clear();
        foreach (var s in _sparks) _sparkLayer.Children.Remove(s.Sprite);
        _sparks.Clear();
        _active = false; // an unfinished round is dropped; the next activation starts from the sleeper
        _sleeperIn = -1;
        _combo = 0;
        _acc = 0;
        _meterTween = _meterFade = null;
        _meterLevel = 0;
        _meter.IsVisible = false;
        _meter.Opacity = 0;
    }

    void SpawnSleeper()
    {
        var a = Host.Arena;
        var hud = Host.HudBounds.Inflate(12);
        double x = a.Left + a.Width * 0.35;
        foreach (double f in new[] { 0.35, 0.2, 0.5, 0.65 })
        {
            x = a.Left + a.Width * f;
            if (!Footprint(new Vec2(x, a.Bottom), DirOf(Edge.Taskbar)).Intersects(hud)) break;
        }
        var bug = AddBug(Kind.Normal, Edge.Taskbar, new Vec2(x, a.Bottom), IntPtr.Zero);
        bug.Sleeping = true;
        bug.EyesOpen.IsVisible = false;
        bug.EyesAsleep.IsVisible = true;
        // drawn as strokes rather than text: it is a picture of snoring, not a word
        bug.Zzz = Art.PathOf("M18,-60 L25,-60 L18,-53 L25,-53 M28,-72 L33,-72 L28,-67 L33,-67", null, Art.Brush(210, 230, 235, 245), 1.8);
        bug.Sprite.Children.Add(bug.Zzz);
    }

    public override bool SupportsLan => true;
    public override (int Score, bool Active)? Race => (_score, _active);
    public override int RaceBaseline => FairRound;
    public override int RaceBest => (int)Host.Stats.Get("whack.best");
    public override double RaceSeconds => RoundSeconds;

    public override void StartRace()
    {
        if (!_active) StartRound();
    }

    void StartRound()
    {
        _active = true;
        Host.RoundStarted();
        _roundLeft = RoundSeconds;
        _score = _hits = _combo = 0;
        _lastHit = -10;
        _spawnIn = 0.5;
        _shownSecond = -1;
        PlaceMeter();
        Host.Sound.Play("fire", 0.6);
        Host.HudChanged();
    }

    void EndRound()
    {
        _active = false;
        Host.RoundEnded(_score);
        _combo = 0;
        SetMeter(0);
        foreach (var b in _bugs)
            if (b.WhackT < 0) Leave(b);

        long before = Host.Stats.Get("whack.best");
        Host.Stats.Max("whack.round", _score);
        Host.Stats.Max("whack.best", _score);
        bool best = _score > before;

        var a = Host.Arena;
        var at = new Vec2(a.Center.X, a.Top + a.Height * 0.3);
        Host.Fx.Popup(at, best ? L.T("NEW BEST!") : L.T("TIME!"), best ? Gold : Colors.White, 38, 2.4,
            L.F("{0} points · {1} whacked", _score, _hits));
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

    bool TrySpawn()
    {
        if (_bugs.Count(b => b.WhackT < 0 && !b.Leaving) >= MaxVisible) return false;
        var a = Host.Arena;
        var hud = Host.HudBounds.Inflate(12);
        // window tops close to the ceiling would push the bug out of the box
        var tops = Host.Platforms.Items.Where(p => p.X2 - p.X1 >= 80 && p.Y >= a.Top + Depth + 10 && p.Y <= a.Bottom - 10).ToList();

        for (int attempt = 0; attempt < 10; attempt++)
        {
            double roll = Rng.NextDouble();
            Edge edge;
            Vec2 anchor;
            IntPtr hwnd = IntPtr.Zero;
            if (tops.Count > 0 && roll < 0.4)
            {
                var top = tops[Rng.Next(tops.Count)];
                edge = Edge.Window;
                anchor = new Vec2(top.X1 + 26 + Rng.NextDouble() * (top.X2 - top.X1 - 52), top.Y);
                hwnd = top.Hwnd;
            }
            else if (roll < (tops.Count > 0 ? 0.75 : 0.6))
            {
                edge = Edge.Taskbar;
                anchor = new Vec2(a.Left + 30 + Rng.NextDouble() * Math.Max(0, a.Width - 60), a.Bottom);
            }
            else
            {
                edge = Rng.NextDouble() < 0.5 ? Edge.Left : Edge.Right;
                anchor = new Vec2(edge == Edge.Left ? a.Left : a.Right, a.Top + 50 + Rng.NextDouble() * Math.Max(0, a.Height - 100));
            }

            var area = Footprint(anchor, DirOf(edge));
            if (area.Intersects(hud) || _bugs.Any(b => Footprint(b.Anchor, b.Out).Inflate(10).Intersects(area))) continue;

            double pick = Rng.NextDouble();
            var kind = pick < 0.15 ? Kind.Ladybug : pick < 0.22 ? Kind.Golden : pick < 0.47 ? Kind.Fast : Kind.Normal;
            var bug = AddBug(kind, edge, anchor, hwnd);
            bug.Stay = (StayStart + (StayEnd - StayStart) * Progress) * (kind == Kind.Fast ? 0.6 : 1);
            return true;
        }
        return false;
    }

    Bug AddBug(Kind kind, Edge edge, Vec2 anchor, IntPtr hwnd)
    {
        var sprite = MakeBug(kind, out var squash, out var open, out var dizzy, out var asleep);
        var holder = new Canvas { IsHitTestVisible = false };
        holder.Children.Add(sprite);
        var bug = new Bug
        {
            Holder = holder, Sprite = sprite, Squash = squash, EyesOpen = open, EyesDizzy = dizzy, EyesAsleep = asleep,
            Kind = kind, Edge = edge, Anchor = anchor, Out = DirOf(edge), Hwnd = hwnd, SeenGen = Host.Platforms.Generation,
        };
        _bugLayer.Children.Add(holder);
        _bugs.Add(bug);
        UpdateClip(bug);
        Draw(bug);
        return bug;
    }

    void Remove(Bug b)
    {
        _bugLayer.Children.Remove(b.Holder);
        _bugs.Remove(b);
    }

    /// <summary>Duck back from wherever the bug is now, and stop taking hits.</summary>
    static void Leave(Bug b)
    {
        if (b.T <= Slide + b.Stay || b.Sleeping)
        {
            double u = Math.Min(1, b.T / Slide);
            b.Stay = 0;
            b.T = Slide + (1 - u) * Slide; // the retreat curve mirrors the rise, so there is no jump
        }
        b.Sleeping = false;
        b.Leaving = true;
    }

    // ------------------------------------------------------------------ input

    /// <summary>How far out of its edge a bug is: 0 hidden, 1 fully peeking.</summary>
    static double OutFrac(Bug b)
    {
        double u;
        if (b.T < Slide) u = b.T / Slide;
        else if (b.Sleeping || b.T < Slide + b.Stay) u = 1;
        else u = 1 - (b.T - Slide - b.Stay) / Slide;
        u = Math.Clamp(u, 0, 1);
        return 1 - (1 - u) * (1 - u);
    }

    static bool Whackable(Bug b) => b.WhackT < 0 && !b.Leaving && !b.Flying && OutFrac(b) >= 0.5;

    // kept about a radius away from the edge, so the circle barely reaches behind it
    static Vec2 HitCenter(Bug b) => b.Anchor + b.Out * Math.Max(HitR - 2, 26 - Depth * (1 - OutFrac(b)));

    public override void CollectHitShapes(List<HitShape> into)
    {
        foreach (var b in _bugs)
            if (Whackable(b)) into.Add(HitShape.Circle(HitCenter(b), HitR));
    }

    public override bool PointerDown(Vec2 p, bool right)
    {
        Bug? target = null;
        double nearest = HitR;
        foreach (var b in _bugs)
        {
            if (!Whackable(b)) continue;
            double d = (HitCenter(b) - p).Length;
            if (d <= nearest)
            {
                nearest = d;
                target = b;
            }
        }
        if (target != null) Whack(target);
        return false;
    }

    void Whack(Bug b)
    {
        if (!Whackable(b)) return;
        var at = HitCenter(b);
        b.WhackFrac = OutFrac(b);
        b.WhackT = 0;
        b.EyesOpen.IsVisible = b.EyesAsleep.IsVisible = false;
        b.EyesDizzy.IsVisible = true;
        if (b.Zzz != null) b.Zzz.IsVisible = false;
        SpawnStars(at, b.Out, b.Kind == Kind.Golden ? 7 : 4);
        var popAt = b.Anchor + b.Out * (Depth + 10);

        if (b.Sleeping)
        {
            b.Sleeping = false;
            PlaySound("thunk", 0.8, 1);
            if (!_active) StartRound();
            return;
        }

        if (b.Kind == Kind.Ladybug)
        {
            _score = Math.Max(0, _score - LadybugPenalty);
            _combo = 0;
            SetMeter(0);
            Host.ShareAction(at, -LadybugPenalty);
            Host.Fx.Popup(popAt, L.F("−{0}", LadybugPenalty), Color.FromRgb(255, 110, 110), 28, 1.3, L.T("that was a feature!"));
            PlaySound("buzzer", 0.35, 1);
        }
        else
        {
            _combo = _time - _lastHit <= ComboWindow ? Math.Min(_combo + 1, MaxCombo) : 1;
            _lastHit = _time;
            SetMeter(_combo);
            int pts = (b.Kind == Kind.Golden ? 5 : b.Kind == Kind.Fast ? 2 : 1) * _combo;
            _score += pts;
            _hits++;
            Host.ShareAction(at, pts);
            Host.Stats.Add("whack.hits");
            Host.Fx.Popup(popAt, _combo > 1 ? L.F("+{0} ×{1}", pts, _combo) : L.F("+{0}", pts),
                b.Kind == Kind.Golden ? Gold : Colors.White, b.Kind == Kind.Golden ? 30 : 24, 0.9);
            PlaySound(b.Kind == Kind.Fast ? "pop" : "thunk", 0.8, 0.85 + Rng.NextDouble() * 0.3);
            if (b.Kind == Kind.Golden) Host.Sound.Play("star", 0.6);
            else if (_combo >= MaxCombo) Host.Sound.Play("score", 0.4);
        }
        Host.HudChanged();
    }

    void PlaySound(string name, double vol, double pitch)
    {
        if (_time - _lastSound < 0.05) return; // hits landing together would stack into noise
        _lastSound = _time;
        Host.Sound.Play(name, vol, pitch);
    }

    public override void Summon(Vec2 p)
    {
        if (_active) return;
        var sleeper = _bugs.FirstOrDefault(b => b.Sleeping);
        if (sleeper == null) return;
        var a = Host.Arena;
        sleeper.Anchor = new Vec2(Clamp(p.X, a.Left + 30, a.Right - 30), a.Bottom);
        sleeper.T = 0; // peek up again at the new spot
        UpdateClip(sleeper);
        Draw(sleeper);
    }

    // ------------------------------------------------------------------ simulation

    public override bool Update(double dt)
    {
        _time += dt;
        FollowWindows();

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

        bool busy = Anims.Update(dt) || _active || _sleeperIn > 0 || _sparks.Count > 0;
        if (_meterLevel > 0 && _meterTween == null && _time - _lastHit > ComboWindow) SetMeter(0); // the combo window closed
        foreach (var b in _bugs)
        {
            Draw(b);
            busy |= !b.Sleeping || b.T < Slide + Settle;
        }
        foreach (var s in _sparks)
        {
            s.Sprite.Set(s.Pos, s.Angle);
            s.Sprite.Opacity = Math.Min(1, 2 * (1 - s.Age / s.Life));
        }
        if (!busy) _acc = 0;
        return busy;
    }

    void Tick(double h)
    {
        if (_active)
        {
            _roundLeft -= h;
            if (_roundLeft <= 0) EndRound();
            else if ((_spawnIn -= h) <= 0) _spawnIn = TrySpawn() ? 1.0 - 0.6 * Progress + Rng.NextDouble() * 0.2 : 0.1;
        }
        else if (_sleeperIn > 0 && (_sleeperIn -= h) <= 0)
        {
            _sleeperIn = -1;
            SpawnSleeper();
        }

        for (int i = _bugs.Count - 1; i >= 0; i--)
        {
            var b = _bugs[i];
            b.T += h;
            if (b.WhackT >= 0)
            {
                if (!b.Flying && (b.WhackT += h) >= SquashTime) SpinOff(b);
            }
            else if (!b.Sleeping && b.T >= Slide * 2 + b.Stay)
            {
                Remove(b);
            }
        }

        var a = Host.Arena;
        for (int i = _sparks.Count - 1; i >= 0; i--)
        {
            var s = _sparks[i];
            s.Age += h;
            if (s.Age >= s.Life)
            {
                _sparkLayer.Children.Remove(s.Sprite);
                _sparks.RemoveAt(i);
                continue;
            }
            s.Vel.Y += StarGravity * h;
            s.Pos += s.Vel * h;
            s.Pos.X = Clamp(s.Pos.X, a.Left + 4, a.Right - 4); // closed box
            s.Pos.Y = Clamp(s.Pos.Y, a.Top + 4, a.Bottom - 4);
            s.Angle += s.Spin * h;
        }
    }

    /// <summary>Bugs on a window top ride along with it, and vanish when the window closes, moves off or gets covered.</summary>
    void FollowWindows()
    {
        var plats = Host.Platforms;
        double ceiling = Host.Arena.Top + Depth;
        for (int i = _bugs.Count - 1; i >= 0; i--)
        {
            var b = _bugs[i];
            if (b.Hwnd == IntPtr.Zero || b.SeenGen == plats.Generation) continue;
            b.SeenGen = plats.Generation;
            b.Anchor += plats.DeltaOf(b.Hwnd);
            bool found = false;
            foreach (var p in plats.Items)
            {
                if (p.Hwnd != b.Hwnd || Math.Abs(p.Y - b.Anchor.Y) > 6 || b.Anchor.X < p.X1 + 10 || b.Anchor.X > p.X2 - 10 || p.Y < ceiling) continue;
                b.Anchor.Y = p.Y;
                found = true;
                break;
            }
            if (found) UpdateClip(b);
            else Remove(b);
        }
    }

    // ------------------------------------------------------------------ visuals

    static Vec2 DirOf(Edge edge) => edge switch
    {
        Edge.Left => new Vec2(1, 0),
        Edge.Right => new Vec2(-1, 0),
        _ => new Vec2(0, -1),
    };

    /// <summary>Screen area covered by a fully peeking bug.</summary>
    static Rect Footprint(Vec2 anchor, Vec2 dir)
    {
        var side = new Vec2(-dir.Y, dir.X) * 24;
        var p = anchor - side;
        var q = anchor + dir * Depth + side;
        return new Rect(Math.Min(p.X, q.X), Math.Min(p.Y, q.Y), Math.Abs(q.X - p.X), Math.Abs(q.Y - p.Y));
    }

    void UpdateClip(Bug b)
    {
        var a = Host.Arena;
        double floor = b.Edge == Edge.Window ? b.Anchor.Y : a.Bottom;
        var box = new Rect(a.Left, a.Top, a.Width, Math.Max(0, floor - a.Top));
        if (b.Holder.Clip != null && box == b.ClipBox) return;
        b.ClipBox = box;
        b.Holder.Clip = new RectangleGeometry(box);
    }

    void Draw(Bug b)
    {
        if (b.Flying) return; // its spin-off tween has it
        double frac = b.WhackT >= 0 ? b.WhackFrac : OutFrac(b);
        double angle = b.Out.X > 0.5 ? 90 : b.Out.X < -0.5 ? -90 : 0;
        b.Sprite.Set(b.Anchor - b.Out * (Depth * (1 - frac)), angle);
        double sx = 1, sy = 1;
        if (b.WhackT >= 0)
        {
            double k = Math.Min(1, b.WhackT / 0.06); // flattened by the whack
            sx = 1 + 0.35 * k;
            sy = 1 - 0.62 * k;
        }
        else if (!Fx.ReducedMotion && !b.Leaving && b.T < Slide + Settle)
        {
            // peeking out: stretched tall on the way up, then a squash as it stops
            double u = b.T < Slide ? Math.Sin(Math.PI * b.T / Slide) : 0;
            double settle = b.T < Slide ? 0 : Math.Sin(Math.PI * (b.T - Slide) / Settle);
            sx = 1 - 0.14 * u + 0.16 * settle;
            sy = 1 + 0.22 * u - 0.14 * settle;
        }
        b.Squash.ScaleX = sx;
        b.Squash.ScaleY = sy;
    }

    // ------------------------------------------------------------------ animation

    /// <summary>A flattened bug peels off its edge and spins away, unflattening as it goes, then is gone.</summary>
    void SpinOff(Bug b)
    {
        b.Flying = true;
        b.Holder.Clip = null; // it leaves the edge behind
        var a = Host.Arena;
        var from = b.Anchor - b.Out * (Depth * (1 - b.WhackFrac));
        double baseAngle = b.Out.X > 0.5 ? 90 : b.Out.X < -0.5 ? -90 : 0;
        double side = Rng.NextDouble() < 0.5 ? -1 : 1;
        var across = new Vec2(-b.Out.Y, b.Out.X) * side;
        Anims.Add(SpinOffTime, k =>
        {
            var p = from + b.Out * (130 * k) + across * (70 * k);
            p = new Vec2(Clamp(p.X, a.Left + 24, a.Right - 24), Clamp(p.Y, a.Top + 24, a.Bottom - 24)); // closed box
            b.Sprite.Set(p, baseAngle + side * 540 * k);
            b.Sprite.Opacity = 1 - k * k;
            b.Squash.ScaleX = 1.35 - 0.35 * k;
            b.Squash.ScaleY = 0.38 + 0.62 * k;
        }, Ease.OutQuad, () => Remove(b));
    }

    /// <summary>Three cells that light up as the combo builds, above the middle of the screen (or beside the HUD).</summary>
    void BuildMeter()
    {
        _meter.RenderTransformOrigin = RelativePoint.TopLeft;
        _meter.RenderTransform = _meterScale;
        _meter.Children.Add(Art.At(new Rectangle
        {
            Width = MeterW, Height = MeterH, RadiusX = 7, RadiusY = 7, Fill = Art.Brush(200, 18, 20, 28), Stroke = Art.Brush(60, 255, 255, 255), StrokeThickness = 1,
        }, -MeterW / 2, -MeterH / 2));
        double cellW = (MeterW - 8 - 2 * (MaxCombo - 1)) / MaxCombo;
        for (int i = 0; i < MaxCombo; i++)
        {
            _meterCells[i] = new Rectangle { Width = cellW, Height = MeterH - 6, RadiusX = 3, RadiusY = 3, Fill = Art.Brush(Gold), Opacity = 0 };
            _meter.Children.Add(Art.At(_meterCells[i], -MeterW / 2 + 4 + i * (cellW + 2), -MeterH / 2 + 3));
        }
    }

    void PlaceMeter()
    {
        var a = Host.Arena;
        var hud = Host.HudBounds.Inflate(12);
        foreach (double f in new[] { 0.5, 0.25, 0.75 })
        {
            var at = new Vec2(a.Left + a.Width * f, a.Top + 34);
            if (new Rect(at.X - MeterW / 2, at.Y - MeterH / 2, MeterW, MeterH).Intersects(hud)) continue;
            Canvas.SetLeft(_meter, at.X);
            Canvas.SetTop(_meter, at.Y);
            return;
        }
        Canvas.SetLeft(_meter, a.Center.X);
        Canvas.SetTop(_meter, a.Bottom - Depth - 30);
    }

    /// <summary>Fills the meter to <paramref name="combo"/> cells (0 drains it and it fades away); a full meter gives a little jump.</summary>
    void SetMeter(int combo)
    {
        double from = _meterLevel, to = Math.Clamp(combo, 0, MaxCombo);
        _meterTween?.Cancel();
        _meterFade?.Cancel();
        _meterFade = null;
        if (to > 0)
        {
            _meter.IsVisible = true;
            _meter.Opacity = 1;
        }
        _meterTween = Anims.Add(0.25, k => ShowMeter(from + (to - from) * k), to > from ? Ease.OutBack : Ease.OutQuad, () =>
        {
            _meterTween = null;
            ShowMeter(to);
            if (to == 0) _meterFade = Anims.Add(0.3, k => _meter.Opacity = 1 - k, Ease.OutQuad, () => _meter.IsVisible = false);
        });
        if (to >= MaxCombo)
            Anims.Add(0.3, k => _meterScale.ScaleX = _meterScale.ScaleY = 1 + 0.18 * k, Ease.Pulse, () => _meterScale.ScaleX = _meterScale.ScaleY = 1);
    }

    void ShowMeter(double level)
    {
        _meterLevel = level;
        for (int i = 0; i < _meterCells.Length; i++) _meterCells[i].Opacity = Math.Clamp(level - i, 0, 1);
    }

    void SpawnStars(Vec2 at, Vec2 dir, int count)
    {
        double baseDeg = Math.Atan2(dir.Y, dir.X) * 180 / Math.PI;
        double spread = 140.0 / Math.Max(1, count - 1);
        for (int i = 0; i < count; i++)
        {
            double deg = baseDeg + (i - (count - 1) / 2.0) * spread + (Rng.NextDouble() - 0.5) * 16;
            var (vx, vy) = Art.Polar(170 + Rng.NextDouble() * 90, deg);
            var sprite = new Sprite { IsHitTestVisible = false };
            sprite.Rotor.Children.Add(Art.PathOf(Art.StarPath(0, 0, 5.5, 2.4), StarFills[i % StarFills.Length], StarStroke, 1));
            sprite.Set(at);
            _sparkLayer.Children.Add(sprite);
            _sparks.Add(new Spark
            {
                Sprite = sprite, Pos = at, Vel = new Vec2(vx, vy), Life = 0.5 + Rng.NextDouble() * 0.2, Spin = (Rng.NextDouble() - 0.5) * 900,
            });
        }
    }

    /// <summary>Front view of a cartoon bug peeking out: base centered on the origin, head toward -y.</summary>
    static Sprite MakeBug(Kind kind, out ScaleTransform squash, out Control open, out Control dizzy, out Control asleep)
    {
        (Color shell, Color head, Color ink) = kind switch
        {
            Kind.Ladybug => (Color.FromRgb(222, 42, 58), Color.FromRgb(34, 34, 38), Color.FromRgb(18, 18, 22)),
            Kind.Golden => (Color.FromRgb(242, 192, 46), Color.FromRgb(255, 222, 120), Color.FromRgb(112, 72, 12)),
            Kind.Fast => (Color.FromRgb(86, 110, 230), Color.FromRgb(140, 170, 255), Color.FromRgb(24, 30, 80)),
            _ => (Color.FromRgb(88, 180, 72), Color.FromRgb(150, 214, 110), Color.FromRgb(28, 60, 24)),
        };
        var inkBrush = Art.Brush(ink);
        var face = kind == Kind.Ladybug ? Art.Brush(Color.FromRgb(236, 236, 240)) : inkBrush; // lines drawn on the head
        var s = new Sprite { IsHitTestVisible = false };
        squash = new ScaleTransform(1, 1);
        var body = new Canvas { RenderTransformOrigin = RelativePoint.TopLeft, RenderTransform = squash };
        s.Rotor.Children.Add(body);

        var tip = Art.Brush(kind == Kind.Ladybug ? shell : head);
        body.Children.Add(Art.PathOf("M-4,-41 Q-7,-50 -13,-53 M4,-41 Q7,-50 13,-53", null, inkBrush, 1.8));
        body.Children.Add(Art.Circle(-13.5, -53.5, 3, tip, inkBrush, 1));
        body.Children.Add(Art.Circle(13.5, -53.5, 3, tip, inkBrush, 1));

        body.Children.Add(Art.At(new Ellipse { Width = 38, Height = 30, Fill = Shade(shell), Stroke = inkBrush, StrokeThickness = 1.4 }, -19, -27));
        body.Children.Add(Art.PathOf("M0,-18 L0,3", null, inkBrush, 1.2));
        switch (kind)
        {
            case Kind.Ladybug:
                foreach (var (x, y, r) in new[] { (-9.0, -14.0, 3.2), (9.0, -14.0, 3.2), (-8.0, -4.0, 2.6), (8.0, -4.0, 2.6) })
                    body.Children.Add(Art.Circle(x, y, r, inkBrush));
                break;
            case Kind.Golden:
                body.Children.Add(Art.PathOf(Art.StarPath(-9, -11, 4.8, 2.1), Art.Brush(220, 255, 255, 255)));
                body.Children.Add(Art.PathOf(Art.StarPath(10, -6, 3.2, 1.4), Art.Brush(180, 255, 255, 255)));
                break;
            case Kind.Fast:
                body.Children.Add(Art.PathOf("M-15,-15 L-9,-11 L-15,-7 M15,-15 L9,-11 L15,-7", null, Art.Brush(190, 255, 255, 255), 1.7));
                break;
            default:
                body.Children.Add(Art.Circle(-9, -11, 2.2, Art.Brush(Art.Blend(shell, ink, 0.45))));
                body.Children.Add(Art.Circle(9, -11, 2.2, Art.Brush(Art.Blend(shell, ink, 0.45))));
                break;
        }

        body.Children.Add(Art.At(new Ellipse { Width = 27, Height = 26, Fill = Shade(head), Stroke = inkBrush, StrokeThickness = 1.4 }, -13.5, -43));

        var eyes = new Canvas();
        var pupil = Art.Brush(Color.FromRgb(20, 20, 24));
        eyes.Children.Add(Art.At(new Ellipse { Width = 10, Height = 12.5, Fill = Brushes.White, Stroke = inkBrush, StrokeThickness = 1 }, -10.5, -38.5));
        eyes.Children.Add(Art.At(new Ellipse { Width = 10, Height = 12.5, Fill = Brushes.White, Stroke = inkBrush, StrokeThickness = 1 }, 0.5, -38.5));
        eyes.Children.Add(Art.Circle(-4.6, -31.5, 2.8, pupil));
        eyes.Children.Add(Art.Circle(4.6, -31.5, 2.8, pupil));
        eyes.Children.Add(Art.Circle(-3.7, -32.6, 1, Brushes.White));
        eyes.Children.Add(Art.Circle(5.5, -32.6, 1, Brushes.White));
        if (kind == Kind.Fast) eyes.Children.Add(Art.PathOf("M-11,-41 L-3,-38.5 M11,-41 L3,-38.5", null, inkBrush, 2));
        body.Children.Add(eyes);
        open = eyes;

        var cross = Art.PathOf("M-8,-35 L-3,-29.5 M-3,-35 L-8,-29.5 M3,-35 L8,-29.5 M8,-35 L3,-29.5", null, face, 1.8);
        cross.IsVisible = false;
        body.Children.Add(cross);
        dizzy = cross;

        var shut = Art.PathOf("M-9,-32 Q-5.5,-28.5 -2,-32 M2,-32 Q5.5,-28.5 9,-32", null, face, 1.6);
        shut.IsVisible = false;
        body.Children.Add(shut);
        asleep = shut;

        body.Children.Add(Art.PathOf("M-4,-22.5 Q0,-19 4,-22.5", null, face, 1.4));

        // little hands gripping the edge
        var hand = Art.Brush(kind == Kind.Ladybug ? head : Art.Blend(shell, ink, 0.25));
        body.Children.Add(Art.At(new Ellipse { Width = 10, Height = 6, Fill = hand, Stroke = inkBrush, StrokeThickness = 1 }, -21, -4));
        body.Children.Add(Art.At(new Ellipse { Width = 10, Height = 6, Fill = hand, Stroke = inkBrush, StrokeThickness = 1 }, 11, -4));
        return s;
    }

    static IBrush Shade(Color c)
    {
        var brush = new RadialGradientBrush { GradientOrigin = new RelativePoint(0.4, 0.25, RelativeUnit.Relative) };
        brush.GradientStops.Add(new GradientStop(Art.Blend(c, Colors.White, 0.35), 0));
        brush.GradientStops.Add(new GradientStop(c, 0.55));
        brush.GradientStops.Add(new GradientStop(Art.Blend(c, Colors.Black, 0.3), 1));
        return brush;
    }

    public override void DemoTick()
    {
        if (!_active)
        {
            var sleeper = _bugs.FirstOrDefault(b => b.Sleeping && Whackable(b));
            if (sleeper != null && Rng.NextDouble() < 0.4) Whack(sleeper);
            else if (sleeper == null && _sleeperIn < 0 && !_bugs.Any(b => b.Sleeping)) SpawnSleeper();
            return;
        }
        if (Rng.NextDouble() > 0.35) return;
        var targets = _bugs.Where(b => b.Kind != Kind.Ladybug && Whackable(b) && OutFrac(b) > 0.9).ToList();
        if (targets.Count > 0) Whack(targets[Rng.Next(targets.Count)]);
    }

    // ------------------------------------------------------------------ the pet keeping you company

    readonly List<Vec2> _petBugs = new();

    public PetFloor PetFloor
    {
        get
        {
            var a = Host.Arena;
            return new PetFloor(a.Left + 26, a.Right - 26, a.Bottom);
        }
    }

    /// <summary>The bugs peeking out (the sleeper too), for the pet to hide from; it never touches them.</summary>
    public PetToy PetToy
    {
        get
        {
            _petBugs.Clear();
            foreach (var b in _bugs)
                if (b.WhackT < 0 && !b.Leaving && OutFrac(b) >= 0.3) _petBugs.Add(HitCenter(b));
            return _petBugs.Count == 0 ? new(PetToyKind.None) : new(PetToyKind.Bugs, _petBugs[0], Bugs: _petBugs);
        }
    }

    public bool PetTouched(PetTouch touch, Vec2 v) => false;
}
