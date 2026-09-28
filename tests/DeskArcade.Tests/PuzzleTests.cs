using System;
using System.Linq;
using DeskArcade.Games;
using Xunit;

namespace DeskArcade.Tests;

public class MinesweeperTests
{
    static MinesweeperRules Board(int size = 1, int seed = 1, bool seeded = false)
    {
        var (c, r, m) = MinesweeperRules.Sizes[size];
        return new MinesweeperRules(c, r, m, new Random(seed), seeded);
    }

    static int MineCount(MinesweeperRules b) => Enumerable.Range(0, b.Count).Count(b.IsMine);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void TheFirstClickIsAlwaysSafeAndOpensACalmRegion(int size)
    {
        for (int seed = 0; seed < 30; seed++)
        {
            var b = Board(size, seed);
            int first = (seed * 37) % b.Count;
            Assert.NotEqual(MineResult.Boom, b.Open(first));
            Assert.False(b.IsMine(first));
            Assert.Equal(0, b.CountOf(first)); // nothing around it, so it opens its region
            Assert.True(b.Opened > 1);
            Assert.Equal(MinesweeperRules.Sizes[size].Mines, MineCount(b));
        }
    }

    [Fact]
    public void NumbersCountTheMinesAround()
    {
        var b = Board(seed: 3);
        b.Open(0);
        for (int i = 0; i < b.Count; i++)
            if (!b.IsMine(i)) Assert.Equal(b.Neighbours(i).Count(b.IsMine), b.CountOf(i));
    }

    [Fact]
    public void NeighboursStayOnTheBoard()
    {
        var b = Board(0);
        Assert.Equal(3, b.Neighbours(0).Count());
        Assert.Equal(5, b.Neighbours(1).Count());
        Assert.Equal(8, b.Neighbours(b.Index(4, 4)).Count());
        Assert.Equal(3, b.Neighbours(b.Count - 1).Count());
    }

    [Fact]
    public void OpeningAMineLosesAndEndsTheGame()
    {
        var b = Board(seed: 5);
        b.Open(0);
        int mine = Enumerable.Range(0, b.Count).First(b.IsMine);
        Assert.Equal(MineResult.Boom, b.Open(mine));
        Assert.True(b.Lost);
        Assert.Equal(mine, b.Exploded);
        Assert.Equal(MineResult.Nothing, b.Open(Enumerable.Range(0, b.Count).First(i => !b.IsMine(i) && b.StateOf(i) == MineCell.Hidden)));
    }

    [Fact]
    public void OpeningEverySafeCellWins()
    {
        var b = Board(0, seed: 8);
        b.Open(40);
        MineResult last = MineResult.Nothing;
        for (int i = 0; i < b.Count; i++)
            if (!b.IsMine(i) && b.StateOf(i) == MineCell.Hidden) last = b.Open(i);
        Assert.Equal(MineResult.Won, last);
        Assert.True(b.Won);
        Assert.Equal(b.Mines, b.Flags); // the mines flag themselves
    }

    [Fact]
    public void AFlagProtectsACellAndComesOffAgain()
    {
        var b = Board(seed: 2);
        b.Open(0);
        int hidden = Enumerable.Range(0, b.Count).First(i => b.StateOf(i) == MineCell.Hidden);
        Assert.True(b.ToggleFlag(hidden));
        Assert.Equal(MineResult.Nothing, b.Open(hidden));
        Assert.Equal(1, b.Flags);
        Assert.True(b.ToggleFlag(hidden));
        Assert.Equal(0, b.Flags);
        Assert.False(b.ToggleFlag(0)); // an open cell takes no flag
    }

    [Fact]
    public void AChordOpensTheRestAroundAFullyFlaggedNumber()
    {
        for (int seed = 0; seed < 50; seed++)
        {
            var b = Board(seed: seed);
            b.Open(0);
            // an open number whose mines are all still hidden: flag them, then chord
            int n = Enumerable.Range(0, b.Count).FirstOrDefault(i => b.StateOf(i) == MineCell.Open && b.CountOf(i) > 0 &&
                b.Neighbours(i).Any(k => !b.IsMine(k) && b.StateOf(k) == MineCell.Hidden), -1);
            if (n < 0) continue;
            foreach (int k in b.Neighbours(n).Where(b.IsMine)) b.ToggleFlag(k);
            int before = b.Opened;
            Assert.NotEqual(MineResult.Boom, b.Open(n));
            Assert.True(b.Opened > before);
            Assert.All(b.Neighbours(n).Where(k => !b.IsMine(k)), k => Assert.Equal(MineCell.Open, b.StateOf(k)));
            return;
        }
        Assert.Fail("no board to chord on");
    }

