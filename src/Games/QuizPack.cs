using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace DeskArcade.Games;

/// <summary>One quiz question in one language: the question and its four answers, the right one first.</summary>
public sealed record QuizText(string Question, string[] Answers);

/// <summary>A question to ask, and the order its four answers are shown in: display place → answer (0 is the right one).</summary>
public sealed record QuizRound(QuizItem Item, int[] Order)
{
    /// <summary>Where the right answer is shown (0–3).</summary>
    public int RightPlace => Array.IndexOf(Order, 0);

    /// <summary>The question and its answers in the order they are shown.</summary>
    public QuizText Shown(string lang)
    {
        var t = Item.In(lang);
        return new QuizText(t.Question, Order.Select(i => t.Answers[i]).ToArray());
    }
}

/// <summary>
/// A question in every language it was written in: "en", "ru" and "uz" for the built-in packs, one language
/// (<see cref="QuizPack.AnyLanguage"/>) for a pack of your own. The answers line up across languages.
/// </summary>
public sealed class QuizItem
{
    public QuizItem(IReadOnlyDictionary<string, QuizText> texts)
    {
        if (texts.Count == 0) throw new ArgumentException("a question needs a text", nameof(texts));
        Texts = texts;
    }

    public IReadOnlyDictionary<string, QuizText> Texts { get; }

    /// <summary>The question in <paramref name="lang"/>, else in English, else in the one language it has.</summary>
    public QuizText In(string lang) =>
        Texts.TryGetValue(lang, out var t) ? t : Texts.TryGetValue("en", out var en) ? en : Texts.Values.First();
}

/// <summary>A mistake in a pack of your own: the line it is on (0 for the file as a whole) and what is wrong, in the player's language.</summary>
public sealed record QuizPackError(int Line, string Message);

/// <summary>
/// A pack of quiz questions. The built-in packs (programming, general knowledge, geography, science) are JSON files
/// under quiz/ built into the program, each question in English, Russian and Uzbek. A pack of your own is a plain text
/// file (see <see cref="Parse"/>), picked in the room setup or dropped into the quiz folder beside the settings
/// (<see cref="Folder"/>); the host sends its questions to the players one at a time.
/// </summary>
public sealed class QuizPack
{
    /// <summary>The language of a pack of your own: shown to everyone as written.</summary>
    public const string AnyLanguage = "*";
    public const string Mixed = "mixed";
    public const int MaxQuestionLength = 200, MaxAnswerLength = 60, MaxQuestions = 500, MaxFileBytes = 512 * 1024, MaxErrors = 20;
    public static readonly string[] BuiltInIds = { "programming", "general", "geography", "science" };
    public static readonly string[] Languages = { "en", "ru", "uz" };

    QuizPack(string id, IReadOnlyDictionary<string, string> titles, IReadOnlyList<QuizItem> items, string? path = null)
    {
        Id = id;
        Titles = titles;
        Items = items;
        Path = path;
    }

    /// <summary>"programming", "general", "geography", "science", "mixed", or "file:" and the path of a pack of your own.</summary>
    public string Id { get; }
    public IReadOnlyDictionary<string, string> Titles { get; }
    public IReadOnlyList<QuizItem> Items { get; }
    /// <summary>The file a pack of your own came from.</summary>
    public string? Path { get; }
    public bool Own => Path != null;

    public string Title(string lang) =>
        Titles.TryGetValue(lang, out var t) ? t : Titles.TryGetValue("en", out var en) ? en : Titles.Values.FirstOrDefault() ?? Id;

    // ------------------------------------------------------------------ the built-in packs

    static List<QuizPack>? _builtIn;
    static QuizPack? _mixed;

    /// <summary>The four built-in packs, in <see cref="BuiltInIds"/> order.</summary>
    public static IReadOnlyList<QuizPack> BuiltIn => _builtIn ??= BuiltInIds.Select(id => FromJson(Resource("quiz/" + id + ".json"))).ToList();

