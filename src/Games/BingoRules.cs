using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace DeskArcade.Games;

/// <summary>
/// One square of a Bingo of Work card: an everyday desk event that ticks itself off once <see cref="Counter"/> has
/// risen by <see cref="Amount"/> since the card was dealt. A counter starting with '@' is not a stats counter: "@unlocked"
/// counts unlocked achievements, and "@morning", "@lunch" and "@evening" hold while you play at that time of day.
/// <see cref="Text"/> is an English key for <c>L.F</c> with {0} for the amount; <see cref="GameId"/> names the game
/// the square is played in (null for the desk squares); <see cref="Needs"/> is what the player must have for the
/// square to be dealt at all ("claude", "task" or "lan").
/// </summary>
public sealed record BingoSquare(string Id, string Counter, long Amount, string Text, string? GameId = null, string? Needs = null)
{
    public bool IsClock => Counter is BingoCard.Morning or BingoCard.Lunch or BingoCard.Evening;
}

/// <summary>
/// A 3×3 Bingo of Work card: eight squares around a free centre, four desk events (Claude finishing, a race won, the
/// pet fed…) and four small game goals (taken from the daily challenges, a third of the size). Squares tick themselves
/// off as the counters rise; three in a row, a column or a diagonal is a bingo, and all nine is a full house. Two
/// squares a card can be swapped for others. The card survives restarts through <see cref="Save"/>/<see cref="Load"/>.
/// </summary>
public sealed class BingoCard
{
    public const int Side = 3, Count = Side * Side, Free = 4, SwapsPerCard = 2, DeskSquares = 4;
    public const string Unlocked = "@unlocked", Morning = "@morning", Lunch = "@lunch", Evening = "@evening";
    const int AllMarked = (1 << Count) - 1;

    /// <summary>Rows, columns, then the two diagonals.</summary>
    public static readonly int[][] Lines =
    {
        new[] { 0, 1, 2 }, new[] { 3, 4, 5 }, new[] { 6, 7, 8 },
        new[] { 0, 3, 6 }, new[] { 1, 4, 7 }, new[] { 2, 5, 8 },
        new[] { 0, 4, 8 }, new[] { 2, 4, 6 },
    };

    public static readonly BingoSquare FreeSquare = new("free", "", 0, "FREE");

    public static readonly BingoSquare[] Desk =
    {
        new("claude", "claude.done", 1, "Be playing when Claude finishes", Needs: "claude"),
        new("task", "task.done", 1, "Be playing when a --while command finishes", Needs: "task"),
        new("cpu", "race.cpuwins", 1, "Beat the computer in a race"),
        new("lan", "lan.wins", 1, "Beat a co-worker over the LAN", Needs: "lan"),
        new("react", "chat.reactions", 1, "Send a co-worker a reaction", Needs: "lan"),
        new("chat", "chat.sent", 1, "Send a co-worker a chat message", Needs: "lan"),
        new("pet", "pet.pets", 3, "Pet the pet {0} times", "pet"),
        new("treat", "pet.treats", 1, "Give the pet a treat", "pet"),
        new("fetch", "pet.fetches", 1, "Play fetch with the pet", "pet"),
        new("daily", "daily.done", 1, "Finish the daily challenge"),
        new("achievement", Unlocked, 1, "Unlock an achievement"),
        new("minutes", "play.minutes", 15, "Play for {0} minutes"),
        new("morning", Morning, 1, "Play before 10 AM"),
        new("lunch", Lunch, 1, "Play at lunchtime (12–2 PM)"),
        new("evening", Evening, 1, "Play after 6 PM"),
    };

    /// <summary>
    /// The game squares: every daily challenge (but Bingo's own), a third of its size, at least 2, and a multiple of 5
    /// once past 10.
    /// </summary>
    public static IReadOnlyList<BingoSquare> Games { get; } = Daily.Pool
        .Where(c => c.GameId != "bingo")
        .GroupBy(c => c.Counter).Select(g => g.First())
        .Select(c => new BingoSquare("g:" + c.Counter, c.Counter, Smaller(c.Target), c.Text, c.GameId))
        .ToArray();

    public static long Smaller(long target)
    {
        long third = Math.Max(2, (target + 2) / 3);
        return third > 10 ? (third + 4) / 5 * 5 : third;
    }

    public static BingoSquare? Find(string id) =>
        id == FreeSquare.Id ? FreeSquare : Desk.FirstOrDefault(s => s.Id == id) ?? Games.FirstOrDefault(s => s.Id == id);

    readonly BingoSquare[] _squares = new BingoSquare[Count];
    readonly long[] _bases = new long[Count];

    BingoCard()
    {
    }

    public BingoSquare this[int i] => _squares[i];
    public long BaseOf(int i) => _bases[i];
    public int Marks { get; private set; }
    /// <summary>The lines already called as bingos (bit per <see cref="Lines"/> entry).</summary>
    public int Called { get; private set; }
    public int SwapsLeft { get; private set; } = SwapsPerCard;

    public bool IsMarked(int i) => (Marks & 1 << i) != 0;
    public int Marked => BitOperations.PopCount((uint)Marks);
    public int Bingos => BitOperations.PopCount((uint)Called);
    public bool FullHouse => Marks == AllMarked;
    public bool IsCalled(int line) => (Called & 1 << line) != 0;

