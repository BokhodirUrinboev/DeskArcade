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
/// Window Jenga (see <see cref="JengaTower"/>): a tower of wooden blocks stands on one of your window tops (on the
/// taskbar when none has room) and rides along when the window is dragged, swaying as it goes. Press on a block below
/// the top and drag it out slowly: a block carrying weight is stiff, and pulling faster than it gives shakes the tower.
/// Then click a place on top to lay it. The tower goes when the weight above a layer, leaning with the sway, no longer
/// sits over that layer's remaining blocks. A round lasts until it falls or ninety seconds run out; the blocks moved are the
/// score: a race against the computer or a co-worker over the LAN.
/// </summary>
public sealed class JengaGame : MiniGame
{
    const double BW = 44, BH = 18, PullDist = BW * 1.15, Seconds = 90, Stiffness = 14, Damping = 1.6;
    static readonly Color Wood = Color.FromRgb(214, 170, 112), WoodDark = Color.FromRgb(160, 118, 70);

    sealed class Falling
    {
        public required Control El;
        public Vec2 At, Vel;
        public double Angle, Spin, Age;
    }

    readonly Canvas _blocks = new() { IsHitTestVisible = false };
    readonly Canvas _marks = new() { IsHitTestVisible = false };
    readonly Canvas _held = new() { IsHitTestVisible = false };
    readonly Rectangle _base = new() { Height = 6, RadiusX = 3, RadiusY = 3, IsHitTestVisible = false };
    readonly Dictionary<(int Layer, int Slot), Rectangle> _els = new();
    readonly List<Falling> _falling = new();
    JengaTower _tower = new(Rng);
    IntPtr _on;
    Vec2 _baseAt, _grab, _offset;
    double _sway, _swayV, _time, _creakT, _demoT;
    (int Layer, int Slot)? _pull, _hover;
    bool _pulling, _racing, _demo, _timeUp;
    int _shownSecond;

    public JengaGame(IGameHost host) : base(host)
    {
        Layer.Children.Add(_base);
        Layer.Children.Add(_blocks);
        Layer.Children.Add(_marks);
        Layer.Children.Add(_held);
        ThemeChanged();
    }

    public override string Id => "jenga";
    public override string Title => "Window Jenga";

