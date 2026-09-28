using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Media;
using DeskArcade.Engine;
using DeskArcade.Net;

namespace DeskArcade.Games;

/// <summary>
/// Typing Race over the LAN: both players type the same text and see each other's car. The host picks the text:
/// "go|round|kind|passage" through a <see cref="DuelChannel"/> starts the race on both screens (the guest's click sends
/// "rq" to ask for one), and "gu|round" says a player gave up. Where each car is streams unreliably, five times a
/// second, as "typ|round|pos|finish" (the finish time in hundredths of a second, -1 until then); each side times its
/// own race from its own GO, so the finish times compare fairly.
/// </summary>
public sealed partial class TypingRaceGame
{
    const double ProgressEvery = 0.2, LanSlack = 1.5;

    readonly List<string> _delivered = new();
    DuelChannel _duel = null!;
    int _session = -1, _round;
    double _progressT;

    void DuelSetup() => _duel = new DuelChannel("ty", Host.Lan.Send);

    /// <summary>A new LAN session (or the end of one) drops a race under way and starts counting races afresh.</summary>
    void DuelCheckSession()
    {
        int session = LanOn ? Host.Lan.Session : -1;
        if (session == _session) return;
        _session = session;
        _duel.Reset();
        _round = 0;
        if (_phase != Phase.Idle) Host.ReleaseKeyboard(this);
        _phase = Phase.Idle;
        NewText();
    }

    void DuelRaceStarted(int round)
    {
        _round = round;
        if (!IsGuest) _duel.Send(string.Create(CultureInfo.InvariantCulture, $"go|{round}|{TypingTexts.SettingOf(_kind)}|{_passage}"));
        SendProgress();
    }

    void DuelAskRace()
    {
        _duel.Send("rq");
        Host.Fx.Popup(new Vec2(PanelRect.Center.X, PanelRect.Top - 10), L.F("asked {0} for a race", RivalName), Colors.White, 18, 1.4);
    }

    void DuelFinished() => SendProgress();

    void DuelGaveUp() => _duel.Send(string.Create(CultureInfo.InvariantCulture, $"gu|{_round}"));

    /// <summary>Over the LAN, whether the co-worker can no longer beat a finish time of <paramref name="mine"/>.</summary>
    bool RivalBeaten(double mine) => _cpu != null ? _cpu.FinishSeconds > mine : _t > mine + LanSlack;

    /// <summary>Messages, resends and the car's position; true while any of it needs frames.</summary>
    bool DuelUpdate(double dt)
    {
        if (!LanOn) return false;
        _delivered.Clear();
        while (Host.Lan.TryReceive(out var msg))
        {
            if (_duel.Handle(msg, _delivered)) continue;
            var f = msg.Split('|');
            if (f[0] != "typ" || f.Length != 4 || !Int(f[1], out int round) || round != _round || _phase == Phase.Idle) continue;
            if (!Int(f[2], out int pos) || !Int(f[3], out int finish)) continue;
            _rivalPos = Math.Clamp(pos, 0, _run.Text.Length);
            if (finish >= 0 && _rivalFinish == null)
            {
                _rivalFinish = finish / 100.0;
                _rival.Tag.Text = L.F("{0} WPM", TypingRun.WholeWpm(TypingRun.TypedLength(_run.Text), _rivalFinish.Value));
                if (_myFinish == null) Host.Sound.Play("ding", 0.35, 0.8);
                Decide();
            }
        }
        foreach (var body in _delivered)
        {
            var f = body.Split('|');
            if (f[0] == "go" && f.Length == 4 && IsGuest && Int(f[1], out int round) && round > _round && Int(f[3], out int passage))
            {
                _kind = TypingTexts.FromSetting(f[2], L.Code);
                StartRace(passage, round);
            }
            else if (f[0] == "rq" && !IsGuest && _phase is Phase.Idle or Phase.Over) StartRace(NextPassage(), _round + 1);
            else if (f[0] == "gu" && f.Length == 2 && Int(f[1], out int r) && r == _round && _phase != Phase.Idle)
            {
                _rivalGaveUp = true;
                _rival.Tag.Text = L.T("gave up");
                Decide();
            }
        }
        _duel.Tick(dt);
        if (_phase is Phase.Countdown or Phase.Racing && (_progressT += dt) >= ProgressEvery)
        {
            _progressT = 0;
            SendProgress();
        }
        return _duel.Pending > 0;
    }

    void SendProgress() => Host.Lan.Send(string.Create(CultureInfo.InvariantCulture,
        $"typ|{_round}|{_run.Pos}|{(_myFinish is { } f ? (int)Math.Round(f * 100) : -1)}"));

    static bool Int(string s, out int value) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
}
