using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using DeskArcade.Engine;
using static DeskArcade.Games.BlockfallRules;

namespace DeskArcade.Games;

/// <summary>
/// Blockfall (see <see cref="BlockfallRules"/>), falling blocks played with the mouse alone, since the overlay
/// never takes the keyboard. The well stands on a window top when one is wide enough (and rides along when
/// that window moves), otherwise on the taskbar. While the pointer is over the well the falling piece follows
/// its column; click to turn the piece, hold the button to drop it faster, right-click to drop it at once.
/// Click the well to start. Over the LAN it is a score race.
/// </summary>
public sealed class BlockfallGame : MiniGame
{
    const double Pad = 8, FollowStep = 0.035, SoftStep = 0.04, HoldForSoft = 0.18, PanelCells = 5;

    static readonly Color[] PieceColors =
    {
        Color.FromRgb(54, 197, 240), Color.FromRgb(247, 208, 56), Color.FromRgb(165, 94, 234), Color.FromRgb(69, 196, 106),
        Color.FromRgb(235, 77, 75), Color.FromRgb(56, 103, 214), Color.FromRgb(250, 130, 49),
    };
    static readonly Color Gold = Color.FromRgb(255, 209, 102);

    readonly Canvas _root = new() { IsHitTestVisible = false };
    readonly Rectangle _frame = new() { RadiusX = 10, RadiusY = 10, Fill = Art.Brush(215, 16, 18, 26), Stroke = Art.Brush(90, 255, 255, 255), StrokeThickness = 1.5 };
    readonly Rectangle[,] _cells = new Rectangle[Height, Width];
    readonly Rectangle[] _piece = new Rectangle[4], _ghost = new Rectangle[4], _next = new Rectangle[4];
    readonly TextBlock _info = new() { FontFamily = Fx.Font, FontSize = 13, FontWeight = FontWeight.Bold, Foreground = Brushes.White, LineHeight = 20 };
    readonly TextBlock _prompt = new() { FontFamily = Fx.Font, FontSize = 15, FontWeight = FontWeight.Bold, Foreground = Art.Brush(Gold), TextAlignment = TextAlignment.Center };

    BlockfallRules? _rules;
    bool _running, _holding;
    double _s = 22, _fallT, _followT, _holdT;
    int _seenGen = -1, _demoTurn = -1, _demoCol;
    Vec2 _origin; // the well's top-left
    Vec2? _summoned;
    IntPtr _hwnd;

    public BlockfallGame(IGameHost host) : base(host)
    {
        _root.Children.Add(_frame);
        for (int r = 0; r < Height; r++)
            for (int c = 0; c < Width; c++)
                _root.Children.Add(_cells[r, c] = Cell(false));
        for (int i = 0; i < 4; i++)
        {
            _root.Children.Add(_ghost[i] = Cell(true));
            _root.Children.Add(_piece[i] = Cell(false));
            _root.Children.Add(_next[i] = Cell(false));
        }
        _root.Children.Add(_info);
        _root.Children.Add(_prompt);
        Layer.Children.Add(_root);
    }

    static Rectangle Cell(bool ghost) => new()
    {
        RadiusX = 3, RadiusY = 3, IsVisible = false, IsHitTestVisible = false,
        StrokeThickness = ghost ? 1.5 : 1, Stroke = ghost ? Art.Brush(140, 255, 255, 255) : Art.Brush(90, 0, 0, 0),
    };

