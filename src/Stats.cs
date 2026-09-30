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
        /// <summary>The local date (yyyy-MM-dd) that <see cref="Today"/> counts.</summary>
        public string? Day { get; set; }
        /// <summary>The same counters for today only, for the office leaderboard.</summary>
        public Dictionary<string, long> Today { get; set; } = new();
        /// <summary>The work counters of earlier days, by local date (yyyy-MM-dd), for the standup notes.</summary>
        public Dictionary<string, Dictionary<string, long>> Days { get; set; } = new();
    }

    /// <summary>How many earlier days <see cref="OnDay"/> keeps.</summary>
    public const int HistoryDays = 60;

    /// <summary>The counters a day keeps when it is over: the working day's (time at the computer, focus blocks, meetings, breaks...) and the time played.</summary>
    public static bool KeptForTheDay(string counter) => counter.StartsWith("work.", StringComparison.Ordinal) || counter == "play.ms";

    Data _data = new();
    bool _dirty;
    double _msCarry; // fractions of a millisecond of play not yet added to "play.ms"
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

    /// <summary>Overridable for tests: the local date that "today" means.</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.Now;

    static string DayOf(DateTime t) => t.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Today's value of a counter: what was added today, or the best value reached today.</summary>
    public long Today(string counter)
    {
        RollDay();
        return _data.Today.TryGetValue(counter, out long v) ? v : 0;
    }

    void RollDay()
    {
        string day = DayOf(Clock());
        if (_data.Day == day) return;
        KeepDay();
        _data.Day = day;
        _data.Today.Clear();
        _dirty = true;
    }

    /// <summary>The day is over: its work counters go into the history, which keeps the last <see cref="HistoryDays"/> days.</summary>
    void KeepDay()
    {
        _data.Days ??= new();
        if (_data.Day is string past)
        {
            var kept = _data.Today.Where(kv => KeptForTheDay(kv.Key) && kv.Value != 0).ToDictionary(kv => kv.Key, kv => kv.Value);
            if (kept.Count > 0) _data.Days[past] = kept;
        }
        string oldest = DayOf(Clock().AddDays(-HistoryDays));
        foreach (string old in _data.Days.Keys.Where(k => string.CompareOrdinal(k, oldest) < 0).ToList()) _data.Days.Remove(old);
    }

    /// <summary>
    /// The work counters of a day: today's as they stand, an earlier day's as it ended (empty when nothing was kept for
    /// it, or it is more than <see cref="HistoryDays"/> days ago).
    /// </summary>
    public IReadOnlyDictionary<string, long> OnDay(DateOnly day)
    {
        RollDay();
        string key = day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (key == _data.Day) return _data.Today.Where(kv => KeptForTheDay(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        return _data.Days != null && _data.Days.TryGetValue(key, out var kept) ? kept : new Dictionary<string, long>();
    }

    /// <summary>A counter summed over the days from <paramref name="from"/> up to (not including) <paramref name="to"/>.</summary>
    public long OverDays(string counter, DateOnly from, DateOnly to)
    {
        long sum = 0;
        for (var d = from; d < to; d = d.AddDays(1))
            if (OnDay(d).TryGetValue(counter, out long v)) sum += v;
        return sum;
    }

    /// <summary>Adds to a running total, e.g. baskets scored.</summary>
    public void Add(string counter, long amount = 1)
    {
        if (amount <= 0) return;
        _data.Counters[counter] = Get(counter) + amount;
        _data.Today[counter] = Today(counter) + amount;
        _dirty = true;
        Check(counter);
        CounterChanged?.Invoke(counter);
    }

    /// <summary>Keeps the highest value seen, e.g. a best streak or a best score.</summary>
    public void Max(string counter, long value)
    {
        if (value > Today(counter))
        {
            _data.Today[counter] = value;
            _dirty = true;
        }
        if (value <= Get(counter)) return;
        _data.Counters[counter] = value;
        _dirty = true;
        Check(counter);
    }

    /// <summary>
    /// Keeps the lowest value seen where fewer is better, e.g. the fewest darts to finish 501. A counter that was
    /// never set counts as unset, not as 0. Achievements only follow <see cref="Add"/> and <see cref="Max"/> counters.
    /// </summary>
    public void Min(string counter, long value)
    {
        if (value <= 0) return;
        long today = Today(counter);
        if (today == 0 || value < today)
        {
            _data.Today[counter] = value;
            _dirty = true;
        }
        long best = Get(counter);
        if (best != 0 && value >= best) return;
        _data.Counters[counter] = value;
        _dirty = true;
    }

    public double SecondsPlayed(string gameId) => _data.Seconds.TryGetValue(gameId, out double s) ? s : 0;

    public double TotalSeconds => _data.Seconds.Values.Sum();

    /// <summary>Called by the overlay for every frame in which a game is actively being played.</summary>
    public void AddTime(string gameId, double seconds)
    {
        if (seconds <= 0) return;
        double before = SecondsPlayed(gameId);
        _data.Seconds[gameId] = before + seconds;
        _msCarry += seconds * 1000;
        long whole = (long)_msCarry;
        _msCarry -= whole;
        _data.Today["play.ms"] = Today("play.ms") + whole;
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
