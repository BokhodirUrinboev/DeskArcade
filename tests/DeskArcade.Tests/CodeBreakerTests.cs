using System;
using System.Linq;
using DeskArcade.Games;
using Xunit;

namespace DeskArcade.Tests;

public class CodeBreakerRulesTests
{
    /// <summary>"AABB" → [0, 0, 1, 1]: letters are colours.</summary>
    static int[] C(string s) => s.Select(ch => ch - 'A').ToArray();

    [Theory]
    [InlineData("AABB", "ABAB", 2, 2)]
    [InlineData("AAAA", "ABCD", 1, 0)]
    [InlineData("ABCD", "DCBA", 0, 4)]
    [InlineData("ABCD", "ABCD", 4, 0)]
    [InlineData("ABCD", "EEFF", 0, 0)]
    [InlineData("AABC", "AAAA", 2, 0)] // the extra As in the guess score nothing
    [InlineData("ABBC", "BBAA", 1, 2)]
    [InlineData("AABB", "BBBA", 1, 2)]
    [InlineData("ABCA", "AAAB", 1, 2)]
    [InlineData("FEDC", "CFEF", 0, 3)]
    public void ScoresBlacksAndWhitesWithRepeats(string code, string guess, int black, int white)
    {
        Assert.Equal((black, white), CodeBreakerRules.Score(C(code), C(guess)));
    }

    [Fact]
    public void ScoringIsSymmetricAndNeverExceedsFourPins()
    {
        var rng = new Random(3);
        for (int i = 0; i < 2000; i++)
        {
            var a = CodeBreakerRules.RandomCode(rng);
            var b = CodeBreakerRules.RandomCode(rng);
            var (black, white) = CodeBreakerRules.Score(a, b);
            Assert.Equal((black, white), CodeBreakerRules.Score(b, a));
            Assert.InRange(black + white, 0, 4);
        }
    }

    [Fact]
    public void RightGuessWins()
    {
        var game = new CodeBreakerRules(C("CAFE"));
        Assert.Equal((0, 2), game.Submit(C("ABCD")));
        Assert.False(game.Over);
        Assert.Equal((4, 0), game.Submit(C("CAFE")));
        Assert.True(game.Won);
        Assert.False(game.Lost);
        Assert.Equal(2, game.Guesses.Count);
        Assert.Throws<InvalidOperationException>(() => game.Submit(C("CAFE")));
    }

    [Fact]
    public void TenWrongGuessesLose()
    {
        var game = new CodeBreakerRules(C("FFFF"));
        for (int i = 0; i < CodeBreakerRules.MaxGuesses; i++)
        {
            Assert.False(game.Over);
            game.Submit(C("ABCD"));
        }
        Assert.True(game.Lost);
        Assert.False(game.Won);
        Assert.Throws<InvalidOperationException>(() => game.Submit(C("FFFF")));
    }

    [Fact]
    public void WinningOnTheLastGuessIsAWin()
    {
        var game = new CodeBreakerRules(C("BEAD"));
        for (int i = 0; i < CodeBreakerRules.MaxGuesses - 1; i++) game.Submit(C("FFFF"));
        game.Submit(C("BEAD"));
        Assert.True(game.Won);
        Assert.False(game.Lost);
    }

    [Fact]
    public void RejectsBadGuesses()
    {
        var game = new CodeBreakerRules(C("AAAA"));
        Assert.Throws<ArgumentException>(() => game.Submit(new[] { 0, 1, 2 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => game.Submit(new[] { 0, 1, 2, 6 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => game.Submit(new[] { 0, -1, 2, 3 }));
        Assert.Empty(game.Guesses);
    }

    [Fact]
    public void EncodeAndDecodeCoverEveryCode()
    {
        for (int n = 0; n < CodeBreakerRules.Codes; n++)
            Assert.Equal(n, CodeBreakerRules.Encode(CodeBreakerRules.Decode(n)));
    }
}

public class CodeBreakerSolverTests
{
    static int Solve(int[] code)
    {
        var game = new CodeBreakerRules(code);
        var solver = new CodeBreakerSolver();
        while (!game.Over)
        {
            var guess = solver.NextGuess();
            var (black, white) = game.Submit(guess);
            solver.Record(guess, black, white);
        }
        Assert.True(game.Won, string.Join("", code));
        return game.Guesses.Count;
    }

    [Fact]
    public void WinsEverySeededCodeWithinTenAndAveragesUnderFiveAndAHalf()
    {
        var rng = new Random(2026);
        var counts = Enumerable.Range(0, 300).Select(_ => Solve(CodeBreakerRules.RandomCode(rng))).ToList();
        Assert.All(counts, n => Assert.InRange(n, 1, 5)); // Knuth's bound, well inside the ten allowed
        Assert.True(counts.Average() < 5.5, $"average {counts.Average():0.00}");
    }

    [Theory]
    [InlineData(new[] { 0, 0, 0, 0 })]
    [InlineData(new[] { 5, 5, 5, 5 })]
    [InlineData(new[] { 0, 0, 1, 1 })] // the opening itself
    [InlineData(new[] { 5, 4, 3, 2 })]
    [InlineData(new[] { 1, 0, 1, 0 })]
    public void SolvesHandPickedCodes(int[] code) => Assert.InRange(Solve(code), 1, 5);

    [Fact]
    public void EveryAnswerNarrowsTheCandidates()
    {
        var game = new CodeBreakerRules(new[] { 2, 3, 3, 4 });
        var solver = new CodeBreakerSolver();
        int before = solver.Candidates;
        var guess = solver.NextGuess();
        var (black, white) = game.Submit(guess);
        solver.Record(guess, black, white);
        Assert.True(solver.Candidates < before);
        Assert.True(solver.Candidates > 0);
    }
}
