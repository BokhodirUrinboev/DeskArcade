using System;
using System.Collections.Generic;
using System.Linq;

namespace DeskArcade.Games;

/// <summary>
/// Last Card, a shedding game for 2–4 players, free of UI so it can be tested and run by the host of a LAN room.
/// A 108-card deck in four colours (red 0, yellow 1, green 2, blue 3): per colour one 0, two each of 1–9, and
/// two each of Skip, Reverse and Draw Two; plus four Wilds and four Wild Draw Fours. Everyone gets seven cards
/// and the first number card from the pile starts the discard.
/// <list type="bullet">
/// <item>On your turn play a card of the current colour, or of the same number or symbol, or a Wild (you pick
/// the colour). A Wild Draw Four is allowed only when you hold nothing of the current colour.</item>
/// <item>Skip passes over the next player; Reverse turns the direction round (with two players it skips);
/// Draw Two and Wild Draw Four make the next player draw and lose their turn.</item>
/// <item>If you can't or won't play, draw one card. If it fits you may play it at once, or pass; if it
/// doesn't, your turn ends.</item>
/// <item>Say "last card" (<c>call</c>) when you're down to one card, or before, with two in hand. Forget, and
/// you draw two as soon as the next player moves.</item>
/// <item>The first to play their last card wins.</item>
/// </list>
/// Cards are numbered: colour × 25 + k for the coloured cards, where k 0 is the 0, k 1–18 the numbers 1–9
/// twice, 19–20 Skip, 21–22 Reverse and 23–24 Draw Two; 100–103 are the Wilds and 104–107 the Wild Draw Fours.
/// </summary>
public sealed class LastCardRules
{
    public const int HandSize = 7, DeckSize = 108, ColorCount = 4, MinPlayers = 2, MaxPlayers = 4;
    /// <summary>The colour of a Wild before it is played.</summary>
    public const int Wild = 4;

    public enum Kind { Number, Skip, Reverse, DrawTwo, Wild, WildDrawFour }

    public static int ColorOf(int card) => card >= 100 ? Wild : card / 25;

    public static Kind KindOf(int card)
    {
        if (card >= 104) return Kind.WildDrawFour;
        if (card >= 100) return Kind.Wild;
        int k = card % 25;
        return k <= 18 ? Kind.Number : k <= 20 ? Kind.Skip : k <= 22 ? Kind.Reverse : Kind.DrawTwo;
    }

    /// <summary>0–9 for a number card, −1 otherwise.</summary>
    public static int NumberOf(int card)
    {
        if (KindOf(card) != Kind.Number) return -1;
        int k = card % 25;
        return k == 0 ? 0 : (k - 1) / 2 + 1;
    }

    /// <summary>What must match when the colour doesn't: the number, or the symbol.</summary>
    static int Face(int card) => KindOf(card) == Kind.Number ? NumberOf(card) : 10 + (int)KindOf(card);

    public static bool IsWild(int card) => card >= 100;

    readonly Random _rng;

    public int Players { get; }
    public List<int>[] Hands { get; }
    public List<int> DrawPile { get; } = new();
    public List<int> Discard { get; } = new();
    public bool[] Called { get; }
    /// <summary>The colour to follow: the top card's, or the one chosen for a Wild.</summary>
    public int Color { get; private set; }
    public int Turn { get; private set; }
    /// <summary>+1 to the left (seat order), −1 after an odd number of Reverses.</summary>
    public int Direction { get; private set; } = 1;
    /// <summary>The player whose turn it is has drawn a card that fits and may play it or pass.</summary>
    public bool Drew { get; private set; }
    public int DrawnCard { get; private set; } = -1;
    public bool Over { get; private set; }
    public int Winner { get; private set; } = -1;
    /// <summary>The last player caught without calling "last card", and how many catches there have been so far.</summary>
    public int Caught { get; private set; } = -1;
    public int Catches { get; private set; }
    /// <summary>Goes up with every change, so a view can tell it is newer.</summary>
    public int Version { get; private set; }

