using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace DeskArcade.Games;

public enum SudokuLevel { Easy, Medium, Hard }

/// <summary>
/// A Sudoku: a 9×9 grid of digits in which every row, column and 3×3 box holds 1 to 9 once. The generator fills a whole
/// grid at random, then takes digits away one by one, keeping each removal only while the puzzle still has exactly one
/// solution, until the level's number of clues is left (Easy 40, Medium 32, Hard 27, or as close as uniqueness allows).
/// The player writes digits into the empty cells, or pencil notes (up to nine small digits a cell); a digit that repeats
/// in its row, column or box is a conflict; the puzzle is solved when every cell holds its digit. UI-free and seeded, so
/// it can be tested, and the same seed gives the same puzzle on every screen.
/// </summary>
public sealed class SudokuRules
{
    const int All = 0x3FE; // bits 1..9

    readonly int[] _solution = new int[81];
    readonly int[] _given = new int[81];
    readonly int[] _entry = new int[81];
    readonly int[] _notes = new int[81];

    public SudokuRules(Random rng, SudokuLevel level)
    {
        Level = level;
        Fill(_solution, 0, rng);
        Array.Copy(_solution, _given, 81);
        int target = Clues(level);
        foreach (int i in Enumerable.Range(0, 81).OrderBy(_ => rng.Next()))
        {
            if (CountGiven() <= target) break;
            int keep = _given[i];
            _given[i] = 0;
            if (CountSolutions((int[])_given.Clone(), 2) != 1) _given[i] = keep; // that one was needed
        }
    }

    /// <summary>A puzzle from given digits (0 for empty), for tests; its solution is worked out.</summary>
    public SudokuRules(int[] given)
    {
        Level = SudokuLevel.Medium;
        Array.Copy(given, _given, 81);
        var grid = (int[])given.Clone();
        if (!Solve(grid)) throw new ArgumentException("no solution");
        Array.Copy(grid, _solution, 81);
    }

    public SudokuLevel Level { get; }

    public static int Clues(SudokuLevel level) => level switch { SudokuLevel.Easy => 40, SudokuLevel.Medium => 32, _ => 27 };

    public static int Row(int i) => i / 9;
    public static int Col(int i) => i % 9;
    public static int Box(int i) => i / 27 * 3 + i % 9 / 3;

    public bool IsGiven(int i) => _given[i] != 0;
    public int ValueAt(int i) => _given[i] != 0 ? _given[i] : _entry[i];
    public int SolutionAt(int i) => _solution[i];
    public int NotesAt(int i) => _notes[i];
    public int CountGiven() => _given.Count(v => v != 0);
    public int Filled => Enumerable.Range(0, 81).Count(i => ValueAt(i) != 0);
    public bool Solved => Enumerable.Range(0, 81).All(i => ValueAt(i) == _solution[i]);

    /// <summary>Writes <paramref name="digit"/> into an empty cell (0 clears it); its notes go, and the digit leaves its neighbours' notes.</summary>
    public bool Place(int i, int digit)
    {
        if (IsGiven(i) || digit < 0 || digit > 9 || Solved) return false;
        _entry[i] = digit;
        _notes[i] = 0;
        if (digit != 0)
            foreach (int p in Peers(i)) _notes[p] &= ~(1 << digit);
        return true;
    }

    /// <summary>Pencils a small <paramref name="digit"/> into an empty cell, or rubs it out.</summary>
    public bool ToggleNote(int i, int digit)
    {
        if (IsGiven(i) || _entry[i] != 0 || digit is < 1 or > 9 || Solved) return false;
        _notes[i] ^= 1 << digit;
        return true;
    }

    /// <summary>Whether the cell's digit repeats in its row, column or box.</summary>
    public bool Conflicts(int i)
    {
        int v = ValueAt(i);
        return v != 0 && Peers(i).Any(p => ValueAt(p) == v);
    }

    /// <summary>The other cells of <paramref name="i"/>'s row, column and box.</summary>
    public static IEnumerable<int> Peers(int i)
    {
        int r = Row(i), c = Col(i), b = Box(i);
        for (int k = 0; k < 81; k++)
            if (k != i && (Row(k) == r || Col(k) == c || Box(k) == b)) yield return k;
    }

    // ------------------------------------------------------------------ solving

    /// <summary>Fills an empty grid with a random complete solution.</summary>
    static bool Fill(int[] grid, int from, Random rng)
    {
        int i = Array.IndexOf(grid, 0, from);
        if (i < 0) return true;
        int free = Free(grid, i);
        foreach (int d in Enumerable.Range(1, 9).Where(d => (free & 1 << d) != 0).OrderBy(_ => rng.Next()))
        {
            grid[i] = d;
            if (Fill(grid, i + 1, rng)) return true;
        }
        grid[i] = 0;
        return false;
    }

    /// <summary>Solves <paramref name="grid"/> in place (the first solution found); false if it has none.</summary>
    public static bool Solve(int[] grid) => Count(grid, 1, keep: true) == 1;

    /// <summary>How many solutions <paramref name="grid"/> has, counting no further than <paramref name="limit"/>.</summary>
    public static int CountSolutions(int[] grid, int limit) => Count(grid, limit, keep: false);

    static int Count(int[] grid, int limit, bool keep)
    {
        // the empty cell with the fewest candidates first
        int best = -1, bestFree = 0, fewest = 10;
        for (int i = 0; i < 81; i++)
        {
            if (grid[i] != 0) continue;
            int free = Free(grid, i), n = BitOperations.PopCount((uint)free);
            if (n == 0) return 0;
            if (n < fewest)
            {
                fewest = n;
                best = i;
                bestFree = free;
                if (n == 1) break;
            }
        }
        if (best < 0) return 1; // full: a solution
        int found = 0;
        for (int d = 1; d <= 9 && found < limit; d++)
        {
            if ((bestFree & 1 << d) == 0) continue;
            grid[best] = d;
            found += Count(grid, limit - found, keep);
            if (keep && found > 0) return found;
        }
        grid[best] = 0;
        return found;
    }

    /// <summary>The digits cell <paramref name="i"/> could still take, as bits 1..9.</summary>
    static int Free(int[] grid, int i)
    {
        int used = 0, r = Row(i), c = Col(i), br = r / 3 * 3, bc = c / 3 * 3;
        for (int k = 0; k < 9; k++)
        {
            used |= 1 << grid[r * 9 + k];
            used |= 1 << grid[k * 9 + c];
            used |= 1 << grid[(br + k / 3) * 9 + bc + k % 3];
        }
        return All & ~used;
    }
}
