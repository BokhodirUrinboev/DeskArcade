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
/// Sea Battle (Battleship) against the CPU or over the LAN. Your fleet is on the left, the enemy's waters
/// on the right. Before the first shot, click your grid to shuffle the fleet and the enemy grid to start.
/// A hit earns another shot. Shells arc across to their square: a splash for a miss, a burst for a hit, and
/// a sunk ship darkens square by square; the enemy's fleet surfaces when the game is over. The grip above
/// the grids (or a right-drag) moves them. Over the LAN each fleet stays on its own PC: only shots and their
/// results cross the link, and a shot is re-sent until its answer arrives. The host fires first. The CPU has
/// four levels, from a random shooter to one that hunts where the most ships could still lie.
/// </summary>
public sealed class SeaBattleGame : MiniGame
{
    const int N = SeaFleet.N;
    const double CpuDelay = 0.7, ResendEvery = 0.4, Flight = 0.4, DarkenStep = 0.12;

    static readonly Color Water = Color.FromRgb(28, 74, 128), Grid = Color.FromRgb(90, 140, 200);
    static readonly Color ShipColor = Color.FromRgb(160, 170, 185), HitColor = Color.FromRgb(240, 70, 60), Gold = Color.FromRgb(255, 209, 102);
    static readonly Color Foam = Color.FromRgb(216, 230, 245), Ember = Color.FromRgb(255, 140, 40);

    enum Phase { Setup, Playing, Over }

    readonly Canvas _board = new() { IsHitTestVisible = false };  // the layers below, in board coordinates, carried to _origin by _boardTr
    readonly TranslateTransform _boardTr = new();
    readonly Canvas _canvas = new() { IsHitTestVisible = false }; // the frame, labels, grids and my ships
    readonly Canvas _marks = new() { IsHitTestVisible = false };  // one sprite per square that was shot at
    readonly Canvas _reveal = new() { IsHitTestVisible = false }; // the enemy's ships, once the game is over
    readonly Canvas _hints = new() { IsHitTestVisible = false };  // whose turn, the shot awaiting its answer, the last shot at me
    readonly Canvas _air = new() { IsHitTestVisible = false };    // shells in flight
    readonly DragHandle _handle;
    readonly Sprite?[] _myMarks = new Sprite?[N * N], _enemyMarks = new Sprite?[N * N];
    readonly sbyte[] _shownMine = new sbyte[N * N], _shownEnemy = new sbyte[N * N]; // what each mark sprite shows
    readonly HashSet<(bool AtMe, int Sq)> _inFlight = new(); // squares whose shell hasn't landed yet
    SeaFleet _fleet = SeaFleet.Random(Rng);
    SeaFleet? _cpuFleet = SeaFleet.Random(Rng), _enemyFleet; // the CPU's fleet (null over the LAN); the rival's once revealed
    sbyte[] _chart = new sbyte[N * N], _cpuChart = new sbyte[N * N];
    Phase _phase;
    bool _myTurn, _placed, _demo, _wasLan, _meReady, _peerReady, _revealShown, _quiet;
    Vec2 _origin;
    double _cell, _cpuIn = -1, _resendT;
    int _gameNo = 1, _shotSeq, _lastAnswered, _pendingSq = -1, _lastShotAtMe = -1, _flights, _lossStreak;
    string _endSub = "";
    readonly Dictionary<int, string> _answers = new(); // LAN: our answers by shot number, for re-sent shots

    public SeaBattleGame(IGameHost host) : base(host)
    {
        foreach (var layer in new[] { _canvas, _marks, _reveal, _hints, _air }) _board.Children.Add(layer);
        _board.RenderTransformOrigin = RelativePoint.TopLeft;
        _board.RenderTransform = _boardTr;
        Layer.Children.Add(_board);
        _handle = new DragHandle(host, Id, Title);
        Layer.Children.Add(_handle.Visual);
    }

    public override string Id => "seabattle";
    public override string Title => "Sea Battle";
    public override bool SupportsLan => true;
    public override bool HasCpuLevels => true;