    public int Top => Discard[^1];

    /// <summary>Whoever played last, while they can still be caught for not calling.</summary>
    int _lastPlayer = -1;

    public LastCardRules(int players, Random rng) : this(players, rng, null) { }

    /// <summary>A deal from a known order (for tests): cards go out one at a time round the table from the start of the list.</summary>
    public LastCardRules(int players, Random rng, IReadOnlyList<int>? deck)
    {
        if (players is < MinPlayers or > MaxPlayers) throw new ArgumentOutOfRangeException(nameof(players));
        _rng = rng;
        Players = players;
        Hands = Enumerable.Range(0, players).Select(_ => new List<int>()).ToArray();
        Called = new bool[players];
        var order = deck?.ToList() ?? Shuffled(Enumerable.Range(0, DeckSize), rng);
        if (order.Count != DeckSize || order.Distinct().Count() != DeckSize || order.Any(c => c < 0 || c >= DeckSize))
            throw new ArgumentException("A deck is each of the 108 cards once.", nameof(deck));
        int next = 0;
        for (int round = 0; round < HandSize; round++)
            for (int p = 0; p < players; p++)
                Hands[p].Add(order[next++]);
        // the pile's top is the end of the list; the first number card from the top starts the discard
        DrawPile.AddRange(order.Skip(next).Reverse());
        int start = DrawPile.FindLastIndex(c => KindOf(c) == Kind.Number);
        Discard.Add(DrawPile[start]);
        DrawPile.RemoveAt(start);
        Color = ColorOf(Top);
    }

