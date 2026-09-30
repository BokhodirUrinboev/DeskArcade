using System;
using System.Collections.Generic;
using System.Linq;
using DeskArcade.Engine;

namespace DeskArcade.Games;

public enum ShortcutPhase { Ready, Asking, Missed, Got, Over }

public enum ShortcutAnswer { Ignored, Progress, Right, Wrong }

/// <summary>
/// A drill of ten shortcuts, one at a time: press the keys (or type them, for a typed drill). Right at the first try
/// scores 10, plus up to 10 more the quicker it came (the bonus runs out after <see cref="BonusSeconds"/>). A wrong press
/// shows the answer, and the shortcut then has to be pressed right to go on, for nothing; Skip gives it up.
/// </summary>
public sealed class ShortcutRound
{
    public const int Size = 10, Base = 10, SpeedBonus = 10;
    public const double BonusSeconds = 8;

    readonly List<KeyChord> _pressed = new();

    public ShortcutRound(IReadOnlyList<ShortcutDrill> drills, KeyOs os)
    {
        Drills = drills;
        Os = os;
    }

    public IReadOnlyList<ShortcutDrill> Drills { get; }
    public KeyOs Os { get; }
    public ShortcutPhase Phase { get; private set; }
    public int Index { get; private set; }
    public int Score { get; private set; }
    /// <summary>Shortcuts right at the first try.</summary>
    public int FirstTry { get; private set; }
    public int LastPoints { get; private set; }
    /// <summary>Seconds spent on the shortcut being asked.</summary>
    public double Seconds { get; private set; }
    /// <summary>The shortcut being asked was pressed wrong (or skipped) at least once.</summary>
    public bool MissedThis { get; private set; }
    /// <summary>What has been typed of a typed drill so far.</summary>
    public string Buffer { get; private set; } = "";
    /// <summary>The chords pressed so far of a sequence (Ctrl+K, then Z).</summary>
    public IReadOnlyList<KeyChord> Pressed => _pressed;

    public ShortcutDrill Current => Drills[Math.Min(Index, Drills.Count - 1)];
    public bool Perfect => Phase == ShortcutPhase.Over && FirstTry == Drills.Count;
    bool Open => Phase is ShortcutPhase.Asking or ShortcutPhase.Missed;

    public static int MaxScore(int count) => count * (Base + SpeedBonus);

    public static int Points(double seconds) => Base + (int)Math.Round(SpeedBonus * Math.Max(0, 1 - seconds / BonusSeconds));

    public void Start()
    {
        if (Phase != ShortcutPhase.Ready || Drills.Count == 0) return;
        Phase = ShortcutPhase.Asking;
    }

    public ShortcutAnswer Press(KeyChord chord)
    {
        if (!Open) return ShortcutAnswer.Ignored;
        if (Current.Typed) return Miss();
        _pressed.Add(chord);
        var answers = Current.Chords(Os);
        var fits = answers.Where(a => a.Count >= _pressed.Count && a.Take(_pressed.Count).SequenceEqual(_pressed)).ToList();
        if (fits.Count == 0) return Miss();
        if (fits.Any(a => a.Count == _pressed.Count)) return Got();
        return ShortcutAnswer.Progress;
    }

    public ShortcutAnswer Type(string text)
    {
        if (!Open || !Current.Typed || text.Length == 0) return ShortcutAnswer.Ignored;
        Buffer += text;
        var answers = Current.Answers(Os);
        if (answers.Contains(Buffer)) return Got();
        if (answers.Any(a => a.StartsWith(Buffer, StringComparison.Ordinal))) return ShortcutAnswer.Progress;
        return Miss();
    }

    public void Backspace()
    {
        if (Open && Buffer.Length > 0) Buffer = Buffer[..^1];
    }

    /// <summary>Gives the shortcut up: it counts as missed and scores nothing.</summary>
    public void Skip()
    {
        if (!Open) return;
        MissedThis = true;
        LastPoints = 0;
        Phase = ShortcutPhase.Got;
    }

    ShortcutAnswer Miss()
    {
        MissedThis = true;
        Buffer = "";
        _pressed.Clear();
        Phase = ShortcutPhase.Missed;
        return ShortcutAnswer.Wrong;
    }

    ShortcutAnswer Got()
    {
        LastPoints = MissedThis ? 0 : Points(Seconds);
        Score += LastPoints;
        if (!MissedThis) FirstTry++;
        Phase = ShortcutPhase.Got;
        return ShortcutAnswer.Right;
    }

    /// <summary>On to the next shortcut once this one is got (or the drill is over after the last).</summary>
    public void Next()
    {
        if (Phase != ShortcutPhase.Got) return;
        Index++;
        Seconds = 0;
        MissedThis = false;
        Buffer = "";
        _pressed.Clear();
        Phase = Index >= Drills.Count ? ShortcutPhase.Over : ShortcutPhase.Asking;
    }

    public void Tick(double dt)
    {
        if (Open) Seconds += dt;
    }
}
