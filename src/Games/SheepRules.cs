using System;
using System.Collections.Generic;
using System.Linq;

namespace DeskArcade.Games;

/// <summary>
/// The simulation behind Sheep Herding, free of UI. A flock of sheep stands about on window tops and the floor (the
/// top of the taskbar). Left alone they graze and amble, keeping loosely together and out of each other's way. The
/// dog (the cursor) frightens the sheep near it into running away from it, and a scared sheep scares its neighbours,
/// so the flock moves as one; a bark (<see cref="TryBark"/>) sends everything close by running harder. Scared sheep
/// hop off the end of a window top rather than turn back, and jump up onto a low window top ahead of them; calm
/// ones turn round at the edge. A pen with a gate stands on the floor: a sheep that walks through the gate, or drops
/// into the pen from above, is <see cref="State.Penned"/> and stays in. Its closed side is a fence. Now and then a
/// calm sheep bolts away from the pen of its own accord (<see cref="Strays"/>). A round is over when the whole flock
/// is in or the clock runs out; <see cref="Score"/> is ten a sheep plus five for every second left once all are in.
/// Coordinates are screen DIPs with y growing downwards; a sheep's position is its feet.
/// </summary>
public sealed class SheepRules
{
    public const double WalkSpeed = 22, FleeSpeed = 150, BarkSpeed = 215, Gravity = 1500;
    public const double FearRadius = 170, BarkRadius = 190, BarkCooldown = 1.1, PanicSeconds = 1.3, BoltSeconds = 1.2;
    public const double FlockRadius = 240, Personal = 24, Contagion = 90, JumpUp = 90, JumpAhead = 26, StepUp = 6, StepDown = 4;
    public const double WallGap = 12, PenInset = 10, FenceGap = 14, BodyLift = 12;
    public const int PerSheep = 10, PerSecond = 5, MaxFlock = 8;

    public enum State { Grazing, Walking, Fleeing, Airborne, Penned }

    /// <summary>What happened to a sheep during a step, for the sounds and the sparkle.</summary>
    public enum Event { Penned, Hopped, Jumped, Landed, Bolted }

    /// <summary>A walkable top edge: a window top (with its window's id) or the floor.</summary>
    public readonly record struct Ledge(double Y, double X1, double X2, IntPtr Window = default);

    public sealed class Sheep
    {
        public int Id;
        public double X, Y, Vx, Vy, Dir = 1;
        public State State;
        /// <summary>The window it stands on (it rides along when that moves), or zero.</summary>
        public IntPtr On;
        /// <summary>Seconds of running left after a bark or a bolt, and which way.</summary>
        public double Panic, PanicDir;
        /// <summary>How frightened of the dog it was on the last step, 0 to 1.</summary>
        public double Fear;
        /// <summary>Seconds left grazing or ambling before it changes its mind; seconds before it may jump again.</summary>
        public double Mind, JumpWait;
        /// <summary>A calm sheep is out for a little walk rather than grazing.</summary>
        public bool Ambling;
        /// <summary>Seconds since it last changed state, for animation.</summary>
        public double Age;
        public bool Free => State != State.Penned;
        public bool Scared => Panic > 0 || Fear > 0;
    }

    readonly List<Sheep> _flock = new();
    readonly Random _rng;
    double _barkWait, _boltT;

    public double Left { get; }
    public double Right { get; }
    public double Floor { get; }
    public double PenX1 { get; }
    public double PenX2 { get; }
    /// <summary>The gate is in the pen's left side (else its right).</summary>
    public bool GateLeft { get; }
    public double Seconds { get; }
    public double TimeLeft { get; private set; }
    /// <summary>The round is on: the clock runs, the dog and strays count and calm sheep wander. Off, they stand still.</summary>
    public bool Running { get; set; }
    /// <summary>Calm sheep now and then bolt away from the pen.</summary>
    public bool Strays { get; set; } = true;

