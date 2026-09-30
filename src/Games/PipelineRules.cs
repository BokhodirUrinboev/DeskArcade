using System;
using System.Collections.Generic;
using System.Linq;

namespace DeskArcade.Games;

/// <summary>A pipe piece: the two sides it joins (a cross joins all four, straight through), or nothing.</summary>
public enum Pipe { None, Horizontal, Vertical, NorthEast, NorthWest, SouthEast, SouthWest, Cross }

/// <summary>A side of a cell.</summary>
public enum Side { North, East, South, West }

/// <summary>What a straight piece may carry: a test run or a code review on the way, worth extra when the build flows through.</summary>
public enum Badge { None, Test, Review }

/// <summary>
/// Pipeline, free of UI: Pipe Mania with a CI/CD skin. A grid with a <b>commit</b> cell that the build leaves from on one
/// side and a <b>deploy</b> cell it must reach, blocked cells in the way, and a queue of pieces the player lays one at a
/// time, the front of the queue first, on any open cell. Laying a piece over one that is already there replaces it and
/// costs time. When the countdown runs out the build flows from the commit, one cell every <see cref="FlowStep"/>
/// seconds; each cell it enters must hold a piece open to the side it comes from, or the build leaks and the level is
/// lost. Reaching the deploy wins the level. The build scores for every piece it flows through, more for a test or a
/// review, and more again for crossing a cross the second time; the deploy pays by the level. Time only passes in
/// <see cref="Step"/>, so the rules can be tested and solved.
/// </summary>
public sealed class PipelineRules
{
    public const int Width = 10, Height = 7, QueueLength = 5;
    public const int PipePoints = 10, BadgePoints = 40, CrossPoints = 50, DeployPoints = 100;
    public const double ReplaceCost = 2, HurryStep = 0.12;

    public enum Phase { Building, Flowing, Deployed, Leaked }

    /// <summary>What happened, for the drawing and the sounds: "placed", "replaced", "flow", "enter" (a cell and its points), "leak", "deploy".</summary>
    public readonly record struct Event(string Kind, int X = -1, int Y = -1, int Points = 0);

    readonly Pipe[,] _pipes = new Pipe[Width, Height];
    readonly Badge[,] _badges = new Badge[Width, Height];
    readonly int[,] _passes = new int[Width, Height];
    readonly bool[,] _blocked = new bool[Width, Height];
    readonly List<(Pipe Pipe, Badge Badge)> _queue = new();
    readonly Random _rng;
    readonly List<(int X, int Y, Pipe Pipe)> _route = new(); // the shortest route and the piece each of its cells wants
    double _flowT;

    public int Level { get; }
    public (int X, int Y) Start { get; private set; }
    public Side StartOut { get; private set; }
    public (int X, int Y) End { get; private set; }
    public Side EndIn { get; private set; }
    public double Countdown { get; private set; }
    public double FlowStep { get; private set; }
    public Phase State { get; private set; } = Phase.Building;
    public int Score { get; private set; }
    public int Placed { get; private set; }
    public int Flowed { get; private set; }

    /// <summary>Where the build is: the cell it is in (or the commit before it has left), the side it came in by, and how far across it (0–1).</summary>
    public (int X, int Y) Head { get; private set; }
    public Side HeadFrom { get; private set; }
    public double HeadProgress => State == Phase.Flowing ? Math.Clamp(_flowT / FlowStep, 0, 1) : State == Phase.Deployed ? 1 : 0;

    /// <summary>The cells the build has flowed through, in order, with the side it came in by.</summary>
    public List<(int X, int Y, Side From)> Path { get; } = new();

    public IReadOnlyList<(Pipe Pipe, Badge Badge)> Queue => _queue;

    public Pipe PipeAt(int x, int y) => _pipes[x, y];
    public Badge BadgeAt(int x, int y) => _badges[x, y];
    public bool Blocked(int x, int y) => _blocked[x, y];
    public bool IsStart(int x, int y) => (x, y) == Start;
    public bool IsEnd(int x, int y) => (x, y) == End;

    /// <summary>Whether a piece can go on a cell: on the grid, open, not the commit or the deploy, and not flowed through yet.</summary>
    public bool CanPlace(int x, int y) =>
        State == Phase.Building || State == Phase.Flowing
            ? In(x, y) && !_blocked[x, y] && !IsStart(x, y) && !IsEnd(x, y) && _passes[x, y] == 0 && (x, y) != Head
            : false;

    /// <summary>The countdown for a level: long at first, never under eight seconds.</summary>
    public static double CountdownFor(int level) => Math.Max(10, 26 - 1.2 * level);

    /// <summary>Seconds per cell once the build flows.</summary>
    public static double FlowStepFor(int level) => Math.Max(0.9, 2.4 - 0.1 * level);

    /// <summary>Blocked cells in a level.</summary>
    public static int ObstaclesFor(int level) => Math.Min(14, 1 + level);

