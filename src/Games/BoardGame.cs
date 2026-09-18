using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using DeskArcade.Engine;
using DeskArcade.Net;

namespace DeskArcade.Games;

/// <summary>
/// The rules of a turn-based grid game, free of UI. Squares are numbered row-major from the top-left.
/// Side +1 sits at the bottom and moves first.
/// </summary>
public interface IBoardRules
{
    /// <summary>0 empty; the sign is the side, the size is the piece kind.</summary>
    sbyte[] Board { get; }
    int Turn { get; }
    int Ply { get; }
    /// <summary>0 while playing, ±1 for the winning side, 2 for a draw.</summary>
    int Result { get; }
    /// <summary>A square to mark in red (a king in check), or −1.</summary>
    int Alert { get; }
    /// <summary>Each legal move as a path of squares: [from, to], [from, landing, landing...], or [square] to place a piece.</summary>
    List<int[]> LegalMoves();
    /// <summary>Plays a legal move; returns the squares of captured pieces.</summary>
    List<int> Apply(int[] move);
    int[] BestMove(Random rng);
    int Count(int side);
    /// <summary>Text form for the network. May contain '|'.</summary>
    string Encode();
}

/// <summary>
/// A board floating over the desktop, played against the computer or a co-worker over the LAN. Click a
/// piece, then the square it should land on (or, in placement games, just the square); right-drag moves
/// the board. Over the LAN the host keeps the
/// real board and sends it every <see cref="SyncEvery"/> seconds; the guest re-sends its move until the
/// host's board includes it, so a lost packet never puts the boards out of step. The guest sees the board
/// turned around, with its own side (−1) at the bottom.
/// </summary>
public abstract class BoardGame : MiniGame
{
    const double CpuDelay = 0.6, SyncEvery = 0.4;

    protected static readonly Color Gold = Color.FromRgb(255, 209, 102);

    readonly Canvas _marks = new() { IsHitTestVisible = false };
    readonly Canvas _pieces = new() { IsHitTestVisible = false };
    readonly Border _frame = new() { CornerRadius = new CornerRadius(6), Background = Art.Brush(230, 60, 40, 28), IsHitTestVisible = false };
    readonly Rectangle[] _cells;

    IBoardRules _game;
    Vec2 _origin, _dragOffset;
    double _cpuIn = -1, _syncT;
    int _selected = -1, _gameNo, _lastFrom = -1, _lastTo = -1, _myWins, _theirWins;
    readonly List<int> _via = new(); // landings clicked so far, when several capture routes start alike
    int[]? _pending; // guest: our move, re-sent until the host's board contains it
    bool _placed, _dragging, _over, _demo, _wasLan;

    protected BoardGame(IGameHost host, Color light, Color dark) : base(host)
    {
        _game = NewRules();
        Layer.Children.Add(_frame);
        var squares = new Canvas { IsHitTestVisible = false };
        _cells = new Rectangle[Cols * Rows];
        for (int i = 0; i < _cells.Length; i++)
        {
            _cells[i] = new Rectangle { Fill = Art.Brush((i / Cols + i % Cols) % 2 == 1 ? dark : light) };
            squares.Children.Add(_cells[i]);
        }
        Layer.Children.Add(squares);
        Layer.Children.Add(_marks);
        Layer.Children.Add(_pieces);
    }

    public override bool SupportsLan => true;

    protected abstract IBoardRules NewRules();
    protected abstract IBoardRules? Decode(string text);
    /// <summary>Adds the visuals for one piece centered on <paramref name="c"/>.</summary>
    protected abstract void DrawPiece(Canvas into, sbyte piece, Vec2 c, double cell);
    /// <summary>Why the game ended in a draw, for the popup.</summary>
    protected abstract string DrawReason(IBoardRules game);
    /// <summary>Adds the visuals for an empty square (e.g. a hole in a Connect Four frame).</summary>
    protected virtual void DrawEmpty(Canvas into, Vec2 c, double cell) { }
    /// <summary>The move a click on <paramref name="sq"/> makes in a placement game, if any.</summary>
    protected virtual int[]? PlacementAt(int sq, List<int[]> moves) => moves.FirstOrDefault(m => m.Length == 1 && m[0] == sq);

    protected virtual int Cols => 8;
    protected virtual int Rows => 8;
    /// <summary>Largest board edge in DIPs.</summary>
    protected virtual double MaxSize => 520;
    /// <summary>Whether the guest sees the board turned around (not for games with gravity).</summary>
    protected virtual bool FlipForGuest => true;
    /// <summary>Scoreboard text: pieces left by default; placement games show games won this session.</summary>
    protected virtual string Score => $"{_game.Count(Me)}–{_game.Count(-Me)}";
    protected string SessionScore => $"{_myWins}–{_theirWins}";
    protected virtual string YourMove => L.T("Your move · click a piece, then where it goes · right-drag moves the board");

