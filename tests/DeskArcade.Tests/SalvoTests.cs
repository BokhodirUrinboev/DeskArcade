using System;
using DeskArcade.Games;
using Xunit;

namespace DeskArcade.Tests;

public class SalvoTests
{
    [Fact]
    public void ClassicTurnsPassOnAMissOnly()
    {
        int left = SeaTurns.Shots(salvo: false, 5);
        Assert.False(SeaTurns.Passes(false, ref left, ShotKind.Hit));
        Assert.False(SeaTurns.Passes(false, ref left, ShotKind.Sunk));
        Assert.True(SeaTurns.Passes(false, ref left, ShotKind.Miss));
    }

    [Fact]
    public void ASalvoIsAShotPerShipAfloatHitOrMiss()
    {
        int left = SeaTurns.Shots(salvo: true, 3);
        Assert.Equal(3, left);
        Assert.False(SeaTurns.Passes(true, ref left, ShotKind.Miss));
        Assert.False(SeaTurns.Passes(true, ref left, ShotKind.Hit));
        Assert.True(SeaTurns.Passes(true, ref left, ShotKind.Hit)); // the third shot ends the turn even on a hit
    }

    /// <summary>Two computers play a whole game; returns how many turns it took.</summary>
    static int PlayOut(bool salvo, int seed)
    {
        var rng = new Random(seed);
        var fleets = new[] { SeaFleet.Random(rng), SeaFleet.Random(rng) };
        var charts = new[] { new sbyte[SeaFleet.N * SeaFleet.N], new sbyte[SeaFleet.N * SeaFleet.N] };
        int shooter = 0, turns = 1, left = SeaTurns.Shots(salvo, fleets[0].ShipsLeft);
        for (int shots = 0; shots < 400; shots++)
        {
            var target = fleets[1 - shooter];
            int sq = SeaChart.NextShot(charts[shooter], rng, 3);
            var r = target.Shoot(sq);
            SeaChart.Record(charts[shooter], sq, r);
            if (r.Kind == ShotKind.Win) return turns;
            if (SeaTurns.Passes(salvo, ref left, r.Kind))
            {
                shooter = 1 - shooter;
                turns++;
                left = SeaTurns.Shots(salvo, fleets[shooter].ShipsLeft);
            }
        }
        throw new InvalidOperationException("the game didn't end");
    }

    [Fact]
    public void SalvoGamesEndAndTakeFewerTurns()
    {
        int classic = 0, salvo = 0;
        for (int seed = 0; seed < 20; seed++)
        {
            classic += PlayOut(false, seed);
            salvo += PlayOut(true, seed);
        }
        Assert.True(salvo < classic, $"salvo {salvo} turns vs classic {classic}");
    }
}
