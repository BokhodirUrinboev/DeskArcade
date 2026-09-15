using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>
/// Desktop Pet: a little cat-like blob that lives on the taskbar and the tops of your windows. It wanders,
/// jumps between windows, naps when ignored and loves being petted. Click to pet it, drag to carry and throw it.
/// There is no score to chase, so it is built to sit perfectly still (zero CPU) most of the time.
/// </summary>
public sealed class PetGame : MiniGame
{
    const double Step = 1.0 / 240, Gravity = 1800, WalkSpeed = 72, HalfW = 22, Height = 46, CenterLift = 20;
    const double HitR = 27, DragStart = 6, CarryCount = 30, DangleY = 18, MaxThrow = 2600;
    const double JumpSide = 280, JumpUp = 240, ApexExtra = 40, EdgeIn = 6;
    const double WallBounce = 0.5, FloorBounce = 0.4, BounceMin = 450, SlideMin = 350;
    const double SleepAfter = 60, DemoSleepAfter = 14, AttentionAfter = 25, NoticeR = 200;
    const double HappyTime = 0.55, SquashTime = 0.22, PupilReach = 1.6;

    static readonly Color[] Blush = { Color.FromRgb(255, 120, 160), Color.FromRgb(255, 200, 220), Colors.White };

    enum Mode { Sit, Walk, Air, Carried, Sleep }

    /// <summary>The pet's drawing. The body canvas faces +x and is flipped/squashed as a whole.</summary>
    sealed class PetArt
    {
        public required Sprite Root;
        public required ScaleTransform BodyScale;
        public required TranslateTransform BodyShift;
        public required TranslateTransform PupilL, PupilR, FootA, FootB;
        public required Control EyesOpen, EyesSleep, EyesHappy, Shadow;
        public required TextBlock Zz;
    }

    sealed class Heart
    {
        public required Path El;
        public required TranslateTransform Tr;
        public required ScaleTransform Sc;
        public Vec2 P, V;
        public double Age, Life, Phase;
    }

    readonly PetArt _art = BuildPet();
    readonly Canvas _heartLayer = new() { IsHitTestVisible = false };
    readonly List<Heart> _hearts = new();
    readonly Stack<Heart> _heartPool = new();
    readonly List<(double t, Vec2 p)> _trail = new();
    readonly Dictionary<string, double> _lastSound = new();
    readonly DispatcherTimer _brain = new() { Interval = TimeSpan.FromSeconds(3) };
    readonly Stopwatch _clock = Stopwatch.StartNew();

    Mode _mode = Mode.Sit;
    Vec2 _pos, _vel, _pressPos, _carryStart, _demoStep;
    Vec2? _demoHand;
    IntPtr _hwnd;
    DeskArcade.Engine.Platform _surface;
    int _seenGen = -1, _pets, _demoTicks;
    double _face = 1, _walkLeft, _walkTargetX = double.NaN, _acc, _animT, _happyT, _squashT, _swing;
    double _lastStir, _sleptAt;
    bool _placed, _active, _pressed, _recheck, _decide, _hopAtEdge, _thrown, _demo;
    string _shownLine = "";

    public PetGame(IGameHost host) : base(host)
    {
        _art.Root.IsHitTestVisible = false;
        Layer.Children.Add(_art.Root);
        Layer.Children.Add(_heartLayer);
        _brain.Tick += (_, _) => Think();
    }

    public override string Id => "pet";
    public override string Title => "Desktop Pet";

    public override Sprite CreateIcon()
    {
        var icon = BuildPet();
        icon.Shadow.IsVisible = false;
        icon.BodyShift.Y = 4;
        icon.Root.Scale = 0.42;
        return icon.Root;
    }

    double Now => _clock.Elapsed.TotalSeconds;
    Vec2 Center => new(_pos.X, _pos.Y - CenterLift);
    bool Grounded => _mode is Mode.Sit or Mode.Walk or Mode.Sleep;

    public override HudInfo Hud => new(_pets.ToString(), StateLine(), L.F("Pets {0}", Host.Stats.Get("pet.pets")));

    string StateLine()
    {
        if (_mode == Mode.Sleep) return L.T("Sleeping · click to wake");
        if (_mode == Mode.Carried) return L.T("Wheee! · let go to throw");
        if (_thrown) return L.T("Wheee!");
        if (Now - _lastStir > AttentionAfter) return L.T("Wants attention · click to pet");
        return _hwnd != IntPtr.Zero ? L.T("Exploring the window tops") : L.T("Click to pet · drag to carry");
    }

