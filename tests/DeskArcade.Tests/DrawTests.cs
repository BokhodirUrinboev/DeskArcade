using System;
using System.Collections.Generic;
using System.Linq;
using DeskArcade.Games;
using Xunit;
using static DeskArcade.Games.DrawRules;

namespace DeskArcade.Tests;

public class DrawStrokeTests
{
    [Fact]
    public void AStrokeSurvivesTheWire()
    {
        var s = DrawStroke.FromPoints(new List<(double, double)> { (0, 0), (100, 50), (460, 460) }, 460, 460, 2, 3);
        var back = DrawStroke.Decode(s.Encode())!;
        Assert.Equal(2, back.Color);
        Assert.Equal(3, back.Size);
        Assert.Equal(s.Points, back.Points);
        Assert.Equal(255, back.Points[^1]); // the board's corner is 255, 255
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("a.b.AAAA")]
    [InlineData("1.1.not base64!")]
    [InlineData("1.1.AAE")] // an odd number of bytes
    public void ABrokenStrokeIsRefused(string text) => Assert.Null(DrawStroke.Decode(text));

    [Fact]
    public void AStraightLineKeepsOnlyItsEnds_ACornerKeepsTheCorner()
    {
        var line = Enumerable.Range(0, 50).Select(i => (i * 2.0, i * 1.0)).ToList();
        Assert.Equal(2, DrawStroke.Simplify(line, 1).Count);
        var corner = Enumerable.Range(0, 20).Select(i => (i * 5.0, 0.0)).Concat(Enumerable.Range(1, 20).Select(i => (95.0, i * 5.0))).ToList();
        var kept = DrawStroke.Simplify(corner, 1);
        Assert.Equal(3, kept.Count);
        Assert.Contains((95.0, 0.0), kept);
    }

    [Fact]
    public void AVeryLongStrokeIsCutToTheLimit()
    {
        var zigzag = Enumerable.Range(0, 2000).Select(i => (i * 0.2, i % 2 == 0 ? 0.0 : 100.0)).ToList();
        Assert.Equal(MaxPointsPerStroke, DrawStroke.FromPoints(zigzag, 460, 460, 0, 1).Count);
    }
}

public class DrawRulesTests
{
    static readonly List<DrawWord> Words = Enumerable.Range(0, 20).Select(i => new DrawWord($"word{i}", $"слово{i}", $"so'z{i}")).Concat(new[]
    {
        new DrawWord("dog|puppy", "собака|пёс", "it|kuchuk"), new DrawWord("ice cream", "мороженое", "muzqaymoq"),
    }).ToList();

    static DrawRules New(int players = 3, int rounds = 1, Func<int, bool>? cpu = null) => new(players, Words, new Random(5), rounds, cpu ?? (_ => false), _ => true);

    /// <summary>A rules object whose first turn draws the dog (seat 0 picks it).</summary>
    static DrawRules Dog(int players = 3)
    {
        for (int seed = 0; seed < 500; seed++)
        {
            var r = new DrawRules(players, Words, new Random(seed), 1, _ => false, _ => true);
            int k = Array.FindIndex(r.Choices, i => Words[i].En.StartsWith("dog"));
            if (k < 0) continue;
            Assert.True(r.Choose(0, k));
            return r;
        }
        throw new InvalidOperationException("no seed deals the dog");
    }

    [Fact]
    public void TheDrawerPicksOneOfThreeWordsAndTheClockStarts()
    {
        var r = New();
        Assert.Equal(Stage.Choosing, r.Phase);
        Assert.Equal(Options, r.Choices.Distinct().Count());
        Assert.False(r.Choose(1, 0)); // not the drawer
        Assert.True(r.Choose(0, 2));
        Assert.Equal(Stage.Drawing, r.Phase);
        Assert.Equal(DrawSeconds, r.TimeLeft);
        Assert.Equal(r.Choices[2], r.Word);
    }

    [Fact]
    public void ADrawerWhoDoesntChooseGetsTheFirstWord()
    {
        var r = New();
        Assert.Contains("chosen", r.Step(ChooseSeconds + 0.1));
        Assert.Equal(r.Choices[0], r.Word);
    }

    [Fact]
    public void AnswersCountInAnyLanguageAndAnyForm()
    {
        foreach (string guess in new[] { "Dog", "  puppy ", "СОБАКА", "пес", "kuchuk", "it" })
        {
            var r = Dog();
            Assert.Equal(GuessKind.Right, r.Guess(1, guess));
        }
    }

    [Fact]
    public void OneLetterOffIsCloseAndOnlyItsGuesserHearsSo()
    {
        var r = Dog();
        Assert.Equal(GuessKind.Close, r.Guess(1, "puppi"));
        Assert.Equal(GuessKind.Wrong, r.Guess(1, "cat"));
        Assert.Equal(GuessKind.Wrong, r.Guess(2, "do")); // too short to be close: two letters could be anything
        var mine = DrawView.Of(r, Words, 1);
        var theirs = DrawView.Of(r, Words, 2);
        Assert.Equal(GuessKind.Close, mine.Feed[0].Kind);
        Assert.Equal(GuessKind.Wrong, theirs.Feed[0].Kind);
    }

