using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DeskArcade.Games;

public enum BugLevel { Easy, Medium, Hard }

/// <summary>
/// One piece of code with exactly one bug in it, for Spot the Bug. <see cref="Bug"/> lists the lines that count as
/// finding it (1-based): usually one, two when the bug truly spans two lines. <see cref="Fix"/> replaces lines by
/// number to correct it; a replacement may hold several lines ("\n") or be empty to drop the line, and may touch a
/// line next to the bug. <see cref="Why"/> says why in each language ("en", "ru", "uz"); the code stays as code.
/// </summary>
public sealed class Snippet
{
    public required string Id { get; init; }
    /// <summary>"csharp", "typescript", "python" or "sql".</summary>
    public required string Lang { get; init; }
    public required BugLevel Level { get; init; }
    /// <summary>The kind of bug, an id such as "off-by-one" (see <see cref="SpotBugDeck.KindName"/>).</summary>
    public required string Kind { get; init; }
    public required IReadOnlyList<string> Code { get; init; }
    public required IReadOnlyList<int> Bug { get; init; }
    public required IReadOnlyDictionary<int, string> Fix { get; init; }
    public required IReadOnlyDictionary<string, string> Why { get; init; }
    /// <summary>C#: the buggy code does not even compile (the fixed one does).</summary>
    public bool CompileError { get; init; }

    public int Lines => Code.Count;

    public bool IsBug(int line) => Bug.Contains(line);

    /// <summary>The explanation in <paramref name="language"/>, or in English when it has none.</summary>
    public string WhyIn(string language) => Why.TryGetValue(language, out var text) && text.Length > 0 ? text : Why["en"];

    /// <summary>The replacement lines for <paramref name="line"/>, empty when the fix drops it; null when the fix leaves it alone.</summary>
    public IReadOnlyList<string>? FixFor(int line) =>
        Fix.TryGetValue(line, out var text) ? text.Length == 0 ? Array.Empty<string>() : text.Split('\n') : null;

    /// <summary>The code with the fix applied.</summary>
    public IReadOnlyList<string> Fixed()
    {
        var lines = new List<string>();
        for (int i = 1; i <= Code.Count; i++) lines.AddRange(FixFor(i) ?? new[] { Code[i - 1] });
        return lines;
    }

    public string Source => string.Join("\n", Code);
    public string FixedSource => string.Join("\n", Fixed());
}

/// <summary>
/// Spot the Bug's snippets, built into the program as JSON (spotbug/*.json), and the picks made from them: a round of
/// ten (easy ones first, hard ones last), seeded for a LAN race so both screens get the same ten, and the daily
/// snippet, the same for everyone on a date. UI-free, so it can be tested.
/// </summary>
public static class SpotBugDeck
{
    const string Prefix = "spotbug/";

    /// <summary>The languages in the order the chips show them.</summary>
    public static readonly string[] Languages = { "csharp", "typescript", "python", "sql" };

    /// <summary>A round: this many snippets of each level, easy first.</summary>
    public static readonly (BugLevel Level, int Count)[] RoundShape = { (BugLevel.Easy, 3), (BugLevel.Medium, 4), (BugLevel.Hard, 3) };

    public const int RoundSize = 10;

    /// <summary>Daily snippet #1 was on this date.</summary>
    public static readonly DateOnly FirstDaily = new(2026, 9, 1);

    static IReadOnlyList<Snippet>? _all;
    static Snippet[]? _dailyOrder;

    public static IReadOnlyList<Snippet> All => _all ??= Load();

    /// <summary>How a language is written on the chips and the code panel.</summary>
    public static string LanguageName(string lang) => lang switch
    {
        "csharp" => "C#",
        "typescript" => "TypeScript",
        "python" => "Python",
        "sql" => "SQL",
        _ => lang,
    };

    /// <summary>The kind of bug in words, for the tag after a snippet.</summary>
    public static string KindName(string kind) => kind switch
    {
        "off-by-one" => L.T("off-by-one"),
        "missing-await" => L.T("missing await"),
        "null" => L.T("null dereference"),
        "injection" => L.T("SQL injection"),
        "race" => L.T("race condition"),
        "comparison" => L.T("wrong comparison"),
        "overflow" => L.T("integer overflow"),
        "leak" => L.T("resource leak"),
        "shadowing" => L.T("shadowed variable"),
        "mutable-default" => L.T("mutable default"),
        "default" => L.T("default evaluated once"),
        "semicolon" => L.T("stray semicolon"),
        "logic" => L.T("wrong logic"),
        "mutation" => L.T("changed while iterating"),
        "closure" => L.T("captured loop variable"),
        "async" => L.T("async misuse"),
        "precision" => L.T("floating-point precision"),
        "types" => L.T("type mix-up"),
        "nulls" => L.T("NULL handling"),
        "join" => L.T("wrong join"),
        "grouping" => L.T("grouping and aggregates"),
        "dates" => L.T("dates and times"),
        "exception" => L.T("exception handling"),
        "equality" => L.T("equality mix-up"),
        "missing-key" => L.T("missing key"),
        "copy" => L.T("changed a copy"),
        "disposed" => L.T("used after dispose"),
        "scope" => L.T("wrong scope"),
        "aliasing" => L.T("shared reference"),
        "state" => L.T("hidden state"),
        "parsing" => L.T("parsing"),
        "iterator" => L.T("used-up iterator"),
        "ignored-result" => L.T("result thrown away"),
        "hash" => L.T("unstable hash"),
        "zero" => L.T("division by zero"),
        "atomicity" => L.T("not atomic"),
        "deferred" => L.T("deferred execution"),
        _ => kind,
    };