    bool LanOn => Host.Lan.Connected;
    bool IsHost => !LanOn || Host.Lan.Role == LanRole.Host;
    string Rival => LanOn ? Host.Lan.PeerName : L.T("CPU");
    int EnemyShipsLeft => SeaFleet.Sizes.Length - SeaChart.SunkSizes(_chart).Count;

    public override Opponent? Opponent => new(Rival, !LanOn, LanOn ? 0 : CpuLevel, _phase == Phase.Playing ? _myTurn : null);

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        s.Rotor.Children.Add(new Rectangle { Width = 18, Height = 5, Fill = Art.Brush(ShipColor), RadiusX = 2.5, RadiusY = 2.5, [Canvas.LeftProperty] = -9.0, [Canvas.TopProperty] = 1.0 });
        s.Rotor.Children.Add(Art.Circle(3, -4, 3, Art.Brush(HitColor)));
        return s;
    }

    public override HudInfo Hud => new(
        $"{_fleet.ShipsLeft}–{EnemyShipsLeft}",
        _phase switch
        {
            Phase.Setup when _meReady => L.F("Waiting for {0} to get ready…", Rival),
            Phase.Setup => L.T("Click your grid to move your ships · click the enemy grid to start"),
            Phase.Over => L.T("Game over · click a grid for a rematch"),
            _ => _myTurn ? L.T("Your shot · click the enemy grid · a hit shoots again") : L.F("{0} is aiming…", Rival),
        },
        LanOn ? L.F("Wins {0}", Host.Stats.Get("seabattle.wins"))
            : L.F("Wins {0} · CPU {1}", Host.Stats.Get("seabattle.wins"), L.T(LevelNames[CpuLevel - 1])));

    // ------------------------------------------------------------------ layout

    // Board coordinates: the grids are laid out from (0, 0) and drawn once at a size; _boardTr carries the whole board to
    // _origin, so a drag moves one transform. Screen() converts for hit shapes, the pointer, the grip and the effects.
    Rect MyGrid => new(0, _cell, _cell * N, _cell * N);
    Rect EnemyGrid => new(_cell * (N + 1.5), _cell, _cell * N, _cell * N);
    Rect Whole => new(-8, -4, _cell * (2 * N + 1.5) + 16, _cell * (N + 1) + 12);

    Vec2 Screen(Vec2 board) => board + _origin;
    Rect Screen(Rect board) => new(board.X + _origin.X, board.Y + _origin.Y, board.Width, board.Height);

    public override void Layout()
    {
        var a = Host.Arena;
        double cell = Math.Floor(Math.Min(Math.Min(a.Width * 0.8 / (2 * N + 1.5), a.Height * 0.7 / (N + 1)), 32));
        double w = cell * (2 * N + 1.5), h = cell * (N + 1);
        if (!_placed)
        {
            _placed = true;
            _origin = _handle.Saved() ?? new Vec2(a.Center.X - w / 2, a.Center.Y - h / 2);
        }
        _origin = new Vec2(Clamp(_origin.X, a.Left + 10, a.Right - w - 10), Clamp(_origin.Y, a.Top + 10, a.Bottom - h - 10));
        bool redraw = cell != _cell; // the grids are drawn again only at a new size; a move is the transform's
        _cell = cell;
        if (LanOn != _wasLan)
        {
            _wasLan = LanOn;
            _gameNo = 1;
            NewGame(); // which draws
            redraw = false;
        }
        _boardTr.X = _origin.X;
        _boardTr.Y = _origin.Y;
        _handle.Show(Screen(Whole));
        if (redraw) Draw();
    }

    /// <summary>Switched to: placed, and drawn again, as the labels (the language, the rival's name) may have changed meanwhile.</summary>
    public override void Activate()
    {
        Layout();
        Draw();
    }

    public override void Deactivate()
    {
        _handle.Cancel();
        _quiet = true; // shells land and ships sink without sounds or popups: the game is leaving the screen
        Anims.Finish();
        _quiet = false;
    }

    public override void PointerCancel() => _handle.Cancel();

    public override void PositionsReset() => _placed = false;

    void NewGame()
    {
        _fleet = SeaFleet.Random(Rng);
        _cpuFleet = LanOn ? null : SeaFleet.Random(Rng);
        _enemyFleet = null;
        _chart = new sbyte[N * N];
        _cpuChart = new sbyte[N * N];
        _phase = Phase.Setup;
        _meReady = _peerReady = _myTurn = _revealShown = false;
        _shotSeq = _lastAnswered = 0;
        _pendingSq = _lastShotAtMe = -1;
        _cpuIn = -1;
        _answers.Clear();
        Anims.Clear(); // the old game's shells and sinkings are dropped
        _air.Children.Clear();
        _flights = 0;
        _inFlight.Clear();
        Changed();
    }

    /// <summary>The grids need drawing again (a new fleet, a new game).</summary>
    void Changed()
    {
        Draw();
        Host.HudChanged();
        Host.Wake();
    }

    /// <summary>A shot went out or came in: the hints and the HUD follow now; the marks follow when the shell lands.</summary>
    void Touched()
    {
        DrawHints();
        Host.HudChanged();
        Host.Wake();
    }

    static int CellAt(Rect grid, double cell, Vec2 p)
    {
        if (!grid.Contains(p.ToPoint())) return -1;
        int c = (int)((p.X - grid.Left) / cell), r = (int)((p.Y - grid.Top) / cell);
        return Math.Clamp(r, 0, N - 1) * N + Math.Clamp(c, 0, N - 1);
    }

    // ------------------------------------------------------------------ input

    public override void CollectHitShapes(List<HitShape> into)
    {
        into.Add(HitShape.Box(Screen(Whole)));
        into.Add(_handle.Hit);
    }

    public override bool PointerDown(Vec2 p, bool right)
    {
        if (right || _handle.Contains(p))
        {
            _handle.Begin(p, _origin, anywhere: true); // the grip, or a right-drag anywhere on the grids
            return true;
        }
        int mine = CellAt(MyGrid, _cell, p - _origin), theirs = CellAt(EnemyGrid, _cell, p - _origin);
        switch (_phase)
        {
            case Phase.Over when mine >= 0 || theirs >= 0:
                _gameNo++;
                NewGame();
                if (LanOn) Host.Lan.Send($"nw|{_gameNo}");
                break;
            case Phase.Setup when mine >= 0 && !_meReady:
                _fleet = SeaFleet.Random(Rng);
                Host.Sound.Play("board", 0.3, 1.4);
                Changed();
                break;
            case Phase.Setup when theirs >= 0 && !_meReady:
                _meReady = true;
                if (LanOn) SendReady();
                if (!LanOn || _peerReady) Start();
                Changed();
                break;
            case Phase.Playing when theirs >= 0 && _myTurn && _pendingSq < 0 && _chart[theirs] == SeaChart.Unknown:
                Fire(theirs);
                break;
        }
        return false;
    }

    public override void PointerUp(Vec2 p) => _handle.End(_origin);

    public override void Summon(Vec2 p)
    {
        _origin = p - new Vec2(_cell * N, _cell * N / 2);
        Layout();
        _handle.Save(_origin);
    }

    // ------------------------------------------------------------------ play

    void Start()
    {
        _phase = Phase.Playing;
        _myTurn = IsHost;
        if (!_myTurn && !LanOn) _cpuIn = CpuDelay;
        Host.Sound.Play("score", 0.4, 0.8);
        Changed();
    }

    void Fire(int sq)
    {
        if (!LanOn)
        {
            ResolveMyShot(sq, _cpuFleet!.Shoot(sq));
            return;
        }
        _pendingSq = sq;
        _shotSeq++;
        SendShot();
        Touched();
    }

    /// <summary>Our shot at <paramref name="sq"/> came back as <paramref name="r"/>.</summary>
    void ResolveMyShot(int sq, ShotResult r)
    {
        _pendingSq = -1;
        SeaChart.Record(_chart, sq, r);
        if (r.Kind is ShotKind.Sunk or ShotKind.Win) Host.Stats.Add("seabattle.sunk");
        if (r.Kind == ShotKind.Win) GameOver(true);
        else if (r.Kind == ShotKind.Miss)
        {
            _myTurn = false;
            if (!LanOn) _cpuIn = CpuDelay;
        }
        Shell(atMe: false, sq, r);
        Touched();
    }

    /// <summary>The rival fired at <paramref name="sq"/>; returns the result for them.</summary>
    ShotResult TakeShot(int sq)
    {
        var r = _fleet.Shoot(sq);
        _lastShotAtMe = sq;
        if (r.Kind == ShotKind.Win) GameOver(false);
        else if (r.Kind == ShotKind.Miss) _myTurn = true;
        Shell(atMe: true, sq, r);
        Touched();
        return r;
    }

    /// <summary>A shell arcs from the shooter's waters to the square; what it did shows when it lands.</summary>
    void Shell(bool atMe, int sq, ShotResult r)
    {
        var key = (atMe, sq);
        _inFlight.Add(key);
        _flights++;
        var s = new Sprite();
        s.Children.Add(Art.Circle(0, 0, _cell * 0.17, Art.Brush("#2A2F3A"), Art.Brush(Foam), 1.2));
        _air.Children.Add(s);
        Host.Sound.Play("whoosh", 0.3, 1.3);
        Anims.Add(Flight, k =>
        {
            Vec2 a = atMe ? new Vec2(EnemyGrid.Left, EnemyGrid.Center.Y) : new Vec2(MyGrid.Right, MyGrid.Center.Y);
            Vec2 b = atMe ? MyCenter(sq) : EnemyCenter(sq);
            var p = a + (b - a) * k;
            p.Y -= _cell * 2.5 * Ease.Pulse(k);
            s.Set(p);
            s.Scale = 1 + 0.8 * Ease.Pulse(k);
        }, Ease.InOutQuad, () =>
        {
            _air.Children.Remove(s);
            _flights--;
            _inFlight.Remove(key);
            Land(atMe, sq, r);
        });
        Host.Wake();
    }

    /// <summary>The shell landed: its mark appears, a sunk ship darkens square by square, and the last one ends the game.</summary>
    void Land(bool atMe, int sq, ShotResult r)
    {
        ShotFx(Screen(atMe ? MyCenter(sq) : EnemyCenter(sq)), r, byMe: !atMe);
        SetMark(atMe, sq, r.Kind == ShotKind.Miss ? SeaChart.Miss : SeaChart.Hit, pop: true);
        double t = 0;
        if (r.Kind is ShotKind.Sunk or ShotKind.Win)
        {
            foreach (int c in r.Ship.OrderBy(c => Math.Abs(c - sq))) // outward from the square that sank it
            {
                int cell = c;
                Anims.After(t, () => SetMark(atMe, cell, SeaChart.Sunk, fade: true));
                t += DarkenStep;
            }
            Anims.After(t + 0.05, () => SyncMarks()); // the empty water around it
        }
        if (r.Kind == ShotKind.Win)
            Anims.After(t + 0.3, () =>
            {
                GameOverFx(!atMe);
                Reveal();
            });
        else if (_phase == Phase.Over && _flights == 0) Reveal();
    }

    void ShotFx(Vec2 at, ShotResult r, bool byMe)
    {
        if (_quiet) return;
        switch (r.Kind)
        {
            case ShotKind.Miss:
                Host.Sound.Play("splash", 0.35, 1.2);
                Host.Fx.Burst(at, new[] { Foam, Grid }, 8, 150, 520, 3.5, 0.45);
                break;
            case ShotKind.Hit:
                Host.Sound.Play("board", 0.7, 0.7);
                Host.Fx.Burst(at, new[] { HitColor, Ember, Gold }, 14, 260, 300, 4, 0.5);
                break;
            default:
                Host.Sound.Play("score", 0.6);
                Host.Fx.Burst(at, new[] { HitColor, Ember, Gold, Colors.White }, 24, 360, 400, 5, 0.7);
                Host.Fx.Popup(at + new Vec2(0, -_cell * 1.5), byMe ? L.T("SUNK!") : L.T("Ship lost"), byMe ? Gold : HitColor, 24, 1.2);
                break;
        }
    }

    /// <summary>The outcome: the score, the CPU level and what the popup will say once the last ship has gone down.</summary>
    void GameOver(bool won)
    {
        _phase = Phase.Over;
        _myTurn = false;
        if (won)
        {
            Host.Stats.Add("seabattle.wins");
            if (LanOn)
            {
                Host.Stats.Add("seabattle.lanwins");
                Host.Stats.Add("lan.wins");
            }
            _endSub = L.F("vs {0}", Rival);
            if (!LanOn && !_demo) // demo games don't move the player's level
            {
                _lossStreak = 0;
                if (CpuLevel < LevelNames.Length)
                {
                    CpuLevel++;
                    _endSub = L.F("the CPU moves up to {0}", L.T(LevelNames[CpuLevel - 1]));
                }
            }
        }
        else
        {
            _endSub = L.T("click a grid for a rematch");
            if (!LanOn && !_demo && ++_lossStreak >= 2 && CpuLevel > 1)
            {
                _lossStreak = 0;
                CpuLevel--;
                _endSub = L.F("the CPU goes easier: {0} · click a grid for a rematch", L.T(LevelNames[CpuLevel - 1]));
            }
        }
        if (!LanOn) _enemyFleet = _cpuFleet;
    }

    void GameOverFx(bool won)
    {
        if (_quiet) return;
        var b = Screen(Whole);
        var at = new Vec2(b.Center.X, b.Top + b.Height * 0.3);
        if (won)
        {
            Host.Fx.Popup(at, L.T("YOU WIN!"), Gold, 42, 2.6, _endSub);
            Host.Fx.Burst(at, new[] { Gold, Colors.White, HitColor }, 44, 540, 700, 7, 1.1);
            Host.Sound.Play("best", 0.8);
        }
        else
        {
            Host.Fx.Popup(at, L.F("{0} WINS", Rival), Colors.White, 38, 2.4, _endSub);
            Host.Sound.Play("buzzer", 0.45);
        }
    }

    public override bool Update(double dt)
    {
        if (_handle.Dragging)
        {
            var whole = Whole;
            _origin = _handle.Move(Host.Pointer, new Size(whole.Width, whole.Height));
            Layout();
        }
        if (LanOn) Network(dt);
        else if (_cpuIn > 0 && (_cpuIn -= dt) <= 0)
        {
            _cpuIn = -1;
            if (_phase == Phase.Playing && !_myTurn)
            {
                int sq = SeaChart.NextShot(_cpuChart, Rng, CpuLevel);
                var r = TakeShot(sq);
                SeaChart.Record(_cpuChart, sq, r);
                if (r.Kind is ShotKind.Hit or ShotKind.Sunk) _cpuIn = CpuDelay; // a hit shoots again
            }
        }
        if (_demo) DemoStep();
        bool animating = Anims.Update(dt);
        return _handle.Dragging || _cpuIn > 0 || LanOn || _demo || animating;
    }

    public override void DemoTick() => _demo = true;

    void DemoStep()
    {
        if (_phase == Phase.Setup && !_meReady) PointerDown(Screen(new Vec2(EnemyGrid.Center.X, EnemyGrid.Center.Y)), false);
        else if (_phase == Phase.Playing && _myTurn && _pendingSq < 0 && _cpuIn < 0 && _flights == 0)
            Fire(SeaChart.NextShot(_chart, Rng));
    }

    // ------------------------------------------------------------------ LAN
    // "rd|game"                      ready (repeated while in setup)
    // "sh|game|seq|square"           a shot, re-sent until its answer arrives
    // "rs|game|seq|square|kind|ship" the answer (kind m/h/s/w; ship = sunk squares)
    // "nw|game"                      rematch
    // "fl|game|ships"                our fleet, revealed once the game is over

    void Network(double dt)
    {
        while (Host.Lan.TryReceive(out var msg))
        {
            var f = msg.Split('|');
            if (f.Length < 2 || !int.TryParse(f[1], out int game)) continue;
            if (game > _gameNo && f[0] is "rd" or "nw")
            {
                _gameNo = game; // the rival started a rematch
                NewGame();
            }
            if (game != _gameNo) continue;
            switch (f[0])
            {
                case "rd":
                    _peerReady = true;
                    if (_meReady && _phase == Phase.Setup) Start();
                    break;
                case "sh" when f.Length == 4 && int.TryParse(f[2], out int seq) && int.TryParse(f[3], out int sq) && sq is >= 0 and < N * N:
                    if (seq == _lastAnswered + 1 && _phase == Phase.Playing && !_myTurn)
                    {
                        var r = TakeShot(sq);
                        _lastAnswered = seq;
                        _answers[seq] = $"rs|{_gameNo}|{seq}|{sq}|{"mhsw"[(int)r.Kind]}|{string.Join(",", r.Ship)}";
                    }
                    if (_answers.TryGetValue(seq, out var answer)) Host.Lan.Send(answer);
                    break;
                case "rs" when f.Length == 6 && int.TryParse(f[2], out int seq) && seq == _shotSeq && int.TryParse(f[3], out int sq) && sq == _pendingSq
                               && f[4].Length == 1 && "mhsw".IndexOf(f[4][0]) is >= 0 and var kindIndex && TryParseSquares(f[5], out var ship):
                    ResolveMyShot(sq, new ShotResult((ShotKind)kindIndex, ship)); // anything malformed is dropped: the shot is re-sent anyway
                    break;
                case "fl" when f.Length == 3 && _phase == Phase.Over && _enemyFleet == null:
                    _enemyFleet = SeaFleet.Decode(f[2]);
                    if (_flights == 0) Reveal(); // otherwise the landing shell reveals it, once the last ship has gone down
                    break;
            }
        }
        if ((_resendT += dt) < ResendEvery) return;
        _resendT = 0;
        if (_phase == Phase.Setup && _meReady || _phase == Phase.Playing) SendReady(); // late joiners still learn we're ready
        if (_pendingSq >= 0) SendShot();
        if (_phase == Phase.Over) Host.Lan.Send($"fl|{_gameNo}|{_fleet.Encode()}");
    }

    void SendReady() => Host.Lan.Send($"rd|{_gameNo}");

    /// <summary>"3,4,5" to squares on the board; false for anything that is not a list of squares (the peer's text is untrusted).</summary>
    static bool TryParseSquares(string text, out int[] squares)
    {
        squares = Array.Empty<int>();
        if (text.Length == 0) return true;
        var parts = text.Split(',');
        var result = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], out result[i]) || result[i] is < 0 or >= N * N) return false;
        squares = result;
        return true;
    }

    void SendShot() => Host.Lan.Send($"sh|{_gameNo}|{_shotSeq}|{_pendingSq}");

    // ------------------------------------------------------------------ visuals

    Vec2 MyCenter(int sq) => new(MyGrid.Left + (sq % N + 0.5) * _cell, MyGrid.Top + (sq / N + 0.5) * _cell);

    Vec2 EnemyCenter(int sq) => new(EnemyGrid.Left + (sq % N + 0.5) * _cell, EnemyGrid.Top + (sq / N + 0.5) * _cell);

    /// <summary>Draws everything afresh at the current size, in board coordinates; marks and the reveal appear at once.</summary>
    void Draw()
    {
        _canvas.Children.Clear();
        var whole = Whole;
        _canvas.Children.Add(Place(new Border { Width = whole.Width, Height = whole.Height, CornerRadius = new CornerRadius(8), Background = Art.Brush(225, 16, 24, 38) }, whole.Left, whole.Top));
        Label(L.T("Your fleet"), MyGrid);
        Label(LanOn ? Host.Lan.PeerName : L.T("Enemy waters"), EnemyGrid);
        DrawGrid(MyGrid);
        DrawGrid(EnemyGrid);

        foreach (var ship in _fleet.Ships)
        {
            var cells = ship.Select(MyCenter).ToList();
            double left = cells.Min(c => c.X), top = cells.Min(c => c.Y), right = cells.Max(c => c.X), bottom = cells.Max(c => c.Y);
            _canvas.Children.Add(Place(new Rectangle
            {
                Width = right - left + _cell * 0.7, Height = bottom - top + _cell * 0.7, RadiusX = _cell * 0.3, RadiusY = _cell * 0.3,
                Fill = Art.Brush(ShipColor), Stroke = Art.Brush("#2A2F3A"), StrokeThickness = 1.5,
            }, left - _cell * 0.35, top - _cell * 0.35));
        }
        SyncMarks(force: true);
        DrawReveal(animate: false);
        DrawHints();
    }

    void DrawGrid(Rect g)
    {
        _canvas.Children.Add(Place(new Rectangle { Width = g.Width, Height = g.Height, Fill = Art.Brush(Water) }, g.Left, g.Top));
        for (int i = 0; i <= N; i++)
        {
            _canvas.Children.Add(new Line { StartPoint = new Point(g.Left + i * _cell, g.Top), EndPoint = new Point(g.Left + i * _cell, g.Bottom), Stroke = Art.Brush(Grid), StrokeThickness = 1 });
            _canvas.Children.Add(new Line { StartPoint = new Point(g.Left, g.Top + i * _cell), EndPoint = new Point(g.Right, g.Top + i * _cell), Stroke = Art.Brush(Grid), StrokeThickness = 1 });
        }
    }

    void Label(string text, Rect grid) => _canvas.Children.Add(Place(new TextBlock
    {
        Text = text, FontFamily = Fx.Font, FontSize = Math.Max(11, _cell * 0.5), FontWeight = FontWeight.Bold, Foreground = Brushes.White,
        Width = grid.Width, TextAlignment = TextAlignment.Center,
    }, grid.Left, grid.Top - _cell * 0.95));

    /// <summary>Whose turn it is (a gold frame on the enemy grid), the shot awaiting its answer and the last shot at me.</summary>
    void DrawHints()
    {
        _hints.Children.Clear();
        if (_lastShotAtMe >= 0) _hints.Children.Add(Ring(MyCenter(_lastShotAtMe), _cell * 0.46, 1.5));
        if (_pendingSq >= 0)
        {
            var c = EnemyCenter(_pendingSq);
            _hints.Children.Add(Ring(c, _cell * 0.32, 2));
            _hints.Children.Add(Ring(c, _cell * 0.46, 1.5));
        }
        if (_phase == Phase.Playing && _myTurn)
            _hints.Children.Add(Place(new Rectangle { Width = EnemyGrid.Width + 6, Height = EnemyGrid.Height + 6, Stroke = Art.Brush(Gold), StrokeThickness = 2 }, EnemyGrid.Left - 3, EnemyGrid.Top - 3));
    }

    static Ellipse Ring(Vec2 c, double r, double thick) => Art.Circle(c.X, c.Y, r, null, Art.Brush(Gold), thick);

    /// <summary>The enemy's fleet surfaces once the game is over, ship by ship.</summary>
    void Reveal()
    {
        if (_revealShown || _enemyFleet == null) return;
        _revealShown = true;
        DrawReveal(animate: !Fx.ReducedMotion);
        Host.Wake();
    }

    void DrawReveal(bool animate)
    {
        _reveal.Children.Clear();
        if (_enemyFleet == null || !_revealShown) return;
        double t = 0.1;
        foreach (var ship in _enemyFleet.Ships)
        {
            foreach (int sq in ship.Where(sq => _chart[sq] == SeaChart.Unknown))
            {
                var s = new Sprite();
                s.Children.Add(Art.Circle(0, 0, _cell * 0.3, Art.Brush(ShipColor)));
                s.Set(EnemyCenter(sq));
                _reveal.Children.Add(s);
                if (!animate) continue;
                s.Scale = 0;
                Anims.Add(0.3, k => s.Scale = k, Ease.OutBack, delay: t);
                t += 0.06;
            }
            t += 0.12;
        }
    }

    /// <summary>What my grid shows at a square: nothing, a miss, a hit or a square of a sunk ship.</summary>
    sbyte MineState(int sq)
    {
        if (!_fleet.WasShot(sq)) return SeaChart.Unknown;
        int ship = _fleet.ShipAt(sq);
        if (ship < 0) return SeaChart.Miss;
        return _fleet.Ships[ship].All(_fleet.WasShot) ? SeaChart.Sunk : SeaChart.Hit;
    }

    /// <summary>Brings every mark up to date with the game at once, except where a shell is still in the air.</summary>
    void SyncMarks(bool force = false)
    {
        if (force)
        {
            _marks.Children.Clear();
            Array.Clear(_myMarks);
            Array.Clear(_enemyMarks);
            Array.Clear(_shownMine);
            Array.Clear(_shownEnemy);
        }
        for (int sq = 0; sq < N * N; sq++)
        {
            if (!_inFlight.Contains((true, sq))) SetMark(true, sq, MineState(sq));
            if (!_inFlight.Contains((false, sq))) SetMark(false, sq, _chart[sq]);
        }
    }

    /// <summary>Shows <paramref name="state"/> at a square: popping in (a fresh shot) or fading in over the old mark (a ship darkening).</summary>
    void SetMark(bool atMe, int sq, sbyte state, bool pop = false, bool fade = false)
    {
        var sprites = atMe ? _myMarks : _enemyMarks;
        var shown = atMe ? _shownMine : _shownEnemy;
        if (shown[sq] == state) return;
        var old = sprites[sq];
        shown[sq] = state;
        sprites[sq] = null;
        if (state == SeaChart.Unknown)
        {
            if (old != null) _marks.Children.Remove(old);
            return;
        }
        var s = MarkSprite(state);
        s.Set(atMe ? MyCenter(sq) : EnemyCenter(sq));
        _marks.Children.Add(s);
        sprites[sq] = s;
        if (fade && old != null)
        {
            s.Opacity = 0;
            Anims.Add(0.22, k => s.Opacity = k, Ease.OutQuad, () => _marks.Children.Remove(old));
            return;
        }
        if (old != null) _marks.Children.Remove(old);
        if (!pop) return;
        s.Scale = 0.3;
        Anims.Add(0.26, k => s.Scale = 0.3 + 0.7 * k, Ease.OutBack);
    }

    /// <summary>A miss is a small dot, a hit a red cross, a sunk square a dark red block; known-empty water a faint dot.</summary>
    Sprite MarkSprite(sbyte state)
    {
        var s = new Sprite();
        double r = _cell * 0.32;
        switch (state)
        {
            case SeaChart.Miss:
                s.Children.Add(Art.Circle(0, 0, _cell * 0.1, Art.Brush(Foam)));
                break;
            case SeaChart.Empty:
                s.Children.Add(Art.Circle(0, 0, _cell * 0.07, Art.Brush(120, 216, 230, 245)));
                break;
            case SeaChart.Sunk:
                s.Children.Add(Place(new Rectangle { Width = _cell - 2, Height = _cell - 2, Fill = Art.Brush("#7A1C1C") }, -_cell / 2 + 1, -_cell / 2 + 1));
                goto case SeaChart.Hit;
            case SeaChart.Hit:
                foreach (int k in new[] { -1, 1 })
                    s.Children.Add(new Line { StartPoint = new Point(-r, -r * k), EndPoint = new Point(r, r * k), Stroke = Art.Brush(HitColor), StrokeThickness = _cell * 0.13, StrokeLineCap = PenLineCap.Round });
                break;
        }
        return s;
    }

    static Control Place(Control c, double x, double y)
    {
        Canvas.SetLeft(c, x);
        Canvas.SetTop(c, y);
        c.IsHitTestVisible = false;
        return c;
    }
}
