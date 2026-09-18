using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using DeskArcade.Engine;
using Xunit;

namespace DeskArcade.Tests;

public class UpdateCheckerTests
{
    [Theory]
    [InlineData("v1.3.0", "1.3.0")]
    [InlineData("1.2.1+9e8e393", "1.2.1")]
    [InlineData("1.3", "1.3.0")]
    [InlineData(" V2.0.5-beta ", "2.0.5")]
    public void ParsesReleaseTags(string tag, string expected) =>
        Assert.Equal(Version.Parse(expected), UpdateChecker.Parse(tag));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    public void RejectsNonVersions(string? tag) => Assert.Null(UpdateChecker.Parse(tag));
}

public class StatsTests
{
    static string TempPath() => Path.Combine(Path.GetTempPath(), "deskarcade-tests", Guid.NewGuid() + ".json");

    [Fact]
    public void AchievementUnlocksOnceAtTarget()
    {
        var stats = Stats.Load(TempPath());
        var unlocked = new List<string>();
        stats.Unlocked += a => unlocked.Add(a.Id);

        stats.Add("hoops.baskets", 99);
        Assert.DoesNotContain("hoops-100", unlocked);
        stats.Add("hoops.baskets");
        stats.Add("hoops.baskets");

        Assert.Equal(new[] { "hoops-100" }, unlocked);
        Assert.True(stats.IsUnlocked("hoops-100"));
        Assert.Equal(101, stats.Get("hoops.baskets"));
    }

    [Fact]
    public void MaxKeepsTheHighestValue()
    {
        var stats = Stats.Load(TempPath());
        stats.Max("hoops.streak", 7);
        stats.Max("hoops.streak", 3);
        Assert.Equal(7, stats.Get("hoops.streak"));
    }

    [Fact]
    public void TimePlayedFeedsGeneralCounters()
    {
        var stats = Stats.Load(TempPath());
        stats.AddTime("hoops", 20 * 60);
        stats.AddTime("golf", 45);
        Assert.Equal(20, stats.Get("play.minutes"));
        Assert.Equal(2, stats.Get("play.games"));
        Assert.Equal(20 * 60 + 45, stats.TotalSeconds, 3);
    }

    [Fact]
    public void SaveAndLoadRoundTrip()
    {
        string path = TempPath();
        var stats = Stats.Load(path);
        stats.Add("bricks.broken", 500); // unlocks and saves
        stats.AddTime("bricks", 12.5);
        stats.Save();

        var again = Stats.Load(path);
        Assert.Equal(500, again.Get("bricks.broken"));
        Assert.True(again.IsUnlocked("bricks-500"));
        Assert.Equal(12.5, again.SecondsPlayed("bricks"), 3);
    }

    [Fact]
    public void AchievementIdsAreUniqueAndCountersNamespaced()
    {
        var ids = Achievements.All.Select(a => a.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(Achievements.All, a =>
        {
            Assert.Contains('.', a.Counter);
            Assert.True(a.Target > 0);
        });
    }
}

public class LocTests
{
    [Fact]
    public void TranslatesAndFallsBackToEnglish()
    {
        try
        {
            L.Apply("ru");
            Assert.Equal("ru", L.Code);
            Assert.Equal("Выход", L.T("Exit"));
            Assert.Equal("not a known string", L.T("not a known string"));
            Assert.Equal("Рекорд 42", L.F("Best {0}", 42));

            L.Apply("uz");
            Assert.Equal("Chiqish", L.T("Exit"));
        }
        finally
        {
            L.Apply("en");
        }
        Assert.Equal("Exit", L.T("Exit"));
        Assert.Equal("Best 3.5", L.F("Best {0}", 3.5)); // invariant formatting
    }
}

public class TranslationCoverageTests
{
    static readonly Regex Call = new(@"L\.[TF]\(\s*""((?:[^""\\]|\\.)*)""");
    static readonly Regex Title = new(@"override string Title => ""((?:[^""\\]|\\.)*)""");

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DeskArcade.csproj"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    static IEnumerable<string> UsedKeys()
    {
        foreach (string file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file).StartsWith("Strings", StringComparison.Ordinal)) continue;
            string text = File.ReadAllText(file);
            foreach (Match m in Call.Matches(text)) yield return m.Groups[1].Value;
            foreach (Match m in Title.Matches(text)) yield return m.Groups[1].Value;
        }
        foreach (var a in Achievements.All)
        {
            yield return a.Title;
            yield return a.Description;
        }
    }

    // strings made only of numbers, placeholders and symbols ("+{0}") need no translation
    static bool NeedsTranslation(string key) => Regex.IsMatch(Regex.Replace(key, @"\{\d+\}", ""), @"\p{L}");

    [Fact]
    public void EveryUiStringIsTranslated()
    {
        var keys = UsedKeys().Where(NeedsTranslation).Distinct().ToList();
        Assert.NotEmpty(keys);
        Assert.Empty(keys.Where(k => !Strings.Uzbek.ContainsKey(k)).Select(k => "uz: " + k));
        Assert.Empty(keys.Where(k => !Strings.Russian.ContainsKey(k)).Select(k => "ru: " + k));
    }

    [Fact]
    public void TranslationsKeepPlaceholders()
    {
        foreach (var table in new[] { Strings.Uzbek, Strings.Russian })
        {
            foreach (var (english, translated) in table)
            {
                var expected = Regex.Matches(english, @"\{\d+\}").Select(m => m.Value).OrderBy(s => s);
                var actual = Regex.Matches(translated, @"\{\d+\}").Select(m => m.Value).OrderBy(s => s);
                Assert.True(expected.SequenceEqual(actual), $"placeholders differ: {english} -> {translated}");
            }
        }
    }
}

