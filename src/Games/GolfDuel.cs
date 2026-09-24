using System.Collections.Generic;
using System.Globalization;
using Avalonia.Media;
using DeskArcade.Engine;
using DeskArcade.Net;

namespace DeskArcade.Games;

/// <summary>
/// Mini Golf match play over the LAN (<see cref="GolfMatch"/>). Each player putts on their own course and
/// they take turns stroke by stroke. Finished strokes travel as reliable events ("st|hole|strokes|sunk|par",
/// "nm" when the host starts a new match and "rq" when the guest asks for one, through a
/// <see cref="DuelChannel"/>); the moving ball streams as
/// "gdg|x|y|angle", its offset from the cup in fractions of the arena, and shows up as a ghost ball
/// around the other player's own cup.
/// </summary>
public sealed partial class GolfGame
{
    const double GhostEvery = 1.0 / 30;

    readonly List<string> _delivered = new();
    DuelChannel _duel = null!;
    Ghost _rivalBall = null!;
    GolfMatch _match = new(true);
    int _session = -1;
    bool _strokeOpen, _ghostWasMoving, _rematchAsked;
    double _ghostT;

    bool DuelOn => Host.Lan.Connected;
    string Rival => Host.Lan.PeerName;
    bool Waiting => DuelOn && !_match.Over && !_match.MyTurn;

    /// <summary>The scoreboard's chip in a match: the other player, and whose putt it is.</summary>
    public override Opponent? Opponent => DuelOn ? new(Rival, false, 0, _match.Over ? null : _match.MyTurn) : null;

    void DuelSetup()
    {
        _duel = new DuelChannel("gd", Host.Lan.Send);
        _rivalBall = new Ghost(MakeBall(R), R);
        _rivalBall.AddTo(Layer);
    }

    HudInfo DuelHud => new(
        $"{_match.MyHoles}–{_match.TheirHoles}",
        _match.Over ? (DuelHost ? L.T("Match over · putt to play again") : L.F("Match over · putt to ask {0} for a rematch", Rival))
            : _match.MyTurn ? L.F("Hole {0}/{1} vs {2} · your putt", _match.Hole, GolfMatch.Holes, Rival)
            : _match.MySunk ? L.F("Hole {0}/{1} · you're in · {2} is still putting", _match.Hole, GolfMatch.Holes, Rival)
            : L.F("Hole {0}/{1} · {2} is putting", _match.Hole, GolfMatch.Holes, Rival),
        L.F("vs {0}", Rival));

    /// <summary>A new LAN session (or the end of one) starts a fresh round and match; the host putts first.</summary>
    void DuelCheckSession()
    {
        int session = DuelOn ? Host.Lan.Session : -1;
        if (session == _session) return;
        _session = session;
        _duel.Reset();
        _rivalBall.Hide();
        NewDuelMatch();
    }

    void NewDuelMatch()
    {
        _match = new GolfMatch(iStartOddHoles: Host.Lan.Role != LanRole.Guest);
        _strokeOpen = _rematchAsked = false;
        if (_placed) NewRound();
        Changed();
    }

    bool DuelHost => Host.Lan.Role != LanRole.Guest;

    /// <summary>False (with a hint) when it isn't this player's putt.</summary>
    bool DuelMayPutt()
    {
        if (!DuelOn) return true;
        if (_match.Over)
        {
            // only the host starts a match, so two "new match" messages can never cross
            if (DuelHost)
            {
                NewDuelMatch();
                _duel.Send("nm");
            }
            else
            {
                if (!_rematchAsked) _duel.Send("rq");
                _rematchAsked = true;
                Host.Fx.Popup(_ball.Pos - new Vec2(0, 44), L.F("asked {0} for a rematch", Rival), Colors.White, 20, 1.2);
                return false;
            }
        }
        if (_match.MyTurn) return true;
        Host.Fx.Popup(_ball.Pos - new Vec2(0, 44), _match.MySunk ? L.T("you're in · wait for the next hole") : L.F("{0}'s putt", Rival),
            Colors.White, 20, 1.0);
        return false;
    }

    void DuelPutted() => _strokeOpen = DuelOn;

    /// <summary>
    /// Our stroke is decided (the ball stopped, or dropped): apply it here and send it. A ball that drops in
    /// with no stroke open (a penalty drop, or a window moving under it) is reported all the same, so the
    /// match never loses a sunk ball.
    /// </summary>
    void DuelStrokeDone(bool sunk)
    {
        if (!DuelOn || !_strokeOpen && !sunk) return;
        _strokeOpen = false;
        int hole = _match.Hole;
        _duel.Send(string.Create(CultureInfo.InvariantCulture, $"st|{hole}|{_strokes}|{(sunk ? 1 : 0)}|{_par}"));
        ApplyStroke(byMe: true, _strokes, sunk, _par);
    }