    /// <summary>Every built-in question together.</summary>
    public static QuizPack MixedPack => _mixed ??= new QuizPack(Mixed,
        new Dictionary<string, string> { ["en"] = "Mixed bag", ["ru"] = "Всё подряд", ["uz"] = "Aralash" },
        BuiltIn.SelectMany(p => p.Items).ToList());

    /// <summary>A built-in pack (or the mixed one) by id; null for anything else.</summary>
    public static QuizPack? ById(string id) => id == Mixed ? MixedPack : BuiltIn.FirstOrDefault(p => p.Id == id);

    static string Resource(string name)
    {
        using var stream = typeof(QuizPack).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException("missing resource " + name);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    sealed class PackFile
    {
        public string Id { get; set; } = "";
        public Dictionary<string, string> Title { get; set; } = new();
        public List<Dictionary<string, TextFile>> Questions { get; set; } = new();
    }

    sealed class TextFile
    {
        public string Q { get; set; } = "";
        public List<string> A { get; set; } = new();
    }

    /// <summary>A built-in pack from its JSON: each question in every language, the right answer first.</summary>
    public static QuizPack FromJson(string json)
    {
        var file = JsonSerializer.Deserialize<PackFile>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("an empty pack");
        var items = file.Questions.Select(q => new QuizItem(q.ToDictionary(kv => kv.Key, kv => new QuizText(kv.Value.Q, kv.Value.A.ToArray())))).ToList();
        return new QuizPack(file.Id, file.Title, items);
    }

    // ------------------------------------------------------------------ packs of your own

    /// <summary>Where packs of your own are looked for: a "quiz" folder beside settings.json.</summary>
    public static string Folder => System.IO.Path.Combine(Settings.DataDirectory, "quiz");

    /// <summary>The text files in <see cref="Folder"/>, by name.</summary>
    public static List<string> FolderFiles()
    {
        try
        {
            return Directory.Exists(Folder)
                ? Directory.GetFiles(Folder, "*.txt").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string>();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new List<string>();
        }
    }

    /// <summary>Makes <see cref="Folder"/> with an example pack in it, if it isn't there yet.</summary>
    public static void EnsureFolder()
    {
        try
        {
            if (Directory.Exists(Folder)) return;
            Directory.CreateDirectory(Folder);
            File.WriteAllText(System.IO.Path.Combine(Folder, "example.txt"), Example, new UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // best effort: the file picker still works
        }
    }

    /// <summary>A small pack that shows the format, written into a new <see cref="Folder"/>.</summary>
    public const string Example =
        "# A Quiz Night pack of your own: a plain text file (UTF-8), in any language.\n" +
        "# A line starting with # is a comment. The title line is optional.\n" +
        "# Each question starts with Q: and has four answers below it:\n" +
        "# + before the right one, - before each of the three wrong ones.\n" +
        "\n" +
        "Title: Our office\n" +
        "\n" +
        "Q: How many legs does a spider have?\n" +
        "+ 8\n" +
        "- 6\n" +
        "- 10\n" +
        "- 12\n" +
        "\n" +
        "Q: Which planet is known as the Red Planet?\n" +
        "- Venus\n" +
        "+ Mars\n" +
        "- Jupiter\n" +
        "- Mercury\n";

    /// <summary>Reads a pack of your own from a file (see <see cref="Parse"/>).</summary>
    public static (QuizPack? Pack, List<QuizPackError> Errors) LoadFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxFileBytes) return (null, new List<QuizPackError> { new(0, L.F("The file is too big: at most {0} KB", MaxFileBytes / 1024)) });
            return Parse(File.ReadAllText(path, Encoding.UTF8), System.IO.Path.GetFileNameWithoutExtension(path), path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return (null, new List<QuizPackError> { new(0, L.F("Can't read the file: {0}", e.Message)) });
        }
    }

