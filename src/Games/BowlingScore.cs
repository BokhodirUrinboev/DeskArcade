using System;
using System.Collections.Generic;

namespace DeskArcade.Games;

/// <summary>
/// Ten-pin scoring without any UI: the rolls so far, the frame scores and running totals, whose ball it
/// is and how many pins are standing for it. A strike scores 10 plus the next two balls, a spare 10 plus
/// the next one; the 10th frame gives one or two bonus balls to finish a strike or spare.
/// </summary>
public sealed class BowlingScore
{
    public const int Frames = 10, Pins = 10;

    readonly List<int> _rolls = new();

    public IReadOnlyList<int> Rolls => _rolls;

    /// <summary>The frame the next ball belongs to (0-based; 9 while finishing the 10th).</summary>
    public int Frame { get; private set; }
    /// <summary>Which ball of <see cref="Frame"/> comes next: 0, 1 or (10th frame only) 2.</summary>
    public int Ball { get; private set; }
    /// <summary>Pins standing for the next ball: 10 on a fresh rack.</summary>
    public int PinsStanding { get; private set; } = Pins;
    /// <summary>
    /// True when the next ball faces a freshly set rack: the first ball of a frame, or in the 10th frame the
    /// ball after a strike or a spare. Ten pins on a fresh rack is a strike; clearing a rack that isn't fresh
    /// is a spare (even after a first ball of 0).
    /// </summary>
    public bool FreshRack { get; private set; } = true;
    public bool GameOver { get; private set; }

    public enum Kind { Open, Strike, Spare }

    public void Reset()
    {
        _rolls.Clear();
        Recount();
    }

    /// <summary>Records a ball (more pins than are standing count as all of them) and says what it was.</summary>
    public Kind Roll(int pins)
    {
        if (GameOver) throw new InvalidOperationException("the game is over");
        pins = Math.Clamp(pins, 0, PinsStanding);
        var kind = pins < PinsStanding ? Kind.Open : FreshRack ? Kind.Strike : Kind.Spare;
        _rolls.Add(pins);
        Recount();
        return kind;
    }

    /// <summary>Index of the first roll of each frame (-1 for frames not reached yet).</summary>
    int[] FrameStarts()
    {
        var starts = new int[Frames];
        int r = 0;
        for (int f = 0; f < Frames; f++)
        {
            if (r >= _rolls.Count)
            {
                for (int g = f; g < Frames; g++) starts[g] = -1;
                break;
            }
            starts[f] = r;
            r += f < Frames - 1 && _rolls[r] == Pins ? 1 : 2;
        }
        return starts;
    }

    void Recount()
    {
        var starts = FrameStarts();
        int last = Array.FindLastIndex(starts, s => s >= 0);
        GameOver = false;
        if (last < 0)
        {
            Frame = Ball = 0;
            PinsStanding = Pins;
            FreshRack = true;
            return;
        }
        int start = starts[last], thrown = _rolls.Count - start;
        if (last < Frames - 1)
        {
            bool done = _rolls[start] == Pins || thrown >= 2;
            Frame = done ? last + 1 : last;
            Ball = done ? 0 : 1;
            PinsStanding = done ? Pins : Pins - _rolls[start];
            FreshRack = done;
            return;
        }

        // the 10th frame: a strike or a spare earns a third ball, and the rack resets after each strike or spare
        Frame = last;
        int r0 = _rolls[start], r1 = thrown > 1 ? _rolls[start + 1] : -1;
        if (thrown == 1)
        {
            Ball = 1;
            PinsStanding = r0 == Pins ? Pins : Pins - r0;
            FreshRack = r0 == Pins;
        }
        else if (thrown == 2 && (r0 == Pins || r0 + r1 == Pins))
        {
            // after X X or a spare the third ball gets a new rack; after X and a count it faces what is left
            Ball = 2;
            FreshRack = r0 < Pins || r1 == Pins;
            PinsStanding = FreshRack ? Pins : Pins - r1;
        }
        else
        {
            Ball = thrown;
            PinsStanding = 0;
            FreshRack = false;
            GameOver = true;
        }
    }

    /// <summary>What each frame scored, or null while it waits for bonus balls or hasn't been bowled.</summary>
    public int?[] FrameScores()
    {
        var starts = FrameStarts();
        var scores = new int?[Frames];
        for (int f = 0; f < Frames; f++)
        {
            int s = starts[f];
            if (s < 0) break;
            int need = _rolls[s] == Pins ? 3 : 2;
            if (need == 2 && s + 1 < _rolls.Count && _rolls[s] + _rolls[s + 1] == Pins) need = 3;
            if (s + need > _rolls.Count) continue;
            int sum = 0;
            for (int i = 0; i < need; i++) sum += _rolls[s + i];
            scores[f] = sum;
        }
        return scores;
    }

    /// <summary>The running total shown under each frame; null until that frame and all before it are scored.</summary>
    public int?[] RunningTotals()
    {
        var scores = FrameScores();
        var totals = new int?[Frames];
        int sum = 0;
        for (int f = 0; f < Frames; f++)
        {
            if (scores[f] is not int s) break;
            sum += s;
            totals[f] = sum;
        }
        return totals;
    }

    /// <summary>The score so far, counting open frames and strikes/spares with the bonus balls bowled so far.</summary>
    public int Total
    {
        get
        {
            var starts = FrameStarts();
            int sum = 0;
            for (int f = 0; f < Frames; f++)
            {
                int s = starts[f];
                if (s < 0) break;
                int need = _rolls[s] == Pins ? 3 : s + 1 < _rolls.Count && _rolls[s] + _rolls[s + 1] == Pins ? 3 : 2;
                for (int i = 0; i < need && s + i < _rolls.Count; i++) sum += _rolls[s + i];
            }
            return sum;
        }
    }

    /// <summary>
    /// The marks in a frame's little boxes: "X" strike, "/" spare, "-" a miss, else the pin count; "" for a
    /// ball not bowled. Two boxes, three in the 10th frame.
    /// </summary>
    public string[] Marks(int frame)
    {
        var starts = FrameStarts();
        bool tenth = frame == Frames - 1;
        var marks = new string[tenth ? 3 : 2];
        Array.Fill(marks, "");
        int s = starts[frame];
        if (s < 0) return marks;
        if (!tenth)
        {
            int r0 = _rolls[s];
            if (r0 == Pins)
            {
                marks[1] = "X"; // a strike is marked in the right-hand box
                return marks;
            }
            marks[0] = Mark(r0);
            if (s + 1 < _rolls.Count) marks[1] = r0 + _rolls[s + 1] == Pins ? "/" : Mark(_rolls[s + 1]);
            return marks;
        }
        int standing = Pins;
        bool fresh = true;
        for (int i = 0; i < 3 && s + i < _rolls.Count; i++)
        {
            int r = _rolls[s + i];
            bool cleared = r == standing;
            marks[i] = cleared ? (fresh ? "X" : "/") : Mark(r);
            fresh = cleared;
            standing = cleared ? Pins : standing - r;
        }
        return marks;
    }

    static string Mark(int pins) => pins == 0 ? "-" : pins.ToString();
}
