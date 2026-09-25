using System;
using System.Linq;
using System.Text;
using DeskArcade.Games;
using Xunit;

namespace DeskArcade.Tests;

/// <summary>The board games' animation planner: what slides where, what vanishes, and when the board is drawn afresh instead.</summary>
public class BoardAnimPlanTests
{
    static int Sq(string name) => ChessRules.Sq(name);

    /// <summary>Builds a position from the piece-placement part of a FEN string.</summary>
    static ChessRules Fen(string placement, bool white, int castle = 0)
    {
        var board = new StringBuilder();
        foreach (char ch in placement)
            if (char.IsDigit(ch)) board.Append('.', ch - '0');
            else if (ch != '/') board.Append(ch);
        return ChessRules.Decode($"{board}|{(white ? "w" : "b")}|{castle}|-1|0|0")!;
    }

    static (sbyte[] Before, sbyte[] After) Played(IBoardRules game, int[] move)
    {
        var before = (sbyte[])game.Board.Clone();
        game.Apply(move);
        return (before, (sbyte[])game.Board.Clone());
    }

    [Fact]
    public void AQuietMoveSlidesJustTheMover()
    {
        var move = new[] { Sq("e2"), Sq("e4") };
        var (before, after) = Played(ChessRules.New(), move);
        var plan = BoardAnim.Plan(before, after, move);
        Assert.NotNull(plan);
        var slide = Assert.Single(plan!.Slides);
        Assert.Equal(move, slide.Path);
        Assert.Equal(ChessRules.Pawn, slide.Piece);
        Assert.Empty(plan.Vanish);
        Assert.Null(plan.Appear);
        Assert.Null(plan.Becomes);
    }

    [Fact]
    public void ACaptureVanishesWhatStoodOnTheLandingSquare()
    {
        var c = ChessRules.New();
        c.Apply(new[] { Sq("e2"), Sq("e4") });
        c.Apply(new[] { Sq("d7"), Sq("d5") });
        var move = new[] { Sq("e4"), Sq("d5") };
        var (before, after) = Played(c, move);
        var plan = BoardAnim.Plan(before, after, move)!;
        Assert.Single(plan.Slides);
        var gone = Assert.Single(plan.Vanish);
        Assert.Equal((Sq("d5"), (sbyte)(-ChessRules.Pawn)), gone);
    }

    [Fact]
    public void EnPassantVanishesThePawnBesideTheLanding()
    {
        var c = ChessRules.New();
        foreach (var (a, b) in new[] { ("e2", "e4"), ("a7", "a6"), ("e4", "e5"), ("d7", "d5") }) c.Apply(new[] { Sq(a), Sq(b) });
        var move = new[] { Sq("e5"), Sq("d6") };
        var (before, after) = Played(c, move);
        var plan = BoardAnim.Plan(before, after, move)!;
        Assert.Equal(Sq("d5"), Assert.Single(plan.Vanish).Square);
        Assert.Equal(0, after[Sq("d5")]);
    }

    [Fact]
    public void CastlingSlidesTheRookAlongWithTheKing()
    {
        var c = Fen("r3k2r/8/8/8/8/8/8/R3K2R", true, 15);
        var move = new[] { Sq("e1"), Sq("g1") };
        Assert.Contains(c.LegalMoves(), m => m.SequenceEqual(move));
        var (before, after) = Played(c, move);
        var plan = BoardAnim.Plan(before, after, move)!;
        Assert.Equal(2, plan.Slides.Count);
        Assert.Equal(move, plan.Slides[0].Path);
        Assert.Equal(new[] { Sq("h1"), Sq("f1") }, plan.Slides[1].Path);
        Assert.Equal(ChessRules.Rook, plan.Slides[1].Piece);
        Assert.Empty(plan.Vanish);
    }

