using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using DeskArcade.Games;
using DeskArcade.Net;
using Xunit;

namespace DeskArcade.Tests;

public class QuizScoringTests
{
    static QuizItem Item(string q = "Which one?") =>
        new(new Dictionary<string, QuizText> { ["en"] = new QuizText(q, new[] { "right", "b", "c", "d" }) });

    /// <summary>A quiz of <paramref name="questions"/> questions, the right answer shown in the first place each time.</summary>
    static QuizMatch Match(int questions, bool[] cpu, int level = 2, int seed = 1) =>
        new(Enumerable.Range(0, questions).Select(i => new QuizRound(Item("Question " + i), new[] { 0, 1, 2, 3 })).ToList(), cpu, level, new Random(seed));

    /// <summary>Moves the quiz on by <paramref name="seconds"/> in frames of <paramref name="step"/>, and a hair more, so a phase of exactly that length is over.</summary>
    static void Run(QuizMatch m, double seconds, double step = 0.05)
    {
        int frames = (int)Math.Ceiling(seconds / step - 1e-9);
        for (int i = 0; i < frames; i++) m.Update(step);
        m.Update(1e-6);
    }

    [Theory]
    [InlineData(0, 1000)]
    [InlineData(5, 875)]
    [InlineData(10, 750)]
    [InlineData(19.99, 500)]
    [InlineData(20, 500)]
    [InlineData(30, 500)] // late copies never go below
    public void TheFastestRightAnswerScoresMost(double seconds, int points)
    {
        Assert.Equal(points, QuizMatch.Points(seconds));
    }

    [Fact]
    public void AQuestionIsReadThenAnsweredThenRevealedThenTheBoardThenTheNext()
    {
        var m = Match(2, new[] { false, false });
        Assert.Equal(QuizPhase.Read, m.Phase);
        Assert.Equal(QuizMatch.ReadSeconds("Question 0") + QuizMatch.FirstExtra, m.Length, 6);
        Assert.False(m.Answer(0, 0, 0)); // the answers aren't open while the question is read
        Run(m, m.Length);
        Assert.Equal(QuizPhase.Answer, m.Phase);
        Run(m, 4);
        Assert.True(m.Answer(0, 0, 0));
        Assert.False(m.Answer(0, 0, 1));  // once
        Assert.False(m.Answer(1, 1, 0));  // not the question being asked
        Assert.Equal(QuizPhase.Answer, m.Phase); // still waiting for player 1
        Assert.InRange(m.Scores[0], QuizMatch.Points(4.1), QuizMatch.Points(3.9)); // four seconds in, give or take a frame
        Assert.True(m.Answer(1, 0, 2)); // wrong
        Assert.Equal(QuizPhase.Reveal, m.Phase); // everyone has answered: no need to wait
        Assert.Equal(0, m.Scores[1]);
        Assert.Equal(new[] { 1, 0 }, m.Rights);
        Run(m, QuizMatch.RevealSeconds + 0.01);
        Assert.Equal(QuizPhase.Board, m.Phase);
        Run(m, QuizMatch.BoardSeconds + 0.01);
        Assert.Equal(QuizPhase.Read, m.Phase);
        Assert.Equal(1, m.Index);
        Assert.Equal(QuizMatch.ReadSeconds("Question 1"), m.Length, 6); // no "get ready" after the first
        Assert.Equal(new[] { -1, -1 }, m.Choice);
    }

    [Fact]
    public void TimeRunsOutWithoutAnAnswerAndTheLastQuestionEndsOnThePodium()
    {
        var m = Match(1, new[] { false });
        Run(m, m.Length);
        Run(m, QuizMatch.AnswerSeconds + 0.1);
        Assert.Equal(QuizPhase.Reveal, m.Phase);
        Assert.Equal(0, m.Scores[0]);
        Assert.Equal(QuizMatch.AnswerSeconds, m.TotalTime[0], 6); // an unanswered question counts all its time
        Run(m, QuizMatch.RevealSeconds + 0.1);
        Assert.True(m.Over); // no leaderboard after the last question
        int version = m.Version;
        Run(m, 10);
        Assert.Equal(version, m.Version);
    }

