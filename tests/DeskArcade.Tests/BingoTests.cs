using System;
using System.Collections.Generic;
using System.Linq;
using DeskArcade.Games;
using Xunit;

namespace DeskArcade.Tests;

public class BingoTests
{
    readonly Dictionary<string, long> _values = new();

    long Value(string counter) => _values.TryGetValue(counter, out long v) ? v : 0;

    BingoCard Deal(int seed = 1, bool hasAll = true) => BingoCard.Deal(new Random(seed), Value, _ => hasAll);

    static IEnumerable<int> Open(BingoCard card) => Enumerable.Range(0, BingoCard.Count).Where(i => !card.IsMarked(i));

    [Fact]
    public void ACardIsFourDeskSquaresAndFourGamesAroundAFreeCentre()
    {
        for (int seed = 0; seed < 50; seed++)
        {
            var card = Deal(seed);
            var squares = Enumerable.Range(0, BingoCard.Count).Select(i => card[i]).ToList();
            Assert.Same(BingoCard.FreeSquare, card[BingoCard.Free]);
            Assert.Equal(1 << BingoCard.Free, card.Marks);
            Assert.Equal(BingoCard.Count, squares.Distinct().Count());
            Assert.Equal(BingoCard.DeskSquares, squares.Count(BingoCard.Desk.Contains));
            Assert.Equal(BingoCard.Count - 1 - BingoCard.DeskSquares, squares.Count(BingoCard.Games.Contains));
            var games = squares.Where(s => s.GameId != null).Select(s => s.GameId).ToList();
            Assert.Equal(games.Count, games.Distinct().Count()); // no two squares in the same game
        }
    }

    [Fact]
    public void SquaresThatNeedSomethingOnlyComeToThoseWhoHaveIt()
    {
        for (int seed = 0; seed < 50; seed++)
        {
            var card = Deal(seed, hasAll: false);
            Assert.All(Enumerable.Range(0, BingoCard.Count), i => Assert.Null(card[i].Needs));
        }
    }

    [Fact]
    public void OnlyWhatHappensAfterTheDealCounts()
    {
        foreach (var s in BingoCard.Desk.Concat(BingoCard.Games)) _values[s.Counter] = 100;
        var card = Deal();
        Assert.Empty(card.Mark(Value, new DateTime(2026, 9, 28, 15, 0, 0), playing: false));
        int i = Open(card).First(k => !card[k].IsClock);
        Assert.Equal(100, card.BaseOf(i));
        _values[card[i].Counter] = 100 + card[i].Amount - 1;
        Assert.Empty(card.Mark(Value, new DateTime(2026, 9, 28, 15, 0, 0), false));
        Assert.Equal(card[i].Amount - 1, card.Progress(i, Value));
        _values[card[i].Counter] = 100 + card[i].Amount;
        var ticked = card.Mark(Value, new DateTime(2026, 9, 28, 15, 0, 0), false);
        Assert.Contains(i, ticked);
        Assert.True(card.IsMarked(i));
        Assert.Empty(card.Mark(Value, new DateTime(2026, 9, 28, 15, 0, 0), false)); // once
    }

    [Fact]
    public void ClockSquaresTickWhenYouPlayAtThatTime()
    {
        Assert.True(BingoCard.ClockHolds(BingoCard.Morning, new DateTime(2026, 9, 28, 9, 59, 0)));
        Assert.False(BingoCard.ClockHolds(BingoCard.Morning, new DateTime(2026, 9, 28, 10, 0, 0)));
        Assert.True(BingoCard.ClockHolds(BingoCard.Lunch, new DateTime(2026, 9, 28, 13, 30, 0)));
        Assert.False(BingoCard.ClockHolds(BingoCard.Lunch, new DateTime(2026, 9, 28, 14, 0, 0)));
        Assert.True(BingoCard.ClockHolds(BingoCard.Evening, new DateTime(2026, 9, 28, 18, 0, 0)));
        Assert.False(BingoCard.ClockHolds(BingoCard.Evening, new DateTime(2026, 9, 28, 17, 59, 0)));

        var card = Enumerable.Range(0, 400).Select(s => Deal(s)).First(c => Enumerable.Range(0, BingoCard.Count).Any(i => c[i].Counter == BingoCard.Lunch));
        int lunch = Enumerable.Range(0, BingoCard.Count).First(i => card[i].Counter == BingoCard.Lunch);
        Assert.DoesNotContain(lunch, card.Mark(Value, new DateTime(2026, 9, 28, 12, 30, 0), playing: false)); // nobody at the desk
        Assert.DoesNotContain(lunch, card.Mark(Value, new DateTime(2026, 9, 28, 16, 0, 0), playing: true));
        Assert.Contains(lunch, card.Mark(Value, new DateTime(2026, 9, 28, 12, 30, 0), playing: true));
    }

