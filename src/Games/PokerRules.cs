using System;
using System.Collections.Generic;
using System.Linq;

namespace DeskArcade.Games;

/// <summary>A pot as a hand ended: its chips, who could win it, who did, and with which hand.</summary>
public sealed class PokerPot
{
    public int Amount { get; set; }
    public int[] Eligible { get; set; } = Array.Empty<int>();
    public int[] Winners { get; set; } = Array.Empty<int>();
    /// <summary>The winning hand's value (<see cref="PokerHand.Evaluate(ReadOnlySpan{int})"/>), 0 when nobody had to show.</summary>
    public int Value { get; set; }
    /// <summary>Chips nobody called, going back to the one player who put them in.</summary>
    public bool Returned { get; set; }
}

/// <summary>
/// Texas hold'em for chips, free of UI so it can be tested and run by the host of a LAN room: 2 to 6 players, each
/// starting with <see cref="StartStack"/> chips, blinds rising every <see cref="HandsPerLevel"/> hands, until one player
/// has every chip, or for a fixed number of hands (<see cref="HandLimit"/>, the lunch-break game).
/// <list type="bullet">
/// <item>The button moves one live seat on each hand. The small blind sits left of the button and the big blind left of
/// that; heads-up the button posts the small blind, acts first before the flop and last after it.</item>
/// <item>Before the flop the player left of the big blind acts first, and the big blind may still raise when everyone
/// just called; after it, the first live player left of the button.</item>
/// <item>A raise is at least the size of the last raise (the big blind to start with). All in for less is allowed, but
/// it does not let a player who has already acted raise again.</item>
/// <item>Chips go into pots by what each player put in, so an all-in player can only win what they matched; a pot is
/// split between equal hands, the odd chips going to the winners first round from the button.</item>
/// <item>At the showdown the last bettor on the river shows first (or the first player left of the button when the
/// river was checked); a later player who cannot win shows only a hand as good as the best shown. When everyone left
/// is all in, the hands are turned up and the board is dealt one street at a time (<see cref="Step"/>).</item>
/// </list>
/// Cards are 0–51 as in <see cref="PokerHand"/>. Actions: "fold", "check", "call", "raise" (to a total bet for this
/// street) and "allin".
/// </summary>
public sealed class PokerRules
{
    public const int MinPlayers = 2, MaxPlayers = 6, StartStack = 1000, DefaultHandsPerLevel = 8;

    /// <summary>The big blind at each level; the small blind is half. Past the end it keeps doubling.</summary>
    public static readonly int[] BigBlinds = { 20, 30, 40, 60, 80, 100, 150, 200, 300, 400, 600, 800, 1000, 1500, 2000, 3000, 4000, 6000 };

    public enum Street { Preflop, Flop, Turn, River, Showdown }

    readonly Random _rng;
    readonly List<int> _deck = new();
    int _next;
    readonly int _firstDealer;

    public int Players { get; }
    public int HandLimit { get; }
    public int HandsPerLevel { get; }
    public int Total { get; }

    public int[] Stacks { get; }
    /// <summary>What each player has put in on this street.</summary>
    public int[] Bets { get; }
    /// <summary>What each player has put in this hand, this street included.</summary>
    public int[] Committed { get; }
    public bool[] Folded { get; }
    public bool[] AllIn { get; }
    /// <summary>Out of chips: out of the game.</summary>
    public bool[] Out { get; }
    public int[][] Hole { get; }
    public List<int> Board { get; } = new();
    /// <summary>The last thing each player did on this street ("check", "call", "bet", "raise", "allin", "fold"), or "".</summary>
    public string[] Acts { get; }
    /// <summary>Each seat's finishing place, 1 for the winner; 0 while still playing.</summary>
    public int[] Places { get; }
    /// <summary>Bets and raises (and calls) by seat and street this hand, for the computer players' reads.</summary>
    public int[][] Raises { get; }
    public int[][] Calls { get; }
    /// <summary>The hands shown at the showdown (all of them when everyone was all in), in the order they were shown.</summary>
    public bool[] Shown { get; }
    public List<int> ShowOrder { get; } = new();
    public List<PokerPot> Results { get; } = new();
    /// <summary>Chips each seat had when the hand was dealt.</summary>
    public int[] HandStart { get; }

