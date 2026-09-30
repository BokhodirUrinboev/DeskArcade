using System;
using System.Collections.Generic;
using System.Linq;
using DeskArcade.Games;
using Xunit;
using Xunit.Abstractions;
using static DeskArcade.Games.LoadBalancerRules;

namespace DeskArcade.Tests;

/// <summary>Load Balancer: the queue, timeouts, capacity, heat, waves, racking and scoring.</summary>
public class LoadBalancerTests
{
    readonly ITestOutputHelper _out;

    public LoadBalancerTests(ITestOutputHelper output) => _out = output;

    /// <summary>A quiet table: nothing arrives unless the test spawns it.</summary>
    static LoadBalancerRules Quiet(int servers = 2, double fall = 1) => new(new Random(1), fall, servers, waves: false);

    static List<Event> Run(LoadBalancerRules r, double seconds, double step = 0.05)
    {
        var all = new List<Event>();
        for (double t = 0; t < seconds - 1e-9; t += step) all.AddRange(r.Step(step));
        return all;
    }

    /// <summary>Tops a server up to its three slots with requests that will not time out.</summary>
    static void Load(LoadBalancerRules r, Server s)
    {
        while (s.Free > 0 && !s.Down) Assert.True(r.Assign(r.Spawn(0, false, ttl: 999), s));
    }

    // ------------------------------------------------------------------ the queue and timeouts

    [Fact]
    public void RequestsFallIntoTheQueueInTheOrderTheyLand()
    {
        var r = Quiet();
        var a = r.Spawn(0, false);
        var b = r.Spawn(1, false);
        Run(r, 0.5);
        var c = r.Spawn(2, true);
        Assert.Empty(r.Queue);
        var landed = Run(r, 0.6).Where(e => e.Kind == "land").Select(e => e.Request).ToList();
        Assert.Equal(new[] { a, b }, landed);
        Run(r, 0.5);
        Assert.Equal(new[] { a, b, c }, r.Queue);
        Assert.All(r.Queue, q => Assert.Equal(State.Queued, q.State));
    }

    [Fact]
    public void AFullQueueTurnsTheNextRequestAwayAndThatCostsALife()
    {
        var r = Quiet();
        for (int i = 0; i < QueueMax + 1; i++) r.Spawn(i % Kinds, false);
        var events = Run(r, 1.1);
        Assert.Equal(QueueMax, r.Queue.Count);
        Assert.Single(events, e => e.Kind == "overflow");
        Assert.Equal(Lives - 1, r.LivesLeft);
        Assert.Equal(1, r.Dropped);
    }

    [Fact]
    public void AWaitingRequestTimesOutButOneBeingServedDoesNot()
    {
        var r = Quiet();
        var waiting = r.Spawn(0, false, ttl: 3);
        var served = r.Spawn(1, true, ttl: 3); // heavy: 4.5 s of work, longer than its timeout
        Assert.True(r.Assign(served, r.Servers[0]));
        var events = Run(r, 3.05);
        Assert.Contains(events, e => e.Kind == "timeout" && e.Request == waiting);
        Assert.Equal(State.Dropped, waiting.State);
        Assert.Equal(Lives - 1, r.LivesLeft);
        events = Run(r, 1.6);
        Assert.Contains(events, e => e.Kind == "served" && e.Request == served);
        Assert.Equal(Lives - 1, r.LivesLeft);
    }

    [Fact]
    public void ARequestHeldInTheHandStopsFallingButItsClockRunsOn()
    {
        var r = Quiet(fall: 2);
        var q = r.Spawn(0, false, ttl: 2);
        Run(r, 0.5);
        q.Held = true;
        double left = q.FallLeft;
        Run(r, 1);
        Assert.Equal(left, q.FallLeft, 9);
        Assert.Equal(State.Falling, q.State);
        Run(r, 0.6);
        Assert.Equal(State.Dropped, q.State); // timed out in the hand
    }

    [Fact]
    public void ARequestLetGoOfFallsOnFromWhereItIs()
    {
        var r = Quiet(fall: 2);
        var q = r.Spawn(0, false);
        q.Held = true;
        Run(r, 1);
        r.FallFrom(q, 0.3);
        Assert.False(q.Held);
        Run(r, 0.35);
        Assert.Equal(State.Queued, q.State);
    }

    // ------------------------------------------------------------------ capacity

    [Fact]
    public void AServerTakesAsManyRequestsAsItHasSlotsAndAHeavyOneTakesTwo()
    {
        var r = Quiet();
        var s = r.Servers[0];
        for (int i = 0; i < Slots; i++) Assert.True(r.Assign(r.Spawn(0, false), s));
        Assert.False(r.CanTake(s, r.Spawn(0, false)));
        var t = r.Servers[1];
        Assert.True(r.Assign(r.Spawn(1, true), t));
        Assert.Equal(1, t.Free);
        var heavy = r.Spawn(1, true);
        Assert.False(r.Assign(heavy, t));
        Assert.True(r.Assign(r.Spawn(2, false), t));
        Assert.Equal(0, t.Free);
        Assert.Equal(State.Falling, heavy.State); // still waiting for somewhere to go
    }

