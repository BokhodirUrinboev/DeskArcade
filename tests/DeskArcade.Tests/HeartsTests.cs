using System;
using System.Collections.Generic;
using System.Linq;
using DeskArcade.Games;
using Xunit;
using Xunit.Abstractions;
using static DeskArcade.Games.HeartsRules;

namespace DeskArcade.Tests;

public class HeartsTests
{
    readonly ITestOutputHelper _out;

    public HeartsTests(ITestOutputHelper output) => _out = output;

    static int C(string card) => PokerHand.Parse(card);

    /// <summary>A deck that deals these four hands (thirteen cards each) to seats 0–3.</summary>
    static Func<int, IReadOnlyList<int>> Deal(params string[] hands)
    {
        var cards = hands.SelectMany(PokerHand.ParseMany).ToList();
        Assert.Equal(52, cards.Distinct().Count());
        return _ => cards;
    }

    // seat 1 has no clubs but diamonds; seat 3 no clubs, ten hearts and three high spades
    static readonly Func<int, IReadOnlyList<int>> Mixed = Deal(
        "2c 3c 4c 5c 2d 3d 4d 5d 2s 3s 4s 5s 2h",
        "Ah Kh Qs 6d 7d 8d 9d Td Jd Qd Kd Ad 6s",
        "6c 7c 8c 9c Tc Jc Qc Kc Ac 7s 8s 9s Ts",
        "3h 4h 5h 6h 7h 8h 9h Th Jh Qh Js Ks As");

    // one suit each, but seat 2 has the ace of hearts and seat 3 the queen of spades: seat 0 takes every trick
    static readonly Func<int, IReadOnlyList<int>> Suits = Deal(
        "2c 3c 4c 5c 6c 7c 8c 9c Tc Jc Qc Kc Ac",
        "2d 3d 4d 5d 6d 7d 8d 9d Td Jd Qd Kd Ad",
        "2s 3s 4s 5s 6s 7s 8s 9s Ts Js Ks As Ah",
        "2h 3h 4h 5h 6h 7h 8h 9h Th Jh Qh Kh Qs");

    static void Play(HeartsRules r, int seat, string card)
    {
        Assert.Equal(seat, r.Turn);
        Assert.True(r.Act(seat, "play", C(card)), $"seat {seat} couldn't play {card}");
    }

    [Fact]
    public void DealsThirteenEachAndPassesLeftRightAcrossThenKeeps()
    {
        var r = new HeartsRules(new Random(1));
        Assert.All(r.Hands, h => Assert.Equal(HandSize, h.Count));
        Assert.Equal(52, r.Hands.SelectMany(h => h).Distinct().Count());
        var seen = new List<(PassDirection, int)>();
        for (int hand = 1; hand <= 5; hand++)
        {
            var h = new HeartsRules(new Random(hand), firstHand: hand);
            seen.Add((h.Pass, h.PassTarget(0)));
            Assert.Equal(h.Pass == PassDirection.Keep ? Stage.Playing : Stage.Passing, h.Phase);
        }
        Assert.Equal(new[] { (PassDirection.Left, 1), (PassDirection.Right, 3), (PassDirection.Across, 2), (PassDirection.Keep, 0), (PassDirection.Left, 1) }, seen);
    }

    [Fact]
    public void PassingSwapsThreeCardsEachAtOnce()
    {
        var r = new HeartsRules(new Random(2), deckFor: Mixed); // hand 1: to the left
        var passes = new[] { "Ac Kc Qc", "Ah Kh Qs", "Ac Kc Qc", "As Ks Js" };
        Assert.False(r.Act(0, "pass", Pack(PokerHand.ParseMany("2c 3c 3c")))); // three different cards
        Assert.False(r.Act(0, "pass", Pack(PokerHand.ParseMany("Ah Kh Qs")))); // not seat 0's
        Assert.True(r.Act(0, "pass", Pack(PokerHand.ParseMany("2c 2d 2s"))));
        Assert.False(r.Act(0, "pass", Pack(PokerHand.ParseMany("3c 3d 3s")))); // once a hand
        Assert.True(r.Act(1, "pass", Pack(PokerHand.ParseMany(passes[1]))));
        Assert.True(r.Act(2, "pass", Pack(PokerHand.ParseMany("Ac Kc Qc"))));
        Assert.Equal(Stage.Passing, r.Phase); // waiting for the last one
        Assert.Contains(C("Ah"), r.Hands[1]);
        Assert.True(r.Act(3, "pass", Pack(PokerHand.ParseMany(passes[3]))));
        Assert.Equal(Stage.Playing, r.Phase);
        Assert.All(r.Hands, h => Assert.Equal(HandSize, h.Count));
        Assert.Equal(PokerHand.ParseMany("2c 2d 2s"), r.Received[1]);
        Assert.Equal(PokerHand.ParseMany("Ah Kh Qs"), r.Received[2]);
        Assert.Equal(PokerHand.ParseMany("As Ks Js"), r.Received[0]);
        Assert.Contains(C("Qs"), r.Hands[2]);
        Assert.DoesNotContain(C("Qs"), r.Hands[1]);
        Assert.Equal(1, r.Turn); // seat 1 got the two of clubs, so it leads
    }

