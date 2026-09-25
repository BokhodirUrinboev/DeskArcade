using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using DeskArcade.Games;
using Xunit;

namespace DeskArcade.Tests;

/// <summary>Reversi's rules (outflanking, flips, passes, the end), its animation plan and its computer player at every level.</summary>
public class ReversiTests
{
    static int Sq(int r, int c) => ReversiRules.Sq(r, c);

    /// <summary>A position from eight rows of 'x' (black), 'o' (white) and '.', with <paramref name="turn"/> to move.</summary>
    static ReversiRules Board(string rows, int turn = 1)
    {
        var cells = string.Concat(rows.Where(ch => ch is 'x' or 'o' or '.'));
        Assert.Equal(64, cells.Length);
        return ReversiRules.Decode($"{cells}|{(turn > 0 ? "x" : "o")}|10|-")!;
    }

    [Fact]
    public void TheStartHasFourDiscsAndBlackHasFourMoves()
    {
        var r = ReversiRules.New();
        Assert.Equal(2, r.Count(1));
        Assert.Equal(2, r.Count(-1));
        Assert.Equal(1, r.Turn);
        Assert.Equal(new[] { Sq(2, 4), Sq(3, 5), Sq(4, 2), Sq(5, 3) }, r.LegalMoves().Select(m => m[0]).OrderBy(s => s));
    }

    [Fact]
    public void AMoveFlipsTheDiscsItOutflanksInAllEightDirections()
    {
        // white all round the middle square, each line closed by black two squares out
        var r = Board("""
            ........
            .x.x.x..
            ..ooo...
            .xo.ox..
            ..ooo...
            .x.x.x..
            ........
            ........
            """);
        var move = new[] { Sq(3, 3) };
        Assert.Contains(r.LegalMoves(), m => m.SequenceEqual(move));
        var flipped = r.Apply(move);
        Assert.Equal(8, flipped.Count);
        foreach (var (dr, dc) in new[] { (-1, -1), (-1, 0), (-1, 1), (0, -1), (0, 1), (1, -1), (1, 0), (1, 1) })
        {
            Assert.Contains(Sq(3 + dr, 3 + dc), flipped);
            Assert.Equal(1, r.Board[Sq(3 + dr, 3 + dc)]);
        }
        Assert.Equal(0, r.Count(-1));
    }

    [Fact]
    public void OnlyOutflankedLinesFlip()
    {
        // a line of two closed by black flips; an open line and one that ends in an empty square do not
        var r = Board("""
            ........
            ........
            ........
            xoo.oo..
            ...o....
            ........
            ........
            ........
            """);
        var flipped = r.Apply(new[] { Sq(3, 3) });
        Assert.Equal(new[] { Sq(3, 1), Sq(3, 2) }, flipped.OrderBy(s => s));
        Assert.Equal(-1, r.Board[Sq(3, 4)]);
        Assert.Equal(-1, r.Board[Sq(4, 3)]);
    }

    [Fact]
    public void ASquareThatOutflanksNothingIsNoMove()
    {
        var r = ReversiRules.New();
        Assert.DoesNotContain(r.LegalMoves(), m => m[0] == Sq(2, 2)); // diagonal to the middle, but nothing closes the line
        Assert.DoesNotContain(r.LegalMoves(), m => m[0] == Sq(3, 3)); // taken
        Assert.Empty(ReversiRules.Flips(r.Board, Sq(0, 0), 1));
    }

    [Fact]
    public void ASideWithNoMovePassesAndTheBoardSaysWho()
    {
        // black takes the bottom disc of the column; white's top disc then has no black line to close, so white passes
        var r = Board("""
            ........
            ........
            ........
            o.......
            x.......
            x.......
            o.......
            ........
            """);
        Assert.Equal(0, r.Passed);
        r.Apply(new[] { Sq(7, 0) });
        Assert.Equal(0, r.Result);
        Assert.Equal(-1, r.Passed);
        Assert.Equal(1, r.Turn);
        var back = ReversiRules.Decode(r.Encode())!;
        Assert.Equal(-1, back.Passed);
        Assert.Equal(1, back.Turn);
        Assert.Equal(new[] { Sq(2, 0) }, Assert.Single(r.LegalMoves()));
        r.Apply(new[] { Sq(2, 0) });
        Assert.Equal(0, r.Passed);
    }