    [Fact]
    public void APlayerWhoLeftIsNotWaitedFor()
    {
        var m = Match(1, new[] { false, false });
        Run(m, m.Length);
        Assert.True(m.Answer(0, 0, 0));
        Assert.Equal(QuizPhase.Answer, m.Phase);
        m.SetActive(1, false);
        Assert.Equal(QuizPhase.Reveal, m.Phase);
        Assert.False(m.Answer(1, 0, 0));
    }

    [Fact]
    public void ScoresStayHiddenUntilTheReveal()
    {
        var m = Match(1, new[] { false, false });
        Run(m, m.Length + 3);
        m.Answer(0, 0, 0);
        var theirs = QuizView.Of(m, 1, "en", 1, new[] { "a", "b" }, "pack", false, 0);
        Assert.Equal(0, theirs.Scores[0]);
        Assert.Equal(0, theirs.Gained[0]);
        Assert.Equal(0, theirs.Rights[0]);
        Assert.Equal(-1, theirs.Right);
        Assert.True(theirs.Answered[0]); // only that they have answered
        m.Answer(1, 0, 3);
        var after = QuizView.Of(m, 1, "en", 1, new[] { "a", "b" }, "pack", false, 0);
        Assert.Equal(QuizPhase.Reveal, after.Stage);
        Assert.Equal(m.Scores[0], after.Scores[0]);
        Assert.Equal(0, after.Right);
        Assert.Equal(new[] { 1, 0, 0, 1 }, after.Picks);
        Assert.Equal(3, after.Mine);
    }

    [Fact]
    public void TheComputerAnswersAtItsLevel()
    {
        var rng = new Random(5);
        double Accuracy(int level) => Enumerable.Range(0, 4000).Count(_ => QuizMatch.CpuAnswer(level, rng).Right) / 4000.0;
        double Pace(int level) => Enumerable.Range(0, 4000).Average(_ => QuizMatch.CpuAnswer(level, rng).Seconds);
        Assert.InRange(Accuracy(1), 0.45, 0.55);
        Assert.InRange(Accuracy(4), 0.84, 0.9);
        Assert.True(Pace(1) > Pace(2) && Pace(2) > Pace(3) && Pace(3) > Pace(4));
        Assert.All(Enumerable.Range(0, 2000), _ => Assert.InRange(QuizMatch.CpuAnswer(rng.Next(1, 5), rng).Seconds, 1.2, QuizMatch.AnswerSeconds - 0.4));
    }

    [Fact]
    public void ComputerPlayersFinishAQuizOnTheirOwn()
    {
        var m = Match(10, new[] { true, true, true }, level: 4, seed: 9);
        for (int i = 0; i < 20000 && !m.Over; i++) m.Update(0.05);
        Assert.True(m.Over);
        Assert.All(m.Rights, r => Assert.InRange(r, 3, 10));
        Assert.All(m.Scores, s => Assert.InRange(s, 0, 10 * QuizMatch.MaxPoints));
        Assert.Equal(new[] { 1, 2, 2 }, QuizMatch.Places(new[] { 900, 400, 400 }));
    }
}

public class QuizPackTests
{
    const string Good = """
        # a comment
        Title: Friday team quiz

        Q: How many legs does a spider have?
        + 8
        - 6
        - 10
        - 12

        q: Which planet is known as the Red Planet?
        - Venus
        +  Mars
        - Jupiter
        - Mercury
        """;

