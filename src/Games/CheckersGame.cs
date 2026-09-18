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
/// Checkers on a board floating over the desktop, against the computer or a co-worker over the LAN.
/// Click a piece, then where it should land (a multi-jump lands on its last square). Right-drag moves
/// the board. Over the LAN the host keeps the real board and sends it a few times a second; the guest
/// sends its move until the host's board shows it, so a lost packet never desyncs the game.
/// The guest sees the board turned around, with its own pieces at the bottom.
/// </summary>
public sealed class CheckersGame : MiniGame
{
    const double CpuDelay = 0.6, SyncEvery = 0.4;

    static readonly Color Gold = Color.FromRgb(255, 209, 102);
    static readonly Color Light = Color.FromRgb(232, 221, 200), DarkSq = Color.FromRgb(120, 84, 60);
    static readonly Color Mine = Color.FromRgb(240, 240, 235), Theirs = Color.FromRgb(214, 64, 69);

    readonly Canvas _squares = new() { IsHitTestVisible = false };
    readonly Canvas _marks = new() { IsHitTestVisible = false };
    readonly Canvas _pieces = new() { IsHitTestVisible = false };
    readonly Border _frame = new() { CornerRadius = new CornerRadius(6), Background = Art.Brush(230, 60, 40, 28), IsHitTestVisible = false };
    readonly Rectangle[] _cells = new Rectangle[64];

    Draughts _game = Draughts.New();
    Vec2 _origin, _dragOffset;
    double _cell, _cpuIn = -1, _syncT;
    int _selected = -1, _gameNo, _lastFrom = -1, _lastTo = -1;
    int[]? _pending; // guest: our move, re-sent until the host's board contains it
    bool _placed, _dragging, _over, _demo, _wasLan;

    public CheckersGame(IGameHost host) : base(host)
    {
        Layer.Children.Add(_frame);
        Layer.Children.Add(_squares);
        for (int i = 0; i < 64; i++)
        {
            _cells[i] = new Rectangle { Fill = Art.Brush(Draughts.Dark(i) ? DarkSq : Light) };
            _squares.Children.Add(_cells[i]);
        }
        Layer.Children.Add(_marks);
        Layer.Children.Add(_pieces);
    }

    public override string Id => "checkers";
    public override string Title => "Checkers";
    public override bool SupportsLan => true;

