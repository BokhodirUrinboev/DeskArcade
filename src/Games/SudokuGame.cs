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
/// Sudoku on a board over the desktop, played with the mouse (see <see cref="SudokuRules"/>): click a cell, then a digit
/// on the pad under the grid; the same digit again clears it, right-click clears a cell, and with Notes on the pad writes
/// small pencil marks instead. A digit that repeats in its row, column or box shows red; the cell's row, column, box and
/// every cell with the same digit light up. The chips pick the level (Easy, Medium, Hard) and the puzzle: a random one
/// or the daily puzzle, the same for everyone that day. Your time is the score, fewer seconds winning: a puzzle is a
/// race against the computer, or against a co-worker over the LAN, where both solve the same puzzle (Medium).
/// </summary>
public sealed class SudokuGame : MiniGame
{
    const double CellS = 40, HeaderH = 34, Pad = 10, KeyS = 36, KeyGap = 2;
    const double GridS = CellS * 9, BoardW = GridS + Pad * 2, PadTop = HeaderH + GridS + 10, BoardH = PadTop + KeyS * 2 + 6 + Pad;
    static readonly string[] LevelNames2 = { "Easy", "Medium", "Hard" };

    sealed class CellView
    {
        public required Rectangle Back;
        public required TextBlock Digit;
        public required TextBlock Notes;
    }

    readonly Canvas _board = new();
    readonly ScaleTransform _zoom = new(1, 1);
    readonly Rectangle _back = new() { RadiusX = 10, RadiusY = 10, StrokeThickness = 1.5, IsHitTestVisible = false };
    readonly Canvas _grid = new() { IsHitTestVisible = false };
    readonly Canvas _lines = new() { IsHitTestVisible = false };
    readonly Canvas _keys = new() { IsHitTestVisible = false };
    readonly CellView[] _cells = new CellView[81];
    readonly (Rectangle Back, TextBlock Text)[] _pad = new (Rectangle, TextBlock)[11]; // 1..9, notes, erase
    readonly TextBlock _levelChip = Chip(), _modeChip = Chip(), _newChip = Chip(), _clock = new() { FontFamily = Fx.Font, FontSize = 14, FontWeight = FontWeight.Black, IsHitTestVisible = false };
    readonly Rectangle _levelBack = ChipBack(), _modeBack = ChipBack(), _newBack = ChipBack();
    readonly DragHandle _handle;
    SudokuRules _rules = null!;
    int _level, _selected = -1, _lanRound, _session = -1;
    bool _notes, _daily, _racing, _placed, _seeded, _started;
    double _elapsed, _scale = 1, _demoT;
    Vec2 _origin;
    Rect _levelRect, _modeRect, _newRect;

