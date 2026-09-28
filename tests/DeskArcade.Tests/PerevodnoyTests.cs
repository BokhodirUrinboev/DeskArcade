using System;
using System.Linq;
using DeskArcade.Games;
using Xunit;

namespace DeskArcade.Tests;

public class PerevodnoyTests
{
    static int C(int suit, int rank) => DurakRules.Card(suit, rank);

    [Fact]
    public void ACardOfTheSameRankPassesTheAttackOn()
    {
        var g = new DurakRules(3, new Random(2), transfers: true);
        int a = g.Attacker, d = g.Defender, next = (d + 1) % 3;
        int t = g.TrumpSuit, plain = (t + 1) % 4, other = (t + 2) % 4;
        g.Hands[a].Clear();
        g.Hands[a].AddRange(new[] { C(plain, 9), C(plain, 12) });
        g.Hands[d].Clear();
        g.Hands[d].AddRange(new[] { C(other, 9), C(plain, 7) });
        Assert.True(g.Attack(a, C(plain, 9)));
        Assert.False(g.CanTransfer(d, C(plain, 7))); // another rank
        Assert.True(g.CanTransfer(d, C(other, 9)));
        Assert.True(g.Transfer(d, C(other, 9)));
        Assert.Equal(2, g.Table.Count);
        Assert.All(g.Table, p => Assert.False(p.Beaten));
        Assert.Equal(d, g.Attacker);
        Assert.Equal(next, g.Defender);
        Assert.DoesNotContain(C(other, 9), g.Hands[d]);
    }

    [Fact]
    public void PodkidnoyHasNoPassingOn()
    {
        var g = new DurakRules(2, new Random(4));
        int a = g.Attacker, d = g.Defender;
        int lead = g.Hands[a][0];
        g.Attack(a, lead);
        Assert.False(g.Transfers);
        Assert.DoesNotContain(g.Hands[d], c => g.CanTransfer(d, c));
        Assert.False(g.Act(d, "transfer", g.Hands[d][0], -1));
    }

    [Fact]
    public void NoPassingOnOnceACardIsBeaten()
    {
        var g = new DurakRules(2, new Random(6), transfers: true);
        int a = g.Attacker, d = g.Defender, t = g.TrumpSuit, plain = (t + 1) % 4, other = (t + 2) % 4;
        g.Hands[a].Clear();
        g.Hands[a].AddRange(new[] { C(plain, 8), C(other, 8), C(plain, 13), C(plain, 14), C(other, 13), C(other, 14) });
        g.Hands[d].Clear();
        g.Hands[d].AddRange(new[] { C(plain, 10), C((t + 3) % 4, 8), C(t, 6), C(t, 7), C(t, 9), C(t, 10) });
        g.Attack(a, C(plain, 8));
        Assert.True(g.Defend(d, 0, C(plain, 10)));
        g.Attack(a, C(other, 8));
        Assert.False(g.CanTransfer(d, C((t + 3) % 4, 8))); // one is beaten already
    }

    [Fact]
    public void TheNextPlayerMustBeAbleToAnswerEveryCard()
    {
        var g = new DurakRules(2, new Random(8), transfers: true);
        int a = g.Attacker, d = g.Defender, t = g.TrumpSuit, plain = (t + 1) % 4, other = (t + 2) % 4, third = (t + 3) % 4;
        g.Hands[a].Clear();
        g.Hands[a].AddRange(new[] { C(plain, 11), C(other, 11) }); // two cards: after throwing both in, the attacker holds none
        g.Hands[d].Clear();
        g.Hands[d].AddRange(new[] { C(third, 11), C(t, 6), C(t, 7), C(t, 8), C(t, 9), C(t, 10) });
        g.Attack(a, C(plain, 11));
        g.Attack(a, C(other, 11));
        Assert.Empty(g.Hands[a]);
        Assert.False(g.CanTransfer(d, C(third, 11))); // passing on to a hand of none: three cards it couldn't answer
    }

    [Fact]
    public void BetweenTwoTheAttackBouncesBack()
    {
        var g = new DurakRules(2, new Random(10), transfers: true);
        int a = g.Attacker, d = g.Defender, t = g.TrumpSuit;
        int[] suits = Enumerable.Range(0, 4).Where(s => s != t).ToArray();
        g.Hands[a].Clear();
        g.Hands[a].AddRange(new[] { C(suits[0], 7), C(suits[2], 7), C(t, 12), C(t, 13), C(t, 14), C(suits[0], 14) });
        g.Hands[d].Clear();
        g.Hands[d].AddRange(new[] { C(suits[1], 7), C(t, 6), C(t, 8), C(t, 9), C(t, 10), C(t, 11) });
        g.Attack(a, C(suits[0], 7));
        Assert.True(g.Transfer(d, C(suits[1], 7)));
        Assert.Equal(a, g.Defender);
        Assert.True(g.Transfer(a, C(suits[2], 7))); // and back again
        Assert.Equal(d, g.Defender);
        Assert.Equal(3, g.Table.Count);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ComputerGamesWithPassingOnFinishWithEveryCard(int players)
    {
        int transfers = 0;
        for (int seed = 0; seed < 25; seed++)
        {
            var g = new DurakRules(players, new Random(seed), transfers: true);
            int actions = 0;
            while (!g.Over && actions++ < 5000)
            {
                bool acted = false;
                foreach (int seat in new[] { g.Defender }.Concat(Enumerable.Range(0, g.Players)))
                {
                    if (g.CpuAction(seat) is not { } a) continue;
                    Assert.True(g.Act(seat, a.Kind, a.Card, a.Index), $"CPU action {a.Kind} by seat {seat} was refused");
                    if (a.Kind == "transfer") transfers++;
                    acted = true;
                    break;
                }
                Assert.True(acted, "nobody could act but the game isn't over");
                Assert.Equal(DurakRules.DeckSize, g.Hands.Sum(h => h.Count) + g.Deck.Count + g.Table.Sum(p => p.Beaten ? 2 : 1) + g.Discarded);
            }
            Assert.True(g.Over, $"seed {seed} didn't finish");
        }
        Assert.True(transfers > 0, "the computer passed an attack on at least once");
    }
}