    void DuelUpdate(double dt)
    {
        WaitingLook();
        if (!DuelOn)
        {
            _rivalBall.Hide();
            return;
        }
        if (_strokeOpen && BallReady)
        {
            _ball.Place(_ball.Pos); // stop it here, so a ball still creeping can't drop in after the stroke was sent
            DuelStrokeDone(false);
        }
        SendGhost(dt);

        _delivered.Clear();
        while (Host.Lan.TryReceive(out var msg))
        {
            if (_duel.Handle(msg, _delivered)) continue;
            var f = msg.Split('|');
            if (f[0] == "gdg" && f.Length == 4) ShowGhost(P(f[1]), P(f[2]), P(f[3]));
        }
        foreach (var body in _delivered)
        {
            var f = body.Split('|');
            if (f[0] == "nm") NewDuelMatch();
            else if (f[0] == "rq" && DuelHost && _match.Over)
            {
                NewDuelMatch();
                _duel.Send("nm");
            }
            else if (f[0] == "st" && f.Length == 5 && (int)P(f[1]) == _match.Hole)
                ApplyStroke(byMe: false, (int)P(f[2]), f[3] == "1", (int)P(f[4]));
        }
        _duel.Tick(dt);
        _rivalBall.Update(dt);
    }

    void ApplyStroke(bool byMe, int strokes, bool sunk, int par)
    {
        int hole = _match.Hole;
        bool wasMyTurn = _match.MyTurn;
        int? result = _match.Stroke(byMe, strokes, sunk, par);
        if (!byMe && sunk)
            Host.Fx.Popup(_cup - new Vec2(0, 140), L.F("{0} is in", Rival), Colors.White, 22, 1.4,
                strokes == 1 ? L.F("{0} stroke · par {1}", strokes, par) : L.F("{0} strokes · par {1}", strokes, par));
        if (result is int r)
        {
            var at = new Vec2(Host.Arena.Center.X, Host.Arena.Top + Host.Arena.Height * 0.3);
            Host.Fx.Popup(at, r > 0 ? L.F("You win hole {0}", hole) : r < 0 ? L.F("{0} wins hole {1}", Rival, hole) : L.F("Hole {0} halved", hole),
                r > 0 ? Gold : Colors.White, 30, 1.8, L.F("holes {0}–{1}", _match.MyHoles, _match.TheirHoles));
            if (_match.Over) DuelOver();
        }
        else if (!byMe && !wasMyTurn && _match.MyTurn) TurnCue();
        Changed();
    }

    /// <summary>The putt has come round to this player: the ball swells once, with a soft sound.</summary>
    void TurnCue()
    {
        Host.Sound.Play("pop", 0.4, 1.4);
        Anims.Add(0.5, k => _ballSprite.Scale = 1 + 0.3 * k, Ease.Pulse, () => _ballSprite.Scale = 1);
    }

    /// <summary>While the other player putts, our ball sits dimmed a little, so it is clear whose go it is.</summary>
    void WaitingLook()
    {
        double opacity = Waiting ? 0.7 : 1;
        if (_ballSprite.Opacity != opacity) _ballSprite.Opacity = opacity;
    }

    void DuelOver()
    {
        var a = Host.Arena;
        var at = new Vec2(a.Center.X, a.Top + a.Height * 0.3 + 80);
        string sub = L.F("holes {0}–{1} · putt for a rematch", _match.MyHoles, _match.TheirHoles);
        if (_match.Won == true)
        {
            Host.Stats.Add("golf.duelwins");
            Host.Stats.Add("lan.wins");
            Host.Fx.Popup(at, L.T("YOU WIN THE MATCH!"), Gold, 40, 3.0, sub);
            Host.Fx.Burst(at, Themes.Current.Confetti, 44, 540, 700, 7, 1.1);
            Host.Sound.Play("best", 0.8);
        }
        else
        {
            Host.Fx.Popup(at, _match.Won == false ? L.F("{0} WINS THE MATCH", Rival) : L.T("MATCH HALVED"), Colors.White, 36, 3.0, sub);
            Host.Sound.Play(_match.Won == false ? "buzzer" : "score", 0.45);
        }
    }

    /// <summary>Streams our ball while it moves, relative to our cup; one last update once it stops.</summary>
    void SendGhost(double dt)
    {
        bool moving = !_ball.Asleep || _sinking || _aiming;
        if (!moving && !_ghostWasMoving) return;
        if ((_ghostT += dt) < GhostEvery && moving) return;
        _ghostT = 0;
        _ghostWasMoving = moving;
        var a = Host.Arena;
        Host.Lan.Send(string.Create(CultureInfo.InvariantCulture,
            $"gdg|{(_ball.Pos.X - _cup.X) / a.Width:0.####}|{(_ball.Pos.Y - _cup.Y) / a.Height:0.####}|{_ball.Angle:0.#}"));
    }

    void ShowGhost(double nx, double ny, double angle)
    {
        var a = Host.Arena;
        var at = new Vec2(Clamp(_cup.X + nx * a.Width, a.Left + R, a.Right - R), Clamp(_cup.Y + ny * a.Height, a.Top + R, a.Bottom - R));
        _rivalBall.Show(at, angle, Rival);
    }

    static double P(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
}