    protected double Cell { get; private set; }
    protected IBoardRules Game => _game;
    bool LanOn => Host.Lan.Connected;
    bool IsGuest => LanOn && Host.Lan.Role == LanRole.Guest;
    protected int Me => IsGuest ? -1 : 1;
    bool MyTurn => !_over && _game.Turn == Me;
    string Rival => LanOn ? Host.Lan.PeerName : L.T("CPU");

    public override HudInfo Hud => new(
        Score,
        _over ? L.T("Game over · click the board for a rematch")
            : MyTurn ? YourMove
            : L.F("{0} is thinking…", Rival),
        L.F("Wins {0}", Host.Stats.Get(Id + ".wins")));

    // ------------------------------------------------------------------ layout

    public override void Layout()
    {
        var a = Host.Arena;
        Cell = Math.Floor(Math.Min(Math.Min(a.Height * 0.7 / Rows, a.Width * 0.5 / Cols), MaxSize / Math.Max(Cols, Rows)));
        double w = Cell * Cols, h = Cell * Rows;
        if (!_placed)
        {
            _placed = true;
            _origin = new Vec2(a.Center.X - w / 2, a.Center.Y - h / 2);
        }
        _origin = new Vec2(Clamp(_origin.X, a.Left + 10, a.Right - w - 10), Clamp(_origin.Y, a.Top + 10, a.Bottom - h - 10));
        if (LanOn != _wasLan)
        {
            _wasLan = LanOn;
            _myWins = _theirWins = 0;
            NewGame();
        }
        Draw();
    }

    public override void Deactivate() => _dragging = false;

    void NewGame()
    {
        _game = NewRules();
        _gameNo++;
        _over = false;
        _selected = _lastFrom = _lastTo = -1;
        _via.Clear();
        _pending = null;
        _cpuIn = -1;
        AfterMove();
    }

    Rect BoardRect => new(_origin.X, _origin.Y, Cell * Cols, Cell * Rows);

    int View(int sq) => IsGuest && FlipForGuest ? Cols * Rows - 1 - sq : sq;

    protected Vec2 Center(int sq)
    {
        int v = View(sq);
        return new Vec2(_origin.X + (v % Cols + 0.5) * Cell, _origin.Y + (v / Cols + 0.5) * Cell);
    }

    int SquareAt(Vec2 p)
    {
        int c = (int)Math.Floor((p.X - _origin.X) / Cell), r = (int)Math.Floor((p.Y - _origin.Y) / Cell);
        return r >= 0 && r < Rows && c >= 0 && c < Cols ? View(r * Cols + c) : -1;
    }

    // ------------------------------------------------------------------ input

    public override void CollectHitShapes(List<HitShape> into) => into.Add(HitShape.Box(BoardRect.Inflate(8)));

    public override bool PointerDown(Vec2 p, bool right)
    {
        if (right)
        {
            _dragging = true;
            _dragOffset = _origin - p;
            return true;
        }
        if (_over)
        {
            if (IsGuest) Host.Lan.Send("r|");
            else NewGame();
            return false;
        }
        int sq = SquareAt(p);
        if (sq < 0 || !MyTurn || _pending != null) return false;
        var moves = _game.LegalMoves();
        if (PlacementAt(sq, moves) is int[] place)
            Play(place);
        else if (_selected >= 0 && ClickLanding(moves, sq)) { }
        else if (moves.Any(m => m[0] == sq))
        {
            _selected = sq;
            _via.Clear();
            Host.Sound.Play("board", 0.25, 1.6);
        }
        else Deselect();
        Draw();
        return false;
    }

    void Deselect()
    {
        _selected = -1;
        _via.Clear();
    }

    /// <summary>The moves of the selected piece that pass through the landings clicked so far.</summary>
    List<int[]> Routes(List<int[]> moves) =>
        moves.Where(m => m[0] == _selected && m.Length > _via.Count && m.Skip(1).Take(_via.Count).SequenceEqual(_via)).ToList();

    /// <summary>
    /// A click on a square while a piece is selected. Clicking where a move ends plays it; when several
    /// capture routes end there (flying kings in shashki), the player clicks the landings one by one and
    /// each click narrows the choice. Returns false if the square isn't on any route.
    /// </summary>
    bool ClickLanding(List<int[]> moves, int sq)
    {
        var routes = Routes(moves);
        if (_via.Count > 0 && sq == _via[^1] && routes.FirstOrDefault(m => m.Length == _via.Count + 1) is int[] stop)
        {
            Play(stop); // clicked the last landing again: the route that ends there
            return true;
        }
        var onward = routes.Where(m => m.Length > _via.Count + 1).ToList();
        var ending = onward.Where(m => m[^1] == sq).ToList();
        var through = onward.Where(m => m[_via.Count + 1] == sq).ToList();
        if (ending.Count == 1 && through.All(m => m == ending[0]))
        {
            Play(ending[0]);
            return true;
        }
        if (through.Count > 0)
        {
            _via.Add(sq);
            var left = Routes(moves);
            if (left.Count == 1 && left[0].Length == _via.Count + 1) Play(left[0]);
            return true;
        }
        if (ending.Count > 1)
        {
            Host.Fx.Popup(Center(sq) - new Vec2(0, Cell), L.T("several ways lead there · click each landing on the way"), Colors.White, 18, 1.8);
            return true;
        }
        return false;
    }

