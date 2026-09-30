using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DeskArcade.Games;

/// <summary>How a letter of a guess did: not in the word, in the word somewhere else, or right where it is.</summary>
public enum WordMark { Gray, Yellow, Green }

/// <summary>Why a guess was taken or turned back.</summary>
public enum WordSubmit { Accepted, TooShort, NotAWord, Over }

/// <summary>
/// One language's words for Word Guess: the answers (the daily word walks down this list, one line a day) and every
/// word accepted as a guess, from <c>words/{name}-answers.txt</c> and <c>words/{name}-allowed.txt</c>. A word is five
/// letters, and a letter is a tile: in Uzbek o' g' sh ch ng are one letter each (any apostrophe will do), and in Russian
/// ё counts as е, as it does in most writing.
/// </summary>
public sealed class WordList
{
    public const int Length = 5;

    /// <summary>The first daily word (day 1).</summary>
    public static readonly DateOnly FirstDay = new(2026, 9, 1);

    public static readonly string[] Codes = { "en", "ru", "uz" };
    static readonly string[] Files = { "english", "russian", "uzbek" };

    static readonly Dictionary<string, WordList> Loaded = new();

    public string Code { get; }
    public IReadOnlyList<string> Answers { get; }
    readonly HashSet<string> _allowed;

    /// <summary>
    /// Whether a guess has to be on the list. Uzbek's list is short (its word forms are many and no free list covers
    /// them), so there any five letters are a guess rather than turning back a real word the list lacks.
    /// </summary>
    public bool Strict => Code != "uz";

    WordList(string code, IEnumerable<string> answers, IEnumerable<string> allowed)
    {
        Code = code;
        Answers = answers.Select(w => Key(code, w)).ToList();
        _allowed = allowed.Select(w => Key(code, w)).Concat(Answers).ToHashSet();
    }

    /// <summary>The list for a language code (en, ru, uz), loaded once from the embedded files.</summary>
    public static WordList For(string code)
    {
        int k = Math.Max(0, Array.IndexOf(Codes, code));
        lock (Loaded)
        {
            if (Loaded.TryGetValue(Codes[k], out var list)) return list;
            list = new WordList(Codes[k], Read($"words/{Files[k]}-answers.txt"), Read($"words/{Files[k]}-allowed.txt"));
            Loaded[Codes[k]] = list;
            return list;
        }
    }

    /// <summary>A list made in code, for the tests.</summary>
    public static WordList Of(string code, IEnumerable<string> answers, IEnumerable<string> allowed) => new(code, answers, allowed);

    static IEnumerable<string> Read(string resource)
    {
        using var stream = typeof(WordList).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"no embedded {resource}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var words = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            line = line.Trim();
            if (line.Length > 0 && !line.StartsWith('#')) words.Add(line);
        }
        return words;
    }

    public bool Allows(string[] tiles) => !Strict || _allowed.Contains(string.Concat(tiles));

    /// <summary>The word for a day: the same for everyone, going down the answers one line a day.</summary>
    public string[] Daily(DateOnly day) => Tiles(Code, Answers[DailyIndex(day) % Answers.Count]);

    /// <summary>Day 1 is <see cref="FirstDay"/>; the number shown in the shared line.</summary>
    public static int DailyNumber(DateOnly day) => DailyIndex(day) + 1;

    static int DailyIndex(DateOnly day) => Math.Max(0, day.DayNumber - FirstDay.DayNumber);

    public string[] Random(Random rng) => Tiles(Code, Answers[rng.Next(Answers.Count)]);

    /// <summary>A word as it is kept: lower case, one apostrophe, ё as е.</summary>
    static string Key(string code, string word) => string.Concat(Tiles(code, word));

    /// <summary>
    /// A word (or what has been typed so far) as its letters, lower case, dropping anything that isn't a letter of the
    /// language. Uzbek reads o' g' sh ch ng as one letter each, except ng before an apostrophe, which is n and g'.
    /// </summary>
    public static string[] Tiles(string code, string text)
    {
        var tiles = new List<string>();
        string s = Plain(code, text);
        for (int i = 0; i < s.Length;)
        {
            if (code == "uz" && i + 1 < s.Length)
            {
                string two = s.Substring(i, 2);
                bool ng = two == "ng" && !(i + 2 < s.Length && s[i + 2] == '\'');
                if (two is "o'" or "g'" or "sh" or "ch" || ng)
                {
                    tiles.Add(two);
                    i += 2;
                    continue;
                }
            }
            if (IsLetter(code, s[i])) tiles.Add(s[i].ToString());
            i++;
        }
        return tiles.ToArray();
    }

    static string Plain(string code, string text)
    {
        var s = new StringBuilder(text.Length);
        foreach (char c in text.ToLowerInvariant())
            s.Append(c switch
            {
                'ʻ' or 'ʼ' or '‘' or '’' or '`' or '´' => '\'',
                'ё' when code == "ru" => 'е',
                _ => c,
            });
        return s.ToString();
    }

    static bool IsLetter(string code, char c) => code switch
    {
        "ru" => c is >= 'а' and <= 'я',
        "uz" => c is >= 'a' and <= 'z' and not 'c' and not 'w',
        _ => c is >= 'a' and <= 'z',
    };

    /// <summary>The letters shown on the drawn keyboard, row by row (Uzbek's own letters on the last row).</summary>
    public static string[][] KeyRows(string code) => code switch
    {
        "ru" => new[]
        {
            "й ц у к е н г ш щ з х ъ".Split(' '), "ф ы в а п р о л д ж э".Split(' '), "я ч с м и т ь б ю".Split(' '),
        },
        "uz" => new[]
        {
            "q e r t y u i o p".Split(' '), "a s d f g h j k l".Split(' '), "z x v b n m".Split(' '), "o' g' sh ch ng".Split(' '),
        },
        _ => new[] { "q w e r t y u i o p".Split(' '), "a s d f g h j k l".Split(' '), "z x c v b n m".Split(' ') },
    };
}

