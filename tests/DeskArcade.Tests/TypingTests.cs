using System;
using System.Linq;
using DeskArcade.Games;
using Xunit;

namespace DeskArcade.Tests;

public class TypingRunTests
{
    [Fact]
    public void RightKeysMoveTheCursorAndFinishTheText()
    {
        var run = new TypingRun("hi there");
        Assert.Equal(TypeResult.Correct, run.Type('h'));
        Assert.Equal(1, run.Pos);
        run.Type("i there");
        Assert.True(run.Done);
        Assert.Equal(8, run.Typed);
        Assert.Equal(1.0, run.Accuracy);
        Assert.Equal(TypeResult.Ignored, run.Type('x')); // nothing more to type
    }

    [Fact]
    public void AWrongKeyBlocksTheTextUntilItIsTakenBack()
    {
        var run = new TypingRun("abc");
        run.Type('a');
        Assert.Equal(TypeResult.Wrong, run.Type('x'));
        Assert.Equal("x", run.Wrong);
        Assert.Equal(TypeResult.Wrong, run.Type('b')); // right letter, but the mistake comes first
        Assert.Equal(1, run.Pos);
        Assert.True(run.Backspace());
        Assert.True(run.Backspace());
        Assert.False(run.Backspace()); // the part typed right stays
        Assert.Equal(TypeResult.Correct, run.Type('b'));
        Assert.Equal(2, run.Pos);
        Assert.Equal(2, run.Mistakes);
        Assert.Equal(4, run.Keystrokes); // a, x, b (wrong: after the x), b
    }

    [Fact]
    public void CtrlBackspaceClearsEveryMistakeAndTheRunOfMistakesIsCapped()
    {
        var run = new TypingRun("abc");
        for (int i = 0; i < TypingRun.MaxWrong + 5; i++) run.Type('z');
        Assert.Equal(TypingRun.MaxWrong, run.Wrong.Length);
        Assert.Equal(TypingRun.MaxWrong, run.Mistakes); // keys past the cap don't count
        Assert.True(run.ClearWrong());
        Assert.Equal("", run.Wrong);
        Assert.False(run.ClearWrong());
    }

    [Theory]
    [InlineData('’', '\'')]
    [InlineData('ʻ', '\'')]
    [InlineData('‘', '\'')]
    [InlineData('`', '\'')]
    [InlineData('“', '"')]
    [InlineData('«', '"')]
    [InlineData('—', '-')]
    [InlineData('–', '-')]
    [InlineData('е', 'ё')]
    [InlineData(' ', ' ')]
    public void LookAlikeKeysCount(char typed, char wanted) => Assert.True(TypingRun.Matches(typed, wanted));

    [Theory]
    [InlineData('a', 'b')]
    [InlineData('ё', 'е')] // only the other way round
    [InlineData('-', '\'')]
    [InlineData('A', 'a')]
    public void OtherKeysDoNot(char typed, char wanted) => Assert.False(TypingRun.Matches(typed, wanted));

    [Fact]
    public void AfterALineBreakTheIndentationFillsItselfIn()
    {
        var run = new TypingRun("if (x)\n    go();\nend");
        run.Type("if (x)");
        Assert.Equal(TypeResult.Correct, run.Enter());
        Assert.Equal("if (x)\n    ".Length, run.Pos);
        Assert.Equal(TypeResult.Ignored, run.Type(' ')); // indenting by habit is let through
        Assert.Equal("", run.Wrong);
        run.Type("go();");
        run.Enter();
        run.Type("end");
        Assert.True(run.Done);
        Assert.Equal(TypingRun.TypedLength(run.Text), run.Typed);
        Assert.Equal(run.Text.Length - 4, run.Typed);
    }

    [Fact]
    public void ASpaceIsOnlyForgivenRightAfterTheIndentation()
    {
        var run = new TypingRun("a\n  b c");
        run.Type('a');
        run.Enter();
        run.Type('b');
        Assert.Equal(TypeResult.Correct, run.Type(' ')); // the space the text wants
        Assert.Equal(TypeResult.Wrong, run.Type(' '));   // one too many
    }

    [Fact]
    public void TheCurrentWordSpansTheCursor()
    {
        var run = new TypingRun("one two three");
        run.Type("one t");
        Assert.Equal((4, 7), run.CurrentWord());
        run.Type("wo ");
        Assert.Equal((8, 13), run.CurrentWord());
    }

    [Fact]
    public void WordsPerMinuteCountFiveCharactersAWord()
    {
        Assert.Equal(60, TypingRun.Wpm(60, 12), 6); // 12 words in a fifth of a minute
        Assert.Equal(0, TypingRun.Wpm(10, 0.5));    // under a second says nothing yet
        Assert.Equal(48, TypingRun.WholeWpm(100, 25));
    }

    [Theory]
    [InlineData(1.0, 100)]
    [InlineData(0.9999, 99)]
    [InlineData(0.955, 95)]
    [InlineData(0, 0)]
    public void OnlyAFlawlessRaceShowsAHundredPercent(double accuracy, int percent) => Assert.Equal(percent, TypingRun.Percent(accuracy));

    [Fact]
    public void ControlCharactersNeverReachTheGame()
    {
        Assert.Equal("ab", TypingPad.Printable("a\rb\n"));
        Assert.Equal("", TypingPad.Printable("\b\t\u007f"));
        Assert.Equal("ё'", TypingPad.Printable("ё'"));
    }
}

