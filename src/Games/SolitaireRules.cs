using System;
using System.Collections.Generic;
using System.Linq;

namespace DeskArcade.Games;

/// <summary>
/// Klondike solitaire, draw one, free of UI. 52 cards: 28 dealt into seven tableau piles (1 to 7 cards, only the
/// top one face up), the rest in the stock.
/// <list type="bullet">
/// <item>Click the stock to turn its top card onto the waste; an empty stock takes the waste back, as often as
/// you like.</item>
/// <item>Foundations build up by suit from the ace to the king, one per suit.</item>
/// <item>Tableau piles build down in alternating colours. Any face-up run moves as a unit; only a king (or a
/// run headed by one) goes into an empty pile. A face-down card left on top turns up by itself.</item>
/// <item>A card may come back down from a foundation onto the tableau.</item>
/// </list>
/// Cards are numbered suit × 13 + (rank − 1): ranks 1 (ace) to 13 (king); suits ♠ 0, ♣ 1, ♦ 2, ♥ 3, the last two
/// red. Every move that changes the layout counts, turning the stock included; <see cref="Undo"/> takes one back.
/// </summary>
public sealed class SolitaireRules
{
    public const int Piles = 7, Suits = 4, Ranks = 13, DeckSize = Suits * Ranks;

    public static int Suit(int card) => card / Ranks;
    public static int Rank(int card) => card % Ranks + 1;
    public static int Card(int suit, int rank) => suit * Ranks + rank - 1;
    public static bool Red(int card) => Suit(card) >= 2;

    public enum Zone { Stock, Waste, Foundation, Tableau }

    /// <summary>A pile: the stock, the waste, foundation 0–3 (by suit) or tableau pile 0–6.</summary>
    public readonly record struct Spot(Zone Zone, int Index = 0)
    {
        public static readonly Spot Stock = new(Zone.Stock);
        public static readonly Spot Waste = new(Zone.Waste);
        public static Spot Foundation(int suit) => new(Zone.Foundation, suit);
        public static Spot Tableau(int pile) => new(Zone.Tableau, pile);
    }

    /// <summary>A move of the top <paramref name="Count"/> cards of one pile onto another.</summary>
    public readonly record struct Move(Spot From, int Count, Spot To);

    readonly List<int> _stock = new(), _waste = new();
    readonly List<int>[] _foundations = Enumerable.Range(0, Suits).Select(_ => new List<int>()).ToArray();
    readonly List<int>[] _tableau = Enumerable.Range(0, Piles).Select(_ => new List<int>()).ToArray();
    readonly int[] _hidden = new int[Piles];
    readonly Stack<Snapshot> _undo = new();

    sealed record Snapshot(int[] Stock, int[] Waste, int[][] Foundations, int[][] Tableau, int[] Hidden);

    /// <summary>A shuffled deal.</summary>
    public SolitaireRules(Random rng) : this(Shuffled(rng)) { }

    /// <summary>
    /// A deal from a known order: the first 28 cards go to the tableau row by row (pile 0 gets one, pile 6
    /// seven; the last card dealt to each pile is its face-up top), the rest form the stock, its top card last.
    /// </summary>
    public SolitaireRules(IReadOnlyList<int> deck)
    {
        if (deck.Count != DeckSize || deck.Distinct().Count() != DeckSize || deck.Any(c => c < 0 || c >= DeckSize))
            throw new ArgumentException("A deck is each of the 52 cards once.", nameof(deck));
        int next = 0;
        for (int row = 0; row < Piles; row++)
            for (int pile = row; pile < Piles; pile++)
                _tableau[pile].Add(deck[next++]);
        for (int pile = 0; pile < Piles; pile++) _hidden[pile] = pile;
        while (next < DeckSize) _stock.Add(deck[next++]);
    }

    static int[] Shuffled(Random rng)
    {
        var deck = Enumerable.Range(0, DeckSize).ToArray();
        for (int i = deck.Length - 1; i > 0; i--) // Fisher–Yates
        {
            int j = rng.Next(i + 1);
            (deck[i], deck[j]) = (deck[j], deck[i]);
        }
        return deck;
    }

    public IReadOnlyList<int> Stock => _stock;
    public IReadOnlyList<int> Waste => _waste;
    public IReadOnlyList<int> Foundation(int suit) => _foundations[suit];
    public IReadOnlyList<int> Tableau(int pile) => _tableau[pile];

    /// <summary>How many cards at the bottom of a tableau pile are face down.</summary>
    public int Hidden(int pile) => _hidden[pile];

    public int Moves { get; private set; }
    public int OnFoundations => _foundations.Sum(f => f.Count);
    public bool Won => OnFoundations == DeckSize;
    public bool CanUndo => _undo.Count > 0;

    public IReadOnlyList<int> Cards(Spot spot) => spot.Zone switch
    {
        Zone.Stock => _stock,
        Zone.Waste => _waste,
        Zone.Foundation => _foundations[spot.Index],
        _ => _tableau[spot.Index],
    };

