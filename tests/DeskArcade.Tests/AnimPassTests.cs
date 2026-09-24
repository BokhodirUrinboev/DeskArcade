using System;
using System.Collections.Generic;
using Avalonia;
using DeskArcade.Engine;
using DeskArcade.Games;
using Xunit;

namespace DeskArcade.Tests;

/// <summary>The CPU levels of Air Hockey and Pong: how a skill and the session's wins turn into speed, reaction and error.</summary>
public class HockeyPongLevelTests
{
    static readonly Rect Arena = new(0, 0, 1920, 1040);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AHigherHockeySkillMovesFaster(int skill) =>
        Assert.True(HockeyTable.SpeedFor(skill + 1, 1) > HockeyTable.SpeedFor(skill, 1));

    [Fact]
    public void HockeyWinsSpeedTheCpuUpButOnlySoFar()
    {
        Assert.True(HockeyTable.SpeedFor(2, 2) > HockeyTable.SpeedFor(2, 1));
        Assert.Equal(HockeyTable.SpeedFor(2, 1), HockeyTable.SpeedFor(2, 0)); // a level below 1 adds nothing
        for (int skill = 1; skill <= 4; skill++)
            for (int level = 1; level <= 30; level++)
                Assert.InRange(HockeyTable.SpeedFor(skill, level), HockeyTable.BaseSpeeds[0], HockeyTable.MaxCpuSpeed);
        Assert.Equal(HockeyTable.MaxCpuSpeed, HockeyTable.SpeedFor(4, 30));
    }

    [Fact]
    public void ASkillOutOfRangeIsClamped()
    {
        Assert.Equal(HockeyTable.SpeedFor(1, 1), HockeyTable.SpeedFor(0, 1));
        Assert.Equal(HockeyTable.SpeedFor(4, 1), HockeyTable.SpeedFor(9, 1));
        Assert.Equal(PongTable.SpeedFor(1, 1), PongTable.SpeedFor(-3, 1));
        Assert.Equal(PongTable.SpeedFor(4, 1), PongTable.SpeedFor(7, 1));
    }

    [Fact]
    public void HockeyReactionAndErrorShrinkWithSkillDownToNothingForAnExpert()
    {
        for (int skill = 1; skill < 4; skill++)
        {
            Assert.True(HockeyTable.ReactionFor(skill) > HockeyTable.ReactionFor(skill + 1));
            Assert.True(HockeyTable.ErrorFor(skill) > HockeyTable.ErrorFor(skill + 1));
        }
        Assert.Equal(0, HockeyTable.ReactionFor(4));
        Assert.Equal(0, HockeyTable.ErrorFor(4));
    }

    [Fact]
    public void TheServeSpotIsOnTheServingSideAndOnTheTable()
    {
        var t = new HockeyTable(Arena, new Random(1));
        Assert.True(t.ServeSpot(1).X > Arena.Center.X);
        Assert.True(t.ServeSpot(-1).X < Arena.Center.X);
        Assert.Equal(Arena.Center.X, t.ServeSpot(0).X);
        foreach (int side in new[] { -1, 0, 1 })
        {
            Assert.True(Arena.Deflate(HockeyTable.PuckR).Contains(t.ServeSpot(side).ToPoint()));
            t.PlacePuck(side);
            Assert.Equal(t.ServeSpot(side), t.Puck);
        }
    }

    [Fact]
    public void AnEasyHockeyCpuHesitatesWhenThePuckArrivesAndAnExpertDoesNot()
    {
        var easy = new HockeyTable(Arena, new Random(3)) { Skill = 1 };
        var expert = new HockeyTable(Arena, new Random(3)) { Skill = 4 };
        foreach (var t in new[] { easy, expert })
        {
            t.PlacePuck(1); // on the computer's half, at rest
            t.Advance(1.0 / 60, t.Me, t.Cpu); // the puck is seen to be on its side
        }
        var easyTo = easy.CpuMove(1.0 / 60, false);
        var expertTo = expert.CpuMove(1.0 / 60, false);
        Assert.Equal(easy.Cpu, easyTo); // still watching from its goal
        Assert.NotEqual(expert.Cpu, expertTo); // already on its way to the puck
        Assert.True((expertTo - expert.Puck).Length < (expert.Cpu - expert.Puck).Length);

        // once its reaction time has passed the easy one sets off too
        for (double t = 0; t < HockeyTable.ReactionFor(1) + 0.1; t += 1.0 / 60) easyTo = easy.CpuMove(1.0 / 60, false);
        Assert.NotEqual(easy.Cpu, easyTo);
    }

