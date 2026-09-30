using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DeskArcade.Dev;

/// <summary>Where a coding agent's session stands.</summary>
public enum AgentState { Working, Done, Attention }

/// <summary>How a session or a lane looks on the scoreboard: its state, or grey when it has gone quiet.</summary>
public enum DevLook { Working, Done, Attention, Running, Passed, Failed, Stale }

/// <summary>One session of one coding agent: a Claude Code window, a Codex thread, an Aider in a project folder.</summary>
public sealed class AgentSession
{
    public required string Key { get; init; }
    /// <summary>"claude", "codex", "aider"...</summary>
    public required string Agent { get; init; }
    public string SessionId { get; init; } = "";
    /// <summary>The project folder, when the agent said; the session is named after it.</summary>
    public string Folder { get; internal set; } = "";
    public AgentState State { get; internal set; }
    /// <summary>When the state last changed.</summary>
    public DateTime StateAt { get; internal set; }
    /// <summary>When the current turn began (or, once it is over, the turn that just ended).</summary>
    public DateTime Since { get; internal set; }
    /// <summary>The last time the agent said anything.</summary>
    public DateTime LastWord { get; internal set; }
    /// <summary>How long the last turn took, once it is over; null when its start was not seen.</summary>
    public TimeSpan? Took { get; internal set; }
    /// <summary>Seconds played during the current turn.</summary>
    public double Played { get; internal set; }
    /// <summary>What it is doing now (see <see cref="AgentStep"/>); empty between steps or without the step hooks.</summary>
    public string StepKind { get; internal set; } = "";
    public string StepTarget { get; internal set; } = "";
    /// <summary>The files changed and the commands run in the current turn, when the step hooks are on.</summary>
    public List<string> Files { get; } = new();
    public List<string> Commands { get; } = new();
    internal int Order { get; init; }

    public bool IsStale(DateTime now) => now - LastWord >= AgentSessions.StaleAfter;

    public DevLook Look(DateTime now) => IsStale(now) ? DevLook.Stale : State switch
    {
        AgentState.Working => DevLook.Working,
        AgentState.Attention => DevLook.Attention,
        _ => DevLook.Done,
    };
}

/// <summary>A turn that ended: from working to done or needing you.</summary>
public sealed record AgentTurn(AgentSession Session, AgentState Ended, DateTime StartUtc, TimeSpan Took, double Played, IReadOnlyList<string> Files, IReadOnlyList<string> Commands);

/// <summary>What a signal did to the table.</summary>
public enum AgentChangeKind { None, Started, Resumed, Finished, Removed, Step }

/// <param name="Turn">The turn that ended (Finished), when its start was seen.</param>
public sealed record AgentChange(AgentChangeKind Kind, AgentSession? Session = null, AgentTurn? Turn = null)
{
    public static readonly AgentChange Nothing = new(AgentChangeKind.None);
}

