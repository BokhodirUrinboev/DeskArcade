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
/// Minesweeper on a board over the desktop (see <see cref="MinesweeperRules"/>): click a cell to open it, right-click to
/// flag a mine, click an open number whose flags are all placed to open the rest around it. The chips above the board
/// pick the size (Beginner, Intermediate, Expert) and the board: a random one, whose first click is always safe, or the
/// daily puzzle, the same board for everyone that day, with a safe first cell marked. Your time is the score, fewer
/// seconds winning: a board is a race against the computer, or against a co-worker over the LAN, where both clear the
/// same board (Intermediate). The grip moves the board.
/// </summary>
public sealed class MinesweeperGame : MiniGame
{
    const double Cell = 26, Gap = 2, HeaderH = 34, Pad = 10;
    static readonly string[] SizeNames = { "Beginner", "Intermediate", "Expert" };
    static readonly Color[] NumberColors =
    {
        Color.FromRgb(90, 160, 255), Color.FromRgb(80, 200, 110), Color.FromRgb(255, 96, 96), Color.FromRgb(180, 140, 255),
        Color.FromRgb(255, 160, 70), Color.FromRgb(60, 210, 210), Color.FromRgb(230, 230, 230), Color.FromRgb(160, 160, 170),
    };

    sealed class CellView
    {
        public required Rectangle Tile;
        public required TextBlock Text;
        public required Canvas Mark;
        public MineCell Drawn = (MineCell)(-1);
        public bool DrawnOver;
    }

    readonly Canvas _board = new();
    readonly ScaleTransform _zoom = new(1, 1);
    readonly Rectangle _back = new() { RadiusX = 10, RadiusY = 10, StrokeThickness = 1.5, IsHitTestVisible = false };
    readonly Canvas _cells = new() { IsHitTestVisible = false };
    readonly Ellipse _startRing = new() { StrokeThickness = 2.5, IsHitTestVisible = false, IsVisible = false };
    readonly TextBlock _sizeChip = Chip(), _modeChip = Chip(), _counter = Label(), _clock = Label();
    readonly Rectangle _sizeBack = ChipBack(), _modeBack = ChipBack(), _newBack = ChipBack();
    readonly TextBlock _newChip = Chip();
    readonly DragHandle _handle;
    CellView[] _views = Array.Empty<CellView>();
    MinesweeperRules _rules = null!;
    int _size = 1, _lanRound, _session = -1;
    bool _daily, _racing, _placed, _seeded;
    Vec2 _origin;
    double _scale = 1, _elapsed, _pulse;
    Rect _sizeRect, _modeRect, _newRect;

    public MinesweeperGame(IGameHost host) : base(host)
    {
        _size = Math.Clamp(host.Settings.Levels.TryGetValue("mines.size", out int s) ? s : 1, 0, 2);
        _board.RenderTransformOrigin = RelativePoint.TopLeft;
        _board.RenderTransform = _zoom;
        foreach (var c in new Control[] { _back, _sizeBack, _sizeChip, _modeBack, _modeChip, _newBack, _newChip, _counter, _clock, _cells, _startRing }) _board.Children.Add(c);
        Layer.Children.Add(_board);
        _handle = new DragHandle(host, Id, Title);
        Layer.Children.Add(_handle.Visual);
        NewBoard();
        ThemeChanged();
    }

    public override string Id => "mines";
    public override string Title => "Minesweeper";

