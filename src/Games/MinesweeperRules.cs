using System;
using System.Collections.Generic;
using System.Linq;

namespace DeskArcade.Games;

public enum MineCell { Hidden, Flagged, Open }

public enum MineResult { Nothing, Opened, Boom, Won }

/// <summary>
/// Minesweeper's board: a grid with mines, each other cell counting the mines around it. Opening a cell with no mines
/// around opens its neighbours too, a whole calm region at once; opening a mine loses; opening every safe cell wins.
/// A flag marks a suspected mine (and can't be opened by mistake); opening a number whose flags are all placed opens its
/// other neighbours, the "chord". A random board lays its mines after the first click, away from it, so the first click
/// is always safe; a seeded board (the daily puzzle, or a LAN race, the same on both screens) lays them at once and
/// names a <see cref="Start"/> cell with no mines around it. UI-free and seeded, so it can be tested.
/// </summary>
public sealed class MinesweeperRules
{
    public static readonly (int Cols, int Rows, int Mines)[] Sizes = { (9, 9, 10), (16, 16, 40), (30, 16, 99) };

    readonly bool[] _mine;
    readonly int[] _count;
    readonly MineCell[] _state;
    readonly Random _rng;
    bool _laid;

    /// <param name="seeded">Lay the mines now (the same for the same seed), with a safe <see cref="Start"/> cell named.</param>
    public MinesweeperRules(int cols, int rows, int mines, Random rng, bool seeded)
    {
        Cols = cols;
        Rows = rows;
        Mines = Math.Clamp(mines, 1, cols * rows - 9);
        _rng = rng;
        _mine = new bool[cols * rows];
        _count = new int[cols * rows];
        _state = new MineCell[cols * rows];
        if (!seeded) return;
        Lay(-1);
        var calm = Enumerable.Range(0, Count).Where(i => !_mine[i] && _count[i] == 0).ToList();
        Start = calm.Count > 0 ? calm[_rng.Next(calm.Count)] : Enumerable.Range(0, Count).First(i => !_mine[i]);
    }

    public int Cols { get; }
    public int Rows { get; }
    public int Mines { get; }
    public int Count => Cols * Rows;

    /// <summary>A seeded board's safe first cell (no mines around it); -1 on a random board, where every first click is safe.</summary>
    public int Start { get; } = -1;

    public bool Lost { get; private set; }
    public bool Won { get; private set; }
    public bool Over => Lost || Won;
    public bool Started { get; private set; }
    public int Exploded { get; private set; } = -1;
    public int Flags => _state.Count(s => s == MineCell.Flagged);
    public int Opened => _state.Count(s => s == MineCell.Open);

    public MineCell StateOf(int i) => _state[i];
    public bool IsMine(int i) => _laid && _mine[i];

    /// <summary>The number shown on an open cell: the mines around it.</summary>
    public int CountOf(int i) => _count[i];

    public int Index(int col, int row) => row * Cols + col;

    public IEnumerable<int> Neighbours(int i)
    {
        int c = i % Cols, r = i / Cols;
        for (int dr = -1; dr <= 1; dr++)
            for (int dc = -1; dc <= 1; dc++)
            {
                if (dr == 0 && dc == 0) continue;
                int nc = c + dc, nr = r + dr;
                if (nc >= 0 && nc < Cols && nr >= 0 && nr < Rows) yield return nr * Cols + nc;
            }
    }

    /// <summary>Opens a hidden cell (the calm region around a zero with it), or chords an open number.</summary>
    public MineResult Open(int i)
    {
        if (Over || i < 0 || i >= Count || _state[i] == MineCell.Flagged) return MineResult.Nothing;
        if (!_laid) Lay(i);
        Started = true;
        if (_state[i] == MineCell.Open) return Chord(i);
        return Reveal(new[] { i });
    }

    /// <summary>Flags a hidden cell, or takes a flag off.</summary>
    public bool ToggleFlag(int i)
    {
        if (Over || i < 0 || i >= Count || _state[i] == MineCell.Open) return false;
        _state[i] = _state[i] == MineCell.Flagged ? MineCell.Hidden : MineCell.Flagged;
        return true;
    }

    MineResult Chord(int i)
    {
        var around = Neighbours(i).ToList();
        if (_count[i] == 0 || around.Count(n => _state[n] == MineCell.Flagged) != _count[i]) return MineResult.Nothing;
        return Reveal(around.Where(n => _state[n] == MineCell.Hidden));
    }

    MineResult Reveal(IEnumerable<int> cells)
    {
        bool any = false;
        var todo = new Stack<int>(cells);
        while (todo.Count > 0)
        {
            int c = todo.Pop();
            if (_state[c] != MineCell.Hidden) continue;
            _state[c] = MineCell.Open;
            any = true;
            if (_mine[c])
            {
                Lost = true;
                Exploded = c;
                return MineResult.Boom;
            }
            if (_count[c] == 0)
                foreach (int n in Neighbours(c))
                    if (_state[n] == MineCell.Hidden) todo.Push(n);
        }
        if (Opened == Count - Mines)
        {
            Won = true;
            for (int k = 0; k < Count; k++)
                if (_mine[k]) _state[k] = MineCell.Flagged; // the mines flag themselves
            return MineResult.Won;
        }
        return any ? MineResult.Opened : MineResult.Nothing;
    }

    /// <summary>Lays the mines, none on <paramref name="safe"/> or around it (-1: anywhere), and counts every cell's neighbours.</summary>
    void Lay(int safe)
    {
        _laid = true;
        var keepClear = new HashSet<int>();
        if (safe >= 0)
        {
            keepClear.Add(safe);
            foreach (int n in Neighbours(safe)) keepClear.Add(n);
        }
        var spots = Enumerable.Range(0, Count).Where(k => !keepClear.Contains(k)).ToList();
        for (int k = 0; k < Mines && spots.Count > 0; k++)
        {
            int pick = _rng.Next(spots.Count);
            _mine[spots[pick]] = true;
            spots.RemoveAt(pick);
        }
        for (int k = 0; k < Count; k++) _count[k] = Neighbours(k).Count(n => _mine[n]);
    }

    /// <summary>The seed of a puzzle everyone gets on <paramref name="day"/>: the daily board, and the n-th board of a LAN race.</summary>
    public static int DailySeed(DateTime day, string game, int round = 0) =>
        unchecked(day.Year * 10000 + day.Month * 100 + day.Day + game.Aggregate(17, (h, ch) => h * 31 + ch) * 7 + round * 104729);
}
