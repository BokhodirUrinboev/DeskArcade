using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using DeskArcade.Games;
using Xunit;
using Xunit.Abstractions;
using static DeskArcade.Games.PokerHand;

namespace DeskArcade.Tests;

public class PokerHandTests
{
    [Theory]
    [InlineData("As Ks Qs Js Ts", Category.StraightFlush, "Royal flush")]
    [InlineData("9h 8h 7h 6h 5h", Category.StraightFlush, "Straight flush, 9 high")]
    [InlineData("5d 4d 3d 2d Ad", Category.StraightFlush, "Straight flush, 5 high")]
    [InlineData("Qc Qd Qh Qs 2c", Category.Quads, "Four of a kind, queens")]
    [InlineData("Kc Kd Kh 7s 7c", Category.FullHouse, "Full house, kings over sevens")]
    [InlineData("Ah 9h 7h 4h 2h", Category.Flush, "Flush, A high")]
    [InlineData("Ts 9d 8c 7h 6s", Category.Straight, "Straight, 10 high")]
    [InlineData("5c 4d 3h 2s Ac", Category.Straight, "Straight, 5 high")]
    [InlineData("Ac Kd Qh Js Tc", Category.Straight, "Straight, A high")]
    [InlineData("8s 8d 8h Kc 2d", Category.Trips, "Three of a kind, eights")]
    [InlineData("Jc Jd 4s 4h Ac", Category.TwoPair, "Two pair, jacks and fours")]
    [InlineData("Kh Kd 9c 7s 2d", Category.Pair, "Pair of kings")]
    [InlineData("Ah Jd 9c 7s 2d", Category.HighCard, "High card, ace")]
    [InlineData("Qs Kd Ah 2c 3d", Category.HighCard, "High card, ace")] // no wrapping round the ace
    // seven cards: the best five of them
    [InlineData("Ac Ad Kc Kd Qc Qd 2s", Category.TwoPair, "Two pair, aces and kings")]
    [InlineData("7c 7d 7h 5c 5d 5h 2s", Category.FullHouse, "Full house, sevens over fives")]
    [InlineData("2h 3h 4s 5h 6d 9h Kh", Category.Flush, "Flush, K high")]
    [InlineData("5h 6h 7h 8h 9h Ah Kh", Category.StraightFlush, "Straight flush, 9 high")]
    [InlineData("2c 3d 4h 5s 6c 7d 8h", Category.Straight, "Straight, 8 high")]
    [InlineData("Ac 2d 3h 4s 5c 5d Kh", Category.Straight, "Straight, 5 high")]
    [InlineData("9c 9d 9h 9s Kd Kc Kh", Category.Quads, "Four of a kind, nines")]
    public void RanksKnownHands(string cards, Category category, string name)
    {
        int value = Evaluate(ParseMany(cards));
        Assert.Equal(category, CategoryOf(value));
        Assert.Equal(name, Describe(value));
    }

    [Theory]
    [InlineData("Kh Kd Ac 7s 2d", "Ks Kc Qh 7d 2c")]    // the kicker decides a pair
    [InlineData("Jc Jd 4s 4h Ac", "Jh Js 4c 4d Kc")]    // and two pair
    [InlineData("Jc Jd 5s 5h 2c", "Jh Js 4c 4d Ac")]    // the second pair before the kicker
    [InlineData("6c 5d 4h 3s 2c", "5c 4d 3h 2s Ac")]    // the wheel is the lowest straight
    [InlineData("Ah Qh 7h 4h 2h", "As Js 9s 8s 6s")]    // flushes compare card by card
    [InlineData("3c 3d 3h 2s 2c", "2d 2h 2s As Ac")]    // a full house by its three
    [InlineData("Ac 3c 9c 9d 9h 9s 2d", "Kc Qd 9c 9d 9h 9s 2d")] // quads on the board: the kicker
    [InlineData("8c 8d 8h 2c 2d", "As Ks Qs Js 9d")]    // every category beats the one below...
    [InlineData("2c 3c 4c 5c 7c", "Ac Kd Qh Js Th")]
    [InlineData("As Ah Kc Kd 2c", "Ac Ad Qc Qd Kh")]
    [InlineData("Ah Kd Qc Js 9h 9s 3c", "Ah Kd Qc Js 8h 8s 7c")]
    public void BetterHandsScoreHigher(string better, string worse) =>
        Assert.True(Evaluate(ParseMany(better)) > Evaluate(ParseMany(worse)), $"{better} should beat {worse}");

