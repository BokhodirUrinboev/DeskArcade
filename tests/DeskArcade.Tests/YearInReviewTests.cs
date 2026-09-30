using System;
using System.Collections.Generic;
using System.Linq;
using DeskArcade.Dev;
using Xunit;

namespace DeskArcade.Tests;

public class YearInReviewTests
{
    static readonly DateTime Now = new(2026, 12, 16, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(2026, 12, 14, null, false)] // not yet
    [InlineData(2026, 12, 15, null, true)]
    [InlineData(2026, 12, 31, 2025, true)]  // shown last year, not this one
    [InlineData(2026, 12, 20, 2026, false)] // shown already
    [InlineData(2027, 1, 3, null, false)]   // January offers it from the menu only
    public void TheCardIsDueOnceAYearFromTheFifteenthOfDecember(int y, int m, int d, int? shown, bool due) =>
        Assert.Equal(due, YearInReview.Due(new DateTime(y, m, d, 10, 0, 0), shown));

    [Fact]
    public void JanuaryLooksBackAtTheYearJustGone()
    {
        Assert.True(YearInReview.InSeason(new DateTime(2027, 1, 20)));
        Assert.False(YearInReview.InSeason(new DateTime(2027, 2, 1)));
        Assert.Equal(2026, YearInReview.YearOf(new DateTime(2027, 1, 20)));
        Assert.Equal(2026, YearInReview.YearOf(new DateTime(2026, 12, 20)));
    }

    [Fact]
    public void TheLinesTellTheYearsWaitsRivalAndCountersSinceItsStart()
    {
        var waits = WaitLog.InMemory();
        waits.Add(new WaitEntry(WaitKind.Command, "dotnet test", Now.AddDays(-30), 600, true, 400));
        waits.Add(new WaitEntry(WaitKind.Ci, "CI · main", Now.AddDays(-10), 1500, true, 300));
        waits.Add(new WaitEntry(WaitKind.Agent, "api", Now.AddDays(-5), 3600, null, 100)); // long, but not a build
        var rivals = Rivalries.InMemory();
        rivals.Record("chess", "Alice", 1, Now.AddDays(-3));
        rivals.Record("chess", "Alice", -1, Now.AddDays(-2));
        rivals.Record("darts", "Alice", 1, Now.AddDays(-1));
        rivals.Record("pong", "Bob", 1, Now.AddDays(-1));
        var counters = new Dictionary<string, long> { ["work.focus"] = 50, ["work.water"] = 30, ["claude.done"] = 12 };
        var yearBase = new Dictionary<string, long> { ["work.focus"] = 20, ["work.water"] = 30 };
        var games = new[] { ("hoops", "Hoops", 7200.0), ("darts", "Darts", 30.0) };

        var lines = YearInReview.Lines(2026, k => counters.TryGetValue(k, out long v) ? v : 0, yearBase, waits, rivals, games,
            Now.AddDays(-100), 40, 200, Now);

        Assert.Contains(lines, l => l.StartsWith("Waited on builds", StringComparison.Ordinal) && l.Contains("3 waits") && l.Contains("13 min"));
        Assert.Contains("The longest wait: CI · main, 25 min", lines);
        Assert.Contains("Favourite game: Hoops, 2 h 0 min played", lines);
        Assert.Contains("Best rival: Alice, 2–1 in 3 games", lines);
        Assert.Contains("Focus blocks: 30 · glasses of water: 0", lines); // the year's own, from the snapshot
        Assert.Contains("Claude finished 12 times while you played", lines); // no snapshot of it: from the start
        Assert.Contains("Your pet has kept you company for 100 days", lines);
        Assert.Equal("Achievements: 40 of 200", lines[^1]);
        Assert.DoesNotContain(lines, l => l.StartsWith("Daily challenges", StringComparison.Ordinal)); // none: nothing said
    }

    [Fact]
    public void LastYearsWaitsAndGamesStayOutOfThisYearsCard()
    {
        var waits = WaitLog.InMemory();
        waits.Add(new WaitEntry(WaitKind.Command, "make", new DateTime(2025, 12, 1, 0, 0, 0, DateTimeKind.Utc), 900, true));
        var rivals = Rivalries.InMemory();
        rivals.Record("chess", "Alice", 1, new DateTime(2025, 11, 1, 0, 0, 0, DateTimeKind.Utc));
        var lines = YearInReview.Lines(2026, _ => 0, null, waits, rivals, Array.Empty<(string, string, double)>(), null, 0, 200, Now);
        Assert.Equal(new[] { "Achievements: 0 of 200" }, lines);
    }

    [Theory]
    [InlineData(12, "12 s")]
    [InlineData(125, "2 min")]
    [InlineData(3 * 3600 + 20 * 60, "3 h 20 min")]
    public void DurationsReadNaturally(double seconds, string expected) => Assert.Equal(expected, YearInReview.Long(seconds));
}
