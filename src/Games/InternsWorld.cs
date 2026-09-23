using System;
using System.Collections.Generic;
using System.Linq;

namespace DeskArcade.Games;

/// <summary>
/// The simulation behind Interns, free of UI. Little office interns drop from a trapdoor one at a time and walk
/// along whatever is under their feet: the floor (the top of the taskbar, with open manholes in it), window
/// tops, and stairs that builders lay. They turn round at the edges of the screen and at blockers, step up
/// small rises, and fall off edges; a fall higher than <see cref="SafeFall"/> without an umbrella kills them,
/// and so does a manhole. Reaching the exit door saves them.
/// Tools (<see cref="Assign"/>): an umbrella (kept for good), a blocker (click again to let them walk on),
/// and a builder, who lays <see cref="StairSteps"/> steps up in the direction they face.
/// Coordinates are screen DIPs with y growing downwards; an intern's position is their feet.
/// </summary>
public sealed class InternsWorld
{
    public const double WalkSpeed = 34, FallSpeed = 230, FloatSpeed = 70, SafeFall = 110, StepUp = 7, StepDown = 4;
    public const double BrickW = 14, BrickRise = 4, BrickEvery = 0.45, BlockReach = 9, ExitReach = 9, SpawnEvery = 1.5;
    public const int StairSteps = 12;

    public enum State { Walking, Falling, Blocking, Building, Saved, Dead }
    public enum Tool { Umbrella, Blocker, Builder }

    /// <summary>A walkable top edge: a window top (with its window's id), the floor, or a step of stairs.</summary>
    public readonly record struct Ledge(double Y, double X1, double X2, IntPtr Window = default);

    public sealed class Intern
    {
        public int Id;
        public double X, Y, Dir = 1, FallFrom, BuildT;
        public State State;
        public bool Umbrella;
        public int StepsLeft;
        /// <summary>The window they stand on (they ride along when it moves), or zero.</summary>
        public IntPtr On;
        /// <summary>Seconds since they last changed state, for animation.</summary>
        public double Age;
        public bool Splat; // dead from a fall (rather than a manhole)
    }

    readonly List<Intern> _interns = new();
    readonly List<Ledge> _bricks = new();
    readonly Dictionary<Tool, int> _tools = new();
    double _spawnT;

    public double Left { get; }
    public double Right { get; }
    public double Floor { get; }
    /// <summary>Open manholes in the floor, as x ranges.</summary>
    public IReadOnlyList<(double X1, double X2)> Pits { get; }
    public double SpawnX { get; private set; }
    public double SpawnY { get; private set; }
    public IntPtr SpawnOn { get; }
    public double SpawnDir { get; }
    public double ExitX { get; }
    public int Total { get; }
    public int Needed { get; }
    public int Released { get; private set; }
    public bool Open { get; set; }

    public IReadOnlyList<Intern> Interns => _interns;
    public IReadOnlyList<Ledge> Bricks => _bricks;
    public int Saved => _interns.Count(i => i.State == State.Saved);
    public int Lost => _interns.Count(i => i.State == State.Dead);
    public int Active => _interns.Count(i => i.State is not (State.Saved or State.Dead));
    /// <summary>Everyone is out and nobody is still on the way (blockers count as on the way).</summary>
    public bool Finished => Released == Total && Active == 0;
    /// <summary>Only blockers are left: they would stand there for ever.</summary>
    public bool OnlyBlockersLeft => Released == Total && Active > 0 && _interns.All(i => i.State is State.Saved or State.Dead or State.Blocking);

    public InternsWorld(double left, double right, double floor, IEnumerable<(double X1, double X2)> pits,
        double spawnX, double spawnY, IntPtr spawnOn, double spawnDir, double exitX, int total, int needed,
        int umbrellas, int blockers, int builders)
    {
        Left = left;
        Right = right;
        Floor = floor;
        Pits = pits.OrderBy(p => p.X1).ToList();
        SpawnX = spawnX;
        SpawnY = spawnY;
        SpawnOn = spawnOn;
        SpawnDir = spawnDir >= 0 ? 1 : -1;
        ExitX = exitX;
        Total = total;
        Needed = needed;
        _tools[Tool.Umbrella] = umbrellas;
        _tools[Tool.Blocker] = blockers;
        _tools[Tool.Builder] = builders;
    }

