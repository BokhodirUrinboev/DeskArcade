using System;
using System.Collections.Generic;
using System.Linq;

namespace DeskArcade.Games;

/// <summary>
/// Durak (podkidnoy, "throw-in fool") for 2–6 players, free of UI so it can be tested and run by the host of
/// a LAN room. A 36-card deck (6 to ace); the bottom card is turned up and its suit is trump. Players sit in
/// seat order; the defender is the next player still in after the attacker.
/// <list type="bullet">
/// <item>The attacker leads any card. Then every player except the defender may throw in cards whose rank
/// is already on the table, up to six a bout (five in the first bout), and never more than the defender
/// has cards to answer.</item>
/// <item>The defender beats each card with a higher card of its suit, or with a trump (a trump only with a
/// higher trump), or takes everything on the table. After a take, the attackers may still throw in.</item>
/// <item>When every card is beaten and the attackers are done, the cards go to the discard pile and the
/// defender attacks next; after a take, the player after the defender attacks.</item>
/// <item>After each bout the players draw back up to six: the attacker first, the defender last.</item>
/// <item>Once the deck is empty, a player without cards is out. The last one holding cards is the durak.</item>
/// </list>
/// Cards are numbered suit × 9 + (rank − 6): ranks 6–14 (jack 11, queen 12, king 13, ace 14), suits
/// ♠ 0, ♣ 1, ♦ 2, ♥ 3.
/// </summary>
public sealed class DurakRules
{
    public const int HandSize = 6, Ranks = 9, Suits = 4, DeckSize = Ranks * Suits;

    public static int Suit(int card) => card / Ranks;
    public static int Rank(int card) => card % Ranks + 6;
    public static int Card(int suit, int rank) => suit * Ranks + rank - 6;

    public sealed class Pair
    {
        public int Attack { get; set; }
        public int Defense { get; set; } = -1;
        public bool Beaten => Defense >= 0;
    }

    public int Players { get; }
    public List<int>[] Hands { get; }
    /// <summary>The draw pile; the last element is drawn first, element 0 is the face-up trump card.</summary>
    public List<int> Deck { get; }
    public int TrumpSuit { get; }
    public int TrumpCard { get; }
    public List<Pair> Table { get; } = new();
    public int Discarded { get; private set; }
    public int Attacker { get; private set; }
    public int Defender { get; private set; }
    /// <summary>The defender has given up on this bout and takes the cards once the attackers are done.</summary>
    public bool Taking { get; private set; }
    /// <summary>Attackers who have said they're done throwing in this bout.</summary>
    public bool[] Done { get; }
    /// <summary>Players who have got rid of all their cards once the deck ran out.</summary>
    public bool[] Out { get; }
    public int Bout { get; private set; } = 1;
    /// <summary>How many attack cards this bout may have.</summary>
    public int Limit { get; private set; }
    public bool Over { get; private set; }
    /// <summary>The loser once the game is over, or −1 when the last cards went out together (a draw).</summary>
    public int Durak { get; private set; } = -1;
    /// <summary>Counts every accepted action, so a view can tell it's out of date.</summary>
    public int Version { get; private set; }

    public DurakRules(int players, Random rng)
    {
        if (players is < 2 or > 6) throw new ArgumentOutOfRangeException(nameof(players));
        Players = players;
        Deck = Enumerable.Range(0, DeckSize).OrderBy(_ => rng.Next()).ToList();
        TrumpCard = Deck[0];
        TrumpSuit = Suit(TrumpCard);
        Hands = Enumerable.Range(0, players).Select(_ => new List<int>()).ToArray();
        Done = new bool[players];
        Out = new bool[players];
        for (int round = 0; round < HandSize; round++)
            for (int p = 0; p < players; p++)
                Hands[p].Add(Draw());

        // the lowest trump leads; with no trumps dealt, seat 0 does
        int first = 0, lowest = int.MaxValue;
        for (int p = 0; p < players; p++)
            foreach (int c in Hands[p].Where(c => Suit(c) == TrumpSuit))
                if (Rank(c) < lowest)
                {
                    lowest = Rank(c);
                    first = p;
                }
        StartBout(first);
    }