    [Fact]
    public void PongSpeedRisesWithSkillAndWins()
    {
        for (int skill = 1; skill < 4; skill++) Assert.True(PongTable.SpeedFor(skill + 1, 1) > PongTable.SpeedFor(skill, 1));
        Assert.True(PongTable.SpeedFor(2, 3) > PongTable.SpeedFor(2, 1));
        for (int skill = 1; skill <= 4; skill++)
            for (int level = 1; level <= 30; level++)
                Assert.InRange(PongTable.SpeedFor(skill, level), PongTable.BaseSpeeds[0], PongTable.MaxCpuSpeed);
    }

    [Fact]
    public void PongErrorAndReactionShrinkWithSkillAndTheErrorWithWins()
    {
        for (int skill = 1; skill < 4; skill++)
        {
            Assert.True(PongTable.ErrorFor(skill, 1) > PongTable.ErrorFor(skill + 1, 1));
            Assert.True(PongTable.ReactionFor(skill) > PongTable.ReactionFor(skill + 1));
        }
        Assert.Equal(0, PongTable.ReactionFor(4));
        Assert.True(PongTable.ErrorFor(2, 6) < PongTable.ErrorFor(2, 1));
        Assert.True(PongTable.ErrorFor(1, 100) > 0); // it never becomes perfect through wins alone
        Assert.True(PongTable.ErrorFor(4, 1) > 0); // and even an expert misjudges a little
    }

    [Fact]
    public void AnEasyPongCpuIsSlowOffTheMarkAfterAReturn()
    {
        var t = new PongTable(Arena, new Random(5)) { Skill = 1 };
        // the ball is about to hit the player's paddle, which sends it toward the computer
        t.Ball = new Vec2(t.MeX + PongTable.PaddleW / 2 + PongTable.BallR + 2, t.MeY);
        t.BallVel = new Vec2(-600, 0);
        t.BallInPlay = true;
        for (int i = 0; i < 6; i++) t.Advance(PongTable.Step, t.MeY, t.ThemY);
        Assert.True(t.BallVel.X > 0, "the paddle did not return the ball");
        double before = t.ThemY;
        double y = before;
        for (double time = 0; time < PongTable.ReactionFor(1) - 0.05; time += 1.0 / 60) y = t.CpuMove(1.0 / 60);
        Assert.Equal(before, y); // no reaction yet
        for (double time = 0; time < 0.2; time += 1.0 / 60) y = t.CpuMove(1.0 / 60);
        Assert.NotEqual(before, y); // now it goes for the ball (or its misjudged idea of it)
    }
}

/// <summary>The race tuning of the eight round-based games: what the computer rival aims at and how long it takes.</summary>
public class RaceChoicesTests
{
    public static IEnumerable<object[]> Games => new[]
    {
        new object[] { "bubbles", BubblesGame.FairRound, BubblesGame.TypicalRoundSeconds },
        new object[] { "whack", WhackGame.FairRound, WhackGame.RoundSeconds },
        new object[] { "tower", TowerGame.FairRound, TowerGame.TypicalRoundSeconds },
        new object[] { "bowling", BowlingGame.FairRound, BowlingGame.TypicalRoundSeconds },
        new object[] { "fishing", FishingGame.FairRound, FishingGame.RoundSeconds },
        new object[] { "pinball", PinballGame.FairRound, PinballGame.TypicalRoundSeconds },
        new object[] { "toss", PaperTossGame.FairRound, PaperTossGame.TypicalRoundSeconds },
        new object[] { "blockfall", BlockfallGame.FairRound, BlockfallGame.TypicalRoundSeconds },
    };