    [Fact]
    public void AGoodPackReadsWithTheRightAnswerFirst()
    {
        var (pack, errors) = QuizPack.Parse(Good, "friday");
        Assert.Empty(errors);
        Assert.NotNull(pack);
        Assert.Equal("Friday team quiz", pack!.Title("ru")); // a pack of your own is in one language, shown to everyone
        Assert.Equal(2, pack.Items.Count);
        var second = pack.Items[1].In("uz");
        Assert.Equal("Which planet is known as the Red Planet?", second.Question);
        Assert.Equal(new[] { "Mars", "Venus", "Jupiter", "Mercury" }, second.Answers);
        Assert.True(pack.Own);
    }

    [Fact]
    public void WithoutATitleThePackIsNamedAfterTheFile()
    {
        var (pack, errors) = QuizPack.Parse("Q: 2 + 2?\r\n+ 4\r\n- 3\r\n- 5\r\n- 22\r\n", "maths");
        Assert.Empty(errors);
        Assert.Equal("maths", pack!.Title("en"));
    }

    static List<QuizPackError> Errors(string text)
    {
        var (pack, errors) = QuizPack.Parse(text, "bad");
        Assert.Null(pack);
        Assert.NotEmpty(errors);
        return errors;
    }

    [Fact]
    public void MistakesAreReportedWithTheirLineNumbers()
    {
        Assert.Equal(1, Errors("+ 4\nQ: 2 + 2?\n+ 4\n- 3\n- 5\n- 22").Single().Line);           // an answer before any question
        Assert.Equal(2, Errors("# three answers\nQ: 2 + 2?\n+ 4\n- 3\n- 5").Single().Line);      // the question's line
        Assert.Equal(1, Errors("Q: 2 + 2?\n+ 4\n+ 3\n- 5\n- 22").Single().Line);                 // two right
        Assert.Equal(1, Errors("Q: 2 + 2?\n- 4\n- 3\n- 5\n- 22").Single().Line);                 // none right
        Assert.Equal(1, Errors("Q: 2 + 2?\n+ 4\n- 3\n- 3\n- 22").Single().Line);                 // two the same
        Assert.Equal(6, Errors("Q: 2 + 2?\n+ 4\n- 3\n- 5\n- 22\n- 23").Single().Line);           // a fifth
        Assert.Equal(3, Errors("Q: 2 + 2?\n+ 4\nfour\n- 5\n- 22\n- 3").Single().Line);           // neither a question nor an answer
        Assert.Equal(1, Errors("Q:\n+ 4\n- 3\n- 5\n- 22").Single().Line);                        // an empty question
        Assert.Equal(2, Errors("Q: 2 + 2?\n+\n- 3\n- 5\n- 22").Single().Line);                   // an empty answer
        Assert.Equal(1, Errors("Q: " + new string('x', 201) + "\n+ 4\n- 3\n- 5\n- 22").Single().Line);
        Assert.Equal(3, Errors("Q: 2 + 2?\n+ 4\n- " + new string('y', 61) + "\n- 5\n- 22").Single().Line);
        Assert.Equal(6, Errors("Q: 2 + 2?\n+ 4\n- 3\n- 5\n- 22\nTitle: late").Single().Line);    // the title after a question
        Assert.Equal(0, Errors("# nothing but a comment\n\n").Single().Line);                   // no questions
        Assert.Equal(0, Errors("").Single().Line);
    }

    [Fact]
    public void EveryMistakeInAFileIsListedAndTheMessageNamesItsLine()
    {
        var errors = Errors("Q: one?\n+ a\n- b\n- c\n\nQ: two?\n+ a\n+ b\n- c\n- d\n\nwhat\n\nQ: three?\n+ a\n- b\n- c\n- d\n");
        Assert.Equal(new[] { 1, 6, 12 }, errors.Select(e => e.Line)); // three answers; two right; a stray line
        Assert.All(errors, e => Assert.Contains(e.Line.ToString(System.Globalization.CultureInfo.InvariantCulture), e.Message));
    }