    public SudokuGame(IGameHost host) : base(host)
    {
        _level = Math.Clamp(host.Settings.Levels.TryGetValue("sudoku.level", out int l) ? l : 0, 0, 2);
        _board.RenderTransformOrigin = RelativePoint.TopLeft;
        _board.RenderTransform = _zoom;
        foreach (var c in new Control[] { _back, _grid, _lines, _keys, _levelBack, _levelChip, _modeBack, _modeChip, _newBack, _newChip, _clock }) _board.Children.Add(c);
        for (int i = 0; i < 81; i++)
        {
            var back = new Rectangle { Width = CellS, Height = CellS, IsHitTestVisible = false };
            var digit = new TextBlock { FontFamily = Fx.Font, FontSize = 22, Width = CellS, TextAlignment = TextAlignment.Center, IsHitTestVisible = false };
            var notes = new TextBlock { FontFamily = Fx.Font, FontSize = 9.5, Width = CellS - 4, TextAlignment = TextAlignment.Center, LineHeight = 11.5, IsHitTestVisible = false };
            double x = Pad + SudokuRules.Col(i) * CellS, y = HeaderH + SudokuRules.Row(i) * CellS;
            Art.At(back, x, y);
            Art.At(digit, x, y + 6);
            Art.At(notes, x + 2, y + 2);
            _grid.Children.Add(back);
            _grid.Children.Add(digit);
            _grid.Children.Add(notes);
            _cells[i] = new CellView { Back = back, Digit = digit, Notes = notes };
        }
        for (int k = 0; k < _pad.Length; k++)
        {
            var back = new Rectangle { Height = KeyS, RadiusX = 6, RadiusY = 6, StrokeThickness = 1, IsHitTestVisible = false };
            var text = new TextBlock { FontFamily = Fx.Font, FontSize = k < 9 ? 20 : 13, FontWeight = FontWeight.Bold, TextAlignment = TextAlignment.Center, IsHitTestVisible = false };
            var r = KeyRect(k);
            back.Width = r.Width;
            text.Width = r.Width;
            Art.At(back, r.X, r.Y);
            Art.At(text, r.X, r.Y + (k < 9 ? 5 : 9));
            _keys.Children.Add(back);
            _keys.Children.Add(text);
            _pad[k] = (back, text);
        }
        _handle = new DragHandle(host, Id, Title);
        Layer.Children.Add(_board);
        Layer.Children.Add(_handle.Visual);
        NewPuzzle();
        ThemeChanged();
    }

    public override string Id => "sudoku";
    public override string Title => "Sudoku";

    bool LanOn => Host.Lan.Connected;
    SudokuLevel Level => LanOn ? SudokuLevel.Medium : (SudokuLevel)_level;
    int Seconds => (int)Math.Floor(_elapsed);
    string BestKey => "sudoku.best" + (int)Level;

