using System;
using System.Collections.Generic;
using System.Linq;

namespace DeskArcade.Dev;

/// <summary>Where a status lane stands.</summary>
public enum LaneState { Running, Passed, Failed }

/// <summary>
/// One status line from a script: <c>deskarcade --status NAME running|passed|failed|clear [--note TEXT] [--source TOOL]</c>,
/// which reaches the overlay as <c>status:&lt;state&gt;|&lt;name&gt;|&lt;note&gt;|&lt;source&gt;</c> (the VS Code extension
/// writes the same line straight to the signal pipe). Fields are cleaned of "|", line breaks and control characters;
/// the name is at most <see cref="MaxName"/> characters and the note at most <see cref="MaxNote"/>; note and source may
/// be empty.
/// </summary>
public sealed record StatusMessage(string State, string Name, string Note = "", string Source = "")
{
    public const string Prefix = "status:";
    public const int MaxName = 40, MaxNote = 80, MaxSource = 40;
    public static readonly string[] States = { "running", "passed", "failed", "clear" };

    public const string Usage = "usage: deskarcade --status <name> running|passed|failed|clear [--note <text>] [--source <tool>]";

    public string ToLine() => Prefix + string.Join('|', State, AgentSignal.Clean(Name, MaxName), AgentSignal.Clean(Note, MaxNote), AgentSignal.Clean(Source, MaxSource));

