using System;
using System.Collections.Generic;
using System.Linq;

namespace DeskArcade.Games;

/// <summary>
/// Blockfall, a falling-blocks game, free of UI. A well 10 cells wide and 20 high; the seven four-cell pieces
/// come in shuffled bags of seven, so no piece is ever long in coming. A piece moves sideways, turns (nudged
/// off a wall or a stack when it would overlap), falls a row per gravity step, and locks when it can't fall.
/// Full rows clear: 1–4 at once score 100, 300, 500 or 800, times the level; every 10 rows is a level, and
/// gravity quickens with it. A soft drop scores 1 a row and a hard drop 2. The game is over when a new
/// piece has no room. Rows count from the top (0) down; columns from the left.
/// </summary>
public sealed class BlockfallRules
{
    public const int Width = 10, Height = 20, Kinds = 7;
    public static readonly string Names = "IOTSZJL";

    /// <summary>Each piece's cells in its four turns, as (column, row) inside a 4×4 box.</summary>
    static readonly (int C, int R)[][][] Shapes = Build();

    static (int, int)[][][] Build()
    {
        var bases = new[]
        {
            new[] { (0, 1), (1, 1), (2, 1), (3, 1) }, // I
            new[] { (1, 0), (2, 0), (1, 1), (2, 1) }, // O
            new[] { (1, 0), (0, 1), (1, 1), (2, 1) }, // T
            new[] { (1, 0), (2, 0), (0, 1), (1, 1) }, // S
            new[] { (0, 0), (1, 0), (1, 1), (2, 1) }, // Z
            new[] { (0, 0), (0, 1), (1, 1), (2, 1) }, // J
            new[] { (2, 0), (0, 1), (1, 1), (2, 1) }, // L
        };
        var all = new (int, int)[Kinds][][];
        for (int k = 0; k < Kinds; k++)
        {
            all[k] = new (int, int)[4][];
            all[k][0] = bases[k];
            int size = k == 0 ? 4 : k == 1 ? 4 : 3; // the I turns in a 4-box, the O not at all, the rest in a 3-box
            for (int t = 1; t < 4; t++)
                all[k][t] = k == 1 ? bases[k] : all[k][t - 1].Select(p => (size - 1 - p.Item2, p.Item1)).ToArray();
        }
        return all;
    }

    readonly Random _rng;
    readonly Queue<int> _bag = new();

    /// <summary>The settled cells: −1 empty, otherwise the kind of piece that left it.</summary>
    public int[,] Cells { get; } = new int[Height, Width];
    public int Kind { get; private set; }
    public int Turn { get; private set; }
    public int Col { get; private set; }
    public int Row { get; private set; }
    public int Next => _bag.Peek();
    public int Score { get; private set; }
    public int Lines { get; private set; }
    public int Level => Lines / 10 + 1;
    public bool Over { get; private set; }
    /// <summary>Goes up whenever the settled cells change, so a view knows to redraw them.</summary>
    public int Version { get; private set; }

    /// <summary>Seconds per gravity step at the current level.</summary>
    public double Gravity => Math.Max(0.06, 0.8 * Math.Pow(0.85, Level - 1));

    public BlockfallRules(Random rng)
    {
        _rng = rng;
        for (int r = 0; r < Height; r++)
            for (int c = 0; c < Width; c++)
                Cells[r, c] = -1;
        Refill();
        Spawn();
    }

    void Refill()
    {
        while (_bag.Count < Kinds + 1)
        {
            var bag = Enumerable.Range(0, Kinds).ToArray();
            for (int i = bag.Length - 1; i > 0; i--)
            {
                int j = _rng.Next(i + 1);
                (bag[i], bag[j]) = (bag[j], bag[i]);
            }
            foreach (int k in bag) _bag.Enqueue(k);
        }
    }

    /// <summary>The cells the falling piece covers, in the well.</summary>
    public IEnumerable<(int C, int R)> PieceCells() => CellsOf(Kind, Turn, Col, Row);

    public static IEnumerable<(int C, int R)> CellsOf(int kind, int turn, int col, int row) =>
        Shapes[kind][turn & 3].Select(p => (col + p.C, row + p.R));

    /// <summary>Where a hard drop would put the piece: its row.</summary>
    public int DropRow()
    {
        int r = Row;
        while (Fits(Kind, Turn, Col, r + 1)) r++;
        return r;
    }

    bool Fits(int kind, int turn, int col, int row) =>
        CellsOf(kind, turn, col, row).All(p => p.C >= 0 && p.C < Width && p.R < Height && (p.R < 0 || Cells[p.R, p.C] < 0));

    /// <summary>For tests: puts <paramref name="kind"/> up next, in place of the falling piece.</summary>
    public void Force(int kind)
    {
        Kind = kind;
        Turn = 0;
        Col = Width / 2 - 2;
        Row = 0;
    }

    public bool Move(int dx)
    {
        if (Over || !Fits(Kind, Turn, Col + dx, Row)) return false;
        Col += dx;
        return true;
    }

    /// <summary>Turns clockwise, nudging up to two cells sideways (or one up) when the plain turn would overlap.</summary>
    public bool Rotate()
    {
        if (Over) return false;
        int t = (Turn + 1) & 3;
        foreach (var (dx, dy) in new[] { (0, 0), (-1, 0), (1, 0), (-2, 0), (2, 0), (0, -1) })
            if (Fits(Kind, t, Col + dx, Row + dy))
            {
                Turn = t;
                Col += dx;
                Row += dy;
                return true;
            }
        return false;
    }

    /// <summary>One gravity step (or soft-drop step): falls a row, or locks. Returns the rows cleared by a lock (0 if none).</summary>
    public int Fall(bool soft = false)
    {
        if (Over) return 0;
        if (Fits(Kind, Turn, Col, Row + 1))
        {
            Row++;
            if (soft) Score++;
            return 0;
        }
        return Lock();
    }

    /// <summary>Drops the piece to the bottom and locks it. Returns the rows cleared.</summary>
    public int HardDrop()
    {
        if (Over) return 0;
        int to = DropRow();
        Score += 2 * (to - Row);
        Row = to;
        return Lock();
    }

    int Lock()
    {
        foreach (var (c, r) in PieceCells())
        {
            if (r < 0)
            {
                Over = true; // locked above the top of the well
                continue;
            }
            Cells[r, c] = Kind;
        }
        int cleared = 0;
        for (int r = Height - 1; r >= 0; r--)
        {
            bool full = true;
            for (int c = 0; c < Width && full; c++) full = Cells[r, c] >= 0;
            if (!full) continue;
            cleared++;
            for (int y = r; y > 0; y--)
                for (int c = 0; c < Width; c++)
                    Cells[y, c] = Cells[y - 1, c];
            for (int c = 0; c < Width; c++) Cells[0, c] = -1;
            r++; // look at the row that moved down into this one
        }
        if (cleared > 0)
        {
            Score += new[] { 0, 100, 300, 500, 800 }[cleared] * Level;
            Lines += cleared;
        }
        Version++;
        if (!Over) Spawn();
        return cleared;
    }

    void Spawn()
    {
        Kind = _bag.Dequeue();
        Refill();
        Turn = 0;
        Col = Width / 2 - 2;
        Row = Kind == 0 ? -1 : 0; // the I's cells sit on the second row of its box
        if (!Fits(Kind, Turn, Col, Row)) Over = true;
    }
}
