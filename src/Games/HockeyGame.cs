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
/// Over the LAN the other player's mallet replaces the CPU: the host runs the physics and sends the
/// state; the guest sends only its mallet and draws the host's state mirrored, so both play from the left.
/// Positions cross the wire as fractions of the host's arena, so the screens may differ in size.
/// </summary>
public sealed class HockeyGame : MiniGame
{
    const double PuckR = 22, MalletR = 36, PostR = 7, Step = 1.0 / 240, Reach = MalletR + 12;
    const double MaxPuck = 2600, Friction = 0.35, WallBounce = 0.88, MalletBounce = 0.92, GoalFraction = 0.34;
    const int WinGoals = 7;

    static readonly Color Gold = Color.FromRgb(255, 209, 102);
    static readonly Color Blue = Color.FromRgb(77, 163, 255);
    static readonly Color Red = Color.FromRgb(255, 92, 108);
    static readonly Color[] Confetti = { Gold, Blue, Red, Colors.White };

    readonly Line _centerLine = new()
    {
        Stroke = Art.Brush(55, 255, 255, 255), StrokeThickness = 2, StrokeDashArray = new AvaloniaList<double> { 6, 8 }, IsHitTestVisible = false,
    };
    readonly Ellipse _centerCircle = new() { Width = 180, Height = 180, Stroke = Art.Brush(55, 255, 255, 255), StrokeThickness = 2, IsHitTestVisible = false };
    readonly Rectangle _myGoal = new() { Width = 6, Fill = Art.Brush(Color.FromArgb(210, Blue.R, Blue.G, Blue.B)), IsHitTestVisible = false };
    readonly Rectangle _cpuGoal = new() { Width = 6, Fill = Art.Brush(Color.FromArgb(210, Red.R, Red.G, Red.B)), IsHitTestVisible = false };
    readonly Sprite[] _posts = new Sprite[4];
    readonly TextBlock _scoreText = new()
    {
        FontFamily = Fx.Font, FontSize = 15, FontWeight = FontWeight.Bold, Foreground = Brushes.White, TextAlignment = TextAlignment.Center,
    };
    readonly Border _scorePanel;
    readonly Sprite _puckSprite = MakePuck();
    readonly Sprite _meSprite = MakeMallet(Blue);
    readonly Sprite _cpuSprite = MakeMallet(Red);
    readonly Dictionary<string, double> _lastSound = new();

    Vec2 _puck, _puckVel, _me, _meVel, _cpu, _cpuVel, _grabOffset;
    int _myGoals, _cpuGoals, _level = 1, _serveSide = -1;
    double _time, _acc, _serveIn = -1, _stuckT, _puckAge, _aimCpu, _aimMe, _cpuBackOff;
    bool _placed, _holding, _matchOver, _demo, _puckOnCpuSide;

    // LAN: the rival's mallet as last reported by the guest, in host-normalized coordinates
    const double SendEvery = 1.0 / 60;
    Vec2 _remoteN;
    bool _remoteSeen, _wasLan;
    double _sendT;

    public HockeyGame(IGameHost host) : base(host)
    {
        _scorePanel = new Border
        {
            Width = 150, CornerRadius = new CornerRadius(10), Background = Art.Brush(200, 18, 20, 28), Padding = new Thickness(8, 3),
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
    }

    public override string Id => "hockey";
    public override string Title => "Air Hockey";
    public override bool SupportsLan => true;

    bool LanOn => Host.Lan.Connected;
    bool IsGuest => LanOn && Host.Lan.Role == LanRole.Guest;
    bool IsLanHost => LanOn && Host.Lan.Role == LanRole.Host;
    string Rival => LanOn ? Host.Lan.PeerName : L.T("CPU");

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        s.Rotor.Children.Add(Art.Circle(-3, -2, 7, Art.Brush(Blue), Art.Brush("#1C3F66"), 1));
        s.Rotor.Children.Add(Art.Circle(-3, -2, 3, Art.Brush("#1C3F66")));
        s.Rotor.Children.Add(Art.Circle(6, 6, 4, Art.Brush("#1D2129"), Art.Brush("#AEB6C2"), 1));
        return s;
    }

