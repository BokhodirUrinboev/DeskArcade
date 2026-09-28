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
/// Rope Bridge (see <see cref="RopeBridge"/>): two windows stand on the desktop with a gap between them and a rope
/// across it, and interns walk over from the left one. Drag planks from the pile onto the rope (or click the rope to lay
/// one there) before they reach the gap: a place with no plank drops whoever steps on it, and every plank wears through
/// after a few crossings, cracking as it goes, so a worn one wants replacing in time. Fifteen interns make a round; the
/// ones who reach the far window are the score, a race against the computer or a co-worker over the LAN.
/// </summary>
public sealed class BridgeGame : MiniGame
{
    const double SlotW = 36, PlankH = 8, RailUp = 30, DropReach = 36, FigureH = 25;
    static readonly Color Wood = Color.FromRgb(196, 148, 92), Rope = Color.FromRgb(214, 190, 140);
    static readonly Color[] Shirts =
    {
        Color.FromRgb(77, 163, 255), Color.FromRgb(255, 92, 108), Color.FromRgb(61, 220, 132), Color.FromRgb(255, 209, 102), Color.FromRgb(180, 140, 255),
    };

    sealed class Figure
    {
        public required RopeBridge.Walker Who;
        public required Canvas El;
        public required Line LegA, LegB;
        public double Phase;
    }

    readonly Canvas _scene = new() { IsHitTestVisible = false };
    readonly Canvas _planks = new() { IsHitTestVisible = false };
    readonly Canvas _people = new() { IsHitTestVisible = false };
    readonly Canvas _pile = new() { IsHitTestVisible = false };
    readonly Canvas _hand = new() { IsHitTestVisible = false };
    readonly List<Figure> _figures = new();
    RopeBridge _bridge = null!;
    Rect _left, _right;
    double _sag, _demoT;
    bool _running, _racing, _dragging, _demo, _built;

    public BridgeGame(IGameHost host) : base(host)
    {
        foreach (var c in new Control[] { _scene, _planks, _people, _pile, _hand }) Layer.Children.Add(c);
    }

