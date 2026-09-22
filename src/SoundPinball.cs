using System;

namespace DeskArcade;

/// <summary>Pinball's table sounds: the pop bumper's solenoid, the flipper clack and the rollover bell.</summary>
public sealed partial class Sound
{
    void SynthesizePinball()
    {
        // a solenoid slamming the bumper skirt down: a falling thump with a bright snap on top
        _clips["pin-bumper"] = Render(0.16, t =>
        {
            double f = 140 + 260 * Math.Exp(-t * 60);
            return Math.Sin(2 * Math.PI * f * t) * Math.Exp(-t * 30) * 0.7 + Noise() * Math.Exp(-t * 260) * 0.5;
        });
        // plastic on a rubber stop: a short click with a little body under it
        _clips["pin-flipper"] = Render(0.07, t =>
            Noise() * Math.Exp(-t * 140) * 0.55 + Math.Sin(2 * Math.PI * 900 * t) * Math.Exp(-t * 90) * 0.3 +
            Math.Sin(2 * Math.PI * 210 * t) * Math.Exp(-t * 60) * 0.4);
        // a small bell: a fundamental with an inharmonic partial that dies away first
        _clips["pin-ding"] = Render(0.5, t =>
            (Math.Sin(2 * Math.PI * 1568 * t) + Math.Sin(2 * Math.PI * 3920 * t) * 0.35 * Math.Exp(-t * 10) +
             Math.Sin(2 * Math.PI * 2350 * t) * 0.2) * Math.Min(1, t / 0.002) * Math.Exp(-t * 7) * 0.3);
    }
}
