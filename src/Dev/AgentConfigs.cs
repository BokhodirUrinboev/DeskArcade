using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeskArcade.Dev;

/// <summary>A ready config for one coding agent: its name, where it goes, and the text to paste.</summary>
public sealed record AgentConfig(string Id, string Name, string Where, string Text);

/// <summary>
/// The configs tray → Coding agents &amp; CI copies, one per agent that can run a command when it starts, finishes or
/// needs you. Each runs <c>deskarcade --signal …</c>, which reads what the agent passes and returns at once:
/// <list type="bullet">
/// <item>Claude Code: hooks in ~/.claude/settings.json, run in the background ("async").</item>
/// <item>OpenAI Codex CLI: <c>notify</c> in ~/.codex/config.toml (a JSON argument when a turn completes), or its
/// Claude-style hooks in ~/.codex/hooks.json.</item>
/// <item>Cursor: ~/.cursor/hooks.json, only hooks that expect no answer (its permission hooks block without one).</item>
/// <item>Gemini CLI: hooks in ~/.gemini/settings.json (PowerShell on Windows, timeouts in milliseconds).</item>
/// <item>GitHub Copilot CLI: ~/.copilot/hooks/*.json, started without a shell.</item>
/// <item>Windsurf: ~/.codeium/windsurf/hooks.json.</item>
/// <item>Aider: <c>notifications-command</c> in .aider.conf.yml, once per reply.</item>
/// <item>Anything else: the command lines to run from its own hooks or scripts.</item>
/// </list>
/// With the step setting on, the configs that can also pass on each tool call do.
/// </summary>
public static class AgentConfigs
{
    public const string Claude = "claude";

    /// <summary>The agents other than Claude Code, in the menu's order.</summary>
    public static readonly string[] Others = { "codex", "codex-hooks", "cursor", "gemini", "copilot", "windsurf", "aider", "command" };

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The menu line for an agent's config.</summary>
    public static string MenuHeader(string id) => id switch
    {
        "codex" => "OpenAI Codex CLI (notify)",
        "codex-hooks" => "OpenAI Codex CLI (hooks.json)",
        "cursor" => "Cursor (hooks.json)",
        "gemini" => "Gemini CLI (settings.json)",
        "copilot" => "GitHub Copilot CLI (hooks)",
        "windsurf" => "Windsurf (hooks.json)",
        "aider" => "Aider (--notifications-command)",
        "command" => L.T("Any other tool (a command line)"),
        _ => "Claude Code",
    };

    /// <param name="exe">The program to run (<see cref="Program.LaunchPath"/>).</param>
    /// <param name="profile">A --profile to pass on, or empty.</param>
    /// <param name="steps">"What Claude is doing": add the hooks that pass on each tool call.</param>
    /// <param name="windows">Write it for Windows (paths, PowerShell); by default, for this PC.</param>
    public static AgentConfig Build(string id, string exe, string profile, bool steps, bool? windows = null)
    {
        bool win = windows ?? OperatingSystem.IsWindows();
        var c = new Commands(exe, profile, win);
        return id switch
        {
            "codex" => new(id, "Codex", L.T("put it at the top of ~/.codex/config.toml"), CodexNotify(c)),
            "codex-hooks" => new(id, "Codex", L.T("save it as ~/.codex/hooks.json, then trust it in /hooks"), CodexHooks(c, steps)),
            "cursor" => new(id, "Cursor", L.T("save it as ~/.cursor/hooks.json, or merge it into the one there"), Cursor(c, steps)),
            "gemini" => new(id, "Gemini CLI", L.T("merge it into ~/.gemini/settings.json"), Gemini(c, steps)),
            "copilot" => new(id, "Copilot CLI", L.T("save it as ~/.copilot/hooks/deskarcade.json"), Copilot(c, steps)),
            "windsurf" => new(id, "Windsurf", L.T("save it as ~/.codeium/windsurf/hooks.json, or merge it into the one there"), Windsurf(c, steps)),
            "aider" => new(id, "Aider", L.T("add it to .aider.conf.yml in your home or project folder"), Aider(c)),
            "command" => new(id, L.T("Command line"), L.T("run these from the tool's own hooks or scripts"), CommandLines(c)),
            _ => new(Claude, "Claude Code", L.T("merge it into ~/.claude/settings.json"), ClaudeCode(c, steps)),
        };
    }

    /// <summary>How to call Desk Arcade from a shell (Git Bash, cmd, sh), from PowerShell, or with no shell at all.</summary>
    sealed class Commands
    {
        readonly string _exe, _profile;
        readonly bool _windows;