    public PipelineRules(int level, int seed)
    {
        Level = Math.Max(1, level);
        _rng = new Random(seed);
        Countdown = CountdownFor(Level);
        FlowStep = FlowStepFor(Level);
        Generate();
        var route = ShortestPath()!;
        for (int i = 0; i < route.Count; i++)
        {
            var prev = i == 0 ? Start : route[i - 1];
            var next = i == route.Count - 1 ? End : route[i + 1];
            _route.Add((route[i].X, route[i].Y, Joining(Towards(route[i], prev), Towards(route[i], next))));
        }
        while (_queue.Count < QueueLength) _queue.Add(NextPiece());
        Head = Start;
        HeadFrom = Opposite(StartOut);
    }

    // ------------------------------------------------------------------ the level

    /// <summary>Commit on the left, deploy further right the higher the level, obstacles that never cut every path.</summary>
    void Generate()
    {
        for (int attempt = 0; ; attempt++)
        {
            Array.Clear(_blocked);
            int span = Math.Min(Width - 1, 4 + Level);
            int sy = _rng.Next(Height), ey = _rng.Next(Height);
            int sx = _rng.Next(0, Math.Max(1, Width - span)), ex = Math.Min(Width - 1, sx + span);
            Start = (sx, sy);
            End = (ex, ey);
            StartOut = sy == 0 ? Side.South : sy == Height - 1 ? Side.North : Side.East;
            if (sx == Width - 1 && StartOut == Side.East) StartOut = sy > 0 ? Side.North : Side.South;
            EndIn = ey == 0 ? Side.South : ey == Height - 1 ? Side.North : Side.West;
            var free = Enumerable.Range(0, Width * Height).Select(i => (X: i % Width, Y: i / Width))
                .Where(c => c != Start && c != End && c != Step(Start, StartOut) && c != Step(End, EndIn)).ToList();
            for (int i = 0; i < ObstaclesFor(Level) && free.Count > 0; i++)
            {
                int k = _rng.Next(free.Count);
                _blocked[free[k].X, free[k].Y] = true;
                free.RemoveAt(k);
            }
            if (In(Step(Start, StartOut)) && In(Step(End, EndIn)) && ShortestPath() is { } path && path.Count >= 3) return;
            if (attempt > 200) throw new InvalidOperationException("no solvable level");
        }
    }