    int Draw()
    {
        int c = Deck[^1];
        Deck.RemoveAt(Deck.Count - 1);
        return c;
    }

    public bool Beats(int defense, int attack) =>
        Suit(defense) == Suit(attack) ? Rank(defense) > Rank(attack) : Suit(defense) == TrumpSuit;

    public int Unbeaten => Table.Count(p => !p.Beaten);

    /// <summary>A player who may still throw cards into this bout (anyone in the game except the defender).</summary>
    public bool IsAttacker(int seat) => !Over && seat != Defender && !Out[seat];

    // ------------------------------------------------------------------ actions

    /// <summary>Plays <paramref name="card"/> onto the table as an attack or a throw-in.</summary>
    public bool Attack(int seat, int card)
    {
        if (!CanAttack(seat, card)) return false;
        Hands[seat].Remove(card);
        Table.Add(new Pair { Attack = card });
        Array.Clear(Done); // a new card gives everyone a new chance to throw in
        Changed();
        AutoEnd();
        return true;
    }

    public bool CanAttack(int seat, int card)
    {
        if (!IsAttacker(seat) || !Hands[seat].Contains(card)) return false;
        if (Table.Count >= Limit || Unbeaten >= Hands[Defender].Count) return false;
        if (Table.Count == 0) return seat == Attacker; // only the attacker leads
        return Table.Any(p => Rank(p.Attack) == Rank(card) || p.Beaten && Rank(p.Defense) == Rank(card));
    }

    /// <summary>The defender beats the attack card at <paramref name="index"/> with <paramref name="card"/>.</summary>
    public bool Defend(int seat, int index, int card)
    {
        if (Over || seat != Defender || Taking || index < 0 || index >= Table.Count) return false;
        var pair = Table[index];
        if (pair.Beaten || !Hands[seat].Contains(card) || !Beats(card, pair.Attack)) return false;
        Hands[seat].Remove(card);
        pair.Defense = card;
        Changed();
        AutoEnd();
        return true;
    }

    /// <summary>The defender gives up: they will take the table once the attackers are done throwing in.</summary>
    public bool Take(int seat)
    {
        if (Over || seat != Defender || Taking || Unbeaten == 0) return false;
        Taking = true;
        Array.Clear(Done);
        Changed();
        AutoEnd();
        return true;
    }

    /// <summary>An attacker has nothing more to throw in this bout.</summary>
    public bool Pass(int seat)
    {
        if (!IsAttacker(seat) || Table.Count == 0 || Done[seat]) return false;
        Done[seat] = true;
        Changed();
        AutoEnd();
        return true;
    }

    /// <summary>Whether the bout still waits on <paramref name="seat"/> to throw in or say done.</summary>
    public bool AwaitsAttacker(int seat) => IsAttacker(seat) && Table.Count > 0 && !Done[seat] && Hands[seat].Count > 0 && (Taking || Unbeaten == 0);

    /// <summary>Whether any card could still be added this bout (the table isn't full and the defender could answer it).</summary>
    bool Room => Table.Count < Limit && Unbeaten < Hands[Defender].Count;

    /// <summary>Ends the bout once nothing more can happen in it.</summary>
    void AutoEnd()
    {
        if (Table.Count == 0) return;
        bool attackersDone = Enumerable.Range(0, Players).Where(IsAttacker).All(s => Done[s] || Hands[s].Count == 0);
        if (Taking && (attackersDone || !Room)) EndBout(taken: true);
        else if (!Taking && Unbeaten == 0 && (attackersDone || !Room)) EndBout(taken: false);
    }

