using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using DeskArcade.Engine;
using DeskArcade.Net;

namespace DeskArcade.Games;

/// <summary>
/// Backgammon (see <see cref="BackgammonRules"/>) on a wooden board over the desktop, your home board at the bottom
/// right. Click ROLL, then a checker and the point it goes to (it goes at once when there is only one place); a checker
/// on the bar comes in first, and the tray on the right takes the ones you bear off. Against the computer at four
/// levels (it moves up after two wins in a row and down after two losses) or a co-worker over the LAN, who sees the
/// board from their side; the games take turns at opening. The grip (or a right-drag) moves the board.
/// </summary>
public sealed class BackgammonGame : MiniGame
{
    const double PointW = 34, R = 15, PointH = R * 10, MidH = 44, Frame = 14, BarW = 34, TrayW = 44;
    const double FieldW = PointW * 12 + BarW;
    const double BoardW = Frame * 3 + FieldW + TrayW, BoardH = Frame * 2 + PointH * 2 + MidH;
    const double StepTime = 0.45, ThinkTime = 0.7, SlideTime = 0.25;
    static readonly Color Ivory = Color.FromRgb(243, 238, 226), Ebony = Color.FromRgb(43, 43, 51);
    static readonly Color LightPoint = Color.FromRgb(233, 216, 180), DarkPoint = Color.FromRgb(168, 65, 47);

    readonly Canvas _board = new();
    readonly ScaleTransform _zoom = new(1, 1);
    readonly Canvas _checkers = new() { IsHitTestVisible = false };
    readonly Canvas _marks = new() { IsHitTestVisible = false };
    readonly Canvas _dice = new() { IsHitTestVisible = false };
    readonly DragHandle _handle;
    readonly List<string> _delivered = new();
    readonly Queue<BgStep> _cpuSteps = new();
    readonly Dictionary<int, Control> _tops = new(); // the top checker drawn on each spot (see SpotKey)
    BackgammonRules _rules = new();
    DuelChannel _duel = null!;
    Vec2 _origin;
    double _scale = 1;
    bool _placed, _busy, _demo, _rematchAsked;
    double _think = -1, _stepT = -1;
    int _session = -1, _gameNo, _winStreak, _lossStreak, _selected = -1;
    (int A, int B) _roll;
    Rect _rollRect;

    public BackgammonGame(IGameHost host) : base(host)
    {
        _board.RenderTransformOrigin = RelativePoint.TopLeft;
        _board.RenderTransform = _zoom;
        _handle = new DragHandle(host, Id, Title);
        Layer.Children.Add(_board);
        Layer.Children.Add(_handle.Visual);
        _duel = new DuelChannel("bg", Host.Lan.Send);
        DrawBoard();
        NewGame();
    }

    public override string Id => "backgammon";
    public override string Title => "Backgammon";

    bool LanOn => Host.Lan.Connected;
    bool IsGuest => LanOn && Host.Lan.Role == LanRole.Guest;
    int Me => IsGuest ? 1 : 0;
    bool MyTurn => !_rules.Over && _rules.Turn == Me;
    string Rival => LanOn ? Host.Lan.PeerName : L.T("CPU");