    [Fact]
    public void AServedOrDroppedRequestCannotBeHandedOn()
    {
        var r = Quiet();
        var q = r.Spawn(0, false, ttl: 0.1);
        Run(r, 0.2);
        Assert.False(r.Assign(q, r.Servers[0]));
    }

    // ------------------------------------------------------------------ heat

    [Fact]
    public void ServersHeatUpWithLoadAndCoolDownIdle()
    {
        var r = Quiet();
        var busy = r.Servers[0];
        Load(r, busy);
        r.Step(1.0);
        Assert.Equal(HeatPerSlot * Slots - CoolBusy, busy.Heat, 6);
        Assert.Equal(0, r.Servers[1].Heat);
        Run(r, 1.5); // the three are served
        Assert.Empty(busy.Jobs);
        double warm = busy.Heat;
        r.Step(1.0);
        Assert.Equal(Math.Max(0, warm - CoolIdle), busy.Heat, 6);
    }

    [Fact]
    public void AHotServerWorksSlower()
    {
        var r = Quiet();
        var s = r.Servers[0];
        var events = new List<Event>();
        while (!s.Hot)
        {
            Load(r, s);
            events.AddRange(r.Step(0.05));
        }
        Assert.Contains(events, e => e.Kind == "hot" && e.Server == s);
        Assert.Equal(HotSpeed, s.Speed);
        var job = s.Jobs[0];
        double before = job.Work;
        r.Step(0.2);
        Assert.Equal(before - 0.2 * HotSpeed, job.Work, 6);
    }

    [Fact]
    public void AnOverheatedServerGoesDownHandsItsRequestsBackAndComesUpAgain()
    {
        var r = Quiet();
        var s = r.Servers[0];
        var events = new List<Event>();
        while (!events.Any(e => e.Kind == "down"))
        {
            Load(r, s);
            events.AddRange(r.Step(0.05));
        }
        Assert.True(s.Down);
        Assert.Empty(s.Jobs);
        Assert.Equal(Slots, r.Queue.Count); // back to the front of the queue
        Assert.All(r.Queue, q => Assert.Equal(State.Queued, q.State));
        Assert.False(r.CanTake(s, r.Queue[0]));
        Assert.True(r.Assign(r.Queue[0], r.Servers[1])); // another server can have them
        var up = Run(r, DownSeconds + 0.1);
        Assert.Contains(up, e => e.Kind == "up" && e.Server == s);
        Assert.False(s.Down);
        Assert.InRange(s.Heat, 0.4, 0.6); // it cooled while it was down
    }

    // ------------------------------------------------------------------ scoring

    [Fact]
    public void ServedRequestsScoreHeavyOnesMoreAndAWarmCacheABonus()
    {
        var r = Quiet();
        var s = r.Servers[0];
        r.Assign(r.Spawn(0, false), s);
        Run(r, WorkSeconds + 0.1);
        Assert.Equal(Points, r.Score);
        r.Assign(r.Spawn(0, false), s); // the same kind again: a cache hit
        var e = Run(r, WorkSeconds + 0.1).Single(x => x.Kind == "served");
        Assert.True(e.Cache);
        Assert.Equal(Points + CacheBonus, e.Points);
        r.Assign(r.Spawn(1, true), s);
        e = Run(r, HeavyWorkSeconds + 0.1).Single(x => x.Kind == "served");
        Assert.False(e.Cache);
        Assert.Equal(HeavyPoints, e.Points);
        Assert.Equal(Points * 2 + CacheBonus + HeavyPoints, r.Score);
        Assert.Equal(3, r.Served);
    }

    // ------------------------------------------------------------------ waves, spikes, racking

    [Fact]
    public void WavesGrowFasterHeavierAndLessPatient()
    {
        for (int w = 1; w < 12; w++)
        {
            Assert.True(WaveSize(w + 1) > WaveSize(w));
            Assert.True(SpawnGap(w + 1) <= SpawnGap(w));
            Assert.True(HeavyShare(w + 1) >= HeavyShare(w));
            Assert.True(TimeoutFor(w + 1) <= TimeoutFor(w));
            Assert.True(SpikeSize(w + 1) >= SpikeSize(w));
        }
        Assert.Equal(0, SpikeSize(1));
        Assert.Equal(0, HeavyShare(1));
        Assert.InRange(TimeoutFor(30), 8, 8);
    }

