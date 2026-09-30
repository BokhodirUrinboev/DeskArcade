using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace DeskArcade;

/// <summary>
/// Translations, keyed by the exact English text passed to <see cref="L.T"/> or <see cref="L.F"/>. They live in gettext
/// files, i18n/&lt;code&gt;.po, built into the program, so translators can use Weblate, Poedit or any .po editor; the
/// English text is the msgid. A language is whatever .po file is there: its "Language" header is the code and its
/// "X-Language-Name" header the name the language menu shows. i18n/parts/*.&lt;code&gt;.po add to a language (new text
/// waiting to be folded into the main file). Keep {0}-style placeholders, "·" separators and "×" intact, and keep the
/// number last in "Best {0}"-style strings (the compact scoreboard shows only the last word). Missing entries, and
/// entries marked fuzzy, show in English. i18n/deskarcade.pot lists every English string, for new languages.
/// </summary>
public static class Strings
{
    const string Prefix = "i18n/", PartsPrefix = "i18n/parts/";

    static Dictionary<string, (string Name, Dictionary<string, string> Table)>? _languages;

    /// <summary>The languages there are .po files for, by code: their own name and their table.</summary>
    static Dictionary<string, (string Name, Dictionary<string, string> Table)> Languages => _languages ??= Load();

    public static IReadOnlyDictionary<string, string> Uzbek => For("uz") ?? new Dictionary<string, string>();

    public static IReadOnlyDictionary<string, string> Russian => For("ru") ?? new Dictionary<string, string>();

    /// <summary>The table for a language code, or null when there is no .po file for it.</summary>
    public static IReadOnlyDictionary<string, string>? For(string code) => Languages.TryGetValue(code, out var l) ? l.Table : null;

    /// <summary>Every language with a .po file, as (code, name): Uzbek and Russian first, then the rest by code.</summary>
    public static IEnumerable<(string Code, string Name)> Available =>
        Languages.OrderBy(kv => kv.Key switch { "uz" => 0, "ru" => 1, _ => 2 }).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => (kv.Key, kv.Value.Name));

    static Dictionary<string, (string Name, Dictionary<string, string> Table)> Load()
    {
        var languages = new Dictionary<string, (string Name, Dictionary<string, string> Table)>();
        var assembly = typeof(Strings).Assembly;
        var names = assembly.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".po", StringComparison.Ordinal)).ToList();
        // the main files first, so a part can only add to a language
        foreach (string name in names.Where(n => !n.StartsWith(PartsPrefix, StringComparison.Ordinal)))
        {
            var po = Read(assembly, name);
            string code = po.Code ?? Path.GetFileNameWithoutExtension(name);
            languages[code] = (po.LanguageName ?? code, po.Entries);
        }
        foreach (string name in names.Where(n => n.StartsWith(PartsPrefix, StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal))
        {
            // "i18n/parts/poker.ru.po" adds to Russian
            string file = Path.GetFileNameWithoutExtension(name);
            string code = file[(file.LastIndexOf('.') + 1)..];
            if (!languages.TryGetValue(code, out var language)) continue;
            foreach (var (key, value) in Read(assembly, name).Entries) language.Table.TryAdd(key, value);
        }
        return languages;
    }

    static PoFile Read(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return PoFile.Parse(reader.ReadToEnd());
    }
}

/// <summary>
/// A gettext .po file, as far as Desk Arcade needs one: msgid and msgstr pairs (no plurals, no contexts), with the
/// strings continued over several quoted lines, C-style escapes, and entries marked "#, fuzzy" left out.
/// </summary>
public sealed class PoFile
{
    public Dictionary<string, string> Entries { get; } = new();
    /// <summary>Every msgid in the order met, repeats included (for checking a file for duplicates).</summary>
    public List<string> Ids { get; } = new();
    public string? Code { get; private set; }
    public string? LanguageName { get; private set; }

    public static PoFile Parse(string text)
    {
        var po = new PoFile();
        string? id = null, str = null;
        bool fuzzy = false, inStr = false;

        void Flush()
        {
            if (id == null) return;
            if (id.Length == 0)
            {
                foreach (string line in (str ?? "").Split('\n'))
                {
                    int colon = line.IndexOf(':');
                    if (colon < 0) continue;
                    string key = line[..colon].Trim(), value = line[(colon + 1)..].Trim();
                    if (key.Equals("Language", StringComparison.OrdinalIgnoreCase) && value.Length > 0) po.Code = value.Replace('_', '-').Split('-')[0].ToLowerInvariant();
                    else if (key.Equals("X-Language-Name", StringComparison.OrdinalIgnoreCase) && value.Length > 0) po.LanguageName = value;
                }
            }
            else
            {
                po.Ids.Add(id);
                if (!fuzzy && !string.IsNullOrEmpty(str)) po.Entries[id] = str;
            }
            id = str = null;
            fuzzy = inStr = false;
        }

        foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0)
            {
                Flush();
                continue;
            }
            if (line.StartsWith('#'))
            {
                if (str != null) Flush(); // a comment starts the next entry
                if (line.StartsWith("#,", StringComparison.Ordinal) && line.Contains("fuzzy", StringComparison.Ordinal)) fuzzy = true;
                continue;
            }
            if (line.StartsWith("msgid ", StringComparison.Ordinal))
            {
                if (id != null) Flush();
                id = Unquote(line[6..]);
                inStr = false;
            }
            else if (line.StartsWith("msgstr ", StringComparison.Ordinal))
            {
                str = Unquote(line[7..]);
                inStr = true;
            }
            else if (line.StartsWith('"'))
            {
                if (inStr) str += Unquote(line);
                else if (id != null) id += Unquote(line);
            }
        }
        Flush();
        return po;
    }

    /// <summary>The text of a quoted .po string, escapes undone.</summary>
    static string Unquote(string quoted)
    {
        quoted = quoted.Trim();
        if (quoted.Length < 2 || quoted[0] != '"' || quoted[^1] != '"') return "";
        var sb = new StringBuilder();
        for (int i = 1; i < quoted.Length - 1; i++)
        {
            char c = quoted[i];
            if (c != '\\' || i + 1 >= quoted.Length - 1)
            {
                sb.Append(c);
                continue;
            }
            char n = quoted[++i];
            sb.Append(n switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => n });
        }
        return sb.ToString();
    }

    /// <summary>A string as a .po file writes it, quoted and escaped.</summary>
    public static string Quote(string text) =>
        "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";
}