    bool LanOn => Host.Lan.Connected;
    (int Cols, int Rows, int Mines) Dim => MinesweeperRules.Sizes[LanOn ? 1 : _size];
    double BoardW => Pad * 2 + Dim.Cols * (Cell + Gap) - Gap;
    double BoardH => HeaderH + Pad + Dim.Rows * (Cell + Gap) - Gap;
    int Seconds => (int)Math.Floor(_elapsed);

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        s.Rotor.Children.Add(Art.Circle(0, 1, 6.5, Art.Brush("#20232C")));
        s.Rotor.Children.Add(Art.PathOf("M0,-9 L0,11 M-10,1 L10,1 M-7,-6 L7,8 M7,-6 L-7,8", null, Art.Brush("#20232C"), 1.6));
        s.Rotor.Children.Add(Art.Circle(-2, -1, 1.8, Brushes.White));
        return s;
    }

    public override HudInfo Hud
    {
        get
        {
            long best = Host.Stats.Get(BestKey);
            string line = _rules.Won ? L.F("Cleared in {0}s · click the face for a new board", Seconds)
                : _rules.Lost ? L.T("Boom! · click the face for a new board")
                : _rules.Started ? L.F("Mines left {0} · right-click flags", _rules.Mines - _rules.Flags)
                : _seeded ? L.T("Start on the marked cell · right-click flags a mine")
                : L.T("Click a cell to start · right-click flags a mine");
            return new HudInfo(L.F("{0}s", Seconds), line, best > 0 ? L.F("Best {0}s", best) : L.T("Best —"));
        }
    }

    string BestKey => "mines.best" + (LanOn ? 1 : _size);

    // ------------------------------------------------------------------ races

    public override bool SupportsLan => true;
    public override (int Score, bool Active)? Race => (_rules.Over ? RoundScore : Seconds, _racing);
    public override bool RaceLowerIsBetter => true;
    public override int RaceBaseline => new[] { 60, 180, 420 }[LanOn ? 1 : _size];
    public override int RaceMin => 3;
    public override int RaceBest => (int)Host.Stats.Get(BestKey);
    public override double RaceSeconds => RaceBaseline;

    /// <summary>A cleared board's time; a lost one counts as the worst.</summary>
    int RoundScore => _rules.Won ? Math.Max(1, Seconds) : 999;

    public override void StartRace()
    {
        if (_racing) return;
        // a fresh seeded board is already the one the co-worker started (both count the race's boards alike); else the next one
        if (_rules.Started || _rules.Over || !_seeded) NewBoard();
        BeginRound();
    }

    void BeginRound()
    {
        if (_racing) return;
        _racing = true;
        Host.RoundStarted();
    }

    // ------------------------------------------------------------------ boards

    void NewBoard()
    {
        CheckSession();
        var (cols, rows, mines) = Dim;
        _seeded = LanOn || _daily;
        var rng = LanOn ? new Random(MinesweeperRules.DailySeed(DateTime.Today, "mines-lan", ++_lanRound))
            : _daily ? new Random(MinesweeperRules.DailySeed(DateTime.Today, "mines" + _size))
            : Rng;
        _rules = new MinesweeperRules(cols, rows, mines, rng, _seeded);
        _elapsed = 0;
        _racing = false;
        BuildCells();
        if (_placed) Place();
        Host.HudChanged();
    }

    /// <summary>True when the pairing changed: both screens then count the race's boards from the start.</summary>
    bool CheckSession()
    {
        int session = LanOn ? Host.Lan.Session : -1;
        if (session == _session) return false;
        _session = session;
        _lanRound = 0;
        return true;
    }

    void BuildCells()
    {
        _cells.Children.Clear();
        var (cols, rows, _) = Dim;
        _views = new CellView[cols * rows];
        for (int i = 0; i < _views.Length; i++)
        {
            var tile = new Rectangle { Width = Cell, Height = Cell, RadiusX = 4, RadiusY = 4, IsHitTestVisible = false };
            var text = new TextBlock { FontFamily = Fx.Font, FontSize = 16, FontWeight = FontWeight.Black, IsHitTestVisible = false, Width = Cell, TextAlignment = TextAlignment.Center };
            var mark = new Canvas { IsHitTestVisible = false };
            double x = Pad + i % cols * (Cell + Gap), y = HeaderH + i / cols * (Cell + Gap);
            Art.At(tile, x, y);
            Art.At(text, x, y + 2);
            Art.At(mark, x + Cell / 2, y + Cell / 2);
            _cells.Children.Add(tile);
            _cells.Children.Add(text);
            _cells.Children.Add(mark);
            _views[i] = new CellView { Tile = tile, Text = text, Mark = mark };
        }
        _back.Width = BoardW;
        _back.Height = BoardH;
        DrawCells();
        PaintHeader();
    }

    // ------------------------------------------------------------------ layout

    public override void Layout()
    {
        var a = Host.Arena;
        _scale = Clamp(Math.Min(a.Width * 0.5 / BoardW, a.Height * 0.6 / BoardH), 0.6, 1.5);
        _scale = Math.Min(_scale, Math.Min((a.Width - 20) / BoardW, (a.Height - 40) / BoardH));
        if (!_placed)
        {
            _placed = true;
            _origin = _handle.Saved() ?? new Vec2(a.Center.X - BoardW * _scale / 2, a.Center.Y - BoardH * _scale / 2);
        }
        if (CheckSession() || (LanOn && _rules.Cols != Dim.Cols))
        {
            NewBoard(); // a new pairing races Intermediate boards, the same on both screens
        }
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
            if (LanOn && _seeded && !_rules.Started) return false; // already a fresh board of the race: skipping one would put the two screens out of step
            NewBoard();
            Host.Sound.Play("board", 0.3, 1.3);
            return false;
        }
        if (!_rules.Started && !LanOn && _sizeRect.Contains(local))
        {
            _size = (_size + 1) % MinesweeperRules.Sizes.Length;
            Host.Settings.Levels["mines.size"] = _size;
            Host.SaveSettings();
            _placed = true;
            NewBoard();
            Layout();
            return false;
        }
        if (!_rules.Started && !LanOn && _modeRect.Contains(local))
        {
            _daily = !_daily;
            NewBoard();
            return false;
        }
        int col = (int)Math.Floor((local.X - Pad) / (Cell + Gap)), row = (int)Math.Floor((local.Y - HeaderH) / (Cell + Gap));
        if (col < 0 || col >= _rules.Cols || row < 0 || row >= _rules.Rows) return false;
        Act(_rules.Index(col, row), right);
        return false;
    }

    public override void PointerUp(Vec2 p) => _handle.End(_origin);
    public override void PointerCancel() => _handle.Cancel();

    void Act(int i, bool flag)
    {
        if (_rules.Over) return;
        if (flag)
        {
            if (_rules.ToggleFlag(i)) Host.Sound.Play("board", 0.25, 1.9);
            DrawCells();
            Host.HudChanged();
            return;
        }
        int before = _rules.Opened;
        var result = _rules.Open(i);
        if (result == MineResult.Nothing) return;
        if (!_racing) BeginRound();
        int opened = _rules.Opened - before;
        if (opened > 0) Host.Stats.Add("mines.cleared", opened);
        switch (result)
        {
            case MineResult.Boom:
                Boom();
                break;
            case MineResult.Won:
                Win();
                break;
            default:
                Host.Sound.Play(opened > 8 ? "swish" : "board", opened > 8 ? 0.35 : 0.25, 1.5);
                break;
        }
        DrawCells();
        Host.HudChanged();
    }

    void Boom()
    {
        var at = CellCenter(_rules.Exploded);
        Host.Fx.Burst(at, new[] { Color.FromRgb(255, 90, 60), Color.FromRgb(255, 200, 60), Colors.White }, 30, 420, 500, 5, 0.8);
        Host.Sound.Play("thunk", 0.6, 0.6);
        Host.Sound.Play("buzzer", 0.35);
        Host.Fx.Popup(at - new Vec2(0, 40), L.T("BOOM!"), Colors.White, 40, 2.2, L.T("click the face for a new board"));
        EndRound();
    }

    void Win()
    {
        long before = Host.Stats.Get(BestKey);
        int secs = Math.Max(1, Seconds);
        Host.Stats.Add("mines.wins");
        Host.Stats.Min(BestKey, secs);
        if (!LanOn && _size == 2) Host.Stats.Add("mines.expert");
        if (!LanOn && _size == 0 && secs < 20) Host.Stats.Add("mines.fast");
        if (_daily && !LanOn) Host.Stats.Add("mines.daily");
        var at = new Vec2(_origin.X + BoardW * _scale / 2, _origin.Y + BoardH * _scale * 0.4);
        bool best = before == 0 || secs < before;
        Host.Fx.Popup(at, best ? L.T("NEW BEST!") : L.T("CLEARED!"), Themes.Themed(Themes.ClassicGold), 42, 2.6,
            _daily && !LanOn ? L.F("today's puzzle in {0}s", secs) : L.F("{0}s", secs));
        Host.Fx.Burst(at, Themes.Current.Confetti, 44, 540, 700, 7, 1.1);
        Host.Sound.Play("best", 0.8);
        EndRound();
    }

    void EndRound()
    {
        if (!_racing) return;
        _racing = false;
        Host.RoundEnded(RoundScore);
    }

    Vec2 CellCenter(int i) => _origin + new Vec2((Pad + i % _rules.Cols * (Cell + Gap) + Cell / 2) * _scale, (HeaderH + i / _rules.Cols * (Cell + Gap) + Cell / 2) * _scale);

    public override bool Update(double dt)
    {
        if (_handle.Dragging)
        {
            _origin = _handle.Move(Host.Pointer, new Size(BoardW * _scale, BoardH * _scale));
            Place();
        }
        bool ticking = _rules.Started && !_rules.Over;
        if (ticking)
        {
            int before = Seconds;
            _elapsed += dt;
            if (Seconds != before)
            {
                PaintHeader();
                Host.HudChanged();
            }
        }
        bool pulsing = _seeded && !_rules.Started;
        if (pulsing)
        {
            _pulse += dt;
            _startRing.Opacity = 0.55 + 0.45 * Math.Sin(_pulse * 4);
        }
        return Anims.Update(dt) || _handle.Dragging || ticking || pulsing;
    }

    // ------------------------------------------------------------------ demo

    /// <summary>A small solver: flags what must be mines, chords what must be safe, and guesses when it has to.</summary>
    public override void DemoTick()
    {
        if (_rules.Over)
        {
            NewBoard();
            return;
        }
        if (!_rules.Started)
        {
            Act(_rules.Start >= 0 ? _rules.Start : Rng.Next(_rules.Count), false);
            return;
        }
        for (int i = 0; i < _rules.Count; i++)
        {
            if (_rules.StateOf(i) != MineCell.Open || _rules.CountOf(i) == 0) continue;
            var around = _rules.Neighbours(i).ToList();
            int hidden = around.Count(n => _rules.StateOf(n) == MineCell.Hidden), flags = around.Count(n => _rules.StateOf(n) == MineCell.Flagged);
            if (hidden == 0) continue;
            if (hidden + flags == _rules.CountOf(i))
            {
                Act(around.First(n => _rules.StateOf(n) == MineCell.Hidden), true);
                return;
            }
            if (flags == _rules.CountOf(i))
            {
                Act(i, false);
                return;
            }
        }
        var guesses = Enumerable.Range(0, _rules.Count).Where(k => _rules.StateOf(k) == MineCell.Hidden).ToList();
        if (guesses.Count > 0) Act(guesses[Rng.Next(guesses.Count)], false);
    }

    // ------------------------------------------------------------------ drawing

    static TextBlock Chip() => new() { FontFamily = Fx.Font, FontSize = 12, FontWeight = FontWeight.Bold, IsHitTestVisible = false };
    static TextBlock Label() => new() { FontFamily = Fx.Font, FontSize = 14, FontWeight = FontWeight.Black, IsHitTestVisible = false };
    static Rectangle ChipBack() => new() { RadiusX = 10, RadiusY = 10, Height = 22, StrokeThickness = 1, IsHitTestVisible = false };

    void PaintHeader()
    {
        _sizeChip.Text = LanOn ? L.T(SizeNames[1]) : L.T(SizeNames[_size]);
        _modeChip.Text = LanOn ? L.T("LAN race") : _daily ? L.T("Daily puzzle") : L.T("Random board");
        _newChip.Text = _rules.Lost ? "×_×" : _rules.Won ? "^‿^" : "•‿•";
        _counter.Text = (_rules.Mines - _rules.Flags).ToString(CultureInfo.InvariantCulture);
        _clock.Text = L.F("{0}s", Seconds);
        double x = Pad;
        _sizeRect = PlaceChip(_sizeBack, _sizeChip, ref x);
        _modeRect = PlaceChip(_modeBack, _modeChip, ref x);
        _newChip.Measure(Size.Infinity);
        double nw = _newChip.DesiredSize.Width + 18;
        _newRect = new Rect(BoardW / 2 - nw / 2, 6, nw, 22);
        if (_newRect.Left < x + 6) _newRect = new Rect(x + 6, 6, nw, 22);
        _newBack.Width = nw;
        Art.At(_newBack, _newRect.X, _newRect.Y);
        Art.At(_newChip, _newRect.X + 9, _newRect.Y + 3);
        _clock.Measure(Size.Infinity);
        Art.At(_clock, BoardW - Pad - _clock.DesiredSize.Width, 7);
        _counter.Measure(Size.Infinity);
        Art.At(_counter, BoardW - Pad - _clock.DesiredSize.Width - 14 - _counter.DesiredSize.Width, 7);
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

    void DrawCells()
    {
        var t = Themes.Current;
        var hidden = Art.Brush(Art.Blend(t.Accent, t.Ink, 0.55));
        var open = Art.Brush(Art.Blend(t.Ink, Colors.White, 0.08));
        for (int i = 0; i < _views.Length; i++)
        {
            var v = _views[i];
            var state = _rules.StateOf(i);
            bool over = _rules.Over;
            if (v.Drawn == state && v.DrawnOver == over) continue;
            v.Drawn = state;
            v.DrawnOver = over;
            v.Mark.Children.Clear();
            v.Text.Text = "";
            bool mine = _rules.IsMine(i);
            switch (state)
            {
                case MineCell.Open:
                    v.Tile.Fill = i == _rules.Exploded ? Art.Brush(Art.Safe(Color.FromRgb(220, 60, 60))) : open;
                    if (mine) DrawMine(v.Mark);
                    else if (_rules.CountOf(i) > 0)
                    {
                        v.Text.Text = _rules.CountOf(i).ToString(CultureInfo.InvariantCulture);
                        v.Text.Foreground = Art.Brush(Art.Safe(NumberColors[_rules.CountOf(i) - 1]));
                    }
                    break;
                case MineCell.Flagged:
                    v.Tile.Fill = hidden;
                    DrawFlag(v.Mark, _rules.Lost && !mine);
                    break;
                default:
                    v.Tile.Fill = hidden;
                    if (_rules.Lost && mine) DrawMine(v.Mark); // show where they were
                    break;
            }
        }
        _startRing.IsVisible = _seeded && !_rules.Started && _rules.Start >= 0;
        if (_startRing.IsVisible)
        {
            int s = _rules.Start;
            _startRing.Width = _startRing.Height = Cell - 4;
            Art.At(_startRing, Pad + s % _rules.Cols * (Cell + Gap) + 2, HeaderH + s / _rules.Cols * (Cell + Gap) + 2);
            _startRing.Stroke = Art.Brush(t.Gold);
        }
        PaintHeader();
    }

    static void DrawMine(Canvas into)
    {
        into.Children.Add(Art.PathOf("M0,-9 L0,9 M-9,0 L9,0 M-6,-6 L6,6 M6,-6 L-6,6", null, Art.Brush("#15171D"), 1.6));
        into.Children.Add(Art.Circle(0, 0, 6, Art.Brush("#15171D")));
        into.Children.Add(Art.Circle(-2, -2, 1.6, Brushes.White));
    }

    /// <summary>A red flag on a pole; crossed out when the game shows it was on a safe cell.</summary>
    static void DrawFlag(Canvas into, bool wrong)
    {
        into.Children.Add(Art.PathOf("M-2,-8 L-2,8 M-6,8 L4,8", null, Art.Brush("#E6E6E6"), 1.5));
        into.Children.Add(Art.PathOf("M-2,-8 L7,-4 L-2,0 Z", Art.Brush(Art.Safe(Color.FromRgb(230, 60, 60)))));
        if (wrong) into.Children.Add(Art.PathOf("M-8,-8 L8,8 M8,-8 L-8,8", null, Art.Brush("#FFD166"), 2));
    }

    public override void ThemeChanged()
    {
        var t = Themes.Current;
        _back.Fill = Art.Brush(Color.FromArgb(238, t.Ink.R, t.Ink.G, t.Ink.B));
        _back.Stroke = Art.Brush(Color.FromArgb(150, t.Accent.R, t.Accent.G, t.Accent.B));
        foreach (var back in new[] { _sizeBack, _modeBack, _newBack })
        {
            back.Fill = Art.Brush(Color.FromArgb(70, t.Accent.R, t.Accent.G, t.Accent.B));
            back.Stroke = Art.Brush(t.Accent);
        }
        foreach (var text in new[] { _sizeChip, _modeChip, _newChip }) text.Foreground = Art.Brush(t.HudFront);
        _counter.Foreground = Art.Brush(Art.Safe(Color.FromRgb(255, 96, 96)));
        _clock.Foreground = Art.Brush(t.Gold);
        foreach (var v in _views) v.Drawn = (MineCell)(-1);
        DrawCells();
    }
}