    [Fact]
    public void TheGameEndsWhenNeitherSideCanMoveAndTheMostDiscsWin()
    {
        var r = Board("""
            xo......
            ........
            ........
            ........
            ........
            ........
            ........
            ........
            """);
        r.Apply(new[] { Sq(0, 2) });
        Assert.Equal(1, r.Result);
        Assert.Empty(r.LegalMoves());
        Assert.Equal(3, r.Count(1));

        // one square left that white can't use: white passes, black fills it and the board is full
        var full = Board(string.Concat(Enumerable.Repeat("xxxxoooo", 7)) + "xxxxooo.", -1);
        Assert.Equal(0, full.Result);
        Assert.Equal(1, full.Turn);
        Assert.Equal(-1, full.Passed);
        Assert.Equal(6, full.Apply(new[] { Sq(7, 7) }).Count); // three along the row, three up the diagonal
        Assert.Equal(1, full.Result);
        Assert.Equal(39, full.Count(1));
    }

    [Fact]
    public void AnEvenBoardIsADraw()
    {
        var r = Board(string.Concat(Enumerable.Repeat("xxxxoooo", 8)));
        Assert.Equal(2, r.Result);
        Assert.Empty(r.LegalMoves());
    }

    [Fact]
    public void EncodeAndDecodeRoundTrip()
    {
        var r = ReversiRules.New();
        var rng = new Random(3);
        for (int i = 0; i < 12 && r.Result == 0; i++) r.Apply(r.BestMove(rng, 0));
        var back = ReversiRules.Decode(r.Encode())!;
        Assert.Equal(r.Board, back.Board);
        Assert.Equal(r.Turn, back.Turn);
        Assert.Equal(r.Ply, back.Ply);
        Assert.Equal(r.Encode(), back.Encode());
        Assert.Null(ReversiRules.Decode("nonsense"));
        Assert.Null(ReversiRules.Decode(new string('.', 63) + "|x|0|-"));
    }

    [Fact]
    public void APlacedDiscAppearsAndTheOutflankedOnesTurnOverNearestFirst()
    {
        var r = Board("""
            ........
            ........
            ........
            xooo....
            ........
            ........
            ........
            ........
            """);
        var before = (sbyte[])r.Board.Clone();
        var move = new[] { Sq(3, 4) };
        r.Apply(move);
        var plan = BoardAnim.Plan(before, r.Board, move, 8)!;
        Assert.NotNull(plan);
        Assert.Equal((Sq(3, 4), (sbyte)1), plan.Appear!.Value);
        Assert.Equal(new[] { Sq(3, 3), Sq(3, 2), Sq(3, 1) }, plan.Flips.Select(f => f.Square));
        Assert.All(plan.Flips, f => Assert.Equal(1, f.Piece));
        Assert.Empty(plan.Slides);
        Assert.Empty(plan.Vanish);
    }

    [Fact]
    public void APlacementThatChangesAnythingElseIsNoPlan()
    {
        var before = new sbyte[64];
        before[10] = -1;
        var after = (sbyte[])before.Clone();
        after[20] = 1;
        after[10] = 0; // a disc vanished
        Assert.Null(BoardAnim.Plan(before, after, new[] { 20 }, 8));
        before[11] = 1;
        after[10] = 1;
        after[11] = -1; // a disc turned over to the side that did not move
        Assert.Null(BoardAnim.Plan(before, after, new[] { 20 }, 8));
        after[11] = 1;
        Assert.Equal(new[] { 10 }, BoardAnim.Plan(before, after, new[] { 20 }, 8)!.Flips.Select(f => f.Square));
    }