    public override HudInfo Hud => new(
        $"{_myGoals}–{_cpuGoals}",
        _matchOver ? L.T("Match over · grab your mallet for a rematch")
            : LanOn ? L.F("First to {0} · vs {1} over LAN · drag your blue mallet", WinGoals, Host.Lan.PeerName)
            : L.F("First to {0} · CPU level {1} · drag your blue mallet", WinGoals, _level),
        L.F("Wins {0}", Host.Settings.HockeyWins));

    double GoalTop => Host.Arena.Center.Y - Host.Arena.Height * GoalFraction / 2;
    double GoalBottom => Host.Arena.Center.Y + Host.Arena.Height * GoalFraction / 2;
    double HomeInset => Math.Max(110, Host.Arena.Width * 0.08);
    double CpuSpeed => Math.Min(1350, 620 + 110 * _level);

    // ------------------------------------------------------------------ match flow

    public override void Layout()
    {
        var a = Host.Arena;
        if (!_placed)
        {
            _placed = true;
            _me = new Vec2(a.Left + HomeInset, a.Center.Y);
            _cpu = new Vec2(a.Right - HomeInset, a.Center.Y);
            NewMatch();
        }
        if (LanOn != _wasLan)
        {
            _wasLan = LanOn; // a LAN match starts fresh, and so does the CPU match after it
            _remoteSeen = false;
            NewMatch();
        }
        _me = ClampSide(_me, false);
        _cpu = ClampSide(_cpu, true);
        if (!a.Deflate(PuckR).Contains(_puck.ToPoint())) PlacePuck(_serveSide);
        DrawTable();
        Draw();
        Host.HudChanged();
    }

    public override void Deactivate() => _holding = false;

    void NewMatch()
    {
        _myGoals = _cpuGoals = 0;
        _matchOver = false;
        PlacePuck(-1);
        UpdateScoreText();
        Host.HudChanged();
    }

    void PlacePuck(int side)
    {
        var a = Host.Arena;
        _puck = new Vec2(a.Center.X + side * a.Width * 0.22, a.Center.Y);
        _puckVel = default;
        _puckAge = 0;
        _stuckT = 0;
    }

    void Goal(bool playerScored)
    {
        var a = Host.Arena;
        var mouth = new Vec2(playerScored ? a.Right - 40 : a.Left + 40, Clamp(_puck.Y, GoalTop, GoalBottom));
        if (playerScored)
        {
            _myGoals++;
            Host.Stats.Add("hockey.goals");
        }
        else _cpuGoals++;
        GoalFx(playerScored, mouth);

        _puckVel = default;
        _serveSide = playerScored ? 1 : -1; // whoever conceded gets the puck
        _serveIn = 0.9;
        _puckSprite.IsVisible = false;
        if (_myGoals >= WinGoals || _cpuGoals >= WinGoals) MatchOver();
        UpdateScoreText();
        Host.HudChanged();
    }

    void GoalFx(bool playerScored, Vec2 mouth)
    {
        Host.Fx.Burst(mouth, playerScored ? new[] { Blue, Gold, Colors.White } : new[] { Red, Colors.White }, 30, 480, 500, 6, 0.8);
        Host.Fx.Popup(mouth + new Vec2(playerScored ? -90 : 90, -60), L.T("GOAL!"), playerScored ? Gold : Red, 38, 1.3,
            playerScored ? L.T("you score") : LanOn ? L.F("{0} scores", Rival) : L.T("CPU scores"));
        Host.Sound.Play(playerScored ? "score" : "buzzer", playerScored ? 0.8 : 0.35);
    }