    void EndBout(bool taken)
    {
        if (taken)
            foreach (var p in Table)
            {
                Hands[Defender].Add(p.Attack);
                if (p.Beaten) Hands[Defender].Add(p.Defense);
            }
        else Discarded += Table.Count * 2;
        Table.Clear();
        Taking = false;

        // draw back up to six: the attacker first, then the others in seat order, the defender last
        var order = Enumerable.Range(1, Players).Select(i => (Attacker + i - 1) % Players).Where(s => s != Defender).ToList();
        order.Add(Defender);
        foreach (int s in order)
            while (Hands[s].Count < HandSize && Deck.Count > 0) Hands[s].Add(Draw());

        if (Deck.Count == 0)
            for (int s = 0; s < Players; s++)
                if (Hands[s].Count == 0) Out[s] = true;
        var left = Enumerable.Range(0, Players).Where(s => !Out[s]).ToList();
        if (left.Count <= 1)
        {
            Over = true;
            Durak = left.Count == 1 ? left[0] : -1;
            Changed();
            return;
        }
        Bout++;
        StartBout(taken ? Next(Defender) : Out[Defender] ? Next(Defender) : Defender);
    }

    void StartBout(int attacker)
    {
        Attacker = attacker;
        Defender = Next(attacker);
        Array.Clear(Done);
        Limit = Math.Min(Bout == 1 ? 5 : HandSize, Hands[Defender].Count);
        Changed();
    }

    /// <summary>The next seat after <paramref name="seat"/> still in the game.</summary>
    int Next(int seat)
    {
        for (int i = 1; i <= Players; i++)
        {
            int s = (seat + i) % Players;
            if (!Out[s]) return s;
        }
        return seat;
    }

    void Changed() => Version++;

    // ------------------------------------------------------------------ computer player

    /// <summary>A card's worth to keep: trumps are worth more than any plain card.</summary>
    int Worth(int card) => Rank(card) + (Suit(card) == TrumpSuit ? 20 : 0);

    /// <summary>
    /// The computer's next action for <paramref name="seat"/>, or null if it has nothing to do right now.
    /// Kinds: "attack" (card), "defend" (index, card), "take", "pass".
    /// </summary>
    public (string Kind, int Card, int Index)? CpuAction(int seat)
    {
        if (Over || Out[seat]) return null;
        var hand = Hands[seat];
        if (seat == Defender)
        {
            if (Taking || Unbeaten == 0) return null;
            // beat the strongest unbeaten card first with the cheapest card that does it
            var used = new HashSet<int>();
            (int Index, int Card)? first = null;
            foreach (var (pair, index) in Table.Select((p, i) => (p, i)).Where(t => !t.p.Beaten).OrderByDescending(t => Worth(t.p.Attack)))
            {
                int answer = hand.Where(c => !used.Contains(c) && Beats(c, pair.Attack)).OrderBy(Worth).DefaultIfEmpty(-1).First();
                if (answer < 0) return ("take", -1, -1);
                // early in the game, don't spend a high trump on a small plain card: take instead
                if (Deck.Count > 10 && Suit(answer) == TrumpSuit && Suit(pair.Attack) != TrumpSuit && Rank(answer) >= 12 && Table.Count <= 2)
                    return ("take", -1, -1);
                used.Add(answer);
                first ??= (index, answer);
            }
            return first is { } f ? ("defend", f.Card, f.Index) : null;
        }

        if (!IsAttacker(seat) || Hands[seat].Count == 0) return null;
        if (Table.Count == 0)
        {
            if (seat != Attacker) return null;
            // lead the cheapest card, preferring a rank you hold twice
            int lead = hand.OrderBy(c => Worth(c) - 3 * (hand.Count(o => Rank(o) == Rank(c)) - 1)).First();
            return ("attack", lead, -1);
        }
        if (!AwaitsAttacker(seat)) return null;
        bool late = Deck.Count == 0;
        int throwIn = hand.Where(c => CanAttack(seat, c) && (late || Suit(c) != TrumpSuit && Rank(c) <= 11)).OrderBy(Worth).DefaultIfEmpty(-1).First();
        return throwIn >= 0 ? ("attack", throwIn, -1) : ("pass", -1, -1);
    }

    /// <summary>Carries out an action from <see cref="CpuAction"/> or from a player.</summary>
    public bool Act(int seat, string kind, int card, int index) => kind switch
    {
        "attack" => Attack(seat, card),
        "defend" => Defend(seat, index, card),
        "take" => Take(seat),
        "pass" => Pass(seat),
        _ => false,
    };
}
