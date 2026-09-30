using System;
using System.Collections.Generic;
using System.Linq;
using static DeskArcade.Games.SolitaireRules;

namespace DeskArcade.Games;

/// <summary>
/// Spider, free of UI: two decks, 104 cards, in one, two or four suits (<see cref="Suits"/>). 54 cards are dealt into ten
/// columns (the first four get six, the rest five), only the top card of each face up; the other 50 wait in the stock.
/// <list type="bullet">
/// <item>Any card goes on a card one rank higher, whatever its suit, and any card or run goes into an empty column.</item>
/// <item>Only a run of one suit, down in order, moves as a unit.</item>
/// <item>A click on the stock deals one card face up onto every column, but only when no column is empty.</item>
/// <item>A whole suit, king down to ace in one suit, leaves the table by itself; eight of them win.</item>
/// <item>A face-down card left on top turns up by itself.</item>
/// </list>
/// Card n (0–103) is rank n mod 13 + 1 of copy n ÷ 13; <see cref="SuitOf"/> says which suit a copy is in each game.
/// Every move and deal counts; <see cref="Undo"/> takes one back.
/// </summary>
public sealed class SpiderRules : IPatience
{
    public const int Columns = 10, DeckSize = 104, Suits13 = 8, RowsDealt = 5;

    readonly List<int> _stock = new();
    readonly List<int>[] _columns = Enumerable.Range(0, Columns).Select(_ => new List<int>()).ToArray();
    readonly int[] _hidden = new int[Columns];
    readonly List<int>[] _done = Enumerable.Range(0, Suits13).Select(_ => new List<int>()).ToArray();
    readonly Stack<Snapshot> _undo = new();
    int _doneCount;

    sealed record Snapshot(int[] Stock, int[][] Columns, int[] Hidden, int[][] Done, int DoneCount);

    /// <summary>A shuffled deal in <paramref name="suits"/> suits (1, 2 or 4).</summary>
    public SpiderRules(int suits, Random rng) : this(suits, Shuffled(rng)) { }

    /// <summary>
    /// A deal from a known order: the first 54 cards go to the columns row by row (a row of ten, five times over, then
    /// one more each for the first four columns), the rest form the stock, its top card last.
    /// </summary>
    public SpiderRules(int suits, IReadOnlyList<int> deck)
    {
        if (suits is not (1 or 2 or 4)) throw new ArgumentOutOfRangeException(nameof(suits));
        if (deck.Count != DeckSize || deck.Distinct().Count() != DeckSize || deck.Any(c => c < 0 || c >= DeckSize))
            throw new ArgumentException("A Spider deck is each of the 104 cards once.", nameof(deck));
        Suits = suits;
        int next = 0;
        for (int row = 0; row < 6; row++)
            for (int col = 0; col < Columns; col++)
                if (row < 5 || col < 4) _columns[col].Add(deck[next++]);
        for (int col = 0; col < Columns; col++) _hidden[col] = _columns[col].Count - 1;
        while (next < DeckSize) _stock.Add(deck[next++]);
    }

    /// <summary>A hand-made table (for tests): the columns bottom first with how many of each are face down, and the stock.</summary>
    public SpiderRules(int suits, IReadOnlyList<IReadOnlyList<int>> columns, IReadOnlyList<int> hidden, IReadOnlyList<int> stock, int suitsDone = 0)
    {
        Suits = suits;
        for (int c = 0; c < Columns; c++)
        {
            _columns[c].AddRange(columns[c]);
            _hidden[c] = hidden[c];
        }
        _stock.AddRange(stock);
        _doneCount = suitsDone;
    }

    static int[] Shuffled(Random rng)
    {
        var deck = Enumerable.Range(0, DeckSize).ToArray();
        for (int i = deck.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (deck[i], deck[j]) = (deck[j], deck[i]);
        }
        return deck;
    }

    /// <summary>1, 2 or 4.</summary>
    public int Suits { get; }

    /// <summary>
    /// The suit of card <paramref name="card"/> in a game of <paramref name="suits"/> suits: all spades in one suit, spades
    /// and hearts in two, the four suits twice over in four.
    /// </summary>
    public static int SuitOf(int suits, int card)
    {
        int copy = card / Ranks;
        return suits switch { 1 => 0, 2 => copy % 2 == 0 ? 0 : 3, _ => copy % 4 };
    }

    public static int RankOf(int card) => card % Ranks + 1;

    int SuitOf(int card) => SuitOf(Suits, card);

    public (int Suit, int Rank) Face(int card) => (SuitOf(card), RankOf(card));

    public int Total => DeckSize;

    /// <summary>The suits taken off the table so far.</summary>
    public int SuitsDone => _doneCount;

    public int Home => _doneCount * Ranks;
    public int Moves { get; private set; }
    public bool Won => _doneCount == Suits13;
    public bool CanUndo => _undo.Count > 0;
    public bool CanFinish => false; // whole suits leave the table by themselves

    public IReadOnlyList<int> Stock => _stock;

    /// <summary>The rows still to deal from the stock.</summary>
    public int DealsLeft => _stock.Count / Columns;

