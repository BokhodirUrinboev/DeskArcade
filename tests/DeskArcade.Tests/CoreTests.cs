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
    public void MinKeepsTheFewestAndTreatsUnsetAsNoRecord()
    {
        var stats = Stats.Load(TempPath());
        stats.Min("darts.fewest", 30);
        stats.Min("darts.fewest", 42);
        stats.Min("darts.fewest", 0); // not a result
        Assert.Equal(30, stats.Get("darts.fewest"));
        stats.Min("darts.fewest", 21);
        Assert.Equal(21, stats.Get("darts.fewest"));
        Assert.Equal(21, stats.Today("darts.fewest"));
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

    internal static string RepoRoot()
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
        foreach (var c in Daily.Pool) yield return c.Text;
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
    public void NoTranslationKeyIsDefinedTwice()
    {
        // the tables are indexer initializers, where a second ["key"] silently wins over the first
        foreach (var name in new[] { "Strings.Uzbek.cs", "Strings.Russian.cs" })
        {
            var keys = Regex.Matches(File.ReadAllText(Path.Combine(RepoRoot(), "src", name)), @"^\s*\[""((?:[^""\\]|\\.)*)""\]\s*=", RegexOptions.Multiline)
                .Select(m => m.Groups[1].Value);
            Assert.Empty(keys.GroupBy(k => k).Where(g => g.Count() > 1).Select(g => $"{name}: {g.Key}"));
        }
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
            // wander without ever offering a capture
            var quiet = d.LegalMoves().First(m =>
            {
                var next = d.Clone();
                next.Apply(m);
                return next.LegalMoves().All(r => { var c = next.Clone(); return c.Apply(r).Count == 0; });
            });
            d.Apply(quiet);
        }
        Assert.True(d.IsDraw);
        Assert.Equal(d.Quiet, DeskArcade.Games.Draughts.Decode(d.Encode())!.Quiet);
    }

    [Fact]
    public void MenCaptureBackwardsButOnlyStepForwards()
    {
        var d = Empty();
        d.Board[35] = 1;   // white man, row 4 col 3
        d.Board[44] = -1;  // black man behind it, row 5 col 4
        Assert.Equal(new[] { 35, 53 }, Assert.Single(d.LegalMoves()));

        var quiet = Empty();
        quiet.Board[35] = 1;
        Assert.Equal(new[] { 26, 28 }, quiet.LegalMoves().Select(m => m[1]).OrderBy(x => x)); // forward only
    }

    [Fact]
    public void KingsFlyAlongTheDiagonal()
    {
        var d = Empty();
        d.Board[56] = 2; // row 7 col 0: the long diagonal is free
        var moves = d.LegalMoves();
        Assert.Equal(7, moves.Count);
        Assert.Contains(moves, m => m.SequenceEqual(new[] { 56, 7 }));
    }

    [Fact]
    public void AKingCapturesFromAfarAndChoosesWhereToLand()
    {
        var d = Empty();
        d.Board[56] = 2;   // white king
        d.Board[35] = -1;  // black man three squares up the diagonal
        var moves = d.LegalMoves();
        Assert.Equal(new[] { 7, 14, 21, 28 }, moves.Select(m => m[1]).OrderBy(x => x));
        var captured = d.Apply(new[] { 56, 14 });
        Assert.Equal(new[] { 35 }, captured);
        Assert.Equal(2, d.Board[14]);
    }

    [Fact]
    public void AKingMustLandWhereItCanGoOnCapturing()
    {
        var d = Empty();
        d.Board[56] = 2;   // white king
        d.Board[35] = -1;  // first victim
        d.Board[12] = -1;  // reachable only after landing on row 2 col 5
        Assert.Equal(new[] { 56, 21, 3 }, Assert.Single(d.LegalMoves()));
    }

    [Fact]
    public void AManCrownedMidCaptureCarriesOnAsAKing()
    {
        var d = Empty();
        d.Board[17] = 1;   // white man, row 2 col 1
        d.Board[10] = -1;  // jumped onto the far row...
        d.Board[21] = -1;  // ...then taken from afar, like a king
        var moves = d.LegalMoves();
        Assert.NotEmpty(moves);
        Assert.All(moves, m => Assert.Equal(new[] { 17, 3 }, m.Take(2)));
        Assert.All(moves, m => Assert.Equal(3, m.Length));
        var captured = d.Apply(moves[0]);
        Assert.Equal(2, captured.Count);
        Assert.Equal(2, d.Board[moves[0][^1]]); // a king now
    }

    [Fact]
    public void TheComputerAnswersQuicklyInAKingEndgame()
    {
        var d = Empty();
        foreach (int sq in new[] { 56, 58, 60, 62 }) d.Board[sq] = 2;  // four white kings on the back row
        foreach (int sq in new[] { 1, 3, 5, 7 }) d.Board[sq] = -2;      // four black kings on the top row
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var m = d.BestMove(new Random(1));
        Assert.True(d.IsLegal(m));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"took {clock.Elapsed.TotalSeconds:0.0}s");
    }

    [Fact]
    public void ThePlayerChoosesAmongCapturesNotTheLongest()
    {
        var d = Empty();
        d.Board[42] = 1;   // white man, row 5 col 2
        d.Board[33] = -1;  // a single capture to the left
        d.Board[35] = -1;  // or a double to the right
        d.Board[21] = -1;
        var moves = d.LegalMoves();
        Assert.Contains(moves, m => m.SequenceEqual(new[] { 42, 24 }));
        Assert.Contains(moves, m => m.SequenceEqual(new[] { 42, 28, 14 }));
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

public class LineRulesTests
{
    static void Play(DeskArcade.Games.LineRules g, params int[] squares)
    {
        foreach (int sq in squares) g.Apply(new[] { sq });
    }

    [Fact]
    public void TicTacToeDetectsARowAndAFullBoard()
    {
        var g = DeskArcade.Games.LineRules.TicTacToe();
        Play(g, 0, 3, 1, 4, 2);
        Assert.Equal(1, g.Result);
        Assert.Empty(g.LegalMoves());

        var draw = DeskArcade.Games.LineRules.TicTacToe();
        Play(draw, 0, 1, 2, 4, 3, 5, 7, 6, 8);
        Assert.Equal(2, draw.Result);
    }

    [Fact]
    public void ConnectFourDropsToTheBottomAndBlocksAThreat()
    {
        var g = DeskArcade.Games.LineRules.ConnectFour();
        Assert.Equal(7, g.LegalMoves().Count);
        Assert.All(g.LegalMoves(), m => Assert.Equal(5, m[0] / 7)); // bottom row
        Play(g, 35, 28, 36, 29, 37); // red: a1 b1 c1 on the bottom row, yellow stacked on a and b
        var block = g.BestMove(new Random(1)); // yellow must take d1
        Assert.Equal(38, block[0]);
    }

    [Fact]
    public void TheCpuTakesAWinningSquare()
    {
        var g = new DeskArcade.Games.LineRules(3, 3, 3, false); // no blunders
        Play(g, 0, 3, 1, 4);
        Assert.Equal(2, g.BestMove(new Random(2))[0]);
    }

    [Fact]
    public void EncodeRoundTrips()
    {
        var g = DeskArcade.Games.LineRules.ConnectFour();
        Play(g, 38, 31);
        var back = DeskArcade.Games.LineRules.Decode(g.Encode(), DeskArcade.Games.LineRules.ConnectFour())!;
        Assert.Equal(g.Encode(), back.Encode());
    }
}

public class SeaBattleTests
{
    [Fact]
    public void RandomFleetsHaveEveryShipInALineAndNoShipsTouch()
    {
        for (int seed = 0; seed < 50; seed++)
        {
            var fleet = DeskArcade.Games.SeaFleet.Random(new Random(seed));
            Assert.Equal(DeskArcade.Games.SeaFleet.Sizes.OrderBy(s => s), fleet.Ships.Select(s => s.Length).OrderBy(s => s));
            foreach (var ship in fleet.Ships)
                Assert.True(ship.All(c => c / 10 == ship[0] / 10) || ship.All(c => c % 10 == ship[0] % 10));
            for (int i = 0; i < fleet.Ships.Count; i++)
                for (int j = i + 1; j < fleet.Ships.Count; j++)
                    Assert.DoesNotContain(fleet.Ships[i], a => fleet.Ships[j].Any(b => Math.Abs(a / 10 - b / 10) <= 1 && Math.Abs(a % 10 - b % 10) <= 1));
        }
    }

    [Fact]
    public void SinkingEveryShipWinsAndTheCpuFinishesAWoundedShip()
    {
        var fleet = DeskArcade.Games.SeaFleet.Random(new Random(7));
        var chart = new sbyte[100];
        var last = default(DeskArcade.Games.ShotResult);
        int shots = 0;
        while (!fleet.AllSunk)
        {
            int sq = DeskArcade.Games.SeaChart.NextShot(chart, new Random(shots));
            Assert.Equal(DeskArcade.Games.SeaChart.Unknown, chart[sq]); // never fires twice at a square
            last = fleet.Shoot(sq);
            DeskArcade.Games.SeaChart.Record(chart, sq, last);
            shots++;
        }
        Assert.Equal(DeskArcade.Games.ShotKind.Win, last.Kind);
        Assert.True(shots < 90, $"took {shots} shots");
        Assert.Equal(fleet.Encode(), DeskArcade.Games.SeaFleet.Decode(fleet.Encode())!.Encode());
    }
}

[Collection("lan")] // every LAN test binds UDP port 47820, so they take turns
public class LanLinkTests
{
    static bool WaitFor(Func<bool> condition, int ms = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            System.Threading.Thread.Sleep(20);
        }
        return condition();
    }

    [Fact]
    public void AddressesParseWithAndWithoutAPort()
    {
        Assert.Equal(new System.Net.IPEndPoint(System.Net.IPAddress.Parse("192.168.1.20"), DeskArcade.Net.LanLink.Port),
            DeskArcade.Net.LanLink.ParseAddress(" 192.168.1.20 "));
        Assert.Equal(5000, DeskArcade.Net.LanLink.ParseAddress("10.0.0.7:5000")!.Port);
        Assert.Null(DeskArcade.Net.LanLink.ParseAddress("not an address at all.invalid"));
    }

    [Fact]
    public async System.Threading.Tasks.Task HostAndGuestPairOverLoopbackAndExchangeMessages()
    {
        using var host = new DeskArcade.Net.LanLink();
        using var guest = new DeskArcade.Net.LanLink();
        host.Host("chess");
        Assert.Equal(DeskArcade.Net.LanState.Waiting, host.State);

        var found = await DeskArcade.Net.LanLink.FindHosts(TimeSpan.FromSeconds(0.8));
        var me = Assert.Single(found, h => h.GameId == "chess");
        Assert.False(me.Busy);

        guest.Join(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, DeskArcade.Net.LanLink.Port));
        Assert.True(WaitFor(() => host.Connected && guest.Connected), "the two links never paired");
        Assert.Equal("chess", guest.GameId);

        guest.Send("mv|0|52,36");
        string? got = null;
        Assert.True(WaitFor(() => host.TryReceive(out got)));
        Assert.Equal("mv|0|52,36", got);

        int emote = -1;
        host.EmoteReceived += i => emote = i;
        guest.SendEmote(1);
        Assert.True(WaitFor(() => emote == 1));
        Assert.False(host.TryReceive(out _)); // emotes never reach the game's queue

        guest.Stop();
        Assert.True(WaitFor(() => host.State == DeskArcade.Net.LanState.Waiting), "the host didn't notice the guest leave");
    }
}

