using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeskArcade.Dev;
using DeskArcade.Office;
using Xunit;

namespace DeskArcade.Tests;

/// <summary>
/// Tests that run git in temporary repos: they run on their own, after the others, so that no other test can have
/// <see cref="Cli.Runner"/> swapped for recorded output while they need the real git.
/// </summary>
[CollectionDefinition("git", DisableParallelization = true)]
public sealed class GitCollection
{
}

public class FreeTimeTests
{
    static readonly DateTime Day = new(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc);

    static DateTime At(double hours) => Day.AddHours(hours);

    static Meeting M(double from, double to, string title = "m") => new(title, At(from), At(to));

    [Fact]
    public void FindsTheStretchesBetweenMeetings()
    {
        var free = FreeTime.Between(new[] { M(10, 11), M(14, 15) }, At(9), At(18));
        Assert.Equal(new[] { (At(9), At(10)), (At(11), At(14)), (At(15), At(18)) }, free.Select(f => (f.Start, f.End)));
        Assert.Equal(TimeSpan.FromHours(3), FreeTime.Longest(new[] { M(10, 11), M(14, 15) }, At(9), At(18))!.Value.Length);
    }

    [Fact]
    public void OverlappingMeetingsAreOneBusyStretch()
    {
        // 10:00–11:30 and 11:00–12:00 overlap; 11:15–11:45 sits inside both
        var free = FreeTime.Between(new[] { M(11, 12), M(10, 11.5), M(11.25, 11.75) }, At(9), At(13));
        Assert.Equal(new[] { (At(9), At(10)), (At(12), At(13)) }, free.Select(f => (f.Start, f.End)));
    }

    [Fact]
    public void BackToBackMeetingsLeaveNoGap()
    {
        var free = FreeTime.Between(new[] { M(10, 11), M(11, 12), M(12, 13.5) }, At(9), At(18));
        Assert.Equal(new[] { (At(9), At(10)), (At(13.5), At(18)) }, free.Select(f => (f.Start, f.End)));
        Assert.Equal(At(13.5), FreeTime.Longest(new[] { M(10, 11), M(11, 12), M(12, 13.5) }, At(9), At(18))!.Value.Start);
    }

    [Fact]
    public void AnAllDayMeetingLeavesNoRoom()
    {
        var workshop = M(-1, 25, "Offsite");
        Assert.Empty(FreeTime.Between(new[] { workshop }, At(9), At(18)));
        Assert.Null(FreeTime.Longest(new[] { workshop, M(10, 11) }, At(9), At(18)));
    }

    [Fact]
    public void AMeetingGoingOnNowBlocksUntilItEnds_AndOneWithNoEndBlocksNothing()
    {
        var free = FreeTime.Between(new[] { M(8.5, 9.5), new Meeting("reminder", At(12), At(12)) }, At(9), At(13));
        Assert.Equal(new[] { (At(9.5), At(13)) }, free.Select(f => (f.Start, f.End)));
    }

    [Fact]
    public void OnlyStretchesOfTwentyFiveMinutesAreWorthAFocusBlock()
    {
        static Meeting Min(int from, int to) => new("m", Day.AddMinutes(from), Day.AddMinutes(to));
        // gaps of 20 minutes (10:40–11:00) and 25 minutes (12:00–12:25)
        var meetings = new[] { Min(540, 640), Min(660, 720), Min(745, 1080) };
        var focus = FreeTime.Longest(meetings, At(9), At(18));
        Assert.Equal(At(12), focus!.Value.Start);
        Assert.Equal(TimeSpan.FromMinutes(25), focus.Value.Length);
        Assert.Equal(2, FreeTime.Between(meetings, At(9), At(18), FreeTime.Listable).Count);
        Assert.Null(FreeTime.Longest(new[] { Min(540, 640), Min(660, 1080) }, At(9), At(18)));
    }

    [Fact]
    public void TheEarlierOfTwoEqualStretchesWins()
    {
        var focus = FreeTime.Longest(new[] { M(10, 11), M(12, 17) }, At(9), At(18));
        Assert.Equal(At(9), focus!.Value.Start);
    }

    [Fact]
    public void NoMeetingsLeaveTheRestOfTheDay_AndNothingAfterItsEnd()
    {
        Assert.Equal((At(9), At(18)), FreeTime.Between(Array.Empty<Meeting>(), At(9), At(18)).Select(f => (f.Start, f.End)).Single());
        Assert.Empty(FreeTime.Between(Array.Empty<Meeting>(), At(19), At(18)));
    }