    [Fact]
    public void TheExamplePackReads()
    {
        var (pack, errors) = QuizPack.Parse(QuizPack.Example, "example");
        Assert.Empty(errors);
        Assert.Equal(2, pack!.Items.Count);
        Assert.Equal("Our office", pack.Title("en"));
    }

    [Fact]
    public void AFileFromDiskReads()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"quiz-{Guid.NewGuid():N}.txt");
        System.IO.File.WriteAllText(path, "\uFEFF" + Good);
        try
        {
            var (pack, errors) = QuizPack.LoadFile(path);
            Assert.Empty(errors);
            Assert.Equal(2, pack!.Items.Count);
            Assert.Equal(path, pack.Path);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
        Assert.Equal(0, QuizPack.LoadFile(path).Errors.Single().Line); // gone: said, not thrown
    }
}

public class QuizContentTests
{
    static readonly Regex Cyrillic = new(@"\p{IsCyrillic}");

    [Fact]
    public void EveryBuiltInQuestionHasOneRightAnswerAndFourDistinctOnesInAllThreeLanguages()
    {
        Assert.Equal(QuizPack.BuiltInIds, QuizPack.BuiltIn.Select(p => p.Id));
        foreach (var pack in QuizPack.BuiltIn)
        {
            Assert.True(pack.Items.Count >= 60, $"{pack.Id} has {pack.Items.Count} questions");
            foreach (string lang in QuizPack.Languages) Assert.False(string.IsNullOrWhiteSpace(pack.Titles.GetValueOrDefault(lang)), $"{pack.Id} has no {lang} title");
            var questions = new HashSet<string>();
            foreach (var item in pack.Items)
            {
                Assert.Equal(QuizPack.Languages.OrderBy(l => l), item.Texts.Keys.OrderBy(l => l));
                foreach (string lang in QuizPack.Languages)
                {
                    var t = item.Texts[lang];
                    string where = $"{pack.Id} [{lang}] {t.Question}";
                    Assert.False(string.IsNullOrWhiteSpace(t.Question), where);
                    Assert.True(t.Question.Length <= QuizPack.MaxQuestionLength, where);
                    Assert.Equal(4, t.Answers.Length);
                    Assert.All(t.Answers, a => Assert.False(string.IsNullOrWhiteSpace(a), where));
                    Assert.True(t.Answers.All(a => a.Length <= 34), where);
                    // exactly one right answer (the first, by the file format) means four answers that can't be mixed up
                    Assert.Equal(4, t.Answers.Select(a => a.Trim().ToLowerInvariant()).Distinct().Count());
                    if (lang == "uz") Assert.False(Cyrillic.IsMatch(t.Question + string.Concat(t.Answers)), "Uzbek is written in Latin: " + where);
                    if (lang == "en") Assert.False(Cyrillic.IsMatch(t.Question + string.Concat(t.Answers)), where);
                    if (lang == "ru") Assert.Matches(Cyrillic, t.Question);
                    if (lang == "uz") Assert.DoesNotMatch("[\u02BB\u02BC\u2018\u2019`]", t.Question + string.Concat(t.Answers)); // the plain apostrophe, as in uz.po
                }
                Assert.True(questions.Add(item.Texts["en"].Question), $"{pack.Id} asks twice: {item.Texts["en"].Question}");
            }
        }
        Assert.Equal(QuizPack.BuiltIn.Sum(p => p.Items.Count), QuizPack.MixedPack.Items.Count);
    }

