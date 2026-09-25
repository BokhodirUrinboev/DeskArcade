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
/// Pong on the screen edges: drag your paddle up and down the left edge and get the ball past the
/// computer's paddle on the right. First to 7. The computer plays at the level set in tray → CPU difficulty
/// (two straight wins move it up, two straight losses down), and within a session each win makes it a
/// little sharper on top. Over the LAN the other
/// player's paddle replaces the computer, like Air Hockey: the host runs the ball and sends the state
/// ("ps|…"), the guest sends its paddle ("pp|y") and draws everything mirrored, so both play from the left.
/// The physics live in <see cref="PongTable"/>.
/// </summary>
public sealed class PongGame : MiniGame, IPetPlayground
{
    const double BallR = PongTable.BallR, PaddleW = PongTable.PaddleW, PaddleH = PongTable.PaddleH, Grab = 30;
    const double SendEvery = 1.0 / 60, ServeDelay = 1.0, TrailMinSpeed = 300;
    const int WinPoints = 7, TrailLength = 5, TrailGap = 2;

    static readonly Color Gold = Color.FromRgb(255, 209, 102);

    readonly PongTable _t;
    readonly Line _net = new() { StrokeThickness = 3, StrokeDashArray = new AvaloniaList<double> { 3, 5 }, IsHitTestVisible = false };
    readonly Rectangle _mePaddle = new() { Width = PaddleW, Height = PaddleH, RadiusX = 7, RadiusY = 7, IsHitTestVisible = false };
    readonly Rectangle _themPaddle = new() { Width = PaddleW, Height = PaddleH, RadiusX = 7, RadiusY = 7, IsHitTestVisible = false };
    readonly Ellipse _ball = new() { Width = BallR * 2, Height = BallR * 2, IsHitTestVisible = false };
    readonly ScaleTransform _meSquash = new(), _themSquash = new();
    readonly Rectangle _goalFlash = new() { Width = 26, Fill = Brushes.White, Opacity = 0, IsHitTestVisible = false };
    readonly Ellipse _serveRing = new() { StrokeThickness = 3, IsVisible = false, IsHitTestVisible = false };
    readonly Ellipse[] _trail = new Ellipse[TrailLength];
    readonly Vec2[] _ballHistory = new Vec2[TrailLength * TrailGap + 1];
    readonly Dictionary<ScaleTransform, Anims.Tween> _squashes = new();
    readonly TextBlock _scoreText = new()
    {
        FontFamily = Fx.Font, FontSize = 15, FontWeight = FontWeight.Bold, Foreground = Brushes.White, TextAlignment = TextAlignment.Center,
    };
    readonly Border _scorePanel;
    readonly Dictionary<string, double> _lastSound = new();

    int _myPoints, _theirPoints, _serveTo = -1, _winStreak, _lossStreak, _historyN;
    double _time, _serveIn = ServeDelay, _grabOffset, _sendT, _remoteY = double.NaN;
    bool _placed, _holding, _matchOver, _demo, _wasLan;

    public PongGame(IGameHost host) : base(host)
    {
        _t = new PongTable(host.Arena, Rng);
        _t.Point += Point;
        _t.Hit += (name, vol, pitch) =>
        {
            PlayThrottled(name, vol, pitch);
            if (name == "board") Squash(_t.BallVel.X > 0 ? _meSquash : _themSquash); // the ball leaves the paddle that hit it
        };
        _mePaddle.RenderTransformOrigin = _themPaddle.RenderTransformOrigin = RelativePoint.Center;
        _mePaddle.RenderTransform = _meSquash;
        _themPaddle.RenderTransform = _themSquash;
        _scorePanel = new Border
        {
            Width = 190, CornerRadius = new CornerRadius(10), Background = Art.Brush(200, 18, 20, 28), Padding = new Thickness(8, 3),
            Child = _scoreText, IsHitTestVisible = false,
        };
        Layer.Children.Add(_net);
        Layer.Children.Add(_goalFlash);
        Layer.Children.Add(_scorePanel);
        Layer.Children.Add(_serveRing);
        Layer.Children.Add(_mePaddle);
        Layer.Children.Add(_themPaddle);
        for (int i = 0; i < _trail.Length; i++)
        {
            _trail[i] = new Ellipse { IsVisible = false, IsHitTestVisible = false };
            Layer.Children.Add(_trail[i]);
        }
        Layer.Children.Add(_ball);
        ThemeChanged();
    }

    public override string Id => "pong";
    public override string Title => "Pong";
    public override bool SupportsLan => true;

