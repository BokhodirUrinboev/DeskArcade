using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>
/// Pool, clear-the-table style: a see-through pool table over the desktop with fifteen numbered balls in
/// a rack. Press on the cue ball, drag back and let go; the further the drag, the harder the shot. The aim
/// line shows the first ball the cue ball would meet and where that ball would go. Pot all fifteen in as
/// few shots as possible; potting the cue ball (a scratch) costs a shot. Only the cue ball takes the mouse.
/// </summary>
public sealed class PoolGame : MiniGame
{
    const double MaxWidth = 1100, WidthShare = 0.72, CushionW = 10, RailW = 28, Reach = 30;
    const double MaxPull = 180, MinPull = 8, MaxShot = 2600, SinkTime = 0.25, StickLen = 380;
    const int Balls = 15;

    static readonly Color Gold = Color.FromRgb(255, 209, 102);
    static readonly Color[] Confetti = { Gold, Colors.White, Color.FromRgb(6, 214, 160), Color.FromRgb(77, 163, 255) };
    static readonly Color[] BallColors =
    {
        Color.FromRgb(245, 197, 24), Color.FromRgb(31, 79, 209), Color.FromRgb(214, 40, 40), Color.FromRgb(106, 44, 145),
        Color.FromRgb(247, 127, 0), Color.FromRgb(27, 127, 59), Color.FromRgb(123, 30, 30), Color.FromRgb(20, 20, 22),
    };
    // Felt: a fixed pool-table green whatever the theme, translucent so the desktop shows faintly through it
    static readonly Color Felt = Color.FromArgb(205, 24, 110, 62);

    sealed class Ball
    {
        public required Disc Body;
        public Sprite Sprite = new();
        public int Number;
        public double SinkT = -1;
        public Vec2 SinkFrom, SinkTo;
    }

    readonly DiscTable _table = new() { Restitution = 0.95, CushionRestitution = 0.75, Friction = 110, Damping = 0.3 };
    readonly Canvas _tableLayer = new() { IsHitTestVisible = false };
    readonly Canvas _ballLayer = new() { IsHitTestVisible = false };
    readonly Canvas _aimLayer = new() { IsHitTestVisible = false };
    readonly Line _aimLine = Dashed(170);
    readonly Line _objLine = Dashed(140);
    readonly Ellipse _ghost = new() { Stroke = Art.Brush(200, 255, 255, 255), StrokeThickness = 1.5, IsHitTestVisible = false };
    readonly Sprite _stick = MakeStick();
    readonly Ball[] _balls = new Ball[Balls + 1]; // [0] is the cue ball
    readonly List<Ball> _sinking = new();
    readonly Dictionary<string, double> _lastSound = new();

    Rect _felt;
    double _r, _time;
    int _shots, _pottedThisShot;
    bool _placed, _aiming, _inShot, _scratch, _cleared;
    Vec2 _pull;

    Ball Cue => _balls[0];

    public PoolGame(IGameHost host) : base(host)
    {
        for (int n = 0; n <= Balls; n++)
            _balls[n] = new Ball { Body = _table.Add(default, 12, 1, n), Number = n };
        _table.Collided += (a, b, speed) =>
        {
            if (speed > 30) PlayThrottled("click", Math.Min(0.9, 0.08 + speed / 1600), 0.9 + Rng.NextDouble() * 0.2);
        };
        _table.Cushion += (d, speed) =>
        {
            if (speed > 60) PlayThrottled("board", Math.Min(0.5, speed / 2600), 1.1);
        };
        _table.Sunk += OnSunk;

        _aimLayer.Children.Add(_aimLine);
        _aimLayer.Children.Add(_objLine);
        _aimLayer.Children.Add(_ghost);
        _aimLayer.IsVisible = false;
        _stick.IsVisible = false;

        Layer.Children.Add(_tableLayer);
        Layer.Children.Add(_aimLayer);
        Layer.Children.Add(_ballLayer);
        Layer.Children.Add(_stick);
    }

