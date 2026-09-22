using System;
using System.Collections.Generic;

namespace DeskArcade.Games;

/// <summary>A kind of fish: how heavy it gets, what it is worth, how hard it pulls, how often it bites and how long it looks (px).</summary>
public sealed record Species(string Id, string Name, double MinKg, double MaxKg, int Points, double Strength, double Chance, double Length)
{
    public bool Golden => Id == "golden";
}

/// <summary>
/// The Fishing fight without any UI. Holding the reel wins line but builds tension against the fish's pull;
/// letting go lets the tension fall while the fish takes line back. The fish pulls in waves, makes sudden runs
/// (surges) and tires against a tight line. Tension at <see cref="SnapAt"/> snaps the line; line at 0 lands the
/// fish. Also holds the species table and what a catch is worth. <see cref="FishingGame"/> draws it; tests drive it.
/// </summary>
public sealed class FishFight
{
    public const double SnapAt = 1, ReelSpeed = 130, TakeSpeed = 45;
    const double RiseRate = 1.5, FallRate = 2.5, SurgeBoost = 0.6, Tire = 0.06, MinStamina = 0.45;

    public static readonly Species[] Table =
    {
        new("perch", "Perch", 0.1, 0.6, 10, 0.35, 0.34, 24),
        new("carp", "Carp", 0.8, 4.5, 20, 0.6, 0.28, 36),
        new("pike", "Pike", 1.5, 6.5, 30, 0.8, 0.2, 46),
        new("catfish", "Catfish", 3, 12, 45, 1.0, 0.14, 52),
        new("golden", "Golden Trout", 0.5, 2.5, 100, 0.7, 0.04, 32),
    };

    public static Species ById(string id) => Array.Find(Table, s => s.Id == id) ?? Table[0];

    /// <summary>A random species, weighted by how common each one is.</summary>
    public static Species Pick(Random rng)
    {
        double total = 0;
        foreach (var s in Table) total += s.Chance;
        double r = rng.NextDouble() * total;
        foreach (var s in Table)
            if ((r -= s.Chance) < 0) return s;
        return Table[0];
    }

    /// <summary>A weight for a fish of this species, to 0.1 kg; small ones are more common than trophies.</summary>
    public static double RollKg(Species s, Random rng) =>
        Math.Round(s.MinKg + (s.MaxKg - s.MinKg) * Math.Pow(rng.NextDouble(), 1.6), 1);

    /// <summary>Where a weight sits in the species' range: 0 the lightest, 1 the heaviest.</summary>
    public static double SizeOf(Species s, double kg) => Math.Clamp((kg - s.MinKg) / (s.MaxKg - s.MinKg), 0, 1);

    /// <summary>The species' base points, up to double for the heaviest of its kind.</summary>
    public static int PointsFor(Species s, double kg) => (int)Math.Round(s.Points * (1 + SizeOf(s, kg)));

    readonly Random _rng;
    double _phase, _phaseRate, _surgeIn, _surgeLeft;

    /// <param name="distance">Line out when the fish is hooked (px); the fish can never take more than <paramref name="maxDistance"/>.</param>
    public FishFight(Species species, double kg, double distance, Random rng, double maxDistance = 0)
    {
        Species = species;
        Kg = kg;
        _rng = rng;
        Distance = distance;
        MaxDistance = Math.Max(distance, maxDistance);
        Strength = species.Strength * (0.8 + 0.4 * SizeOf(species, kg)); // a heavy one pulls harder than its kind
        _phase = rng.NextDouble() * Math.PI * 2;
        _phaseRate = 1.5 + rng.NextDouble();
        _surgeIn = 0.4 + rng.NextDouble(); // the first run comes soon after the hook is set
    }

    public Species Species { get; }
    public double Kg { get; }
    public double Strength { get; }
    public double Distance { get; private set; }
    public double MaxDistance { get; private set; }

