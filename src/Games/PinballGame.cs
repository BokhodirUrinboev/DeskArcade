using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>
/// Pinball: the whole screen is the table and the taskbar is the drain. Click the ball waiting above the right
/// flipper to serve it, then press near a flipper to flip it (right-click flips both). Pop bumpers, slingshots and
/// window tops keep it bouncing; light all three rollover lanes for a multiplier. Three balls per game.
/// </summary>
public sealed class PinballGame : MiniGame
{
    const int Balls = 3;
    const double ServeReach = 28, BoxMaxW = 300, BoxAbove = 150, BoxBelow = 70, FlashTime = 0.18, BallArtR = 12.5;
    const double MinWindowWidth = 140, DemoHold = 0.2;

    static readonly Color Gold = Color.FromRgb(255, 209, 102);
    static readonly Color[] BumperColors = { Color.FromRgb(239, 71, 111), Color.FromRgb(77, 163, 255), Color.FromRgb(255, 166, 43) };
    static readonly Color WindowBumperColor = Color.FromRgb(170, 110, 240);
    static readonly Color[] Sparks = { Gold, Colors.White, Color.FromRgb(255, 240, 180) };
    static readonly IBrush LaneOff = Art.Brush(150, 40, 44, 54), LaneOn = Art.Brush(Gold), LaneRim = Art.Brush(200, 255, 255, 255);

    sealed class BumperArt
    {
        public required Sprite Sprite;
        public required Control Flash;
        public double FlashT;
    }

    readonly PinballTable _table;
    readonly Canvas _tableArt = new() { IsHitTestVisible = false };
    readonly Canvas _bumperLayer = new() { IsHitTestVisible = false };
    readonly Sprite _ball = MakeBall();
    readonly Ellipse _serveRing = new()
    {
        Width = ServeReach * 2, Height = ServeReach * 2, Stroke = Art.Brush(170, 255, 209, 102), StrokeThickness = 2,
        Fill = Art.Brush(30, 255, 209, 102), IsHitTestVisible = false,
    };
    readonly Sprite[] _flippers = new Sprite[2];
    readonly Path[] _flipperBodies = new Path[2];
    readonly List<BumperArt> _mainArt = new();
    readonly BumperArt?[] _windowArt = new BumperArt?[2];
    readonly PinballTable.Bumper?[] _windowBumpers = new PinballTable.Bumper?[2];
    readonly IntPtr[] _windowHwnds = new IntPtr[2];
    readonly Ellipse[] _laneLights = new Ellipse[3];
    readonly Path?[] _slingFlash = new Path?[2];
    readonly double[] _slingT = new double[2];
    readonly double[] _demoFlipAt = { -1, -1 }, _demoReleaseAt = { -1, -1 };
    readonly Dictionary<string, double> _lastSound = new();

    Rect _builtFor;
    double _time;
    int _ballNo = 1, _held, _seenGen = -1, _demoServeTicks = -1;
    bool _gameOver;

    public PinballGame(IGameHost host) : base(host)
    {
        _table = new PinballTable(new Rect(0, 0, 1920, 1040), default, new Random());
        _table.Event += OnTable;
        _table.ResetBall();
        for (int i = 0; i < 2; i++)
        {
            _flippers[i] = MakeFlipper(out _flipperBodies[i]);
            if (i == 1) _flippers[i].FlipX = -1; // the right flipper is the left one mirrored
        }
        Layer.Children.Add(_tableArt);
        Layer.Children.Add(_bumperLayer);
        Layer.Children.Add(_flippers[0]);
        Layer.Children.Add(_flippers[1]);
        Layer.Children.Add(_serveRing);
        Layer.Children.Add(_ball);
    }

    public override string Id => "pinball";
    public override string Title => "Pinball";

    public override Sprite CreateIcon()
    {
        var icon = new Sprite();
        icon.Children.Add(Art.PathOf("M-9,-1 A3.5,3.5 0 0 1 -8,-7 L8,1 A2,2 0 0 1 7,4 Z", Art.Brush(Themes.Current.Mine), Brushes.White, 1.2));
        icon.Children.Add(Art.Circle(3, -7, 4.5, SteelBrush(), Art.Brush("#4A515C"), 0.8));
        return icon;
    }