    /// <summary>
    /// A pack of your own from its text. The format, line by line: "#" starts a comment; an optional "Title: …" before the
    /// first question names the pack (the file name does otherwise); "Q: …" starts a question; the four lines after it are its
    /// answers, "+ …" for the right one and "- …" for each wrong one. Blank lines don't matter. Every mistake is reported with its
    /// line number, and a pack with any mistake is not used.
    /// </summary>
    public static (QuizPack? Pack, List<QuizPackError> Errors) Parse(string text, string name, string? path = null)
    {
        var errors = new List<QuizPackError>();
        var items = new List<QuizItem>();
        string? title = null, question = null;
        int questionLine = 0;
        bool broken = false; // the question being read already has a mistake reported
        var answers = new List<(string Text, bool Right)>();

        void Error(int line, string message)
        {
            if (errors.Count < MaxErrors) errors.Add(new QuizPackError(line, message));
        }

        void Close()
        {
            if (question == null) return;
            if (!broken)
            {
                if (answers.Count != 4) Error(questionLine, L.F("Line {0}: a question needs four answers; this one has {1}", questionLine, answers.Count));
                else if (answers.Count(a => a.Right) != 1) Error(questionLine, L.F("Line {0}: mark exactly one answer as right, with +", questionLine));
                else if (answers.Select(a => Squash(a.Text)).Distinct().Count() != 4) Error(questionLine, L.F("Line {0}: two of the answers are the same", questionLine));
                else
                {
                    var ordered = answers.Where(a => a.Right).Concat(answers.Where(a => !a.Right)).Select(a => a.Text).ToArray();
                    items.Add(new QuizItem(new Dictionary<string, QuizText> { [AnyLanguage] = new QuizText(question, ordered) }));
                }
            }
            question = null;
            broken = false;
            answers.Clear();
        }

        var lines = text.TrimStart('﻿').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            int n = i + 1;
            string line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("title:", StringComparison.OrdinalIgnoreCase))
            {
                if (question != null || items.Count > 0) Error(n, L.F("Line {0}: the title goes before the first question", n));
                else title = line[6..].Trim();
                continue;
            }
            if (line.StartsWith("q:", StringComparison.OrdinalIgnoreCase))
            {
                Close();
                question = line[2..].Trim();
                questionLine = n;
                broken = true;
                if (question.Length == 0) Error(n, L.F("Line {0}: the question is empty", n));
                else if (question.Length > MaxQuestionLength) Error(n, L.F("Line {0}: the question is too long ({1} characters, at most {2})", n, question.Length, MaxQuestionLength));
                else broken = false;
                if (items.Count >= MaxQuestions)
                {
                    Error(n, L.F("Line {0}: a pack has at most {1} questions", n, MaxQuestions));
                    break;
                }
                continue;
            }
            if (line[0] is '+' or '-')
            {
                string answer = line[1..].Trim();
                if (question == null)
                {
                    Error(n, L.F("Line {0}: an answer before any question (start a question with Q:)", n));
                    continue;
                }
                if (answers.Count == 4)
                {
                    if (!broken) Error(n, L.F("Line {0}: a fifth answer: a question has four", n));
                    broken = true;
                    continue;
                }
                if (answer.Length == 0) Error(n, L.F("Line {0}: the answer is empty", n));
                else if (answer.Length > MaxAnswerLength) Error(n, L.F("Line {0}: the answer is too long ({1} characters, at most {2})", n, answer.Length, MaxAnswerLength));
                broken |= answer.Length == 0 || answer.Length > MaxAnswerLength;
                answers.Add((answer, line[0] == '+'));
                continue;
            }
            Error(n, L.F("Line {0}: start a question with Q: and each answer with + (right) or - (wrong)", n));
        }
        Close();
        if (items.Count == 0 && errors.Count == 0) Error(0, L.T("The file has no questions: start each one with Q:"));
        if (errors.Count > 0) return (null, errors.OrderBy(e => e.Line).ToList()); // a question's own mistakes are found when it ends
        var titles = new Dictionary<string, string> { [AnyLanguage] = string.IsNullOrWhiteSpace(title) ? name : title! };
        return (new QuizPack("file:" + (path ?? name), titles, items, path ?? name), errors);
    }

    /// <summary>An answer as the duplicate check sees it: trimmed, spaces squeezed, case ignored.</summary>
    static string Squash(string s) => string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
}

