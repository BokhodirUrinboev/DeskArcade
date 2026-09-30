using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace DeskArcade.Dev;

/// <summary>
/// What a coding agent's hook told the overlay, as one line on the signal pipe:
/// <c>agent:&lt;state&gt;|&lt;agent&gt;|&lt;session&gt;|&lt;folder&gt;|&lt;kind&gt;|&lt;target&gt;|&lt;note&gt;</c>.
/// The state is working, done, attention, end (the session closed), step (a tool is about to run), did (a tool ran)
/// or idle; the agent is "claude", "codex", "aider", "cursor"...; the session and folder come from what the agent
/// passes the hook (Claude Code's session_id and cwd, Cursor's conversation_id and workspace_roots, Codex's thread-id);
/// kind and target describe a step (see <see cref="AgentStep"/>); the note is Claude Code's notification_type.
/// Fields are cleaned of "|", line breaks and control characters, and cut to a sensible length.
/// </summary>
public sealed record AgentSignal(string State, string Agent, string Session = "", string Folder = "", string Kind = "", string Target = "", string Note = "")
{
    public const string Prefix = "agent:";
    public const string DefaultAgent = "claude";
    const int MaxAgent = 24, MaxSession = 80, MaxFolder = 260, MaxTarget = 300, MaxNote = 40;

    /// <summary>The state a --signal word stands for (with the old aliases start, stop and notify); null for other signals.</summary>
    public static string? StateOf(string word) => word.Trim().ToLowerInvariant() switch
    {
        "working" or "start" => "working",
        "done" or "stop" => "done",
        "attention" or "notify" => "attention",
        "end" => "end",
        "step" => "step",
        "did" => "did",
        "idle" => "idle",
        _ => null,
    };

    /// <summary>
    /// The state for <c>--signal auto</c>, from the event the agent names: Claude Code's and Codex's hook_event_name,
    /// Gemini CLI's, Cursor's and Copilot CLI's event names, Windsurf's agent_action_name, or Codex notify's type. Null
    /// for an event that says nothing about the session.
    /// </summary>
    public static string? StateFromEvent(string? name) => name switch
    {
        "UserPromptSubmit" or "beforeSubmitPrompt" or "BeforeAgent" or "userPromptSubmitted" or "pre_user_prompt" => "working",
        "Stop" or "stop" or "AfterAgent" or "agentStop" or "post_cascade_response" or "agent-turn-complete" => "done",
        "Notification" or "PermissionRequest" or "permissionRequest" or "notification" => "attention",
        "SessionEnd" or "sessionEnd" => "end",
        "PreToolUse" or "BeforeTool" => "step",
        "PostToolUse" or "AfterTool" or "postToolUse" or "afterFileEdit" or "afterShellExecution" or "post_write_code" or "post_run_command" or "post_read_code" => "did",
        _ => null,
    };

    /// <summary>
    /// Notifications that do not need the person (a sign-in went through, an answer was taken, a quota note, a shell
    /// command finished): Claude Code and Copilot CLI send them all to the same hook.
    /// </summary>
    static bool NeedsNobody(string note) =>
        note is "auth_success" or "elicitation_complete" or "elicitation_response" or "agent_completed" or "agent_idle" or "push_notification"
            or "computer_use_enter" or "computer_use_exit" or "shell_completed" or "shell_detached_completed" || note.StartsWith("quota_", StringComparison.Ordinal);

    /// <summary>The line for the signal pipe.</summary>
    public string ToMessage() => Prefix + string.Join('|', State, Clean(Agent, MaxAgent), Clean(Session, MaxSession), Clean(Folder, MaxFolder),
        Clean(Kind, 16), Clean(Target, MaxTarget), Clean(Note, MaxNote));

