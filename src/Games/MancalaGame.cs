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
/// Mancala (see <see cref="MancalaRules"/>) on a wooden board that lies above the taskbar: your six pits along the
/// bottom and your store on the right. Click one of your pits and its seeds are sown one by one around the board, a
/// soft plop each; a capture sends seeds flying into your store. Against the computer at four levels (it moves up after
/// two wins in a row and down after two losses) or a co-worker over the LAN, who sees the same board from their side;
/// the host moves first. The grip (or a right-drag) moves the board.
/// </summary>
public sealed class MancalaGame : MiniGame
{
    const double PitR = 26, PitGap = 12, StoreW = 62, StoreH = 136, Margin = 16, RowGap = 22;
    const double BoardW = Margin * 2 + StoreW * 2 + PitGap * 2 + MancalaRules.Pits * (PitR * 2 + PitGap) - PitGap;
    const double BoardH = Margin * 2 + StoreH;
    const double StepTime = 0.13, ThinkTime = 0.7;

    static readonly Color[] SeedColors =
    {
        Color.FromRgb(230, 90, 80), Color.FromRgb(90, 170, 230), Color.FromRgb(240, 200, 80), Color.FromRgb(120, 200, 120),
        Color.FromRgb(200, 130, 220), Color.FromRgb(240, 240, 235),
    };

    readonly Canvas _board = new();
    readonly TranslateTransform _move = new();
    readonly Canvas[] _holes = new Canvas[14];
    readonly TextBlock[] _counts = new TextBlock[14];
    readonly Ellipse[] _glow = new Ellipse[14];
    readonly int[] _shown = new int[14]; // what the pits show while a sowing plays out
    readonly DragHandle _handle;
    readonly List<string> _delivered = new();
    MancalaRules _rules = new();
    DuelChannel _duel = null!;
    Vec2 _origin;
    bool _placed, _busy, _demo, _rematchAsked;
    double _think = -1;
    int _session = -1, _gameNo, _winStreak, _lossStreak;

    public MancalaGame(IGameHost host) : base(host)
    {
        _board.RenderTransform = _move;
        for (int i = 0; i < 14; i++)
        {
            _holes[i] = new Canvas { IsHitTestVisible = false };
            _counts[i] = new TextBlock { FontFamily = Fx.Font, FontSize = 13, FontWeight = FontWeight.Black, IsHitTestVisible = false, Width = 40, TextAlignment = TextAlignment.Center };
            _glow[i] = new Ellipse { StrokeThickness = 3, IsHitTestVisible = false, IsVisible = false };
        }
        _handle = new DragHandle(host, Id, Title);
        Layer.Children.Add(_board);
        Layer.Children.Add(_handle.Visual);
        _duel = new DuelChannel("mc", Host.Lan.Send);
        DrawBoard();
        Show();
    }