    void UpdateHud()
    {
        string line = StateLine();
        if (line == _shownLine) return;
        _shownLine = line;
        Host.HudChanged();
    }

    // ------------------------------------------------------------------ life cycle

    public override void Layout()
    {
        var a = Host.Arena;
        if (!_placed)
        {
            _placed = true;
            _pos = new Vec2(a.Left + a.Width * 0.3, a.Bottom);
            _lastStir = Now;
        }
        _pos.X = Clamp(_pos.X, a.Left + HalfW, a.Right - HalfW);
        _pos.Y = Clamp(_pos.Y, a.Top + Height, a.Bottom);
        if (Grounded && _hwnd == IntPtr.Zero) _pos.Y = a.Bottom;
        // windows may have moved while we were away: check the window top again without applying a stale delta
        _seenGen = Host.Platforms.Generation;
        _recheck = Grounded && _hwnd != IntPtr.Zero;
        _active = true;
        RunBrain();
        Draw();
        _shownLine = StateLine();
        Host.HudChanged();
    }

    public override void Deactivate()
    {
        _active = false;
        _brain.Stop();
        _decide = false;
        _pressed = false;
        _demoHand = null;
        if (_mode == Mode.Carried) Drop(default);
        foreach (var h in _hearts)
        {
            h.El.IsVisible = false;
            _heartPool.Push(h);
        }
        _hearts.Clear();
    }

    void RunBrain()
    {
        if (_active && _mode != Mode.Sleep && !_brain.IsEnabled) _brain.Start();
    }

    /// <summary>
    /// The behaviour timer. The pointer is only fresh while frames run, so the timer just wakes the loop
    /// and the next frame picks what to do.
    /// </summary>
    void Think()
    {
        _brain.Interval = TimeSpan.FromSeconds(2 + Rng.NextDouble() * 4);
        if (!_active || _pressed || _mode != Mode.Sit) return;
        _decide = true;
        Host.Wake();
    }

    void Decide()
    {
        if (_mode != Mode.Sit || _pressed) return;
        if (Now - _lastStir > (_demo ? DemoSleepAfter : SleepAfter))
        {
            GoToSleep();
            return;
        }

        var toCursor = Host.Pointer - Center;
        if (toCursor.Length < NoticeR)
        {
            if (Math.Abs(toCursor.X) > 6) _face = toCursor.X > 0 ? 1 : -1;
            if (Math.Abs(toCursor.X) > 40 && Rng.NextDouble() < 0.5) WalkTo(Host.Pointer.X);
            return;
        }

        double roll = Rng.NextDouble();
        if (roll < 0.35) StartWalk(Rng.NextDouble() < 0.5 ? -1 : 1, 2 + Rng.NextDouble() * 4);
        else if (roll < 0.6 && TryJumpUp()) { }
        else if (roll < 0.75 && _hwnd != IntPtr.Zero) HopDown();
        else if (Rng.NextDouble() < 0.4) _face = -_face; // look around
    }

    void GoToSleep()
    {
        _mode = Mode.Sleep;
        _sleptAt = Now;
        _brain.Stop(); // a sleeping pet costs nothing until someone clicks it
        _art.Zz.Text = L.T("z z");
        Canvas.SetTop(_art.Zz, Math.Max(-46, Host.Arena.Top + 2 - Center.Y)); // closed box: keep the label on screen
        Draw();
        UpdateHud();
    }

    // ------------------------------------------------------------------ behaviours

    void StartWalk(double dir, double seconds)
    {
        _mode = Mode.Walk;
        _face = dir;
        _walkLeft = seconds;
        _walkTargetX = double.NaN;
        _hopAtEdge = false;
    }

    (double lo, double hi) WalkRange()
    {
        var a = Host.Arena;
        if (_hwnd == IntPtr.Zero) return (a.Left + HalfW, a.Right - HalfW);
        return (Math.Max(_surface.X1 + EdgeIn, a.Left + HalfW), Math.Min(_surface.X2 - EdgeIn, a.Right - HalfW));
    }

