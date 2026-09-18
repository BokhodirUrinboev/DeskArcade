using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Media;
using DeskArcade.Engine;
using DeskArcade.Net;

namespace DeskArcade.Games;

/// <summary>
/// Archery over the LAN (<see cref="ArcheryMatch"/>): the players take turns, one arrow each, at their own
/// targets in the same wind. Reliable events go through a <see cref="DuelChannel"/>: "ar|points" for an
/// arrow once it lands, "nr|round|wind" when the host starts a round and "rq" when the guest asks for one.
/// The flying arrow streams as "adg|x|y|dx|dy" (its offset from the bow and its direction, mirrored so x
/// points the way the bow faces) and shows up as a ghost arrow leaving the other player's own bow.
/// </summary>
public sealed partial class ArcheryGame
{
    const double GhostEvery = 1.0 / 30;

    readonly List<string> _delivered = new();
    DuelChannel _duel = null!;
    Ghost _rivalArrow = null!;
    ArcheryMatch _match = new(true);
    int _session = -1, _duelRound, _scoreBeforeShot;
    bool _shotOpen;
    double _ghostT;

    bool DuelOn => Host.Lan.Connected;
    bool DuelHost => Host.Lan.Role != LanRole.Guest;
    string Rival => Host.Lan.PeerName;

    void DuelSetup()
    {
        _duel = new DuelChannel("ad", Host.Lan.Send);
        _rivalArrow = new Ghost(MakeArrowSprite(), 20);
        _rivalArrow.AddTo(Layer);
    }

    HudInfo DuelHud => new(
        $"{_match.MyScore}–{_match.TheirScore}",
        _match.Over ? (DuelHost ? L.T("Match over · pull the bow for a rematch") : L.F("Match over · pull the bow to ask {0} for a rematch", Rival))
            : _match.MyTurn ? L.F("Arrow {0}/{1} vs {2} · your shot · wind {3}", ArcheryMatch.Arrows - _match.MyLeft + 1, ArcheryMatch.Arrows, Rival, WindText)
            : L.F("{0} is shooting · wind {1}", Rival, WindText),
        L.F("vs {0}", Rival));

    /// <summary>A new LAN session starts a calm first round on both screens, the host shooting first.</summary>
    void DuelCheckSession()
    {
        int session = DuelOn ? Host.Lan.Session : -1;
        if (session == _session) return;
        _session = session;
        _duel.Reset();
        _rivalArrow.Hide();
        _duelRound = 0;
        if (DuelOn) StartDuelRound(1, 0);
        else if (_round > 0) NewRound();
    }

    void StartDuelRound(int round, double wind)
    {
        _duelRound = round;
        _match = new ArcheryMatch(iShootFirst: (round % 2 == 1) == DuelHost);
        _shotOpen = false;
        NewRound(wind);
    }

    /// <summary>False (with a hint) when it isn't this player's shot.</summary>
    bool DuelMayShoot()
    {
        if (!DuelOn) return true;
        if (_match.Over)
        {
            if (DuelHost) HostNewRound();
            else
            {
                _duel.Send("rq");
                Host.Fx.Popup(_bowPos - new Vec2(0, GrabR + 30), L.F("asked {0} for a rematch", Rival), Colors.White, 20, 1.4);
            }
            return false;
        }
        if (_match.MyTurn && !_shotOpen) return true;
        Host.Fx.Popup(_bowPos - new Vec2(0, GrabR + 30), L.F("{0}'s shot", Rival), Colors.White, 20, 1.0);
        return false;
    }

    void HostNewRound()
    {
        int round = _duelRound + 1;
        double wind = (Rng.NextDouble() * 2 - 1) * Math.Min(260, 60 + round * 40);
        _duel.Send(string.Create(CultureInfo.InvariantCulture, $"nr|{round}|{wind:0.#}"));
        StartDuelRound(round, wind);
    }

    void DuelFired()
    {
        if (!DuelOn) return;
        _shotOpen = true;
        _scoreBeforeShot = _roundScore;
    }

