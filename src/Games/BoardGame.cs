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

/// <summary>Rules whose computer player can look further ahead or less far, for the CPU levels.</summary>
public interface ILeveledRules
{
    int[] BestMove(Random rng, int depth);
}

/// <summary>
/// What one move does to the pieces on screen, worked out from the board before and after it: which pieces slide
/// along which path, which vanish (captured), which one appears (placed) and what the mover turns into where it
/// lands (a promoted pawn, a crowned man).
/// </summary>
public sealed class BoardMovePlan
{
    /// <summary>Each sliding piece with its path: the square it starts on, then every landing. The mover comes first.</summary>
    public List<(int[] Path, sbyte Piece)> Slides { get; } = new();

    /// <summary>The captured pieces and where they stood.</summary>
    public List<(int Square, sbyte Piece)> Vanish { get; } = new();

    /// <summary>A piece placed on an empty square (Tic-tac-toe, Connect Four).</summary>
    public (int Square, sbyte Piece)? Appear { get; set; }

    /// <summary>What the mover has become where it landed, when it changed kind on the way.</summary>
    public (int Square, sbyte Piece)? Becomes { get; set; }
}

/// <summary>Plans the animation of a move from the board before and after it. UI-free, so it can be tested.</summary>
public static class BoardAnim
{
    /// <summary>
    /// The plan for <paramref name="move"/>, or null when the difference between the boards is not what that one
    /// move explains (then the board is simply drawn afresh). A castling rook slides along with the king; anything
    /// else that left the board was captured.
    /// </summary>
    public static BoardMovePlan? Plan(sbyte[] before, sbyte[] after, int[] move)
    {
        if (before.Length != after.Length || move.Length == 0 || move.Any(sq => sq < 0 || sq >= before.Length)) return null;
        var gone = new List<int>();    // squares that emptied
        var came = new List<int>();    // squares that filled
        var swapped = new List<int>(); // squares whose occupant changed to another piece
        for (int sq = 0; sq < before.Length; sq++)
        {
            if (before[sq] == after[sq]) continue;
            if (after[sq] == 0) gone.Add(sq);
            else if (before[sq] == 0) came.Add(sq);
            else swapped.Add(sq);
        }
        if (gone.Count + came.Count + swapped.Count == 0) return null;

        var plan = new BoardMovePlan();
        if (move.Length == 1)
        {
            int sq = move[0];
            if (came.Count != 1 || came[0] != sq || gone.Count != 0 || swapped.Count != 0) return null;
            plan.Appear = (sq, after[sq]);
            return plan;
        }

        int from = move[0], to = move[^1];
        sbyte mover = before[from];
        if (mover == 0 || !gone.Remove(from)) return null;
        if (after[to] == 0 || Math.Sign(after[to]) != Math.Sign(mover)) return null;
        if (!came.Remove(to) && !swapped.Remove(to)) return null;
        if (swapped.Count > 0) return null;
        if (before[to] != 0) plan.Vanish.Add((to, before[to]));
        plan.Slides.Add((move, mover));
        if (after[to] != mover) plan.Becomes = (to, after[to]);
        foreach (int sq in came)
        {
            int i = Math.Sign(after[sq]) == Math.Sign(mover) ? gone.FindIndex(g => before[g] == after[sq]) : -1;
            if (i < 0) return null;
            plan.Slides.Add((new[] { gone[i], sq }, after[sq]));
            gone.RemoveAt(i);
        }
        foreach (int sq in gone)
        {
            if (Math.Sign(before[sq]) == Math.Sign(mover)) return null; // a friendly piece can't just disappear
            plan.Vanish.Add((sq, before[sq]));
        }
        return plan;
    }

    /// <summary>
    /// For a board that arrived over the network: the plan when it is exactly one legal move on from the board we
    /// show, otherwise null (several plies at once, a rematch, anything odd: draw it at once).
    /// </summary>
    public static BoardMovePlan? Decide(sbyte[] before, sbyte[] after, int plyBefore, int plyAfter, int[]? path, List<int[]> legal)
    {
        if (plyAfter != plyBefore + 1 || path == null || !legal.Any(m => m.SequenceEqual(path))) return null;
        return Plan(before, after, path);
    }