    void WalkTo(double x)
    {
        var (lo, hi) = WalkRange();
        x = Clamp(x, lo, hi);
        if (Math.Abs(x - _pos.X) < 8) return;
        StartWalk(x > _pos.X ? 1 : -1, 6);
        _walkTargetX = x;
    }

    void StopWalking()
    {
        _mode = Mode.Sit;
        _walkTargetX = double.NaN;
        _hopAtEdge = false;
    }

    /// <summary>Walk to the nearer real edge of the window top and hop off it.</summary>
    void HopDown()
    {
        var a = Host.Arena;
        bool leftOpen = _surface.X1 > a.Left + HalfW, rightOpen = _surface.X2 < a.Right - HalfW;
        if (!leftOpen && !rightOpen) return;
        double dir = !leftOpen ? 1 : !rightOpen ? -1 : _pos.X - _surface.X1 < _surface.X2 - _pos.X ? -1 : 1;
        StartWalk(dir, 6);
        _hopAtEdge = true;
    }

    bool TryJumpUp()
    {
        var a = Host.Arena;
        var hud = Host.HudBounds.Inflate(20);
        var options = new List<Vec2>();
        foreach (var p in Host.Platforms.Items)
        {
            double rise = _pos.Y - p.Y;
            if (rise < 24 || rise > JumpUp || p.Y - ApexExtra - Height < a.Top + 4) continue;
            double lo = Math.Max(p.X1 + EdgeIn + 8, a.Left + HalfW), hi = Math.Min(p.X2 - EdgeIn - 8, a.Right - HalfW);
            if (hi <= lo) continue;
            double near = Clamp(_pos.X, lo, hi);
            if (Math.Abs(near - _pos.X) > JumpSide) continue;
            double inward = near > _pos.X ? 1 : near < _pos.X ? -1 : Rng.NextDouble() < 0.5 ? -1 : 1;
            double x = Clamp(near + inward * Rng.NextDouble() * 60, lo, hi);
            if (hud.Contains(new Point(x, p.Y - CenterLift))) continue;
            options.Add(new Vec2(x, p.Y));
        }
        if (options.Count == 0) return false;
        JumpTo(options[Rng.Next(options.Count)]);
        return true;
    }

    /// <summary>A ballistic arc that peaks a little above the target window top and comes down onto it.</summary>
    void JumpTo(Vec2 target)
    {
        double up = _pos.Y - target.Y + ApexExtra;
        double vy = -Math.Sqrt(2 * Gravity * up);
        double time = -vy / Gravity + Math.Sqrt(2 * ApexExtra / Gravity);
        double vx = (target.X - _pos.X) / time;
        if (Math.Abs(vx) > 1) _face = vx > 0 ? 1 : -1;
        Drop(new Vec2(vx, vy));
        PlayThrottled("pop", 0.2, 1.9);
    }

    void Hop() => Drop(new Vec2(_face * 150, -260));

    /// <summary>Leave the ground: jumping, hopping off, thrown, or the window underneath went away.</summary>
    void Drop(Vec2 v)
    {
        if (_mode == Mode.Sleep) _lastStir = Now; // the fall woke it up
        _mode = Mode.Air;
        _vel = v;
        _hwnd = IntPtr.Zero;
        _walkTargetX = double.NaN;
        _hopAtEdge = _recheck = false;
        RunBrain();
    }

    void Land(IntPtr hwnd, DeskArcade.Engine.Platform plat)
    {
        _mode = Mode.Sit;
        _vel = default;
        _hwnd = hwnd;
        _surface = plat;
        _seenGen = Host.Platforms.Generation;
        _thrown = false;
        _swing = 0;
        _squashT = SquashTime;
        PlayThrottled("thunk", 0.2, 1.7);
    }

    // ------------------------------------------------------------------ input

    public override void CollectHitShapes(List<HitShape> into) => into.Add(HitShape.Circle(Center - new Vec2(0, 3), HitR));

    public override bool PointerDown(Vec2 p, bool right)
    {
        if (_pressed || (p - (Center - new Vec2(0, 3))).Length > HitR) return false;
        _pressed = true;
        _pressPos = p;
        return true; // capture until release: a still release pets it, a drag carries it
    }

    public override void PointerUp(Vec2 p)
    {
        if (!_pressed) return;
        _pressed = false;
        if (_mode == Mode.Carried) Throw(ThrowVelocity());
        else Pet();
    }

