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
/// Curling (see <see cref="CurlingRules"/>) on a sheet of ice laid along the taskbar: throw from the hack on the left
/// toward the house on the right. Press on your stone at the hack and drag back, away from the house: the direction and
/// the length of the pull set the line and the weight; let go to throw. While your stone slides, hold the mouse on the
/// ice ahead of it to sweep, and it slides further. Four stones a side an end, three ends; the stones nearest the
/// button score. Against the computer at four levels, or a co-worker over the LAN, whose screen settles each of their
/// throws and sends where the stones came to rest.
/// </summary>
public sealed class CurlingGame : MiniGame
{
    const double PullToSpeed = 3.2, ThinkTime = 0.9;

    readonly Canvas _sheet = new() { IsHitTestVisible = false };
    readonly Canvas _stonesLayer = new() { IsHitTestVisible = false };
    readonly Line _aim = new() { StrokeThickness = 2.5, StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 3 }, IsHitTestVisible = false, IsVisible = false };
    readonly Rectangle _power = new() { Height = 6, RadiusX = 3, RadiusY = 3, IsHitTestVisible = false, IsVisible = false };
    readonly Canvas _waiting = new() { IsHitTestVisible = false };
    readonly Dictionary<Disc, Canvas> _stoneEls = new();
    readonly List<string> _delivered = new();
    readonly DuelChannel _duel;
    CurlingRules _rules = new();
    Rect _box; // the sheet on screen
    double _scale = 1, _think = -1, _sweepT;
    bool _aiming, _sweeping, _demo, _rematchAsked;
    Vec2 _pressAt;
    int _session = -1, _gameNo, _winStreak, _lossStreak, _lastEnd = 1;

    public CurlingGame(IGameHost host) : base(host)
    {
        Layer.Children.Add(_sheet);
        Layer.Children.Add(_stonesLayer);
        Layer.Children.Add(_waiting);
        Layer.Children.Add(_aim);
        Layer.Children.Add(_power);
        _duel = new DuelChannel("cu", Host.Lan.Send);
        _rules.Collided += (_, _, speed) => Host.Sound.Play("thunk", Math.Clamp(speed / 300, 0.15, 0.7), 1.3);
    }

    public override string Id => "curling";
    public override string Title => "Curling";

    bool LanOn => Host.Lan.Connected;
    bool IsGuest => LanOn && Host.Lan.Role == LanRole.Guest;
    int Me => IsGuest ? 1 : 0;
    bool MyTurn => !_rules.Over && !_rules.Moving && _rules.Turn == Me;
    string Rival => LanOn ? Host.Lan.PeerName : L.T("CPU");

    public override bool SupportsLan => true;
    public override bool HasCpuLevels => true;
    protected override int DefaultCpuLevel => 1;
    public override Opponent? Opponent => new(Rival, !LanOn, LanOn ? 0 : CpuLevel, _rules.Over ? null : _rules.Turn == Me);

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        s.Rotor.Children.Add(Art.Circle(0, 2, 8, Art.Brush("#8E939E"), Art.Brush("#4A4F5A"), 1.2));
        s.Rotor.Children.Add(Art.At(new Rectangle { Width = 10, Height = 4, RadiusX = 2, RadiusY = 2, Fill = Art.Brush(Art.Safe(Themes.Current.Mine)) }, -5, -4));
        return s;
    }

    public override HudInfo Hud
    {
        get
        {
            string line = _rules.Over ? (IsGuest ? L.T("Game over · click the ice to ask for a rematch") : L.T("Game over · click the ice for a new game"))
                : _rules.Moving && _rules.Current?.Tag == Me ? L.T("Hold the mouse on the ice ahead of your stone to sweep")
                : MyTurn ? L.F("End {0}/{1} · {2} stones left · drag back from your stone and let go", _rules.End, CurlingRules.Ends, _rules.StonesLeft(Me))
                : L.F("End {0}/{1} · {2} to throw", _rules.End, CurlingRules.Ends, Rival);
            return new HudInfo($"{_rules.Score(Me)}–{_rules.Score(1 - Me)}", line, L.F("Wins {0}", Host.Stats.Get("curling.wins")));
        }
    }

    // ------------------------------------------------------------------ layout

    Vec2 ToScreen(Vec2 p) => new(_box.Left + p.X * _scale, _box.Top + p.Y * _scale);
    Vec2 ToSheet(Vec2 p) => new((p.X - _box.Left) / _scale, (p.Y - _box.Top) / _scale);

    public override void Layout()
    {
        var a = Host.Arena;
        double w = Math.Min(a.Width - 40, 1500);
        _scale = w / (CurlingRules.BackX + 30);
        double h = CurlingRules.Width * _scale;
        _box = new Rect(a.Center.X - w / 2, a.Bottom - h - 6, w, h);
        CheckSession();
        DrawSheet();
        Draw();
        Host.HudChanged();
    }

    public override void Deactivate()
    {
        _aiming = _sweeping = false;
        _aim.IsVisible = _power.IsVisible = false;
        Anims.Finish();
    }

    public override void Summon(Vec2 p) { } // the sheet lies along the taskbar

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
        _rules = new CurlingRules();
        _rules.Collided += (_, _, speed) => Host.Sound.Play("thunk", Math.Clamp(speed / 300, 0.15, 0.7), 1.3);
        _gameNo = number >= 0 ? number : _gameNo + 1;
        _think = -1;
        _lastEnd = 1;
        _rematchAsked = false;
        foreach (var el in _stoneEls.Values) _stonesLayer.Children.Remove(el);
        _stoneEls.Clear();
        Draw();
        Host.HudChanged();
    }

    // ------------------------------------------------------------------ input

    public override void CollectHitShapes(List<HitShape> into)
    {
        bool mine = MyTurn || _rules.Moving && _rules.Current?.Tag == Me || _rules.Over;
        if (mine) into.Add(HitShape.Box(_box.Inflate(4)));
    }

    public override bool PointerDown(Vec2 p, bool right)
    {
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
        if (_rules.Moving && _rules.Current?.Tag == Me)
        {
            _sweeping = true; // hold on the ice to sweep
            return true;
        }
        if (!MyTurn) return false;
        if ((ToSheet(p) - CurlingRules.Hack).Length > CurlingRules.StoneR * 4) return false;
        _aiming = true;
        _pressAt = p;
        return true;
    }

    public override void PointerUp(Vec2 p)
    {
        if (_sweeping)
        {
            _sweeping = false;
            _rules.Sweep(false);
            return;
        }
        if (!_aiming) return;
        _aiming = false;
        _aim.IsVisible = _power.IsVisible = false;
        var v = PullVelocity(p);
        if (v.Length < 60) return; // a tap, not a throw
        Throw(v);
    }

    public override void PointerCancel()
    {
        _aiming = _sweeping = false;
        _aim.IsVisible = _power.IsVisible = false;
        _rules.Sweep(false);
    }

    /// <summary>The throw for a pull from the press to <paramref name="p"/>: opposite the pull, faster the longer it is.</summary>
    Vec2 PullVelocity(Vec2 p)
    {
        var pull = (_pressAt - p) / _scale; // in sheet units
        var v = pull * PullToSpeed;
        if (v.X < 0) v = new Vec2(0, 0); // only toward the house
        return v.Length > CurlingRules.MaxSpeed ? v * (CurlingRules.MaxSpeed / v.Length) : v;
    }

    void Throw(Vec2 v)
    {
        int n = ThrowNumber;
        if (!_rules.Throw(v)) return;
        if (LanOn) _duel.Send(string.Create(CultureInfo.InvariantCulture, $"th|{n}|{v.X:0.###}|{v.Y:0.###}"));
        Host.Stats.Add("curling.stones");
        Host.Sound.Play("whoosh", 0.35, 0.7);
        Draw();
        Host.HudChanged();
        Host.Wake();
    }

    int ThrowNumber => (_rules.End - 1) * CurlingRules.StonesPerSide * 2 + _rules.Thrown;

    void AskRematch()
    {
        if (!_rematchAsked) _duel.Send("rq");
        _rematchAsked = true;
        Host.Fx.Popup(new Vec2(_box.Center.X, _box.Top - 20), L.F("asked {0} for a rematch", Rival), Colors.White, 18, 1.3);
    }

    // ------------------------------------------------------------------ the ice

    public override bool Update(double dt)
    {
        bool duel = DuelUpdate(dt);
        if (_aiming)
        {
            var v = PullVelocity(Host.Pointer);
            var from = ToScreen(CurlingRules.Hack);
            _aim.StartPoint = from.ToPoint();
            _aim.EndPoint = (from + v * (_scale * 0.9)).ToPoint();
            _aim.IsVisible = true;
            double k = v.Length / CurlingRules.MaxSpeed;
            _power.Width = Math.Max(4, 120 * k);
            _power.Fill = Art.Brush(Art.Blend(Color.FromRgb(80, 200, 120), Color.FromRgb(230, 70, 60), k));
            Canvas.SetLeft(_power, from.X - 20);
            Canvas.SetTop(_power, _box.Bottom - 14);
            _power.IsVisible = true;
        }
        if (_rules.Moving)
        {
            int endBefore = _rules.End, thrownBy = _rules.Current?.Tag ?? -1;
            bool sweepNow = _sweeping && thrownBy == Me && IsAhead(Host.Pointer);
            _rules.Sweep(sweepNow);
            if (sweepNow && (_sweepT += dt) > 0.12)
            {
                _sweepT = 0;
                Host.Sound.Play("rustle", 0.25, 1.6);
                Host.Fx.Burst(Host.Pointer, new[] { Colors.White, Color.FromRgb(200, 225, 255) }, 3, 80, 0, 2, 0.3);
            }
            if (_rules.Step(dt)) Settled(thrownBy, endBefore);
            Draw();
        }
        else if (_think >= 0 && (_think -= dt) < 0)
        {
            _think = -1;
            if (!LanOn && !_rules.Over && _rules.Turn != Me) Throw(_rules.CpuThrow(CpuLevel, Rng));
        }
        if (_demo && MyTurn && _think < 0 && !_aiming) Throw(_rules.CpuThrow(3, Rng));
        return _aiming || _rules.Moving || _think >= 0 || duel || Anims.Update(dt);
    }

    /// <summary>Whether the pointer is on the ice ahead of the sliding stone, where sweeping helps.</summary>
    bool IsAhead(Vec2 p)
    {
        if (_rules.Current is not { } c) return false;
        var s = ToSheet(p);
        return s.X > c.Pos.X && s.X < c.Pos.X + 160 && Math.Abs(s.Y - c.Pos.Y) < 40;
    }

    /// <summary>A throw came to rest: send the stones to the other screen if it was ours, then the end's score and the next turn.</summary>
    void Settled(int thrownBy, int endBefore)
    {
        _sweeping = false;
        if (LanOn && thrownBy == Me) SendStones(ThrowNumber - 1, endBefore);
        AfterThrow(endBefore);
    }

    void AfterThrow(int endBefore)
    {
        if (_rules.End != endBefore || _rules.Over) EndScored();
        if (_rules.Over) GameOver();
        else if (!LanOn && _rules.Turn != Me) _think = ThinkTime;
        Draw();
        Host.HudChanged();
    }

    void EndScored()
    {
        var (side, points) = _rules.LastEnd;
        var at = new Vec2(_box.Center.X, _box.Top - 30);
        if (side < 0) Host.Fx.Popup(at, L.T("BLANK END"), Colors.White, 30, 1.8, L.T("no stone in the house"));
        else if (side == Me)
        {
            Host.Fx.Popup(at, L.F("YOU SCORE {0}", points), Themes.Themed(Themes.ClassicGold), 34, 1.8, L.F("{0}–{1}", _rules.Score(Me), _rules.Score(1 - Me)));
            Host.Sound.Play("score", 0.6);
            Host.Stats.Add("curling.points", points);
            if (points >= 3) Host.Stats.Add("curling.big");
        }
        else
        {
            Host.Fx.Popup(at, L.F("{0} SCORES {1}", Rival, points), Colors.White, 30, 1.8, L.F("{0}–{1}", _rules.Score(Me), _rules.Score(1 - Me)));
            Host.Sound.Play("board", 0.5);
        }
        _lastEnd = _rules.End;
        foreach (var el in _stoneEls.Values) _stonesLayer.Children.Remove(el);
        _stoneEls.Clear();
    }

    void GameOver()
    {
        var at = new Vec2(_box.Center.X, _box.Top - 70);
        int mine = _rules.Score(Me), theirs = _rules.Score(1 - Me);
        if (LanOn) Host.RecordResult(Id, Host.Lan.PeerName, _rules.Winner == Me ? 1 : _rules.Winner < 0 ? 0 : -1);
        if (_rules.Winner == Me)
        {
            Host.Stats.Add("curling.wins");
            if (LanOn) Host.Stats.Add("lan.wins");
            else if (CpuLevel >= 3) Host.Stats.Add("curling.hardwins");
            string sub = L.F("{0}–{1}", mine, theirs);
            if (!LanOn && LevelStep(true) is string up) sub += " · " + up;
            Host.Fx.Popup(at, L.T("YOU WIN!"), Themes.Themed(Themes.ClassicGold), 42, 2.6, sub);
            Host.Fx.Burst(at, Themes.Current.Confetti, 40, 520, 700, 7, 1.1);
            Host.Sound.Play("best", 0.8);
        }
        else if (_rules.Winner < 0) Host.Fx.Popup(at, L.T("DRAW"), Colors.White, 38, 2.4, L.F("{0}–{1}", mine, theirs));
        else
        {
            string sub = L.F("{0}–{1}", mine, theirs);
            if (!LanOn && LevelStep(false) is string down) sub += " · " + down;
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

    // ------------------------------------------------------------------ LAN

    /// <summary>"th|n|vx|vy" replays a throw here; "st|n|x:y:side:out;…" is where it came to rest, which settles it.</summary>
    bool DuelUpdate(double dt)
    {
        if (!LanOn) return false;
        _delivered.Clear();
        while (Host.Lan.TryReceive(out var msg)) _duel.Handle(msg, _delivered);
        foreach (var body in _delivered)
        {
            var f = body.Split('|');
            switch (f[0])
            {
                case "th" when f.Length == 4 && I(f[1]) == ThrowNumber && _rules.Turn != Me:
                    _rules.Throw(new Vec2(P(f[2]), P(f[3])), remote: true);
                    Host.Sound.Play("whoosh", 0.3, 0.7);
                    break;
                case "st" when f.Length == 3 && I(f[1]) == ThrowNumber:
                    int endBefore = _rules.End;
                    var stones = f[2].Split(';', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split(':'))
                        .Where(x => x.Length == 4).Select(x => (P(x[0]), P(x[1]), I(x[2]), x[3] == "1")).ToList();
                    _rules.SettleFrom(stones);
                    AfterThrow(endBefore);
                    break;
                case "ng" when f.Length == 2 && IsGuest:
                    NewGame(I(f[1]));
                    break;
                case "rq" when !IsGuest && _rules.Over:
                    NewGame();
                    _duel.Send(string.Create(CultureInfo.InvariantCulture, $"ng|{_gameNo}"));
                    break;
            }
        }
        _duel.Tick(dt);
        return _duel.Pending > 0 || _rules.Moving;
    }

    /// <summary>
    /// Where our throw left the stones, for the other screen. An end that was just scored has cleared the ice, so it sends
    /// the stones as they lay when the end closed (the other screen scores the end from them).
    /// </summary>
    void SendStones(int throwNumber, int endBefore)
    {
        var stones = _rules.End != endBefore || _rules.Over ? _rules.EndStones : _rules.Stones.Select(d => (d.Pos.X, d.Pos.Y, d.Tag, d.Sunk)).ToList();
        string list = string.Join(";", stones.Select(s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1:0.##}:{s.Item2:0.##}:{s.Item3}:{(s.Item4 ? 1 : 0)}")));
        _duel.Send(string.Create(CultureInfo.InvariantCulture, $"st|{throwNumber}|{list}"));
    }

    static double P(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
    static int I(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : -1;

    public override void DemoTick()
    {
        _demo = true;
        if (_rules.Over && !IsGuest) NewGame();
    }

    // ------------------------------------------------------------------ drawing

    void DrawSheet()
    {
        _sheet.Children.Clear();
        var b = _box;
        _sheet.Children.Add(Art.At(new Rectangle
        {
            Width = b.Width, Height = b.Height, RadiusX = 10, RadiusY = 10, Stroke = Art.Brush("#8FA9C4"), StrokeThickness = 2,
            Fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromRgb(236, 244, 252), 0), new GradientStop(Color.FromRgb(214, 228, 242), 1) },
            },
        }, b.Left, b.Top));
        var c = ToScreen(CurlingRules.Button);
        foreach (var (r, color) in new[] { (CurlingRules.HouseR, "#2F6FD6"), (CurlingRules.HouseR * 0.66, "#F4F8FC"), (CurlingRules.HouseR * 0.33, "#D8333F"), (CurlingRules.HouseR * 0.1, "#F4F8FC") })
            _sheet.Children.Add(Art.Circle(c.X, c.Y, r * _scale, Art.Brush(color)));
        void V(double x, string color, double thick)
        {
            var p = ToScreen(new Vec2(x, 0));
            _sheet.Children.Add(Art.PathOf($"M{Art.F(p.X)},{Art.F(b.Top + 2)} L{Art.F(p.X)},{Art.F(b.Bottom - 2)}", null, Art.Brush(color), thick));
        }
        V(CurlingRules.HogX, "#D8333F", 3);
        V(CurlingRules.Button.X, "#6A7D93", 1.2);
        V(CurlingRules.BackX, "#6A7D93", 1.5);
        _sheet.Children.Add(Art.PathOf($"M{Art.F(b.Left + 20)},{Art.F(c.Y)} L{Art.F(b.Right - 20)},{Art.F(c.Y)}", null, Art.Brush(80, 106, 125, 147), 1));
        var hack = ToScreen(CurlingRules.Hack - new Vec2(26, 0));
        _sheet.Children.Add(Art.At(new Rectangle { Width = 8 * _scale, Height = 22 * _scale, RadiusX = 2, RadiusY = 2, Fill = Art.Brush("#2A2E38") }, hack.X, hack.Y - 11 * _scale));
    }

    void Draw()
    {
        var seen = new HashSet<Disc>();
        foreach (var d in _rules.Stones)
        {
            if (d.Sunk) continue;
            seen.Add(d);
            if (!_stoneEls.TryGetValue(d, out var el))
            {
                el = Stone(d.Tag == Me);
                _stoneEls[d] = el;
                _stonesLayer.Children.Add(el);
            }
            var at = ToScreen(d.Pos);
            Canvas.SetLeft(el, at.X);
            Canvas.SetTop(el, at.Y);
        }
        foreach (var gone in _stoneEls.Keys.Where(d => !seen.Contains(d)).ToList())
        {
            var el = _stoneEls[gone];
            _stoneEls.Remove(gone);
            Anims.Add(0.35, k => el.Opacity = 1 - k, Ease.OutQuad, () => _stonesLayer.Children.Remove(el));
        }
        // the next stone waits at the hack for whoever throws it
        _waiting.Children.Clear();
        if (!_rules.Over && !_rules.Moving && _rules.StonesLeft(_rules.Turn) > 0)
        {
            var w = Stone(_rules.Turn == Me);
            var at = ToScreen(CurlingRules.Hack);
            Canvas.SetLeft(w, at.X);
            Canvas.SetTop(w, at.Y);
            w.Opacity = _rules.Turn == Me ? 1 : 0.6;
            _waiting.Children.Add(w);
        }
        _aim.Stroke = Art.Brush(Art.Safe(Themes.Current.Mine));
    }

    /// <summary>A granite stone seen from above with its coloured handle, drawn around (0, 0) at the sheet's scale.</summary>
    Canvas Stone(bool mine)
    {
        double r = CurlingRules.StoneR * _scale;
        var handle = Art.Safe(mine ? Themes.Current.Mine : Themes.Current.Rival);
        var c = new Canvas { IsHitTestVisible = false };
        c.Children.Add(Art.Circle(1.5, 2, r, Art.Brush(60, 0, 0, 0)));
        c.Children.Add(Art.Circle(0, 0, r, Art.Brush("#9AA0AB"), Art.Brush("#555B66"), 1.5));
        c.Children.Add(Art.Circle(0, 0, r * 0.62, Art.Brush("#B7BCC5")));
        c.Children.Add(Art.At(new Rectangle { Width = r * 1.2, Height = r * 0.5, RadiusX = r * 0.2, RadiusY = r * 0.2, Fill = Art.Brush(handle), Stroke = Art.Brush(Art.Blend(handle, Colors.Black, 0.4)), StrokeThickness = 1 }, -r * 0.6, -r * 0.25));
        return c;
    }

    public override void ThemeChanged()
    {
        foreach (var el in _stoneEls.Values) _stonesLayer.Children.Remove(el);
        _stoneEls.Clear();
        Draw();
    }
}
