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
/// Clay Shooting: click the trap machine for a round of 15 pulls. Clays arc across the closed box of the
/// screen, bouncing off its edges, and shatter when they reach the floor or a window top. Click them
/// first; one shot that breaks two is a double.
/// </summary>
public sealed class ClayGame : MiniGame
{
    const double ClayR = 16, FlatR = ClayR * 0.5, HitPad = 14, BreakR = 36, LagSlack = 18;
    const double Step = 1.0 / 240, Gravity = 900, AirDrag = 0.05, WallBounce = 0.7;
    const double PullEvery = 1.4, FirstPull = 0.8, FastScale = 1.25, DoubleChance = 0.4, GoldenChance = 0.1;
    const double TrapHalfW = 42, TrapH = 70, MouthX = 48, MouthY = 59, LabelW = 150;
    const double ArmTime = 0.35, ArmRest = -12, ArmSwing = -78;
    const int Pulls = 15, DoubleFrom = 6, FastFrom = 11, GoldPoints = 5, DoubleBonus = 5, MaxShards = 90;

    static readonly Color Gold = Color.FromRgb(255, 209, 102);
    static readonly Color[] ClayChips =
    {
        Color.FromRgb(244, 122, 32), Color.FromRgb(255, 168, 76), Color.FromRgb(196, 84, 18), Color.FromRgb(255, 214, 160),
    };
    static readonly Color[] GoldChips = { Gold, Color.FromRgb(255, 240, 180), Color.FromRgb(196, 146, 36), Colors.White };
    static readonly Color[] Dust = { Color.FromRgb(176, 158, 138), Color.FromRgb(124, 110, 96), Color.FromRgb(214, 142, 82) };
    static readonly Color[] Confetti = { Gold, Color.FromRgb(244, 122, 32), Color.FromRgb(6, 214, 160), Colors.White };
    static readonly IBrush[] ClayChipBrushes = ClayChips.Select(c => Art.Brush(c)).ToArray();
    static readonly IBrush[] GoldChipBrushes = GoldChips.Select(c => Art.Brush(c)).ToArray();

    sealed class Clay
    {
        public required Sprite Sprite;
        public required ScaleTransform Squash;
        public required TranslateTransform Mark;
        public Vec2 Pos, Vel;
        public double Gravity, K, Age, Tilt, SpinDeg, SpinRate, Wobble;
        public bool Golden, DemoSkip;
    }

    sealed class Shard
    {
        public required Path El;
        public required TranslateTransform Tr;
        public required RotateTransform Rot;
        public Vec2 Pos, Vel;
        public double Spin, Age, Life;
    }

    readonly Sprite _trap = new() { IsHitTestVisible = false };
    readonly RotateTransform _armRot = new(ArmRest);
    readonly Control _armClay;
    readonly Rectangle _glow = new()
    {
        Width = TrapHalfW * 2 + 8, Height = TrapH + 2, RadiusX = 12, RadiusY = 12, IsHitTestVisible = false,
        Stroke = Art.Brush(120, 255, 255, 255), StrokeThickness = 1.5, StrokeDashArray = new AvaloniaList<double> { 3, 5 },
    };
    readonly TextBlock _labelText = new()
    {
        FontFamily = Fx.Font, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, TextAlignment = TextAlignment.Center,
    };
    readonly Border _label;
    readonly Canvas _clayLayer = new() { IsHitTestVisible = false };
    readonly Canvas _shardLayer = new() { IsHitTestVisible = false };
    readonly List<Clay> _clays = new();
    readonly List<Shard> _shards = new();
    readonly Dictionary<string, double> _lastSound = new();

    double _trapX = double.NaN, _time, _acc, _launchIn = -1, _endIn = -1, _armT, _demoWait;
    int _face = 1, _pull, _hits, _thrown, _score;
    long _bestBefore;
    bool _roundActive, _played;

