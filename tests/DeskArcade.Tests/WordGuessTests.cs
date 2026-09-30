using System;
using System.Linq;
using DeskArcade.Games;
using Xunit;
using static DeskArcade.Games.WordMark;

namespace DeskArcade.Tests;

public class WordGuessMarkTests
{
    static WordMark[] Mark(string answer, string guess) => WordGuessRound.Mark(WordList.Tiles("en", answer), WordList.Tiles("en", guess));

    [Fact]
    public void RightPlaceIsGreenElsewhereYellowAbsentGray() =>
        Assert.Equal(new[] { Green, Yellow, Gray, Yellow, Yellow }, Mark("crane", "cater"));

    [Fact]
    public void ARepeatedLetterIsYellowOnlyAsOftenAsTheWordHasIt()
    {
        // one e in the answer: the first e of the guess takes it, the second is gray
        Assert.Equal(new[] { Gray, Yellow, Gray, Gray, Gray }, Mark("abbey", "keeps"));
        // a green takes its letter first, so another copy of it elsewhere is gray
        Assert.Equal(new[] { Gray, Yellow, Gray, Green, Gray }, Mark("crane", "nanny"));
        Assert.Equal(new[] { Gray, Green, Green, Gray, Gray }, Mark("abbey", "bbbxx"));
    }

    [Fact]
    public void TwoOfALetterInTheWordLetTwoMarksThrough() =>
        Assert.Equal(new[] { Yellow, Yellow, Green, Green, Gray }, Mark("abbey", "babes"));

    [Fact]
    public void AllGreenWins_SixMissesLose()
    {
        var words = WordList.Of("en", new[] { "crane" }, new[] { "slate", "pious", "dough", "lumpy", "witch", "bread" });
        var r = new WordGuessRound(WordList.Tiles("en", "crane"));
        Assert.Equal(WordSubmit.TooShort, r.Submit(WordList.Tiles("en", "cra"), words));
        Assert.Equal(WordSubmit.NotAWord, r.Submit(WordList.Tiles("en", "zzzzz"), words));
        Assert.Empty(r.Guesses);
        foreach (string w in new[] { "slate", "pious", "dough", "lumpy", "witch" }) Assert.Equal(WordSubmit.Accepted, r.Submit(WordList.Tiles("en", w), words));
        Assert.False(r.Over);
        Assert.Equal(WordSubmit.Accepted, r.Submit(WordList.Tiles("en", "crane"), words));
        Assert.True(r.Won);
        Assert.Equal(WordSubmit.Over, r.Submit(WordList.Tiles("en", "bread"), words));

        var lost = new WordGuessRound(WordList.Tiles("en", "crane"));
        for (int i = 0; i < WordGuessRound.Tries; i++) lost.Submit(WordList.Tiles("en", "bread"), words);
        Assert.True(lost.Lost);
    }

    [Fact]
    public void TheKeyboardShowsEachLettersBestMark()
    {
        var r = new WordGuessRound(WordList.Tiles("en", "crane"));
        r.Submit(WordList.Tiles("en", "react"), null);
        r.Submit(WordList.Tiles("en", "trace"), null);
        var keys = r.Keys();
        Assert.Equal(Green, keys["r"]);   // yellow first, then green
        Assert.Equal(Green, keys["a"]);
        Assert.Equal(Gray, keys["t"]);
        Assert.Equal(Yellow, keys["c"]);
    }

    [Fact]
    public void TheSharedGridHasNoLetters()
    {
        var r = new WordGuessRound(WordList.Tiles("en", "crane"));
        r.Submit(WordList.Tiles("en", "cater"), null);
        r.Submit(WordList.Tiles("en", "crane"), null);
        string line = WordGuessRound.ShareLine(30, "en", r);
        Assert.Equal("Word Guess #30 · EN · 2/6\n🟩🟨⬛🟨🟨\n🟩🟩🟩🟩🟩", line);
    }
}

public class WordGuessTileTests
{
    [Theory]
    [InlineData("sharcha", "sh a r ch a")]
    [InlineData("yorug'", "y o r u g'")]
    [InlineData("YORUGʻ", "y o r u g'")]      // an Uzbek apostrophe, and capitals
    [InlineData("to‘ng", "t o' ng")]
    [InlineData("tong'a", "t o n g' a")]      // ng before an apostrophe is n and g'
    [InlineData("chelak", "ch e l a k")]
    [InlineData("ab1c-w", "a b")]             // c and w aren't Uzbek letters, digits and dashes aren't letters
    public void UzbekReadsItsDigraphsAsOneLetter(string text, string tiles) => Assert.Equal(tiles.Split(' '), WordList.Tiles("uz", text));

    [Fact]
    public void RussianCountsYoAsYe()
    {
        Assert.Equal(WordList.Tiles("ru", "полет"), WordList.Tiles("ru", "ПОЛЁТ"));
        Assert.Empty(WordList.Tiles("ru", "hello")); // an English layout types nothing into a Russian word
    }

    [Fact]
    public void EnglishTakesOnlyItsLetters() => Assert.Equal(new[] { "c", "a", "t" }, WordList.Tiles("en", "C a-t!"));
}

public class WordGuessListTests
{
    [Theory]
    [InlineData("en", 700)]
    [InlineData("ru", 500)]
    [InlineData("uz", 300)]
    public void EveryWordIsFiveLettersAndEveryAnswerIsAllowed(string code, int answers)
    {
        var list = WordList.For(code);
        Assert.True(list.Answers.Count >= answers, $"{list.Answers.Count} answers");
        Assert.Equal(list.Answers.Count, list.Answers.Distinct().Count());
        foreach (string a in list.Answers)
        {
            var tiles = WordList.Tiles(code, a);
            Assert.True(tiles.Length == WordList.Length, $"{code}: {a}");
            Assert.True(list.Allows(tiles), $"{code}: {a}");
        }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    [InlineData("uz")]
    public void EveryDailyWordForTwoYearsIsInItsList(string code)
    {
        var list = WordList.For(code);
        var answers = list.Answers.ToHashSet();
        for (int d = 0; d < 730; d++)
        {
            var day = WordList.FirstDay.AddDays(d);
            Assert.Contains(string.Concat(list.Daily(day)), answers);
        }
    }

    [Fact]
    public void TheDailyWordIsTheSameAllDayAndMovesOnTheNext()
    {
        var list = WordList.For("en");
        var day = new DateOnly(2026, 10, 5);
        Assert.Equal(list.Daily(day), list.Daily(day));
        Assert.NotEqual(list.Daily(day), list.Daily(day.AddDays(1)));
        Assert.Equal(1, WordList.DailyNumber(WordList.FirstDay));
        Assert.Equal(35, WordList.DailyNumber(day));
    }

    [Fact]
    public void EnglishAndRussianTurnBackWordsOffTheList_UzbekTakesAnyFiveLetters()
    {
        Assert.False(WordList.For("en").Allows(WordList.Tiles("en", "qzxvb")));
        Assert.False(WordList.For("ru").Allows(WordList.Tiles("ru", "ъъъъъ")));
        Assert.True(WordList.For("uz").Allows(WordList.Tiles("uz", "qqqqq")));
    }

    [Fact]
    public void EveryLetterOfEveryAnswerIsOnTheDrawnKeyboard()
    {
        foreach (string code in WordList.Codes)
        {
            var keys = WordList.KeyRows(code).SelectMany(r => r).ToHashSet();
            foreach (string a in WordList.For(code).Answers)
                Assert.All(WordList.Tiles(code, a), t => Assert.Contains(t, keys));
        }
    }
}