    public int HandNumber { get; private set; }
    public int Level { get; private set; }
    public int SmallBlind { get; private set; }
    public int BigBlind { get; private set; }
    public int Dealer { get; private set; }
    public int SmallBlindSeat { get; private set; }
    public int BigBlindSeat { get; private set; }
    public Street Phase { get; private set; }
    /// <summary>The seat to act, or −1 when nobody is (between streets, a run-out, the hand over).</summary>
    public int Turn { get; private set; } = -1;
    public int CurrentBet { get; private set; }
    /// <summary>The smallest raise allowed now: the last full raise, or the big blind.</summary>
    public int MinRaise { get; private set; }
    /// <summary>The last player to bet or raise on this street.</summary>
    public int LastAggressor { get; private set; } = -1;
    /// <summary>Everyone left is all in (bar one at most): the board comes one street at a time through <see cref="Step"/>.</summary>
    public bool RunningOut { get; private set; }
    /// <summary>The hands are face up because everyone left went all in.</summary>
    public bool Revealed { get; private set; }
    public bool HandOver { get; private set; }
    /// <summary>The hand went to a showdown (rather than to the last player who didn't fold).</summary>
    public bool Showdown { get; private set; }
    public bool Over { get; private set; }
    /// <summary>Goes up with every change, so a view can tell it is newer.</summary>
    public int Version { get; private set; }

    bool[] _acted, _canRaise;

    /// <summary>
    /// Tests: the deck for a hand (by its number), in the order the cards go out: two hole cards for each seat in
    /// seat order (seats out of the game are skipped), then the five board cards; the rest may follow.
    /// </summary>
    public Func<int, IReadOnlyList<int>>? DeckFor { get; set; }

    /// <param name="handLimit">0 plays until one player has every chip; otherwise the game ends after this many hands.</param>
    /// <param name="stacks">Tests: each seat's chips to start with, instead of <paramref name="startStack"/> each.</param>
    public PokerRules(int players, Random rng, int handLimit = 0, int handsPerLevel = DefaultHandsPerLevel, int startStack = StartStack,
        int firstDealer = 0, Func<int, IReadOnlyList<int>>? deckFor = null, IReadOnlyList<int>? stacks = null)
    {
        if (players is < MinPlayers or > MaxPlayers) throw new ArgumentOutOfRangeException(nameof(players));
        _rng = rng;
        Players = players;
        HandLimit = Math.Max(0, handLimit);
        HandsPerLevel = Math.Max(1, handsPerLevel);
        Total = stacks?.Sum() ?? startStack * players;
        _firstDealer = Math.Clamp(firstDealer, 0, players - 1);
        DeckFor = deckFor;
        Stacks = stacks?.ToArray() ?? Enumerable.Repeat(startStack, players).ToArray();
        if (Stacks.Length != players) throw new ArgumentException("one stack per seat", nameof(stacks));
        Bets = new int[players];
        Committed = new int[players];
        Folded = new bool[players];
        AllIn = new bool[players];
        Out = new bool[players];
        Acts = Enumerable.Repeat("", players).ToArray();
        Places = new int[players];
        Shown = new bool[players];
        HandStart = new int[players];
        Hole = Enumerable.Range(0, players).Select(_ => Array.Empty<int>()).ToArray();
        Raises = Enumerable.Range(0, players).Select(_ => new int[4]).ToArray();
        Calls = Enumerable.Range(0, players).Select(_ => new int[4]).ToArray();
        _acted = new bool[players];
        _canRaise = new bool[players];
        StartHand();
    }

    public static int BigBlindAt(int level) =>
        level < BigBlinds.Length ? BigBlinds[level] : (int)Math.Min(int.MaxValue / 4, (long)BigBlinds[^1] << Math.Min(24, level - BigBlinds.Length + 1));

    /// <summary>Hands until the blinds go up (counting this one), or 0 in the last hand of a fixed game.</summary>
    public int HandsToNextLevel => HandsPerLevel - (HandNumber - 1) % HandsPerLevel;

    /// <summary>The players still in the game (with chips, or all in on this hand).</summary>
    public int Live => Enumerable.Range(0, Players).Count(s => !Out[s]);

    /// <summary>The chips in the middle: everything put in this hand until it is paid out.</summary>
    public int Pot => HandOver ? 0 : Committed.Sum();

    /// <summary>Every chip in the game, wherever it is: always <see cref="Total"/>.</summary>
    public int ChipsInPlay => Stacks.Sum() + Pot;

    public int Owed(int seat) => Math.Max(0, CurrentBet - Bets[seat]);

    /// <summary>Whether <paramref name="seat"/> may still raise on this street (a short all-in raise doesn't reopen it).</summary>
    public bool CanRaise(int seat) => seat == Turn && _canRaise[seat] && Stacks[seat] > Owed(seat);