    bool Over => _tower.Fallen || _timeUp;

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        var wood = Art.Brush(Wood);
        var edge = Art.Brush("#7A5530");
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 3; c++)
            {
                if (r == 1 && c == 1) continue; // the missing block
                s.Rotor.Children.Add(Art.At(new Rectangle { Width = 6.5, Height = 4.5, Fill = wood, Stroke = edge, StrokeThickness = 0.6 }, -10 + c * 7, 8 - r * 5));
            }
        return s;
    }

    public override HudInfo Hud
    {
        get
        {
            long best = Host.Stats.Get("jenga.best");
            int left = (int)Math.Ceiling(Math.Max(0, Seconds - _time));
            string line = _tower.Fallen ? L.T("The tower fell · click its base for a new one")
                : _timeUp ? L.T("Time's up and it still stands · click its base for a new one")
                : _tower.Holding ? L.T("Click a place on top to lay the block")
                : _racing ? L.F("{0}s · drag a block out slowly, then lay it on top", left)
                : L.T("Drag a block out slowly, then click the top to lay it");
            return new HudInfo(_tower.Moved.ToString(CultureInfo.InvariantCulture), line, best > 0 ? L.F("Best {0}", best) : L.T("Best —"));
        }
    }

    // ------------------------------------------------------------------ races

    public override bool SupportsLan => true;
    public override (int Score, bool Active)? Race => (_tower.Moved, _racing);
    public override int RaceBaseline => 12;
    public override int RaceBest => (int)Host.Stats.Get("jenga.best");
    public override double RaceSeconds => Seconds;

    public override void StartRace()
    {
        if (_racing) return;
        if (_tower.Moved > 0 || Over) NewTower();
        BeginRound();
    }

    void BeginRound()
    {
        if (_racing) return;
        _racing = true;
        _time = 0;
        Host.RoundStarted();
    }

    void NewTower()
    {
        foreach (var f in _falling) _blocks.Children.Remove(f.El);
        _falling.Clear();
        _tower = new JengaTower(Rng);
        _sway = _swayV = _time = 0;
        _timeUp = _pulling = false;
        _pull = _hover = null;
        _held.Children.Clear();
        PlaceTower();
        Build();
        Host.HudChanged();
    }

    // ------------------------------------------------------------------ layout

    public override void Layout()
    {
        PlaceTower();
        Build();
        Host.HudChanged();
    }

    public override void Summon(Vec2 p) { }

    public override void Deactivate()
    {
        _pulling = false;
        _pull = null;
        Anims.Finish();
    }

    /// <summary>On a window top with room for the tower and some headroom, else on the taskbar right of the middle.</summary>
    void PlaceTower()
    {
        var a = Host.Arena;
        double need = BW * 3 + 30, room = BH * 26;
        var hud = Host.HudBounds.Inflate(30);
        var tops = Host.Platforms.Items.Where(p => p.X2 - p.X1 >= need && p.Y - a.Top > room && p.Y < a.Bottom - 40
            && !hud.Intersects(new Rect((p.X1 + p.X2) / 2 - need / 2, p.Y - room, need, room))).ToList();
        var pick = tops.OrderBy(p => Math.Abs((p.X1 + p.X2) / 2 - a.Center.X)).FirstOrDefault();
        if (pick.X2 > pick.X1)
        {
            _on = pick.Hwnd;
            _baseAt = new Vec2((pick.X1 + pick.X2) / 2, pick.Y);
        }
        else
        {
            _on = IntPtr.Zero;
            _baseAt = new Vec2(a.Left + a.Width * 0.62, a.Bottom);
        }
    }

    /// <summary>The top-left corner of a block, leaning with the sway by its height in the tower.</summary>
    Vec2 BlockAt(int layer, int slot)
    {
        double lean = _tower.Top > 0 ? _sway * layer / _tower.Top : 0;
        return new Vec2(_baseAt.X - BW * 1.5 + slot * BW + lean, _baseAt.Y - (layer + 1) * BH);
    }

    Rect BlockRect(int layer, int slot)
    {
        var p = BlockAt(layer, slot);
        return new Rect(p.X, p.Y, BW, BH);
    }

    // ------------------------------------------------------------------ input

    public override void CollectHitShapes(List<HitShape> into)
    {
        if (Over)
        {
            into.Add(HitShape.Box(new Rect(_baseAt.X - BW * 2, _baseAt.Y - BH * 3, BW * 4, BH * 3 + 6)));
            return;
        }
        if (_tower.Holding)
        {
            foreach (int s in _tower.OpenSlots()) into.Add(HitShape.Box(BlockRect(_tower.PlaceLayer, s).Inflate(6)));
            return;
        }
        into.Add(HitShape.Box(new Rect(_baseAt.X - BW * 1.5 - Math.Abs(_sway) - 4, _baseAt.Y - (_tower.Height + 1) * BH, BW * 3 + Math.Abs(_sway) * 2 + 8, (_tower.Height + 1) * BH)));
    }

    public override bool PointerDown(Vec2 p, bool right)
    {
        if (Over)
        {
            NewTower();
            Host.Sound.Play("board", 0.4, 0.9);
            return false;
        }
        if (_tower.Holding)
        {
            foreach (int s in _tower.OpenSlots())
                if (BlockRect(_tower.PlaceLayer, s).Inflate(6).Contains(p.ToPoint()))
                {
                    Lay(s);
                    return false;
                }
            return false;
        }
        var at = BlockUnder(p);
        if (at is not { } b || !_tower.Takeable(b.Layer, b.Slot)) return false;
        if (!_racing) BeginRound();
        _pull = b;
        _pulling = true;
        _grab = p;
        _offset = default;
        Host.Sound.Play("click", 0.25, 0.8);
        return true;
    }

    public override void PointerUp(Vec2 p)
    {
        if (!_pulling || _pull is not { } b) return;
        _pulling = false;
        // not out yet: it slides back in
        var from = _offset;
        Anims.Add(0.2, k =>
        {
            _offset = from * (1 - k);
        }, Ease.OutQuad, () => _pull = null);
    }

    public override void PointerCancel() => PointerUp(Host.Pointer);

    (int Layer, int Slot)? BlockUnder(Vec2 p)
    {
        for (int l = _tower.Height - 1; l >= 0; l--)
            for (int s = 0; s < JengaTower.Slots; s++)
                if (_tower.Present(l, s) && BlockRect(l, s).Contains(p.ToPoint())) return (l, s);
        return null;
    }

    // ------------------------------------------------------------------ play

    public override bool Update(double dt)
    {
        bool anim = Anims.Update(dt);
        if (_on != IntPtr.Zero)
        {
            var d = Host.Platforms.DeltaOf(_on);
            if (d.X != 0 || d.Y != 0)
            {
                _baseAt += d;
                _swayV -= d.X * 2.2; // the tower's top lags behind a moving window
            }
        }
        StepFalling(dt);
        if (Over)
        {
            Draw();
            return anim || _falling.Count > 0 || _demo;
        }
        // the sway: a damped spring
        _swayV += (-Stiffness * _sway - Damping * _swayV) * dt;
        _sway += _swayV * dt;
        if (_racing)
        {
            _time += dt;
            if (_time >= Seconds) TimeUp();
        }
        if (_pulling) Pull(dt);
        if (_demo) DemoStep(dt);
        _hover = !_pulling && !_tower.Holding ? BlockUnder(Host.Pointer) : null;
        if (!_tower.Fallen && _tower.FailingLayer(_sway / BW) is var fail and >= 0) Collapse(fail);
        Draw();
        int second = (int)Math.Ceiling(Seconds - _time);
        if (second != _shownSecond)
        {
            _shownSecond = second;
            Host.HudChanged();
        }
        return true;
    }

    /// <summary>The block follows the pull as fast as it gives; pulling harder shakes the tower.</summary>
    void Pull(double dt)
    {
        if (_pull is not { } b) return;
        var want = Host.Pointer - _grab;
        if (want.Length > PullDist * 1.3) want = want * (PullDist * 1.3 / want.Length);
        double tight = _tower.Tightness(b.Layer, b.Slot);
        double speed = 420 - 360 * tight;
        var d = want - _offset;
        double step = speed * dt;
        _offset = d.Length <= step ? want : _offset + d * (step / d.Length);
        double strain = (want - _offset).Length - 16;
        if (strain > 0)
        {
            _swayV += (Rng.NextDouble() - 0.5) * strain * 9 * dt * (0.5 + tight);
            if ((_creakT -= dt) <= 0)
            {
                Host.Sound.Play("board", Math.Min(0.35, 0.1 + strain / 200), 0.5 + Rng.NextDouble() * 0.2);
                _creakT = 0.25;
            }
        }
        if (_offset.Length >= PullDist) Out(b);
    }

    /// <summary>The block is free: it is in the hand now, and the tower must stand without it.</summary>
    void Out((int Layer, int Slot) b)
    {
        _pulling = false;
        _pull = null;
        _offset = default;
        _tower.Take(b.Layer, b.Slot);
        _swayV += (Rng.NextDouble() - 0.5) * 12;
        Host.Sound.Play("click", 0.35, 1.2);
        Build();
        Host.HudChanged();
    }

    void Lay(int slot)
    {
        if (!_tower.Place(slot)) return;
        _swayV += (slot - 1) * 10 + (Rng.NextDouble() - 0.5) * 8; // a block laid off centre gives it a nudge
        Host.Stats.Add("jenga.moved");
        Host.Sound.Play("click", 0.4, 0.9);
        Host.ShareAction(new Vec2(BlockRect(_tower.Top, slot).Center.X, BlockRect(_tower.Top, slot).Top), 1);
        Build();
        Host.HudChanged();
    }

    void TimeUp()
    {
        _timeUp = true;
        Host.Stats.Add("jenga.standing");
        EndRound();
        var at = new Vec2(_baseAt.X, _baseAt.Y - (_tower.Height + 4) * BH);
        Host.Fx.Popup(at, L.T("STILL STANDING!"), Themes.Themed(Themes.ClassicGold), 36, 2.4, L.F("{0} blocks moved", _tower.Moved));
        Host.Fx.Burst(at, Themes.Current.Confetti, 36, 480, 600, 6, 1.0);
        Host.Sound.Play("best", 0.7);
        Host.HudChanged();
    }

    /// <summary>The tower gives way at <paramref name="layer"/>: everything above it tumbles off the way it leans.</summary>
    void Collapse(int layer)
    {
        _tower.Check(_sway / BW);
        double dir = _sway != 0 ? Math.Sign(_sway) : Rng.Next(2) * 2 - 1;
        foreach (var ((l, s), el) in _els.Where(kv => kv.Key.Layer > layer).ToList())
        {
            var at = BlockAt(l, s);
            _falling.Add(new Falling
            {
                El = el, At = at, Angle = 0, Spin = dir * (60 + Rng.NextDouble() * 160),
                Vel = new Vec2(dir * (60 + (l - layer) * 14 + Rng.NextDouble() * 60), -40 - Rng.NextDouble() * 80),
            });
            _els.Remove((l, s));
        }
        Host.Sound.Play("thunk", 0.6, 0.7);
        Anims.After(0.15, () => Host.Sound.Play("thunk", 0.5, 0.9));
        Anims.After(0.35, () => Host.Sound.Play("board", 0.5, 0.6));
        EndRound();
        Host.Fx.Popup(new Vec2(_baseAt.X, _baseAt.Y - (layer + 6) * BH), L.T("CRASH!"), Colors.White, 38, 2.2, L.F("{0} blocks moved", _tower.Moved));
        Host.HudChanged();
    }

    void EndRound()
    {
        long before = Host.Stats.Get("jenga.best");
        Host.Stats.Max("jenga.best", _tower.Moved);
        if (_tower.Moved > before && _tower.Moved > 0 && !_timeUp)
            Host.Fx.Popup(new Vec2(_baseAt.X, _baseAt.Y - BH * 2), L.T("NEW BEST!"), Themes.Themed(Themes.ClassicGold), 26, 2);
        if (!_racing) return;
        _racing = false;
        Host.RoundEnded(_tower.Moved);
    }

    void StepFalling(double dt)
    {
        var a = Host.Arena;
        foreach (var f in _falling.ToList())
        {
            f.Age += dt;
            f.Vel += new Vec2(0, 1400 * dt);
            f.At += f.Vel * dt;
            f.Angle += f.Spin * dt;
            if (f.At.Y > a.Bottom - BH)
            {
                f.At = new Vec2(f.At.X, a.Bottom - BH);
                f.Vel = new Vec2(f.Vel.X * 0.6, -f.Vel.Y * 0.25);
                f.Spin *= 0.5;
            }
            Canvas.SetLeft(f.El, f.At.X);
            Canvas.SetTop(f.El, f.At.Y);
            f.El.RenderTransform = new RotateTransform(f.Angle);
            if (f.Age > 1.6) f.El.Opacity = Math.Max(0, 1 - (f.Age - 1.6) / 0.6);
            if (f.Age > 2.2)
            {
                _blocks.Children.Remove(f.El);
                _falling.Remove(f);
            }
        }
    }

    // ------------------------------------------------------------------ demo

    public override void DemoTick() => _demo = true;

    /// <summary>The demo takes a block the tower can spare (a middle first), and lays it in the middle when it can.</summary>
    void DemoStep(double dt)
    {
        if ((_demoT -= dt) > 0 || _pulling) return;
        _demoT = 1.2;
        if (_tower.Holding)
        {
            var open = _tower.OpenSlots();
            Lay(open.Contains(1) ? 1 : open[Rng.Next(open.Count)]);
            return;
        }
        var safe = Enumerable.Range(0, _tower.Height).SelectMany(l => Enumerable.Range(0, 3).Select(s => (l, s)))
            .Where(b => _tower.Takeable(b.l, b.s))
            .Where(b =>
            {
                var t = _tower.Clone();
                t.Take(b.l, b.s);
                return t.SwayRoom() > 0.4;
            })
            .OrderByDescending(b => b.s == 1).ThenBy(_ => Rng.Next()).ToList();
        if (safe.Count == 0) return;
        if (!_racing) BeginRound();
        Out(safe[0]);
    }

    // ------------------------------------------------------------------ drawing

    /// <summary>Makes a block element for every block the tower has (the falling ones keep theirs).</summary>
    void Build()
    {
        foreach (var el in _els.Values) _blocks.Children.Remove(el);
        _els.Clear();
        for (int l = 0; l < _tower.Height; l++)
            for (int s = 0; s < JengaTower.Slots; s++)
            {
                if (!_tower.Present(l, s)) continue;
                var el = BlockArt(l * 3 + s);
                _blocks.Children.Add(el);
                _els[(l, s)] = el;
            }
        _held.Children.Clear();
        if (_tower.Holding) _held.Children.Add(BlockArt(7));
        Draw();
    }

    static Rectangle BlockArt(int seed)
    {
        // a touch of variety in the wood, the same for the same block every time it is drawn
        double t = (seed * 37 % 11) / 10.0;
        return new Rectangle
        {
            Width = BW - 1, Height = BH - 1, RadiusX = 2, RadiusY = 2, IsHitTestVisible = false,
            Fill = Art.Brush(Art.Blend(Wood, WoodDark, 0.15 + t * 0.35)), Stroke = Art.Brush(Art.Blend(WoodDark, Colors.Black, 0.3)), StrokeThickness = 1,
        };
    }

    void Draw()
    {
        foreach (var ((l, s), el) in _els)
        {
            var p = BlockAt(l, s);
            if (_pull is { } b && b.Layer == l && b.Slot == s) p += _offset;
            Canvas.SetLeft(el, p.X);
            Canvas.SetTop(el, p.Y);
            el.StrokeThickness = _hover is { } h && h.Layer == l && h.Slot == s && _tower.Takeable(l, s) ? 2.5 : 1;
            el.Stroke = el.StrokeThickness > 1 ? Art.Brush(Themes.Current.Gold) : Art.Brush(Art.Blend(WoodDark, Colors.Black, 0.3));
        }
        _base.Width = BW * 3 + 24;
        Canvas.SetLeft(_base, _baseAt.X - _base.Width / 2);
        Canvas.SetTop(_base, _baseAt.Y - 3);
        _marks.Children.Clear();
        if (_tower.Holding && !Over)
        {
            var gold = Art.Brush(Themes.Current.Gold);
            foreach (int s in _tower.OpenSlots())
            {
                var r = BlockRect(_tower.PlaceLayer, s);
                _marks.Children.Add(Art.At(new Rectangle { Width = r.Width - 1, Height = r.Height - 1, RadiusX = 2, RadiusY = 2, Stroke = gold, StrokeThickness = 2, StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 3, 2 }, Fill = Art.Brush(40, 255, 255, 255) }, r.X, r.Y));
            }
            if (_held.Children.Count > 0)
            {
                var p = _demo ? BlockAt(_tower.PlaceLayer, 1) + new Vec2(0, -BH * 2) : Host.Pointer - new Vec2(BW / 2, BH / 2);
                Canvas.SetLeft(_held.Children[0], p.X);
                Canvas.SetTop(_held.Children[0], p.Y);
            }
        }
    }

    public override void ThemeChanged()
    {
        _base.Fill = Art.Brush(Art.Blend(WoodDark, Colors.Black, 0.35));
        if (_els.Count > 0) Draw();
    }
}