    public override HudInfo Hud => new(
        _table.Score.ToString(),
        _gameOver ? L.T("Game over · click the ball to play again")
            : !_table.InPlay ? L.F("Ball {0} of 3 · click the ball to serve", _ballNo)
            : _table.Multiplier > 1 ? L.F("Ball {0} of 3 · ×{1}", _ballNo, _table.Multiplier)
            : L.F("Ball {0} of 3", _ballNo),
        L.F("Best {0}", Host.Stats.Get("pinball.best")));

    // ------------------------------------------------------------------ game flow

    public override void Layout()
    {
        var a = Host.Arena;
        if (a != _builtFor)
        {
            _builtFor = a;
            for (int j = 0; j < 2; j++) RemoveWindowBumper(j); // they are re-picked for the new screen below
            _table.Build(a, Host.HudBounds);
            _table.InPlay = false; // a ball in play goes back to the plunger spot; it isn't lost
            _table.Ball = _table.ServeSpot;
            _table.BallVel = default;
            DrawTable();
        }
        SyncWindows(force: true);
        Draw();
        Host.HudChanged();
    }

    public override void Deactivate()
    {
        SetFlippers(3, false, quiet: true);
        _held = 0;
        for (int i = 0; i < 2; i++) _demoFlipAt[i] = _demoReleaseAt[i] = -1;
    }

    void NewGame()
    {
        _gameOver = false;
        _ballNo = 1;
        _table.Score = 0;
        _table.ResetBall();
    }

    void Serve()
    {
        if (_gameOver) NewGame();
        _table.Serve();
        Host.Sound.Play("whoosh", 0.5, 1.2);
        Host.HudChanged();
    }

    void Drained()
    {
        PlaySound("buzzer", 0.3, 1);
        if (_ballNo >= Balls)
        {
            GameOver();
            return;
        }
        _ballNo++;
        _table.ResetBall();
        var a = Host.Arena;
        Host.Fx.Popup(new Vec2(a.Center.X, _table.Left.Pivot.Y - 160), L.T("Drained!"), Color.FromRgb(255, 130, 130), 28, 1.3,
            L.F("Ball {0} of 3", _ballNo));
        Host.HudChanged();
    }

    void GameOver()
    {
        _gameOver = true;
        int score = _table.Score;
        long before = Host.Stats.Get("pinball.best");
        Host.Stats.Add("pinball.games");
        Host.Stats.Max("pinball.best", score);
        bool best = score > before;
        _table.ResetBall();

        var a = Host.Arena;
        var at = new Vec2(a.Center.X, a.Top + a.Height * 0.5);
        Host.Fx.Popup(at, best ? L.T("NEW BEST!") : L.T("GAME OVER"), best ? Gold : Colors.White, 38, 2.4, L.F("{0} points", score));
        if (best)
        {
            Host.Fx.Burst(at, Themes.Current.Confetti, 40, 520, 700, 7, 1.1);
            Host.Sound.Play("best", 0.8);
        }
        Host.HudChanged();
    }

    void OnTable(PinballTable.Hit kind, int index, Vec2 at, double speed)
    {
        switch (kind)
        {
            case PinballTable.Hit.Bumper:
                Host.Stats.Add("pinball.bumpers");
                var art = ArtFor(_table.Bumpers[index]);
                if (art != null) art.FlashT = FlashTime;
                PlaySound("pin-bumper", 0.7, 0.9 + Rng.NextDouble() * 0.2);
                Host.Fx.Popup(at - new Vec2(0, 24), $"+{PinballTable.BumperPoints * _table.Multiplier}", Gold, 18, 0.5);
                Host.Fx.Burst(at, Sparks, 6, 260, 300, 4, 0.35);
                Host.HudChanged();
                break;
            case PinballTable.Hit.Sling:
                _slingT[index] = FlashTime;
                PlaySound("kick", 0.6, 1.4);
                Host.HudChanged();
                break;
            case PinballTable.Hit.Rollover:
                PlaySound("pin-ding", speed > 0 ? 0.6 : 0.25, 1);
                Host.HudChanged();
                break;
            case PinballTable.Hit.LanesDone:
                Host.Fx.Popup(at + new Vec2(0, 50), L.F("×{0} multiplier", index), Gold, 30, 1.4, L.F("+{0} bonus", PinballTable.LanesBonus));
                Host.Sound.Play("score", 0.6);
                Host.HudChanged();
                break;
            case PinballTable.Hit.Wall:
                PlaySound("rim", Math.Min(0.3, speed / 6000), 2.2);
                break;
            case PinballTable.Hit.Flipper:
                PlaySound("board", Math.Min(0.5, speed / 5000), 1.6);
                break;
            case PinballTable.Hit.Drain:
                Drained();
                break;
        }
    }

