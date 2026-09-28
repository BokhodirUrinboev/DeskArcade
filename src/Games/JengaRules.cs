using System;
using System.Collections.Generic;
using System.Linq;

namespace DeskArcade.Games;

/// <summary>
/// A Jenga tower seen from the side, in block widths and layers: each layer holds three blocks side by side (slots 0, 1,
/// 2), bottom layer 0. Take a block from any layer below the top one (below the two top ones while the top is still
/// being built), then lay it on top: the top layer fills up before a new one starts. The tower stands while, for every
/// layer, the weight of everything above it rests over that layer's remaining blocks (its centre of mass, shifted by the
/// tower's sway, inside their span); a layer that loses its last block drops everything above. Blocks carrying more
/// weight sit tighter (see <see cref="Tightness"/>). UI-free, so the tower can be tested.
/// </summary>
public sealed class JengaTower
{
    public const int Slots = 3, StartLayers = 12;
    /// <summary>How far past its supporting blocks (in block widths) the weight may lean before the tower goes.</summary>
    public const double Margin = 0.04;

    readonly List<bool[]> _layers = new();
    readonly List<double[]> _grip = new();
    readonly Random _rng;

    public JengaTower(Random rng, int layers = StartLayers)
    {
        _rng = rng;
        for (int i = 0; i < layers; i++) AddLayer(full: true);
    }

    JengaTower(JengaTower o)
    {
        _rng = o._rng;
        foreach (var l in o._layers) _layers.Add((bool[])l.Clone());
        foreach (var g in o._grip) _grip.Add((double[])g.Clone());
        Holding = o.Holding;
        Moved = o.Moved;
        Fallen = o.Fallen;
    }

    /// <summary>A copy to try a move on (the computer's and the tests' what-if).</summary>
    public JengaTower Clone() => new(this);

    void AddLayer(bool full)
    {
        _layers.Add(new[] { full, full, full });
        _grip.Add(Enumerable.Range(0, Slots).Select(_ => 0.15 + _rng.NextDouble() * 0.6).ToArray());
    }

    /// <summary>Layers with at least one block (the top one may be half built).</summary>
    public int Height => _layers.Count;

    public bool Present(int layer, int slot) => layer >= 0 && layer < _layers.Count && _layers[layer][slot];
    public int Count(int layer) => _layers[layer].Count(b => b);
    public int Top => _layers.Count - 1;
    public bool TopComplete => Count(Top) == Slots;

    /// <summary>A block has been taken and waits to be laid on top.</summary>
    public bool Holding { get; private set; }

    /// <summary>Blocks taken out and laid on top so far.</summary>
    public int Moved { get; private set; }

    public bool Fallen { get; private set; }

    /// <summary>
    /// Whether a block may be taken: never while one is held, never from the top layer, and not from the one below it
    /// while the top is still being built.
    /// </summary>
    public bool Takeable(int layer, int slot)
    {
        if (Fallen || Holding || !Present(layer, slot)) return false;
        int highest = TopComplete ? Top - 1 : Top - 2;
        return layer <= highest;
    }

    public bool Take(int layer, int slot)
    {
        if (!Takeable(layer, slot)) return false;
        _layers[layer][slot] = false;
        Holding = true;
        return true;
    }

    /// <summary>Where the held block may go: the empty slots of the top layer, or all three of a new one.</summary>
    public IReadOnlyList<int> OpenSlots()
    {
        if (!Holding || Fallen) return Array.Empty<int>();
        if (TopComplete) return new[] { 0, 1, 2 };
        return Enumerable.Range(0, Slots).Where(s => !_layers[Top][s]).ToArray();
    }

    /// <summary>The layer the held block would be laid on.</summary>
    public int PlaceLayer => TopComplete ? Top + 1 : Top;

    public bool Place(int slot)
    {
        if (!OpenSlots().Contains(slot)) return false;
        if (TopComplete) AddLayer(full: false);
        _layers[Top][slot] = true;
        Holding = false;
        Moved++;
        return true;
    }

    /// <summary>
    /// How stiff a block is to pull, 0 (loose) to 1: its own fit, more with more of the tower above it, and more again
    /// when its layer is down to fewer blocks sharing the weight.
    /// </summary>
    public double Tightness(int layer, int slot)
    {
        double above = Math.Max(0, Top - layer) / (double)Math.Max(1, Top);
        double share = Count(layer) switch { 3 => 0, 2 => 0.2, _ => 0.4 };
        return Math.Clamp(_grip[layer][slot] * (0.5 + 0.5 * above) + share, 0, 1);
    }

    /// <summary>
    /// The lowest layer the tower gives way at, with the top leaning <paramref name="sway"/> block widths off centre
    /// (each layer by its share of the height), or -1 while it stands.
    /// </summary>
    public int FailingLayer(double sway = 0)
    {
        for (int i = 0; i < _layers.Count - 1; i++)
        {
            int n = 0;
            double sum = 0;
            for (int j = i + 1; j < _layers.Count; j++)
            {
                double lean = Top > 0 ? sway * j / Top : 0;
                for (int s = 0; s < Slots; s++)
                {
                    if (!_layers[j][s]) continue;
                    sum += s + 0.5 + lean;
                    n++;
                }
            }
            if (n == 0) continue;
            if (Count(i) == 0) return i;
            double lo = Enumerable.Range(0, Slots).First(s => _layers[i][s]);
            double hi = Enumerable.Range(0, Slots).Last(s => _layers[i][s]) + 1;
            double lowLean = Top > 0 ? sway * i / Top : 0;
            double com = sum / n;
            if (com < lo + lowLean - Margin || com > hi + lowLean + Margin) return i;
        }
        return -1;
    }

    /// <summary>How far (in block widths) the tower's top may sway either way before it goes, as it stands now.</summary>
    public double SwayRoom()
    {
        double lo = 0, hi = 4;
        if (FailingLayer(0) >= 0) return 0;
        for (int k = 0; k < 24; k++)
        {
            double mid = (lo + hi) / 2;
            if (FailingLayer(mid) < 0 && FailingLayer(-mid) < 0) lo = mid;
            else hi = mid;
        }
        return lo;
    }

    /// <summary>Checks the tower after a move; true (and <see cref="Fallen"/>) when it gives way.</summary>
    public bool Check(double sway = 0)
    {
        if (!Fallen && FailingLayer(sway) >= 0) Fallen = true;
        return Fallen;
    }
}
