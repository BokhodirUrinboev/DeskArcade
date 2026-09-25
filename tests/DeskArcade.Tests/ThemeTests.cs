using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using DeskArcade.Engine;
using Xunit;

namespace DeskArcade.Tests;

public class ThemePaletteTests
{
    public static IEnumerable<object[]> Every() => Themes.All.Select(t => new object[] { t });

    [Fact]
    public void ThereAreThirteenThemesWithDistinctIdsAndNames()
    {
        Assert.Equal(13, Themes.All.Count);
        Assert.Equal(Themes.All.Count, Themes.All.Select(t => t.Id).Distinct().Count());
        Assert.Equal(Themes.All.Count, Themes.All.Select(t => t.Name).Distinct().Count());
        Assert.All(Themes.All, t => Assert.Matches("^[a-z]+$", t.Id));
        foreach (var expected in new[] { "classic", "neon", "retro", "halloween", "winter", "ocean", "forest", "sunset", "candy", "mono", "spring", "autumn", "midnight" })
            Assert.Contains(Themes.All, t => t.Id == expected);
    }

    [Theory]
    [MemberData(nameof(Every))]
    public void EveryColourIsOpaqueAndEveryRoleIsFilled(Theme t)
    {
        var solid = new[] { t.Mine, t.Rival, t.Puck, t.PuckRim, t.Line, t.Ball, t.BallSeam, t.GolfBall, t.Accent, t.Gold, t.HudBack, t.HudFront, t.Ink };
        Assert.All(solid, c => Assert.Equal(255, c.A));
        Assert.NotEmpty(t.Confetti);
        Assert.All(t.Confetti, c => Assert.Equal(255, c.A));
        foreach (var optional in new[] { t.BoardLight, t.BoardDark, t.BoardFrame, t.Felt, t.CardBack })
            if (optional is { } c) Assert.Equal(255, c.A);
        Assert.False(string.IsNullOrWhiteSpace(t.Mood));
        Assert.True(Enum.IsDefined(t.Decor));
        Assert.NotEqual(t.HudBack, t.HudFront);
        Assert.NotEqual(t.Mine, t.Rival);
    }

    [Fact]
    public void ClassicKeepsTheGamesOwnBoardsAndTablesAndTheOthersDressThem()
    {
        var c = Themes.Classic;
        Assert.Null(c.BoardLight);
        Assert.Null(c.BoardDark);
        Assert.Null(c.BoardFrame);
        Assert.Null(c.Felt);
        Assert.Null(c.CardBack);
        Assert.Equal(Themes.ClassicGold, c.Gold);
        foreach (var t in Themes.All.Where(t => t != c))
        {
            Assert.NotNull(t.BoardLight);
            Assert.NotNull(t.BoardDark);
            Assert.NotNull(t.BoardFrame);
            Assert.NotNull(t.Felt);
            Assert.NotNull(t.CardBack);
            Assert.NotEqual(t.BoardLight, t.BoardDark);
        }
    }

    [Theory]
    [MemberData(nameof(Every))]
    public void ScoreboardTextReadsAgainstItsBackground(Theme t)
    {
        // WCAG contrast; 4.5 is the usual bar for body text
        Assert.True(Contrast(t.HudBack, t.HudFront) >= 4.5, $"{t.Id}: contrast {Contrast(t.HudBack, t.HudFront):0.0}");
        Assert.True(Contrast(t.HudBack, t.Gold) >= 3, $"{t.Id}: gold contrast {Contrast(t.HudBack, t.Gold):0.0}");
    }

