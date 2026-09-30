using System;
using System.Collections.Generic;
using System.Linq;
using DeskArcade.Games;
using Xunit;
using Xunit.Abstractions;
using static DeskArcade.Games.SolitaireRules;

namespace DeskArcade.Tests;

/// <summary>FreeCell and Spider on the Solitaire table: deals, move rules, supermoves, autoplay, and the simple players.</summary>
public class SolitaireVariantsTests
{
    readonly ITestOutputHelper _out;

    public SolitaireVariantsTests(ITestOutputHelper output) => _out = output;

    /// <summary>A card from its name: "JD", "10S" or "TS", "AH".</summary>
    static int P(string name)
    {
        string rank = name[..^1];
        int r = rank switch { "A" => 1, "T" => 10, "J" => 11, "Q" => 12, "K" => 13, _ => int.Parse(rank) };
        int s = name[^1] switch { 'S' => 0, 'C' => 1, 'D' => 2, 'H' => 3, _ => throw new ArgumentException(name) };
        return Card(s, r);
    }

    static int[] Row(string cards) => cards.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(P).ToArray();

    /// <summary>A FreeCell table with every suit home up to <paramref name="home"/> and the other cards where the test puts them.</summary>
    static FreeCellRules Fc(int home, string[] cascades, string cells = "")
    {
        var cols = Enumerable.Range(0, FreeCellRules.Cascades).Select(i => (IReadOnlyList<int>)(i < cascades.Length ? Row(cascades[i]) : Array.Empty<int>())).ToList();
        return new FreeCellRules(cols, Row(cells), new[] { home, home, home, home });
    }

    // ------------------------------------------------------------------ FreeCell deals

    /// <summary>Deal 1 as every FreeCell program shows it (the rows are the cards across the eight cascades).</summary>
    [Fact]
    public void MicrosoftDealOneIsTheWellKnownLayout()
    {
        var rows = new[]
        {
            "JD 2D 9H JC 5D 7H 7C 5H",
            "KD KC 9S 5S AD QC KH 3H",
            "2S KS 9D QD JS AS AH 3C",
            "4C 5C TS QH 4H AC 4D 7S",
            "3S TD 4S TH 8H 2C JH 7D",
            "6D 8S 8D QS 6C 3D 8C TC",
            "6S 9C 2H 6H",
        };
        AssertDeal(1, rows);
    }

    [Fact]
    public void MicrosoftDeal617MatchesToo()
    {
        var rows = new[]
        {
            "7D AD 5C 3S 5S 8C 2D AH",
            "TD 7S QD AC 6D 8H AS KH",
            "TH QC 3H 9D 6S 8D 3D TC",
            "KD 5H 9S 3C 8S 7H 4D JS",
            "4C QS 9C 9H 7C 6H 2C 2S",
            "4S TS 2H 5D JC 6C JH QH",
            "JD KS KC 4H",
        };
        AssertDeal(617, rows);
    }

    static void AssertDeal(int deal, string[] rows)
    {
        var r = new FreeCellRules(deal);
        Assert.Equal(deal, r.Deal);
        for (int c = 0; c < FreeCellRules.Cascades; c++)
        {
            var expected = rows.Select(Row).Where(row => row.Length > c).Select(row => row[c]).ToArray();
            Assert.Equal(expected, r.Cards(Spot.Tableau(c)));
        }
    }