    /// <summary>The smallest total bet a raise can make now (all in, if that is less).</summary>
    public int MinRaiseTo(int seat) => Math.Min(CurrentBet + MinRaise, MaxRaiseTo(seat));

    public int MaxRaiseTo(int seat) => Bets[seat] + Stacks[seat];

    // ------------------------------------------------------------------ dealing

    void StartHand()
    {
        HandNumber++;
        Level = (HandNumber - 1) / HandsPerLevel;
        BigBlind = BigBlindAt(Level);
        SmallBlind = BigBlind / 2;
        for (int s = 0; s < Players; s++)
        {
            Out[s] = Stacks[s] <= 0;
            Folded[s] = Out[s];
            AllIn[s] = false;
            Bets[s] = Committed[s] = 0;
            _acted[s] = false;
            _canRaise[s] = !Out[s];
            Acts[s] = "";
            Shown[s] = false;
            Hole[s] = Array.Empty<int>();
            HandStart[s] = Stacks[s];
            Array.Clear(Raises[s]);
            Array.Clear(Calls[s]);
        }
        Board.Clear();
        Results.Clear();
        ShowOrder.Clear();
        HandOver = RunningOut = Revealed = Showdown = false;
        Phase = Street.Preflop;
        LastAggressor = -1;
        Dealer = HandNumber == 1 ? (Out[_firstDealer] ? NextLive(_firstDealer) : _firstDealer) : NextLive(Dealer);

        _deck.Clear();
        var given = DeckFor?.Invoke(HandNumber);
        if (given != null) _deck.AddRange(given);
        var rest = Enumerable.Range(0, PokerHand.Deck).Except(_deck).ToList();
        for (int i = rest.Count - 1; i > 0; i--) // Fisher–Yates
        {
            int j = _rng.Next(i + 1);
            (rest[i], rest[j]) = (rest[j], rest[i]);
        }
        _deck.AddRange(rest);
        _next = 0;
        for (int s = 0; s < Players; s++)
            if (!Out[s]) Hole[s] = new[] { _deck[_next++], _deck[_next++] };

        bool headsUp = Enumerable.Range(0, Players).Count(s => !Out[s]) == 2;
        SmallBlindSeat = headsUp ? Dealer : NextLive(Dealer);
        BigBlindSeat = NextLive(SmallBlindSeat);
        Pay(SmallBlindSeat, SmallBlind);
        Pay(BigBlindSeat, BigBlind);
        CurrentBet = Bets.Max(); // a big blind short of chips only asks the others for what was really posted
        MinRaise = BigBlind;
        Turn = NextToAct(BigBlindSeat);
        if (Turn < 0) EndStreet();
        Version++;
    }

    /// <summary>The next seat after <paramref name="seat"/> that still has chips (or is in this hand).</summary>
    int NextLive(int seat)
    {
        for (int i = 1; i <= Players; i++)
        {
            int s = (seat + i) % Players;
            if (!Out[s]) return s;
        }
        return seat;
    }

    void Pay(int seat, int amount)
    {
        amount = Math.Min(amount, Stacks[seat]);
        Stacks[seat] -= amount;
        Bets[seat] += amount;
        Committed[seat] += amount;
        if (Stacks[seat] == 0 && !Out[seat]) AllIn[seat] = true;
    }

    bool NeedsToAct(int seat)
    {
        if (Out[seat] || Folded[seat] || AllIn[seat]) return false;
        if (_acted[seat] && Bets[seat] >= CurrentBet) return false;
        // alone among the players who can still bet, with nothing to call: there is nobody to bet against
        bool others = Enumerable.Range(0, Players).Any(s => s != seat && !Folded[s] && !AllIn[s]);
        return others || Bets[seat] < CurrentBet;
    }

    int NextToAct(int after)
    {
        for (int i = 1; i <= Players; i++)
        {
            int s = (after + i) % Players;
            if (NeedsToAct(s)) return s;
        }
        return -1;
    }

    // ------------------------------------------------------------------ betting

    /// <summary>A player's move. False if it isn't allowed; nothing changes then.</summary>
    public bool Act(int seat, string kind, int amount = 0)
    {
        if (Over || HandOver || RunningOut || seat < 0 || seat >= Players || seat != Turn) return false;
        int owed = Owed(seat), street = (int)Phase;
        switch (kind)
        {
            case "fold":
                Folded[seat] = true;
                Acts[seat] = "fold";
                break;
            case "check":
                if (owed > 0) return false;
                Acts[seat] = "check";
                break;
            case "call":
                if (owed <= 0) return false;
                Pay(seat, owed);
                Acts[seat] = AllIn[seat] ? "allin" : "call";
                Calls[seat][street]++;
                break;
            case "raise":
                if (!RaiseTo(seat, amount)) return false;
                break;
            case "allin":
                int all = MaxRaiseTo(seat);
                if (all > CurrentBet && _canRaise[seat]) return Act(seat, "raise", all);
                if (owed <= 0) return false;
                return Act(seat, "call"); // can't raise any more: all in means calling with what is left
            default:
                return false;
        }
        _acted[seat] = true;
        _canRaise[seat] = false;
        Advance(seat);
        Version++;
        return true;
    }