    /// <summary>A line from the signal pipe; null when it is not an agent line or its state is unknown.</summary>
    public static AgentSignal? Parse(string message)
    {
        if (!message.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return null;
        string[] parts = message[Prefix.Length..].Split('|');
        if (StateOf(parts[0]) is not string state) return null;
        string Part(int i, int max) => i < parts.Length ? Clean(parts[i], max) : "";
        string agent = Part(1, MaxAgent).ToLowerInvariant();
        return new AgentSignal(state, agent.Length > 0 ? agent : DefaultAgent, Part(2, MaxSession), Part(3, MaxFolder),
            Part(4, 16).ToLowerInvariant(), Part(5, MaxTarget), Part(6, MaxNote).ToLowerInvariant());
    }

    /// <summary>
    /// The signal a hook sends, or null when there is nothing to send (an "auto" hook for an event that does not
    /// matter, a notification that needs nobody). <paramref name="state"/> comes from the command line ("auto" takes it
    /// from the event); the rest from the JSON the agent passed (<paramref name="payload"/>: stdin for Claude Code,
    /// Cursor, Gemini CLI and Copilot CLI, the last argument for Codex's notify). A payload from Cursor or Copilot CLI
    /// running Claude Code's hooks names its own agent. Without a folder in the payload, an agent other than Claude
    /// Code is named after the folder the hook ran in (<paramref name="currentFolder"/>), the project for Aider and a
    /// plain command line; Claude Code without a payload (an old hook config, a script) stays the one session it was.
    /// </summary>
    public static AgentSignal? FromHook(string state, string? agent, string? payload, string? currentFolder = null)
    {
        var hook = HookPayload.Read(payload);
        if (state == "auto" && StateFromEvent(hook.Event ?? hook.Type) is not string fromEvent) return null;
        if (state == "auto") state = StateFromEvent(hook.Event ?? hook.Type)!;
        string note = Clean(hook.NotificationType, MaxNote).ToLowerInvariant();
        if (state == "attention" && NeedsNobody(note)) return null;

        string name = Clean(agent, MaxAgent).ToLowerInvariant();
        if ((name.Length == 0 || name == DefaultAgent) && hook.Sender != null) name = hook.Sender;
        if (name.Length == 0) name = DefaultAgent;
        string folder = hook.Folder ?? (name != DefaultAgent ? currentFolder : null) ?? "";
        string kind = "", target = "";
        if (state is "step" or "did")
        {
            (kind, target) = hook.Tool != null
                ? AgentStep.FromTool(hook.Tool, hook.Input)
                : hook.Event switch
                {
                    // Cursor's and Windsurf's hooks describe the step in the event itself
                    "afterFileEdit" or "post_write_code" or "pre_write_code" => ("edit", hook.FilePath ?? hook.Get("file_path")),
                    "beforeReadFile" or "post_read_code" => ("read", hook.FilePath ?? hook.Get("file_path")),
                    "beforeShellExecution" or "afterShellExecution" => ("run", hook.Command ?? ""),
                    "post_run_command" or "pre_run_command" => ("run", hook.Get("command_line")),
                    _ => ("", ""),
                };
        }
        return new AgentSignal(state, name, Clean(hook.Session, MaxSession), Clean(folder, MaxFolder), kind, Clean(target, MaxTarget), note);
    }

    /// <summary>
    /// The text with "|" and control characters (line breaks, tabs) as spaces, trimmed, and cut to
    /// <paramref name="max"/> characters, the last of them "…" when it was longer.
    /// </summary>
    public static string Clean(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(Math.Min(text.Length, max + 8));
        foreach (char c in text)
        {
            if (sb.Length > max + 4) break;
            sb.Append(c == '|' || char.IsControl(c) ? ' ' : c);
        }
        return AgentStep.Cut(sb.ToString().Trim(), max);
    }
}

/// <summary>
/// The fields Desk Arcade reads from what an agent passes its hooks, read leniently: a payload cut off part way (a
/// Write of a large file) still gives the fields before the cut, and anything else is skipped. The tool's input keeps
/// only its string fields (the file, the command, the pattern), which is all a step label needs.
/// </summary>
public sealed class HookPayload
{
    public string? Session, Folder, Event, Tool, NotificationType, Type, FilePath, Command;
    /// <summary>The string fields of the tool's input (tool_input, Copilot's toolArgs, Windsurf's tool_info).</summary>
    public Dictionary<string, string>? Input;
    /// <summary>The agent the payload gives away when another agent runs Claude Code's hooks: "cursor", "copilot", "windsurf".</summary>
    public string? Sender;
    string? _cwd, _root;

    public string Get(string field) => Input != null && Input.TryGetValue(field, out string? v) ? v : "";