    /// <summary>Which leg of <paramref name="path"/> (0-based) passes over or lands on <paramref name="sq"/>; the last leg when none does.</summary>
    public static int LegOf(int[] path, int sq, int cols)
    {
        int r = sq / cols, c = sq % cols;
        for (int i = 1; i < path.Length; i++)
        {
            int a = path[i - 1], b = path[i];
            if (sq == b) return i - 1;
            int ra = a / cols, ca = a % cols, rb = b / cols, cb = b % cols;
            int dr = Math.Sign(rb - ra), dc = Math.Sign(cb - ca), n = Math.Max(Math.Abs(rb - ra), Math.Abs(cb - ca));
            for (int t = 1; t < n; t++)
                if (ra + dr * t == r && ca + dc * t == c) return i - 1;
        }
        return Math.Max(0, path.Length - 2);
    }
}

/// <summary>
/// A board floating over the desktop, played against the computer or a co-worker over the LAN. Click a
/// piece, then the square it should land on (or, in placement games, just the square); the grip above the
/// board or a right-drag moves it. Moves slide, captures fade, a placed piece pops or drops in. Over the LAN
/// the host keeps the real board and sends it every <see cref="SyncEvery"/> seconds; the guest re-sends its
/// move until the host's board includes it, so a lost packet never puts the boards out of step. The guest sees
/// the board turned around, with its own side (−1) at the bottom.
/// </summary>
public abstract class BoardGame : MiniGame
{
    const double CpuDelay = 0.6, SyncEvery = 0.4, LegSeconds = 0.22;
    /// <summary>How often the CPU plays a random move instead of its best one, per level (Easy … Expert).</summary>
    static readonly double[] Blunders = { 0.45, 0.2, 0.05, 0 };

    protected static readonly Color Gold = Color.FromRgb(255, 209, 102);
    static readonly IBrush Glow = Art.Brush(150, 255, 209, 102);

    /// <summary>One piece on screen: its sprite, centred on its square, the shadow it casts when lifted, and what it is.</summary>
    sealed class PieceView
    {
        public required Sprite Sprite;
        public required Ellipse Shadow;
        public sbyte Piece;
    }

    // everything that moves with the board lives in _board, in board coordinates (0,0 = the top-left corner of the squares)
    readonly Canvas _board = new() { IsHitTestVisible = false };
    readonly Canvas _squares = new(), _deco = new(), _marks = new(), _glow = new(), _pieces = new();
    readonly Border _frame = new() { CornerRadius = new CornerRadius(6) };
    readonly Rectangle _dim = new() { Fill = Art.Brush(Colors.Black), Opacity = 0, RadiusX = 6, RadiusY = 6 };
    readonly Rectangle _shake = new() { Fill = Art.Brush(90, 255, 255, 255), IsVisible = false };
    readonly Rectangle[] _cells;
    readonly DragHandle _handle;
    readonly Anims _motion = new(); // pieces on the move; finished (snapped into place) whenever the board is drawn afresh
    readonly Color _light, _dark;

    IBoardRules _game;
    Vec2 _origin;
    double _cpuIn = -1, _syncT, _settle; // _settle: seconds until the last move's pieces have landed
    int _selected = -1, _gameNo, _myWins, _theirWins;
    int[]? _lastPath;
    readonly List<int> _via = new(); // landings clicked so far, when several capture routes start alike
    int[]? _pending; // guest: our move, re-sent until the host's board contains it
    int _lossStreak;
    bool _placed, _over, _demo, _wasLan;
    PieceView?[] _at = Array.Empty<PieceView?>();
    PieceView? _lifted;
    Anims.Tween? _pulse, _fade, _shaking;

    protected BoardGame(IGameHost host, Color light, Color dark) : base(host)
    {
        _game = NewRules();
        _light = light;
        _dark = dark;
        _cells = new Rectangle[Cols * Rows];
        for (int i = 0; i < _cells.Length; i++)
        {
            _cells[i] = new Rectangle();
            _squares.Children.Add(_cells[i]);
        }
        PaintBoard();
        foreach (var layer in new Control[] { _frame, _squares, _deco, _marks, _glow, _pieces, _shake, _dim })
        {
            layer.IsHitTestVisible = false;
            _board.Children.Add(layer);
        }
        Layer.Children.Add(_board);
        _handle = new DragHandle(host, Id, Title);
        Layer.Children.Add(_handle.Visual);
    }

    public override bool SupportsLan => true;