    public override string Id => "pool";
    public override string Title => "Pool";

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        s.Rotor.Children.Add(Art.PathOf("M-11,9 L-3,1", null, Art.Brush("#C99A5B"), 2.2));
        s.Rotor.Children.Add(Art.Circle(-1, -1, 4.2, Brushes.White, Art.Brush("#8A9099"), 0.8));
        s.Rotor.Children.Add(Art.Circle(6, -5, 6, Art.Brush("#141416")));
        s.Rotor.Children.Add(Art.Circle(6, -5, 2.6, Brushes.White));
        return s;
    }

    int BallsLeft
    {
        get
        {
            int n = 0;
            for (int i = 1; i <= Balls; i++)
                if (!_balls[i].Body.Sunk) n++;
            return n;
        }
    }

    bool Ready => !_inShot && _sinking.Count == 0 && !Cue.Body.Sunk;

    public override HudInfo Hud
    {
        get
        {
            long best = Host.Stats.Get("pool.best");
            return new(
                _shots.ToString(),
                _cleared ? L.F("Cleared in {0} shots · press the cue ball to rack again", _shots) : L.F("Shots {0} · balls left {1}", _shots, BallsLeft),
                best > 0 ? L.F("Best {0}", best) : L.T("Best —"));
        }
    }

    // ------------------------------------------------------------------ table

    public override void Layout()
    {
        var old = _felt;
        double oldR = _r;
        _felt = PlaceTable();
        _r = Math.Clamp(_felt.Width / 92, 8, 12);
        _table.Bounds = _felt;
        _table.Pockets.Clear();
        double r = _r, f = _felt.X, t = _felt.Y, w = _felt.Width, h = _felt.Height;
        foreach (var (x, y) in new[] { (f - r * 0.5, t - r * 0.5), (f + w + r * 0.5, t - r * 0.5), (f - r * 0.5, t + h + r * 0.5), (f + w + r * 0.5, t + h + r * 0.5) })
            _table.Pockets.Add(new DiscTable.Pocket(new Vec2(x, y), r * 2));
        _table.Pockets.Add(new DiscTable.Pocket(new Vec2(f + w / 2, t - r * 0.9), r * 1.75));
        _table.Pockets.Add(new DiscTable.Pocket(new Vec2(f + w / 2, t + h + r * 0.9), r * 1.75));

        foreach (var b in _balls) b.Body.R = r;
        if (!_placed)
        {
            _placed = true;
            Rack();
        }
        else if (old != _felt)
        {
            // the table moved or changed size: every ball keeps its place on the felt
            double k = _felt.Width / Math.Max(1, old.Width);
            foreach (var b in _balls)
            {
                if (b.Body.Sunk) continue;
                b.Body.Pos = new Vec2(_felt.X + (b.Body.Pos.X - old.X) * k, _felt.Y + (b.Body.Pos.Y - old.Y) * k);
                b.Body.Vel *= k;
            }
        }
        if (oldR != _r) RebuildBalls();
        DrawTable();
        Draw();
        Host.HudChanged();
    }

    public override void Deactivate()
    {
        _aiming = false;
        HideAim();
    }

    /// <summary>Centred on the screen and clear of the HUD, shrinking if it has to.</summary>
    Rect PlaceTable()
    {
        var a = Host.Arena;
        var hud = Host.HudBounds.Inflate(10);
        double border = RailW + CushionW;
        double w = Math.Min(a.Width * WidthShare, MaxWidth);
        w = Math.Min(w, (a.Height - border * 2 - 30) * 2);
        for (int attempt = 0; attempt < 10; attempt++, w *= 0.92)
        {
            double h = w / 2;
            var felt = new Rect(a.Center.X - w / 2, a.Center.Y - h / 2, w, h);
            var outer = felt.Inflate(border);
            if (hud.Width <= 0 || !outer.Intersects(hud)) return felt;
            // slide it away from the HUD: below, to the left, above or to the right, whichever fits
            foreach (var shift in new[]
            {
                new Vector(0, hud.Bottom + 1 - outer.Top), new Vector(hud.Left - 1 - outer.Right, 0),
                new Vector(0, hud.Top - 1 - outer.Bottom), new Vector(hud.Right + 1 - outer.Left, 0),
            })
            {
                var moved = outer.Translate(shift);
                if (moved.Left >= a.Left + 4 && moved.Right <= a.Right - 4 && moved.Top >= a.Top + 4 && moved.Bottom <= a.Bottom - 4 && !moved.Intersects(hud))
                    return felt.Translate(shift);
            }
        }
        double hh = w / 2;
        return new Rect(a.Center.X - w / 2, a.Center.Y - hh / 2, w, hh);
    }

    Vec2 HeadSpot => new(_felt.X + _felt.Width * 0.25, _felt.Center.Y);

    void Rack()
    {
        _shots = 0;
        _cleared = false;
        _inShot = false;
        foreach (var b in _sinking) b.Sprite.IsVisible = false;
        _sinking.Clear();

        // the 1 at the apex, the 8 in the middle of the third row, a solid and a stripe in the back corners
        var numbers = new List<int> { 2, 3, 4, 5, 6, 7, 9, 10, 11, 12, 13, 14, 15 };
        for (int i = numbers.Count - 1; i > 0; i--)
        {
            int j = Rng.Next(i + 1);
            (numbers[i], numbers[j]) = (numbers[j], numbers[i]);
        }
        int solid = numbers.Find(n => n < 8), stripe = numbers.Find(n => n > 8);
        numbers.Remove(solid);
        numbers.Remove(stripe);
        bool solidLeft = Rng.NextDouble() < 0.5;
        var slots = new int[Balls];
        slots[0] = 1;
        slots[4] = 8;
        slots[10] = solidLeft ? solid : stripe;
        slots[14] = solidLeft ? stripe : solid;
        int next = 0;
        for (int i = 0; i < Balls; i++)
            if (slots[i] == 0) slots[i] = numbers[next++];

        double r = _r, gap = r * 2.02;
        var foot = new Vec2(_felt.X + _felt.Width * 0.72, _felt.Center.Y);
        int slot = 0;
        for (int row = 0; row < 5; row++)
        {
            for (int i = 0; i <= row; i++, slot++)
            {
                var b = _balls[slots[slot]];
                b.Body.Pos = foot + new Vec2(row * gap * 0.866, (i - row / 2.0) * gap);
                b.Body.Vel = default;
                b.Body.Sunk = false;
                b.SinkT = -1;
                b.Sprite.IsVisible = true;
                b.Sprite.Scale = 1;
                b.Sprite.Opacity = 1;
            }
        }
        PlaceCue();
        Host.HudChanged();
    }

    void PlaceCue()
    {
        var cue = Cue;
        cue.Body.Sunk = false;
        cue.Body.Vel = default;
        cue.Body.Pos = FreeSpot(HeadSpot);
        cue.SinkT = -1;
        cue.Sprite.IsVisible = true;
        cue.Sprite.Scale = 1;
        cue.Sprite.Opacity = 1;
    }

    /// <summary>The spot itself, or the nearest free place along the head string if a ball is sitting on it.</summary>
    Vec2 FreeSpot(Vec2 spot)
    {
        for (int k = 0; k < 40; k++)
        {
            double dy = (k + 1) / 2 * (_r * 2 + 2) * (k % 2 == 0 ? 1 : -1);
            var p = new Vec2(spot.X - (k >= 20 ? _r * 3 : 0), Clamp(spot.Y + dy * (k == 0 ? 0 : 1), _felt.Top + _r, _felt.Bottom - _r));
            bool free = true;
            for (int i = 1; i <= Balls && free; i++)
                if (!_balls[i].Body.Sunk && (_balls[i].Body.Pos - p).Length < _r * 2 + 1) free = false;
            if (free) return p;
        }
        return spot;
    }

    // ------------------------------------------------------------------ input

    public override void CollectHitShapes(List<HitShape> into)
    {
        if (Ready) into.Add(HitShape.Circle(Cue.Body.Pos, Reach));
    }

    public override bool PointerDown(Vec2 p, bool right)
    {
        if (!Ready || (p - Cue.Body.Pos).Length > Reach) return false;
        if (_cleared) Rack();
        _aiming = true;
        _pull = default;
        return true;
    }

    public override void PointerUp(Vec2 p)
    {
        if (!_aiming) return;
        _aiming = false;
        HideAim();
        double len = _pull.Length;
        if (len >= MinPull) Shoot(-_pull / len, SpeedFor(len));
    }

    static double SpeedFor(double pull) => MaxShot * Math.Pow(Math.Min(pull, MaxPull) / MaxPull, 1.3);

    void Shoot(Vec2 dir, double speed)
    {
        if (!Ready) return;
        Cue.Body.Vel = dir * Math.Min(speed, MaxShot);
        _shots++;
        _inShot = true;
        _pottedThisShot = 0;
        _scratch = false;
        Host.Sound.Play("click", 0.3 + 0.5 * speed / MaxShot, 0.7);
        Host.HudChanged();
    }

    public override void Summon(Vec2 p)
    {
        // the table stays put; only the cue ball's nearest free spot behind the head string is offered
        if (!Ready || _aiming) return;
        var spot = new Vec2(Clamp(p.X, _felt.Left + _r, _felt.Left + _felt.Width * 0.25), Clamp(p.Y, _felt.Top + _r, _felt.Bottom - _r));
        Cue.Body.Pos = FreeSpot(spot);
        Draw();
    }

    // ------------------------------------------------------------------ simulation

    public override bool Update(double dt)
    {
        _time += dt;
        if (_aiming)
        {
            var pull = Host.Pointer - Cue.Body.Pos;
            if (pull.Length > MaxPull) pull *= MaxPull / pull.Length;
            _pull = pull;
            UpdateAim();
        }

        if (_inShot) _table.Advance(dt);
        for (int i = _sinking.Count - 1; i >= 0; i--)
        {
            var b = _sinking[i];
            b.SinkT += dt;
            double k = Math.Min(1, b.SinkT / SinkTime);
            b.Sprite.Set(b.SinkFrom + (b.SinkTo - b.SinkFrom) * k);
            b.Sprite.Scale = 1 - 0.6 * k;
            b.Sprite.Opacity = 1 - k;
            if (k < 1) continue;
            b.Sprite.IsVisible = false;
            _sinking.RemoveAt(i);
        }
        if (_inShot && _table.AllStill && _sinking.Count == 0) ResolveShot();

        Draw();
        return _aiming || _inShot || _sinking.Count > 0;
    }

    void OnSunk(Disc d, int pocket)
    {
        var b = _balls[d.Tag];
        b.SinkT = 0;
        b.SinkFrom = d.Pos;
        b.SinkTo = _table.Pockets[pocket].Pos;
        _sinking.Add(b);
        PlayThrottled("thunk", 0.6, 0.7);
        if (b.Number == 0)
        {
            _scratch = true;
            return;
        }
        _pottedThisShot++;
        Host.Stats.Add("pool.potted");
        Host.HudChanged();
    }

    void ResolveShot()
    {
        _inShot = false;
        var top = new Vec2(_felt.Center.X, _felt.Top - RailW - 30);
        if (_pottedThisShot > 0) Host.Stats.Max("pool.multi", _pottedThisShot);
        if (_pottedThisShot >= 2)
        {
            Host.Fx.Popup(top, L.F("{0} balls in one shot!", _pottedThisShot), Gold, 30, 1.4);
            Host.Sound.Play("score", 0.6);
        }
        if (_scratch)
        {
            _shots++;
            PlaceCue();
            Host.Fx.Popup(HeadSpot - new Vec2(0, 50), L.T("Scratch +1"), Color.FromRgb(255, 150, 150), 26, 1.3);
            Host.Sound.Play("buzzer", 0.25);
        }
        if (BallsLeft == 0) Cleared();
        Host.HudChanged();
    }

    void Cleared()
    {
        _cleared = true;
        long before = Host.Stats.Get("pool.best");
        Host.Stats.Add("pool.cleared");
        Host.Stats.Min("pool.best", _shots);
        bool best = before == 0 || _shots < before;
        var a = Host.Arena;
        var at = new Vec2(_felt.Center.X, Math.Max(a.Top + 60, _felt.Center.Y - 40));
        Host.Fx.Popup(at, L.F("CLEARED in {0} shots", _shots), best ? Gold : Colors.White, 38, 2.4, best ? L.T("NEW BEST!") : null);
        Host.Fx.Burst(at, Confetti, best ? 44 : 26, 520, 700, 7, 1.1);
        Host.Sound.Play(best ? "best" : "done", 0.8);
    }

    // ------------------------------------------------------------------ aiming

    void UpdateAim()
    {
        double len = _pull.Length;
        if (len < MinPull)
        {
            HideAim();
            return;
        }
        var dir = -_pull / len;
        var from = Cue.Body.Pos;
        double r = _r;
        var angle = Math.Atan2(-dir.Y, -dir.X) * 180 / Math.PI;
        _stick.Set(from - dir * (r + 4 + len * 0.45), angle);
        _stick.IsVisible = true;

        // cast the cue ball along the aim: stop at the first ball (ghost ball there) or at the cushion
        if (_table.Cast(from, dir, r, Cue.Body, out var hit) is double t && hit != null)
        {
            var contact = from + dir * t;
            SetLine(_aimLine, from + dir * r, contact - dir * r);
            _ghost.Width = _ghost.Height = r * 2;
            Canvas.SetLeft(_ghost, contact.X - r);
            Canvas.SetTop(_ghost, contact.Y - r);
            _ghost.IsVisible = true;
            var go = (hit.Pos - contact).Normalized();
            SetLine(_objLine, hit.Pos + go * r, hit.Pos + go * (r + 30 + 80 * Math.Max(0, Vec2.Dot(go, dir))));
            _objLine.IsVisible = true;
        }
        else
        {
            SetLine(_aimLine, from + dir * r, from + dir * _table.CastToCushion(from, dir, r));
            _ghost.IsVisible = false;
            _objLine.IsVisible = false;
        }
        _aimLayer.IsVisible = true;
    }

    void HideAim()
    {
        _aimLayer.IsVisible = false;
        _stick.IsVisible = false;
    }

    static void SetLine(Line line, Vec2 a, Vec2 b)
    {
        line.StartPoint = a.ToPoint();
        line.EndPoint = b.ToPoint();
    }

    static Line Dashed(byte alpha) => new()
    {
        Stroke = Art.Brush(alpha, 255, 255, 255), StrokeThickness = 2, StrokeDashArray = new AvaloniaList<double> { 3, 3 }, IsHitTestVisible = false,
    };

    // ------------------------------------------------------------------ visuals

    void Draw()
    {
        foreach (var b in _balls)
            if (!b.Body.Sunk) b.Sprite.Set(b.Body.Pos);
    }

    void RebuildBalls()
    {
        _ballLayer.Children.Clear();
        foreach (var b in _balls)
        {
            var s = MakeBall(b.Number, _r);
            s.IsVisible = !b.Body.Sunk;
            b.Sprite = s;
            _ballLayer.Children.Add(s);
        }
    }

    void DrawTable()
    {
        _tableLayer.Children.Clear();
        var felt = _felt;
        var cushion = felt.Inflate(CushionW);
        var outer = cushion.Inflate(RailW);

        var wood = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative) };
        wood.GradientStops.Add(new GradientStop(Color.FromRgb(122, 70, 34), 0));
        wood.GradientStops.Add(new GradientStop(Color.FromRgb(94, 52, 24), 0.5));
        wood.GradientStops.Add(new GradientStop(Color.FromRgb(70, 38, 16), 1));
        _tableLayer.Children.Add(Art.At(new Rectangle { Width = outer.Width, Height = outer.Height, RadiusX = 14, RadiusY = 14, Fill = Art.Brush(80, 0, 0, 0) }, outer.X + 4, outer.Y + 6));
        // the rails are four strips, so the felt in the middle stays see-through
        var railPath = $"M{Art.F(outer.Left)},{Art.F(outer.Top)} H{Art.F(outer.Right)} V{Art.F(outer.Bottom)} H{Art.F(outer.Left)} Z " +
                       $"M{Art.F(cushion.Left)},{Art.F(cushion.Top)} V{Art.F(cushion.Bottom)} H{Art.F(cushion.Right)} V{Art.F(cushion.Top)} Z";
        var rails = Art.PathOf(railPath, wood, Art.Brush("#3A1F0C"), 1.5);
        _tableLayer.Children.Add(rails);

        var cushionFill = Art.Brush(Color.FromArgb(235, 18, 88, 48));
        var cushionPath = $"M{Art.F(cushion.Left)},{Art.F(cushion.Top)} H{Art.F(cushion.Right)} V{Art.F(cushion.Bottom)} H{Art.F(cushion.Left)} Z " +
                          $"M{Art.F(felt.Left)},{Art.F(felt.Top)} V{Art.F(felt.Bottom)} H{Art.F(felt.Right)} V{Art.F(felt.Top)} Z";
        _tableLayer.Children.Add(Art.PathOf(cushionPath, cushionFill));
        _tableLayer.Children.Add(Art.At(new Rectangle { Width = felt.Width, Height = felt.Height, Fill = Art.Brush(Felt) }, felt.X, felt.Y));

        // head string, head spot and foot spot
        double headX = felt.X + felt.Width * 0.25;
        _tableLayer.Children.Add(Art.PathOf($"M{Art.F(headX)},{Art.F(felt.Top)} L{Art.F(headX)},{Art.F(felt.Bottom)}", null, Art.Brush(45, 255, 255, 255), 1));
        _tableLayer.Children.Add(Art.Circle(headX, felt.Center.Y, 2, Art.Brush(110, 255, 255, 255)));
        _tableLayer.Children.Add(Art.Circle(felt.X + felt.Width * 0.72, felt.Center.Y, 2, Art.Brush(110, 255, 255, 255)));

        // diamonds on the rails
        var pearl = Art.Brush("#EDE6D6");
        double midRail = RailW / 2;
        foreach (double k in new[] { 0.125, 0.25, 0.375, 0.625, 0.75, 0.875 })
        {
            double x = felt.X + felt.Width * k;
            _tableLayer.Children.Add(Diamond(x, outer.Top + midRail, pearl));
            _tableLayer.Children.Add(Diamond(x, outer.Bottom - midRail, pearl));
        }
        foreach (double k in new[] { 0.25, 0.5, 0.75 })
        {
            double y = felt.Y + felt.Height * k;
            _tableLayer.Children.Add(Diamond(outer.Left + midRail, y, pearl));
            _tableLayer.Children.Add(Diamond(outer.Right - midRail, y, pearl));
        }

        // pockets over the rails
        foreach (var p in _table.Pockets)
        {
            _tableLayer.Children.Add(Art.Circle(p.Pos.X, p.Pos.Y, p.R + 3, Art.Brush("#2B2B2B")));
            _tableLayer.Children.Add(Art.Circle(p.Pos.X, p.Pos.Y, p.R, Art.Brush("#050506")));
        }
    }

    static Path Diamond(double x, double y, IBrush fill) =>
        Art.PathOf($"M{Art.F(x)},{Art.F(y - 4)} L{Art.F(x + 2.5)},{Art.F(y)} L{Art.F(x)},{Art.F(y + 4)} L{Art.F(x - 2.5)},{Art.F(y)} Z", fill);

    void PlayThrottled(string name, double vol, double pitch)
    {
        if (_lastSound.TryGetValue(name, out double t) && _time - t < 0.035) return;
        _lastSound[name] = _time;
        Host.Sound.Play(name, vol, pitch);
    }

    /// <summary>A numbered ball from above: solids 1-7, the black 8, stripes 9-15 with a white band; 0 is the cue ball.</summary>
    static Sprite MakeBall(int number, double r)
    {
        var s = new Sprite { IsHitTestVisible = false };
        s.Children.Add(Art.Circle(1.2, 2, r, Art.Brush(80, 0, 0, 0)));
        bool stripe = number > 8;
        var color = number == 0 ? Color.FromRgb(246, 244, 236) : BallColors[(number - 1) % 8];
        var clip = new EllipseGeometry(new Rect(-r, -r, r * 2, r * 2));
        var body = new Canvas { Clip = clip };
        body.Children.Add(Art.Circle(0, 0, r, Art.Brush(stripe ? Color.FromRgb(246, 244, 236) : color)));
        if (stripe) body.Children.Add(Art.At(new Rectangle { Width = r * 2, Height = r * 1.15, Fill = Art.Brush(color) }, -r, -r * 0.575));
        // soft shading: light from the top left, a darker rim
        var shade = new RadialGradientBrush { GradientOrigin = new RelativePoint(0.35, 0.3, RelativeUnit.Relative) };
        shade.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.55));
        shade.GradientStops.Add(new GradientStop(Color.FromArgb(90, 0, 0, 0), 1));
        body.Children.Add(Art.Circle(0, 0, r, shade));
        s.Children.Add(body);
        if (number > 0)
        {
            double nr = r * 0.52;
            s.Children.Add(Art.Circle(0, 0, nr, Brushes.White));
            double size = number >= 10 ? r * 0.62 : r * 0.72;
            s.Children.Add(Art.At(new TextBlock
            {
                Text = number.ToString(), FontFamily = Fx.Font, FontSize = size, FontWeight = FontWeight.Bold,
                Foreground = Art.Brush("#111111"), Width = nr * 2, TextAlignment = TextAlignment.Center, IsHitTestVisible = false,
            }, -nr, -size * 0.68));
        }
        s.Children.Add(Art.At(new Ellipse { Width = r * 0.6, Height = r * 0.38, Fill = Art.Brush(120, 255, 255, 255), RenderTransform = new RotateTransform(-30) }, -r * 0.62, -r * 0.66));
        s.Children.Add(Art.Circle(0, 0, r, null, Art.Brush(70, 0, 0, 0), 0.8));
        return s;
    }

    /// <summary>The cue stick, tip at the origin, lying along +x.</summary>
    static Sprite MakeStick()
    {
        var s = new Sprite { IsHitTestVisible = false };
        double L = StickLen;
        s.Rotor.Children.Add(Art.PathOf($"M3,4 L{Art.F(L + 3)},9 L{Art.F(L + 3)},-1 Z", Art.Brush(60, 0, 0, 0))); // shadow
        s.Rotor.Children.Add(Art.PathOf($"M0,-1.6 L{Art.F(L * 0.66)},-3.2 L{Art.F(L * 0.66)},3.2 L0,1.6 Z", Art.Brush("#E3C48F")));
        s.Rotor.Children.Add(Art.PathOf($"M{Art.F(L * 0.66)},-3.2 L{Art.F(L)},-4.8 L{Art.F(L)},4.8 L{Art.F(L * 0.66)},3.2 Z", Art.Brush("#4A2412")));
        s.Rotor.Children.Add(Art.PathOf($"M{Art.F(L * 0.66)},-3.2 L{Art.F(L * 0.66)},3.2", null, Art.Brush("#D9D2C0"), 2));
        s.Rotor.Children.Add(Art.At(new Rectangle { Width = 9, Height = 3.2, Fill = Art.Brush("#F4F1EA") }, 3, -1.6));
        s.Rotor.Children.Add(Art.At(new Rectangle { Width = 3, Height = 3.2, Fill = Art.Brush("#3F7FBF") }, 0, -1.6));
        return s;
    }

    // ------------------------------------------------------------------ demo

    public override void DemoTick()
    {
        if (!Ready || _aiming || Rng.NextDouble() > 0.25) return;
        if (_cleared)
        {
            Rack();
            return;
        }
        var cue = Cue.Body;
        if (BallsLeft == Balls && _shots == 0)
        {
            // the break: straight at the apex ball, hard
            var apex = _balls[1].Body.Pos;
            Shoot(Rotate((apex - cue.Pos).Normalized(), (Rng.NextDouble() - 0.5) * 0.01), MaxShot * 0.95);
            return;
        }
        if (_table.PlanPot(cue, MaxShot) is DiscTable.Shot shot)
        {
            double err = (Rng.NextDouble() + Rng.NextDouble() - 1) * 0.9 * Math.PI / 180;
            Shoot(Rotate(shot.Dir, err), Math.Min(MaxShot, shot.Speed * (1.1 + Rng.NextDouble() * 0.25)));
            return;
        }
        // nothing clean: hit the nearest ball firmly and hope for a better leave
        Disc? nearest = null;
        for (int i = 1; i <= Balls; i++)
        {
            var b = _balls[i].Body;
            if (!b.Sunk && (nearest == null || (b.Pos - cue.Pos).LengthSquared < (nearest.Pos - cue.Pos).LengthSquared)) nearest = b;
        }
        if (nearest != null) Shoot((nearest.Pos - cue.Pos).Normalized(), 1300 + Rng.NextDouble() * 500);
    }

    static Vec2 Rotate(Vec2 v, double rad) =>
        new(v.X * Math.Cos(rad) - v.Y * Math.Sin(rad), v.X * Math.Sin(rad) + v.Y * Math.Cos(rad));
}