    static List<int> Shuffled(IEnumerable<int> cards, Random rng)
    {
        var list = cards.ToList();
        for (int i = list.Count - 1; i > 0; i--) // Fisher–Yates
        {
            int j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }

    public int Next(int steps = 1) => ((Turn + Direction * steps) % Players + Players) % Players;

    /// <summary>Whether <paramref name="card"/> fits on the discard, ignoring whose turn it is.</summary>
    public bool Fits(int card) => IsWild(card) || ColorOf(card) == Color || Face(card) == Face(Top);

    public bool CanPlay(int seat, int card)
    {
        if (Over || seat != Turn || !Hands[seat].Contains(card) || !Fits(card)) return false;
        if (Drew && card != DrawnCard) return false; // after drawing, only the drawn card may go
        return KindOf(card) != Kind.WildDrawFour || Hands[seat].All(c => ColorOf(c) != Color);
    }

    /// <summary>
    /// A player's move: "play" (<paramref name="card"/>, and for a Wild the <paramref name="color"/> 0–3),
    /// "draw", "pass" (only after drawing a card that fits) or "call" (last card, any time with one or two
    /// cards in hand). False if it isn't allowed; nothing changes then.
    /// </summary>
    public bool Act(int seat, string kind, int card = -1, int color = -1)
    {
        if (seat < 0 || seat >= Players) return false;
        switch (kind)
        {
            case "call":
                if (Over || Called[seat] || Hands[seat].Count is 0 or > 2) return false;
                Called[seat] = true;
                break;
            case "play":
                if (!CanPlay(seat, card) || IsWild(card) && color is < 0 or >= ColorCount) return false;
                CatchForgetter(seat);
                Hands[seat].Remove(card);
                Discard.Add(card);
                Color = IsWild(card) ? color : ColorOf(card);
                Drew = false;
                DrawnCard = -1;
                _lastPlayer = seat;
                if (Hands[seat].Count == 0)
                {
                    Over = true;
                    Winner = seat;
                    break;
                }
                switch (KindOf(card))
                {
                    case Kind.Skip:
                        Turn = Next(2);
                        break;
                    case Kind.Reverse when Players == 2:
                        Turn = Next(2); // with two, a Reverse brings the turn straight back
                        break;
                    case Kind.Reverse:
                        Direction = -Direction;
                        Turn = Next();
                        break;
                    case Kind.DrawTwo or Kind.WildDrawFour:
                        int victim = Next();
                        Give(victim, KindOf(card) == Kind.DrawTwo ? 2 : 4);
                        Turn = Next(2);
                        break;
                    default:
                        Turn = Next();
                        break;
                }
                break;
            case "draw":
                if (Over || seat != Turn || Drew) return false;
                CatchForgetter(seat);
                int drawn = Give(seat, 1).FirstOrDefault(-1);
                if (drawn >= 0 && CanPlayDrawn(seat, drawn))
                {
                    Drew = true;
                    DrawnCard = drawn;
                }
                else Turn = Next(); // nothing to draw, or it doesn't fit: the turn ends
                break;
            case "pass":
                if (Over || seat != Turn || !Drew) return false;
                Drew = false;
                DrawnCard = -1;
                Turn = Next();
                break;
            default:
                return false;
        }
        for (int s = 0; s < Players; s++)
            if (Hands[s].Count > 2) Called[s] = false; // a call only lasts while you're down to one or two
        Version++;
        return true;
    }

    bool CanPlayDrawn(int seat, int card)
    {
        Drew = true;
        DrawnCard = card;
        bool ok = CanPlay(seat, card);
        Drew = false;
        DrawnCard = -1;
        return ok;
    }

    /// <summary>The next player moved while the last one sat on one card without calling: two cards for them.</summary>
    void CatchForgetter(int actor)
    {
        int p = _lastPlayer;
        _lastPlayer = -1;
        if (p < 0 || p == actor || Hands[p].Count != 1 || Called[p]) return;
        Give(p, 2);
        Caught = p;
        Catches++;
    }

    /// <summary>Deals <paramref name="n"/> cards from the pile, reshuffling the discard (all but its top) into it when it runs out.</summary>
    List<int> Give(int seat, int n)
    {
        var got = new List<int>();
        for (int i = 0; i < n; i++)
        {
            if (DrawPile.Count == 0 && Discard.Count > 1)
            {
                var rest = Discard.Take(Discard.Count - 1).ToList();
                Discard.RemoveRange(0, rest.Count);
                DrawPile.AddRange(Shuffled(rest, _rng));
            }
            if (DrawPile.Count == 0) break; // every card is in someone's hand
            int card = DrawPile[^1];
            DrawPile.RemoveAt(DrawPile.Count - 1);
            Hands[seat].Add(card);
            got.Add(card);
        }
        return got;
    }

    /// <summary>
    /// A computer player's move on its turn, or null when it has nothing to do. It keeps its Wilds for when
    /// nothing else fits, hits a player close to going out with Skip and Draw cards, sheds high numbers first
    /// and picks the colour it holds most of. Calling "last card" is left to the caller, which may forget.
    /// </summary>
    public (string Kind, int Card, int Color)? CpuAction(int seat)
    {
        if (Over || seat != Turn) return null;
        var hand = Hands[seat];
        var playable = hand.Where(c => CanPlay(seat, c)).ToList();
        if (Drew) return playable.Count > 0 ? ("play", DrawnCard, BestColor(seat, DrawnCard)) : ("pass", -1, -1);
        if (playable.Count == 0) return ("draw", -1, -1);
        bool threat = Hands[Next()].Count <= 2;
        int Score(int c) => KindOf(c) switch
        {
            Kind.WildDrawFour => threat ? 60 : -20,
            Kind.Wild => -10,
            Kind.DrawTwo or Kind.Skip => threat ? 50 : 15,
            Kind.Reverse => threat && Players > 2 ? 40 : 14,
            _ => NumberOf(c) + (ColorOf(c) == Color ? 1 : 0),
        };
        int best = playable.OrderByDescending(Score).First();
        return ("play", best, BestColor(seat, best));
    }

    int BestColor(int seat, int card)
    {
        if (!IsWild(card)) return -1;
        var counts = new int[ColorCount];
        foreach (int c in Hands[seat].Where(c => c != card && !IsWild(c))) counts[ColorOf(c)]++;
        int best = Array.IndexOf(counts, counts.Max());
        return counts[best] > 0 ? best : Color;
    }
}
