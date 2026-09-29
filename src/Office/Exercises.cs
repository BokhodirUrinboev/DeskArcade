using System;

namespace DeskArcade.Office;

/// <summary>
/// Box breathing, one minute of it: breathe in for four seconds, hold for four, breathe out for four, hold for four,
/// four times over. The overlay draws a circle that grows and shrinks with the breath.
/// </summary>
public static class Breathing
{
    public const double Side = 4;
    public const int Rounds = 4;
    public const double Seconds = Side * 4 * Rounds;

    /// <summary>The four phases, in order: breathe in, hold (full), breathe out, hold (empty).</summary>
    public static readonly string[] Phases = { "Breathe in", "Hold", "Breathe out", "Hold" };

    /// <param name="t">Seconds since the start.</param>
    /// <returns>The phase (0–3), how far through it (0–1), the count shown (4, 3, 2, 1), the round (1–4), and how full the circle is (0–1).</returns>
    public static (int Phase, double K, int Count, int Round, double Size) At(double t)
    {
        t = Math.Clamp(t, 0, Seconds - 1e-9);
        int step = (int)(t / Side);
        int phase = step % 4;
        double k = (t - step * Side) / Side;
        int count = (int)Side - (int)(k * Side);
        double eased = 0.5 - 0.5 * Math.Cos(Math.PI * k);
        double size = phase switch { 0 => eased, 1 => 1, 2 => 1 - eased, _ => 0 };
        return (phase, k, count, step / 4 + 1, size);
    }
}

/// <summary>
/// A one-minute stretch at the desk in four moves of fifteen seconds, shown by a little figure the player copies.
/// </summary>
public static class Stretches
{
    public const double StepSeconds = 15;

    /// <summary>What each move is called and how to do it (English keys for translation).</summary>
    public static readonly (string Title, string Hint)[] Steps =
    {
        ("Roll your shoulders", "slowly backwards, big circles"),
        ("Tilt your head", "ear towards the shoulder, one side and then the other"),
        ("Stretch your wrists", "arm out, palm up, pull the fingers back gently"),
        ("Stand up and reach", "hands up high, then let them fall"),
    };

    public static double Seconds => Steps.Length * StepSeconds;

    /// <returns>The move (0–3) and how far through it (0–1).</returns>
    public static (int Step, double K) At(double t)
    {
        t = Math.Clamp(t, 0, Seconds - 1e-9);
        int step = (int)(t / StepSeconds);
        return (step, (t - step * StepSeconds) / StepSeconds);
    }
}

/// <summary>The 20-20-20 eye break: look at something 20 feet (6 metres) away for 20 seconds.</summary>
public static class EyeBreak
{
    public const double Seconds = 20;
}
