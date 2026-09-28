using System;
using System.Collections.Generic;
using System.Linq;

namespace DeskArcade.Games;

/// <summary>A word falling in Word Rain: its text, how much of it is typed, and where it is.</summary>
public sealed class RainWord
{
    public required int Id { get; init; }
    public required string Text { get; init; }
    public int Typed { get; set; }
    public double X { get; set; }
    public double Y { get; set; } // the bottom of the word
    public double Speed { get; set; }
    /// <summary>Seconds left resting on a window top; 0 while falling.</summary>
    public double Resting { get; set; }
    public IntPtr RestingOn { get; set; }
    /// <summary>The window it rested on and dripped off, so it doesn't land there again at once.</summary>
    public IntPtr LeftFrom { get; set; }
}

public enum RainHit { Progress, Zapped, Miss, Ignored }

/// <summary>
/// Word Rain's rules: which word a key goes to, the score, the levels and the lives. A key goes to the word being typed;
/// with none, to the lowest word it starts (the most urgent), so the words on screen are drawn with different first
/// letters where the pool allows. A wrong key is a miss: it breaks the combo but costs nothing else, and Backspace lets
/// go of the word. A typed word scores ten a letter plus the combo, times the level; every ten words the level goes up,
/// and the words come faster, fall faster and grow longer. A word that reaches the floor costs one of the three lives.
/// UI-free (the game moves the words), so it can be tested.
/// </summary>
public sealed class WordRainRules
{
    public const int StartLives = 3, WordsPerLevel = 10;

    readonly List<RainWord> _words = new();
    int _nextId;

    public IReadOnlyList<RainWord> Words => _words;
    public RainWord? Target { get; private set; }
    public int Level { get; private set; } = 1;
    public int Score { get; private set; }
    public int Lives { get; private set; } = StartLives;
    public int Zapped { get; private set; }
    public int Combo { get; private set; }
    public int BestCombo { get; private set; }
    public int Misses { get; private set; }
    public bool Over => Lives <= 0;

    /// <summary>Seconds between new words at this level.</summary>
    public double SpawnEvery => Math.Max(0.75, 2.6 - 0.2 * (Level - 1));

    /// <summary>How fast words fall at this level, in pixels a second at the overlay's normal size.</summary>
    public double FallSpeed => 34 + 8 * (Level - 1);

    /// <summary>The most words falling at once at this level.</summary>
    public int MaxWords => Math.Min(9, 3 + Level);

    /// <summary>The word lengths this level draws from: short words first, longer ones later.</summary>
    public (int Min, int Max) Lengths => (Level <= 2 ? 2 : 3, Math.Min(14, 5 + Level));

    /// <summary>
    /// Picks the next word from <paramref name="pool"/>: a length this level allows, not already falling, and, when it
    /// can, starting with a letter no falling word starts with (so the first key names one word).
    /// </summary>
    public string Pick(IReadOnlyList<string> pool, Random rng)
    {
        var (min, max) = Lengths;
        var falling = _words.Select(w => w.Text).ToHashSet();
        var firsts = _words.Select(w => First(w.Text)).ToHashSet();
        var fits = pool.Where(p => p.Length >= min && p.Length <= max && !falling.Contains(p)).ToList();
        if (fits.Count == 0) fits = pool.Where(p => !falling.Contains(p)).ToList();
        if (fits.Count == 0) fits = pool.ToList();
        var fresh = fits.Where(p => !firsts.Contains(First(p))).ToList();
        var from = fresh.Count > 0 ? fresh : fits;
        return from[rng.Next(from.Count)];
    }

    public RainWord Spawn(string text, double x, double y, double speedScale = 1)
    {
        var w = new RainWord { Id = ++_nextId, Text = text, X = x, Y = y, Speed = FallSpeed * speedScale };
        _words.Add(w);
        return w;
    }

    /// <summary>One typed key: progress on a word, a word finished (zapped and gone), a miss, or nothing (game over).</summary>
    public (RainHit Hit, RainWord? Word) Type(char c)
    {
        if (Over) return (RainHit.Ignored, null);
        var word = Target ?? _words.Where(w => Same(c, w.Text[0])).OrderByDescending(w => w.Y).FirstOrDefault();
        if (word == null || !Same(c, word.Text[word.Typed]))
        {
            Misses++;
            Combo = 0;
            return (RainHit.Miss, Target);
        }
        word.Typed++;
        Target = word;
        if (word.Typed < word.Text.Length) return (RainHit.Progress, word);
        Target = null;
        _words.Remove(word);
        Combo++;
        BestCombo = Math.Max(BestCombo, Combo);
        Zapped++;
        Score += Points(word.Text.Length, Combo, Level);
        if (Zapped % WordsPerLevel == 0) Level++;
        return (RainHit.Zapped, word);
    }

    /// <summary>Backspace: lets go of the word being typed, which keeps falling from where it is.</summary>
    public bool Release()
    {
        if (Target == null) return false;
        Target.Typed = 0;
        Target = null;
        return true;
    }

    /// <summary>A word reached the floor: it is gone, with a life and the combo.</summary>
    public void Landed(RainWord word)
    {
        if (!_words.Remove(word)) return;
        if (Target == word) Target = null;
        Combo = 0;
        Lives = Math.Max(0, Lives - 1);
    }

    /// <summary>What a word of <paramref name="letters"/> scores as the <paramref name="combo"/>-th in a row at <paramref name="level"/>.</summary>
    public static int Points(int letters, int combo, int level) => (letters * 10 + Math.Min(combo, 20) * 5) * level;

    /// <summary>Keys match whatever the case, and the look-alikes Typing Race allows count here too.</summary>
    public static bool Same(char typed, char wanted) =>
        TypingRun.Matches(typed, wanted) || char.ToLowerInvariant(typed) == char.ToLowerInvariant(wanted) ||
        TypingRun.Matches(char.ToLowerInvariant(typed), char.ToLowerInvariant(wanted));

    static char First(string text) => char.ToLowerInvariant(text[0]);
}
