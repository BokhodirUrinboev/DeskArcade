using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DeskArcade;

/// <summary>An achievement unlocks when <see cref="Counter"/> reaches <see cref="Target"/>.</summary>
public sealed record Achievement(string Id, string GameId, string Title, string Description, string Counter, long Target);

/// <summary>
/// Local play statistics: named counters such as "hoops.baskets", time played per game, and unlocked
/// achievements. Stored in stats.json next to settings.json. Games report events; this class decides
/// what unlocks.
/// </summary>
public sealed class Stats
{
    sealed class Data
    {
        public Dictionary<string, long> Counters { get; set; } = new();
        public Dictionary<string, double> Seconds { get; set; } = new();
        public Dictionary<string, DateTime> Unlocked { get; set; } = new();
    }

    Data _data = new();
    bool _dirty;
    string _path = "";

    Stats()
    {
    }

    /// <summary>Raised once when an achievement unlocks.</summary>
    public event Action<Achievement>? Unlocked;

    /// <summary>Raised after any counter changes (e.g. for the daily challenge).</summary>
    public event Action<string>? CounterChanged;

    static string DefaultPath => Path.Combine(Settings.DataDirectory, "stats.json");

    /// <param name="path">Where stats.json lives; by default next to settings.json.</param>
    public static Stats Load(string? path = null)
    {
        var stats = new Stats { _path = path ?? DefaultPath };
        try
        {
            if (File.Exists(stats._path))
                stats._data = JsonSerializer.Deserialize<Data>(File.ReadAllText(stats._path)) ?? new Data();
        }
        catch
        {
            // corrupt file: start fresh
        }
        return stats;
    }

    public void Save()
    {
        if (!_dirty) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true }));
            _dirty = false;
        }
        catch
        {
            // best effort
        }
    }

    public long Get(string counter) => _data.Counters.TryGetValue(counter, out long v) ? v : 0;

    /// <summary>Adds to a running total, e.g. baskets scored.</summary>
    public void Add(string counter, long amount = 1)
    {
        if (amount <= 0) return;
        _data.Counters[counter] = Get(counter) + amount;
        _dirty = true;
        Check(counter);
        CounterChanged?.Invoke(counter);
    }

    /// <summary>Keeps the highest value seen, e.g. a best streak or a best score.</summary>
    public void Max(string counter, long value)
    {
        if (value <= Get(counter)) return;
        _data.Counters[counter] = value;
        _dirty = true;
        Check(counter);
    }

    public double SecondsPlayed(string gameId) => _data.Seconds.TryGetValue(gameId, out double s) ? s : 0;

    public double TotalSeconds => _data.Seconds.Values.Sum();

    /// <summary>Called by the overlay for every frame in which a game is actively being played.</summary>
    public void AddTime(string gameId, double seconds)
    {
        if (seconds <= 0) return;
        double before = SecondsPlayed(gameId);
        _data.Seconds[gameId] = before + seconds;
        _dirty = true;
        Max("play.minutes", (long)(TotalSeconds / 60));
        if (before < 30 && before + seconds >= 30) Max("play.games", _data.Seconds.Count(kv => kv.Value >= 30));
    }

    public bool IsUnlocked(string id) => _data.Unlocked.ContainsKey(id);

    public DateTime? UnlockedAt(string id) => _data.Unlocked.TryGetValue(id, out var at) ? at : null;

    public int UnlockedCount => Achievements.All.Count(a => _data.Unlocked.ContainsKey(a.Id));

    public void Reset()
    {
        _data = new Data();
        _dirty = true;
        Save();
    }

    void Check(string counter)
    {
        long value = Get(counter);
        foreach (var a in Achievements.All)
        {
            if (a.Counter != counter || value < a.Target || _data.Unlocked.ContainsKey(a.Id)) continue;
            _data.Unlocked[a.Id] = DateTime.UtcNow;
            _dirty = true;
            Save();
            Unlocked?.Invoke(a);
        }
    }
}