    [Theory]
    [InlineData("Ah Kd Qc Js 9h", "Ad Kc Qh Jd 9c")]
    [InlineData("As Ks Qs Js Ts 2c 3d", "As Ks Qs Js Ts 4h 5h")] // the board plays for both
    [InlineData("Kh Kd 7c 7s 2d 2c Ah", "Kc Ks 7d 7h 3d 3c Ad")]  // two pair, the third pair doesn't count
    public void EqualHandsTie(string a, string b) => Assert.Equal(Evaluate(ParseMany(a)), Evaluate(ParseMany(b)));

    /// <summary>Every five-card hand there is, counted by category: the numbers every poker book prints.</summary>
    [Fact]
    public void CountsEveryFiveCardHand()
    {
        var counts = new int[9];
        Span<int> five = stackalloc int[5];
        for (int a = 0; a < 52; a++)
            for (int b = a + 1; b < 52; b++)
                for (int c = b + 1; c < 52; c++)
                    for (int d = c + 1; d < 52; d++)
                        for (int e = d + 1; e < 52; e++)
                        {
                            five[0] = a; five[1] = b; five[2] = c; five[3] = d; five[4] = e;
                            counts[(int)CategoryOf(Evaluate(five))]++;
                        }
        Assert.Equal(new[] { 1302540, 1098240, 123552, 54912, 10200, 5108, 3744, 624, 40 }, counts);
    }

    /// <summary>Seven cards: the value is the best of the 21 five-card hands in them, and <see cref="PokerHand.BestFive"/> finds one.</summary>
    [Fact]
    public void SevenCardsScoreTheirBestFive()
    {
        var rng = new Random(7);
        for (int n = 0; n < 4000; n++)
        {
            var cards = Enumerable.Range(0, 52).OrderBy(_ => rng.Next()).Take(7).ToArray();
            int best = 0;
            for (int skip1 = 0; skip1 < 7; skip1++)
                for (int skip2 = skip1 + 1; skip2 < 7; skip2++)
                    best = Math.Max(best, Evaluate(cards.Where((_, i) => i != skip1 && i != skip2).ToArray()));
            Assert.Equal(best, Evaluate(cards));
            var five = BestFive(cards);
            Assert.Equal(5, five.Distinct().Count());
            Assert.All(five, c => Assert.Contains(c, cards));
            Assert.Equal(best, Evaluate(five));
        }
    }

    [Fact]
    public void BestFiveReadsInOrder()
    {
        var five = BestFive(ParseMany("Kc 7d Kh 2s 7c Ad 3h"));
        Assert.Equal(ParseMany("Kc Kh 7d 7c Ad").OrderBy(c => c), five.OrderBy(c => c));
        Assert.Equal(13, Rank(five[0]));
        Assert.Equal(14, Rank(five[4])); // the kicker last
        var wheel = BestFive(ParseMany("Ac 2d 3h 4s 5c Kd Qh"));
        Assert.Equal(5, Rank(wheel[0]));
        Assert.Equal(14, Rank(wheel[4])); // the ace plays low
    }

