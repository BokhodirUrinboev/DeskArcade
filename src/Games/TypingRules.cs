using System;
using System.Globalization;
using System.Text;

namespace DeskArcade.Games;

public enum TypeResult { Correct, Wrong, Ignored }

/// <summary>
/// One text being typed, as in Typing Race. The typed part only grows: a key that doesn't match goes into a short
/// run of mistakes shown in red after it, and nothing more counts until Backspace has taken those out again (the text
/// never gets ahead of what was typed right). Look-alike keys count as the key the text wants: curly quotes and other
/// apostrophes, a long dash for a hyphen, е for ё. After a line break the next line's indentation fills itself in, and
/// a space typed out of habit there is let through. UI-free, so it can be tested.
/// </summary>
public sealed class TypingRun
{
    public const int MaxWrong = 10;

    readonly StringBuilder _wrong = new();
    bool _afterIndent;

    public TypingRun(string text) => Text = text;

    public string Text { get; }

    /// <summary>Characters of <see cref="Text"/> done, indentation filled in included.</summary>
    public int Pos { get; private set; }

    /// <summary>Characters the player actually typed right (not the indentation filled in for them).</summary>
    public int Typed { get; private set; }

    /// <summary>The wrong keys typed after <see cref="Pos"/>, to be taken back with Backspace.</summary>
    public string Wrong => _wrong.ToString();

    public int Keystrokes { get; private set; }
    public int Mistakes { get; private set; }

    public bool Done => Pos >= Text.Length;
    public double Progress => Text.Length == 0 ? 1 : (double)Pos / Text.Length;

    /// <summary>The share of keys that were right, 0 to 1.</summary>
    public double Accuracy => Keystrokes == 0 ? 1 : (double)(Keystrokes - Mistakes) / Keystrokes;

    public TypeResult Type(char c)
    {
        if (Done) return TypeResult.Ignored;
        if (_wrong.Length == 0 && Matches(c, Text[Pos]))
        {
            Keystrokes++;
            Typed++;
            _afterIndent = false;
            if (Text[Pos++] == '\n') FillIndent();
            return TypeResult.Correct;
        }
        if (c == ' ' && _afterIndent && _wrong.Length == 0) return TypeResult.Ignored; // indenting by hand what is already there
        if (_wrong.Length >= MaxWrong) return TypeResult.Ignored; // enough red: fix those first
        Keystrokes++;
        Mistakes++;
        _wrong.Append(c);
        return TypeResult.Wrong;
    }

    public TypeResult Type(string text)
    {
        var last = TypeResult.Ignored;
        foreach (char c in text)
        {
            var r = Type(c);
            if (r != TypeResult.Ignored) last = r;
        }
        return last;
    }

    public TypeResult Enter() => Type('\n');

    /// <summary>Takes back the last wrong key; the part typed right stays.</summary>
    public bool Backspace()
    {
        if (_wrong.Length == 0) return false;
        _wrong.Length--;
        return true;
    }

    /// <summary>Ctrl+Backspace: takes back every wrong key at once.</summary>
    public bool ClearWrong()
    {
        if (_wrong.Length == 0) return false;
        _wrong.Clear();
        return true;
    }

    void FillIndent()
    {
        while (Pos < Text.Length && Text[Pos] == ' ')
        {
            Pos++;
            _afterIndent = true;
        }
    }

    /// <summary>The word the cursor is in (or about to start): its start and end in <see cref="Text"/>.</summary>
    public (int Start, int End) CurrentWord()
    {
        int start = Math.Min(Pos, Text.Length), end = start;
        while (start > 0 && !char.IsWhiteSpace(Text[start - 1])) start--;
        while (end < Text.Length && !char.IsWhiteSpace(Text[end])) end++;
        return (start, end);
    }

    /// <summary>Whether a typed key counts as the character the text wants.</summary>
    public static bool Matches(char typed, char expected)
    {
        if (typed == expected) return true;
        return expected switch
        {
            '\'' => typed is '’' or '‘' or 'ʻ' or 'ʼ' or '`' or '´',
            '"' => typed is '“' or '”' or '«' or '»' or '„',
            '-' => typed is '–' or '—' or '−',
            ' ' => typed == ' ',
            'ё' => typed == 'е',
            'Ё' => typed == 'Е',
            _ => false,
        };
    }

