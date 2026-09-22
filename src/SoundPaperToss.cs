using System;

namespace DeskArcade;

/// <summary>Paper Toss: the rustle of a crumpled paper ball.</summary>
public sealed partial class Sound
{
    void SynthesizePaperToss()
    {
        // crumpled paper crackles: bright noise whose loudness jumps every few milliseconds
        var crackle = new double[64];
        for (int i = 0; i < crackle.Length; i++) crackle[i] = 0.25 + 0.75 * Math.Pow(_rng.NextDouble(), 2);
        const double len = 0.2;
        _clips["rustle"] = Normalize(Filtered(len, 0.6, 0.85, t =>
            crackle[(int)(t / 0.003) % crackle.Length] * Math.Min(1, t / 0.01) * Math.Pow(Math.Max(0, 1 - t / len), 1.5)), 0.5);
    }
}
