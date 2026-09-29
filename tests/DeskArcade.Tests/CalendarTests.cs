using System;
using System.Linq;
using DeskArcade.Office;
using Xunit;

namespace DeskArcade.Tests;

/// <summary>Reading .ics calendars the way Outlook and Google publish them: repeats, zones, exceptions, moved meetings.</summary>
public class CalendarTests
{
    static DateTime Utc(int y, int mo, int d, int h, int mi = 0) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    static IcsCalendar Cal(string events, string zones = "")
    {
        var cal = IcsCalendar.Parse("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\n" + zones + events + "END:VCALENDAR\r\n");
        cal.Local = TimeZoneInfo.Utc; // floating times read as UTC, whatever zone the test machine is in
        return cal;
    }

    static string Event(string body) => "BEGIN:VEVENT\r\n" + body.Replace("\n", "\r\n") + "\r\nEND:VEVENT\r\n";

    // Central European time as a VTIMEZONE under a name no system knows, so only the file's own rules can place it
    const string Cet = """
        BEGIN:VTIMEZONE
        TZID:Customized Time Zone
        BEGIN:STANDARD
        DTSTART:16010101T030000
        TZOFFSETFROM:+0200
        TZOFFSETTO:+0100
        RRULE:FREQ=YEARLY;BYDAY=-1SU;BYMONTH=10
        END:STANDARD
        BEGIN:DAYLIGHT
        DTSTART:16010101T020000
        TZOFFSETFROM:+0100
        TZOFFSETTO:+0200
        RRULE:FREQ=YEARLY;BYDAY=-1SU;BYMONTH=3
        END:DAYLIGHT
        END:VTIMEZONE

        """;

    [Fact]
    public void ReadsASingleMeetingInUtc()
    {
        var cal = Cal(Event("UID:1\nSUMMARY:Standup\nDTSTART:20260929T100000Z\nDTEND:20260929T101500Z"));
        var m = Assert.Single(cal.Between(Utc(2026, 9, 29, 9), Utc(2026, 9, 29, 12)));
        Assert.Equal("Standup", m.Title);
        Assert.Equal(Utc(2026, 9, 29, 10), m.Start);
        Assert.Equal(Utc(2026, 9, 29, 10, 15), m.End);
    }

    [Fact]
    public void UnfoldsLinesAndUnescapesText()
    {
        var cal = Cal("BEGIN:VEVENT\r\nUID:2\r\nSUMMARY:Design review\\, part\r\n  2\\; bring notes\r\nDTSTART:20260929T100000Z\r\nDURATION:PT45M\r\nEND:VEVENT\r\n");
        var m = Assert.Single(cal.Between(Utc(2026, 9, 29, 0), Utc(2026, 9, 30, 0)));
        Assert.Equal("Design review, part 2; bring notes", m.Title);
        Assert.Equal(TimeSpan.FromMinutes(45), m.End - m.Start);
    }

    [Fact]
    public void LeavesOutAllDayAndCancelledEvents()
    {
        var cal = Cal(Event("UID:3\nSUMMARY:Holiday\nDTSTART;VALUE=DATE:20260929\nDTEND;VALUE=DATE:20260930")
            + Event("UID:4\nSUMMARY:Old sync\nSTATUS:CANCELLED\nDTSTART:20260929T110000Z"));
        Assert.Empty(cal.Between(Utc(2026, 9, 28, 0), Utc(2026, 10, 1, 0)));
        Assert.Equal(1, cal.EventCount); // the cancelled one is read, the all-day one is not
    }

    [Fact]
    public void KeepsAMeetingThatHasStartedButNotEnded()
    {
        var cal = Cal(Event("UID:5\nSUMMARY:Planning\nDTSTART:20260929T090000Z\nDTEND:20260929T110000Z"));
        Assert.Single(cal.Between(Utc(2026, 9, 29, 10), Utc(2026, 9, 29, 12)));
        Assert.Empty(cal.Between(Utc(2026, 9, 29, 11), Utc(2026, 9, 29, 12)));
    }

