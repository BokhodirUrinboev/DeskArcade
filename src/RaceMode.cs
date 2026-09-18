using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using Avalonia.Threading;
using DeskArcade.Engine;
using DeskArcade.Net;

namespace DeskArcade;

/// <summary>
/// Race mode over the LAN for round-based games (<see cref="MiniGame.Race"/>): when either player starts a
/// round the other's starts too, both see the rival's live score, and when both rounds are over the higher
/// score wins. Only scores cross the link. Games report their rounds through <see cref="IGameHost.RoundStarted"/>
/// and <see cref="IGameHost.RoundEnded"/>.
/// </summary>
public sealed class RaceMode
{
    readonly OverlayWindow _w;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    // Each side numbers its own race rounds 1, 2, 3…; my n-th round is compared with the rival's n-th.
    // A player may start the next round before the other finishes this one, so finals are kept by number.
    readonly Dictionary<int, int> _mine = new(), _theirs = new();
    readonly HashSet<int> _announced = new();
    int _round, _rivalRound = -1, _rivalScore;
    bool _rivalActive, _remoteStart;

    public RaceMode(OverlayWindow w)
    {
        _w = w;
        _timer.Tick += (_, _) => Tick();
    }

    MiniGame? Game => _w.Lan.Connected && _w.Current?.Race != null ? _w.Current : null;

    /// <summary>Called when the LAN link or the current game changes.</summary>
    public void Reset()
    {
        _round = 0;
        _rivalRound = -1;
        _mine.Clear();
        _theirs.Clear();
        _announced.Clear();
        _rivalScore = 0;
        _rivalActive = false;
        if (Game != null) _timer.Start();
        else
        {
            _timer.Stop();
            _w.SetRaceLabel(null);
        }
    }

    /// <summary>The current game started a round: count it, and unless the rival asked for it, start theirs too.</summary>
    public void LocalStart()
    {
        if (Game == null) return;
        _round++;
        if (!_remoteStart) _w.Lan.Send($"go|{_round}");
    }

    public void LocalEnd(int score)
    {
        if (Game == null || _round == 0) return; // a round from before the race began doesn't count
        _mine[_round] = score;
        Tick();
    }

    // "go|round" starts the rival's round; "rs|round|score|active" and "rf|round|final" (for our last few
    // finished rounds) repeat four times a second, so a lost packet only delays the result.
    void Tick()
    {
        if (Game is not MiniGame game || game.Race is not { } race)
        {
            Reset();
            return;
        }
        var (score, active) = race;
        while (_w.Lan.TryReceive(out var msg))
        {
            var f = msg.Split('|');
            if (f.Length < 2 || !int.TryParse(f[1], out int round)) continue;
            if (f[0] == "go" && round > _round && !active)
            {
                _remoteStart = true; // StartRace reports the round back through LocalStart, which counts it
                try { game.StartRace(); }
                finally { _remoteStart = false; }
                active = true;
            }
            else if (f[0] == "rs" && f.Length == 4 && int.TryParse(f[2], out int s))
            {
                _rivalRound = round;
                _rivalScore = s;
                _rivalActive = f[3] == "1";
            }
            else if (f[0] == "rf" && f.Length == 3 && int.TryParse(f[2], out int final)) _theirs[round] = final;
        }
        _w.Lan.Send($"rs|{_round}|{score}|{(active ? 1 : 0)}");
        foreach (var (round, final) in _mine.OrderByDescending(kv => kv.Key).Take(3)) _w.Lan.Send($"rf|{round}|{final}");

        string rival = _w.Lan.PeerName;
        _w.SetRaceLabel(_round == 0 ? L.F("Race {0}: start a round and they start too", rival)
            : L.F("{0}: {1}{2}", rival, _rivalRound == _round ? _rivalScore : 0, _rivalActive ? "" : " · " + L.T("done")));

        foreach (int round in _mine.Keys.Where(r => _theirs.ContainsKey(r) && !_announced.Contains(r)).ToList())
        {
            _announced.Add(round);
            int mine = _mine[round], theirs = _theirs[round];
            bool won = mine > theirs, tie = mine == theirs;
            if (won) _w.Stats.Add("lan.wins");
            _w.RaceResult(tie ? L.T("DEAD HEAT") : won ? L.T("YOU WIN THE RACE!") : L.F("{0} WINS THE RACE", rival),
                L.F("{0} – {1}", mine, theirs), won ? Color.FromRgb(255, 209, 102) : Colors.White, won);
        }
    }
}