    [Fact]
    public void PreflopRanksPairsOfAcesFirstAndSevenTwoAtTheBottom()
    {
        Assert.True(PreflopPercentile(Parse("As"), Parse("Ah")) < 0.01);
        Assert.True(PreflopPercentile(Parse("As"), Parse("Ks")) < 0.05);
        Assert.InRange(PreflopPercentile(Parse("7s"), Parse("2h")), 0.97, 1.0);
        Assert.True(PreflopPercentile(Parse("Js"), Parse("Ts")) < PreflopPercentile(Parse("Jd"), Parse("Tc")));
        Assert.Equal(PreflopPercentile(Parse("Qs"), Parse("9s")), PreflopPercentile(Parse("9h"), Parse("Qh")));
    }

    [Fact]
    public void EquityMatchesKnownOdds()
    {
        var rng = new Random(3);
        // aces against one random hand win about 85 % of the time; seven-two about 35 %
        Assert.InRange(Equity(ParseMany("As Ah"), Array.Empty<int>(), 1, rng, 6000), 0.82, 0.88);
        Assert.InRange(Equity(ParseMany("7s 2h"), Array.Empty<int>(), 1, rng, 6000), 0.31, 0.38);
        // the nuts on the river always wins
        Assert.Equal(1.0, Equity(ParseMany("As Ks"), ParseMany("Qs Js Ts 2d 3c"), 3, rng, 500));
        // against several opponents, aces win less often
        Assert.InRange(Equity(ParseMany("As Ah"), Array.Empty<int>(), 5, rng, 4000), 0.45, 0.55);
    }
}

public class PokerRulesTests
{
    readonly ITestOutputHelper _out;

    public PokerRulesTests(ITestOutputHelper output) => _out = output;

    /// <summary>A deck that deals these hole cards seat by seat, then this board.</summary>
    static Func<int, IReadOnlyList<int>> Deck(params string[] holesThenBoard) =>
        _ => holesThenBoard.SelectMany(ParseMany).ToList();

    static void Must(PokerRules r, int seat, string kind, int amount = 0)
    {
        Assert.Equal(seat, r.Turn);
        Assert.True(r.Act(seat, kind, amount), $"seat {seat} {kind} {amount} refused");
        Assert.Equal(r.Total, r.ChipsInPlay);
    }

    static void RunOut(PokerRules r)
    {
        while (r.Step()) Assert.Equal(r.Total, r.ChipsInPlay);
    }

    [Fact]
    public void HeadsUpTheButtonPostsTheSmallBlindAndActsFirstBeforeTheFlop()
    {
        var r = new PokerRules(2, new Random(1));
        Assert.Equal(0, r.Dealer);
        Assert.Equal(0, r.SmallBlindSeat);
        Assert.Equal(1, r.BigBlindSeat);
        Assert.Equal(new[] { 990, 980 }, r.Stacks);
        Assert.Equal(0, r.Turn);
        Must(r, 0, "call");
        Assert.Equal(1, r.Turn); // the big blind's option
        Assert.True(r.CanRaise(1));
        Must(r, 1, "check");
        Assert.Equal(PokerRules.Street.Flop, r.Phase);
        Assert.Equal(3, r.Board.Count);
        Assert.Equal(1, r.Turn); // after the flop the big blind acts first, the button last
        Assert.True(r.InPosition(0));
        Must(r, 1, "check");
        Must(r, 0, "check");
        Assert.Equal(PokerRules.Street.Turn, r.Phase);
        Must(r, 1, "raise", 40);
        Must(r, 0, "fold");
        Assert.True(r.HandOver);
        Assert.Equal(new[] { 980, 1020 }, r.Stacks);
        Assert.True(r.NextHand());
        Assert.Equal(1, r.Dealer); // the button moves, and with it the blinds
        Assert.Equal(1, r.SmallBlindSeat);
        Assert.Equal(1, r.Turn);
    }