    public IReadOnlyList<Sheep> Flock => _flock;
    public int Total => _flock.Count;
    public int Penned => _flock.Count(s => s.State == State.Penned);
    public bool AllIn => _flock.Count > 0 && _flock.All(s => s.State == State.Penned);
    public bool Over => AllIn || TimeLeft <= 0;
    public int Score => RoundScore(Penned, AllIn, TimeLeft);
    public bool BarkReady => _barkWait <= 0;
    /// <summary>Anything still moving: a sheep in the air or on its feet with somewhere to go.</summary>
    public bool Moving => _flock.Any(s => s.State == State.Airborne || Math.Abs(s.Vx) > 0.5);
    public double PenMid => (PenX1 + PenX2) / 2;

    public SheepRules(double left, double right, double floor, double penX1, double penX2, bool gateLeft, double seconds, Random rng)
    {
        Left = left;
        Right = right;
        Floor = floor;
        PenX1 = penX1;
        PenX2 = penX2;
        GateLeft = gateLeft;
        Seconds = TimeLeft = seconds;
        _rng = rng;
        _boltT = 8 + rng.NextDouble() * 5;
    }

    // ------------------------------------------------------------------ rounds and scoring

    /// <summary>Round 1 has five sheep and every round one more, up to eight.</summary>
    public static int FlockSize(int round) => Math.Clamp(4 + round, 5, MaxFlock);

    /// <summary>Ninety seconds on the clock for round 1 and seven fewer every round after, down to forty-five.</summary>
    public static double RoundSeconds(int round) => Math.Max(45, 90 - 7 * (Math.Max(1, round) - 1));

    /// <summary>Ten a sheep in the pen, and once the whole flock is in, five for every second left (a started second counts).</summary>
    public static int RoundScore(int penned, bool allIn, double timeLeft) =>
        penned * PerSheep + (allIn ? (int)Math.Ceiling(Math.Max(0, timeLeft)) * PerSecond : 0);

    /// <summary>The pen: wide enough for the flock to stand in, packed a little.</summary>
    public static double PenWidth(int sheep) => 70 + sheep * 18;

    public bool InPen(double x) => x > PenX1 + PenInset && x < PenX2 - PenInset;

    // ------------------------------------------------------------------ the flock

    public Sheep Add(double x, double y, IntPtr on = default)
    {
        var s = new Sheep
        {
            Id = _flock.Count, X = Math.Clamp(x, Left + WallGap, Right - WallGap), Y = y, On = on,
            Dir = _rng.NextDouble() < 0.5 ? -1 : 1, Mind = _rng.NextDouble() * 2,
        };
        _flock.Add(s);
        return s;
    }

    /// <summary>
    /// A bark at (<paramref name="x"/>, <paramref name="y"/>): every free sheep within <see cref="BarkRadius"/> runs away
    /// from it for a while, faster than from the dog alone. False while the dog gets its breath back.
    /// </summary>
    public bool TryBark(double x, double y, out int scared)
    {
        scared = 0;
        if (_barkWait > 0) return false;
        _barkWait = BarkCooldown;
        foreach (var s in _flock)
        {
            if (!s.Free) continue;
            double d = Dist(s, x, y);
            if (d > BarkRadius) continue;
            s.Panic = PanicSeconds * (1 - 0.4 * d / BarkRadius);
            s.PanicDir = Away(s, x);
            scared++;
        }
        return true;
    }

    /// <summary>Lets the sheep ride the window they stand on when it moves.</summary>
    public void Carry(IntPtr window, double dx, double dy)
    {
        foreach (var s in _flock)
            if (s.On == window && s.State != State.Airborne && s.Free)
            {
                s.X = Math.Clamp(s.X + dx, Left + WallGap, Right - WallGap);
                s.Y += dy;
            }
    }

