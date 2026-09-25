using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Avalonia;
using DeskArcade;
using DeskArcade.Games;
using Xunit;
using Top = DeskArcade.Engine.Platform;

namespace DeskArcade.Tests;

/// <summary>The pet's life over the weeks: growing up, the favourite nap spot and the pet's own voice volume.</summary>
public class PetLifeTests
{
    static readonly Rect Arena = new(0, 0, 1920, 1040);
    static readonly Rect NoHud = new(-100, -100, 1, 1);

    // ------------------------------------------------------------------ growing up

    [Fact]
    public void AStageNeedsBothTheDaysAndThePlay()
    {
        Assert.Equal(PetLife.Young, PetLife.StageOf(0, 0));
        Assert.Equal(PetLife.Young, PetLife.StageOf(365, 0));                               // old but ignored
        Assert.Equal(PetLife.Young, PetLife.StageOf(0, 100_000));                           // played with a lot, but only today
        Assert.Equal(PetLife.Young, PetLife.StageOf(PetLife.GrownDays - 1, PetLife.GrownPlay));
        Assert.Equal(PetLife.Young, PetLife.StageOf(PetLife.GrownDays, PetLife.GrownPlay - 1));
        Assert.Equal(PetLife.Grown, PetLife.StageOf(PetLife.GrownDays, PetLife.GrownPlay));
        Assert.Equal(PetLife.Grown, PetLife.StageOf(PetLife.WiseDays - 1, PetLife.WisePlay * 10));
        Assert.Equal(PetLife.Grown, PetLife.StageOf(PetLife.WiseDays * 10, PetLife.WisePlay - 1));
        Assert.Equal(PetLife.Wise, PetLife.StageOf(PetLife.WiseDays, PetLife.WisePlay));
        Assert.True(PetLife.WiseDays > PetLife.GrownDays && PetLife.WisePlay > PetLife.GrownPlay);
        Assert.Equal(3, PetLife.StageNames.Length);
    }

    [Fact]
    public void EachStageIsALittleBiggerAndStaysWithinTheHitArea()
    {
        double young = PetLife.ScaleOf(PetLife.Young), grown = PetLife.ScaleOf(PetLife.Grown), wise = PetLife.ScaleOf(PetLife.Wise);
        Assert.Equal(1.0, grown);
        Assert.True(young < grown && grown < wise);
        for (int stage = -1; stage <= 5; stage++) Assert.InRange(PetLife.ScaleOf(stage), 0.9, 1.08);
    }

    [Fact]
    public void NextStageSaysWhatItNeeds()
    {
        Assert.Equal((PetLife.GrownDays, PetLife.GrownPlay), PetLife.NextStage(PetLife.Young));
        Assert.Equal((PetLife.WiseDays, PetLife.WisePlay), PetLife.NextStage(PetLife.Grown));
        Assert.Null(PetLife.NextStage(PetLife.Wise));
    }

    [Fact]
    public void AgeCountsCalendarDaysAndNeverGoesNegative()
    {
        var adopted = new DateTime(2026, 9, 1, 23, 30, 0);
        Assert.Equal(0, PetLife.AgeDays(adopted, new DateTime(2026, 9, 1, 23, 59, 0)));
        Assert.Equal(1, PetLife.AgeDays(adopted, new DateTime(2026, 9, 2, 0, 1, 0)));
        Assert.Equal(30, PetLife.AgeDays(adopted, new DateTime(2026, 10, 1)));
        Assert.Equal(0, PetLife.AgeDays(adopted, new DateTime(2026, 8, 1))); // the clock went back
    }

    [Fact]
    public void OldPlayCountsTowardGrowingUp()
    {
        Assert.Equal(0, PetLife.PlayFromTotals(0, 0, 0));
        Assert.Equal(10 * PetLife.PetPoints + 4 * PetLife.TreatPoints + 2 * PetLife.FetchPoints, PetLife.PlayFromTotals(10, 4, 2));
        Assert.Equal(0, PetLife.PlayFromTotals(-5, 0, 0));
        Assert.True(PetLife.FetchPoints > PetLife.TreatPoints && PetLife.TreatPoints > PetLife.PetPoints);
    }

