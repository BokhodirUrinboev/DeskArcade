using System.Collections.Generic;
using System.Globalization;
using Avalonia.Media;
using DeskArcade.Engine;
using DeskArcade.Net;

namespace DeskArcade.Games;

/// <summary>
/// Cannon Castles over the LAN. Each player sees their own castle on the left and the co-worker's on the right, and
/// they take turns, one ball each. The shooter's screen flies the ball against its own desk and decides what it hit;
/// reliable events go through a <see cref="DuelChannel"/>: "sh|shot|castle|block|wind" once a ball stops (see
/// <see cref="CannonRules.ShotMessage"/>, with the wind for the next shot), "ng|game" when the host starts a new game
/// and "rq" when the guest asks for one. Every game opens calm, so both screens start alike without a message. The
/// flying ball streams as "cng|u|v" (<see cref="CannonRules.GhostOut"/>) and shows up as a ghost ball flying from
/// the co-worker's cannon to yours.
/// </summary>
public sealed partial class CannonGame
{
    const double GhostEvery = 1.0 / 30;

    readonly List<string> _delivered = new();
    DuelChannel _duel = null!;
    Ghost _rivalBall = null!;
    int _session = -1;
    bool _rematchAsked;
    double _ghostT;
    Vec2 _ghostAt;

    bool DuelOn => Host.Lan.Connected;
    bool DuelHost => Host.Lan.Role != LanRole.Guest;

    void DuelSetup()
    {
        _duel = new DuelChannel("cn", Host.Lan.Send);
        _rivalBall = new Ghost(NewBall(7), 14);
        _rivalBall.AddTo(Layer);
    }

    /// <summary>A new LAN session (or the end of one) starts a fresh game and tally; the host shoots first.</summary>
    void DuelCheckSession()
    {
        int session = DuelOn ? Host.Lan.Session : -1;
        if (session == _session) return;
        _session = session;
        _duel.Reset();
        _rivalBall.Hide();
        _wins = _losses = 0;
        _gameNo = 0;
        NewGame();
    }

    /// <summary>Our ball is away: from now on it streams to the other screen.</summary>
    void DuelFired() => _ghostT = GhostEvery;

    /// <summary>Our ball stopped: the other screen records the same shot, with the wind we drew for its turn.</summary>
    void DuelShotLanded(Impact impact, double nextWind)
    {
        _duel.Send(CannonRules.ShotMessage(_rules.Shots, 0, impact.Castle, impact.Block, nextWind));
        SendGhost(impact.At);
    }

    void DuelNewGameSent() => _duel.Send(string.Create(CultureInfo.InvariantCulture, $"ng|{_gameNo}"));

    void DuelAskRematch()
    {
        if (!_rematchAsked) _duel.Send("rq");
        _rematchAsked = true;
        Host.Fx.Popup(_rules.Castles[0].CannonPivot - new Vec2(0, GrabR + 24), L.F("asked {0} for a rematch", Rival), Colors.White, 20, 1.2);
    }

    /// <summary>Messages, resends and the ghost ball; true while any of it still needs frames.</summary>
    bool DuelUpdate(double dt)
    {
        if (!DuelOn)
        {
            _rivalBall.Hide();
            return false;
        }
        if (_flight is { } f && _shooter == 0 && (_ghostT += dt) >= GhostEvery)
        {
            _ghostT = 0;
            SendGhost(f.Pos);
        }

        _delivered.Clear();
        while (Host.Lan.TryReceive(out var msg))
        {
            if (_duel.Handle(msg, _delivered)) continue;
            var parts = msg.Split('|');
            if (parts[0] == "cng" && parts.Length == 3) ShowGhost(P(parts[1]), P(parts[2]));
        }
        foreach (var body in _delivered)
        {
            if (CannonRules.TryReadShot(body, out int shot, out int castle, out int block, out double wind))
            {
                // their shot on their turn, in step with ours: a stale or doubled one is dropped
                if (shot == _rules.Shots && _rules.Turn == 1 && _flight == null)
                {
                    _rivalBall.Hide();
                    Apply(1, castle, block, wind, null, castle < 0 ? ImpactKind.Ground : ImpactKind.Block);
                }
                continue;
            }
            var parts = body.Split('|');
            if (parts[0] == "ng" && parts.Length == 2 && !DuelHost) NewGame((int)P(parts[1]));
            else if (parts[0] == "rq" && DuelHost && _rules.Over)
            {
                NewGame();
                DuelNewGameSent();
            }
        }
        _duel.Tick(dt);
        return _rivalBall.Update(dt) || _duel.Pending > 0;
    }

    void SendGhost(Vec2 ball)
    {
        var (u, v) = CannonRules.GhostOut(ball, _rules.Castles[0].CannonPivot, _rules.Castles[1].CannonPivot, Host.Arena.Height);
        Host.Lan.Send(string.Create(CultureInfo.InvariantCulture, $"cng|{u:0.####}|{v:0.####}"));
    }

    /// <summary>Their ball, drawn on its way from their cannon (on our right) to ours.</summary>
    void ShowGhost(double u, double v)
    {
        var a = Host.Arena;
        var at = CannonRules.GhostIn(u, v, _rules.Castles[1].CannonPivot, _rules.Castles[0].CannonPivot, a.Height);
        _ghostAt = new Vec2(Clamp(at.X, a.Left, a.Right), Clamp(at.Y, a.Top, a.Bottom));
        _rivalBall.Show(_ghostAt, 0, Rival);
    }

    static double P(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
}