    static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    static double Luminance(Color c)
    {
        static double Lin(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    [Fact]
    public void EachThemeHasItsDecorAndMonoAndClassicHaveNone()
    {
        Assert.Equal(Decor.None, Themes.Classic.Decor);
        Assert.Equal(Decor.None, Themes.Mono.Decor);
        Assert.Equal(Decor.Snow, Themes.Winter.Decor);
        Assert.Equal(Decor.Bubbles, Themes.Ocean.Decor);
        Assert.Equal(Decor.Fireflies, Themes.Forest.Decor);
        Assert.Equal(Decor.Embers, Themes.Sunset.Decor);
        Assert.Equal(Decor.Petals, Themes.Candy.Decor);
        Assert.Equal(Decor.Petals, Themes.Spring.Decor);
        Assert.Equal(Decor.Leaves, Themes.Autumn.Decor);
        Assert.Equal(Decor.Stars, Themes.Midnight.Decor);
        Assert.Equal(Decor.Embers, Themes.Halloween.Decor);
        Assert.Equal(Decor.Confetti, Themes.Neon.Decor);
        Assert.Equal(Decor.Stars, Themes.Retro.Decor);
    }

    [Fact]
    public void ChoicesListSeasonalFirstThenEveryTheme()
    {
        var choices = Themes.Choices.ToList();
        Assert.Equal(("seasonal", "Seasonal"), choices[0]);
        Assert.Equal(Themes.All.Select(t => t.Id), choices.Skip(1).Select(c => c.Id));
        Assert.Equal(Themes.All.Select(t => t.Name), choices.Skip(1).Select(c => c.Name));
    }

    [Fact]
    public void ThemedTurnsTheClassicGoldIntoTheCurrentThemesGoldOnly()
    {
        Assert.Equal(Themes.Current.Gold, Themes.Themed(Themes.ClassicGold));
        Assert.Equal(Colors.White, Themes.Themed(Colors.White));
        Assert.Equal(Themes.Ocean.Accent, Themes.Themed(Themes.Ocean.Accent));
    }
}

public class SeasonalThemeTests
{
    [Theory]
    [InlineData(1, 15, "winter")]
    [InlineData(2, 10, "midnight")]
    [InlineData(3, 1, "spring")]
    [InlineData(4, 20, "spring")]
    [InlineData(5, 31, "spring")]
    [InlineData(6, 1, "ocean")]
    [InlineData(7, 15, "ocean")]
    [InlineData(8, 31, "ocean")]
    [InlineData(9, 1, "autumn")]
    [InlineData(10, 23, "autumn")]
    [InlineData(10, 24, "halloween")]
    [InlineData(10, 31, "halloween")]
    [InlineData(11, 1, "forest")]
    [InlineData(11, 30, "forest")]
    [InlineData(12, 1, "winter")]
    [InlineData(12, 31, "winter")]
    public void TheCalendarPicksATheme(int month, int day, string id)
    {
        Assert.Equal(id, Themes.Seasonal(new DateTime(2026, month, day)).Id);
        Assert.Equal(id, Themes.Resolve("seasonal", new DateTime(2026, month, day)).Id);
    }

    [Fact]
    public void EveryDayOfTheYearResolvesToSomeTheme()
    {
        for (var d = new DateTime(2026, 1, 1); d.Year == 2026; d = d.AddDays(1))
            Assert.Contains(Themes.Seasonal(d), Themes.All);
    }

    [Fact]
    public void AnIdResolvesToItsThemeAndTheRestToClassic()
    {
        foreach (var t in Themes.All) Assert.Same(t, Themes.Resolve(t.Id, DateTime.Today));
        Assert.Same(Themes.Classic, Themes.Resolve("no-such-theme", DateTime.Today));
        Assert.Same(Themes.Classic, Themes.Resolve(null, DateTime.Today));
    }
}

public class DecorModelTests
{
    static readonly Rect Box = new(100, 50, 1400, 800);
    static readonly List<Engine.Platform> Platforms = new()
    {
        new Engine.Platform((IntPtr)1, 400, 300, 900), new Engine.Platform((IntPtr)2, 620, 700, 1300),
    };

    public static IEnumerable<object[]> Kinds() => Enum.GetValues<Decor>().Where(k => k != Decor.None).Select(k => new object[] { k });

    static void Run(DecorModel m, double seconds, bool active)
    {
        for (double t = 0; t < seconds; t += 1.0 / 60) m.Update(1.0 / 60, active, Platforms);
    }

    static IEnumerable<DecorModel.Particle> Alive(DecorModel m) => Enumerable.Range(0, DecorModel.Max).Select(i => m[i]).Where(p => p.Alive);

    [Theory]
    [MemberData(nameof(Kinds))]
    public void ParticlesAppearWhileActiveAndNeverLeaveTheBox(Decor kind)
    {
        var m = new DecorModel(new Random(7)) { Box = Box };
        m.Reset(kind);
        int seen = 0;
        for (double t = 0; t < 40; t += 1.0 / 60)
        {
            m.Update(1.0 / 60, true, Platforms);
            foreach (var p in Alive(m))
            {
                seen++;
                Assert.InRange(p.P.X, Box.Left, Box.Right);
                Assert.InRange(p.P.Y, Box.Top, Box.Bottom);
                Assert.InRange(p.Alpha, 0, 1);
                Assert.True(p.Size > 0);
            }
            Assert.InRange(m.Count, 0, DecorModel.Max);
        }
        Assert.True(seen > 0);
        Assert.True(m.Busy);
        Assert.True(m.Count >= 5, $"{kind}: only {m.Count} on screen after 40 s");
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void GoesQuietWithinTwoSecondsOfTheOverlayGoingIdle(Decor kind)
    {
        var m = new DecorModel(new Random(3)) { Box = Box };
        m.Reset(kind);
        Run(m, 12, active: true);
        Assert.True(m.Busy);
        Run(m, 2, active: false);
        Assert.False(m.Busy);
        Assert.Equal(0, m.Count);
        Run(m, 1, active: false); // and nothing comes back while idle
        Assert.Equal(0, m.Count);
    }

    [Fact]
    public void NothingHappensWithoutADecor()
    {
        var m = new DecorModel(new Random(1)) { Box = Box };
        m.Reset(Decor.None);
        Assert.False(m.Update(0.5, true, Platforms));
        Assert.Equal(0, m.Count);
    }

    [Fact]
    public void SnowSettlesOnWindowTopsForAMomentAndThenGoes()
    {
        var m = new DecorModel(new Random(11)) { Box = Box };
        m.Reset(Decor.Snow);
        bool settledOnWindow = false, settledOnFloor = false;
        for (double t = 0; t < 60; t += 1.0 / 60)
        {
            m.Update(1.0 / 60, true, Platforms);
            foreach (var p in Alive(m).Where(p => p.Settled))
            {
                if (Math.Abs(p.P.Y + p.Size / 2 - 400) < 0.01 || Math.Abs(p.P.Y + p.Size / 2 - 620) < 0.01) settledOnWindow = true;
                if (Math.Abs(p.P.Y + p.Size / 2 - Box.Bottom) < 0.01) settledOnFloor = true;
                Assert.True(p.Rest < 4, "a flake sat for too long");
            }
        }
        Assert.True(settledOnWindow);
        Assert.True(settledOnFloor);
    }

    [Fact]
    public void ResetDropsEverythingAndSwitchesKind()
    {
        var m = new DecorModel(new Random(5)) { Box = Box };
        m.Reset(Decor.Confetti);
        Run(m, 5, active: true);
        Assert.True(m.Count > 0);
        m.Reset(Decor.Bubbles);
        Assert.Equal(0, m.Count);
        Assert.Equal(Decor.Bubbles, m.Kind);
        Assert.False(m.Busy);
    }

    [Fact]
    public void ATinyBoxStillKeepsEveryParticleInside()
    {
        var box = new Rect(10, 10, 60, 45);
        foreach (var kind in Enum.GetValues<Decor>().Where(k => k != Decor.None))
        {
            var m = new DecorModel(new Random(2)) { Box = box };
            m.Reset(kind);
            for (double t = 0; t < 20; t += 1.0 / 60)
            {
                m.Update(1.0 / 60, true, null);
                foreach (var p in Alive(m))
                {
                    Assert.InRange(p.P.X, box.Left, box.Right);
                    Assert.InRange(p.P.Y, box.Top, box.Bottom);
                }
            }
        }
    }
}
