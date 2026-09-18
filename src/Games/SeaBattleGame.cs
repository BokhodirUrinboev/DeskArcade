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
/// A hit earns another shot. Over the LAN each fleet stays on its own PC: only shots and their results
/// cross the link, and a shot is re-sent until its answer arrives. The host fires first.
/// </summary>
public sealed class SeaBattleGame : MiniGame
{
    const int N = SeaFleet.N;
    const double CpuDelay = 0.7, ResendEvery = 0.4;

    static readonly Color Water = Color.FromRgb(28, 74, 128), Grid = Color.FromRgb(90, 140, 200);
    static readonly Color ShipColor = Color.FromRgb(160, 170, 185), HitColor = Color.FromRgb(240, 70, 60), Gold = Color.FromRgb(255, 209, 102);

    enum Phase { Setup, Playing, Over }

    readonly Canvas _canvas = new() { IsHitTestVisible = false };
    SeaFleet _fleet = SeaFleet.Random(Rng);
    SeaFleet? _cpuFleet = SeaFleet.Random(Rng), _enemyFleet; // the CPU's fleet (null over the LAN); the rival's once revealed
    sbyte[] _chart = new sbyte[N * N], _cpuChart = new sbyte[N * N];
    Phase _phase;
    bool _myTurn, _placed, _dragging, _demo, _wasLan, _meReady, _peerReady;
    Vec2 _origin, _dragOffset;
    double _cell, _cpuIn = -1, _resendT;
    int _gameNo = 1, _shotSeq, _lastAnswered, _pendingSq = -1, _lastShotAtMe = -1;
    readonly Dictionary<int, string> _answers = new(); // LAN: our answers by shot number, for re-sent shots

    public SeaBattleGame(IGameHost host) : base(host) => Layer.Children.Add(_canvas);

    public override string Id => "seabattle";
    public override string Title => "Sea Battle";
    public override bool SupportsLan => true;

    bool LanOn => Host.Lan.Connected;
    bool IsHost => !LanOn || Host.Lan.Role == LanRole.Host;
    string Rival => LanOn ? Host.Lan.PeerName : L.T("CPU");
    int EnemyShipsLeft => SeaFleet.Sizes.Length - SunkShips(_chart);