    // ------------------------------------------------------------------ window tops

    /// <summary>Window tops become ledges, and up to two of them carry a bumper that rides along with the window.</summary>
    void SyncWindows(bool force)
    {
        var plats = Host.Platforms;
        if (!force && plats.Generation == _seenGen) return;
        _seenGen = plats.Generation;
        _table.SetLedges(plats.Items);

        for (int j = 0; j < 2; j++)
        {
            var b = _windowBumpers[j];
            if (b == null) continue;
            IntPtr h = _windowHwnds[j];
            double want = b.Center.X + plats.DeltaOf(h).X;
            bool found = false;
            double bestGap = double.MaxValue;
            Vec2 bestAt = default;
            foreach (var p in plats.Items)
            {
                if (p.Hwnd != h) continue;
                double x = Clamp(want, p.X1 + b.Radius + 10, p.X2 - b.Radius - 10);
                if (!Suitable(p, x, b.Radius, j)) continue;
                double gap = Math.Abs(x - want);
                if (gap >= bestGap) continue;
                bestGap = gap;
                bestAt = new Vec2(x, p.Y - b.Radius);
                found = true;
            }
            if (found) b.Center = bestAt;
            else RemoveWindowBumper(j);
        }

        for (int j = 0; j < 2; j++)
        {
            if (_windowBumpers[j] != null) continue;
            double r = Math.Round(_table.Bumpers[0].Radius * 0.8);
            foreach (var p in plats.Items)
            {
                if (p.Hwnd == _windowHwnds[1 - j] && _windowBumpers[1 - j] != null) continue;
                double x = (p.X1 + p.X2) / 2;
                if (!Suitable(p, x, r, j)) continue;
                var bumper = new PinballTable.Bumper { Center = new Vec2(x, p.Y - r), Radius = r, Window = true };
                _table.Bumpers.Add(bumper);
                _windowBumpers[j] = bumper;
                _windowHwnds[j] = p.Hwnd;
                var art = MakeBumper(r, WindowBumperColor);
                _bumperLayer.Children.Add(art.Sprite);
                _windowArt[j] = art;
                break;
            }
        }
        DrawWindowBumpers();
    }

    bool Suitable(Engine.Platform p, double x, double r, int slot)
    {
        var a = Host.Arena;
        if (p.X2 - p.X1 < MinWindowWidth || p.Y < a.Top + 120 || p.Y > _table.LedgeMaxY) return false;
        if (x < p.X1 + r + 10 || x > p.X2 - r - 10) return false;
        var c = new Vec2(x, p.Y - r);
        double room = 2 * _table.BallR + 10;
        if (c.Y + r > _table.RailYAt(x) - 3 * _table.BallR) return false;
        if (Host.HudBounds.Inflate(20).Intersects(new Rect(c.X - r, c.Y - r, 2 * r, 2 * r))) return false;
        for (int i = 0; i < 3; i++)
        {
            var m = _table.Bumpers[i];
            if ((m.Center - c).Length < m.Radius + r + room) return false;
            if ((_table.Lanes[i] - c).Length < _table.LaneR + r + room) return false;
        }
        var other = _windowBumpers[1 - slot];
        return other == null || (other.Center - c).Length >= other.Radius + r + room;
    }

    void RemoveWindowBumper(int j)
    {
        if (_windowBumpers[j] is { } b) _table.Bumpers.Remove(b);
        if (_windowArt[j] is { } art) _bumperLayer.Children.Remove(art.Sprite);
        _windowBumpers[j] = null;
        _windowArt[j] = null;
        _windowHwnds[j] = IntPtr.Zero;
    }

    BumperArt? ArtFor(PinballTable.Bumper b)
    {
        int i = _table.Bumpers.IndexOf(b);
        if (i >= 0 && i < 3 && i < _mainArt.Count) return _mainArt[i];
        for (int j = 0; j < 2; j++)
            if (_windowBumpers[j] == b) return _windowArt[j];
        return null;
    }

    // ------------------------------------------------------------------ input

