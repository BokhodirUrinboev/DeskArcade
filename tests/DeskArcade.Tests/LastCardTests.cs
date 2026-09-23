using System;
using System.Collections.Generic;
using System.Linq;
using DeskArcade.Games;
using Xunit;
using static DeskArcade.Games.LastCardRules;

namespace DeskArcade.Tests;

public class LastCardTests
{
    const int Red = 0, Yellow = 1, Green = 2, Blue = 3;

    /// <summary>A coloured card: the first copy of <paramref name="kind"/> (or of the number) in <paramref name="color"/>.</summary>
    static int C(int color, int number) => color * 25 + (number == 0 ? 0 : (number - 1) * 2 + 1);
    static int C2(int color, int number) => C(color, number) + 1; // the second copy of 1–9
    static int Skip(int color) => color * 25 + 19;
    static int Reverse(int color) => color * 25 + 21;
    static int DrawTwo(int color) => color * 25 + 23;
    const int WildCard = 100, WildFour = 104;

    /// <summary>
    /// A game whose hands are exactly <paramref name="hands"/> (seven cards each), with <paramref name="start"/>
    /// on the discard and <paramref name="pileTop"/> on top of the draw pile (first = drawn first).
    /// </summary>
    static LastCardRules Game(int[][] hands, int start, params int[] pileTop)
    {
        int players = hands.Length;
        var used = hands.SelectMany(h => h).Append(start).Concat(pileTop).ToList();
        Assert.Equal(used.Count, used.Distinct().Count());
        // everything left over goes under the pile, with no number cards so the start card stays the only one
        var rest = Enumerable.Range(0, DeckSize).Except(used).OrderBy(c => KindOf(c) == Kind.Number ? 1 : 0).ToList();
        var deck = new List<int>();
        for (int round = 0; round < HandSize; round++)
            for (int p = 0; p < players; p++)
                deck.Add(hands[p][round]);
        // after the hands, the list runs from the pile's top down: the start card (the first number from the top
        // starts the discard), then the cards to draw, then the rest with its numbers deepest
        var pile = new List<int> { start };
        pile.AddRange(pileTop);
        pile.AddRange(rest.Where(c => KindOf(c) != Kind.Number));
        pile.AddRange(rest.Where(c => KindOf(c) == Kind.Number));
        deck.AddRange(pile);
        var g = new LastCardRules(players, new Random(0), deck);
        Assert.Equal(start, g.Top);
        return g;
    }

    static int[] Hand(params int[] cards)
    {
        // pad to seven with cards nobody will play in these tests: blue 9s and yellow 9s, second copies
        var pad = new[] { C2(Blue, 9), C2(Yellow, 9), C2(Blue, 8), C2(Yellow, 8), C2(Blue, 7), C2(Yellow, 7), C2(Blue, 6) };
        return cards.Concat(pad.Except(cards)).Take(HandSize).ToArray();
    }

    static int[] Hand2(params int[] cards)
    {
        var pad = new[] { C2(Green, 9), C2(Red, 9), C2(Green, 8), C2(Red, 8), C2(Green, 7), C2(Red, 7), C2(Green, 6) };
        return cards.Concat(pad.Except(cards)).Take(HandSize).ToArray();
    }

    [Fact]
    public void TheDeckHas108CardsOfTheRightKinds()
    {
        var all = Enumerable.Range(0, DeckSize).ToList();
        Assert.Equal(4, all.Count(c => NumberOf(c) == 0));
        Assert.Equal(8, all.Count(c => NumberOf(c) == 7));
        Assert.Equal(8, all.Count(c => KindOf(c) == Kind.Skip));
        Assert.Equal(8, all.Count(c => KindOf(c) == Kind.Reverse));
        Assert.Equal(8, all.Count(c => KindOf(c) == Kind.DrawTwo));
        Assert.Equal(4, all.Count(c => KindOf(c) == Kind.Wild));
        Assert.Equal(4, all.Count(c => KindOf(c) == Kind.WildDrawFour));
        Assert.Equal(25, all.Count(c => ColorOf(c) == Green));
    }

