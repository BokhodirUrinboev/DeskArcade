using System;
using System.Collections.Generic;
using System.Linq;

namespace DeskArcade.Games;

/// <summary>
/// Load Balancer, free of UI. Requests fall from the top of the screen into the load balancer's queue; the player hands
/// each one to a server before it times out (<see cref="Request.Ttl"/> seconds after it appeared). A server works on as
/// many requests as it has <see cref="Slots"/> (a heavy one takes two, works twice as long and heats twice as much). It
/// heats up with its load and cools down when idle; a hot one (<see cref="HotAt"/>) works at <see cref="HotSpeed"/>, and
/// one that reaches full heat goes down for <see cref="DownSeconds"/>, handing its requests back to the front of the queue.
/// Requests come in waves that grow; from the second wave each has a traffic spike halfway through. A request that times
/// out, or finds the queue full, costs one of <see cref="Lives"/> lives, and the round is over when they run out. The
/// budget racks extra servers on the taskbar (<see cref="Rack"/>), one per slot, with a cooldown between them; each wave
/// cleared adds one to the budget. Served requests score (heavy ones more, and a bonus when the server has just served
/// the same kind, its cache still warm), and so does every wave cleared. Time only passes in <see cref="Step"/>.
/// </summary>
public sealed class LoadBalancerRules
{
    public const int Lives = 5, QueueMax = 8, Slots = 3, RackSlots = 3, MaxBudget = 3, StartBudget = 1, Kinds = 4;
    public const double HeatPerSlot = 0.045, CoolBusy = 0.03, CoolIdle = 0.12, HotAt = 0.7, HotSpeed = 0.55, DownSeconds = 4;
    public const double WorkSeconds = 2.2, HeavyWorkSeconds = 4.5, WaveBreak = 3, RackCooldown = 12, SpikeGap = 0.3;
    public const int Points = 10, HeavyPoints = 25, CacheBonus = 5, WaveBonus = 20;

    public enum State { Falling, Queued, Serving, Served, Dropped }

    public sealed class Request
    {
        public int Id { get; init; }
        /// <summary>Its colour, 0 to <see cref="Kinds"/> − 1 (GET, POST, PUT, DELETE on screen).</summary>
        public int Kind { get; init; }
        public bool Heavy { get; init; }
        public double Ttl { get; init; }
        public int Wave { get; init; }
        /// <summary>Where across the screen it appeared, 0 (left) to 1 (right).</summary>
        public double Lane { get; init; }
        public double Age { get; internal set; }
        /// <summary>Seconds left before it lands in the queue, while it falls.</summary>
        public double FallLeft { get; internal set; }
        public double FallTotal { get; internal set; }
        public State State { get; internal set; }
        /// <summary>Held in the player's hand: it stops falling, but the clock runs on.</summary>
        public bool Held { get; set; }
        public Server? Server { get; internal set; }
        /// <summary>Work left, in seconds at full speed, while a server has it.</summary>
        public double Work { get; internal set; }
        public int Size => Heavy ? 2 : 1;
        public double Left => Math.Max(0, Ttl - Age);
        public bool Waiting => State is State.Falling or State.Queued;
        public double WorkTotal => Heavy ? HeavyWorkSeconds : WorkSeconds;
    }

    public sealed class Server
    {
        public int Id { get; init; }
        /// <summary>The taskbar rack slot it was racked in, or −1 for a server standing on a window.</summary>
        public int RackSlot { get; init; } = -1;
        public double Heat { get; internal set; }
        public double DownFor { get; internal set; }
        public List<Request> Jobs { get; } = new();
        /// <summary>The kind it served last (−1: none yet): the same kind next is a cache hit.</summary>
        public int LastKind { get; internal set; } = -1;
        public int Used => Jobs.Sum(j => j.Size);
        public int Free => Slots - Used;
        public bool Down => DownFor > 0;
        public bool Hot => !Down && Heat >= HotAt;
        public double Speed => Down ? 0 : Hot ? HotSpeed : 1;
        public bool Racked => RackSlot >= 0;
    }

    /// <summary>
    /// What a step did, for the drawing and the sounds: "spawn", "land", "timeout", "overflow", "served" (with its
    /// <see cref="Points"/> and whether it was a <see cref="Cache"/> hit), "hot", "down", "up", "wave", "spike",
    /// "cleared" (with the bonus, and <see cref="Clean"/> when nothing was dropped and no server went down) and "over".
    /// </summary>
    public readonly record struct Event(string Kind, Request? Request = null, Server? Server = null, int Points = 0, bool Cache = false, bool Clean = false);

    readonly Random _rng;
    readonly bool _waves;
    readonly double _fallSeconds;
    readonly List<Server> _servers = new();
    readonly List<Request> _live = new();
    readonly List<Request> _queue = new();
    int _nextId, _spawned, _waveSize, _burst;
    double _spawnT, _breakLeft = WaveBreak;
    bool _inWave, _spiked, _waveClean;

