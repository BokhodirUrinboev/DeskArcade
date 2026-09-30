using System;
using System.IO;
using System.Linq;
using Xunit;

namespace DeskArcade.Tests;

public class RivalriesTests
{
    static readonly DateTime Oct = new(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TheRecordCountsWinsDrawsAndLossesPerGameAndInAll()
    {
        var r = Rivalries.InMemory();
        r.Record("chess", "Alice", 1, Oct);
        r.Record("chess", "Alice", 1, Oct);
        r.Record("chess", "Alice", -1, Oct);
        r.Record("darts", "alice ", 0, Oct); // the same co-worker: case and spaces don't matter
        r.Record("darts", "Bob", -1, Oct);
        Assert.Equal((2, 0, 1), r.Against("Alice", "chess"));
        Assert.Equal((0, 1, 0), r.Against("Alice", "darts"));
        Assert.Equal((2, 1, 1), r.Against("ALICE"));
        Assert.Equal((0, 0, 1), r.Against("Bob"));
        Assert.Equal(new[] { "Alice", "Bob" }, r.Opponents.ToArray()); // the most games first
        var byGame = r.ByGame("Alice").ToList();
        Assert.Equal("chess", byGame[0].GameId); // three chess games before one of darts
        Assert.Equal((2, 0, 1), (byGame[0].Won, byGame[0].Drawn, byGame[0].Lost));
    }

    [Fact]
    public void AnyOutcomeCountsOnlyItsSign()
    {
        var r = Rivalries.InMemory();
        r.Record("pong", "Alice", 7, Oct);
        r.Record("pong", "Alice", -3, Oct);
        Assert.Equal((1, 0, 1), r.Against("Alice"));
    }

    [Fact]
    public void TheLadderIsEloOverTheMonthsBoardGamesOnly()
    {
        var r = Rivalries.InMemory();
        Assert.Equal(Rivalries.StartRating, r.Rating(Oct));
        r.Record("chess", "Alice", 1, Oct);
        Assert.Equal(1216, r.Rating(Oct)); // an even game won: half of K
        r.Record("darts", "Alice", 1, Oct); // a race, not a board game
        Assert.Equal(1216, r.Rating(Oct));
        Assert.Equal(1, r.LadderGamesPlayed(Oct));
        r.Record("reversi", "Bob", -1, Oct);
        Assert.True(r.Rating(Oct) < 1216);
        r.Record("chess", "Alice", 1, Oct.AddMonths(-1)); // last month is not this month's ladder
        Assert.Equal(2, r.LadderGamesPlayed(Oct));
        Assert.Equal(Rivalries.StartRating, r.Rating(Oct.AddMonths(1))); // a new month starts level
    }

    [Fact]
    public void EloIsZeroSumBetweenEqualsAndADrawBetweenEqualsChangesNothing()
    {
        double up = Rivalries.Elo(1200, 1200, 1), down = Rivalries.Elo(1200, 1200, -1);
        Assert.Equal(1200 - (down - 1200), up, 6);
        Assert.Equal(1200, Rivalries.Elo(1200, 1200, 0), 6);
        Assert.True(Rivalries.Elo(1400, 1200, 1) - 1400 < 16); // the favourite gains less for the expected win
    }

    [Fact]
    public void TheRecordSurvivesARestartAndForgetsTheOldest()
    {
        string dir = Path.Combine(Path.GetTempPath(), "da-rivals-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(dir, "rivals.json");
            var r = Rivalries.Load(path);
            r.Record("chess", "Alice", 1, Oct.AddDays(-Rivalries.KeepDays - 5));
            r.Record("chess", "Alice", -1, Oct);
            r.Save();
            var back = Rivalries.Load(path);
            Assert.Equal((0, 0, 1), back.Against("Alice")); // the one past KeepDays went when the newer one came
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ADamagedFileStartsAgain()
    {
        string dir = Path.Combine(Path.GetTempPath(), "da-rivals-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "rivals.json");
            File.WriteAllText(path, "{ not json");
            Assert.Empty(Rivalries.Load(path).Results);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