    [Fact]
    public void DealsAreRepeatableAndEachIsTheWholeDeck()
    {
        Assert.Equal(FreeCellRules.MsDeal(11982), FreeCellRules.MsDeal(11982));
        Assert.NotEqual(FreeCellRules.MsDeal(1), FreeCellRules.MsDeal(2));
        foreach (int deal in new[] { 1, 2, 31999, 32000, 123456, FreeCellRules.MaxDeal })
            Assert.Equal(Enumerable.Range(0, DeckSize), FreeCellRules.MsDeal(deal).OrderBy(c => c));
        Assert.Throws<ArgumentOutOfRangeException>(() => FreeCellRules.MsDeal(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => FreeCellRules.MsDeal(FreeCellRules.MaxDeal + 1));
    }

    [Fact]
    public void EightCascadesOfSevenAndSixWithTheCellsEmpty()
    {
        var r = new FreeCellRules(5);
        for (int c = 0; c < FreeCellRules.Cascades; c++) Assert.Equal(c < 4 ? 7 : 6, r.Cards(Spot.Tableau(c)).Count);
        Assert.Equal(4, r.FreeCellsLeft);
        Assert.Equal(0, r.Home);
        Assert.False(r.CanUndo);
    }

    // ------------------------------------------------------------------ FreeCell moves

    [Fact]
    public void AFreeCellHoldsAnyOneCard()
    {
        var r = Fc(10, new[] { "KS QH", "JS", "KH QS JH", "KD QC JD", "KC QD JC" });
        Assert.True(r.Apply(new Move(Spot.Tableau(0), 1, Spot.Cell(0))));
        Assert.Equal(new[] { P("QH") }, r.Cards(Spot.Cell(0)));
        Assert.False(r.IsLegal(new Move(Spot.Tableau(0), 1, Spot.Cell(0))));  // taken
        Assert.False(r.IsLegal(new Move(Spot.Tableau(2), 2, Spot.Cell(1))));  // one card only
        Assert.True(r.IsLegal(new Move(Spot.Cell(0), 1, Spot.Tableau(0))));   // and back onto the black king
        Assert.Equal(3, r.FreeCellsLeft);
    }

    [Fact]
    public void CascadesBuildDownInAlternatingColours()
    {
        var r = Fc(10, new[] { "KS", "QH", "QS", "JD", "JC", "KH KD KC", "QD QC JS JH" });
        Assert.True(r.IsLegal(new Move(Spot.Tableau(1), 1, Spot.Tableau(0))));  // red queen on black king
        Assert.False(r.IsLegal(new Move(Spot.Tableau(2), 1, Spot.Tableau(0)))); // black on black
        Assert.False(r.IsLegal(new Move(Spot.Tableau(3), 1, Spot.Tableau(0)))); // a jack on a king
        Assert.True(r.Apply(new Move(Spot.Tableau(1), 1, Spot.Tableau(0))));
        Assert.True(r.IsLegal(new Move(Spot.Tableau(4), 1, Spot.Tableau(0))));  // black jack on the red queen
        Assert.Equal(2, r.Movable(Spot.Tableau(0)));
        Assert.Equal(1, r.Movable(Spot.Tableau(5))); // KC on KD is no run
    }

    [Fact]
    public void AnyCardGoesIntoAnEmptyCascade()
    {
        var r = Fc(10, new[] { "KS QH JC", "KH", "KD", "KC", "QS", "QD", "QC", "JS JD JH" });
        Assert.False(r.Apply(new Move(Spot.Tableau(1), 1, Spot.Tableau(2)))); // K♥ doesn't go on K♦
        Assert.True(r.Apply(new Move(Spot.Tableau(4), 1, Spot.Tableau(1))));         // QS on KH
        Assert.True(r.Apply(new Move(Spot.Tableau(7), 1, Spot.Cell(0))));
        Assert.True(r.Apply(new Move(Spot.Tableau(7), 1, Spot.Cell(1))));
        Assert.True(r.Apply(new Move(Spot.Tableau(7), 1, Spot.Tableau(4))));          // a jack into the empty cascade, no king needed
        Assert.Equal(new[] { P("JS") }, r.Cards(Spot.Tableau(4)));
    }

    [Fact]
    public void FoundationsBuildUpBySuitAndKeepTheirCards()
    {
        var r = Fc(10, new[] { "KS QH JS", "KH QS JH", "KD QC JD", "KC QD JC" });
        Assert.False(r.IsLegal(new Move(Spot.Tableau(0), 1, Spot.Foundation(3)))); // a spade on hearts
        Assert.True(r.Apply(new Move(Spot.Tableau(0), 1, Spot.Foundation(0))));
        Assert.Equal(41, r.Home);
        Assert.Equal(0, r.Movable(Spot.Foundation(0)));
        Assert.False(r.IsLegal(new Move(Spot.Foundation(0), 1, Spot.Tableau(1)))); // no coming back down
    }

    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(1, 0, 2)]
    [InlineData(4, 0, 5)]
    [InlineData(0, 1, 2)]
    [InlineData(2, 1, 6)]
    [InlineData(3, 2, 16)]
    [InlineData(4, 2, 20)]
    [InlineData(4, 3, 40)]
    public void SupermovesAreFreeCellsPlusOneDoubledForEachEmptyCascade(int cells, int empty, int expected) =>
        Assert.Equal(expected, FreeCellRules.MaxMoveFor(cells, empty));