    static string Clock(long s) => string.Create(CultureInfo.InvariantCulture, $"{s / 60}:{s % 60:00}");

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        s.Rotor.Children.Add(Art.At(new Rectangle { Width = 18, Height = 18, Stroke = Art.Brush(Themes.Current.HudFront), StrokeThickness = 1.5, RadiusX = 2, RadiusY = 2 }, -9, -9));
        s.Rotor.Children.Add(Art.PathOf("M-3,-9 L-3,9 M3,-9 L3,9 M-9,-3 L9,-3 M-9,3 L9,3", null, Art.Brush(Themes.Current.HudFront), 0.8));
        var nine = new TextBlock { Text = "9", FontFamily = Fx.Font, FontSize = 8, FontWeight = FontWeight.Black, Foreground = Art.Brush(Themes.Current.Gold) };
        s.Rotor.Children.Add(Art.At(nine, -8, -9));
        return s;
    }

    public override HudInfo Hud
    {
        get
        {
            long best = Host.Stats.Get(BestKey);
            string line = _rules.Solved ? L.F("Solved in {0} · New for another puzzle", Clock(Seconds))
                : _selected < 0 ? L.T("Click a cell, then a digit on the pad · right-click clears")
                : _notes ? L.T("Notes on: the pad writes pencil marks")
                : L.F("Filled {0}/81 · the same digit again clears the cell", _rules.Filled);
            return new HudInfo(Clock(Seconds), line, best > 0 ? L.F("Best {0}", Clock(best)) : L.T("Best —"));
        }
    }

    /// <summary>A solved puzzle to share: its time, and the date when it was the daily puzzle everyone had.</summary>
    public override string? ShareText => !_rules.Solved ? null
        : _daily && !LanOn ? L.F("Sudoku daily · {0} ({1}): solved in {2} 🧩", DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), L.T(Level.ToString()), Clock(Math.Max(1, Seconds)))
        : L.F("Sudoku ({0}): solved in {1} 🧩", L.T(Level.ToString()), Clock(Math.Max(1, Seconds)));

    // ------------------------------------------------------------------ races

    public override bool SupportsLan => true;
    public override (int Score, bool Active)? Race => (_rules.Solved ? Math.Max(1, Seconds) : Seconds, _racing);
    public override bool RaceLowerIsBetter => true;
    public override int RaceBaseline => new[] { 300, 480, 720 }[(int)Level];
    public override int RaceMin => 30;
    public override int RaceBest => (int)Host.Stats.Get(BestKey);
    public override double RaceSeconds => RaceBaseline;

    public override void StartRace()
    {
        if (_racing) return;
        if (_started || !_seeded) NewPuzzle(); // a fresh seeded puzzle is already the one the co-worker started
        BeginRound();
    }

    void BeginRound()
    {
        _started = true;
        if (_racing) return;
        _racing = true;
        Host.RoundStarted();
    }

    // ------------------------------------------------------------------ puzzles

    void NewPuzzle()
    {
        CheckSession();
        _seeded = LanOn || _daily;
        var rng = LanOn ? new Random(MinesweeperRules.DailySeed(DateTime.Today, "sudoku-lan", ++_lanRound))
            : _daily ? new Random(MinesweeperRules.DailySeed(DateTime.Today, "sudoku" + _level))
            : Rng;
        _rules = new SudokuRules(rng, Level);
        _elapsed = 0;
        _selected = -1;
        _started = _racing = false;
        Draw();
        Host.HudChanged();
    }

    bool CheckSession()
    {
        int session = LanOn ? Host.Lan.Session : -1;
        if (session == _session) return false;
        _session = session;
        _lanRound = 0;
        return true;
    }

    // ------------------------------------------------------------------ layout

    public override void Layout()
    {
        var a = Host.Arena;
        _scale = Clamp(Math.Min(a.Width * 0.34 / BoardW, a.Height * 0.72 / BoardH), 0.6, 1.5);
        _scale = Math.Min(_scale, Math.Min((a.Width - 20) / BoardW, (a.Height - 40) / BoardH));
        if (!_placed)
        {
            _placed = true;
            _origin = _handle.Saved() ?? new Vec2(a.Center.X - BoardW * _scale / 2, a.Center.Y - BoardH * _scale / 2);
        }
        if (CheckSession() || (LanOn && _rules.Level != Level)) NewPuzzle();
        Place();
        Host.HudChanged();
    }

    void Place()
    {
        var a = Host.Arena;
        double w = BoardW * _scale, h = BoardH * _scale;
        _origin = new Vec2(Clamp(_origin.X, a.Left + 8, Math.Max(a.Left + 8, a.Right - w - 8)),
            Clamp(_origin.Y, a.Top + DragHandle.Height + 12, Math.Max(a.Top + DragHandle.Height + 12, a.Bottom - h - 8)));
        _zoom.ScaleX = _zoom.ScaleY = _scale;
        Canvas.SetLeft(_board, _origin.X);
        Canvas.SetTop(_board, _origin.Y);
        _handle.Show(new Rect(_origin.X, _origin.Y, w, h));
    }

    public override void PositionsReset() => _placed = false;

    public override void Summon(Vec2 p)
    {
        _origin = p - new Vec2(BoardW * _scale / 2, 20);
        Place();
        _handle.Save(_origin);
    }

    public override void Deactivate()
    {
        _handle.Cancel();
        Anims.Finish();
    }

    // ------------------------------------------------------------------ input

    static Rect KeyRect(int k)
    {
        double w = (GridS - KeyGap * 8) / 9;
        if (k < 9) return new Rect(Pad + k * (w + KeyGap), PadTop, w, KeyS);
        double half = (GridS - KeyGap) / 2;
        return new Rect(Pad + (k - 9) * (half + KeyGap), PadTop + KeyS + 6, half, KeyS);
    }

    public override void CollectHitShapes(List<HitShape> into)
    {
        into.Add(HitShape.Box(new Rect(_origin.X, _origin.Y, BoardW * _scale, BoardH * _scale)));
        into.Add(_handle.Hit);
    }

    public override bool PointerDown(Vec2 p, bool right)
    {
        if (_handle.Contains(p))
        {
            _handle.Begin(p, _origin);
            return true;
        }
        var local = new Point((p.X - _origin.X) / _scale, (p.Y - _origin.Y) / _scale);
        if (_newRect.Contains(local))
        {
            if (LanOn && _seeded && !_started) return false; // already a fresh puzzle of the race
            NewPuzzle();
            Host.Sound.Play("board", 0.3, 1.3);
            return false;
        }
        if (!_started && !LanOn && _levelRect.Contains(local))
        {
            _level = (_level + 1) % 3;
            Host.Settings.Levels["sudoku.level"] = _level;
            Host.SaveSettings();
            NewPuzzle();
            return false;
        }
        if (!_started && !LanOn && _modeRect.Contains(local))
        {
            _daily = !_daily;
            NewPuzzle();
            return false;
        }
        for (int k = 0; k < _pad.Length; k++)
            if (KeyRect(k).Contains(local))
            {
                Key(k);
                return false;
            }
        int col = (int)Math.Floor((local.X - Pad) / CellS), row = (int)Math.Floor((local.Y - HeaderH) / CellS);
        if (col is < 0 or > 8 || row is < 0 or > 8) return false;
        int i = row * 9 + col;
        _selected = i;
        if (right && _rules.Place(i, 0)) Host.Sound.Play("board", 0.2, 1.1);
        else Host.Sound.Play("key", 0.25, 1.1);
        Draw();
        Host.HudChanged();
        return false;
    }

    public override void PointerUp(Vec2 p) => _handle.End(_origin);
    public override void PointerCancel() => _handle.Cancel();

    /// <summary>A pad key: a digit (0–8 for 1–9), Notes (9) or Erase (10).</summary>
    void Key(int k)
    {
        if (k == 9)
        {
            _notes = !_notes;
            Host.Sound.Play("key", 0.3, 1.3);
            Draw();
            Host.HudChanged();
            return;
        }
        if (_selected < 0 || _rules.Solved) return;
        int digit = k == 10 ? 0 : k + 1;
        bool changed = _notes && digit > 0 ? _rules.ToggleNote(_selected, digit)
            : _rules.Place(_selected, _rules.ValueAt(_selected) == digit ? 0 : digit);
        if (!changed) return;
        if (!_racing) BeginRound();
        if (!_notes && digit > 0 && _rules.ValueAt(_selected) == _rules.SolutionAt(_selected)) Host.Stats.Add("sudoku.digits");
        Host.Sound.Play(_rules.Conflicts(_selected) ? "key-bad" : "key", 0.35, 0.95 + Rng.NextDouble() * 0.1);
        if (_rules.Solved) Win();
        Draw();
        Host.HudChanged();
    }

    void Win()
    {
        long before = Host.Stats.Get(BestKey);
        int secs = Math.Max(1, Seconds);
        Host.Stats.Add("sudoku.wins");
        Host.Stats.Min(BestKey, secs);
        if (Level == SudokuLevel.Hard) Host.Stats.Add("sudoku.hard");
        if (_daily && !LanOn) Host.Stats.Add("sudoku.daily");
        var at = new Vec2(_origin.X + BoardW * _scale / 2, _origin.Y + BoardH * _scale * 0.35);
        bool best = before == 0 || secs < before;
        Host.Fx.Popup(at, best ? L.T("NEW BEST!") : L.T("SOLVED!"), Themes.Themed(Themes.ClassicGold), 42, 2.6,
            _daily && !LanOn ? L.F("today's puzzle in {0}", Clock(secs)) : Clock(secs));
        Host.Fx.Burst(at, Themes.Current.Confetti, 44, 540, 700, 7, 1.1);
        Host.Sound.Play("best", 0.8);
        _selected = -1;
        if (_racing)
        {
            _racing = false;
            Host.RoundEnded(secs);
        }
    }

    public override bool Update(double dt)
    {
        if (_handle.Dragging)
        {
            _origin = _handle.Move(Host.Pointer, new Size(BoardW * _scale, BoardH * _scale));
            Place();
        }
        bool ticking = _started && !_rules.Solved;
        if (ticking)
        {
            int before = Seconds;
            _elapsed += dt;
            if (Seconds != before)
            {
                _clock.Text = Clock(Seconds);
                Host.HudChanged();
            }
        }
        return Anims.Update(dt) || _handle.Dragging || ticking;
    }

    // ------------------------------------------------------------------ demo

    /// <summary>Fills in the solution a cell at a time, now and then a wrong digit taken back, like someone thinking it through.</summary>
    public override void DemoTick()
    {
        if ((_demoT += 0.15) < 0.6) return;
        _demoT = 0;
        if (_rules.Solved)
        {
            NewPuzzle();
            return;
        }
        var empty = Enumerable.Range(0, 81).Where(i => _rules.ValueAt(i) == 0 || _rules.ValueAt(i) != _rules.SolutionAt(i)).ToList();
        if (empty.Count == 0) return;
        int cell = empty[Rng.Next(empty.Count)];
        _selected = cell;
        Key(_rules.ValueAt(cell) != 0 ? 10 : Rng.NextDouble() < 0.1 ? Rng.Next(9) : _rules.SolutionAt(cell) - 1);
    }

    // ------------------------------------------------------------------ drawing

    static TextBlock Chip() => new() { FontFamily = Fx.Font, FontSize = 12, FontWeight = FontWeight.Bold, IsHitTestVisible = false };
    static Rectangle ChipBack() => new() { RadiusX = 10, RadiusY = 10, Height = 22, StrokeThickness = 1, IsHitTestVisible = false };

    void Draw()
    {
        var t = Themes.Current;
        int sel = _selected, selValue = sel >= 0 ? _rules.ValueAt(sel) : 0;
        var plain = Art.Blend(t.Ink, Colors.White, 0.06);
        var peer = Art.Blend(t.Accent, t.Ink, 0.82);
        var same = Art.Blend(t.Accent, t.Ink, 0.6);
        var chosen = Art.Blend(t.Accent, t.Ink, 0.35);
        var mine = Art.Blend(Art.Safe(t.Mine), Colors.White, 0.35);
        for (int i = 0; i < 81; i++)
        {
            var c = _cells[i];
            int v = _rules.ValueAt(i);
            bool isPeer = sel >= 0 && (SudokuRules.Row(i) == SudokuRules.Row(sel) || SudokuRules.Col(i) == SudokuRules.Col(sel) || SudokuRules.Box(i) == SudokuRules.Box(sel));
            c.Back.Fill = Art.Brush(i == sel ? chosen : v != 0 && v == selValue ? same : isPeer ? peer : plain);
            c.Digit.Text = v == 0 ? "" : v.ToString(CultureInfo.InvariantCulture);
            c.Digit.FontWeight = _rules.IsGiven(i) ? FontWeight.Black : FontWeight.SemiBold;
            c.Digit.Foreground = Art.Brush(_rules.Conflicts(i) ? Art.Safe(Color.FromRgb(255, 90, 90)) : _rules.IsGiven(i) ? t.HudFront : mine);
            int notes = _rules.NotesAt(i);
            c.Notes.Text = v != 0 || notes == 0 ? "" : string.Join("\n", Enumerable.Range(0, 3).Select(row =>
                string.Join(" ", Enumerable.Range(1, 3).Select(k => (notes & 1 << (row * 3 + k)) != 0 ? (row * 3 + k).ToString(CultureInfo.InvariantCulture) : " "))));
            c.Notes.Foreground = Art.Brush(Art.Blend(t.HudFront, t.Ink, 0.3));
        }
        DrawLines(t);
        DrawPad(t);
        DrawHeader(t);
    }

    void DrawLines(Theme t)
    {
        _lines.Children.Clear();
        var thin = Art.Brush(Color.FromArgb(70, t.HudFront.R, t.HudFront.G, t.HudFront.B));
        var thick = Art.Brush(Color.FromArgb(200, t.Accent.R, t.Accent.G, t.Accent.B));
        for (int k = 0; k <= 9; k++)
        {
            double at = k * CellS;
            bool box = k % 3 == 0;
            _lines.Children.Add(Art.PathOf($"M{Art.F(Pad + at)},{Art.F(HeaderH)} L{Art.F(Pad + at)},{Art.F(HeaderH + GridS)}", null, box ? thick : thin, box ? 2 : 1));
            _lines.Children.Add(Art.PathOf($"M{Art.F(Pad)},{Art.F(HeaderH + at)} L{Art.F(Pad + GridS)},{Art.F(HeaderH + at)}", null, box ? thick : thin, box ? 2 : 1));
        }
    }

    void DrawPad(Theme t)
    {
        for (int k = 0; k < _pad.Length; k++)
        {
            var (back, text) = _pad[k];
            bool done = k < 9 && Enumerable.Range(0, 81).Count(i => _rules.ValueAt(i) == k + 1) >= 9;
            bool on = k == 9 && _notes;
            back.Fill = Art.Brush(on ? Art.Blend(t.Accent, t.Ink, 0.4) : Color.FromArgb(done ? (byte)25 : (byte)60, t.Accent.R, t.Accent.G, t.Accent.B));
            back.Stroke = Art.Brush(Color.FromArgb(done ? (byte)60 : (byte)180, t.Accent.R, t.Accent.G, t.Accent.B));
            text.Text = k < 9 ? (k + 1).ToString(CultureInfo.InvariantCulture) : k == 9 ? (_notes ? L.T("Notes: on") : L.T("Notes: off")) : L.T("Erase");
            text.Foreground = Art.Brush(done ? Art.Blend(t.HudFront, t.Ink, 0.6) : t.HudFront);
        }
    }

    void DrawHeader(Theme t)
    {
        _levelChip.Text = L.T(LevelNames2[(int)Level]);
        _modeChip.Text = LanOn ? L.T("LAN race") : _daily ? L.T("Daily puzzle") : L.T("Random board");
        _newChip.Text = L.T("New");
        double x = Pad;
        _levelRect = PlaceChip(_levelBack, _levelChip, ref x);
        _modeRect = PlaceChip(_modeBack, _modeChip, ref x);
        _newRect = PlaceChip(_newBack, _newChip, ref x);
        _clock.Text = Clock(Seconds);
        _clock.Foreground = Art.Brush(t.Gold);
        _clock.Measure(Size.Infinity);
        Art.At(_clock, BoardW - Pad - _clock.DesiredSize.Width, 7);
    }

    static Rect PlaceChip(Rectangle back, TextBlock text, ref double x)
    {
        text.Measure(Size.Infinity);
        double w = text.DesiredSize.Width + 18;
        var r = new Rect(x, 6, w, 22);
        back.Width = w;
        Art.At(back, r.X, r.Y);
        Art.At(text, r.X + 9, r.Y + 3);
        x += w + 6;
        return r;
    }

    public override void ThemeChanged()
    {
        var t = Themes.Current;
        _back.Fill = Art.Brush(Color.FromArgb(238, t.Ink.R, t.Ink.G, t.Ink.B));
        _back.Stroke = Art.Brush(Color.FromArgb(150, t.Accent.R, t.Accent.G, t.Accent.B));
        _back.Width = BoardW;
        _back.Height = BoardH;
        foreach (var back in new[] { _levelBack, _modeBack, _newBack })
        {
            back.Fill = Art.Brush(Color.FromArgb(70, t.Accent.R, t.Accent.G, t.Accent.B));
            back.Stroke = Art.Brush(t.Accent);
        }
        foreach (var text in new[] { _levelChip, _modeChip, _newChip }) text.Foreground = Art.Brush(t.HudFront);
        Draw();
    }
}
