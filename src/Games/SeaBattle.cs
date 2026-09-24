using System;
using System.Collections.Generic;
using System.Linq;

namespace DeskArcade.Games;

public enum ShotKind { Miss, Hit, Sunk, Win }

/// <summary>The answer to a shot; <see cref="Ship"/> lists the ship's squares once it is sunk.</summary>
public readonly record struct ShotResult(ShotKind Kind, int[] Ship);

/// <summary>
/// One player's fleet on a 10×10 grid (squares 0..99, row-major), free of UI. Ships of 5, 4, 3, 3 and 2
/// squares lie in straight lines and never touch, not even at the corners.
/// </summary>
public sealed class SeaFleet
{
    public const int N = 10;
    public static readonly int[] Sizes = { 5, 4, 3, 3, 2 };

    readonly List<int[]> _ships;
    readonly bool[] _shot = new bool[N * N];

    SeaFleet(List<int[]> ships) => _ships = ships;

    public IReadOnlyList<int[]> Ships => _ships;
    public bool WasShot(int sq) => _shot[sq];
    public int ShipsLeft => _ships.Count(s => !s.All(c => _shot[c]));
    public bool AllSunk => ShipsLeft == 0;
    public int ShipAt(int sq) => _ships.FindIndex(s => s.Contains(sq));

    public static SeaFleet Random(Random rng)
    {
        while (true)
        {
            var ships = new List<int[]>();
            foreach (int size in Sizes)
            {
                int[]? placed = null;
                for (int attempt = 0; attempt < 200 && placed == null; attempt++)
                {
                    bool across = rng.Next(2) == 0;
                    int r = rng.Next(across ? N : N - size + 1), c = rng.Next(across ? N - size + 1 : N);
                    var cells = Enumerable.Range(0, size).Select(k => across ? r * N + c + k : (r + k) * N + c).ToArray();
                    if (cells.All(sq => !ships.Any(s => s.Any(o => Touches(o, sq))))) placed = cells;
                }
                if (placed == null) break;
                ships.Add(placed);
            }
            if (ships.Count == Sizes.Length) return new SeaFleet(ships);
        }
    }

    static bool Touches(int a, int b) => Math.Abs(a / N - b / N) <= 1 && Math.Abs(a % N - b % N) <= 1;

    public ShotResult Shoot(int sq)
    {
        _shot[sq] = true;
        int i = ShipAt(sq);
        if (i < 0) return new(ShotKind.Miss, Array.Empty<int>());
        var ship = _ships[i];
        if (!ship.All(c => _shot[c])) return new(ShotKind.Hit, Array.Empty<int>());
        return new(AllSunk ? ShotKind.Win : ShotKind.Sunk, ship);
    }

    /// <summary>"a,b,c;d,e,..." — used to reveal the fleet at the end of a LAN game.</summary>
    public string Encode() => string.Join(";", _ships.Select(s => string.Join(",", s)));