    /// <summary>A row can be dealt: there are cards in the stock and no column is empty.</summary>
    public bool CanDeal => _stock.Count >= Columns && _columns.All(c => c.Count > 0);

    public IReadOnlyList<int> Cards(Spot spot) => spot.Zone switch
    {
        Zone.Stock => _stock,
        Zone.Foundation => _done[spot.Index],
        Zone.Tableau => _columns[spot.Index],
        _ => Array.Empty<int>(),
    };

    public int Hidden(Spot spot) => spot.Zone switch
    {
        Zone.Tableau => _hidden[spot.Index],
        Zone.Stock => _stock.Count,
        _ => 0,
    };

    /// <summary>The run at the top of a column: face up, one suit, down in order. Nothing else moves.</summary>
    public int Movable(Spot spot) => spot.Zone == Zone.Tableau ? Run(spot.Index) : 0;

    int Run(int col)
    {
        var pile = _columns[col];
        int up = pile.Count - _hidden[col];
        if (up <= 0) return 0;
        int n = 1;
        while (n < up && SuitOf(pile[^n]) == SuitOf(pile[^(n + 1)]) && RankOf(pile[^(n + 1)]) == RankOf(pile[^n]) + 1) n++;
        return n;
    }

    public bool IsLegal(Move m)
    {
        if (m.From.Zone != Zone.Tableau || m.To.Zone != Zone.Tableau || m.From == m.To) return false;
        if (m.Count < 1 || m.Count > Movable(m.From)) return false;
        var to = _columns[m.To.Index];
        return to.Count == 0 || RankOf(to[^1]) == RankOf(_columns[m.From.Index][^m.Count]) + 1;
    }

    public bool Apply(Move m)
    {
        if (!IsLegal(m)) return false;
        Save();
        var from = _columns[m.From.Index];
        _columns[m.To.Index].AddRange(from.GetRange(from.Count - m.Count, m.Count));
        from.RemoveRange(from.Count - m.Count, m.Count);
        TurnUp(m.From.Index);
        Collect(m.To.Index);
        Moves++;
        return true;
    }

    /// <summary>Deals a row: one card face up onto each column, when the stock has them and no column is empty.</summary>
    public bool Draw()
    {
        if (!CanDeal) return false;
        Save();
        for (int col = 0; col < Columns; col++)
        {
            _columns[col].Add(_stock[^1]);
            _stock.RemoveAt(_stock.Count - 1);
        }
        for (int col = 0; col < Columns; col++) Collect(col);
        Moves++;
        return true;
    }

    /// <summary>A face-down card left on top turns up.</summary>
    void TurnUp(int col)
    {
        if (_hidden[col] > 0 && _hidden[col] >= _columns[col].Count) _hidden[col] = _columns[col].Count - 1;
    }

    /// <summary>A king-to-ace run of one suit on top of the column leaves the table; its king ends up on top of the pile it goes to.</summary>
    void Collect(int col)
    {
        if (Run(col) < Ranks || _doneCount >= Suits13) return;
        var pile = _columns[col];
        var suit = pile.GetRange(pile.Count - Ranks, Ranks);
        suit.Reverse(); // ace at the bottom, king on top
        _done[_doneCount++].AddRange(suit);
        pile.RemoveRange(pile.Count - Ranks, Ranks);
        TurnUp(col);
    }

    /// <summary>
    /// Where a one-click move of the top <paramref name="count"/> cards should go: onto a card of the same suit first,
    /// then onto any card one rank higher (looking right of the column first), then an empty column. Null when nothing fits.
    /// </summary>
    public Move? Best(Spot from, int count)
    {
        if (from.Zone != Zone.Tableau || count < 1 || count > Movable(from)) return null;
        int card = _columns[from.Index][^count];
        Move? any = null, empty = null;
        for (int k = 1; k < Columns; k++)
        {
            int col = (from.Index + k) % Columns;
            var m = new Move(from, count, Spot.Tableau(col));
            if (!IsLegal(m)) continue;
            var to = _columns[col];
            if (to.Count == 0)
            {
                if (count < _columns[from.Index].Count) empty ??= m; // a whole column into an empty one is pointless
                continue;
            }
            if (SuitOf(to[^1]) == SuitOf(card)) return m;
            any ??= m;
        }
        return any ?? empty;
    }

    public Move? NextHome() => null;

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var s = _undo.Pop();
        _stock.Clear();
        _stock.AddRange(s.Stock);
        for (int i = 0; i < Columns; i++)
        {
            _columns[i].Clear();
            _columns[i].AddRange(s.Columns[i]);
        }
        s.Hidden.CopyTo(_hidden, 0);
        for (int i = 0; i < Suits13; i++)
        {
            _done[i].Clear();
            _done[i].AddRange(s.Done[i]);
        }
        _doneCount = s.DoneCount;
        Moves++;
        return true;
    }

    void Save() => _undo.Push(new Snapshot(_stock.ToArray(), _columns.Select(c => c.ToArray()).ToArray(), (int[])_hidden.Clone(),
        _done.Select(d => d.ToArray()).ToArray(), _doneCount));
}