public class HorseMatchTests
{
    [Fact]
    public void AMissedMatchCostsALetterAndFiveLose()
    {
        var m = new DeskArcade.Games.HorseMatch(iSetFirst: true);
        Assert.True(m.MyShot);
        for (int i = 1; i <= 5; i++)
        {
            Assert.Equal(0, m.Apply(byMe: true, made: true));   // I set a shot and make it
            Assert.False(m.MyShot);                              // they must match
            Assert.Equal(-1, m.Apply(byMe: false, made: false)); // they miss: a letter
            Assert.Equal(i, m.TheirLetters);
        }
        Assert.Equal(DeskArcade.Games.HorseMatch.Phase.Over, m.Now);
        Assert.Equal(0, m.MyLetters);
    }

    [Fact]
    public void AMissedSetPassesTheBallAndAMadeMatchCostsNothing()
    {
        var m = new DeskArcade.Games.HorseMatch(iSetFirst: true);
        m.Apply(byMe: true, made: false);
        Assert.False(m.SetterIsMe);
        Assert.False(m.MyShot);
        m.Apply(byMe: false, made: true);                      // they set
        Assert.True(m.MyShot);                                  // I match
        Assert.Equal(0, m.Apply(byMe: true, made: true));
        Assert.False(m.MyShot);                                 // the setter sets again
        Assert.Equal(0, m.MyLetters + m.TheirLetters);
    }
}

