using System;
using System.Collections.Generic;
using System.Linq;

namespace DeskArcade.Games;

public enum SpotPhase { Ready, Playing, Revealed, Over }

public enum SpotClick { Ignored, Wrong, Right, Missed }

/// <summary>
/// A round of Spot the Bug: a few snippets, one after another, each against its own fuse. Finding the bug scores
/// <see cref="FoundPoints"/> plus a speed bonus of up to <see cref="SpeedPoints"/> for the fuse left; every wrong click
/// takes <see cref="WrongPenalty"/> off that snippet (which never scores below 0), and the <see cref="MaxWrong"/>th wrong
/// click, or the fuse burning down, misses it. After each snippet the fix and the reason are shown (the fuse waits),
/// and <see cref="Next"/> moves on. A daily snippet is a round of one. UI-free, so it can be tested.
/// </summary>
public sealed class SpotBugRound
{
    public const int FoundPoints = 10, SpeedPoints = 10, WrongPenalty = 3, MaxWrong = 3;

    readonly HashSet<int> _tried = new();

    public SpotBugRound(IReadOnlyList<Snippet> snippets)
    {
        if (snippets.Count == 0) throw new ArgumentException("a round needs a snippet", nameof(snippets));
        Snippets = snippets;
        Left = FuseFor(snippets[0].Level);
    }

    /// <summary>Seconds on a snippet's fuse: longer for the harder, longer snippets.</summary>
    public static double FuseFor(BugLevel level) => level switch { BugLevel.Easy => 30, BugLevel.Medium => 40, _ => 50 };

    /// <summary>What finding a bug scores with <paramref name="left"/> of <paramref name="fuse"/> seconds to spare after <paramref name="wrong"/> wrong clicks.</summary>
    public static int PointsFor(double left, double fuse, int wrong) =>
        Math.Max(0, FoundPoints + (int)Math.Round(SpeedPoints * Math.Clamp(left / fuse, 0, 1)) - WrongPenalty * wrong);

    public static int MaxScore(int snippets) => snippets * (FoundPoints + SpeedPoints);

    public IReadOnlyList<Snippet> Snippets { get; }
    public int Index { get; private set; }
    public Snippet Current => Snippets[Index];
    public SpotPhase Phase { get; private set; } = SpotPhase.Ready;
    public bool IsLast => Index == Snippets.Count - 1;

    /// <summary>The current snippet's fuse: its length and what is left of it.</summary>
    public double Fuse => FuseFor(Current.Level);
    public double Left { get; private set; }

    /// <summary>Wrong clicks on the current snippet, and the lines they hit.</summary>
    public int Wrong { get; private set; }
    public IReadOnlyCollection<int> Tried => _tried;

    public int Score { get; private set; }
    public int Found { get; private set; }
    /// <summary>Bugs found with the first click.</summary>
    public int FirstClicks { get; private set; }
    /// <summary>All the clicks of the round, right and wrong.</summary>
    public int Clicks { get; private set; }
    /// <summary>Seconds spent looking, fuses only (reading the fix doesn't count).</summary>
    public double Seconds { get; private set; }

    /// <summary>How the last snippet went: found or not, what it scored, and in how long.</summary>
    public bool LastFound { get; private set; }
    public int LastPoints { get; private set; }
    public double LastSeconds { get; private set; }
    /// <summary>The last snippet went because its fuse burned down (rather than three wrong clicks).</summary>
    public bool LastTimedOut { get; private set; }

    /// <summary>Found every bug, each with the first click.</summary>
    public bool Perfect => Phase == SpotPhase.Over && FirstClicks == Snippets.Count;

    public void Start()
    {
        if (Phase != SpotPhase.Ready) return;
        Phase = SpotPhase.Playing;
        Left = Fuse;
    }

    /// <summary>A click on <paramref name="line"/> (1-based) of the current snippet.</summary>
    public SpotClick Click(int line)
    {
        if (Phase != SpotPhase.Playing || line < 1 || line > Current.Lines || _tried.Contains(line)) return SpotClick.Ignored;
        Clicks++;
        if (Current.IsBug(line))
        {
            LastPoints = PointsFor(Left, Fuse, Wrong);
            if (Wrong == 0) FirstClicks++;
            Found++;
            Reveal(found: true, timedOut: false);
            return SpotClick.Right;
        }
        _tried.Add(line);
        Wrong++;
        if (Wrong < MaxWrong) return SpotClick.Wrong;
        LastPoints = 0;
        Reveal(found: false, timedOut: false);
        return SpotClick.Missed;
    }

    /// <summary>Burns the fuse; true when it has just run out (the snippet is missed and revealed).</summary>
    public bool Tick(double dt)
    {
        if (Phase != SpotPhase.Playing || dt <= 0) return false;
        Left = Math.Max(0, Left - dt);
        if (Left > 0) return false;
        LastPoints = 0;
        Reveal(found: false, timedOut: true);
        return true;
    }

    void Reveal(bool found, bool timedOut)
    {
        LastFound = found;
        LastTimedOut = timedOut;
        LastSeconds = Fuse - Left;
        Seconds += LastSeconds;
        Score += LastPoints;
        Phase = SpotPhase.Revealed;
    }

    /// <summary>After a reveal: on to the next snippet, or the round is over after the last.</summary>
    public void Next()
    {
        if (Phase != SpotPhase.Revealed) return;
        if (IsLast)
        {
            Phase = SpotPhase.Over;
            return;
        }
        Index++;
        Wrong = 0;
        _tried.Clear();
        Left = Fuse;
        Phase = SpotPhase.Playing;
    }
}

/// <summary>
/// The daily snippet's streak: days in a row with the bug found. A find on the day after the last one adds to it, a
/// find after a gap starts again at 1, and a miss breaks it. Days are <see cref="DateOnly.DayNumber"/>s.
/// </summary>
public static class SpotBugStreak
{
    public static int After(int lastFoundDay, int streak, int today, bool found) =>
        !found ? 0 : lastFoundDay == today - 1 ? streak + 1 : lastFoundDay == today ? Math.Max(1, streak) : 1;

    /// <summary>The streak to show today: it still stands when the last find was today or yesterday.</summary>
    public static int Showing(int lastFoundDay, int streak, int today) => lastFoundDay >= today - 1 ? streak : 0;

    /// <summary>The line to paste after a daily: "Spot the Bug daily #57 · found it in 1 click, 9 s 🐞".</summary>
    public static string ShareLine(int number, bool found, int clicks, int seconds) => found
        ? L.F("Spot the Bug daily #{0} · found it in {1}, {2} s", number, clicks == 1 ? L.T("1 click") : L.F("{0} clicks", clicks), seconds) + " 🐞"
        : L.F("Spot the Bug daily #{0} · the bug got away", number) + " 🐛";

    /// <summary>The line to paste after a round: "Spot the Bug · 8/10 bugs · 142 points 🐞".</summary>
    public static string RoundLine(int found, int of, int score) => L.F("Spot the Bug · {0}/{1} bugs · {2} points", found, of, score) + " 🐞";
}