    public ClayGame(IGameHost host) : base(host)
    {
        _armClay = BuildTrap();
        _label = new Border
        {
            Width = LabelW, CornerRadius = new CornerRadius(8), Background = Art.Brush(235, 18, 20, 28),
            Padding = new Thickness(6, 2, 6, 3), Child = _labelText, IsHitTestVisible = false,
        };
        Layer.Children.Add(_glow);
        Layer.Children.Add(_trap);
        Layer.Children.Add(_label);
        Layer.Children.Add(_clayLayer);
        Layer.Children.Add(_shardLayer);
    }

    public override string Id => "clay";
    public override string Title => "Clay Shooting";

    public override Sprite CreateIcon()
    {
        var icon = MakeClay(8, false, out var squash, out _);
        squash.ScaleY = 0.55;
        icon.Set(default, -15);
        var sight = Art.Brush(220, 255, 255, 255);
        icon.Children.Add(Art.Circle(0, 0, 9.5, null, sight, 1.2));
        icon.Children.Add(Art.PathOf("M-12,0 L-6,0 M6,0 L12,0 M0,-12 L0,-6 M0,6 L0,12", null, sight, 1.2));
        return icon;
    }

    public override HudInfo Hud => new(
        _score.ToString(),
        _roundActive ? L.F("Pull {0}/{1} · hits {2}", _pull, Pulls, _hits)
            : _played ? L.F("Last round {0}/{1} · click the trap to go again", _hits, _thrown)
            : L.T("Click the trap machine to start · shoot the clays"),
        L.F("Best {0}", Host.Stats.Get("clay.best")));

    // ------------------------------------------------------------------ round flow

    public override void Layout()
    {
        var a = Host.Arena;
        PlaceTrap(double.IsNaN(_trapX) ? a.Left + 110 : _trapX);
        foreach (var c in _clays)
        {
            c.Pos.X = Clamp(c.Pos.X, a.Left + ClayR, Math.Max(a.Left + ClayR, a.Right - ClayR));
            c.Pos.Y = Clamp(c.Pos.Y, a.Top + ClayR, Math.Max(a.Top + ClayR, a.Bottom - ClayR));
            DrawClay(c);
        }
        DrawTrap();
        Host.HudChanged();
    }

    public override void Deactivate()
    {
        _acc = 0;
        foreach (var s in _shards) _shardLayer.Children.Remove(s.El);
        _shards.Clear();
    }

    void PlaceTrap(double x)
    {
        var a = Host.Arena;
        double lo = a.Left + LabelW / 2 + 6, hi = Math.Max(lo, a.Right - LabelW / 2 - 6);
        x = Clamp(x, lo, hi);
        var hud = Host.HudBounds;
        if (hud.Width > 0 && TrapArea(x).Intersects(hud.Inflate(16)))
        {
            // slide out from under the scoreboard, to whichever side is closer
            double left = hud.Left - 17 - LabelW / 2, right = hud.Right + 17 + LabelW / 2;
            bool leftFits = left >= lo, rightFits = right <= hi;
            if (leftFits && (!rightFits || x - left <= right - x)) x = left;
            else if (rightFits) x = right;
        }
        _trapX = x;
        _face = x < a.Center.X ? 1 : -1;
    }

    Rect TrapArea(double x) => new(x - LabelW / 2, Host.Arena.Bottom - TrapH - 30, LabelW, TrapH + 30);
    Rect TrapHit => new(_trapX - TrapHalfW, Host.Arena.Bottom - TrapH, TrapHalfW * 2, TrapH);
    Vec2 Mouth => new(_trapX + _face * MouthX, Host.Arena.Bottom - MouthY);

    void StartRound()
    {
        _roundActive = true;
        _score = _hits = _thrown = _pull = 0;
        _launchIn = FirstPull;
        _endIn = -1;
        _acc = 0;
        _bestBefore = Host.Stats.Get("clay.best");
        Host.Sound.Play("fire", 0.6);
        DrawTrap();
        Host.HudChanged();
    }

