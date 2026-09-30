using System;
using System.IO;
using System.Linq;
using DeskArcade.Dev;
using Xunit;

namespace DeskArcade.Tests;

public class WaitLogTests
{
    static readonly DateTime Start = new(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AWaitIsKeptTrimmedAndItsPlayCannotOutlastIt()
    {
        var log = WaitLog.InMemory();
        WaitEntry? added = null;
        log.Added += e => added = e;
        log.Add(new WaitEntry(WaitKind.Command, "  dotnet test ", Start, 250, true, PlayedSeconds: 900));
        var e = Assert.Single(log.Entries);
        Assert.Equal("dotnet test", e.Label);
        Assert.Equal(250, e.PlayedSeconds);
        Assert.NotNull(added);
    }

    [Fact]
    public void AnInstantOrNamelessWaitIsSkipped()
    {
        var log = WaitLog.InMemory();
        log.Add(new WaitEntry(WaitKind.Agent, "api", Start, WaitLog.MinSeconds / 2));
        log.Add(new WaitEntry(WaitKind.Lane, " ", Start, 60));
        Assert.Empty(log.Entries);
    }

    [Fact]
    public void WaitsOlderThanTheKeepDaysGoAndBetweenPicksAWeek()
    {
        var log = WaitLog.InMemory();
        log.Add(new WaitEntry(WaitKind.Ci, "CI · main", Start.AddDays(-WaitLog.KeepDays - 1), 300, false));
        log.Add(new WaitEntry(WaitKind.Ci, "CI · main", Start.AddDays(-3), 320, true));
        log.Add(new WaitEntry(WaitKind.Ci, "CI · main", Start, 310, true));
        Assert.Equal(2, log.Entries.Count);
        Assert.Single(log.Between(Start.AddDays(-7), Start));
        Assert.Equal(2, log.Between(Start.AddDays(-7), Start.AddSeconds(1)).Count());
    }

    [Fact]
    public void TheLogSurvivesARestartWithItsKinds()
    {
        string dir = Path.Combine(Path.GetTempPath(), "da-waits-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(dir, "waits.json");
            var log = WaitLog.Load(path);
            log.Add(new WaitEntry(WaitKind.Download, "big.iso", Start, 42.5, null, 10, "browser"));
            log.Save();
            Assert.Contains("\"Download\"", File.ReadAllText(path)); // kinds are written as names
            var back = Assert.Single(WaitLog.Load(path).Entries);
            Assert.Equal(new WaitEntry(WaitKind.Download, "big.iso", Start, 42.5, null, 10, "browser"), back);
            File.WriteAllText(path, "[{ broken");
            Assert.Empty(WaitLog.Load(path).Entries);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