    [Fact]
    public void AWisePetLearnsASecondTrickOfItsOwn()
    {
        var everyday = PetGame.Kinds.SelectMany(PetGame.HabitsOf).Concat(new[] { "eat", "morning", "beg", "settle", "swat", "stalk", "chatter", "puff", "thump", "pant" }).ToHashSet();
        var firsts = PetGame.Kinds.Select(k => PetGame.TrickFor(k).Name).ToHashSet();
        var seconds = PetGame.Kinds.Select(k => PetLife.SecondTrickFor(k).Name).ToList();
        Assert.Equal(seconds.Count, seconds.Distinct().Count());
        foreach (var kind in PetGame.Kinds)
        {
            var (name, length) = PetLife.SecondTrickFor(kind);
            Assert.True(length > 0);
            Assert.DoesNotContain(name, everyday);
            Assert.DoesNotContain(name, firsts);
            Assert.Equal(length, PetGame.ActLength(name));      // a visitor over the LAN is posed for the right length
            Assert.True(name.Length <= 16 && name.All(char.IsAsciiLetterLower), $"{name} would not cross the LAN");
            Assert.True(PetGame.TryDecodeVisit(PetGame.EncodeVisit(new PetGame.Visit(kind, 0.5, 0.5, 1, PetGame.Mode.Sit, name)), out _));
        }
        Assert.Equal("playdead", PetLife.SecondTrickFor("dog").Name);
        Assert.Equal("wave", PetLife.SecondTrickFor("cat").Name);
        Assert.Equal(0, PetLife.SecondTrickSeconds("stretch"));
    }

    [Fact]
    public void GrowingUpHasAchievements()
    {
        var stages = Achievements.All.Where(a => a.Counter == PetLife.StageAchievementCounter).ToList();
        Assert.Equal(2, stages.Count);
        Assert.All(stages, a => Assert.Equal("pet", a.GameId));
        Assert.Equal(new long[] { PetLife.Grown, PetLife.Wise }, stages.Select(a => a.Target).OrderBy(t => t).ToArray());
    }

    // ------------------------------------------------------------------ nap spots

