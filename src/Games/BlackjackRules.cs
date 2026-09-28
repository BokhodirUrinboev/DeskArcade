using System;
using System.Collections.Generic;
using System.Linq;

namespace DeskArcade.Games;

public enum BlackjackPhase { Betting, Playing, Dealer, Settled, RoundOver }

public enum BlackjackOutcome { None, Blackjack, Win, Push, Lose, Bust }

/// <summary>
/// Blackjack against the house, a round at a time: ten hands starting from 100 chips, and the chips at the end are the
/// round's score. Place a bet (5, 10 or 25), then hit, stand or double down (one more card for twice the bet); get
/// closer to 21 than the dealer without going over. Aces count 1 or 11, faces 10. The dealer draws to 17 and stands on
/// a soft 17 too; a two-card 21 (blackjack) pays 3 to 2, and the dealer's blackjack is checked at once. The shoe is two
/// decks, reshuffled when it runs low. A round ends early when the chips can't cover the smallest bet. Cards are 0–51:
/// suit = card / 13 (♠ ♣ ♦ ♥), rank = card % 13 + 1 (1 ace … 13 king). UI-free and seeded, so it can be tested.
/// </summary>
public sealed class BlackjackRules
{
    public const int StartChips = 100, Hands = 10, Decks = 2;
    public static readonly int[] Bets = { 5, 10, 25 };

    readonly Random _rng;
    readonly List<int> _shoe = new();

    public BlackjackRules(Random rng)
    {
        _rng = rng;
        Shuffle();
    }

    public int Chips { get; private set; } = StartChips;
    public int Hand { get; private set; } // hands dealt this round
    public int Bet { get; private set; }
    public BlackjackPhase Phase { get; private set; } = BlackjackPhase.Betting;
    public BlackjackOutcome Outcome { get; private set; }
    public int Paid { get; private set; } // what the last hand won (or lost, negative)
    public bool Doubled { get; private set; }
    public List<int> Player { get; } = new();
    public List<int> Dealer { get; } = new();

    /// <summary>The dealer's second card stays face down until the player is done.</summary>
    public bool HoleHidden => Phase is BlackjackPhase.Playing;

    public static int Rank(int card) => card % 13 + 1;
    public static int Suit(int card) => card / 13;

    /// <summary>A hand's best total, and whether an ace is counting 11 in it (a soft total).</summary>
    public static (int Total, bool Soft) Value(IEnumerable<int> cards)
    {
        int total = 0, aces = 0;
        foreach (int c in cards)
        {
            int r = Rank(c);
            total += r == 1 ? 1 : Math.Min(10, r);
            if (r == 1) aces++;
        }
        bool soft = aces > 0 && total + 10 <= 21;
        return (soft ? total + 10 : total, soft);
    }

    public static bool IsBlackjack(IReadOnlyList<int> cards) => cards.Count == 2 && Value(cards).Total == 21;

    public bool CanDouble => Phase == BlackjackPhase.Playing && Player.Count == 2 && Chips >= Bet * 2;

    /// <summary>Starts a hand with <paramref name="bet"/> chips: two cards each, and a blackjack on either side settles at once.</summary>
    public bool Deal(int bet)
    {
        if (Phase is not (BlackjackPhase.Betting or BlackjackPhase.Settled) || bet <= 0 || bet > Chips || Hand >= Hands) return false;
        if (_shoe.Count < 15) Shuffle();
        Player.Clear();
        Dealer.Clear();
        Bet = bet;
        Doubled = false;
        Outcome = BlackjackOutcome.None;
        Paid = 0;
        Hand++;
        Player.Add(Draw());
        Dealer.Add(Draw());
        Player.Add(Draw());
        Dealer.Add(Draw());
        Phase = BlackjackPhase.Playing;
        if (IsBlackjack(Player) || IsBlackjack(Dealer)) Settle();
        return true;
    }

    public bool Hit()
    {
        if (Phase != BlackjackPhase.Playing) return false;
        Player.Add(Draw());
        int total = Value(Player).Total;
        if (total > 21) Settle();
        else if (total == 21) Stand();
        return true;
    }

    public bool Stand()
    {
        if (Phase != BlackjackPhase.Playing) return false;
        Phase = BlackjackPhase.Dealer;
        while (DealerHits()) Dealer.Add(Draw());
        Settle();
        return true;
    }

    public bool Double()
    {
        if (!CanDouble) return false;
        Bet *= 2;
        Doubled = true;
        Player.Add(Draw());
        if (Value(Player).Total > 21) Settle();
        else Stand();
        return true;
    }

    /// <summary>The dealer draws below 17 and on a soft 17.</summary>
    bool DealerHits()
    {
        var (total, soft) = Value(Dealer);
        return total < 17 || total == 17 && soft;
    }

    void Settle()
    {
        int me = Value(Player).Total, dealer = Value(Dealer).Total;
        bool myBj = IsBlackjack(Player), dealerBj = IsBlackjack(Dealer);
        (Outcome, Paid) = true switch
        {
            _ when myBj && dealerBj => (BlackjackOutcome.Push, 0),
            _ when myBj => (BlackjackOutcome.Blackjack, Bet * 3 / 2),
            _ when dealerBj => (BlackjackOutcome.Lose, -Bet),
            _ when me > 21 => (BlackjackOutcome.Bust, -Bet),
            _ when dealer > 21 || me > dealer => (BlackjackOutcome.Win, Bet),
            _ when me == dealer => (BlackjackOutcome.Push, 0),
            _ => (BlackjackOutcome.Lose, -Bet),
        };
        Chips += Paid;
        Phase = Hand >= Hands || Chips < Bets[0] ? BlackjackPhase.RoundOver : BlackjackPhase.Settled;
    }

    /// <summary>A fresh round: 100 chips and ten hands to play.</summary>
    public void NewRound()
    {
        Chips = StartChips;
        Hand = 0;
        Bet = 0;
        Player.Clear();
        Dealer.Clear();
        Outcome = BlackjackOutcome.None;
        Phase = BlackjackPhase.Betting;
    }

    public int ShoeLeft => _shoe.Count;

    int Draw()
    {
        if (_shoe.Count == 0) Shuffle();
        int c = _shoe[^1];
        _shoe.RemoveAt(_shoe.Count - 1);
        return c;
    }

    void Shuffle()
    {
        _shoe.Clear();
        for (int d = 0; d < Decks; d++) _shoe.AddRange(Enumerable.Range(0, 52));
        for (int i = _shoe.Count - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            (_shoe[i], _shoe[j]) = (_shoe[j], _shoe[i]);
        }
    }

    /// <summary>
    /// A simple basic strategy, for the demo player and for tests: double 10 and 11 against a weak up card, stand on 17 or
    /// more, on 12 to 16 against a 2 to 6, and on soft 18 or more; otherwise hit.
    /// </summary>
    public static char Advice(IReadOnlyList<int> player, int dealerUp, bool canDouble)
    {
        var (total, soft) = Value(player);
        int up = Rank(dealerUp) == 1 ? 11 : Math.Min(10, Rank(dealerUp));
        if (canDouble && total is 10 or 11 && up < total) return 'D';
        if (soft) return total >= 18 ? 'S' : 'H';
        if (total >= 17) return 'S';
        if (total >= 12 && up is >= 2 and <= 6) return 'S';
        return 'H';
    }
}