    /// <summary>Every kind id <see cref="KindName"/> knows.</summary>
    public static readonly string[] Kinds =
    {
        "off-by-one", "missing-await", "null", "injection", "race", "comparison", "overflow", "leak", "shadowing",
        "mutable-default", "default", "semicolon", "logic", "mutation", "closure", "async", "precision", "types", "nulls",
        "join", "grouping", "dates", "exception", "equality", "missing-key", "copy", "disposed", "scope", "aliasing",
        "state", "parsing", "iterator", "ignored-result", "hash", "zero", "atomicity", "deferred",
    };

    public static int DailyNumber(DateOnly day) => day.DayNumber - FirstDaily.DayNumber + 1;

    /// <summary>
    /// The daily snippet: the snippets go round in one fixed order (sorted by a hash of their ids, which mixes the
    /// languages and levels and doesn't depend on the process), one a day, so a snippet comes back only after all the
    /// others have had their day.
    /// </summary>
    public static Snippet Daily(DateOnly day)
    {
        var order = _dailyOrder ??= All.OrderBy(s => Mix(StableHash(s.Id))).ThenBy(s => s.Id, StringComparer.Ordinal).ToArray();
        long n = day.DayNumber - FirstDaily.DayNumber;
        return order[(int)(((n % order.Length) + order.Length) % order.Length)];
    }

    /// <summary>FNV-1a over the UTF-16 code units: the same on every machine and every run, unlike string.GetHashCode.</summary>
    public static uint StableHash(string text)
    {
        uint h = 2166136261;
        foreach (char c in text)
        {
            h ^= c;
            h *= 16777619;
        }
        return h;
    }

    /// <summary>MurmurHash3's finaliser: spreads FNV's bits, which on short ids like "cs-12" keep the ids of a language together.</summary>
    public static uint Mix(uint h)
    {
        h ^= h >> 16;
        h *= 0x85EBCA6B;
        h ^= h >> 13;
        h *= 0xC2B2AE35;
        h ^= h >> 16;
        return h;
    }

    /// <summary>
    /// Ten snippets for a round in <paramref name="lang"/> (null for every language): three easy, four medium and three
    /// hard, in that order, none twice. When a language runs short of a level, the round takes more of the next level
    /// (then of any level) so it still has ten. The same seed gives the same round on every screen.
    /// </summary>
    public static IReadOnlyList<Snippet> Round(Random rng, string? lang)
    {
        var pool = All.Where(s => lang == null || s.Lang == lang).ToList();
        var round = new List<Snippet>();
        int carry = 0;
        foreach (var (level, count) in RoundShape)
        {
            var of = Shuffle(pool.Where(s => s.Level == level && !round.Contains(s)).ToList(), rng);
            int want = count + carry;
            round.AddRange(of.Take(want));
            carry = Math.Max(0, want - of.Count);
        }
        if (round.Count < RoundSize)
            round.AddRange(Shuffle(pool.Where(s => !round.Contains(s)).ToList(), rng).Take(RoundSize - round.Count));
        return round;
    }

    static List<Snippet> Shuffle(List<Snippet> list, Random rng)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }

    // ------------------------------------------------------------------ loading

    static IReadOnlyList<Snippet> Load()
    {
        var assembly = typeof(SpotBugDeck).Assembly;
        var all = new List<Snippet>();
        foreach (string name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            all.AddRange(Parse(reader.ReadToEnd()));
        }
        return all.OrderBy(s => s.Id, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Reads a JSON array of snippets (the format of spotbug/*.json).</summary>
    public static IEnumerable<Snippet> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var list = new List<Snippet>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var fix = new Dictionary<int, string>();
            foreach (var p in e.GetProperty("fix").EnumerateObject()) fix[int.Parse(p.Name, CultureInfo.InvariantCulture)] = p.Value.GetString() ?? "";
            var why = new Dictionary<string, string>();
            foreach (var p in e.GetProperty("why").EnumerateObject()) why[p.Name] = p.Value.GetString() ?? "";
            list.Add(new Snippet
            {
                Id = e.GetProperty("id").GetString()!,
                Lang = e.GetProperty("lang").GetString()!,
                Level = e.GetProperty("level").GetString() switch { "easy" => BugLevel.Easy, "medium" => BugLevel.Medium, _ => BugLevel.Hard },
                Kind = e.GetProperty("kind").GetString()!,
                Code = e.GetProperty("code").EnumerateArray().Select(l => l.GetString() ?? "").ToArray(),
                Bug = e.GetProperty("bug").EnumerateArray().Select(l => l.GetInt32()).ToArray(),
                Fix = fix,
                Why = why,
                CompileError = e.TryGetProperty("compile", out var c) && c.GetString() == "error",
            });
        }
        return list;
    }
}
