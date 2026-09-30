using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskArcade.Dev;

/// <summary>What was waited on.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WaitKind>))]
public enum WaitKind
{
    /// <summary>A command run with --while (arcade dotnet test).</summary>
    Command,
    /// <summary>A coding agent's turn: Claude Code, Codex, Aider... from the prompt to done.</summary>
    Agent,
    /// <summary>A CI run or pipeline, from the GitHub or GitLab command-line tool.</summary>
    Ci,
    /// <summary>A status lane lit by a script (deskarcade --status), the VS Code extension's tasks among them.</summary>
    Lane,
    /// <summary>A browser download, a --wait-pid, --wait-file or --wait-url.</summary>
    Download,
}

/// <summary>
/// One wait: what (<paramref name="Kind"/> and <paramref name="Label"/>: "dotnet test", "api", "CI · main"), where it
/// came from (<paramref name="Source"/>: the agent's name, the repo folder), when it started, how long it took, how it
/// ended (null when there is no pass or fail), and how much of it was spent playing.
/// </summary>
public sealed record WaitEntry(WaitKind Kind, string Label, DateTime StartUtc, double Seconds, bool? Passed = null, double PlayedSeconds = 0, string? Source = null);

/// <summary>
/// Every wait of the last <see cref="KeepDays"/> days, for the wait report: how long builds, tests, CI and agents kept
/// you waiting, and which are getting slower. Kept in waits.json next to settings.json, on this PC only.
/// </summary>
public sealed class WaitLog
{
    public const int KeepDays = 120, MaxEntries = 20_000;
    /// <summary>Waits shorter than this are not worth a line (an instant "done").</summary>
    public const double MinSeconds = 2;

    static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    readonly List<WaitEntry> _entries = new();
    readonly string _path;
    bool _dirty;

    WaitLog(string path) => _path = path;

    /// <summary>Raised after a wait is added.</summary>
    public event Action<WaitEntry>? Added;

    public IReadOnlyList<WaitEntry> Entries => _entries;

    /// <param name="path">Where waits.json lives; by default next to settings.json.</param>
    public static WaitLog Load(string? path = null)
    {
        var log = new WaitLog(path ?? Path.Combine(Settings.DataDirectory, "waits.json"));
        try
        {
            if (File.Exists(log._path) && JsonSerializer.Deserialize<List<WaitEntry>>(File.ReadAllText(log._path), Json) is { } entries)
                log._entries.AddRange(entries.Where(e => e != null && e.Label != null).OrderBy(e => e.StartUtc));
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // a damaged file: start again
        }
        return log;
    }

    /// <summary>A log that is never saved, for tests.</summary>
    public static WaitLog InMemory() => new("");

    /// <summary>Adds a finished wait (a very short one is skipped) and forgets the oldest past <see cref="KeepDays"/>.</summary>
    public void Add(WaitEntry entry)
    {
        if (entry.Seconds < MinSeconds || string.IsNullOrWhiteSpace(entry.Label)) return;
        _entries.Add(entry with { Label = entry.Label.Trim(), PlayedSeconds = Math.Clamp(entry.PlayedSeconds, 0, entry.Seconds) });
        var cutoff = entry.StartUtc.AddDays(-KeepDays);
        _entries.RemoveAll(e => e.StartUtc < cutoff);
        if (_entries.Count > MaxEntries) _entries.RemoveRange(0, _entries.Count - MaxEntries);
        _dirty = true;
        Added?.Invoke(entry);
    }

    /// <summary>The waits that started from <paramref name="fromUtc"/> up to (not including) <paramref name="toUtc"/>.</summary>
    public IEnumerable<WaitEntry> Between(DateTime fromUtc, DateTime toUtc) => _entries.Where(e => e.StartUtc >= fromUtc && e.StartUtc < toUtc);

    public void Save()
    {
        if (!_dirty || _path.Length == 0) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_entries, Json));
            _dirty = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }
}