    bool LanOn => Host.Lan.Connected;
    bool IsGuest => LanOn && Host.Lan.Role == LanRole.Guest;
    int Me => IsGuest ? -1 : 1;
    bool MyTurn => !_over && _game.Turn == Me;
    string Rival => LanOn ? Host.Lan.PeerName : L.T("CPU");

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        s.Rotor.Children.Add(Art.Circle(-3, 2, 7, Art.Brush(Theirs), Art.Brush("#6B1E22"), 1));
        s.Rotor.Children.Add(Art.Circle(3, -3, 7, Art.Brush(Mine), Art.Brush("#555555"), 1));
        return s;
    }

    public override HudInfo Hud => new(
        $"{_game.Count(Me)}–{_game.Count(-Me)}",
        _over ? L.T("Game over · click the board for a rematch")
            : MyTurn ? L.T("Your move · click a piece, then where it goes · right-drag moves the board")
            : L.F("{0} is thinking…", Rival),
        L.F("Wins {0}", Host.Stats.Get("checkers.wins")));

    // ------------------------------------------------------------------ layout

    public override void Layout()
    {
        var a = Host.Arena;
        _cell = Math.Floor(Math.Min(Math.Min(a.Height * 0.7, a.Width * 0.5), 520) / 8);
        double size = _cell * 8;
        if (!_placed)
        {
            _placed = true;
            _origin = new Vec2(a.Center.X - size / 2, a.Center.Y - size / 2);
        }
        _origin = new Vec2(Clamp(_origin.X, a.Left + 10, a.Right - size - 10), Clamp(_origin.Y, a.Top + 10, a.Bottom - size - 10));
        if (LanOn != _wasLan)
        {
            _wasLan = LanOn;
            NewGame();
        }
        Draw();
    }

    public override void Deactivate() => _dragging = false;

    void NewGame()
    {
        _game = Draughts.New();
        _gameNo++;
        _over = false;
        _selected = _lastFrom = _lastTo = -1;
        _pending = null;
        _cpuIn = -1;
        AfterMove();
    }

    Rect BoardRect => new(_origin.X, _origin.Y, _cell * 8, _cell * 8);

    int View(int sq) => IsGuest ? 63 - sq : sq;

    Vec2 Center(int sq)
    {
        int v = View(sq);
        return new Vec2(_origin.X + (v % 8 + 0.5) * _cell, _origin.Y + (v / 8 + 0.5) * _cell);
    }

    int SquareAt(Vec2 p)
    {
        int c = (int)Math.Floor((p.X - _origin.X) / _cell), r = (int)Math.Floor((p.Y - _origin.Y) / _cell);
        return r is >= 0 and < 8 && c is >= 0 and < 8 ? View(r * 8 + c) : -1;
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
        if (_selected >= 0 && moves.FirstOrDefault(m => m[0] == _selected && m[^1] == sq) is int[] move)
            Play(move);
        else if (moves.Any(m => m[0] == sq))
        {
            _selected = sq;
            Host.Sound.Play("board", 0.25, 1.6);
        }
        else _selected = -1;
        Draw();
        return false;
    }

    public override void PointerUp(Vec2 p) => _dragging = false;

    public override void Summon(Vec2 p)
    {
        _origin = p - new Vec2(_cell * 4, _cell * 4);
        Layout();
    }

    // ------------------------------------------------------------------ play

    void Play(int[] move)
    {
        int ply = _game.Ply;
        var captured = _game.Apply(move);
        _selected = -1;
        _lastFrom = move[0];
        _lastTo = move[^1];
        MoveFx(captured.Count, _game.Turn != Me);
        if (IsGuest)
        {
            _pending = move;
            SendMove(ply);
        }
        AfterMove();
    }

    void MoveFx(int captures, bool byMe)
    {
        if (captures > 0)
        {
            Host.Sound.Play("score", 0.5 + 0.1 * captures);
            if (byMe) Host.Stats.Add("checkers.captures", captures);
            if (captures >= 2)
                Host.Fx.Popup(Center(_lastTo) + new Vec2(0, -_cell), captures >= 3 ? L.T("TRIPLE JUMP!") : L.T("DOUBLE JUMP!"), Gold, 26, 1.2);
        }
        else Host.Sound.Play("board", 0.45, 1.2);
    }

    void AfterMove()
    {
        if (!_over && _game.Winner is int w && w != 0) GameOver(w == Me);
        else if (!_over && _game.IsDraw) DrawnGame();
        if (!_over && !LanOn && _game.Turn != Me) _cpuIn = CpuDelay;
        if (!IsGuest) SendState();
        Draw();
        Host.HudChanged();
        Host.Wake();
    }

    void DrawnGame()
    {
        _over = true;
        var b = BoardRect;
        Host.Fx.Popup(new Vec2(b.Center.X, b.Top + b.Height * 0.3), L.T("DRAW"), Colors.White, 38, 2.4,
            L.T("40 moves without a capture · click the board for a rematch"));
        Host.Sound.Play("buzzer", 0.3);
        Host.Stats.Add("checkers.draws");
    }

    void GameOver(bool won)
    {
        _over = true;
        var b = BoardRect;
        var at = new Vec2(b.Center.X, b.Top + b.Height * 0.3);
        if (won)
        {
            Host.Stats.Add("checkers.wins");
            if (LanOn) Host.Stats.Add("checkers.lanwins");
            Host.Fx.Popup(at, L.T("YOU WIN!"), Gold, 42, 2.6, L.F("vs {0}", Rival));
            Host.Fx.Burst(at, new[] { Gold, Mine, Theirs }, 44, 540, 700, 7, 1.1);
            Host.Sound.Play("best", 0.8);
        }
        else
        {
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
            if (!_over && _game.Turn != Me) PlayOther(_game.BestMove(Rng));
        }
        if (_demo && MyTurn && _pending == null && _cpuIn < 0) Play(_game.BestMove(Rng, 2));
        return _dragging || _cpuIn > 0 || LanOn;
    }

    void PlayOther(int[] move)
    {
        var captured = _game.Apply(move);
        _lastFrom = move[0];
        _lastTo = move[^1];
        _selected = -1;
        MoveFx(captured.Count, false);
        AfterMove();
    }

    // ------------------------------------------------------------------ LAN
    // host → guest  "st|gameNo|board|turn|ply"   (Draughts.Encode, sent often)
    // guest → host  "mv|ply|a,b,c"                (re-sent until the host's ply passes it)
    // guest → host  "r|"                          (rematch)

    void Network(double dt)
    {
        while (Host.Lan.TryReceive(out var msg))
        {
            var f = msg.Split('|', 3);
            if (IsGuest && f[0] == "st" && f.Length == 3) ReadState(f[1], f[2]);
            else if (!IsGuest && f[0] == "mv" && f.Length == 3) ReadMove(f[1], f[2]);
            else if (!IsGuest && f[0] == "r" && _over) NewGame();
        }
        if ((_syncT += dt) < SyncEvery) return;
        _syncT = 0;
        if (IsGuest)
        {
            if (_pending != null) SendMove(_game.Ply - 1);
        }
        else SendState();
    }

    void SendState() => Host.Lan.Send($"st|{_gameNo}|{_game.Encode()}");

    void SendMove(int ply) => Host.Lan.Send($"mv|{ply}|{string.Join(",", _pending!)}");

    void ReadMove(string plyText, string pathText)
    {
        if (!int.TryParse(plyText, out int ply) || ply != _game.Ply || _game.Turn != -1) return; // stale or repeated
        var path = pathText.Split(',').Select(s => int.TryParse(s, out int v) ? v : -1).ToArray();
        if (path.Length < 2 || !_game.IsLegal(path)) return;
        PlayOther(path);
    }

    void ReadState(string gameNoText, string board)
    {
        if (!int.TryParse(gameNoText, out int gameNo) || Draughts.Decode(board) is not Draughts d) return;
        bool newGame = gameNo != _gameNo;
        if (!newGame && d.Ply < _game.Ply) return; // our own move hasn't reached the host yet
        if (_pending != null && d.Ply > _game.Ply - 1) _pending = null;
        if (!newGame && d.Ply == _game.Ply && d.Encode() == _game.Encode()) return;

        if (newGame)
        {
            _gameNo = gameNo;
            _over = false;
            _pending = null;
            _lastFrom = _lastTo = -1;
        }
        else if (d.Ply > _game.Ply) HighlightChange(_game, d);
        _game = d;
        _selected = -1;
        AfterMove();
    }

    /// <summary>Guest: find the host's last move from the board difference, to mark it and play its sound.</summary>
    void HighlightChange(Draughts before, Draughts after)
    {
        int captured = 0;
        for (int i = 0; i < 64; i++)
        {
            if (before.Board[i] != 0 && after.Board[i] == 0)
            {
                if (Math.Sign(before.Board[i]) == Me) captured++;
                else _lastFrom = i;
            }
            else if (before.Board[i] == 0 && after.Board[i] != 0) _lastTo = i;
        }
        MoveFx(captured, false);
    }

    // ------------------------------------------------------------------ visuals

    void Draw()
    {
        var b = BoardRect;
        Canvas.SetLeft(_frame, b.Left - 8);
        Canvas.SetTop(_frame, b.Top - 8);
        _frame.Width = b.Width + 16;
        _frame.Height = b.Height + 16;
        for (int sq = 0; sq < 64; sq++)
        {
            var c = Center(sq);
            _cells[sq].Width = _cells[sq].Height = _cell;
            Canvas.SetLeft(_cells[sq], c.X - _cell / 2);
            Canvas.SetTop(_cells[sq], c.Y - _cell / 2);
        }

        _marks.Children.Clear();
        foreach (int sq in new[] { _lastFrom, _lastTo })
            if (sq >= 0) _marks.Children.Add(Box(sq, Art.Brush(70, 255, 209, 102), null));
        if (MyTurn && _pending == null)
        {
            var moves = _game.LegalMoves();
            if (_selected >= 0)
            {
                _marks.Children.Add(Box(_selected, null, Art.Brush(Gold)));
                foreach (var m in moves.Where(m => m[0] == _selected))
                {
                    var c = Center(m[^1]);
                    _marks.Children.Add(Art.Circle(c.X, c.Y, _cell * 0.16, Art.Brush(200, 255, 209, 102)));
                }
            }
            else
                foreach (int from in moves.Select(m => m[0]).Distinct())
                    _marks.Children.Add(Box(from, null, Art.Brush(120, 255, 209, 102)));
        }

        _pieces.Children.Clear();
        double r = _cell * 0.38;
        for (int sq = 0; sq < 64; sq++)
        {
            sbyte p = _game.Board[sq];
            if (p == 0) continue;
            var c = Center(sq);
            var color = Math.Sign(p) == Me ? Mine : Theirs;
            _pieces.Children.Add(Art.Circle(c.X + 2, c.Y + 3, r, Art.Brush(70, 0, 0, 0)));
            _pieces.Children.Add(Art.Circle(c.X, c.Y, r, Art.Brush(color), Art.Brush(Art.Blend(color, Colors.Black, 0.45)), 2));
            _pieces.Children.Add(Art.Circle(c.X, c.Y, r * 0.62, null, Art.Brush(Art.Blend(color, Colors.Black, 0.25)), 1.5));
            if (Math.Abs(p) == 2) _pieces.Children.Add(Art.Circle(c.X, c.Y, r * 0.3, Art.Brush(Gold)));
        }
    }

    Rectangle Box(int sq, IBrush? fill, IBrush? stroke)
    {
        var c = Center(sq);
        var box = new Rectangle { Width = _cell, Height = _cell, Fill = fill, Stroke = stroke, StrokeThickness = 3 };
        Canvas.SetLeft(box, c.X - _cell / 2);
        Canvas.SetTop(box, c.Y - _cell / 2);
        return box;
    }

    public override void DemoTick() => _demo = true;
}