    void Pet()
    {
        _pets++;
        Host.Stats.Add("pet.pets");
        _lastStir = Now;
        if (_mode == Mode.Sleep) _mode = Mode.Sit;
        if (_mode == Mode.Walk) StopWalking();
        if (Math.Abs(Host.Pointer.X - _pos.X) > 6 && _mode == Mode.Sit) _face = Host.Pointer.X > _pos.X ? 1 : -1;
        _happyT = HappyTime;
        var top = Center - new Vec2(0, 26);
        for (int i = 0; i < 5; i++)
            SpawnHeart(top + new Vec2((Rng.NextDouble() - 0.5) * 30, Rng.NextDouble() * 8),
                new Vec2((Rng.NextDouble() - 0.5) * 90, -70 - Rng.NextDouble() * 60), 0.9 + Rng.NextDouble() * 0.5);
        Host.Fx.Burst(Center, Blush, 6, 150, 250, 4, 0.5);
        PlayThrottled("star", 0.3, 1.7 + Rng.NextDouble() * 0.2);
        RunBrain();
        UpdateHud();
        Host.HudChanged(); // the score changed even if the line did not
    }

    void StartCarry()
    {
        if (_mode == Mode.Walk) StopWalking();
        _mode = Mode.Carried;
        _hwnd = IntPtr.Zero;
        _thrown = _recheck = false;
        _carryStart = _pos;
        _trail.Clear();
        _lastStir = Now;
        RunBrain();
        PlayThrottled("pop", 0.3, 1.8);
    }

    void Throw(Vec2 v)
    {
        if (v.Length > MaxThrow) v *= MaxThrow / v.Length;
        if ((_pos - _carryStart).Length > CarryCount) Host.Stats.Add("pet.carries");
        Drop(v);
        _thrown = v.Length > 250;
        _lastStir = Now;
        if (v.Length > 600) PlayThrottled("whoosh", Math.Min(0.5, v.Length / 4000));
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
        return dt < 0.008 ? default : (last.p - first.p) / dt;
    }

    public override void Summon(Vec2 p)
    {
        if (_pressed) return;
        var a = Host.Arena;
        _pos = new Vec2(Clamp(p.X, a.Left + HalfW, a.Right - HalfW), Clamp(p.Y + CenterLift, a.Top + Height, a.Bottom));
        _lastStir = Now;
        Drop(default);
        Draw();
    }

    // ------------------------------------------------------------------ simulation

    public override bool Update(double dt)
    {
        _animT += dt;
        CheckSurface();
        if (_decide)
        {
            _decide = false;
            Decide();
        }
        if (_pressed && _mode != Mode.Carried && (Host.Pointer - _pressPos).Length > DragStart) StartCarry();
        if (_mode == Mode.Carried) Carry(dt);

        if (_mode is Mode.Walk or Mode.Air)
        {
            _acc += dt;
            while (_acc >= Step && (_mode is Mode.Walk or Mode.Air))
            {
                _acc -= Step;
                if (_mode == Mode.Walk) WalkStep(Step);
                else AirStep(Step);
            }
        }
        else
        {
            _acc = 0;
        }

        if (_mode == Mode.Sit && !_pressed) FaceCursor();
        if (_mode != Mode.Carried) _swing *= Math.Max(0, 1 - dt * 8);
        _happyT = Math.Max(0, _happyT - dt);
        _squashT = Math.Max(0, _squashT - dt);
        bool hearts = UpdateHearts(dt);
        Draw();
        UpdateHud();
        // sitting and sleeping are still: no frames needed until the behaviour timer or the user wakes us
        return _pressed || (_mode is Mode.Walk or Mode.Air or Mode.Carried) || _happyT > 0 || _squashT > 0 || hearts;
    }

    /// <summary>Ride along with the window underneath, or fall when it moved away, closed or got covered.</summary>
    void CheckSurface()
    {
        var plats = Host.Platforms;
        bool changed = plats.Generation != _seenGen;
        _seenGen = plats.Generation;
        if (!Grounded || _hwnd == IntPtr.Zero || (!changed && !_recheck)) return;
        if (changed) _pos += plats.DeltaOf(_hwnd);
        _recheck = false;
        foreach (var p in plats.Items)
        {
            if (p.Hwnd != _hwnd || Math.Abs(p.Y - _pos.Y) > 4 || _pos.X < p.X1 - EdgeIn || _pos.X > p.X2 + EdgeIn) continue;
            if (p.Y - Height < Host.Arena.Top) break; // too close to the top of the screen to fit
            _surface = p;
            _pos.Y = p.Y;
            var (lo, hi) = WalkRange();
            if (lo <= hi) _pos.X = Clamp(_pos.X, lo, hi);
            return;
        }
        Drop(default);
    }