    /// <summary>The press area for a flipper: the flipper and a bit around and above it, from the drain gap outward.</summary>
    Rect Box(int side)
    {
        var a = Host.Arena;
        var f = side == 0 ? _table.Left : _table.Right;
        double cx = a.Center.X;
        double w = Math.Min(BoxMaxW, Math.Abs(cx - f.Pivot.X) + 90);
        double top = f.Pivot.Y - BoxAbove, bottom = Math.Min(a.Bottom, f.Pivot.Y + BoxBelow);
        return side == 0 ? new Rect(cx - w, top, w - 2, bottom - top) : new Rect(cx + 2, top, w - 2, bottom - top);
    }

    public override void CollectHitShapes(List<HitShape> into)
    {
        into.Add(HitShape.Box(Box(0)));
        into.Add(HitShape.Box(Box(1)));
        if (!_table.InPlay) into.Add(HitShape.Circle(_table.ServeSpot, ServeReach));
    }

    public override bool PointerDown(Vec2 p, bool right)
    {
        if (!_table.InPlay && (p - _table.ServeSpot).Length <= ServeReach)
        {
            Serve();
            return false;
        }
        bool inLeft = Box(0).Contains(p.ToPoint()), inRight = Box(1).Contains(p.ToPoint());
        int mask = right && (inLeft || inRight) ? 3 : inLeft ? 1 : inRight ? 2 : 0;
        if (mask == 0) return false;
        _held = mask;
        SetFlippers(mask, true);
        return true;
    }

    public override void PointerUp(Vec2 p)
    {
        SetFlippers(_held, false);
        _held = 0;
    }

    /// <summary>Raise or drop the flippers in <paramref name="mask"/> (1 left, 2 right, 3 both).</summary>
    void SetFlippers(int mask, bool up, bool quiet = false)
    {
        for (int i = 0; i < 2; i++)
        {
            if ((mask & (1 << i)) == 0) continue;
            var f = i == 0 ? _table.Left : _table.Right;
            if (f.Up == up) continue;
            f.Up = up;
            if (!quiet) Host.Sound.Play("pin-flipper", up ? 0.6 : 0.25, up ? 1 : 0.8);
        }
    }

    public override void Summon(Vec2 p)
    {
        if (!_table.InPlay) return;
        var a = Host.Arena;
        double r = _table.BallR;
        if (p.X < a.Left + r || p.X > a.Right - r || p.Y < a.Top + r || p.Y > _table.RailYAt(p.X) - 2 * r) return;
        _table.Ball = p;
        _table.BallVel = default;
    }

    // ------------------------------------------------------------------ simulation

    public override bool Update(double dt)
    {
        _time += dt;
        SyncWindows(force: false);
        bool busy = RunDemoFlips();

        if (_table.InPlay || _table.FlippersMoving)
        {
            _table.Advance(dt);
            busy = true;
        }

        foreach (var art in _mainArt) busy |= Fade(art, dt);
        foreach (var art in _windowArt)
            if (art != null) busy |= Fade(art, dt);
        for (int i = 0; i < 2; i++)
        {
            if (_slingT[i] <= 0) continue;
            _slingT[i] = Math.Max(0, _slingT[i] - dt);
            busy = true;
        }

        Draw();
        return busy || _table.InPlay || _table.FlippersMoving;
    }

    static bool Fade(BumperArt art, double dt)
    {
        if (art.FlashT <= 0) return false;
        art.FlashT = Math.Max(0, art.FlashT - dt);
        return true;
    }

    void PlaySound(string name, double vol, double pitch)
    {
        if (_lastSound.TryGetValue(name, out double t) && _time - t < 0.05) return;
        _lastSound[name] = _time;
        Host.Sound.Play(name, vol, pitch);
    }

    // ------------------------------------------------------------------ visuals

    void Draw()
    {
        _ball.Set(_table.Ball);
        _ball.Scale = _table.BallR / BallArtR;
        _serveRing.IsVisible = !_table.InPlay;
        Canvas.SetLeft(_serveRing, _table.ServeSpot.X - ServeReach);
        Canvas.SetTop(_serveRing, _table.ServeSpot.Y - ServeReach);

        _flippers[0].Set(_table.Left.Pivot, _table.Left.Angle * 180 / Math.PI);
        _flippers[1].Set(_table.Right.Pivot, _table.Right.Angle * 180 / Math.PI);

        foreach (var art in _mainArt) DrawFlash(art);
        DrawWindowBumpers();
        for (int i = 0; i < 3; i++) _laneLights[i].Fill = _table.LaneLit[i] ? LaneOn : LaneOff;
        for (int i = 0; i < 2; i++)
            if (_slingFlash[i] is { } flash) flash.Opacity = _slingT[i] / FlashTime;
    }

