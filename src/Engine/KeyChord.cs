using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Avalonia.Input;
using AKey = Avalonia.Input.Key;

namespace DeskArcade.Engine;

[Flags]
public enum KeyMods { None = 0, Ctrl = 1, Alt = 2, Shift = 4, Meta = 8 }

/// <summary>The desktops, for the names of keys: Windows says Ctrl+Shift+P, Linux the same with Super, a Mac ⌘⇧P.</summary>
public enum KeyOs { Windows, Linux, Mac }

/// <summary>
/// A key pressed with its modifiers, as a shortcut: Ctrl+Shift+P, F12, ⌥F12. The key is a name that doesn't depend on the
/// keyboard layout (the letter or symbol printed on a US keyboard, or F12, Enter, Up); Meta is the Windows key, Super on
/// Linux, and Command on a Mac.
/// </summary>
public readonly record struct KeyChord(KeyMods Mods, string Key)
{
    public static KeyOs CurrentOs => OperatingSystem.IsMacOS() ? KeyOs.Mac : OperatingSystem.IsLinux() ? KeyOs.Linux : KeyOs.Windows;

    /// <summary>"Ctrl+Shift+P", "Cmd+Option+L", "Shift+F12" or a bare "F2". Null when a part isn't a known key.</summary>
    public static KeyChord? Parse(string text)
    {
        var mods = KeyMods.None;
        var parts = text.Trim().Split('+');
        // "Ctrl++" would split into an empty last part: shortcuts name that key "=" instead
        for (int i = 0; i < parts.Length - 1; i++)
        {
            var m = parts[i].Trim().ToLowerInvariant() switch
            {
                "ctrl" or "control" => KeyMods.Ctrl,
                "alt" or "option" or "opt" => KeyMods.Alt,
                "shift" => KeyMods.Shift,
                "cmd" or "command" or "win" or "super" or "meta" => KeyMods.Meta,
                _ => (KeyMods?)null,
            };
            if (m is not { } known) return null;
            mods |= known;
        }
        string key = Normalize(parts[^1].Trim());
        return key.Length == 0 ? null : new KeyChord(mods, key);
    }

    /// <summary>A sequence of chords pressed one after another, space-separated: "Ctrl+K Ctrl+C". Null when any part fails.</summary>
    public static List<KeyChord>? ParseSequence(string text)
    {
        var chords = new List<KeyChord>();
        foreach (string part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Parse(part) is not { } c) return null;
            chords.Add(c);
        }
        return chords.Count > 0 ? chords : null;
    }

    static readonly string[] Named =
    {
        "Enter", "Esc", "Tab", "Space", "Backspace", "Delete", "Insert", "Home", "End", "PageUp", "PageDown", "Up", "Down", "Left", "Right",
    };

    static string Normalize(string key)
    {
        if (key.Length == 1)
        {
            char c = char.ToUpperInvariant(key[0]);
            return c is >= 'A' and <= 'Z' or >= '0' and <= '9' || "`-=[]\\;',./".Contains(c) ? c.ToString() : "";
        }
        if (key.Length is 2 or 3 && (key[0] is 'F' or 'f') && int.TryParse(key.AsSpan(1), out int f) && f is >= 1 and <= 24) return "F" + f;
        if (key.Equals("Escape", StringComparison.OrdinalIgnoreCase)) return "Esc";
        if (key.Equals("Return", StringComparison.OrdinalIgnoreCase)) return "Enter";
        if (key.Equals("Del", StringComparison.OrdinalIgnoreCase)) return "Delete";
        return Named.FirstOrDefault(n => n.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? "";
    }

    /// <summary>A key press from Avalonia, or null for a modifier on its own or a key no shortcut uses.</summary>
    public static KeyChord? From(AKey key, KeyModifiers modifiers)
    {
        string? name = key switch
        {
            >= AKey.A and <= AKey.Z => ((char)('A' + (key - AKey.A))).ToString(),
            >= AKey.D0 and <= AKey.D9 => ((char)('0' + (key - AKey.D0))).ToString(),
            >= AKey.NumPad0 and <= AKey.NumPad9 => ((char)('0' + (key - AKey.NumPad0))).ToString(),
            >= AKey.F1 and <= AKey.F24 => "F" + (1 + (key - AKey.F1)),
            AKey.OemPeriod or AKey.Decimal => ".",
            AKey.OemComma => ",",
            AKey.OemQuestion or AKey.Divide => "/",
            AKey.OemSemicolon => ";",
            AKey.OemQuotes => "'",
            AKey.OemOpenBrackets => "[",
            AKey.OemCloseBrackets => "]",
            AKey.OemPipe or AKey.OemBackslash => "\\",
            AKey.OemMinus or AKey.Subtract => "-",
            AKey.OemPlus => "=",
            AKey.OemTilde => "`",
            AKey.Enter => "Enter",
            AKey.Escape => "Esc",
            AKey.Tab => "Tab",
            AKey.Space => "Space",
            AKey.Back => "Backspace",
            AKey.Delete => "Delete",
            AKey.Insert => "Insert",
            AKey.Home => "Home",
            AKey.End => "End",
            AKey.PageUp => "PageUp",
            AKey.PageDown => "PageDown",
            AKey.Up => "Up",
            AKey.Down => "Down",
            AKey.Left => "Left",
            AKey.Right => "Right",
            _ => null,
        };
        if (name == null) return null;
        var mods = KeyMods.None;
        if (modifiers.HasFlag(KeyModifiers.Control)) mods |= KeyMods.Ctrl;
        if (modifiers.HasFlag(KeyModifiers.Alt)) mods |= KeyMods.Alt;
        if (modifiers.HasFlag(KeyModifiers.Shift)) mods |= KeyMods.Shift;
        if (modifiers.HasFlag(KeyModifiers.Meta)) mods |= KeyMods.Meta;
        return new KeyChord(mods, name);
    }

    /// <summary>The caps to draw for the chord, one per key, in the order the desktop writes them: Ctrl, Shift, Alt, Win, then the key (⌃ ⌥ ⇧ ⌘ on a Mac).</summary>
    public IReadOnlyList<string> Caps(KeyOs os)
    {
        var caps = new List<string>();
        if (os == KeyOs.Mac)
        {
            if (Mods.HasFlag(KeyMods.Ctrl)) caps.Add("⌃");
            if (Mods.HasFlag(KeyMods.Alt)) caps.Add("⌥");
            if (Mods.HasFlag(KeyMods.Shift)) caps.Add("⇧");
            if (Mods.HasFlag(KeyMods.Meta)) caps.Add("⌘");
        }
        else
        {
            if (Mods.HasFlag(KeyMods.Ctrl)) caps.Add("Ctrl");
            if (Mods.HasFlag(KeyMods.Shift)) caps.Add("Shift");
            if (Mods.HasFlag(KeyMods.Alt)) caps.Add("Alt");
            if (Mods.HasFlag(KeyMods.Meta)) caps.Add(os == KeyOs.Linux ? "Super" : "Win");
        }
        caps.Add(KeyName(Key, os));
        return caps;
    }

    static string KeyName(string key, KeyOs os) => (key, os) switch
    {
        ("Up", _) => "↑",
        ("Down", _) => "↓",
        ("Left", _) => "←",
        ("Right", _) => "→",
        ("Enter", KeyOs.Mac) => "↩",
        ("Backspace", KeyOs.Mac) => "⌫",
        ("Delete", KeyOs.Mac) => "⌦",
        ("Esc", KeyOs.Mac) => "⎋",
        ("Tab", KeyOs.Mac) => "⇥",
        ("PageUp", _) => os == KeyOs.Mac ? "⇞" : "PgUp",
        ("PageDown", _) => os == KeyOs.Mac ? "⇟" : "PgDn",
        _ => key,
    };

    /// <summary>The chord as the desktop writes it: "Ctrl+Shift+P" on Windows and Linux, "⌘⇧P" on a Mac.</summary>
    public string Text(KeyOs os) => os == KeyOs.Mac ? string.Concat(Caps(os)) : string.Join("+", Caps(os));

    public static string Text(IEnumerable<KeyChord> sequence, KeyOs os) => string.Join(" ", sequence.Select(c => c.Text(os)));

    public override string ToString()
    {
        var s = new StringBuilder();
        foreach (var (flag, name) in new[] { (KeyMods.Ctrl, "Ctrl"), (KeyMods.Alt, "Alt"), (KeyMods.Shift, "Shift"), (KeyMods.Meta, "Meta") })
            if (Mods.HasFlag(flag)) s.Append(name).Append('+');
        return s.Append(Key).ToString();
    }
}

/// <summary>
/// A typing game that takes whole key presses too, for drilling shortcuts. The typing window asks it about each press
/// (with its modifiers) before anything else; one it wants goes to <see cref="ChordPressed"/> and nowhere else, one it
/// doesn't want is typed as usual.
/// </summary>
public interface IChordSink : IKeySink
{
    bool WantsChord(KeyChord chord);
    void ChordPressed(KeyChord chord);
}