public class PlatformsTests
{
    static readonly Rect Arena = new(0, 0, 1920, 1040);

    [Fact]
    public void BallLandsOnVisibleWindowTop()
    {
        var plats = new Platforms();
        plats.Refresh(new List<(IntPtr, Rect)> { ((IntPtr)1, new Rect(400, 500, 600, 400)) }, Arena);

        Assert.True(plats.FindLanding(700, 490, 510, out var hit));
        Assert.Equal(500, hit.Y);
        Assert.False(plats.FindLanding(1200, 490, 510, out _)); // beside the window
        Assert.False(plats.FindLanding(700, 505, 520, out _)); // already below the top: one-way
    }

    [Fact]
    public void WindowInFrontHidesThePartOfTheTopItCovers()
    {
        var plats = new Platforms();
        plats.Refresh(new List<(IntPtr, Rect)>
        {
            ((IntPtr)2, new Rect(600, 300, 200, 700)), // topmost, covers x 600-800 at y 500
            ((IntPtr)1, new Rect(400, 500, 600, 400)),
        }, Arena);

        Assert.False(plats.FindLanding(700, 490, 510, out _));
        Assert.True(plats.FindLanding(500, 490, 510, out _));
        Assert.True(plats.FindLanding(900, 490, 510, out _));
    }

    [Fact]
    public void ReportsHowFarAWindowMoved()
    {
        var plats = new Platforms();
        plats.Refresh(new List<(IntPtr, Rect)> { ((IntPtr)1, new Rect(400, 500, 600, 400)) }, Arena);
        plats.Refresh(new List<(IntPtr, Rect)> { ((IntPtr)1, new Rect(430, 480, 600, 400)) }, Arena);
        var d = plats.DeltaOf((IntPtr)1);
        Assert.Equal(30, d.X, 3);
        Assert.Equal(-20, d.Y, 3);
    }
}

public class DraughtsTests
{
    static DeskArcade.Games.Draughts Empty(int turn = 1)
    {
        var d = DeskArcade.Games.Draughts.Decode(new string('.', 64) + (turn > 0 ? "|w|0" : "|b|0"))!;
        return d;
    }

    [Fact]
    public void OpeningHasSevenMovesAndTwelvePiecesEach()
    {
        var d = DeskArcade.Games.Draughts.New();
        Assert.Equal(12, d.Count(1));
        Assert.Equal(12, d.Count(-1));
        Assert.Equal(7, d.LegalMoves().Count);
    }

    [Fact]
    public void CaptureIsCompulsoryAndChains()
    {
        var d = Empty();
        d.Board[7 * 8 + 0] = 1;   // white man at a1-ish corner (row 7, col 0)
        d.Board[6 * 8 + 1] = -1;  // black man to jump
        d.Board[4 * 8 + 3] = -1;  // and a second one after landing on row 5, col 2
        d.Board[7 * 8 + 6] = 1;   // another white man with a quiet move available
        var moves = d.LegalMoves();
        var only = Assert.Single(moves);
        Assert.Equal(new[] { 56, 42, 28 }, only);
        var captured = d.Apply(only);
        Assert.Equal(2, captured.Count);
        Assert.Equal(0, d.Count(-1));
        Assert.Equal(1, d.Winner); // black has nothing left to move
    }

    [Fact]
    public void ManIsCrownedOnTheFarRow()
    {
        var d = Empty();
        d.Board[1 * 8 + 2] = 1;
        d.Apply(new[] { 10, 1 });
        Assert.Equal(2, d.Board[1]);
    }

    [Fact]
    public void KingsShufflingIsADraw()
    {
        var d = Empty();
        d.Board[63 - 7] = 2;   // white king, row 7 col 0
        d.Board[1] = -2;       // black king, row 0 col 1
        for (int i = 0; i < DeskArcade.Games.Draughts.DrawPlies; i++)
        {
            Assert.False(d.IsDraw);
            d.Apply(d.LegalMoves()[0]); // the two kings wander without ever meeting
        }
        Assert.True(d.IsDraw);
        Assert.Equal(d.Quiet, DeskArcade.Games.Draughts.Decode(d.Encode())!.Quiet);
    }