    [Fact]
    public void WithFourTheBlindsSitLeftOfTheButtonAndTheNextSeatActsFirst()
    {
        var r = new PokerRules(4, new Random(2), firstDealer: 1);
        Assert.Equal(1, r.Dealer);
        Assert.Equal(2, r.SmallBlindSeat);
        Assert.Equal(3, r.BigBlindSeat);
        Assert.Equal(0, r.Turn); // left of the big blind
        Must(r, 0, "call");
        Must(r, 1, "call");
        Must(r, 2, "call");
        Must(r, 3, "check");
        Assert.Equal(PokerRules.Street.Flop, r.Phase);
        Assert.Equal(2, r.Turn); // the first live seat left of the button
        Must(r, 2, "fold");
        Must(r, 3, "check");
        Must(r, 0, "check");
        Must(r, 1, "check");
        Assert.Equal(3, r.Turn); // the small blind folded: the big blind starts the turn
        Assert.Equal(80, r.Pot);
    }

    [Fact]
    public void ARaiseIsAtLeastTheLastRaise()
    {
        var r = new PokerRules(3, new Random(3)); // button 0, blinds 1 and 2, seat 0 first
        Assert.False(r.Act(0, "raise", 30));  // 10 more than the big blind of 20
        Assert.False(r.Act(0, "check"));      // there is a bet to call
        Assert.Equal(40, r.MinRaiseTo(0));
        Must(r, 0, "raise", 100);             // a raise of 80
        Assert.Equal(180, r.MinRaiseTo(1));
        Assert.False(r.Act(1, "raise", 150));
        Must(r, 1, "raise", 180);
        Assert.Equal(80, r.MinRaise);
        Assert.False(r.Act(2, "raise", 1021)); // more than the stack
        Must(r, 2, "allin");
        Assert.True(r.AllIn[2]);
        Assert.Equal(1000, r.CurrentBet);
    }

    [Fact]
    public void AShortAllInDoesNotReopenTheBettingForThoseWhoActed()
    {
        // seat 0 raises; the small blind is all in for a little more; the big blind calls; seat 0 may only call or fold
        var r = new PokerRules(3, new Random(4), stacks: new[] { 1000, 130, 1000 });
        Must(r, 0, "raise", 100);
        Must(r, 1, "allin");                  // 130: 30 more, less than a full raise of 80
        Assert.Equal(130, r.CurrentBet);
        Assert.Equal(80, r.MinRaise);
        Assert.True(r.CanRaise(2));           // the big blind hasn't acted yet: it may raise
        Must(r, 2, "call");
        Assert.Equal(0, r.Turn);
        Assert.False(r.CanRaise(0));
        Assert.False(r.Act(0, "raise", 300));
        Must(r, 0, "call");
        Assert.Equal(PokerRules.Street.Flop, r.Phase);
    }

    [Fact]
    public void ThreeAllInsWithDifferentStacksMakeSidePots()
    {
        // A (100) has the best hand, B (300) the second, C (500) the worst
        var r = new PokerRules(3, new Random(5), stacks: new[] { 100, 300, 500 },
            deckFor: Deck("Ah Ad", "Kh Kd", "Qh Qd", "2c 7d 9h Js 3s"));
        Must(r, 0, "allin");
        Must(r, 1, "allin");
        Must(r, 2, "allin");
        Assert.True(r.RunningOut);
        Assert.True(r.Revealed);
        RunOut(r);
        Assert.True(r.HandOver);
        Assert.Equal(3, r.Results.Count);
        Assert.Equal(300, r.Results[0].Amount);
        Assert.Equal(new[] { 0, 1, 2 }, r.Results[0].Eligible);
        Assert.Equal(new[] { 0 }, r.Results[0].Winners);
        Assert.Equal(400, r.Results[1].Amount);
        Assert.Equal(new[] { 1, 2 }, r.Results[1].Eligible);
        Assert.Equal(new[] { 1 }, r.Results[1].Winners);
        Assert.Equal(200, r.Results[2].Amount);
        Assert.True(r.Results[2].Returned); // nobody could call C's last 200
        Assert.Equal(new[] { 300, 400, 200 }, r.Stacks);
        Assert.Equal(900, r.ChipsInPlay);
    }

