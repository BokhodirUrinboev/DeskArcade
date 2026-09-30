using System;
using System.Collections.Generic;
using System.Linq;

namespace DeskArcade.Games;

/// <summary>
/// Hearts for four, as Windows played it, free of UI so it can be tested and run by the host of a LAN room.
/// <list type="bullet">
/// <item>Thirteen cards each. Before each hand everyone passes three cards: to the left, then the right, then across,
/// then nobody passes, and round again. Left is the next seat.</item>
/// <item>The two of clubs leads the first trick. Follow suit if you can; the highest card of the suit led takes the
/// trick, and its taker leads the next.</item>
/// <item>No points on the first trick: a heart or the queen of spades can't be thrown on it unless there is nothing else.</item>
/// <item>Hearts can't be led until one has been thrown on a trick (hearts are broken), unless there is nothing else.</item>
/// <item>Each heart taken is a point and the queen of spades thirteen. Take all 26 and you shoot the moon: you score
/// nothing and everyone else 26.</item>
/// <item>When someone reaches <see cref="GameTo"/> at the end of a hand, the lowest score wins.</item>
/// </list>
/// Cards are 0–51 as in <see cref="PokerHand"/>: suit card / 13 (♠ 0, ♣ 1, ♦ 2, ♥ 3), rank card % 13 + 2 (ace high).
/// Actions: "pass" with three cards packed into one number (<see cref="Pack"/>), and "play" with a card.
/// </summary>
public sealed class HeartsRules
{
    public const int Players = 4, HandSize = 13, DefaultGameTo = 100, MoonPoints = 26;
    public const int Spades = 0, Clubs = 1, Diamonds = 2, Hearts = 3;
    public static readonly int TwoOfClubs = PokerHand.Card(2, Clubs), QueenOfSpades = PokerHand.Card(12, Spades);

    public enum Stage { Passing, Playing, HandOver }

    /// <summary>Where the three cards go this hand; Keep is the hand nobody passes.</summary>
    public enum PassDirection { Left, Right, Across, Keep }

    public static int Suit(int card) => card / 13;
    public static int Rank(int card) => card % 13 + 2;
    public static int PointsOf(int card) => Suit(card) == Hearts ? 1 : card == QueenOfSpades ? 13 : 0;

    /// <summary>Three cards as one number for a "pass" action, and back.</summary>
    public static int Pack(IReadOnlyList<int> cards) => cards[0] + 52 * cards[1] + 52 * 52 * cards[2];
    public static int[] Unpack(int packed) => new[] { packed % 52, packed / 52 % 52, packed / (52 * 52) % 52 };

    readonly Random _rng;

    public int GameTo { get; }
    public List<int>[] Hands { get; }
    public int[] Scores { get; } = new int[Players];
    /// <summary>Points taken so far this hand.</summary>
    public int[] Taken { get; } = new int[Players];
    public int[] TricksWon { get; } = new int[Players];
    /// <summary>The cards each seat has chosen to pass (null until chosen), and what each received.</summary>
    public int[]?[] Chosen { get; } = new int[]?[Players];
    public int[][] Received { get; } = Enumerable.Range(0, Players).Select(_ => Array.Empty<int>()).ToArray();
    /// <summary>The trick on the table: each seat's card, −1 until played.</summary>
    public int[] Trick { get; } = { -1, -1, -1, -1 };
    public int[] LastTrick { get; } = { -1, -1, -1, -1 };
    /// <summary>Every card played this hand, in order: who played it and in which trick (0–12).</summary>
    public List<(int Seat, int Card, int TrickNo)> History { get; } = new();
    /// <summary>What each seat's score went up by at the end of the last hand.</summary>
    public int[] HandPoints { get; } = new int[Players];