    void Pull()
    {
        _pull++;
        _launchIn = _pull < Pulls ? PullEvery : -1;
        double k = _pull >= FastFrom ? FastScale : 1;
        var (from, vel, fromTrap) = PickLaunch();
        SpawnClay(from, vel, k);
        if (_pull >= DoubleFrom && Rng.NextDouble() < DoubleChance)
        {
            if (Rng.NextDouble() < 0.5)
            {
                // a true pair: same spot, nearly the same line, so one good shot can take both
                double turn = (2 + Rng.NextDouble() * 2) * (Rng.NextDouble() < 0.5 ? -1 : 1) * Math.PI / 180;
                SpawnClay(from + new Vec2(0, -10), Rotate(vel, turn) * (0.97 + Rng.NextDouble() * 0.06), k);
            }
            else
            {
                var (from2, vel2, fromTrap2) = PickLaunch();
                SpawnClay(from2, vel2, k);
                fromTrap |= fromTrap2;
            }
        }
        if (fromTrap)
        {
            _armT = ArmTime;
            Host.Sound.Play("twang", 0.45, 1.3);
        }
        Host.Sound.Play("whoosh", 0.35 * k, 1.1);
        Host.HudChanged();
    }

    /// <summary>Picks a launch point and a velocity whose arc peaks at a chosen height and lands a chosen distance away.</summary>
    (Vec2 From, Vec2 Vel, bool FromTrap) PickLaunch()
    {
        var a = Host.Arena;
        var hud = Host.HudBounds.Width > 0 ? Host.HudBounds.Inflate(50) : default;
        (Vec2 From, Vec2 Vel, bool FromTrap) pick = default;
        for (int tries = 0; tries < 8; tries++)
        {
            double roll = Rng.NextDouble(), r1 = Rng.NextDouble(), r2 = Rng.NextDouble();
            bool fromTrap = roll < 0.45;
            Vec2 from;
            double dir, height, travel;
            if (fromTrap)
            {
                from = Mouth;
                dir = _face;
                height = a.Height * (0.38 + r1 * 0.34);
                travel = a.Width * (0.45 + r2 * 0.55);
            }
            else if (roll < 0.7)
            {
                // the opposite floor corner throws back toward the trap
                dir = -_face;
                from = new Vec2(_face > 0 ? a.Right - ClayR - 6 : a.Left + ClayR + 6, a.Bottom - 30);
                height = a.Height * (0.4 + r1 * 0.3);
                travel = a.Width * (0.4 + r2 * 0.5);
            }
            else
            {
                // a crosser enters low from either side and skims across the screen
                dir = Rng.NextDouble() < 0.5 ? 1 : -1;
                from = new Vec2(dir > 0 ? a.Left + ClayR + 2 : a.Right - ClayR - 2, a.Bottom - a.Height * (0.28 + r1 * 0.25));
                height = a.Height * (0.08 + r2 * 0.16);
                travel = a.Width * (0.7 + Rng.NextDouble() * 0.4);
            }
            if (Rng.NextDouble() < 0.12) height = from.Y - a.Top + 60; // now and then one clips the top edge
            height = Math.Max(40, height);

            double up = Math.Sqrt(2 * height / Gravity);
            double down = Math.Sqrt(2 * Math.Max(0, a.Bottom - (from.Y - height)) / Gravity);
            var vel = new Vec2(dir * travel / (up + down), -Math.Sqrt(2 * Gravity * height));
            pick = (from, vel, fromTrap);
            var apex = new Vec2(from.X + vel.X * up, from.Y - height);
            if (hud.Width <= 0 || (!hud.Contains(from.ToPoint()) && !hud.Contains(apex.ToPoint()))) break;
        }
        return pick;
    }

