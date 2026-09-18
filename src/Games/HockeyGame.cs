using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using DeskArcade.Engine;
using DeskArcade.Net;

namespace DeskArcade.Games;

/// <summary>
/// Air Hockey against the computer. Your whole screen is the table: the puck bounces off every edge,
/// except the goal mouths cut into the left (yours) and right (the CPU's) sides. First to 7 wins.
/// Over the LAN the other player's mallet replaces the CPU and matches form a best-of-3 series: the host
/// runs the physics and sends the state; the guest sends only its mallet and draws the host's state
/// mirrored, so both play from the left. Positions cross the wire as fractions of the host's arena, so the
/// screens may differ in size. The physics live in <see cref="HockeyTable"/>.
/// </summary>
public sealed class HockeyGame : MiniGame
{
    const double PuckR = HockeyTable.PuckR, MalletR = HockeyTable.MalletR, PostR = HockeyTable.PostR, Reach = MalletR + 12;
    const int WinGoals = 7, SeriesWins = 2;

    static readonly Color Gold = Color.FromRgb(255, 209, 102);

    readonly HockeyTable _t;
    readonly Line _centerLine = new() { StrokeThickness = 2, StrokeDashArray = new AvaloniaList<double> { 6, 8 }, IsHitTestVisible = false };
    readonly Ellipse _centerCircle = new() { Width = 180, Height = 180, StrokeThickness = 2, IsHitTestVisible = false };
    readonly Rectangle _myGoal = new() { Width = 6, IsHitTestVisible = false };
    readonly Rectangle _cpuGoal = new() { Width = 6, IsHitTestVisible = false };
    readonly Sprite[] _posts = new Sprite[4];
    readonly TextBlock _scoreText = new()
    {
        FontFamily = Fx.Font, FontSize = 15, FontWeight = FontWeight.Bold, Foreground = Brushes.White, TextAlignment = TextAlignment.Center,
    };
    readonly Border _scorePanel;
    readonly Sprite _puckSprite = new() { IsHitTestVisible = false };
    readonly Sprite _meSprite = new() { IsHitTestVisible = false };
    readonly Sprite _cpuSprite = new() { IsHitTestVisible = false };
    readonly Dictionary<string, double> _lastSound = new();

    Vec2 _grabOffset;
    int _myGoals, _cpuGoals, _serveSide = -1, _seriesMine, _seriesTheirs;
    double _time, _serveIn = -1, _puckAge;
    bool _placed, _holding, _matchOver, _demo;

    // LAN: the rival's mallet as last reported by the guest, in host-normalized coordinates
    const double SendEvery = 1.0 / 60;
    Vec2 _remoteN;
    bool _remoteSeen, _wasLan;
    double _sendT;

    public HockeyGame(IGameHost host) : base(host)
    {
        _t = new HockeyTable(host.Arena, Rng);
        _t.Goal += Goal;
        _t.Hit += (name, vol, pitch) => PlayThrottled(name, vol, pitch);
        _scorePanel = new Border
        {
            Width = 190, CornerRadius = new CornerRadius(10), Background = Art.Brush(200, 18, 20, 28), Padding = new Thickness(8, 3),
            Child = _scoreText, IsHitTestVisible = false,
        };
        Layer.Children.Add(_centerLine);
        Layer.Children.Add(_centerCircle);
        Layer.Children.Add(_myGoal);
        Layer.Children.Add(_cpuGoal);
        for (int i = 0; i < _posts.Length; i++)
        {
            _posts[i] = new Sprite { IsHitTestVisible = false };
            _posts[i].Children.Add(Art.Circle(0, 0, PostR, Art.Brush("#D9DEE5"), Art.Brush("#4A5260"), 1.5));
            Layer.Children.Add(_posts[i]);
        }
        Layer.Children.Add(_scorePanel);
        Layer.Children.Add(_puckSprite);
        Layer.Children.Add(_cpuSprite);
        Layer.Children.Add(_meSprite);
        ThemeChanged();
    }

    public override string Id => "hockey";
    public override string Title => "Air Hockey";
    public override bool SupportsLan => true;

