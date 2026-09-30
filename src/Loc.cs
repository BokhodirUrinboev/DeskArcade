using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;

namespace DeskArcade;

/// <summary>
/// UI text translation. Text is written in English in the code and looked up in the table of the language in use
/// (see <see cref="Strings"/>: one .po file per language); anything missing falls back to English.
/// </summary>
public static class L
{
    /// <summary>Choices for the language menu: setting value and display name (shown untranslated).</summary>
    public static IReadOnlyList<(string Code, string Name)> Languages { get; } =
        new[] { ("auto", "Automatic"), ("en", "English") }.Concat(Strings.Available).ToArray();

    static IReadOnlyDictionary<string, string>? _table;

    /// <summary>The active language: "en", or the code of a language with a .po file ("uz", "ru", ...).</summary>
    public static string Code { get; private set; } = "en";

    /// <summary>Raised after the active language changes.</summary>
    public static event Action? Changed;

    /// <param name="setting">"auto" (follow the system), "en", or a language code with a .po file.</param>
    public static void Apply(string? setting)
    {
        string code = setting == "en" || setting != null && Strings.For(setting) != null ? setting : Detect();
        _table = code == "en" ? null : Strings.For(code);
        bool changed = code != Code;
        Code = code;
        if (changed) Changed?.Invoke();
    }

    /// <summary>Translates an English UI string.</summary>
    public static string T(string english) =>
        _table != null && _table.TryGetValue(english, out var text) ? text : english;

    /// <summary>Translates an English format string, then fills in {0}, {1}, ...</summary>
    public static string F(string english, params object?[] args) =>
        string.Format(CultureInfo.InvariantCulture, T(english), args);

    /// <summary>
    /// The system UI language, when there is a translation for it; English otherwise. CultureInfo is invariant in this
    /// app, so ask the OS directly: Windows for its preferred UI languages ("ru-RU"), Linux and macOS through LANGUAGE
    /// ("uz:ru:en"), LC_ALL, LC_MESSAGES or LANG ("pt_BR.UTF-8").
    /// </summary>
    static string Detect()
    {
        try
        {
            foreach (string wanted in SystemLanguages())
            {
                string code = wanted.Replace('_', '-').Split('-', '.', '@')[0].ToLowerInvariant();
                if (code == "en") return "en";
                if (code.Length > 0 && Strings.For(code) != null) return code;
            }
        }
        catch
        {
            // fall back to English
        }
        return "en";
    }

    static IEnumerable<string> SystemLanguages()
    {
        if (OperatingSystem.IsWindows())
        {
            uint count = 0, size = 0;
            if (GetUserPreferredUILanguages(MuiLanguageName, ref count, null, ref size) && size > 0)
            {
                var buffer = new char[size];
                if (GetUserPreferredUILanguages(MuiLanguageName, ref count, buffer, ref size))
                    foreach (string name in new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries)) yield return name;
            }
            yield break;
        }
        foreach (var name in new[] { "LANGUAGE", "LC_ALL", "LC_MESSAGES", "LANG" })
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(value)) continue;
            foreach (string part in value.Split(':', StringSplitOptions.RemoveEmptyEntries)) yield return part;
            yield break;
        }
    }

    const uint MuiLanguageName = 0x8;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetUserPreferredUILanguages(uint flags, ref uint count, char[]? buffer, ref uint size);
}