    void DrawWindowBumpers()
    {
        for (int j = 0; j < 2; j++)
        {
            if (_windowArt[j] is not { } art || _windowBumpers[j] is not { } b) continue;
            art.Sprite.Set(b.Center);
            DrawFlash(art);
        }
    }

    static void DrawFlash(BumperArt art)
    {
        double k = art.FlashT / FlashTime;
        art.Flash.Opacity = k;
        art.Sprite.Scale = Fx.ReducedMotion ? 1 : 1 + 0.1 * k;
    }

    /// <summary>The fixed parts (rails, slingshots, lanes, the table's own bumpers), redrawn when the arena changes.</summary>
    void DrawTable()
    {
        _tableArt.Children.Clear();
        foreach (var art in _mainArt) _bumperLayer.Children.Remove(art.Sprite);
        _mainArt.Clear();
        string F(double v) => Art.F(v);

        var shade = Art.Brush(150, 20, 22, 28);
        var chrome = Art.Brush("#C9D1DC");
        for (int i = 0; i < 2; i++)
        {
            var s = _table.Segments[i];
            string d = $"M{F(s.A.X)},{F(s.A.Y)} L{F(s.B.X)},{F(s.B.Y)}";
            _tableArt.Children.Add(Art.PathOf(d, null, shade, s.Radius * 2 + 3));
            _tableArt.Children.Add(Art.PathOf(d, null, chrome, s.Radius * 2 - 2));
        }

        var rubber = Art.Brush(235, 245, 245, 245);
        var body = Art.Brush(90, 255, 255, 255);
        for (int side = 0; side < 2; side++)
        {
            var p = _table.Slings[side];
            _tableArt.Children.Add(Art.PathOf(
                $"M{F(p[0].X)},{F(p[0].Y)} L{F(p[1].X)},{F(p[1].Y)} L{F(p[2].X)},{F(p[2].Y)} Z", body, rubber, 6));
            string face = $"M{F(p[2].X)},{F(p[2].Y)} L{F(p[0].X)},{F(p[0].Y)}";
            _tableArt.Children.Add(Art.PathOf(face, null, Art.Brush(BumperColors[0]), 6));
            var flash = Art.PathOf(face, null, Brushes.White, 9);
            flash.Opacity = 0;
            _slingFlash[side] = flash;
            _tableArt.Children.Add(flash);
        }

        var postFill = Art.Brush("#DDE3EA");
        foreach (var post in _table.Posts)
            _tableArt.Children.Add(Art.Circle(post.X, post.Y, _table.PostRadius, postFill, shade, 1.5));
        for (int i = 0; i < 3; i++)
        {
            var lane = _table.Lanes[i];
            _laneLights[i] = Art.Circle(lane.X, lane.Y, 8, LaneOff, LaneRim, 1.5);
            _tableArt.Children.Add(_laneLights[i]);
        }

        for (int i = 0; i < 3; i++)
        {
            var b = _table.Bumpers[i];
            var art = MakeBumper(b.Radius, BumperColors[i]);
            art.Sprite.Set(b.Center);
            _bumperLayer.Children.Insert(i, art.Sprite);
            _mainArt.Add(art);
        }

        for (int i = 0; i < 2; i++)
        {
            var f = i == 0 ? _table.Left : _table.Right;
            _flipperBodies[i].Data = Geometry.Parse(FlipperPath(f.Length, f.BaseR, f.TipR));
        }
    }

    public override void ThemeChanged()
    {
        foreach (var body in _flipperBodies) body.Fill = Art.Brush(Themes.Current.Mine);
    }

    static string FlipperPath(double len, double baseR, double tipR)
    {
        string F(double v) => Art.F(v);
        return $"M0,{F(-baseR)} L{F(len)},{F(-tipR)} A{F(tipR)},{F(tipR)} 0 0 1 {F(len)},{F(tipR)} " +
               $"L0,{F(baseR)} A{F(baseR)},{F(baseR)} 0 0 1 0,{F(-baseR)} Z";
    }