    /// <summary>
    /// Advances the flock by <paramref name="dt"/> seconds with the dog at <paramref name="dog"/> (null: no dog about)
    /// over <paramref name="windows"/> (window tops this frame) and the floor. Returns what happened to whom.
    /// </summary>
    public List<(Sheep Sheep, Event Event)> Step(double dt, (double X, double Y)? dog, IReadOnlyList<Ledge> windows)
    {
        var events = new List<(Sheep, Event)>();
        var ledges = new List<Ledge>(windows.Count + 1);
        ledges.AddRange(windows);
        ledges.Add(new Ledge(Floor, Left, Right));
        bool live = Running && !Over;
        if (live) TimeLeft = Math.Max(0, TimeLeft - dt);
        _barkWait -= dt;
        if (live && Strays && (_boltT -= dt) <= 0) Bolt(events);

        foreach (var s in _flock)
        {
            var before = s.State;
            s.JumpWait -= dt;
            s.Panic = Math.Max(0, s.Panic - dt);
            if (s.State == State.Penned) Mill(s, dt, live);
            else if (s.State == State.Airborne) Fly(s, dt, ledges, events);
            else Walk(s, dt, live ? dog : null, live, ledges, events);
            s.Age = s.State == before ? s.Age + dt : 0;
        }
        return events;
    }

    /// <summary>A calm sheep out in the open takes fright at nothing and runs away from the pen.</summary>
    void Bolt(List<(Sheep, Event)> events)
    {
        _boltT = 9 + _rng.NextDouble() * 6;
        var calm = _flock.Where(s => s.Free && s.State != State.Airborne && !s.Scared).ToList();
        if (calm.Count == 0) return;
        var s = calm[_rng.Next(calm.Count)];
        s.Panic = BoltSeconds;
        s.PanicDir = s.X < PenMid ? -1 : 1;
        if (s.X - Left < 60) s.PanicDir = 1; // nowhere to go that way
        else if (Right - s.X < 60) s.PanicDir = -1;
        events.Add((s, Event.Bolted));
    }

    void Walk(Sheep s, double dt, (double X, double Y)? dog, bool live, List<Ledge> ledges, List<(Sheep, Event)> events)
    {
        if (Ground(s.X, s.Y, ledges) is not { } ground)
        {
            s.State = State.Airborne; // the window under it moved away or closed
            s.Vy = 0;
            s.On = default;
            return;
        }
        s.Y = ground.Y;
        s.On = ground.Window;

        // how badly it wants to run, and which way
        double want = 0;
        s.Fear = 0;
        if (dog is { } d)
        {
            double dist = Dist(s, d.X, d.Y);
            if (dist < FearRadius)
            {
                s.Fear = 1 - dist / FearRadius;
                want = Away(s, d.X) * FleeSpeed * (0.35 + 0.65 * s.Fear);
            }
        }
        if (!live) s.Panic = 0;
        if (s.Panic > 0 && BarkSpeed > Math.Abs(want)) want = s.PanicDir * BarkSpeed;
        bool scared = want != 0;
        if (!scared && live)
        {
            // a neighbour running for it sets this one off too, a little slower
            foreach (var o in _flock)
                if (o != s && o.Free && o.State != State.Airborne && SameLevel(s, o) && Math.Abs(o.X - s.X) < Contagion &&
                    Math.Abs(o.Vx) > WalkSpeed * 2 && Math.Abs(o.Vx) * 0.7 > Math.Abs(want))
                    want = o.Vx * 0.7;
            scared = want != 0;
        }
        if (!scared) want = live ? Wander(s, dt) : 0;
        if (live) want += Spacing(s);

        s.Vx += (want - s.Vx) * Math.Min(1, dt * (scared ? 7 : 3));
        if (Math.Abs(s.Vx) > 1) s.Dir = Math.Sign(s.Vx);
        s.State = scared && Math.Abs(want) > WalkSpeed * 1.5 ? State.Fleeing : Math.Abs(s.Vx) > 3 ? State.Walking : State.Grazing;

        if (scared && s.JumpWait <= 0 && TryJump(s, ledges))
        {
            events.Add((s, Event.Jumped));
            return;
        }

        double nx = s.X + s.Vx * dt;
        if (nx < Left + WallGap || nx > Right - WallGap)
        {
            nx = Math.Clamp(nx, Left + WallGap, Right - WallGap);
            s.Vx = 0;
            if (!scared) s.Dir = -s.Dir;
        }
        bool onFloor = ground.Window == IntPtr.Zero && Math.Abs(ground.Y - Floor) < 1;
        if (onFloor && Fence(s, ref nx))
        {
            s.X = nx;
            s.State = State.Penned;
            s.Vx = 0;
            events.Add((s, Event.Penned));
            return;
        }
        if (Ground(nx, s.Y, ledges) is { } next)
        {
            s.X = nx;
            s.Y = next.Y;
            s.On = next.Window;
            return;
        }
        // the end of a window top: a scared sheep goes over it, a calm one turns round
        if (scared)
        {
            s.X = nx;
            s.State = State.Airborne;
            s.Vy = -70;
            s.On = default;
            events.Add((s, Event.Hopped));
        }
        else
        {
            s.Vx = 0;
            s.Dir = -s.Dir;
            s.Mind = 0.5 + _rng.NextDouble();
        }
    }

