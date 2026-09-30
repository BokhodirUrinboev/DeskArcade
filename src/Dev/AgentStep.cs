using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DeskArcade.Dev;

/// <summary>
/// What a coding agent is doing, read from a tool call its hooks pass on (PreToolUse / PostToolUse in Claude Code and
/// Codex, BeforeTool / AfterTool in Gemini CLI, postToolUse in Cursor and Copilot CLI): a kind ("edit", "run",
/// "read"...) and a target (the file, the command, the pattern), and the short label the scoreboard puts under the
/// session's line.
/// </summary>
public static class AgentStep
{
    /// <summary>The longest step label on the scoreboard, in characters.</summary>
    public const int MaxLabel = 40;

    /// <summary>The longest command kept for the done card's list.</summary>
    public const int MaxCommand = 30;

    /// <summary>Kinds whose target is a file the agent changed.</summary>
    public static bool TouchesFile(string kind) => kind is "edit" or "write";

    /// <summary>
    /// The kind and target of a tool call from its name and the string fields of its input. Claude Code's tools (Edit,
    /// Write, MultiEdit, NotebookEdit, Bash, PowerShell, Read, Grep, Glob, WebFetch, WebSearch, Agent/Task, TodoWrite,
    /// TaskCreate, mcp__server__tool), Codex's (Bash, apply_patch), Gemini CLI's (replace, write_file,
    /// run_shell_command, read_file, grep_search, glob, web_fetch, google_web_search, write_todos), Cursor's (Shell,
    /// Read, Write, Grep, Delete, Task, MCP:name) and Copilot CLI's (bash, powershell, view, create, edit, glob, grep,
    /// web_fetch, task); any other tool is "tool" with its name.
    /// </summary>
    public static (string Kind, string Target) FromTool(string? tool, IReadOnlyDictionary<string, string>? input)
    {
        if (string.IsNullOrWhiteSpace(tool)) return ("", "");
        string Str(params string[] names)
        {
            if (input == null) return "";
            foreach (string name in names)
                if (input.TryGetValue(name, out string? s) && s.Length > 0) return s;
            return "";
        }
        switch (tool)
        {
            case "Edit" or "MultiEdit" or "replace" or "edit" or "Delete":
                return ("edit", Str("file_path", "path"));
            case "NotebookEdit":
                return ("edit", Str("notebook_path", "file_path"));
            case "Write" or "write_file" or "create":
                return ("write", Str("file_path", "path"));
            case "apply_patch":
                return ("edit", PatchedFile(Str("command", "input", "patch")));
            case "Bash" or "PowerShell" or "Shell" or "run_shell_command" or "bash" or "powershell" or "shell":
                return ("run", Str("command"));
            case "Read" or "read_file" or "view":
                return ("read", Str("file_path", "absolute_path", "path"));
            case "Grep" or "Glob" or "grep_search" or "search_file_content" or "glob" or "grep":
                return ("search", Str("pattern", "query"));
            case "WebFetch" or "web_fetch":
                return ("fetch", Str("url", "prompt"));
            case "WebSearch" or "google_web_search":
                return ("websearch", Str("query"));
            case "Task" or "Agent" or "task":
                return ("task", Str("description", "subagent_type"));
            case "TodoWrite" or "TaskCreate" or "TaskUpdate" or "write_todos" or "update_plan":
                return ("plan", "");
        }
        if (tool.StartsWith("mcp__", StringComparison.Ordinal))
        {
            // mcp__github__create_issue: "github create_issue"
            return ("tool", string.Join(' ', tool.Split("__", StringSplitOptions.RemoveEmptyEntries).Skip(1)));
        }
        if (tool.StartsWith("MCP:", StringComparison.Ordinal)) return ("tool", tool[4..]);
        return ("tool", tool);
    }

    /// <summary>The first file an apply_patch patch touches ("*** Update File: src/app.py").</summary>
    public static string PatchedFile(string patch)
    {
        foreach (string raw in patch.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.Trim();
            foreach (string head in new[] { "*** Update File:", "*** Add File:", "*** Delete File:" })
                if (line.StartsWith(head, StringComparison.Ordinal)) return line[head.Length..].Trim();
        }
        return "";
    }

    /// <summary>"Editing Program.cs", "Running npm test", "Searching TODO": the step under a session's line, cut to <paramref name="max"/>.</summary>
    public static string Label(string kind, string target, int max = MaxLabel)
    {
        string format = kind switch
        {
            "edit" => L.T("Editing {0}"),
            "write" => L.T("Writing {0}"),
            "read" => L.T("Reading {0}"),
            "run" => L.T("Running {0}"),
            "search" => L.T("Searching {0}"),
            "fetch" => L.T("Reading {0}"),
            "websearch" => L.T("Searching the web for {0}"),
            "task" => L.T("Handing off {0}"),
            "plan" => L.T("Planning"),
            "tool" => L.T("Using {0}"),
            _ => "",
        };
        if (format.Length == 0) return "";
        if (!format.Contains("{0}", StringComparison.Ordinal)) return format;
        int room = Math.Max(8, max - (format.Length - 3));
        string shown = kind switch
        {
            "edit" or "write" or "read" => FileName(target, room),
            "fetch" => Host(target, room),
            "run" => Command(target, room),
            _ => Cut(OneLine(target), room),
        };
        if (shown.Length == 0)
        {
            // a call without a target still says what kind of work it is
            shown = kind switch { "run" => L.T("a command"), "search" => L.T("the code"), "task" => L.T("a task"), _ => "…" };
        }
        return string.Format(System.Globalization.CultureInfo.InvariantCulture, format, shown);
    }

    /// <summary>The file's own name, without its folders, cut to fit while keeping the extension: "VeryLongName….cs".</summary>
    public static string FileName(string path, int max = 32)
    {
        string name = OneLine(path).TrimEnd('/', '\\');
        int slash = name.LastIndexOfAny(new[] { '/', '\\' });
        if (slash >= 0) name = name[(slash + 1)..];
        if (name.Length <= max) return name;
        int dot = name.LastIndexOf('.');
        string ext = dot > 0 && name.Length - dot <= 8 ? name[dot..] : "";
        int keep = Math.Max(1, max - ext.Length - 1);
        return name[..keep] + "…" + ext;
    }

    /// <summary>A command's first line, with its runs of spaces made one, cut to fit; " …" marks more lines.</summary>
    public static string Command(string command, int max = MaxCommand)
    {
        var lines = command.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0) return "";
        string line = Cut(OneLine(lines[0]), max);
        return lines.Count > 1 && !line.EndsWith('…') && line.Length + 2 <= max ? line + " …" : line;
    }

    static string Host(string url, int max) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? Cut(uri.Host, max) : Cut(OneLine(url), max);

    /// <summary>The text with control characters as spaces and runs of whitespace made one.</summary>
    public static string OneLine(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool space = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                space = sb.Length > 0;
                continue;
            }
            if (space) sb.Append(' ');
            space = false;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>The text cut to <paramref name="max"/> characters, the last of them "…" when it was longer.</summary>
    public static string Cut(string text, int max)
    {
        if (max <= 0) return "";
        if (text.Length <= max) return text;
        return max == 1 ? "…" : text[..(max - 1)].TrimEnd() + "…";
    }
}