    [Fact]
    public void TheTwoOfClubsLeadsAndSuitMustBeFollowed()
    {
        var r = new HeartsRules(new Random(3), deckFor: Mixed, firstHand: 4);
        Assert.Equal(Stage.Playing, r.Phase);
        Assert.Equal(0, r.Turn);
        Assert.False(r.Act(0, "play", C("3c")));
        Play(r, 0, "2c");
        Play(r, 1, "6d");                    // no clubs: anything but points on the first trick
        Assert.False(r.Act(2, "play", C("7s"))); // seat 2 has clubs and must follow
        Play(r, 2, "Ac");
        Play(r, 3, "Js");
        Assert.Equal(2, r.LastWinner);         // the highest club
        Assert.Equal(2, r.Turn);               // the taker leads
        Assert.Equal(1, r.TrickNumber);
        Assert.All(r.Taken, t => Assert.Equal(0, t));
    }

    [Fact]
    public void NoPointsOnTheFirstTrickUnlessThereIsNothingElse()
    {
        var r = new HeartsRules(new Random(4), deckFor: Mixed, firstHand: 4);
        Play(r, 0, "2c");
        Assert.False(r.CanPlay(1, C("Ah")));
        Assert.False(r.CanPlay(1, C("Qs")));
        Assert.True(r.CanPlay(1, C("6s")));

        var s = new HeartsRules(new Random(5), deckFor: Suits, firstHand: 4);
        Play(s, 0, "2c");
        Play(s, 1, "2d");
        Assert.False(s.CanPlay(2, C("Ah")));  // seat 2 still has spades to throw
        Play(s, 2, "2s");
        Assert.True(s.CanPlay(3, C("Qs")));   // seat 3 holds nothing but points
        Assert.True(s.CanPlay(3, C("Kh")));
        Play(s, 3, "Kh");
        Assert.True(s.HeartsBroken);
        Assert.Equal(1, s.Taken[0]);
    }

    [Fact]
    public void HeartsCantBeLedUntilBrokenUnlessThatIsAllThereIs()
    {
        var r = new HeartsRules(new Random(6), deckFor: Mixed, firstHand: 4);
        Play(r, 0, "2c");
        Play(r, 1, "6d");
        Play(r, 2, "Ac");
        Play(r, 3, "Js");
        Play(r, 2, "7s");
        Play(r, 3, "Ks");
        Play(r, 0, "2s");
        Play(r, 1, "Qs");                      // the queen, on the second trick: allowed
        Assert.Equal(3, r.LastWinner);
        Assert.Equal(13, r.Taken[3]);
        Assert.False(r.HeartsBroken);          // the queen doesn't break hearts
        Assert.False(r.CanPlay(3, C("3h")));   // seat 3 still has the ace of spades
        Play(r, 3, "As");
        Play(r, 0, "3s");
        Play(r, 1, "6s");
        Play(r, 2, "8s");
        Assert.Equal(3, r.Turn);
        Assert.True(r.CanPlay(3, C("3h")));    // nothing but hearts left
        Play(r, 3, "3h");
        Assert.True(r.HeartsBroken);
    }

    [Fact]
    public void TakingEveryPointShootsTheMoon()
    {
        var r = new HeartsRules(new Random(7), deckFor: Suits, firstHand: 4);
        var rng = new Random(1);
        while (r.Phase == Stage.Playing)
        {
            var legal = r.LegalCards(r.Turn);
            Assert.True(r.Act(r.Turn, "play", legal[rng.Next(legal.Count)]));
        }
        Assert.Equal(26, r.Taken[0]);
        Assert.Equal(0, r.Moon);
        Assert.Equal(new[] { 0, 26, 26, 26 }, r.HandPoints);
        Assert.Equal(new[] { 0, 26, 26, 26 }, r.Scores);
        Assert.False(r.Over);
    }

    [Fact]
    public void ScoresAHeartAPointAndTheQueenThirteen()
    {
        var r = new HeartsRules(new Random(8), deckFor: Mixed, firstHand: 4);
        var rng = new Random(2);
        while (r.Phase == Stage.Playing)
        {
            var legal = r.LegalCards(r.Turn);
            r.Act(r.Turn, "play", legal[rng.Next(legal.Count)]);
        }
        Assert.Equal(26, r.Taken.Sum());
        var points = new int[4];
        foreach (var trick in r.History.GroupBy(h => h.TrickNo))
        {
            int led = Suit(trick.First().Card);
            int winner = trick.Where(p => Suit(p.Card) == led).OrderByDescending(p => Rank(p.Card)).First().Seat;
            points[winner] += trick.Sum(p => PointsOf(p.Card));
        }
        Assert.Equal(points, r.Taken);
        Assert.Equal(r.Moon < 0 ? r.Taken : r.HandPoints, r.HandPoints);
    }