    /// <summary>Sunk ships on a chart: ships never touch, so each group of sunk squares is one ship.</summary>
    static int SunkShips(sbyte[] chart)
    {
        var seen = new HashSet<int>();
        int count = 0;
        for (int sq = 0; sq < N * N; sq++)
        {
            if (chart[sq] != SeaChart.Sunk || !seen.Add(sq)) continue;
            count++;
            var stack = new Stack<int>(new[] { sq });
            while (stack.Count > 0)
            {
                int c = stack.Pop();
                foreach (int d in new[] { -1, 1, -N, N })
                {
                    int o = c + d;
                    if (o < 0 || o >= N * N || (d is -1 or 1 && o / N != c / N)) continue;
                    if (chart[o] == SeaChart.Sunk && seen.Add(o)) stack.Push(o);
                }
            }
        }
        return count;
    }

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
        L.F("Wins {0}", Host.Stats.Get("seabattle.wins")));

    // ------------------------------------------------------------------ layout

    Rect MyGrid => new(_origin.X, _origin.Y + _cell, _cell * N, _cell * N);
    Rect EnemyGrid => new(_origin.X + _cell * (N + 1.5), _origin.Y + _cell, _cell * N, _cell * N);
    Rect Whole => new(_origin.X - 8, _origin.Y - 4, _cell * (2 * N + 1.5) + 16, _cell * (N + 1) + 12);

    public override void Layout()
    {
        var a = Host.Arena;
        _cell = Math.Floor(Math.Min(Math.Min(a.Width * 0.8 / (2 * N + 1.5), a.Height * 0.7 / (N + 1)), 32));
        double w = _cell * (2 * N + 1.5), h = _cell * (N + 1);
        if (!_placed)
        {
            _placed = true;
            _origin = new Vec2(a.Center.X - w / 2, a.Center.Y - h / 2);
        }
        _origin = new Vec2(Clamp(_origin.X, a.Left + 10, a.Right - w - 10), Clamp(_origin.Y, a.Top + 10, a.Bottom - h - 10));
        if (LanOn != _wasLan)
        {
            _wasLan = LanOn;
            _gameNo = 1;
            NewGame();
        }
        Draw();
    }

    public override void Deactivate() => _dragging = false;

    void NewGame()
    {
        _fleet = SeaFleet.Random(Rng);
        _cpuFleet = LanOn ? null : SeaFleet.Random(Rng);
        _enemyFleet = null;
        _chart = new sbyte[N * N];
        _cpuChart = new sbyte[N * N];
        _phase = Phase.Setup;
        _meReady = _peerReady = _myTurn = false;
        _shotSeq = _lastAnswered = 0;
        _pendingSq = _lastShotAtMe = -1;
        _cpuIn = -1;
        _answers.Clear();
        Changed();
    }

    void Changed()
    {
        Draw();
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

    public override void CollectHitShapes(List<HitShape> into) => into.Add(HitShape.Box(Whole));

    public override bool PointerDown(Vec2 p, bool right)
    {
        if (right)
        {
            _dragging = true;
            _dragOffset = _origin - p;
            return true;
        }
        int mine = CellAt(MyGrid, _cell, p), theirs = CellAt(EnemyGrid, _cell, p);
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

    public override void PointerUp(Vec2 p) => _dragging = false;

    public override void Summon(Vec2 p)
    {
        _origin = p - new Vec2(_cell * N, _cell * N / 2);
        Layout();
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
        Changed();
    }

    /// <summary>Our shot at <paramref name="sq"/> came back as <paramref name="r"/>.</summary>
    void ResolveMyShot(int sq, ShotResult r)
    {
        _pendingSq = -1;
        SeaChart.Record(_chart, sq, r);
        ShotFx(EnemyCenter(sq), r, byMe: true);
        if (r.Kind == ShotKind.Win) GameOver(true);
        else if (r.Kind == ShotKind.Miss)
        {
            _myTurn = false;
            if (!LanOn) _cpuIn = CpuDelay;
        }
        Changed();
    }

    /// <summary>The rival fired at <paramref name="sq"/>; returns the result for them.</summary>
    ShotResult TakeShot(int sq)
    {
        var r = _fleet.Shoot(sq);
        _lastShotAtMe = sq;
        ShotFx(MyCenter(sq), r, byMe: false);
        if (r.Kind == ShotKind.Win) GameOver(false);
        else if (r.Kind == ShotKind.Miss) _myTurn = true;
        Changed();
        return r;
    }

    void ShotFx(Vec2 at, ShotResult r, bool byMe)
    {
        switch (r.Kind)
        {
            case ShotKind.Miss:
                Host.Sound.Play("rim", 0.3, 1.6);
                break;
            case ShotKind.Hit:
                Host.Sound.Play("board", 0.7, 0.7);
                Host.Fx.Burst(at, new[] { HitColor, Gold }, 12, 260, 300, 4, 0.5);
                break;
            default:
                Host.Sound.Play("score", 0.6);
                Host.Fx.Burst(at, new[] { HitColor, Gold, Colors.White }, 24, 360, 400, 5, 0.7);
                Host.Fx.Popup(at + new Vec2(0, -_cell * 1.5), byMe ? L.T("SUNK!") : L.T("Ship lost"), byMe ? Gold : HitColor, 24, 1.2);
                if (byMe) Host.Stats.Add("seabattle.sunk");
                break;
        }
    }

    void GameOver(bool won)
    {
        _phase = Phase.Over;
        var b = Whole;
        var at = new Vec2(b.Center.X, b.Top + b.Height * 0.3);
        if (won)
        {
            Host.Stats.Add("seabattle.wins");
            if (LanOn) Host.Stats.Add("seabattle.lanwins");
            Host.Fx.Popup(at, L.T("YOU WIN!"), Gold, 42, 2.6, L.F("vs {0}", Rival));
            Host.Fx.Burst(at, new[] { Gold, Colors.White, HitColor }, 44, 540, 700, 7, 1.1);
            Host.Sound.Play("best", 0.8);
        }
        else
        {
            Host.Fx.Popup(at, L.F("{0} WINS", Rival), Colors.White, 38, 2.4, L.T("click a grid for a rematch"));
            Host.Sound.Play("buzzer", 0.45);
        }
        if (!LanOn) _enemyFleet = _cpuFleet;
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
            if (_phase == Phase.Playing && !_myTurn)
            {
                int sq = SeaChart.NextShot(_cpuChart, Rng);
                var r = TakeShot(sq);
                SeaChart.Record(_cpuChart, sq, r);
                if (r.Kind is ShotKind.Hit or ShotKind.Sunk) _cpuIn = CpuDelay; // a hit shoots again
            }
        }
        if (_demo) DemoStep();
        return _dragging || _cpuIn > 0 || LanOn || _demo;
    }

    public override void DemoTick() => _demo = true;

    void DemoStep()
    {
        if (_phase == Phase.Setup && !_meReady) PointerDown(new Vec2(EnemyGrid.Center.X, EnemyGrid.Center.Y), false);
        else if (_phase == Phase.Playing && _myTurn && _pendingSq < 0 && _cpuIn < 0)
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
                case "rs" when f.Length == 6 && int.TryParse(f[2], out int seq) && seq == _shotSeq && int.TryParse(f[3], out int sq) && sq == _pendingSq:
                    var kind = (ShotKind)"mhsw".IndexOf(f[4][0]);
                    var ship = f[5].Length == 0 ? Array.Empty<int>() : f[5].Split(',').Select(int.Parse).ToArray();
                    ResolveMyShot(sq, new ShotResult(kind, ship));
                    break;
                case "fl" when f.Length == 3 && _phase == Phase.Over && _enemyFleet == null:
                    _enemyFleet = SeaFleet.Decode(f[2]);
                    Changed();
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

    void SendShot() => Host.Lan.Send($"sh|{_gameNo}|{_shotSeq}|{_pendingSq}");

    // ------------------------------------------------------------------ visuals

    Vec2 MyCenter(int sq) => new(MyGrid.Left + (sq % N + 0.5) * _cell, MyGrid.Top + (sq / N + 0.5) * _cell);

    Vec2 EnemyCenter(int sq) => new(EnemyGrid.Left + (sq % N + 0.5) * _cell, EnemyGrid.Top + (sq / N + 0.5) * _cell);

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
            bool sunk = ship.All(_fleet.WasShot);
            _canvas.Children.Add(Place(new Rectangle
            {
                Width = right - left + _cell * 0.7, Height = bottom - top + _cell * 0.7, RadiusX = _cell * 0.3, RadiusY = _cell * 0.3,
                Fill = Art.Brush(sunk ? Art.Blend(ShipColor, Colors.Black, 0.5) : ShipColor), Stroke = Art.Brush("#2A2F3A"), StrokeThickness = 1.5,
            }, left - _cell * 0.35, top - _cell * 0.35));
        }
        for (int sq = 0; sq < N * N; sq++)
            if (_fleet.WasShot(sq)) Mark(MyCenter(sq), _fleet.ShipAt(sq) >= 0 ? SeaChart.Hit : SeaChart.Miss, sq == _lastShotAtMe);

        if (_enemyFleet != null)
            foreach (int sq in _enemyFleet.Ships.SelectMany(s => s).Where(sq => _chart[sq] == SeaChart.Unknown))
                _canvas.Children.Add(Art.Circle(EnemyCenter(sq).X, EnemyCenter(sq).Y, _cell * 0.3, Art.Brush(ShipColor)));
        for (int sq = 0; sq < N * N; sq++)
            if (_chart[sq] != SeaChart.Unknown) Mark(EnemyCenter(sq), _chart[sq], false);
        if (_pendingSq >= 0) Mark(EnemyCenter(_pendingSq), -1, true);
        if (_phase == Phase.Playing && _myTurn)
            _canvas.Children.Add(Place(new Rectangle { Width = EnemyGrid.Width + 6, Height = EnemyGrid.Height + 6, Stroke = Art.Brush(Gold), StrokeThickness = 2 }, EnemyGrid.Left - 3, EnemyGrid.Top - 3));
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

    /// <summary>A miss is a small dot, a hit a red cross, a sunk square a dark red block; −1 is a shot in flight.</summary>
    void Mark(Vec2 c, int state, bool fresh)
    {
        double r = _cell * 0.32;
        switch (state)
        {
            case SeaChart.Miss:
                _canvas.Children.Add(Art.Circle(c.X, c.Y, _cell * 0.1, Art.Brush("#D8E6F5")));
                break;
            case SeaChart.Empty:
                _canvas.Children.Add(Art.Circle(c.X, c.Y, _cell * 0.07, Art.Brush(120, 216, 230, 245)));
                break;
            case SeaChart.Sunk:
                _canvas.Children.Add(Place(new Rectangle { Width = _cell - 2, Height = _cell - 2, Fill = Art.Brush("#7A1C1C") }, c.X - _cell / 2 + 1, c.Y - _cell / 2 + 1));
                goto case SeaChart.Hit;
            case SeaChart.Hit:
                foreach (int s in new[] { -1, 1 })
                    _canvas.Children.Add(new Line { StartPoint = new Point(c.X - r, c.Y - r * s), EndPoint = new Point(c.X + r, c.Y + r * s), Stroke = Art.Brush(HitColor), StrokeThickness = _cell * 0.13, StrokeLineCap = PenLineCap.Round });
                break;
            default:
                _canvas.Children.Add(Art.Circle(c.X, c.Y, r, null, Art.Brush(Gold), 2));
                break;
        }
        if (fresh) _canvas.Children.Add(Art.Circle(c.X, c.Y, _cell * 0.46, null, Art.Brush(Gold), 1.5));
    }

    static Control Place(Control c, double x, double y)
    {
        Canvas.SetLeft(c, x);
        Canvas.SetTop(c, y);
        c.IsHitTestVisible = false;
        return c;
    }
}