    [Fact]
    public void PromotionChangesTheMoverWhereItLands()
    {
        var c = Fen("4k3/P7/8/8/8/8/8/4K3", true);
        var move = new[] { Sq("a7"), Sq("a8") };
        var (before, after) = Played(c, move);
        var plan = BoardAnim.Plan(before, after, move)!;
        Assert.Equal(ChessRules.Pawn, Assert.Single(plan.Slides).Piece);
        Assert.Equal((Sq("a8"), (sbyte)ChessRules.Queen), plan.Becomes!.Value);
    }

    [Fact]
    public void AMultipleJumpVisitsEachLandingAndVanishesEveryPieceTaken()
    {
        var d = Draughts.Decode(new string('.', 64) + "|w|0")!;
        d.Board[44] = 1;  // a white man
        d.Board[35] = -1; // two black men on its way up
        d.Board[19] = -1;
        var move = Assert.Single(d.LegalMoves());
        Assert.Equal(new[] { 44, 26, 12 }, move);
        var (before, after) = Played(d, move);
        var plan = BoardAnim.Plan(before, after, move)!;
        Assert.Equal(new[] { 44, 26, 12 }, Assert.Single(plan.Slides).Path);
        Assert.Equal(new[] { 19, 35 }, plan.Vanish.Select(v => v.Square).OrderBy(s => s));
        Assert.Equal(0, BoardAnim.LegOf(move, 35, 8)); // taken on the first leg
        Assert.Equal(1, BoardAnim.LegOf(move, 19, 8)); // taken on the second
    }

    [Fact]
    public void CrowningChangesTheManWhereItLands()
    {
        var d = Draughts.Decode(new string('.', 64) + "|w|0")!;
        d.Board[10] = 1;
        d.Board[40] = -1;
        var move = new[] { 10, 1 };
        Assert.Contains(d.LegalMoves(), m => m.SequenceEqual(move));
        var (before, after) = Played(d, move);
        var plan = BoardAnim.Plan(before, after, move)!;
        Assert.Equal((1, (sbyte)2), plan.Becomes!.Value);
        Assert.Empty(plan.Vanish);
    }

    [Fact]
    public void APlacedPieceAppearsOnItsSquare()
    {
        var move = new[] { 38 }; // the bottom of column 3
        var (before, after) = Played(LineRules.ConnectFour(), move);
        var plan = BoardAnim.Plan(before, after, move)!;
        Assert.Empty(plan.Slides);
        Assert.Empty(plan.Vanish);
        Assert.Equal((38, (sbyte)1), plan.Appear!.Value);
    }

    [Fact]
    public void TwoPliesAtOnceAreNotOneMove()
    {
        var c = ChessRules.New();
        var before = (sbyte[])c.Board.Clone();
        var move = new[] { Sq("e2"), Sq("e4") };
        c.Apply(move);
        c.Apply(new[] { Sq("e7"), Sq("e5") });
        Assert.Null(BoardAnim.Plan(before, c.Board, move));
    }

    [Fact]
    public void AnUnchangedBoardOrTheWrongMoveIsNoPlan()
    {
        var c = ChessRules.New();
        var same = (sbyte[])c.Board.Clone();
        Assert.Null(BoardAnim.Plan(c.Board, same, new[] { Sq("e2"), Sq("e4") }));
        var (before, after) = Played(c, new[] { Sq("e2"), Sq("e4") });
        Assert.Null(BoardAnim.Plan(before, after, new[] { Sq("d2"), Sq("d4") }));
    }

    [Fact]
    public void LegOfFindsTheLegThatPassesOverASquare()
    {
        var path = new[] { 44, 26, 12 };
        Assert.Equal(0, BoardAnim.LegOf(path, 35, 8));
        Assert.Equal(1, BoardAnim.LegOf(path, 19, 8));
        Assert.Equal(1, BoardAnim.LegOf(path, 12, 8)); // the landing itself
        Assert.Equal(1, BoardAnim.LegOf(path, 0, 8));  // off the path: the last leg
        Assert.Equal(0, BoardAnim.LegOf(new[] { 60, 12 }, 36, 8)); // a rook's file passes the middle
    }
}