    [Fact]
    public void TheBiggestStackWinningTakesEveryPot()
    {
        var r = new PokerRules(3, new Random(6), stacks: new[] { 100, 300, 500 },
            deckFor: Deck("Qh Qd", "Kh Kd", "Ah Ad", "2c 7d 9h Js 3s"));
        Must(r, 0, "allin");
        Must(r, 1, "allin");
        Must(r, 2, "call");                  // 300 covers B; C still has 200 behind
        Assert.False(r.AllIn[2]);
        RunOut(r);
        Assert.Equal(new[] { 0, 0, 900 }, r.Stacks);
        Assert.True(r.Over);                 // one player has every chip
        Assert.Equal(1, r.Places[2]);
        Assert.Equal(2, r.Places[1]);        // B started the hand with more than A
        Assert.Equal(3, r.Places[0]);
    }

    [Fact]
    public void ASidePotCanGoToAnotherPlayerThanTheMainPot()
    {
        var r = new PokerRules(4, new Random(7), stacks: new[] { 200, 1000, 1000, 1000 }, firstDealer: 3,
            deckFor: Deck("Ah Ad", "Kh Kd", "Qh Qd", "3c 4d", "2c 7d 9h Js 5s"));
        // button 3: blinds 0 and 1, seat 2 first
        Must(r, 2, "raise", 200);
        Must(r, 3, "fold");
        Must(r, 0, "allin");                 // 200 all in
        Must(r, 1, "call");
        Assert.Equal(PokerRules.Street.Flop, r.Phase);
        Must(r, 1, "raise", 300);
        Must(r, 2, "call");
        Must(r, 1, "check");
        Must(r, 2, "check");
        Must(r, 1, "check");
        Must(r, 2, "check");
        Assert.True(r.HandOver);
        Assert.Equal(2, r.Results.Count);
        Assert.Equal(600, r.Results[0].Amount);   // 3 × 200
        Assert.Equal(new[] { 0 }, r.Results[0].Winners);
        Assert.Equal(600, r.Results[1].Amount);   // 2 × 300
        Assert.Equal(new[] { 1 }, r.Results[1].Winners);
        Assert.Equal(new[] { 600, 1100, 500, 1000 }, r.Stacks);
    }

    [Fact]
    public void EqualHandsSplitThePotAndTheOddChipGoesLeftOfTheButton()
    {
        // both straights to the 9; the big blind has a pair of twos
        var r = new PokerRules(3, new Random(8), deckFor: Deck("9c 3d", "9d 3c", "2c 2d", "5c 6d 7h 8s Kd"));
        Must(r, 0, "raise", 41);
        Must(r, 1, "call");
        Must(r, 2, "call");
        for (int street = 0; street < 3; street++)
        {
            Must(r, 1, "check");
            Must(r, 2, "check");
            Must(r, 0, "check");
        }
        Assert.True(r.HandOver);
        var pot = Assert.Single(r.Results);
        Assert.Equal(123, pot.Amount);
        Assert.Equal(new[] { 1, 0 }, pot.Winners); // seat 1 first: it sits left of the button
        Assert.Equal(new[] { 1020, 1021, 959 }, r.Stacks);
    }

    [Fact]
    public void TheLastBettorShowsFirstAndALoserAfterABetterHandMucks()
    {
        var r = new PokerRules(3, new Random(9), deckFor: Deck("Ah Ad", "Kh Kd", "2c 3d", "5c 6d 9h Js Qd"));
        Must(r, 0, "call");
        Must(r, 1, "call");
        Must(r, 2, "check");
        for (int street = 0; street < 2; street++)
        {
            Must(r, 1, "check");
            Must(r, 2, "check");
            Must(r, 0, "check");
        }
        Must(r, 1, "raise", 20); // B bets the river
        Must(r, 2, "call");
        Must(r, 0, "call");
        Assert.Equal(new[] { 1, 2, 0 }, r.ShowOrder);
        Assert.True(r.Shown[1]);   // the bettor shows first
        Assert.False(r.Shown[2]);  // can't beat the kings: mucks
        Assert.True(r.Shown[0]);   // the aces win
    }