    [Fact]
    public void TodaysTenAreTheSameOnEveryRunAndEveryDayHasItsOwn()
    {
        var day = new DateOnly(2026, 10, 23);
        var once = QuizDeck.Daily(day);
        var again = QuizDeck.Daily(day);
        Assert.Equal(QuizDeck.DailyCount, once.Count);
        Assert.Equal(once.Select(r => r.Item.Texts["en"].Question), again.Select(r => r.Item.Texts["en"].Question));
        Assert.Equal(once.Select(r => string.Join(",", r.Order)), again.Select(r => string.Join(",", r.Order)));
        Assert.Equal(QuizDeck.DailyCount, once.Select(r => r.Item).Distinct().Count());
        Assert.All(once, r => Assert.Equal(new[] { 0, 1, 2, 3 }, r.Order.OrderBy(i => i)));
        // two or three from each pack
        var fromPack = QuizPack.BuiltIn.Select(p => once.Count(r => p.Items.Contains(r.Item))).ToList();
        Assert.All(fromPack, n => Assert.InRange(n, 2, 3));
        Assert.Equal(QuizDeck.DailyCount, fromPack.Sum());
        Assert.NotEqual(once.Select(r => r.Item), QuizDeck.Daily(day.AddDays(1)).Select(r => r.Item));
    }

    [Fact]
    public void TheDailyTenGoThroughEachPackBeforeAQuestionComesBack()
    {
        var start = new DateOnly(2026, 11, 1);
        int days = QuizPack.BuiltIn.Min(p => p.Items.Count) * 4 / 10 - 1; // every pack gives two or three a day
        var seen = new HashSet<QuizItem>();
        for (int d = 0; d < days; d++)
            foreach (var r in QuizDeck.Daily(start.AddDays(d)))
                Assert.True(seen.Add(r.Item), $"a question came back on day {d}");
        Assert.Equal(10, Enumerable.Range(0, 4).Sum(p => QuizDeck.Share(start.DayNumber, p)));
    }

    [Fact]
    public void APickFromAPackHasNoRepeats()
    {
        var picked = QuizDeck.Pick(QuizPack.BuiltIn[0], 10, new Random(3));
        Assert.Equal(10, picked.Select(r => r.Item).Distinct().Count());
        Assert.Equal(2, QuizDeck.Pick(QuizPack.Parse("Q: a?\n+ 1\n- 2\n- 3\n- 4\nQ: b?\n+ 1\n- 2\n- 3\n- 4", "x").Pack!, 10, new Random(3)).Count);
    }
}

/// <summary>Quiz Night between three copies, each with its own socket, over the loopback: a whole quiz, start to podium.</summary>
[Collection("room")]
public class QuizLoopbackTests
{
    const int TestPort = 47893; // not the real rooms port: a copy of Desk Arcade on this PC must not hear these