    /// <param name="fallSeconds">How long a request takes to fall into the queue.</param>
    /// <param name="servers">Servers to start with (on windows; more are racked).</param>
    /// <param name="waves">False for a quiet table where only <see cref="Spawn"/> brings requests (tests).</param>
    public LoadBalancerRules(Random rng, double fallSeconds, int servers = 2, bool waves = true)
    {
        _rng = rng;
        _fallSeconds = fallSeconds;
        _waves = waves;
        for (int i = 0; i < servers; i++) AddServer();
        LivesLeft = Lives;
        Budget = StartBudget;
    }

    public IReadOnlyList<Server> Servers => _servers;

    /// <summary>The requests still in play: falling, queued or being served.</summary>
    public IReadOnlyList<Request> Live => _live;

    /// <summary>The queue, first come first.</summary>
    public IReadOnlyList<Request> Queue => _queue;

    public int Score { get; private set; }
    public int LivesLeft { get; private set; }
    public int Wave { get; private set; }
    public int Budget { get; private set; }
    public double RackCooldownLeft { get; private set; }
    public int Served { get; private set; }
    public int Dropped { get; private set; }
    public bool Over => LivesLeft <= 0;

    /// <summary>A wave is on (rather than the short break before the next).</summary>
    public bool InWave => _inWave;

    /// <summary>Seconds of break left before the next wave.</summary>
    public double BreakLeft => _inWave ? 0 : _breakLeft;

    // ------------------------------------------------------------------ waves

    /// <summary>Requests in wave <paramref name="wave"/>, the spike not counted.</summary>
    public static int WaveSize(int wave) => 6 + 3 * wave;

    /// <summary>Seconds between requests in a wave.</summary>
    public static double SpawnGap(int wave) => Math.Max(0.55, 2.1 - 0.18 * wave);

    /// <summary>The share of heavy requests in a wave.</summary>
    public static double HeavyShare(int wave) => Math.Min(0.35, 0.07 * (wave - 1));

    /// <summary>How long a request of that wave waits before it times out.</summary>
    public static double TimeoutFor(int wave) => Math.Max(8, 16 - 0.8 * wave);

    /// <summary>The traffic spike's extra requests, from the second wave on.</summary>
    public static int SpikeSize(int wave) => wave < 2 ? 0 : 3 + wave;

    // ------------------------------------------------------------------ the player's side

    /// <summary>A server standing on a window (the game places it).</summary>
    public Server AddServer()
    {
        var s = new Server { Id = _servers.Count };
        _servers.Add(s);
        return s;
    }

    public bool CanRack(int slot) => !Over && slot is >= 0 and < RackSlots && Budget > 0 && RackCooldownLeft <= 0 && _servers.All(s => s.RackSlot != slot);

    /// <summary>Racks a new server in taskbar slot <paramref name="slot"/>: it costs one from the budget and starts the cooldown.</summary>
    public Server? Rack(int slot)
    {
        if (!CanRack(slot)) return null;
        Budget--;
        RackCooldownLeft = RackCooldown;
        var s = new Server { Id = _servers.Count, RackSlot = slot };
        _servers.Add(s);
        return s;
    }

    /// <summary>The server is up and has the slots free for it, and the request is still waiting.</summary>
    public bool CanTake(Server s, Request r) => !Over && r.Waiting && !s.Down && s.Free >= r.Size && _servers.Contains(s);

    /// <summary>Hands a waiting request to a server.</summary>
    public bool Assign(Request r, Server s)
    {
        if (!CanTake(s, r)) return false;
        _queue.Remove(r);
        r.State = State.Serving;
        r.Held = false;
        r.Server = s;
        r.Work = r.WorkTotal;
        s.Jobs.Add(r);
        return true;
    }

    /// <summary>A request let go of in mid-air falls on from there, taking <paramref name="fallSeconds"/> to reach the queue.</summary>
    public void FallFrom(Request r, double fallSeconds)
    {
        if (r.State != State.Falling) return;
        r.Held = false;
        r.FallTotal = r.FallLeft = Math.Max(0.05, fallSeconds);
    }

    /// <summary>A new falling request (the waves spawn them; tests call it directly).</summary>
    public Request Spawn(int kind, bool heavy, double? ttl = null)
    {
        var r = new Request
        {
            Id = _nextId++, Kind = kind, Heavy = heavy, Ttl = ttl ?? TimeoutFor(Math.Max(1, Wave)), Wave = Wave,
            Lane = 0.08 + _rng.NextDouble() * 0.84, FallLeft = _fallSeconds, FallTotal = _fallSeconds,
        };
        _live.Add(r);
        return r;
    }

