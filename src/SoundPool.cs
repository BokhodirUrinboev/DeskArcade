using System;

namespace DeskArcade;

/// <summary>Pool's ball-on-ball click: a short, bright knock of two hard resin balls, lower-pitched for bowling pins.</summary>
public sealed partial class Sound
{
    void SynthesizePool()
    {
        _clips["click"] = Normalize(Render(0.06, t =>
            Math.Sin(2 * Math.PI * 2750 * t) * Math.Exp(-t * 110) +
            Math.Sin(2 * Math.PI * 4300 * t) * Math.Exp(-t * 170) * 0.55 +
            Math.Sin(2 * Math.PI * 1500 * t) * Math.Exp(-t * 90) * 0.35 +
            Noise() * Math.Exp(-t * 600) * 0.5), 0.7);
    }
}
