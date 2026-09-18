using System;
using System.Linq;
using DeskArcade.Platform;

namespace DeskArcade;

/// <summary>Which modifier keys the global shortcuts use. On macOS Alt is Option.</summary>
public enum ShortcutModifiers { CtrlAlt, CtrlShift, AltShift, CtrlAltShift }

/// <summary>
/// The global shortcuts: one modifier combination plus a letter per action (in <see cref="HotkeyAction"/>
/// order). Defaults to Ctrl+Alt+G / N / B (Control+Option on macOS).
/// </summary>
public sealed record HotkeySet(ShortcutModifiers Modifiers, string Keys)
{
    public static readonly HotkeySet Default = new(ShortcutModifiers.CtrlAlt, "GNB");

    /// <summary>Reads the settings, falling back to the defaults for anything invalid.</summary>
    public static HotkeySet From(string? modifiers, string? keys)
    {
        var mods = Enum.TryParse(modifiers, out ShortcutModifiers m) ? m : Default.Modifiers;
        return new HotkeySet(mods, Valid(keys) ? keys!.ToUpperInvariant() : Default.Keys);
    }

    /// <summary>Three different letters A–Z.</summary>
    public static bool Valid(string? keys) =>
        keys is { Length: 3 } && keys.All(char.IsAsciiLetter) && keys.ToUpperInvariant().Distinct().Count() == 3;

    public char Key(HotkeyAction action) => Keys[(int)action];

    public bool Ctrl => Modifiers is ShortcutModifiers.CtrlAlt or ShortcutModifiers.CtrlShift or ShortcutModifiers.CtrlAltShift;
    public bool Alt => Modifiers is ShortcutModifiers.CtrlAlt or ShortcutModifiers.AltShift or ShortcutModifiers.CtrlAltShift;
    public bool Shift => Modifiers is ShortcutModifiers.CtrlShift or ShortcutModifiers.AltShift or ShortcutModifiers.CtrlAltShift;

    /// <summary>"Ctrl+Alt+" (or "Ctrl+Option+" on macOS).</summary>
    public string ModifierLabel => ModifierLabelFor(Modifiers);

    public static string ModifierLabelFor(ShortcutModifiers m)
    {
        var set = new HotkeySet(m, Default.Keys);
        return (set.Ctrl ? "Ctrl+" : "") + (set.Alt ? OperatingSystem.IsMacOS() ? "Option+" : "Alt+" : "") + (set.Shift ? "Shift+" : "");
    }

    public string Label(HotkeyAction action) => ModifierLabel + Key(action);

    /// <summary>The XDG GlobalShortcuts "preferred_trigger", e.g. "CTRL+ALT+g".</summary>
    public string PortalTrigger(HotkeyAction action) =>
        (Ctrl ? "CTRL+" : "") + (Alt ? "ALT+" : "") + (Shift ? "SHIFT+" : "") + char.ToLowerInvariant(Key(action));
}

/// <summary>The shortcuts in use, for menus and hints.</summary>
public static class Shortcuts
{
    public static HotkeySet Current { get; set; } = HotkeySet.Default;

    public static string Label(HotkeyAction action) => Current.Label(action);
}