    public static HookPayload Read(string? json)
    {
        var p = new HookPayload();
        if (string.IsNullOrWhiteSpace(json)) return p;
        byte[] bytes = Encoding.UTF8.GetBytes(json.Trim());
        var reader = new Utf8JsonReader(bytes, isFinalBlock: false, state: default);
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return p;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                string name = reader.GetString() ?? "";
                if (!reader.Read()) break;
                switch (name)
                {
                    case "session_id" or "conversation_id" or "thread-id" or "thread_id" or "trajectory_id":
                        p.Session ??= Text(ref reader);
                        if (name == "trajectory_id") p.Sender ??= "windsurf";
                        break;
                    case "sessionId":
                        p.Session ??= Text(ref reader);
                        p.Sender ??= "copilot";
                        break;
                    case "cursor_version":
                        p.Sender = "cursor";
                        Skip(ref reader);
                        break;
                    case "cwd":
                        p._cwd ??= Text(ref reader);
                        break;
                    case "workspace_roots" or "workspaceRoots":
                        p._root ??= FirstText(ref reader);
                        break;
                    case "hook_event_name" or "hookEventName" or "agent_action_name":
                        p.Event ??= Text(ref reader);
                        break;
                    case "tool_name" or "toolName":
                        p.Tool ??= Text(ref reader);
                        break;
                    case "tool_input" or "toolArgs" or "tool_info":
                        var fields = Fields(ref reader, out bool cut);
                        p.Input ??= fields;
                        if (cut) goto done;
                        break;
                    case "notification_type":
                        p.NotificationType ??= Text(ref reader);
                        break;
                    case "type":
                        p.Type ??= Text(ref reader);
                        break;
                    case "file_path":
                        p.FilePath ??= Text(ref reader);
                        break;
                    case "command":
                        p.Command ??= Text(ref reader);
                        break;
                    default:
                        if (!Skip(ref reader)) goto done;
                        break;
                }
            }
        }
        catch (JsonException)
        {
            // the payload was cut off or broken here: what was read so far stands
        }
        catch (InvalidOperationException)
        {
            // a value of an unexpected type
        }
        done:
        // Cursor names the workspace (its shell hooks carry the command's own cwd too); the others give cwd
        p.Folder = p._root ?? p._cwd;
        return p;
    }

    static string? Text(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.String) return reader.GetString();
        if (!Skip(ref reader)) throw new JsonException("cut off");
        return null;
    }

    static string? FirstText(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.String) return reader.GetString();
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            if (!Skip(ref reader)) throw new JsonException("cut off");
            return null;
        }
        string? first = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.String) first ??= reader.GetString();
            else if (!Skip(ref reader)) throw new JsonException("cut off");
        }
        if (reader.TokenType != JsonTokenType.EndArray) throw new JsonException("cut off");
        return first;
    }

    /// <summary>
    /// The string fields of an object, as far as it goes: a Write's file_path is read even when its content is cut off.
    /// A string holding JSON (Copilot CLI's toolArgs) is read the same way.
    /// </summary>
    /// <param name="cut">Set when the object was cut off: nothing after it can be read.</param>
    static Dictionary<string, string>? Fields(ref Utf8JsonReader reader, out bool cut)
    {
        cut = false;
        if (reader.TokenType == JsonTokenType.String)
        {
            string? s = reader.GetString();
            if (string.IsNullOrWhiteSpace(s) || !s.TrimStart().StartsWith('{')) return null;
            var inner = new Utf8JsonReader(Encoding.UTF8.GetBytes(s.Trim()), isFinalBlock: false, state: default);
            try
            {
                return inner.Read() ? Fields(ref inner, out _) : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            cut = !Skip(ref reader);
            return null;
        }
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        cut = true;
        try
        {
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    cut = false;
                    break;
                }
                if (reader.TokenType != JsonTokenType.PropertyName) break;
                string name = reader.GetString() ?? "";
                if (!reader.Read()) break;
                if (reader.TokenType == JsonTokenType.String) fields[name] = reader.GetString() ?? "";
                else if (!Skip(ref reader)) break;
            }
        }
        catch (JsonException)
        {
            // cut off inside the object: keep the fields before the cut
        }
        return fields;
    }

    /// <summary>Skips the value the reader is on; false when it is cut off.</summary>
    static bool Skip(ref Utf8JsonReader reader) => reader.TrySkip();
}
