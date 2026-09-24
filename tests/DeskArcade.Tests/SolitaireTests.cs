using System;
using System.Collections.Generic;
using System.Linq;
using DeskArcade.Games;
using Xunit;
using static DeskArcade.Games.SolitaireRules;

namespace DeskArcade.Tests;

public class SolitaireTests
{
    /// <summary>
    /// A deck laid out so the tableau tops are known: the 28 tableau cards come first (dealt row by row), then
    /// the stock with its top card last.
    /// </summary>
    static SolitaireRules Deal(int[] tops, params int[] stockTopFirst)
    {
        // tableau row by row: row r deals to piles r..6; the last card a pile gets is its top
        var rest = new Queue<int>(Enumerable.Range(0, DeckSize).Except(tops).Except(stockTopFirst));
        var deck = new List<int>();
        for (int row = 0; row < Piles; row++)
            for (int pile = row; pile < Piles; pile++)
                deck.Add(row == pile ? tops[pile] : rest.Dequeue());
        var stock = rest.ToList();
        stock.AddRange(stockTopFirst.Reverse());
        deck.AddRange(stock);
        return new SolitaireRules(deck);
    }

    static int C(int suit, int rank) => Card(suit, rank);
    const int Spades = 0, Clubs = 1, Diamonds = 2, Hearts = 3;

    [Fact]
    public void DealsSevenPilesAndTheStock()
    {
        var r = new SolitaireRules(new Random(1));
        for (int p = 0; p < Piles; p++)
        {
            Assert.Equal(p + 1, r.Tableau(p).Count);
            Assert.Equal(p, r.Hidden(p));
        }
        Assert.Equal(24, r.Stock.Count);
        Assert.Empty(r.Waste);
        Assert.Equal(DeckSize, Enumerable.Range(0, Piles).SelectMany(r.Tableau).Concat(r.Stock).Distinct().Count());
    }

    [Fact]
    public void TheStockTurnsOverAndTakesTheWasteBack()
    {
        var r = new SolitaireRules(new Random(2));
        int top = r.Stock[^1];
        Assert.True(r.Draw());
        Assert.Equal(top, r.Waste[^1]);
        for (int i = 0; i < 23; i++) r.Draw();
        Assert.Empty(r.Stock);
        var waste = r.Waste.ToList();
        Assert.True(r.Draw()); // back into the stock, in the same order as before
        Assert.Empty(r.Waste);
        Assert.Equal(top, r.Stock[^1]);
        Assert.Equal(waste.AsEnumerable().Reverse(), r.Stock);
        Assert.Equal(25, r.Moves);
    }

    [Fact]
    public void TableauBuildsDownInAlternatingColours()
    {
        var r = Deal(new[] { C(Hearts, 8), C(Spades, 7), C(Clubs, 7), C(Diamonds, 7), C(Spades, 9), C(Hearts, 6), C(Clubs, 13) });
        Assert.True(r.IsLegal(new Move(Spot.Tableau(1), 1, Spot.Tableau(0))));  // black 7 on red 8
        Assert.False(r.IsLegal(new Move(Spot.Tableau(3), 1, Spot.Tableau(0)))); // red on red
        Assert.False(r.IsLegal(new Move(Spot.Tableau(5), 1, Spot.Tableau(0)))); // 6 on 8
        Assert.True(r.Apply(new Move(Spot.Tableau(1), 1, Spot.Tableau(0))));
        Assert.Equal(0, r.Hidden(1)); // the card underneath turned up
        Assert.True(r.Apply(new Move(Spot.Tableau(5), 1, Spot.Tableau(0)))); // red 6 on black 7
        Assert.Equal(3, r.Movable(Spot.Tableau(0)));
        Assert.True(r.IsLegal(new Move(Spot.Tableau(0), 3, Spot.Tableau(4)))); // the whole run 8-7-6 onto the black 9
        Assert.False(r.IsLegal(new Move(Spot.Tableau(0), 2, Spot.Tableau(4)))); // 7-6 doesn't fit a 9
    }