    static bool WaitFor(Func<bool> condition, int ms = 8000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            System.Threading.Thread.Sleep(20);
        }
        return condition();
    }

    static RoomLink Link(string name) => new() { MyName = name, Game = "quiz", Capacity = RoomLink.MaxCapacity, ListenPort = TestPort };

    [Fact]
    public void ThreeCopiesPlayAWholeQuizOverLoopback()
    {
        using var hostLink = Link("Host");
        using var aliceLink = Link("Alice");
        using var bobLink = Link("Bob");
        var host = new QuizTable(hostLink, new Random(1)) { Language = "en" };
        var alice = new QuizTable(aliceLink, new Random(2)) { Language = "ru" };
        var bob = new QuizTable(bobLink, new Random(3)) { Language = "uz" };
        host.Host("QZLB");
        Assert.Equal(QuizTable.TableMode.Hosting, host.Mode);
        var at = new IPEndPoint(IPAddress.Loopback, TestPort);
        alice.Join("QZLB", at);
        Assert.True(WaitFor(() => aliceLink.State == RoomState.Joined), $"Alice: {aliceLink.State} {aliceLink.Refusal}");
        bob.Join("qzlb", at);
        Assert.True(WaitFor(() => bobLink.State == RoomState.Joined), $"Bob: {bobLink.State} {bobLink.Refusal}");
        Assert.True(WaitFor(() => hostLink.Seats().Count(s => s.Connected) == 3));

        var pack = QuizPack.MixedPack;
        var rounds = QuizDeck.Pick(pack, 10, new Random(7));
        Assert.True(host.StartRoom(3, rounds, pack.Title, "Host", i => "CPU " + i, 2));
        Assert.False(hostLink.Open); // nobody joins mid-quiz

        // quiz time runs twelve times faster than the clock; the network is real
        var tables = new[] { host, alice, bob };
        var until = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < until && !tables.All(t => t.View is { Stage: QuizPhase.Over }))
        {
            foreach (var t in tables) t.Tick(0.25);
            var m = host.Match!;
            if (m.Phase == QuizPhase.Answer)
            {
                int right = m.Current.RightPlace;
                // Alice always right, Bob always wrong, the host right on every other question
                if (alice.View is { Stage: QuizPhase.Answer } av && av.Index == m.Index && alice.MyPick < 0) Assert.True(alice.Pick(right));
                if (bob.View is { Stage: QuizPhase.Answer } bv && bv.Index == m.Index && bob.MyPick < 0) Assert.True(bob.Pick((right + 1) % 4));
                if (host.MyPick < 0) Assert.True(host.Pick(m.Index % 2 == 0 ? right : (right + 2) % 4));
            }
            System.Threading.Thread.Sleep(20);
        }
        Assert.All(tables, t => Assert.Equal(QuizPhase.Over, t.View!.Stage));
        var final = host.Match!;
        Assert.Equal(new[] { 5, 10, 0 }, final.Rights); // seats: host, Alice, Bob
        Assert.Equal(0, final.Scores[2]);
        Assert.True(final.Scores[1] > final.Scores[0]);
        foreach (var t in new[] { alice, bob })
        {
            Assert.Equal(final.Scores, t.View!.Scores); // everyone ends on the same scores
            Assert.Equal(new[] { "Host", "Alice", "Bob" }, t.View.Names);
        }
        Assert.Equal(1, alice.View!.Seat);
        Assert.Equal(1, QuizMatch.Places(alice.View.Scores)[alice.View.Seat]);
        // each saw the questions in their own language
        Assert.Matches(@"\p{IsCyrillic}", alice.View.Question);
        Assert.Equal("uz", bob.View!.Lang);
        Assert.Equal(final.Current.Item.Texts["uz"].Question, bob.View.Question);
        Assert.False(alice.Sending); // every action was acknowledged
        Assert.False(bob.Sending);
        Assert.True(WaitFor(() => hostLink.Open)); // the next quiz takes latecomers
    }

    [Fact]
    public void AGuestWhoLeavesIsNotWaitedFor()
    {
        using var hostLink = Link("Host");
        using var guestLink = Link("Guest");
        var host = new QuizTable(hostLink, new Random(1));
        var guest = new QuizTable(guestLink, new Random(2));
        host.Host("QZLV");
        guest.Join("QZLV", new IPEndPoint(IPAddress.Loopback, TestPort));
        Assert.True(WaitFor(() => guestLink.State == RoomState.Joined));
        Assert.True(WaitFor(() => hostLink.Seats().Count(s => s.Connected) == 2));
        var rounds = QuizDeck.Pick(QuizPack.BuiltIn[2], 2, new Random(4));
        Assert.True(host.StartRoom(2, rounds, _ => "Geo", "Host", i => "CPU " + i, 2));
        Assert.True(WaitFor(() =>
        {
            host.Tick(0.25);
            guest.Tick(0.25);
            return host.Match!.Phase == QuizPhase.Answer && guest.View is { Stage: QuizPhase.Answer };
        }));
        guest.Leave();
        Assert.True(host.Pick(0));
        // three seconds at 0.05 of quiz time a step is well under the 20 s answer time: the question ended because
        // everyone still in the room had answered, not because the time ran out
        Assert.True(WaitFor(() =>
        {
            host.Tick(0.05);
            return host.Match!.Phase == QuizPhase.Reveal;
        }, 3000), "the host still waits for the guest who left");
        Assert.False(host.Match!.Active[1]);
    }
}