    [Fact]
    public void EveryoneFoldingGivesTheBlindsToTheBigBlind()
    {
        var r = new PokerRules(3, new Random(10));
        Must(r, 0, "fold");
        Must(r, 1, "fold");
        Assert.True(r.HandOver);
        Assert.False(r.Showdown);
        Assert.Equal(new[] { 1000, 990, 1010 }, r.Stacks);
    }

    [Fact]
    public void AShortBigBlindIsAllInAndTheBoardRunsOut()
    {
        var r = new PokerRules(2, new Random(11), stacks: new[] { 1000, 5 });
        Assert.True(r.AllIn[1]);
        Assert.Equal(-1, r.Turn);         // the small blind already covers the 5: nothing to decide
        Assert.True(r.RunningOut);
        RunOut(r);
        Assert.True(r.HandOver);
        Assert.Equal(1005, r.ChipsInPlay);
    }

    [Fact]
    public void BlindsRiseEveryFewHandsAndTheButtonSkipsBustedSeats()
    {
        var r = new PokerRules(3, new Random(12), handsPerLevel: 2, stacks: new[] { 1000, 1000, 1000 });
        var bigBlinds = new List<int>();
        for (int hand = 0; hand < 5; hand++)
        {
            bigBlinds.Add(r.BigBlind);
            while (!r.HandOver) Assert.True(r.Act(r.Turn, "fold"));
            r.NextHand();
        }
        Assert.Equal(new[] { 20, 20, 30, 30, 40 }, bigBlinds);

        var s = new PokerRules(3, new Random(13), stacks: new[] { 1000, 10, 1000 }, deckFor: Deck("Ah Ad", "7c 2d", "Kh Kd", "3c 4d 9h Js Qd"));
        Must(s, 0, "call");     // button 0: seat 1 posts its last 10 as the small blind
        Must(s, 2, "check");
        while (!s.HandOver) Assert.True(s.Act(s.Turn, "check") || s.Step());
        Assert.True(s.Out[1]);
        Assert.Equal(3, s.Places[1]);
        s.NextHand();
        Assert.Equal(2, s.Dealer);        // seat 1 is skipped
        Assert.Equal(2, s.SmallBlindSeat); // heads-up now: the button is the small blind
        Assert.Empty(s.Hole[1]);
    }

    [Fact]
    public void AFixedGameEndsAfterItsHandsWithTheBiggestStackFirst()
    {
        var r = new PokerRules(3, new Random(14), handLimit: 2);
        while (!r.HandOver) Assert.True(r.Act(r.Turn, "fold"));
        Assert.False(r.Over);
        r.NextHand();
        while (!r.HandOver) Assert.True(r.Act(r.Turn, "fold"));
        Assert.True(r.Over);
        Assert.False(r.NextHand());
        int leader = Enumerable.Range(0, 3).OrderByDescending(s => r.Stacks[s]).First();
        Assert.Equal(1, r.Places[leader]);
        Assert.All(Enumerable.Range(0, 3), s => Assert.InRange(r.Places[s], 1, 3));
    }

    [Fact]
    public void MovesOutOfTurnOrAfterTheHandAreRefused()
    {
        var r = new PokerRules(3, new Random(15));
        Assert.False(r.Act(1, "call"));
        Assert.False(r.Act(0, "bogus"));
        while (!r.HandOver) r.Act(r.Turn, "fold");
        Assert.False(r.Act(0, "check"));
        Assert.False(r.Step());
    }