    /// <summary>How many characters of <paramref name="text"/> a player types: all but the indentation filled in after a line break.</summary>
    public static int TypedLength(string text)
    {
        int count = 0;
        bool indent = false;
        foreach (char c in text)
        {
            if (c == ' ' && indent) continue;
            indent = c == '\n';
            count++;
        }
        return count;
    }

    /// <summary>Words per minute, counting five characters as a word; 0 for less than a second of typing.</summary>
    public static double Wpm(int chars, double seconds) => seconds < 1 ? 0 : chars / 5.0 / (seconds / 60.0);

    /// <summary>A whole number of words per minute, for the scoreboard and the stats.</summary>
    public static int WholeWpm(int chars, double seconds) => (int)Math.Round(Wpm(chars, seconds));

    /// <summary>Accuracy as a percentage, rounded down so that only a race without a mistake shows 100.</summary>
    public static int Percent(double accuracy) => (int)Math.Floor(accuracy * 100 + 1e-9);

    public static string Format(double seconds) => seconds.ToString("0.0", CultureInfo.InvariantCulture);
}

/// <summary>
/// The computer's typist in Typing Race. It types the same text at the words per minute of its level (a little faster
/// or slower from race to race), not like a metronome: longer after a word or a line, now and then a pause to think,
/// and at the lower levels more typos, each costing the keys it takes to see and fix one. Its whole race is worked out
/// when it starts (the time each character is done), so both where it is and when it will finish are known; the
/// average holds its words per minute. UI-free, so it can be tested.
/// </summary>
public sealed class CpuTypist
{
    /// <summary>Words per minute at Easy, Medium, Hard and Expert.</summary>
    public static readonly int[] LevelWpm = { 28, 45, 63, 82 };

    static readonly double[] TypoChance = { 0.05, 0.035, 0.02, 0.01 };

    readonly double[] _doneAt; // when each character of the text is done, from the start
    readonly int _typed;

    public CpuTypist(string text, int level, Random rng)
    {
        Level = Math.Clamp(level, 1, LevelWpm.Length);
        TargetWpm = LevelWpm[Level - 1] * (0.9 + rng.NextDouble() * 0.2);
        _doneAt = new double[text.Length];

        // raw effort per character, then scaled so the typed characters take what the target speed allows
        var effort = new double[text.Length];
        double total = 0;
        bool indent = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == ' ' && indent)
            {
                effort[i] = 0; // indentation the editor fills in
                continue;
            }
            indent = c == '\n';
            double e = 0.65 + rng.NextDouble() * 0.7;
            if (i > 0 && text[i - 1] == ' ') e *= 1.35; // the start of a word
            if (c is '.' or ',' or ';' or ':' or '!' or '?') e *= 1.5;
            if (c == '\n') e *= 2.2;
            if (!char.IsLetterOrDigit(c) && c != ' ' && c != '\n') e *= 1.25; // symbols are further away
            if (rng.NextDouble() < 0.025) e += 3 + rng.NextDouble() * 4; // a moment's thought
            if (rng.NextDouble() < TypoChance[Level - 1]) e += 2.5 + rng.NextDouble() * 2; // a typo, seen and fixed
            effort[i] = e;
            total += e;
            _typed++;
        }
        double seconds = _typed * 12.0 / TargetWpm; // 5 characters a word: 60 / (wpm * 5) seconds a character
        double scale = total > 0 ? seconds / total : 0;
        double t = 0;
        for (int i = 0; i < text.Length; i++) _doneAt[i] = t += effort[i] * scale;
    }

    public int Level { get; }

    /// <summary>The speed it types at over the whole text.</summary>
    public double TargetWpm { get; }

    /// <summary>When it types the last character.</summary>
    public double FinishSeconds => _doneAt.Length == 0 ? 0 : _doneAt[^1];

    /// <summary>Its words per minute over the text, as the scoreboard reports it.</summary>
    public int Wpm => TypingRun.WholeWpm(_typed, FinishSeconds);

    /// <summary>How many characters of the text it has done after <paramref name="seconds"/>.</summary>
    public int PosAt(double seconds)
    {
        int lo = 0, hi = _doneAt.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_doneAt[mid] <= seconds) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}