    void MatchOver()
    {
        _matchOver = true;
        bool won = _myGoals > _cpuGoals;
        var a = Host.Arena;
        var at = new Vec2(a.Center.X, a.Top + a.Height * 0.3);
        if (LanOn) LanMatchOverFx(won, at);
        else if (won)
        {
            Host.Settings.HockeyWins++;
            _level++;
            Host.Stats.Add("hockey.wins");
            Host.Stats.Max("hockey.level", _level);
            Host.SaveSettings();
            Host.Fx.Popup(at, L.T("YOU WIN!"), Gold, 42, 2.6, L.F("{0}–{1} · the CPU gets faster", _myGoals, _cpuGoals));
            Host.Fx.Burst(at, Confetti, 44, 540, 700, 7, 1.1);
            Host.Sound.Play("best", 0.8);
        }
        else
        {
            Host.Fx.Popup(at, L.T("CPU WINS"), Colors.White, 38, 2.4, L.F("{0}–{1} · grab your mallet for a rematch", _myGoals, _cpuGoals));
            Host.Sound.Play("buzzer", 0.45);
        }
    }

    void LanMatchOverFx(bool won, Vec2 at)
    {
        if (won)
        {
            Host.Stats.Add("hockey.lanwins");
            Host.Stats.Add("lan.wins");
            Host.Fx.Popup(at, L.T("YOU WIN!"), Gold, 42, 2.6, L.F("{0}–{1} vs {2}", _myGoals, _cpuGoals, Rival));
            Host.Fx.Burst(at, Confetti, 44, 540, 700, 7, 1.1);
            Host.Sound.Play("best", 0.8);
        }
        else
        {
            Host.Fx.Popup(at, L.F("{0} WINS", Rival), Colors.White, 38, 2.4, L.F("{0}–{1} · grab your mallet for a rematch", _myGoals, _cpuGoals));
            Host.Sound.Play("buzzer", 0.45);
        }
    }

    // ------------------------------------------------------------------ input

    public override void CollectHitShapes(List<HitShape> into) => into.Add(HitShape.Circle(_me, Reach));

    public override bool PointerDown(Vec2 p, bool right)
    {
        if ((p - _me).Length > Reach) return false;
        if (_matchOver)
        {
            if (IsGuest) Host.Lan.Send("r|"); // the host restarts the match
            else NewMatch();
        }
        _holding = true;
        _grabOffset = _me - p;
        return true;
    }

    public override void PointerUp(Vec2 p) => _holding = false;

    public override void Summon(Vec2 p)
    {
        if (_holding) return;
        _me = ClampSide(p, false);
        _meVel = default;
        Draw();
    }

    Vec2 ClampSide(Vec2 p, bool cpu)
    {
        var a = Host.Arena;
        double cx = a.Center.X;
        p.X = cpu ? Clamp(p.X, cx + MalletR, a.Right - MalletR) : Clamp(p.X, a.Left + MalletR, cx - MalletR);
        p.Y = Clamp(p.Y, a.Top + MalletR, a.Bottom - MalletR);
        return p;
    }

    // ------------------------------------------------------------------ simulation