    public int Count(Tool t) => _tools[t];

    /// <summary>The floor between the manholes.</summary>
    public IEnumerable<Ledge> FloorLedges()
    {
        double x = Left;
        foreach (var (a, b) in Pits)
        {
            if (a > x) yield return new Ledge(Floor, x, a);
            x = Math.Max(x, b);
        }
        if (x < Right) yield return new Ledge(Floor, x, Right);
    }

    /// <summary>Moves the trapdoor with the window it hangs under.</summary>
    public void MoveSpawn(double dx, double dy)
    {
        SpawnX = Math.Clamp(SpawnX + dx, Left + 10, Right - 10);
        SpawnY += dy;
    }

    /// <summary>Gives <paramref name="tool"/> to an intern. False when none are left or it doesn't suit what they're doing.</summary>
    public bool Assign(Intern who, Tool tool)
    {
        if (who.State is State.Saved or State.Dead) return false;
        if (who.State == State.Blocking)
        {
            // a click on a blocker lets them walk on, whatever tool is picked; the blocker isn't given back
            who.State = State.Walking;
            who.Age = 0;
            return true;
        }
        if (_tools[tool] <= 0) return false;
        switch (tool)
        {
            case Tool.Umbrella:
                if (who.Umbrella) return false;
                who.Umbrella = true;
                break;
            case Tool.Blocker:
                if (who.State != State.Walking) return false;
                who.State = State.Blocking;
                break;
            case Tool.Builder:
                if (who.State != State.Walking) return false;
                who.State = State.Building;
                who.StepsLeft = StairSteps;
                who.BuildT = 0;
                break;
        }
        who.Age = 0;
        _tools[tool]--;
        return true;
    }

    /// <summary>
    /// Advances everyone by <paramref name="dt"/> seconds over <paramref name="windows"/> (window tops this
    /// frame), plus the floor and the stairs. Returns the interns saved or lost during the step.
    /// </summary>
    public List<Intern> Step(double dt, IReadOnlyList<Ledge> windows)
    {
        var changed = new List<Intern>();
        var ledges = new List<Ledge>(windows.Count + _bricks.Count + Pits.Count + 1);
        ledges.AddRange(windows);
        ledges.AddRange(FloorLedges());
        ledges.AddRange(_bricks);

        if (Open && Released < Total && (_spawnT -= dt) <= 0)
        {
            _spawnT = SpawnEvery;
            _interns.Add(new Intern { Id = Released++, X = SpawnX, Y = SpawnY, FallFrom = SpawnY, Dir = SpawnDir, State = State.Falling });
        }

        foreach (var it in _interns)
        {
            if (it.State is State.Saved or State.Dead) continue;
            it.Age += dt;
            var before = it.State;
            switch (it.State)
            {
                case State.Walking: Walk(it, dt, ledges); break;
                case State.Falling: Fall(it, dt, ledges); break;
                case State.Blocking:
                    if (Ground(it.X, it.Y, ledges) is null) StartFalling(it); // the window under them went away
                    break;
                case State.Building: Build(it, dt, ledges); break;
            }
            if (it.State is State.Walking && Math.Abs(it.X - ExitX) <= ExitReach && Math.Abs(it.Y - Floor) < 3)
                it.State = State.Saved;
            if (it.State != before)
            {
                it.Age = 0;
                if (it.State is State.Saved or State.Dead) changed.Add(it);
            }
        }
        return changed;
    }