    [Fact]
    public void AWrongChordSetsOffTheMine()
    {
        for (int seed = 0; seed < 50; seed++)
        {
            var b = Board(seed: seed);
            b.Open(0);
            int n = Enumerable.Range(0, b.Count).FirstOrDefault(i => b.StateOf(i) == MineCell.Open && b.CountOf(i) == 1 &&
                b.Neighbours(i).Count(k => !b.IsMine(k) && b.StateOf(k) == MineCell.Hidden) >= 1, -1);
            if (n < 0) continue;
            int safe = b.Neighbours(n).First(k => !b.IsMine(k) && b.StateOf(k) == MineCell.Hidden);
            b.ToggleFlag(safe); // the flag on the wrong cell
            Assert.Equal(MineResult.Boom, b.Open(n));
            return;
        }
        Assert.Fail("no board for a wrong chord");
    }

    [Fact]
    public void ASeededBoardIsTheSameEverywhereAndNamesASafeStart()
    {
        int seed = MinesweeperRules.DailySeed(new DateTime(2026, 9, 28), "mines1");
        var a = Board(seed: seed, seeded: true);
        var b = Board(seed: seed, seeded: true);
        Assert.Equal(Enumerable.Range(0, a.Count).Select(a.IsMine), Enumerable.Range(0, b.Count).Select(b.IsMine));
        Assert.True(a.Start >= 0);
        Assert.False(a.IsMine(a.Start));
        Assert.Equal(0, a.CountOf(a.Start));
        Assert.Equal(MineResult.Opened, a.Open(a.Start));
    }

    [Fact]
    public void EachDayAndEachRaceHaveTheirOwnBoard()
    {
        var day = new DateTime(2026, 9, 28);
        Assert.NotEqual(MinesweeperRules.DailySeed(day, "mines1"), MinesweeperRules.DailySeed(day.AddDays(1), "mines1"));
        Assert.NotEqual(MinesweeperRules.DailySeed(day, "mines-lan", 1), MinesweeperRules.DailySeed(day, "mines-lan", 2));
        Assert.NotEqual(MinesweeperRules.DailySeed(day, "mines1"), MinesweeperRules.DailySeed(day, "sudoku1"));
    }
}

public class SudokuTests
{
    static bool ValidGrid(Func<int, int> value)
    {
        for (int k = 0; k < 9; k++)
        {
            var row = Enumerable.Range(0, 9).Select(c => value(k * 9 + c));
            var col = Enumerable.Range(0, 9).Select(r => value(r * 9 + k));
            var box = Enumerable.Range(0, 81).Where(i => SudokuRules.Box(i) == k).Select(value);
            foreach (var group in new[] { row, col, box })
                if (!group.OrderBy(v => v).SequenceEqual(Enumerable.Range(1, 9))) return false;
        }
        return true;
    }

    [Theory]
    [InlineData(SudokuLevel.Easy)]
    [InlineData(SudokuLevel.Medium)]
    [InlineData(SudokuLevel.Hard)]
    public void APuzzleHasAValidSolutionAndOnlyOne(SudokuLevel level)
    {
        for (int seed = 0; seed < 4; seed++)
        {
            var s = new SudokuRules(new Random(seed), level);
            Assert.True(ValidGrid(s.SolutionAt));
            var given = Enumerable.Range(0, 81).Select(i => s.IsGiven(i) ? s.SolutionAt(i) : 0).ToArray();
            Assert.Equal(1, SudokuRules.CountSolutions(given, 2));
            Assert.InRange(s.CountGiven(), SudokuRules.Clues(level), SudokuRules.Clues(level) + 12);
        }
    }

    [Fact]
    public void HarderLevelsGiveFewerClues()
    {
        double Mean(SudokuLevel l) => Enumerable.Range(0, 5).Average(seed => new SudokuRules(new Random(seed), l).CountGiven());
        Assert.True(Mean(SudokuLevel.Easy) > Mean(SudokuLevel.Medium));
        Assert.True(Mean(SudokuLevel.Medium) > Mean(SudokuLevel.Hard));
    }

