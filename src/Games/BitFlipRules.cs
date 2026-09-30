using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DeskArcade.Games;

/// <summary>A number falling down the well, shown in decimal or hex.</summary>
public sealed class BitTarget
{
    public required int Value { get; init; }
    public required bool Hex { get; init; }
    /// <summary>How far it has fallen, 0 at the top to 1 on the bits.</summary>
    public double Y { get; set; }

    /// <summary>"42", or "0x2A" in hex (as many digits as the bits take).</summary>
    public string Text(int bits) => Hex ? "0x" + Value.ToString("X" + ((bits + 3) / 4), CultureInfo.InvariantCulture) : Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>One level: how many bits, how fast a number falls, how often the next comes, and how many are in hex.</summary>
public sealed record BitLevel(int Number, int Bits, double FallSeconds, double Every, double HexShare);

/// <summary>
/// Bit Flip: numbers fall down a well towards a row of bits. Flip bits to make the number at the bottom (the lowest
/// one) and it clears: the bits go back to zero and it scores, more for a higher level, for hex, and for catching it
/// high up. A number that reaches the bits costs a life; three and the game is over. Every <see cref="PerLevel"/>
/// cleared the level goes up (<see cref="Levels"/>): four bits, then eight, hex mixed in, twelve and sixteen.
/// </summary>
public sealed class BitFlipRules
{
    public const int Lives = 3, PerLevel = 8;
    const double EmptyWait = 0.6;

    public static readonly IReadOnlyList<BitLevel> Levels = new BitLevel[]
    {
        new(1, 4, 11, 4.2, 0),
        new(2, 4, 9, 3.4, 0),
        new(3, 6, 10, 3.8, 0),
        new(4, 8, 11, 4.2, 0),
        new(5, 8, 10, 3.8, 0.35),
        new(6, 8, 9, 3.4, 0.6),
        new(7, 12, 12, 4.6, 0.5),
        new(8, 12, 11, 4.2, 0.6),
        new(9, 16, 14, 5.2, 0.6),
        new(10, 16, 12, 4.6, 0.7),
    };

    readonly Random _rng;
    readonly List<BitTarget> _targets = new();
    double _nextIn;

    public BitFlipRules(Random rng)
    {
        _rng = rng;
        _nextIn = EmptyWait;
    }

    public int Level { get; private set; } = 1;
    public BitLevel Current => Levels[Level - 1];
    public int Bits => Current.Bits;
    /// <summary>The bits as flipped now; bit 0 is the rightmost (worth 1).</summary>
    public int Value { get; private set; }
    public int Score { get; private set; }
    public int Cleared { get; private set; }
    public int LivesLeft { get; private set; } = Lives;
    public bool Over => LivesLeft <= 0;
    /// <summary>The numbers in the well, the lowest last.</summary>
    public IReadOnlyList<BitTarget> Targets => _targets;
    public BitTarget? Lowest => _targets.Count > 0 ? _targets[^1] : null;
    public int LastPoints { get; private set; }

    /// <summary>Flips bit <paramref name="bit"/> (0 is worth 1). Returns the number it cleared, if the bits now make the lowest one.</summary>
    public BitTarget? Flip(int bit)
    {
        if (Over || bit < 0 || bit >= Bits) return null;
        Value ^= 1 << bit;
        return Check();
    }

    /// <summary>All bits back to zero.</summary>
    public void Reset()
    {
        if (!Over) Value = 0;
    }

    BitTarget? Check()
    {
        var low = Lowest;
        if (low == null || low.Value != Value) return null;
        _targets.RemoveAt(_targets.Count - 1);
        LastPoints = Points(Level, low.Hex, low.Y);
        Score += LastPoints;
        Cleared++;
        Value = 0;
        if (Cleared % PerLevel == 0 && Level < Levels.Count) Level++;
        return low;
    }

    /// <summary>10 a number at level 1, 5 more a level, half again in hex, and up to double for catching it at the top.</summary>
    public static int Points(int level, bool hex, double y)
    {
        double points = (10 + 5 * (level - 1)) * (hex ? 1.5 : 1) * (1 + Math.Clamp(1 - y, 0, 1));
        return (int)Math.Round(points);
    }

    /// <summary>
    /// Moves the numbers down and drops in a new one when it's time (at once when the well is empty). Returns the
    /// numbers that reached the bits in this step: each costs a life.
    /// </summary>
    public List<BitTarget> Step(double dt)
    {
        var landed = new List<BitTarget>();
        if (Over) return landed;
        foreach (var t in _targets) t.Y += dt / Current.FallSeconds;
        for (int i = _targets.Count - 1; i >= 0; i--)
        {
            if (_targets[i].Y < 1) continue;
            landed.Add(_targets[i]);
            _targets.RemoveAt(i);
            LivesLeft = Math.Max(0, LivesLeft - 1);
        }
        if (landed.Count > 0 && !Over) Check(); // the next one down may already be flipped
        _nextIn -= dt;
        if (_targets.Count == 0) _nextIn = Math.Min(_nextIn, EmptyWait); // an empty well gets its next number soon
        if (!Over && _nextIn <= 0)
        {
            _targets.Insert(0, NewTarget());
            _nextIn = Current.Every;
        }
        return landed;
    }

    /// <summary>A number the bits can make, never zero (nothing to flip) and never the same as one already falling.</summary>
    BitTarget NewTarget()
    {
        int max = (1 << Bits) - 1, value;
        do value = 1 + _rng.Next(max);
        while (_targets.Any(t => t.Value == value));
        return new BitTarget { Value = value, Hex = _rng.NextDouble() < Current.HexShare };
    }

    /// <summary>The bits to flip from what is set now to make <paramref name="target"/>, lowest first.</summary>
    public static IEnumerable<int> Differences(int from, int target, int bits) => Enumerable.Range(0, bits).Where(b => ((from ^ target) >> b & 1) == 1);
}