    [Fact]
    public void DealsSevenEachAndStartsOnANumber()
    {
        for (int seed = 0; seed < 50; seed++)
        {
            var g = new LastCardRules(3, new Random(seed));
            Assert.All(g.Hands, h => Assert.Equal(HandSize, h.Count));
            Assert.Equal(Kind.Number, KindOf(g.Top));
            Assert.Equal(DeckSize, g.Hands.Sum(h => h.Count) + g.DrawPile.Count + g.Discard.Count);
            Assert.Equal(0, g.Turn);
        }
    }

    [Fact]
    public void PlaysOnColourOrNumber()
    {
        var g = Game(new[] { Hand(C(Red, 3), C(Green, 5), C(Yellow, 8)), Hand2() }, C(Red, 5));
        Assert.True(g.CanPlay(0, C(Red, 3)));    // colour
        Assert.True(g.CanPlay(0, C(Green, 5)));  // number
        Assert.False(g.CanPlay(0, C(Yellow, 8)));
        Assert.False(g.CanPlay(1, C2(Red, 9)));  // not their turn
        Assert.True(g.Act(0, "play", C(Green, 5)));
        Assert.Equal(Green, g.Color);
        Assert.Equal(1, g.Turn);
    }

    [Fact]
    public void AWildSetsTheColourItsPlayerPicks()
    {
        var g = Game(new[] { Hand(WildCard), Hand2() }, C(Red, 5));
        Assert.False(g.Act(0, "play", WildCard));        // needs a colour
        Assert.True(g.Act(0, "play", WildCard, Blue));
        Assert.Equal(Blue, g.Color);
        Assert.True(g.Fits(C(Blue, 3)));  // any blue now fits
        Assert.False(g.Fits(C2(Red, 9)));
    }

    [Fact]
    public void AWildDrawFourOnlyWithoutTheColour()
    {
        var g = Game(new[] { Hand(WildFour, C(Red, 2)), Hand2() }, C(Red, 5));
        Assert.False(g.CanPlay(0, WildFour)); // holds a red card
        var h = Game(new[] { Hand(WildFour, C(Green, 2)), Hand2() }, C(Red, 5));
        Assert.True(h.CanPlay(0, WildFour));
    }

    [Fact]
    public void SkipReverseAndDrawCardsInFourPlayers()
    {
        var hands = new[]
        {
            Hand(Skip(Red), Reverse(Red), DrawTwo(Red)),
            Hand2(C(Red, 1)),
            new[] { C2(Red, 2), C(Yellow, 1), C(Yellow, 2), C(Yellow, 3), C(Yellow, 4), C(Yellow, 5), C(Yellow, 6) },
            new[] { C(Red, 4), C(Red, 6), C(Blue, 1), C(Blue, 2), C(Blue, 3), C(Blue, 4), C(Blue, 5) },
        };
        var g = Game(hands, C(Red, 5), C(Green, 1));
        Assert.True(g.Act(0, "play", Skip(Red)));
        Assert.Equal(2, g.Turn); // seat 1 skipped
        Assert.True(g.Act(2, "play", C2(Red, 2)));
        Assert.True(g.Act(3, "play", C(Red, 4)));
        Assert.True(g.Act(0, "play", Reverse(Red)));
        Assert.Equal(-1, g.Direction);
        Assert.Equal(3, g.Turn); // back the other way
        Assert.True(g.Act(3, "play", C(Red, 6)));
        Assert.Equal(2, g.Turn);
        Assert.True(g.Act(2, "draw")); // a green 1 on a red 6: no fit, the turn moves on
        Assert.Equal(1, g.Turn);
        Assert.True(g.Act(1, "play", C(Red, 1)));
        Assert.True(g.Act(0, "play", DrawTwo(Red)));
        Assert.Equal(7, g.Hands[3].Count); // seat 3 had five, drew two...
        Assert.Equal(2, g.Turn);           // ...and lost their turn
    }