    [Fact]
    public void ARightGuessScoresMoreTheSoonerAndTheDrawerScoresForIt()
    {
        var r = Dog();
        r.Step(DrawSeconds / 2);
        Assert.Equal(GuessKind.Right, r.Guess(1, "dog"));
        int first = GuessBase + (int)Math.Round(GuessSpeed * 0.5) + FirstBonus;
        Assert.Equal(first, r.Scores[1]);
        Assert.Equal(DrawerPoints, r.Scores[0]);
        Assert.True(r.Guessed[1]);
        Assert.Null(r.Guess(1, "dog")); // once is enough
        Assert.Equal("", r.Feed[^1].Text); // the answer isn't shown to the others
        r.Step(DrawSeconds / 4);
        Assert.Equal(GuessKind.Right, r.Guess(2, "dog"));
        Assert.True(r.Scores[2] < first); // later, and not first
        Assert.Equal(Stage.Reveal, r.Phase); // everyone has it: the turn ends
        Assert.Equal(2 * DrawerPoints, r.Scores[0]);
    }

    [Fact]
    public void TheDrawerCannotGuessAndOthersCannotDraw()
    {
        var r = Dog();
        Assert.Null(r.Guess(0, "dog"));
        var s = DrawStroke.FromPoints(new List<(double, double)> { (0, 0), (10, 10) }, 100, 100, 0, 1);
        Assert.False(r.AddStroke(1, s));
        Assert.True(r.AddStroke(0, s));
        Assert.False(r.Undo(1));
        Assert.True(r.Undo(0));
        Assert.Empty(r.Strokes);
    }

    [Fact]
    public void ADrawingHasAPointBudget()
    {
        var r = Dog();
        var big = new DrawStroke(0, 1, new byte[MaxPointsPerStroke * 2]);
        int fits = MaxPointsPerDrawing / MaxPointsPerStroke;
        for (int i = 0; i < fits; i++) Assert.True(r.AddStroke(0, big));
        Assert.False(r.AddStroke(0, big));
    }

    [Fact]
    public void HintsShowLettersAsTheTimeRunsDown()
    {
        var r = Dog(); // "dog" is too short for hints
        r.Step(DrawSeconds * 0.8);
        Assert.Equal(0, r.Hints);
        Assert.Equal("_ _ _   _ _ _ _ _", HintOf("ice cream", 0)); // the space between the words stays
        Assert.Equal(8, HintOf("ice cream", 0).Count(c => c == '_'));
        Assert.Equal(6, HintOf("ice cream", 2).Count(c => c == '_'));
        Assert.Equal(HintOf("ice cream", 1), HintOf("ice cream", 1)); // the same letters every time
        Assert.Equal(1, HintOf("abc", 5).Count(char.IsLetter)); // never all but the last two
    }

    [Fact]
    public void TurnsGoRoundAndTheGameEndsAfterEveryoneHasDrawnItsRounds()
    {
        var r = New(players: 2, rounds: 2);
        var drawers = new List<int>();
        for (int guard = 0; guard < 20 && !r.Over; guard++)
        {
            drawers.Add(r.Drawer);
            r.Step(ChooseSeconds + 0.1);   // the word, chosen for the drawer
            r.Step(DrawSeconds + 0.1);     // nobody gets it
            r.Step(RevealSeconds + 0.1);   // the next turn
        }
        Assert.True(r.Over);
        Assert.Equal(new[] { 0, 1, 0, 1 }, drawers);
    }

    [Fact]
    public void AComputerDrawerOnlyGetsWordsItCanDraw()
    {
        var drawable = new HashSet<int> { 3, 7, 11, 15 };
        var r = new DrawRules(3, Words, new Random(1), 1, s => s == 0, drawable.Contains);
        Assert.All(r.Choices, c => Assert.Contains(c, drawable));
    }

    [Theory]
    [InlineData("  Ice-Cream ", "ice cream")]
    [InlineData("Ёжик", "ежик")]
    [InlineData("so‘z", "so'z")]
    [InlineData("so`z", "so'z")]
    [InlineData("dog!", "dog")]
    public void GuessesAreComparedPlainly(string text, string expected) => Assert.Equal(expected, Normalize(text));

    [Fact]
    public void TheEditDistanceCountsOneChangeAsOne()
    {
        Assert.Equal(0, Levenshtein("kitten", "kitten"));
        Assert.Equal(1, Levenshtein("kitten", "kittens"));
        Assert.Equal(1, Levenshtein("kitten", "sitten"));
        Assert.Equal(3, Levenshtein("kitten", "sitting"));
    }
}

public class DrawContentTests
{
    [Fact]
    public void TheWordListLoadsInThreeLanguages()
    {
        var words = DrawGame.Words;
        Assert.True(words.Count >= 300, $"{words.Count} words");
        Assert.All(words, w => Assert.True(w.En.Length > 0 && w.Ru.Length > 0 && w.Uz.Length > 0));
        Assert.True(words.Count(w => w.Tag == "code") >= 30); // programming words among them
        Assert.Equal(words.Count, words.Select(w => w.In("en")).Distinct().Count());
    }

    [Fact]
    public void EveryPictureIsForAWordOnTheListAndFitsTheBoard()
    {
        var english = DrawGame.Words.Select(w => w.In("en")).ToHashSet();
        Assert.True(DrawPictures.All.Count >= 40, $"{DrawPictures.All.Count} pictures");
        foreach (var (word, strokes) in DrawPictures.All)
        {
            Assert.Contains(word, english);
            Assert.NotEmpty(strokes);
            Assert.True(strokes.Sum(s => s.Count) <= MaxPointsPerDrawing, word);
            Assert.All(strokes, s => Assert.True(s.Count <= MaxPointsPerStroke && s.Color < DrawGame.Palette.Length, word));
            Assert.All(strokes, s => Assert.NotNull(DrawStroke.Decode(s.Encode())));
        }
    }
}