    /// <summary>A run of four (Q♠ J♥ 10♠ 9♥) onto the red king, with the free cells filled one by one.</summary>
    [Fact]
    public void ARunMovesOnlyAsFarAsTheFreeSpaceAllows()
    {
        // home up to 8: the 9s to kings are on the table
        var r = Fc(8, new[] { "KC QS JH TS 9H", "KH", "KS QD JS TD 9S", "KD QC JC", "TC 9C", "TH 9D", "QH JD" });
        Assert.Equal(4, r.Movable(Spot.Tableau(0))); // K♣ isn't part of it: Q♠ on K♣ is black on black
        var run = new Move(Spot.Tableau(0), 4, Spot.Tableau(1));
        Assert.Equal(4, r.FreeCellsLeft);
        Assert.Equal(1, r.EmptyCascades);
        Assert.True(r.IsLegal(run)); // (4 + 1) × 2 = 10
        foreach (int cell in new[] { 0, 1, 2 })
            Assert.True(r.Apply(new Move(Spot.Tableau(4 + cell), 1, Spot.Cell(cell))));
        Assert.True(r.IsLegal(run)); // (1 + 1) × 2 = 4
        Assert.True(r.Apply(new Move(Spot.Tableau(2), 1, Spot.Cell(3)))); // no cascade empties on the way
        Assert.Equal(1, r.EmptyCascades);
        Assert.False(r.IsLegal(run)); // (0 + 1) × 2 = 2
        Assert.False(r.IsLegal(run with { Count = 2 })); // two cards from the top start with 10♠, which doesn't go on K♥
        Assert.Equal(2, r.MaxMove(Spot.Tableau(1)));
    }

    [Fact]
    public void AnEmptyCascadeYouMoveIntoDoesNotCount()
    {
        var r = Fc(9, new[] { "KC QD JS TH", "KS QH", "KH JC", "KD QS JH", "QC JD TC", "TD" }, "TS");
        // free cells: 3, empty cascades: 2 (6 and 7)
        Assert.Equal(3, r.FreeCellsLeft);
        Assert.Equal(2, r.EmptyCascades);
        Assert.Equal(16, r.MaxMove(Spot.Tableau(1)));  // onto a card: (3 + 1) × 4
        Assert.Equal(8, r.MaxMove(Spot.Tableau(6)));   // into an empty one: (3 + 1) × 2
    }

    [Fact]
    public void CardsNoLongerNeededGoHomeAsPartOfTheMove()
    {
        // home up to 10: a jack is safe (both tens of the other colour are home), a queen is not yet
        var r = Fc(10, new[] { "KS JS QH", "KC", "KH JH QS", "KD JD QC", "JC QD" });
        Assert.Null(r.SafeHome());
        Assert.True(r.Apply(new Move(Spot.Tableau(0), 1, Spot.Tableau(1)))); // Q♥ onto K♣ uncovers J♠
        Assert.Equal(new Move(Spot.Tableau(0), 1, Spot.Foundation(0)), r.SafeHome());
        FreeCellSolver.Settle(r);
        Assert.Equal(11, r.Cards(Spot.Foundation(0)).Count); // J♠ went home by itself
        Assert.Null(r.SafeHome());
        Assert.Equal(1, r.Moves);                             // and that was no move of its own
        Assert.True(r.Undo());
        Assert.Equal(10, r.Cards(Spot.Foundation(0)).Count);  // one undo takes the move and the card home back
        Assert.Equal(new[] { P("KS"), P("JS"), P("QH") }, r.Cards(Spot.Tableau(0)));
    }

    [Fact]
    public void ACardStaysWhileSomethingCouldStillGoOnIt()
    {
        // 5♥ could still take the black fours, which are not home
        Assert.False(FreeCellRules.Safe(P("5H"), new[] { 3, 3, 4, 4 }));
        Assert.True(FreeCellRules.Safe(P("5H"), new[] { 4, 4, 4, 4 }));
        Assert.True(FreeCellRules.Safe(P("2C"), new[] { 0, 1, 0, 0 }));
        Assert.True(FreeCellRules.Safe(P("AD"), new[] { 0, 0, 0, 0 }));
    }