/// <summary>
/// Every coding agent session the hooks have told about, without any UI: a session starts working with the prompt,
/// ends its turn done or needing you, shows its current step, and goes when the agent closes it. A session that has
/// said nothing for an hour is shown grey; after half a day it is forgotten. Several sessions run side by side, each
/// with its own timer and its own time played. A hook config from before 1.8.6, which passed nothing but the state, is
/// the one session it always was.
/// </summary>
public sealed class AgentSessions
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1), ForgetAfter = TimeSpan.FromHours(12);
    /// <summary>
    /// Hooks that run in the background can arrive a moment out of order: a step this soon after the turn ended or
    /// asked for you belongs to what came before, not to new work.
    /// </summary>
    public static readonly TimeSpan LateWindow = TimeSpan.FromSeconds(3);
    public const int MaxSessions = 12;

    readonly List<AgentSession> _sessions = new();
    int _order;

    /// <summary>The sessions in the order they first spoke.</summary>
    public IReadOnlyList<AgentSession> All => _sessions;

    public int Count => _sessions.Count;

    /// <summary>The key a signal's session goes by: the agent's session id, else its folder, else the agent alone.</summary>
    public static string KeyOf(AgentSignal s) =>
        s.Agent + "|" + (s.Session.Length > 0 ? s.Session : s.Folder.Length > 0 ? "dir:" + s.Folder.TrimEnd('/', '\\').ToLowerInvariant() : "");

    public AgentSession? Find(AgentSignal s) => _sessions.FirstOrDefault(x => x.Key == KeyOf(s));

    /// <param name="steps">Whether the step hooks count ("What Claude is doing"); off, step and did signals are ignored.</param>
    public AgentChange Apply(AgentSignal s, DateTime now, bool steps = true)
    {
        switch (s.State)
        {
            case "end" or "idle":
            {
                var gone = Find(s);
                if (gone == null) return AgentChange.Nothing;
                _sessions.Remove(gone);
                return new AgentChange(AgentChangeKind.Removed, gone);
            }
            case "working":
            {
                var (session, isNew) = GetOrAdd(s, now);
                session.LastWord = now;
                if (!isNew && session.State == AgentState.Working) return new AgentChange(AgentChangeKind.None, session);
                StartTurn(session, now);
                return new AgentChange(AgentChangeKind.Started, session);
            }
            case "done" or "attention":
            {
                var state = s.State == "done" ? AgentState.Done : AgentState.Attention;
                var (session, isNew) = GetOrAdd(s, now);
                session.LastWord = now;
                // Claude Code's reminder a minute after a turn ("waiting for your input") is not news
                if (state == AgentState.Attention && s.Note == "idle_prompt" && session.State == AgentState.Done && !isNew)
                    return new AgentChange(AgentChangeKind.None, session);
                if (!isNew && session.State == state) return new AgentChange(AgentChangeKind.None, session);
                AgentTurn? turn = null;
                if (!isNew && session.State == AgentState.Working)
                {
                    var took = now - session.Since;
                    turn = new AgentTurn(session, state, session.Since, took, session.Played, session.Files.ToList(), session.Commands.ToList());
                    session.Took = took;
                }
                else session.Took = null; // done after needing you: when the work picked up again was not seen
                session.State = state;
                session.StateAt = now;
                if (state == AgentState.Attention)
                {
                    // what is waiting for approval stays under the line; the next stretch of work starts from here
                    session.Since = now;
                    session.Played = 0;
                    session.Took = null;
                }
                else session.StepKind = session.StepTarget = "";
                return new AgentChange(AgentChangeKind.Finished, session, turn);
            }
            case "step" or "did":
            {
                if (!steps) return AgentChange.Nothing;
                var (session, isNew) = GetOrAdd(s, now);
                session.LastWord = now;
                var kind = AgentChangeKind.Step;
                // a step of the turn that just ended, or of what waits for approval, is no new work
                bool late = !isNew && session.State != AgentState.Working && now - session.StateAt < LateWindow;
                if (!late && (isNew || session.State == AgentState.Done))
                {
                    StartTurn(session, now); // a turn whose prompt was not seen (the overlay started late)
                    kind = AgentChangeKind.Started;
                }
                else if (!late && session.State == AgentState.Attention)
                {
                    // approved: back to work in the same turn
                    session.State = AgentState.Working;
                    session.StateAt = now;
                    session.Since = now;
                    session.Played = 0;
                    kind = AgentChangeKind.Resumed;
                }
                if (s.State == "step" && s.Kind.Length > 0)
                {
                    session.StepKind = s.Kind;
                    session.StepTarget = s.Target;
                }
                else if (s.State == "did" && s.Target.Length > 0)
                {
                    if (AgentStep.TouchesFile(s.Kind))
                    {
                        if (!session.Files.Contains(s.Target, StringComparer.OrdinalIgnoreCase)) session.Files.Add(s.Target);
                    }
                    else if (s.Kind == "run") session.Commands.Add(s.Target);
                    // agents with no hook before a tool (Cursor, Copilot CLI) show what they last did
                    if (!late)
                    {
                        session.StepKind = s.Kind;
                        session.StepTarget = s.Target;
                    }
                }
                return new AgentChange(kind, session);
            }
        }
        return AgentChange.Nothing;
    }

    static void StartTurn(AgentSession session, DateTime now)
    {
        session.State = AgentState.Working;
        session.StateAt = now;
        session.Since = now;
        session.Played = 0;
        session.Took = null;
        session.StepKind = session.StepTarget = "";
        session.Files.Clear();
        session.Commands.Clear();
    }

    (AgentSession Session, bool IsNew) GetOrAdd(AgentSignal s, DateTime now)
    {
        string key = KeyOf(s);
        var found = _sessions.FirstOrDefault(x => x.Key == key);
        if (found != null)
        {
            if (found.Folder.Length == 0 && s.Folder.Length > 0) found.Folder = s.Folder;
            return (found, false);
        }
        if (_sessions.Count >= MaxSessions)
        {
            // make room: the one that has been quiet longest, preferring one that is not working
            var drop = _sessions.OrderBy(x => x.State == AgentState.Working && !x.IsStale(now)).ThenBy(x => x.LastWord).First();
            _sessions.Remove(drop);
        }
        var session = new AgentSession
        {
            Key = key, Agent = s.Agent, SessionId = s.Session, Folder = s.Folder, Order = ++_order,
            State = AgentState.Done, StateAt = DateTime.MinValue, Since = now, LastWord = now,
        };
        _sessions.Add(session);
        return (session, true);
    }

    /// <summary>Adds time played to every session working (and not gone quiet).</summary>
    public void AddPlayed(double seconds, DateTime now)
    {
        foreach (var s in _sessions)
            if (s.State == AgentState.Working && !s.IsStale(now)) s.Played += seconds;
    }

    /// <summary>Forgets sessions quiet for half a day; true when any went.</summary>
    public bool Prune(DateTime now) => _sessions.RemoveAll(s => now - s.LastWord >= ForgetAfter) > 0;

    /// <summary>Forgets the sessions that are done, need you or went quiet: only those working stay.</summary>
    public void ClearFinished(DateTime now) => _sessions.RemoveAll(s => s.State != AgentState.Working || s.IsStale(now));

    /// <summary>Sessions working now (not gone quiet).</summary>
    public int WorkingCount(DateTime now) => _sessions.Count(s => s.State == AgentState.Working && !s.IsStale(now));

    /// <summary>The most urgent look, for the pill's one dot: needs you, then working, then done, then grey; null with no sessions.</summary>
    public DevLook? Urgent(DateTime now)
    {
        if (_sessions.Count == 0) return null;
        var looks = _sessions.Select(s => s.Look(now)).ToList();
        foreach (var look in new[] { DevLook.Attention, DevLook.Working, DevLook.Done })
            if (looks.Contains(look)) return look;
        return DevLook.Stale;
    }

    /// <summary>
    /// What a session is called: its folder's name ("api"), or the agent's name without one ("Claude"), with a number
    /// when two sessions share a name ("api 2").
    /// </summary>
    public string NameOf(AgentSession session)
    {
        string name = BaseName(session);
        int same = 0, index = 0;
        foreach (var s in _sessions)
        {
            if (!string.Equals(BaseName(s), name, StringComparison.OrdinalIgnoreCase)) continue;
            same++;
            if (s == session) index = same;
        }
        return same > 1 && index > 1 ? $"{name} {index}" : name;
    }

    static string BaseName(AgentSession s)
    {
        string folder = s.Folder.TrimEnd('/', '\\');
        int slash = folder.LastIndexOfAny(new[] { '/', '\\' });
        string name = slash >= 0 ? folder[(slash + 1)..] : folder;
        if (name.EndsWith(':')) name = folder; // a drive root, "C:"
        return name.Length > 0 ? AgentStep.Cut(name, 24) : AgentNames.Display(s.Agent);
    }

    /// <summary>"api", or "web · Codex" for an agent other than Claude Code, so each line says whose it is.</summary>
    public string WhoOf(AgentSession s)
    {
        string name = NameOf(s), agent = AgentNames.Display(s.Agent);
        return s.Agent == AgentSignal.DefaultAgent || name.StartsWith(agent, StringComparison.OrdinalIgnoreCase) ? name : $"{name} · {agent}";
    }

    /// <summary>The session's line on the scoreboard: "api · working 3:12", "web · needs you", "docs · done after 2:05".</summary>
    public string Line(AgentSession s, DateTime now)
    {
        string who = WhoOf(s);
        if (s.IsStale(now)) return L.F("{0} · quiet since {1}", who, LocalClock(s.LastWord));
        return s.State switch
        {
            AgentState.Working => L.F("{0} · working {1}", who, Hud.FormatWait(now - s.Since)),
            AgentState.Attention => L.F("{0} · needs you", who),
            _ => s.Took is TimeSpan t && t.TotalSeconds >= 5 ? L.F("{0} · done after {1}", who, Hud.FormatWait(t)) : L.F("{0} · done", who),
        };
    }

    /// <summary>The step under a session's line while it works or waits for approval ("Editing Program.cs"); empty otherwise.</summary>
    public static string StepLine(AgentSession s, DateTime now) =>
        s.State != AgentState.Done && !s.IsStale(now) && s.StepKind.Length > 0 ? AgentStep.Label(s.StepKind, s.StepTarget) : "";

    /// <summary>"14:02" in the PC's time zone.</summary>
    public static string LocalClock(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZoneInfo.Local).ToString("HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>The coding agents Desk Arcade knows by name.</summary>
public static class AgentNames
{
    /// <summary>"Claude", "Codex", "Aider"... or the name as given, with a capital letter.</summary>
    public static string Display(string agent) => agent switch
    {
        "claude" or "claude-code" => "Claude",
        "codex" => "Codex",
        "aider" => "Aider",
        "cursor" => "Cursor",
        "gemini" => "Gemini",
        "copilot" => "Copilot",
        "windsurf" => "Windsurf",
        "cline" => "Cline",
        "opencode" => "opencode",
        "" => "Claude",
        _ => char.ToUpperInvariant(agent[0]) + agent[1..],
    };
}
