using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Avalonia;
using DeskArcade.Net;
using DeskArcade.Office;
using Xunit;

namespace DeskArcade.Tests;

public class BodyBreakTests
{
    /// <summary>Runs the clock for <paramref name="minutes"/> in one-second steps at the given idle time; returns the first break due.</summary>
    static BreakKind? Run(BodyBreaks b, double minutes, double? idle = 1, bool eyes = false, int stretch = 0, int water = 0, bool quiet = false)
    {
        BreakKind? first = null;
        for (int i = 0; i < minutes * 60; i++)
            first ??= b.Step(1, idle, eyes, stretch, water, quiet);
        return first;
    }

    [Fact]
    public void StretchComesAfterTheTimeAtTheComputer()
    {
        var b = new BodyBreaks();
        Assert.Null(Run(b, 44.9, stretch: 45));
        Assert.Equal(BreakKind.Stretch, Run(b, 0.2, stretch: 45));
        b.Taken(BreakKind.Stretch);
        Assert.Null(Run(b, 10, stretch: 45));
    }

    [Fact]
    public void TimeAwayDoesNotCountAndFiveMinutesAwayIsABreak()
    {
        var b = new BodyBreaks();
        Run(b, 40, stretch: 45);
        Assert.Null(Run(b, 30, idle: 200, stretch: 45)); // away: the clock stands still
        Assert.Equal(40 * 60, b.StretchSeconds, 0);
        b.Step(1, BodyBreaks.AwayIsBreak, false, 45, 0, false); // back from five minutes away
        Assert.Equal(0, b.StretchSeconds);
    }

    [Fact]
    public void TwoMinutesAwayRestTheEyes()
    {
        var b = new BodyBreaks();
        Run(b, 19, eyes: true);
        b.Step(1, 90, true, 0, 0, false); // a minute and a half without input: reading, still counting
        Assert.True(b.EyeSeconds > 19 * 60);
        b.Step(1, BodyBreaks.AwayRestsEyes, true, 0, 0, false);
        Assert.Equal(0, b.EyeSeconds);
        Assert.Null(Run(b, 19, eyes: true));
        Assert.Equal(BreakKind.Eye, Run(b, 1.1, eyes: true));
    }

    [Fact]
    public void ReadingWithoutTouchingStillCounts()
    {
        var b = new BodyBreaks();
        Assert.Equal(BreakKind.Eye, Run(b, 20.1, idle: 90, eyes: true));
    }

    [Fact]
    public void QuietTimesHoldTheBreakBack()
    {
        var b = new BodyBreaks();
        Assert.Null(Run(b, 50, stretch: 45, quiet: true));
        Assert.Equal(BreakKind.Stretch, Run(b, 1.0 / 60, stretch: 45));
    }

    [Fact]
    public void StretchComesBeforeWaterAndWaterBeforeEyes_AndAStretchRestsTheEyes()
    {
        var b = new BodyBreaks();
        Assert.Equal(BreakKind.Stretch, Run(b, 61, stretch: 60, water: 60)); // both due at the hour: the stretch first
        Assert.Equal(60 * 60, b.EyeSeconds, 0); // Run stops at the first break due
        b.Taken(BreakKind.Stretch);
        Assert.Equal(0, b.EyeSeconds);
        Assert.Equal(BreakKind.Water, b.Step(1, 1, true, 60, 60, false));
    }

    [Fact]
    public void LaterBringsTheBreakBackSoon()
    {
        var b = new BodyBreaks();
        Run(b, 61, water: 60);
        b.Snooze(BreakKind.Water, 60, 10 * 60);
        Assert.Null(Run(b, 9.9, water: 60));
        Assert.Equal(BreakKind.Water, Run(b, 0.2, water: 60));
    }

    [Fact]
    public void AnUnknownIdleTimeCountsAsPresent()
    {
        var b = new BodyBreaks();
        Assert.Equal(BreakKind.Stretch, Run(b, 30.1, idle: null, stretch: 30));
    }
}