/// <summary>Whether a board that arrived over the network is animated as one move or drawn at once.</summary>
public class BoardAnimDecideTests
{
    static int Sq(string name) => ChessRules.Sq(name);

    [Fact]
    public void OneLegalPlyOnIsAnimated()
    {
        var c = ChessRules.New();
        var legal = c.LegalMoves();
        var before = (sbyte[])c.Board.Clone();
        int ply = c.Ply;
        var move = new[] { Sq("g1"), Sq("f3") };
        c.Apply(move);
        var plan = BoardAnim.Decide(before, c.Board, ply, c.Ply, move, legal);
        Assert.NotNull(plan);
        Assert.Equal(move, Assert.Single(plan!.Slides).Path);
    }

    [Fact]
    public void SeveralPliesNoPathAnIllegalPathOrARematchRedrawAtOnce()
    {
        var c = ChessRules.New();
        var legal = c.LegalMoves();
        var start = (sbyte[])c.Board.Clone();
        int ply = c.Ply;
        var move = new[] { Sq("e2"), Sq("e4") };
        c.Apply(move);
        var one = (sbyte[])c.Board.Clone();
        int onePly = c.Ply;
        c.Apply(new[] { Sq("e7"), Sq("e5") });
        Assert.Null(BoardAnim.Decide(start, c.Board, ply, c.Ply, new[] { Sq("e7"), Sq("e5") }, legal)); // two plies
        Assert.Null(BoardAnim.Decide(start, one, ply, onePly, null, legal));                            // no path known
        Assert.Null(BoardAnim.Decide(start, one, ply, onePly, new[] { Sq("e2"), Sq("e5") }, legal));    // not a legal move
        Assert.Null(BoardAnim.Decide(one, ChessRules.New().Board, onePly, 0, null, legal));             // a rematch
        Assert.NotNull(BoardAnim.Decide(start, one, ply, onePly, move, legal));
    }
}

/// <summary>The winning line of the "N in a row" games, which lights up.</summary>
public class LineArtTests
{
    [Fact]
    public void TheDiagonalThatWinsTicTacToeIsFound()
    {
        var g = LineRules.TicTacToe();
        foreach (int sq in new[] { 0, 1, 4, 2, 8 }) g.Apply(new[] { sq }); // X: 0, 4, 8; O: 1, 2
        Assert.Equal(1, g.Winner());
        Assert.Equal(new[] { 0, 4, 8 }, LineArt.WinningLine(g.Board, 3, 3, 3));
    }

    [Fact]
    public void NoLineWhileNobodyHasWon()
    {
        Assert.Null(LineArt.WinningLine(new sbyte[9], 3, 3, 3));
        var g = LineRules.TicTacToe();
        foreach (int sq in new[] { 0, 1, 4 }) g.Apply(new[] { sq });
        Assert.Null(LineArt.WinningLine(g.Board, 3, 3, 3));
    }

    [Fact]
    public void FourAcrossTheBottomRowOfConnectFour()
    {
        var g = LineRules.ConnectFour();
        foreach (int col in new[] { 0, 6, 1, 6, 2, 6, 3 }) g.Apply(g.LegalMoves().First(m => m[0] % 7 == col));
        Assert.Equal(1, g.Winner());
        Assert.Equal(new[] { 35, 36, 37, 38 }, LineArt.WinningLine(g.Board, 7, 6, 4));
    }
}