    public override void PointerUp(Vec2 p) => _dragging = false;

    public override void Summon(Vec2 p)
    {
        _origin = p - new Vec2(Cell * 4, Cell * 4);
        Layout();
    }

    // ------------------------------------------------------------------ play

    void Play(int[] move)
    {
        int ply = _game.Ply;
        PlayAny(move);
        if (IsGuest)
        {
            _pending = move;
            SendMove(ply);
        }
    }

    void PlayAny(int[] move)
    {
        bool mine = _game.Turn == Me;
        var captured = _game.Apply(move);
        Deselect();
        _lastFrom = move[0];
        _lastTo = move[^1];
        MoveFx(captured.Count, mine);
        AfterMove();
    }

    void MoveFx(int captures, bool byMe)
    {
        if (captures > 0)
        {
            Host.Sound.Play("score", 0.5 + 0.1 * captures);
            if (byMe) Host.Stats.Add(Id + ".captures", captures);
            if (captures >= 2)
                Host.Fx.Popup(Center(_lastTo) + new Vec2(0, -Cell), captures >= 3 ? L.T("TRIPLE JUMP!") : L.T("DOUBLE JUMP!"), Gold, 26, 1.2);
        }
        else Host.Sound.Play("board", 0.45, 1.2);
    }

    void AfterMove()
    {
        if (!_over && _game.Result != 0) GameOver(_game.Result);
        else if (!_over && _game.Alert >= 0) Host.Sound.Play("rim", 0.5, 0.8);
        if (!_over && !LanOn && _game.Turn != Me) _cpuIn = CpuDelay;
        if (!IsGuest) SendState();
        Draw();
        Host.HudChanged();
        Host.Wake();
    }

    void GameOver(int result)
    {
        _over = true;
        var b = BoardRect;
        var at = new Vec2(b.Center.X, b.Top + b.Height * 0.3);
        if (result == 2)
        {
            Host.Stats.Add(Id + ".draws");
            Host.Fx.Popup(at, L.T("DRAW"), Colors.White, 38, 2.4, DrawReason(_game) + " · " + L.T("click the board for a rematch"));
            Host.Sound.Play("buzzer", 0.3);
        }
        else if (result == Me)
        {
            _myWins++;
            Host.Stats.Add(Id + ".wins");
            if (LanOn)
            {
                Host.Stats.Add(Id + ".lanwins");
                Host.Stats.Add("lan.wins");
            }
            Host.Fx.Popup(at, L.T("YOU WIN!"), Gold, 42, 2.6, L.F("vs {0}", Rival));
            Host.Fx.Burst(at, new[] { Gold, Colors.White, Color.FromRgb(214, 64, 69) }, 44, 540, 700, 7, 1.1);
            Host.Sound.Play("best", 0.8);
        }
        else
        {
            _theirWins++;
            Host.Fx.Popup(at, L.F("{0} WINS", Rival), Colors.White, 38, 2.4, L.T("click the board for a rematch"));
            Host.Sound.Play("buzzer", 0.45);
        }
    }

    public override bool Update(double dt)
    {
        if (_dragging)
        {
            _origin = Host.Pointer + _dragOffset;
            Layout();
        }
        if (LanOn) Network(dt);
        else if (_cpuIn > 0 && (_cpuIn -= dt) <= 0)
        {
            _cpuIn = -1;
            if (!_over && _game.Turn != Me) PlayAny(_game.BestMove(Rng));
        }
        if (_demo && MyTurn && _pending == null && _cpuIn < 0) Play(_game.BestMove(Rng));
        return _dragging || _cpuIn > 0 || LanOn;
    }

    public override void DemoTick() => _demo = true;

    // ------------------------------------------------------------------ LAN
    // host → guest  "st|gameNo|lastFrom|lastTo|board"  (IBoardRules.Encode, sent often)
    // guest → host  "mv|ply|a,b,c"                      (re-sent until the host's ply passes it)
    // guest → host  "r|"                                (rematch)