    /// <summary>How many cards from the top of a pile can be picked up together: 1 for the waste or a foundation.</summary>
    public int Movable(Spot spot) => spot.Zone switch
    {
        Zone.Waste or Zone.Foundation => Cards(spot).Count > 0 ? 1 : 0,
        Zone.Tableau => _tableau[spot.Index].Count - _hidden[spot.Index],
        _ => 0,
    };

    /// <summary>Turns the stock's top card onto the waste, or the whole waste back into the stock when the stock is empty.</summary>
    public bool Draw()
    {
        if (_stock.Count == 0 && _waste.Count == 0) return false;
        Save();
        if (_stock.Count > 0)
        {
            _waste.Add(_stock[^1]);
            _stock.RemoveAt(_stock.Count - 1);
        }
        else
        {
            for (int i = _waste.Count - 1; i >= 0; i--) _stock.Add(_waste[i]);
            _waste.Clear();
        }
        Moves++;
        return true;
    }

    public bool IsLegal(Move m)
    {
        if (m.Count < 1 || m.Count > Movable(m.From) || m.From == m.To) return false;
        int card = Cards(m.From)[^m.Count];
        switch (m.To.Zone)
        {
            case Zone.Foundation:
                var f = _foundations[m.To.Index];
                return m.Count == 1 && Suit(card) == m.To.Index && Rank(card) == f.Count + 1;
            case Zone.Tableau:
                var t = _tableau[m.To.Index];
                if (t.Count == 0) return Rank(card) == Ranks;
                int top = t[^1];
                return Red(top) != Red(card) && Rank(top) == Rank(card) + 1;
            default:
                return false;
        }
    }

    public bool Apply(Move m)
    {
        if (!IsLegal(m)) return false;
        Save();
        var from = (List<int>)Cards(m.From);
        var to = (List<int>)Cards(m.To);
        to.AddRange(from.GetRange(from.Count - m.Count, m.Count));
        from.RemoveRange(from.Count - m.Count, m.Count);
        if (m.From.Zone == Zone.Tableau)
        {
            int pile = m.From.Index;
            if (_hidden[pile] > 0 && _hidden[pile] == _tableau[pile].Count) _hidden[pile]--; // turn the new top up
        }
        Moves++;
        return true;
    }

    /// <summary>
    /// Where a one-click move of the top <paramref name="count"/> cards of <paramref name="from"/> should go:
    /// a single card goes home to its foundation first, then onto a tableau pile, a non-empty one before an
    /// empty one (a king that already heads a pile stays put). Null when nothing fits.
    /// </summary>
    public Move? Best(Spot from, int count)
    {
        if (count == 1 && from.Zone != Zone.Foundation)
        {
            var home = new Move(from, 1, Spot.Foundation(Suit(Cards(from)[^1])));
            if (IsLegal(home)) return home;
        }
        Move? empty = null;
        for (int k = 1; k <= Piles; k++)
        {
            int pile = from.Zone == Zone.Tableau ? (from.Index + k) % Piles : k - 1; // look right of the pile first
            var m = new Move(from, count, Spot.Tableau(pile));
            if (!IsLegal(m)) continue;
            if (_tableau[pile].Count > 0) return m;
            bool pointless = from.Zone == Zone.Tableau && count == Cards(from).Count; // a king moving between empty piles
            if (!pointless) empty ??= m;
        }
        return empty;
    }

    /// <summary>Every card is face up and the stock and waste are empty: the rest can go home by itself.</summary>
    public bool CanFinish => !Won && _stock.Count == 0 && _waste.Count == 0 && _hidden.All(h => h == 0);

    /// <summary>The next card to send home: the lowest one that fits a foundation, or null.</summary>
    public Move? NextHome()
    {
        Move? best = null;
        int lowest = int.MaxValue;
        var sources = new List<Spot> { Spot.Waste };
        for (int p = 0; p < Piles; p++) sources.Add(Spot.Tableau(p));
        foreach (var s in sources)
        {
            if (Cards(s).Count == 0 || Movable(s) == 0) continue;
            int card = Cards(s)[^1];
            var m = new Move(s, 1, Spot.Foundation(Suit(card)));
            if (IsLegal(m) && Rank(card) < lowest)
            {
                lowest = Rank(card);
                best = m;
            }
        }
        return best;
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var s = _undo.Pop();
        Restore(_stock, s.Stock);
        Restore(_waste, s.Waste);
        for (int i = 0; i < Suits; i++) Restore(_foundations[i], s.Foundations[i]);
        for (int i = 0; i < Piles; i++) Restore(_tableau[i], s.Tableau[i]);
        s.Hidden.CopyTo(_hidden, 0);
        Moves++; // an undo is a move too, so it never lowers the count
        return true;
    }

    static void Restore(List<int> into, int[] from)
    {
        into.Clear();
        into.AddRange(from);
    }

    void Save() => _undo.Push(new Snapshot(_stock.ToArray(), _waste.ToArray(),
        _foundations.Select(f => f.ToArray()).ToArray(), _tableau.Select(t => t.ToArray()).ToArray(), (int[])_hidden.Clone()));
}
