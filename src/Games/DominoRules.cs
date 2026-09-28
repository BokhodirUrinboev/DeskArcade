using System;
using System.Collections.Generic;
using System.Linq;

namespace DeskArcade.Games;

/// <summary>A domino: two ends of 0–6 pips, kept with the smaller first so each tile of the set has one spelling.</summary>
public readonly record struct Domino
{
    public Domino(int a, int b)
    {
        A = Math.Min(a, b);
        B = Math.Max(a, b);
    }

    public int A { get; }
    public int B { get; }
    public int Pips => A + B;
    public bool IsDouble => A == B;
    public bool Has(int n) => A == n || B == n;

    /// <summary>The end that is not <paramref name="n"/> (or <paramref name="n"/> itself on a double).</summary>
    public int Other(int n) => A == n ? B : A;

    public override string ToString() => $"{A}-{B}";

    /// <summary>All 28 tiles of a double-six set.</summary>
    public static IReadOnlyList<Domino> Set { get; } =
        Enumerable.Range(0, 7).SelectMany(a => Enumerable.Range(a, 7 - a).Select(b => new Domino(a, b))).ToArray();
}

/// <summary>
/// Draw dominoes for two, played as a match to <see cref="MatchTo"/>: each takes seven tiles and the rest is the
/// boneyard. The opener lays any tile; after that a tile must match one of the two open ends of the line. With nothing
/// that fits you draw from the boneyard until something does, and pass once it is empty. Whoever plays their last tile
/// ("domino!") scores the pips left in the other hand; when the line is blocked (both pass) the lighter hand scores the
/// heavier one's pips. The rounds take turns at opening. UI-free, so the rules and the computer player can be tested.
/// </summary>
public sealed class DominoRules
{
    public const int HandSize = 7, MatchTo = 50;

    /// <summary>One tile in the line, as it lies: <see cref="L"/> pips on its left half, <see cref="R"/> on its right.</summary>
    public readonly record struct Laid(Domino Tile, int L, int R);

    readonly List<Domino>[] _hands = { new(), new() };
    readonly List<Domino> _yard = new();
    readonly List<Laid> _line = new();
    readonly HashSet<int>[] _lacks = { new(), new() };
    readonly int[] _score = new int[2];

    public DominoRules()
    {
    }

    /// <summary>Tiles in the line, left to right.</summary>
    public IReadOnlyList<Laid> Line => _line;

    /// <summary>How many tiles lie left of the first one laid (the line grows both ways from it).</summary>
    public int FirstIndex { get; private set; }

    public int LeftEnd => _line.Count == 0 ? -1 : _line[0].L;
    public int RightEnd => _line.Count == 0 ? -1 : _line[^1].R;

    public IReadOnlyList<Domino> Hand(int side) => _hands[side];
    public int Boneyard => _yard.Count;
    public int Score(int side) => _score[side];

    /// <summary>The side to move: 0 or 1.</summary>
    public int Turn { get; private set; }

    /// <summary>Plays, draws and passes this round, for keeping two screens in step.</summary>
    public int Ply { get; private set; }

    public int Round { get; private set; }
    public bool RoundOver { get; private set; } = true;

    /// <summary>After a round: who took it (-1 for a blocked tie), and for how many points.</summary>
    public int RoundWinner { get; private set; } = -1;
    public int RoundPoints { get; private set; }

    /// <summary>True when the round ended with a player going out rather than a blocked line.</summary>
    public bool WentOut { get; private set; }

    public bool MatchOver => _score[0] >= MatchTo || _score[1] >= MatchTo;
    public int MatchWinner => !MatchOver ? -1 : _score[0] >= _score[1] ? 0 : 1;

    int _passes;

    /// <summary>
    /// Deals the next round from a shuffle made with <paramref name="seed"/> (both screens of a LAN game use the same
    /// one), <paramref name="opener"/> to lay the first tile.
    /// </summary>
    public void Deal(int seed, int opener)
    {
        var rng = new Random(seed);
        var tiles = Domino.Set.ToList();
        for (int i = tiles.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (tiles[i], tiles[j]) = (tiles[j], tiles[i]);
        }
        for (int side = 0; side < 2; side++)
        {
            _hands[side].Clear();
            _hands[side].AddRange(tiles.Skip(side * HandSize).Take(HandSize));
            _lacks[side].Clear();
        }
        _yard.Clear();
        _yard.AddRange(tiles.Skip(2 * HandSize));
        _line.Clear();
        FirstIndex = 0;
        Turn = opener;
        Ply = 0;
        _passes = 0;
        Round++;
        RoundOver = false;
        RoundWinner = -1;
        RoundPoints = 0;
        WentOut = false;
    }

    /// <summary>Starts a new match: the scores go back to nought (deal the first round with <see cref="Deal"/>).</summary>
    public void NewMatch()
    {
        _score[0] = _score[1] = 0;
        Round = 0;
        RoundOver = true;
    }

    /// <summary>Where <paramref name="t"/> can go now: at the left end, at the right end (both true on an empty line).</summary>
    public (bool Left, bool Right) Fits(Domino t)
    {
        if (_line.Count == 0) return (true, true);
        return (t.Has(LeftEnd), t.Has(RightEnd));
    }

    /// <summary>The tiles <paramref name="side"/> could play now.</summary>
    public IEnumerable<Domino> Playable(int side) => _hands[side].Where(t => Fits(t) is (true, _) or (_, true));