    void WalkStep(double h)
    {
        if (!double.IsNaN(_walkTargetX) && (_walkTargetX - _pos.X) * _face <= 0)
        {
            StopWalking();
            return;
        }
        var a = Host.Arena;
        _pos.X += _face * WalkSpeed * h;
        if (_hwnd == IntPtr.Zero) _pos.Y = a.Bottom;

        var (lo, hi) = WalkRange();
        if (_pos.X < lo || _pos.X > hi)
        {
            bool atLeft = _pos.X < lo;
            // only a real window edge can be hopped off; where the window meets the screen edge, turn around
            bool windowEdge = _hwnd != IntPtr.Zero &&
                (atLeft ? _surface.X1 + EdgeIn >= a.Left + HalfW : _surface.X2 - EdgeIn <= a.Right - HalfW);
            _pos.X = atLeft ? lo : hi;
            if (windowEdge && (_hopAtEdge || Rng.NextDouble() < 0.3))
            {
                Hop();
                return;
            }
            _face = atLeft ? 1 : -1;
            if (!double.IsNaN(_walkTargetX) || _hopAtEdge)
            {
                StopWalking();
                return;
            }
        }
        if ((_walkLeft -= h) <= 0) StopWalking();
    }

    void AirStep(double h)
    {
        var a = Host.Arena;
        double prevY = _pos.Y;
        _vel.Y += Gravity * h;
        _pos += _vel * h;

        // closed box: bounce off the sides and the top of the screen
        if (_pos.X < a.Left + HalfW)
        {
            _pos.X = a.Left + HalfW;
            if (_vel.X < 0) { WallHit(-_vel.X); _vel.X = -_vel.X * WallBounce; }
        }
        else if (_pos.X > a.Right - HalfW)
        {
            _pos.X = a.Right - HalfW;
            if (_vel.X > 0) { WallHit(_vel.X); _vel.X = -_vel.X * WallBounce; }
        }
        if (_pos.Y - Height < a.Top)
        {
            _pos.Y = a.Top + Height;
            if (_vel.Y < 0) { WallHit(-_vel.Y); _vel.Y = -_vel.Y * WallBounce; }
        }
        if (_vel.Y < 0)
        {
            if (_pos.Y > a.Bottom) _pos.Y = a.Bottom;
            return;
        }

        double ground = double.NaN;
        IntPtr hwnd = IntPtr.Zero;
        DeskArcade.Engine.Platform plat = default;
        if (Host.Platforms.FindLanding(_pos.X, prevY, _pos.Y, out var top) && top.Y - Height >= a.Top)
        {
            ground = top.Y;
            hwnd = top.Hwnd;
            plat = top;
        }
        else if (_pos.Y >= a.Bottom)
        {
            ground = a.Bottom;
        }
        if (double.IsNaN(ground)) return;

        _pos.Y = ground;
        if (_vel.Y > BounceMin || Math.Abs(_vel.X) > SlideMin)
        {
            // a hard landing bounces; a fast sideways one skips along until it slows down
            PlayThrottled("bounce", Math.Min(0.5, (_vel.Y + Math.Abs(_vel.X)) / 4000), 1.5);
            _vel.Y = -Math.Max(_vel.Y * FloorBounce, Math.Abs(_vel.X) * 0.2);
            _vel.X *= 0.55;
            _squashT = SquashTime;
            return;
        }
        Land(hwnd, plat);
    }

    void WallHit(double speed)
    {
        if (speed > 300) PlayThrottled("bounce", Math.Min(0.5, speed / 3000), 1.3);
    }

    void Carry(double dt)
    {
        var a = Host.Arena;
        var hand = _demoHand ?? Host.Pointer;
        _pos = new Vec2(Clamp(hand.X, a.Left + HalfW, a.Right - HalfW), Clamp(hand.Y + DangleY + CenterLift, a.Top + Height, a.Bottom));
        _trail.Add((_animT, _pos));
        while (_trail.Count > 0 && _animT - _trail[0].t > 0.15) _trail.RemoveAt(0);
        // it dangles: the body swings behind the hand
        double lean = Clamp(ThrowVelocity().X * 0.02, -28, 28);
        _swing += (lean - _swing) * Math.Min(1, dt * 10);
    }