/// <summary>
/// A game of Word Guess: a hidden five-letter word and six tries. Each guess is marked letter by letter: green for the
/// right letter in the right place, yellow for a letter the word has elsewhere, gray for one it lacks (or has fewer of:
/// a repeated letter is yellow only as many times as the word still has it).
/// </summary>
public sealed class WordGuessRound
{
    public const int Tries = 6;

    public string[] Answer { get; }
    public List<string[]> Guesses { get; } = new();
    public List<WordMark[]> Marks { get; } = new();

    public WordGuessRound(string[] answer) => Answer = answer;

    public bool Won => Marks.Count > 0 && Marks[^1].All(m => m == WordMark.Green);
    public bool Lost => !Won && Guesses.Count >= Tries;
    public bool Over => Won || Lost;

    /// <param name="words">The list a guess has to be on, or null to take any five letters.</param>
    public WordSubmit Submit(string[] guess, WordList? words)
    {
        if (Over) return WordSubmit.Over;
        if (guess.Length != WordList.Length) return WordSubmit.TooShort;
        if (words != null && !words.Allows(guess)) return WordSubmit.NotAWord;
        Guesses.Add(guess);
        Marks.Add(Mark(Answer, guess));
        return WordSubmit.Accepted;
    }

    /// <summary>Greens first, then yellows from the letters the greens left over, left to right.</summary>
    public static WordMark[] Mark(string[] answer, string[] guess)
    {
        var marks = new WordMark[guess.Length];
        var left = new Dictionary<string, int>();
        for (int i = 0; i < guess.Length; i++)
        {
            if (guess[i] == answer[i]) marks[i] = WordMark.Green;
            else left[answer[i]] = left.GetValueOrDefault(answer[i]) + 1;
        }
        for (int i = 0; i < guess.Length; i++)
        {
            if (marks[i] == WordMark.Green || left.GetValueOrDefault(guess[i]) == 0) continue;
            marks[i] = WordMark.Yellow;
            left[guess[i]]--;
        }
        return marks;
    }

    /// <summary>What the keyboard shows for each letter tried: the best mark it has had.</summary>
    public Dictionary<string, WordMark> Keys()
    {
        var keys = new Dictionary<string, WordMark>();
        for (int g = 0; g < Guesses.Count; g++)
            for (int i = 0; i < Guesses[g].Length; i++)
                if (!keys.TryGetValue(Guesses[g][i], out var m) || Marks[g][i] > m) keys[Guesses[g][i]] = Marks[g][i];
        return keys;
    }

    /// <summary>The grid to paste, one row of squares per guess, with no letters in it.</summary>
    public string Grid() => string.Join("\n", Marks.Select(row => string.Concat(row.Select(m => m switch
    {
        WordMark.Green => "🟩",
        WordMark.Yellow => "🟨",
        _ => "⬛",
    }))));

    /// <summary>"Word Guess #30 · EN · 4/6" (X/6 when it got away), then the grid.</summary>
    public static string ShareLine(int number, string code, WordGuessRound round) =>
        $"{L.T("Word Guess")} #{number} · {code.ToUpperInvariant()} · {(round.Won ? round.Guesses.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) : "X")}/{Tries}\n{round.Grid()}";
}
