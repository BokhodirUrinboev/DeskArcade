using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>
/// Curling on a sheet of ice along the taskbar, on a fixed sheet (<see cref="Length"/> × <see cref="Width"/> units) so two
/// screens of any size play the same ice. The stones slide from the hack on the left toward the house on the right and
/// knock each other about (<see cref="DiscTable"/>). A stone that touches the side boards or passes the back line is out
/// of play, and so is one that stops short of the hog line. An end is eight stones, four a side, thrown in turn; then the
/// side with the stone nearest the button scores one for each of its stones nearer than the other side's best (in the
/// house only). A game is three ends; the side that did not score throws first in the next end. Sweeping in front of a
/// moving stone lets it slide further. UI-free, so the scoring and the computer player can be tested.
/// </summary>
public sealed class CurlingRules
{
    public const double Length = 1000, Width = 160, StoneR = 11, HouseR = 62, HogX = 560, BackX = 960;
    public const double MaxSpeed = 470, Friction = 58, SweepDrag = 0.72;
    public const int StonesPerSide = 4, Ends = 3;
    public static readonly Vec2 Hack = new(58, Width / 2), Button = new(846, Width / 2);

    readonly DiscTable _ice = new()
    {
        Bounds = new Rect(0, 0, BackX + 40, Width), Friction = Friction, Damping = 0.04, Restitution = 0.9, CushionRestitution = 0,
    };
    readonly int[] _score = new int[2];
    bool _remote; // the stone was thrown on the other screen: it settles when that screen says where everything stopped

    public CurlingRules(int firstSide = 0)
    {
        First = firstSide;
        Turn = firstSide;
        _ice.Cushion += (d, _) => d.Sunk = true; // the side boards (and the back board past the back line) take a stone out
    }

    public IReadOnlyList<Disc> Stones => _ice.Discs;
    public int End { get; private set; } = 1;
    public int First { get; private set; }
    public int Turn { get; private set; }
    public int Thrown { get; private set; } // stones thrown this end
    public bool Moving { get; private set; }
    public bool Over { get; private set; }
    public Disc? Current { get; private set; }
    public int Score(int side) => _score[side];

    /// <summary>The stones as they lay when the last end closed (the ice is cleared for the next end).</summary>
    public List<(double X, double Y, int Side, bool Out)> EndStones { get; private set; } = new();

    /// <summary>What the last end scored: the side (−1 for a blank end) and the points.</summary>
    public (int Side, int Points) LastEnd { get; private set; } = (-1, 0);

    public int StonesLeft(int side) => StonesPerSide - _ice.Discs.Count(d => d.Tag == side);

    public event Action<Disc, Disc, double>? Collided
    {
        add => _ice.Collided += value;
        remove => _ice.Collided -= value;
    }

    /// <summary>
    /// Throws the side to move's next stone from the hack; false if one is still sliding or the game is over. A
    /// <paramref name="remote"/> throw (the co-worker's, replayed here) waits for <see cref="SettleFrom"/> instead of settling.
    /// </summary>
    public bool Throw(Vec2 velocity, bool remote = false)
    {
        if (Moving || Over) return false;
        _remote = remote;
        double speed = velocity.Length;
        if (speed > MaxSpeed) velocity = velocity * (MaxSpeed / speed);
        Current = _ice.Add(Hack, StoneR, 1, Turn);
        Current.Vel = velocity;
        Moving = true;
        return true;
    }

    /// <summary>While <paramref name="on"/>, the stone just thrown slides with less friction.</summary>
    public void Sweep(bool on)
    {
        if (Current is { } c) c.Drag = on && Moving ? SweepDrag : 1;
    }

    /// <summary>Advances the ice; when everything has stopped the throw is settled (true once, at that moment).</summary>
    public bool Step(double dt)
    {
        if (!Moving) return false;
        _ice.Advance(dt);
        foreach (var d in _ice.Discs) // past the back line, or against a side board (a collision can press one there)
            if (!d.Sunk && (d.Pos.X > BackX + d.R || d.Pos.Y - d.R <= 0.5 || d.Pos.Y + d.R >= Width - 0.5)) d.Sunk = true;
        if (!_ice.AllStill || _remote) return false;
        Settle();
        return true;
    }

    /// <summary>Runs a throw until everything stops (tests and the computer's planning).</summary>
    public void Finish()
    {
        for (int i = 0; i < 20000 && Moving; i++) Step(DiscTable.Step * 4);
    }

