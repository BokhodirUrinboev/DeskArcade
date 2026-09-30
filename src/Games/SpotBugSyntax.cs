using System;
using System.Collections.Generic;
using Avalonia.Media;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>What a piece of code is, for its colour.</summary>
public enum Tok { Plain, Keyword, Type, String, Number, Comment, Call, Punct }

/// <summary>
/// A small tokenizer for Spot the Bug's code panel: C#, TypeScript, Python and SQL, a line at a time, enough to colour
/// keywords, types, strings, numbers, comments and calls. It carries a block comment, a Python triple-quoted string or a
/// TypeScript template string from one line to the next in a <see cref="State"/>. It is not a parser: unknown text comes
/// out plain, never lost, so the tokens of a line always join back into the line.
/// </summary>
public static class SpotBugSyntax
{
    /// <summary>What is still open at the end of a line: the text that closes it, and whether it is a comment or a string.</summary>
    public struct State
    {
        public string? Close;
        public Tok Kind;
    }

    static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "async", "await", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class",
        "const", "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit",
        "extern", "false", "finally", "fixed", "float", "for", "foreach", "get", "goto", "if", "implicit", "in", "init",
        "int", "interface", "internal", "is", "lock", "long", "nameof", "namespace", "new", "null", "object", "operator",
        "out", "override", "params", "private", "protected", "public", "readonly", "record", "ref", "return", "sbyte",
        "sealed", "set", "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
        "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "var", "virtual", "void", "volatile",
        "when", "where", "while", "with", "yield",
    };

    static readonly HashSet<string> TypeScriptKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "async", "await", "break", "case", "catch", "class", "const", "constructor", "continue",
        "debugger", "declare", "default", "delete", "do", "else", "enum", "export", "extends", "false", "finally", "for",
        "from", "function", "get", "if", "implements", "import", "in", "instanceof", "interface", "is", "keyof", "let",
        "module", "namespace", "new", "null", "of", "private", "protected", "public", "readonly", "return", "set",
        "static", "super", "switch", "this", "throw", "true", "try", "type", "typeof", "undefined", "var", "void",
        "while", "with", "yield",
    };

    static readonly HashSet<string> TypeScriptTypes = new(StringComparer.Ordinal)
    {
        "any", "bigint", "boolean", "never", "number", "object", "string", "symbol", "unknown",
    };

    static readonly HashSet<string> PythonKeywords = new(StringComparer.Ordinal)
    {
        "False", "None", "True", "and", "as", "assert", "async", "await", "break", "case", "class", "continue", "def",
        "del", "elif", "else", "except", "finally", "for", "from", "global", "if", "import", "in", "is", "lambda", "match",
        "nonlocal", "not", "or", "pass", "raise", "return", "self", "try", "while", "with", "yield",
    };

    static readonly HashSet<string> PythonBuiltins = new(StringComparer.Ordinal)
    {
        "abs", "all", "any", "bool", "dict", "enumerate", "float", "hash", "int", "isinstance", "len", "list", "max",
        "min", "open", "print", "range", "repr", "round", "set", "sorted", "str", "sum", "super", "tuple", "type", "zip",
    };

    static readonly HashSet<string> SqlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ALL", "AND", "ANY", "AS", "ASC", "BEGIN", "BETWEEN", "BY", "CASE", "COMMIT", "CREATE", "CROSS", "DECLARE",
        "DELETE", "DESC", "DISTINCT", "ELSE", "END", "EXEC", "EXISTS", "FALSE", "FOR", "FROM", "FULL", "GROUP", "HAVING",
        "IN", "INNER", "INSERT", "INT", "INTO", "IS", "JOIN", "LEFT", "LIKE", "LIMIT", "NOT", "NULL", "NVARCHAR",
        "OFFSET", "ON", "OR", "ORDER", "OUTER", "OVER", "PARTITION", "PROCEDURE", "RIGHT", "ROLLBACK", "SELECT", "SET",
        "TABLE", "THEN", "TRUE", "UNION", "UPDATE", "VALUES", "VARCHAR", "WHEN", "WHERE", "WITH",
    };

    /// <summary>The tokens of every line of <paramref name="code"/>, with open comments and strings carried from line to line.</summary>
    public static List<List<(string Text, Tok Kind)>> Lines(string lang, IEnumerable<string> code)
    {
        var state = new State();
        var all = new List<List<(string, Tok)>>();
        foreach (string line in code) all.Add(Line(lang, line, ref state));
        return all;
    }

    /// <summary>The tokens of one line; <paramref name="state"/> says what the line before left open, and what this one leaves.</summary>
    public static List<(string Text, Tok Kind)> Line(string lang, string line, ref State state)
    {
        var tokens = new List<(string Text, Tok Kind)>();
        void Add(string text, Tok kind)
        {
            if (text.Length == 0) return;
            if (tokens.Count > 0 && tokens[^1].Kind == kind) tokens[^1] = (tokens[^1].Text + text, kind);
            else tokens.Add((text, kind));
        }

        int i = 0, n = line.Length;
        if (state.Close is { } close)
        {
            int end = line.IndexOf(close, StringComparison.Ordinal);
            if (end < 0)
            {
                Add(line, state.Kind);
                return tokens;
            }
            Add(line[..(end + close.Length)], state.Kind);
            i = end + close.Length;
            state = default;
        }

        bool sql = lang == "sql", py = lang == "python", cs = lang == "csharp";
        while (i < n)
        {
            char c = line[i];
            string rest = line[i..];
            if (char.IsWhiteSpace(c))
            {
                int j = i;
                while (j < n && char.IsWhiteSpace(line[j])) j++;
                Add(line[i..j], Tok.Plain);
                i = j;
                continue;
            }
            // comments
            if ((!py && !sql && rest.StartsWith("//", StringComparison.Ordinal)) || (py && c == '#') || (sql && rest.StartsWith("--", StringComparison.Ordinal)))
            {
                Add(rest, Tok.Comment);
                break;
            }
            if (!py && rest.StartsWith("/*", StringComparison.Ordinal))
            {
                int end = line.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    Add(rest, Tok.Comment);
                    state = new State { Close = "*/", Kind = Tok.Comment };
                    break;
                }
                Add(line[i..(end + 2)], Tok.Comment);
                i = end + 2;
                continue;
            }
            // strings
            int s = StringAt(lang, line, i, out int quote, out bool verbatim);
            if (s >= 0)
            {
                int end = StringEnd(lang, line, quote, verbatim, out string? open);
                if (open != null)
                {
                    Add(line[i..], Tok.String);
                    state = new State { Close = open, Kind = Tok.String };
                    break;
                }
                Add(line[i..end], Tok.String);
                i = end;
                continue;
            }
            // numbers
            if (char.IsDigit(c))
            {
                int j = i + 1;
                while (j < n && (char.IsLetterOrDigit(line[j]) || line[j] == '_' || (line[j] == '.' && j + 1 < n && char.IsDigit(line[j + 1])))) j++;
                Add(line[i..j], Tok.Number);
                i = j;
                continue;
            }
            // parameters and decorators: @name, :name, $1
            if ((sql && (c == '@' || c == ':' || c == '$') || py && c == '@') && i + 1 < n && (char.IsLetterOrDigit(line[i + 1]) || line[i + 1] == '_'))
            {
                int j = i + 1;
                while (j < n && (char.IsLetterOrDigit(line[j]) || line[j] == '_' || py && line[j] == '.')) j++;
                Add(line[i..j], py ? Tok.Call : Tok.Type);
                i = j;
                continue;
            }
            // words
            if (char.IsLetter(c) || c == '_' || cs && c == '@' && i + 1 < n && char.IsLetter(line[i + 1]))
            {
                int j = i + 1;
                while (j < n && (char.IsLetterOrDigit(line[j]) || line[j] == '_')) j++;
                string word = line[i..j];
                int k = j;
                while (k < n && line[k] == ' ') k++;
                Add(word, Classify(lang, word, call: k < n && line[k] == '('));
                i = j;
                continue;
            }
            // TypeScript regex literals, where a value is expected: /\d+/g
            if (lang == "typescript" && c == '/' && RegexAllowedAfter(tokens) && RegexEnd(line, i) is int regexEnd and > 0)
            {
                Add(line[i..regexEnd], Tok.String);
                i = regexEnd;
                continue;
            }
            Add(c.ToString(), Tok.Punct);
            i++;
        }
        return tokens;
    }

    static Tok Classify(string lang, string word, bool call) => lang switch
    {
        "csharp" => CSharpKeywords.Contains(word) ? Tok.Keyword : call ? Tok.Call : char.IsUpper(word[0]) ? Tok.Type : Tok.Plain,
        "typescript" => TypeScriptTypes.Contains(word) ? Tok.Type : TypeScriptKeywords.Contains(word) ? Tok.Keyword
            : call ? Tok.Call : char.IsUpper(word[0]) ? Tok.Type : Tok.Plain,
        "python" => PythonKeywords.Contains(word) ? Tok.Keyword : PythonBuiltins.Contains(word) ? Tok.Type : call ? Tok.Call : Tok.Plain,
        _ => SqlKeywords.Contains(word) ? Tok.Keyword : call ? Tok.Call : Tok.Plain,
    };

    /// <summary>Whether a string starts at <paramref name="i"/> (after any prefix such as $, @, f, r or N): the index of its quote, else -1.</summary>
    static int StringAt(string lang, string line, int i, out int quote, out bool verbatim)
    {
        quote = -1;
        verbatim = false;
        int j = i;
        switch (lang)
        {
            case "csharp":
                while (j < line.Length && j - i < 2 && (line[j] == '$' || line[j] == '@'))
                {
                    if (line[j] == '@') verbatim = true;
                    j++;
                }
                if (j < line.Length && (line[j] == '"' || line[j] == '\'' && j == i)) quote = j;
                else verbatim = false;
                break;
            case "typescript":
                if (line[j] is '"' or '\'' or '`') quote = j;
                break;
            case "python":
                if (i > 0 && (char.IsLetterOrDigit(line[i - 1]) || line[i - 1] == '_')) break; // inside a word
                while (j < line.Length && j - i < 2 && "rRbBfFuU".IndexOf(line[j]) >= 0)
                {
                    if (line[j] is 'r' or 'R') verbatim = true;
                    j++;
                }
                if (j < line.Length && (line[j] == '"' || line[j] == '\'')) quote = j;
                else verbatim = false;
                break;
            default:
                bool national = (line[j] == 'N' || line[j] == 'n') && j + 1 < line.Length && line[j + 1] == '\'' && (i == 0 || !char.IsLetterOrDigit(line[i - 1]));
                if (national) quote = j + 1;
                else if (line[j] == '\'') quote = j;
                break;
        }
        return quote;
    }

    /// <summary>A slash starts a regex rather than a division when no value comes before it: at the start, or after an operator or bracket.</summary>
    static bool RegexAllowedAfter(List<(string Text, Tok Kind)> tokens)
    {
        for (int t = tokens.Count - 1; t >= 0; t--)
        {
            string text = tokens[t].Text.TrimEnd();
            if (text.Length == 0) continue;
            return tokens[t].Kind == Tok.Punct && "=(,:[!&|?{};".Contains(text[^1]) || tokens[t].Kind == Tok.Keyword && text == "return";
        }
        return true;
    }

    /// <summary>The index just past a regex literal starting at <paramref name="i"/>, flags included; -1 when the line has no closing slash.</summary>
    static int RegexEnd(string line, int i)
    {
        bool inClass = false;
        for (int j = i + 1; j < line.Length; j++)
        {
            char c = line[j];
            if (c == '\\') j++;
            else if (c == '[') inClass = true;
            else if (c == ']') inClass = false;
            else if (c == '/' && !inClass)
            {
                int end = j + 1;
                while (end < line.Length && char.IsLetter(line[end])) end++;
                return end;
            }
        }
        return -1;
    }

    /// <summary>The index just past the string whose quote is at <paramref name="quote"/>; <paramref name="open"/> is what closes it when it runs past the line.</summary>
    static int StringEnd(string lang, string line, int quote, bool verbatim, out string? open)
    {
        open = null;
        char q = line[quote];
        if (lang == "python" && quote + 2 < line.Length && line[quote + 1] == q && line[quote + 2] == q)
        {
            string triple = new(q, 3);
            int end = line.IndexOf(triple, quote + 3, StringComparison.Ordinal);
            if (end < 0)
            {
                open = triple;
                return line.Length;
            }
            return end + 3;
        }
        for (int j = quote + 1; j < line.Length; j++)
        {
            char c = line[j];
            bool doubled = (lang == "sql" || lang == "csharp" && verbatim) && c == q && j + 1 < line.Length && line[j + 1] == q;
            if (doubled)
            {
                j++;
                continue;
            }
            if (c == '\\' && !verbatim && lang != "sql")
            {
                j++;
                continue;
            }
            if (c == q) return j + 1;
        }
        if (lang == "typescript" && q == '`') open = "`";
        return line.Length;
    }

    // ------------------------------------------------------------------ colours

    /// <summary>The code panel's colours in one theme: its paper, the gutter, and a readable colour for every kind of token.</summary>
    public sealed record Palette(bool Light, Color Back, Color Gutter, Color LineNumber, IReadOnlyDictionary<Tok, Color> Ink);

    /// <summary>The themes whose boards are light enough that code reads best on paper rather than on a dark editor.</summary>
    static readonly HashSet<string> LightThemes = new(StringComparer.Ordinal) { "winter", "candy", "spring" };

    /// <summary>The minimum contrast of code against the panel (WCAG's bar for body text), and of the line numbers.</summary>
    public const double TextContrast = 4.5, NumberContrast = 3.0;

    /// <summary>
    /// The code panel's colours for a theme: a dark editor for most themes and light paper for the pale ones, with the
    /// keywords taking the theme's accent, and every colour nudged lighter or darker until it reads against the paper.
    /// </summary>
    public static Palette PaletteFor(Theme t)
    {
        bool light = LightThemes.Contains(t.Id);
        var back = light ? Art.Blend(t.BoardLight ?? Color.FromRgb(246, 247, 250), Colors.White, 0.7) : Art.Blend(t.Ink, Color.FromRgb(8, 9, 14), 0.35);
        var gutter = light ? Art.Blend(back, t.Accent, 0.12) : Art.Blend(back, t.Accent, 0.08);
        var baseInk = light
            ? new Dictionary<Tok, Color>
            {
                [Tok.Plain] = Color.FromRgb(31, 35, 40), [Tok.Keyword] = Art.Blend(Color.FromRgb(0, 0, 200), t.Accent, 0.35),
                [Tok.Type] = Color.FromRgb(38, 127, 153), [Tok.String] = Color.FromRgb(163, 21, 21), [Tok.Number] = Color.FromRgb(9, 134, 88),
                [Tok.Comment] = Color.FromRgb(0, 128, 0), [Tok.Call] = Color.FromRgb(121, 94, 38), [Tok.Punct] = Color.FromRgb(87, 96, 106),
            }
            : new Dictionary<Tok, Color>
            {
                [Tok.Plain] = Color.FromRgb(212, 212, 212), [Tok.Keyword] = Art.Blend(Color.FromRgb(86, 156, 214), t.Accent, 0.4),
                [Tok.Type] = Color.FromRgb(78, 201, 176), [Tok.String] = Color.FromRgb(206, 145, 120), [Tok.Number] = Color.FromRgb(181, 206, 168),
                [Tok.Comment] = Color.FromRgb(106, 153, 85), [Tok.Call] = Color.FromRgb(220, 220, 170), [Tok.Punct] = Color.FromRgb(160, 168, 182),
            };
        var ink = new Dictionary<Tok, Color>();
        foreach (var (kind, c) in baseInk) ink[kind] = Readable(Art.Safe(c), back, TextContrast);
        var number = Readable(Art.Blend(light ? Color.FromRgb(110, 118, 129) : Color.FromRgb(133, 133, 133), t.Accent, 0.2), gutter, NumberContrast);
        return new Palette(light, back, gutter, number, ink);
    }

    /// <summary><paramref name="fg"/>, moved towards black or white (whichever the background is further from) until it reaches <paramref name="min"/> contrast.</summary>
    public static Color Readable(Color fg, Color bg, double min)
    {
        fg = Color.FromRgb(fg.R, fg.G, fg.B);
        var target = Luminance(bg) > 0.4 ? Colors.Black : Colors.White;
        for (int step = 0; step <= 20 && Contrast(fg, bg) < min; step++) fg = Art.Blend(fg, target, 0.12);
        return Contrast(fg, bg) >= min ? fg : target;
    }

    /// <summary>WCAG contrast ratio, 1 to 21.</summary>
    public static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    static double Luminance(Color c)
    {
        static double Lin(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }
}