    protected abstract IBoardRules NewRules();
    protected abstract IBoardRules? Decode(string text);
    /// <summary>Adds the visuals for one piece centred on <paramref name="c"/> (the origin of the piece's own sprite, which slides as one).</summary>
    protected abstract void DrawPiece(Canvas into, sbyte piece, Vec2 c, double cell);
    /// <summary>Why the game ended in a draw, for the popup.</summary>
    protected abstract string DrawReason(IBoardRules game);
    /// <summary>Adds the visuals of a square itself, under any piece (grid lines, a hole in a Connect Four frame); drawn for every square.</summary>
    protected virtual void DrawSquare(Canvas into, int sq, Vec2 c, double cell) { }
    /// <summary>The move a click on <paramref name="sq"/> makes in a placement game, if any.</summary>
    protected virtual int[]? PlacementAt(int sq, List<int[]> moves) => moves.FirstOrDefault(m => m.Length == 1 && m[0] == sq);
    /// <summary>Where a placed piece falls from, in board coordinates, and how long the fall takes (Connect Four); null pops it in place.</summary>
    protected virtual (Vec2 From, double Seconds)? DropFrom(int sq) => null;
    /// <summary>The squares of the line that just won, in games that have one, so it can light up.</summary>
    protected virtual int[]? WinningLine => null;

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
    /// <summary>
    /// How far the CPU looks ahead at each level (Easy, Medium, Hard, Expert), for games that have levels;
    /// null keeps the game's own fixed strength. Easy starts every new player off gently.
    /// </summary>
    protected virtual int[]? LevelDepths => null;

    /// <summary>The CPU level, 1 (Easy) to 4 (Expert); it goes up when you win and down when you keep losing.</summary>
    public int Level
    {
        get => CpuLevel;
        set => CpuLevel = value;
    }

    public bool HasLevels => LevelDepths != null;
    public override bool HasCpuLevels => LevelDepths != null;
    /// <summary>Easy starts every new player off gently.</summary>
    protected override int DefaultCpuLevel => 1;

    public override Opponent? Opponent => new(Rival, !LanOn, HasLevels && !LanOn ? Level : 0, _over ? null : MyTurn);

    int[] CpuMove()
    {
        if (LevelDepths is not { } depths || _game is not ILeveledRules rules) return _game.BestMove(Rng);
        var moves = _game.LegalMoves();
        if (Rng.NextDouble() < Blunders[Level - 1]) return moves[Rng.Next(moves.Count)];
        return rules.BestMove(Rng, depths[Level - 1]);
    }

    protected double Cell { get; private set; }
    protected IBoardRules Game => _game;
    bool LanOn => Host.Lan.Connected;
    bool IsGuest => LanOn && Host.Lan.Role == LanRole.Guest;
    protected int Me => IsGuest ? -1 : 1;
    bool MyTurn => !_over && _game.Turn == Me;
    string Rival => LanOn ? Host.Lan.PeerName : L.T("CPU");
    int LastTo => _lastPath is { Length: > 0 } p ? p[^1] : -1;

    public override HudInfo Hud => new(
        Score,
        _over ? L.T("Game over · click the board for a rematch")
            : MyTurn ? YourMove
            : L.F("{0} is thinking…", Rival),
        HasLevels && !LanOn ? L.F("Wins {0} · CPU {1}", Host.Stats.Get(Id + ".wins"), L.T(LevelNames[Level - 1]))
            : L.F("Wins {0}", Host.Stats.Get(Id + ".wins")));

    // ------------------------------------------------------------------ layout

    public override void Layout()
    {
        var a = Host.Arena;
        double cell = Math.Floor(Math.Min(Math.Min(a.Height * 0.7 / Rows, a.Width * 0.5 / Cols), MaxSize / Math.Max(Cols, Rows)));
        bool fresh = cell != Cell || _at.Length == 0;
        Cell = cell;
        double w = Cell * Cols, h = Cell * Rows;
        if (!_placed)
        {
            _placed = true;
            _origin = _handle.Saved() ?? new Vec2(a.Center.X - w / 2, a.Center.Y - h / 2);
        }
        _origin = new Vec2(Clamp(_origin.X, a.Left + 10, a.Right - w - 10), Clamp(_origin.Y, a.Top + 10, a.Bottom - h - 10));
        Canvas.SetLeft(_board, _origin.X);
        Canvas.SetTop(_board, _origin.Y);
        _handle.Show(new Rect(_origin.X - 8, _origin.Y - 8, w + 16, h + 16));
        if (LanOn != _wasLan)
        {
            _wasLan = LanOn;
            _myWins = _theirWins = 0;
            NewGame();
        }
        else if (fresh) Rebuild();
    }