    void SpawnClay(Vec2 at, Vec2 vel, double k)
    {
        var a = Host.Arena;
        bool golden = Rng.NextDouble() < GoldenChance;
        var sprite = MakeClay(ClayR, golden, out var squash, out var mark);
        // k speeds the clay up along the same path: velocity scales by k, gravity by k squared
        var c = new Clay
        {
            Sprite = sprite, Squash = squash, Mark = mark, Golden = golden, K = k, Gravity = Gravity * k * k,
            Pos = new Vec2(Clamp(at.X, a.Left + ClayR, Math.Max(a.Left + ClayR, a.Right - ClayR)), Clamp(at.Y, a.Top + ClayR, a.Bottom - ClayR)),
            Vel = vel * k,
            Tilt = (Rng.NextDouble() - 0.5) * 30,
            SpinRate = (Rng.NextDouble() < 0.5 ? -1 : 1) * (700 + Rng.NextDouble() * 500),
            Wobble = Rng.NextDouble() * 6,
            DemoSkip = Rng.NextDouble() < 0.18,
        };
        DrawClay(c);
        _clayLayer.Children.Add(sprite);
        _clays.Add(c);
        _thrown++;
    }

    void RemoveClay(Clay c)
    {
        _clayLayer.Children.Remove(c.Sprite);
        _clays.Remove(c);
    }

    void EndRound()
    {
        _roundActive = false;
        _played = true;
        _launchIn = -1;
        Host.Stats.Max("clay.best", _score);
        bool best = _score > _bestBefore;
        var a = Host.Arena;
        var at = new Vec2(a.Center.X, a.Top + a.Height * 0.3);
        Host.Fx.Popup(at, best ? L.T("NEW BEST!") : L.T("ROUND OVER"), best ? Gold : Colors.White, 38, 2.4,
            L.F("{0} of {1} clays hit · {2} points", _hits, _thrown, _score));
        if (best)
        {
            Host.Fx.Burst(at, Confetti, 40, 520, 700, 7, 1.1);
            Host.Sound.Play("best", 0.8);
        }
        else
        {
            Host.Sound.Play("buzzer", 0.4);
        }
        DrawTrap();
        Host.HudChanged();
    }

    // ------------------------------------------------------------------ input

    public override void CollectHitShapes(List<HitShape> into)
    {
        if (!_roundActive)
        {
            if (!double.IsNaN(_trapX)) into.Add(HitShape.Box(TrapHit));
            return;
        }
        foreach (var c in _clays) into.Add(HitShape.Circle(c.Pos, ClayR + HitPad));
    }

    public override bool PointerDown(Vec2 p, bool right)
    {
        if (_roundActive) Shoot(p);
        else if (!double.IsNaN(_trapX) && TrapHit.Inflate(4).Contains(p.ToPoint())) StartRound();
        return false;
    }

    void Shoot(Vec2 p)
    {
        var hit = _clays.Where(c => (c.Pos - p).Length <= BreakR).ToList();
        if (hit.Count == 0)
        {
            // the clay moved on since the hit shapes were pushed: take the one the click was meant for
            var near = _clays.OrderBy(c => (c.Pos - p).LengthSquared).FirstOrDefault();
            if (near == null || (near.Pos - p).Length > ClayR + HitPad + LagSlack) return;
            hit.Add(near);
        }

        int gained = 0;
        bool golden = false;
        Vec2 sum = default;
        foreach (var c in hit)
        {
            bool rising = c.Vel.Y < 0;
            int pts = (c.Golden ? GoldPoints : 1) + (rising ? 1 : 0);
            gained += pts;
            golden |= c.Golden;
            sum += c.Pos;
            _hits++;
            Host.Stats.Add("clay.hits");
            Host.Fx.Popup(c.Pos - new Vec2(0, 34), L.F("+{0}", pts), c.Golden ? Gold : Colors.White, c.Golden ? 30 : 24, 0.9,
                rising ? L.T("still rising") : null);
            Host.Fx.Burst(c.Pos, c.Golden ? GoldChips : ClayChips, 6, 240, 700, 4, 0.4);
            SpawnShards(c.Pos, c.Vel, c.Golden);
            RemoveClay(c);
        }

        if (hit.Count >= 2)
        {
            gained += DoubleBonus;
            Host.Stats.Add("clay.doubles");
            Host.Fx.Popup(sum / hit.Count - new Vec2(0, 84), L.T("DOUBLE!"), Gold, 36, 1.4, L.F("+{0} bonus", DoubleBonus));
            Host.Sound.Play("score", 0.7);
        }
        _score += gained;
        Host.Stats.Max("clay.best", _score);
        Host.Sound.Play("pop", 0.9, 0.85 + Rng.NextDouble() * 0.35);
        if (golden) Host.Sound.Play("star", 0.6);
        Host.HudChanged();
    }