    public override string Id => "mancala";
    public override string Title => "Mancala";

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
        s.Rotor.Children.Add(Art.At(new Rectangle { Width = 22, Height = 12, RadiusX = 6, RadiusY = 6, Fill = Art.Brush("#8B5A2B") }, -11, -6));
        foreach (double x in new[] { -5.0, 1.0, 7.0 }) s.Rotor.Children.Add(Art.Circle(x - 1, 0, 2.2, Art.Brush("#4A2E14")));
        s.Rotor.Children.Add(Art.Circle(-6, -1, 1.2, Art.Brush("#F0C850")));
        s.Rotor.Children.Add(Art.Circle(0, 1, 1.2, Art.Brush("#E65A50")));
        return s;
    }

    public override HudInfo Hud
    {
        get
        {
            int mine = _rules.Score(Me), theirs = _rules.Score(1 - Me);
            string line = _rules.Over ? (IsGuest ? L.T("Game over · click the board to ask for a rematch") : L.T("Game over · click the board for a new game"))
                : _busy ? L.T("Sowing…")
                : MyTurn ? L.T("Your turn · click one of your pits (the bottom row)")
                : L.F("{0}'s turn", Rival);
            return new HudInfo($"{mine}–{theirs}", line, L.F("Wins {0}", Host.Stats.Get("mancala.wins")));
        }
    }

    // ------------------------------------------------------------------ layout

    /// <summary>Where board index <paramref name="i"/> is drawn: your pits along the bottom, left to right, your store on the right.</summary>
    Vec2 SpotOf(int i)
    {
        int side = MancalaRules.SideOf(i);
        if (i == MancalaRules.Store(side)) return new Vec2(side == Me ? BoardW - Margin - StoreW / 2 : Margin + StoreW / 2, BoardH / 2);
        int pit = i - side * 7;
        double x0 = Margin + StoreW + PitGap + PitR;
        if (side == Me) return new Vec2(x0 + pit * (PitR * 2 + PitGap), BoardH - Margin - PitR - 4);
        return new Vec2(x0 + (MancalaRules.Pits - 1 - pit) * (PitR * 2 + PitGap), Margin + PitR + 4);
    }

    public override void Layout()
    {
        var a = Host.Arena;
        if (!_placed)
        {
            _placed = true;
            _origin = _handle.Saved() ?? new Vec2(a.Center.X - BoardW / 2, a.Bottom - BoardH - 12);
        }
        CheckSession();
        Place();
        Host.HudChanged();
    }

    void Place()
    {
        var a = Host.Arena;
        _origin = new Vec2(Clamp(_origin.X, a.Left + 8, Math.Max(a.Left + 8, a.Right - BoardW - 8)),
            Clamp(_origin.Y, a.Top + DragHandle.Height + 12, Math.Max(a.Top + DragHandle.Height + 12, a.Bottom - BoardH - 4)));
        _move.X = _origin.X;
        _move.Y = _origin.Y;
        _handle.Show(new Rect(_origin.X, _origin.Y, BoardW, BoardH));
    }

    public override void PositionsReset() => _placed = false;

    public override void Summon(Vec2 p)
    {
        _origin = p - new Vec2(BoardW / 2, BoardH / 2);
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
        _rules = new MancalaRules();
        _gameNo = number >= 0 ? number : _gameNo + 1;
        _busy = _rematchAsked = false;
        _think = -1;
        Array.Clear(_shown);
        for (int i = 0; i < 14; i++) _shown[i] = _rules[i];
        DrawBoard();
        Show();
        Host.HudChanged();
    }

    // ------------------------------------------------------------------ input

    public override void CollectHitShapes(List<HitShape> into)
    {
        into.Add(HitShape.Box(new Rect(_origin.X, _origin.Y, BoardW, BoardH)));
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
        var local = p - _origin;
        for (int pit = 0; pit < MancalaRules.Pits; pit++)
        {
            int i = MancalaRules.PitOf(Me, pit);
            if ((local - SpotOf(i)).Length <= PitR + 4 && _rules[i] > 0)
            {
                Sow(pit, mine: true);
                return false;
            }
        }
        return false;
    }

    public override void PointerUp(Vec2 p) => _handle.End(_origin);
    public override void PointerCancel() => _handle.Cancel();

    void AskRematch()
    {
        if (!_rematchAsked) _duel.Send("rq");
        _rematchAsked = true;
        Host.Fx.Popup(new Vec2(_origin.X + BoardW / 2, _origin.Y - 10), L.F("asked {0} for a rematch", Rival), Colors.White, 18, 1.3);
    }

    // ------------------------------------------------------------------ sowing

    /// <summary>Plays a pit for the side to move and shows the seeds going round, one pit at a time.</summary>
    void Sow(int pit, bool mine)
    {
        int ply = _rules.Ply;
        var sowing = _rules.Play(pit);
        if (sowing == null) return;
        if (mine && LanOn) _duel.Send(string.Create(CultureInfo.InvariantCulture, $"mv|{ply}|{pit}"));
        if (mine) Host.Stats.Add("mancala.sown", sowing.Drops.Count);
        _busy = true;
        _shown[sowing.From] = 0;
        Draw(sowing.From);
        Host.Sound.Play("click", 0.3, 1.1);
        double t = 0.1;
        foreach (int drop in sowing.Drops)
        {
            int at = drop;
            Anims.After(t += StepTime, () =>
            {
                _shown[at]++;
                Draw(at);
                Host.Sound.Play("plop", 0.25, 1.3 + Rng.NextDouble() * 0.3);
            });
        }
        if (sowing.Captured > 0)
        {
            int landed = sowing.Drops[^1], from = sowing.CapturedFrom, store = MancalaRules.Store(MancalaRules.SideOf(landed));
            Anims.After(t += 0.35, () =>
            {
                var target = _origin + SpotOf(store);
                foreach (int k in new[] { landed, from }) Host.Fx.Burst(_origin + SpotOf(k), SeedColors, 8, 160, 0, 4, 0.4);
                _shown[store] += _shown[landed] + _shown[from];
                _shown[landed] = _shown[from] = 0;
                Draw(landed);
                Draw(from);
                Draw(store);
                Host.Fx.Popup(target - new Vec2(0, 50), $"+{sowing.Captured}", Themes.Themed(Themes.ClassicGold), 22, 1);
                Host.Sound.Play("score", 0.5, 1.1);
                if (mine)
                {
                    Host.Stats.Add("mancala.captured", sowing.Captured);
                    if (sowing.Captured >= 10) Host.Stats.Add("mancala.bigcaptures");
                }
            });
        }
        Anims.After(t + 0.25, () => Settled(sowing));
        Host.Wake();
    }

    void Settled(MancalaRules.Sowing sowing)
    {
        _busy = false;
        for (int i = 0; i < 14; i++) _shown[i] = _rules[i];
        Show();
        if (sowing.Again && !_rules.Over)
            Host.Fx.Popup(new Vec2(_origin.X + BoardW / 2, _origin.Y - 10), _rules.Turn == Me ? L.T("Last seed in your store · go again!") : L.F("{0} goes again", Rival), Colors.White, 18, 1.1);
        if (_rules.Over) GameOver();
        else if (!LanOn && !MyTurn) _think = ThinkTime;
        Host.HudChanged();
    }

    void GameOver()
    {
        int mine = _rules.Score(Me), theirs = _rules.Score(1 - Me);
        var at = new Vec2(_origin.X + BoardW / 2, _origin.Y - 30);
        if (_rules.Winner == Me)
        {
            Host.Stats.Add("mancala.wins");
            if (LanOn) Host.Stats.Add("lan.wins");
            else if (CpuLevel >= 3) Host.Stats.Add("mancala.hardwins");
            string sub = L.F("{0}–{1}", mine, theirs);
            if (!LanOn && LevelStep(won: true) is string up) sub += " · " + up;
            Host.Fx.Popup(at, L.T("YOU WIN!"), Themes.Themed(Themes.ClassicGold), 42, 2.6, sub);
            Host.Fx.Burst(at, Themes.Current.Confetti, 40, 520, 700, 7, 1.1);
            Host.Sound.Play("best", 0.8);
        }
        else if (_rules.Winner < 0)
        {
            Host.Fx.Popup(at, L.T("DRAW"), Colors.White, 38, 2.4, L.F("{0}–{1}", mine, theirs));
            Host.Sound.Play("board", 0.5);
        }
        else
        {
            string sub = L.F("{0}–{1}", mine, theirs);
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
            _origin = _handle.Move(Host.Pointer, new Size(BoardW, BoardH));
            Place();
        }
        bool duel = DuelUpdate(dt);
        bool thinking = _think >= 0;
        if (thinking && !_busy && (_think -= dt) < 0)
        {
            _think = -1;
            if (!LanOn && !_rules.Over && !MyTurn) Sow(CpuPick(), mine: false);
        }
        if (_demo && MyTurn && !_busy && _think < 0) Sow(_rules.BestPit(2, Rng), mine: true);
        return Anims.Update(dt) || _handle.Dragging || _busy || thinking || duel;
    }

    /// <summary>The computer's pit at its level; Easy picks at random a third of the time.</summary>
    int CpuPick()
    {
        int level = Math.Clamp(CpuLevel, 1, 4);
        if (level == 1 && Rng.NextDouble() < 0.33)
        {
            var legal = _rules.LegalPits().ToList();
            return legal[Rng.Next(legal.Count)];
        }
        return _rules.BestPit(MancalaRules.Depths[level - 1], Rng);
    }

    bool DuelUpdate(double dt)
    {
        if (!LanOn) return false;
        _delivered.Clear();
        while (Host.Lan.TryReceive(out var msg)) _duel.Handle(msg, _delivered);
        foreach (var body in _delivered)
        {
            var f = body.Split('|');
            if (f[0] == "mv" && f.Length == 3 && int.TryParse(f[1], out int ply) && int.TryParse(f[2], out int pit))
            {
                if (ply == _rules.Ply && !MyTurn && !_rules.Over) Sow(pit, mine: false);
            }
            else if (f[0] == "ng" && f.Length == 2 && IsGuest && int.TryParse(f[1], out int n)) NewGame(n);
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
    }

    // ------------------------------------------------------------------ drawing

    void DrawBoard()
    {
        _board.Children.Clear();
        var wood = Color.FromRgb(139, 90, 43);
        _board.Children.Add(Art.At(new Rectangle { Width = BoardW + 4, Height = BoardH + 4, RadiusX = 30, RadiusY = 30, Fill = Art.Brush(70, 0, 0, 0) }, 2, 4));
        _board.Children.Add(Art.At(new Rectangle
        {
            Width = BoardW, Height = BoardH, RadiusX = 30, RadiusY = 30, Stroke = Art.Brush(Art.Blend(wood, Colors.Black, 0.45)), StrokeThickness = 2,
            Fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Art.Blend(wood, Colors.White, 0.12), 0), new GradientStop(Art.Blend(wood, Colors.Black, 0.12), 1) },
            },
        }, 0, 0));
        for (int k = 0; k < 5; k++) // the grain
            _board.Children.Add(Art.PathOf($"M20,{Art.F(22 + k * 30)} Q{Art.F(BoardW / 2)},{Art.F(14 + k * 31)} {Art.F(BoardW - 20)},{Art.F(24 + k * 29)}", null, Art.Brush(40, 60, 30, 10), 1.2));
        var hole = Art.Brush(Art.Blend(wood, Colors.Black, 0.55));
        for (int i = 0; i < 14; i++)
        {
            var c = SpotOf(i);
            bool store = i is 6 or 13;
            double w = store ? StoreW : PitR * 2, h = store ? StoreH - 16 : PitR * 2;
            _board.Children.Add(Art.At(new Ellipse { Width = w, Height = h, Fill = hole, Stroke = Art.Brush(60, 0, 0, 0), StrokeThickness = 2 }, c.X - w / 2, c.Y - h / 2));
            _glow[i].Width = w + 6;
            _glow[i].Height = h + 6;
            Art.At(_glow[i], c.X - w / 2 - 3, c.Y - h / 2 - 3);
            _board.Children.Add(_glow[i]);
            _board.Children.Add(_holes[i]);
            bool top = !store && MancalaRules.SideOf(i) != Me;
            Art.At(_counts[i], c.X - 20, store ? c.Y + h / 2 - 20 : top ? c.Y - PitR - 19 : c.Y + PitR + 1);
            _board.Children.Add(_counts[i]);
        }
    }

    void Show()
    {
        for (int i = 0; i < 14; i++) Draw(i);
    }

    /// <summary>A pit's seeds, heaped in a little spiral, and its count; your pits you can play glow.</summary>
    void Draw(int i)
    {
        var into = _holes[i];
        into.Children.Clear();
        var c = SpotOf(i);
        bool store = i is 6 or 13;
        int n = _shown[i];
        for (int k = 0; k < n; k++)
        {
            double a = k * 2.39996, d = Math.Sqrt(k + 0.5) * (store ? 5.8 : 4.8);
            double x = c.X + Math.Cos(a) * d * (store ? 0.7 : 1), y = c.Y + Math.Sin(a) * d * (store ? 1.6 : 1);
            var color = SeedColors[(i * 7 + k * 3) % SeedColors.Length];
            into.Children.Add(Art.At(new Ellipse { Width = 10, Height = 8.5, Fill = Art.Brush(color), Stroke = Art.Brush(Art.Blend(color, Colors.Black, 0.45)), StrokeThickness = 0.8 }, x - 5, y - 4.25));
        }
        _counts[i].Text = n.ToString(CultureInfo.InvariantCulture);
        _counts[i].Foreground = Art.Brush(store ? Themes.Current.Gold : Color.FromRgb(250, 236, 210));
        bool playable = !store && MyTurn && !_busy && MancalaRules.SideOf(i) == Me && _rules[i] > 0;
        _glow[i].IsVisible = playable;
        _glow[i].Stroke = Art.Brush(Color.FromArgb(200, Themes.Current.Gold.R, Themes.Current.Gold.G, Themes.Current.Gold.B));
    }

    public override void ThemeChanged() => Show();
}