    public override string Id => "bridge";
    public override string Title => "Rope Bridge";

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        s.Rotor.Children.Add(Art.PathOf("M-11,-6 Q0,2 11,-6", null, Art.Brush("#C8B48C"), 1.4));
        s.Rotor.Children.Add(Art.PathOf("M-11,2 Q0,10 11,2", null, Art.Brush("#C8B48C"), 1.4));
        for (int i = -2; i <= 2; i++) s.Rotor.Children.Add(Art.At(new Rectangle { Width = 3.5, Height = 2.5, Fill = Art.Brush(Wood) }, i * 4.3 - 1.7, 4.6 + Math.Cos(i * 0.6) * 1.4));
        s.Rotor.Children.Add(Art.Circle(-6, -9, 2, Art.Brush("#F2C9A0")));
        return s;
    }

    public override HudInfo Hud
    {
        get
        {
            long best = Host.Stats.Get("bridge.best");
            var b = _bridge;
            string line = b == null ? ""
                : b.Over ? L.F("{0} of {1} across · click the planks for another round", b.Saved, RopeBridge.RoundInterns)
                : !_running ? L.T("Drag planks onto the rope before the interns reach the gap")
                : L.F("Interns {0} of {1} · worn planks crack: replace them in time", b.Spawned, RopeBridge.RoundInterns);
            return new HudInfo(b?.Saved.ToString(CultureInfo.InvariantCulture) ?? "0", line, best > 0 ? L.F("Best {0}", best) : L.T("Best —"));
        }
    }

    // ------------------------------------------------------------------ races

    public override bool SupportsLan => true;
    public override (int Score, bool Active)? Race => (_bridge?.Saved ?? 0, _racing);
    public override int RaceBaseline => 11;
    public override int RaceBest => (int)Host.Stats.Get("bridge.best");
    public override double RaceSeconds => RopeBridge.RoundInterns * RopeBridge.SpawnEvery + 12;

    public override void StartRace()
    {
        if (_racing) return;
        if (!_built) Build();
        if (_running || _bridge.Over) NewRound();
        Begin();
    }

    void Begin()
    {
        if (_running) return;
        _running = true;
        _racing = true;
        Host.RoundStarted();
        Host.HudChanged();
    }

    void NewRound()
    {
        _running = _racing = _dragging = false;
        foreach (var f in _figures) _people.Children.Remove(f.El);
        _figures.Clear();
        _hand.Children.Clear();
        Build();
        Host.HudChanged();
    }

    // ------------------------------------------------------------------ layout

    public override void Layout()
    {
        if (!_built || !_running) Build();
        Host.HudChanged();
    }

    public override void Summon(Vec2 p) { }

    public override void Deactivate()
    {
        _dragging = false;
        _hand.Children.Clear();
        Anims.Finish();
    }

    /// <summary>Two windows standing on the taskbar, the gap between them, and a fresh bridge over it.</summary>
    void Build()
    {
        _built = true;
        var a = Host.Arena;
        double ww = Math.Min(a.Width * 0.22, 360), gap = Math.Clamp(a.Width * 0.28, SlotW * 6, 430), wh = Math.Min(260, a.Height * 0.35);
        double x0 = a.Center.X - (ww * 2 + gap) / 2, top = a.Bottom - wh;
        _left = new Rect(x0, top, ww, wh);
        _right = new Rect(x0 + ww + gap, top, ww, wh);
        _bridge = new RopeBridge(_left.Right, _right.Left, SlotW, Rng, from: _left.Right - Math.Min(150, ww - 30));
        _sag = (_right.Left - _left.Right) * 0.06;
        DrawScene();
        DrawPlanks();
        DrawPile();
    }

    /// <summary>How far the deck hangs at <paramref name="x"/> along the gap.</summary>
    double DeckY(double x)
    {
        double top = _left.Top;
        if (x <= _left.Right || x >= _right.Left) return top;
        double t = (x - _left.Right) / (_right.Left - _left.Right);
        return top + _sag * 4 * t * (1 - t); // the same curve the rope is drawn with
    }

    Vec2 SlotCenter(int slot)
    {
        double x = _bridge.Start + (slot + 0.5) * _bridge.SlotW;
        return new Vec2(x, DeckY(x) + PlankH / 2);
    }

    Rect PileRect => new(_left.Right - 70, _left.Top - 52, 60, 52);
    Rect DeckRect => new(_bridge.Start, _left.Top - RailUp, _bridge.End - _bridge.Start, _sag + RailUp + 26);

    // ------------------------------------------------------------------ input

    public override void CollectHitShapes(List<HitShape> into)
    {
        into.Add(HitShape.Box(PileRect.Inflate(6)));
        if (!_bridge.Over) into.Add(HitShape.Box(DeckRect));
    }

    public override bool PointerDown(Vec2 p, bool right)
    {
        if (_bridge.Over)
        {
            NewRound();
            return false;
        }
        if (PileRect.Inflate(6).Contains(p.ToPoint()))
        {
            if (_bridge.Pile == 0)
            {
                Host.Fx.Popup(p - new Vec2(0, 30), L.T("No planks yet · more are coming"), Colors.White, 16, 1);
                return false;
            }
            _dragging = true;
            _hand.Children.Clear();
            _hand.Children.Add(PlankArt(RopeBridge.MaxWear, 0));
            PlaceHand(p);
            Host.Sound.Play("click", 0.25, 0.9);
            return true;
        }
        int slot = NearestSlot(p);
        if (slot >= 0) LayAt(slot); // a click on the rope lays a plank there straight from the pile
        return false;
    }

    public override void PointerUp(Vec2 p)
    {
        if (!_dragging) return;
        _dragging = false;
        _hand.Children.Clear();
        int slot = NearestSlot(p);
        if (slot >= 0) LayAt(slot);
    }

    public override void PointerCancel()
    {
        _dragging = false;
        _hand.Children.Clear();
    }

    int NearestSlot(Vec2 p)
    {
        int best = -1;
        double bestD = DropReach;
        for (int s = 0; s < _bridge.Slots; s++)
        {
            double d = (SlotCenter(s) - p).Length;
            if (d < bestD)
            {
                bestD = d;
                best = s;
            }
        }
        return best;
    }

    void LayAt(int slot)
    {
        if (_bridge.Pile == 0)
        {
            Host.Sound.Play("board", 0.2, 0.7);
            return;
        }
        bool replacing = _bridge.HasPlank(slot);
        if (!_bridge.Lay(slot))
        {
            Host.Fx.Popup(SlotCenter(slot) - new Vec2(0, 30), L.T("Someone is standing on it"), Colors.White, 16, 1);
            return;
        }
        if (!_demo) Host.Stats.Add("bridge.planks");
        Host.Sound.Play("thunk", 0.3, replacing ? 1.1 : 1.4);
        if (!_running) Begin();
        DrawPlanks();
        DrawPile();
        Host.Wake();
    }

    void PlaceHand(Vec2 p)
    {
        if (_hand.Children.Count == 0) return;
        Canvas.SetLeft(_hand.Children[0], p.X);
        Canvas.SetTop(_hand.Children[0], p.Y);
    }

    // ------------------------------------------------------------------ play

    public override bool Update(double dt)
    {
        bool anim = Anims.Update(dt);
        if (_dragging) PlaceHand(Host.Pointer);
        if (_demo) DemoStep(dt);
        if (!_running || _bridge.Over) return anim || _dragging || _demo;
        int pile = _bridge.Pile;
        foreach (var e in _bridge.Step(dt)) OnEvent(e);
        if (_bridge.Pile != pile) DrawPile();
        MoveFigures(dt);
        if (_bridge.Over) RoundOver();
        return true;
    }

    void OnEvent(RopeBridge.Event e)
    {
        switch (e.Kind)
        {
            case "spawn":
                AddFigure(e.Who);
                Host.HudChanged();
                break;
            case "step":
                DrawPlanks();
                if (_bridge.Wear(e.Slot) <= 1) Host.Sound.Play("board", 0.2, 0.6); // a creak: this one is nearly through
                break;
            case "snap":
                var at = SlotCenter(e.Slot);
                Host.Fx.Burst(at, new[] { Wood, Art.Blend(Wood, Colors.Black, 0.4) }, 10, 200, 700, 4, 0.6);
                Host.Sound.Play("thunk", 0.5, 0.7);
                DrawPlanks();
                break;
            case "fell":
                Fall(e.Who);
                Host.HudChanged();
                break;
            case "saved":
                Saved(e.Who);
                break;
        }
    }

    void Saved(RopeBridge.Walker w)
    {
        if (!_demo) Host.Stats.Add("bridge.saved");
        var f = _figures.FirstOrDefault(x => x.Who == w);
        var at = new Vec2(w.X, _right.Top - 20);
        Host.Fx.Popup(at - new Vec2(0, 16), "+1", Themes.Themed(Themes.ClassicGold), 18, 0.9);
        Host.Sound.Play("pop", 0.3, 1.3);
        Host.ShareAction(at, 1);
        if (f != null)
        {
            var el = f.El;
            Anims.Add(0.4, k =>
            {
                el.Opacity = 1 - k;
                Canvas.SetTop(el, _right.Top - FigureH - 14 * Math.Sin(Math.PI * k));
            }, Ease.Linear, () => _people.Children.Remove(el));
            _figures.Remove(f);
        }
        Host.HudChanged();
    }

    void Fall(RopeBridge.Walker w)
    {
        var f = _figures.FirstOrDefault(x => x.Who == w);
        var at = SlotCenter(Math.Max(0, w.Slot));
        Host.Fx.Popup(at - new Vec2(0, 34), L.T("Aaah!"), Colors.White, 16, 0.9);
        Host.Sound.Play("whoosh", 0.3, 0.7);
        if (f == null) return;
        _figures.Remove(f);
        var el = f.El;
        double x = w.X, y0 = at.Y - FigureH, spin = Rng.NextDouble() < 0.5 ? -1 : 1;
        double floor = Host.Arena.Bottom - FigureH;
        Anims.Add(0.9, k =>
        {
            Canvas.SetLeft(el, x + spin * 20 * k);
            Canvas.SetTop(el, y0 + (floor - y0) * k * k);
            el.RenderTransform = new RotateTransform(spin * 300 * k);
            el.Opacity = k < 0.75 ? 1 : (1 - k) * 4;
        }, Ease.Linear, () => _people.Children.Remove(el));
    }

    void RoundOver()
    {
        var b = _bridge;
        long before = Host.Stats.Get("bridge.best");
        if (!_demo)
        {
            Host.Stats.Max("bridge.best", b.Saved);
            if (b.Lost == 0) Host.Stats.Add("bridge.perfect");
        }
        if (_racing)
        {
            _racing = false;
            Host.RoundEnded(b.Saved);
        }
        _running = false;
        _demoT = 3;
        bool best = b.Saved > before && b.Saved > 0;
        var at = new Vec2((_left.Right + _right.Left) / 2, _left.Top - 110);
        string title = b.Lost == 0 ? L.T("NOBODY FELL!") : best ? L.T("NEW BEST!") : L.T("ROUND OVER");
        Host.Fx.Popup(at, title, b.Lost == 0 || best ? Themes.Themed(Themes.ClassicGold) : Colors.White, 36, 2.4, L.F("{0} of {1} across", b.Saved, RopeBridge.RoundInterns));
        if (b.Lost == 0 || best) Host.Fx.Burst(at, Themes.Current.Confetti, 40, 520, 700, 7, 1.1);
        Host.Sound.Play(b.Lost == 0 || best ? "best" : "score", 0.6);
        Host.HudChanged();
    }

    // ------------------------------------------------------------------ the interns

    void AddFigure(RopeBridge.Walker w)
    {
        var shirt = Art.Brush(Shirts[Rng.Next(Shirts.Length)]);
        var el = new Canvas { IsHitTestVisible = false, RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative) };
        var legA = new Line { StartPoint = new Point(0, 15), EndPoint = new Point(-4, FigureH), Stroke = Art.Brush("#2B2F3A"), StrokeThickness = 2.6 };
        var legB = new Line { StartPoint = new Point(0, 15), EndPoint = new Point(4, FigureH), Stroke = Art.Brush("#2B2F3A"), StrokeThickness = 2.6 };
        el.Children.Add(legA);
        el.Children.Add(legB);
        el.Children.Add(Art.At(new Rectangle { Width = 11, Height = 12, RadiusX = 3, RadiusY = 3, Fill = shirt }, -5.5, 4));
        el.Children.Add(Art.PathOf("M0,5 L-1.5,10 L0,13 L1.5,10 Z", Art.Brush("#20232C"))); // the tie
        el.Children.Add(Art.Circle(0, 0, 5, Art.Brush("#F2C9A0")));
        el.Opacity = 0;
        Anims.Add(0.3, k => el.Opacity = k);
        _people.Children.Add(el);
        _figures.Add(new Figure { Who = w, El = el, LegA = legA, LegB = legB });
    }

    void MoveFigures(double dt)
    {
        foreach (var f in _figures)
        {
            f.Phase += dt * 9;
            double swing = Math.Sin(f.Phase) * 3;
            f.LegA.EndPoint = new Point(-swing * 1.3, FigureH);
            f.LegB.EndPoint = new Point(swing * 1.3, FigureH);
            double y = DeckY(f.Who.X);
            Canvas.SetLeft(f.El, f.Who.X);
            Canvas.SetTop(f.El, y - FigureH - Math.Abs(Math.Sin(f.Phase)) * 1.5);
        }
    }

    // ------------------------------------------------------------------ demo

    public override void DemoTick() => _demo = true;

    /// <summary>The demo keeps the bridge mended: an empty or worn-through place gets a plank, a few at a time.</summary>
    void DemoStep(double dt)
    {
        if (_bridge.Over)
        {
            if ((_demoT -= dt) <= 0) NewRound();
            return;
        }
        if ((_demoT -= dt) > 0) return;
        _demoT = 0.45;
        int slot = Enumerable.Range(0, _bridge.Slots).Where(s => _bridge.Wear(s) <= 0 && !_bridge.Occupied(s)).DefaultIfEmpty(-1).First();
        if (slot >= 0 && _bridge.Pile > 0) LayAt(slot);
    }

    // ------------------------------------------------------------------ drawing

    /// <summary>The two windows, and the ropes: a handrail and the deck rope, with hangers between.</summary>
    void DrawScene()
    {
        _scene.Children.Clear();
        var t = Themes.Current;
        foreach (var w in new[] { _left, _right })
        {
            _scene.Children.Add(Art.At(new Rectangle { Width = w.Width + 4, Height = w.Height, RadiusX = 8, RadiusY = 8, Fill = Art.Brush(60, 0, 0, 0) }, w.X + 2, w.Y + 4));
            _scene.Children.Add(Art.At(new Rectangle { Width = w.Width, Height = w.Height, RadiusX = 8, RadiusY = 8, Fill = Art.Brush(Color.FromArgb(240, t.Ink.R, t.Ink.G, t.Ink.B)), Stroke = Art.Brush(Art.Blend(t.Accent, t.Ink, 0.3)), StrokeThickness = 1.5 }, w.X, w.Y));
            _scene.Children.Add(Art.At(new Rectangle { Width = w.Width, Height = 22, RadiusX = 8, RadiusY = 8, Fill = Art.Brush(Art.Blend(t.Accent, t.Ink, 0.45)) }, w.X, w.Y));
            for (int i = 0; i < 3; i++) _scene.Children.Add(Art.Circle(w.Right - 14 - i * 14, w.Y + 11, 4, Art.Brush(new[] { "#FF5F57", "#FEBC2E", "#28C840" }[2 - i])));
            for (int i = 0; i < 5; i++) // lines of "text"
            {
                double len = (0.35 + (i * 37 % 5) * 0.1) * (w.Width - 40);
                _scene.Children.Add(Art.At(new Rectangle { Width = len, Height = 5, RadiusX = 2.5, RadiusY = 2.5, Fill = Art.Brush(Color.FromArgb(70, t.HudFront.R, t.HudFront.G, t.HudFront.B)) }, w.X + 20, w.Y + 40 + i * 18));
            }
        }
        var rope = Art.Brush(Rope);
        double x0 = _left.Right, x1 = _right.Left, top = _left.Top;
        string Curve(double lift) => $"M{Art.F(x0)},{Art.F(top - lift)} Q{Art.F((x0 + x1) / 2)},{Art.F(top - lift + _sag * 2)} {Art.F(x1)},{Art.F(top - lift)}";
        _scene.Children.Add(Art.PathOf(Curve(RailUp), null, rope, 2));
        _scene.Children.Add(Art.PathOf(Curve(0), null, rope, 2));
        for (int s = 0; s <= _bridge.Slots; s++)
        {
            double x = _bridge.Start + s * _bridge.SlotW, y = DeckY(x);
            _scene.Children.Add(Art.PathOf($"M{Art.F(x)},{Art.F(y)} L{Art.F(x)},{Art.F(y - RailUp)}", null, Art.Brush(Color.FromArgb(160, Rope.R, Rope.G, Rope.B)), 1));
        }
        foreach (double x in new[] { x0, x1 }) // posts
            _scene.Children.Add(Art.At(new Rectangle { Width = 6, Height = RailUp + 6, RadiusX = 2, RadiusY = 2, Fill = Art.Brush(Art.Blend(Wood, Colors.Black, 0.35)) }, x - 3, top - RailUp - 4));
    }

    void DrawPlanks()
    {
        _planks.Children.Clear();
        for (int s = 0; s < _bridge.Slots; s++)
        {
            if (!_bridge.HasPlank(s)) continue;
            var c = SlotCenter(s);
            double x = _bridge.Start + (s + 0.5) * _bridge.SlotW;
            double slope = (DeckY(x + 1) - DeckY(x - 1)) / 2;
            var plank = PlankArt(_bridge.Wear(s), Math.Atan(slope) * 180 / Math.PI);
            Canvas.SetLeft(plank, c.X);
            Canvas.SetTop(plank, c.Y);
            _planks.Children.Add(plank);
        }
    }

    /// <summary>A plank centred on (0, 0): darker and cracked as it wears, red-tinged when one more step will snap it.</summary>
    static Canvas PlankArt(int wear, double angle)
    {
        var c = new Canvas { IsHitTestVisible = false, RenderTransform = new RotateTransform(angle) };
        double w = SlotW - 3;
        double worn = 1 - Math.Clamp(wear / (double)RopeBridge.MaxWear, 0, 1);
        var fill = Art.Blend(Wood, Colors.Black, 0.1 + worn * 0.35);
        if (wear == 0) fill = Art.Blend(fill, Color.FromRgb(200, 60, 50), 0.35);
        c.Children.Add(Art.At(new Rectangle { Width = w, Height = PlankH, RadiusX = 1.5, RadiusY = 1.5, Fill = Art.Brush(fill), Stroke = Art.Brush(Art.Blend(Wood, Colors.Black, 0.5)), StrokeThickness = 0.8 }, -w / 2, -PlankH / 2));
        if (wear <= 2) c.Children.Add(Art.PathOf("M-6,-4 L-2,0 L-5,4 M5,-4 L2,1", null, Art.Brush("#3A2A1A"), 1));
        return c;
    }

    void DrawPile()
    {
        _pile.Children.Clear();
        var r = PileRect;
        for (int i = 0; i < _bridge.Pile; i++)
        {
            var plank = PlankArt(RopeBridge.MaxWear, 0);
            Canvas.SetLeft(plank, r.Center.X + (i % 2 == 0 ? -2 : 2));
            Canvas.SetTop(plank, r.Bottom - PlankH / 2 - i * (PlankH + 1));
            _pile.Children.Add(plank);
        }
        if (_bridge.Pile == 0)
            _pile.Children.Add(Art.At(new TextBlock { Text = "…", FontFamily = Fx.Font, FontSize = 16, FontWeight = FontWeight.Black, Foreground = Brushes.White }, r.Center.X - 6, r.Bottom - 22));
    }

    public override void ThemeChanged()
    {
        if (_built) DrawScene();
    }
}
