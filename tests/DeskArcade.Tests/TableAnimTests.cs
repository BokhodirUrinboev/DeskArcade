using System;
using System.Collections.Generic;
using System.Linq;
using DeskArcade.Engine;
using DeskArcade.Games;
using Xunit;

namespace DeskArcade.Tests;

/// <summary>The UI-free parts of the card tables: how a deal is timed, how a hand fans, and whom the scoreboard's chip names.</summary>
public class TableAnimTests
{
    [Fact]
    public void ADealGoesRoundTheTableOneCardAtATime()
    {
        var plan = CardTable.DealPlan(3, 6);
        Assert.Equal(18, plan.Count);
        Assert.Equal((0, 0, 0.0), plan[0]);
        Assert.Equal((1, 0, CardTable.DealGap), plan[1]);
        Assert.Equal((2, 0, 2 * CardTable.DealGap), plan[2]);
        Assert.Equal((0, 1, 3 * CardTable.DealGap), plan[3]); // the second round starts once everyone has one
        // one card leaves the deck every gap, and the last one goes out after all the others
        for (int i = 1; i < plan.Count; i++) Assert.Equal(CardTable.DealGap, plan[i].Delay - plan[i - 1].Delay, 9);
        Assert.Equal(17 * CardTable.DealGap, plan[^1].Delay, 9);
        Assert.Equal(plan[^1].Delay, CardTable.DealDelay(2, 5, 3), 9);
        // each seat gets its cards in order
        foreach (var seat in plan.GroupBy(p => p.Seat))
            Assert.Equal(Enumerable.Range(0, 6), seat.Select(p => p.Index));
        Assert.Equal(0.18, CardTable.DealSeconds, 9);
        Assert.Equal(0.05, CardTable.DealGap, 9);
    }

    [Fact]
    public void DealDelayInterleavesTheSeats()
    {
        // with four players, a seat's next card comes four gaps after its last
        Assert.Equal(4 * CardTable.DealGap, CardTable.DealDelay(1, 1, 4) - CardTable.DealDelay(1, 0, 4), 9);
        Assert.True(CardTable.DealDelay(3, 0, 4) < CardTable.DealDelay(0, 1, 4));
        Assert.Equal(0, CardTable.DealDelay(0, 0, 0), 9); // no players: nothing to wait for, nothing to divide by
    }

    [Fact]
    public void AHandFansOutCentredAndOverlapsWhenWide()
    {
        var xs = CardTable.Fan(3, 60, 600, 400);
        Assert.Equal(new[] { 304.0, 370.0, 436.0 }, xs); // 6 apart, and the row's middle is at 400
        Assert.Equal(400, (xs[0] + xs[^1] + 60) / 2, 9);

        var many = CardTable.Fan(20, 60, 600, 400);
        Assert.Equal(20, many.Length);
        Assert.Equal(100, many[0], 9);   // the row fills the width allowed...
        Assert.Equal(640, many[^1], 9);  // ...and no more
        double step = many[1] - many[0];
        Assert.True(step < 60, "the cards overlap when there are many");
        for (int i = 1; i < many.Length; i++) Assert.Equal(step, many[i] - many[i - 1], 9);

        Assert.Empty(CardTable.Fan(0, 60, 600, 400));
        Assert.Equal(new[] { 370.0 }, CardTable.Fan(1, 60, 600, 400));
    }

    [Fact]
    public void TheHandRefansWhenACardLeaves()
    {
        var before = CardTable.Fan(6, 60, 600, 400);
        var after = CardTable.Fan(5, 60, 600, 400);
        // the row stays centred and loses one step, so every card that stays slides half a step inwards
        Assert.Equal(400, (after[0] + after[^1] + 60) / 2, 9);
        for (int i = 0; i < after.Length; i++) Assert.Equal(before[i] + 33, after[i], 9);

        // a crowded hand spreads out a little as it empties
        var crowded = CardTable.Fan(20, 60, 600, 400);
        var lessCrowded = CardTable.Fan(19, 60, 600, 400);
        Assert.True(lessCrowded[1] - lessCrowded[0] > crowded[1] - crowded[0]);
        Assert.Equal(crowded[0], lessCrowded[0], 9);
    }

