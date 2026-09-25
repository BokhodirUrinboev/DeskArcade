using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using DeskArcade.Games;
using Xunit;

namespace DeskArcade.Tests;

/// <summary>Gomoku's rules (five or more in a row, in every direction) and its computer player at every level.</summary>
public class GomokuTests
{
    static int Sq(int r, int c) => GomokuRules.Sq(r, c);

    /// <summary>Plays black on <paramref name="black"/> and white on <paramref name="white"/>, alternating (white's extra moves go far away).</summary>
    static GomokuRules Play(IEnumerable<int> black, IEnumerable<int> white)
    {
        var g = new GomokuRules();
        var b = new Queue<int>(black);
        var w = new Queue<int>(white);
        while (b.Count > 0 || w.Count > 0)
        {
            if (g.Turn == 1) g.Apply(new[] { b.Count > 0 ? b.Dequeue() : throw new InvalidOperationException("black ran out") });
            else g.Apply(new[] { w.Count > 0 ? w.Dequeue() : throw new InvalidOperationException("white ran out") });
        }
        return g;
    }

    /// <summary>White stones scattered along the top edge, well apart, out of every line in play.</summary>
    static IEnumerable<int> Far(int n) => Enumerable.Range(0, n).Select(i => Sq(0, i * 3));

    public static IEnumerable<object[]> Directions() => new[]
    {
        new object[] { 0, 1 },  // across
        new object[] { 1, 0 },  // down
        new object[] { 1, 1 },  // down to the right
        new object[] { 1, -1 }, // down to the left
    };

    [Theory]
    [MemberData(nameof(Directions))]
    public void FiveInARowWinsInEveryDirection(int dr, int dc)
    {
        var line = Enumerable.Range(0, 5).Select(k => Sq(5 + dr * k, 7 + dc * k)).ToArray();
        var g = Play(line.Take(4), Far(4));
        Assert.Equal(0, g.Result);
        Assert.Null(g.WinLine);
        g.Apply(new[] { line[4] });
        Assert.Equal(1, g.Result);
        Assert.Equal(line.OrderBy(s => s), g.WinLine!.OrderBy(s => s));
        Assert.Empty(g.LegalMoves());
    }

    [Theory]
    [MemberData(nameof(Directions))]
    public void FourInARowIsNotAWin(int dr, int dc)
    {
        var line = Enumerable.Range(0, 4).Select(k => Sq(5 + dr * k, 7 + dc * k));
        var g = Play(line, Far(4));
        Assert.Equal(0, g.Result);
        Assert.Equal(0, g.Winner);
        Assert.NotEmpty(g.LegalMoves());
    }

    [Fact]
    public void FiveWithAGapIsNotAWinUntilTheGapIsFilled()
    {
        var g = Play(new[] { Sq(7, 3), Sq(7, 4), Sq(7, 6), Sq(7, 7), Sq(7, 8) }, Far(5));
        Assert.Equal(0, g.Result);
        g.Apply(new[] { Sq(7, 5) });
        Assert.Equal(1, g.Result);
        Assert.Equal(6, g.WinLine!.Length); // freestyle: six in a row wins, and the whole run lights up
    }

    [Fact]
    public void WhiteWinsToo()
    {
        var g = Play(new[] { Sq(0, 0), Sq(0, 3), Sq(0, 6), Sq(0, 9), Sq(0, 12) }, Enumerable.Range(0, 5).Select(k => Sq(10, 2 + k)));
        Assert.Equal(-1, g.Result);
    }

    [Fact]
    public void AStoneCannotGoOnAnotherAndTheStartIsEmpty()
    {
        var g = new GomokuRules();
        Assert.Equal(225, g.LegalMoves().Count);
        g.Apply(new[] { Sq(7, 7) });
        Assert.DoesNotContain(g.LegalMoves(), m => m[0] == Sq(7, 7));
        Assert.Equal(224, g.LegalMoves().Count);
    }

