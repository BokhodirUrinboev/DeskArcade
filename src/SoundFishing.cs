using System;

namespace DeskArcade;

/// <summary>Fishing: the bobber's splash and plop, and the ratchet of the reel.</summary>
public sealed partial class Sound
{
    void SynthesizeFishing()
    {
        // water noise that closes down as the drops fall back, over a low "bloop" of the air pocket
        var spray = Filtered(0.4, 0.3, 0.3, t => Math.Min(1, t / 0.004) * (Math.Exp(-t * 14) * 0.8 + Math.Exp(-t * 60) * 0.5));
        var bloop = Render(0.4, t =>
        {
            double f = 180 + 260 * Math.Exp(-t * 30);
            return Math.Sin(2 * Math.PI * f * t) * Math.Exp(-t * 18) * 0.5;
        });
        for (int i = 0; i < spray.Length; i++) spray[i] += bloop[i];
        _clips["splash"] = Normalize(spray, 0.6);

        // a bubble rising in pitch: the bobber dipping for a nibble
        _clips["plop"] = Normalize(Render(0.09, t =>
            Math.Sin(2 * Math.PI * (500 + 5000 * t) * t) * Math.Min(1, t / 0.003) * Math.Exp(-t * 40)), 0.5);

        // one tooth of the reel's ratchet
        _clips["reel"] = Normalize(Render(0.03, t =>
            Noise() * Math.Exp(-t * 500) + Math.Sin(2 * Math.PI * 2900 * t) * Math.Exp(-t * 260) * 0.6), 0.4);
    }
}