    [Fact]
    public void RepeatsWeeklyOnSomeDaysAcrossAClockChange()
    {
        // 09:30 in Berlin on Mon, Wed and Fri; the clocks go back on Sunday 25 October 2026
        var cal = Cal(Event("UID:6\nSUMMARY:Standup\nDTSTART;TZID=Customized Time Zone:20260921T093000\nDTEND;TZID=Customized Time Zone:20260921T094500\nRRULE:FREQ=WEEKLY;BYDAY=MO,WE,FR"), Cet);
        var starts = cal.Between(Utc(2026, 10, 19, 0), Utc(2026, 10, 31, 0)).Select(m => m.Start).ToList();
        Assert.Equal(new[]
        {
            Utc(2026, 10, 19, 7, 30), Utc(2026, 10, 21, 7, 30), Utc(2026, 10, 23, 7, 30), // summer time, UTC+2
            Utc(2026, 10, 26, 8, 30), Utc(2026, 10, 28, 8, 30), Utc(2026, 10, 30, 8, 30), // winter time, UTC+1
        }, starts);
    }

    [Theory]
    [InlineData("Europe/Berlin")]
    [InlineData("W. Europe Standard Time")]
    [InlineData("/mozilla.org/20050126_1/Europe/Berlin")]
    public void KnowsZonesByTheirIanaOrWindowsNames(string zone)
    {
        var cal = Cal(Event($"UID:7\nSUMMARY:Sync\nDTSTART;TZID={zone}:20260715T100000\nDTEND;TZID={zone}:20260715T103000"));
        Assert.Equal(Utc(2026, 7, 15, 8), Assert.Single(cal.Between(Utc(2026, 7, 15, 0), Utc(2026, 7, 16, 0))).Start);
    }

    [Fact]
    public void WindowsZoneNamesWorkWithTheFilesOwnBlockToo()
    {
        const string pacific = """
            BEGIN:VTIMEZONE
            TZID:Pacific Standard Time
            BEGIN:STANDARD
            DTSTART:16011104T020000
            RRULE:FREQ=YEARLY;BYDAY=1SU;BYMONTH=11
            TZOFFSETFROM:-0700
            TZOFFSETTO:-0800
            END:STANDARD
            BEGIN:DAYLIGHT
            DTSTART:16010311T020000
            RRULE:FREQ=YEARLY;BYDAY=2SU;BYMONTH=3
            TZOFFSETFROM:-0800
            TZOFFSETTO:-0700
            END:DAYLIGHT
            END:VTIMEZONE

            """;
        var cal = Cal(Event("UID:8\nSUMMARY:All hands\nDTSTART;TZID=Pacific Standard Time:20261203T090000\nDTEND;TZID=Pacific Standard Time:20261203T100000"), pacific);
        Assert.Equal(Utc(2026, 12, 3, 17), Assert.Single(cal.Between(Utc(2026, 12, 3, 0), Utc(2026, 12, 4, 0))).Start);
    }

    [Fact]
    public void SkipsExcludedDatesAndFollowsMovedAndCancelledOccurrences()
    {
        var cal = Cal(
            Event("UID:9\nSUMMARY:Daily\nDTSTART:20260928T090000Z\nDTEND:20260928T091500Z\nRRULE:FREQ=DAILY;COUNT=5\nEXDATE:20260929T090000Z")
            + Event("UID:9\nRECURRENCE-ID:20260930T090000Z\nSUMMARY:Daily (moved)\nDTSTART:20260930T110000Z\nDTEND:20260930T111500Z")
            + Event("UID:9\nRECURRENCE-ID:20261001T090000Z\nSTATUS:CANCELLED\nDTSTART:20261001T090000Z"));
        var got = cal.Between(Utc(2026, 9, 27, 0), Utc(2026, 10, 10, 0)).Select(m => (m.Start, m.Title)).ToList();
        Assert.Equal(new[]
        {
            (Utc(2026, 9, 28, 9), "Daily"),
            (Utc(2026, 9, 30, 11), "Daily (moved)"),
            (Utc(2026, 10, 2, 9), "Daily"),
        }, got);
    }