    public override void Activate()
    {
        Layout();
        Intro();
    }

    public override void Deactivate()
    {
        _handle.Cancel();
        _motion.Finish();
        Anims.Finish();
    }

    public override void PointerCancel() => _handle.Cancel();

    public override void PositionsReset() => _placed = false;

    public override void ThemeChanged() => PaintBoard();

    /// <summary>
    /// The squares and the frame: the theme's board colours when it has them and the game has a two-tone board
    /// (chess, checkers); a one-colour board (tic-tac-toe's paper, Connect Four's frame) always keeps its own.
    /// </summary>
    void PaintBoard()
    {
        var t = Themes.Current;
        bool themed = _light != _dark && t.BoardLight is { } && t.BoardDark is { };
        Color light = themed ? t.BoardLight!.Value : _light, dark = themed ? t.BoardDark!.Value : _dark;
        for (int i = 0; i < _cells.Length; i++)
            _cells[i].Fill = Art.Brush((i / Cols + i % Cols) % 2 == 1 ? dark : light);
        var frame = themed && t.BoardFrame is { } f ? Color.FromArgb(230, f.R, f.G, f.B) : Color.FromArgb(230, 60, 40, 28);
        _frame.Background = Art.Brush(frame);
    }

    void NewGame()
    {
        _game = NewRules();
        _gameNo++;
        _over = false;
        _selected = -1;
        _via.Clear();
        _lastPath = null;
        _pending = null;
        _cpuIn = -1;
        Rebuild();
        Intro();
        AfterMove();
    }

    Rect BoardRect => new(_origin.X, _origin.Y, Cell * Cols, Cell * Rows);

    int View(int sq) => IsGuest && FlipForGuest ? Cols * Rows - 1 - sq : sq;

    /// <summary>The centre of a square in board coordinates (what the pieces and marks are placed with).</summary>
    protected Vec2 Local(int sq)
    {
        int v = View(sq);
        return new Vec2((v % Cols + 0.5) * Cell, (v / Cols + 0.5) * Cell);
    }

    /// <summary>The centre of a square on screen.</summary>
    protected Vec2 Center(int sq) => _origin + Local(sq);

    int SquareAt(Vec2 p)
    {
        int c = (int)Math.Floor((p.X - _origin.X) / Cell), r = (int)Math.Floor((p.Y - _origin.Y) / Cell);
        return r >= 0 && r < Rows && c >= 0 && c < Cols ? View(r * Cols + c) : -1;
    }

    // ------------------------------------------------------------------ input

    public override void CollectHitShapes(List<HitShape> into)
    {
        into.Add(HitShape.Box(BoardRect.Inflate(8)));
        into.Add(_handle.Hit);
    }

    public override bool PointerDown(Vec2 p, bool right)
    {
        if (right || _handle.Contains(p))
        {
            _handle.Begin(p, _origin, anywhere: true); // the grip, or a right-drag anywhere on the board
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
        {
            Play(place);
            return false;
        }
        if (_selected >= 0 && ClickLanding(moves, sq)) { }
        else if (moves.Any(m => m[0] == sq))
        {
            _selected = sq;
            _via.Clear();
            Host.Sound.Play("board", 0.25, 1.6);
        }
        else
        {
            Deselect();
            Shake(sq);
        }
        DrawMarks();
        Host.Wake();
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

    public override void PointerUp(Vec2 p) => _handle.End(_origin);

    public override void Summon(Vec2 p)
    {
        _origin = p - new Vec2(Cell * Cols / 2, Cell * Rows / 2);
        Layout();
        _handle.Save(_origin);
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
        var before = (sbyte[])_game.Board.Clone();
        var captured = _game.Apply(move);
        Deselect();
        _lastPath = move;
        Show(BoardAnim.Plan(before, _game.Board, move));
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
                Host.Fx.Popup(Center(LastTo) + new Vec2(0, -Cell), captures >= 3 ? L.T("TRIPLE JUMP!") : L.T("DOUBLE JUMP!"), Gold, 26, 1.2);
        }
        else Host.Sound.Play("board", 0.45, 1.2);
    }

    /// <summary>The board changed (a move, a new game, the host's board): the outcome, the CPU's turn, the marks and the HUD.</summary>
    void AfterMove()
    {
        if (!_over && _game.Result != 0) GameOver(_game.Result);
        else if (!_over && _game.Alert >= 0) Host.Sound.Play("rim", 0.5, 0.8);
        if (!_over && !LanOn && _game.Turn != Me) _cpuIn = Math.Max(CpuDelay, _settle + 0.25);
        if (!IsGuest) SendState();
        DrawMarks();
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
            Fade(Art.Brush(Color.FromRgb(128, 128, 128)), 0.45);
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
            string sub = L.F("vs {0}", Rival);
            if (HasLevels && !LanOn && !_demo) // demo games don't move the player's level
            {
                _lossStreak = 0;
                if (Level < LevelNames.Length)
                {
                    Level++;
                    sub = L.F("the CPU moves up to {0}", L.T(LevelNames[Level - 1]));
                }
            }
            Host.Fx.Popup(at, L.T("YOU WIN!"), Gold, 42, 2.6, sub);
            Host.Fx.Burst(at, new[] { Gold, Colors.White, Color.FromRgb(214, 64, 69) }, 44, 540, 700, 7, 1.1);
            Host.Sound.Play("best", 0.8);
            Celebrate(result);
        }
        else
        {
            _theirWins++;
            string sub = L.T("click the board for a rematch");
            if (HasLevels && !LanOn && !_demo && ++_lossStreak >= 2 && Level > 1)
            {
                _lossStreak = 0;
                Level--;
                sub = L.F("the CPU goes easier: {0} · click the board for a rematch", L.T(LevelNames[Level - 1]));
            }
            Host.Fx.Popup(at, L.F("{0} WINS", Rival), Colors.White, 38, 2.4, sub);
            Host.Sound.Play("buzzer", 0.45);
            Fade(Art.Brush(Colors.Black), 0.5);
        }
    }