public class FocusTimerTests
{
    static readonly DateTime T0 = new(2026, 9, 29, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void FocusThenBreakThenStops()
    {
        var f = new FocusTimer();
        f.Start(T0, 25);
        Assert.Equal(FocusPhase.Focus, f.Phase);
        Assert.Equal(1, f.Block);
        Assert.Null(f.Step(T0.AddMinutes(24), 25, 5, false));
        Assert.Equal(TimeSpan.FromMinutes(1), f.Left(T0.AddMinutes(24)));
        Assert.Equal(FocusEvent.FocusDone, f.Step(T0.AddMinutes(25), 25, 5, false));
        Assert.Equal(FocusPhase.Break, f.Phase);
        Assert.Equal(T0.AddMinutes(30), f.Ends);
        Assert.Equal(FocusEvent.BreakOver, f.Step(T0.AddMinutes(30), 25, 5, false));
        Assert.Equal(FocusPhase.Off, f.Phase);
    }

    [Fact]
    public void EveryFourthBreakIsLong_AndAutoGoesOn()
    {
        var f = new FocusTimer();
        var now = T0;
        f.Start(now, 25);
        for (int block = 1; block <= 4; block++)
        {
            Assert.Equal(block, f.Block);
            now = f.Ends;
            Assert.Equal(FocusEvent.FocusDone, f.Step(now, 25, 5, true));
            Assert.Equal(block == 4, f.LongBreak);
            Assert.Equal(block == 4 ? 15 : 5, (f.Ends - now).TotalMinutes);
            now = f.Ends;
            Assert.Equal(FocusEvent.NextFocus, f.Step(now, 25, 5, true));
        }
        Assert.Equal(1, f.Block); // a new set of four
    }

    [Fact]
    public void AManualStartSoonAfterABreakCountsOn_ALateOneStartsANewSet()
    {
        var f = new FocusTimer();
        f.Start(T0, 25);
        f.Step(T0.AddMinutes(25), 25, 5, false);
        f.Step(T0.AddMinutes(30), 25, 5, false);
        f.Start(T0.AddMinutes(40), 25);
        Assert.Equal(2, f.Block);

        var late = new FocusTimer();
        late.Start(T0, 25);
        late.Step(T0.AddMinutes(25), 25, 5, false);
        late.Step(T0.AddMinutes(30), 25, 5, false);
        late.Start(T0.AddHours(2), 25);
        Assert.Equal(1, late.Block);
    }

    [Fact]
    public void StoppingEndsTheSet()
    {
        var f = new FocusTimer();
        f.Start(T0, 50);
        f.Stop();
        Assert.Equal(FocusPhase.Off, f.Phase);
        Assert.Null(f.Step(T0.AddHours(1), 50, 10, true));
        f.Start(T0.AddMinutes(1), 50);
        Assert.Equal(1, f.Block);
    }
}

public class ExerciseTests
{
    [Fact]
    public void BoxBreathingGoesInHoldOutHold()
    {
        Assert.Equal(64, Breathing.Seconds);
        Assert.Equal((0, 4, 1), Pick(Breathing.At(0)));
        Assert.Equal((1, 4, 1), Pick(Breathing.At(4)));
        Assert.Equal((2, 2, 1), Pick(Breathing.At(10.5)));
        Assert.Equal((3, 1, 1), Pick(Breathing.At(15.9)));
        Assert.Equal((0, 4, 2), Pick(Breathing.At(16)));
        Assert.Equal((3, 1, 4), Pick(Breathing.At(99)));
        Assert.Equal(0, Breathing.At(0).Size, 3);
        Assert.Equal(1, Breathing.At(5).Size, 3);
        Assert.Equal(0.5, Breathing.At(10).Size, 3);

        static (int, int, int) Pick((int Phase, double K, int Count, int Round, double Size) a) => (a.Phase, a.Count, a.Round);
    }

    [Fact]
    public void TheStretchHasFourMovesOfFifteenSeconds()
    {
        Assert.Equal(60, Stretches.Seconds);
        Assert.Equal(0, Stretches.At(14.9).Step);
        Assert.Equal(1, Stretches.At(15).Step);
        Assert.Equal(3, Stretches.At(120).Step);
        Assert.Equal(0.5, Stretches.At(22.5).K, 3);
    }
}

public class WaitTests
{
    static readonly DateTime Now = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("report.pdf.crdownload", "report.pdf")]
    [InlineData("photo.jpg.part", "photo.jpg")]
    [InlineData("setup.exe.download", "setup.exe")]
    [InlineData("Unconfirmed 481516.crdownload", null)]
    [InlineData("notes.txt", null)]
    public void KnowsWhatAPartialFileBecomes(string partial, string? final) => Assert.Equal(final, DownloadWatch.FinalName(partial));

