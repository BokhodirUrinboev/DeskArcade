using System;

namespace DeskArcade.Engine;

/// <summary>
/// The computer's side of a solo race (<see cref="MiniGame.Race"/>): it "plays" a round of its own alongside the
/// player's, scoring at a pace set by its level and the score it is aiming at, and the higher final score wins (the
/// lower one in games where fewer is better). UI-free, so it can be tested.
/// </summary>
public sealed class CpuRival
{
    /// <summary>Share of the reference score the computer reaches per level (Easy … Expert) when higher is better.</summary>
    public static readonly double[] Factors = { 0.5, 0.75, 1.0, 1.25 };

    /// <summary>Multiple of the reference count per level when fewer is better.</summary>
    public static readonly double[] LowerFactors = { 1.5, 1.2, 1.0, 0.85 };

    readonly double _seconds, _slump;
    double _t;

    /// <param name="level">1 (Easy) to 4 (Expert).</param>
    /// <param name="reference">The score it measures itself against: the player's best, or the game's baseline.</param>
    /// <param name="lowerIsBetter">Fewer is better (darts thrown, moves made).</param>
    /// <param name="seconds">About how long a round lasts; the computer spreads its scoring over that time.</param>
    /// <param name="min">The lowest possible round score; a fewer-is-better computer aims at least one above it.</param>
    /// <param name="max">The highest possible round score; a higher-is-better computer aims at least one below it.</param>
    public CpuRival(int level, int reference, bool lowerIsBetter, double seconds, Random rng, int min = 0, int max = int.MaxValue)
    {
        Level = Math.Clamp(level, 1, MiniGame.LevelNames.Length);
        LowerIsBetter = lowerIsBetter;
        _seconds = Math.Max(3, seconds * (0.85 + rng.NextDouble() * 0.3));
        double factor = (lowerIsBetter ? LowerFactors : Factors)[Level - 1] * (0.88 + rng.NextDouble() * 0.24);
        Target = Math.Max(lowerIsBetter ? 1 : 0, (int)Math.Round(Math.Max(1, reference) * factor));
        // stay inside what the game can score, and leave the perfect round to the player
        int lo = lowerIsBetter ? min + 1 : min, hi = lowerIsBetter || max == int.MaxValue ? max : max - 1;
        if (hi < lo) hi = lo;
        Target = Math.Clamp(Target, lo, hi);
        // now and then the computer has an off day, more often at the easy levels
        double offDay = Level == 1 ? 0.3 : Level == 2 ? 0.15 : 0.05;
        _slump = rng.NextDouble() < offDay ? 0.4 + rng.NextDouble() * 0.3 : 1;
    }

    public int Level { get; }
    public bool LowerIsBetter { get; }

    /// <summary>The score it is heading for (the count it will finish with, when fewer is better).</summary>
    public int Target { get; }

    public bool Done { get; private set; }

    /// <summary>Its score so far: it climbs quickly at first and flattens out, like a real round.</summary>
    public int Score => LowerIsBetter ? Target : (int)Math.Round(Target * _slump * Ease.OutQuad(Math.Min(1, _t / _seconds)));

    /// <summary>Advances its round; true when the score changed (something to mark on screen).</summary>
    public bool Tick(double dt)
    {
        if (Done) return false;
        int before = Score;
        _t += dt;
        if (_t >= _seconds) Done = true;
        return Score != before;
    }

    /// <summary>The player's round ended: the computer stops where it is.</summary>
    public void Finish() => Done = true;
}