    public override bool Update(double dt)
    {
        if (_handle.Dragging)
        {
            _origin = _handle.Move(Host.Pointer, new Size(Cell * Cols, Cell * Rows));
            Layout();
        }
        if (LanOn) Network(dt);
        else if (_cpuIn > 0 && (_cpuIn -= dt) <= 0)
        {
            _cpuIn = -1;
            if (!_over && _game.Turn != Me) PlayAny(CpuMove());
        }
        if (_demo && MyTurn && _pending == null && _cpuIn < 0 && !_motion.Busy) Play(_game.BestMove(Rng));
        bool moving = _motion.Update(dt);
        bool effects = Anims.Update(dt);
        return _handle.Dragging || _cpuIn > 0 || LanOn || moving || effects;
    }

    public override void DemoTick() => _demo = true;

    // ------------------------------------------------------------------ LAN
    // host → guest  "st|gameNo|a,b,c|board"  (the last move's path, then IBoardRules.Encode; sent often)
    // guest → host  "mv|ply|a,b,c"           (re-sent until the host's ply passes it)
    // guest → host  "r|"                     (rematch)

    void Network(double dt)
    {
        while (Host.Lan.TryReceive(out var msg))
        {
            if (IsGuest && msg.StartsWith("st|", StringComparison.Ordinal)) ReadState(msg.Split('|', 4));
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

    void SendState() => Host.Lan.Send($"st|{_gameNo}|{(_lastPath == null ? "" : string.Join(",", _lastPath))}|{_game.Encode()}");

    void SendMove(int ply) => Host.Lan.Send($"mv|{ply}|{string.Join(",", _pending!)}");

    static int[]? ParsePath(string text)
    {
        if (text.Length == 0) return null;
        var path = text.Split(',').Select(s => int.TryParse(s, out int v) ? v : -1).ToArray();
        return path.All(sq => sq >= 0) ? path : null;
    }

    void ReadMove(string[] f)
    {
        if (f.Length != 3 || !int.TryParse(f[1], out int ply) || ply != _game.Ply || _game.Turn != -1) return; // stale or repeated
        if (ParsePath(f[2]) is not int[] path) return;
        if (_game.LegalMoves().Any(m => m.SequenceEqual(path))) PlayAny(path);
    }

    void ReadState(string[] f)
    {
        if (f.Length != 4 || !int.TryParse(f[1], out int gameNo) || Decode(f[3]) is not IBoardRules d) return;
        bool newGame = gameNo != _gameNo;
        if (!newGame && d.Ply < _game.Ply) return; // our own move hasn't reached the host yet
        _pending = null;
        if (!newGame && d.Ply == _game.Ply && d.Encode() == _game.Encode()) return;

        var path = ParsePath(f[2]);
        int before = _game.Count(Me);
        bool advanced = !newGame && d.Ply > _game.Ply;
        var plan = newGame ? null : BoardAnim.Decide(_game.Board, d.Board, _game.Ply, d.Ply, path, _game.LegalMoves());
        if (newGame)
        {
            _gameNo = gameNo;
            _over = false;
        }
        _game = d;
        Deselect();
        _lastPath = path;
        if (newGame)
        {
            Rebuild();
            Intro();
        }
        else Show(plan);
        if (advanced) MoveFx(before - d.Count(Me), false);
        AfterMove();
    }

    // ------------------------------------------------------------------ visuals

    /// <summary>Draws the board afresh: every piece on its square, nothing in motion.</summary>
    void Rebuild()
    {
        _motion.Finish();
        Anims.Finish();
        _pulse = _fade = _shaking = null;
        _lifted = null;
        _settle = 0;
        double w = Cell * Cols, h = Cell * Rows;
        foreach (var box in new Control[] { _frame, _dim })
        {
            Canvas.SetLeft(box, -8);
            Canvas.SetTop(box, -8);
            box.Width = w + 16;
            box.Height = h + 16;
        }
        _dim.Opacity = 0;
        _shake.Width = _shake.Height = Cell;
        _shake.IsVisible = false;
        _deco.Children.Clear();
        for (int sq = 0; sq < _cells.Length; sq++)
        {
            var c = Local(sq);
            _cells[sq].Width = _cells[sq].Height = Cell;
            Canvas.SetLeft(_cells[sq], c.X - Cell / 2);
            Canvas.SetTop(_cells[sq], c.Y - Cell / 2);
            DrawSquare(_deco, sq, c, Cell);
        }
        _pieces.Children.Clear();
        _at = new PieceView?[_cells.Length];
        for (int sq = 0; sq < _cells.Length; sq++)
            if (_game.Board[sq] != 0) _at[sq] = Create(_game.Board[sq], sq);
        _glow.Children.Clear();
        if (_over && _game.Result is 1 or -1 && WinningLine is { } line)
            foreach (int sq in line) _glow.Children.Add(Box(sq, Glow, null));
        DrawMarks();
    }

    /// <summary>Animates the move when the plan fits what is on screen; otherwise draws the board afresh.</summary>
    void Show(BoardMovePlan? plan)
    {
        if (plan == null || Fx.ReducedMotion) Rebuild();
        else Animate(plan);
    }

    void Animate(BoardMovePlan plan)
    {
        _motion.Finish();
        var mover = plan.Slides.Count > 0 ? plan.Slides[0].Path : null;
        double end = 0;
        foreach (var (sq, _) in plan.Vanish)
        {
            if (_at[sq] is not { } victim)
            {
                Rebuild();
                return;
            }
            _at[sq] = null;
            double at = mover == null ? 0 : (BoardAnim.LegOf(mover, sq, Cols) + 0.75) * LegSeconds;
            var s = victim.Sprite;
            _motion.Add(0.25, k =>
            {
                s.Scale = 1 - 0.7 * k;
                s.Opacity = 1 - k;
            }, Ease.OutQuad, () => _pieces.Children.Remove(s), at);
            end = Math.Max(end, at + 0.25);
        }
        foreach (var (path, _) in plan.Slides)
        {
            if (_at[path[0]] is not { } pv || _at[path[^1]] != null)
            {
                Rebuild();
                return;
            }
            _at[path[0]] = null;
            _at[path[^1]] = pv;
            ToFront(pv.Sprite);
            var s = pv.Sprite;
            for (int i = 1; i < path.Length; i++)
            {
                Vec2 from = Local(path[i - 1]), to = Local(path[i]);
                _motion.Add(LegSeconds, k => s.Set(from + (to - from) * k), Ease.OutCubic, delay: (i - 1) * LegSeconds);
            }
            end = Math.Max(end, (path.Length - 1) * LegSeconds);
        }
        if (plan.Appear is { } appear)
        {
            if (_at[appear.Square] != null)
            {
                Rebuild();
                return;
            }
            var s = (_at[appear.Square] = Create(appear.Piece, appear.Square)).Sprite;
            var home = Local(appear.Square);
            if (DropFrom(appear.Square) is { } drop)
            {
                s.Set(drop.From);
                _motion.Add(drop.Seconds, k => s.Set(drop.From + (home - drop.From) * k), Ease.OutBounce);
                end = Math.Max(end, drop.Seconds);
            }
            else
            {
                s.Scale = 0;
                _motion.Add(0.3, k => s.Scale = k, Ease.OutBack);
                end = Math.Max(end, 0.3);
            }
        }
        if (plan.Becomes is { } becomes)
        {
            if (_at[becomes.Square] is not { } pv)
            {
                Rebuild();
                return;
            }
            double at = mover == null ? 0 : (mover.Length - 1) * LegSeconds;
            bool swapped = false;
            var s = pv.Sprite;
            _motion.Add(0.32, k =>
            {
                if (!swapped)
                {
                    swapped = true;
                    Repaint(pv, becomes.Piece);
                }
                s.Scale = 0.6 + 0.4 * k;
            }, Ease.OutBack, delay: at);
            end = Math.Max(end, at + 0.32);
        }
        // whatever the plan said, the screen must end up showing the board
        for (int sq = 0; sq < _at.Length; sq++)
        {
            sbyte shown = _at[sq]?.Piece ?? 0;
            if (plan.Becomes is { } b && b.Square == sq && _at[sq] != null) shown = b.Piece;
            if (shown != _game.Board[sq])
            {
                Rebuild();
                return;
            }
        }
        _settle = end;
    }

    /// <summary>The pieces drop into place row by row, over about 0.4 s (skipped with reduced motion).</summary>
    void Intro()
    {
        if (Fx.ReducedMotion) return;
        _motion.Finish();
        for (int sq = 0; sq < _at.Length; sq++)
        {
            if (_at[sq] is not { } pv) continue;
            var s = pv.Sprite;
            var home = Local(sq);
            var from = home - new Vec2(0, Cell * 0.5);
            s.Set(from);
            s.Opacity = 0;
            _motion.Add(0.26, k =>
            {
                s.Set(from + (home - from) * k);
                s.Opacity = k;
            }, Ease.OutCubic, delay: View(sq) / Cols * (0.4 / Rows));
        }
        Host.Wake();
    }

    PieceView Create(sbyte piece, int sq)
    {
        var s = new Sprite();
        var shadow = Art.Circle(Cell * 0.06, Cell * 0.1, Cell * 0.36, Art.Brush(80, 0, 0, 0));
        shadow.IsVisible = false;
        s.Children.Insert(0, shadow);
        DrawPiece(s, piece, new Vec2(0, 0), Cell);
        s.Set(Local(sq));
        _pieces.Children.Add(s);
        return new PieceView { Sprite = s, Shadow = shadow, Piece = piece };
    }

    /// <summary>Redraws a piece as another kind (a pawn that became a queen), keeping its sprite, shadow and place.</summary>
    void Repaint(PieceView pv, sbyte piece)
    {
        while (pv.Sprite.Children.Count > 2) pv.Sprite.Children.RemoveAt(2); // keeps the shadow and the rotor
        DrawPiece(pv.Sprite, piece, new Vec2(0, 0), Cell);
        pv.Piece = piece;
    }

    void ToFront(Control c)
    {
        _pieces.Children.Remove(c);
        _pieces.Children.Add(c);
    }

    /// <summary>The last move, a king in check (pulsing), the selected piece lifted with its landing dots, or the pieces that can move.</summary>
    void DrawMarks()
    {
        _pulse?.Cancel();
        _fade?.Cancel();
        _pulse = _fade = null;
        _marks.Children.Clear();
        var gold = Themes.Current.Gold;
        if (_lastPath is { Length: > 0 } last)
            foreach (int sq in new[] { last[0], last[^1] }.Distinct())
                _marks.Children.Add(Box(sq, Art.Brush(Color.FromArgb(70, gold.R, gold.G, gold.B)), null));
        if (_game.Alert >= 0)
        {
            var box = Box(_game.Alert, Art.Brush(110, 255, 60, 60), null);
            _marks.Children.Add(box);
            _pulse = Anims.Add(2.4, k => box.Opacity = k >= 1 ? 1 : 0.5 + 0.5 * Ease.Pulse(k * 3 % 1), Ease.Linear);
        }
        if (MyTurn && _pending == null)
        {
            var moves = _game.LegalMoves();
            if (_selected >= 0)
            {
                _marks.Children.Add(Box(_selected, null, Art.Brush(gold)));
                foreach (int sq in _via) _marks.Children.Add(Box(sq, null, Art.Brush(gold)));
                var dots = new List<Control>();
                foreach (var m in Routes(moves).Where(m => m.Length > _via.Count + 1))
                {
                    var c = Local(m[^1]);
                    dots.Add(Art.Circle(c.X, c.Y, Cell * 0.16, Art.Brush(Color.FromArgb(200, gold.R, gold.G, gold.B))));
                    if (m.Length > _via.Count + 2) // a route with more landings: mark the next one too
                    {
                        var n = Local(m[_via.Count + 1]);
                        dots.Add(Art.Circle(n.X, n.Y, Cell * 0.1, null, Art.Brush(Color.FromArgb(200, gold.R, gold.G, gold.B)), 2));
                    }
                }
                foreach (var dot in dots)
                {
                    dot.Opacity = 0;
                    _marks.Children.Add(dot);
                }
                _fade = Anims.Add(0.18, k =>
                {
                    foreach (var dot in dots) dot.Opacity = k;
                }, Ease.OutQuad);
            }
            else
                foreach (int from in moves.Where(m => m.Length > 1).Select(m => m[0]).Distinct())
                    _marks.Children.Add(Box(from, null, Art.Brush(Color.FromArgb(120, gold.R, gold.G, gold.B))));
        }
        Lift(_selected >= 0 ? _at[_selected] : null);
    }

    /// <summary>Raises the selected piece a little (scale and shadow) and sets the previous one down.</summary>
    void Lift(PieceView? pv)
    {
        if (_lifted == pv) return;
        if (_lifted is { } old)
        {
            var s = old.Sprite;
            double from = s.Scale;
            old.Shadow.IsVisible = false;
            Anims.Add(0.12, k => s.Scale = from + (1 - from) * k, Ease.OutCubic);
        }
        _lifted = pv;
        if (pv != null)
        {
            var s = pv.Sprite;
            double from = s.Scale;
            pv.Shadow.IsVisible = true;
            ToFront(s);
            Anims.Add(0.12, k => s.Scale = from + (1.08 - from) * k, Ease.OutCubic);
        }
    }

    /// <summary>A click that does nothing: the square wobbles and the board knocks, low.</summary>
    void Shake(int sq)
    {
        Host.Sound.Play("board", 0.3, 0.6);
        _shaking?.Cancel();
        var c = Local(sq);
        _shake.IsVisible = true;
        Canvas.SetTop(_shake, c.Y - Cell / 2);
        _shaking = Anims.Add(0.28, k => Canvas.SetLeft(_shake, c.X - Cell / 2 + Math.Sin(k * Math.PI * 4) * Cell * 0.06 * (1 - k)),
            Ease.Linear, () => _shake.IsVisible = false);
    }

    /// <summary>The board goes dark (a loss) or grey (a draw) for a moment, once the last move has landed.</summary>
    void Fade(IBrush brush, double depth)
    {
        _dim.Fill = brush;
        Anims.Add(1.5, k => _dim.Opacity = depth * (k < 0.2 ? k / 0.2 : k > 0.55 ? (1 - k) / 0.45 : 1), Ease.Linear, delay: _settle);
    }

    /// <summary>The winning pieces hop one after another (the winning line, where there is one, and it lights up).</summary>
    void Celebrate(int side)
    {
        var line = WinningLine;
        var squares = line?.ToList() ?? Enumerable.Range(0, _at.Length)
            .Where(sq => _at[sq] is { } p && Math.Sign(p.Piece) == side)
            .OrderByDescending(sq => View(sq) / Cols).ThenBy(sq => View(sq) % Cols).ToList();
        int i = 0;
        foreach (int sq in squares)
        {
            if (_at[sq] is not { } pv) continue;
            var s = pv.Sprite;
            var home = Local(sq);
            Anims.Add(0.36, k => s.Set(home - new Vec2(0, Cell * 0.22 * Ease.Pulse(k))), Ease.Linear, delay: _settle + 0.06 * i++);
        }
        if (line == null) return;
        int j = 0;
        foreach (int sq in line)
        {
            var box = Box(sq, Glow, null);
            box.Opacity = 0;
            _glow.Children.Add(box);
            Anims.Add(0.3, k => box.Opacity = k, Ease.OutQuad, delay: _settle + 0.06 * j++);
        }
    }

    Rectangle Box(int sq, IBrush? fill, IBrush? stroke)
    {
        var c = Local(sq);
        var box = new Rectangle { Width = Cell, Height = Cell, Fill = fill, Stroke = stroke, StrokeThickness = 3 };
        Canvas.SetLeft(box, c.X - Cell / 2);
        Canvas.SetTop(box, c.Y - Cell / 2);
        return box;
    }
}