    public override void Summon(Vec2 p)
    {
        PlaceTrap(p.X);
        DrawTrap();
    }

    // ------------------------------------------------------------------ simulation

    public override bool Update(double dt)
    {
        _time += dt;
        bool busy = false;

        if (_roundActive)
        {
            busy = true;
            if (_launchIn > 0 && (_launchIn -= dt) <= 0) Pull();

            _acc = Math.Min(_acc + dt, 0.1); // a long stall must not replay seconds of flight in one frame
            while (_acc >= Step)
            {
                _acc -= Step;
                for (int i = _clays.Count - 1; i >= 0; i--) StepClay(_clays[i], Step);
            }

            foreach (var c in _clays)
            {
                c.Age += dt;
                c.SpinDeg = (c.SpinDeg + c.SpinRate * dt) % 360;
                c.Wobble += dt * 6;
                DrawClay(c);
            }

            if (_pull >= Pulls && _clays.Count == 0 && _endIn < 0) _endIn = 0.6;
            if (_endIn > 0 && (_endIn -= dt) <= 0)
            {
                _endIn = -1;
                EndRound();
            }
        }
        else
        {
            _acc = 0;
        }

        if (_armT > 0)
        {
            _armT = Math.Max(0, _armT - dt);
            busy = true;
        }
        busy |= UpdateShards(dt);
        DrawTrap();
        return busy;
    }

    void StepClay(Clay c, double h)
    {
        var a = Host.Arena;
        double prevBottom = c.Pos.Y + FlatR;
        c.Vel.Y += c.Gravity * h;
        c.Vel *= 1 - AirDrag * h;
        c.Pos += c.Vel * h;

        // closed box: the sides and the top send the clay back, a little slower
        bool bumped = false;
        if (c.Pos.X - ClayR < a.Left)
        {
            c.Pos.X = a.Left + ClayR;
            if (c.Vel.X < 0) { c.Vel.X = -c.Vel.X * WallBounce; bumped = true; }
        }
        else if (c.Pos.X + ClayR > a.Right)
        {
            c.Pos.X = a.Right - ClayR;
            if (c.Vel.X > 0) { c.Vel.X = -c.Vel.X * WallBounce; bumped = true; }
        }
        if (c.Pos.Y - ClayR < a.Top)
        {
            c.Pos.Y = a.Top + ClayR;
            if (c.Vel.Y < 0) { c.Vel.Y = -c.Vel.Y * WallBounce; bumped = true; }
        }
        if (bumped)
        {
            c.SpinRate = -c.SpinRate * 0.85;
            PlayThrottled("board", 0.22, 1.9);
        }
        if (c.Vel.Y < 0) return;

        double bottom = c.Pos.Y + FlatR;
        if (Host.Platforms.FindLanding(c.Pos.X, prevBottom, bottom, out var top)) Shatter(c, new Vec2(c.Pos.X, top.Y));
        else if (bottom >= a.Bottom) Shatter(c, new Vec2(c.Pos.X, a.Bottom));
    }

    /// <summary>A missed clay breaks on the floor or a window top: dust only, no points.</summary>
    void Shatter(Clay c, Vec2 at)
    {
        RemoveClay(c);
        Host.Fx.Burst(at, Dust, 10, 170, 650, 4, 0.45);
        PlayThrottled("thunk", 0.2, 1.7);
    }