    [Theory]
    [InlineData("Code", "code")]
    [InlineData("  WindowsTerminal.exe ", "windowsterminal")]
    [InlineData("firefox-bin", "firefox-bin")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    [InlineData("<>|", null)]
    public void SpotsAreKeptByAppName(string? process, string? key) => Assert.Equal(key, PetLife.SpotKey(process));

    [Fact]
    public void ASpotKeyIsShort() => Assert.Equal(40, PetLife.SpotKey(new string('a', 100))!.Length);

    [Fact]
    public void TheFavouriteIsTheMostNappedSpotOnceItHasEnoughNaps()
    {
        var tally = new Dictionary<string, int>();
        Assert.Null(PetLife.FavouriteSpot(tally));
        Assert.Null(PetLife.FavouriteSpot(null));
        for (int i = 0; i < PetLife.FavouriteAfter - 1; i++) PetLife.RecordNap(tally, "code");
        Assert.Null(PetLife.FavouriteSpot(tally)); // not yet a habit
        PetLife.RecordNap(tally, "code");
        Assert.Equal("code", PetLife.FavouriteSpot(tally));
        for (int i = 0; i < PetLife.FavouriteAfter; i++) PetLife.RecordNap(tally, "chrome");
        Assert.Equal("chrome", PetLife.FavouriteSpot(tally)); // a tie goes to the first name
        PetLife.RecordNap(tally, "code");
        Assert.Equal("code", PetLife.FavouriteSpot(tally));
    }

    [Fact]
    public void TheTallyStaysSmallAndOldFavouritesFade()
    {
        var tally = new Dictionary<string, int> { ["old"] = 50 };
        for (int i = 0; i < 20; i++) PetLife.RecordNap(tally, "app" + i);
        Assert.Equal(PetLife.KeepSpots, tally.Count);
        Assert.Contains("old", tally.Keys);        // the least-napped went, not the favourite
        Assert.Contains("app19", tally.Keys);      // nor the one just napped on

        var loyal = new Dictionary<string, int> { ["old"] = PetLife.HalveAt - 1 };
        PetLife.RecordNap(loyal, "new");
        Assert.True(loyal.Values.Sum() < PetLife.HalveAt);
        Assert.Equal((PetLife.HalveAt - 1) / 2, loyal["old"]);
        Assert.Equal(1, loyal["new"]);
        for (int i = 0; i < 400; i++) PetLife.RecordNap(loyal, "new");
        Assert.Equal("new", PetLife.FavouriteSpot(loyal)); // a new favourite can take over
    }

    static string? AppOf(IntPtr hwnd) => hwnd switch { 1 => "code", 2 => "chrome", 3 => "code", _ => null };

    [Fact]
    public void ItNapsOnTheWidestWindowOfItsFavouriteApp()
    {
        var tops = new List<Top>
        {
            new((IntPtr)2, 500, 100, 900),   // chrome
            new((IntPtr)1, 400, 1000, 1200), // code, narrow
            new((IntPtr)3, 600, 1000, 1800), // code, wide
        };
        Assert.True(PetLife.TryNapSpot(tops, AppOf, "code", Arena, NoHud, 300, 46, 22, out var top, out double x));
        Assert.Equal((IntPtr)3, top.Hwnd);
        Assert.Equal(1000 + 22 + 10, x); // the end nearest the pet, a little way in from the edge

        Assert.True(PetLife.TryNapSpot(tops, AppOf, "chrome", Arena, NoHud, 300, 46, 22, out top, out x));
        Assert.Equal((IntPtr)2, top.Hwnd);
        Assert.Equal(300, x); // right above it
    }

    [Fact]
    public void WithoutItsWindowOrRoomItNapsWhereItIs()
    {
        var tops = new List<Top> { new((IntPtr)2, 500, 100, 900) };
        Assert.False(PetLife.TryNapSpot(tops, AppOf, "code", Arena, NoHud, 300, 46, 22, out _, out _)); // not open
        Assert.False(PetLife.TryNapSpot(new List<Top>(), AppOf, "code", Arena, NoHud, 300, 46, 22, out _, out _));
        // too near the top of the screen for the pet to stand on
        Assert.False(PetLife.TryNapSpot(new List<Top> { new((IntPtr)1, 50, 100, 900) }, AppOf, "code", Arena, NoHud, 300, 46, 22, out _, out _));
        // too narrow to lie on
        Assert.False(PetLife.TryNapSpot(new List<Top> { new((IntPtr)1, 500, 100, 150) }, AppOf, "code", Arena, NoHud, 300, 46, 22, out _, out _));
    }

    [Fact]
    public void ItKeepsClearOfTheScoreboard()
    {
        var tops = new List<Top> { new((IntPtr)1, 500, 100, 900) };
        var hud = new Rect(200, 440, 200, 80); // over the spot right above the pet
        Assert.True(PetLife.TryNapSpot(tops, AppOf, "code", Arena, hud, 300, 46, 22, out _, out double x));
        Assert.False(hud.Contains(new Point(x, 500 - 23)));
        var everywhere = new Rect(0, 400, 1920, 200);
        Assert.False(PetLife.TryNapSpot(tops, AppOf, "code", Arena, everywhere, 300, 46, 22, out _, out _));
    }

    // ------------------------------------------------------------------ the pet's voice

    [Fact]
    public void PetVolumeLevelsRunFromOffToLoud()
    {
        Assert.Equal(4, PetLife.VolumeNames.Length);
        Assert.Equal(0, PetLife.VolumeFactor(0));
        Assert.Equal(1, PetLife.VolumeFactor(PetLife.DefaultVolume));
        for (int level = 1; level < PetLife.VolumeNames.Length; level++)
            Assert.True(PetLife.VolumeFactor(level) > PetLife.VolumeFactor(level - 1));
        Assert.Equal(1, PetLife.VolumeFactor(-3)); // a hand-edited settings file plays at the normal level
        Assert.Equal(1, PetLife.VolumeFactor(99));
    }

    [Fact]
    public void CallsSoftenLateInTheEvening()
    {
        Assert.Equal(1, PetLife.EveningFactor(new TimeOnly(10, 0)));
        Assert.Equal(1, PetLife.EveningFactor(new TimeOnly(19, 59)));
        Assert.Equal(1, PetLife.EveningFactor(new TimeOnly(6, 0)));
        Assert.Equal((1 + PetLife.NightFactor) / 2, PetLife.EveningFactor(new TimeOnly(21, 0)), 6);
        Assert.Equal(PetLife.NightFactor, PetLife.EveningFactor(new TimeOnly(22, 0)));
        Assert.Equal(PetLife.NightFactor, PetLife.EveningFactor(new TimeOnly(3, 0)));
        double last = 1;
        for (int m = 20 * 60; m < 22 * 60; m += 10)
        {
            double f = PetLife.EveningFactor(new TimeOnly(m / 60, m % 60));
            Assert.True(f <= last);
            last = f;
        }
        // it follows the pet's own idea of night
        for (int h = 0; h < 24; h++)
            if (PetGame.IsNight(new TimeOnly(h, 30))) Assert.Equal(PetLife.NightFactor, PetLife.EveningFactor(new TimeOnly(h, 30)));

        Assert.Equal(0, PetLife.VoiceGain(0, new TimeOnly(12, 0)));
        Assert.Equal(PetLife.VolumeFactor(3) * PetLife.NightFactor, PetLife.VoiceGain(3, new TimeOnly(23, 0)));
    }

    // ------------------------------------------------------------------ settings

    [Fact]
    public void OlderSettingsLoadWithTheDefaults()
    {
        var settings = JsonSerializer.Deserialize<Settings>("{\"PetKind\":\"dog\",\"Volume\":0.5}")!;
        Assert.Equal("dog", settings.PetKind);
        Assert.Equal(PetLife.DefaultVolume, settings.PetVolume);
        Assert.Empty(settings.PetAdopted);
        Assert.Empty(settings.PetNapSpots);

        settings.PetVolume = 1;
        settings.PetAdopted["cat"] = new DateTime(2026, 9, 1);
        settings.PetNapSpots["code"] = 4;
        var back = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!;
        Assert.Equal(1, back.PetVolume);
        Assert.Equal(new DateTime(2026, 9, 1), back.PetAdopted["cat"]);
        Assert.Equal("code", PetLife.FavouriteSpot(back.PetNapSpots));
    }
}
