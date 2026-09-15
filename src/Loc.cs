using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;

namespace DeskArcade;

/// <summary>
/// UI text translation. Text is written in English in the code and looked up in the Uzbek or Russian
/// table in <see cref="Strings"/>; anything missing falls back to English.
/// </summary>
public static class L
{
    /// <summary>Choices for the language menu: setting value and display name (shown untranslated).</summary>
    public static readonly (string Code, string Name)[] Languages =
    {
        ("auto", "Automatic"), ("en", "English"), ("uz", "O'zbekcha"), ("ru", "Русский"),
    };

    static IReadOnlyDictionary<string, string>? _table;

    /// <summary>The active language: "en", "uz" or "ru".</summary>
    public static string Code { get; private set; } = "en";

    /// <summary>Raised after the active language changes.</summary>
    public static event Action? Changed;

    /// <param name="setting">"auto" (follow the system), "en", "uz" or "ru".</param>
    public static void Apply(string? setting)
    {
        string code = setting is "en" or "uz" or "ru" ? setting : Detect();
        _table = code switch { "uz" => Strings.Uzbek, "ru" => Strings.Russian, _ => null };
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

    /// <summary>The system UI language. CultureInfo is invariant in this app, so ask the OS directly.</summary>
    static string Detect()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                int primary = GetUserDefaultUILanguage() & 0x3FF;
                return primary == 0x19 ? "ru" : primary == 0x43 ? "uz" : "en";
            }
            foreach (var name in new[] { "LANGUAGE", "LC_ALL", "LC_MESSAGES", "LANG" })
            {
                string? value = Environment.GetEnvironmentVariable(name);
                if (string.IsNullOrEmpty(value)) continue;
                if (value.StartsWith("ru", StringComparison.OrdinalIgnoreCase)) return "ru";
                if (value.StartsWith("uz", StringComparison.OrdinalIgnoreCase)) return "uz";
                return "en";
            }
        }
        catch
        {
            // fall back to English
        }
        return "en";
    }

    [DllImport("kernel32.dll")]
    static extern ushort GetUserDefaultUILanguage();
}