    bool LanOn => Host.Lan.Connected;
    bool IsGuest => LanOn && Host.Lan.Role == LanRole.Guest;
    bool IsLanHost => LanOn && Host.Lan.Role == LanRole.Host;
    string Rival => LanOn ? Host.Lan.PeerName : L.T("CPU");
    static Theme Th => Themes.Current;

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        s.Rotor.Children.Add(Art.Circle(-3, -2, 7, Art.Brush(Themes.Classic.Mine), Art.Brush("#1C3F66"), 1));
        s.Rotor.Children.Add(Art.Circle(-3, -2, 3, Art.Brush("#1C3F66")));
        s.Rotor.Children.Add(Art.Circle(6, 6, 4, Art.Brush("#1D2129"), Art.Brush("#AEB6C2"), 1));
        return s;
    }

    public override HudInfo Hud => new(
        $"{_myGoals}–{_cpuGoals}",
        _matchOver ? L.T("Match over · grab your mallet for a rematch")
            : LanOn ? L.F("First to {0} · best of 3 vs {1} · drag your blue mallet", WinGoals, Host.Lan.PeerName)
            : L.F("First to {0} · CPU level {1} · drag your blue mallet", WinGoals, _t.Level),
        L.F("Wins {0}", Host.Settings.HockeyWins));

    public override void ThemeChanged()
    {
        var line = Art.Brush(Color.FromArgb(55, Th.Line.R, Th.Line.G, Th.Line.B));
        _centerLine.Stroke = line;
        _centerCircle.Stroke = line;
        _myGoal.Fill = Art.Brush(Color.FromArgb(210, Th.Mine.R, Th.Mine.G, Th.Mine.B));
        _cpuGoal.Fill = Art.Brush(Color.FromArgb(210, Th.Rival.R, Th.Rival.G, Th.Rival.B));
        Paint(_puckSprite, MakePuck());
        Paint(_meSprite, MakeMallet(Th.Mine));
        Paint(_cpuSprite, MakeMallet(Th.Rival));
    }

    static void Paint(Sprite into, IEnumerable<Control> parts)
    {
        into.Children.Clear();
        foreach (var part in parts) into.Children.Add(part);
    }

    // ------------------------------------------------------------------ match flow

    public override void Layout()
    {
        _t.Arena = Host.Arena;
        if (!_placed)
        {
            _placed = true;
            _t.Me = _t.Home(false);
            _t.Cpu = _t.Home(true);
            NewMatch();
        }
        if (LanOn != _wasLan)
        {
            _wasLan = LanOn; // a LAN match starts fresh, and so does the CPU match after it
            _remoteSeen = false;
            _seriesMine = _seriesTheirs = 0;
            NewMatch();
        }
        _t.Me = _t.ClampSide(_t.Me, false);
        _t.Cpu = _t.ClampSide(_t.Cpu, true);
        if (!Host.Arena.Deflate(PuckR).Contains(_t.Puck.ToPoint())) _t.PlacePuck(_serveSide);
        DrawTable();
        Draw();
        Host.HudChanged();
    }

    public override void Deactivate() => _holding = false;

    bool SeriesOver => _seriesMine >= SeriesWins || _seriesTheirs >= SeriesWins;

    void NewMatch()
    {
        if (SeriesOver) _seriesMine = _seriesTheirs = 0; // the last series is decided: start the next one
        _myGoals = _cpuGoals = 0;
        _matchOver = false;
        _serveIn = -1;
        _t.PuckInPlay = true;
        _puckSprite.IsVisible = true;
        _puckAge = 0;
        _t.PlacePuck(-1);
        UpdateScoreText();
        Host.HudChanged();
    }

    void Goal(bool playerScored)
    {
        var a = Host.Arena;
        var mouth = new Vec2(playerScored ? a.Right - 40 : a.Left + 40, Clamp(_t.Puck.Y, _t.GoalTop, _t.GoalBottom));
        if (playerScored)
        {
            _myGoals++;
            Host.Stats.Add("hockey.goals");
        }
        else _cpuGoals++;
        GoalFx(playerScored, mouth);

        _serveSide = playerScored ? 1 : -1; // whoever conceded gets the puck
        _serveIn = 0.9;
        _puckSprite.IsVisible = false;
        if (_myGoals >= WinGoals || _cpuGoals >= WinGoals) MatchOver();
        UpdateScoreText();
        Host.HudChanged();
    }

    void GoalFx(bool playerScored, Vec2 mouth)
    {
        Host.Fx.Burst(mouth, playerScored ? new[] { Th.Mine, Gold, Colors.White } : new[] { Th.Rival, Colors.White }, 30, 480, 500, 6, 0.8);
        Host.Fx.Popup(mouth + new Vec2(playerScored ? -90 : 90, -60), L.T("GOAL!"), playerScored ? Gold : Th.Rival, 38, 1.3,
            playerScored ? L.T("you score") : LanOn ? L.F("{0} scores", Rival) : L.T("CPU scores"));
        Host.Sound.Play(playerScored ? "score" : "buzzer", playerScored ? 0.8 : 0.35);
    }

    void MatchOver()
    {
        _matchOver = true;
        bool won = _myGoals > _cpuGoals;
        var a = Host.Arena;
        var at = new Vec2(a.Center.X, a.Top + a.Height * 0.3);
        if (LanOn)
        {
            if (won) _seriesMine++;
            else _seriesTheirs++;
            LanMatchOverFx(won, at);
        }
        else if (won)
        {
            Host.Settings.HockeyWins++;
            _t.Level++;
            Host.Stats.Add("hockey.wins");
            Host.Stats.Max("hockey.level", _t.Level);
            Host.SaveSettings();
            Host.Fx.Popup(at, L.T("YOU WIN!"), Gold, 42, 2.6, L.F("{0}–{1} · the CPU gets faster", _myGoals, _cpuGoals));
            Host.Fx.Burst(at, Th.Confetti, 44, 540, 700, 7, 1.1);
            Host.Sound.Play("best", 0.8);
        }
        else
        {
            Host.Fx.Popup(at, L.T("CPU WINS"), Colors.White, 38, 2.4, L.F("{0}–{1} · grab your mallet for a rematch", _myGoals, _cpuGoals));
            Host.Sound.Play("buzzer", 0.45);
        }
    }

    /// <summary>A LAN match is over; the series counts already include it.</summary>
    void LanMatchOverFx(bool won, Vec2 at)
    {
        if (won)
        {
            Host.Stats.Add("hockey.lanwins");
            Host.Stats.Add("lan.wins");
        }
        if (SeriesOver)
        {
            bool series = _seriesMine > _seriesTheirs;
            if (series) Host.Stats.Add("hockey.series");
            Host.Fx.Popup(at, series ? L.T("YOU WIN THE SERIES!") : L.F("{0} WINS THE SERIES", Rival), series ? Gold : Colors.White, 40, 3.0,
                L.F("{0}–{1} in matches · grab your mallet for a new series", _seriesMine, _seriesTheirs));
            if (series) Host.Fx.Burst(at, Th.Confetti, 60, 600, 700, 7, 1.3);
            Host.Sound.Play(series ? "best" : "buzzer", series ? 0.9 : 0.45);
            return;
        }
        if (won)
        {
            Host.Fx.Popup(at, L.T("YOU WIN!"), Gold, 42, 2.6, L.F("{0}–{1} vs {2} · series {3}–{4}", _myGoals, _cpuGoals, Rival, _seriesMine, _seriesTheirs));
            Host.Fx.Burst(at, Th.Confetti, 44, 540, 700, 7, 1.1);
            Host.Sound.Play("best", 0.8);
        }
        else
        {
            Host.Fx.Popup(at, L.F("{0} WINS", Rival), Colors.White, 38, 2.4, L.F("{0}–{1} · series {2}–{3} · grab your mallet for the next match",
                _myGoals, _cpuGoals, _seriesMine, _seriesTheirs));
            Host.Sound.Play("buzzer", 0.45);
        }
    }

    // ------------------------------------------------------------------ input

    public override void CollectHitShapes(List<HitShape> into) => into.Add(HitShape.Circle(_t.Me, Reach));

    public override bool PointerDown(Vec2 p, bool right)
    {
        if ((p - _t.Me).Length > Reach) return false;
        if (_matchOver)
        {
            if (IsGuest) Host.Lan.Send("r|"); // the host restarts the match
            else NewMatch();
        }
        _holding = true;
        _grabOffset = _t.Me - p;
        return true;
    }

    public override void PointerUp(Vec2 p) => _holding = false;

    public override void Summon(Vec2 p)
    {
        if (_holding) return;
        _t.Me = _t.ClampSide(p, false);
        _t.MeVel = default;
        Draw();
    }

    // ------------------------------------------------------------------ simulation

    public override bool Update(double dt)
    {
        _time += dt;
        _t.Arena = Host.Arena;
        if (IsGuest) return GuestUpdate(dt);
        if (IsLanHost) ReadGuest();
        Vec2 cpuFrom = _t.Cpu;
        Vec2 meTo = _holding ? _t.ClampSide(Host.Pointer + _grabOffset, false)
            : _demo && !_matchOver ? _t.DemoMove(dt)
            : _t.Me;
        Vec2 cpuTo = IsLanHost
            ? _remoteSeen ? _t.ClampSide(FromNorm(_remoteN), true) : _t.Cpu
            : _t.CpuMove(dt, holdHome: _matchOver || _serveIn > 0);
        _t.Advance(dt, meTo, cpuTo);

        bool busy = _holding || _demo || (_t.Cpu - cpuFrom).Length > 0.05 || _t.PuckVel.Length > 0 || _serveIn > 0 || _puckAge < 0.3;
        if (_serveIn > 0 && (_serveIn -= dt) <= 0)
        {
            _serveIn = -1;
            _t.PlacePuck(_matchOver ? 0 : _serveSide);
            _t.PuckInPlay = true;
            _puckSprite.IsVisible = true;
            _puckAge = 0;
        }
        _t.Unstick(dt, humanRival: LanOn, active: !_matchOver && _serveIn <= 0);
        _puckAge += dt;
        Draw();
        if (IsLanHost) SendState(dt);
        return busy || LanOn;
    }

    // ------------------------------------------------------------------ LAN

    Vec2 ToNorm(Vec2 p)
    {
        var a = Host.Arena;
        return new Vec2((p.X - a.Left) / a.Width, (p.Y - a.Top) / a.Height);
    }

    Vec2 FromNorm(Vec2 n)
    {
        var a = Host.Arena;
        return new Vec2(a.Left + n.X * a.Width, a.Top + n.Y * a.Height);
    }

    static Vec2 Mirror(Vec2 n) => new(1 - n.X, n.Y);

    static string F(double v) => v.ToString("0.#####", CultureInfo.InvariantCulture);

    static double P(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;

    /// <summary>Host: take the guest's mallet ("m|x|y") and rematch requests ("r|").</summary>
    void ReadGuest()
    {
        while (Host.Lan.TryReceive(out var msg))
        {
            var f = msg.Split('|');
            if (f[0] == "m" && f.Length >= 3)
            {
                _remoteN = new Vec2(P(f[1]), P(f[2]));
                _remoteSeen = true;
            }
            else if (f[0] == "r" && _matchOver) NewMatch();
        }
    }

    /// <summary>Host: "s|puckX|puckY|puckShown|malletX|malletY|hostGoals|guestGoals|over|hostSeries|guestSeries".</summary>
    void SendState(double dt)
    {
        if ((_sendT += dt) < SendEvery) return;
        _sendT = 0;
        Vec2 puck = ToNorm(_t.Puck), me = ToNorm(_t.Me);
        Host.Lan.Send($"s|{F(puck.X)}|{F(puck.Y)}|{(_puckSprite.IsVisible ? 1 : 0)}|{F(me.X)}|{F(me.Y)}|{_myGoals}|{_cpuGoals}|{(_matchOver ? 1 : 0)}|{_seriesMine}|{_seriesTheirs}");
    }

    /// <summary>Guest: move our mallet locally, send it, and draw the host's latest state mirrored.</summary>
    bool GuestUpdate(double dt)
    {
        _t.Me = _holding ? _t.ClampSide(Host.Pointer + _grabOffset, false)
            : _demo && !_matchOver ? _t.DemoMove(dt)
            : _t.Me;
        if ((_sendT += dt) >= SendEvery)
        {
            _sendT = 0;
            Vec2 n = Mirror(ToNorm(_t.Me));
            Host.Lan.Send($"m|{F(n.X)}|{F(n.Y)}");
        }

        string? last = null;
        while (Host.Lan.TryReceive(out var msg))
            if (msg.StartsWith("s|", StringComparison.Ordinal)) last = msg;
        if (last?.Split('|') is { Length: >= 11 } f)
        {
            Vec2 puck = FromNorm(Mirror(new Vec2(P(f[1]), P(f[2]))));
            _t.PuckVel = (puck - _t.Puck) / Math.Max(dt, 1e-3); // only the demo AI reads it
            _t.Puck = puck;
            _t.PuckInPlay = _puckSprite.IsVisible = f[3] == "1";
            _t.Cpu = FromNorm(Mirror(new Vec2(P(f[4]), P(f[5]))));
            int rival = (int)P(f[6]), mine = (int)P(f[7]);
            bool over = f[8] == "1";
            int seriesRival = (int)P(f[9]), seriesMine = (int)P(f[10]);
            var a = Host.Arena;
            if (mine > _myGoals) Host.Stats.Add("hockey.goals");
            if (mine > _myGoals || rival > _cpuGoals)
                GoalFx(mine > _myGoals, new Vec2(mine > _myGoals ? a.Right - 40 : a.Left + 40, Clamp(_t.Puck.Y, _t.GoalTop, _t.GoalBottom)));
            bool changed = mine != _myGoals || rival != _cpuGoals || over != _matchOver || seriesMine != _seriesMine || seriesRival != _seriesTheirs;
            _myGoals = mine;
            _cpuGoals = rival;
            _seriesMine = seriesMine;
            _seriesTheirs = seriesRival;
            if (over && !_matchOver) LanMatchOverFx(mine > rival, new Vec2(a.Center.X, a.Top + a.Height * 0.3));
            _matchOver = over;
            if (changed)
            {
                UpdateScoreText();
                Host.HudChanged();
            }
        }
        _t.Me = _t.KeepOffPinnedPuck(_t.Me, false);
        _puckAge = 1;
        Draw();
        return true;
    }

    // ------------------------------------------------------------------ visuals

    void DrawTable()
    {
        var a = Host.Arena;
        _centerLine.StartPoint = new Point(a.Center.X, a.Top);
        _centerLine.EndPoint = new Point(a.Center.X, a.Bottom);
        Canvas.SetLeft(_centerCircle, a.Center.X - 90);
        Canvas.SetTop(_centerCircle, a.Center.Y - 90);
        double h = _t.GoalBottom - _t.GoalTop;
        _myGoal.Height = _cpuGoal.Height = h;
        Canvas.SetLeft(_myGoal, a.Left);
        Canvas.SetTop(_myGoal, _t.GoalTop);
        Canvas.SetLeft(_cpuGoal, a.Right - 6);
        Canvas.SetTop(_cpuGoal, _t.GoalTop);
        _posts[0].Set(new Vec2(a.Left + 3, _t.GoalTop));
        _posts[1].Set(new Vec2(a.Left + 3, _t.GoalBottom));
        _posts[2].Set(new Vec2(a.Right - 3, _t.GoalTop));
        _posts[3].Set(new Vec2(a.Right - 3, _t.GoalBottom));
        Canvas.SetLeft(_scorePanel, a.Center.X - 95);
        Canvas.SetTop(_scorePanel, a.Top + 10);
    }

    void Draw()
    {
        _puckSprite.Set(_t.Puck);
        _puckSprite.Scale = Math.Max(0.01, Math.Min(1, _puckAge / 0.25));
        _meSprite.Set(_t.Me);
        _cpuSprite.Set(_t.Cpu);
    }

    void UpdateScoreText() => _scoreText.Text = LanOn
        ? L.F("YOU  {0} : {1}  {2}", _myGoals, _cpuGoals, Rival.ToUpperInvariant()) + "\n" + L.F("series {0}–{1}", _seriesMine, _seriesTheirs)
        : L.F("YOU  {0} : {1}  CPU", _myGoals, _cpuGoals);

    void PlayThrottled(string name, double vol, double pitch = 1)
    {
        if (_lastSound.TryGetValue(name, out double t) && _time - t < 0.05) return;
        _lastSound[name] = _time;
        Host.Sound.Play(name, vol, pitch);
    }

    static RadialGradientBrush Shade(Color c)
    {
        var brush = new RadialGradientBrush { GradientOrigin = new RelativePoint(0.35, 0.3, RelativeUnit.Relative) };
        brush.GradientStops.Add(new GradientStop(Art.Blend(c, Colors.White, 0.4), 0));
        brush.GradientStops.Add(new GradientStop(c, 0.55));
        brush.GradientStops.Add(new GradientStop(Art.Blend(c, Colors.Black, 0.35), 1));
        return brush;
    }

    static Control[] MakeMallet(Color c) => new Control[]
    {
        Art.Circle(3, 5, MalletR, Art.Brush(60, 0, 0, 0)),
        Art.Circle(0, 0, MalletR, Shade(c), Art.Brush(Art.Blend(c, Colors.Black, 0.5)), 2),
        Art.Circle(0, 0, MalletR * 0.58, Art.Brush(Art.Blend(c, Colors.Black, 0.2)), Art.Brush(80, 255, 255, 255), 1.5),
        Art.Circle(0, 0, MalletR * 0.32, Shade(Art.Blend(c, Colors.White, 0.2))),
    };

    static Control[] MakePuck() => new Control[]
    {
        Art.Circle(2, 4, PuckR, Art.Brush(60, 0, 0, 0)),
        Art.Circle(0, 0, PuckR, Art.Brush(Th.Puck), Art.Brush(Th.PuckRim), 2),
        Art.Circle(0, 0, PuckR * 0.62, null, Art.Brush(Color.FromArgb(70, Th.PuckRim.R, Th.PuckRim.G, Th.PuckRim.B)), 1.5),
    };

    public override void DemoTick()
    {
        _demo = true;
        if (_matchOver) NewMatch();
    }
}