    /// <summary>
    /// Deals a new card: <see cref="DeskSquares"/> desk squares the player can get (<paramref name="has"/> answers
    /// <see cref="BingoSquare.Needs"/>) and game squares from different games, in random places around the free centre.
    /// Each square starts from the counter's value now (<paramref name="value"/>), so only what happens next counts.
    /// </summary>
    public static BingoCard Deal(Random rng, Func<string, long> value, Func<string, bool> has)
    {
        var card = new BingoCard();
        var desk = Pick(rng, Desk.Where(s => s.Needs == null || has(s.Needs)), DeskSquares);
        var games = Pick(rng, Games, Count - 1 - desk.Count, desk);
        var all = desk.Concat(games).OrderBy(_ => rng.Next()).ToList();
        for (int i = 0, k = 0; i < Count; i++)
        {
            var s = i == Free ? FreeSquare : all[k++];
            card._squares[i] = s;
            card._bases[i] = i == Free ? 0 : Start(s, value);
        }
        card.Marks = 1 << Free;
        return card;
    }

    /// <summary><paramref name="count"/> squares from <paramref name="from"/>, no two of the same game.</summary>
    static List<BingoSquare> Pick(Random rng, IEnumerable<BingoSquare> from, int count, IEnumerable<BingoSquare>? taken = null)
    {
        var games = new HashSet<string>((taken ?? Array.Empty<BingoSquare>()).Select(s => s.GameId ?? "").Where(g => g.Length > 0));
        var picked = new List<BingoSquare>();
        foreach (var s in from.OrderBy(_ => rng.Next()))
        {
            if (picked.Count == count) break;
            if (s.GameId != null && !games.Add(s.GameId)) continue;
            picked.Add(s);
        }
        return picked;
    }

    static long Start(BingoSquare s, Func<string, long> value) => s.IsClock ? 0 : value(s.Counter);

    /// <summary>How far a square has come, 0 to its amount (a clock square is 0 until it ticks).</summary>
    public long Progress(int i, Func<string, long> value)
    {
        var s = _squares[i];
        if (IsMarked(i)) return s.Amount;
        if (s.IsClock) return 0;
        return Math.Clamp(value(s.Counter) - _bases[i], 0, s.Amount);
    }

    /// <summary>True while playing at <paramref name="now"/> ticks the clock square <paramref name="counter"/>.</summary>
    public static bool ClockHolds(string counter, DateTime now) => counter switch
    {
        Morning => now.Hour < 10,
        Lunch => now.Hour is >= 12 and < 14,
        Evening => now.Hour >= 18,
        _ => false,
    };

    /// <summary>
    /// Ticks off every square whose counter has risen far enough, and the clock squares whose time it is while
    /// <paramref name="playing"/>. Returns the squares ticked just now.
    /// </summary>
    public List<int> Mark(Func<string, long> value, DateTime now, bool playing)
    {
        var ticked = new List<int>();
        for (int i = 0; i < Count; i++)
        {
            if (IsMarked(i)) continue;
            var s = _squares[i];
            bool done = s.IsClock ? playing && ClockHolds(s.Counter, now) : value(s.Counter) - _bases[i] >= s.Amount;
            if (!done) continue;
            Marks |= 1 << i;
            ticked.Add(i);
        }
        return ticked;
    }

    /// <summary>Ticks off one open square by hand (the --demo player, which has no working day to watch).</summary>
    public bool Tick(int i)
    {
        if (IsMarked(i)) return false;
        Marks |= 1 << i;
        return true;
    }

    /// <summary>The lines completed and not yet called; calling them marks them so each is a bingo once.</summary>
    public List<int> CallLines()
    {
        var lines = new List<int>();
        for (int l = 0; l < Lines.Length; l++)
        {
            if (IsCalled(l) || !Lines[l].All(IsMarked)) continue;
            Called |= 1 << l;
            lines.Add(l);
        }
        return lines;
    }

    /// <summary>Swaps an open square for another of the same kind (desk or game) not on the card, if a swap is left.</summary>
    public bool Swap(int i, Random rng, Func<string, long> value, Func<string, bool> has)
    {
        if (SwapsLeft <= 0 || i == Free || IsMarked(i)) return false;
        var old = _squares[i];
        var others = _squares.Where((_, k) => k != i).ToList();
        var pool = old.GameId != null && Games.Contains(old) ? Games : Desk.Where(s => s.Needs == null || has(s.Needs));
        var next = Pick(rng, pool.Where(s => s != old && !others.Contains(s)), 1, others).FirstOrDefault();
        if (next == null) return false;
        _squares[i] = next;
        _bases[i] = Start(next, value);
        SwapsLeft--;
        return true;
    }

    // ------------------------------------------------------------------ saving

    /// <summary>"marks|called|swaps|id=base,id=base,…", nine squares in order.</summary>
    public string Save() =>
        string.Join("|", Marks.ToString(CultureInfo.InvariantCulture), Called.ToString(CultureInfo.InvariantCulture),
            SwapsLeft.ToString(CultureInfo.InvariantCulture),
            string.Join(",", _squares.Select((s, i) => s.Id + "=" + _bases[i].ToString(CultureInfo.InvariantCulture))));

    /// <summary>A card saved by <see cref="Save"/>, or null when the text is not one (or names a square that is gone).</summary>
    public static BingoCard? Load(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var parts = text.Split('|');
        if (parts.Length != 4 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int marks)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int called)
            || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int swaps)) return null;
        var cells = parts[3].Split(',');
        if (cells.Length != Count) return null;
        var card = new BingoCard
        {
            Marks = (marks & AllMarked) | 1 << Free,
            Called = called & ((1 << Lines.Length) - 1),
            SwapsLeft = Math.Clamp(swaps, 0, SwapsPerCard),
        };
        for (int i = 0; i < Count; i++)
        {
            var kv = cells[i].Split('=');
            if (kv.Length != 2 || Find(kv[0]) is not { } s || (i == Free) != (s == FreeSquare)
                || !long.TryParse(kv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long b)) return null;
            card._squares[i] = s;
            card._bases[i] = b;
        }
        return card;
    }
}