public class HotkeySetTests
{
    [Fact]
    public void InvalidSettingsFallBackToTheDefaults()
    {
        Assert.Equal(DeskArcade.HotkeySet.Default, DeskArcade.HotkeySet.From(null, null));
        Assert.Equal("GNB", DeskArcade.HotkeySet.From("CtrlShift", "GGB").Keys); // letters must differ
        Assert.Equal("GNB", DeskArcade.HotkeySet.From("CtrlShift", "G1B").Keys);
        var set = DeskArcade.HotkeySet.From("CtrlShift", "xyz");
        Assert.Equal(DeskArcade.ShortcutModifiers.CtrlShift, set.Modifiers);
        Assert.Equal('Y', set.Key(DeskArcade.Platform.HotkeyAction.NextGame));
    }

    [Fact]
    public void LabelsAndPortalTriggersFollowTheModifiers()
    {
        var set = new DeskArcade.HotkeySet(DeskArcade.ShortcutModifiers.CtrlAltShift, "QWE");
        Assert.Equal("CTRL+ALT+SHIFT+q", set.PortalTrigger(DeskArcade.Platform.HotkeyAction.ToggleOverlay));
        Assert.EndsWith("Shift+E", set.Label(DeskArcade.Platform.HotkeyAction.Summon));
        Assert.Equal("CTRL+ALT+g", DeskArcade.HotkeySet.Default.PortalTrigger(DeskArcade.Platform.HotkeyAction.ToggleOverlay));
    }
}