    /// <summary>
    /// The shortest run of open cells from the cell the commit feeds to the cell that feeds the deploy (both included),
    /// or null when the obstacles cut it off.
    /// </summary>
    public List<(int X, int Y)>? ShortestPath()
    {
        var from = Step(Start, StartOut);
        var to = Step(End, EndIn);
        var prev = new Dictionary<(int, int), (int, int)>();
        var seen = new HashSet<(int, int)> { from };
        var queue = new Queue<(int X, int Y)>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            if (c == to)
            {
                var path = new List<(int X, int Y)> { c };
                while (path[^1] != from) path.Add(prev[path[^1]]);
                path.Reverse();
                return path;
            }
            foreach (Side s in Enum.GetValues<Side>())
            {
                var n = Step(c, s);
                if (!In(n) || _blocked[n.X, n.Y] || n == Start || n == End || !seen.Add(n)) continue;
                prev[n] = c;
                queue.Enqueue(n);
            }
        }
        return null;
    }

    /// <summary>
    /// The next piece for the queue. A fair dealer: four times in ten it deals a piece the shortest route still lacks (so a
    /// level never waits forever on one corner); otherwise straights and corners mostly, a cross now and then. One straight
    /// in four carries a test or a review.
    /// </summary>
    (Pipe, Badge) NextPiece()
    {
        var lacking = _route.Where(c => _pipes[c.X, c.Y] != c.Pipe && !(c.Pipe is Pipe.Horizontal or Pipe.Vertical && _pipes[c.X, c.Y] == Pipe.Cross)).ToList();
        double r = _rng.NextDouble();
        var pipe = lacking.Count > 0 && _rng.NextDouble() < 0.4 ? lacking[_rng.Next(lacking.Count)].Pipe
            : r < 0.17 ? Pipe.Horizontal : r < 0.34 ? Pipe.Vertical : r < 0.9 ? (Pipe)(3 + _rng.Next(4)) : Pipe.Cross;
        var badge = pipe is Pipe.Horizontal or Pipe.Vertical && _rng.NextDouble() < 0.25 ? (_rng.Next(2) == 0 ? Badge.Test : Badge.Review) : Badge.None;
        return (pipe, badge);
    }

    // ------------------------------------------------------------------ geometry

    public static bool In(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

    static bool In((int X, int Y) c) => In(c.X, c.Y);

    public static (int X, int Y) Step((int X, int Y) c, Side s) => s switch
    {
        Side.North => (c.X, c.Y - 1), Side.South => (c.X, c.Y + 1), Side.East => (c.X + 1, c.Y), _ => (c.X - 1, c.Y),
    };

    public static Side Opposite(Side s) => (Side)(((int)s + 2) % 4);

    /// <summary>The side of cell <paramref name="from"/> that faces its neighbour <paramref name="to"/>.</summary>
    public static Side Towards((int X, int Y) from, (int X, int Y) to) =>
        to.X > from.X ? Side.East : to.X < from.X ? Side.West : to.Y > from.Y ? Side.South : Side.North;

    /// <summary>The sides a piece is open on.</summary>
    public static Side[] Openings(Pipe p) => p switch
    {
        Pipe.Horizontal => new[] { Side.East, Side.West },
        Pipe.Vertical => new[] { Side.North, Side.South },
        Pipe.NorthEast => new[] { Side.North, Side.East },
        Pipe.NorthWest => new[] { Side.North, Side.West },
        Pipe.SouthEast => new[] { Side.South, Side.East },
        Pipe.SouthWest => new[] { Side.South, Side.West },
        Pipe.Cross => new[] { Side.North, Side.East, Side.South, Side.West },
        _ => Array.Empty<Side>(),
    };

    /// <summary>The side the build leaves by after coming in by <paramref name="from"/>, or null when the piece is closed there.</summary>
    public static Side? Exit(Pipe p, Side from)
    {
        if (p == Pipe.Cross) return Opposite(from);
        var open = Openings(p);
        return open.Contains(from) ? open.First(s => s != from) : null;
    }

    /// <summary>The piece that joins two sides (a straight or a corner).</summary>
    public static Pipe Joining(Side a, Side b)
    {
        foreach (var p in new[] { Pipe.Horizontal, Pipe.Vertical, Pipe.NorthEast, Pipe.NorthWest, Pipe.SouthEast, Pipe.SouthWest })
            if (Openings(p).Contains(a) && Openings(p).Contains(b)) return p;
        return Pipe.None;
    }

    // ------------------------------------------------------------------ the player's side

    /// <summary>Lays the front piece of the queue on a cell; replacing a piece costs <see cref="ReplaceCost"/> seconds.</summary>
    public Event? Place(int x, int y)
    {
        if (!CanPlace(x, y)) return null;
        bool replacing = _pipes[x, y] != Pipe.None;
        var (pipe, badge) = _queue[0];
        _queue.RemoveAt(0);
        _queue.Add(NextPiece());
        _pipes[x, y] = pipe;
        _badges[x, y] = badge;
        Placed++;
        if (replacing)
        {
            if (State == Phase.Building) Countdown = Math.Max(0, Countdown - ReplaceCost);
            else _flowT += FlowStep * 0.5; // the flow gets there sooner
        }
        return new Event(replacing ? "replaced" : "placed", x, y);
    }

    /// <summary>"Deploy now": the countdown ends, and the build rushes through at <see cref="HurryStep"/> seconds a cell.</summary>
    public void Hurry()
    {
        if (State is Phase.Deployed or Phase.Leaked) return;
        Countdown = 0;
        FlowStep = HurryStep;
    }

    // ------------------------------------------------------------------ time

    public List<Event> Step(double dt)
    {
        var events = new List<Event>();
        if (State == Phase.Building)
        {
            Countdown -= dt;
            if (Countdown > 0) return events;
            dt = -Countdown;
            Countdown = 0;
            State = Phase.Flowing;
            _flowT = 0;
            events.Add(new Event("flow", Start.X, Start.Y));
        }
        if (State != Phase.Flowing) return events;
        _flowT += dt;
        while (State == Phase.Flowing && _flowT >= FlowStep)
        {
            _flowT -= FlowStep;
            Advance(events);
        }
        return events;
    }

    /// <summary>The build moves into the next cell: through a piece open to it, into the deploy, or out into a leak.</summary>
    void Advance(List<Event> events)
    {
        Side outSide = Head == Start ? StartOut : Exit(_pipes[Head.X, Head.Y], HeadFrom) ?? StartOut;
        var next = Step(Head, outSide);
        var from = Opposite(outSide);
        if (next == End)
        {
            if (from != EndIn)
            {
                Leak(events, next);
                return;
            }
            int bonus = DeployPoints * Level;
            Score += bonus;
            Head = next;
            HeadFrom = from;
            State = Phase.Deployed;
            events.Add(new Event("deploy", next.X, next.Y, bonus));
            return;
        }
        if (!In(next) || _blocked[next.X, next.Y] || next == Start || Exit(_pipes[next.X, next.Y], from) is null)
        {
            Leak(events, next);
            return;
        }
        _passes[next.X, next.Y]++;
        int points = _passes[next.X, next.Y] > 1 ? CrossPoints : PipePoints + (_badges[next.X, next.Y] != Badge.None ? BadgePoints : 0);
        Score += points;
        Flowed++;
        Head = next;
        HeadFrom = from;
        Path.Add((next.X, next.Y, from));
        events.Add(new Event("enter", next.X, next.Y, points));
    }

    void Leak(List<Event> events, (int X, int Y) at)
    {
        State = Phase.Leaked;
        events.Add(new Event("leak", at.X, at.Y));
    }

    public bool Over => State is Phase.Deployed or Phase.Leaked;
}