    void Network(double dt)
    {
        while (Host.Lan.TryReceive(out var msg))
        {
            if (IsGuest && msg.StartsWith("st|", StringComparison.Ordinal)) ReadState(msg.Split('|', 5));
            else if (!IsGuest && msg.StartsWith("mv|", StringComparison.Ordinal)) ReadMove(msg.Split('|', 3));
            else if (!IsGuest && msg.StartsWith("r|", StringComparison.Ordinal) && _over) NewGame();
        }
        if ((_syncT += dt) < SyncEvery) return;
        _syncT = 0;
        if (IsGuest)
        {
            if (_pending != null) SendMove(_game.Ply - 1);
        }
        else SendState();
    }

    void SendState() => Host.Lan.Send($"st|{_gameNo}|{_lastFrom}|{_lastTo}|{_game.Encode()}");

    void SendMove(int ply) => Host.Lan.Send($"mv|{ply}|{string.Join(",", _pending!)}");

    void ReadMove(string[] f)
    {
        if (f.Length != 3 || !int.TryParse(f[1], out int ply) || ply != _game.Ply || _game.Turn != -1) return; // stale or repeated
        var path = f[2].Split(',').Select(s => int.TryParse(s, out int v) ? v : -1).ToArray();
        if (_game.LegalMoves().Any(m => m.SequenceEqual(path))) PlayAny(path);
    }

    void ReadState(string[] f)
    {
        if (f.Length != 5 || !int.TryParse(f[1], out int gameNo) || Decode(f[4]) is not IBoardRules d) return;
        bool newGame = gameNo != _gameNo;
        if (!newGame && d.Ply < _game.Ply) return; // our own move hasn't reached the host yet
        _pending = null;
        if (!newGame && d.Ply == _game.Ply && d.Encode() == _game.Encode()) return;

        int before = _game.Count(Me);
        bool advanced = !newGame && d.Ply > _game.Ply;
        if (newGame)
        {
            _gameNo = gameNo;
            _over = false;
        }
        _game = d;
        Deselect();
        _lastFrom = int.TryParse(f[2], out int from) ? from : -1;
        _lastTo = int.TryParse(f[3], out int to) ? to : -1;
        if (advanced) MoveFx(before - d.Count(Me), false);
        AfterMove();
    }

    // ------------------------------------------------------------------ visuals

    void Draw()
    {
        var b = BoardRect;
        Canvas.SetLeft(_frame, b.Left - 8);
        Canvas.SetTop(_frame, b.Top - 8);
        _frame.Width = b.Width + 16;
        _frame.Height = b.Height + 16;
        for (int sq = 0; sq < _cells.Length; sq++)
        {
            var c = Center(sq);
            _cells[sq].Width = _cells[sq].Height = Cell;
            Canvas.SetLeft(_cells[sq], c.X - Cell / 2);
            Canvas.SetTop(_cells[sq], c.Y - Cell / 2);
        }

        _marks.Children.Clear();
        foreach (int sq in new[] { _lastFrom, _lastTo })
            if (sq >= 0) _marks.Children.Add(Box(sq, Art.Brush(70, 255, 209, 102), null));
        if (_game.Alert >= 0) _marks.Children.Add(Box(_game.Alert, Art.Brush(110, 255, 60, 60), null));
        if (MyTurn && _pending == null)
        {
            var moves = _game.LegalMoves();
            if (_selected >= 0)
            {
                _marks.Children.Add(Box(_selected, null, Art.Brush(Gold)));
                foreach (int sq in _via) _marks.Children.Add(Box(sq, null, Art.Brush(Gold)));
                foreach (var m in Routes(moves).Where(m => m.Length > _via.Count + 1))
                {
                    var c = Center(m[^1]);
                    _marks.Children.Add(Art.Circle(c.X, c.Y, Cell * 0.16, Art.Brush(200, 255, 209, 102)));
                    if (m.Length > _via.Count + 2) // a route with more landings: mark the next one too
                    {
                        var n = Center(m[_via.Count + 1]);
                        _marks.Children.Add(Art.Circle(n.X, n.Y, Cell * 0.1, null, Art.Brush(200, 255, 209, 102), 2));
                    }
                }
            }
            else
                foreach (int from in moves.Where(m => m.Length > 1).Select(m => m[0]).Distinct())
                    _marks.Children.Add(Box(from, null, Art.Brush(120, 255, 209, 102)));
        }

        _pieces.Children.Clear();
        for (int sq = 0; sq < _cells.Length; sq++)
            if (_game.Board[sq] != 0) DrawPiece(_pieces, _game.Board[sq], Center(sq), Cell);
            else DrawEmpty(_pieces, Center(sq), Cell);
    }

    Rectangle Box(int sq, IBrush? fill, IBrush? stroke)
    {
        var c = Center(sq);
        var box = new Rectangle { Width = Cell, Height = Cell, Fill = fill, Stroke = stroke, StrokeThickness = 3 };
        Canvas.SetLeft(box, c.X - Cell / 2);
        Canvas.SetTop(box, c.Y - Cell / 2);
        return box;
    }
}