public class DailyTests
{
    [Fact]
    public void EveryChallengeCountsSomethingTheGamesRecord()
    {
        var sources = string.Concat(Directory.GetFiles(Path.Combine(TranslationCoverageTests.RepoRoot(), "src", "Games"), "*.cs").Select(File.ReadAllText));
        // either spelled out, or built from the game id as the board games do (Id + ".captures")
        Assert.All(Daily.Pool, c => Assert.True(
            sources.Contains($"\"{c.Counter}\"") || sources.Contains($"Id + \".{c.Counter.Split('.')[1]}\""), c.Counter));
    }

    [Fact]
    public void FinishingOnConsecutiveDaysBuildsAStreak()
    {
        var stats = Stats.Load(Path.Combine(Path.GetTempPath(), $"da-daily-{Guid.NewGuid():N}.json"));
        var settings = new Settings();
        var daily = new Daily(settings, stats);
        var day = new DateOnly(2026, 9, 18);
        for (int i = 0; i < 3; i++, day = day.AddDays(1))
        {
            var c = daily.Current(day);
            Assert.False(daily.Check(day));
            stats.Add(c.Counter, c.Target);
            Assert.True(daily.Check(day));
            Assert.False(daily.Check(day)); // only once a day
            Assert.Equal(i + 1, daily.Streak(day));
        }
        Assert.Equal(0, daily.Streak(day.AddDays(1))); // a missed day ends it
    }
}

public class AccessibilityTests
{
    [Fact]
    public void ColourBlindModeSwapsGreensAndRedsOnly()
    {
        var green = Avalonia.Media.Color.FromRgb(61, 220, 132);
        var red = Avalonia.Media.Color.FromRgb(255, 92, 108);
        var gold = Avalonia.Media.Color.FromRgb(255, 209, 102);
        Art.ColorBlind = false;
        Assert.Equal(green, Art.Safe(green));
        Art.ColorBlind = true;
        try
        {
            Assert.Equal(Avalonia.Media.Color.FromRgb(86, 180, 233), Art.Safe(green));
            Assert.Equal(Avalonia.Media.Color.FromRgb(213, 94, 0), Art.Safe(red));
            Assert.Equal(gold, Art.Safe(gold));
            Assert.Equal(Avalonia.Media.Colors.White, Art.Safe(Avalonia.Media.Colors.White));
        }
        finally
        {
            Art.ColorBlind = false;
        }
    }
}

public class InstallerNameTests
{
    [Fact]
    public void TheInstallerNameMatchesTheReleaseAssets()
    {
        string name = UpdateChecker.InstallerName(new Version(1, 4, 0));
        Assert.Matches(@"^DeskArcade-Setup-1\.4\.0(-arm64|-standalone)?\.exe$", name);
    }
}