    [Fact]
    public void ThreeInALineIsABingoOnce()
    {
        var card = Deal();
        Assert.Empty(card.CallLines()); // the free centre alone is no line
        card.Tick(3);
        Assert.Empty(card.CallLines());
        card.Tick(5);
        Assert.Equal(new[] { 1 }, card.CallLines()); // the middle row, through the free centre
        Assert.Empty(card.CallLines());
        Assert.Equal(1, card.Bingos);
        card.Tick(0);
        card.Tick(8);
        Assert.Equal(new[] { 6 }, card.CallLines()); // a diagonal
        Assert.False(card.FullHouse);
        foreach (int i in Open(card).ToList()) card.Tick(i);
        Assert.True(card.FullHouse);
        Assert.Equal(BingoCard.Lines.Length, card.Bingos + card.CallLines().Count);
    }

    [Fact]
    public void TwoSquaresACardCanBeSwappedForOthersOfTheirKind()
    {
        var card = Deal(7);
        int desk = Open(card).First(i => BingoCard.Desk.Contains(card[i]));
        int game = Open(card).First(i => BingoCard.Games.Contains(card[i]));
        var oldDesk = card[desk];
        var oldGame = card[game];
        Assert.False(card.Swap(BingoCard.Free, new Random(1), Value, _ => true));
        Assert.True(card.Swap(desk, new Random(1), Value, _ => true));
        Assert.True(card.Swap(game, new Random(1), Value, _ => true));
        Assert.NotEqual(oldDesk, card[desk]);
        Assert.Contains(card[desk], BingoCard.Desk);
        Assert.NotEqual(oldGame, card[game]);
        Assert.Contains(card[game], BingoCard.Games);
        var squares = Enumerable.Range(0, BingoCard.Count).Select(i => card[i]).ToList();
        Assert.Equal(BingoCard.Count, squares.Distinct().Count());
        Assert.Equal(0, card.SwapsLeft);
        Assert.False(card.Swap(desk, new Random(1), Value, _ => true));

        var fresh = Deal(8);
        fresh.Tick(0);
        Assert.False(fresh.Swap(0, new Random(1), Value, _ => true)); // a dabbed square stays
    }

    [Fact]
    public void ACardSurvivesARestart()
    {
        _values["race.cpuwins"] = 12;
        var card = Deal(3);
        card.Tick(2);
        card.Swap(Open(card).First(), new Random(2), Value, _ => true);
        card.Tick(6);
        var back = BingoCard.Load(card.Save());
        Assert.NotNull(back);
        Assert.Equal(card.Save(), back!.Save());
        Assert.Equal(card.Marks, back.Marks);
        Assert.Equal(card.SwapsLeft, back.SwapsLeft);
        Assert.All(Enumerable.Range(0, BingoCard.Count), i =>
        {
            Assert.Equal(card[i], back[i]);
            Assert.Equal(card.BaseOf(i), back.BaseOf(i));
        });
        Assert.Null(BingoCard.Load(null));
        Assert.Null(BingoCard.Load("garbage"));
        Assert.Null(BingoCard.Load(card.Save().Replace(card[0].Id + "=", "gone=")));
    }

    [Fact]
    public void GameSquaresAreAThirdOfTheDailyChallenge()
    {
        Assert.Equal(2, BingoCard.Smaller(2));
        Assert.Equal(3, BingoCard.Smaller(8));
        Assert.Equal(5, BingoCard.Smaller(15));
        Assert.Equal(15, BingoCard.Smaller(40));
        Assert.Equal(200, BingoCard.Smaller(600));
        Assert.DoesNotContain(BingoCard.Games, s => s.GameId == "bingo");
        Assert.All(BingoCard.Games, s => Assert.Contains(Daily.Pool, c => c.Counter == s.Counter && c.Text == s.Text));
    }
}