    [Fact]
    public void OnlyAKingGoesIntoAnEmptyPile()
    {
        var r = Deal(new[] { C(Hearts, 12), C(Spades, 13), C(Clubs, 2), C(Diamonds, 7), C(Spades, 9), C(Hearts, 6), C(Clubs, 5) });
        Assert.True(r.Apply(new Move(Spot.Tableau(0), 1, Spot.Tableau(1)))); // red queen on black king; pile 0 is empty
        Assert.Empty(r.Tableau(0));
        Assert.False(r.IsLegal(new Move(Spot.Tableau(3), 1, Spot.Tableau(0))));
        Assert.True(r.IsLegal(new Move(Spot.Tableau(1), 2, Spot.Tableau(0)))); // the run headed by the king
    }

    [Fact]
    public void FoundationsBuildUpBySuitFromTheAce()
    {
        var r = Deal(new[] { C(Hearts, 1), C(Hearts, 2), C(Hearts, 3), C(Spades, 2), C(Spades, 9), C(Hearts, 6), C(Clubs, 5) });
        Assert.False(r.IsLegal(new Move(Spot.Tableau(1), 1, Spot.Foundation(Hearts)))); // a 2 before the ace
        Assert.True(r.Apply(new Move(Spot.Tableau(0), 1, Spot.Foundation(Hearts))));
        Assert.False(r.IsLegal(new Move(Spot.Tableau(3), 1, Spot.Foundation(Hearts)))); // wrong suit
        Assert.True(r.Apply(new Move(Spot.Tableau(1), 1, Spot.Foundation(Hearts))));
        Assert.True(r.Apply(new Move(Spot.Tableau(2), 1, Spot.Foundation(Hearts))));
        Assert.Equal(3, r.OnFoundations);
    }

    [Fact]
    public void ACardComesBackDownFromItsFoundation()
    {
        var r = Deal(new[] { C(Hearts, 1), C(Hearts, 2), C(Spades, 3), C(Diamonds, 7), C(Spades, 9), C(Hearts, 6), C(Clubs, 5) });
        r.Apply(new Move(Spot.Tableau(0), 1, Spot.Foundation(Hearts)));
        r.Apply(new Move(Spot.Tableau(1), 1, Spot.Foundation(Hearts)));
        Assert.True(r.Apply(new Move(Spot.Foundation(Hearts), 1, Spot.Tableau(2)))); // red 2 on black 3
        Assert.Equal(C(Hearts, 2), r.Tableau(2)[^1]);
        Assert.Equal(1, r.OnFoundations);
    }

    [Fact]
    public void OneClickSendsACardHomeFirst()
    {
        var r = Deal(new[] { C(Hearts, 1), C(Hearts, 2), C(Spades, 3), C(Diamonds, 7), C(Spades, 9), C(Hearts, 6), C(Clubs, 5) });
        Assert.Equal(new Move(Spot.Tableau(0), 1, Spot.Foundation(Hearts)), r.Best(Spot.Tableau(0), 1));
        r.Apply(r.Best(Spot.Tableau(0), 1)!.Value);
        Assert.Equal(new Move(Spot.Tableau(1), 1, Spot.Foundation(Hearts)), r.Best(Spot.Tableau(1), 1));
        Assert.Null(r.Best(Spot.Tableau(3), 1)); // a red 7 with no black 8 anywhere
    }

    [Fact]
    public void OneClickMovesAKingIntoAnEmptyPile()
    {
        var r = Deal(new[] { C(Hearts, 12), C(Spades, 13), C(Clubs, 7), C(Diamonds, 9), C(Spades, 9), C(Hearts, 6), C(Clubs, 13) });
        r.Apply(new Move(Spot.Tableau(0), 1, Spot.Tableau(1))); // red queen on black king empties pile 0
        Assert.Equal(new Move(Spot.Tableau(6), 1, Spot.Tableau(0)), r.Best(Spot.Tableau(6), 1));
        Assert.Equal(new Move(Spot.Tableau(1), 2, Spot.Tableau(0)), r.Best(Spot.Tableau(1), 2)); // a king with its run
    }