    [Fact]
    public void OneClickGoesHomeThenOntoACascadeThenIntoAFreeCell()
    {
        var r = Fc(10, new[] { "KS QH", "KH", "KD QC", "KC", "QS JH", "QD JS", "JD JC" });
        Assert.Equal(new Move(Spot.Tableau(0), 1, Spot.Tableau(3)), r.Best(Spot.Tableau(0), 1)); // Q♥ onto K♣
        Assert.Equal(new Move(Spot.Tableau(4), 2, Spot.Tableau(1)), r.Best(Spot.Tableau(4), 2)); // the run onto K♥
        Assert.Equal(new Move(Spot.Tableau(6), 1, Spot.Foundation(1)), r.Best(Spot.Tableau(6), 1)); // J♣ home before Q♥
        var fc = Fc(12, new[] { "KS", "KH", "KD", "KC" });
        Assert.Equal(new Move(Spot.Tableau(0), 1, Spot.Foundation(0)), fc.Best(Spot.Tableau(0), 1));
        var cell = Fc(10, new[] { "KS JC QH", "KH JS QC", "KD JH QS", "KC JD QD" });
        Assert.Equal(Zone.Cell, cell.Best(Spot.Tableau(0), 1)!.Value.To.Zone); // nowhere else to go
    }

    [Fact]
    public void WhenEveryCascadeRunsDownTheRestGoesHome()
    {
        var r = Fc(10, new[] { "KS QH JC", "KH QC JH", "KD QS", "KC QD JS", "JD" });
        Assert.True(r.CanFinish);
        int guard = 0;
        while (!r.Won && guard++ < 60) Assert.True(r.Autoplay(r.NextHome()!.Value));
        Assert.True(r.Won);
        Assert.False(Fc(10, new[] { "KS QH JC", "KH QC JH", "KD QS", "KC JS QD", "JD" }).CanFinish); // Q♦ sits on J♠
    }

    [Fact]
    public void FreeCellUndoCountsAsAMove()
    {
        var r = new FreeCellRules(1);
        var m = Enumerable.Range(0, 8).Select(c => r.Best(Spot.Tableau(c), 1)).First(b => b != null)!.Value;
        Assert.True(r.Apply(m));
        Assert.True(r.Undo());
        Assert.Equal(2, r.Moves);
        Assert.Equal(new FreeCellRules(1).Cards(Spot.Tableau(3)), r.Cards(Spot.Tableau(3)));
    }