    /// <summary>Computer players of every level play whole games: every move legal, and never a chip lost or made.</summary>
    [Fact]
    public void ComputerPlayersFinish300GamesWithoutLosingAChip()
    {
        var rng = new Random(2026);
        int hands = 0;
        for (int game = 0; game < 300; game++)
        {
            int players = 2 + game % 5;
            var levels = Enumerable.Range(0, players).Select(_ => 1 + rng.Next(4)).ToArray();
            var r = new PokerRules(players, new Random(game), handLimit: game % 7 == 0 ? 20 : 0, handsPerLevel: 3);
            int steps = 0;
            while (!r.Over)
            {
                Assert.True(++steps < 20000, $"game {game} never ended");
                if (r.HandOver)
                {
                    Assert.True(r.NextHand());
                    hands++;
                }
                else if (r.RunningOut) Assert.True(r.Step());
                else
                {
                    int seat = r.Turn;
                    var move = PokerAi.Decide(PokerAi.SpotFor(r, seat), levels[seat], rng, iterations: 40);
                    Assert.True(r.Act(seat, move.Kind, move.Amount), $"game {game}: seat {seat} {move} refused");
                }
                Assert.Equal(r.Total, r.ChipsInPlay);
                Assert.All(r.Stacks, s => Assert.True(s >= 0));
            }
            Assert.Equal(r.Total, r.Stacks.Sum());
            Assert.Contains(1, r.Places);
            Assert.All(r.Places, p => Assert.InRange(p, 1, players));
            if (r.HandLimit == 0) Assert.Single(r.Stacks, s => s > 0);
        }
        _out.WriteLine($"300 games, {hands} hands");
    }

    /// <summary>
    /// Each level against the one below, heads-up for 100 big blinds a hand, every deal played twice with the seats
    /// swapped so the cards even out; the result in big blinds won per 100 hands.
    /// </summary>
    [Theory]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 3)]
    public void EachLevelBeatsTheOneBelow(int strong, int weak)
    {
        const int deals = 1500, stack = 2000;
        long won = 0;
        for (int deal = 0; deal < deals; deal++)
        {
            var cards = Enumerable.Range(0, 52).ToArray();
            new Random(deal * 7919).Shuffle(cards);
            for (int strongSeat = 0; strongSeat < 2; strongSeat++)
            {
                var r = new PokerRules(2, new Random(deal), handLimit: 1, stacks: new[] { stack, stack }, firstDealer: deal % 2, deckFor: _ => cards);
                var rng = new Random(deal * 31 + strongSeat);
                while (!r.HandOver)
                {
                    if (r.RunningOut)
                    {
                        r.Step();
                        continue;
                    }
                    int seat = r.Turn;
                    var move = PokerAi.Decide(PokerAi.SpotFor(r, seat), seat == strongSeat ? strong : weak, rng, iterations: 300, millis: 10000);
                    Assert.True(r.Act(seat, move.Kind, move.Amount));
                }
                won += r.Stacks[strongSeat] - stack;
            }
        }
        double bbPer100 = won / 20.0 / (deals * 2) * 100;
        _out.WriteLine($"level {strong} vs {weak}: {bbPer100:+0.0;-0.0} big blinds per 100 hands over {deals * 2} hands");
        Assert.True(bbPer100 > 0, $"level {strong} lost to level {weak}: {bbPer100:0.0} bb/100");
    }

    [Fact]
    public void TheExpertThinksWithinItsTimeBudget()
    {
        var r = new PokerRules(6, new Random(16));
        Assert.True(r.Act(r.Turn, "raise", 60)); // facing a raise the Expert works out its chance against a range
        var spot = PokerAi.SpotFor(r, r.Turn);
        var watch = Stopwatch.StartNew();
        PokerAi.Decide(spot, 4, new Random(1), iterations: 10_000_000, millis: 100);
        watch.Stop();
        _out.WriteLine($"expert thought for {watch.ElapsedMilliseconds} ms");
        Assert.True(watch.ElapsedMilliseconds < 400, $"{watch.ElapsedMilliseconds} ms");
    }
}