    bool RaiseTo(int seat, int to)
    {
        if (!_canRaise[seat]) return false;
        int all = MaxRaiseTo(seat);
        if (to <= CurrentBet || to > all) return false;
        int by = to - CurrentBet;
        if (by < MinRaise && to != all) return false;
        if (by >= MinRaise)
        {
            // a full raise: everyone still in may raise again
            MinRaise = by;
            for (int s = 0; s < Players; s++)
                if (s != seat && !Folded[s] && !AllIn[s]) _canRaise[s] = true;
        }
        bool bet = CurrentBet == 0;
        Pay(seat, to - Bets[seat]);
        CurrentBet = to;
        LastAggressor = seat;
        Acts[seat] = AllIn[seat] ? "allin" : bet ? "bet" : "raise";
        Raises[seat][(int)Phase]++;
        return true;
    }

    void Advance(int seat)
    {
        if (Enumerable.Range(0, Players).Count(s => !Folded[s]) == 1)
        {
            Uncontested();
            return;
        }
        int next = NextToAct(seat);
        if (next >= 0) Turn = next;
        else EndStreet();
    }

    int _riverAggressor = -1;

    /// <summary>The betting on this street is done: deal the next one, or show down.</summary>
    void EndStreet()
    {
        Turn = -1;
        if (Phase == Street.River)
        {
            _riverAggressor = LastAggressor;
            ShowDown();
            return;
        }
        for (int s = 0; s < Players; s++)
        {
            Bets[s] = 0;
            _acted[s] = false;
            _canRaise[s] = !Folded[s] && !AllIn[s];
            if (!Folded[s]) Acts[s] = AllIn[s] ? "allin" : "";
        }
        CurrentBet = 0;
        MinRaise = BigBlind;
        LastAggressor = -1;
        DealStreet();
        if (Enumerable.Range(0, Players).Count(s => !Folded[s] && !AllIn[s]) <= 1)
        {
            // nobody left to bet against: the hands turn up and the board comes out one street at a time
            RunningOut = Revealed = true;
            return;
        }
        Turn = NextToAct(Dealer);
    }

    void DealStreet()
    {
        Phase++;
        int cards = Phase == Street.Flop ? 3 : 1;
        for (int i = 0; i < cards; i++) Board.Add(_deck[_next++]);
    }

    /// <summary>The host's step while the board is run out: the next street, or the showdown after the river. False when there is nothing to step.</summary>
    public bool Step()
    {
        if (!RunningOut || HandOver || Over) return false;
        if (Phase == Street.River)
        {
            _riverAggressor = -1;
            ShowDown();
        }
        else
        {
            for (int s = 0; s < Players; s++) Bets[s] = 0;
            DealStreet();
        }
        Version++;
        return true;
    }

    /// <summary>Everyone else folded: the last player takes every chip without showing.</summary>
    void Uncontested()
    {
        int winner = Enumerable.Range(0, Players).First(s => !Folded[s]);
        int pot = Committed.Sum();
        Stacks[winner] += pot;
        Results.Add(new PokerPot { Amount = pot, Eligible = new[] { winner }, Winners = new[] { winner } });
        EndHand();
    }