    [Theory]
    [InlineData(100, 25, 25)]
    [InlineData(100, 50, 50)]
    [InlineData(100, 90, 90)]
    [InlineData(60, 90, 50)]
    [InlineData(40, 50, 25)]
    [InlineData(30, 90, 25)]
    [InlineData(200, 120, 120)] // a usual length set by hand
    public void TheFocusBlockFitsTheStretch(int freeMinutes, int usual, int expected) =>
        Assert.Equal(expected, FreeTime.FocusMinutesFor(new FreeStretch(At(9), At(9).AddMinutes(freeMinutes)), usual));
}

public class TodaysThreeTests
{
    static readonly DateOnly Tuesday = new(2026, 9, 29), Wednesday = new(2026, 9, 30);

    [Fact]
    public void KeepsUpToThreeNonBlankItems()
    {
        var three = new TodaysThree();
        three.Set(new[] { "  Finish the report ", "", null, "Review #46", "Call the bank", "Fourth thing" }, Tuesday);
        Assert.Equal(new[] { "Finish the report", "Review #46", "Call the bank" }, three.Items.Select(i => i.Text));
        Assert.Equal("2026-09-29", three.Day);
        three.Set(new[] { new string('x', 200) }, Tuesday);
        Assert.Equal(TodaysThree.MaxLength, Assert.Single(three.Items).Text.Length);
    }

    [Fact]
    public void AClickTicksAndUnticks_AndFinishingCountsOnceADay()
    {
        var three = new TodaysThree();
        three.Set(new[] { "a", "b" }, Tuesday);
        Assert.False(three.Toggle(0, Tuesday));
        Assert.True(three.Items[0].Done);
        Assert.True(three.Toggle(1, Tuesday)); // the last one: the list is done
        Assert.True(three.AllDone);
        Assert.False(three.Toggle(1, Tuesday)); // unticked
        Assert.False(three.Toggle(1, Tuesday)); // ticked again the same day: it counted already
        Assert.False(three.Toggle(5, Tuesday));
    }

    [Fact]
    public void TheRestCarriesOverToTomorrow_WhatGotDoneGoes()
    {
        var three = new TodaysThree();
        three.Set(new[] { "report", "review", "call" }, Tuesday);
        three.Toggle(1, Tuesday);
        var (done, carried) = three.CarryOver();
        Assert.Equal((1, 2), (done, carried));
        Assert.Equal(new[] { "report", "call" }, three.Items.Select(i => i.Text));
        Assert.All(three.Items, i => Assert.True(i.Carried && !i.Done));
    }

    [Fact]
    public void ANewDayCarriesOverByItself_WhenTheEndOfDayCardNeverCame()
    {
        var three = new TodaysThree();
        three.Set(new[] { "report", "review" }, Tuesday);
        three.Toggle(0, Tuesday);
        Assert.False(three.RollOver(Tuesday));
        Assert.True(three.RollOver(Wednesday));
        Assert.Equal("review", Assert.Single(three.Items).Text);
        Assert.True(three.Items[0].Carried);
        Assert.Equal("2026-09-30", three.Day);
        Assert.False(three.RollOver(Wednesday));
    }

    [Fact]
    public void RetypingKeepsTicksAndCarriedMarks()
    {
        var three = new TodaysThree();
        three.Set(new[] { "report", "review" }, Tuesday);
        three.CarryOver();
        three.RollOver(Wednesday);
        three.Toggle(0, Wednesday);
        three.Set(new[] { "report", "review", "lunch with Ada" }, Wednesday);
        Assert.True(three.Items[0].Done && three.Items[0].Carried);
        Assert.False(three.Items[2].Carried);
    }

    [Fact]
    public void AnEmptyListIsNeverDone() => Assert.False(new TodaysThree().AllDone);
}

public class MorningTests
{
    static readonly DateTime Tuesday = new(2026, 9, 29), Saturday = new(2026, 10, 3), Monday = new(2026, 9, 28);

    [Fact]
    public void ComesOnceAWorkingDay_AtTheFirstActivity()
    {
        var m = new MorningCue();
        Assert.False(m.Step(Tuesday.AddHours(8), atComputer: false, busy: false, end: null));
        Assert.True(m.Step(Tuesday.AddHours(8.5), true, false, null));
        Assert.Equal("2026-09-29", m.ShownOn);
        Assert.False(m.Step(Tuesday.AddHours(9), true, false, null));
        Assert.True(m.Step(Tuesday.AddDays(1).AddHours(9), true, false, null));
    }

    [Fact]
    public void WaitsForAPresentationOrAMeetingToEnd()
    {
        var m = new MorningCue();
        Assert.False(m.Step(Tuesday.AddHours(9), true, busy: true, null));
        Assert.Null(m.ShownOn);
        Assert.True(m.Step(Tuesday.AddHours(10), true, false, null));
    }