    public bool CanPlay(int side) => Playable(side).Any();

    /// <summary>What a turn may be now: play, draw (nothing fits, the boneyard has tiles) or pass.</summary>
    public bool MustDraw => !RoundOver && !CanPlay(Turn) && _yard.Count > 0;
    public bool MustPass => !RoundOver && !CanPlay(Turn) && _yard.Count == 0;

    /// <summary>Lays <paramref name="t"/> from the mover's hand at one end; false if it isn't theirs or doesn't fit there.</summary>
    public bool Play(Domino t, bool atLeft)
    {
        if (RoundOver || !_hands[Turn].Contains(t)) return false;
        var (left, right) = Fits(t);
        if (atLeft ? !left : !right) return false;
        int side = Turn;
        if (_line.Count == 0) _line.Add(new Laid(t, t.A, t.B));
        else if (atLeft)
        {
            _line.Insert(0, new Laid(t, t.Other(LeftEnd), LeftEnd));
            FirstIndex++;
        }
        else _line.Add(new Laid(t, RightEnd, t.Other(RightEnd)));
        _hands[side].Remove(t);
        _passes = 0;
        Ply++;
        if (_hands[side].Count == 0) EndRound(side, wentOut: true);
        else Turn = 1 - side;
        return true;
    }

    /// <summary>The mover takes the top of the boneyard; only when nothing in their hand fits. Returns the tile drawn.</summary>
    public Domino? Draw()
    {
        if (!MustDraw) return null;
        Note(Turn);
        var t = _yard[^1];
        _yard.RemoveAt(_yard.Count - 1);
        _hands[Turn].Add(t);
        Ply++;
        return t;
    }

    /// <summary>The mover passes: only when nothing fits and the boneyard is empty. Two passes in a row block the line.</summary>
    public bool Pass()
    {
        if (!MustPass) return false;
        Note(Turn);
        Ply++;
        if (++_passes >= 2)
        {
            int a = _hands[0].Sum(t => t.Pips), b = _hands[1].Sum(t => t.Pips);
            if (a == b) EndRound(-1, wentOut: false);
            else EndRound(a < b ? 0 : 1, wentOut: false);
            return true;
        }
        Turn = 1 - Turn;
        return true;
    }

    /// <summary>A side that draws or passes has neither open end: worth remembering for the computer.</summary>
    void Note(int side)
    {
        if (_line.Count == 0) return;
        _lacks[side].Add(LeftEnd);
        _lacks[side].Add(RightEnd);
    }

    void EndRound(int winner, bool wentOut)
    {
        RoundOver = true;
        WentOut = wentOut;
        RoundWinner = winner;
        RoundPoints = winner < 0 ? 0 : _hands[1 - winner].Sum(t => t.Pips);
        if (winner >= 0) _score[winner] += RoundPoints;
    }

    /// <summary>Pips in <paramref name="side"/>'s hand.</summary>
    public int HandPips(int side) => _hands[side].Sum(t => t.Pips);

    // ------------------------------------------------------------------ the computer

    /// <summary>
    /// The computer's play for the side to move at <paramref name="level"/> 1 (Easy) to 4 (Expert), or null when it has
    /// to draw or pass. Easy lays any tile that fits; Medium the heaviest; Hard also keeps numbers it can follow and
    /// leaves ends the other side has shown it lacks; Expert also counts the tiles still unseen of each number.
    /// </summary>
    public (Domino Tile, bool AtLeft)? BestMove(int level, Random rng)
    {
        int me = Turn;
        var moves = new List<(Domino Tile, bool AtLeft)>();
        foreach (var t in _hands[me])
        {
            var (l, r) = Fits(t);
            if (l) moves.Add((t, true));
            if (r && (_line.Count > 0 && !(l && LeftEnd == RightEnd))) moves.Add((t, false)); // the same place twice when both ends match
        }
        if (moves.Count == 0) return null;
        if (level <= 1) return moves[rng.Next(moves.Count)];
        var seen = new int[7];
        foreach (var laid in _line)
        {
            seen[laid.Tile.A]++;
            if (!laid.Tile.IsDouble) seen[laid.Tile.B]++;
        }
        foreach (var t in _hands[me])
        {
            seen[t.A]++;
            if (!t.IsDouble) seen[t.B]++;
        }
        (Domino, bool)? best = null;
        double bestValue = double.NegativeInfinity;
        foreach (var (t, atLeft) in moves)
        {
            int nl, nr;
            if (_line.Count == 0) (nl, nr) = (t.A, t.B);
            else if (atLeft) (nl, nr) = (t.Other(LeftEnd), RightEnd);
            else (nl, nr) = (LeftEnd, t.Other(RightEnd));
            double v = t.Pips + (t.IsDouble ? 3 : 0) + rng.NextDouble() * 0.5;
            if (level >= 3)
            {
                var rest = _hands[me].Where(h => h != t).ToList();
                v += (rest.Count(h => h.Has(nl)) + rest.Count(h => h.Has(nr))) * 1.5; // I can follow on
                if (_lacks[1 - me].Contains(nl)) v += 3; // they could not play on that before
                if (_lacks[1 - me].Contains(nr)) v += 3;
            }
            if (level >= 4) v -= ((7 - seen[nl]) + (7 - seen[nr])) * 0.6; // tiles of those numbers still out there for them
            if (v > bestValue)
            {
                bestValue = v;
                best = (t, atLeft);
            }
        }
        return best;
    }
}