    public int HandNumber { get; private set; }
    public PassDirection Pass => (PassDirection)((HandNumber - 1) % 4);
    public Stage Phase { get; private set; }
    public int Leader { get; private set; }
    /// <summary>The seat to play, or −1 while passing or between hands.</summary>
    public int Turn { get; private set; } = -1;
    /// <summary>Tricks finished this hand (0–13).</summary>
    public int TrickNumber { get; private set; }
    public bool HeartsBroken { get; private set; }
    public int LastLeader { get; private set; } = -1;
    public int LastWinner { get; private set; } = -1;
    /// <summary>Who shot the moon last hand, or −1.</summary>
    public int Moon { get; private set; } = -1;
    public bool Over { get; private set; }
    /// <summary>Goes up with every change, so a view can tell it is newer.</summary>
    public int Version { get; private set; }

    /// <summary>Tests: the deck for a hand (by its number), thirteen cards for each seat in seat order.</summary>
    public Func<int, IReadOnlyList<int>>? DeckFor { get; set; }

    /// <param name="firstHand">Tests: the number of the first hand, which sets its passing (4 passes nothing).</param>
    public HeartsRules(Random rng, int gameTo = DefaultGameTo, Func<int, IReadOnlyList<int>>? deckFor = null, int firstHand = 1)
    {
        _rng = rng;
        GameTo = gameTo;
        DeckFor = deckFor;
        HandNumber = Math.Max(1, firstHand) - 1;
        Hands = Enumerable.Range(0, Players).Select(_ => new List<int>()).ToArray();
        Deal();
    }

    /// <summary>The seat that gets <paramref name="seat"/>'s three cards this hand.</summary>
    public int PassTarget(int seat) => Pass switch
    {
        PassDirection.Left => (seat + 1) % Players,
        PassDirection.Right => (seat + 3) % Players,
        PassDirection.Across => (seat + 2) % Players,
        _ => seat,
    };

    /// <summary>The seats with the lowest score (the winners, once the game is over).</summary>
    public IEnumerable<int> Leaders => Enumerable.Range(0, Players).Where(s => Scores[s] == Scores.Min());

    void Deal()
    {
        HandNumber++;
        var deck = DeckFor?.Invoke(HandNumber)?.ToList() ?? new List<int>();
        var rest = Enumerable.Range(0, 52).Except(deck).ToList();
        for (int i = rest.Count - 1; i > 0; i--) // Fisher–Yates
        {
            int j = _rng.Next(i + 1);
            (rest[i], rest[j]) = (rest[j], rest[i]);
        }
        deck.AddRange(rest);
        for (int s = 0; s < Players; s++)
        {
            Hands[s].Clear();
            Hands[s].AddRange(deck.Skip(s * HandSize).Take(HandSize));
            Taken[s] = TricksWon[s] = 0;
            Chosen[s] = null;
            Received[s] = Array.Empty<int>();
            Trick[s] = LastTrick[s] = -1;
        }
        History.Clear();
        LastLeader = LastWinner = -1;
        TrickNumber = 0;
        HeartsBroken = false;
        if (Pass == PassDirection.Keep) StartPlay();
        else
        {
            Phase = Stage.Passing;
            Turn = -1;
        }
    }

    void StartPlay()
    {
        Phase = Stage.Playing;
        Leader = Turn = Enumerable.Range(0, Players).First(s => Hands[s].Contains(TwoOfClubs));
    }

    /// <summary>The suit led to the trick on the table, or −1 when it is still to be led.</summary>
    public int LedSuit => Trick[Leader] < 0 || Phase != Stage.Playing ? -1 : Suit(Trick[Leader]);