    [Fact]
    public void StopsAtUntilAndCountsFromTheFirstOccurrence()
    {
        var cal = Cal(Event("UID:10\nSUMMARY:Retro\nDTSTART:20260105T140000Z\nRRULE:FREQ=WEEKLY;INTERVAL=2;UNTIL=20260302T140000Z"));
        var starts = cal.Between(Utc(2026, 1, 1, 0), Utc(2026, 12, 31, 0)).Select(m => m.Start.Date).ToList();
        Assert.Equal(new[] { new DateTime(2026, 1, 5), new DateTime(2026, 1, 19), new DateTime(2026, 2, 2), new DateTime(2026, 2, 16), new DateTime(2026, 3, 2) }, starts);

        var counted = Cal(Event("UID:11\nSUMMARY:Onboarding\nDTSTART:20260105T140000Z\nRRULE:FREQ=DAILY;COUNT=3"));
        // asked about a window after the third occurrence: nothing, although the repeat itself would go on
        Assert.Empty(counted.Between(Utc(2026, 1, 8, 0), Utc(2026, 1, 20, 0)));
        Assert.Equal(3, counted.Between(Utc(2026, 1, 1, 0), Utc(2026, 1, 20, 0)).Count);
    }

    [Fact]
    public void RepeatsMonthlyOnTheLastFridayAndTheLastWorkingDay()
    {
        var lastFriday = Cal(Event("UID:12\nSUMMARY:Demo\nDTSTART:20260130T150000Z\nRRULE:FREQ=MONTHLY;BYDAY=-1FR"));
        Assert.Equal(new[] { new DateTime(2026, 9, 25), new DateTime(2026, 10, 30) },
            lastFriday.Between(Utc(2026, 9, 1, 0), Utc(2026, 11, 1, 0)).Select(m => m.Start.Date));

        var lastWorkday = Cal(Event("UID:13\nSUMMARY:Timesheets\nDTSTART:20260130T160000Z\nRRULE:FREQ=MONTHLY;BYDAY=MO,TU,WE,TH,FR;BYSETPOS=-1"));
        Assert.Equal(new[] { new DateTime(2026, 5, 29), new DateTime(2026, 6, 30), new DateTime(2026, 7, 31), new DateTime(2026, 8, 31) },
            lastWorkday.Between(Utc(2026, 5, 1, 0), Utc(2026, 9, 1, 0)).Select(m => m.Start.Date));
    }

    [Fact]
    public void RepeatsOnTheSecondTuesdayAndOnADayOfTheMonth()
    {
        var second = Cal(Event("UID:14\nSUMMARY:Board\nDTSTART:20260113T100000Z\nRRULE:FREQ=MONTHLY;BYDAY=2TU"));
        Assert.Equal(new[] { new DateTime(2026, 10, 13), new DateTime(2026, 11, 10) },
            second.Between(Utc(2026, 10, 1, 0), Utc(2026, 12, 1, 0)).Select(m => m.Start.Date));

        var the31st = Cal(Event("UID:15\nSUMMARY:Close\nDTSTART:20260131T100000Z\nRRULE:FREQ=MONTHLY"));
        // months without a 31st are skipped, as RFC 5545 says
        Assert.Equal(new[] { new DateTime(2026, 7, 31), new DateTime(2026, 8, 31), new DateTime(2026, 10, 31) },
            the31st.Between(Utc(2026, 7, 1, 0), Utc(2026, 11, 1, 0)).Select(m => m.Start.Date));
    }

    [Fact]
    public void RepeatsYearlyAndEveryOtherDay()
    {
        var yearly = Cal(Event("UID:16\nSUMMARY:Review\nDTSTART:20240315T120000Z\nRRULE:FREQ=YEARLY"));
        Assert.Equal(Utc(2026, 3, 15, 12), Assert.Single(yearly.Between(Utc(2026, 1, 1, 0), Utc(2026, 12, 31, 0))).Start);

        var other = Cal(Event("UID:17\nSUMMARY:Check-in\nDTSTART:20260901T080000Z\nRRULE:FREQ=DAILY;INTERVAL=2"));
        Assert.Equal(new[] { 1, 3, 5 }, other.Between(Utc(2026, 9, 1, 0), Utc(2026, 9, 6, 0)).Select(m => m.Start.Day));
    }