    [Fact]
    public void TheSameSeedGivesTheSamePuzzle()
    {
        var a = new SudokuRules(new Random(42), SudokuLevel.Medium);
        var b = new SudokuRules(new Random(42), SudokuLevel.Medium);
        Assert.Equal(Enumerable.Range(0, 81).Select(a.ValueAt), Enumerable.Range(0, 81).Select(b.ValueAt));
    }

    [Fact]
    public void FillingInTheSolutionSolvesIt()
    {
        var s = new SudokuRules(new Random(7), SudokuLevel.Easy);
        Assert.False(s.Solved);
        foreach (int i in Enumerable.Range(0, 81).Where(i => !s.IsGiven(i))) Assert.True(s.Place(i, s.SolutionAt(i)));
        Assert.True(s.Solved);
        Assert.Equal(81, s.Filled);
        Assert.False(s.Place(Enumerable.Range(0, 81).First(i => !s.IsGiven(i)), 0)); // a solved grid stays solved
    }

    [Fact]
    public void GivenCellsCantBeChanged()
    {
        var s = new SudokuRules(new Random(1), SudokuLevel.Easy);
        int given = Enumerable.Range(0, 81).First(s.IsGiven);
        Assert.False(s.Place(given, 0));
        Assert.False(s.ToggleNote(given, 3));
    }

    [Fact]
    public void ARepeatedDigitIsAConflictOnBothCells()
    {
        var s = new SudokuRules(new Random(3), SudokuLevel.Hard);
        int empty = Enumerable.Range(0, 81).First(i => !s.IsGiven(i));
        int clash = SudokuRules.Peers(empty).First(s.IsGiven);
        Assert.True(s.Place(empty, s.ValueAt(clash)));
        Assert.True(s.Conflicts(empty));
        Assert.True(s.Conflicts(clash));
        s.Place(empty, 0);
        Assert.False(s.Conflicts(clash));
    }

    [Fact]
    public void NotesComeAndGoAndADigitRubsThemOutAround()
    {
        var s = new SudokuRules(new Random(4), SudokuLevel.Hard);
        var empties = Enumerable.Range(0, 81).Where(i => !s.IsGiven(i)).ToList();
        int a = empties[0];
        int b = empties.Skip(1).First(i => SudokuRules.Peers(a).Contains(i));
        Assert.True(s.ToggleNote(b, 5));
        Assert.True(s.ToggleNote(b, 7));
        Assert.Equal(1 << 5 | 1 << 7, s.NotesAt(b));
        Assert.True(s.ToggleNote(b, 7));
        Assert.Equal(1 << 5, s.NotesAt(b));
        s.Place(a, 5); // the 5 goes from the peer's notes
        Assert.Equal(0, s.NotesAt(b));
        Assert.False(s.ToggleNote(a, 3)); // a filled cell takes no notes
    }

    [Fact]
    public void PeersAreTheRowColumnAndBox()
    {
        var peers = SudokuRules.Peers(40).ToList(); // the centre
        Assert.Equal(20, peers.Count);
        Assert.DoesNotContain(40, peers);
        Assert.Contains(36, peers); // same row
        Assert.Contains(4, peers);  // same column
        Assert.Contains(30, peers); // same box
    }

    [Fact]
    public void TheSolverFindsTheOnlySolution()
    {
        // a classic puzzle with one solution
        int[] given =
        {
            5, 3, 0, 0, 7, 0, 0, 0, 0, 6, 0, 0, 1, 9, 5, 0, 0, 0, 0, 9, 8, 0, 0, 0, 0, 6, 0,
            8, 0, 0, 0, 6, 0, 0, 0, 3, 4, 0, 0, 8, 0, 3, 0, 0, 1, 7, 0, 0, 0, 2, 0, 0, 0, 6,
            0, 6, 0, 0, 0, 0, 2, 8, 0, 0, 0, 0, 4, 1, 9, 0, 0, 5, 0, 0, 0, 0, 8, 0, 0, 7, 9,
        };
        Assert.Equal(1, SudokuRules.CountSolutions((int[])given.Clone(), 2));
        var s = new SudokuRules(given);
        Assert.True(ValidGrid(s.SolutionAt));
        Assert.Equal(4, s.SolutionAt(2));
        var empty = new int[81];
        Assert.Equal(2, SudokuRules.CountSolutions(empty, 2)); // an empty grid has many
    }
}