        public Commands(string exe, string profile, bool windows)
        {
            _exe = exe;
            _profile = profile;
            _windows = windows;
        }

        /// <summary>The program for an agent that starts it without a shell: the path as the system writes it.</summary>
        public string Exe => _windows ? _exe.Replace('/', '\\') : _exe;

        /// <summary>The arguments after the program.</summary>
        public List<string> Args(string signal, string? agent)
        {
            var args = new List<string>();
            if (_profile.Length > 0) args.AddRange(new[] { "--profile", _profile });
            args.AddRange(new[] { "--signal", signal });
            if (agent != null) args.AddRange(new[] { "--agent", agent });
            return args;
        }

        /// <summary>
        /// For Git Bash, cmd.exe and sh alike: the path in double quotes with forward slashes (a backslash would be
        /// eaten by Git Bash), then the arguments.
        /// </summary>
        public string Shell(string signal, string? agent = null)
        {
            string path = _windows ? _exe.Replace('\\', '/') : _exe;
            return "\"" + path.Replace("\"", "\\\"") + "\" " + string.Join(' ', Args(signal, agent));
        }

        /// <summary>For PowerShell: the call operator and the path in single quotes.</summary>
        public string PowerShell(string signal, string? agent = null) =>
            "& '" + Exe.Replace("'", "''") + "' " + string.Join(' ', Args(signal, agent));

        /// <summary>Gemini CLI runs hooks in PowerShell on Windows and in a POSIX shell elsewhere.</summary>
        public string Native(string signal, string? agent = null) => _windows ? PowerShell(signal, agent) : Shell(signal, agent);
    }

    static string Json(JsonNode node) => node.ToJsonString(Indented).Replace("\r\n", "\n") + "\n";

    /// <summary>{ "hooks": [ { "type": "command", "command": … } ] } with extra fields.</summary>
    static JsonArray Hook(string command, bool async = true, int? timeout = null, string? matcher = null)
    {
        var handler = new JsonObject { ["type"] = "command", ["command"] = command };
        if (async) handler["async"] = true;
        if (timeout is int t) handler["timeout"] = t;
        var group = new JsonObject();
        if (matcher != null) group["matcher"] = matcher;
        group["hooks"] = new JsonArray(handler);
        return new JsonArray(group);
    }

    /// <summary>
    /// Claude Code: the prompt starts a turn, Stop ends it, a permission request or a question needs you, and
    /// SessionEnd closes the session. All but SessionEnd run in the background, so Claude never waits for them;
    /// SessionEnd gets a short timeout, as Claude Code only waits briefly on its way out.
    /// </summary>
    static string ClaudeCode(Commands c, bool steps)
    {
        var hooks = new JsonObject
        {
            ["UserPromptSubmit"] = Hook(c.Shell("working")),
            ["Stop"] = Hook(c.Shell("done")),
            ["PermissionRequest"] = Hook(c.Shell("attention")),
            ["Notification"] = Hook(c.Shell("attention"), matcher: "permission_prompt|elicitation_dialog|elicitation_url_dialog"),
            ["SessionEnd"] = Hook(c.Shell("end"), async: false, timeout: 5),
        };
        if (steps)
        {
            hooks["PreToolUse"] = Hook(c.Shell("step"));
            hooks["PostToolUse"] = Hook(c.Shell("did"));
        }
        return Json(new JsonObject { ["hooks"] = hooks });
    }

    /// <summary>Codex's notify: started with no shell, the turn's JSON added as the last argument.</summary>
    static string CodexNotify(Commands c)
    {
        var items = new List<string> { c.Exe };
        items.AddRange(c.Args("done", "codex"));
        return "# ~/.codex/config.toml, at the top (before any [table])\nnotify = [" + string.Join(", ", items.Select(Toml)) + "]\n";
    }