    /// <summary>
    /// The simple player (<see cref="FreeCellSolver"/>) plays Microsoft deals 1 to 100, each solution is replayed move by
    /// move through the rules, and most deals come out.
    /// </summary>
    [Fact]
    public void TheSimplePlayerSolvesMostFreeCellDeals()
    {
        int solved = 0, moves = 0;
        var failed = new List<int>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int deal = 1; deal <= 100; deal++)
        {
            var r = new FreeCellRules(deal);
            var solution = FreeCellSolver.Solve(r);
            if (solution == null)
            {
                failed.Add(deal);
                continue;
            }
            FreeCellSolver.Settle(r);
            foreach (var m in solution)
            {
                Assert.True(r.Apply(m), $"deal {deal}: {m}");
                FreeCellSolver.Settle(r);
            }
            Assert.True(r.Won, $"deal {deal}");
            solved++;
            moves += r.Moves;
        }
        _out.WriteLine($"FreeCell deals 1-100: {solved} solved ({moves / Math.Max(1, solved)} moves on average), in {clock.Elapsed.TotalSeconds:0.0} s; not solved: {string.Join(", ", failed)}");
        Assert.InRange(solved, 85, 100);
    }

    [Fact]
    public void Deal11982IsTheOneThatCannotBeWonButStillPlays()
    {
        var r = new FreeCellRules(11982);
        Assert.Equal(52, Enumerable.Range(0, 8).Sum(c => r.Cards(Spot.Tableau(c)).Count));
        Assert.Null(FreeCellSolver.Solve(r, 3000)); // nobody has solved it; the player gives up quickly
    }

    // ------------------------------------------------------------------ Spider

    static int Sp(int copy, int rank) => copy * 13 + rank - 1;

    static SpiderRules Spider(int suits, int[][] columns, int[]? hidden = null, int[]? stock = null) =>
        new(suits, Enumerable.Range(0, SpiderRules.Columns).Select(c => (IReadOnlyList<int>)(c < columns.Length ? columns[c] : Array.Empty<int>())).ToList(),
            hidden ?? new int[SpiderRules.Columns], stock ?? Array.Empty<int>());

    [Fact]
    public void SpiderDealsFiftyFourCardsAndKeepsFiftyForFiveRows()
    {
        var r = new SpiderRules(2, new Random(4));
        for (int c = 0; c < SpiderRules.Columns; c++)
        {
            Assert.Equal(c < 4 ? 6 : 5, r.Cards(Spot.Tableau(c)).Count);
            Assert.Equal(c < 4 ? 5 : 4, r.Hidden(Spot.Tableau(c)));
        }
        Assert.Equal(50, r.Stock.Count);
        Assert.Equal(5, r.DealsLeft);
        Assert.Equal(104, Enumerable.Range(0, 10).SelectMany(c => r.Cards(Spot.Tableau(c))).Concat(r.Stock).Distinct().Count());
    }

    [Theory]
    [InlineData(1, 104, 0, 0, 0)]
    [InlineData(2, 52, 0, 0, 52)]
    [InlineData(4, 26, 26, 26, 26)]
    public void TheSuitsFollowTheGameChosen(int suits, int spades, int clubs, int diamonds, int hearts)
    {
        var counts = new int[4];
        for (int card = 0; card < SpiderRules.DeckSize; card++) counts[SpiderRules.SuitOf(suits, card)]++;
        Assert.Equal(new[] { spades, clubs, diamonds, hearts }, counts);
        // every suit has eight of each rank between its copies
        Assert.All(Enumerable.Range(1, 13), rank => Assert.Equal(8, Enumerable.Range(0, SpiderRules.DeckSize).Count(c => SpiderRules.RankOf(c) == rank)));
    }

    [Fact]
    public void AnyCardGoesOnOneHigherButOnlyARunOfOneSuitMoves()
    {
        // two suits: even copies are spades, odd copies hearts
        var r = Spider(2, new[]
        {
            new[] { Sp(0, 13) },               // K♠
            new[] { Sp(1, 12) },               // Q♥
            new[] { Sp(2, 12), Sp(1, 11) },    // Q♠ J♥: not one suit
            new[] { Sp(0, 9), Sp(2, 8), Sp(4, 7) }, // 9♠ 8♠ 7♠
            new[] { Sp(1, 9) },                // 9♥
        });
        Assert.True(r.IsLegal(new Move(Spot.Tableau(1), 1, Spot.Tableau(0)))); // hearts on spades is fine
        Assert.Equal(1, r.Movable(Spot.Tableau(2)));
        Assert.False(r.IsLegal(new Move(Spot.Tableau(2), 2, Spot.Tableau(0))));
        Assert.Equal(3, r.Movable(Spot.Tableau(3)));
        Assert.True(r.IsLegal(new Move(Spot.Tableau(3), 3, Spot.Tableau(5)))); // into an empty column
        Assert.False(r.IsLegal(new Move(Spot.Tableau(3), 3, Spot.Tableau(4)))); // a 9 on a 9
        Assert.True(r.IsLegal(new Move(Spot.Tableau(3), 2, Spot.Tableau(4))));  // 8♠ 7♠ on 9♥
    }

    [Fact]
    public void ARowIsDealtOnlyWhenNoColumnIsEmpty()
    {
        var stock = Enumerable.Range(20, 10).ToArray();
        var cols = Enumerable.Range(0, SpiderRules.Columns).Select(c => c == 9 ? Array.Empty<int>() : new[] { c }).ToArray();
        var r = Spider(1, cols, stock: stock);
        Assert.False(r.CanDeal);
        Assert.False(r.Draw());
        Assert.True(r.Apply(new Move(Spot.Tableau(1), 1, Spot.Tableau(9))));
        Assert.False(r.Draw()); // column 1 is empty now
        Assert.True(r.Undo());
        cols[9] = new[] { 9 };
        r = Spider(1, cols, stock: stock);
        Assert.True(r.Draw());
        Assert.Empty(r.Stock);
        for (int c = 0; c < SpiderRules.Columns; c++)
        {
            Assert.Equal(2, r.Cards(Spot.Tableau(c)).Count);
            Assert.Equal(stock[^(c + 1)], r.Cards(Spot.Tableau(c))[^1]); // column 0 gets the top card
        }
        Assert.Equal(1, r.Moves);
    }

    [Fact]
    public void AWholeSuitLeavesTheTableAndTurnsUpTheCardBelow()
    {
        var kingToTwo = Enumerable.Range(2, 12).Reverse().Select(rank => Sp(0, rank)).ToArray();
        var r = Spider(1, new[] { new[] { Sp(3, 5) }.Concat(kingToTwo).ToArray(), new[] { Sp(0, 1) } }, hidden: new[] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        Assert.Equal(12, r.Movable(Spot.Tableau(0)));
        Assert.True(r.Apply(new Move(Spot.Tableau(1), 1, Spot.Tableau(0))));
        Assert.Equal(1, r.SuitsDone);
        Assert.Equal(13, r.Home);
        Assert.Equal(new[] { Sp(3, 5) }, r.Cards(Spot.Tableau(0)));
        Assert.Equal(0, r.Hidden(Spot.Tableau(0)));             // the card below turned up
        Assert.Equal(13, SpiderRules.RankOf(r.Cards(Spot.Foundation(0))[^1])); // the king shows on the pile
        Assert.True(r.Undo());
        Assert.Equal(0, r.SuitsDone);
        Assert.Equal(1, r.Hidden(Spot.Tableau(0)));
    }

    [Fact]
    public void SpiderOneClickPrefersTheSameSuit()
    {
        var r = Spider(2, new[]
        {
            new[] { Sp(0, 8) },            // 8♠
            new[] { Sp(1, 9) },            // 9♥
            new[] { Sp(2, 9) },            // 9♠
            Array.Empty<int>(),
            new[] { Sp(3, 5), Sp(1, 8) },  // 5♥ 8♥
        });
        Assert.Equal(new Move(Spot.Tableau(0), 1, Spot.Tableau(2)), r.Best(Spot.Tableau(0), 1)); // past the 9♥, onto the 9♠
        Assert.Equal(new Move(Spot.Tableau(4), 1, Spot.Tableau(1)), r.Best(Spot.Tableau(4), 1)); // 8♥ onto 9♥
        Assert.Null(r.Best(Spot.Tableau(1), 2));
    }

    /// <summary>The simple player (<see cref="SpiderSolver"/>) plays one-suit deals; each win is replayed through the rules.</summary>
    [Fact]
    public void TheSimplePlayerSolvesSomeOneSuitSpiderDeals()
    {
        int solved = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        const int Deals = 12;
        for (int seed = 1; seed <= Deals; seed++)
        {
            var r = new SpiderRules(1, new Random(seed));
            var solution = SpiderSolver.Solve(r);
            if (solution == null) continue;
            foreach (var m in solution) Assert.True(m is { } move ? r.Apply(move) : r.Draw(), $"seed {seed}");
            Assert.True(r.Won, $"seed {seed}");
            solved++;
        }
        _out.WriteLine($"one-suit Spider: {solved} of {Deals} deals solved in {clock.Elapsed.TotalSeconds:0.0} s");
        Assert.InRange(solved, Deals / 2, Deals);
    }

    // ------------------------------------------------------------------ the table

    [Theory]
    [InlineData(0, 52, 0)]
    [InlineData(30, 52, 30)]
    [InlineData(52, 52, 52)]
    [InlineData(13, 104, 6)]
    [InlineData(52, 104, 26)]
    [InlineData(104, 104, 52)]
    public void EveryDealRacesOutOf52(int home, int total, int expected) => Assert.Equal(expected, Patience.RaceScore(home, total));

    [Fact]
    public void DealsFanOutInOrderInEveryGame()
    {
        foreach (var kind in Enum.GetValues<PatienceKind>())
        {
            int columns = Patience.Columns(kind);
            var orders = new HashSet<int>();
            for (int pile = 0; pile < columns; pile++)
                for (int pos = 0; pos < 6; pos++)
                    if (kind != PatienceKind.Klondike || pos <= pile) Assert.True(orders.Add(Patience.DealOrder(kind, pile, pos)));
        }
        Assert.Equal(SolitaireGame.DealIndex(3, 2), Patience.DealOrder(PatienceKind.Klondike, 3, 2));
        Assert.Equal(9, Patience.DealOrder(PatienceKind.FreeCell, 1, 1));
        Assert.Equal(12, Patience.DealOrder(PatienceKind.Spider2, 2, 1));
    }
}
