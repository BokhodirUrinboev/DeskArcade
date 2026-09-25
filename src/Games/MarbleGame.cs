using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using DeskArcade.Engine;
using static DeskArcade.Games.MarbleRules;

namespace DeskArcade.Games;

/// <summary>
/// Marble Run (see <see cref="MarbleRules"/>): a marble waits in a funnel near the top of the screen and a cup is
/// sunk into the taskbar somewhere else. Drag a ramp or a bumper out of the tray beside the funnel and drop it on
/// the desktop; drag a placed piece to move it, drag a ramp's knob to tilt it, right-click a piece to put it back.
/// Click the funnel and the marble falls, rolls along your window tops and off their edges, bounces off the pieces
/// and drops into the cup if it gets there slowly enough. Fewer pieces score more; five courses make a round, which
/// races the computer or a co-worker. <b>Ctrl+Alt+B</b> brings the tray to the cursor.
/// </summary>
public sealed class MarbleGame : MiniGame
{
    const double FunnelReach = 30, TrayW = 118, TrayH = 44, SlotW = 40, GrabR = 13, KnobR = 7, TurnSnap = 5;
    const double SinkTime = 0.3, AfterCup = 1.3, AfterMiss = 0.9;

    /// <summary>A fair round for a decent player: every course sunk, most of them with a piece or two and a miss here and there.</summary>
    public const int FairRound = 550;
    /// <summary>About how long a round of five courses takes, in seconds.</summary>
    public const double TypicalRoundSeconds = 100;

    static readonly Color Gold = Color.FromRgb(255, 209, 102);

    enum Drag { None, Move, Turn }