    [Fact]
    public void NextSeatGoesRoundEitherWayAndSkipsPlayersWhoAreOut()
    {
        Assert.Equal(1, CardTable.NextSeat(0, 4));
        Assert.Equal(0, CardTable.NextSeat(3, 4));
        Assert.Equal(3, CardTable.NextSeat(0, 4, -1)); // after a Reverse
        Assert.Equal(1, CardTable.NextSeat(2, 4, -1));
        Assert.Equal(1, CardTable.NextSeat(0, 2));
        Assert.Equal(0, CardTable.NextSeat(1, 2, -1));
        Assert.Equal(2, CardTable.NextSeat(0, 4, 1, new[] { false, true, false, false }));
        Assert.Equal(3, CardTable.NextSeat(0, 4, -1, new[] { false, true, false, false }));
        Assert.Equal(0, CardTable.NextSeat(0, 3, 1, new[] { false, true, true })); // nobody else is in
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void TheChipNamesTheComputerOrWhoseTurnItIs(int seats)
    {
        var names = Enumerable.Range(0, seats).Select(i => "Player " + i).ToArray();

        // against computer players, the chip is the computer, whatever the seats are called
        var solo = CardTable.Chip(names, solo: true, turnSeat: 1, nextSeat: 1, myTurn: false);
        Assert.NotNull(solo);
        Assert.True(solo!.IsCpu);
        Assert.Equal(0, solo.Level);
        Assert.False(solo.MyTurn);
        Assert.True(CardTable.Chip(names, true, 0, 1, true)!.MyTurn);

        // in a room, someone else's turn shows their name
        for (int turn = 1; turn < seats; turn++)
        {
            var chip = CardTable.Chip(names, solo: false, turnSeat: turn, nextSeat: 1, myTurn: false);
            Assert.NotNull(chip);
            Assert.Equal(names[turn], chip!.Name);
            Assert.False(chip.IsCpu);
            Assert.False(chip.MyTurn);
        }

        // my turn shows who comes next
        int next = CardTable.NextSeat(0, seats);
        var mine = CardTable.Chip(names, solo: false, turnSeat: 0, nextSeat: next, myTurn: true);
        Assert.Equal(names[next], mine!.Name);
        Assert.Equal(1, next);
        Assert.True(mine.MyTurn);

        // with a Reverse in play the next player is the other way round
        int back = CardTable.NextSeat(0, seats, -1);
        Assert.Equal(names[back], CardTable.Chip(names, false, 0, back, true)!.Name);
        Assert.Equal(seats - 1, back);

        // over: the chip stays but has no turn
        Assert.Null(CardTable.Chip(names, false, -1, next, null)!.MyTurn);
        Assert.Equal(names[next], CardTable.Chip(names, false, -1, next, null)!.Name);
    }

    [Fact]
    public void TheChipIsNothingWithoutAGame()
    {
        Assert.Null(CardTable.Chip(Array.Empty<string>(), false, 0, 0, true));
        Assert.Null(CardTable.Chip(new[] { "Me", "You" }, false, -1, -1, false));
    }

    static DurakView Durak(int players, int seat, int attacker, int defender) => new()
    {
        Seat = seat,
        Names = Enumerable.Range(0, players).Select(i => "P" + i).ToArray(),
        Cpu = new bool[players],
        Counts = Enumerable.Repeat(6, players).ToArray(),
        Done = new bool[players],
        Out = new bool[players],
        Attacker = attacker,
        Defender = defender,
        Limit = 5,
    };

    [Fact]
    public void ADurakViewKnowsWhoTheBoutWaitsOn()
    {
        var v = Durak(3, seat: 0, attacker: 1, defender: 2);
        Assert.Equal(1, v.TurnSeat()); // the attacker leads
        Assert.False(v.MyTurn);

        v.Table.Add(new[] { 3, -1 });
        Assert.Equal(2, v.TurnSeat()); // the defender must answer
        Assert.False(v.MyTurn);
        Assert.False(v.Awaits(0));     // nothing to throw in on an unbeaten card

        v.Table[0][1] = 5;             // beaten: the attackers may throw in, the attacker first...
        Assert.Equal(1, v.TurnSeat());
        Assert.True(v.Awaits(0));      // ...but so may I, so it is my turn too
        Assert.True(v.MyTurn);

        v.Done[1] = true;              // the attacker is done: the bout waits on me alone
        Assert.Equal(0, v.TurnSeat());
        v.Done[0] = true;
        Assert.Equal(2, v.TurnSeat()); // everyone is done: it ends on the defender
        Assert.False(v.MyTurn);

        v.Over = true;
        Assert.Equal(-1, v.TurnSeat());
        Assert.False(v.MyTurn);
    }

    [Fact]
    public void ADurakDefenderIsOnTurnUntilTheCardsAreBeatenOrTaken()
    {
        var v = Durak(4, seat: 2, attacker: 1, defender: 2);
        v.Table.Add(new[] { 3, -1 });
        Assert.True(v.MyTurn);
        v.Taking = true;
        Assert.False(v.MyTurn);        // taking: the others may still throw in
        Assert.Equal(1, v.TurnSeat());
        v.Done[1] = v.Done[3] = true;
        Assert.Equal(0, v.TurnSeat()); // seat 0 has not said done yet
        v.Out[0] = true;
        Assert.Equal(2, v.TurnSeat()); // seat 0 is out: the bout ends on the defender
    }

    [Fact]
    public void ADurakChipFollowsTheBoutRoundARoom()
    {
        var v = Durak(4, seat: 0, attacker: 1, defender: 2);
        Opponent? Chip() => CardTable.Chip(v.Names, false, v.TurnSeat(), CardTable.NextSeat(v.Seat, v.Players, 1, v.Out), v.Over ? null : v.MyTurn);

        Assert.Equal("P1", Chip()!.Name); // P1's attack
        Assert.False(Chip()!.MyTurn);
        v.Table.Add(new[] { 3, 5 });
        Assert.True(Chip()!.MyTurn);      // I may throw in: the chip names who is next round the table
        Assert.Equal("P1", Chip()!.Name);
        v.Out[1] = true;
        Assert.Equal("P2", Chip()!.Name); // P1 is out, so P2 sits next
        v.Over = true;
        Assert.Null(Chip()!.MyTurn);
    }

    [Fact]
    public void ALastCardChipFollowsTheTurnAndTheDirection()
    {
        var v = new LastCardView { Seat = 0, Names = new[] { "Me", "Ann", "Bob" }, Turn = 1 };
        Opponent? Chip() => CardTable.Chip(v.Names, false, v.Over ? -1 : v.Turn, CardTable.NextSeat(v.Seat, v.Players, v.Direction), v.Over ? null : v.MyTurn);

        Assert.Equal("Ann", Chip()!.Name);
        Assert.False(Chip()!.MyTurn);
        v.Turn = 2;
        Assert.Equal("Bob", Chip()!.Name);
        v.Turn = 0;
        Assert.True(Chip()!.MyTurn);
        Assert.Equal("Ann", Chip()!.Name); // next to the left
        v.Direction = -1;
        Assert.Equal("Bob", Chip()!.Name); // after a Reverse, next to the right
        v.Over = true;
        Assert.Null(Chip()!.MyTurn);
    }
}