    [Fact]
    public void TheGameEndsAtItsTargetAndTheLowestScoreWins()
    {
        var r = new HeartsRules(new Random(9), gameTo: 26, deckFor: Suits, firstHand: 4);
        var rng = new Random(3);
        while (r.Phase == Stage.Playing)
        {
            var legal = r.LegalCards(r.Turn);
            r.Act(r.Turn, "play", legal[rng.Next(legal.Count)]);
        }
        Assert.True(r.Over);
        Assert.Equal(new[] { 0 }, r.Leaders);
        Assert.Equal(1, r.PlaceOf(0));
        Assert.Equal(2, r.PlaceOf(1));
        Assert.False(r.NextHand());
        Assert.False(r.Act(0, "play", C("2c")));
    }

    /// <summary>Computer players of every level play 300 games to 100: every move legal, every card played, 26 points a hand.</summary>
    [Fact]
    public void ComputerPlayersFinish300GamesTo100()
    {
        var rng = new Random(2026);
        int hands = 0, moons = 0;
        for (int game = 0; game < 300; game++)
        {
            var levels = Enumerable.Range(0, 4).Select(_ => 1 + rng.Next(4)).ToArray();
            var r = new HeartsRules(new Random(game));
            while (!r.Over)
            {
                if (r.Phase == Stage.Passing)
                {
                    for (int s = 0; s < Players; s++)
                    {
                        var pass = HeartsAi.ChoosePass(r, s, levels[s], rng);
                        Assert.True(r.Act(s, "pass", Pack(pass)), $"game {game}: seat {s} can't pass {string.Join(",", pass)}");
                    }
                }
                while (r.Phase == Stage.Playing)
                {
                    int seat = r.Turn, card = HeartsAi.ChoosePlay(r, seat, levels[seat], rng);
                    Assert.True(r.Act(seat, "play", card), $"game {game}: seat {seat} can't play {card}");
                    Assert.Equal(52, r.Hands.Sum(h => h.Count) + r.History.Count);
                }
                Assert.Equal(Stage.HandOver, r.Phase);
                Assert.Equal(52, r.History.Count);
                Assert.Equal(26, r.Taken.Sum());
                Assert.Equal(r.Moon < 0 ? 26 : 78, r.HandPoints.Sum());
                if (r.Moon >= 0) moons++;
                hands++;
                if (!r.Over) Assert.True(r.NextHand());
                Assert.True(hands < 300 * 40, "a game never ended");
            }
            Assert.True(r.Scores.Max() >= 100);
            Assert.NotEmpty(r.Leaders);
        }
        _out.WriteLine($"300 games, {hands} hands, {moons} moons");
    }

    /// <summary>Two players of each level at one table, the seats swapped half the time: the stronger level takes fewer points.</summary>
    [Theory]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 3)]
    public void EachLevelBeatsTheOneBelow(int strong, int weak)
    {
        const int games = 300;
        long strongPoints = 0, weakPoints = 0, strongWins = 0, weakWins = 0, hands = 0;
        for (int game = 0; game < games; game++)
        {
            var levels = game % 2 == 0 ? new[] { strong, weak, strong, weak } : new[] { weak, strong, weak, strong };
            var rng = new Random(game * 13 + 5);
            var r = new HeartsRules(new Random(game));
            while (!r.Over)
            {
                if (r.Phase == Stage.Passing)
                    for (int s = 0; s < Players; s++) r.Act(s, "pass", Pack(HeartsAi.ChoosePass(r, s, levels[s], rng)));
                while (r.Phase == Stage.Playing)
                {
                    int seat = r.Turn;
                    Assert.True(r.Act(seat, "play", HeartsAi.ChoosePlay(r, seat, levels[seat], rng)));
                }
                hands++;
                for (int s = 0; s < Players; s++)
                    if (levels[s] == strong) strongPoints += r.HandPoints[s];
                    else weakPoints += r.HandPoints[s];
                r.NextHand();
            }
            foreach (int w in r.Leaders)
                if (levels[w] == strong) strongWins++;
                else weakWins++;
        }
        double strongAvg = strongPoints / 2.0 / hands, weakAvg = weakPoints / 2.0 / hands;
        _out.WriteLine($"level {strong} vs {weak}: {strongAvg:0.00} vs {weakAvg:0.00} points a hand over {hands} hands; games won {strongWins} to {weakWins}");
        Assert.True(strongAvg < weakAvg, $"level {strong} took {strongAvg:0.00} a hand, level {weak} {weakAvg:0.00}");
        Assert.True(strongWins > weakWins);
    }
}