    void Walk(Intern it, double dt, List<Ledge> ledges)
    {
        double nx = it.X + it.Dir * WalkSpeed * dt;
        if (nx < Left + 4 || nx > Right - 4 || Blocked(it, nx))
        {
            it.Dir = -it.Dir;
            return;
        }
        if (Ground(nx, it.Y, ledges) is { } g)
        {
            it.X = nx;
            it.Y = g.Y;
            it.On = g.Window;
        }
        else
        {
            it.X = nx;
            StartFalling(it);
        }
    }

    bool Blocked(Intern it, double nx) => _interns.Any(b =>
        b != it && b.State == State.Blocking && Math.Abs(b.Y - it.Y) < 12 &&
        Math.Abs(b.X - it.X) > 0.5 && Math.Sign(b.X - it.X) == Math.Sign(it.Dir) && Math.Abs(b.X - nx) < BlockReach);

    void StartFalling(Intern it)
    {
        it.State = State.Falling;
        it.FallFrom = it.Y;
        it.On = default;
    }

    void Fall(Intern it, double dt, List<Ledge> ledges)
    {
        double ny = it.Y + (it.Umbrella ? FloatSpeed : FallSpeed) * dt;
        // land on the highest edge crossed on the way down
        Ledge? hit = null;
        foreach (var l in ledges)
            if (it.X >= l.X1 && it.X <= l.X2 && it.Y <= l.Y + 1 && ny >= l.Y && (hit is null || l.Y < hit.Value.Y))
                hit = l;
        if (hit is { } h)
        {
            it.Y = h.Y;
            it.On = h.Window;
            if (!it.Umbrella && h.Y - it.FallFrom > SafeFall)
            {
                it.State = State.Dead;
                it.Splat = true;
            }
            else it.State = State.Walking;
            return;
        }
        it.Y = ny;
        if (it.Y > Floor + 24) it.State = State.Dead; // down a manhole (or off a window with nothing below)
    }

    void Build(Intern it, double dt, List<Ledge> ledges)
    {
        if ((it.BuildT += dt) < BrickEvery) return;
        it.BuildT = 0;
        double x1 = it.Dir > 0 ? it.X : it.X - BrickW, x2 = x1 + BrickW;
        if (x1 < Left + 2 || x2 > Right - 2 || it.StepsLeft <= 0)
        {
            it.State = State.Walking;
            it.Dir = it.StepsLeft <= 0 ? it.Dir : -it.Dir;
            return;
        }
        var brick = new Ledge(it.Y - BrickRise, x1, x2);
        _bricks.Add(brick);
        ledges.Add(brick);
        it.X += it.Dir * BrickW / 2;
        it.Y = brick.Y;
        it.On = default;
        if (--it.StepsLeft == 0) it.State = State.Walking;
    }

    /// <summary>What someone at (<paramref name="x"/>, <paramref name="y"/>) stands on: the highest edge a step up or down.</summary>
    static Ledge? Ground(double x, double y, List<Ledge> ledges)
    {
        Ledge? best = null;
        foreach (var l in ledges)
            if (x >= l.X1 && x <= l.X2 && l.Y >= y - StepUp && l.Y <= y + StepDown && (best is null || l.Y < best.Value.Y))
                best = l;
        return best;
    }

    /// <summary>Lets an intern ride the window they stand on when it moves.</summary>
    public void Carry(IntPtr window, double dx, double dy)
    {
        foreach (var it in _interns)
            if (it.On == window && it.State is State.Walking or State.Blocking or State.Building)
            {
                it.X = Math.Clamp(it.X + dx, Left + 4, Right - 4);
                it.Y += dy;
            }
    }

    /// <summary>The nearest intern to a point within <paramref name="reach"/>, still in play; null if none.</summary>
    public Intern? At(double x, double y, double reach) => _interns
        .Where(i => i.State is not (State.Saved or State.Dead))
        .Select(i => (i, d: Math.Sqrt((i.X - x) * (i.X - x) + (i.Y - 8 - y) * (i.Y - 8 - y))))
        .Where(t => t.d <= reach).OrderBy(t => t.d).Select(t => t.i).FirstOrDefault();
}
