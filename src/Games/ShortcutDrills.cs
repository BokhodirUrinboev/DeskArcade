using System;
using System.Collections.Generic;
using System.Linq;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>
/// One shortcut to learn: what it does, and the keys on each desktop. A chord drill is pressed (Ctrl+Shift+P, or a
/// sequence such as Ctrl+K Z); a typed drill is typed (Vim's <c>:wq</c>, the shell's <c>!!</c>), and any of its answers
/// will do. Keys are written as <see cref="KeyChord.Parse"/> reads them; alternatives are split by " | ".
/// </summary>
public sealed record ShortcutDrill(string Set, string Action, bool Typed, string Windows, string? Linux, string? Mac)
{
    /// <summary>A stable name for the misses kept in the settings: the set and the action.</summary>
    public string Id => Set + ":" + Action;

    /// <summary>The keys on a desktop: each answer a sequence of chords (a typed drill's are empty), none when the desktop lacks it.</summary>
    public IReadOnlyList<IReadOnlyList<KeyChord>> Chords(KeyOs os)
    {
        if (Typed) return Array.Empty<IReadOnlyList<KeyChord>>();
        return Answers(os).Select(a => (IReadOnlyList<KeyChord>)KeyChord.ParseSequence(a)!).ToList();
    }

    /// <summary>The answers as written for a desktop: Linux falls back to the Windows keys, a Mac never does.</summary>
    public IReadOnlyList<string> Answers(KeyOs os)
    {
        string? keys = os switch
        {
            KeyOs.Mac => Typed ? Windows : Mac,
            KeyOs.Linux => Linux ?? Windows,
            _ => Windows,
        };
        return string.IsNullOrEmpty(keys) ? Array.Empty<string>() : keys.Split(" | ");
    }

    public bool On(KeyOs os) => Answers(os).Count > 0;

    /// <summary>How the answer is shown after a miss: the first answer, in the desktop's own names.</summary>
    public string Shown(KeyOs os) => Typed ? Answers(os)[0] : KeyChord.Text(Chords(os)[0], os);
}

/// <summary>
/// The drills, by set: VS Code, JetBrains IDEs (the default keymaps), Vim, the shell (bash and zsh line editing) and
/// the desktop's everyday keys in apps and browsers. Shortcuts the desktop takes for itself before a window sees them
/// (Alt+F4, ⌘Q, ⌘H, the Windows and Super keys' own, Ctrl+Alt+T and Ctrl+Alt+L on Linux) are left out, or left out on
/// that desktop, since the typing window could never hear them.
/// </summary>
public static class ShortcutDrills
{
    public static readonly string[] Sets = { "vscode", "jetbrains", "vim", "shell", "desktop" };

    /// <summary>The set's name on its chip. Product names stay as they are.</summary>
    public static string SetName(string set) => set switch
    {
        "vscode" => "VS Code",
        "jetbrains" => "JetBrains",
        "vim" => "Vim",
        "shell" => L.T("Shell"),
        _ => L.T("Desktop"),
    };

    const string None = "";

    static ShortcutDrill K(string set, string action, string windows, string? linux, string? mac) => new(set, action, false, windows, linux, mac);
    static ShortcutDrill T(string set, string action, string typed) => new(set, action, true, typed, null, null);