    bool LanOn => Host.Lan.Connected;
    bool IsGuest => LanOn && Host.Lan.Role == LanRole.Guest;
    bool IsLanHost => LanOn && Host.Lan.Role == LanRole.Host;
    string Rival => LanOn ? Host.Lan.PeerName : L.T("CPU");
    static Theme Th => Themes.Current;

    public override bool HasCpuLevels => true;
    public override Opponent? Opponent => new(Rival, !LanOn, LanOn ? 0 : CpuLevel, null);

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        s.Rotor.Children.Add(Art.At(new Rectangle { Width = 3, Height = 12, RadiusX = 1, RadiusY = 1, Fill = Art.Brush(Themes.Classic.Mine) }, -9, -6));
        s.Rotor.Children.Add(Art.At(new Rectangle { Width = 3, Height = 12, RadiusX = 1, RadiusY = 1, Fill = Art.Brush(Themes.Classic.Rival) }, 6, -2));
        s.Rotor.Children.Add(Art.Circle(0, -3, 2.5, Brushes.White));
        return s;
    }

    public override HudInfo Hud => new(
        $"{_myPoints}–{_theirPoints}",
        _matchOver ? L.T("Match over · grab your paddle for a rematch")
            : LanOn ? L.F("First to {0} vs {1} · drag your paddle", WinPoints, Host.Lan.PeerName)
            : L.F("First to {0} · CPU {1} · drag your paddle", WinPoints, L.T(LevelNames[CpuLevel - 1])),
        L.F("Wins {0}", Host.Stats.Get("pong.wins")));

    public override void ThemeChanged()
    {
        _net.Stroke = Art.Brush(Color.FromArgb(60, Th.Line.R, Th.Line.G, Th.Line.B));
        _mePaddle.Fill = Art.Brush(Art.Safe(Th.Mine));
        _themPaddle.Fill = Art.Brush(Art.Safe(Th.Rival));
        _ball.Fill = Art.Brush(Th.GolfBall);
        _ball.Stroke = Art.Brush(90, 0, 0, 0);
        _ball.StrokeThickness = 1;
        _serveRing.Stroke = Art.Brush(Color.FromArgb(200, Th.Line.R, Th.Line.G, Th.Line.B));
        foreach (var ghost in _trail) ghost.Fill = Art.Brush(Color.FromArgb(110, Th.GolfBall.R, Th.GolfBall.G, Th.GolfBall.B));
    }

    // ------------------------------------------------------------------ match flow

    public override void Layout()
    {
        _t.Arena = Host.Arena;
        if (!_placed)
        {
            _placed = true;
            NewMatch();
        }
        if (LanOn != _wasLan)
        {
            _wasLan = LanOn; // a LAN match starts fresh, and so does the CPU match after it
            _remoteY = double.NaN;
            NewMatch();
        }
        _t.MeY = _t.ClampPaddle(_t.MeY);
        _t.ThemY = _t.ClampPaddle(_t.ThemY);
        if (!Host.Arena.Contains(_t.Ball.ToPoint())) _t.Ball = new Vec2(Host.Arena.Center.X, Host.Arena.Center.Y);
        var a = Host.Arena;
        _net.StartPoint = new Point(a.Center.X, a.Top);
        _net.EndPoint = new Point(a.Center.X, a.Bottom);
        Canvas.SetLeft(_scorePanel, a.Center.X - 95);
        Canvas.SetTop(_scorePanel, a.Top + 10);
        Draw();
        Host.HudChanged();
    }

    public override void Deactivate() => _holding = false;

    void NewMatch()
    {
        _myPoints = _theirPoints = 0;
        _matchOver = false;
        _t.BallInPlay = false;
        _t.Ball = new Vec2(Host.Arena.Center.X, Host.Arena.Center.Y);
        _serveTo = -1;
        _serveIn = ServeDelay;
        if (!IsGuest) ServePulse(ServeDelay);
        UpdateScoreText();
        Host.HudChanged();
    }

    void Point(bool mine)
    {
        if (mine) _myPoints++;
        else _theirPoints++;
        PointFx(mine);
        _serveTo = mine ? 1 : -1; // serve toward whoever just lost the point
        _serveIn = ServeDelay;
        if (_myPoints >= WinPoints || _theirPoints >= WinPoints) MatchOver();
        else ServePulse(ServeDelay);
        UpdateScoreText();
        Host.HudChanged();
    }

    void PointFx(bool mine)
    {
        var a = Host.Arena;
        FlashGoal(mine);
        var at = new Vec2(mine ? a.Right - 120 : a.Left + 120, Clamp(_t.Ball.Y, a.Top + 80, a.Bottom - 80));
        Host.Fx.Popup(at, mine ? L.T("POINT!") : L.F("{0} scores", Rival), mine ? Gold : Colors.White, 30, 1.1);
        Host.Sound.Play(mine ? "score" : "buzzer", mine ? 0.7 : 0.3);
    }

    void MatchOver()
    {
        _matchOver = true;
        bool won = _myPoints > _theirPoints;
        var a = Host.Arena;
        var at = new Vec2(a.Center.X, a.Top + a.Height * 0.3);
        if (won)
        {
            Host.Stats.Add("pong.wins");
            if (LanOn) Host.Stats.Add("lan.wins");
            else
            {
                _t.Level++;
                Host.Stats.Max("pong.level", _t.Level);
            }
            string sub = LanOn ? L.F("{0}–{1} vs {2}", _myPoints, _theirPoints, Rival) : L.F("{0}–{1} · the CPU gets sharper", _myPoints, _theirPoints);
            if (!LanOn && LevelStep(won: true) is string up) sub = L.F("{0}–{1}", _myPoints, _theirPoints) + " · " + up;
            Host.Fx.Popup(at, L.T("YOU WIN!"), Gold, 42, 2.6, sub);
            Host.Fx.Burst(at, Th.Confetti, 44, 540, 700, 7, 1.1);
            Host.Sound.Play("best", 0.8);
        }
        else
        {
            string sub = L.F("{0}–{1} · grab your paddle for a rematch", _myPoints, _theirPoints);
            if (!LanOn && LevelStep(won: false) is string down)
                sub = L.F("{0}–{1}", _myPoints, _theirPoints) + " · " + down + " · " + L.T("grab your paddle for a rematch");
            Host.Fx.Popup(at, LanOn ? L.F("{0} WINS", Rival) : L.T("CPU WINS"), Colors.White, 38, 2.4, sub);
            Host.Sound.Play("buzzer", 0.45);
        }
    }

    /// <summary>
    /// Two straight wins move the CPU up a level and two straight losses move it down, like the board games;
    /// the line to say so, or null when the level stays. Demo matches leave the player's setting alone.
    /// </summary>
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

    // ------------------------------------------------------------------ input

    Rect MyPaddleRect => new(_t.MeX - PaddleW / 2 - Grab, _t.MeY - PaddleH / 2 - Grab / 2, PaddleW + Grab * 2, PaddleH + Grab);

    public override void CollectHitShapes(List<HitShape> into) => into.Add(HitShape.Box(MyPaddleRect));

    public override bool PointerDown(Vec2 p, bool right)
    {
        if (!MyPaddleRect.Contains(p.ToPoint())) return false;
        if (_matchOver)
        {
            if (IsGuest) Host.Lan.Send("pr|"); // the host restarts the match
            else NewMatch();
        }
        _holding = true;
        _grabOffset = _t.MeY - p.Y;
        return true;
    }

    public override void PointerUp(Vec2 p) => _holding = false;

    public override void Summon(Vec2 p)
    {
        if (_holding) return;
        _t.MeY = _t.ClampPaddle(p.Y);
        Draw();
    }

    // ------------------------------------------------------------------ simulation

    public override bool Update(double dt)
    {
        _time += dt;
        _t.Arena = Host.Arena;
        _t.Skill = CpuLevel;
        bool anim = Anims.Update(dt);
        if (IsGuest) return GuestUpdate(dt);
        if (IsLanHost) ReadGuest();

        double meTo = _holding ? Host.Pointer.Y + _grabOffset
            : _demo && !_matchOver ? DemoPaddle(dt)
            : _t.MeY;
        double themTo = IsLanHost ? double.IsNaN(_remoteY) ? _t.ThemY : _t.ClampPaddle(Host.Arena.Top + _remoteY * Host.Arena.Height)
            : _t.CpuMove(dt);
        _t.Advance(dt, meTo, themTo);

        if (!_t.BallInPlay && !_matchOver && (_serveIn -= dt) <= 0) _t.Serve(_serveTo);
        Draw();
        if (IsLanHost) SendState(dt);
        return !_matchOver || _holding || _demo || LanOn || anim; // idle only between matches
    }

    double DemoPaddle(double dt)
    {
        double target = _t.BallVel.X < 0 ? _t.PredictY(_t.MeX + PaddleW / 2 + BallR) : Host.Arena.Center.Y;
        double step = 900 * dt, d = target - _t.MeY;
        return Math.Abs(d) <= step ? target : _t.MeY + Math.Sign(d) * step;
    }

    // ------------------------------------------------------------------ LAN

    static string F(double v) => v.ToString("0.#####", CultureInfo.InvariantCulture);

    static double P(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;

    /// <summary>Host: the guest's paddle ("pp|y", a fraction of the height) and rematch requests ("pr|").</summary>
    void ReadGuest()
    {
        while (Host.Lan.TryReceive(out var msg))
        {
            var f = msg.Split('|');
            if (f[0] == "pp" && f.Length == 2) _remoteY = Math.Clamp(P(f[1]), 0, 1);
            else if (f[0] == "pr" && _matchOver) NewMatch();
        }
    }

    /// <summary>Host: "ps|ballX|ballY|ballShown|hostY|guestY|hostPoints|guestPoints|over", fractions of the arena.</summary>
    void SendState(double dt)
    {
        if ((_sendT += dt) < SendEvery) return;
        _sendT = 0;
        var a = Host.Arena;
        Host.Lan.Send($"ps|{F((_t.Ball.X - a.Left) / a.Width)}|{F((_t.Ball.Y - a.Top) / a.Height)}|{(_t.BallInPlay ? 1 : 0)}|" +
                      $"{F((_t.MeY - a.Top) / a.Height)}|{F((_t.ThemY - a.Top) / a.Height)}|{_myPoints}|{_theirPoints}|{(_matchOver ? 1 : 0)}");
    }

    /// <summary>Guest: move our paddle locally, send it, and draw the host's latest state mirrored.</summary>
    bool GuestUpdate(double dt)
    {
        var a = Host.Arena;
        if (_holding) _t.MeY = _t.ClampPaddle(Host.Pointer.Y + _grabOffset);
        else if (_demo && !_matchOver) _t.MeY = _t.ClampPaddle(DemoPaddle(dt));
        if ((_sendT += dt) >= SendEvery)
        {
            _sendT = 0;
            Host.Lan.Send($"pp|{F((_t.MeY - a.Top) / a.Height)}");
        }

        string? last = null;
        while (Host.Lan.TryReceive(out var msg))
            if (msg.StartsWith("ps|", StringComparison.Ordinal)) last = msg;
        if (last?.Split('|') is { Length: 9 } f)
        {
            var ball = new Vec2(a.Left + (1 - P(f[1])) * a.Width, a.Top + P(f[2]) * a.Height);
            _t.BallVel = (ball - _t.Ball) / Math.Max(dt, 1e-3); // only the demo reads it
            _t.Ball = ball;
            _t.BallInPlay = f[3] == "1";
            _t.ThemY = _t.ClampPaddle(a.Top + P(f[4]) * a.Height);
            int rival = (int)P(f[6]), mine = (int)P(f[7]);
            bool over = f[8] == "1";
            if (mine > _myPoints || rival > _theirPoints) PointFx(mine > _myPoints);
            bool changed = mine != _myPoints || rival != _theirPoints || over != _matchOver;
            _myPoints = mine;
            _theirPoints = rival;
            if (over && !_matchOver) MatchOverFromHost(); // the stats and fanfare, as on the host
            _matchOver = over;
            if (changed)
            {
                UpdateScoreText();
                Host.HudChanged();
            }
        }
        Draw();
        return true;
    }

    void MatchOverFromHost()
    {
        var a = Host.Arena;
        var at = new Vec2(a.Center.X, a.Top + a.Height * 0.3);
        if (_myPoints > _theirPoints)
        {
            Host.Stats.Add("pong.wins");
            Host.Stats.Add("lan.wins");
            Host.Fx.Popup(at, L.T("YOU WIN!"), Gold, 42, 2.6, L.F("{0}–{1} vs {2}", _myPoints, _theirPoints, Rival));
            Host.Fx.Burst(at, Th.Confetti, 44, 540, 700, 7, 1.1);
            Host.Sound.Play("best", 0.8);
        }
        else
        {
            Host.Fx.Popup(at, L.F("{0} WINS", Rival), Colors.White, 38, 2.4, L.F("{0}–{1} · grab your paddle for a rematch", _myPoints, _theirPoints));
            Host.Sound.Play("buzzer", 0.45);
        }
    }

    // ------------------------------------------------------------------ visuals

    void Draw()
    {
        Canvas.SetLeft(_mePaddle, _t.MeX - PaddleW / 2);
        Canvas.SetTop(_mePaddle, _t.MeY - PaddleH / 2);
        Canvas.SetLeft(_themPaddle, _t.ThemX - PaddleW / 2);
        Canvas.SetTop(_themPaddle, _t.ThemY - PaddleH / 2);
        Canvas.SetLeft(_ball, _t.Ball.X - BallR);
        Canvas.SetTop(_ball, _t.Ball.Y - BallR);
        _ball.IsVisible = _t.BallInPlay;
        DrawTrail();
    }

    // ------------------------------------------------------------------ animation

    /// <summary>A short trail of fading ghosts behind a fast ball, from where it was on the last few frames.</summary>
    void DrawTrail()
    {
        _ballHistory[_historyN++ % _ballHistory.Length] = _t.Ball;
        bool show = _t.BallInPlay && !Fx.ReducedMotion && _t.BallVel.Length > TrailMinSpeed && _historyN > _ballHistory.Length;
        for (int i = 0; i < _trail.Length; i++)
        {
            var ghost = _trail[i];
            ghost.IsVisible = show;
            if (!show) continue;
            var p = _ballHistory[(_historyN - 1 - (i + 1) * TrailGap + _ballHistory.Length) % _ballHistory.Length];
            double r = BallR * (0.9 - 0.12 * i);
            ghost.Width = ghost.Height = r * 2;
            ghost.Opacity = 0.5 * (1 - (double)i / _trail.Length);
            Canvas.SetLeft(ghost, p.X - r);
            Canvas.SetTop(ghost, p.Y - r);
        }
    }

    /// <summary>The edge the ball just went out over lights up and fades: the right edge for my point, the left for theirs.</summary>
    void FlashGoal(bool mine)
    {
        var a = Host.Arena;
        Canvas.SetLeft(_goalFlash, mine ? a.Right - _goalFlash.Width : a.Left);
        Canvas.SetTop(_goalFlash, a.Top);
        _goalFlash.Height = a.Height;
        Anims.Add(0.55, k => _goalFlash.Opacity = 0.8 * (1 - k), Ease.OutQuad, () => _goalFlash.Opacity = 0);
    }

    /// <summary>A paddle that just returned the ball squashes flat for an instant.</summary>
    void Squash(ScaleTransform paddle)
    {
        if (_squashes.TryGetValue(paddle, out var old)) old.Cancel();
        _squashes[paddle] = Anims.Add(0.18, k =>
        {
            paddle.ScaleX = 1 - 0.3 * k;
            paddle.ScaleY = 1 + 0.12 * k;
        }, Ease.Pulse, () => paddle.ScaleX = paddle.ScaleY = 1);
    }

    /// <summary>A ring at the net that pulses for as long as the next serve takes.</summary>
    void ServePulse(double seconds)
    {
        var a = Host.Arena;
        Canvas.SetLeft(_serveRing, a.Center.X - BallR * 2);
        Canvas.SetTop(_serveRing, a.Center.Y - BallR * 2);
        _serveRing.Width = _serveRing.Height = BallR * 4;
        _serveRing.RenderTransformOrigin = RelativePoint.Center;
        var sc = new ScaleTransform();
        _serveRing.RenderTransform = sc;
        _serveRing.IsVisible = true;
        Anims.Add(seconds, k =>
        {
            double beat = Ease.Pulse(k * 2 % 1); // two beats
            sc.ScaleX = sc.ScaleY = 0.5 + 0.5 * beat;
            _serveRing.Opacity = 0.25 + 0.75 * beat;
        }, Ease.Linear, () => _serveRing.IsVisible = false);
    }

    void UpdateScoreText() => _scoreText.Text = LanOn
        ? L.F("YOU  {0} : {1}  {2}", _myPoints, _theirPoints, Rival.ToUpperInvariant())
        : L.F("YOU  {0} : {1}  CPU", _myPoints, _theirPoints);

    void PlayThrottled(string name, double vol, double pitch = 1)
    {
        if (_lastSound.TryGetValue(name, out double t) && _time - t < 0.05) return;
        _lastSound[name] = _time;
        Host.Sound.Play(name, vol, pitch);
    }

    public override void DemoTick()
    {
        _demo = true;
        if (_matchOver && !IsGuest) NewMatch();
    }

    // ------------------------------------------------------------------ the pet keeping you company

    /// <summary>The pet runs along the bottom edge after the ball, clear of the paddles; it never touches it.</summary>
    public PetFloor PetFloor => new(Host.Arena.Left + 60, Host.Arena.Right - 60, Host.Arena.Bottom);

    public PetToy PetToy => _t.BallInPlay ? new(PetToyKind.Chase, _t.Ball, _t.BallVel, BallR) : new(PetToyKind.None);

    public bool PetTouched(PetTouch touch, Vec2 v) => false;
}