    [Fact]
    public void EncodeAndDecodeRoundTripAndFindTheWinner()
    {
        var g = Play(new[] { Sq(3, 3), Sq(4, 4), Sq(5, 5), Sq(6, 6) }, Far(4));
        var back = GomokuRules.Decode(g.Encode())!;
        Assert.Equal(g.Board, back.Board);
        Assert.Equal(g.Turn, back.Turn);
        Assert.Equal(g.Ply, back.Ply);
        g.Apply(new[] { Sq(7, 7) });
        var won = GomokuRules.Decode(g.Encode())!;
        Assert.Equal(1, won.Result);
        Assert.Equal(5, won.WinLine!.Length);
        Assert.Null(GomokuRules.Decode("x|x|1"));
    }

    // ------------------------------------------------------------------ computer player

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public void EveryLevelPlaysLegalMovesToTheEnd(int depth)
    {
        var rng = new Random(depth + 3);
        var g = new GomokuRules();
        int moves = 0;
        while (g.Result == 0 && moves++ < 225)
        {
            var legal = g.LegalMoves();
            var move = g.Turn == 1 ? g.BestMove(rng, depth) : g.BestMove(rng, 0);
            Assert.Contains(legal, m => m.SequenceEqual(move));
            g.Apply(move);
        }
        Assert.NotEqual(0, g.Result);
    }

    [Fact]
    public void TheFirstStoneGoesInTheMiddle()
    {
        foreach (int depth in new[] { 0, 1, 3 }) Assert.Equal(Sq(7, 7), new GomokuRules().BestMove(new Random(1), depth)[0]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public void TakesTheWinRatherThanBlocking(int depth)
    {
        // black (to move) has four with one end open; white has an open four of its own
        var g = Play(new[] { Sq(7, 3), Sq(7, 4), Sq(7, 5), Sq(7, 6), Sq(0, 14) },
            new[] { Sq(7, 2), Sq(10, 4), Sq(10, 5), Sq(10, 6), Sq(10, 7) });
        Assert.Equal(1, g.Turn);
        for (int seed = 0; seed < 5; seed++) Assert.Equal(Sq(7, 7), g.BestMove(new Random(seed), depth)[0]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public void BlocksAFourItCannotBeat(int depth)
    {
        // white has four down a column, blocked above; black must take the one point below
        var g = Play(new[] { Sq(2, 10), Sq(8, 2), Sq(12, 12), Sq(5, 12) },
            new[] { Sq(3, 10), Sq(4, 10), Sq(5, 10), Sq(6, 10) });
        Assert.Equal(1, g.Turn);
        for (int seed = 0; seed < 5; seed++) Assert.Equal(Sq(7, 10), g.BestMove(new Random(seed), depth)[0]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void BlocksAnOpenThree(int depth)
    {
        // white's open three across the middle: black must stop one end (or the gap) before it becomes an open four
        var g = Play(new[] { Sq(2, 2), Sq(12, 12), Sq(2, 12) }, new[] { Sq(7, 6), Sq(7, 7), Sq(7, 8) });
        Assert.Equal(1, g.Turn);
        int move = g.BestMove(new Random(2), depth)[0];
        Assert.Contains(move, new[] { Sq(7, 4), Sq(7, 5), Sq(7, 9), Sq(7, 10) });
    }

    [Fact]
    public void HardThinksWithinTheTimeLimit()
    {
        var rng = new Random(9);
        var g = new GomokuRules();
        for (int i = 0; i < 16 && g.Result == 0; i++) g.Apply(g.BestMove(rng, 1));
        Assert.Equal(0, g.Result);
        g.BestMove(rng, 4); // warm up the JIT
        var times = new List<long>();
        foreach (int depth in new[] { 3, 4, 4 })
        {
            var clock = Stopwatch.StartNew();
            g.BestMove(rng, depth);
            times.Add(clock.ElapsedMilliseconds);
        }
        times.Sort();
        Assert.True(times[1] < 150, $"Gomoku CPU took {string.Join(", ", times)} ms");
    }
}