    /// <summary>The pond changed size mid-fight: the fish can take no more line than <paramref name="max"/>, and is pulled in if it is farther.</summary>
    public void Limit(double max)
    {
        MaxDistance = Math.Max(1, max);
        Distance = Math.Min(Distance, MaxDistance);
    }
    /// <summary>0 slack … <see cref="SnapAt"/> snapped.</summary>
    public double Tension { get; private set; }
    /// <summary>1 fresh, falling as the fish fights a tight line.</summary>
    public double Stamina { get; private set; } = 1;
    /// <summary>How hard the fish is pulling right now.</summary>
    public double Pull { get; private set; }
    public bool Surging => _surgeLeft > 0;
    public bool Snapped { get; private set; }
    public bool Landed => Distance <= 0;
    public bool Over => Snapped || Landed;

    public void Step(double h, bool reeling)
    {
        if (Over) return;
        _phase += _phaseRate * h;
        if (_surgeLeft > 0) _surgeLeft -= h;
        else if ((_surgeIn -= h) <= 0)
        {
            _surgeLeft = 0.4 + _rng.NextDouble() * 0.4;
            _surgeIn = 1.5 + _rng.NextDouble() * 2;
        }
        double wave = 0.55 + 0.45 * (0.5 + 0.5 * Math.Sin(_phase));
        Pull = Strength * Stamina * (wave + (Surging ? SurgeBoost : 0));

        // reeling drives the tension toward more than the pull; a slack reel lets it settle well below
        double target = reeling ? Pull * 1.1 + 0.15 : Pull * 0.3;
        Tension += (target - Tension) * (1 - Math.Exp(-(reeling ? RiseRate : FallRate) * h));
        if (Tension >= SnapAt)
        {
            Tension = SnapAt;
            Snapped = true;
            return;
        }

        if (reeling) Distance -= ReelSpeed * (1 - 0.3 * Math.Min(1, Pull)) * h;
        else Distance += Pull * TakeSpeed * h;
        Distance = Math.Clamp(Distance, 0, MaxDistance);
        Stamina = Math.Max(MinStamina, Stamina - Tire * Tension * h);
    }
}

public enum BiteEvent { None, Nibble, Bite, Missed }
public enum BiteClick { Spooked, Hooked, TooLate }

/// <summary>
/// What a fish does once it reaches the bobber: 0-3 nibbles (small dips), then the bite, when the bobber goes
/// under for <see cref="Window"/> seconds. Striking during the bite hooks it; striking earlier spooks it; after
/// the window it has stolen the bait. Time 0 is the fish's arrival.
/// </summary>
public sealed class BiteSchedule
{
    public const double Window = 0.7, NibbleTime = 0.22;

    readonly double[] _nibbles;
    int _next;
    bool _bit, _missed;

    public BiteSchedule(Random rng)
    {
        _nibbles = new double[rng.Next(0, 4)];
        double t = 0.3 + rng.NextDouble() * 0.5;
        for (int i = 0; i < _nibbles.Length; i++)
        {
            _nibbles[i] = t;
            t += 0.55 + rng.NextDouble() * 0.9;
        }
        BiteAt = t;
    }

    public IReadOnlyList<double> NibbleTimes => _nibbles;
    public double BiteAt { get; }
    public double T { get; private set; }
    public bool Biting => T >= BiteAt && T < BiteAt + Window;
    public bool Missed => T >= BiteAt + Window;

    /// <summary>Moves time on; returns at most one event per call, in order.</summary>
    public BiteEvent Advance(double h)
    {
        T += h;
        if (_next < _nibbles.Length && T >= _nibbles[_next])
        {
            _next++;
            return BiteEvent.Nibble;
        }
        if (!_bit && T >= BiteAt)
        {
            _bit = true;
            return BiteEvent.Bite;
        }
        if (!_missed && T >= BiteAt + Window)
        {
            _missed = true;
            return BiteEvent.Missed;
        }
        return BiteEvent.None;
    }

    /// <summary>How far the bobber is pulled down: small dips for nibbles, all the way under (1) for the bite.</summary>
    public double Dip
    {
        get
        {
            if (T >= BiteAt) return Missed ? 0 : 1;
            foreach (double n in _nibbles)
                if (T >= n && T < n + NibbleTime) return 0.35 * Math.Sin(Math.PI * (T - n) / NibbleTime);
            return 0;
        }
    }

    public BiteClick Click() => T < BiteAt ? BiteClick.Spooked : T < BiteAt + Window ? BiteClick.Hooked : BiteClick.TooLate;
}
