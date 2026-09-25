using System;

namespace DeskArcade;

/// <summary>
/// Sheep Herding: the flock's bleats. A "baa" opens from the closed lips of the "b" into a long "aa" whose voice
/// shakes about twenty times a second, which is what makes it a sheep and not a goat or a calf; a lamb's is higher
/// and shorter. The sheepdog borrows the pet dog's bark.
/// </summary>
public sealed partial class Sound
{
    void SynthesizeSheep()
    {
        _clips["baa"] = Baa(0.62, 330);
        _clips["baa2"] = Baa(0.42, 520);
    }

    float[] Baa(double len, double f) => Vocal(len,
        t => Arc(t / len, f * 0.96, f * 1.08, f * 0.9),
        t => Swell(t, len, 0.03) * (0.72 + 0.28 * Math.Sin(2 * Math.PI * 21 * t)),
        t => t < 0.06 ? (300 + 550 * t / 0.06, 900 + 350 * t / 0.06, 2500) : (Arc(t / len, 850, 820, 700), Arc(t / len, 1250, 1350, 1650), 2700),
        breath: 0.14, rasp: 0.3, jitter: 0.03, vibrato: 21, depth: 0.035, bright: 0.65);
}
