using System;
using System.Collections.Generic;
using System.Linq;
using static DeskArcade.Games.SolitaireRules;

namespace DeskArcade.Games;

/// <summary>
/// FreeCell, free of UI. All 52 cards are dealt face up into eight cascades (the first four get seven cards, the rest
/// six), with four free cells and four foundations above.
/// <list type="bullet">
/// <item>A free cell holds any one card.</item>
/// <item>Foundations build up by suit from the ace to the king; a card never comes back down.</item>
/// <item>Cascades build down in alternating colours, and any card goes into an empty cascade.</item>
/// <item>A run moves as a unit when there is room to shuffle it across one card at a time: (free cells + 1) ×
/// 2^(empty cascades), with the empty cascade it goes into not counted (<see cref="MaxMoveFor"/>). It counts as one
/// move.</item>
/// </list>
/// Deals are numbered the way Microsoft's FreeCell numbers them (<see cref="MsDeal"/>), so "deal 11982" is the same
/// layout everywhere. Cards are numbered as in <see cref="SolitaireRules"/>. Cards no longer needed on the table go
/// home by themselves (<see cref="SafeHome"/>, <see cref="Autoplay"/>), as part of the move before, which
/// <see cref="Undo"/> takes back with it.
/// </summary>
public sealed class FreeCellRules : IPatience
{
    public const int Cascades = 8, FreeCells = 4;

    /// <summary>The deal numbers the chip picks from at random: Microsoft's original 32,000.</summary>
    public const int ClassicDeals = 32000;

    /// <summary>The highest deal number that can be typed in (the numbering carries on past the classic 32,000).</summary>
    public const int MaxDeal = 1_000_000;

    readonly List<int>[] _cascades = Enumerable.Range(0, Cascades).Select(_ => new List<int>()).ToArray();
    readonly List<int>[] _cells = Enumerable.Range(0, FreeCells).Select(_ => new List<int>()).ToArray();
    readonly List<int>[] _foundations = Enumerable.Range(0, Suits).Select(_ => new List<int>()).ToArray();
    readonly Stack<Snapshot> _undo = new();

    sealed record Snapshot(int[][] Cascades, int[][] Cells, int[][] Foundations);

    /// <summary>Deal number <paramref name="deal"/> (1 to <see cref="MaxDeal"/>).</summary>
    public FreeCellRules(int deal) : this(Columns(MsDeal(deal))) => Deal = deal;

    /// <summary>A hand-made layout: the cascades bottom first, cards already in free cells, and how far each suit is home.</summary>
    public FreeCellRules(IReadOnlyList<IReadOnlyList<int>> cascades, IReadOnlyList<int>? cells = null, IReadOnlyList<int>? home = null)
    {
        if (cascades.Count != Cascades || (cells?.Count ?? 0) > FreeCells) throw new ArgumentException("Eight cascades and at most four free cells.");
        for (int c = 0; c < Cascades; c++) _cascades[c].AddRange(cascades[c]);
        for (int i = 0; i < (cells?.Count ?? 0); i++) _cells[i].Add(cells![i]);
        for (int s = 0; s < Suits && home != null; s++)
            for (int r = 1; r <= home[s]; r++) _foundations[s].Add(Card(s, r));
        var all = _cascades.Concat(_cells).Concat(_foundations).SelectMany(p => p).ToList();
        if (all.Count != DeckSize || all.Distinct().Count() != DeckSize || all.Any(c => c < 0 || c >= DeckSize))
            throw new ArgumentException("A layout is each of the 52 cards once.");
    }

    /// <summary>The deal's number, or 0 for a hand-made layout.</summary>
    public int Deal { get; }

    /// <summary>
    /// Microsoft's deal <paramref name="deal"/>: the 52 cards in the order they are dealt, one to each cascade in turn.
    /// The deck starts as A♣ A♦ A♥ A♠ 2♣ … K♠; the generator is Microsoft C's rand() seeded with the deal number
    /// (state × 214013 + 2531011, mod 2³¹, and the top 15 bits of the state); each card dealt is the one at
    /// rand() mod the cards left, swapped out for the last card.
    /// </summary>
    public static int[] MsDeal(int deal)
    {
        if (deal < 1 || deal > MaxDeal) throw new ArgumentOutOfRangeException(nameof(deal));
        int[] msSuit = { 1, 2, 3, 0 }; // clubs, diamonds, hearts, spades as numbered here
        var deck = new List<int>(DeckSize);
        for (int i = 0; i < DeckSize; i++) deck.Add(Card(msSuit[i % 4], i / 4 + 1));
        uint state = (uint)deal;
        var dealt = new int[DeckSize];
        for (int n = 0; n < DeckSize; n++)
        {
            state = unchecked(state * 214013 + 2531011) & 0x7FFFFFFF;
            int left = DeckSize - n, j = (int)(state >> 16) % left;
            dealt[n] = deck[j];
            deck[j] = deck[left - 1];
            deck.RemoveAt(left - 1);
        }
        return dealt;
    }