    static Sprite MakeFlipper(out Path body)
    {
        var s = new Sprite { IsHitTestVisible = false };
        body = Art.PathOf(FlipperPath(125, 12, 8), Art.Brush(Themes.Current.Mine), Art.Brush(240, 255, 255, 255), 2.5);
        s.Rotor.Children.Add(body);
        s.Rotor.Children.Add(Art.Circle(0, 0, 4, Art.Brush("#DDE3EA"), Art.Brush(160, 20, 22, 28), 1));
        return s;
    }

    static BumperArt MakeBumper(double r, Color color)
    {
        var s = new Sprite { IsHitTestVisible = false };
        s.Children.Add(Art.Circle(2, 4, r, Art.Brush(60, 0, 0, 0)));
        s.Children.Add(Art.Circle(0, 0, r, Art.Brush(Art.Blend(color, Colors.Black, 0.35)), Art.Brush(230, 255, 255, 255), 2.5));
        var cap = new RadialGradientBrush { GradientOrigin = new RelativePoint(0.35, 0.3, RelativeUnit.Relative) };
        cap.GradientStops.Add(new GradientStop(Art.Blend(color, Colors.White, 0.6), 0));
        cap.GradientStops.Add(new GradientStop(color, 0.6));
        cap.GradientStops.Add(new GradientStop(Art.Blend(color, Colors.Black, 0.2), 1));
        s.Children.Add(Art.Circle(0, 0, r * 0.68, cap, Art.Brush(200, 255, 255, 255), 1.5));
        s.Children.Add(Art.PathOf(Art.StarPath(0, 0, r * 0.3, r * 0.13), Art.Brush(220, 255, 255, 255)));
        var flash = Art.Circle(0, 0, r * 1.08, Art.Brush(170, 255, 255, 255));
        flash.Opacity = 0;
        s.Children.Add(flash);
        return new BumperArt { Sprite = s, Flash = flash };
    }

    static IBrush SteelBrush()
    {
        var body = new RadialGradientBrush { GradientOrigin = new RelativePoint(0.35, 0.3, RelativeUnit.Relative) };
        body.GradientStops.Add(new GradientStop(Colors.White, 0));
        body.GradientStops.Add(new GradientStop(Color.FromRgb(0xB8, 0xC0, 0xCC), 0.45));
        body.GradientStops.Add(new GradientStop(Color.FromRgb(0x4E, 0x56, 0x62), 1));
        return body;
    }

    static Sprite MakeBall()
    {
        var s = new Sprite { IsHitTestVisible = false };
        s.Children.Add(Art.Circle(2, 4, BallArtR, Art.Brush(60, 0, 0, 0)));
        s.Children.Add(Art.Circle(0, 0, BallArtR, SteelBrush(), Art.Brush("#3C434D"), 1));
        s.Children.Add(Art.Circle(-BallArtR * 0.35, -BallArtR * 0.4, BallArtR * 0.22, Art.Brush(220, 255, 255, 255)));
        return s;
    }

    // ------------------------------------------------------------------ demo

    public override void DemoTick()
    {
        if (!_table.InPlay)
        {
            if (_demoServeTicks < 0) _demoServeTicks = 5 + Rng.Next(8);
            else if (_demoServeTicks-- == 0) Serve();
            return;
        }
        _demoServeTicks = -1;
        // look ahead past the next tick and flip just before the ball reaches the sweet spot; a little
        // timing noise means it misses now and then, like a person would
        int side = _table.PredictFlip(0.4, out double at);
        if (side < 0 || _demoFlipAt[side] >= 0 || _demoReleaseAt[side] >= 0) return;
        _demoFlipAt[side] = _time + Math.Max(0, at - 0.03 + (Rng.NextDouble() - 0.5) * 0.05);
    }

    bool RunDemoFlips()
    {
        bool pending = false;
        for (int i = 0; i < 2; i++)
        {
            if (_demoFlipAt[i] >= 0 && _time >= _demoFlipAt[i])
            {
                _demoFlipAt[i] = -1;
                SetFlippers(1 << i, true);
                _demoReleaseAt[i] = _time + DemoHold;
            }
            else if (_demoReleaseAt[i] >= 0 && _time >= _demoReleaseAt[i])
            {
                _demoReleaseAt[i] = -1;
                SetFlippers(1 << i, false);
            }
            pending |= _demoFlipAt[i] >= 0 || _demoReleaseAt[i] >= 0;
        }
        return pending;
    }
}
