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

/// <summary>What one player knows about the other's grid, and the CPU's aim.</summary>
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
    /// Hunt and target: with a wounded ship, extend the line of hits (or try around a single hit);
    /// otherwise fire at unknown squares on a checkerboard, since every ship covers one of those.
    /// </summary>
    public static int NextShot(sbyte[] chart, Random rng)
    {
        const int n = SeaFleet.N;
        var hits = Enumerable.Range(0, n * n).Where(sq => chart[sq] == Hit).ToList();
        bool Open(int r, int c) => r is >= 0 and < n && c is >= 0 and < n && chart[r * n + c] == Unknown;
        if (hits.Count > 0)
        {
            var options = new List<int>();
            bool across = hits.Count > 1 && hits.All(h => h / n == hits[0] / n);
            bool down = hits.Count > 1 && hits.All(h => h % n == hits[0] % n);
            foreach (int h in hits)
                foreach (var (dr, dc) in new[] { (0, -1), (0, 1), (-1, 0), (1, 0) })
                {
                    if (across && dr != 0 || down && dc != 0) continue;
                    if (Open(h / n + dr, h % n + dc)) options.Add((h / n + dr) * n + h % n + dc);
                }
            if (options.Count > 0) return options[rng.Next(options.Count)];
        }
        var open = Enumerable.Range(0, n * n).Where(sq => chart[sq] == Unknown).ToList();
        var parity = open.Where(sq => (sq / n + sq % n) % 2 == 0).ToList();
        var pool = parity.Count > 0 ? parity : open;
        return pool[rng.Next(pool.Count)];
    }
}