    // ------------------------------------------------------------------ computer player

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(6)]
    public void EveryLevelPlaysOnlyLegalMovesToTheEnd(int depth)
    {
        var rng = new Random(depth + 1);
        var r = ReversiRules.New();
        int moves = 0;
        while (r.Result == 0 && moves++ < 80)
        {
            var legal = r.LegalMoves();
            var move = r.Turn == 1 ? r.BestMove(rng, depth) : legal[rng.Next(legal.Count)];
            Assert.Contains(legal, m => m.SequenceEqual(move));
            r.Apply(move);
        }
        Assert.NotEqual(0, r.Result);
    }

    [Fact]
    public void HardTakesTheWipeOut()
    {
        // g5 flips both white discs and ends the game; the other two moves flip just one
        var r = Board("""
            ........
            ........
            ........
            ...xoo..
            .....x..
            ........
            ........
            ........
            """);
        Assert.Equal(3, r.LegalMoves().Count);
        foreach (int depth in new[] { 4, 6 })
            for (int seed = 0; seed < 5; seed++)
                Assert.Equal(Sq(3, 6), r.BestMove(new Random(seed), depth)[0]);
        r.Apply(new[] { Sq(3, 6) });
        Assert.Equal(1, r.Result);
    }

    /// <summary>The best outcome (+1 win, 0 draw, −1 loss) the side to move can force, by searching every line.</summary>
    static int Solve(sbyte[] board, int side, bool passed = false)
    {
        var moves = ReversiRules.Moves(board, side);
        if (moves.Count == 0)
        {
            if (passed)
            {
                int diff = board.Sum(p => p * side);
                return Math.Sign(diff);
            }
            return -Solve(board, -side, true);
        }
        int best = -2;
        foreach (int m in moves)
        {
            var b = (sbyte[])board.Clone();
            foreach (int f in ReversiRules.Flips(board, m, side)) b[f] = (sbyte)side;
            b[m] = (sbyte)side;
            best = Math.Max(best, -Solve(b, -side));
        }
        return best;
    }

    static int Outcome(ReversiRules r, int move)
    {
        var b = (sbyte[])r.Board.Clone();
        foreach (int f in ReversiRules.Flips(r.Board, move, r.Turn)) b[f] = (sbyte)r.Turn;
        b[move] = (sbyte)r.Turn;
        return -Solve(b, -r.Turn);
    }

    [Fact]
    public void HardFindsTheWinAndDodgesTheLossNearTheEnd()
    {
        // random games played down to four empty squares: Hard sees to the end, so it never picks a worse outcome than the best
        var rng = new Random(11);
        int checkedPositions = 0, choices = 0;
        for (int game = 0; game < 60; game++)
        {
            var r = ReversiRules.New();
            while (r.Result == 0 && r.Board.Count(p => p == 0) > 4)
            {
                var legal = r.LegalMoves();
                r.Apply(legal[rng.Next(legal.Count)]);
            }
            if (r.Result != 0) continue;
            var outcomes = r.LegalMoves().Select(m => Outcome(r, m[0])).ToList();
            int best = outcomes.Max();
            if (outcomes.Distinct().Count() > 1) choices++;
            Assert.Equal(best, Outcome(r, r.BestMove(new Random(game), 4)[0]));
            checkedPositions++;
        }
        Assert.True(checkedPositions > 20);
        Assert.True(choices > 3); // some positions had a losing move on offer to avoid
    }

    [Fact]
    public void MediumTakesACornerOverAnInnerSquare()
    {
        // the corner and a square in the middle both flip one disc; the corner is worth far more
        var r = Board("""
            ........
            .o......
            ..x.....
            ........
            ...o....
            ....x...
            ........
            ........
            """);
        Assert.Equal(Sq(0, 0), r.BestMove(new Random(1), 1)[0]);
    }

    [Fact]
    public void HardThinksWithinTheTimeLimit()
    {
        var rng = new Random(5);
        var r = ReversiRules.New();
        for (int i = 0; i < 20 && r.Result == 0; i++) r.Apply(r.BestMove(rng, 0));
        Assert.Equal(0, r.Result);
        r.BestMove(rng, 6); // warm up the JIT
        var times = new List<long>();
        foreach (int depth in new[] { 4, 6, 6 })
        {
            var clock = Stopwatch.StartNew();
            r.BestMove(rng, depth);
            times.Add(clock.ElapsedMilliseconds);
        }
        times.Sort();
        Assert.True(times[1] < 150, $"Reversi CPU took {string.Join(", ", times)} ms");
    }
}
