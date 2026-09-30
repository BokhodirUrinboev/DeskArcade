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
/// Dominoes (see <see cref="DominoRules"/>): the line of tiles is laid along the taskbar from the middle, both ways, and
/// turns up the side of the screen when it runs out of room. Your seven tiles stand in a rack above it: click one that
/// fits an open end (and then the end, when it fits both); with nothing that fits, click the boneyard to draw. Going
/// out scores the other hand's pips, a blocked line the heavier hand's; first to fifty takes the match. Against the
/// computer at four levels or a co-worker over the LAN. The grip moves the rack.
/// </summary>
public sealed class DominoGame : MiniGame
{
    const double Cell = 28, Gap = 3, Pad = 10, HeaderH = 26, Margin = 16;
    const double ThinkTime = 0.8, DrawTime = 0.45;
    static readonly Color Ivory = Color.FromRgb(246, 241, 228), Pip = Color.FromRgb(32, 35, 44), Edge = Color.FromRgb(150, 140, 120);

    readonly Canvas _line = new() { IsHitTestVisible = false };
    readonly Canvas _ends = new() { IsHitTestVisible = false };
    readonly Canvas _rack = new();
    readonly Rectangle _rackBack = new() { RadiusX = 10, RadiusY = 10, StrokeThickness = 1.5, IsHitTestVisible = false };
    readonly TextBlock _rackTitle = new() { FontFamily = Fx.Font, FontSize = 12, FontWeight = FontWeight.Bold, IsHitTestVisible = false };
    readonly TextBlock _rival = new() { FontFamily = Fx.Font, FontSize = 12, FontWeight = FontWeight.Bold, IsHitTestVisible = false };
    readonly Canvas _tiles = new() { IsHitTestVisible = false };
    readonly DragHandle _handle;
    readonly List<string> _delivered = new();
    readonly List<(Domino Tile, Rect Box)> _hits = new();
    DominoRules _rules = new();
    DuelChannel _duel = null!;
    Vec2 _origin, _leftSpot, _rightSpot;
    Rect _yardRect;
    bool _placed, _demo, _busy, _rematchAsked;
    double _think = -1;
    int _session = int.MinValue, _winStreak, _lossStreak; // no session yet: the first layout deals
    Domino? _choosing; // a tile that fits both ends: which one?

    public DominoGame(IGameHost host) : base(host)
    {
        foreach (var c in new Control[] { _rackBack, _rackTitle, _rival, _tiles }) _rack.Children.Add(c);
        Layer.Children.Add(_line);
        Layer.Children.Add(_ends);
        Layer.Children.Add(_rack);
        _handle = new DragHandle(host, Id, Title);
        Layer.Children.Add(_handle.Visual);
        _duel = new DuelChannel("dm", Host.Lan.Send);
        ThemeChanged();
    }

    public override string Id => "dominoes";
    public override string Title => "Dominoes";

    bool LanOn => Host.Lan.Connected;
    bool IsGuest => LanOn && Host.Lan.Role == LanRole.Guest;
    int Me => IsGuest ? 1 : 0;
    bool MyTurn => !_rules.RoundOver && _rules.Turn == Me;
    string Rival => LanOn ? Host.Lan.PeerName : L.T("CPU");