    void FaceCursor()
    {
        var d = Host.Pointer - Center;
        if (Math.Abs(d.X) > 10 && d.Length < NoticeR) _face = d.X > 0 ? 1 : -1;
    }

    // ------------------------------------------------------------------ visuals

    const string HeartPath = "M0,5 C-8,-1 -7,-8 -3,-8 C-1.5,-8 -0.4,-7 0,-5.8 C0.4,-7 1.5,-8 3,-8 C7,-8 8,-1 0,5 Z";
    static readonly IBrush HeartFill = Art.Brush("#FF5C8A");
    static readonly IBrush HeartInk = Art.Brush("#B8325A");

    void Draw()
    {
        double bob = 0, sx = 1, sy = 1;
        if (_mode == Mode.Walk) bob = -Math.Abs(Math.Sin(_animT * 11)) * 2.5;
        if (_happyT > 0)
        {
            double k = 1 - _happyT / HappyTime;
            bob -= Math.Sin(Math.PI * Math.Min(1, k / 0.7)) * 14;
        }
        if (_squashT > 0)
        {
            double k = _squashT / SquashTime;
            sy = 1 - 0.18 * k;
            sx = 1 + 0.12 * k;
        }
        else if (_mode == Mode.Carried)
        {
            sy = 1.06;
            sx = 0.95;
        }

        _art.Root.Set(Center, _swing);
        _art.BodyScale.ScaleX = _face * sx;
        _art.BodyScale.ScaleY = sy;
        _art.BodyShift.Y = bob + CenterLift * (1 - sy); // squash toward the feet, not the middle

        double stepA = 0, stepB = 0;
        if (_mode == Mode.Walk)
        {
            double s = Math.Sin(_animT * 11);
            stepA = -2.2 * Math.Max(0, s);
            stepB = -2.2 * Math.Max(0, -s);
        }
        else if (_mode == Mode.Carried)
        {
            double s = Math.Sin(_animT * 16);
            stepA = 2 * s;
            stepB = -2 * s;
        }
        _art.FootA.Y = stepA;
        _art.FootB.Y = stepB;

        bool sleeping = _mode == Mode.Sleep, happy = !sleeping && _happyT > 0;
        _art.EyesSleep.IsVisible = sleeping;
        _art.EyesHappy.IsVisible = happy;
        _art.EyesOpen.IsVisible = !sleeping && !happy;
        _art.Zz.IsVisible = sleeping;
        _art.Shadow.IsVisible = Grounded;
        if (sleeping || happy) return;

        var d = Host.Pointer - (Center + new Vec2(_face * 2.5, -3 + bob));
        double len = d.Length;
        var look = len < 1 ? default : d * (Math.Min(1, len / 60) * PupilReach / len);
        _art.PupilL.X = _art.PupilR.X = look.X * _face; // the body canvas is mirrored when facing left
        _art.PupilL.Y = _art.PupilR.Y = look.Y;
    }

    void SpawnHeart(Vec2 p, Vec2 v, double life)
    {
        if (_hearts.Count >= 16) return;
        Heart h;
        if (_heartPool.Count > 0)
        {
            h = _heartPool.Pop();
            h.El.IsVisible = true;
        }
        else
        {
            var tr = new TranslateTransform();
            var sc = new ScaleTransform(0.01, 0.01);
            var el = Art.PathOf(HeartPath, HeartFill, HeartInk, 0.8);
            el.IsHitTestVisible = false;
            el.RenderTransformOrigin = RelativePoint.TopLeft;
            el.RenderTransform = new TransformGroup { Children = { sc, tr } };
            _heartLayer.Children.Add(el);
            h = new Heart { El = el, Tr = tr, Sc = sc };
        }
        h.P = p;
        h.V = v;
        h.Age = 0;
        h.Life = life;
        h.Phase = Rng.NextDouble() * 6;
        h.Sc.ScaleX = h.Sc.ScaleY = 0.01;
        h.El.Opacity = 1;
        _hearts.Add(h);
    }