    void SpawnShards(Vec2 at, Vec2 carry, bool golden)
    {
        var brushes = golden ? GoldChipBrushes : ClayChipBrushes;
        int count = golden ? 12 : 9;
        for (int i = 0; i < count && _shards.Count < MaxShards; i++)
        {
            double s = 3 + Rng.NextDouble() * 4;
            var el = Art.PathOf(
                $"M{Art.F(-s)},{Art.F(-s * 0.4)} L{Art.F(s)},{Art.F(-s * 0.2)} L{Art.F(-s * 0.2)},{Art.F(s * 0.6)} Z", brushes[i % brushes.Length]);
            var rot = new RotateTransform(Rng.NextDouble() * 360);
            var tr = new TranslateTransform(at.X, at.Y);
            el.IsHitTestVisible = false;
            el.RenderTransformOrigin = RelativePoint.TopLeft;
            el.RenderTransform = new TransformGroup { Children = { rot, tr } };
            _shardLayer.Children.Add(el);

            var (vx, vy) = Art.Polar(140 + Rng.NextDouble() * 320, Rng.NextDouble() * 360);
            _shards.Add(new Shard
            {
                El = el, Tr = tr, Rot = rot, Pos = at, Vel = new Vec2(vx, vy - 120) + carry * 0.25,
                Spin = (Rng.NextDouble() - 0.5) * 1400, Life = 0.55 + Rng.NextDouble() * 0.4,
            });
        }
    }

    bool UpdateShards(double dt)
    {
        var a = Host.Arena;
        for (int i = _shards.Count - 1; i >= 0; i--)
        {
            var s = _shards[i];
            s.Age += dt;
            if (s.Age >= s.Life)
            {
                _shardLayer.Children.Remove(s.El);
                _shards.RemoveAt(i);
                continue;
            }
            s.Vel.Y += 1300 * dt;
            s.Pos += s.Vel * dt;
            // shards stay in the box too, and settle on the floor
            if (s.Pos.X < a.Left) { s.Pos.X = a.Left; s.Vel.X = Math.Abs(s.Vel.X) * 0.5; }
            else if (s.Pos.X > a.Right) { s.Pos.X = a.Right; s.Vel.X = -Math.Abs(s.Vel.X) * 0.5; }
            if (s.Pos.Y < a.Top) { s.Pos.Y = a.Top; s.Vel.Y = Math.Abs(s.Vel.Y) * 0.5; }
            if (s.Pos.Y > a.Bottom - 2)
            {
                s.Pos.Y = a.Bottom - 2;
                s.Vel = new Vec2(s.Vel.X * 0.6, -Math.Abs(s.Vel.Y) * 0.25);
                s.Spin *= 0.5;
            }
            s.Rot.Angle += s.Spin * dt;
            s.Tr.X = s.Pos.X;
            s.Tr.Y = s.Pos.Y;
            double k = s.Age / s.Life;
            s.El.Opacity = k < 0.5 ? 1 : 1 - (k - 0.5) / 0.5;
        }
        return _shards.Count > 0;
    }

    // ------------------------------------------------------------------ visuals

    void DrawTrap()
    {
        if (double.IsNaN(_trapX)) return;
        var a = Host.Arena;
        _trap.Set(new Vec2(_trapX, a.Bottom));
        _trap.FlipX = _face;

        double k = 1 - _armT / ArmTime;
        _armRot.Angle = _armT <= 0 ? ArmRest
            : k < 0.3 ? ArmRest + (ArmSwing - ArmRest) * k / 0.3
            : ArmSwing + (ArmRest - ArmSwing) * (k - 0.3) / 0.7;
        _armClay.IsVisible = _armT <= 0 || k > 0.85; // thrown, then reloaded

        bool idle = !_roundActive;
        _label.IsVisible = _glow.IsVisible = idle;
        if (!idle) return;
        _labelText.Text = L.T("click to start");
        Canvas.SetLeft(_label, _trapX - LabelW / 2);
        Canvas.SetTop(_label, a.Bottom - TrapH - 30);
        Canvas.SetLeft(_glow, _trapX - TrapHalfW - 4);
        Canvas.SetTop(_glow, a.Bottom - TrapH - 4);
    }