    void Settle()
    {
        Moving = false;
        if (Current is { Sunk: false } c && c.Pos.X < HogX) c.Sunk = true; // short of the hog line
        if (Current != null) Current.Drag = 1;
        Current = null;
        Thrown++;
        if (Thrown < StonesPerSide * 2)
        {
            Turn = 1 - Turn;
            return;
        }
        ScoreEnd();
    }

    /// <summary>The side lying shot and how many it counts, as the end stands now.</summary>
    public (int Side, int Points) Count()
    {
        var inHouse = _ice.Discs.Where(d => !d.Sunk && (d.Pos - Button).Length <= HouseR + d.R).OrderBy(d => (d.Pos - Button).Length).ToList();
        if (inHouse.Count == 0) return (-1, 0);
        int side = inHouse[0].Tag;
        var theirs = inHouse.FirstOrDefault(d => d.Tag != side);
        double limit = theirs == null ? double.MaxValue : (theirs.Pos - Button).Length;
        return (side, inHouse.Count(d => d.Tag == side && (d.Pos - Button).Length < limit));
    }

    void ScoreEnd()
    {
        EndStones = _ice.Discs.Select(d => (d.Pos.X, d.Pos.Y, d.Tag, d.Sunk)).ToList();
        LastEnd = Count();
        if (LastEnd.Side >= 0) _score[LastEnd.Side] += LastEnd.Points;
        if (End >= Ends)
        {
            Over = true;
            return;
        }
        End++;
        _ice.Discs.Clear();
        Thrown = 0;
        // the side that did not score throws first; after a blank end the first thrower stays
        if (LastEnd.Side >= 0) First = 1 - LastEnd.Side;
        Turn = First;
    }

    /// <summary>The winner once <see cref="Over"/>: 0, 1, or −1 for a tie.</summary>
    public int Winner => !Over ? -1 : _score[0] > _score[1] ? 0 : _score[1] > _score[0] ? 1 : -1;

    // ------------------------------------------------------------------ the computer

    /// <summary>The start speed that stops a stone after <paramref name="distance"/> units on this ice.</summary>
    public double DrawSpeed(double distance) => _ice.SpeedToTravel(distance, 0);

    /// <summary>
    /// The computer's throw at <paramref name="level"/> (1–4): take out the other side's shot stone when it lies in the
    /// house, otherwise draw to the button; aim and weight both wobble less at the higher levels.
    /// </summary>
    public Vec2 CpuThrow(int level, Random rng)
    {
        int me = Turn;
        var (side, _) = Count();
        Vec2 target = Button;
        double speed;
        var shot = _ice.Discs.Where(d => !d.Sunk && d.Tag != me).OrderBy(d => (d.Pos - Button).Length).FirstOrDefault();
        if (side >= 0 && side != me && shot != null && level >= 2)
        {
            target = shot.Pos;
            speed = Math.Min(MaxSpeed, DrawSpeed((target - Hack).Length) * 1.35 + 40); // a takeout: through it
        }
        else speed = DrawSpeed((Button - Hack).Length);
        double[] aimError = { 0.035, 0.018, 0.009, 0.004 }, weightError = { 0.07, 0.04, 0.022, 0.01 };
        int k = Math.Clamp(level, 1, 4) - 1;
        double angle = Math.Atan2(target.Y - Hack.Y, target.X - Hack.X) + Gauss(rng) * aimError[k];
        speed *= 1 + Gauss(rng) * weightError[k];
        return new Vec2(Math.Cos(angle), Math.Sin(angle)) * speed;
    }

    static double Gauss(Random rng) => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());

    /// <summary>Puts the stones where another screen says they came to rest (a LAN throw is settled by the thrower's screen).</summary>
    public void Sync(IReadOnlyList<(double X, double Y, int Side, bool Out)> stones)
    {
        _ice.Discs.Clear();
        foreach (var (x, y, side, gone) in stones)
        {
            var d = _ice.Add(new Vec2(x, y), StoneR, 1, side);
            d.Sunk = gone;
        }
    }

    /// <summary>For a LAN game: the throw settled on the thrower's screen; take its stones and move on as it did.</summary>
    public void SettleFrom(IReadOnlyList<(double X, double Y, int Side, bool Out)> stones)
    {
        Sync(stones);
        Current = null;
        Moving = _remote = false;
        Thrown++;
        if (Thrown < StonesPerSide * 2) Turn = 1 - Turn;
        else ScoreEnd();
    }
}