    public override bool SupportsLan => true;
    public override bool HasCpuLevels => true;
    protected override int DefaultCpuLevel => 1;
    public override Opponent? Opponent => new(Rival, !LanOn, LanOn ? 0 : CpuLevel, _rules.Over ? null : MyTurn);

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        s.Rotor.Children.Add(Art.At(new Rectangle { Width = 22, Height = 16, RadiusX = 2, RadiusY = 2, Fill = Art.Brush("#6B4423") }, -11, -8));
        s.Rotor.Children.Add(Art.PathOf("M-9,-6 L-6,2 L-3,-6 Z M3,6 L6,-2 L9,6 Z", Art.Brush(DarkPoint)));
        s.Rotor.Children.Add(Art.Circle(-6, 4, 3, Art.Brush(Ivory), Art.Brush("#8C826E"), 0.8));
        s.Rotor.Children.Add(Art.Circle(6, -4, 3, Art.Brush(Ebony), Art.Brush("#111"), 0.8));
        return s;
    }

    public override HudInfo Hud
    {
        get
        {
            string line = _rules.Over ? (IsGuest ? L.T("Game over · click the board to ask for a rematch") : L.T("Game over · click the board for a new game"))
                : !MyTurn ? L.F("{0}'s turn", Rival)
                : !_rules.Rolled ? L.F("Your turn · click ROLL · pips {0}–{1}", _rules.Pips(Me), _rules.Pips(1 - Me))
                : L.T("Your move · click a checker, then where it goes");
            return new HudInfo($"{_rules.BorneOff(Me)}–{_rules.BorneOff(1 - Me)}", line, L.F("Wins {0}", Host.Stats.Get("backgammon.wins")));
        }
    }

    // ------------------------------------------------------------------ geometry

    /// <summary>Where the checker <paramref name="k"/> from the edge of the viewer's point <paramref name="r"/> (1–24) sits.</summary>
    static Vec2 Slot(int r, int k)
    {
        k = Math.Min(k, 4);
        bool bottom = r <= 12;
        int col = bottom ? 12 - r : r - 13;
        double x = Frame + col * PointW + (col >= 6 ? BarW : 0) + PointW / 2;
        return bottom ? new Vec2(x, BoardH - Frame - R - k * R * 2) : new Vec2(x, Frame + R + k * R * 2);
    }

    /// <summary>The viewer's bar checkers stack up from the middle of the bar, the other side's down from it.</summary>
    static Vec2 BarSlot(bool viewer, int k)
    {
        double x = Frame + 6 * PointW + BarW / 2;
        k = Math.Min(k, 3);
        return viewer ? new Vec2(x, BoardH / 2 - MidH / 2 - R - k * R * 2) : new Vec2(x, BoardH / 2 + MidH / 2 + R + k * R * 2);
    }

    static Rect Tray => new(Frame * 2 + FieldW, Frame, TrayW, BoardH - Frame * 2);

    /// <summary>A spot's key: absolute points 0–23, then 100 + side for a bar, 200 + side for the borne-off checkers.</summary>
    static int SpotKey(int side, int r) => r == BackgammonRules.BarSpot ? 100 + side : r == BackgammonRules.OffSpot ? 200 + side : BackgammonRules.Abs(side, r);

    /// <summary><paramref name="side"/>'s point <paramref name="r"/> as the viewer's point number.</summary>
    int View(int side, int r) => side == Me ? r : 25 - r;

    Vec2 TopOf(int side, int r)
    {
        if (r == BackgammonRules.BarSpot) return BarSlot(side == Me, Math.Max(0, _rules.OnBar(side) - 1));
        if (r == BackgammonRules.OffSpot) return OffSlot(side, Math.Max(0, _rules.BorneOff(side) - 1));
        return Slot(View(side, r), Math.Max(0, _rules.Count(side, r) - 1));
    }

    static Vec2 OffSlot(bool viewer, int k)
    {
        var t = Tray;
        return viewer ? new Vec2(t.Center.X, t.Bottom - 6 - k * 9) : new Vec2(t.Center.X, t.Top + 6 + k * 9);
    }

    Vec2 OffSlot(int side, int k) => OffSlot(side == Me, k);

    /// <summary>The viewer's point (1–24), 25 for the bar or 0 for the tray under a board-local position; -1 for nothing.</summary>
    static int SpotAt(Point p)
    {
        if (Tray.Contains(p)) return BackgammonRules.OffSpot;
        double x = p.X - Frame;
        if (x < 0 || x >= FieldW || p.Y < Frame || p.Y > BoardH - Frame) return -1;
        if (x >= 6 * PointW && x < 6 * PointW + BarW) return BackgammonRules.BarSpot;
        int col = (int)((x >= 6 * PointW + BarW ? x - BarW : x) / PointW);
        if (p.Y > BoardH / 2) return 12 - col;
        return 13 + col;
    }

    // ------------------------------------------------------------------ layout

    public override void Layout()
    {
        var a = Host.Arena;
        _scale = Clamp(Math.Min(a.Width * 0.45 / BoardW, a.Height * 0.62 / BoardH), 0.75, 1.5);
        _scale = Math.Min(_scale, Math.Min((a.Width - 20) / BoardW, (a.Height - 40) / BoardH));
        if (!_placed)
        {
            _placed = true;
            _origin = _handle.Saved() ?? new Vec2(a.Center.X - BoardW * _scale / 2, a.Center.Y - BoardH * _scale / 2);
        }
        CheckSession();
        Place();
        Host.HudChanged();
    }

    void Place()
    {
        var a = Host.Arena;
        double w = BoardW * _scale, h = BoardH * _scale;
        _origin = new Vec2(Clamp(_origin.X, a.Left + 8, Math.Max(a.Left + 8, a.Right - w - 8)),
            Clamp(_origin.Y, a.Top + DragHandle.Height + 12, Math.Max(a.Top + DragHandle.Height + 12, a.Bottom - h - 4)));
        _zoom.ScaleX = _zoom.ScaleY = _scale;
        Canvas.SetLeft(_board, _origin.X);
        Canvas.SetTop(_board, _origin.Y);
        _handle.Show(new Rect(_origin.X, _origin.Y, w, h));
    }

    public override void PositionsReset() => _placed = false;

    /// <summary>A board-local position on the screen.</summary>
    Vec2 Screen(Vec2 local) => _origin + local * _scale;

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

    // ------------------------------------------------------------------ games

    void CheckSession()
    {
        int session = LanOn ? Host.Lan.Session : -1;
        if (session == _session) return;
        _session = session;
        _duel.Reset();
        _gameNo = 0;
        NewGame();
    }

    void NewGame(int number = -1)
    {
        Anims.Clear();
        _rules = new BackgammonRules();
        _gameNo = number >= 0 ? number : _gameNo + 1;
        _rules.Opens((_gameNo + 1) % 2); // the sides take turns at opening: side 0 the first game
        _busy = _rematchAsked = false;
        _think = _stepT = -1;
        _selected = -1;
        _cpuSteps.Clear();
        _roll = default;
        if (!LanOn && !MyTurn) _think = ThinkTime;
        Redraw();
        Host.HudChanged();
    }

    // ------------------------------------------------------------------ input

    public override void CollectHitShapes(List<HitShape> into)
    {
        into.Add(HitShape.Box(new Rect(_origin.X, _origin.Y, BoardW * _scale, BoardH * _scale)));
        into.Add(_handle.Hit);
    }

    public override bool PointerDown(Vec2 p, bool right)
    {
        if (right || _handle.Contains(p))
        {
            _handle.Begin(p, _origin, anywhere: true);
            return true;
        }
        if (_rules.Over)
        {
            if (IsGuest) AskRematch();
            else
            {
                NewGame();
                if (LanOn) _duel.Send(string.Create(CultureInfo.InvariantCulture, $"ng|{_gameNo}"));
            }
            return false;
        }
        if (!MyTurn || _busy) return false;
        var local = new Point((p.X - _origin.X) / _scale, (p.Y - _origin.Y) / _scale);
        if (!_rules.Rolled)
        {
            if (_rollRect.Inflate(6).Contains(local)) RollMine();
            return false;
        }
        int spot = SpotAt(local);
        if (spot < 0) return false;
        int r = spot; // the viewer's points are Me's own
        var legal = _rules.LegalSteps();
        if (_selected >= 0)
        {
            var to = legal.Where(s => s.From == _selected && s.To == r).OrderBy(s => s.Die).ToList();
            if (to.Count > 0)
            {
                Step(to[0], mine: true);
                return false;
            }
        }
        var from = legal.Where(s => s.From == r).ToList();
        if (from.Count == 0)
        {
            _selected = -1;
            DrawMarks();
            return false;
        }
        if (from.Select(s => s.To).Distinct().Count() == 1) Step(from.OrderBy(s => s.Die).First(), mine: true); // one place to go: go
        else
        {
            _selected = r;
            DrawMarks();
            Host.Sound.Play("click", 0.2, 1.4);
        }
        return false;
    }

    public override void PointerUp(Vec2 p) => _handle.End(_origin);
    public override void PointerCancel() => _handle.Cancel();

    void AskRematch()
    {
        if (!_rematchAsked) _duel.Send("rq");
        _rematchAsked = true;
        Host.Fx.Popup(Screen(new Vec2(BoardW / 2, -10)), L.F("asked {0} for a rematch", Rival), Colors.White, 18, 1.3);
    }

    // ------------------------------------------------------------------ turns

    void RollMine()
    {
        int a = Rng.Next(1, 7), b = Rng.Next(1, 7), ply = _rules.Ply;
        if (LanOn) _duel.Send(string.Create(CultureInfo.InvariantCulture, $"rl|{ply}|{a}|{b}"));
        Rolled(a, b);
    }

    /// <summary>The side to move has rolled <paramref name="a"/> and <paramref name="b"/>: show the dice; a side that cannot move passes.</summary>
    void Rolled(int a, int b)
    {
        int side = _rules.Turn;
        bool can = _rules.Roll(a, b);
        _roll = (a, b);
        Host.Sound.Play("click", 0.35, 0.8);
        Anims.After(0.08, () => Host.Sound.Play("click", 0.3, 1.1));
        if (!can)
        {
            string who = side == Me ? L.T("No moves this time") : L.F("{0} can't move", Rival);
            Host.Fx.Popup(Screen(new Vec2(BoardW / 2, BoardH / 2 - 30)), who, Colors.White, 22, 1.4);
            TurnPassed();
        }
        else if (side != Me && !LanOn)
        {
            foreach (var s in _rules.BestPlay(Math.Clamp(CpuLevel, 1, 4), Rng)) _cpuSteps.Enqueue(s);
            _stepT = StepTime + 0.2;
        }
        else if (_demo && side == Me)
        {
            foreach (var s in _rules.BestPlay(2, Rng)) _cpuSteps.Enqueue(s);
            _stepT = StepTime;
        }
        Redraw();
        Host.HudChanged();
        Host.Wake();
    }

    /// <summary>Plays one step for the side to move, sliding the checker across (and a hit one to the bar).</summary>
    void Step(BgStep step, bool mine)
    {
        int side = _rules.Turn, ply = _rules.Ply;
        var from = TopOf(side, step.From);
        var result = _rules.Play(step);
        if (result is not { } r) return;
        if (mine && LanOn) _duel.Send(string.Create(CultureInfo.InvariantCulture, $"mv|{ply}|{step.From}|{step.To}|{step.Die}"));
        _selected = -1;
        if (side == Me && !_demo)
        {
            if (r.Hit) Host.Stats.Add("backgammon.hits");
            if (r.BoreOff) Host.Stats.Add("backgammon.off");
        }
        Redraw();
        Slide(SpotKey(side, step.To), from, step.To == BackgammonRules.OffSpot ? OffSlot(side, _rules.BorneOff(side) - 1) : TopOf(side, step.To));
        if (r.Hit)
        {
            int other = 1 - side;
            Slide(SpotKey(other, BackgammonRules.BarSpot), TopOf(side, step.To), BarSlot(other == Me, _rules.OnBar(other) - 1));
            Host.Sound.Play("thunk", 0.4, 1.3);
            Host.Fx.Popup(Screen(TopOf(side, step.To) - new Vec2(0, 30)), side == Me ? L.T("Hit!") : L.F("{0} hits!", Rival), side == Me ? Themes.Themed(Themes.ClassicGold) : Colors.White, 20, 1);
        }
        else Host.Sound.Play(r.BoreOff ? "plop" : "click", 0.35, r.BoreOff ? 1.1 : 1.3);
        if (r.Won) GameOver();
        else if (r.TurnOver) TurnPassed();
        Host.HudChanged();
        Host.Wake();
    }

    void TurnPassed()
    {
        _cpuSteps.Clear();
        _stepT = -1;
        if (!_rules.Over && !LanOn && !MyTurn) _think = ThinkTime;
        if (_demo && MyTurn && !_rules.Over) _think = ThinkTime;
    }

    void Slide(int key, Vec2 from, Vec2 to)
    {
        if (!_tops.TryGetValue(key, out var el)) return;
        var shift = new TranslateTransform(from.X - to.X, from.Y - to.Y);
        el.RenderTransform = shift;
        _busy = true;
        double dx = shift.X, dy = shift.Y;
        Anims.Add(SlideTime, k =>
        {
            shift.X = dx * (1 - k);
            shift.Y = dy * (1 - k) - Math.Sin(Math.PI * k) * 10;
        }, Ease.OutQuad, () => _busy = false);
    }

    void GameOver()
    {
        _cpuSteps.Clear();
        _stepT = _think = -1;
        int kind = _rules.WinKind;
        string how = kind == 3 ? L.T("a backgammon: triple") : kind == 2 ? L.T("a gammon: double") : L.F("{0}–{1} off", _rules.BorneOff(Me), _rules.BorneOff(1 - Me));
        var at = Screen(new Vec2(BoardW / 2, -30));
        if (LanOn) Host.RecordResult(Id, Rival, _rules.Winner == Me ? 1 : -1);
        if (_rules.Winner == Me)
        {
            if (!_demo)
            {
                Host.Stats.Add("backgammon.wins");
                if (kind >= 2) Host.Stats.Add("backgammon.gammons");
                if (LanOn) Host.Stats.Add("lan.wins");
                else if (CpuLevel >= 3) Host.Stats.Add("backgammon.hardwins");
            }
            string sub = how;
            if (!LanOn && LevelStep(won: true) is string up) sub += " · " + up;
            Host.Fx.Popup(at, L.T("YOU WIN!"), Themes.Themed(Themes.ClassicGold), 42, 2.6, sub);
            Host.Fx.Burst(at, Themes.Current.Confetti, 40, 520, 700, 7, 1.1);
            Host.Sound.Play("best", 0.8);
        }
        else
        {
            string sub = how;
            if (!LanOn && LevelStep(won: false) is string down) sub += " · " + down;
            Host.Fx.Popup(at, LanOn ? L.F("{0} WINS", Rival) : L.T("CPU WINS"), Colors.White, 38, 2.4, sub);
            Host.Sound.Play("buzzer", 0.4);
        }
    }

    string? LevelStep(bool won)
    {
        if (_demo) return null;
        if (won)
        {
            _lossStreak = 0;
            if (++_winStreak < 2 || CpuLevel >= LevelNames.Length) return null;
            _winStreak = 0;
            CpuLevel++;
            return L.F("the CPU moves up to {0}", L.T(LevelNames[CpuLevel - 1]));
        }
        _winStreak = 0;
        if (++_lossStreak < 2 || CpuLevel <= 1) return null;
        _lossStreak = 0;
        CpuLevel--;
        return L.F("the CPU goes easier: {0}", L.T(LevelNames[CpuLevel - 1]));
    }

    public override bool Update(double dt)
    {
        if (_handle.Dragging)
        {
            _origin = _handle.Move(Host.Pointer, new Size(BoardW * _scale, BoardH * _scale));
            Place();
        }
        bool duel = DuelUpdate(dt);
        bool thinking = _think >= 0;
        if (thinking && !_busy && (_think -= dt) < 0)
        {
            _think = -1;
            if (!_rules.Over && !_rules.Rolled && ((!LanOn && !MyTurn) || (_demo && MyTurn))) Rolled(Rng.Next(1, 7), Rng.Next(1, 7));
        }
        bool stepping = _stepT >= 0;
        if (stepping && !_busy && (_stepT -= dt) < 0)
        {
            _stepT = _cpuSteps.Count > 1 ? StepTime : -1;
            if (_cpuSteps.TryDequeue(out var s)) Step(s, mine: _demo && MyTurn);
        }
        return Anims.Update(dt) || _handle.Dragging || _busy || thinking || stepping || duel;
    }

    bool DuelUpdate(double dt)
    {
        if (!LanOn) return false;
        _delivered.Clear();
        while (Host.Lan.TryReceive(out var msg)) _duel.Handle(msg, _delivered);
        foreach (var body in _delivered)
        {
            var f = body.Split('|');
            int[] n = f.Skip(1).Select(x => int.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : int.MinValue).ToArray();
            if (n.Contains(int.MinValue)) continue;
            if (f[0] == "rl" && n.Length == 3)
            {
                if (n[0] == _rules.Ply && !MyTurn && !_rules.Rolled && !_rules.Over) Rolled(n[1], n[2]);
            }
            else if (f[0] == "mv" && n.Length == 4)
            {
                if (n[0] == _rules.Ply && !MyTurn && _rules.Rolled) Step(new BgStep(n[1], n[2], n[3]), mine: false);
            }
            else if (f[0] == "ng" && n.Length == 1 && IsGuest) NewGame(n[0]);
            else if (f[0] == "rq" && !IsGuest && _rules.Over)
            {
                NewGame();
                _duel.Send(string.Create(CultureInfo.InvariantCulture, $"ng|{_gameNo}"));
            }
        }
        _duel.Tick(dt);
        return _duel.Pending > 0;
    }

    public override void DemoTick()
    {
        _demo = true;
        if (_rules.Over && !_busy && !IsGuest) NewGame();
        else if (MyTurn && !_rules.Rolled && _think < 0 && !_busy) _think = ThinkTime;
    }

    // ------------------------------------------------------------------ drawing

    void DrawBoard()
    {
        _board.Children.Clear();
        var wood = Color.FromRgb(107, 68, 35);
        var felt = Themes.Current.Felt ?? Color.FromRgb(31, 77, 58);
        _board.Children.Add(Art.At(new Rectangle { Width = BoardW + 4, Height = BoardH + 4, RadiusX = 12, RadiusY = 12, Fill = Art.Brush(70, 0, 0, 0) }, 2, 4));
        _board.Children.Add(new Rectangle
        {
            Width = BoardW, Height = BoardH, RadiusX = 12, RadiusY = 12, Stroke = Art.Brush(Art.Blend(wood, Colors.Black, 0.45)), StrokeThickness = 2,
            Fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Art.Blend(wood, Colors.White, 0.12), 0), new GradientStop(Art.Blend(wood, Colors.Black, 0.15), 1) },
            },
        });
        var feltBrush = Art.Brush(felt);
        _board.Children.Add(Art.At(new Rectangle { Width = PointW * 6, Height = BoardH - Frame * 2, Fill = feltBrush }, Frame, Frame));
        _board.Children.Add(Art.At(new Rectangle { Width = PointW * 6, Height = BoardH - Frame * 2, Fill = feltBrush }, Frame + PointW * 6 + BarW, Frame));
        _board.Children.Add(Art.At(new Rectangle { Width = TrayW, Height = BoardH - Frame * 2, RadiusX = 4, RadiusY = 4, Fill = Art.Brush(Art.Blend(felt, Colors.Black, 0.25)) }, Tray.X, Tray.Y));
        for (int r = 1; r <= 24; r++)
        {
            var c = Slot(r, 0);
            bool bottom = r <= 12;
            double tip = bottom ? BoardH - Frame - PointH + 6 : Frame + PointH - 6, bse = bottom ? BoardH - Frame : Frame;
            var fill = Art.Brush(r % 2 == 0 ? LightPoint : DarkPoint);
            _board.Children.Add(Art.PathOf($"M{Art.F(c.X - PointW / 2 + 1)},{Art.F(bse)} L{Art.F(c.X)},{Art.F(tip)} L{Art.F(c.X + PointW / 2 - 1)},{Art.F(bse)} Z", fill));
        }
        _board.Children.Add(_marks);
        _board.Children.Add(_checkers);
        _board.Children.Add(_dice);
    }

    void Redraw()
    {
        DrawCheckers();
        DrawMarks();
        DrawDice();
    }

    void DrawCheckers()
    {
        _checkers.Children.Clear();
        _tops.Clear();
        for (int side = 0; side < 2; side++)
        {
            for (int r = 1; r <= 24; r++)
            {
                int n = _rules.Count(side, r);
                for (int k = 0; k < Math.Min(n, 5); k++) Add(side, SpotKey(side, r), Slot(View(side, r), k), k == Math.Min(n, 5) - 1 && n > 5 ? n : 0);
            }
            int bar = _rules.OnBar(side);
            for (int k = 0; k < Math.Min(bar, 4); k++) Add(side, SpotKey(side, BackgammonRules.BarSpot), BarSlot(side == Me, k), k == Math.Min(bar, 4) - 1 && bar > 4 ? bar : 0);
            for (int k = 0; k < _rules.BorneOff(side); k++)
            {
                var at = OffSlot(side, k);
                var slab = Art.At(new Rectangle { Width = TrayW - 10, Height = 7, RadiusX = 2, RadiusY = 2, Fill = Art.Brush(side == Me ? Ivory : Ebony), Stroke = Art.Brush(side == Me ? "#8C826E" : "#000000"), StrokeThickness = 0.8 }, at.X - (TrayW - 10) / 2, at.Y - 3.5);
                _checkers.Children.Add(slab);
                _tops[SpotKey(side, BackgammonRules.OffSpot)] = slab;
            }
        }
    }

    void Add(int side, int key, Vec2 at, int count)
    {
        bool mine = side == Me;
        var c = new Canvas { IsHitTestVisible = false };
        c.Children.Add(Art.Circle(0, 1.5, R, Art.Brush(80, 0, 0, 0)));
        c.Children.Add(Art.Circle(0, 0, R, Art.Brush(mine ? Ivory : Ebony), Art.Brush(mine ? "#8C826E" : "#000000"), 1.2));
        c.Children.Add(Art.Circle(0, 0, R * 0.62, null, Art.Brush(mine ? Color.FromArgb(120, 140, 130, 110) : Color.FromArgb(120, 120, 120, 140)), 1.2));
        if (count > 0)
            c.Children.Add(Art.At(new TextBlock { Text = count.ToString(CultureInfo.InvariantCulture), FontFamily = Fx.Font, FontSize = 13, FontWeight = FontWeight.Black, Foreground = Art.Brush(mine ? Ebony : Ivory), Width = R * 2, TextAlignment = TextAlignment.Center }, -R, -9));
        Art.At(c, at.X, at.Y);
        _checkers.Children.Add(c);
        _tops[key] = c;
    }

    /// <summary>The picked checker glows; the places it can go get a ring (the tray a frame).</summary>
    void DrawMarks()
    {
        _marks.Children.Clear();
        if (!MyTurn || !_rules.Rolled) return;
        var gold = Art.Brush(Themes.Current.Gold);
        var legal = _rules.LegalSteps();
        if (_selected < 0)
        {
            foreach (int from in legal.Select(s => s.From).Distinct()) // what can move, faintly
            {
                var p = TopOf(Me, from);
                _marks.Children.Add(Art.Circle(p.X, p.Y, R + 3, null, Art.Brush(Color.FromArgb(110, Themes.Current.Gold.R, Themes.Current.Gold.G, Themes.Current.Gold.B)), 2));
            }
            return;
        }
        var sel = TopOf(Me, _selected);
        _marks.Children.Add(Art.Circle(sel.X, sel.Y, R + 4, null, gold, 3));
        foreach (int to in legal.Where(s => s.From == _selected).Select(s => s.To).Distinct())
        {
            if (to == BackgammonRules.OffSpot)
            {
                var t = Tray;
                _marks.Children.Add(Art.At(new Rectangle { Width = t.Width, Height = t.Height, RadiusX = 4, RadiusY = 4, Stroke = gold, StrokeThickness = 3 }, t.X, t.Y));
                continue;
            }
            var p = Slot(View(Me, to), _rules.Count(Me, to));
            _marks.Children.Add(Art.Circle(p.X, p.Y, R - 2, Art.Brush(Color.FromArgb(70, 255, 255, 255)), gold, 2.5));
        }
    }

    /// <summary>The dice on the roller's half of the board (used ones faded), or the ROLL button when it is your roll.</summary>
    void DrawDice()
    {
        _dice.Children.Clear();
        _rollRect = default;
        double y = BoardH / 2;
        double rightX = Frame + PointW * 9 + BarW, leftX = Frame + PointW * 3;
        if (MyTurn && !_rules.Rolled && !_rules.Over)
        {
            var t = Themes.Current;
            var text = new TextBlock { Text = L.T("ROLL"), FontFamily = Fx.Font, FontSize = 15, FontWeight = FontWeight.Black, Foreground = Art.Brush(t.HudFront) };
            text.Measure(Size.Infinity);
            double w = text.DesiredSize.Width + 28;
            _rollRect = new Rect(rightX - w / 2, y - 15, w, 30);
            _dice.Children.Add(Art.At(new Rectangle { Width = w, Height = 30, RadiusX = 15, RadiusY = 15, Fill = Art.Brush(Art.Blend(t.Accent, Colors.Black, 0.25)), Stroke = Art.Brush(t.Gold), StrokeThickness = 1.5 }, _rollRect.X, _rollRect.Y));
            _dice.Children.Add(Art.At(text, _rollRect.X + 14, y - text.DesiredSize.Height / 2));
            return;
        }
        if (_roll.A == 0 || !_rules.Rolled) return;
        var rolled = _roll.A == _roll.B ? new[] { _roll.A, _roll.A, _roll.A, _roll.A } : new[] { _roll.A, _roll.B };
        var left = _rules.Dice.ToList();
        double x0 = (_rules.Turn == Me ? rightX : leftX) - (rolled.Length * 30 - 4) / 2;
        for (int i = 0; i < rolled.Length; i++)
        {
            bool live = left.Remove(rolled[i]);
            _dice.Children.Add(Die(rolled[i], x0 + i * 30, y - 13, live));
        }
    }

    static Canvas Die(int value, double x, double y, bool live)
    {
        var c = new Canvas { Opacity = live ? 1 : 0.35, IsHitTestVisible = false };
        c.Children.Add(new Rectangle { Width = 26, Height = 26, RadiusX = 5, RadiusY = 5, Fill = Brushes.White, Stroke = Art.Brush("#9AA0AA"), StrokeThickness = 1 });
        var dot = Art.Brush("#20232C");
        double[] a = { 7, 13, 19 };
        var spots = value switch
        {
            1 => new[] { (1, 1) },
            2 => new[] { (0, 0), (2, 2) },
            3 => new[] { (0, 0), (1, 1), (2, 2) },
            4 => new[] { (0, 0), (2, 0), (0, 2), (2, 2) },
            5 => new[] { (0, 0), (2, 0), (1, 1), (0, 2), (2, 2) },
            _ => new[] { (0, 0), (2, 0), (0, 1), (2, 1), (0, 2), (2, 2) },
        };
        foreach (var (cx, cy) in spots) c.Children.Add(Art.Circle(a[cx], a[cy], 2.6, dot));
        return Art.At(c, x, y);
    }

    public override void ThemeChanged()
    {
        DrawBoard();
        Redraw();
    }
}