    public override string Id => "blockfall";
    public override string Title => "Blockfall";

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        foreach (var (c, r, k) in new[] { (-1, 0, 2), (0, 0, 2), (1, 0, 2), (0, -1, 2), (-1, 1, 0), (0, 1, 3), (1, 1, 4) })
            s.Rotor.Children.Add(Art.At(new Rectangle { Width = 6.4, Height = 6.4, RadiusX = 1.2, RadiusY = 1.2, Fill = Art.Brush(PieceColors[k]) }, c * 7 - 3.2, r * 7 - 3.2));
        return s;
    }

    public override HudInfo Hud
    {
        get
        {
            var g = _rules;
            string line = !_running
                ? g is { Over: true } ? L.F("Game over · {0} lines · click the well to play again", g.Lines) : L.T("Click the well to start · click turns, right-click drops")
                : L.F("Level {0} · lines {1}", g!.Level, g.Lines);
            return new HudInfo((g?.Score ?? 0).ToString(CultureInfo.InvariantCulture), line, L.F("Best {0}", Host.Stats.Get("blockfall.best")));
        }
    }

    // ------------------------------------------------------------------ layout

    double WellW => Width * _s;
    double WellH => Height * _s;
    Rect Box => new(_origin.X - Pad, _origin.Y - Pad, WellW + PanelCells * _s + Pad * 3, WellH + Pad * 2);

    public override void Layout()
    {
        var a = Host.Arena;
        _s = Math.Clamp(Math.Floor(a.Height * 0.62 / Height), 14, 28);
        Place();
        Draw();
        Host.HudChanged();
    }

    /// <summary>On a window top wide and low enough for the well, else on the taskbar (or where it was summoned).</summary>
    void Place()
    {
        var a = Host.Arena;
        double w = WellW + PanelCells * _s + Pad * 3, h = WellH + Pad * 2;
        var hud = Host.HudBounds.Inflate(10);
        _hwnd = default;
        _seenGen = Host.Platforms.Generation;
        if (_summoned is { } p)
        {
            _origin = Fit(new Vec2(p.X - w / 2, p.Y - h / 2), w, h) + new Vec2(Pad, Pad);
            return;
        }
        var top = Host.Platforms.Items
            .Where(t => t.X2 - t.X1 >= w + 20 && t.Y - h >= a.Top + 10)
            .Select(t => (t, box: new Rect(Math.Min(t.X2 - w - 10, t.X1 + 10), t.Y - h, w, h)))
            .FirstOrDefault(t => !t.box.Intersects(hud));
        if (top.box.Width > 0)
        {
            _hwnd = top.t.Hwnd;
            _origin = new Vec2(top.box.X + Pad, top.box.Y + Pad);
            return;
        }
        var floor = Fit(new Vec2(a.Right - w - a.Width * 0.08, a.Bottom - h), w, h);
        if (new Rect(floor.X, floor.Y, w, h).Intersects(hud)) floor = Fit(new Vec2(a.Left + a.Width * 0.08, a.Bottom - h), w, h);
        _origin = floor + new Vec2(Pad, Pad);
    }

    Vec2 Fit(Vec2 at, double w, double h)
    {
        var a = Host.Arena;
        return new Vec2(Clamp(at.X, a.Left + 4, Math.Max(a.Left + 4, a.Right - w - 4)), Clamp(at.Y, a.Top + 4, Math.Max(a.Top + 4, a.Bottom - h)));
    }

    /// <summary>Rides along with the window the well stands on; drops to the taskbar when that window goes.</summary>
    void FollowWindow()
    {
        var plats = Host.Platforms;
        if (plats.Generation == _seenGen || _hwnd == IntPtr.Zero) return;
        _seenGen = plats.Generation;
        var d = plats.DeltaOf(_hwnd);
        _origin += d;
        bool stands = plats.Items.Any(p => p.Hwnd == _hwnd && Math.Abs(p.Y - (_origin.Y + WellH + Pad)) < 3 &&
            _origin.X - Pad >= p.X1 - 2 && _origin.X + WellW + PanelCells * _s + Pad * 2 <= p.X2 + 2);
        if (!stands) Place();
        Draw();
    }

    public override void Summon(Vec2 p)
    {
        _summoned = p;
        Place();
        Draw();
    }

    // ------------------------------------------------------------------ play

    public override bool SupportsLan => true;
    public override (int Score, bool Active)? Race => (_rules?.Score ?? 0, _running);

    public override void StartRace()
    {
        if (!_running) NewGame();
    }

    void NewGame()
    {
        _rules = new BlockfallRules(Rng);
        _running = true;
        _fallT = 0;
        _demoTurn = -1;
        Host.RoundStarted();
        Host.Sound.Play("whoosh", 0.35, 1.2);
        Draw();
        Host.HudChanged();
    }

    void GameOver()
    {
        var g = _rules!;
        _running = false;
        _holding = false;
        long before = Host.Stats.Get("blockfall.best");
        Host.Stats.Max("blockfall.best", g.Score);
        Host.Stats.Add("blockfall.games");
        Host.RoundEnded(g.Score);
        var at = new Vec2(_origin.X + WellW / 2, _origin.Y + WellH * 0.4);
        bool best = g.Score > before;
        Host.Fx.Popup(at, best ? L.T("NEW BEST!") : L.T("GAME OVER"), best ? Gold : Colors.White, 34, 2.6, L.F("{0} points · {1} lines", g.Score, g.Lines));
        if (best) Host.Fx.Burst(at, Themes.Current.Confetti, 40, 520, 700, 7, 1.0);
        Host.Sound.Play(best ? "best" : "buzzer", best ? 0.8 : 0.4);
        Draw();
        Host.HudChanged();
    }

    /// <summary>After a lock: the rows it cleared, and the end if the next piece has no room.</summary>
    void Locked(int cleared, int levelBefore)
    {
        var g = _rules!;
        if (cleared > 0)
        {
            Host.Stats.Add("blockfall.lines", cleared);
            if (cleared == 4) Host.Stats.Add("blockfall.fours");
            var at = new Vec2(_origin.X + WellW / 2, _origin.Y + WellH * 0.55);
            string text = cleared switch { 4 => L.T("FOUR LINES!"), 3 => L.T("Three lines"), 2 => L.T("Two lines"), _ => "" };
            if (text.Length > 0) Host.Fx.Popup(at, text, cleared == 4 ? Gold : Colors.White, cleared == 4 ? 30 : 22, 1.2);
            Host.Fx.Burst(at, PieceColors, 10 * cleared, 380, 500, 5, 0.6);
            Host.Sound.Play(cleared == 4 ? "fire" : "score", 0.5, 1 + cleared * 0.05);
            if (g.Level > levelBefore) Host.Fx.Popup(at - new Vec2(0, 50), L.F("Level {0}", g.Level), Gold, 24, 1.4);
        }
        else Host.Sound.Play("thunk", 0.25, 1.3);
        if (g.Over) GameOver();
        Host.HudChanged();
    }

    public override void CollectHitShapes(List<HitShape> into) => into.Add(HitShape.Box(Box));

    public override bool PointerDown(Vec2 p, bool right)
    {
        if (!_running)
        {
            if (!right) NewGame();
            return false;
        }
        var g = _rules!;
        if (right)
        {
            int level = g.Level;
            Locked(g.HardDrop(), level);
            _fallT = 0;
            Draw();
            return false;
        }
        if (g.Rotate()) Host.Sound.Play("board", 0.2, 1.8);
        _holding = true;
        _holdT = 0;
        Draw();
        return true; // held: soft drop until the button comes up
    }

    public override void PointerUp(Vec2 p) => _holding = false;

    public override void PointerCancel() => _holding = false;

    public override void Deactivate() => _holding = false;

    public override bool Update(double dt)
    {
        FollowWindow();
        if (!_running || _rules is not { } g) return false;
        dt = Math.Min(dt, 0.1);
        bool moved = false;

        // the piece follows the pointer's column while the pointer is over the well
        var ptr = Host.Pointer;
        if ((_followT += dt) >= FollowStep && (_holding || Box.Contains(ptr.ToPoint())))
        {
            _followT = 0;
            var cells = g.PieceCells().ToList();
            double mid = (cells.Min(c => c.C) + cells.Max(c => c.C) + 1) / 2.0;
            int want = (int)Math.Floor((ptr.X - _origin.X) / _s);
            int dx = Math.Clamp(want - (int)Math.Floor(mid), -1, 1);
            if (dx != 0 && Math.Abs(want + 0.5 - mid) >= 1 && g.Move(dx)) moved = true;
        }

        _holdT += dt;
        bool soft = _holding && _holdT >= HoldForSoft;
        _fallT += dt;
        if (_fallT >= (soft ? SoftStep : g.Gravity))
        {
            _fallT = 0;
            int level = g.Level, version = g.Version;
            int cleared = g.Fall(soft);
            if (g.Version != version) Locked(cleared, level);
            moved = true;
        }
        if (moved) Draw();
        return true;
    }

    // ------------------------------------------------------------------ demo

    /// <summary>Finds the placement with the flattest, hole-free stack and plays it out: turn, slide, drop.</summary>
    public override void DemoTick()
    {
        if (!_running)
        {
            NewGame();
            return;
        }
        var g = _rules!;
        if (_demoTurn < 0 || g.Version != _drawnDemoVersion || g.Kind != _demoKind) Plan(g);
        if (g.Turn != _demoTurn)
        {
            g.Rotate();
        }
        else if (g.Col != _demoCol)
        {
            if (!g.Move(Math.Sign(_demoCol - g.Col))) _demoCol = g.Col; // blocked on the way: drop where it is
        }
        else
        {
            int level = g.Level;
            Locked(g.HardDrop(), level);
            _demoTurn = -1;
        }
        Draw();
    }

    int _drawnDemoVersion = -1, _demoKind = -1;

    void Plan(BlockfallRules g)
    {
        _drawnDemoVersion = g.Version;
        _demoKind = g.Kind;
        double best = double.MaxValue;
        for (int turn = 0; turn < 4; turn++)
            for (int col = -2; col < Width; col++)
            {
                var shape = CellsOf(g.Kind, turn, col, 0).ToList();
                if (shape.Any(p => p.C < 0 || p.C >= Width)) continue;
                int row = 0;
                bool Fits(int r) => shape.All(p => p.R + r < Height && (p.R + r < 0 || g.Cells[p.R + r, p.C] < 0));
                if (!Fits(0)) continue;
                while (Fits(row + 1)) row++;
                double score = Evaluate(g, shape.Select(p => (p.C, p.R + row)).ToList());
                if (score < best)
                {
                    best = score;
                    _demoTurn = turn;
                    _demoCol = col;
                }
            }
        if (_demoTurn < 0)
        {
            _demoTurn = g.Turn;
            _demoCol = g.Col;
        }
    }

    static double Evaluate(BlockfallRules g, List<(int C, int R)> placed)
    {
        var grid = new bool[Height, Width];
        for (int r = 0; r < Height; r++)
            for (int c = 0; c < Width; c++)
                grid[r, c] = g.Cells[r, c] >= 0;
        foreach (var (c, r) in placed)
            if (r >= 0) grid[r, c] = true;
        int lines = 0;
        for (int r = 0; r < Height; r++)
        {
            bool full = true;
            for (int c = 0; c < Width && full; c++) full = grid[r, c];
            if (full) lines++;
        }
        int holes = 0, aggregate = 0, bumps = 0, prev = -1;
        for (int c = 0; c < Width; c++)
        {
            int h = 0;
            bool roof = false;
            for (int r = 0; r < Height; r++)
            {
                if (grid[r, c])
                {
                    if (!roof) h = Height - r;
                    roof = true;
                }
                else if (roof) holes++;
            }
            aggregate += h;
            if (prev >= 0) bumps += Math.Abs(h - prev);
            prev = h;
        }
        return aggregate * 0.51 + holes * 3.6 + bumps * 0.18 - lines * 0.76;
    }

    // ------------------------------------------------------------------ drawing

    void Draw()
    {
        var box = Box;
        _frame.Width = box.Width;
        _frame.Height = box.Height;
        Canvas.SetLeft(_frame, box.X);
        Canvas.SetTop(_frame, box.Y);
        var g = _rules;
        for (int r = 0; r < Height; r++) // all of them, since the well itself may have moved
            for (int c = 0; c < Width; c++)
            {
                int k = g?.Cells[r, c] ?? -1;
                Put(_cells[r, c], c, r, k, k >= 0);
            }
        var piece = g != null && !g.Over && _running ? g.PieceCells().ToList() : new List<(int C, int R)>();
        int drop = g != null && _running ? g.DropRow() - g.Row : 0;
        for (int i = 0; i < 4; i++)
        {
            bool show = i < piece.Count && piece[i].R >= 0;
            if (show) Put(_piece[i], piece[i].C, piece[i].R, g!.Kind, true);
            else _piece[i].IsVisible = false;
            bool ghost = i < piece.Count && drop > 0 && piece[i].R + drop >= 0;
            if (ghost)
            {
                Put(_ghost[i], piece[i].C, piece[i].R + drop, -1, true);
                _ghost[i].Fill = Art.Brush(30, 255, 255, 255);
            }
            else _ghost[i].IsVisible = false;
        }
        // the next piece, in the panel beside the well
        double px = _origin.X + WellW + Pad, py = _origin.Y + _s * 1.2;
        var next = g != null ? CellsOf(g.Next, 0, 0, 0).ToList() : new List<(int C, int R)>();
        for (int i = 0; i < 4; i++)
        {
            if (i >= next.Count)
            {
                _next[i].IsVisible = false;
                continue;
            }
            var n = _next[i];
            n.Width = n.Height = _s * 0.8 - 1;
            n.Fill = Art.Brush(PieceColors[g!.Next]);
            n.IsVisible = true;
            Canvas.SetLeft(n, px + _s * 0.4 + next[i].C * _s * 0.8);
            Canvas.SetTop(n, py + next[i].R * _s * 0.8);
        }
        _info.Text = g == null ? L.T("Next") : string.Join("\n", L.T("Next"), "", "", "", L.F("Lines {0}", g.Lines), L.F("Level {0}", g.Level));
        _info.FontSize = Math.Max(11, _s * 0.55);
        Canvas.SetLeft(_info, px + _s * 0.3);
        Canvas.SetTop(_info, _origin.Y);
        _prompt.IsVisible = !_running;
        _prompt.Text = g is { Over: true } ? L.T("Click to play again") : L.T("Click to start");
        _prompt.Width = WellW;
        Canvas.SetLeft(_prompt, _origin.X);
        Canvas.SetTop(_prompt, _origin.Y + WellH * 0.45);
    }

    void Put(Rectangle cell, int c, int r, int kind, bool show)
    {
        cell.IsVisible = show;
        if (!show) return;
        cell.Width = cell.Height = _s - 1;
        if (kind >= 0) cell.Fill = Art.Brush(PieceColors[kind]);
        Canvas.SetLeft(cell, _origin.X + c * _s + 0.5);
        Canvas.SetTop(cell, _origin.Y + r * _s + 0.5);
    }
}