    [Fact]
    public void AReverseSkipsWithTwoPlayers()
    {
        var g = Game(new[] { Hand(Reverse(Red), C(Red, 1)), Hand2() }, C(Red, 5));
        Assert.True(g.Act(0, "play", Reverse(Red)));
        Assert.Equal(0, g.Turn);
    }

    [Fact]
    public void DrawingACardThatFitsLetsYouPlayItOrPass()
    {
        var g = Game(new[] { Hand(), Hand2() }, C(Red, 5), C(Red, 1));
        Assert.True(g.Act(0, "draw"));
        Assert.True(g.Drew);
        Assert.Equal(C(Red, 1), g.DrawnCard);
        Assert.False(g.Act(0, "draw")); // one draw a turn
        Assert.True(g.Act(0, "pass"));
        Assert.Equal(1, g.Turn);

        var h = Game(new[] { Hand(C(Red, 7)), Hand2() }, C(Red, 5), C(Red, 1));
        h.Act(0, "draw");
        Assert.False(h.CanPlay(0, C(Red, 7))); // only the drawn card may go now
        Assert.True(h.Act(0, "play", C(Red, 1)));
    }

    [Fact]
    public void DrawingACardThatDoesntFitEndsTheTurn()
    {
        var g = Game(new[] { Hand(), Hand2() }, C(Red, 5), C(Green, 1));
        Assert.True(g.Act(0, "draw"));
        Assert.False(g.Drew);
        Assert.Equal(1, g.Turn);
        Assert.Contains(C(Green, 1), g.Hands[0]);
    }

    [Fact]
    public void ForgettingToCallCostsTwoCards()
    {
        var g = Game(new[] { Hand(C(Red, 1)), Hand2(C(Red, 2)) }, C(Red, 5));
        g.Hands[0].RemoveRange(1, 5); // down to two: red 1 and one pad card
        g.Act(0, "play", C(Red, 1));
        Assert.Single(g.Hands[0]);
        Assert.True(g.Act(1, "play", C(Red, 2))); // the next player moves: caught
        Assert.Equal(3, g.Hands[0].Count);
        Assert.Equal(0, g.Caught);
        Assert.Equal(1, g.Catches);
    }

    [Fact]
    public void CallingInTimeIsSafe()
    {
        var g = Game(new[] { Hand(C(Red, 1)), Hand2(C(Red, 2)) }, C(Red, 5));
        g.Hands[0].RemoveRange(1, 5);
        Assert.True(g.Act(0, "call")); // called with two, before playing
        g.Act(0, "play", C(Red, 1));
        g.Act(1, "play", C(Red, 2));
        Assert.Single(g.Hands[0]);
        Assert.Equal(0, g.Catches);
        Assert.False(g.Act(1, "call")); // seven cards: nothing to call
    }

    [Fact]
    public void PlayingTheLastCardWins()
    {
        var g = Game(new[] { Hand(C(Red, 1)), Hand2() }, C(Red, 5));
        g.Hands[0].RemoveRange(1, 6);
        Assert.True(g.Act(0, "play", C(Red, 1)));
        Assert.True(g.Over);
        Assert.Equal(0, g.Winner);
        Assert.False(g.Act(1, "draw"));
    }

    /// <summary>Computer players play full games against each other: every move is legal, no card is lost, and someone wins.</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ComputerPlayersFinishGames(int players)
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var g = new LastCardRules(players, new Random(seed));
            for (int step = 0; step < 3000 && !g.Over; step++)
            {
                int seat = g.Turn;
                if (g.Hands[seat].Count <= 2) g.Act(seat, "call");
                var a = g.CpuAction(seat);
                Assert.NotNull(a);
                Assert.True(g.Act(seat, a!.Value.Kind, a.Value.Card, a.Value.Color), $"seed {seed}: {a}");
                Assert.Equal(DeckSize, g.Hands.Sum(h => h.Count) + g.DrawPile.Count + g.Discard.Count);
            }
            Assert.True(g.Over, $"seed {seed} never ended");
        }
    }
}
