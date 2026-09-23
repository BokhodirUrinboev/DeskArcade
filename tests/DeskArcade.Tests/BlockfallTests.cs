using System;
using System.Collections.Generic;
using System.Linq;
using DeskArcade.Games;
using Xunit;
using static DeskArcade.Games.BlockfallRules;

namespace DeskArcade.Tests;

public class BlockfallTests
{
    const int I = 0, O = 1, T = 2;

    static void FillRow(BlockfallRules g, int row, params int[] gaps)
    {
        for (int c = 0; c < Width; c++) g.Cells[row, c] = gaps.Contains(c) ? -1 : 3;
    }

    [Fact]
    public void EveryBagHasAllSevenPieces()
    {
        var g = new BlockfallRules(new Random(1));
        var seen = new List<int>();
        for (int i = 0; i < 14; i++)
        {
            seen.Add(g.Kind);
            for (int r = 0; r < Height; r++) FillRow(g, r, Enumerable.Range(0, Width).ToArray()); // an empty well each time
            g.HardDrop();
        }
        Assert.Equal(Enumerable.Range(0, Kinds), seen.Take(7).OrderBy(k => k));
        Assert.Equal(Enumerable.Range(0, Kinds), seen.Skip(7).Take(7).OrderBy(k => k));
    }

    [Fact]
    public void EachShapeTurnsBackToItselfAfterFourTurns()
    {
        for (int k = 0; k < Kinds; k++)
        {
            var start = CellsOf(k, 0, 0, 0).OrderBy(p => p).ToList();
            Assert.Equal(start, CellsOf(k, 4, 0, 0).OrderBy(p => p).ToList());
            Assert.Equal(4, CellsOf(k, 1, 0, 0).Distinct().Count());
        }
    }

    [Fact]
    public void PiecesStopAtTheWalls()
    {
        var g = new BlockfallRules(new Random(2));
        g.Force(O);
        int moves = 0;
        while (g.Move(-1)) moves++;
        Assert.Equal(0, g.PieceCells().Min(p => p.C));
        while (g.Move(1)) moves++;
        Assert.Equal(Width - 1, g.PieceCells().Max(p => p.C));
    }

    [Fact]
    public void ATurnAgainstTheWallIsNudgedInside()
    {
        var g = new BlockfallRules(new Random(3));
        g.Force(I);
        g.Rotate(); // upright
        while (g.Move(1)) { }
        Assert.True(g.Rotate()); // flat again: it would stick out, so it steps left
        Assert.All(g.PieceCells(), p => Assert.InRange(p.C, 0, Width - 1));
    }

    [Fact]
    public void AHardDropLandsOnTheStackAndScoresTwoARow()
    {
        var g = new BlockfallRules(new Random(4));
        FillRow(g, Height - 1, 0); // a row with a gap, so it stays
        g.Force(O);
        int before = g.Score;
        int row = g.DropRow();
        g.HardDrop();
        Assert.Equal(2 * row, g.Score - before);
        Assert.Equal(O, g.Cells[Height - 2, Width / 2 - 1]);
    }

    [Fact]
    public void FullRowsClearAndScoreByTheLevel()
    {
        var g = new BlockfallRules(new Random(5));
        for (int r = Height - 4; r < Height; r++) FillRow(g, r, 0); // four rows missing only column 0
        g.Force(I);
        g.Rotate(); // upright: columns 2
        while (g.Move(-1)) { }
        int before = g.Score, drop = g.DropRow() - g.Row;
        Assert.Equal(4, g.HardDrop());
        Assert.Equal(4, g.Lines);
        Assert.Equal(800 + 2 * drop, g.Score - before);
        for (int r = 0; r < Height; r++)
            for (int c = 0; c < Width; c++)
                Assert.Equal(-1, g.Cells[r, c]); // everything cleared
    }

    [Fact]
    public void RowsAboveAClearMoveDown()
    {
        var g = new BlockfallRules(new Random(6));
        FillRow(g, Height - 1, 4, 5);
        g.Cells[Height - 2, 9] = 6;
        g.Force(O); // columns 4 and 5
        g.HardDrop();
        Assert.Equal(1, g.Lines);
        Assert.Equal(6, g.Cells[Height - 1, 9]);
        Assert.Equal(O, g.Cells[Height - 1, 4]); // the O's top half came down a row
    }

    [Fact]
    public void TheLevelRisesEveryTenLinesAndGravityQuickens()
    {
        var g = new BlockfallRules(new Random(7));
        double slow = g.Gravity;
        for (int i = 0; i < 5; i++)
        {
            FillRow(g, Height - 1, 4, 5);
            FillRow(g, Height - 2, 4, 5);
            g.Force(O);
            g.HardDrop();
        }
        Assert.Equal(10, g.Lines);
        Assert.Equal(2, g.Level);
        Assert.True(g.Gravity < slow);
    }

    [Fact]
    public void TheGameEndsWhenANewPieceHasNoRoom()
    {
        var g = new BlockfallRules(new Random(8));
        for (int r = 0; r < Height; r++) FillRow(g, r, r % Width); // nearly full, never a full row
        g.Force(T);
        g.HardDrop();
        Assert.True(g.Over);
        Assert.False(g.Move(1));
        Assert.Equal(0, g.Fall());
    }

    [Fact]
    public void RandomPlayNeverLosesACell()
    {
        var rng = new Random(9);
        var g = new BlockfallRules(new Random(9));
        for (int step = 0; step < 5000 && !g.Over; step++)
        {
            switch (rng.Next(6))
            {
                case 0: g.Rotate(); break;
                case 1: g.Move(-1); break;
                case 2: g.Move(1); break;
                case 3: g.HardDrop(); break;
                default: g.Fall(); break;
            }
            Assert.All(g.PieceCells(), p => Assert.InRange(p.C, 0, Width - 1));
            Assert.All(g.PieceCells().Where(p => p.R >= 0), p => Assert.True(g.Over || g.Cells[p.R, p.C] < 0));
        }
    }
}