    [Fact]
    public void NotAtTheWeekend_NorAtNight_NorLateInTheDay()
    {
        var m = new MorningCue();
        Assert.False(m.Step(Saturday.AddHours(10), true, false, null));
        Assert.False(m.Step(Tuesday.AddHours(3), true, false, null)); // a late night, not a morning
        Assert.False(m.Step(Tuesday.AddHours(17.5), true, false, null)); // an hour before 18:00
        Assert.False(m.Step(Tuesday.AddHours(15.5), true, false, new TimeOnly(16, 0)));
        Assert.True(m.Step(Tuesday.AddHours(14.5), true, false, new TimeOnly(16, 0)));
    }

    [Theory]
    [InlineData(2026, 9, 28, 2026, 9, 25)] // Monday reads Friday
    [InlineData(2026, 9, 29, 2026, 9, 28)] // Tuesday reads Monday
    [InlineData(2026, 10, 4, 2026, 10, 2)] // Sunday reads Friday
    [InlineData(2026, 10, 3, 2026, 10, 2)] // Saturday reads Friday
    public void TheLastWorkingDay(int y, int mo, int d, int ey, int em, int ed) =>
        Assert.Equal(new DateOnly(ey, em, ed), WorkDays.LastWorkingDay(new DateOnly(y, mo, d)));

    [Fact]
    public void MondaysStandupCoversFridayAndTheWeekend()
    {
        var (from, to) = WorkDays.StandupRange(DateOnly.FromDateTime(Monday));
        Assert.Equal(new DateTime(2026, 9, 25), from);
        Assert.Equal(new DateTime(2026, 9, 28), to);
    }

    [Fact]
    public void TheWaitReportIsOfferedOnTheFirstMorningOfAWeek()
    {
        var m = new MorningCue();
        Assert.True(m.TakeWeeklyReport(new DateOnly(2026, 9, 29))); // a Tuesday after a Monday off
        Assert.False(m.TakeWeeklyReport(new DateOnly(2026, 9, 30)));
        Assert.False(m.TakeWeeklyReport(new DateOnly(2026, 10, 2)));
        Assert.True(m.TakeWeeklyReport(new DateOnly(2026, 10, 5)));
        Assert.Equal("2026-10-05", m.ReportWeek);
    }

    [Fact]
    public void TheEndOfTheDayComesHalfAnHourEarlierOnAFridayWithRepos()
    {
        var six = new TimeOnly(18, 0);
        Assert.Equal(new TimeOnly(17, 30), EndOfDay.EndOn(new DateOnly(2026, 10, 2), six, fridayEarly: true));
        Assert.Equal(six, EndOfDay.EndOn(new DateOnly(2026, 10, 2), six, fridayEarly: false));
        Assert.Equal(six, EndOfDay.EndOn(new DateOnly(2026, 10, 1), six, fridayEarly: true)); // Thursday
        Assert.Null(EndOfDay.EndOn(new DateOnly(2026, 10, 2), null, fridayEarly: true));

        var d = new EndOfDay();
        var friday = new DateTime(2026, 10, 2);
        var end = EndOfDay.EndOn(DateOnly.FromDateTime(friday), six, true);
        Assert.Null(d.Step(friday.AddHours(17.4), end, true, 8 * 3600));
        Assert.Equal(DayCue.WrapUp, d.Step(friday.AddHours(17.5), end, true, 8 * 3600));
        Assert.Equal(DayCue.StillHere, d.Step(friday.AddHours(18.5), end, true, 8 * 3600));
    }
}

public class DayHistoryTests
{
    static Stats Fresh(out string path, Func<DateTime> clock)
    {
        path = Path.Combine(Path.GetTempPath(), "da-stats-" + Guid.NewGuid().ToString("N") + ".json");
        var stats = Stats.Load(path);
        stats.Clock = clock;
        return stats;
    }