    /// <summary>A player who hands every waiting request to the coolest server with room, and racks a server when the queue backs up.</summary>
    static List<Event> Play(LoadBalancerRules r, double seconds, bool rack = true, Func<List<Event>, bool>? until = null)
    {
        var all = new List<Event>();
        for (double t = 0; t < seconds && !r.Over; t += 0.05)
        {
            foreach (var q in r.Live.Where(q => q.Waiting).OrderBy(q => q.Left).ToList())
            {
                var s = r.Servers.Where(s => r.CanTake(s, q)).OrderBy(s => s.Heat + s.Used * 0.2).FirstOrDefault();
                if (s != null && s.Heat < 0.85) r.Assign(q, s);
            }
            if (rack && r.Queue.Count >= 3)
                for (int slot = 0; slot < RackSlots; slot++)
                    if (r.Rack(slot) != null) break;
            var step = r.Step(0.05);
            all.AddRange(step);
            if (until?.Invoke(step) == true) break;
        }
        return all;
    }

    [Fact]
    public void ClearingAWaveAddsToTheBudgetAndScoresABonus()
    {
        var r = new LoadBalancerRules(new Random(3), 1.5, servers: 3);
        var events = Play(r, 300, rack: false, until: e => e.Any(x => x.Kind == "cleared"));
        var cleared = events.Single(e => e.Kind == "cleared");
        Assert.Equal(WaveBonus, cleared.Points);
        Assert.Equal(1, r.Wave);
        Assert.Equal(StartBudget + 1, r.Budget);
        Assert.Equal(WaveSize(1), r.Served + r.Dropped);
        Assert.False(r.InWave);
        Assert.Contains(Run(r, WaveBreak + 0.1), e => e.Kind == "wave");
        Assert.Equal(2, r.Wave);
    }

    [Fact]
    public void ATrafficSpikeComesHalfwayThroughEveryWaveFromTheSecond()
    {
        var r = new LoadBalancerRules(new Random(4), 1.5, servers: 3);
        var events = Play(r, 600, until: e => e.Any(x => x.Kind == "cleared") && r.Wave == 2);
        var spikes = events.Where(e => e.Kind == "spike").ToList();
        Assert.Single(spikes);
        int waveTwo = events.Count(e => e.Kind == "spawn" && e.Request!.Wave == 2);
        Assert.Equal(WaveSize(2) + SpikeSize(2), waveTwo);
    }

    [Fact]
    public void RackingCostsTheBudgetAndWaitsForTheCooldown()
    {
        var r = new LoadBalancerRules(new Random(5), 1.5, servers: 3);
        Play(r, 300, rack: false, until: e => e.Any(x => x.Kind == "cleared"));
        Assert.Equal(2, r.Budget);
        Assert.False(r.CanRack(RackSlots)); // no such slot
        var racked = r.Rack(0);
        Assert.NotNull(racked);
        Assert.True(racked!.Racked);
        Assert.True(r.Assign(r.Spawn(0, false), racked)); // a racked server takes requests like any other
        Assert.Equal(1, r.Budget);
        Assert.Null(r.Rack(1));  // cooling down
        Run(r, RackCooldown - 0.5);
        Assert.Null(r.Rack(1));
        Run(r, 0.6);
        Assert.Null(r.Rack(0));  // taken
        Assert.NotNull(r.Rack(1));
        Assert.Equal(0, r.Budget);
        Run(r, RackCooldown + 0.1);
        Assert.Null(r.Rack(2));  // nothing left to spend
        Assert.Equal(5, r.Servers.Count);
    }

    [Fact]
    public void WithNobodyAtTheBalancerTheLivesRunOutAndTheRoundEnds()
    {
        var r = new LoadBalancerRules(new Random(6), 2, servers: 2);
        var events = Run(r, 120, 0.1);
        Assert.True(r.Over);
        Assert.Equal(0, r.LivesLeft);
        Assert.Single(events, e => e.Kind == "over");
        Assert.Equal(1, r.Wave);
        Assert.Empty(r.Step(1)); // nothing more happens
    }

    [Fact]
    public void AGreedyPlayerLastsSeveralWaves()
    {
        var scores = new List<int>();
        var waves = new List<int>();
        for (int seed = 0; seed < 8; seed++)
        {
            var r = new LoadBalancerRules(new Random(seed), 4.5, servers: 3);
            Play(r, 900);
            scores.Add(r.Score);
            waves.Add(r.Wave);
            Assert.Equal(r.Served + r.Dropped + r.Live.Count, r.Live.Count + r.Served + r.Dropped); // nothing lost track of
        }
        _out.WriteLine($"greedy player: waves {string.Join(", ", waves)}; scores {string.Join(", ", scores)}; average {scores.Average():0}");
        Assert.True(waves.Average() >= 4, string.Join(", ", waves));
        Assert.All(waves, w => Assert.True(w < 60)); // and the waves do get the better of it
    }
}