    readonly MarbleRules _rules = new(new Rect(0, 0, 1920, 1040));
    readonly Canvas _pieceLayer = new() { IsHitTestVisible = false };
    readonly Line _plumb = new() { StrokeThickness = 1.5, StrokeDashArray = new AvaloniaList<double> { 3, 5 }, IsHitTestVisible = false };
    readonly Border _tray = new() { Width = TrayW, Height = TrayH, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1.5), IsHitTestVisible = false };
    readonly Canvas _trayInside = new();
    readonly Ellipse[] _trayDots = new Ellipse[Budget];
    readonly Dictionary<MarblePiece, Sprite> _sprites = new();
    readonly Dictionary<string, double> _lastSound = new();

    Sprite _marble = new(), _cup = new(), _funnel = new();
    Canvas _rampIcon = new(), _bumperIcon = new();
    Drag _drag;
    MarblePiece? _dragPiece;
    bool _dragNew, _placed, _roundOn, _between, _builtColorBlind;
    Vec2 _dragOffset;
    Vec2? _trayAt; // where the tray was summoned to; otherwise it sits beside the funnel
    double _acc, _time;
    int _lastScore, _lastSunk, _rounds, _seenGen = -1;

    public MarbleGame(IGameHost host) : base(host)
    {
        _tray.Child = _trayInside;
        for (int i = 0; i < Budget; i++) _trayInside.Children.Add(_trayDots[i] = new Ellipse { Width = 7, Height = 7 });
        Layer.Children.Add(_plumb);
        Layer.Children.Add(_pieceLayer);
        BuildArt(); // under them the cup; over them the funnel, the marble and the tray
    }

    public override string Id => "marble";
    public override string Title => "Marble Run";

    // A round of five courses is the race; the computer or the co-worker plays a round of their own alongside.
    public override bool SupportsLan => true;
    public override (int Score, bool Active)? Race => (_roundOn ? _rules.Score : _lastScore, _roundOn);
    public override int RaceBaseline => FairRound;
    public override int RaceBest => (int)Host.Stats.Get("marble.best");
    public override double RaceSeconds => TypicalRoundSeconds;
    public override int RaceMax => MaxRound;

    public override void StartRace() => BeginRound();

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        s.Rotor.Children.Add(Art.PathOf("M-9,-4 L5,1", null, Art.Brush(Themes.Current.Accent), 2.4));
        s.Rotor.Children.Add(Art.At(new Ellipse { Width = 11, Height = 4, Fill = Art.Brush("#101214"), Stroke = Art.Brush(Themes.Current.Line), StrokeThickness = 0.8 }, -2, 6));
        s.Rotor.Children.Add(Art.Circle(-6, -9, 3.2, Art.Brush(Art.Safe(Themes.Current.Mine)), Art.Brush(90, 0, 0, 0), 0.6));
        return s;
    }

    public override HudInfo Hud
    {
        get
        {
            var g = _rules;
            string line = _roundOn
                ? L.F("Course {0}/{1} · pieces left {2} · try {3}/{4}", g.Course, Courses, g.PiecesLeft, Math.Min(Tries, g.Misses + 1), Tries)
                : _rounds > 0 ? L.F("Round over · {0} of {1} in the cup · click the funnel to play again", _lastSunk, Courses)
                : L.T("Drag a ramp or a bumper out of the tray, then click the funnel");
            return new HudInfo((_roundOn ? g.Score : _lastScore).ToString(CultureInfo.InvariantCulture), line, L.F("Best {0}", Host.Stats.Get("marble.best")));
        }
    }

    // ------------------------------------------------------------------ layout

    public override void Layout()
    {
        var a = Host.Arena;
        bool resized = _rules.Arena != a;
        _rules.Arena = a;
        if (!_placed)
        {
            _placed = true;
            _rules.NewRound(Rng, Host.HudBounds);
        }
        else if (resized && !_rules.Running && !_between && (!a.Contains(_rules.Drop.ToPoint()) || _rules.CupX < a.Left || _rules.CupX > a.Right))
        {
            _rules.Plan(Rng, Host.HudBounds); // the screen changed under the course: a fresh one, same course number
        }
        if (_builtColorBlind != Art.ColorBlind) BuildArt(); // the overlay calls Layout when colour-blind mode is toggled
        SyncPieces();
        Draw();
        Host.HudChanged();
    }

    public override void ThemeChanged()
    {
        BuildArt();
        SyncPieces(rebuild: true);
        Draw();
    }

    public override void Deactivate() => EndDrag(cancel: true);

    public override void PointerCancel() => EndDrag(cancel: true);

    public override void PositionsReset() => _trayAt = null;

    /// <summary>Brings the tray of pieces to the cursor.</summary>
    public override void Summon(Vec2 p)
    {
        _trayAt = p - new Vec2(TrayW / 2, TrayH / 2);
        Draw();
    }

    Rect TrayRect
    {
        get
        {
            var a = Host.Arena;
            var d = _rules.Drop;
            // beside the funnel: on its right, unless the screen's edge or the scoreboard is there
            var right = new Rect(d.X + FunnelReach + 12, d.Y - TrayH / 2, TrayW, TrayH);
            Vec2 at = _trayAt ?? (right.Right < a.Right && !right.Intersects(Host.HudBounds) ? new Vec2(right.X, right.Y) : new Vec2(d.X - FunnelReach - 12 - TrayW, right.Y));
            return new Rect(Clamp(at.X, a.Left + 4, Math.Max(a.Left + 4, a.Right - TrayW - 4)), Clamp(at.Y, a.Top + 4, Math.Max(a.Top + 4, a.Bottom - TrayH - 4)), TrayW, TrayH);
        }
    }

    Rect SlotRect(MarblePieceKind kind)
    {
        var t = TrayRect;
        return new Rect(t.X + 6 + (kind == MarblePieceKind.Ramp ? 0 : SlotW + 4), t.Y + 2, SlotW, TrayH - 4);
    }

    // ------------------------------------------------------------------ round flow

    /// <summary>The round is under way from the first release (or from the rival's start, in a race).</summary>
    void BeginRound()
    {
        if (_roundOn) return;
        if (_rules.Course != 1 || _rules.Score != 0) _rules.NewRound(Rng, Host.HudBounds);
        _roundOn = true;
        SyncPieces();
        Host.RoundStarted();
        Draw();
        Host.HudChanged();
    }

    void Release()
    {
        if (_between || _rules.Running) return;
        BeginRound();
        if (!_rules.Release()) return;
        _acc = 0;
        _marble.Scale = 1;
        _marble.Opacity = 1;
        Host.Sound.Play("whoosh", 0.3, 1.4);
        Draw();
        Host.HudChanged();
    }

    void InTheCup()
    {
        var g = _rules;
        int used = g.Pieces.Count, misses = g.Misses;
        int points = g.Sink();
        _between = true;
        var cup = new Vec2(g.CupX, g.CupY);
        Host.Stats.Add("marble.cups");
        if (used == 0) Host.Stats.Add("marble.bare");
        Host.Stats.Max("marble.streak", g.Streak);
        Host.ShareAction(cup, points);
        string title = used == 0 && misses == 0 ? L.T("NO HANDS!") : misses == 0 ? L.T("IN THE CUP!") : L.T("In the cup");
        string sub = g.Streak >= 2 ? L.F("{0} in a row", g.Streak) : L.F("{0} of {1} pieces", used, Budget);
        Host.Fx.Popup(cup - new Vec2(0, 90), $"+{points}", misses == 0 ? Gold : Colors.White, 30, 1.4, title + " · " + sub);
        Host.Fx.Burst(cup - new Vec2(0, 12), Themes.Current.Confetti, misses == 0 ? 30 : 14, 420, 650, 6, 0.9);
        Host.Sound.Play(misses == 0 ? "fire" : "score", 0.6);
        var from = _marble;
        Anims.Add(SinkTime, k => from.Scale = 1 - 0.7 * k, Ease.InQuad, () => from.Opacity = 0);
        Anims.After(AfterCup, NextCourse);
        Host.HudChanged();
    }

    void Missed()
    {
        var g = _rules;
        bool lost = g.Miss();
        _between = true;
        Host.ShareAction(g.Pos, 0);
        Host.Fx.Popup(g.Pos - new Vec2(0, 44), lost ? L.T("COURSE LOST") : L.T("MISS"), Color.FromRgb(255, 160, 160), 24, 1.1,
            lost ? L.T("on to the next one") : L.F("try {0} of {1}", g.Misses + 1, Tries));
        Host.Sound.Play("buzzer", 0.25);
        var m = _marble;
        Anims.Add(AfterMiss * 0.6, k => m.Opacity = 1 - k, Ease.InQuad, delay: AfterMiss * 0.4);
        Anims.After(AfterMiss, () =>
        {
            if (lost)
            {
                NextCourse();
                return;
            }
            _between = false;
            g.Rest(); // back to the funnel; the pieces stay put for another go
            _marble.Opacity = 1;
            Draw();
            Host.HudChanged();
        });
        Host.HudChanged();
    }

    void NextCourse()
    {
        _between = false;
        _rules.NextCourse(Rng, Host.HudBounds);
        _marble.Scale = 1;
        _marble.Opacity = 1;
        if (_rules.RoundOver) EndRound();
        SyncPieces();
        Draw();
        Host.HudChanged();
    }

    void EndRound()
    {
        var g = _rules;
        _roundOn = false;
        _lastScore = g.Score;
        _lastSunk = g.Sunk;
        _rounds++;
        long before = Host.Stats.Get("marble.best");
        Host.Stats.Max("marble.best", g.Score);
        Host.Stats.Add("marble.rounds");
        Host.RoundEnded(g.Score);
        bool best = g.Score > before;
        var a = Host.Arena;
        var at = new Vec2(a.Center.X, a.Top + a.Height * 0.3);
        Host.Fx.Popup(at, best ? L.T("NEW BEST!") : L.T("ROUND OVER"), best ? Gold : Colors.White, 36, 2.4,
            L.F("{0} points · {1} of {2} in the cup", g.Score, g.Sunk, Courses));
        if (best) Host.Fx.Burst(at, Themes.Current.Confetti, 40, 520, 700, 7, 1.1);
        Host.Sound.Play(best ? "best" : "done", 0.7);
        g.NewRound(Rng, Host.HudBounds); // the next round's first course is ready; it starts with the first release
    }

    // ------------------------------------------------------------------ input

    public override void CollectHitShapes(List<HitShape> into)
    {
        into.Add(HitShape.Circle(_rules.Drop, FunnelReach));
        into.Add(HitShape.Box(TrayRect));
        foreach (var p in _rules.Pieces)
        {
            if (p.Kind == MarblePieceKind.Bumper)
            {
                into.Add(HitShape.Circle(p.Pos, BumperR + 4));
                continue;
            }
            for (int i = -2; i <= 2; i++) into.Add(HitShape.Circle(p.Pos + p.Dir * (RampHalf * 0.4 * i), GrabR));
            into.Add(HitShape.Circle(p.B, GrabR));
        }
    }

    /// <summary>The piece under the pointer, and whether it was a ramp's knob (knobs first: they sit on the ends).</summary>
    (MarblePiece? Piece, bool Knob) PieceAt(Vec2 p)
    {
        for (int i = _rules.Pieces.Count - 1; i >= 0; i--)
        {
            var piece = _rules.Pieces[i];
            if (piece.Kind == MarblePieceKind.Ramp && (p - piece.B).Length <= GrabR) return (piece, true);
        }
        for (int i = _rules.Pieces.Count - 1; i >= 0; i--)
        {
            var piece = _rules.Pieces[i];
            if (piece.Kind == MarblePieceKind.Bumper ? (p - piece.Pos).Length <= BumperR + 4 : DistanceToRamp(piece, p) <= GrabR) return (piece, false);
        }
        return (null, false);
    }

    static double DistanceToRamp(MarblePiece r, Vec2 p)
    {
        Vec2 a = r.A, ab = r.B - a;
        double t = Math.Clamp(Vec2.Dot(p - a, ab) / ab.LengthSquared, 0, 1);
        return (p - (a + ab * t)).Length;
    }

    public override bool PointerDown(Vec2 p, bool right)
    {
        if (_drag != Drag.None) return false;
        var (piece, knob) = PieceAt(p);
        if (piece != null)
        {
            if (_rules.Running || _between) return false; // the course is set while the marble runs
            if (right)
            {
                _rules.Remove(piece);
                Host.Sound.Play("pop", 0.35, 0.9);
                SyncPieces();
                Draw();
                Host.HudChanged();
                return false;
            }
            StartDrag(piece, knob ? Drag.Turn : Drag.Move, piece.Pos - p, isNew: false);
            return true;
        }
        if (TrayRect.Contains(p.ToPoint()))
        {
            if (right || _rules.Running || _between) return false;
            var kind = SlotRect(MarblePieceKind.Bumper).Contains(p.ToPoint()) ? MarblePieceKind.Bumper : MarblePieceKind.Ramp;
            if (!_rules.CanPlace)
            {
                Host.Fx.Popup(p - new Vec2(0, 30), L.T("No pieces left"), Colors.White, 18, 0.9, L.T("right-click a piece to take it back"));
                Host.Sound.Play("buzzer", 0.15);
                return false;
            }
            var placed = _rules.Place(kind, p, kind == MarblePieceKind.Ramp ? 20 : 0)!;
            SyncPieces();
            StartDrag(placed, Drag.Move, default, isNew: true);
            Host.HudChanged();
            return true;
        }
        if (!right && (p - _rules.Drop).Length <= FunnelReach) Release();
        return false;
    }

    void StartDrag(MarblePiece piece, Drag how, Vec2 offset, bool isNew)
    {
        _drag = how;
        _dragPiece = piece;
        _dragOffset = offset;
        _dragNew = isNew;
        Host.Sound.Play("board", 0.2, 1.6);
    }

    public override void PointerUp(Vec2 p)
    {
        if (_drag == Drag.None) return;
        FollowPointer(p);
        // a new piece dropped back on the tray goes back in it
        EndDrag(cancel: _dragNew && TrayRect.Contains(p.ToPoint()));
    }

    void EndDrag(bool cancel)
    {
        if (_drag == Drag.None) return;
        if (cancel && _dragNew && _dragPiece != null) _rules.Remove(_dragPiece);
        else Host.Sound.Play("thunk", 0.2, 1.4);
        _drag = Drag.None;
        _dragPiece = null;
        SyncPieces();
        Draw();
        Host.HudChanged();
    }

    void FollowPointer(Vec2 p)
    {
        if (_dragPiece is not { } piece) return;
        if (_drag == Drag.Move) piece.Pos = _rules.Keep(p + _dragOffset);
        else
        {
            var d = p - piece.Pos;
            if (d.Length > 4) piece.Angle = Math.Round(Math.Atan2(d.Y, d.X) * 180 / Math.PI / TurnSnap) * TurnSnap;
        }
        PlacePiece(piece);
    }

    // ------------------------------------------------------------------ simulation

    public override bool Update(double dt)
    {
        _time += dt;
        bool busy = Anims.Update(dt);
        if (Host.Platforms.Generation != _seenGen)
        {
            _seenGen = Host.Platforms.Generation;
            DrawPlumb(); // a window moved under the drop point
        }
        if (_drag != Drag.None)
        {
            FollowPointer(Host.Pointer);
            busy = true;
        }
        if (_rules.Running)
        {
            _acc += Math.Min(dt, 0.1);
            var tops = Host.Platforms.Items;
            while (_acc >= Step && _rules.Running)
            {
                _acc -= Step;
                React(_rules.Advance(tops));
            }
            _marble.Set(_rules.Pos, _rules.Angle);
            busy = true;
        }
        return busy || _rules.Running || _drag != Drag.None;
    }

    void React(MarbleEvents ev)
    {
        if (ev.Floor > 160) PlayThrottled("bounce", Math.Min(0.5, ev.Floor / 2400), 2.1);
        if (ev.Wall > 160) PlayThrottled("bounce", Math.Min(0.4, ev.Wall / 2400), 2.3);
        if (ev.LippedOut) PlayThrottled("rim", 0.3, 1.7);
        if (ev.Piece >= 0 && ev.PieceSpeed > 120)
        {
            var piece = _rules.Pieces[ev.Piece];
            if (piece.Kind == MarblePieceKind.Bumper)
            {
                PlayThrottled("pin-bumper", 0.5, 1.1);
                Flash(piece);
            }
            else PlayThrottled("board", Math.Min(0.4, ev.PieceSpeed / 2000), 1.5);
        }
        if (ev.Outcome == MarbleOutcome.Cup) InTheCup();
        else if (ev.Outcome == MarbleOutcome.Miss) Missed();
    }

    /// <summary>A struck bumper swells for an instant.</summary>
    void Flash(MarblePiece bumper)
    {
        if (!_sprites.TryGetValue(bumper, out var s)) return;
        Anims.Add(0.18, k => s.Scale = 1 + 0.25 * k, Ease.Pulse, () => s.Scale = 1);
    }

    void PlayThrottled(string name, double vol, double pitch = 1)
    {
        if (_lastSound.TryGetValue(name, out double t) && _time - t < 0.08) return;
        _lastSound[name] = _time;
        Host.Sound.Play(name, vol, pitch);
    }

    // ------------------------------------------------------------------ demo

    /// <summary>Lays a ramp under the funnel toward the cup (a new tilt after each miss) and lets the marble go.</summary>
    public override void DemoTick()
    {
        if (_rules.Running || _between || _drag != Drag.None) return;
        if (_rules.Pieces.Count == 0)
        {
            var (pos, angle) = _rules.SuggestRamp(15 + Rng.NextDouble() * 25);
            _rules.Place(MarblePieceKind.Ramp, pos, angle);
            SyncPieces();
        }
        else if (_rules.Misses > 0 && _rules.Pieces[0].Kind == MarblePieceKind.Ramp)
        {
            var ramp = _rules.Pieces[0];
            ramp.Angle = Math.Sign(ramp.Angle == 0 ? 1 : ramp.Angle) * (10 + Rng.NextDouble() * 35);
            PlacePiece(ramp);
        }
        Release();
    }

    // ------------------------------------------------------------------ drawing

    void BuildArt()
    {
        _builtColorBlind = Art.ColorBlind;
        var t = Themes.Current;
        Layer.Children.Remove(_cup);
        Layer.Children.Remove(_funnel);
        Layer.Children.Remove(_marble);
        _cup = MakeCup(t);
        _funnel = MakeFunnel(t);
        _marble = MakeMarble(Art.Safe(t.Mine));
        Layer.Children.Insert(0, _cup);
        Layer.Children.Add(_funnel);
        Layer.Children.Add(_marble);
        Layer.Children.Remove(_tray);
        Layer.Children.Add(_tray); // the tray on top

        _plumb.Stroke = Art.Brush(Color.FromArgb(90, t.Line.R, t.Line.G, t.Line.B));
        _tray.Background = Art.Brush(Color.FromArgb(215, t.Ink.R, t.Ink.G, t.Ink.B));
        _tray.BorderBrush = Art.Brush(Color.FromArgb(120, t.Accent.R, t.Accent.G, t.Accent.B));
        _trayInside.Children.Remove(_rampIcon);
        _trayInside.Children.Remove(_bumperIcon);
        _rampIcon = new Canvas { Width = SlotW, Height = TrayH - 4 };
        _rampIcon.Children.Add(RampShape(Art.Safe(t.Accent), 15, SlotW / 2, (TrayH - 4) / 2 - 1));
        _bumperIcon = new Canvas { Width = SlotW, Height = TrayH - 4 };
        foreach (var c in BumperShapes(10, SlotW / 2, (TrayH - 4) / 2 - 1)) _bumperIcon.Children.Add(c);
        _trayInside.Children.Add(Art.At(_rampIcon, 4, 0.5));
        _trayInside.Children.Add(Art.At(_bumperIcon, 4 + SlotW + 4, 0.5));
    }

    /// <summary>Keeps one sprite per placed piece.</summary>
    void SyncPieces(bool rebuild = false)
    {
        foreach (var (piece, sprite) in _sprites.ToList())
        {
            if (!rebuild && _rules.Pieces.Contains(piece)) continue;
            _pieceLayer.Children.Remove(sprite);
            _sprites.Remove(piece);
        }
        foreach (var piece in _rules.Pieces)
        {
            if (_sprites.ContainsKey(piece)) continue;
            var s = MakePiece(piece.Kind);
            _sprites[piece] = s;
            _pieceLayer.Children.Add(s);
            PlacePiece(piece);
        }
    }

    void PlacePiece(MarblePiece piece)
    {
        if (_sprites.TryGetValue(piece, out var s)) s.Set(piece.Pos, piece.Kind == MarblePieceKind.Ramp ? piece.Angle : 0);
    }

    void Draw()
    {
        var g = _rules;
        _cup.Set(new Vec2(g.CupX, g.CupY));
        _funnel.Set(g.Drop);
        _marble.Set(g.Pos, g.Angle);
        var tray = TrayRect;
        Canvas.SetLeft(_tray, tray.X);
        Canvas.SetTop(_tray, tray.Y);
        bool open = !g.Running && !_between;
        _tray.Opacity = open && g.CanPlace ? 1 : 0.55;
        var t = Themes.Current;
        for (int i = 0; i < _trayDots.Length; i++)
        {
            var dot = _trayDots[i];
            dot.Fill = i < g.PiecesLeft ? Art.Brush(t.Gold) : Art.Brush(60, 255, 255, 255);
            Canvas.SetLeft(dot, TrayW - 20);
            Canvas.SetTop(dot, 7 + i * 10);
        }
        foreach (var piece in g.Pieces) PlacePiece(piece);
        DrawPlumb();
    }

    /// <summary>A dashed line from the funnel down to the first thing the marble will land on, while it waits.</summary>
    void DrawPlumb()
    {
        var g = _rules;
        _plumb.IsVisible = !g.Running && !_between;
        if (!_plumb.IsVisible) return;
        double y = g.SurfaceBelow(g.Drop, Host.Platforms.Items);
        _plumb.StartPoint = new Point(g.Drop.X, g.Drop.Y + R + 4);
        _plumb.EndPoint = new Point(g.Drop.X, Math.Max(g.Drop.Y + R + 4, y));
    }

    static Sprite MakeMarble(Color c)
    {
        var s = new Sprite { IsHitTestVisible = false };
        s.Children.Insert(0, Art.Circle(1.5, 2.5, R, Art.Brush(50, 0, 0, 0)));
        var glass = new RadialGradientBrush { GradientOrigin = new RelativePoint(0.35, 0.3, RelativeUnit.Relative) };
        glass.GradientStops.Add(new GradientStop(Art.Blend(c, Colors.White, 0.55), 0));
        glass.GradientStops.Add(new GradientStop(c, 0.6));
        glass.GradientStops.Add(new GradientStop(Art.Blend(c, Colors.Black, 0.45), 1));
        s.Rotor.Children.Add(Art.Circle(0, 0, R, glass, Art.Brush(110, 0, 0, 0), 0.8));
        // the swirl inside the glass, which shows it rolling
        s.Rotor.Children.Add(Art.PathOf($"M{Art.F(-R * 0.6)},{Art.F(R * 0.2)} Q0,{Art.F(-R * 0.7)} {Art.F(R * 0.6)},{Art.F(-R * 0.1)}", null,
            Art.Brush(Color.FromArgb(200, 255, 255, 255)), 1.6));
        s.Children.Add(Art.At(new Ellipse { Width = R * 0.7, Height = R * 0.45, Fill = Art.Brush(170, 255, 255, 255) }, -R * 0.62, -R * 0.66));
        return s;
    }

    static Sprite MakeCup(Theme t)
    {
        var s = new Sprite { IsHitTestVisible = false };
        s.Children.Add(Art.At(new Ellipse { Width = CupHalf * 2 + 6, Height = 10, Fill = Art.Brush("#101214") }, -CupHalf - 3, -5));
        s.Children.Add(Art.At(new Ellipse { Width = CupHalf * 2 + 6, Height = 10, Stroke = Art.Brush(t.Line), StrokeThickness = 1.5 }, -CupHalf - 3, -5));
        // a pennant beside the mouth, so the cup can be found on a busy taskbar
        s.Children.Add(Art.PathOf($"M{Art.F(CupHalf + 6)},-3 L{Art.F(CupHalf + 6)},-48", null, Art.Brush("#E9ECEF"), 2));
        s.Children.Add(Art.PathOf($"M{Art.F(CupHalf + 7)},-48 L{Art.F(CupHalf + 27)},-41 L{Art.F(CupHalf + 7)},-34 Z", Art.Brush(Art.Safe(t.Gold))));
        return s;
    }

    static Sprite MakeFunnel(Theme t)
    {
        var s = new Sprite();
        var fill = Art.Brush(Color.FromArgb(200, t.Ink.R, t.Ink.G, t.Ink.B));
        var rim = Art.Brush(t.Accent);
        s.Children.Add(Art.PathOf($"M-24,-26 L24,-26 L{Art.F(R + 3)},-4 L{Art.F(R + 3)},{Art.F(R + 2)} M-24,-26 L{Art.F(-R - 3)},-4 L{Art.F(-R - 3)},{Art.F(R + 2)}", null, rim, 3));
        s.Children.Insert(0, Art.PathOf($"M-24,-26 L24,-26 L{Art.F(R + 3)},-4 L{Art.F(R + 3)},{Art.F(R + 2)} L{Art.F(-R - 3)},{Art.F(R + 2)} L{Art.F(-R - 3)},-4 Z", fill));
        return s;
    }

    static Shape RampShape(Color c, double half, double x, double y) => Art.At(new Rectangle
    {
        Width = half * 2, Height = RampThick * 2, RadiusX = RampThick, RadiusY = RampThick, Fill = Art.Brush(c), Stroke = Art.Brush(90, 0, 0, 0), StrokeThickness = 1,
        RenderTransform = new RotateTransform(20), RenderTransformOrigin = RelativePoint.Center,
    }, x - half, y - RampThick);

    static IEnumerable<Control> BumperShapes(double r, double x, double y)
    {
        var t = Themes.Current;
        yield return Art.Circle(x, y, r, Art.Brush(Art.Safe(t.Rival)), Art.Brush(Art.Safe(t.Gold)), Math.Max(1.5, r * 0.18));
        yield return Art.Circle(x, y, r * 0.35, Art.Brush(200, 255, 255, 255));
    }

    static Sprite MakePiece(MarblePieceKind kind)
    {
        var s = new Sprite { IsHitTestVisible = false };
        var t = Themes.Current;
        if (kind == MarblePieceKind.Bumper)
        {
            foreach (var c in BumperShapes(BumperR, 0, 0)) s.Children.Add(c);
            return s;
        }
        s.Rotor.Children.Add(Art.At(new Rectangle
        {
            Width = RampHalf * 2, Height = RampThick * 2, RadiusX = RampThick, RadiusY = RampThick,
            Fill = Art.Brush(Art.Safe(t.Accent)), Stroke = Art.Brush(110, 0, 0, 0), StrokeThickness = 1,
        }, -RampHalf, -RampThick));
        // the knob that turns it
        s.Rotor.Children.Add(Art.Circle(RampHalf, 0, KnobR, Art.Brush(t.Ink), Art.Brush(t.Gold), 2));
        return s;
    }
}