    /// <summary>
    /// Whether <paramref name="card"/> may go on the trick from <paramref name="hand"/>: the two of clubs to lead the
    /// first trick; the suit led if you have it; no points on the first trick unless you hold nothing else; and no heart
    /// led before hearts are broken unless you hold nothing else.
    /// </summary>
    public static bool Legal(IReadOnlyList<int> hand, int card, int ledSuit, bool firstTrick, bool heartsBroken)
    {
        if (!hand.Contains(card)) return false;
        if (ledSuit < 0)
        {
            if (firstTrick) return card == TwoOfClubs || !hand.Contains(TwoOfClubs);
            return Suit(card) != Hearts || heartsBroken || hand.All(c => Suit(c) == Hearts);
        }
        if (hand.Any(c => Suit(c) == ledSuit)) return Suit(card) == ledSuit;
        return !firstTrick || PointsOf(card) == 0 || hand.All(c => PointsOf(c) > 0);
    }

    public bool CanPlay(int seat, int card) =>
        Phase == Stage.Playing && !Over && seat == Turn && Legal(Hands[seat], card, LedSuit, TrickNumber == 0, HeartsBroken);

    public List<int> LegalCards(int seat) => Hands[seat].Where(c => CanPlay(seat, c)).ToList();

    /// <summary>A player's move: "pass" (three cards packed, <see cref="Pack"/>) or "play" (a card). False if it isn't allowed; nothing changes then.</summary>
    public bool Act(int seat, string kind, int value)
    {
        if (seat < 0 || seat >= Players || Over) return false;
        bool ok = kind switch
        {
            "pass" => PassCards(seat, Unpack(value)),
            "play" => Play(seat, value),
            _ => false,
        };
        if (ok) Version++;
        return ok;
    }

    bool PassCards(int seat, int[] cards)
    {
        if (Phase != Stage.Passing || Chosen[seat] != null || cards.Distinct().Count() != 3 || !cards.All(Hands[seat].Contains)) return false;
        Chosen[seat] = cards;
        if (Chosen.Any(c => c == null)) return true;
        // everyone has chosen: the cards change hands all at once
        for (int s = 0; s < Players; s++)
            foreach (int c in Chosen[s]!) Hands[s].Remove(c);
        for (int s = 0; s < Players; s++)
        {
            int to = PassTarget(s);
            Hands[to].AddRange(Chosen[s]!);
            Received[to] = Chosen[s]!.ToArray();
        }
        StartPlay();
        return true;
    }

    bool Play(int seat, int card)
    {
        if (!CanPlay(seat, card)) return false;
        Hands[seat].Remove(card);
        Trick[seat] = card;
        History.Add((seat, card, TrickNumber));
        if (Suit(card) == Hearts) HeartsBroken = true;
        if (Trick.Any(c => c < 0))
        {
            Turn = (Turn + 1) % Players;
            return true;
        }
        // the trick is complete: the highest card of the suit led takes it
        int led = Suit(Trick[Leader]);
        int winner = Enumerable.Range(0, Players).Where(s => Suit(Trick[s]) == led).OrderByDescending(s => Rank(Trick[s])).First();
        Taken[winner] += Trick.Sum(PointsOf);
        TricksWon[winner]++;
        Array.Copy(Trick, LastTrick, Players);
        LastLeader = Leader;
        LastWinner = winner;
        Array.Fill(Trick, -1);
        Leader = Turn = winner;
        TrickNumber++;
        if (TrickNumber == HandSize) EndHand();
        return true;
    }

    void EndHand()
    {
        Moon = Array.IndexOf(Taken, MoonPoints);
        for (int s = 0; s < Players; s++)
        {
            HandPoints[s] = Moon < 0 ? Taken[s] : s == Moon ? 0 : MoonPoints;
            Scores[s] += HandPoints[s];
        }
        Phase = Stage.HandOver;
        Turn = -1;
        if (Scores.Max() >= GameTo) Over = true;
    }

    /// <summary>After a hand: deals the next one. False while a hand is on, or when the game is over.</summary>
    public bool NextHand()
    {
        if (Phase != Stage.HandOver || Over) return false;
        Deal();
        Version++;
        return true;
    }

    /// <summary>Each seat's place by score (1 for the lowest; equal scores share a place).</summary>
    public int PlaceOf(int seat) => 1 + Scores.Count(s => s < Scores[seat]);
}