    [Theory]
    [MemberData(nameof(Games))]
    public void EveryRaceGameHasAPositiveBaselineAndARoundOfSensibleLength(string id, int baseline, double seconds)
    {
        Assert.True(baseline > 0, id);
        Assert.InRange(seconds, 10, 600);
    }

    [Fact]
    public void TimedRoundsRaceForTheirWholeClock()
    {
        Assert.Equal(30, WhackGame.RoundSeconds);
        Assert.Equal(120, FishingGame.RoundSeconds);
        // Bubbles: more than one wave, but not the rest of the afternoon
        Assert.True(BubblesGame.TypicalRoundSeconds > BubblesGame.WaveSeconds(1) + BubblesGame.WaveSeconds(2) / 2);
        Assert.True(BubblesGame.TypicalRoundSeconds < BubblesGame.WaveSeconds(1) + BubblesGame.WaveSeconds(2) + BubblesGame.WaveSeconds(3) + BubblesGame.WaveSeconds(4));
    }

    [Fact]
    public void BubbleWavesGetLongerUpToAMinute()
    {
        Assert.Equal(24, BubblesGame.WaveSeconds(1));
        for (int wave = 1; wave < 12; wave++) Assert.True(BubblesGame.WaveSeconds(wave + 1) >= BubblesGame.WaveSeconds(wave));
        Assert.Equal(60, BubblesGame.WaveSeconds(20));
    }

    [Fact]
    public void BaselinesFitEachGamesScoring()
    {
        Assert.InRange(BowlingGame.FairRound, 60, 300); // 300 is a perfect game
        Assert.InRange(TowerGame.FairRound, 5, 60); // blocks high
        Assert.InRange(PaperTossGame.FairRound, 2, 20); // baskets before the miss
        Assert.InRange(WhackGame.FairRound, 15, 150); // a 30-second round, up to 15 points a whack
        Assert.InRange(PinballGame.FairRound, 500, 5000); // 5,000 in a game is the "Pinball wizard" achievement
        Assert.InRange(BlockfallGame.FairRound, 1000, 10000); // ten single lines at level 1 are 1,000
        Assert.InRange(FishingGame.FairRound, 40, 400); // a few fish; a golden trout alone is 100 to 200
        Assert.InRange(BubblesGame.FairRound, 100, 800); // a first wave popped clean is 57 before combos and bonus
    }

    [Theory]
    [MemberData(nameof(Games))]
    public void AHardComputerRivalAimsAboutAtTheBaselineAndFinishesWithinTheRound(string id, int baseline, double seconds)
    {
        var rival = new CpuRival(3, baseline, false, seconds, new Random(7));
        Assert.InRange(rival.Target, (int)Math.Floor(baseline * 0.87), (int)Math.Ceiling(baseline * 1.13));
        double elapsed = 0;
        while (!rival.Done && elapsed < seconds * 1.3)
        {
            rival.Tick(0.25);
            elapsed += 0.25;
        }
        Assert.True(rival.Done, $"{id}: the rival was still playing after {elapsed:0} s of a {seconds:0} s round");
    }
}

public class BlockfallClearAnimationTests
{
    [Fact]
    public void RowDropsCountTheFullRowsBelowEachRow()
    {
        // rows from the top: empty, full, half, full (2 columns)
        var cells = new[,] { { -1, -1 }, { 0, 1 }, { 2, -1 }, { 3, 3 } };
        var full = new bool[4];
        var drops = BlockfallGame.RowDrops(cells, full);
        Assert.Equal(new[] { false, true, false, true }, full);
        Assert.Equal(new[] { 2, 0, 1, 0 }, drops);
    }

    [Fact]
    public void AWellWithNoFullRowsDropsNothing()
    {
        var cells = new[,] { { -1, 0 }, { 0, -1 } };
        var full = new bool[2];
        Assert.Equal(new[] { 0, 0 }, BlockfallGame.RowDrops(cells, full));
        Assert.DoesNotContain(true, full);
    }
}