    public static readonly IReadOnlyList<ShortcutDrill> All = new[]
    {
        K("vscode", "Show all commands", "Ctrl+Shift+P", null, "Cmd+Shift+P"),
        K("vscode", "Open a file by name", "Ctrl+P", null, "Cmd+P"),
        K("vscode", "Go to definition", "F12", null, "F12"),
        K("vscode", "Peek at the definition", "Alt+F12", "Ctrl+Shift+F10", "Option+F12"),
        K("vscode", "Show references", "Shift+F12", null, "Shift+F12"),
        K("vscode", "Rename symbol", "F2", null, "F2"),
        K("vscode", "Format the document", "Shift+Alt+F", "Ctrl+Shift+I", "Shift+Option+F"),
        K("vscode", "Comment the line out", "Ctrl+/", null, "Cmd+/"),
        K("vscode", "Show the terminal", "Ctrl+`", null, "Ctrl+`"),
        K("vscode", "Show or hide the sidebar", "Ctrl+B", null, "Cmd+B"),
        K("vscode", "Search all files", "Ctrl+Shift+F", null, "Cmd+Shift+F"),
        K("vscode", "Go to line", "Ctrl+G", null, "Ctrl+G"),
        K("vscode", "Go to a symbol in the file", "Ctrl+Shift+O", null, "Cmd+Shift+O"),
        K("vscode", "Move the line down", "Alt+Down", null, "Option+Down"),
        K("vscode", "Copy the line down", "Shift+Alt+Down", "Ctrl+Shift+Alt+Down", "Shift+Option+Down"),
        K("vscode", "Delete the line", "Ctrl+Shift+K", null, "Cmd+Shift+K"),
        K("vscode", "Add a cursor below", "Ctrl+Alt+Down", "Shift+Alt+Down", "Option+Cmd+Down"),
        K("vscode", "Select the next match", "Ctrl+D", null, "Cmd+D"),
        K("vscode", "Quick fix", "Ctrl+.", null, "Cmd+."),
        K("vscode", "Split the editor", "Ctrl+\\", null, "Cmd+\\"),
        K("vscode", "Go back", "Alt+Left", "Ctrl+Alt+-", "Ctrl+-"),
        K("vscode", "Open the settings", "Ctrl+,", null, "Cmd+,"),
        K("vscode", "Show the Explorer", "Ctrl+Shift+E", null, "Cmd+Shift+E"),
        K("vscode", "Zen mode", "Ctrl+K Z", null, "Cmd+K Z"),
        K("vscode", "Comment out a block", "Shift+Alt+A", "Ctrl+Shift+A", "Shift+Option+A"),

        K("jetbrains", "Find an action", "Ctrl+Shift+A", null, "Cmd+Shift+A"),
        K("jetbrains", "Go to a class", "Ctrl+N", null, "Cmd+O"),
        K("jetbrains", "Go to a file", "Ctrl+Shift+N", null, "Cmd+Shift+O"),
        K("jetbrains", "Go to the declaration", "Ctrl+B", null, "Cmd+B"),
        K("jetbrains", "Find usages", "Alt+F7", null, "Option+F7"),
        K("jetbrains", "Rename", "Shift+F6", null, "Shift+F6"),
        K("jetbrains", "Reformat the code", "Ctrl+Alt+L", None, "Cmd+Option+L"),
        K("jetbrains", "Optimize imports", "Ctrl+Alt+O", null, "Ctrl+Option+O"),
        K("jetbrains", "Show the quick fixes", "Alt+Enter", null, "Option+Enter"),
        K("jetbrains", "Recent files", "Ctrl+E", null, "Cmd+E"),
        K("jetbrains", "Duplicate the line", "Ctrl+D", null, "Cmd+D"),
        K("jetbrains", "Delete the line", "Ctrl+Y", null, "Cmd+Backspace"),
        K("jetbrains", "Comment the line out", "Ctrl+/", null, "Cmd+/"),
        K("jetbrains", "Extract a variable", "Ctrl+Alt+V", null, "Cmd+Option+V"),
        K("jetbrains", "Extract a method", "Ctrl+Alt+M", null, "Cmd+Option+M"),
        K("jetbrains", "Run", "Shift+F10", null, "Ctrl+R"),
        K("jetbrains", "Debug", "Shift+F9", null, "Ctrl+D"),
        K("jetbrains", "Toggle a breakpoint", "Ctrl+F8", null, "Cmd+F8"),
        K("jetbrains", "Step over", "F8", null, "F8"),
        K("jetbrains", "Go to line", "Ctrl+G", null, "Cmd+L"),
        K("jetbrains", "Surround with", "Ctrl+Alt+T", None, "Cmd+Option+T"),
        K("jetbrains", "Generate code", "Alt+Insert", null, "Cmd+N"),
        K("jetbrains", "File structure", "Ctrl+F12", null, "Cmd+F12"),
        K("jetbrains", "Complete the statement", "Ctrl+Shift+Enter", null, "Cmd+Shift+Enter"),

        T("vim", "Save and quit", ":wq | :x"),
        T("vim", "Quit without saving", ":q!"),
        T("vim", "Delete the line", "dd"),
        T("vim", "Undo", "u"),
        K("vim", "Redo", "Ctrl+R", null, "Ctrl+R"),
        T("vim", "Go to the first line", "gg"),
        T("vim", "Go to the last line", "G"),
        T("vim", "Copy the line", "yy"),
        T("vim", "Paste after the cursor", "p"),
        T("vim", "Change the word under the cursor", "ciw"),
        T("vim", "Delete to the end of the line", "D | d$"),
        T("vim", "Search forward for foo", "/foo"),
        T("vim", "Next match", "n"),
        T("vim", "Jump to the end of the line", "$"),
        T("vim", "Jump to the start of the line", "0 | ^"),
        T("vim", "Insert at the end of the line", "A"),
        T("vim", "Open a new line below", "o"),
        T("vim", "Replace every foo with bar", ":%s/foo/bar/g"),
        K("vim", "Visual block mode", "Ctrl+V", null, "Ctrl+V"),
        T("vim", "Indent the line", ">>"),
        T("vim", "Next word", "w"),
        K("vim", "Half a page down", "Ctrl+D", null, "Ctrl+D"),

        K("shell", "Search the history", "Ctrl+R", null, "Ctrl+R"),
        K("shell", "Go to the start of the line", "Ctrl+A", null, "Ctrl+A"),
        K("shell", "Go to the end of the line", "Ctrl+E", null, "Ctrl+E"),
        K("shell", "Delete the word before the cursor", "Ctrl+W", null, "Ctrl+W"),
        K("shell", "Clear the screen", "Ctrl+L", null, "Ctrl+L"),
        K("shell", "Cut to the end of the line", "Ctrl+K", null, "Ctrl+K"),
        K("shell", "Cut to the start of the line", "Ctrl+U", null, "Ctrl+U"),
        K("shell", "Stop the running command", "Ctrl+C", null, "Ctrl+C"),
        K("shell", "Send end of input", "Ctrl+D", null, "Ctrl+D"),
        K("shell", "Suspend the running command", "Ctrl+Z", null, "Ctrl+Z"),
        K("shell", "Paste what was cut", "Ctrl+Y", null, "Ctrl+Y"),
        K("shell", "Back one word", "Alt+B", null, None),
        T("shell", "Run the last command again", "!!"),
        T("shell", "The last command's last argument", "!$"),
        T("shell", "Back to the previous folder", "cd -"),
        T("shell", "The last command's exit code", "echo $?"),

        K("desktop", "Copy", "Ctrl+C", null, "Cmd+C"),
        K("desktop", "Paste", "Ctrl+V", null, "Cmd+V"),
        K("desktop", "Cut", "Ctrl+X", null, "Cmd+X"),
        K("desktop", "Undo", "Ctrl+Z", null, "Cmd+Z"),
        K("desktop", "Redo", "Ctrl+Y | Ctrl+Shift+Z", "Ctrl+Shift+Z", "Cmd+Shift+Z"),
        K("desktop", "Select all", "Ctrl+A", null, "Cmd+A"),
        K("desktop", "Find", "Ctrl+F", null, "Cmd+F"),
        K("desktop", "Save", "Ctrl+S", null, "Cmd+S"),
        K("desktop", "Print", "Ctrl+P", null, "Cmd+P"),
        K("desktop", "New tab", "Ctrl+T", null, "Cmd+T"),
        K("desktop", "Reopen the tab just closed", "Ctrl+Shift+T", null, "Cmd+Shift+T"),
        K("desktop", "Next tab", "Ctrl+Tab", null, "Ctrl+Tab"),
        K("desktop", "Reload the page", "F5 | Ctrl+R", null, "Cmd+R"),
        K("desktop", "Go to the address bar", "Ctrl+L", null, "Cmd+L"),
        K("desktop", "Paste as plain text", "Ctrl+Shift+V", null, "Cmd+Shift+Option+V"),
        K("desktop", "Zoom in", "Ctrl+=", null, "Cmd+="),
        K("desktop", "Reset the zoom", "Ctrl+0", null, "Cmd+0"),
        K("desktop", "Open a private window", "Ctrl+Shift+N", null, "Cmd+Shift+N"),
        K("desktop", "Bold", "Ctrl+B", null, "Cmd+B"),
        K("desktop", "Go to the top of the document", "Ctrl+Home", null, "Cmd+Up"),
        K("desktop", "Delete the word before the cursor", "Ctrl+Backspace", null, "Option+Backspace"),
    };

    /// <summary>The set's drills that this desktop has.</summary>
    public static List<ShortcutDrill> For(string set, KeyOs os) => All.Where(d => d.Set == set && d.On(os)).ToList();

    /// <summary>
    /// <paramref name="count"/> different drills, the ones missed before more often: a drill's chance goes up with its
    /// misses (<paramref name="misses"/>), so the keys you miss come back until you know them.
    /// </summary>
    public static List<ShortcutDrill> Pick(IReadOnlyList<ShortcutDrill> pool, int count, Func<ShortcutDrill, int> misses, Random rng)
    {
        var left = pool.ToList();
        var picked = new List<ShortcutDrill>();
        while (picked.Count < count && left.Count > 0)
        {
            var weights = left.Select(d => Weight(misses(d))).ToList();
            double r = rng.NextDouble() * weights.Sum();
            int k = 0;
            while (k < left.Count - 1 && (r -= weights[k]) >= 0) k++;
            picked.Add(left[k]);
            left.RemoveAt(k);
        }
        return picked;
    }

    /// <summary>A drill never missed has weight 1; each miss kept adds 2, up to five misses.</summary>
    public static double Weight(int misses) => 1 + 2 * Math.Clamp(misses, 0, 5);
}