    [Fact]
    public void AGonePartialWithItsFileIsAFinishedDownload()
    {
        var w = new DownloadWatch();
        var files = new Dictionary<string, DateTime>();
        var (started, finished) = w.Step(new[] { "report.pdf.crdownload" }, files, Now);
        Assert.Equal(new[] { "report.pdf" }, started);
        Assert.Empty(finished);
        files["report.pdf"] = Now;
        (_, finished) = w.Step(Array.Empty<string>(), files, Now.AddSeconds(3));
        Assert.Equal(new[] { "report.pdf" }, finished);
        Assert.Empty(w.InProgress);
    }

    [Fact]
    public void AnUnnamedDownloadIsTheNewestFile_AndACancelledOneIsNothing()
    {
        var w = new DownloadWatch();
        var files = new Dictionary<string, DateTime> { ["old.zip"] = Now.AddHours(-3) };
        w.Step(new[] { "Unconfirmed 42.crdownload" }, files, Now);
        files["big.iso"] = Now.AddSeconds(2);
        Assert.Equal(new[] { "big.iso" }, w.Step(Array.Empty<string>(), files, Now.AddSeconds(3)).Finished);

        w.Step(new[] { "movie.mkv.part" }, files, Now.AddMinutes(5));
        Assert.Empty(w.Step(Array.Empty<string>(), files, Now.AddMinutes(6)).Finished); // cancelled: no movie.mkv
    }

    [Fact]
    public void AFileIsReadyOnceItStopsGrowing()
    {
        var f = new FileWait();
        Assert.False(f.Step(1, false, 0, false));
        Assert.False(f.Step(1, true, 100, false));
        Assert.False(f.Step(2, true, 200, false)); // still growing
        Assert.False(f.Step(2, true, 200, false));
        Assert.False(f.Step(2, true, 200, true)); // the browser is still writing next to it
        Assert.False(f.Step(1, true, 200, false));
        Assert.False(f.Step(2.5, true, 200, false));
        Assert.True(f.Step(2.5, true, 200, false));
    }

    [Fact]
    public void ReadsTheXdgDownloadFolder()
    {
        const string dirs = "# comment\nXDG_DESKTOP_DIR=\"$HOME/Desktop\"\nXDG_DOWNLOAD_DIR=\"$HOME/Загрузки\"\n";
        Assert.Equal("/home/ada/Загрузки", DownloadWatch.XdgDownloadDir(dirs, "/home/ada"));
        Assert.Null(DownloadWatch.XdgDownloadDir("XDG_MUSIC_DIR=\"$HOME/Music\"", "/home/ada"));
    }
}

public class WorkDayTests
{
    static readonly TimeOnly Six = new(18, 0);

    [Fact]
    public void WrapsUpAtTheEndOfTheDayAndNudgesAnHourLater_OnceEach()
    {
        var d = new EndOfDay();
        var day = new DateTime(2026, 9, 29);
        Assert.Null(d.Step(day.AddHours(17.9), Six, true, 8 * 3600));
        Assert.Equal(DayCue.WrapUp, d.Step(day.AddHours(18), Six, true, 8 * 3600));
        Assert.Null(d.Step(day.AddHours(18.5), Six, true, 8 * 3600));
        Assert.Equal(DayCue.StillHere, d.Step(day.AddHours(19), Six, true, 8 * 3600));
        Assert.Null(d.Step(day.AddHours(21), Six, true, 8 * 3600));
        Assert.Equal(DayCue.WrapUp, d.Step(day.AddDays(1).AddHours(18.2), Six, true, 7 * 3600)); // the next day
    }

    [Fact]
    public void SaysNothingToSomeoneAwayOrBarelyHere()
    {
        var d = new EndOfDay();
        var day = new DateTime(2026, 9, 29);
        Assert.Null(d.Step(day.AddHours(18), Six, false, 8 * 3600));
        Assert.Null(d.Step(day.AddHours(18), Six, true, 10 * 60));
        Assert.Null(d.Step(day.AddHours(18), null, true, 8 * 3600));
        Assert.Equal(DayCue.WrapUp, d.Step(day.AddHours(18.3), Six, true, 8 * 3600)); // came back to the desk later
    }