/// <summary>Sea Battle's computer shooter at each of its levels.</summary>
public class SeaBattleCpuTests
{
    static sbyte[] Chart() => new sbyte[SeaFleet.N * SeaFleet.N];

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void EveryLevelSinksAFleetWithoutFiringTwiceAtASquare(int level)
    {
        var rng = new Random(7);
        var chart = Chart();
        var fleet = SeaFleet.Random(rng);
        for (int shot = 0; shot < 100 && !fleet.AllSunk; shot++)
        {
            int sq = SeaChart.NextShot(chart, rng, level);
            if (level == 1) Assert.True(chart[sq] is SeaChart.Unknown or SeaChart.Empty);
            else Assert.Equal(SeaChart.Unknown, chart[sq]);
            SeaChart.Record(chart, sq, fleet.Shoot(sq));
        }
        Assert.True(fleet.AllSunk);
    }

    [Fact]
    public void MediumAndUpTargetAroundAHit()
    {
        var chart = Chart();
        chart[55] = SeaChart.Hit;
        foreach (int level in new[] { 2, 3, 4 })
            for (int i = 0; i < 20; i++)
                Assert.Contains(SeaChart.NextShot(chart, new Random(i), level), new[] { 45, 54, 56, 65 });
    }

    [Fact]
    public void TwoHitsInARowAreExtendedAlongTheirLine()
    {
        var chart = Chart();
        chart[54] = chart[55] = SeaChart.Hit;
        Assert.Equal(new[] { 53, 56 }, SeaChart.Targets(chart).OrderBy(s => s));
        chart[45] = SeaChart.Hit;
        chart[54] = SeaChart.Unknown;
        Assert.Equal(new[] { 35, 65 }, SeaChart.Targets(chart).OrderBy(s => s));
    }

    [Fact]
    public void HardHuntsOnACheckerboard()
    {
        var chart = Chart();
        for (int i = 0; i < 30; i++)
        {
            int sq = SeaChart.NextShot(chart, new Random(i), 3);
            Assert.Equal(0, (sq / 10 + sq % 10) % 2);
        }
    }

    [Fact]
    public void EasyWastesShotsNextToASunkShipWhereHardNeverDoes()
    {
        var chart = Chart();
        SeaChart.Record(chart, 0, new ShotResult(ShotKind.Hit, Array.Empty<int>()));
        SeaChart.Record(chart, 1, new ShotResult(ShotKind.Sunk, new[] { 0, 1 }));
        bool wasted = false;
        for (int i = 0; i < 300 && !wasted; i++) wasted = chart[SeaChart.NextShot(chart, new Random(i), 1)] == SeaChart.Empty;
        Assert.True(wasted);
        for (int i = 0; i < 50; i++) Assert.Equal(SeaChart.Unknown, chart[SeaChart.NextShot(chart, new Random(i), 3)]);
    }

    [Fact]
    public void ExpertHuntsWhereShipsCouldStillLie()
    {
        var chart = Chart();
        Array.Fill(chart, SeaChart.Miss);
        for (int c = 2; c < 7; c++) chart[40 + c] = SeaChart.Unknown; // the only gap a ship still fits in
        for (int i = 0; i < 10; i++) Assert.InRange(SeaChart.NextShot(chart, new Random(i), 4), 42, 46);
        var density = SeaChart.Density(chart);
        Assert.True(density[44] > density[42]); // the middle of the gap fits more ships than its end
        Assert.Equal(0, density[0]);
    }

    [Fact]
    public void RemainingSizesFollowTheShipsSunk()
    {
        var chart = Chart();
        Assert.Equal(new[] { 5, 4, 3, 3, 2 }, SeaChart.RemainingSizes(chart));
        SeaChart.Record(chart, 20, new ShotResult(ShotKind.Sunk, new[] { 20, 21, 22 }));
        Assert.Equal(new[] { 3 }, SeaChart.SunkSizes(chart));
        Assert.Equal(new[] { 5, 4, 3, 2 }, SeaChart.RemainingSizes(chart));
        SeaChart.Record(chart, 99, new ShotResult(ShotKind.Sunk, new[] { 89, 99 }));
        Assert.Equal(new[] { 2, 3 }, SeaChart.SunkSizes(chart).OrderBy(s => s));
    }
}