    /// <summary>The cascades a dealing order makes: card n goes onto cascade n mod 8.</summary>
    public static IReadOnlyList<int>[] Columns(IReadOnlyList<int> dealt)
    {
        var columns = Enumerable.Range(0, Cascades).Select(_ => new List<int>()).ToArray();
        for (int n = 0; n < dealt.Count; n++) columns[n % Cascades].Add(dealt[n]);
        return columns;
    }

    public int Total => DeckSize;
    public int Home => _foundations.Sum(f => f.Count);
    public int Moves { get; private set; }
    public bool Won => Home == DeckSize;
    public bool CanUndo => _undo.Count > 0;
    public (int Suit, int Rank) Face(int card) => (Suit(card), Rank(card));

    public int FreeCellsLeft => _cells.Count(c => c.Count == 0);
    public int EmptyCascades => _cascades.Count(c => c.Count == 0);

    /// <summary>A pile's cards; FreeCell has no stock or waste, and they are always empty.</summary>
    public IReadOnlyList<int> Cards(Spot spot) => spot.Zone is Zone.Stock or Zone.Waste ? Array.Empty<int>() : Pile(spot);

    List<int> Pile(Spot spot) => spot.Zone switch
    {
        Zone.Cell => _cells[spot.Index],
        Zone.Foundation => _foundations[spot.Index],
        Zone.Tableau => _cascades[spot.Index],
        _ => throw new ArgumentException("FreeCell has no " + spot.Zone, nameof(spot)),
    };

    public int Hidden(Spot spot) => 0;

    /// <summary>A free cell's card, or the run at the top of a cascade: down in alternating colours. Foundations keep their cards.</summary>
    public int Movable(Spot spot) => spot.Zone switch
    {
        Zone.Cell => _cells[spot.Index].Count,
        Zone.Tableau => RunLength(_cascades[spot.Index]),
        _ => 0,
    };

    static int RunLength(List<int> pile)
    {
        if (pile.Count == 0) return 0;
        int n = 1;
        while (n < pile.Count && Builds(pile[^n], pile[^(n + 1)])) n++;
        return n;
    }

    /// <summary>True if <paramref name="card"/> may sit on <paramref name="under"/> in a cascade: one rank lower, the other colour.</summary>
    public static bool Builds(int card, int under) => Red(card) != Red(under) && Rank(under) == Rank(card) + 1;

    /// <summary>The longest run that can move at once with these free cells and empty cascades (the target not counted).</summary>
    public static int MaxMoveFor(int freeCells, int emptyCascades) => (freeCells + 1) << Math.Max(0, emptyCascades);

    /// <summary>The longest run that can move onto <paramref name="to"/> now.</summary>
    public int MaxMove(Spot to)
    {
        bool intoEmpty = to.Zone == Zone.Tableau && _cascades[to.Index].Count == 0;
        return MaxMoveFor(FreeCellsLeft, EmptyCascades - (intoEmpty ? 1 : 0));
    }

    public bool IsLegal(Move m)
    {
        if (m.Count < 1 || m.Count > Movable(m.From) || m.From == m.To) return false;
        int card = Pile(m.From)[^m.Count];
        switch (m.To.Zone)
        {
            case Zone.Cell:
                return m.Count == 1 && m.To.Index is >= 0 and < FreeCells && _cells[m.To.Index].Count == 0;
            case Zone.Foundation:
                return m.Count == 1 && Suit(card) == m.To.Index && Rank(card) == _foundations[m.To.Index].Count + 1;
            case Zone.Tableau:
                if (m.Count > MaxMove(m.To)) return false;
                var t = _cascades[m.To.Index];
                return t.Count == 0 || Builds(card, t[^1]);
            default:
                return false;
        }
    }

    /// <summary>The run would fit where it is going, but there is not the free space to move that many cards at once.</summary>
    public bool TooBig(Move m)
    {
        if (m.To.Zone != Zone.Tableau || m.From == m.To || m.Count < 2 || m.Count > Movable(m.From) || m.Count <= MaxMove(m.To)) return false;
        var t = _cascades[m.To.Index];
        return t.Count == 0 || Builds(Pile(m.From)[^m.Count], t[^1]);
    }

    public bool Apply(Move m)
    {
        if (!IsLegal(m)) return false;
        Save();
        Shift(m);
        Moves++;
        return true;
    }