    void ShowDown()
    {
        Phase = Street.Showdown;
        RunningOut = false;
        Showdown = true;
        var value = new int[Players];
        for (int s = 0; s < Players; s++)
            if (!Folded[s]) value[s] = PokerHand.Evaluate(Hole[s].Concat(Board).ToArray());
        foreach (var (amount, eligible) in SplitPots(Committed, Folded))
        {
            var pot = new PokerPot { Amount = amount, Eligible = eligible, Returned = eligible.Length == 1 };
            int best = eligible.Max(s => value[s]);
            // the odd chips go to the winners first round from the button
            var winners = eligible.Where(s => value[s] == best).OrderBy(s => (s - Dealer - 1 + Players) % Players).ToArray();
            pot.Winners = winners;
            pot.Value = pot.Returned ? 0 : best;
            for (int i = 0; i < winners.Length; i++) Stacks[winners[i]] += amount / winners.Length + (i < amount % winners.Length ? 1 : 0);
            Results.Add(pot);
        }
        // showing: the last bettor on the river first, else the first player left of the button, then round the table
        int first = _riverAggressor >= 0 && !Folded[_riverAggressor] ? _riverAggressor : NextIn(Dealer);
        int shownBest = -1;
        for (int i = 0; i < Players; i++)
        {
            int s = (first + i) % Players;
            if (Folded[s]) continue;
            ShowOrder.Add(s);
            bool wins = Results.Any(p => !p.Returned && p.Winners.Contains(s));
            if (Revealed || shownBest < 0 || wins || value[s] >= shownBest)
            {
                Shown[s] = true;
                shownBest = Math.Max(shownBest, value[s]);
            }
        }
        EndHand();
    }

    int NextIn(int seat)
    {
        for (int i = 1; i <= Players; i++)
        {
            int s = (seat + i) % Players;
            if (!Folded[s]) return s;
        }
        return seat;
    }

    /// <summary>
    /// The chips in the middle as pots, by what each player put in: the main pot everyone still in can win, then a side
    /// pot for each all-in level above it. A pot with one player in it is chips nobody called, going back.
    /// </summary>
    public static List<(int Amount, int[] Eligible)> SplitPots(IReadOnlyList<int> committed, IReadOnlyList<bool> folded)
    {
        var pots = new List<(int Amount, int[] Eligible)>();
        var levels = committed.Where((c, s) => !folded[s] && c > 0).Distinct().OrderBy(c => c).ToList();
        int prev = 0;
        foreach (int level in levels)
        {
            int amount = 0;
            for (int s = 0; s < committed.Count; s++) amount += Math.Min(committed[s], level) - Math.Min(committed[s], prev);
            var eligible = Enumerable.Range(0, committed.Count).Where(s => !folded[s] && committed[s] >= level).ToArray();
            pots.Add((amount, eligible));
            prev = level;
        }
        int rest = committed.Sum() - pots.Sum(p => p.Amount);
        if (rest > 0 && pots.Count > 0) pots[^1] = (pots[^1].Amount + rest, pots[^1].Eligible); // folded chips above every live player
        return pots;
    }

    void EndHand()
    {
        HandOver = true;
        Turn = -1;
        RunningOut = false;
        for (int s = 0; s < Players; s++) Bets[s] = 0;
        // players who ran out of chips this hand: the one who started it with more finishes higher
        var busted = Enumerable.Range(0, Players).Where(s => !Out[s] && Stacks[s] == 0).ToList();
        int alive = Enumerable.Range(0, Players).Count(s => Stacks[s] > 0);
        foreach (int s in busted)
        {
            Places[s] = alive + 1 + busted.Count(o => HandStart[o] > HandStart[s]);
            Out[s] = true;
        }
        if (alive <= 1)
        {
            Over = true;
            foreach (int s in Enumerable.Range(0, Players).Where(s => Stacks[s] > 0)) Places[s] = 1;
        }
        else if (HandLimit > 0 && HandNumber >= HandLimit)
        {
            Over = true;
            foreach (int s in Enumerable.Range(0, Players).Where(s => Stacks[s] > 0))
                Places[s] = 1 + Enumerable.Range(0, Players).Count(o => Stacks[o] > Stacks[s]);
        }
    }

    /// <summary>After a hand: deals the next one. False while a hand is on, or when the game is over.</summary>
    public bool NextHand()
    {
        if (!HandOver || Over) return false;
        StartHand();
        return true;
    }

    /// <summary>The players who finished first (several when a fixed game ends level).</summary>
    public IEnumerable<int> Winners => Enumerable.Range(0, Players).Where(s => Places[s] == 1);

    // ------------------------------------------------------------------ reads for the computer players

    /// <summary>The live players still to act on this street after <paramref name="seat"/>, in order.</summary>
    public int ToActAfter(int seat)
    {
        int n = 0;
        for (int i = 1; i < Players; i++)
        {
            int s = (seat + i) % Players;
            if (s != seat && NeedsToAct(s)) n++;
        }
        return n;
    }

    /// <summary>Whether <paramref name="seat"/> acts last after the flop among the players who can still bet.</summary>
    public bool InPosition(int seat)
    {
        int last = -1;
        for (int i = 1; i <= Players; i++)
        {
            int s = (Dealer + i) % Players;
            if (!Folded[s] && !AllIn[s]) last = s;
        }
        return last == seat;
    }
}
