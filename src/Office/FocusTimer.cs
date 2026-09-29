using System;

namespace DeskArcade.Office;

public enum FocusPhase { Off, Focus, Break }

/// <summary>What changed at a step of the focus timer.</summary>
public enum FocusEvent { FocusDone, BreakOver, NextFocus }

/// <summary>
/// Focus blocks in the Pomodoro way: focus for a while (25 minutes by default) with the games out of sight, then a short
/// break with them back; every fourth break is a long one of 15 minutes. After a break the next block starts by itself
/// when <c>auto</c> is on, otherwise the timer stops until it is started again.
/// </summary>
public sealed class FocusTimer
{
    public const int BlocksPerSet = 4, LongBreakMinutes = 15;

    public FocusPhase Phase { get; private set; }
    /// <summary>When the current focus block or break ends (UTC).</summary>
    public DateTime Ends { get; private set; }
    /// <summary>Which block of the set of four this is (1–4), or the one just finished during a break.</summary>
    public int Block { get; private set; }
    /// <summary>True during the long break after the fourth block.</summary>
    public bool LongBreak { get; private set; }

    public bool Focusing => Phase == FocusPhase.Focus;

    /// <summary>A block started this soon after the last break counts on in the set of four; later, a new set begins.</summary>
    public static readonly TimeSpan SetGap = TimeSpan.FromMinutes(30);

    DateTime _breakEnded = DateTime.MinValue;

    public void Start(DateTime nowUtc, int focusMinutes)
    {
        bool onward = Phase == FocusPhase.Break || (Phase == FocusPhase.Off && Block > 0 && nowUtc - _breakEnded < SetGap);
        Block = onward ? Block % BlocksPerSet + 1 : 1;
        Phase = FocusPhase.Focus;
        LongBreak = false;
        Ends = nowUtc + TimeSpan.FromMinutes(Math.Max(1, focusMinutes));
    }

    /// <summary>Ends the block or break early; the next block starts a new set.</summary>
    public void Stop()
    {
        Phase = FocusPhase.Off;
        LongBreak = false;
        Block = 0;
    }

    /// <summary>Time left in the current block or break.</summary>
    public TimeSpan Left(DateTime nowUtc) => Phase == FocusPhase.Off || Ends <= nowUtc ? TimeSpan.Zero : Ends - nowUtc;

    public FocusEvent? Step(DateTime nowUtc, int focusMinutes, int breakMinutes, bool auto)
    {
        if (Phase == FocusPhase.Off || nowUtc < Ends) return null;
        if (Phase == FocusPhase.Focus)
        {
            Phase = FocusPhase.Break;
            LongBreak = Block >= BlocksPerSet;
            Ends = nowUtc + TimeSpan.FromMinutes(LongBreak ? LongBreakMinutes : Math.Max(1, breakMinutes));
            return FocusEvent.FocusDone;
        }
        if (auto)
        {
            Start(nowUtc, focusMinutes);
            return FocusEvent.NextFocus;
        }
        Phase = FocusPhase.Off;
        LongBreak = false;
        _breakEnded = nowUtc;
        return FocusEvent.BreakOver;
    }
}