    [Theory]
    [InlineData("18:00", 18, 0)]
    [InlineData("9:30", 9, 30)]
    public void ReadsTheEndTime(string text, int h, int m) => Assert.Equal(new TimeOnly(h, m), EndOfDay.ParseTime(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("six")]
    [InlineData("25:00")]
    public void AnythingElseIsOff(string? text) => Assert.Null(EndOfDay.ParseTime(text));

    [Theory]
    [InlineData("10", 600)]
    [InlineData("10m", 600)]
    [InlineData("10 min", 600)]
    [InlineData("90s", 90)]
    [InlineData("1h30m", 5400)]
    [InlineData("1:30", 5400)]
    [InlineData("1.5h", 5400)]
    [InlineData("2 hours", 7200)]
    public void ReadsTimerDurations(string text, int seconds) => Assert.Equal(TimeSpan.FromSeconds(seconds), Durations.Parse(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("3 days")]
    [InlineData("25h")]
    [InlineData("1e300")]
    [InlineData("99999999999")]
    [InlineData("99999999999m")]
    [InlineData("99:00")]
    public void RejectsOtherDurations(string? text) => Assert.Null(Durations.Parse(text));

    [Fact]
    public void KnowsAFullScreenWindow()
    {
        var monitor = new PixelRect(0, 0, 1920, 1080);
        Assert.True(FullScreen.Covers(new PixelRect(0, 0, 1920, 1080), monitor));
        Assert.True(FullScreen.Covers(new PixelRect(-8, -8, 1936, 1096), monitor));
        Assert.False(FullScreen.Covers(new PixelRect(0, 0, 1920, 1040), monitor)); // maximised above the taskbar
        Assert.False(FullScreen.Covers(new PixelRect(1920, 0, 1920, 1080), monitor)); // on the other monitor
    }
}

[Collection("lan")]
public class OfficeInviteTests
{
    const int TestPort = 47893;

    [Fact]
    public void InvitesAndAnswersRoundTrip()
    {
        var invite = new Invite("abc123", "me01", "ada", InviteKind.Lunch, 15);
        Assert.Equal("DA1|iv|abc123|me01|ada|lunch|15", OfficeInvites.Encode(invite));
        Assert.Equal(invite, OfficeInvites.Decode(OfficeInvites.Encode(invite)));
        var reply = new InviteReply("abc123", "you2", "grace", InviteAnswer.Focusing);
        Assert.Equal(reply, OfficeInvites.Decode(OfficeInvites.Encode(reply)));
        Assert.Equal("abc123", OfficeInvites.Decode("DA1|ix|abc123|me01"));
    }

    [Theory]
    [InlineData("DA1|iv|abc|me|ada|tea|5")]
    [InlineData("DA1|iv|abc|me|ada|coffee|999")]
    [InlineData("DA1|iv|abc|me||coffee|5")]
    [InlineData("DA1|ir|abc|you|grace|maybe")]
    [InlineData("DA2|iv|abc|me|ada|coffee|5")]
    [InlineData("hello")]
    public void RejectsMalformedMessages(string text) => Assert.Null(OfficeInvites.Decode(text));

    [Fact]
    public void NamesCannotBreakTheFormat()
    {
        string wire = OfficeInvites.Encode(new Invite("id", "me", "evil|name\n", InviteKind.Coffee, 0));
        var back = Assert.IsType<Invite>(OfficeInvites.Decode(wire));
        Assert.Equal("evilname", back.From);
    }

    [Fact]
    public void HearsInvitesOnce_AndNotItsOwn()
    {
        using var invites = new OfficeInvites("me01", () => "ada", TestPort);
        var heard = new List<Invite>();
        var answers = new List<InviteReply>();
        invites.InviteReceived += i => { lock (heard) heard.Add(i); };
        invites.ReplyReceived += r => { lock (answers) answers.Add(r); };
        invites.Start();
        Assert.True(invites.Running);

        using var udp = new UdpClient(AddressFamily.InterNetwork);
        void Send(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            udp.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, TestPort));
        }
        string wire = OfficeInvites.Encode(new Invite("x1", "you2", "grace", InviteKind.Coffee, 5));
        Send(wire);
        Send(wire); // a repeat
        Send(OfficeInvites.Encode(new Invite("x2", "me01", "ada", InviteKind.Walk, 0))); // our own, heard back
        Send(OfficeInvites.Encode(new InviteReply("mine", "you2", "grace", InviteAnswer.Yes)));

        var until = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < until && (heard.Count < 1 || answers.Count < 1)) Thread.Sleep(20);
        Thread.Sleep(150);
        lock (heard) Assert.Equal("x1", Assert.Single(heard).Id);
        lock (answers) Assert.Equal(InviteAnswer.Yes, Assert.Single(answers).Answer);
    }
}