    /// <summary>
    /// The pen's fence for a sheep walking the floor to <paramref name="nx"/>: through the gate it is penned (true); into
    /// the closed side it is stopped short.
    /// </summary>
    bool Fence(Sheep s, ref double nx)
    {
        if (InPen(s.X)) return false;
        bool gateSide = GateLeft ? s.X < PenMid : s.X > PenMid;
        if (gateSide) return InPen(nx);
        // outside the closed side: it comes no closer than FenceGap (or than it already is)
        double stop = GateLeft ? Math.Min(s.X, PenX2 + FenceGap) : Math.Max(s.X, PenX1 - FenceGap);
        if (GateLeft ? nx < stop : nx > stop)
        {
            nx = stop;
            s.Vx = 0;
        }
        return false;
    }

    /// <summary>Grazing and ambling: stands a few seconds, walks a little (usually toward the others), stands again.</summary>
    double Wander(Sheep s, double dt)
    {
        if ((s.Mind -= dt) <= 0)
        {
            s.Ambling = !s.Ambling && _rng.NextDouble() < 0.6;
            s.Mind = s.Ambling ? 1 + _rng.NextDouble() * 2 : 1.5 + _rng.NextDouble() * 2.5;
            if (s.Ambling)
            {
                double pull = FlockPull(s);
                s.Dir = Math.Abs(pull) > 30 && _rng.NextDouble() < 0.75 ? Math.Sign(pull) : _rng.NextDouble() < 0.5 ? -1 : 1;
            }
        }
        return s.Ambling ? s.Dir * WalkSpeed : 0;
    }

    /// <summary>How far the middle of the free sheep nearby (on the same level) is from this one.</summary>
    double FlockPull(Sheep s)
    {
        double sum = 0;
        int n = 0;
        foreach (var o in _flock)
            if (o != s && o.Free && SameLevel(s, o) && Math.Abs(o.X - s.X) < FlockRadius)
            {
                sum += o.X;
                n++;
            }
        return n == 0 ? 0 : sum / n - s.X;
    }

    /// <summary>A nudge away from any sheep closer than <see cref="Personal"/>.</summary>
    double Spacing(Sheep s)
    {
        double push = 0;
        foreach (var o in _flock)
        {
            if (o == s || !o.Free || !SameLevel(s, o)) continue;
            double dx = s.X - o.X;
            if (Math.Abs(dx) >= Personal) continue;
            push += (dx == 0 ? (s.Id < o.Id ? -1 : 1) : Math.Sign(dx)) * 20 * (1 - Math.Abs(dx) / Personal);
        }
        return push;
    }

