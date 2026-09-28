using System;

namespace DeskArcade;

/// <summary>
/// The typing games' keys: a light mechanical clack for a right key (the games vary its pitch a little, so a run of
/// keys doesn't sound like one repeated sample), a dull, low tap for a wrong one, the carriage return's ratchet for a
/// line break, a zap for a word shot down in Word Rain, and a desk bell for the finish.
/// </summary>
public sealed partial class Sound
{
    void SynthesizeTyping()
    {
        // the key hitting bottom: a click with a woody knock under it
        _clips["key"] = Normalize(Render(0.05, t =>
            Noise() * Math.Exp(-t * 420) * 0.8 + Math.Sin(2 * Math.PI * 1900 * t) * Math.Exp(-t * 160) * 0.35 +
            Math.Sin(2 * Math.PI * 320 * t) * Math.Exp(-t * 90) * 0.45), 0.55);
        _clips["key-bad"] = Render(0.09, t =>
            Math.Sin(2 * Math.PI * 150 * t) * Math.Exp(-t * 45) * 0.6 + Noise() * Math.Exp(-t * 180) * 0.2);
        // the carriage return: a quick ratchet of small clicks
        _clips["key-return"] = Render(0.22, t =>
        {
            double tick = t * 55 % 1;
            return Noise() * Math.Exp(-tick * 30) * 0.35 * (1 - t / 0.22) + Math.Sin(2 * Math.PI * 700 * t) * Math.Exp(-t * 25) * 0.15;
        });
        _clips["zap"] = Render(0.2, t =>
        {
            double f = 1800 * Math.Exp(-t * 14) + 220;
            return Math.Sin(2 * Math.PI * f * t) * Math.Exp(-t * 16) * 0.4 + Noise() * Math.Exp(-t * 90) * 0.12;
        });
        // a desk bell, the kind on a hotel counter
        _clips["ding"] = Render(1.1, t =>
            (Math.Sin(2 * Math.PI * 2093 * t) + Math.Sin(2 * Math.PI * 5230 * t) * 0.3 * Math.Exp(-t * 6) +
             Math.Sin(2 * Math.PI * 3140 * t) * 0.25) * Math.Min(1, t / 0.002) * Math.Exp(-t * 3.2) * 0.3);
    }
}