    /// <summary>A TOML basic string.</summary>
    static string Toml(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>Codex's hooks: Claude Code's shape; there is no Notification, PermissionRequest asks for you.</summary>
    static string CodexHooks(Commands c, bool steps)
    {
        var hooks = new JsonObject
        {
            ["UserPromptSubmit"] = Hook(c.Shell("working", "codex")),
            ["PermissionRequest"] = Hook(c.Shell("attention", "codex")),
            ["Stop"] = Hook(c.Shell("done", "codex")),
            ["SessionEnd"] = Hook(c.Shell("end", "codex"), async: false, timeout: 3),
        };
        if (steps)
        {
            hooks["PreToolUse"] = Hook(c.Shell("step", "codex"));
            hooks["PostToolUse"] = Hook(c.Shell("did", "codex"));
        }
        return Json(new JsonObject { ["hooks"] = hooks });
    }

    /// <summary>Cursor: only hooks that expect no answer, as its permission hooks (before a shell command, a read, a tool) block without one.</summary>
    static string Cursor(Commands c, bool steps)
    {
        JsonArray Run(string signal) => new(new JsonObject { ["command"] = c.Shell(signal, "cursor") });
        var hooks = new JsonObject
        {
            ["beforeSubmitPrompt"] = Run("working"),
            ["stop"] = Run("done"),
            ["sessionEnd"] = Run("end"),
        };
        if (steps)
        {
            hooks["afterFileEdit"] = Run("did");
            hooks["afterShellExecution"] = Run("did");
        }
        return Json(new JsonObject { ["version"] = 1, ["hooks"] = hooks });
    }

    /// <summary>Gemini CLI: BeforeAgent and AfterAgent frame a turn; the timeout is in milliseconds.</summary>
    static string Gemini(Commands c, bool steps)
    {
        JsonArray Run(string signal)
        {
            var handler = new JsonObject { ["name"] = "deskarcade-" + signal, ["type"] = "command", ["command"] = c.Native(signal, "gemini"), ["timeout"] = 5000 };
            return new JsonArray(new JsonObject { ["matcher"] = "", ["hooks"] = new JsonArray(handler) });
        }
        var hooks = new JsonObject
        {
            ["BeforeAgent"] = Run("working"),
            ["AfterAgent"] = Run("done"),
            ["Notification"] = Run("attention"),
            ["SessionEnd"] = Run("end"),
        };
        if (steps)
        {
            hooks["BeforeTool"] = Run("step");
            hooks["AfterTool"] = Run("did");
        }
        return Json(new JsonObject { ["hooks"] = hooks });
    }

    /// <summary>
    /// GitHub Copilot CLI: the program and its arguments with no shell. Its preToolUse denies the tool if a hook
    /// fails, so steps come from postToolUse only.
    /// </summary>
    static string Copilot(Commands c, bool steps)
    {
        JsonArray Run(string signal) => new(new JsonObject
        {
            ["type"] = "command", ["exec"] = c.Exe, ["args"] = new JsonArray(c.Args(signal, "copilot").Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()), ["timeoutSec"] = 5,
        });
        var hooks = new JsonObject
        {
            ["userPromptSubmitted"] = Run("working"),
            ["agentStop"] = Run("done"),
            ["notification"] = Run("attention"),
            ["sessionEnd"] = Run("end"),
        };
        if (steps) hooks["postToolUse"] = Run("did");
        return Json(new JsonObject { ["version"] = 1, ["hooks"] = hooks });
    }

    /// <summary>Windsurf: a command for bash and one for PowerShell (used on Windows); it has no session end or approval event.</summary>
    static string Windsurf(Commands c, bool steps)
    {
        JsonArray Run(string signal) => new(new JsonObject
        {
            ["command"] = c.Shell(signal, "windsurf"), ["powershell"] = c.PowerShell(signal, "windsurf"), ["show_output"] = false,
        });
        var hooks = new JsonObject
        {
            ["pre_user_prompt"] = Run("working"),
            ["post_cascade_response"] = Run("done"),
        };
        if (steps)
        {
            hooks["post_write_code"] = Run("did");
            hooks["post_run_command"] = Run("did");
        }
        return Json(new JsonObject { ["hooks"] = hooks });
    }

    /// <summary>Aider: one command when a reply is done and it waits for you (it cannot tell a question from the end of a reply).</summary>
    static string Aider(Commands c) =>
        "# .aider.conf.yml (or: aider --notifications --notifications-command \"…\")\n" +
        "notifications: true\n" +
        "notifications-command: '" + c.Shell("done", "aider").Replace("'", "''") + "'\n";

    static string CommandLines(Commands c)
    {
        var sb = new StringBuilder();
        sb.Append("# when the agent starts working\n").Append(c.Shell("working", "NAME")).Append('\n');
        sb.Append("# when it is done\n").Append(c.Shell("done", "NAME")).Append('\n');
        sb.Append("# when it needs you\n").Append(c.Shell("attention", "NAME")).Append('\n');
        sb.Append("# with hooks that pass JSON on stdin (session_id, cwd, hook_event_name), one line can do all three\n")
            .Append(c.Shell("auto", "NAME")).Append('\n');
        string status = c.Shell("x").Split(" --signal ")[0];
        sb.Append("# a status lane for any script: running, passed, failed or clear\n")
            .Append(status).Append(" --status deploy running --note staging\n")
            .Append(status).Append(" --status deploy passed\n");
        return sb.ToString();
    }
}