    [Fact]
    public void ADayKeepsItsWorkCountersWhenItIsOver()
    {
        var now = new DateTime(2026, 9, 25, 17, 0, 0); // a Friday
        var stats = Fresh(out string path, () => now);
        try
        {
            stats.Add("work.focus", 3);
            stats.Add("work.meetings", 2);
            stats.Add("work.seconds", 7 * 3600);
            stats.Add("hoops.baskets", 40); // a game's counter is not the working day's
            now = new DateTime(2026, 9, 28, 9, 0, 0); // Monday
            Assert.Equal(0, stats.Today("work.focus"));
            var friday = stats.OnDay(new DateOnly(2026, 9, 25));
            Assert.Equal(3, friday["work.focus"]);
            Assert.Equal(2, friday["work.meetings"]);
            Assert.False(friday.ContainsKey("hoops.baskets"));
            // Monday reads Friday (and the weekend, when nothing happened)
            var (from, to) = WorkDays.StandupRange(new DateOnly(2026, 9, 28));
            Assert.Equal(3, stats.OverDays("work.focus", DateOnly.FromDateTime(from), DateOnly.FromDateTime(to)));
            Assert.Equal(7 * 3600, stats.OverDays("work.seconds", DateOnly.FromDateTime(from), DateOnly.FromDateTime(to)));

            stats.Add("work.focus");
            stats.Save();
            var again = Stats.Load(path);
            again.Clock = () => now;
            Assert.Equal(3, again.OnDay(new DateOnly(2026, 9, 25))["work.focus"]);
            Assert.Equal(1, again.OnDay(new DateOnly(2026, 9, 28))["work.focus"]); // today, as it stands
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void KeepsAboutSixtyDays()
    {
        var now = new DateTime(2026, 6, 1, 12, 0, 0);
        var stats = Fresh(out string path, () => now);
        try
        {
            for (int i = 0; i < 90; i++)
            {
                stats.Add("work.focus", i + 1);
                now = now.AddDays(1);
                stats.Today("work.focus"); // the day rolls over
            }
            Assert.Empty(stats.OnDay(DateOnly.FromDateTime(now.AddDays(-Stats.HistoryDays - 1))));
            Assert.Equal(90, stats.OnDay(DateOnly.FromDateTime(now.AddDays(-1)))["work.focus"]);
            Assert.Equal(90 - Stats.HistoryDays + 1, stats.OnDay(DateOnly.FromDateTime(now.AddDays(-Stats.HistoryDays)))["work.focus"]);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class WaitReportTests
{
    static readonly DateTime End = new(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc); // a Monday

    static WaitEntry W(WaitKind kind, string label, double daysAgo, double seconds, double played = 0) =>
        new(kind, label, End.AddDays(-daysAgo), seconds, null, played);

    [Fact]
    public void SumsTheWeekByKind_AndTheTimePlayed()
    {
        var entries = new[]
        {
            W(WaitKind.Command, "dotnet build", 1, 60, 30),
            W(WaitKind.Lane, "deploy", 2, 120, 0),
            W(WaitKind.Ci, "CI · main", 3, 600, 300),
            W(WaitKind.Agent, "api", 4, 300, 150),
            W(WaitKind.Download, "big.iso", 5, 90, 90),
            W(WaitKind.Command, "dotnet build", 9, 999, 999), // the week before: not in these sums
        };
        var r = WaitReport.Build(entries, End);
        Assert.Equal(new WaitSum(2, 180, 30), r.Builds);
        Assert.Equal(new WaitSum(1, 600, 300), r.Ci);
        Assert.Equal(new WaitSum(1, 300, 150), r.Agents);
        Assert.Equal(new WaitSum(1, 90, 90), r.Downloads);
        Assert.Equal(new WaitSum(5, 1170, 570), r.All);
        Assert.Equal(570.0 / 1170, r.PlayedShare, 6);
        Assert.Equal(End.AddDays(-7), r.FromUtc);
    }

    [Fact]
    public void TheSlowestCommandsByAverage()
    {
        var entries = new[]
        {
            W(WaitKind.Command, "dotnet test", 1, 240), W(WaitKind.Command, "dotnet test", 2, 260), W(WaitKind.Command, "Dotnet Test", 3, 250),
            W(WaitKind.Command, "npm run build", 1, 90),
            W(WaitKind.Lane, "deploy", 1, 400),
            W(WaitKind.Ci, "CI · main", 1, 900), // CI is not a command
        };
        var r = WaitReport.Build(entries, End);
        Assert.Equal(new[] { "deploy", "dotnet test", "npm run build" }, r.Slowest.Select(c => c.Label));
        var test = r.Slowest[1];
        Assert.Equal(3, test.Runs);
        Assert.Equal(250, test.AverageSeconds, 6);
        Assert.Contains("4:10", WaitReport.SlowLine(test));
    }

    [Fact]
    public void ACommandGettingSlower_AgainstTheFourWeeksBefore()
    {
        var entries = new List<WaitEntry>();
        // the four weeks before: 3:00 on average
        foreach (double day in new[] { 8, 12, 16, 20, 24, 28, 34 }) entries.Add(W(WaitKind.Command, "dotnet test", day, 180));
        entries.Add(W(WaitKind.Command, "dotnet test", 40, 9999)); // five weeks ago: too old to count
        // this week: 4:12, 40 % up
        foreach (double day in new[] { 1, 2, 3 }) entries.Add(W(WaitKind.Command, "dotnet test", day, 252));
        var r = WaitReport.Build(entries, End);
        var trend = Assert.Single(r.Slower);
        Assert.Equal("dotnet test", trend.Label);
        Assert.Equal(252, trend.AverageSeconds, 6);
        Assert.Equal(180, trend.EarlierAverage, 6);
        Assert.Equal(7, trend.EarlierRuns);
        Assert.Equal(40, trend.Percent);
        string line = WaitReport.TrendLine(trend);
        Assert.Contains("dotnet test", line);
        Assert.Contains("4:12", line);
        Assert.Contains("40", line);
    }

    [Fact]
    public void FasterSteadyOrThinCommandsAreNotGettingSlower()
    {
        var entries = new List<WaitEntry>
        {
            // faster
            W(WaitKind.Command, "build", 10, 300), W(WaitKind.Command, "build", 11, 300), W(WaitKind.Command, "build", 1, 200), W(WaitKind.Command, "build", 2, 200),
            // a little slower: 5 % and 2 seconds is noise
            W(WaitKind.Command, "lint", 10, 40), W(WaitKind.Command, "lint", 11, 40), W(WaitKind.Command, "lint", 1, 42), W(WaitKind.Command, "lint", 2, 42),
            // much slower, but once only this week
            W(WaitKind.Command, "e2e", 10, 100), W(WaitKind.Command, "e2e", 11, 100), W(WaitKind.Command, "e2e", 1, 500),
            // new this week
            W(WaitKind.Command, "bench", 1, 100), W(WaitKind.Command, "bench", 2, 300),
        };
        Assert.Empty(WaitReport.Build(entries, End).Slower);
    }

    [Fact]
    public void CiRunsCanGetSlowerToo_TheBiggestChangeFirst()
    {
        var entries = new List<WaitEntry>
        {
            W(WaitKind.Ci, "CI · main", 10, 600), W(WaitKind.Ci, "CI · main", 12, 600), W(WaitKind.Ci, "CI · main", 1, 900), W(WaitKind.Ci, "CI · main", 2, 900),
            W(WaitKind.Command, "make", 10, 100), W(WaitKind.Command, "make", 12, 100), W(WaitKind.Command, "make", 1, 120), W(WaitKind.Command, "make", 2, 120),
        };
        var r = WaitReport.Build(entries, End);
        Assert.Equal(new[] { ("CI · main", 50), ("make", 20) }, r.Slower.Select(t => (t.Label, t.Percent)));
    }

    [Fact]
    public void LastWeekRunsMondayToSunday()
    {
        var zone = TimeZoneInfo.Utc;
        var entries = new[] { W(WaitKind.Command, "make", 0.5, 60), W(WaitKind.Command, "make", -0.5, 70) }; // Sunday, then Monday
        var r = WaitReport.LastWeek(entries, new DateOnly(2026, 9, 30), zone); // a Wednesday
        Assert.Equal(End, r.ToUtc);
        Assert.Equal(new WaitSum(1, 60, 0), r.All);
        Assert.Equal("21.09–27.09", r.Week(zone));
    }

    [Fact]
    public void AnEmptyWeekSaysSo()
    {
        var r = WaitReport.Build(Array.Empty<WaitEntry>(), End);
        Assert.True(r.Empty);
        Assert.Equal(0, r.PlayedShare);
        Assert.Empty(r.SumLines());
    }

    [Fact]
    public void TheTextHasTheNumbers()
    {
        var entries = new List<WaitEntry>();
        foreach (double day in new[] { 8, 15, 22 }) entries.Add(W(WaitKind.Command, "dotnet test", day, 180));
        foreach (double day in new[] { 1, 2 }) entries.Add(W(WaitKind.Command, "dotnet test", day, 252, 126));
        string text = WaitReport.Build(entries, End).Text(TimeZoneInfo.Utc);
        Assert.Contains("21.09–27.09", text);
        Assert.Contains("dotnet test", text);
        Assert.Contains("4:12", text);
        Assert.Contains("40", text);
        Assert.Contains("50", text); // half the time played
    }
}

public class AppTimeTests
{
    static readonly DateOnly Day = new(2026, 9, 30);

    [Fact]
    public void SumsMinutesPerProgram_TheTopFirst_TheRestAsOther()
    {
        var apps = AppTime.InMemory();
        for (int i = 0; i < 250; i++) apps.AddMinute(Day, "Code");
        for (int i = 0; i < 115; i++) apps.AddMinute(Day, AppTime.Browser);
        apps.AddMinute(Day, "Teams", 80);
        apps.AddMinute(Day, "Slack", 30);
        apps.AddMinute(Day, "Explorer", 10);
        apps.AddMinute(Day.AddDays(-1), "Code", 5);
        Assert.Equal(250, apps.OnDay(Day)["Code"]);
        var top = apps.Top(Day, 3);
        Assert.Equal(new (string?, int)[] { ("Code", 250), ("browser", 115), ("Teams", 80), (null, 40) }, top);
        Assert.Equal("4:10", AppTime.HoursMinutes(250));
        Assert.Equal("0:05", AppTime.HoursMinutes(5));
        Assert.Empty(apps.Top(Day.AddDays(1), 3));
    }

    [Fact]
    public void KeepsThirtyDays()
    {
        var apps = AppTime.InMemory();
        for (int i = 0; i < 40; i++) apps.AddMinute(Day.AddDays(i), "Code");
        Assert.Equal(AppTime.KeepDays, apps.Days.Count());
        Assert.Equal(Day.AddDays(10), apps.Days.First());
        Assert.Empty(apps.OnDay(Day.AddDays(9)));
    }

    [Fact]
    public void SavesACsvForATimesheet()
    {
        var apps = AppTime.InMemory();
        apps.AddMinute(Day, "Code", 90);
        apps.AddMinute(Day, "Word, Excel & co", 15);
        apps.AddMinute(Day, "say \"hi\"", 1);
        apps.AddMinute(Day.AddDays(-1), AppTime.Browser, 45);
        string csv = apps.Csv(key => key == AppTime.Browser ? "Browser" : key);
        Assert.Equal(
            "date,app,minutes,hours\r\n" +
            "2026-09-29,Browser,45,0.75\r\n" +
            "2026-09-30,Code,90,1.50\r\n" +
            "2026-09-30,\"Word, Excel & co\",15,0.25\r\n" +
            "2026-09-30,\"say \"\"hi\"\"\",1,0.02\r\n", csv);
    }

    [Fact]
    public void SavesAndLoads()
    {
        string path = Path.Combine(Path.GetTempPath(), "da-apps-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var apps = AppTime.Load(path);
            apps.AddMinute(Day, "Code", 3);
            apps.Save();
            Assert.Equal(3, AppTime.Load(path).OnDay(Day)["Code"]);
            File.WriteAllText(path, "{ not json");
            Assert.Empty(AppTime.Load(path).Days);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("Code", null, "Code")]
    [InlineData("code-insiders", null, "Code")]
    [InlineData("chrome", null, "browser")]
    [InlineData("msedge.exe", null, "browser")]
    [InlineData("firefox", null, "browser")]
    [InlineData("WindowsTerminal", null, "terminal")]
    [InlineData("gnome-terminal-", null, "terminal")] // /proc's 15 characters
    [InlineData("ms-teams", null, "Teams")]
    [InlineData("OUTLOOK", null, "Outlook")]
    [InlineData("devenv", null, "Visual Studio")]
    [InlineData("rider64", null, "Rider")]
    [InlineData("DeskArcade", null, "Desk Arcade")]
    [InlineData("someapp", null, "someapp")]
    [InlineData("Electron", "/Applications/Visual Studio Code.app/Contents/MacOS/Electron", "Code")]
    [InlineData("Electron", "/Applications/Figma Agent.app/Contents/MacOS/Electron", "Figma Agent")]
    [InlineData("LockApp", null, null)]
    [InlineData("", null, null)]
    public void NamesProgramsFriendly(string process, string? path, string? expected) => Assert.Equal(expected, AppTime.Friendly(process, path));
}

[Collection("git")]
public class GitRepoTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "da-git-" + Guid.NewGuid().ToString("N")[..10]);
    readonly string _emptyConfig;

    public GitRepoTests()
    {
        Directory.CreateDirectory(_root);
        _emptyConfig = Path.Combine(_root, "empty.gitconfig");
        File.WriteAllText(_emptyConfig, "");
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in new DirectoryInfo(_root).EnumerateFiles("*", SearchOption.AllDirectories)) file.Attributes = FileAttributes.Normal; // git's read-only objects
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Runs git for setting a repo up, away from the user's own configuration, at a given date when one is set.</summary>
    string Git(string folder, string args, DateTime? localDate = null)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        psi.Environment["GIT_CONFIG_GLOBAL"] = _emptyConfig;
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (localDate is DateTime d)
        {
            string iso = new DateTimeOffset(d, TimeZoneInfo.Local.GetUtcOffset(d)).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
            psi.Environment["GIT_AUTHOR_DATE"] = iso;
            psi.Environment["GIT_COMMITTER_DATE"] = iso;
        }
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync();
        string error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {args}: {error}");
        return output.Result;
    }

    string NewRepo(string name, string email = "me@example.com")
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        Git(dir, "init -q -b main");
        Git(dir, $"config user.email {email}");
        Git(dir, "config user.name Me");
        Git(dir, "config commit.gpgsign false");
        return dir;
    }

    void Commit(string dir, string message, DateTime? localDate = null, string? author = null)
    {
        File.AppendAllText(Path.Combine(dir, "file.txt"), message + "\n");
        Git(dir, "add -A");
        Git(dir, $"commit -q -m \"{message}\"" + (author != null ? $" --author=\"{author}\"" : ""), localDate);
    }

    /// <summary>A repo with a bare repo beside it as origin, main pushed and tracking it.</summary>
    string PushedRepo(string name)
    {
        string bare = Path.Combine(_root, name + ".git");
        Directory.CreateDirectory(bare);
        Git(bare, "init -q --bare -b main");
        string dir = NewRepo(name);
        Commit(dir, "first");
        Git(dir, $"remote add origin \"{bare}\"");
        Git(dir, "push -q -u origin main");
        return dir;
    }

    static DateTime Utc(DateTime local) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeZoneInfo.Local);

    [Fact]
    public async Task OnMondayTheStandupReadsFridaysCommitsByMyEmail()
    {
        string dir = NewRepo("api");
        Commit(dir, "Thursday's work", new DateTime(2026, 9, 24, 16, 0, 0));
        Commit(dir, "Fix the login redirect", new DateTime(2026, 9, 25, 10, 15, 0));
        Commit(dir, "Add the health check", new DateTime(2026, 9, 25, 17, 40, 0));
        Commit(dir, "Someone else's fix", new DateTime(2026, 9, 25, 12, 0, 0), author: "Grace <grace@example.com>");
        Git(dir, "checkout -q -b feature");
        Commit(dir, "A weekend idea on a branch", new DateTime(2026, 9, 26, 11, 0, 0));
        Git(dir, "checkout -q main");
        Commit(dir, "Monday morning", new DateTime(2026, 9, 28, 9, 5, 0));

        var (from, to) = WorkDays.StandupRange(new DateOnly(2026, 9, 28));
        var notes = await GitRepos.CommitsAsync(dir, Utc(from), Utc(to));
        Assert.Equal("api", notes.Repo);
        Assert.Null(notes.Problem);
        Assert.Equal(new[] { "Fix the login redirect", "Add the health check", "A weekend idea on a branch" }, notes.Commits.Select(c => c.Subject));
        Assert.Equal(40, notes.Commits[0].Hash.Length);

        // the same repo read on Tuesday has Monday's only
        var (tFrom, tTo) = WorkDays.StandupRange(new DateOnly(2026, 9, 29));
        Assert.Equal("Monday morning", Assert.Single((await GitRepos.CommitsAsync(dir, Utc(tFrom), Utc(tTo))).Commits).Subject);
    }

    [Fact]
    public async Task ACommitRebasedTodayStillCountsForTheDayItWasWritten()
    {
        string dir = NewRepo("web");
        var friday = new DateTime(2026, 9, 25, 15, 0, 0);
        Commit(dir, "Written on Friday", friday);
        // amended on Monday morning: the commit date moves to Monday, the author date stays on Friday
        string fridayIso = new DateTimeOffset(friday, TimeZoneInfo.Local.GetUtcOffset(friday)).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
        Git(dir, $"commit -q --amend --no-edit --date=\"{fridayIso}\"", new DateTime(2026, 9, 28, 8, 0, 0));
        Assert.Contains("2026-09-28", Git(dir, "log -1 --format=%ci"));
        var (from, to) = WorkDays.StandupRange(new DateOnly(2026, 9, 28));
        Assert.Equal("Written on Friday", Assert.Single((await GitRepos.CommitsAsync(dir, Utc(from), Utc(to))).Commits).Subject);
    }

    [Fact]
    public async Task WithoutAGitEmailThereAreNoCommitsToShow()
    {
        string dir = Path.Combine(_root, "noemail");
        Directory.CreateDirectory(dir);
        Git(dir, "init -q -b main");
        // the user's own global config may have an email: the repo's own empty one stands in front of it
        Git(dir, "config user.email \"\"");
        var notes = await GitRepos.CommitsAsync(dir, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);
        Assert.True(notes.NoEmail);
        Assert.Empty(notes.Commits);
        Assert.Equal("folder not found", (await GitRepos.CommitsAsync(Path.Combine(_root, "gone"), DateTime.UtcNow, DateTime.UtcNow)).Problem);
    }

    [Fact]
    public async Task ACleanPushedRepoHasNothingToDo()
    {
        var state = await GitRepos.StateAsync(PushedRepo("clean"));
        Assert.True(state.Clean, state.ToString());
        Assert.False(state.NoRemote);
    }

    [Fact]
    public async Task UncommittedChangesAreCounted()
    {
        string dir = PushedRepo("dirty");
        File.AppendAllText(Path.Combine(dir, "file.txt"), "more\n");
        File.WriteAllText(Path.Combine(dir, "new.txt"), "new");
        var state = await GitRepos.StateAsync(dir);
        Assert.Equal((2, 0, 0), (state.Changed, state.Unpushed, state.Stashes));
        Assert.False(state.Clean);
    }

    [Fact]
    public async Task CommitsAheadOfTheUpstreamAreNotPushed()
    {
        string dir = PushedRepo("ahead");
        Commit(dir, "one");
        Commit(dir, "two");
        var state = await GitRepos.StateAsync(dir);
        Assert.Equal((0, 2, 0, false), (state.Changed, state.Unpushed, state.Stashes, state.NoRemote));
        Assert.Equal("ahead: 2 commits not pushed", OfficeDesk.RepoLine(state));
        Git(dir, "push -q");
        Assert.True((await GitRepos.StateAsync(dir)).Clean);
    }

    [Fact]
    public async Task ABranchNeverPushedCounts_EachCommitOnce()
    {
        string dir = PushedRepo("branches");
        Git(dir, "checkout -q -b feature");
        Commit(dir, "feature work");
        Git(dir, "checkout -q -b feature-2");
        Commit(dir, "more feature work"); // feature-2 holds feature's commit too
        Git(dir, "checkout -q main");
        var state = await GitRepos.StateAsync(dir);
        Assert.Equal(2, state.Unpushed);
    }

    [Fact]
    public async Task ARepoWithNoRemoteHasNothingPushed()
    {
        string dir = NewRepo("local");
        Commit(dir, "one");
        Commit(dir, "two");
        Commit(dir, "three");
        var state = await GitRepos.StateAsync(dir);
        Assert.True(state.NoRemote);
        Assert.Equal(3, state.Unpushed);
        Assert.Equal("local: 3 commits not pushed (no remote)", OfficeDesk.RepoLine(state));
    }

    [Fact]
    public async Task StashesAreCounted()
    {
        string dir = PushedRepo("stashed");
        File.AppendAllText(Path.Combine(dir, "file.txt"), "wip 1\n");
        Git(dir, "stash push -q -m first");
        File.AppendAllText(Path.Combine(dir, "file.txt"), "wip 2\n");
        Git(dir, "stash push -q -m second");
        var state = await GitRepos.StateAsync(dir);
        Assert.Equal((0, 0, 2), (state.Changed, state.Unpushed, state.Stashes));
        Assert.Equal("stashed: 2 stashes", OfficeDesk.RepoLine(state));
    }

    [Fact]
    public async Task EveryStateAtOnce_AndAFolderThatIsNoRepo()
    {
        string dir = PushedRepo("everything");
        Commit(dir, "ahead");
        File.AppendAllText(Path.Combine(dir, "file.txt"), "wip\n");
        Git(dir, "stash push -q");
        File.WriteAllText(Path.Combine(dir, "new.txt"), "new");
        string plain = Path.Combine(_root, "plain");
        Directory.CreateDirectory(plain);
        var states = await GitRepos.StatesAsync(new[] { dir, plain, dir, Path.Combine(_root, "gone") });
        Assert.Equal(3, states.Count);
        Assert.Equal("everything: 1 uncommitted change, 1 commit not pushed, 1 stash", OfficeDesk.RepoLine(states[0]));
        if (!InsideARepo(_root)) Assert.NotNull(states[1].Problem); // a plain folder, unless the temp folder sits in a repo itself
        Assert.Equal("folder not found", states[2].Problem);
        Assert.False(states[2].Clean);
    }

    static bool InsideARepo(string folder)
    {
        for (var d = new DirectoryInfo(folder); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, ".git"))) return true;
        return false;
    }

    [Fact]
    public async Task MergedPullRequestsComeFromGh()
    {
        const string json = """
            [
              {"number":47,"title":"At work: office life","url":"https://github.com/o/r/pull/47","mergedAt":"2026-09-25T13:05:00Z"},
              {"number":46,"title":"Fix the LAN rejoin loop","url":"https://github.com/o/r/pull/46","mergedAt":"2026-09-25T09:30:00Z"},
              {"number":40,"title":"Too early","url":"https://github.com/o/r/pull/40","mergedAt":"2026-09-23T09:30:00Z"},
              {"number":41,"title":"No date","url":"https://github.com/o/r/pull/41","mergedAt":null}
            ]
            """;
        var real = Cli.Runner;
        var asked = new List<string>();
        Cli.Runner = (tool, args, folder, timeout, ct) =>
        {
            asked.Add(tool + " " + string.Join(' ', args));
            return Task.FromResult(new CliResult(0, json, ""));
        };
        try
        {
            var from = new DateTime(2026, 9, 25, 0, 0, 0, DateTimeKind.Utc);
            var pulls = await GitRepos.MergedPullsAsync(_root, from, from.AddDays(1));
            Assert.Equal(new[] { 46, 47 }, pulls.Select(p => p.Number));
            Assert.Equal("Fix the LAN rejoin loop", pulls[0].Title);
            string call = Assert.Single(asked);
            Assert.StartsWith("gh pr list --state merged --author @me", call);
            Assert.Contains("merged:>=2026-09-24", call);
        }
        finally
        {
            Cli.Runner = real;
        }
        Assert.Empty(GitRepos.ParseMergedPulls("not json", DateTime.MinValue, DateTime.MaxValue));
        Assert.Empty(GitRepos.ParseMergedPulls("{\"message\":\"Bad credentials\"}", DateTime.MinValue, DateTime.MaxValue));
    }
}