    public override bool Update(double dt)
    {
        _time += dt;
        if (IsGuest) return GuestUpdate(dt);
        if (IsLanHost) ReadGuest();
        bool cpuSide = _puck.X > Host.Arena.Center.X;
        if (cpuSide != _puckOnCpuSide)
        {
            _puckOnCpuSide = cpuSide; // each time the puck changes sides, pick a new spot to shoot at
            _aimCpu = Rng.NextDouble() * 2 - 1;
            _aimMe = Rng.NextDouble() * 2 - 1;
        }
        Vec2 meFrom = _me, cpuFrom = _cpu;
        Vec2 meTo = _holding ? ClampSide(Host.Pointer + _grabOffset, false)
            : _demo && !_matchOver ? MoveToward(_me, ClampSide(AiTarget(_me, false), false), CpuSpeed * dt)
            : _me;
        if (_cpuBackOff > 0) _cpuBackOff -= dt;
        Vec2 cpuTarget = _matchOver || _serveIn > 0 || _cpuBackOff > 0 ? new Vec2(Host.Arena.Right - HomeInset, Host.Arena.Center.Y) : AiTarget(_cpu, true);
        Vec2 cpuTo = IsLanHost
            ? _remoteSeen ? ClampSide(FromNorm(_remoteN), true) : _cpu
            : MoveToward(_cpu, ClampSide(cpuTarget, true), CpuSpeed * dt);
        meTo = KeepOffPinnedPuck(meTo, false);
        cpuTo = KeepOffPinnedPuck(cpuTo, true);

        if (dt > 0)
        {
            _meVel = (meTo - meFrom) / dt;
            if (_meVel.Length > 4000) _meVel *= 4000 / _meVel.Length;
            _cpuVel = (cpuTo - cpuFrom) / dt;
            if (_cpuVel.Length > 4000) _cpuVel *= 4000 / _cpuVel.Length;
        }

        _acc += dt;
        int steps = (int)(_acc / Step);
        _acc -= steps * Step;
        for (int i = 1; i <= steps; i++)
        {
            double k = (double)i / steps;
            _me = meFrom + (meTo - meFrom) * k; // move mallets smoothly through the sub-steps
            _cpu = cpuFrom + (cpuTo - cpuFrom) * k;
            if (_serveIn <= 0) SimStep(Step);
        }
        _me = meTo = KeepOffPinnedPuck(meTo, false);
        _cpu = cpuTo = KeepOffPinnedPuck(cpuTo, true);

        bool busy = _holding || _demo || (cpuTo - cpuFrom).Length > 0.05 || _puckVel.Length > 0 || _serveIn > 0 || _puckAge < 0.3;
        if (_serveIn > 0 && (_serveIn -= dt) <= 0)
        {
            _serveIn = -1;
            PlacePuck(_matchOver ? 0 : _serveSide);
            _puckSprite.IsVisible = true;
        }
        UnstickPuck(dt);
        _puckAge += dt;
        Draw();
        if (IsLanHost) SendState(dt);
        return busy || LanOn;
    }

    void SimStep(double h)
    {
        var a = Host.Arena;
        _puckVel *= 1 - Friction * h;
        if (_puckVel.Length < 15) _puckVel = default;
        _puck += _puckVel * h;

        HitMallet(_me, _meVel);
        HitMallet(_cpu, _cpuVel);

        double gTop = GoalTop, gBottom = GoalBottom;
        bool inMouth = _puck.Y > gTop && _puck.Y < gBottom;
        foreach (var post in new[] { new Vec2(a.Left, gTop), new Vec2(a.Left, gBottom), new Vec2(a.Right, gTop), new Vec2(a.Right, gBottom) })
            HitPoint(post, PostR, WallBounce);

        // closed box: every edge bounces, except the goal mouths on the left and right
        if (_puck.X - PuckR < a.Left)
        {
            if (inMouth)
            {
                if (_puck.X <= a.Left + 2) Goal(false);
            }
            else
            {
                _puck.X = a.Left + PuckR;
                if (_puckVel.X < 0) Bounce(ref _puckVel.X);
            }
        }
        else if (_puck.X + PuckR > a.Right)
        {
            if (inMouth)
            {
                if (_puck.X >= a.Right - 2) Goal(true);
            }
            else
            {
                _puck.X = a.Right - PuckR;
                if (_puckVel.X > 0) Bounce(ref _puckVel.X);
            }
        }
        if (_puck.Y - PuckR < a.Top)
        {
            _puck.Y = a.Top + PuckR;
            if (_puckVel.Y < 0) Bounce(ref _puckVel.Y);
        }
        else if (_puck.Y + PuckR > a.Bottom)
        {
            _puck.Y = a.Bottom - PuckR;
            if (_puckVel.Y > 0) Bounce(ref _puckVel.Y);
        }
    }