    bool UpdateHearts(double dt)
    {
        var a = Host.Arena;
        for (int i = _hearts.Count - 1; i >= 0; i--)
        {
            var h = _hearts[i];
            h.Age += dt;
            if (h.Age >= h.Life)
            {
                h.El.IsVisible = false;
                _hearts.RemoveAt(i);
                _heartPool.Push(h);
                continue;
            }
            h.V.X *= Math.Max(0, 1 - dt * 2);
            h.P += h.V * dt;
            h.P.X = Clamp(h.P.X, a.Left + 10, a.Right - 10);
            h.P.Y = Math.Max(h.P.Y, a.Top + 10); // closed box: hearts gather under the top edge
            double k = h.Age / h.Life;
            double s = Math.Min(1, h.Age / 0.15) * (0.85 + 0.15 * Math.Sin(h.Age * 12 + h.Phase));
            h.Sc.ScaleX = h.Sc.ScaleY = Math.Max(0.01, s);
            h.Tr.X = h.P.X + Math.Sin(h.Age * 5 + h.Phase) * 4;
            h.Tr.Y = h.P.Y;
            h.El.Opacity = k < 0.6 ? 1 : 1 - (k - 0.6) / 0.4;
        }
        return _hearts.Count > 0;
    }

    void PlayThrottled(string name, double vol, double pitch = 1)
    {
        double now = Now;
        if (_lastSound.TryGetValue(name, out double t) && now - t < 0.08) return;
        _lastSound[name] = now;
        Host.Sound.Play(name, vol, pitch);
    }

