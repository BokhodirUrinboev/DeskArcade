using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeskArcade.Games;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using Xunit.Abstractions;

namespace DeskArcade.Tests;

/// <summary>The snippets themselves: every one marks a bug that exists, has a fix and says why in all three languages.</summary>
public class SpotBugDataTests
{
    public static IEnumerable<object[]> Ids() => SpotBugDeck.All.Select(s => new object[] { s.Id });

    static Snippet Get(string id) => SpotBugDeck.All.Single(s => s.Id == id);

    [Fact]
    public void ThereAreAHundredAndFiftySnippetsWithUniqueIds()
    {
        Assert.True(SpotBugDeck.All.Count >= 150, $"only {SpotBugDeck.All.Count} snippets");
        Assert.Equal(SpotBugDeck.All.Count, SpotBugDeck.All.Select(s => s.Id).Distinct().Count());
    }

    [Fact]
    public void EveryLanguageHasEveryLevelAndEnoughForARound()
    {
        foreach (string lang in SpotBugDeck.Languages)
        {
            var of = SpotBugDeck.All.Where(s => s.Lang == lang).ToList();
            Assert.True(of.Count >= 25, $"{lang}: {of.Count}");
            foreach (var (level, count) in SpotBugDeck.RoundShape)
                Assert.True(of.Count(s => s.Level == level) >= count + 2, $"{lang} {level}: {of.Count(s => s.Level == level)}");
        }
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void MarksLinesThatExistAndFixesThem(string id)
    {
        var s = Get(id);
        Assert.Contains(s.Lang, SpotBugDeck.Languages);
        Assert.Contains(s.Kind, SpotBugDeck.Kinds);
        Assert.InRange(s.Lines, 4, 16);
        Assert.StartsWith(s.Lang switch { "csharp" => "cs-", "typescript" => "ts-", "python" => "py-", _ => "sql-" }, s.Id);
        // one line, or a small set when the bug truly spans them
        Assert.InRange(s.Bug.Count, 1, 2);
        Assert.Equal(s.Bug.Count, s.Bug.Distinct().Count());
        Assert.All(s.Bug, line => Assert.InRange(line, 1, s.Lines));
        Assert.All(s.Bug, line => Assert.False(string.IsNullOrWhiteSpace(s.Code[line - 1]), $"{id}: line {line} is blank"));
        Assert.NotEmpty(s.Fix);
        Assert.All(s.Fix.Keys, line => Assert.InRange(line, 1, s.Lines));
        Assert.Contains(s.Bug, line => s.Fix.ContainsKey(line)); // the fix touches the bug
        Assert.All(s.Fix.Keys, line => Assert.True(s.Bug.Contains(line) || s.Bug.Any(b => Math.Abs(b - line) <= 2), $"{id}: the fix of line {line} is far from the bug"));
        Assert.All(s.Fix, kv => Assert.NotEqual(s.Code[kv.Key - 1], kv.Value));
        Assert.NotEqual(s.Source, s.FixedSource);
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void ExplainsWhyInEnglishRussianAndUzbek(string id)
    {
        var s = Get(id);
        foreach (string lang in new[] { "en", "ru", "uz" })
        {
            Assert.True(s.Why.TryGetValue(lang, out var why) && why.Trim().Length >= 30, $"{id}: no explanation in {lang}");
            Assert.True(why!.Length <= 330, $"{id}: the {lang} explanation is {why.Length} characters");
        }
        Assert.Matches(@"\p{IsCyrillic}", s.Why["ru"]);
        Assert.DoesNotMatch(@"\p{IsCyrillic}", s.Why["uz"]); // Uzbek in Latin script
        Assert.DoesNotMatch("[‘’ʻʼ]", s.Why["uz"]); // the plain apostrophe, as in uz.po
        Assert.NotEqual(s.Why["en"], s.Why["uz"]);
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void CodeFitsThePanel(string id)
    {
        var s = Get(id);
        foreach (string line in s.Code.Concat(s.Fixed()))
        {
            Assert.True(line.Length <= 74, $"{id}: {line.Length} characters: {line}");
            Assert.DoesNotContain('\t', line);
            Assert.Equal(line.TrimEnd(), line);
        }
        Assert.False(string.IsNullOrWhiteSpace(s.Code[0]));
        Assert.False(string.IsNullOrWhiteSpace(s.Code[^1]));
    }
}

/// <summary>Rounds, scoring, the fuse, the daily pick and its streak.</summary>
public class SpotBugRulesTests
{
    static SpotBugRound Round(params string[] ids) => new(ids.Select(id => SpotBugDeck.All.Single(s => s.Id == id)).ToList());

    static int WrongLine(Snippet s) => Enumerable.Range(1, s.Lines).First(l => !s.IsBug(l));

    [Fact]
    public void AFindScoresTenPlusSpeedAndWrongClicksCostThreeEach()
    {
        Assert.Equal(20, SpotBugRound.PointsFor(30, 30, 0));
        Assert.Equal(15, SpotBugRound.PointsFor(15, 30, 0));
        Assert.Equal(10, SpotBugRound.PointsFor(0, 30, 0));
        Assert.Equal(14, SpotBugRound.PointsFor(30, 30, 2));
        Assert.Equal(0, SpotBugRound.PointsFor(0, 30, 5)); // never below 0
        Assert.Equal(200, SpotBugRound.MaxScore(10));
    }

    [Fact]
    public void TheRightLineIsFoundAndScoredWithWhatIsLeftOfTheFuse()
    {
        var r = Round("cs-01", "cs-02");
        Assert.Equal(SpotPhase.Ready, r.Phase);
        Assert.Equal(SpotClick.Ignored, r.Click(4)); // not started
        r.Start();
        Assert.False(r.Tick(6));
        Assert.Equal(SpotClick.Right, r.Click(4));
        Assert.Equal(SpotPhase.Revealed, r.Phase);
        Assert.True(r.LastFound);
        Assert.Equal(SpotBugRound.PointsFor(24, 30, 0), r.LastPoints);
        Assert.Equal(r.LastPoints, r.Score);
        Assert.Equal(6, r.LastSeconds, 6);
        Assert.Equal(SpotClick.Ignored, r.Click(3)); // waits for Next
        r.Next();
        Assert.Equal(SpotPhase.Playing, r.Phase);
        Assert.Equal("cs-02", r.Current.Id);
        Assert.Equal(30, r.Left);
    }

    [Fact]
    public void ThreeWrongClicksMissTheSnippetAndATriedLineCountsOnce()
    {
        var r = Round("cs-09");
        var s = r.Current;
        r.Start();
        var wrong = Enumerable.Range(1, s.Lines).Where(l => !s.IsBug(l)).Take(3).ToArray();
        Assert.Equal(SpotClick.Wrong, r.Click(wrong[0]));
        Assert.Equal(SpotClick.Ignored, r.Click(wrong[0])); // the same line again is not another strike
        Assert.Equal(SpotClick.Wrong, r.Click(wrong[1]));
        Assert.Equal(2, r.Wrong);
        Assert.Equal(SpotClick.Missed, r.Click(wrong[2]));
        Assert.False(r.LastFound);
        Assert.Equal(0, r.Score);
        r.Next();
        Assert.Equal(SpotPhase.Over, r.Phase);
        Assert.False(r.Perfect);
    }

    [Fact]
    public void AFindAfterWrongClicksScoresLess()
    {
        var r = Round("cs-09");
        r.Start();
        r.Click(WrongLine(r.Current));
        Assert.Equal(SpotClick.Right, r.Click(r.Current.Bug[0]));
        Assert.Equal(SpotBugRound.PointsFor(30, 30, 1), r.LastPoints);
        Assert.Equal(0, r.FirstClicks);
        Assert.Equal(2, r.Clicks);
    }

    [Fact]
    public void TheFuseRunningOutMissesTheSnippet()
    {
        var r = Round("cs-27"); // hard: 50 seconds
        r.Start();
        Assert.Equal(50, r.Fuse);
        Assert.False(r.Tick(49.5));
        Assert.True(r.Tick(1));
        Assert.Equal(SpotPhase.Revealed, r.Phase);
        Assert.True(r.LastTimedOut);
        Assert.Equal(0, r.Score);
        Assert.False(r.Tick(1)); // the fuse waits while the fix is read
    }

    [Fact]
    public void EitherLineOfATwoLineBugCounts()
    {
        foreach (int line in new[] { 4, 6 })
        {
            var r = Round("cs-18");
            r.Start();
            Assert.Equal(SpotClick.Right, r.Click(line));
        }
    }

    [Fact]
    public void AllFirstClickFindsMakeAPerfectRound()
    {
        var r = Round("cs-01", "ts-01", "py-02");
        r.Start();
        for (int i = 0; i < 3; i++)
        {
            r.Click(r.Current.Bug[0]);
            r.Next();
        }
        Assert.Equal(SpotPhase.Over, r.Phase);
        Assert.True(r.Perfect);
        Assert.Equal(3, r.Found);
        Assert.Equal(60, r.Score);
    }

    [Fact]
    public void ARoundHasTenSnippetsEasyToHardWithNoneTwice()
    {
        foreach (string? lang in SpotBugDeck.Languages.Append(null))
            for (int seed = 0; seed < 20; seed++)
            {
                var round = SpotBugDeck.Round(new Random(seed), lang);
                Assert.Equal(SpotBugDeck.RoundSize, round.Count);
                Assert.Equal(round.Count, round.Distinct().Count());
                Assert.All(round, s => Assert.True(lang == null || s.Lang == lang));
                Assert.Equal(round.Select(s => s.Level).OrderBy(l => l), round.Select(s => s.Level));
                Assert.Equal(new[] { BugLevel.Easy, BugLevel.Easy, BugLevel.Easy }, round.Take(3).Select(s => s.Level));
            }
    }

    [Fact]
    public void TheSameSeedGivesTheSameRoundOnBothScreens()
    {
        int seed = MinesweeperRules.DailySeed(new DateTime(2026, 10, 20), "spotbug-lan", 3);
        var a = SpotBugDeck.Round(new Random(seed), null).Select(s => s.Id);
        var b = SpotBugDeck.Round(new Random(seed), null).Select(s => s.Id);
        Assert.Equal(a, b);
        Assert.NotEqual(a, SpotBugDeck.Round(new Random(seed + 1), null).Select(s => s.Id));
    }

    [Fact]
    public void TheDailySnippetIsFixedForADateAndGoesRoundEverySnippet()
    {
        var day = new DateOnly(2026, 10, 20);
        Assert.Equal(SpotBugDeck.Daily(day).Id, SpotBugDeck.Daily(new DateOnly(2026, 10, 20)).Id);
        Assert.Equal(50, SpotBugDeck.DailyNumber(day));
        Assert.Equal(1, SpotBugDeck.DailyNumber(SpotBugDeck.FirstDaily));
        int n = SpotBugDeck.All.Count;
        var cycle = Enumerable.Range(0, n).Select(i => SpotBugDeck.Daily(SpotBugDeck.FirstDaily.AddDays(i)).Id).ToList();
        Assert.Equal(n, cycle.Distinct().Count()); // every snippet has its day before any comes back
        Assert.Equal(cycle[0], SpotBugDeck.Daily(SpotBugDeck.FirstDaily.AddDays(n)).Id);
        Assert.Equal(cycle[^1], SpotBugDeck.Daily(SpotBugDeck.FirstDaily.AddDays(-1)).Id); // before #1 too
        // consecutive days mix the languages
        Assert.True(cycle.Take(12).Select(id => id.Split('-')[0]).Distinct().Count() >= 3);
    }

    /// <summary>The daily order must not depend on the process (string.GetHashCode does): FNV-1a's published values.</summary>
    [Fact]
    public void TheStableHashIsFnv1a()
    {
        Assert.Equal(0x811C9DC5u, SpotBugDeck.StableHash(""));
        Assert.Equal(0xE40C292Cu, SpotBugDeck.StableHash("a"));
        Assert.Equal(0xBF9CF968u, SpotBugDeck.StableHash("foobar"));
    }

    [Theory]
    [InlineData(100, 3, 101, true, 4)]   // the day after: one more
    [InlineData(100, 3, 102, true, 1)]   // after a gap: starts again
    [InlineData(100, 3, 101, false, 0)]  // a miss breaks it
    [InlineData(0, 0, 101, true, 1)]     // the first find
    [InlineData(101, 4, 101, true, 4)]   // the same day again changes nothing
    public void TheStreakGrowsDayByDayAndBreaksOnAMiss(int last, int streak, int today, bool found, int expected) =>
        Assert.Equal(expected, SpotBugStreak.After(last, streak, today, found));

    [Fact]
    public void TheStreakShowsOnlyWhileItStands()
    {
        Assert.Equal(5, SpotBugStreak.Showing(100, 5, 100));
        Assert.Equal(5, SpotBugStreak.Showing(100, 5, 101));
        Assert.Equal(0, SpotBugStreak.Showing(100, 5, 102));
    }

    [Fact]
    public void TheShareLinesSayWhatHappened()
    {
        Assert.Equal("Spot the Bug daily #57 · found it in 1 click, 9 s 🐞", SpotBugStreak.ShareLine(57, true, 1, 9));
        Assert.Equal("Spot the Bug daily #57 · found it in 3 clicks, 21 s 🐞", SpotBugStreak.ShareLine(57, true, 3, 21));
        Assert.Equal("Spot the Bug daily #57 · the bug got away 🐛", SpotBugStreak.ShareLine(57, false, 3, 30));
        Assert.Equal("Spot the Bug · 8/10 bugs · 142 points 🐞", SpotBugStreak.RoundLine(8, 10, 142));
    }

    [Fact]
    public void EveryKindHasWordsOfItsOwn()
    {
        var names = SpotBugDeck.Kinds.Select(SpotBugDeck.KindName).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Equal(SpotBugDeck.Kinds.Length, SpotBugDeck.Kinds.Distinct().Count());
        Assert.All(SpotBugDeck.All, s => Assert.Contains(s.Kind, SpotBugDeck.Kinds));
        Assert.Equal("unknown-kind", SpotBugDeck.KindName("unknown-kind")); // anything else shows as it is
    }
}

/// <summary>The code panel's tokenizer and colours.</summary>
public class SpotBugSyntaxTests
{
    public static IEnumerable<object[]> Themes() => DeskArcade.Engine.Themes.All.Select(t => new object[] { t.Id });

    static List<(string Text, Tok Kind)> Line(string lang, string line)
    {
        var state = new SpotBugSyntax.State();
        return SpotBugSyntax.Line(lang, line, ref state);
    }

    static Tok KindOf(List<(string Text, Tok Kind)> tokens, string text) => tokens.First(t => t.Text.Trim() == text).Kind;

    [Fact]
    public void TheTokensOfEveryLineJoinBackIntoTheLine()
    {
        foreach (var s in SpotBugDeck.All)
        {
            var lines = SpotBugSyntax.Lines(s.Lang, s.Code);
            for (int i = 0; i < s.Lines; i++) Assert.Equal(s.Code[i], string.Concat(lines[i].Select(t => t.Text)));
            var fixedLines = SpotBugSyntax.Lines(s.Lang, s.Fixed());
            Assert.Equal(s.Fixed(), fixedLines.Select(l => string.Concat(l.Select(t => t.Text))));
        }
    }

    [Fact]
    public void CSharpKeywordsTypesStringsCallsAndComments()
    {
        var t = Line("csharp", "    using var reader = new StreamReader(path); // open it");
        Assert.Equal(Tok.Keyword, KindOf(t, "using"));
        Assert.Equal(Tok.Keyword, KindOf(t, "new"));
        Assert.Equal(Tok.Call, KindOf(t, "StreamReader"));
        Assert.Equal(Tok.Comment, t[^1].Kind);
        Assert.StartsWith("//", t[^1].Text);
        var s = Line("csharp", "return $\"Hi, {name}!\" + 'x' + 0.5m;");
        Assert.Equal(Tok.String, KindOf(s, "$\"Hi, {name}!\""));
        Assert.Equal(Tok.String, KindOf(s, "'x'"));
        Assert.Equal(Tok.Number, KindOf(s, "0.5m"));
    }

    [Fact]
    public void TypeScriptTemplatesRegexesAndTypes()
    {
        var t = Line("typescript", "const digits = /\\d+/g; let n: number = `a ${b}`.length;");
        Assert.Equal(Tok.Keyword, KindOf(t, "const"));
        Assert.Equal(Tok.String, KindOf(t, "/\\d+/g"));
        Assert.Equal(Tok.Type, KindOf(t, "number"));
        Assert.Equal(Tok.String, KindOf(t, "`a ${b}`"));
        var division = Line("typescript", "const half = total / 2 / 3;");
        Assert.DoesNotContain(division, x => x.Kind == Tok.String);
    }

    [Fact]
    public void PythonPrefixesTripleQuotesAndBuiltins()
    {
        var t = Line("python", "    for i in range(n): print(f\"{i}\")  # count");
        Assert.Equal(Tok.Keyword, KindOf(t, "for"));
        Assert.Equal(Tok.Type, KindOf(t, "range"));
        Assert.Equal(Tok.String, KindOf(t, "f\"{i}\""));
        Assert.Equal(Tok.Comment, t[^1].Kind);
        // a docstring left open carries on to the next line
        var lines = SpotBugSyntax.Lines("python", new[] { "\"\"\"one", "two\"\"\" and x" });
        Assert.Equal(Tok.String, lines[0].Single().Kind);
        Assert.Equal(("two\"\"\"", Tok.String), lines[1][0]);
        Assert.Equal(Tok.Keyword, KindOf(lines[1], "and"));
    }

    [Fact]
    public void SqlKeywordsInAnyCaseStringsWithDoubledQuotesAndParameters()
    {
        var t = Line("sql", "select id from t where name = 'O''Brien' and x = :last_id -- note");
        Assert.Equal(Tok.Keyword, KindOf(t, "select"));
        Assert.Equal(Tok.Keyword, KindOf(t, "where"));
        Assert.Equal(Tok.String, KindOf(t, "'O''Brien'"));
        Assert.Equal(Tok.Type, KindOf(t, ":last_id"));
        Assert.Equal(Tok.Comment, t[^1].Kind);
        Assert.Equal(Tok.Call, KindOf(Line("sql", "SELECT COUNT(*) FROM t"), "COUNT"));
        Assert.Equal(Tok.String, KindOf(Line("sql", "EXEC sp_executesql N'SELECT 1';"), "N'SELECT 1'"));
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void CodeReadsAgainstThePanelInEveryTheme(string id)
    {
        var theme = DeskArcade.Engine.Themes.All.Single(t => t.Id == id);
        foreach (bool colourBlind in new[] { false, true })
        {
            DeskArcade.Engine.Art.ColorBlind = colourBlind;
            try
            {
                var p = SpotBugSyntax.PaletteFor(theme);
                foreach (var (kind, ink) in p.Ink)
                    Assert.True(SpotBugSyntax.Contrast(ink, p.Back) >= SpotBugSyntax.TextContrast, $"{id} {kind}: {SpotBugSyntax.Contrast(ink, p.Back):0.00}");
                Assert.True(SpotBugSyntax.Contrast(p.LineNumber, p.Gutter) >= SpotBugSyntax.NumberContrast, $"{id} line numbers");
                Assert.Equal(p.Light, SpotBugSyntax.Contrast(p.Back, Avalonia.Media.Colors.White) < 2);
            }
            finally
            {
                DeskArcade.Engine.Art.ColorBlind = false;
            }
        }
    }

    [Fact]
    public void ThereAreLightAndDarkPanels()
    {
        var palettes = DeskArcade.Engine.Themes.All.Select(SpotBugSyntax.PaletteFor).ToList();
        Assert.Contains(palettes, p => p.Light);
        Assert.Contains(palettes, p => !p.Light);
    }
}

/// <summary>
/// The C# snippets compiled with and without their fix (Roslyn, in memory). A snippet marked as a compile error must
/// fail as written and compile once fixed; every other one compiles both ways, and for a good share of them a probe runs
/// both versions and shows the bug changes what the code does, while the fix gives the right answer.
/// </summary>
public class SpotBugCSharpTests
{
    const string Usings = """
        using System;
        using System.Collections.Concurrent;
        using System.Collections.Generic;
        using System.Data.Common;
        using System.IO;
        using System.Linq;
        using System.Net.Http;
        using System.Text;
        using System.Threading;
        using System.Threading.Tasks;
        using Snippets.Common;

        """;

    /// <summary>The domain types the snippets lean on (orders, users…), and the probes' helper.</summary>
    const string Common = """
        using System;
        using System.Collections;
        using System.Globalization;
        using System.Linq;
        using System.Threading.Tasks;

        namespace Snippets.Common;

        public record Order(int Id, int CustomerId, decimal Total, DateTime PlacedAt, string Status);
        public record Customer(int Id, string Name, string? Email);
        public record Address(string City, string? Zip);

        public class User
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";
            public string? Email { get; set; }
            public Address? Address { get; set; }
        }

        public interface IUserStore
        {
            Task<User?> FindAsync(int id);
            Task SaveAsync(User user);
        }

        public interface ICache
        {
            Task LoadAsync(string key);
        }

        public class PriceFeed
        {
            public event Action<decimal>? Changed;
            public int Listeners => Changed?.GetInvocationList().Length ?? 0;
            public void Publish(decimal price) => Changed?.Invoke(price);
        }

        /// <summary>A store whose calls finish a little later, as a real one would: it shows whether they were awaited.</summary>
        public class SlowStore : IUserStore
        {
            public int Saved;
            public async Task<User?> FindAsync(int id) { await Task.Delay(40); return null; }
            public async Task SaveAsync(User user) { await Task.Delay(40); System.Threading.Interlocked.Increment(ref Saved); }
        }

        public class SlowCache : ICache
        {
            public int Loaded;
            public async Task LoadAsync(string key) { await Task.Delay(40); System.Threading.Interlocked.Increment(ref Loaded); }
        }

        public static class P
        {
            /// <summary>What running <paramref name="f"/> gives, as text: its value, or the type of exception it throws.</summary>
            public static string Try(Func<object?> f)
            {
                try { return Show(f()); }
                catch (Exception e) { return e.GetType().Name; }
            }

            public static string Show(object? o) => o switch
            {
                null => "null",
                string s => s,
                double d => d.ToString(CultureInfo.InvariantCulture),
                decimal m => m.ToString(CultureInfo.InvariantCulture),
                IEnumerable e => string.Join(",", e.Cast<object?>().Select(Show)),
                _ => Convert.ToString(o, CultureInfo.InvariantCulture) ?? "",
            };
        }
        """;

    /// <summary>
    /// Probes by snippet id: code for Probe.Run in the snippet's namespace (an expression, or a body with return), and
    /// what the fixed code must give. The buggy code must give something else.
    /// </summary>
    static readonly Dictionary<string, (string Code, string Expected)> Probes = new()
    {
        ["cs-01"] = ("P.Try(() => Snippet.CountAbove(new[] { 1, 5, 9 }, 4))", "2"),
        ["cs-02"] = ("P.Try(() => Snippet.Greeting(null))", "Hello, guest from somewhere!"),
        ["cs-03"] = ("P.Try(() => Snippet.IsWeekend(new DateTime(2026, 10, 3)))", "True"),
        ["cs-04"] = ("P.Try(() => Snippet.AverageScore(new List<int> { 1, 2 }))", "1.5"),
        ["cs-05"] = ("P.Try(() => Snippet.NamesWithEmail(new List<User> { new() { Name = \"Ann\", Email = \"ann@example.com\" }, new() { Name = \"Bob\" } }))", "Ann"),
        ["cs-08"] = ("P.Try(() => Snippet.ClampPercent(42))", "42"),
        ["cs-09"] = ("var orders = Enumerable.Range(1, 5).Select(i => new Order(i, 1, 10m, DateTime.Today, \"paid\")).ToList(); return P.Try(() => Snippet.Page(orders, 1, 2).Select(o => o.Id));", "1,2"),
        ["cs-10"] = ("P.Try(() => Snippet.TotalBytes(3000, 1_000_000))", "3000000000"),
        ["cs-11"] = ("var orders = new List<Order> { new(1, 1, 5m, DateTime.Today, \"cancelled\"), new(2, 1, 7m, DateTime.Today, \"paid\") }; return P.Try(() => { Snippet.RemoveCancelled(orders); return orders.Select(o => o.Id); });", "2"),
        ["cs-12"] = ("P.Try(() => Snippet.CustomerName(new List<Customer>(), 7))", "unknown"),
        ["cs-14"] = ("var c = new Counter(); c.Add(2); c.Add(3); return P.Show(c.Total);", "5"),
        ["cs-15"] = ("P.Try(() => Snippet.CanVote(18))", "True"),
        ["cs-16"] = ("var store = new SlowStore(); new Snippet().SaveAllAsync(store, new List<User> { new(), new() }).GetAwaiter().GetResult(); return P.Show(store.Saved);", "2"),
        ["cs-17"] = ("P.Try(() => Snippet.DisplayName(new User { Name = \"Ann\", Email = \"ann@example.com\" }))", "Ann"),
        ["cs-18"] = ("P.Try(() => Snippet.Greeters(new[] { \"Ann\", \"Bob\" })[0]())", "Hi, Ann!"),
        ["cs-21"] = ("P.Try(() => Snippet.AgeOn(new DateTime(2000, 10, 15), new DateTime(2026, 10, 1)))", "25"),
        ["cs-22"] = ("P.Try(() => Snippet.CountChanged(new Dictionary<string, object> { [\"qty\"] = 5 }, new Dictionary<string, object> { [\"qty\"] = 5 }))", "0"),
        ["cs-23"] = ("P.Try(() => Snippet.IsPaidInFull(new[] { 0.1, 0.2 }, 0.3))", "True"),
        ["cs-24"] = ("var feed = new PriceFeed(); new PriceWidget(feed).Dispose(); return P.Show(feed.Listeners);", "0"),
        ["cs-25"] = ("P.Try(() => Snippet.Truncate(\"hello\", 5))", "hello"),
        ["cs-26"] = ("P.Try(() => new ByTotal().Compare(new Order(1, 1, 10.20m, DateTime.Today, \"paid\"), new Order(2, 1, 10.70m, DateTime.Today, \"paid\")))", "-1"),
        ["cs-29"] = ("P.Try(() => Snippet.TotalItems(2, 3))", "5"),
        ["cs-31"] = ("P.Try(() => Snippet.ParseAll(new[] { \"1\", \"x\" }).Count())", "0"),
        ["cs-32"] = ("P.Try(() => Snippet.Tally(new[] { \"a\", \"b\", \"a\" }).Select(kv => kv.Key + \"=\" + kv.Value))", "a=2,b=1"),
        ["cs-35"] = ("var cache = new SlowCache(); Snippet.WarmUpAsync(cache, new List<string> { \"a\", \"b\" }).GetAwaiter().GetResult(); return P.Show(cache.Loaded);", "2"),
        ["cs-36"] = ("P.Try(() => new HashSet<Sku> { new(\"A-1\"), new(\"A-1\") }.Count)", "1"),
        ["cs-37"] = ("P.Try(() => Snippet.TimedOut(unchecked(int.MaxValue - 2000 + 3000), int.MaxValue - 2000, 1000))", "True"),
        ["cs-38"] = ("try { Snippet.Import(\"orders.csv\"); return \"no exception\"; } catch (Exception e) { return P.Show(e.StackTrace!.Contains(\"ParseFile\")); }", "True"),
        ["cs-39"] = ("string dir = Directory.CreateTempSubdirectory().FullName; File.WriteAllText(Path.Combine(dir, \"config.json\"), \"{}\"); return P.Try(() => { using var s = Snippet.OpenConfig(dir); return s.ReadByte(); });", "123"),
        ["cs-44"] = ("P.Try(() => Snippet.ExistsAsync(new SlowStore(), 7).GetAwaiter().GetResult())", "False"),
        ["cs-45"] = ("P.Try(() => Snippet.SumBetween(3, 5))", "12"),
    };

    public static IEnumerable<object[]> CSharpIds() => SpotBugDeck.All.Where(s => s.Lang == "csharp").Select(s => new object[] { s.Id });
    public static IEnumerable<object[]> ProbeIds() => Probes.Keys.Select(id => new object[] { id });

    sealed class Built
    {
        public required Dictionary<string, ImmutableArray<Diagnostic>> Errors;
        public required Dictionary<string, string> Results;
    }

    static readonly Lazy<(Built Buggy, Built Fixed)> Compiled = new(() => (Build(fixedCode: false), Build(fixedCode: true)));

    static string Ns(Snippet s) => "Snippets.S_" + s.Id.Replace('-', '_');

    static readonly Regex TypeDeclaration = new(@"^\s*((public|internal|sealed|static|abstract|partial|readonly)\s+)*(class|record|struct|interface|enum)\b");

    /// <summary>The snippet as a compilable file: in a class of its own, unless it declares its own types.</summary>
    static string Wrap(Snippet s, bool fixedCode)
    {
        var code = fixedCode ? s.Fixed() : s.Code;
        var sb = new StringBuilder(Usings);
        sb.Append("namespace ").Append(Ns(s)).Append(";\n\n");
        if (TypeDeclaration.IsMatch(code.First(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith("//", StringComparison.Ordinal)))) sb.Append(string.Join("\n", code)).Append('\n');
        else sb.Append("public class Snippet\n{\n").Append(string.Join("\n", code)).Append("\n}\n");
        if (Probes.TryGetValue(s.Id, out var probe))
        {
            string body = probe.Code.Contains("return ", StringComparison.Ordinal) ? probe.Code : "return " + probe.Code + ";";
            sb.Append("\npublic static class Probe\n{\n    public static string Run()\n    {\n").Append(body).Append("\n    }\n}\n");
        }
        return sb.ToString();
    }

    static readonly Lazy<MetadataReference[]> References = new(() =>
    {
        string dir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        return ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => string.Equals(Path.GetDirectoryName(p), dir, StringComparison.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToArray();
    });

    static Built Build(bool fixedCode)
    {
        var parse = new CSharpParseOptions(LanguageVersion.Latest);
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable, optimizationLevel: OptimizationLevel.Debug);
        var common = CSharpSyntaxTree.ParseText(Common, parse, "Common.cs");
        var snippets = SpotBugDeck.All.Where(s => s.Lang == "csharp").ToList();
        var trees = snippets.ToDictionary(s => s.Id, s => CSharpSyntaxTree.ParseText(Wrap(s, fixedCode), parse, s.Id + ".cs"));

        // errors per snippet, all in one compilation (each snippet has a namespace of its own)
        var all = CSharpCompilation.Create("SnippetsCheck", trees.Values.Prepend(common), References.Value, options);
        var diagnostics = all.GetDiagnostics();
        var errors = trees.ToDictionary(kv => kv.Key, kv => diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error && d.Location.SourceTree == kv.Value).ToImmutableArray());

        // then the ones that compile, emitted and loaded, for the probes
        var runnable = trees.Where(kv => errors[kv.Key].IsEmpty).Select(kv => kv.Value).Prepend(common);
        var emit = CSharpCompilation.Create("Snippets" + (fixedCode ? "Fixed" : "Buggy"), runnable, References.Value, options);
        using var ms = new MemoryStream();
        var result = emit.Emit(ms);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        ms.Position = 0;
        var context = new AssemblyLoadContext("spotbug-" + (fixedCode ? "fixed" : "buggy"), isCollectible: true);
        var assembly = context.LoadFromStream(ms);
        var results = new Dictionary<string, string>();
        foreach (var s in snippets.Where(s => Probes.ContainsKey(s.Id) && errors[s.Id].IsEmpty))
            results[s.Id] = (string)assembly.GetType(Ns(s) + ".Probe")!.GetMethod("Run")!.Invoke(null, null)!;
        return new Built { Errors = errors, Results = results };
    }

    static string Show(IEnumerable<Diagnostic> ds) => string.Join("\n", ds.Select(d => $"{d.Id} {d.GetMessage()} at {d.Location.GetLineSpan().StartLinePosition}"));

    [Theory]
    [MemberData(nameof(CSharpIds))]
    public void CompilesWithTheFixAndFailsOnlyWhenTheBugIsACompileError(string id)
    {
        var s = SpotBugDeck.All.Single(x => x.Id == id);
        var (buggy, fixedCode) = Compiled.Value;
        Assert.True(fixedCode.Errors[id].IsEmpty, $"{id} does not compile with the fix:\n{Show(fixedCode.Errors[id])}");
        if (s.CompileError) Assert.False(buggy.Errors[id].IsEmpty, $"{id} is marked as a compile error but compiles");
        else Assert.True(buggy.Errors[id].IsEmpty, $"{id} does not compile as written:\n{Show(buggy.Errors[id])}");
    }

    [Theory]
    [MemberData(nameof(ProbeIds))]
    public void TheBugChangesWhatTheCodeDoesAndTheFixGetsItRight(string id)
    {
        var (buggy, fixedCode) = Compiled.Value;
        Assert.True(buggy.Results.ContainsKey(id), $"{id} did not run");
        Assert.Equal(Probes[id].Expected, fixedCode.Results[id]);
        Assert.NotEqual(fixedCode.Results[id], buggy.Results[id]);
    }

    [Fact]
    public void AGoodShareOfTheCSharpSnippetsIsRun()
    {
        int csharp = SpotBugDeck.All.Count(s => s.Lang == "csharp");
        Assert.True(Probes.Count * 2 >= csharp, $"{Probes.Count} of {csharp} run");
        Assert.All(Probes.Keys, id => Assert.Contains(SpotBugDeck.All, s => s.Id == id && !s.CompileError));
    }
}

/// <summary>
/// The Python snippets compiled (as py_compile does) and the TypeScript ones parsed, with and without their fix, when
/// the tools are on this machine: python (or python3, py) on PATH; node, and the typescript package either in the
/// global node_modules or where DESKARCADE_TYPESCRIPT points. Without them the checks say so and pass, so a machine
/// without Python or Node still runs the rest of the suite. Nothing is downloaded.
/// </summary>
public class SpotBugScriptTests
{
    readonly ITestOutputHelper _out;

    public SpotBugScriptTests(ITestOutputHelper output) => _out = output;

    /// <summary>Runs a program; null when it isn't there or doesn't finish in time.</summary>
    static (int Code, string Out, string Err)? Run(string file, string args, string? stdin = null, int timeoutMs = 60000)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin != null,
                UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            if (stdin != null)
            {
                p.StandardInput.Write(stdin);
                p.StandardInput.Close();
            }
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { /* already gone */ }
                return null;
            }
            return (p.ExitCode, stdout.Result, stderr.Result);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // not installed
        }
    }