    void Bounce(ref double v)
    {
        PlayThrottled("rim", Math.Min(0.5, Math.Abs(v) / 3000), 1.9);
        v = -v * WallBounce;
    }

    void HitMallet(Vec2 m, Vec2 mv)
    {
        Vec2 d = _puck - m;
        double dist = d.Length, min = PuckR + MalletR;
        if (dist >= min) return;
        Vec2 n = dist < 1e-6 ? new Vec2(1, 0) : d / dist;
        _puck = m + n * min;
        double vn = Vec2.Dot(_puckVel - mv, n);
        if (vn >= 0) return;
        _puckVel -= n * ((1 + MalletBounce) * vn);
        double speed = _puckVel.Length;
        if (speed > MaxPuck) _puckVel *= MaxPuck / speed;
        PlayThrottled("board", Math.Min(0.9, -vn / 1800), 1.35);
    }

    void HitPoint(Vec2 c, double r, double bounce)
    {
        Vec2 d = _puck - c;
        double dist = d.Length, min = PuckR + r;
        if (dist >= min || dist < 1e-6) return;
        Vec2 n = d / dist;
        _puck = c + n * min;
        double vn = Vec2.Dot(_puckVel, n);
        if (vn >= 0) return;
        _puckVel -= n * ((1 + bounce) * vn);
        PlayThrottled("rim", Math.Min(0.6, -vn / 2000), 1.5);
    }

    /// <summary>
    /// Where a computer-controlled mallet wants to be: defend its goal while the puck is away, otherwise
    /// get behind the puck and drive through it toward the other goal.
    /// </summary>
    Vec2 AiTarget(Vec2 mallet, bool cpu)
    {
        var a = Host.Arena;
        double side = cpu ? 1 : -1;
        bool puckOnMySide = (_puck.X - a.Center.X) * side > 0;
        var home = new Vec2(cpu ? a.Right - HomeInset : a.Left + HomeInset, Clamp(_puck.Y, GoalTop + 20, GoalBottom - 20));
        if (!puckOnMySide || _serveIn > 0) return home;
        if (_puckVel.X * side > 300 && (mallet.X - _puck.X) * side > 0) return home; // puck already coming at us fast: block

        double aim = cpu ? _aimCpu : _aimMe;
        double aimY = a.Center.Y + aim * (GoalBottom - GoalTop) * 0.4;
        if (Math.Abs(aim) > 0.8) aimY = aim > 0 ? 2 * a.Bottom - aimY : 2 * a.Top - aimY; // bank it off the edge
        var goal = new Vec2(cpu ? a.Left : a.Right, aimY);
        Vec2 dir = (goal - _puck).Normalized();
        Vec2 behind = _puck - dir * (PuckR + MalletR - 4);
        if (Vec2.Dot(mallet - _puck, dir) > 0)
        {
            // on the wrong side of the puck: swing around it
            Vec2 perp = new(-dir.Y, dir.X);
            double around = Vec2.Dot(mallet - _puck, perp) >= 0 ? 1 : -1;
            return behind + perp * (around * (PuckR + MalletR + 12));
        }
        return (mallet - behind).Length < 24 ? _puck + dir * 60 : behind;
    }

    /// <summary>
    /// A puck pressed against an edge can't be pushed any further, so a mallet driven into it stops at
    /// contact instead of sliding over it. Without this a mallet parked in a corner swallows the puck.
    /// </summary>
    Vec2 KeepOffPinnedPuck(Vec2 m, bool cpu)
    {
        if (!_puckSprite.IsVisible) return m;
        var a = Host.Arena;
        bool inMouth = _puck.Y > GoalTop && _puck.Y < GoalBottom;
        bool pinned = _puck.Y <= a.Top + PuckR + 1 || _puck.Y >= a.Bottom - PuckR - 1 ||
                      !inMouth && (_puck.X <= a.Left + PuckR + 1 || _puck.X >= a.Right - PuckR - 1);
        Vec2 d = m - _puck;
        double min = PuckR + MalletR;
        if (!pinned || d.Length >= min) return m;
        Vec2 n = d.Length < 1e-6 ? (new Vec2(a.Center.X, a.Center.Y) - _puck).Normalized() : d / d.Length;
        return ClampSide(_puck + n * min, cpu);
    }

