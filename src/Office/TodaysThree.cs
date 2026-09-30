using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DeskArcade.Office;

/// <summary>One of today's three: what to get done, whether it is, and whether it came over from an earlier day.</summary>
public sealed class TodayItem
{
    public string Text { get; set; } = "";
    public bool Done { get; set; }
    public bool Carried { get; set; }
}

/// <summary>
/// Today's three: up to three things to get done today, asked for on the morning card, shown under the scoreboard and
/// ticked with a click. What is left at the end of the day carries over to the next one; what got done goes. Kept in
/// settings.json, on this PC.
/// </summary>
public sealed class TodaysThree
{
    public const int Max = 3, MaxLength = 80;

    public List<TodayItem> Items { get; set; } = new();
    /// <summary>The local day (yyyy-MM-dd) the list is for.</summary>
    public string? Day { get; set; }
    /// <summary>The last day every item on the list was ticked (it counts once a day).</summary>
    public string? FinishedOn { get; set; }

    public static string DayKey(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public bool AllDone => Items.Count > 0 && Items.All(i => i.Done);

    /// <summary>
    /// Today's list, from what was typed: blank lines dropped, at most three, each cut to <see cref="MaxLength"/>.
    /// An item typed as it was before keeps its tick and its "carried".
    /// </summary>
    public void Set(IEnumerable<string?> texts, DateOnly today)
    {
        var before = Items;
        Items = texts.Select(Clean).Where(t => t.Length > 0).Take(Max)
            .Select(t => before.FirstOrDefault(b => b.Text == t) is { } kept
                ? new TodayItem { Text = t, Done = kept.Done, Carried = kept.Carried }
                : new TodayItem { Text = t })
            .ToList();
        Day = DayKey(today);
    }

    static string Clean(string? text) =>
        new string((text ?? "").Where(c => !char.IsControl(c)).Take(MaxLength).ToArray()).Trim();

    /// <summary>Ticks or unticks an item. True when that tick finished the whole list, the first time today.</summary>
    public bool Toggle(int index, DateOnly today)
    {
        if (index < 0 || index >= Items.Count) return false;
        Items[index].Done = !Items[index].Done;
        if (!AllDone || FinishedOn == DayKey(today)) return false;
        FinishedOn = DayKey(today);
        return true;
    }

    /// <summary>
    /// The end of the day (or the first look on a new one): the items done go, the rest carry over, marked as such.
    /// Returns how many were done and how many carry over.
    /// </summary>
    public (int Done, int Carried) CarryOver()
    {
        int done = Items.Count(i => i.Done);
        Items = Items.Where(i => !i.Done).Select(i => new TodayItem { Text = i.Text, Carried = true }).ToList();
        return (done, Items.Count);
    }

    /// <summary>A new day since the list was made: what was left carries over to <paramref name="today"/>. True when anything changed.</summary>
    public bool RollOver(DateOnly today)
    {
        string key = DayKey(today);
        if (Day == key) return false;
        bool had = Items.Count > 0;
        if (Day != null) CarryOver();
        Day = key;
        return had;
    }
}