    /// <summary>A line from the signal pipe; null when it is not a status line, has no name or an unknown state.</summary>
    public static StatusMessage? Parse(string line)
    {
        if (!line.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return null;
        string[] parts = line[Prefix.Length..].Split('|');
        string state = parts[0].Trim().ToLowerInvariant();
        if (!States.Contains(state) || parts.Length < 2) return null;
        string name = AgentSignal.Clean(parts[1], MaxName);
        if (name.Length == 0) return null;
        string note = parts.Length > 2 ? AgentSignal.Clean(parts[2], MaxNote) : "";
        // anything after the fourth field belongs to the source (a sender that forgot to clean a "|")
        string source = parts.Length > 3 ? AgentSignal.Clean(string.Join(' ', parts[3..]), MaxSource) : "";
        return new StatusMessage(state, name, note, source);
    }

    /// <summary>The message for <c>--status</c> at <paramref name="at"/> in <paramref name="args"/>, or the usage line to print.</summary>
    public static (StatusMessage? Message, string? Error) FromArgs(string[] args, int at)
    {
        string? Value(string flag)
        {
            int i = Array.FindIndex(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        var words = args[(at + 1)..].TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        if (words.Count < 2) return (null, Usage);
        string name = words[0], state = words[1].ToLowerInvariant();
        // "--status running deploy" is forgiven
        if (!States.Contains(state) && States.Contains(name.ToLowerInvariant())) (name, state) = (words[1], name.ToLowerInvariant());
        if (!States.Contains(state)) return (null, Usage);
        name = AgentSignal.Clean(name, MaxName);
        if (name.Length == 0) return (null, Usage);
        return (new StatusMessage(state, name, AgentSignal.Clean(Value("--note"), MaxNote), AgentSignal.Clean(Value("--source"), MaxSource)), null);
    }
}

/// <summary>One lane on the scoreboard: a deploy, a watch task, a git hook, or CI.</summary>
public sealed class StatusLane
{
    public required string Name { get; init; }
    public string Note { get; internal set; } = "";
    public string Source { get; internal set; } = "";
    public LaneState State { get; internal set; }
    /// <summary>When it started running (for CI, when the run was created).</summary>
    public DateTime Since { get; internal set; }
    public DateTime LastWord { get; internal set; }
    /// <summary>How long it ran, once it passed or failed; null when it was never seen running.</summary>
    public TimeSpan? Took { get; internal set; }
    /// <summary>Seconds played while it ran.</summary>
    public double Played { get; internal set; }
    /// <summary>A CI run followed through gh or glab rather than a script's lane.</summary>
    public bool Ci { get; init; }

    public bool IsStale(DateTime now) => now - LastWord >= StatusLanes.StaleAfter;

    public DevLook Look(DateTime now) => IsStale(now) ? DevLook.Stale : State switch
    {
        LaneState.Running => DevLook.Running,
        LaneState.Passed => DevLook.Passed,
        _ => DevLook.Failed,
    };
}

/// <summary>What a status line did to a lane.</summary>
public enum LaneChangeKind { None, Started, Finished, Cleared }

/// <param name="Took">How long it ran, when a lane that was running passed or failed.</param>
public sealed record LaneChange(LaneChangeKind Kind, StatusLane? Lane = null, TimeSpan? Took = null)
{
    public static readonly LaneChange Nothing = new(LaneChangeKind.None);
}

/// <summary>
/// The status lanes, without any UI. A lane runs blue with a timer, ends green or red, goes grey after 30 minutes
/// without a word, and goes when cleared or after half a day. With more than <see cref="FoldAbove"/> lanes the scoreboard
/// folds them into one chip with a count.
/// </summary>
public sealed class StatusLanes
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30), ForgetAfter = TimeSpan.FromHours(12);
    public const int FoldAbove = 3, MaxLanes = 16;

    readonly List<StatusLane> _lanes = new();

    /// <summary>The lanes in the order they were lit.</summary>
    public IReadOnlyList<StatusLane> All => _lanes;
    public int Count => _lanes.Count;

    /// <summary>More lanes than the scoreboard shows one by one.</summary>
    public bool Folded => _lanes.Count > FoldAbove;

    public StatusLane? Find(string name) => _lanes.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <param name="since">When it really started (a CI run's creation time); now when null.</param>
    /// <param name="ci">A CI lane, fed by the repo watcher.</param>
    public LaneChange Apply(StatusMessage m, DateTime now, DateTime? since = null, bool ci = false)
    {
        var lane = Find(m.Name);
        if (m.State == "clear")
        {
            if (lane == null) return LaneChange.Nothing;
            _lanes.Remove(lane);
            return new LaneChange(LaneChangeKind.Cleared, lane);
        }
        bool isNew = lane == null;
        if (lane == null)
        {
            if (_lanes.Count >= MaxLanes) _lanes.Remove(_lanes.OrderBy(l => l.State == LaneState.Running && !l.IsStale(now)).ThenBy(l => l.LastWord).First());
            lane = new StatusLane { Name = m.Name, Ci = ci, Since = since ?? now };
            _lanes.Add(lane);
        }
        lane.LastWord = now;
        lane.Note = m.Note;
        if (m.Source.Length > 0) lane.Source = m.Source;
        var state = m.State switch { "running" => LaneState.Running, "passed" => LaneState.Passed, _ => LaneState.Failed };
        if (!isNew && lane.State == state)
        {
            if (state == LaneState.Running && since is DateTime s) lane.Since = s;
            return new LaneChange(LaneChangeKind.None, lane);
        }
        if (state == LaneState.Running)
        {
            lane.State = LaneState.Running;
            lane.Since = since ?? now;
            lane.Played = 0;
            lane.Took = null;
            return new LaneChange(LaneChangeKind.Started, lane);
        }
        TimeSpan? took = !isNew && lane.State == LaneState.Running ? now - lane.Since : null;
        lane.Took = took;
        lane.State = state;
        return new LaneChange(LaneChangeKind.Finished, lane, took);
    }

    /// <summary>Adds time played to every lane running (and not gone quiet).</summary>
    public void AddPlayed(double seconds, DateTime now)
    {
        foreach (var l in _lanes)
            if (l.State == LaneState.Running && !l.IsStale(now)) l.Played += seconds;
    }

    /// <summary>Forgets lanes quiet for half a day; true when any went.</summary>
    public bool Prune(DateTime now) => _lanes.RemoveAll(l => now - l.LastWord >= ForgetAfter) > 0;

    /// <summary>Clears every lane lit by a script (CI lanes stay while their repo is followed).</summary>
    public void ClearScripts() => _lanes.RemoveAll(l => !l.Ci);

    public void Remove(string name) => _lanes.RemoveAll(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The lanes that are running now (not gone quiet).</summary>
    public int RunningCount(DateTime now) => _lanes.Count(l => l.State == LaneState.Running && !l.IsStale(now));

    /// <summary>The most urgent look, for a folded chip and the pill's dot: failed, then running, then passed, then grey.</summary>
    public DevLook? Urgent(DateTime now)
    {
        if (_lanes.Count == 0) return null;
        var looks = _lanes.Select(l => l.Look(now)).ToList();
        foreach (var look in new[] { DevLook.Failed, DevLook.Running, DevLook.Passed })
            if (looks.Contains(look)) return look;
        return DevLook.Stale;
    }

    /// <summary>A lane's line: "deploy · running 4:12", "tests · passed 1:02 · 3 skipped", "lint · failed".</summary>
    public static string Line(StatusLane l, DateTime now)
    {
        string text = l.IsStale(now) ? L.F("{0} · quiet since {1}", l.Name, AgentSessions.LocalClock(l.LastWord)) : l.State switch
        {
            LaneState.Running => L.F("{0} · running {1}", l.Name, Hud.FormatWait(now - l.Since)),
            LaneState.Passed when l.Took is TimeSpan t && t.TotalSeconds >= 1 => L.F("{0} · passed {1}", l.Name, Hud.FormatWait(t)),
            LaneState.Passed => L.F("{0} · passed", l.Name),
            _ when l.Took is TimeSpan t && t.TotalSeconds >= 1 => L.F("{0} · failed {1}", l.Name, Hud.FormatWait(t)),
            _ => L.F("{0} · failed", l.Name),
        };
        return l.Note.Length > 0 ? $"{text} · {l.Note}" : text;
    }

    /// <summary>The folded chip: "5 lanes · 2 running · 1 failed · 2 passed".</summary>
    public string FoldedLine(DateTime now)
    {
        var parts = new List<string> { L.F("{0} lanes", _lanes.Count) };
        int running = _lanes.Count(l => l.Look(now) == DevLook.Running), failed = _lanes.Count(l => l.Look(now) == DevLook.Failed);
        int passed = _lanes.Count(l => l.Look(now) == DevLook.Passed), quiet = _lanes.Count(l => l.Look(now) == DevLook.Stale);
        if (running > 0) parts.Add(L.F("{0} running", running));
        if (failed > 0) parts.Add(L.F("{0} failed", failed));
        if (passed > 0) parts.Add(L.F("{0} passed", passed));
        if (quiet > 0) parts.Add(L.F("{0} quiet", quiet));
        return string.Join(" · ", parts);
    }

    /// <summary>Every lane's line, one per line, for the folded chip's tooltip.</summary>
    public string AllLines(DateTime now) => string.Join("\n", _lanes.Select(l => Line(l, now)));
}