    // ------------------------------------------------------------------ time

    public List<Event> Step(double dt)
    {
        var events = new List<Event>();
        if (Over) return events;
        RackCooldownLeft = Math.Max(0, RackCooldownLeft - dt);
        if (_waves) Waves(dt, events);

        foreach (var r in _live.ToList())
        {
            if (!r.Waiting) continue;
            r.Age += dt;
            if (r.Age >= r.Ttl)
            {
                Drop(r, "timeout", events);
                continue;
            }
            if (r.State != State.Falling || r.Held) continue;
            r.FallLeft -= dt;
            if (r.FallLeft > 0) continue;
            if (_queue.Count >= QueueMax)
            {
                Drop(r, "overflow", events);
                continue;
            }
            r.State = State.Queued;
            _queue.Add(r);
            events.Add(new Event("land", r));
        }

        foreach (var s in _servers)
        {
            if (s.Down)
            {
                s.DownFor = Math.Max(0, s.DownFor - dt);
                s.Heat = Math.Max(0, s.Heat - CoolIdle * dt);
                if (!s.Down) events.Add(new Event("up", Server: s));
                continue;
            }
            foreach (var job in s.Jobs.ToList())
            {
                job.Work -= dt * s.Speed;
                if (job.Work > 0) continue;
                s.Jobs.Remove(job);
                bool cache = s.LastKind == job.Kind;
                s.LastKind = job.Kind;
                job.State = State.Served;
                job.Server = null;
                _live.Remove(job);
                int points = (job.Heavy ? HeavyPoints : Points) + (cache ? CacheBonus : 0);
                Score += points;
                Served++;
                events.Add(new Event("served", job, s, points, cache));
            }
            bool wasHot = s.Hot;
            int used = s.Used;
            s.Heat = Math.Clamp(s.Heat + (HeatPerSlot * used - (used > 0 ? CoolBusy : CoolIdle)) * dt, 0, 1);
            if (s.Heat >= 1) GoDown(s, events);
            else if (s.Hot && !wasHot) events.Add(new Event("hot", Server: s));
        }

        if (_waves && _inWave && _spawned >= _waveSize && _burst == 0 && _live.All(r => r.Wave != Wave))
        {
            _inWave = false;
            _breakLeft = WaveBreak;
            int bonus = WaveBonus * Wave;
            Score += bonus;
            Budget = Math.Min(MaxBudget, Budget + 1);
            events.Add(new Event("cleared", Points: bonus, Clean: _waveClean));
        }
        if (Over) events.Add(new Event("over"));
        return events;
    }

    void Waves(double dt, List<Event> events)
    {
        if (!_inWave)
        {
            if ((_breakLeft -= dt) > 0) return;
            Wave++;
            _inWave = true;
            _waveClean = true;
            _spawned = _burst = 0;
            _spiked = false;
            _waveSize = WaveSize(Wave);
            _spawnT = 0.4;
            events.Add(new Event("wave"));
        }
        if (!_spiked && SpikeSize(Wave) > 0 && _spawned >= _waveSize / 2)
        {
            _spiked = true;
            _burst = SpikeSize(Wave);
            events.Add(new Event("spike"));
        }
        _spawnT -= dt;
        while (_spawnT <= 0 && (_burst > 0 || _spawned < _waveSize))
        {
            if (_burst > 0)
            {
                _burst--;
                _spawnT += SpikeGap;
            }
            else
            {
                _spawned++;
                _spawnT += SpawnGap(Wave) * (0.75 + _rng.NextDouble() * 0.5);
            }
            var r = Spawn(_rng.Next(Kinds), _rng.NextDouble() < HeavyShare(Wave));
            events.Add(new Event("spawn", r));
        }
    }

    void Drop(Request r, string why, List<Event> events)
    {
        _queue.Remove(r);
        _live.Remove(r);
        r.State = State.Dropped;
        r.Held = false;
        Dropped++;
        _waveClean = false;
        LivesLeft = Math.Max(0, LivesLeft - 1);
        events.Add(new Event(why, r));
    }

    /// <summary>Full heat: the server goes down for a while, and its requests go back to the front of the queue (those that fit).</summary>
    void GoDown(Server s, List<Event> events)
    {
        s.Heat = 1;
        s.DownFor = DownSeconds;
        _waveClean = false;
        events.Add(new Event("down", Server: s));
        var back = s.Jobs.ToList();
        s.Jobs.Clear();
        int at = 0;
        foreach (var r in back)
        {
            r.Server = null;
            if (_queue.Count >= QueueMax)
            {
                Drop(r, "overflow", events);
                continue;
            }
            r.State = State.Queued;
            _queue.Insert(at++, r);
            events.Add(new Event("land", r));
        }
    }
}
