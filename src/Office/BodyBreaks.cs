using System;

namespace DeskArcade.Office;

/// <summary>The breaks for the body: look far away, stretch, drink some water.</summary>
public enum BreakKind { Eye, Stretch, Water }

/// <summary>
/// Break reminders by time at the computer rather than time played. Time counts while the keyboard or mouse was used
/// in the last two minutes (reading a page without touching anything still counts). Two minutes away from the
/// computer rest the eyes; five minutes away are a real break and start the stretch clock over too. Water counts only
/// time at the computer, and starts over when a glass is drunk. A break that falls due while the overlay must stay
/// quiet (a presentation, a meeting, a focus block, another break on screen) waits until it is over.
/// </summary>
public sealed class BodyBreaks
{
    /// <summary>The 20-20-20 rule: every 20 minutes, look 20 feet (6 m) away for 20 seconds.</summary>
    public const int EyeMinutes = 20;
    public const double ActiveIdleLimit = 120, AwayRestsEyes = ActiveIdleLimit, AwayIsBreak = 300;

    double _eye, _stretch, _water;

    /// <summary>Seconds at the computer since each break was last taken.</summary>
    public double EyeSeconds => _eye;
    public double StretchSeconds => _stretch;
    public double WaterSeconds => _water;

    /// <param name="dt">Seconds since the last step.</param>
    /// <param name="idle">Seconds since the last keyboard or mouse input, or null when the system cannot tell (counted as present).</param>
    /// <param name="eyes">20-20-20 reminders on.</param>
    /// <param name="stretchMinutes">A stretch after this many minutes at the computer; 0 is off.</param>
    /// <param name="waterMinutes">A glass of water after this many minutes at the computer; 0 is off.</param>
    /// <param name="quiet">Nothing may be shown now: the due break waits.</param>
    /// <returns>The break to show now, or null. The caller shows it and then calls <see cref="Taken"/> or <see cref="Snooze"/>.</returns>
    public BreakKind? Step(double dt, double? idle, bool eyes, int stretchMinutes, int waterMinutes, bool quiet)
    {
        double away = idle ?? 0;
        if (away >= AwayIsBreak) _stretch = 0;
        if (away >= AwayRestsEyes) _eye = 0;
        if (away < ActiveIdleLimit && dt > 0 && dt < 600)
        {
            _eye += dt;
            _stretch += dt;
            _water += dt;
        }
        if (quiet || away >= ActiveIdleLimit) return null; // nobody there to remind
        if (stretchMinutes > 0 && _stretch >= stretchMinutes * 60) return BreakKind.Stretch;
        if (waterMinutes > 0 && _water >= waterMinutes * 60) return BreakKind.Water;
        if (eyes && _eye >= EyeMinutes * 60) return BreakKind.Eye;
        return null;
    }

    /// <summary>The break was taken (or skipped): its clock starts over. A stretch rests the eyes as well.</summary>
    public void Taken(BreakKind kind)
    {
        switch (kind)
        {
            case BreakKind.Eye: _eye = 0; break;
            case BreakKind.Stretch: _stretch = 0; _eye = 0; break;
            case BreakKind.Water: _water = 0; break;
        }
    }

    /// <summary>"Later": the break comes back after <paramref name="seconds"/> more at the computer.</summary>
    public void Snooze(BreakKind kind, int intervalMinutes, double seconds)
    {
        double back = Math.Max(0, intervalMinutes * 60 - seconds);
        switch (kind)
        {
            case BreakKind.Eye: _eye = Math.Max(0, EyeMinutes * 60 - seconds); break;
            case BreakKind.Stretch: _stretch = back; break;
            case BreakKind.Water: _water = back; break;
        }
    }
}