    /// <summary>A round cat-like blob facing +x, origin at the body center, feet at y = CenterLift.</summary>
    static PetArt BuildPet()
    {
        var ink = Art.Brush("#8A4F2A");
        var fur = Art.Brush("#F2A566");
        var innerEar = Art.Brush("#FF9FB0");
        var eyeInk = Art.Brush("#2B2320");
        var s = new Sprite();

        var shadow = Art.At(new Ellipse { Width = 32, Height = 6, Fill = Art.Brush(50, 0, 0, 0) }, -16, CenterLift - 3);
        s.Children.Insert(0, shadow);

        var bodyScale = new ScaleTransform(1, 1);
        var bodyShift = new TranslateTransform();
        var body = new Canvas
        {
            RenderTransformOrigin = RelativePoint.TopLeft,
            RenderTransform = new TransformGroup { Children = { bodyScale, bodyShift } },
        };
        s.Rotor.Children.Add(body);

        // tail and ears first, so the body covers their roots
        const string tail = "M-16,9 C-29,9 -33,-4 -26,-14";
        body.Children.Add(Art.PathOf(tail, null, ink, 6.5));
        body.Children.Add(Art.PathOf(tail, null, fur, 3.8));
        body.Children.Add(Art.PathOf("M-16,-6 L-14,-25 L-3,-15 Z", fur, ink, 1.3));
        body.Children.Add(Art.PathOf("M5,-15 L15,-25 L18,-6 Z", fur, ink, 1.3));
        body.Children.Add(Art.PathOf("M-12.5,-11 L-12.5,-20 L-7,-14.5 Z", innerEar));
        body.Children.Add(Art.PathOf("M9,-14.5 L14,-20 L15,-10 Z", innerEar));

        var furFill = new RadialGradientBrush
        {
            GradientOrigin = new RelativePoint(0.35, 0.3, RelativeUnit.Relative),
            Center = new RelativePoint(0.45, 0.42, RelativeUnit.Relative),
        };
        furFill.GradientStops.Add(new GradientStop(Color.Parse("#FFE3C2"), 0));
        furFill.GradientStops.Add(new GradientStop(Color.Parse("#F7B37A"), 0.6));
        furFill.GradientStops.Add(new GradientStop(Color.Parse("#E08A4E"), 1));
        body.Children.Add(Art.At(new Ellipse { Width = 40, Height = 34, Fill = furFill, Stroke = ink, StrokeThickness = 1.3 }, -20, -17));
        body.Children.Add(Art.At(new Ellipse { Width = 22, Height = 12, Fill = Art.Brush(190, 255, 244, 228) }, -8, 3));
        body.Children.Add(Art.At(new Ellipse { Width = 9, Height = 5, Fill = Art.Brush(120, 255, 255, 255), RenderTransform = new RotateTransform(-30) }, -14, -12));

        var blush = Art.Brush(110, 255, 110, 150);
        body.Children.Add(Art.At(new Ellipse { Width = 7, Height = 4, Fill = blush }, -12.5, 3));
        body.Children.Add(Art.At(new Ellipse { Width = 7, Height = 4, Fill = blush }, 10.5, 3));
        body.Children.Add(Art.PathOf("M-1,3.5 Q0.75,6 2.5,3.5 Q4.25,6 6,3.5", null, Art.Brush("#5A3320"), 1.1));

        var eyes = new Canvas();
        var pupilL = new TranslateTransform();
        var pupilR = new TranslateTransform();
        foreach (var (x, tr) in new[] { (-4.0, pupilL), (9.0, pupilR) })
        {
            eyes.Children.Add(Art.At(new Ellipse { Width = 8.6, Height = 9.8, Fill = Brushes.White, Stroke = eyeInk, StrokeThickness = 0.9 }, x - 4.3, -7.9));
            var pupil = new Canvas { RenderTransformOrigin = RelativePoint.TopLeft, RenderTransform = tr };
            pupil.Children.Add(Art.Circle(x, -2.6, 2.4, eyeInk));
            pupil.Children.Add(Art.Circle(x - 0.9, -3.6, 0.85, Brushes.White));
            eyes.Children.Add(pupil);
        }
        body.Children.Add(eyes);
        var eyesSleep = Art.PathOf("M-7.5,-3 Q-4,0 -0.5,-3 M5.5,-3 Q9,0 12.5,-3", null, eyeInk, 1.4);
        var eyesHappy = Art.PathOf("M-7.5,-2 Q-4,-6.5 -0.5,-2 M5.5,-2 Q9,-6.5 12.5,-2", null, eyeInk, 1.5);
        eyesSleep.IsVisible = eyesHappy.IsVisible = false;
        body.Children.Add(eyesSleep);
        body.Children.Add(eyesHappy);

        var paw = Art.Brush("#FFE7CC");
        var footA = new TranslateTransform();
        var footB = new TranslateTransform();
        body.Children.Add(Art.At(new Ellipse { Width = 11, Height = 7, Fill = paw, Stroke = ink, StrokeThickness = 1, RenderTransform = footA }, -13.5, 13));
        body.Children.Add(Art.At(new Ellipse { Width = 11, Height = 7, Fill = paw, Stroke = ink, StrokeThickness = 1, RenderTransform = footB }, 2.5, 13));

        // outside the mirrored body, so the text never reads backwards
        var zz = new TextBlock
        {
            Text = L.T("z z"), FontFamily = Fx.Font, FontSize = 14, FontWeight = FontWeight.Bold,
            Foreground = Art.Brush("#8FA3D1"), IsVisible = false,
        };
        s.Children.Add(Art.At(zz, 8, -46));

        return new PetArt
        {
            Root = s, BodyScale = bodyScale, BodyShift = bodyShift, PupilL = pupilL, PupilR = pupilR, FootA = footA, FootB = footB,
            EyesOpen = eyes, EyesSleep = eyesSleep, EyesHappy = eyesHappy, Shadow = shadow, Zz = zz,
        };
    }

    // ------------------------------------------------------------------ demo

    public override void DemoTick()
    {
        _demo = true;
        if (_demoHand is Vec2 hand)
        {
            if (_demoTicks-- > 0)
            {
                _demoHand = hand + _demoStep; // carry it a short way, like a drag would
                return;
            }
            _demoHand = null;
            _pressed = false;
            Throw(new Vec2(_demoStep.X * 14, -300 - Rng.NextDouble() * 700));
            return;
        }
        if (_pressed || _mode is Mode.Air or Mode.Carried) return;
        if (_mode == Mode.Sleep)
        {
            if (Now - _sleptAt > 4) Pet();
            return;
        }

        double roll = Rng.NextDouble();
        if (roll < 0.012)
        {
            Pet();
        }
        else if (roll < 0.02)
        {
            _pressed = true;
            _demoHand = Center - new Vec2(0, DangleY);
            _demoTicks = 4;
            _demoStep = new Vec2((Rng.NextDouble() < 0.5 ? -1 : 1) * (15 + Rng.NextDouble() * 20), -(12 + Rng.NextDouble() * 14));
            StartCarry();
        }
        // otherwise the behaviour timer lets it roam
    }
}
