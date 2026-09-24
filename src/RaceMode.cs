using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using Avalonia.Threading;
using DeskArcade.Engine;
using DeskArcade.Net;

namespace DeskArcade;

/// <summary>
/// Races for round-based games (<see cref="MiniGame.Race"/>). Over the LAN: when either player starts a round the
/// other's starts too, both see the rival's live score, and when both rounds are over the higher score wins (the
/// lower one where fewer is better); only scores cross the link. Alone, with "Race the computer" on, a
/// <see cref="CpuRival"/> plays a round of its own at the game's CPU level, marks its scoring on screen, and the level
/// moves up when the player keeps winning and down when they keep losing. Like a co-worker, the computer finishes its
/// own round in its own time: a round the player ends in seconds still waits for the computer's score. Games report
/// their rounds through <see cref="IGameHost.RoundStarted"/> and <see cref="IGameHost.RoundEnded"/>.
/// </summary>
public sealed class RaceMode
{
    const double TickSeconds = 0.25;

    readonly OverlayWindow _w;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(TickSeconds) };
    // Each side numbers its own race rounds 1, 2, 3…; my n-th round is compared with the rival's n-th.
    // A player may start the next round before the other finishes this one, so finals are kept by number.
    readonly Dictionary<int, int> _mine = new(), _theirs = new();
    readonly HashSet<int> _announced = new();
    readonly Random _rng = new();
    int _round, _rivalRound = -1, _rivalScore;
    bool _rivalActive, _remoteStart;
    readonly Dictionary<int, CpuRival> _cpus = new(); // the computer's rounds, by my round number, until they finish
    int _cpuWinStreak, _cpuLossStreak, _cpuShown;
    double _lastMark;

    public RaceMode(OverlayWindow w)
    {
        _w = w;
        _timer.Tick += (_, _) => Tick();
    }

    bool LanOn => _w.Lan.Connected;
    bool CpuOn => !LanOn && _w.Settings.CpuRival;
    MiniGame? Game => _w.Current?.Race != null && (LanOn || CpuOn) ? _w.Current : null;

    /// <summary>The rival shown on the scoreboard while a race game is up: the co-worker, or the computer and its level.</summary>
    public Opponent? Opponent => Game is not { } game ? null
        : LanOn ? new Opponent(_w.Lan.PeerName, false, 0, null)
        : new Opponent(L.T("CPU"), true, game.CpuLevel, null);

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
        _cpus.Clear();
        _cpuWinStreak = _cpuLossStreak = 0;
        _shown = false;
        _timer.Stop();
        if (Game != null) Tick(); // shows the idle line; over the LAN the tick keeps the timer going
        else _w.SetRaceLabel(null);
    }

    /// <summary>A setting changed (race the computer on or off): start or stop without disturbing a race that goes on.</summary>
    public void Refresh()
    {
        if (Game == null ? _timer.IsEnabled : !_shown) Reset();
    }

    bool _shown; // the label is up for the current game

    /// <summary>The timer has work while the LAN is on or a computer round is still running or waiting for its verdict.</summary>
    bool Working => LanOn || _cpus.Count > 0 || _mine.Keys.Any(r => !_announced.Contains(r));

    /// <summary>The current game started a round: count it, and unless the rival asked for it, start theirs too.</summary>
    public void LocalStart()
    {
        if (Game is not { } game) return;
        _round++;
        if (LanOn)
        {
            if (!_remoteStart) _w.Lan.Send($"go|{_round}");
            return;
        }
        // the computer aims at the player's best when there is one, else at the game's baseline
        int reference = game.RaceBest <= 0 ? game.RaceBaseline
            : game.RaceLowerIsBetter ? Math.Min(game.RaceBaseline, game.RaceBest) : Math.Max(game.RaceBaseline, game.RaceBest);
        _cpus[_round] = new CpuRival(game.CpuLevel, reference, game.RaceLowerIsBetter, game.RaceSeconds, _rng, game.RaceMin, game.RaceMax);
        _lastMark = 0;
        _cpuShown = 0;
        Tick();
    }

    public void LocalEnd(int score)
    {
        if (Game == null || _round == 0) return; // a round from before the race began doesn't count
        _mine[_round] = score;
        Tick();
    }

    // LAN: "go|round" starts the rival's round; "rs|round|score|active" and "rf|round|final" (for our last few
    // finished rounds) repeat four times a second, so a lost packet only delays the result.
    void Tick()
    {
        if (Game is not MiniGame game || game.Race is not { } race)
        {
            Reset();
            return;
        }
        var (score, active) = race;
        _shown = true;
        string rival;
        if (LanOn)
        {
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

            rival = _w.Lan.PeerName;
            _w.SetRaceLabel(_round == 0 ? L.F("Race {0}: start a round and they start too", rival)
                : L.F("{0}: {1}{2}", rival, _rivalRound == _round ? _rivalScore : 0, _rivalActive ? "" : " · " + L.T("done")));
        }
        else
        {
            rival = L.T("CPU");
            foreach (var (round, cpu) in _cpus.ToList())
            {
                if (cpu.Done) continue;
                if (cpu.Tick(TickSeconds) && round == _round) MarkCpu(cpu);
                if (cpu.Done) _theirs[round] = cpu.Score;
            }
            foreach (int round in _cpus.Keys.Where(r => _cpus[r].Done && _theirs.ContainsKey(r) && (_announced.Contains(r) || r < _round - 1)).ToList()) _cpus.Remove(round);
            if (_cpus.TryGetValue(_round, out var current))
            {
                string level = L.T(MiniGame.LevelNames[current.Level - 1]);
                _w.SetRaceLabel(current.LowerIsBetter && !current.Done
                    ? L.F("CPU ({0}) is heading for {1}", level, current.Target)
                    : L.F("CPU ({0}): {1}", level, current.Score) + (current.Done ? " · " + L.T("done") : ""));
            }
            else _w.SetRaceLabel(L.T("Race the computer: start a round and it plays one too"));
        }

        foreach (int round in _mine.Keys.Where(r => _theirs.ContainsKey(r) && !_announced.Contains(r)).ToList())
        {
            _announced.Add(round);
            int mine = _mine[round], theirs = _theirs[round];
            bool tie = mine == theirs, won = game.RaceLowerIsBetter ? mine < theirs : mine > theirs;
            string sub = L.F("{0} – {1}", mine, theirs);
            if (LanOn)
            {
                if (won) _w.Stats.Add("lan.wins");
            }
            else
            {
                if (won) _w.Stats.Add("race.cpuwins");
                sub = AdjustLevel(game, won, tie, sub);
            }
            // the game's own "TIME!" popup sits at the usual spot at the same moment, so the computer's verdict goes a little lower
            _w.RaceResult(tie ? L.T("DEAD HEAT") : won ? L.T("YOU WIN THE RACE!") : L.F("{0} WINS THE RACE", rival),
                sub, won ? Color.FromRgb(255, 209, 102) : Colors.White, won, LanOn ? 0 : 96);
        }

        // four ticks a second only while there is something to pace or announce; otherwise the label sits still
        if (Working) { if (!_timer.IsEnabled) _timer.Start(); }
        else _timer.Stop();
    }

    /// <summary>Two wins in a row move the computer up a level, two losses in a row move it down, like the board games.</summary>
    string AdjustLevel(MiniGame game, bool won, bool tie, string sub)
    {
        if (tie || _w.Demo) return sub; // a demo playing itself must not move the player's level
        if (won)
        {
            _cpuLossStreak = 0;
            if (++_cpuWinStreak < 2 || game.CpuLevel >= MiniGame.LevelNames.Length) return sub;
            _cpuWinStreak = 0;
            game.CpuLevel++;
            return sub + " · " + L.F("the CPU moves up to {0}", L.T(MiniGame.LevelNames[game.CpuLevel - 1]));
        }
        _cpuWinStreak = 0;
        if (++_cpuLossStreak < 2 || game.CpuLevel <= 1) return sub;
        _cpuLossStreak = 0;
        game.CpuLevel--;
        return sub + " · " + L.F("the CPU goes easier: {0}", L.T(MiniGame.LevelNames[game.CpuLevel - 1]));
    }

    /// <summary>The computer scored: a ring somewhere on the desktop, like a co-worker's marker, so its play can be seen.</summary>
    void MarkCpu(CpuRival cpu)
    {
        double now = _w.Clock;
        if (now - _lastMark < 1.2) return;
        _lastMark = now;
        var a = _w.Arena;
        var hud = _w.HudBounds.Inflate(40);
        Vec2 at = default;
        for (int i = 0; i < 6; i++)
        {
            at = new Vec2(a.Left + 60 + _rng.NextDouble() * Math.Max(1, a.Width - 120), a.Top + 80 + _rng.NextDouble() * Math.Max(1, a.Height - 160));
            if (!hud.Contains(at.ToPoint())) break;
        }
        _w.Fx.Marker(at, OverlayWindow.RivalColor, 8, 36, 0.8);
        int gained = cpu.Score - _cpuShown;
        _cpuShown = cpu.Score;
        if (!cpu.LowerIsBetter && gained > 0) _w.Fx.Popup(at - new Vec2(0, 24), $"+{gained}", OverlayWindow.RivalColor, 16, 0.8);
        _w.Wake();
    }
}