    static void DrawClay(Clay c)
    {
        var (mx, my) = Art.Polar(ClayR * 0.72, c.SpinDeg);
        c.Mark.X = mx;
        c.Mark.Y = my;
        c.Squash.ScaleY = 0.42 + 0.1 * Math.Sin(c.Wobble);
        c.Sprite.Scale = Math.Min(1, 0.3 + c.Age * 6);
        c.Sprite.Set(c.Pos, c.Tilt + Clamp(c.Vel.X * 0.012, -16, 16));
    }

    /// <summary>Side view of the trap facing +x, origin at the middle of its feet.</summary>
    Control BuildTrap()
    {
        var r = _trap.Rotor;
        r.Children.Add(Art.At(new Ellipse { Width = 78, Height = 8, Fill = Art.Brush(70, 0, 0, 0) }, -39, -4));
        r.Children.Add(Art.PathOf("M-26,0 L-18,-17 M26,0 L18,-17", null, Art.Brush("#2B2F36"), 3.5));
        r.Children.Add(Art.At(new Rectangle { Width = 64, Height = 5, RadiusX = 2, RadiusY = 2, Fill = Art.Brush("#3A3F47") }, -32, -20));
        r.Children.Add(Art.At(new Rectangle
        {
            Width = 54, Height = 24, RadiusX = 4, RadiusY = 4, Stroke = Art.Brush("#1B2A14"), StrokeThickness = 1,
            Fill = Vertical(Color.FromRgb(98, 146, 72), Color.FromRgb(44, 72, 32)),
        }, -27, -42));
        r.Children.Add(Art.At(new Rectangle { Width = 46, Height = 3, RadiusX = 1.5, RadiusY = 1.5, Fill = Art.Brush(70, 255, 255, 255) }, -23, -38));

        // magazine of spare clays
        r.Children.Add(Art.At(new Rectangle
        {
            Width = 16, Height = 20, Stroke = Art.Brush("#353B43"), StrokeThickness = 1,
            Fill = Vertical(Color.FromRgb(160, 168, 178), Color.FromRgb(88, 96, 106)),
        }, -23, -60));
        for (int i = 0; i < 2; i++)
            r.Children.Add(Art.At(new Ellipse { Width = 20, Height = 6, Fill = ClayChipBrushes[0], Stroke = Art.Brush("#7A3408"), StrokeThickness = 1 }, -25, -64 - i * 3));

        r.Children.Add(Art.At(new Rectangle { Width = 6, Height = 8, Fill = Art.Brush("#2B2F36") }, 7, -48));
        var arm = new Canvas { RenderTransformOrigin = RelativePoint.TopLeft, RenderTransform = _armRot };
        arm.Children.Add(Art.At(new Rectangle
        {
            Width = 38, Height = 4, RadiusX = 2, RadiusY = 2, Fill = Art.Brush("#C9D1DC"), Stroke = Art.Brush("#59616B"), StrokeThickness = 0.8,
        }, 0, -2));
        var loaded = Art.At(new Ellipse { Width = 20, Height = 6, Fill = ClayChipBrushes[0], Stroke = Art.Brush("#7A3408"), StrokeThickness = 1 }, 24, -8);
        arm.Children.Add(loaded);
        arm.Children.Add(Art.Circle(0, 0, 3, Art.Brush("#1B1B1B")));
        r.Children.Add(Art.At(arm, 10, -46));
        return loaded;
    }