    [Fact]
    public void FloatingTimesAreLocal()
    {
        var cal = Cal(Event("UID:18\nSUMMARY:Lunch\nDTSTART:20260929T120000\nDTEND:20260929T130000"));
        cal.Local = TimeZoneInfo.CreateCustomTimeZone("plus5", TimeSpan.FromHours(5), "plus5", "plus5");
        Assert.Equal(Utc(2026, 9, 29, 7), Assert.Single(cal.Between(Utc(2026, 9, 29, 0), Utc(2026, 9, 30, 0))).Start);
    }

    [Fact]
    public void SurvivesGarbage()
    {
        var cal = IcsCalendar.Parse("this is not a calendar\nBEGIN:VEVENT\nDTSTART:yesterday\nEND:VEVENT\nEND:VCALENDAR\nEND:VCALENDAR");
        Assert.Empty(cal.Between(DateTime.UtcNow.AddYears(-1), DateTime.UtcNow.AddYears(1)));
    }

    [Fact]
    public void ARuleWithAPartGivenTwiceStillReads()
    {
        var cal = Cal(Event("UID:19\nSUMMARY:Sync\nDTSTART:20260928T090000Z\nRRULE:FREQ=WEEKLY;BYDAY=MO;BYDAY=TU"));
        Assert.Equal(new[] { 28 }, cal.Between(Utc(2026, 9, 27, 0), Utc(2026, 10, 1, 0)).Select(m => m.Start.Day)); // the first BYDAY counts
    }

    [Fact]
    public void DeepNestingNeitherOverflowsNorHidesTheEvents()
    {
        var junk = new System.Text.StringBuilder();
        for (int i = 0; i < 20000; i++) junk.Append("BEGIN:X\r\n");
        for (int i = 0; i < 20000; i++) junk.Append("END:X\r\n");
        var cal = Cal(junk + Event("UID:20\nSUMMARY:After the junk\nDTSTART:20260929T100000Z"));
        Assert.Equal("After the junk", Assert.Single(cal.Between(Utc(2026, 9, 29, 0), Utc(2026, 9, 30, 0))).Title);

        var unclosed = new System.Text.StringBuilder("BEGIN:VCALENDAR\r\n");
        for (int i = 0; i < 200000; i++) unclosed.Append("BEGIN:X\r\n");
        Assert.Equal(0, IcsCalendar.Parse(unclosed.ToString()).EventCount);
    }

    [Fact]
    public void ExtremeDatesAndDurationsAreReadSafely()
    {
        // a daily repeat from the year 1 (in a zone ahead of UTC, so its first start is before DateTime.MinValue in UTC)
        // is still on today; a duration too long to hold is taken as none
        var cal = Cal(Event("UID:21\nSUMMARY:Ancient\nDTSTART;TZID=Asia/Tokyo:00010101T000000\nDTEND;TZID=Asia/Tokyo:00010101T010000\nRRULE:FREQ=DAILY")
            + Event("UID:22\nSUMMARY:Fine\nDTSTART:20260929T100000Z\nDURATION:P10000000D10000000D"));
        var got = cal.Between(Utc(2026, 9, 29, 0), Utc(2026, 9, 30, 0));
        Assert.Equal(new[] { "Fine", "Ancient" }, got.Select(m => m.Title));
        Assert.Equal(got[0].Start, got[0].End);
        Assert.Equal(Utc(2026, 9, 29, 15), got[1].Start);
    }