    [Fact]
    public void UndoPutsEverythingBack()
    {
        var r = Deal(new[] { C(Hearts, 8), C(Spades, 7), C(Clubs, 7), C(Diamonds, 7), C(Spades, 9), C(Hearts, 6), C(Clubs, 13) });
        var before = Enumerable.Range(0, Piles).Select(p => r.Tableau(p).ToArray()).ToArray();
        r.Apply(new Move(Spot.Tableau(1), 1, Spot.Tableau(0)));
        r.Draw();
        Assert.True(r.Undo());
        Assert.True(r.Undo());
        Assert.False(r.CanUndo);
        for (int p = 0; p < Piles; p++) Assert.Equal(before[p], r.Tableau(p));
        Assert.Equal(1, r.Hidden(1)); // face down again
        Assert.Empty(r.Waste);
        Assert.Equal(4, r.Moves); // undoing never lowers the count
    }

    [Fact]
    public void IllegalMovesChangeNothing()
    {
        var r = new SolitaireRules(new Random(3));
        Assert.False(r.Apply(new Move(Spot.Tableau(6), 7, Spot.Tableau(0)))); // face-down cards don't move
        Assert.False(r.Apply(new Move(Spot.Waste, 1, Spot.Tableau(0))));        // the waste is empty
        Assert.False(r.Apply(new Move(Spot.Tableau(0), 1, Spot.Tableau(0))));
        Assert.Equal(0, r.Moves);
        Assert.False(r.CanUndo);
    }

    [Fact]
    public void RejectsABadDeck()
    {
        Assert.Throws<ArgumentException>(() => new SolitaireRules(Enumerable.Range(0, 51).ToArray()));
        Assert.Throws<ArgumentException>(() => new SolitaireRules(Enumerable.Repeat(0, 52).ToArray()));
    }

    /// <summary>
    /// A simple player (home first, then moves that turn a card up, then the waste, then draw) plays many deals
    /// to the end: every step is legal, no card is lost or copied, and some deals are solved, with the rest
    /// sent home by <see cref="SolitaireRules.NextHome"/> once everything is face up.
    /// </summary>
    [Fact]
    public void ASimplePlayerSolvesSomeDealsWithoutBreakingTheRules()
    {
        int solved = 0;
        for (int seed = 0; seed < 300; seed++)
        {
            var r = new SolitaireRules(new Random(seed));
            int idle = 0;
            while (!r.Won && idle <= (r.Stock.Count + r.Waste.Count + 1) * 2)
            {
                if (r.CanFinish) { Assert.True(r.Apply(r.NextHome()!.Value)); continue; }
                var m = Next(r);
                if (m is { } move) { Assert.True(r.Apply(move)); idle = 0; }
                else { r.Draw(); idle++; }
                int cards = r.Stock.Count + r.Waste.Count + r.OnFoundations + Enumerable.Range(0, Piles).Sum(p => r.Tableau(p).Count);
                Assert.Equal(DeckSize, cards);
            }
            if (r.Won) solved++;
        }
        Assert.InRange(solved, 10, 300);
    }

    static Move? Next(SolitaireRules r)
    {
        if (r.NextHome() is { } home) return home;
        for (int p = 0; p < Piles; p++)
        {
            int n = r.Movable(Spot.Tableau(p));
            if (n > 0 && r.Hidden(p) > 0 && r.Best(Spot.Tableau(p), n) is { To.Zone: Zone.Tableau } m) return m;
        }
        if (r.Waste.Count > 0 && r.Best(Spot.Waste, 1) is { } w) return w;
        return null;
    }
}