public class CpuTypistTests
{
    const string Text = "The quick brown fox jumps over the lazy dog, then files a bug report about the dog.";

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ItKeepsToTheSpeedOfItsLevel(int level)
    {
        for (int seed = 0; seed < 50; seed++)
        {
            var cpu = new CpuTypist(Text, level, new Random(seed));
            double expected = TypingRun.TypedLength(Text) * 12.0 / cpu.TargetWpm;
            Assert.InRange(cpu.FinishSeconds, expected * 0.999, expected * 1.001);
            Assert.InRange(cpu.TargetWpm, CpuTypist.LevelWpm[level - 1] * 0.9, CpuTypist.LevelWpm[level - 1] * 1.1);
            Assert.InRange(cpu.Wpm, Math.Floor(cpu.TargetWpm) - 1, Math.Ceiling(cpu.TargetWpm) + 1);
        }
    }

    [Fact]
    public void HigherLevelsFinishSooner()
    {
        double[] mean = Enumerable.Range(1, 4)
            .Select(level => Enumerable.Range(0, 40).Average(seed => new CpuTypist(Text, level, new Random(seed)).FinishSeconds)).ToArray();
        for (int i = 1; i < mean.Length; i++) Assert.True(mean[i] < mean[i - 1], $"level {i + 1} should beat level {i}");
    }

    [Fact]
    public void ItMovesForwardOnlyAndEndsAtTheLastCharacter()
    {
        var cpu = new CpuTypist(Text, 2, new Random(3));
        Assert.Equal(0, cpu.PosAt(0));
        int last = 0;
        for (double t = 0; t <= cpu.FinishSeconds + 1; t += 0.05)
        {
            int pos = cpu.PosAt(t);
            Assert.True(pos >= last);
            last = pos;
        }
        Assert.Equal(Text.Length, cpu.PosAt(cpu.FinishSeconds));
        Assert.True(cpu.PosAt(cpu.FinishSeconds - 0.01) < Text.Length);
    }

    [Fact]
    public void IndentationCostsItNoTime()
    {
        const string code = "a\n        b";
        var cpu = new CpuTypist(code, 3, new Random(1));
        int afterBreak = Enumerable.Range(0, 400).Select(i => cpu.PosAt(i * cpu.FinishSeconds / 400)).First(p => p >= 2);
        Assert.Equal(10, afterBreak); // the line break and all eight spaces land at once
    }

    [Fact]
    public void AnEmptyTextIsFinishedAtOnce()
    {
        var cpu = new CpuTypist("", 2, new Random(1));
        Assert.Equal(0, cpu.FinishSeconds);
        Assert.Equal(0, cpu.PosAt(5));
    }
}

public class TypingTextsTests
{
    [Fact]
    public void EveryKindHasPlentyOfTextsAndWords()
    {
        foreach (var kind in TypingTexts.Kinds)
        {
            Assert.True(TypingTexts.Passages(kind).Count >= 12, $"{kind} passages");
            Assert.True(TypingTexts.Words(kind).Count >= 100, $"{kind} words");
            Assert.Equal(TypingTexts.Words(kind).Count, TypingTexts.Words(kind).Distinct().Count());
        }
    }

    [Fact]
    public void EveryTextTypesOnAnOrdinaryKeyboard()
    {
        const string awkward = "—–“”‘’ʻʼ«»…\t ";
        foreach (var kind in TypingTexts.Kinds)
            foreach (var text in TypingTexts.Passages(kind).Concat(TypingTexts.Words(kind)))
            {
                Assert.DoesNotContain(text, c => awkward.Contains(c));
                Assert.False(text.Contains("  ") && kind != TypingKind.Code, $"double space: {text}");
                Assert.Equal(text.Trim(), text);
                Assert.DoesNotContain(" \n", text); // no trailing space at the end of a line
                if (kind != TypingKind.Russian) Assert.All(text, c => Assert.True(c == '\n' || c is >= ' ' and <= '~', $"{kind}: '{c}' in {text}"));
                else Assert.All(text, c => Assert.True(c is >= ' ' and <= '~' || c is >= 'а' and <= 'я' || c is >= 'А' and <= 'Я' || c is 'ё' or 'Ё', $"'{c}' in {text}"));
            }
    }

    [Fact]
    public void OnlyCodeRunsOverSeveralLines()
    {
        foreach (var kind in TypingTexts.Kinds.Where(k => k != TypingKind.Code))
            Assert.All(TypingTexts.Passages(kind), p => Assert.DoesNotContain('\n', p));
        Assert.Contains(TypingTexts.Passages(TypingKind.Code), p => p.Contains('\n'));
    }

    [Theory]
    [InlineData("auto", "en", TypingKind.English)]
    [InlineData("auto", "ru", TypingKind.Russian)]
    [InlineData("auto", "uz", TypingKind.Uzbek)]
    [InlineData("code", "ru", TypingKind.Code)]
    [InlineData("uz", "en", TypingKind.Uzbek)]
    [InlineData(null, "en", TypingKind.English)]
    [InlineData("klingon", "en", TypingKind.English)]
    public void TheSettingPicksTheKind(string? setting, string ui, TypingKind kind) => Assert.Equal(kind, TypingTexts.FromSetting(setting, ui));

    [Fact]
    public void TheChipCyclesThroughEveryKindAndTheSettingRoundTrips()
    {
        var seen = new System.Collections.Generic.HashSet<TypingKind>();
        var kind = TypingKind.English;
        for (int i = 0; i < TypingTexts.Kinds.Length; i++)
        {
            seen.Add(kind);
            Assert.Equal(kind, TypingTexts.FromSetting(TypingTexts.SettingOf(kind), "en"));
            kind = TypingTexts.Next(kind);
        }
        Assert.Equal(TypingKind.English, kind);
        Assert.Equal(TypingTexts.Kinds.Length, seen.Count);
    }
}