    void DuelUpdate(double dt)
    {
        if (!DuelOn)
        {
            _rivalArrow.Hide();
            return;
        }
        if (_shotOpen && !_arrows.Any(a => a.Flying))
        {
            _shotOpen = false;
            int points = _roundScore - _scoreBeforeShot;
            _duel.Send($"ar|{points}");
            Apply(byMe: true, points);
        }
        SendGhost(dt);

        _delivered.Clear();
        while (Host.Lan.TryReceive(out var msg))
        {
            if (_duel.Handle(msg, _delivered)) continue;
            var f = msg.Split('|');
            if (f[0] == "adg" && f.Length == 5) ShowGhost(P(f[1]), P(f[2]), P(f[3]), P(f[4]));
        }
        foreach (var body in _delivered)
        {
            var f = body.Split('|');
            if (f[0] == "ar" && f.Length == 2) Apply(byMe: false, (int)P(f[1]));
            else if (f[0] == "nr" && f.Length == 3) StartDuelRound((int)P(f[1]), P(f[2]));
            else if (f[0] == "rq" && DuelHost && _match.Over) HostNewRound();
        }
        _duel.Tick(dt);
        _rivalArrow.Update(dt);
    }

    void Apply(bool byMe, int points)
    {
        bool wasMyTurn = _match.MyTurn;
        _match.Arrow(byMe, points);
        if (!byMe)
            Host.Fx.Popup(_bowPos - new Vec2(0, GrabR + 40), points > 0 ? L.F("{0}: +{1}", Rival, points) : L.F("{0} missed", Rival),
                points > 0 ? Gold : Colors.White, 22, 1.3);
        if (_match.Over) DuelOver();
        else if (!byMe && !wasMyTurn && _match.MyTurn) Host.Sound.Play("pop", 0.4, 1.4); // your shot
        Changed();
    }

    void DuelOver()
    {
        var a = Host.Arena;
        var at = new Vec2(a.Center.X, a.Top + a.Height * 0.3);
        string sub = L.F("{0}–{1} · pull the bow for a rematch", _match.MyScore, _match.TheirScore);
        if (_match.Won == true)
        {
            Host.Stats.Add("archery.duelwins");
            Host.Stats.Add("lan.wins");
            Host.Fx.Popup(at, L.T("YOU WIN THE MATCH!"), Gold, 40, 3.0, sub);
            Host.Fx.Burst(at, Themes.Current.Confetti, 44, 540, 700, 7, 1.1);
            Host.Sound.Play("best", 0.8);
        }
        else
        {
            Host.Fx.Popup(at, _match.Won == false ? L.F("{0} WINS THE MATCH", Rival) : L.T("A TIE"), Colors.White, 36, 3.0, sub);
            Host.Sound.Play(_match.Won == false ? "buzzer" : "score", 0.45);
        }
    }

    /// <summary>Streams our flying arrow relative to our bow, x mirrored so it always points the way the bow faces.</summary>
    void SendGhost(double dt)
    {
        var ar = _arrows.LastOrDefault(x => x.Flying);
        if (ar == null || (_ghostT += dt) < GhostEvery) return;
        _ghostT = 0;
        var a = Host.Arena;
        var dir = ar.Vel.Normalized();
        Host.Lan.Send(string.Create(CultureInfo.InvariantCulture,
            $"adg|{(ar.Tip.X - _bowPos.X) * _face / a.Width:0.####}|{(ar.Tip.Y - _bowPos.Y) / a.Height:0.####}|{dir.X * _face:0.###}|{dir.Y:0.###}"));
    }

    void ShowGhost(double nx, double ny, double dx, double dy)
    {
        var a = Host.Arena;
        var at = new Vec2(Clamp(_bowPos.X + nx * _face * a.Width, a.Left, a.Right), Clamp(_bowPos.Y + ny * a.Height, a.Top, a.Bottom));
        _rivalArrow.Show(at, Math.Atan2(dy, dx * _face) * 180 / Math.PI, Rival);
    }

    static double P(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
}
