using System.Collections.Generic;
using static DeskArcade.Games.SolitaireRules;

namespace DeskArcade.Games;

/// <summary>
/// What the Solitaire table needs from a deal, whichever game it is: Klondike (<see cref="SolitaireRules"/>),
/// <see cref="FreeCellRules"/> or <see cref="SpiderRules"/>. The piles are <see cref="Spot"/>s, a move takes the top
/// cards of one pile to another, and cards are numbered by the rules (<see cref="Face"/> says what each one shows).
/// </summary>
public interface IPatience
{
    /// <summary>The cards in play: 52, or 104 for Spider's two decks.</summary>
    int Total { get; }

    /// <summary>Cards sent home: on the foundations, or in the suits Spider took off the table.</summary>
    int Home { get; }

    /// <summary>Moves so far; an undo counts as one too, so the count never goes down.</summary>
    int Moves { get; }

    bool Won { get; }
    bool CanUndo { get; }

    /// <summary>Nothing is left to work out: the rest can go home by itself, one card after another (<see cref="NextHome"/>).</summary>
    bool CanFinish { get; }

    /// <summary>A pile's cards, bottom first.</summary>
    IReadOnlyList<int> Cards(Spot spot);

    /// <summary>How many cards at the bottom of a pile are face down.</summary>
    int Hidden(Spot spot);

    /// <summary>The most cards that can be picked up together from the top of a pile (0 when none can).</summary>
    int Movable(Spot spot);

    bool IsLegal(Move m);
    bool Apply(Move m);

    /// <summary>Where a one-click move of the top <paramref name="count"/> cards should go, or null when nothing fits.</summary>
    Move? Best(Spot from, int count);

    /// <summary>The next card to send home by itself once <see cref="CanFinish"/>, or null.</summary>
    Move? NextHome();

    /// <summary>A click on the stock: Klondike turns a card, Spider deals a row. False when it does nothing.</summary>
    bool Draw();

    bool Undo();

    /// <summary>What a card shows: its suit (♠ 0, ♣ 1, ♦ 2, ♥ 3) and rank (1 ace … 13 king).</summary>
    (int Suit, int Rank) Face(int card);
}

/// <summary>The deals the chip under the felt switches between.</summary>
public enum PatienceKind { Klondike, FreeCell, Spider1, Spider2, Spider4 }

/// <summary>Names and pile counts of the deals, UI-free so they can be checked.</summary>
public static class Patience
{
    public static bool IsSpider(PatienceKind kind) => kind is PatienceKind.Spider1 or PatienceKind.Spider2 or PatienceKind.Spider4;

    /// <summary>Spider's suits: 1, 2 or 4 (0 for the other deals).</summary>
    public static int SpiderSuits(PatienceKind kind) => kind switch
    {
        PatienceKind.Spider1 => 1,
        PatienceKind.Spider2 => 2,
        PatienceKind.Spider4 => 4,
        _ => 0,
    };

    /// <summary>The tableau's columns: 7 in Klondike, 8 in FreeCell, 10 in Spider.</summary>
    public static int Columns(PatienceKind kind) => kind switch
    {
        PatienceKind.Klondike => SolitaireRules.Piles,
        PatienceKind.FreeCell => FreeCellRules.Cascades,
        _ => SpiderRules.Columns,
    };

    /// <summary>
    /// A deal's share sent home as a race score out of 52, whatever the game: Spider's 104 cards count half each (a suit
    /// taken off is 6½), so every deal races on the same scale and a whole deal is 52.
    /// </summary>
    public static int RaceScore(int home, int total) => total <= 52 ? home : home * 52 / total;

    /// <summary>
    /// The order a deal lays the tableau out in, so the cards fan out one by one: Klondike row by row across the
    /// shrinking piles (<see cref="SolitaireGame.DealIndex"/>), FreeCell and Spider row by row across every column.
    /// </summary>
    public static int DealOrder(PatienceKind kind, int pile, int position) =>
        kind == PatienceKind.Klondike ? SolitaireGame.DealIndex(pile, position) : position * Columns(kind) + pile;
}