    void Shift(Move m)
    {
        var from = Pile(m.From);
        Pile(m.To).AddRange(from.GetRange(from.Count - m.Count, m.Count));
        from.RemoveRange(from.Count - m.Count, m.Count);
    }

    /// <summary>
    /// Where a one-click move should go: a single card home first, then onto a cascade it builds on (looking right of
    /// its own first), then a lone card from a cascade into a free cell, then an empty cascade. Null when nothing fits.
    /// </summary>
    public Move? Best(Spot from, int count)
    {
        if (count < 1 || count > Movable(from)) return null;
        if (count == 1)
        {
            var home = new Move(from, 1, Spot.Foundation(Suit(Pile(from)[^1])));
            if (IsLegal(home)) return home;
        }
        Move? empty = null;
        for (int k = 1; k <= Cascades; k++)
        {
            int pile = from.Zone == Zone.Tableau ? (from.Index + k) % Cascades : k - 1;
            var m = new Move(from, count, Spot.Tableau(pile));
            if (!IsLegal(m)) continue;
            if (_cascades[pile].Count > 0) return m;
            bool pointless = from.Zone == Zone.Tableau && count == Pile(from).Count; // a whole cascade into an empty one
            if (!pointless) empty ??= m;
        }
        if (count == 1 && from.Zone == Zone.Tableau)
            for (int c = 0; c < FreeCells; c++)
                if (_cells[c].Count == 0) return new Move(from, 1, Spot.Cell(c));
        return empty;
    }

    /// <summary>The next card to send home: the lowest one that fits a foundation, from a free cell or a cascade.</summary>
    public Move? NextHome()
    {
        Move? best = null;
        int lowest = int.MaxValue;
        foreach (var s in Sources())
        {
            var pile = Pile(s);
            if (pile.Count == 0) continue;
            var m = new Move(s, 1, Spot.Foundation(Suit(pile[^1])));
            if (IsLegal(m) && Rank(pile[^1]) < lowest)
            {
                lowest = Rank(pile[^1]);
                best = m;
            }
        }
        return best;
    }

    /// <summary>
    /// A card that can go home by itself because nothing still in play could need it: an ace, a two, or a card whose
    /// two lower cards of the other colour are both home already (the only cards that could have gone on it). Free
    /// cells first, then the cascades from the left. Null when there is none.
    /// </summary>
    public Move? SafeHome()
    {
        foreach (var s in Sources())
        {
            var pile = Pile(s);
            if (pile.Count == 0) continue;
            int card = pile[^1];
            var m = new Move(s, 1, Spot.Foundation(Suit(card)));
            if (IsLegal(m) && Safe(card, _foundations.Select(f => f.Count).ToArray())) return m;
        }
        return null;
    }

    /// <summary>The safe-to-send-home test of <see cref="SafeHome"/>, given how many cards of each suit are home.</summary>
    public static bool Safe(int card, IReadOnlyList<int> homeBySuit)
    {
        int rank = Rank(card);
        if (rank <= 2) return true;
        for (int s = 0; s < Suits; s++)
            if (Red(Card(s, 1)) != Red(card) && homeBySuit[s] < rank - 1) return false;
        return true;
    }

    IEnumerable<Spot> Sources()
    {
        for (int c = 0; c < FreeCells; c++) yield return Spot.Cell(c);
        for (int p = 0; p < Cascades; p++) yield return Spot.Tableau(p);
    }

    /// <summary>Sends a card home as part of the move before it: no undo step of its own and not counted as a move.</summary>
    public bool Autoplay(Move m)
    {
        if (m.To.Zone != Zone.Foundation || !IsLegal(m)) return false;
        Shift(m);
        return true;
    }

    /// <summary>Every cascade runs down from the bottom card up: whatever is in the cells, the rest can go home in order.</summary>
    public bool CanFinish => !Won && _cascades.All(c =>
    {
        for (int i = 1; i < c.Count; i++)
            if (Rank(c[i]) >= Rank(c[i - 1])) return false;
        return true;
    });

    public bool Draw() => false;

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var s = _undo.Pop();
        for (int i = 0; i < Cascades; i++) Restore(_cascades[i], s.Cascades[i]);
        for (int i = 0; i < FreeCells; i++) Restore(_cells[i], s.Cells[i]);
        for (int i = 0; i < Suits; i++) Restore(_foundations[i], s.Foundations[i]);
        Moves++; // as in Klondike, an undo is a move too
        return true;
    }

    static void Restore(List<int> into, int[] from)
    {
        into.Clear();
        into.AddRange(from);
    }

    void Save() => _undo.Push(new Snapshot(_cascades.Select(c => c.ToArray()).ToArray(), _cells.Select(c => c.ToArray()).ToArray(),
        _foundations.Select(f => f.ToArray()).ToArray()));
}
