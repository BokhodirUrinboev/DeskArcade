using System;
using System.Linq;
using System.Text;
using DeskArcade.Office;
using Xunit;

namespace DeskArcade.Tests;

/// <summary>
/// The Join button's link, read from invitations the way Outlook, Google Calendar, Zoom and Webex write them: lines
/// folded at 75 characters, escaped commas and semicolons, HTML notes, help links before the real one.
/// </summary>
public class MeetingLinkTests
{
    static DateTime Utc(int y, int mo, int d, int h, int mi = 0) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    /// <summary>Folds a content line the way calendars write them: 75 characters, then CRLF and a space.</summary>
    static string Fold(string line)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < line.Length; i += i == 0 ? 75 : 74)
        {
            if (i > 0) sb.Append("\r\n ");
            sb.Append(line, i, Math.Min(i == 0 ? 75 : 74, line.Length - i));
        }
        return sb.Append("\r\n").ToString();
    }

    /// <summary>A calendar of events whose lines are folded; the lines of each event come one per string.</summary>
    static IcsCalendar Cal(params string[][] events)
    {
        var sb = new StringBuilder("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\n");
        foreach (var lines in events)
        {
            sb.Append("BEGIN:VEVENT\r\n");
            foreach (string line in lines) sb.Append(Fold(line));
            sb.Append("END:VEVENT\r\n");
        }
        var cal = IcsCalendar.Parse(sb.Append("END:VCALENDAR\r\n").ToString());
        cal.Local = TimeZoneInfo.Utc;
        return cal;
    }

    static Meeting One(IcsCalendar cal) => Assert.Single(cal.Between(Utc(2026, 10, 1, 0), Utc(2026, 10, 2, 0)));

    const string TeamsJoin =
        "https://teams.microsoft.com/l/meetup-join/19%3ameeting_ZDk4YjQ2NzEtNWYxNi00ZWIxLWJhNzktNjM5ZTg2ZTEwMWM0%40thread.v2/0" +
        "?context=%7b%22Tid%22%3a%2272f988bf-86f1-41af-91ab-2d7cd011db47%22%2c%22Oid%22%3a%22a1b2c3d4-0000-4000-8000-123456789abc%22%7d";

    [Fact]
    public void OutlookTeams_TheJoinTheMeetingNowLinkInTheNotes()
    {
        // Outlook's plain-text notes: a help link comes first, the join link after it, then dial-in and options links
        var cal = Cal(new[]
        {
            "UID:040000008200E00074C5B7101A82E00800000000A1B2C3D4E5F6A701000000000000000010000000",
            "SUMMARY:Sprint review",
            "DTSTART:20261001T100000Z",
            "DTEND:20261001T110000Z",
            "LOCATION:Microsoft Teams Meeting",
            "DESCRIPTION:\\n______________________________________________________________________" +
            "__________\\nMicrosoft Teams Need help?<https://aka.ms/JoinTeamsMeeting?omkt=en-US>\\n" +
            "Join the meeting now<" + TeamsJoin + ">\\nMeeting ID: 245 123 456 789\\nPasscode: aB3cD4\\n" +
            "________________________________\\nDial in by phone\\n+1 323-849-4874\\,\\,123456789# " +
            "<tel:+13238494874\\,\\,123456789#> United States\\, Los Angeles\\n" +
            "Find a local number<https://dialin.teams.microsoft.com/6787a136-fb7b-4bd6-96e5-d0ab5d0ba59b?id=123456789>\\n" +
            "For organizers: Meeting options<https://teams.microsoft.com/meetingOptions/?organizerId=a1b2&tenantId=72f9" +
            "&threadId=19_meeting_ZDk4@thread.v2&messageId=0&language=en-US> | Reset dial-in PIN<https://dialin.teams.microsoft.com/usp/pstnconferencing>\\n",
        });
        var m = One(cal);
        Assert.Equal(TeamsJoin, m.JoinUrl);
        Assert.Equal(MeetingService.Teams, MeetingLinks.Classify(m.JoinUrl!));
    }

    [Fact]
    public void OutlookTeams_TheHtmlNotesAndTheTeamsProperty()
    {
        // the plain notes lost their link (a "titles and locations" publish keeps less); the HTML copy still has it
        string html = "<html><head><meta name=\"Generator\" content=\"Microsoft Exchange Server\"></head><body>" +
            "<div style=\"width:100%\"><span style=\"font-size:18pt\">Microsoft Teams</span> " +
            "<a href=\"https://aka.ms/JoinTeamsMeeting?omkt=en-US\" style=\"font-size:10.5pt\">Need help?</a></div>" +
            "<div><a class=\"me-email-headline\" href=\"" + TeamsJoin.Replace("&", "&amp;") + "\" target=\"_blank\" " +
            "rel=\"noreferrer noopener\" style=\"font-size:20pt\">Join the meeting now</a></div>" +
            "<div>Meeting ID: <span>245 123 456 789</span></div></body></html>";
        var cal = Cal(
            new[] { "UID:a", "SUMMARY:Planning", "DTSTART:20261001T090000Z", "DTEND:20261001T093000Z", "LOCATION:Microsoft Teams Meeting",
                "DESCRIPTION:Microsoft Teams meeting\\nJoin the meeting now", "X-ALT-DESC;FMTTYPE=text/html:" + html },
            new[] { "UID:b", "SUMMARY:1:1", "DTSTART:20261001T140000Z", "DTEND:20261001T143000Z", "LOCATION:Microsoft Teams Meeting",
                "X-MICROSOFT-SKYPETEAMSMEETINGURL:https://teams.microsoft.com/meet/2345678901234?p=AbCdEfGhIjKlMnOp" });
        var meetings = cal.Between(Utc(2026, 10, 1, 0), Utc(2026, 10, 2, 0));
        Assert.Equal(TeamsJoin, meetings[0].JoinUrl);
        Assert.Equal("https://teams.microsoft.com/meet/2345678901234?p=AbCdEfGhIjKlMnOp", meetings[1].JoinUrl);
    }

    [Fact]
    public void GoogleCalendar_MeetFromTheConferenceDataAndTheNotes()
    {
        string notes = "-::~:~::~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~::~:~::-\\n" +
            "Join with Google Meet: https://meet.google.com/abc-defg-hij\\nOr dial: (US) +1 408-555-0199 PIN: 123456789#\\n" +
            "More phone numbers: https://tel.meet/abc-defg-hij?pin=1234567890123\\n\\n" +
            "Learn more about Meet at: https://support.google.com/a/users/answer/9282720\\n\\nPlease do not edit this section.\\n" +
            "-::~:~::~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~::~:~::-";
        var cal = Cal(
            new[]
            {
                "DTSTART:20261001T090000Z", "DTEND:20261001T093000Z", "DTSTAMP:20260929T120000Z",
                "ORGANIZER;CN=ada@example.com:mailto:ada@example.com", "UID:1a2b3c4d5e6f7g8h9i0j@google.com",
                "ATTENDEE;CUTYPE=INDIVIDUAL;ROLE=REQ-PARTICIPANT;PARTSTAT=ACCEPTED;CN=grace@example.com;X-NUM-GUESTS=0:mailto:grace@example.com",
                "X-GOOGLE-CONFERENCE:https://meet.google.com/abc-defg-hij", "CREATED:20260920T080000Z",
                "DESCRIPTION:" + notes, "LAST-MODIFIED:20260929T120000Z", "LOCATION:", "SEQUENCE:0", "STATUS:CONFIRMED",
                "SUMMARY:Standup", "TRANSP:OPAQUE",
            },
            // the conference data alone: an event whose notes were edited away
            new[] { "UID:2@google.com", "SUMMARY:Retro", "DTSTART:20261001T150000Z", "DTEND:20261001T160000Z", "LOCATION:Room 4.01 (Main office)",
                "X-GOOGLE-CONFERENCE:https://meet.google.com/xyz-abcd-efg" });
        var meetings = cal.Between(Utc(2026, 10, 1, 0), Utc(2026, 10, 2, 0));
        Assert.Equal("https://meet.google.com/abc-defg-hij", meetings[0].JoinUrl);
        Assert.Equal("https://meet.google.com/xyz-abcd-efg", meetings[1].JoinUrl);
    }

    [Fact]
    public void GoogleCalendar_HtmlNotesWithGooglesRedirect()
    {
        var cal = Cal(new[]
        {
            "UID:3@google.com", "SUMMARY:Customer call", "DTSTART:20261001T120000Z", "DTEND:20261001T130000Z",
            "DESCRIPTION:Agenda: <a href=\"https://www.google.com/url?q=https://docs.google.com/document/d/1AbC&amp;sa=D\">the doc</a><br>" +
            "Call: <a href=\"https://www.google.com/url?q=https://us02web.zoom.us/j/85550001111?pwd%3DZmFrZXB3ZA.1&amp;sa=D&amp;source=calendar" +
            "&amp;usd=2&amp;usg=AOvVaw0abc\" target=\"_blank\">https://us02web.zoom.us/j/85550001111?pwd=ZmFrZXB3ZA.1</a>",
        });
        Assert.Equal("https://us02web.zoom.us/j/85550001111?pwd=ZmFrZXB3ZA.1", One(cal).JoinUrl);
    }

    [Fact]
    public void Zoom_TheInvitationZoomExports()
    {
        string notes = "Bokhodir Urinboev is inviting you to a scheduled Zoom meeting.\\n\\nJoin Zoom Meeting\\n" +
            "https://us06web.zoom.us/j/81234567890?pwd=aBcDeFgHiJkLmNoPqRsTuVwXyZ.1\\n\\nMeeting ID: 812 3456 7890\\nPasscode: 123456\\n\\n---\\n\\n" +
            "One tap mobile\\n+16699006833\\,\\,81234567890#\\,\\,\\,\\,*123456# US (San Jose)\\n+16694449171\\,\\,81234567890#\\,\\,\\,\\,*123456# US\\n\\n" +
            "---\\n\\nDial by your location\\n• +1 669 900 6833 US (San Jose)\\n\\nFind your local number: https://us06web.zoom.us/u/kdXyz12AbC\\n\\n";
        const string zones = "BEGIN:VTIMEZONE\r\nTZID:Asia/Tashkent\r\nBEGIN:STANDARD\r\nDTSTART:19700101T000000\r\nTZOFFSETFROM:+0500\r\n" +
            "TZOFFSETTO:+0500\r\nEND:STANDARD\r\nEND:VTIMEZONE\r\n";
        string text = "BEGIN:VCALENDAR\r\nPRODID:-//zoom.us//iCalendar Event//EN\r\nVERSION:2.0\r\nCALSCALE:GREGORIAN\r\nMETHOD:PUBLISH\r\n" +
            "CLASS:PUBLIC\r\n" + zones + "BEGIN:VEVENT\r\nDTSTAMP:20260929T091500Z\r\n" +
            "DTSTART;TZID=Asia/Tashkent:20261001T150000\r\nDTEND;TZID=Asia/Tashkent:20261001T160000\r\nSUMMARY:Design review\r\n" +
            "UID:20260929T091500Z-81234567890@fe80:0:0:0:1234:5678:9abc:def0ens3\r\nTZID:Asia/Tashkent\r\n" +
            Fold("DESCRIPTION:" + notes) +
            Fold("LOCATION:https://us06web.zoom.us/j/81234567890?pwd=aBcDeFgHiJkLmNoPqRsTuVwXyZ.1") +
            "BEGIN:VALARM\r\nTRIGGER:-PT10M\r\nACTION:DISPLAY\r\nDESCRIPTION:Reminder https://zoom.us/j/1\r\nEND:VALARM\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var m = Assert.Single(IcsCalendar.Parse(text).Between(Utc(2026, 10, 1, 0), Utc(2026, 10, 2, 0)));
        Assert.Equal(Utc(2026, 10, 1, 10), m.Start);
        Assert.Equal("https://us06web.zoom.us/j/81234567890?pwd=aBcDeFgHiJkLmNoPqRsTuVwXyZ.1", m.JoinUrl);
    }

    [Fact]
    public void Webex_TheLocationAndTheNotes()
    {
        const string join = "https://example.webex.com/example/j.php?MTID=m0123456789abcdef0123456789abcdef";
        string notes = "Hello\\,\\n\\nNeed help? Go to https://help.webex.com\\n\\nJoin the Webex meeting:\\n" + join + "\\n\\n" +
            "Meeting number (access code): 2634 123 4567\\nMeeting password: AbC12dEf\\n\\n" +
            "Join from a mobile device (attendees only)\\n+1-415-655-0001\\,\\,26341234567## US Toll\\n\\n" +
            "Join by video system\\nDial 26341234567@example.webex.com\\nYou can also dial 173.243.2.68 and enter your meeting number.\\n";
        var cal = Cal(
            new[] { "UID:w1", "SUMMARY:Webex meeting: Budget", "DTSTART:20261001T080000Z", "DTEND:20261001T090000Z", "LOCATION:" + join, "DESCRIPTION:" + notes },
            new[] { "UID:w2", "SUMMARY:Office hours", "DTSTART:20261001T100000Z", "DTEND:20261001T110000Z", "LOCATION:", "DESCRIPTION:" + notes },
            new[] { "UID:w3", "SUMMARY:Personal room", "DTSTART:20261001T120000Z", "DTEND:20261001T121500Z", "LOCATION:https://example.webex.com/meet/jdoe" });
        var meetings = cal.Between(Utc(2026, 10, 1, 0), Utc(2026, 10, 2, 0));
        Assert.Equal(new[] { join, join, "https://example.webex.com/meet/jdoe" }, meetings.Select(m => m.JoinUrl));
    }

    [Fact]
    public void EscapedCommasAndSemicolonsEndALink()
    {
        var cal = Cal(
            new[] { "UID:c1", "SUMMARY:A", "DTSTART:20261001T080000Z", "DTEND:20261001T090000Z", "LOCATION:Room 4.01\\, https://zoom.us/j/81234567890\\, Building B" },
            new[] { "UID:c2", "SUMMARY:B", "DTSTART:20261001T100000Z", "DTEND:20261001T110000Z", "LOCATION:https://meet.jit.si/TeamRetroOctober\\;Room 2" });
        var meetings = cal.Between(Utc(2026, 10, 1, 0), Utc(2026, 10, 2, 0));
        Assert.Equal("https://zoom.us/j/81234567890", meetings[0].JoinUrl);
        Assert.Equal("https://meet.jit.si/TeamRetroOctober", meetings[1].JoinUrl);
    }

    [Fact]
    public void TheLocationComesFirst_ThenTheNotes_ThenTheLinkFields()
    {
        var cal = Cal(new[]
        {
            "UID:o1", "SUMMARY:Both", "DTSTART:20261001T080000Z", "DTEND:20261001T090000Z",
            "X-MICROSOFT-SKYPETEAMSMEETINGURL:" + TeamsJoin, "DESCRIPTION:or Teams: " + TeamsJoin,
            "LOCATION:https://company.zoom.us/my/jdoe",
        });
        Assert.Equal("https://company.zoom.us/my/jdoe", One(cal).JoinUrl);
        Assert.Equal(MeetingLinks.Properties, new[]
        {
            "LOCATION", "DESCRIPTION", "X-ALT-DESC", "URL", "X-MICROSOFT-SKYPETEAMSMEETINGURL",
            "X-MICROSOFT-ONLINEMEETINGCONFLINK", "X-GOOGLE-CONFERENCE", "CONFERENCE",
        });
    }

    [Fact]
    public void TheConferenceProperty_AndAMovedMeetingKeepsTheSeriesLink()
    {
        var cal = Cal(
            new[] { "UID:s1", "SUMMARY:Planning", "DTSTART:20260929T090000Z", "DTEND:20260929T093000Z", "RRULE:FREQ=DAILY;COUNT=5",
                "CONFERENCE;VALUE=URI;FEATURE=AUDIO,VIDEO;LABEL=Join:https://jitsi.example.org/planning" },
            new[] { "UID:s1", "RECURRENCE-ID:20261001T090000Z", "SUMMARY:Planning (moved)", "DTSTART:20261001T110000Z", "DTEND:20261001T113000Z" });
        var m = One(cal);
        Assert.Equal("Planning (moved)", m.Title);
        Assert.Equal("https://jitsi.example.org/planning", m.JoinUrl);
    }

    [Fact]
    public void AMeetingWithoutACallHasNoLink()
    {
        var cal = Cal(new[]
        {
            "UID:n1", "SUMMARY:Lunch with the team", "DTSTART:20261001T120000Z", "DTEND:20261001T130000Z",
            "LOCATION:https://maps.google.com/?q=Cafe+Central", "DESCRIPTION:Slides: https://docs.google.com/presentation/d/abc\\n" +
            "Teams help: https://aka.ms/JoinTeamsMeeting and https://www.microsoft.com/microsoft-teams/join-a-meeting",
            "URL:https://intranet.example.com/events/42", "X-MICROSOFT-ONLINEMEETINGCONFLINK:conf:sip:jdoe@example.com;gruu;opaque=app:conf:focus:id:ABC123",
        });
        Assert.Null(One(cal).JoinUrl);
    }

    [Theory]
    [InlineData("https://teams.microsoft.com/l/meetup-join/19%3ameeting_abc%40thread.v2/0", MeetingService.Teams)]
    [InlineData("https://teams.live.com/meet/9876543210?p=xYz", MeetingService.Teams)]
    [InlineData("https://gov.teams.microsoft.us/l/meetup-join/19%3ameeting_abc%40thread.v2/0", MeetingService.Teams)]
    [InlineData("https://us02web.zoom.us/j/81234567890?pwd=cXRBd0xWeE5KN21uR2tSOHBBaFhNZz09", MeetingService.Zoom)]
    [InlineData("https://agency.zoomgov.com/j/1601234567?pwd=abc", MeetingService.Zoom)]
    [InlineData("https://zoomgov.com/j/1601234567", MeetingService.Zoom)]
    [InlineData("https://meet.google.com/abc-defg-hij?authuser=0", MeetingService.Meet)]
    [InlineData("https://example.webex.com/wbxmjs/joinservice/sites/example/meeting/download/abc", MeetingService.Webex)]
    [InlineData("https://meet.jit.si/TeamRetroOctober", MeetingService.Jitsi)]
    [InlineData("https://8x8.vc/vpaas-magic-cookie-1234/Standup", MeetingService.Jitsi)]
    [InlineData("https://jitsi-meet.corp.example.com/weekly-sync", MeetingService.Jitsi)]
    public void KnowsEachService(string link, MeetingService service) => Assert.Equal(service, MeetingLinks.Classify(link));

    [Theory]
    [InlineData("https://teams.microsoft.com/meetingOptions/?organizerId=a")]
    [InlineData("https://dialin.teams.microsoft.com/usp/pstnconferencing")]
    [InlineData("https://aka.ms/JoinTeamsMeeting")]
    [InlineData("https://support.zoom.us/hc/en-us/articles/201362193")]
    [InlineData("https://us06web.zoom.us/u/kdXyz12AbC")]
    [InlineData("https://zoom.us/j/")]
    [InlineData("https://tel.meet/abc-defg-hij?pin=1")]
    [InlineData("https://meet.google.com/")]
    [InlineData("https://help.webex.com/en-us/article/123")]
    [InlineData("https://jitsi.example.org/static/close.html")]
    [InlineData("https://meet.jit.si/")]
    [InlineData("ftp://meet.jit.si/Room")]
    [InlineData("https://notjitsi.example.com/room")]
    public void IgnoresOtherLinks(string link) => Assert.Null(MeetingLinks.Classify(link));

    [Fact]
    public void HtmlEntitiesSurviveEscapedSemicolons()
    {
        // Outlook escapes every semicolon in X-ALT-DESC, the ones closing "&amp;" inside a link too
        var cal = Cal(new[]
        {
            "UID:h1", "SUMMARY:Sync", "DTSTART:20261001T080000Z", "DTEND:20261001T083000Z",
            "X-ALT-DESC;FMTTYPE=text/html:<html>\\n<head>\\n<meta http-equiv=\"Content-Type\" content=\"text/html\\; charset=utf-8\">\\n</head>" +
            "<body><p>Join: <a href=\"https://us06web.zoom.us/j/81234567890?pwd=abc.1&amp\\;from=addon\">Zoom</a></p></body></html>",
        });
        Assert.Equal("https://us06web.zoom.us/j/81234567890?pwd=abc.1&from=addon", One(cal).JoinUrl);
    }

    [Fact]
    public void UnwrapsOutlookSafeLinks()
    {
        string wrapped = "https://eur01.safelinks.protection.outlook.com/?url=https%3A%2F%2Fteams.microsoft.com%2Fl%2Fmeetup-join%2F19%253ameeting_abc" +
            "%2540thread.v2%2F0%3Fcontext%3D%257b%257d&data=05%7C01%7C&sdata=abc&reserved=0";
        Assert.Equal("https://teams.microsoft.com/l/meetup-join/19%3ameeting_abc%40thread.v2/0?context=%7b%7d", MeetingLinks.FirstIn("Join: " + wrapped));
        Assert.Null(MeetingLinks.FirstIn("https://eur01.safelinks.protection.outlook.com/?url=https%3A%2F%2Fexample.com&data=1"));
    }
}