    /// <summary>A puck parked out of reach (e.g. in a corner) drifts back toward the middle.</summary>
    void UnstickPuck(double dt)
    {
        if (_serveIn > 0 || _puckVel.Length > 30 || _matchOver)
        {
            _stuckT = 0;
            return;
        }
        var a = Host.Arena;
        bool cornered = _puck.Y < a.Top + MalletR * 1.4 || _puck.Y > a.Bottom - MalletR * 1.4 ||
                        _puck.X < a.Left + MalletR * 1.4 || _puck.X > a.Right - MalletR * 1.4;
        bool cpuSide = _puck.X > a.Center.X;
        if (!cornered && (!cpuSide || LanOn)) return; // a human rival can reach the puck on their side
        // the CPU leaning on a cornered puck frees it quickly; anything else gets a few seconds to play it
        bool cpuPinning = cornered && cpuSide && !LanOn && (_cpu - _puck).Length < PuckR + MalletR + 8;
        if ((_stuckT += dt) < (cpuPinning ? 0.8 : 3)) return;
        _stuckT = 0;
        if (cpuSide && !LanOn) _cpuBackOff = 1.0; // step aside so the puck can come out
        _puckVel = (new Vec2(a.Center.X, a.Center.Y) - _puck).Normalized() * 420;
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

    /// <summary>Host: "s|puckX|puckY|puckShown|malletX|malletY|hostGoals|guestGoals|over".</summary>
    void SendState(double dt)
    {
        if ((_sendT += dt) < SendEvery) return;
        _sendT = 0;
        Vec2 puck = ToNorm(_puck), me = ToNorm(_me);
        Host.Lan.Send($"s|{F(puck.X)}|{F(puck.Y)}|{(_puckSprite.IsVisible ? 1 : 0)}|{F(me.X)}|{F(me.Y)}|{_myGoals}|{_cpuGoals}|{(_matchOver ? 1 : 0)}");
    }

    /// <summary>Guest: move our mallet locally, send it, and draw the host's latest state mirrored.</summary>
    bool GuestUpdate(double dt)
    {
        _me = _holding ? ClampSide(Host.Pointer + _grabOffset, false)
            : _demo && !_matchOver ? MoveToward(_me, ClampSide(AiTarget(_me, false), false), CpuSpeed * dt)
            : _me;
        if ((_sendT += dt) >= SendEvery)
        {
            _sendT = 0;
            Vec2 n = Mirror(ToNorm(_me));
            Host.Lan.Send($"m|{F(n.X)}|{F(n.Y)}");
        }

        string? last = null;
        while (Host.Lan.TryReceive(out var msg))
            if (msg.StartsWith("s|", StringComparison.Ordinal)) last = msg;
        if (last?.Split('|') is { Length: >= 9 } f)
        {
            Vec2 puck = FromNorm(Mirror(new Vec2(P(f[1]), P(f[2]))));
            _puckVel = (puck - _puck) / Math.Max(dt, 1e-3); // only the demo AI reads it
            _puck = puck;
            _puckSprite.IsVisible = f[3] == "1";
            _cpu = FromNorm(Mirror(new Vec2(P(f[4]), P(f[5]))));
            int rival = (int)P(f[6]), mine = (int)P(f[7]);
            bool over = f[8] == "1";
            var a = Host.Arena;
            if (mine > _myGoals) Host.Stats.Add("hockey.goals");
            if (mine > _myGoals || rival > _cpuGoals)
                GoalFx(mine > _myGoals, new Vec2(mine > _myGoals ? a.Right - 40 : a.Left + 40, Clamp(_puck.Y, GoalTop, GoalBottom)));
            bool changed = mine != _myGoals || rival != _cpuGoals || over != _matchOver;
            _myGoals = mine;
            _cpuGoals = rival;
            if (over && !_matchOver) LanMatchOverFx(mine > rival, new Vec2(a.Center.X, a.Top + a.Height * 0.3));
            _matchOver = over;
            if (changed)
            {
                UpdateScoreText();
                Host.HudChanged();
            }
        }
        _me = KeepOffPinnedPuck(_me, false);
        _puckAge = 1;
        Draw();
        return true;
    }

    static Vec2 MoveToward(Vec2 from, Vec2 to, double maxStep)
    {
        Vec2 d = to - from;
        double len = d.Length;
        return len <= maxStep ? to : from + d * (maxStep / len);
    }

    // ------------------------------------------------------------------ visuals

    void DrawTable()
    {
        var a = Host.Arena;
        _centerLine.StartPoint = new Point(a.Center.X, a.Top);
        _centerLine.EndPoint = new Point(a.Center.X, a.Bottom);
        Canvas.SetLeft(_centerCircle, a.Center.X - 90);
        Canvas.SetTop(_centerCircle, a.Center.Y - 90);
        double h = GoalBottom - GoalTop;
        _myGoal.Height = _cpuGoal.Height = h;
        Canvas.SetLeft(_myGoal, a.Left);
        Canvas.SetTop(_myGoal, GoalTop);
        Canvas.SetLeft(_cpuGoal, a.Right - 6);
        Canvas.SetTop(_cpuGoal, GoalTop);
        _posts[0].Set(new Vec2(a.Left + 3, GoalTop));
        _posts[1].Set(new Vec2(a.Left + 3, GoalBottom));
        _posts[2].Set(new Vec2(a.Right - 3, GoalTop));
        _posts[3].Set(new Vec2(a.Right - 3, GoalBottom));
        Canvas.SetLeft(_scorePanel, a.Center.X - 75);
        Canvas.SetTop(_scorePanel, a.Top + 10);
    }

    void Draw()
    {
        _puckSprite.Set(_puck);
        _puckSprite.Scale = Math.Max(0.01, Math.Min(1, _puckAge / 0.25));
        _meSprite.Set(_me);
        _cpuSprite.Set(_cpu);
    }

    void UpdateScoreText() => _scoreText.Text = LanOn
        ? L.F("YOU  {0} : {1}  {2}", _myGoals, _cpuGoals, Rival.ToUpperInvariant())
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

    static Sprite MakeMallet(Color c)
    {
        var s = new Sprite { IsHitTestVisible = false };
        s.Children.Add(Art.Circle(3, 5, MalletR, Art.Brush(60, 0, 0, 0)));
        s.Children.Add(Art.Circle(0, 0, MalletR, Shade(c), Art.Brush(Art.Blend(c, Colors.Black, 0.5)), 2));
        s.Children.Add(Art.Circle(0, 0, MalletR * 0.58, Art.Brush(Art.Blend(c, Colors.Black, 0.2)), Art.Brush(80, 255, 255, 255), 1.5));
        s.Children.Add(Art.Circle(0, 0, MalletR * 0.32, Shade(Art.Blend(c, Colors.White, 0.2))));
        return s;
    }

    static Sprite MakePuck()
    {
        var s = new Sprite { IsHitTestVisible = false };
        s.Children.Add(Art.Circle(2, 4, PuckR, Art.Brush(60, 0, 0, 0)));
        s.Children.Add(Art.Circle(0, 0, PuckR, Art.Brush("#1D2129"), Art.Brush("#AEB6C2"), 2));
        s.Children.Add(Art.Circle(0, 0, PuckR * 0.62, null, Art.Brush(70, 255, 255, 255), 1.5));
        return s;
    }

    public override void DemoTick()
    {
        _demo = true;
        if (_matchOver) NewMatch();
    }
}
