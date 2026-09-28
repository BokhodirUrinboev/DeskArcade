using System;
using System.Linq;
using DeskArcade.Games;
using Xunit;

namespace DeskArcade.Tests;

public class WordRainTests
{
    static WordRainRules With(params (string Text, double Y)[] words)
    {
        var rules = new WordRainRules();
        foreach (var (text, y) in words) rules.Spawn(text, 100, y);
        return rules;
    }

    [Fact]
    public void TheFirstKeyPicksTheLowestWordItStarts()
    {
        var rules = With(("cat", 100), ("cow", 400), ("dog", 500));
        var (hit, word) = rules.Type('c');
        Assert.Equal(RainHit.Progress, hit);
        Assert.Equal("cow", word!.Text); // lower on screen than "cat": more urgent
        Assert.Same(word, rules.Target);
    }

    [Fact]
    public void KeysStayWithTheTargetUntilItIsZapped()
    {
        var rules = With(("cow", 400), ("owl", 300));
        rules.Type('c');
        Assert.Equal(RainHit.Progress, rules.Type('o').Hit); // not the "owl"
        var (hit, word) = rules.Type('w');
        Assert.Equal(RainHit.Zapped, hit);
        Assert.Equal("cow", word!.Text);
        Assert.Null(rules.Target);
        Assert.Single(rules.Words);
        Assert.Equal(1, rules.Zapped);
        Assert.Equal(WordRainRules.Points(3, 1, 1), rules.Score);
    }

    [Fact]
    public void AMissBreaksTheComboButNotTheWord()
    {
        var rules = With(("ab", 400), ("cd", 300), ("ef", 200));
        rules.Type("ab".ToCharArray());
        Assert.Equal(1, rules.Combo);
        rules.Type('c');
        Assert.Equal(RainHit.Miss, rules.Type('x').Hit);
        Assert.Equal(0, rules.Combo);
        Assert.Equal(1, rules.Target!.Typed); // the typed "c" stays
        Assert.Equal(RainHit.Zapped, rules.Type('d').Hit);
        Assert.Equal(RainHit.Miss, rules.Type('q').Hit); // no word starts with q
        Assert.Equal(2, rules.Misses);
    }

    [Fact]
    public void BackspaceLetsGoOfTheWord()
    {
        var rules = With(("cat", 300), ("car", 200));
        rules.Type('c');
        rules.Type('a');
        Assert.True(rules.Release());
        Assert.Null(rules.Target);
        Assert.All(rules.Words, w => Assert.Equal(0, w.Typed));
        Assert.False(rules.Release());
    }

    [Fact]
    public void ThreeWordsOnTheFloorEndTheGame()
    {
        var rules = With(("a", 1), ("b", 2), ("c", 3), ("d", 4));
        rules.Type('a'); // one zapped for a combo
        foreach (var w in rules.Words.Take(3).ToList()) rules.Landed(w);
        Assert.Equal(0, rules.Lives);
        Assert.True(rules.Over);
        Assert.Equal(0, rules.Combo);
        Assert.Equal(RainHit.Ignored, rules.Type('d').Hit);
    }

    [Fact]
    public void TheTargetCanLandToo()
    {
        var rules = With(("cat", 300));
        rules.Type('c');
        rules.Landed(rules.Words[0]);
        Assert.Null(rules.Target);
        Assert.Equal(WordRainRules.StartLives - 1, rules.Lives);
        rules.Landed(new RainWord { Id = 99, Text = "x" }); // not falling: nothing happens
        Assert.Equal(WordRainRules.StartLives - 1, rules.Lives);
    }

    [Fact]
    public void EveryTenWordsIsALevelAndLevelsGetHarder()
    {
        var rules = new WordRainRules();
        double spawn = rules.SpawnEvery, speed = rules.FallSpeed;
        int max = rules.MaxWords, longest = rules.Lengths.Max;
        for (int i = 0; i < WordRainRules.WordsPerLevel; i++)
        {
            rules.Spawn("w", 0, 10);
            rules.Type('w');
        }
        Assert.Equal(2, rules.Level);
        Assert.True(rules.SpawnEvery < spawn);
        Assert.True(rules.FallSpeed > speed);
        Assert.True(rules.MaxWords > max);
        Assert.True(rules.Lengths.Max > longest);
        Assert.Equal(WordRainRules.WordsPerLevel, rules.BestCombo);
    }

    [Fact]
    public void PointsGrowWithTheWordTheComboAndTheLevel()
    {
        Assert.Equal(55, WordRainRules.Points(5, 1, 1));
        Assert.True(WordRainRules.Points(8, 1, 1) > WordRainRules.Points(5, 1, 1));
        Assert.True(WordRainRules.Points(5, 6, 1) > WordRainRules.Points(5, 1, 1));
        Assert.Equal(2 * WordRainRules.Points(5, 1, 1), WordRainRules.Points(5, 1, 2));
        Assert.Equal(WordRainRules.Points(5, 20, 1), WordRainRules.Points(5, 50, 1)); // the combo bonus tops out
    }

    [Fact]
    public void PickedWordsFitTheLevelAndStartWithANewLetter()
    {
        var pool = TypingTexts.Words(TypingKind.English);
        var rng = new Random(4);
        var rules = new WordRainRules();
        for (int i = 0; i < 5; i++)
        {
            string word = rules.Pick(pool, rng);
            var (min, max) = rules.Lengths;
            Assert.InRange(word.Length, min, max);
            Assert.DoesNotContain(rules.Words, w => char.ToLowerInvariant(w.Text[0]) == char.ToLowerInvariant(word[0]));
            rules.Spawn(word, 0, 0);
        }
    }

    [Fact]
    public void PickingNeverFailsEvenFromATinyPool()
    {
        var rules = new WordRainRules();
        rules.Spawn("aa", 0, 0);
        Assert.Equal("aa", rules.Pick(new[] { "aa" }, new Random(1))); // nothing else to give
        Assert.Equal("ab", rules.Pick(new[] { "aa", "ab" }, new Random(1)));
    }

    [Theory]
    [InlineData('S', 's')]
    [InlineData('s', 'S')]
    [InlineData('’', '\'')]
    [InlineData('Е', 'ё')]
    public void KeysMatchWhateverTheCase(char typed, char wanted) => Assert.True(WordRainRules.Same(typed, wanted));

    [Fact]
    public void EveryWordStartsWithSomethingTypeable()
    {
        foreach (var kind in TypingTexts.Kinds)
            Assert.All(TypingTexts.Words(kind), w => Assert.False(char.IsWhiteSpace(w[0])));
    }
}

static class RainExtensions
{
    public static void Type(this WordRainRules rules, char[] keys)
    {
        foreach (char c in keys) rules.Type(c);
    }
}
