using System;

namespace DeskArcade.Games;

/// <summary>
/// The rules of Memory (pairs), free of UI: a shuffled deck where every face appears exactly twice. Flip one
/// card, then a second: a match stays face up, a mismatch stays shown until <see cref="Settle"/> turns both
/// face down again. Each pair of flips is one move; the board is won when every pair is found.
/// </summary>
public sealed class MemoryRules
{
    public const int DefaultPairs = 12;

    public enum Card : byte { Down, Up, Matched }

    public enum FlipResult { Ignored, First, Match, Mismatch }

    readonly int[] _faces;
    readonly Card[] _state;

    public MemoryRules(Random rng, int pairs = DefaultPairs)
    {
        if (pairs < 1) throw new ArgumentOutOfRangeException(nameof(pairs));
        _faces = new int[pairs * 2];
        _state = new Card[pairs * 2];
        for (int i = 0; i < _faces.Length; i++) _faces[i] = i / 2;
        for (int i = _faces.Length - 1; i > 0; i--) // Fisher–Yates
        {
            int j = rng.Next(i + 1);
            (_faces[i], _faces[j]) = (_faces[j], _faces[i]);
        }
    }

    public int Count => _faces.Length;
    public int Pairs => _faces.Length / 2;
    public int Moves { get; private set; }
    public int Found { get; private set; }
    public bool Won => Found == Pairs;

    /// <summary>The first card of the move in progress (face up on its own), or −1.</summary>
    public int Open { get; private set; } = -1;

    /// <summary>The two cards of a mismatch that are still shown, waiting for <see cref="Settle"/>.</summary>
    public (int A, int B)? Mismatch { get; private set; }

    public int FaceOf(int card) => _faces[card];

    public Card StateOf(int card) => _state[card];

    /// <summary>
    /// Turns a face-down card up. Ignored for cards already up or matched, after the win, and while a
    /// mismatch is still shown (so quick clicks never reveal a third card).
    /// </summary>
    public FlipResult Flip(int card)
    {
        if (Won || Mismatch != null || card < 0 || card >= Count || _state[card] != Card.Down) return FlipResult.Ignored;
        _state[card] = Card.Up;
        if (Open < 0)
        {
            Open = card;
            return FlipResult.First;
        }
        int first = Open;
        Open = -1;
        Moves++;
        if (_faces[first] == _faces[card])
        {
            _state[first] = _state[card] = Card.Matched;
            Found++;
            return FlipResult.Match;
        }
        Mismatch = (first, card);
        return FlipResult.Mismatch;
    }

    /// <summary>Turns a shown mismatch face down again; false if there was none.</summary>
    public bool Settle()
    {
        if (Mismatch is not { } m) return false;
        _state[m.A] = _state[m.B] = Card.Down;
        Mismatch = null;
        return true;
    }
}