    [Theory]
    [InlineData("PT30M", 30)]
    [InlineData("PT1H30M", 90)]
    [InlineData("P1D", 1440)]
    [InlineData("P1W", 10080)]
    [InlineData("-PT5M", -5)]
    public void ReadsDurations(string text, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), IcsCalendar.ParseDuration(text));

    [Theory]
    [InlineData("30M")]
    [InlineData("PT")]
    [InlineData("P1H")]
    [InlineData("P10000000D10000000D")]
    [InlineData("PT99999999999999999999H")]
    public void RejectsBadDurations(string text) => Assert.Null(IcsCalendar.ParseDuration(text));

    [Fact]
    public void NamesZonesBothWays()
    {
        Assert.Equal("Pacific Standard Time", ZoneNames.WindowsOf("America/Los_Angeles"));
        Assert.Equal("West Asia Standard Time", ZoneNames.WindowsOf("Asia/Tashkent"));
        Assert.Equal("Europe/Berlin", ZoneNames.IanaOf("W. Europe Standard Time"));
        Assert.Null(ZoneNames.WindowsOf("Mars/Olympus_Mons"));
    }
}

public class MeetingWatchTests
{
    static DateTime T(int h, int m, int s = 0) => new(2026, 9, 29, h, m, s, DateTimeKind.Utc);

    static MeetingWatch Watch(params Meeting[] meetings)
    {
        var w = new MeetingWatch();
        w.Update(meetings);
        return w;
    }

    [Fact]
    public void WarnsThenSaysSoonThenStarts_EachOnce()
    {
        var standup = new Meeting("Standup", T(10, 0), T(10, 15));
        var w = Watch(standup);
        Assert.Empty(w.Step(T(9, 54), 5));
        Assert.Equal(MeetingCue.Warn, Assert.Single(w.Step(T(9, 55), 5)).Cue);
        Assert.Empty(w.Step(T(9, 56), 5));
        w.Update(new[] { standup }); // the calendar read again changes nothing already said
        Assert.Empty(w.Step(T(9, 57), 5));
        Assert.Equal(MeetingCue.Soon, Assert.Single(w.Step(T(9, 59, 5), 5)).Cue);
        Assert.Equal(MeetingCue.Start, Assert.Single(w.Step(T(10, 0), 5)).Cue);
        Assert.Empty(w.Step(T(10, 1), 5));
    }

    [Fact]
    public void ALateStartGivesOnlyTheLatestCue()
    {
        var w = Watch(new Meeting("Sync", T(10, 0), T(10, 30)));
        Assert.Equal(MeetingCue.Soon, Assert.Single(w.Step(T(9, 59, 30), 5)).Cue);
        Assert.Equal(MeetingCue.Start, Assert.Single(w.Step(T(10, 0, 30), 5)).Cue);

        var late = Watch(new Meeting("Sync", T(10, 0), T(10, 30)));
        Assert.Equal(MeetingCue.Start, Assert.Single(late.Step(T(10, 1), 5)).Cue);
        Assert.Empty(late.Step(T(10, 5), 5)); // long past the grace: nothing more
    }

    [Fact]
    public void WithoutAWarningTimeOnlySoonAndStart()
    {
        var w = Watch(new Meeting("1:1", T(14, 0), T(14, 30)));
        Assert.Empty(w.Step(T(13, 55), 0));
        Assert.Equal(MeetingCue.Soon, Assert.Single(w.Step(T(13, 59, 30), 0)).Cue);
    }

    [Fact]
    public void KnowsTheCurrentAndTheNextMeeting()
    {
        var w = Watch(new Meeting("A", T(10, 0), T(11, 0)), new Meeting("B", T(11, 0), T(11, 30)), new Meeting("C", T(15, 0), T(15, 0)));
        Assert.Equal("A", w.Current(T(10, 30))!.Title);
        Assert.Equal("B", w.Next(T(10, 30))!.Title);
        Assert.Equal("B", w.Current(T(11, 0))!.Title);
        Assert.Null(w.Current(T(12, 0)));
        Assert.Equal("C", w.Current(T(15, 1))!.Title); // no end time: "on" for the grace after its start
        Assert.Null(w.Next(T(15, 1)));
    }

    [Theory]
    [InlineData(299, "4:59")]
    [InlineData(3725, "1:02:05")]
    [InlineData(-5, "0:00")]
    public void FormatsTheTimeLeft(int seconds, string text) => Assert.Equal(text, MeetingWatch.Until(TimeSpan.FromSeconds(seconds)));
}