    /// <summary>A clay disc seen at an angle: a circle squashed into an ellipse, with a mark that circles as it spins.</summary>
    static Sprite MakeClay(double r, bool golden, out ScaleTransform squash, out TranslateTransform mark)
    {
        var (light, body, dark) = golden
            ? (Color.FromRgb(255, 244, 196), Color.FromRgb(255, 204, 64), Color.FromRgb(150, 104, 16))
            : (Color.FromRgb(255, 186, 110), Color.FromRgb(240, 118, 30), Color.FromRgb(122, 52, 8));
        var s = new Sprite { IsHitTestVisible = false };
        if (golden) s.Children.Insert(0, Art.Circle(0, 0, r * 1.6, Art.Brush(50, 255, 214, 64)));

        squash = new ScaleTransform(1, 0.45);
        var disc = new Canvas { RenderTransformOrigin = RelativePoint.TopLeft, RenderTransform = squash };
        disc.Children.Add(Art.Circle(0, r * 0.45, r, Art.Brush(dark))); // underside peeking out gives the disc some thickness
        var face = new RadialGradientBrush { GradientOrigin = new RelativePoint(0.4, 0.3, RelativeUnit.Relative) };
        face.GradientStops.Add(new GradientStop(light, 0));
        face.GradientStops.Add(new GradientStop(body, 0.55));
        face.GradientStops.Add(new GradientStop(Art.Blend(body, dark, 0.45), 1));
        disc.Children.Add(Art.Circle(0, 0, r, face, Art.Brush(dark), 1.2));
        disc.Children.Add(Art.Circle(0, 0, r * 0.58, null, Art.Brush(Color.FromArgb(150, dark.R, dark.G, dark.B)), 1.2));
        mark = new TranslateTransform(r * 0.72, 0);
        disc.Children.Add(Art.At(new Ellipse
        {
            Width = r * 0.36, Height = r * 0.36, Fill = Art.Brush(Color.FromArgb(190, dark.R, dark.G, dark.B)), RenderTransform = mark,
        }, -r * 0.18, -r * 0.18));
        s.Rotor.Children.Add(disc);
        return s;
    }

    static LinearGradientBrush Vertical(Color top, Color bottom)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        };
        brush.GradientStops.Add(new GradientStop(top, 0));
        brush.GradientStops.Add(new GradientStop(bottom, 1));
        return brush;
    }

    static Vec2 Rotate(Vec2 v, double rad)
    {
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        return new Vec2(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos);
    }

    void PlayThrottled(string name, double vol, double pitch = 1)
    {
        if (_lastSound.TryGetValue(name, out double t) && _time - t < 0.05) return;
        _lastSound[name] = _time;
        Host.Sound.Play(name, vol, pitch);
    }

    public override void DemoTick()
    {
        if (!_roundActive)
        {
            _demoWait += 0.15;
            if (_demoWait < 2.4) return; // let the round-over popup show first
            _demoWait = 0;
            StartRound();
            return;
        }
        _demoWait = 0;

        var ready = _clays.Where(c => c.Age > 0.25 && !c.DemoSkip).ToList();
        if (ready.Count == 0) return;

        // two clays close together in the upper part of their arcs: go for the double
        for (int i = 0; i < ready.Count; i++)
        {
            for (int j = i + 1; j < ready.Count; j++)
            {
                var (p, q) = (ready[i], ready[j]);
                if ((p.Pos - q.Pos).Length <= BreakR * 1.8 && Math.Abs(p.Vel.Y) < 450 * p.K && Math.Abs(q.Vel.Y) < 450 * q.K)
                {
                    Shoot((p.Pos + q.Pos) / 2);
                    return;
                }
            }
        }

        var target = ready.Where(c => Math.Abs(c.Vel.Y) < 220 * c.K).OrderBy(c => Math.Abs(c.Vel.Y) / c.K).FirstOrDefault();
        // a clay that bounced off the top never slows down near an apex: take it before it lands
        target ??= ready.FirstOrDefault(c => c.Vel.Y > 0 && c.Pos.Y > Host.Arena.Bottom - 160);
        if (target != null) Shoot(target.Pos + new Vec2((Rng.NextDouble() - 0.5) * 10, (Rng.NextDouble() - 0.5) * 10));
    }
}