    /// <summary>A frightened sheep jumps up onto a low window top just ahead of it.</summary>
    bool TryJump(Sheep s, List<Ledge> ledges)
    {
        double dir = Math.Sign(s.Vx) is var d && d != 0 ? d : s.Dir;
        double ahead = s.X + dir * JumpAhead;
        Ledge? best = null;
        foreach (var l in ledges)
            if (l.Y < s.Y - 14 && l.Y >= s.Y - JumpUp && ahead >= l.X1 + 6 && ahead <= l.X2 - 6 && (best is null || l.Y > best.Value.Y))
                best = l;
        if (best is not { } top) return false;
        s.State = State.Airborne;
        s.Vy = -Math.Sqrt(2 * Gravity * (s.Y - top.Y + 16));
        s.Vx = dir * 60;
        s.On = default;
        s.JumpWait = 1.5;
        return true;
    }

    void Fly(Sheep s, double dt, List<Ledge> ledges, List<(Sheep, Event)> events)
    {
        double ny = s.Y + (s.Vy += Gravity * dt) * dt;
        s.X = Math.Clamp(s.X + s.Vx * dt, Left + WallGap, Right - WallGap);
        if (s.Vy > 0)
        {
            // one-way edges: land on the first one crossed on the way down
            Ledge? hit = null;
            foreach (var l in ledges)
                if (s.X >= l.X1 && s.X <= l.X2 && s.Y <= l.Y + 1 && ny >= l.Y && (hit is null || l.Y < hit.Value.Y))
                    hit = l;
            if (hit is { } h)
            {
                s.Y = h.Y;
                s.Vy = 0;
                s.Vx *= 0.5;
                s.On = h.Window;
                bool intoPen = h.Window == IntPtr.Zero && InPen(s.X);
                s.State = intoPen ? State.Penned : s.Scared ? State.Fleeing : State.Walking;
                events.Add((s, intoPen ? Event.Penned : Event.Landed));
                return;
            }
        }
        s.Y = Math.Min(ny, Floor);
    }

    /// <summary>A penned sheep shuffles about inside the fence and pays the dog no mind.</summary>
    void Mill(Sheep s, double dt, bool live)
    {
        s.Y = Floor;
        s.On = default;
        s.Fear = s.Panic = 0;
        double want = 0;
        if (live || Math.Abs(s.Vx) > 0.5)
        {
            if ((s.Mind -= dt) <= 0)
            {
                s.Mind = 1 + _rng.NextDouble() * 3;
                s.Dir = _rng.NextDouble() < 0.5 ? -1 : 1;
                s.Vx = _rng.NextDouble() < 0.4 ? s.Dir * WalkSpeed * 0.6 : 0;
            }
            want = live ? s.Vx : 0;
        }
        s.Vx = want;
        double lo = PenX1 + PenInset + 2, hi = PenX2 - PenInset - 2;
        s.X = Math.Clamp(s.X + s.Vx * dt, lo, hi);
        if (s.X <= lo || s.X >= hi) s.Vx = 0;
    }

    /// <summary>What a sheep at (<paramref name="x"/>, <paramref name="y"/>) stands on: the highest edge a step up or down.</summary>
    static Ledge? Ground(double x, double y, List<Ledge> ledges)
    {
        Ledge? best = null;
        foreach (var l in ledges)
            if (x >= l.X1 && x <= l.X2 && l.Y >= y - StepUp && l.Y <= y + StepDown && (best is null || l.Y < best.Value.Y))
                best = l;
        return best;
    }

    static bool SameLevel(Sheep a, Sheep b) => Math.Abs(a.Y - b.Y) < 30;

    /// <summary>From the middle of the sheep's body to a point.</summary>
    static double Dist(Sheep s, double x, double y)
    {
        double dx = s.X - x, dy = s.Y - BodyLift - y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>The way away from <paramref name="x"/>; straight above or below, the way it faces.</summary>
    static double Away(Sheep s, double x) => Math.Abs(s.X - x) < 2 ? s.Dir : Math.Sign(s.X - x);
}