    public static SeaFleet? Decode(string text)
    {
        try
        {
            var ships = text.Split(';').Select(s => s.Split(',').Select(int.Parse).ToArray()).ToList();
            return ships.All(s => s.All(c => c is >= 0 and < N * N)) ? new SeaFleet(ships) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>What one player knows about the other's grid, and the CPU's aim at each of its levels.</summary>
public static class SeaChart
{
    public const sbyte Unknown = 0, Miss = 1, Hit = 2, Sunk = 3, Empty = 4; // Empty: next to a sunk ship

    public static void Record(sbyte[] chart, int sq, ShotResult r)
    {
        chart[sq] = r.Kind == ShotKind.Miss ? Miss : Hit;
        if (r.Kind is ShotKind.Sunk or ShotKind.Win)
            foreach (int c in r.Ship)
            {
                chart[c] = Sunk;
                for (int dr = -1; dr <= 1; dr++)
                    for (int dc = -1; dc <= 1; dc++)
                    {
                        int rr = c / SeaFleet.N + dr, cc = c % SeaFleet.N + dc;
                        if (rr is >= 0 and < SeaFleet.N && cc is >= 0 and < SeaFleet.N && chart[rr * SeaFleet.N + cc] == Unknown)
                            chart[rr * SeaFleet.N + cc] = Empty;
                    }
            }
    }

    /// <summary>
    /// The computer's next shot at its level. Easy (1) fires anywhere it hasn't fired yet, even beside a sunk ship.
    /// Medium (2) hunts at random and, once it has hit something, works along the ship. Hard (3) hunts on a
    /// checkerboard, since every ship covers one of those squares. Expert (4) hunts where the most ships still
    /// afloat could lie, and picks its target squares the same way.
    /// </summary>
    public static int NextShot(sbyte[] chart, Random rng, int level = 3)
    {
        const int n = SeaFleet.N;
        level = Math.Clamp(level, 1, 4);
        if (level == 1)
        {
            var any = Enumerable.Range(0, n * n).Where(sq => chart[sq] is Unknown or Empty).ToList();
            return any[rng.Next(any.Count)];
        }
        var targets = Targets(chart);
        if (targets.Count > 0) return level == 4 ? Densest(targets, chart, rng) : targets[rng.Next(targets.Count)];
        var open = Enumerable.Range(0, n * n).Where(sq => chart[sq] == Unknown).ToList();
        if (level == 2) return open[rng.Next(open.Count)];
        if (level == 4) return Densest(open, chart, rng);
        var parity = open.Where(sq => (sq / n + sq % n) % 2 == 0).ToList();
        var pool = parity.Count > 0 ? parity : open;
        return pool[rng.Next(pool.Count)];
    }

    /// <summary>With a wounded ship on the chart, the unknown squares that extend its line of hits (or lie around a single hit).</summary>
    public static List<int> Targets(sbyte[] chart)
    {
        const int n = SeaFleet.N;
        var hits = Enumerable.Range(0, n * n).Where(sq => chart[sq] == Hit).ToList();
        var options = new List<int>();
        if (hits.Count == 0) return options;
        bool across = hits.Count > 1 && hits.All(h => h / n == hits[0] / n);
        bool down = hits.Count > 1 && hits.All(h => h % n == hits[0] % n);
        foreach (int h in hits)
            foreach (var (dr, dc) in new[] { (0, -1), (0, 1), (-1, 0), (1, 0) })
            {
                if (across && dr != 0 || down && dc != 0) continue;
                int r = h / n + dr, c = h % n + dc;
                if (r is >= 0 and < n && c is >= 0 and < n && chart[r * n + c] == Unknown) options.Add(r * n + c);
            }
        return options;
    }

    /// <summary>The sizes of the ships sunk so far: ships never touch, so each group of sunk squares is one ship.</summary>
    public static List<int> SunkSizes(sbyte[] chart)
    {
        const int n = SeaFleet.N;
        var seen = new HashSet<int>();
        var sizes = new List<int>();
        for (int sq = 0; sq < n * n; sq++)
        {
            if (chart[sq] != Sunk || !seen.Add(sq)) continue;
            int size = 0;
            var stack = new Stack<int>(new[] { sq });
            while (stack.Count > 0)
            {
                int c = stack.Pop();
                size++;
                foreach (int d in new[] { -1, 1, -n, n })
                {
                    int o = c + d;
                    if (o < 0 || o >= n * n || (d is -1 or 1 && o / n != c / n)) continue;
                    if (chart[o] == Sunk && seen.Add(o)) stack.Push(o);
                }
            }
            sizes.Add(size);
        }
        return sizes;
    }

    /// <summary>The sizes of the ships still afloat, going by the ones sunk.</summary>
    public static List<int> RemainingSizes(sbyte[] chart)
    {
        var left = SeaFleet.Sizes.ToList();
        foreach (int size in SunkSizes(chart)) left.Remove(size);
        return left;
    }

    /// <summary>For each square, how many ways the ships still afloat could lie over it, given what the chart shows.</summary>
    public static int[] Density(sbyte[] chart)
    {
        const int n = SeaFleet.N;
        var density = new int[n * n];
        foreach (int size in RemainingSizes(chart))
            for (int r = 0; r < n; r++)
                for (int c = 0; c < n; c++)
                    foreach (bool across in new[] { true, false })
                    {
                        if (across ? c + size > n : r + size > n) continue;
                        var cells = Enumerable.Range(0, size).Select(k => across ? r * n + c + k : (r + k) * n + c).ToArray();
                        if (!cells.All(sq => chart[sq] is Unknown or Hit)) continue;
                        foreach (int sq in cells)
                            if (chart[sq] == Unknown) density[sq]++;
                    }
        return density;
    }

    static int Densest(List<int> squares, sbyte[] chart, Random rng)
    {
        var density = Density(chart);
        int best = squares.Max(sq => density[sq]);
        var top = squares.Where(sq => density[sq] == best).ToList();
        return top[rng.Next(top.Count)];
    }
}
