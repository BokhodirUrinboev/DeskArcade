using System;
using System.Collections.Generic;
using System.Linq;

namespace DeskArcade.Games;

/// <summary>
/// A rope bridge across a gap, in DIPs along the walk: the gap runs from <see cref="Start"/> to <see cref="End"/> in
/// <see cref="Slots"/> places for planks. Interns walk across from the left; one stepping onto a place with no plank
/// falls, and so does one stepping onto a plank that is worn through: every plank holds a few crossings (its
/// <see cref="Wear"/>) and snaps on the next one. A round is <see cref="RoundInterns"/> interns; those who reach the far
/// side are saved. The pile of planks refills over time. UI-free, so the bridge can be tested.
/// </summary>
public sealed class RopeBridge
{
    public const int RoundInterns = 15, PileMax = 6, MinWear = 3, MaxWear = 6;
    public const double Speed = 60, SpawnEvery = 3.6, RefillEvery = 1.3, Beyond = 90;

    public sealed class Walker
    {
        public double X;
        public bool Gone;   // saved or fallen: no longer walking
        public bool Fell;
        public int Slot = -1; // the place it stands on now (-1: on solid ground)
    }

    /// <summary>What a step of the simulation did, for the drawing and the sounds.</summary>
    public readonly record struct Event(string Kind, int Slot, Walker Who);

    readonly int[] _wear;
    readonly Random _rng;
    readonly List<Walker> _walkers = new();
    double _spawnT, _refillT;

    public RopeBridge(double start, double end, double slotWidth, Random rng, double from)
    {
        Start = start;
        End = end;
        Slots = Math.Max(3, (int)Math.Floor((end - start) / slotWidth));
        SlotW = (end - start) / Slots;
        From = from;
        _wear = Enumerable.Repeat(-1, Slots).ToArray();
        _rng = rng;
        Pile = PileMax;
        _spawnT = 2.5;
    }

    public double Start { get; }
    public double End { get; }
    public int Slots { get; }
    public double SlotW { get; }
    /// <summary>Where the interns come from, left of the gap.</summary>
    public double From { get; }
    /// <summary>Where they count as across: a little way onto the far side.</summary>
    public double To => End + Beyond;

    /// <summary>Crossings a plank has left (0: it snaps on the next), or -1 where there is none.</summary>
    public int Wear(int slot) => _wear[slot];
    public bool HasPlank(int slot) => _wear[slot] >= 0;
    public int Pile { get; private set; }
    public int Spawned { get; private set; }
    public int Saved { get; private set; }
    public int Lost { get; private set; }
    public int Laid { get; private set; }
    public IReadOnlyList<Walker> Walkers => _walkers;
    public bool Over => Spawned == RoundInterns && _walkers.All(w => w.Gone);

    /// <summary>True while an intern stands on the place (its plank can't be swapped then).</summary>
    public bool Occupied(int slot) => _walkers.Any(w => !w.Gone && w.Slot == slot);

    /// <summary>The place under <paramref name="x"/>, or -1 off the gap.</summary>
    public int SlotAt(double x) => x < Start || x >= End ? -1 : Math.Min(Slots - 1, (int)((x - Start) / SlotW));

    /// <summary>Lays a plank from the pile in <paramref name="slot"/>; one already there (worn or not) is replaced.</summary>
    public bool Lay(int slot, int? wear = null)
    {
        if (slot < 0 || slot >= Slots || Pile == 0) return false;
        if (Occupied(slot)) return false; // not from under someone's feet
        _wear[slot] = wear ?? _rng.Next(MinWear, MaxWear + 1);
        Pile--;
        Laid++;
        return true;
    }

    /// <summary>Walks everyone on, brings new interns and planks; returns what happened ("saved", "fell", "snap", "step", "spawn").</summary>
    public List<Event> Step(double dt)
    {
        var events = new List<Event>();
        if ((_refillT += dt) >= RefillEvery)
        {
            _refillT = 0;
            if (Pile < PileMax) Pile++;
        }
        if (Spawned < RoundInterns && (_spawnT -= dt) <= 0)
        {
            _spawnT = SpawnEvery;
            var w = new Walker { X = From };
            _walkers.Add(w);
            Spawned++;
            events.Add(new Event("spawn", -1, w));
        }
        foreach (var w in _walkers.Where(w => !w.Gone))
        {
            double before = w.X;
            w.X += Speed * dt;
            int slot = SlotAt(w.X);
            if (slot != w.Slot && slot >= 0)
            {
                // a new place underfoot: a plank takes the weight, a worn one snaps, and no plank at all is a fall
                if (_wear[slot] < 0)
                {
                    Fall(w, slot, events, snapped: false);
                    continue;
                }
                if (_wear[slot] == 0)
                {
                    _wear[slot] = -1;
                    Fall(w, slot, events, snapped: true);
                    continue;
                }
                _wear[slot]--;
                events.Add(new Event("step", slot, w));
            }
            w.Slot = slot;
            if (before < To && w.X >= To)
            {
                w.Gone = true;
                Saved++;
                events.Add(new Event("saved", -1, w));
            }
        }
        return events;
    }

    void Fall(Walker w, int slot, List<Event> events, bool snapped)
    {
        w.Gone = w.Fell = true;
        w.Slot = slot;
        Lost++;
        if (snapped) events.Add(new Event("snap", slot, w));
        events.Add(new Event("fell", slot, w));
    }
}
