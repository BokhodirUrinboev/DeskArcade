using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DeskArcade;

/// <summary>A game against a co-worker, as this player saw it end: +1 a win, 0 a draw, −1 a loss.</summary>
public sealed record MatchResult(string GameId, string Opponent, int Outcome, DateTime AtUtc);

/// <summary>
/// Rivalries: the record against each co-worker in every game played together over the LAN ("vs Alice: Chess 7–5,
/// Darts 3–4"), and a monthly rating across the board games for the office ladder. Games report a result through
/// <see cref="Engine.IGameHost.RecordResult"/>; a room game reports one per co-worker at the table (ahead of them is a
/// win). Only co-workers count: the computer is never a rival. Kept in rivals.json next to settings.json.
/// </summary>
public sealed class Rivalries
{
    /// <summary>The games whose results move the ladder's rating: the board games, where a win says something.</summary>
    public static readonly HashSet<string> LadderGames = new(StringComparer.Ordinal)
    {
        "chess", "checkers", "connect4", "tictactoe", "reversi", "gomoku", "seabattle", "backgammon", "mancala", "dominoes",
    };

    public const int StartRating = 1200, KFactor = 32, KeepDays = 400;

    readonly List<MatchResult> _results = new();
    readonly string _path;
    bool _dirty;

    Rivalries(string path) => _path = path;

    public event Action<MatchResult>? Recorded;

    public IReadOnlyList<MatchResult> Results => _results;

    public static Rivalries Load(string? path = null)
    {
        var r = new Rivalries(path ?? Path.Combine(Settings.DataDirectory, "rivals.json"));
        try
        {
            if (File.Exists(r._path) && JsonSerializer.Deserialize<List<MatchResult>>(File.ReadAllText(r._path)) is { } list)
                r._results.AddRange(list.Where(m => m?.Opponent != null && m.GameId != null));
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // a damaged file: start again
        }
        return r;
    }

    public static Rivalries InMemory() => new("");

    /// <summary>A co-worker's name as a rivalry keys it: trimmed, and never empty.</summary>
    public static string Key(string name) => string.IsNullOrWhiteSpace(name) ? "?" : name.Trim();

    public void Record(string gameId, string opponent, int outcome, DateTime? atUtc = null)
    {
        var result = new MatchResult(gameId, Key(opponent), Math.Sign(outcome), atUtc ?? DateTime.UtcNow);
        _results.Add(result);
        var cutoff = result.AtUtc.AddDays(-KeepDays);
        _results.RemoveAll(m => m.AtUtc < cutoff);
        _dirty = true;
        Recorded?.Invoke(result);
    }

    public void Save()
    {
        if (!_dirty || _path.Length == 0) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_results));
            _dirty = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }

    /// <summary>Wins, draws and losses against <paramref name="opponent"/>, in one game or (null) all of them.</summary>
    public (int Won, int Drawn, int Lost) Against(string opponent, string? gameId = null)
    {
        string key = Key(opponent);
        int w = 0, d = 0, l = 0;
        foreach (var m in _results)
        {
            if (!m.Opponent.Equals(key, StringComparison.OrdinalIgnoreCase) || gameId != null && m.GameId != gameId) continue;
            if (m.Outcome > 0) w++;
            else if (m.Outcome < 0) l++;
            else d++;
        }
        return (w, d, l);
    }

    /// <summary>The record against <paramref name="opponent"/> game by game, the most played first.</summary>
    public IEnumerable<(string GameId, int Won, int Drawn, int Lost)> ByGame(string opponent)
    {
        string key = Key(opponent);
        return _results.Where(m => m.Opponent.Equals(key, StringComparison.OrdinalIgnoreCase))
            .GroupBy(m => m.GameId)
            .Select(g => (g.Key, g.Count(m => m.Outcome > 0), g.Count(m => m.Outcome == 0), g.Count(m => m.Outcome < 0)))
            .OrderByDescending(t => t.Item2 + t.Item3 + t.Item4).ThenBy(t => t.Key, StringComparer.Ordinal);
    }

    /// <summary>Everyone played, the most games first.</summary>
    public IEnumerable<string> Opponents => _results.GroupBy(m => m.Opponent, StringComparer.OrdinalIgnoreCase)
        .OrderByDescending(g => g.Count()).Select(g => g.First().Opponent);

    /// <summary>"yyyy-MM" of a UTC time, in local time: the ladder's month.</summary>
    public static string Month(DateTime utc) => utc.ToLocalTime().ToString("yyyy-MM", CultureInfo.InvariantCulture);

    /// <summary>
    /// This player's ladder rating for the month of <paramref name="nowUtc"/>: Elo from <see cref="StartRating"/>, over
    /// the board games played that month, each co-worker taken at the start rating (the rating they show on the office
    /// board is theirs to report). A month starts everyone level again.
    /// </summary>
    public int Rating(DateTime nowUtc)
    {
        string month = Month(nowUtc);
        double rating = StartRating;
        foreach (var m in _results.Where(m => LadderGames.Contains(m.GameId) && Month(m.AtUtc) == month).OrderBy(m => m.AtUtc))
            rating = Elo(rating, StartRating, m.Outcome);
        return (int)Math.Round(rating);
    }

    /// <summary>The number of ladder games played in the month of <paramref name="nowUtc"/>.</summary>
    public int LadderGamesPlayed(DateTime nowUtc)
    {
        string month = Month(nowUtc);
        return _results.Count(m => LadderGames.Contains(m.GameId) && Month(m.AtUtc) == month);
    }

    /// <summary>One Elo step: <paramref name="mine"/> after a game against <paramref name="theirs"/> ended <paramref name="outcome"/>.</summary>
    public static double Elo(double mine, double theirs, int outcome)
    {
        double expected = 1 / (1 + Math.Pow(10, (theirs - mine) / 400));
        double score = outcome > 0 ? 1 : outcome < 0 ? 0 : 0.5;
        return mine + KFactor * (score - expected);
    }
}