    public override bool SupportsLan => true;
    public override bool HasCpuLevels => true;
    protected override int DefaultCpuLevel => 1;
    public override Opponent? Opponent => new(Rival, !LanOn, LanOn ? 0 : CpuLevel, _rules.RoundOver ? null : MyTurn);

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        s.Rotor.Children.Add(Art.At(new Rectangle { Width = 12, Height = 22, RadiusX = 2.5, RadiusY = 2.5, Fill = Art.Brush(Ivory), Stroke = Art.Brush("#20232C"), StrokeThickness = 1.2 }, -6, -11));
        s.Rotor.Children.Add(Art.PathOf("M-4,0 L4,0", null, Art.Brush("#20232C"), 1));
        foreach (var (x, y) in new[] { (-2.5, -7.5), (2.5, -3.5), (0.0, 5.5), (-2.5, 3.0), (2.5, 8.0) }) s.Rotor.Children.Add(Art.Circle(x, y, 1.3, Art.Brush("#20232C")));
        return s;
    }

    public override HudInfo Hud
    {
        get
        {
            string line = _rules.MatchOver ? (IsGuest ? L.T("Match over · click your tiles to ask for a rematch") : L.T("Match over · click your tiles for a new match"))
                : _rules.RoundOver ? (IsGuest ? L.F("Round over · waiting for {0} to deal", Rival) : L.T("Round over · click your tiles for the next round"))
                : !MyTurn ? L.F("{0}'s turn", Rival)
                : _rules.MustDraw ? L.T("Nothing fits · click the boneyard to draw")
                : L.F("Your turn · lay a tile that fits an end · first to {0}", DominoRules.MatchTo);
            return new HudInfo($"{_rules.Score(Me)}–{_rules.Score(1 - Me)}", line, L.F("Wins {0}", Host.Stats.Get("dominoes.wins")));
        }
    }

    // ------------------------------------------------------------------ rounds

    void CheckSession()
    {
        int session = LanOn ? Host.Lan.Session : -1;
        if (session == _session) return;
        _session = session;
        _duel.Reset();
        _rules = new DominoRules();
        _rules.NewMatch();
        if (!IsGuest) NextRound();
        else Redraw();
    }

    /// <summary>The host (or the only player) deals; over the LAN the shuffle's seed goes to the guest.</summary>
    void NextRound()
    {
        if (_rules.MatchOver) _rules.NewMatch();
        int seed = Rng.Next(), opener = _rules.Round % 2; // the rounds take turns at opening
        Deal(seed, opener);
        if (LanOn) _duel.Send(string.Create(CultureInfo.InvariantCulture, $"rd|{_rules.Round}|{seed}|{opener}"));
    }

    void Deal(int seed, int opener)
    {
        Anims.Clear();
        _rules.Deal(seed, opener);
        _choosing = null;
        _busy = _rematchAsked = false;
        _think = !LanOn && !MyTurn ? ThinkTime : -1;
        Host.Sound.Play("click", 0.3, 0.9);
        Redraw();
        Host.HudChanged();
    }

    // ------------------------------------------------------------------ layout

    double RackW => Pad * 2 + Math.Max(7, _rules.Hand(Me).Count) * (Cell + 6) + Cell + 14;
    const double RackH = HeaderH + Cell * 2 + Pad + 4;

    public override void Layout()
    {
        var a = Host.Arena;
        if (!_placed)
        {
            _placed = true;
            _origin = _handle.Saved() ?? new Vec2(a.Center.X - RackW / 2, a.Bottom - Cell * 2 - RackH - 60);
        }
        CheckSession();
        Place();
        Redraw();
        Host.HudChanged();
    }

    void Place()
    {
        var a = Host.Arena;
        _origin = new Vec2(Clamp(_origin.X, a.Left + 8, Math.Max(a.Left + 8, a.Right - RackW - 8)),
            Clamp(_origin.Y, a.Top + DragHandle.Height + 12, Math.Max(a.Top + DragHandle.Height + 12, a.Bottom - RackH - 8)));
        Canvas.SetLeft(_rack, _origin.X);
        Canvas.SetTop(_rack, _origin.Y);
        _handle.Show(new Rect(_origin.X, _origin.Y, RackW, RackH));
    }

    public override void PositionsReset() => _placed = false;

    public override void Summon(Vec2 p)
    {
        _origin = p - new Vec2(RackW / 2, 20);
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
        into.Add(HitShape.Box(new Rect(_origin.X, _origin.Y, RackW, RackH)));
        into.Add(_handle.Hit);
        if (_choosing != null)
        {
            into.Add(HitShape.Circle(_leftSpot, Cell));
            into.Add(HitShape.Circle(_rightSpot, Cell));
        }
    }

    public override bool PointerDown(Vec2 p, bool right)
    {
        if (_handle.Contains(p))
        {
            _handle.Begin(p, _origin);
            return true;
        }
        if (_rules.RoundOver)
        {
            if (IsGuest)
            {
                if (_rules.MatchOver) AskRematch();
            }
            else NextRound();
            return false;
        }
        if (!MyTurn || _busy) return false;
        if (_choosing is { } both)
        {
            if ((p - _leftSpot).Length <= Cell) Lay(both, atLeft: true, mine: true);
            else if ((p - _rightSpot).Length <= Cell) Lay(both, atLeft: false, mine: true);
            else
            {
                _choosing = null;
                Redraw();
            }
            return false;
        }
        var local = new Point(p.X - _origin.X, p.Y - _origin.Y);
        if (_yardRect.Contains(local))
        {
            if (_rules.MustDraw) DrawTile(mine: true);
            else Host.Sound.Play("board", 0.2, 0.7);
            return false;
        }
        foreach (var (t, box) in _hits)
        {
            if (!box.Contains(local)) continue;
            var (l, r) = _rules.Fits(t);
            if (!l && !r)
            {
                Host.Fx.Popup(p - new Vec2(0, 30), L.T("It doesn't fit an end"), Colors.White, 16, 1);
                Host.Sound.Play("board", 0.2, 0.7);
            }
            else if (l && r && _rules.Line.Count > 0 && _rules.LeftEnd != _rules.RightEnd && t.Other(_rules.LeftEnd) != t.Other(_rules.RightEnd))
            {
                _choosing = t; // it fits both ends and it matters which
                Redraw();
                Host.Sound.Play("click", 0.2, 1.4);
            }
            else Lay(t, atLeft: l, mine: true);
            return false;
        }
        return false;
    }

    public override void PointerUp(Vec2 p) => _handle.End(_origin);
    public override void PointerCancel() => _handle.Cancel();

    void AskRematch()
    {
        if (!_rematchAsked) _duel.Send("rq");
        _rematchAsked = true;
        Host.Fx.Popup(new Vec2(_origin.X + RackW / 2, _origin.Y - 10), L.F("asked {0} for a rematch", Rival), Colors.White, 18, 1.3);
    }

    // ------------------------------------------------------------------ turns

    void Lay(Domino t, bool atLeft, bool mine)
    {
        int side = _rules.Turn, ply = _rules.Ply;
        var from = side == Me ? TileSpot(t) : new Vec2(Host.Arena.Center.X, Host.Arena.Top + 60);
        if (!_rules.Play(t, atLeft)) return;
        if (mine && LanOn) _duel.Send(string.Create(CultureInfo.InvariantCulture, $"pl|{ply}|{t.A}|{t.B}|{(atLeft ? 1 : 0)}"));
        if (side == Me && !_demo) Host.Stats.Add("dominoes.played");
        _choosing = null;
        Redraw();
        // the new tile slides in from the hand it left
        int index = atLeft ? 0 : _rules.Line.Count - 1;
        if (_lineEls.Count > index)
        {
            var el = _lineEls[index];
            var to = new Vec2(Canvas.GetLeft(el), Canvas.GetTop(el));
            var shift = new TranslateTransform(from.X - to.X, from.Y - to.Y);
            el.RenderTransform = shift;
            double dx = shift.X, dy = shift.Y;
            _busy = true;
            Anims.Add(0.3, k =>
            {
                shift.X = dx * (1 - k);
                shift.Y = dy * (1 - k);
            }, Ease.OutQuad, () => _busy = false);
        }
        Host.Sound.Play("click", 0.4, t.IsDouble ? 0.8 : 1.1);
        AfterAction();
    }

    void DrawTile(bool mine)
    {
        int ply = _rules.Ply;
        if (_rules.Draw() is null) return;
        if (mine && LanOn) _duel.Send(string.Create(CultureInfo.InvariantCulture, $"dr|{ply}"));
        Host.Sound.Play("board", 0.25, 1.4);
        Redraw();
        AfterAction();
    }

    void Pass(bool mine)
    {
        int side = _rules.Turn, ply = _rules.Ply;
        if (!_rules.Pass()) return;
        if (mine && LanOn) _duel.Send(string.Create(CultureInfo.InvariantCulture, $"ps|{ply}"));
        Host.Fx.Popup(new Vec2(Host.Arena.Center.X, Host.Arena.Bottom - Cell * 4), side == Me ? L.T("Nothing fits · you pass") : L.F("{0} passes", Rival), Colors.White, 20, 1.3);
        Redraw();
        AfterAction();
    }

    /// <summary>After any move: the round may be over; the computer thinks; a stuck player of ours passes by itself.</summary>
    void AfterAction()
    {
        if (_rules.RoundOver)
        {
            RoundOver();
            return;
        }
        if (!LanOn && !MyTurn) _think = _rules.CanPlay(_rules.Turn) ? ThinkTime : DrawTime;
        else if (MyTurn && (_rules.MustPass || _demo)) _think = _demo ? 0.5 : 0.9;
        Host.HudChanged();
        Host.Wake();
    }

    void RoundOver()
    {
        _think = -1;
        int w = _rules.RoundWinner;
        var at = new Vec2(Host.Arena.Center.X, Host.Arena.Bottom - Cell * 5);
        string title = _rules.WentOut ? L.T("DOMINO!") : L.T("BLOCKED");
        string sub = w < 0 ? L.T("a tie: nobody scores")
            : w == Me ? L.F("you score {0}", _rules.RoundPoints) : L.F("{0} scores {1}", Rival, _rules.RoundPoints);
        if (w == Me && !_demo)
        {
            Host.Stats.Add("dominoes.points", _rules.RoundPoints);
            if (_rules.WentOut) Host.Stats.Add("dominoes.outs");
        }
        if (_rules.MatchOver)
        {
            bool won = _rules.MatchWinner == Me;
            if (LanOn) Host.RecordResult(Id, Rival, won ? 1 : -1);
            if (won && !_demo)
            {
                Host.Stats.Add("dominoes.wins");
                if (LanOn) Host.Stats.Add("lan.wins");
                else if (CpuLevel >= 3) Host.Stats.Add("dominoes.hardwins");
            }
            string score = L.F("{0}–{1}", _rules.Score(Me), _rules.Score(1 - Me));
            if (!LanOn && LevelStep(won) is string step) score += " · " + step;
            Host.Fx.Popup(at - new Vec2(0, 60), won ? L.T("YOU WIN!") : LanOn ? L.F("{0} WINS", Rival) : L.T("CPU WINS"), won ? Themes.Themed(Themes.ClassicGold) : Colors.White, 42, 2.8, score);
            if (won) Host.Fx.Burst(at - new Vec2(0, 60), Themes.Current.Confetti, 40, 520, 700, 7, 1.1);
            Host.Sound.Play(won ? "best" : "buzzer", won ? 0.8 : 0.4);
        }
        else
        {
            Host.Fx.Popup(at, title, w == Me ? Themes.Themed(Themes.ClassicGold) : Colors.White, 34, 2.2, sub);
            Host.Sound.Play(w == Me ? "score" : "board", 0.5);
        }
        Redraw();
        Host.HudChanged();
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
            _origin = _handle.Move(Host.Pointer, new Size(RackW, RackH));
            Place();
        }
        bool duel = DuelUpdate(dt);
        bool thinking = _think >= 0;
        if (thinking && !_busy && (_think -= dt) < 0)
        {
            _think = -1;
            if (_rules.RoundOver)
            {
                if (_demo && !IsGuest) NextRound();
            }
            else if ((!LanOn && !MyTurn) || (MyTurn && (_demo || _rules.MustPass))) Act(MyTurn ? 2 : Math.Clamp(CpuLevel, 1, 4));
        }
        return Anims.Update(dt) || _handle.Dragging || _busy || thinking || duel;
    }

    /// <summary>One move for the side to move by the computer's choice: lay a tile, or draw, or pass.</summary>
    void Act(int level)
    {
        bool mine = MyTurn;
        if (_rules.BestMove(level, Rng) is { } m) Lay(m.Tile, m.AtLeft, mine);
        else if (_rules.MustDraw) DrawTile(mine);
        else if (_rules.MustPass) Pass(mine);
    }

    bool DuelUpdate(double dt)
    {
        if (!LanOn) return false;
        _delivered.Clear();
        while (Host.Lan.TryReceive(out var msg)) _duel.Handle(msg, _delivered);
        foreach (var body in _delivered)
        {
            var f = body.Split('|');
            var n = f.Skip(1).Select(x => int.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : int.MinValue).ToArray();
            if (n.Contains(int.MinValue)) continue;
            bool theirs = !MyTurn && !_rules.RoundOver;
            switch (f[0])
            {
                case "rd" when n.Length == 3 && IsGuest:
                    if (_rules.MatchOver || n[0] == 1) _rules.NewMatch();
                    Deal(n[1], n[2]);
                    break;
                case "pl" when n.Length == 4 && theirs && n[0] == _rules.Ply:
                    Lay(new Domino(n[1], n[2]), n[3] == 1, mine: false);
                    break;
                case "dr" when n.Length == 1 && theirs && n[0] == _rules.Ply:
                    DrawTile(mine: false);
                    break;
                case "ps" when n.Length == 1 && theirs && n[0] == _rules.Ply:
                    Pass(mine: false);
                    break;
                case "rq" when !IsGuest && _rules.MatchOver:
                    NextRound();
                    break;
            }
        }
        _duel.Tick(dt);
        return _duel.Pending > 0;
    }

    public override void DemoTick()
    {
        _demo = true;
        if (_think >= 0 || _busy) return;
        if (_rules.RoundOver)
        {
            if (!IsGuest) _think = 1.5;
        }
        else if (MyTurn) _think = 0.5;
    }

    // ------------------------------------------------------------------ the line

    readonly List<Control> _lineEls = new();

    /// <summary>
    /// Lays the line out from its first tile in the middle of the taskbar, out both ways; a side that reaches the edge
    /// of the screen turns up it, and along the top back in if it gets that far. Doubles lie crosswise.
    /// </summary>
    void DrawLine()
    {
        _line.Children.Clear();
        _lineEls.Clear();
        var a = Host.Arena;
        var line = _rules.Line;
        double floor = a.Bottom - Cell / 2 - 8;
        if (line.Count == 0)
        {
            _leftSpot = _rightSpot = new Vec2(a.Center.X, floor);
            return;
        }
        var els = new Control[line.Count];
        int first = _rules.FirstIndex;
        var f = line[first];
        double flen = f.Tile.IsDouble ? Cell : Cell * 2;
        var center = new Vec2(a.Center.X, floor);
        els[first] = Place(f.Tile.IsDouble ? TileArt(f.L, f.R, vertical: true) : TileArt(f.L, f.R, vertical: false), center);
        // walk out to the right, then to the left
        var walkR = new Walker(center + new Vec2(flen / 2 + Gap, 0), new Vec2(1, 0), a);
        for (int i = first + 1; i < line.Count; i++) els[i] = Lay(walkR, line[i].L, line[i].R, line[i].Tile.IsDouble);
        _rightSpot = walkR.Peek(Cell * 2);
        var walkL = new Walker(center - new Vec2(flen / 2 + Gap, 0), new Vec2(-1, 0), a);
        for (int i = first - 1; i >= 0; i--) els[i] = Lay(walkL, line[i].R, line[i].L, line[i].Tile.IsDouble);
        _leftSpot = walkL.Peek(Cell * 2);
        _lineEls.AddRange(els);
    }

    Control Lay(Walker w, int inner, int outer, bool isDouble)
    {
        double len = isDouble ? Cell : Cell * 2;
        var (center, dir) = w.Next(len);
        bool horizontal = dir.Y == 0;
        Canvas art;
        if (isDouble) art = TileArt(inner, outer, vertical: horizontal);
        else if (horizontal) art = dir.X > 0 ? TileArt(inner, outer, false) : TileArt(outer, inner, false);
        else art = dir.Y < 0 ? TileArt(outer, inner, true) : TileArt(inner, outer, true);
        return Place(art, center);
    }

    Control Place(Canvas art, Vec2 center)
    {
        Canvas.SetLeft(art, center.X);
        Canvas.SetTop(art, center.Y);
        _line.Children.Add(art);
        return art;
    }

    /// <summary>Walks the line outward along the floor, turning up the side and then back along the top.</summary>
    sealed class Walker
    {
        readonly Rect _box;
        Vec2 _pos, _dir;
        Vec2 _lastEnd;

        public Walker(Vec2 start, Vec2 dir, Rect arena)
        {
            _pos = start;
            _dir = dir;
            _box = new Rect(arena.Left + Margin, arena.Top + Margin + 40, arena.Width - Margin * 2, arena.Height - Margin * 2 - 40);
            _lastEnd = start;
        }

        /// <summary>The centre and direction of the next tile <paramref name="len"/> long, turning first if it would not fit.</summary>
        public (Vec2 Center, Vec2 Dir) Next(double len)
        {
            var far = _pos + _dir * len;
            if (!Inside(far)) Turn();
            var center = _pos + _dir * (len / 2);
            _lastEnd = _pos + _dir * len;
            _pos = _lastEnd + _dir * Gap;
            return (center, _dir);
        }

        /// <summary>Where a tile laid next would sit, without laying it.</summary>
        public Vec2 Peek(double len)
        {
            var copy = (_pos, _dir, _lastEnd);
            var (c, _) = Next(len);
            (_pos, _dir, _lastEnd) = copy;
            return c;
        }

        bool Inside(Vec2 p) => p.X >= _box.Left && p.X <= _box.Right && p.Y >= _box.Top && p.Y <= _box.Bottom + Cell;

        void Turn()
        {
            if (_dir.Y == 0)
            {
                // along the floor: turn up at the edge, above the last tile's outer half
                double x = _lastEnd.X - _dir.X * Cell / 2;
                _pos = new Vec2(x, _lastEnd.Y - Cell / 2 - Gap);
                _dir = new Vec2(0, -1);
            }
            else
            {
                // up the side: turn back in along the top
                double inward = _lastEnd.X > (_box.Left + _box.Right) / 2 ? -1 : 1;
                _pos = new Vec2(_lastEnd.X + inward * (Cell / 2 + Gap), _lastEnd.Y + Cell / 2);
                _dir = new Vec2(inward, 0);
            }
        }
    }

    // ------------------------------------------------------------------ drawing

    /// <summary>
    /// A tile centred on (0, 0): <paramref name="first"/> on its left (or top) half, <paramref name="second"/> on its right
    /// (or bottom); face down when both are -1.
    /// </summary>
    static Canvas TileArt(int first, int second, bool vertical, bool glow = false)
    {
        double w = vertical ? Cell : Cell * 2, h = vertical ? Cell * 2 : Cell;
        var c = new Canvas { IsHitTestVisible = false };
        bool down = first < 0;
        c.Children.Add(Art.At(new Rectangle { Width = w, Height = h, RadiusX = 4, RadiusY = 4, Fill = Art.Brush(60, 0, 0, 0) }, -w / 2 + 1, -h / 2 + 2));
        c.Children.Add(Art.At(new Rectangle
        {
            Width = w, Height = h, RadiusX = 4, RadiusY = 4, Fill = Art.Brush(down ? Themes.Current.CardBack ?? Color.FromRgb(60, 90, 140) : Ivory),
            Stroke = glow ? Art.Brush(Themes.Current.Gold) : Art.Brush(Edge), StrokeThickness = glow ? 2.5 : 1,
        }, -w / 2, -h / 2));
        if (down) return c;
        c.Children.Add(vertical ? Art.PathOf($"M{Art.F(-Cell / 2 + 4)},0 L{Art.F(Cell / 2 - 4)},0", null, Art.Brush(Edge), 1.2)
            : Art.PathOf($"M0,{Art.F(-Cell / 2 + 4)} L0,{Art.F(Cell / 2 - 4)}", null, Art.Brush(Edge), 1.2));
        AddPips(c, first, vertical ? new Vec2(0, -Cell / 2) : new Vec2(-Cell / 2, 0));
        AddPips(c, second, vertical ? new Vec2(0, Cell / 2) : new Vec2(Cell / 2, 0));
        return c;
    }

    static void AddPips(Canvas c, int n, Vec2 at)
    {
        const double s = 6.5, r = 2.3;
        var spots = n switch
        {
            0 => Array.Empty<(int, int)>(),
            1 => new[] { (0, 0) },
            2 => new[] { (-1, -1), (1, 1) },
            3 => new[] { (-1, -1), (0, 0), (1, 1) },
            4 => new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) },
            5 => new[] { (-1, -1), (1, -1), (0, 0), (-1, 1), (1, 1) },
            _ => new[] { (-1, -1), (1, -1), (-1, 0), (1, 0), (-1, 1), (1, 1) },
        };
        var brush = Art.Brush(Pip);
        foreach (var (x, y) in spots) c.Children.Add(Art.Circle(at.X + x * s, at.Y + y * s, r, brush));
    }

    Vec2 TileSpot(Domino t)
    {
        foreach (var (tile, box) in _hits)
            if (tile == t) return _origin + new Vec2(box.Center.X, box.Center.Y);
        return _origin + new Vec2(RackW / 2, RackH / 2);
    }

    void Redraw()
    {
        DrawLine();
        DrawRack();
        DrawEnds();
    }

    void DrawRack()
    {
        var t = Themes.Current;
        _tiles.Children.Clear();
        _hits.Clear();
        _rackBack.Width = RackW;
        _rackBack.Height = RackH;
        var hand = _rules.Hand(Me);
        bool mine = MyTurn;
        _rackTitle.Text = L.T("Your tiles");
        Art.At(_rackTitle, Pad, 5);
        _rival.Text = _rules.RoundOver && _rules.Round > 0
            ? L.F("{0} had {1} pips", Rival, _rules.HandPips(1 - Me))
            : L.F("{0} · {1} tiles", Rival, _rules.Hand(1 - Me).Count);
        _rival.Measure(Size.Infinity);
        Art.At(_rival, RackW - Pad - _rival.DesiredSize.Width, 5);
        for (int i = 0; i < hand.Count; i++)
        {
            var tile = hand[i];
            var (l, r) = _rules.Fits(tile);
            bool fits = mine && (l || r);
            double x = Pad + i * (Cell + 6) + Cell / 2, y = HeaderH + Cell;
            var art = TileArt(tile.A, tile.B, vertical: true, glow: _choosing == tile || (fits && _choosing == null));
            if (mine && !fits) art.Opacity = 0.55;
            Art.At(art, x, y);
            _tiles.Children.Add(art);
            _hits.Add((tile, new Rect(x - Cell / 2, y - Cell, Cell, Cell * 2)));
        }
        // the boneyard: a face-down pile at the end of the rack, with how many are left
        double bx = RackW - Pad - Cell / 2, by = HeaderH + Cell;
        _yardRect = new Rect(bx - Cell / 2 - 4, by - Cell - 4, Cell + 8, Cell * 2 + 8);
        if (_rules.Boneyard > 0)
        {
            for (int k = Math.Min(2, _rules.Boneyard - 1); k >= 0; k--) _tiles.Children.Add(Art.At(TileArt(-1, -1, vertical: true, glow: k == 0 && mine && _rules.MustDraw), bx + k * 2, by - k * 2));
            var count = new TextBlock { Text = _rules.Boneyard.ToString(CultureInfo.InvariantCulture), FontFamily = Fx.Font, FontSize = 13, FontWeight = FontWeight.Black, Foreground = Brushes.White, Width = Cell, TextAlignment = TextAlignment.Center };
            _tiles.Children.Add(Art.At(count, bx - Cell / 2, by - 9));
        }
        Place();
    }

    /// <summary>When a tile fits both ends, rings on the two places it could go.</summary>
    void DrawEnds()
    {
        _ends.Children.Clear();
        if (_choosing == null) return;
        var gold = Art.Brush(Themes.Current.Gold);
        foreach (var spot in new[] { _leftSpot, _rightSpot })
            _ends.Children.Add(Art.Circle(spot.X, spot.Y, Cell * 0.8, Art.Brush(Color.FromArgb(60, 255, 255, 255)), gold, 3));
    }

    public override void ThemeChanged()
    {
        var t = Themes.Current;
        _rackBack.Fill = Art.Brush(Color.FromArgb(235, t.Ink.R, t.Ink.G, t.Ink.B));
        _rackBack.Stroke = Art.Brush(Color.FromArgb(150, t.Accent.R, t.Accent.G, t.Accent.B));
        _rackTitle.Foreground = Art.Brush(t.HudFront);
        _rival.Foreground = Art.Brush(Art.Blend(t.HudFront, t.Ink, 0.3));
        Redraw();
    }
}