    /// <summary>A temporary file with <paramref name="text"/> in it, deleted with the returned handle.</summary>
    sealed class TempFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "deskarcade-spotbug-" + Guid.NewGuid().ToString("N"));
        public TempFile(string text, string extension)
        {
            Path += extension;
            File.WriteAllText(Path, text, new UTF8Encoding(false));
        }
        public void Dispose()
        {
            try { File.Delete(Path); } catch { /* best effort */ }
        }
    }

    static string SourcesJson(string lang) => JsonSerializer.Serialize(SpotBugDeck.All.Where(s => s.Lang == lang)
        .SelectMany(s => new[] { new { name = s.Id + " as written", code = s.Source }, new { name = s.Id + " fixed", code = s.FixedSource } }));

    /// <summary>Lines of "name: error" for every source that doesn't compile or parse; empty when all do.</summary>
    static List<string> Failures(string output) => output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Where(l => l.Length > 0 && l != "ok").ToList();

    const string PythonCheck = """
        import json, sys, warnings
        warnings.simplefilter("ignore")
        sources = json.load(open(sys.argv[1], encoding="utf-8"))
        for s in sources:
            try:
                compile(s["code"], s["name"], "exec")
            except SyntaxError as e:
                print(f"{s['name']}: {e.msg} at line {e.lineno}")
        print("ok")
        """;

    [Fact]
    public void ThePythonSnippetsCompileWithAndWithoutTheFix()
    {
        string? python = new[] { "python", "python3", "py" }.FirstOrDefault(p => Run(p, "--version") is { Code: 0 } r && (r.Out + r.Err).Contains("Python 3", StringComparison.Ordinal));
        if (python == null)
        {
            _out.WriteLine("skipped: no Python 3 on PATH");
            return;
        }
        using var sources = new TempFile(SourcesJson("python"), ".json");
        using var script = new TempFile(PythonCheck, ".py");
        var result = Run(python, $"\"{script.Path}\" \"{sources.Path}\"");
        Assert.NotNull(result);
        Assert.True(result.Value.Code == 0, result.Value.Err);
        Assert.Contains("ok", result.Value.Out);
        Assert.Empty(Failures(result.Value.Out));
        _out.WriteLine($"{SpotBugDeck.All.Count(s => s.Lang == "python") * 2} Python sources compiled with {python}");
    }

    const string TypeScriptCheck = """
        const ts = require(process.argv[2]);
        const sources = JSON.parse(require("fs").readFileSync(process.argv[3], "utf8"));
        for (const s of sources) {
          const out = ts.transpileModule(s.code, { reportDiagnostics: true, fileName: "snippet.ts", compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ESNext } });
          for (const d of out.diagnostics || []) console.log(`${s.name}: ${ts.flattenDiagnosticMessageText(d.messageText, " ")}`);
        }
        console.log("ok " + ts.version);
        """;

    /// <summary>The typescript package: DESKARCADE_TYPESCRIPT, else the global node_modules; null when there is none.</summary>
    static string? TypeScriptPackage()
    {
        string? dir = Environment.GetEnvironmentVariable("DESKARCADE_TYPESCRIPT");
        if (string.IsNullOrEmpty(dir) && Run(OperatingSystem.IsWindows() ? "cmd" : "npm", OperatingSystem.IsWindows() ? "/c npm root -g" : "root -g", timeoutMs: 20000) is { Code: 0 } npm)
            dir = Path.Combine(npm.Out.Trim(), "typescript");
        return dir != null && File.Exists(Path.Combine(dir, "package.json")) ? dir : null;
    }

    [Fact]
    public void TheTypeScriptSnippetsParseWithAndWithoutTheFix()
    {
        if (Run("node", "--version") is not { Code: 0 })
        {
            _out.WriteLine("skipped: no node on PATH");
            return;
        }
        string? typescript = TypeScriptPackage();
        if (typescript == null)
        {
            _out.WriteLine("skipped: no typescript package (npm i -g typescript, or set DESKARCADE_TYPESCRIPT to its folder)");
            return;
        }
        using var sources = new TempFile(SourcesJson("typescript"), ".json");
        using var script = new TempFile(TypeScriptCheck, ".js");
        var result = Run("node", $"\"{script.Path}\" \"{typescript}\" \"{sources.Path}\"");
        Assert.NotNull(result);
        Assert.True(result.Value.Code == 0, result.Value.Err);
        var lines = Failures(result.Value.Out);
        string? version = lines.FirstOrDefault(l => l.StartsWith("ok ", StringComparison.Ordinal));
        Assert.NotNull(version);
        Assert.DoesNotContain(lines, l => l != version);
        _out.WriteLine($"{SpotBugDeck.All.Count(s => s.Lang == "typescript") * 2} TypeScript sources parsed with TypeScript {version![3..]}");
    }
}