    [Fact]
    public void EncodeRoundTrips()
    {
        var d = DeskArcade.Games.Draughts.New();
        d.Apply(d.LegalMoves()[0]);
        var back = DeskArcade.Games.Draughts.Decode(d.Encode())!;
        Assert.Equal(d.Encode(), back.Encode());
        Assert.Equal(-1, back.Turn);
        Assert.Equal(1, back.Ply);
    }

    [Fact]
    public void ComputerPlaysALegalMove()
    {
        var d = DeskArcade.Games.Draughts.New();
        var m = d.BestMove(new Random(1));
        Assert.True(d.IsLegal(m));
    }
}

public class ChessRulesTests
{
    /// <summary>Builds a position from the piece-placement part of a FEN string.</summary>
    static DeskArcade.Games.ChessRules Fen(string placement, bool white, int castle, int ep = -1)
    {
        var board = new System.Text.StringBuilder();
        foreach (char ch in placement)
            if (char.IsDigit(ch)) board.Append('.', ch - '0');
            else if (ch != '/') board.Append(ch);
        return DeskArcade.Games.ChessRules.Decode($"{board}|{(white ? "w" : "b")}|{castle}|{ep}|0|0")!;
    }

    static long Perft(DeskArcade.Games.ChessRules c, int depth)
    {
        if (depth == 0) return 1;
        long n = 0;
        foreach (var m in c.LegalMoves())
        {
            var next = c.Clone();
            next.Apply(m);
            n += Perft(next, depth - 1);
        }
        return n;
    }

    [Theory]
    [InlineData(1, 20)]
    [InlineData(2, 400)]
    [InlineData(3, 8902)]
    public void PerftFromTheStart(int depth, long nodes) => Assert.Equal(nodes, Perft(DeskArcade.Games.ChessRules.New(), depth));

    [Theory]
    [InlineData(1, 48)]
    [InlineData(2, 2039)]
    [InlineData(3, 97862)]
    public void PerftKiwipeteCoversCastlingAndEnPassant(int depth, long nodes) =>
        Assert.Equal(nodes, Perft(Fen("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R", true, 15), depth));

    [Theory]
    [InlineData(1, 14)]
    [InlineData(2, 191)]
    [InlineData(3, 2812)]
    [InlineData(4, 43238)]
    public void PerftEndgameCoversEnPassantPins(int depth, long nodes) =>
        Assert.Equal(nodes, Perft(Fen("8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8", true, 0), depth));

    [Fact]
    public void FoolsMateIsAWinForBlack()
    {
        var c = DeskArcade.Games.ChessRules.New();
        foreach (var (from, to) in new[] { ("f2", "f3"), ("e7", "e5"), ("g2", "g4"), ("d8", "h4") })
            c.Apply(new[] { DeskArcade.Games.ChessRules.Sq(from), DeskArcade.Games.ChessRules.Sq(to) });
        Assert.Equal(-1, c.Result);
        Assert.Equal(DeskArcade.Games.ChessRules.Sq("e1"), c.Alert);
    }

    [Fact]
    public void PawnPromotesToAQueenAndEncodingRoundTrips()
    {
        var c = Fen("8/P6k/8/8/8/8/8/K7", true, 0);
        c.Apply(new[] { DeskArcade.Games.ChessRules.Sq("a7"), DeskArcade.Games.ChessRules.Sq("a8") });
        Assert.Equal(DeskArcade.Games.ChessRules.Queen, c.Board[0]);
        Assert.Equal(c.Encode(), DeskArcade.Games.ChessRules.Decode(c.Encode())!.Encode());
    }

    [Fact]
    public void BareKingsAreADrawAndTheCpuTakesAFreeQueen()
    {
        Assert.Equal(2, Fen("8/8/8/4k3/8/8/8/K7", true, 0).Result);
        var c = Fen("4k3/8/8/3q4/8/8/8/3RK3", true, 0);
        for (int seed = 0; seed < 5; seed++)
            Assert.Equal(DeskArcade.Games.ChessRules.Sq("d5"), c.BestMove(new Random(seed))[1]);
    }
}

public class LaunchPathTests
{
    [Fact]
    public void AnAppImageIsLaunchedByItsFileNotItsTemporaryMount()
    {
        string file = Path.GetTempFileName();
        string? before = Environment.GetEnvironmentVariable("APPIMAGE");
        try
        {
            Environment.SetEnvironmentVariable("APPIMAGE", file);
            Assert.Equal(file, Program.LaunchPath);
            Environment.SetEnvironmentVariable("APPIMAGE", file + ".gone"); // stale variable: fall back
            Assert.NotEqual(file + ".gone", Program.LaunchPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("APPIMAGE", before);
            File.Delete(file);
        }
    }
}