/// <summary>
/// Which questions a quiz asks, and in what order their answers are shown. The daily ten are the same for everyone on a
/// date: two or three from each built-in pack, taken in turn along a fixed shuffle of the pack, so a question comes back
/// only after the whole pack has been through.
/// </summary>
public static class QuizDeck
{
    public const int DailyCount = 10;

    /// <summary>A small random generator whose sequence never changes (SplitMix64), for picks that must agree everywhere.</summary>
    public sealed class Seeded
    {
        ulong _state;

        public Seeded(ulong seed) => _state = seed;

        public ulong Next()
        {
            ulong z = _state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        /// <summary>0 ≤ n &lt; <paramref name="max"/>.</summary>
        public int Below(int max) => (int)(Next() % (ulong)Math.Max(1, max));

        public int[] Shuffled(int count)
        {
            var a = Enumerable.Range(0, count).ToArray();
            for (int i = count - 1; i > 0; i--)
            {
                int j = Below(i + 1);
                (a[i], a[j]) = (a[j], a[i]);
            }
            return a;
        }
    }

    /// <summary>A stable hash of a text (FNV-1a), for seeds.</summary>
    public static ulong Hash(string text)
    {
        ulong h = 14695981039346656037UL;
        foreach (char c in text)
        {
            h ^= c;
            h *= 1099511628211UL;
        }
        return h;
    }

    /// <summary>Days before <paramref name="day"/> (from day 0) whose number leaves <paramref name="rest"/> when divided by 4.</summary>
    static long DaysWithRest(long day, int rest) => (day + 3 - rest) / 4;

    /// <summary>The questions a built-in pack gives on a day: two, and one more on two days out of four, turn about.</summary>
    public static int Share(long day, int pack) => 2 + (day % 4 == pack ? 1 : 0) + (day % 4 == (pack + 3) % 4 ? 1 : 0);

    /// <summary>
    /// The day's ten questions from the four built-in packs (the same for everyone, whatever their language): pack by pack,
    /// the next ones along that pack's fixed shuffle, taken in turn starting with a different pack each day, with the answers
    /// in an order that depends on the day.
    /// </summary>
    public static List<QuizRound> Daily(DateOnly date, IReadOnlyList<QuizPack>? packs = null)
    {
        packs ??= QuizPack.BuiltIn;
        if (packs.Count != 4) throw new ArgumentException("the daily ten come from four packs", nameof(packs));
        long day = date.DayNumber;
        var perPack = new List<List<QuizItem>>();
        for (int p = 0; p < 4; p++)
        {
            var items = packs[p].Items;
            var order = new Seeded(Hash("quiz-daily|" + packs[p].Id)).Shuffled(items.Count);
            long taken = 2 * day + DaysWithRest(day, p) + DaysWithRest(day, (p + 3) % 4);
            int share = Share(day, p);
            perPack.Add(Enumerable.Range(0, share).Select(k => items[order[(int)((taken + k) % items.Count)]]).ToList());
        }
        var picked = new List<QuizItem>();
        for (int k = 0; picked.Count < DailyCount; k++)
            for (int i = 0; i < 4 && picked.Count < DailyCount; i++)
            {
                var list = perPack[(int)((day + i) % 4)];
                if (k < list.Count) picked.Add(list[k]);
            }
        var answers = new Seeded(Hash("quiz-answers|") ^ (ulong)day);
        return picked.Select(item => new QuizRound(item, answers.Shuffled(4))).ToList();
    }

    /// <summary><paramref name="count"/> questions from a pack in a random order (all of them if it has fewer), answers shuffled.</summary>
    public static List<QuizRound> Pick(QuizPack pack, int count, Random rng)
    {
        var order = Enumerable.Range(0, pack.Items.Count).OrderBy(_ => rng.Next()).Take(Math.Max(1, count)).ToList();
        return order.Select(i => new QuizRound(pack.Items[i], Enumerable.Range(0, 4).OrderBy(_ => rng.Next()).ToArray())).ToList();
    }
}
