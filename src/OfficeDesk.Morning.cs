using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeskArcade.Dev;
using DeskArcade.Office;

namespace DeskArcade;

/// <summary>
/// My day, in the morning: a card on the first activity of each working day with the standup notes (the last working
/// day's commits in the added repos by your git email, your pull requests merged, meetings and focus blocks, and a
/// copy button), room to focus (the longest free stretch between today's meetings, offered as a focus block) and
/// today's three; on the first morning of a week, the wait report. Also the focus block planned for later and the
/// signals and settings of My day.
/// </summary>
public sealed partial class OfficeDesk
{
    const int MaxRepos = 12, CommitsShown = 3, PullsShown = 3;

    readonly MorningCue _morning = new();
    bool _morningBusy;

    /// <summary>Where repos are added, for the cards and windows that read them.</summary>
    public static string AddReposHint => L.T("Add your repos under Coding agents & CI → Add a repo folder…");

    /// <summary>The repo folders the player added, each once.</summary>
    List<string> Repos => S.Repos.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxRepos).ToList();

    /// <summary>The day's own name: "Monday".</summary>
    public static string DayName(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => L.T("Monday"),
        DayOfWeek.Tuesday => L.T("Tuesday"),
        DayOfWeek.Wednesday => L.T("Wednesday"),
        DayOfWeek.Thursday => L.T("Thursday"),
        DayOfWeek.Friday => L.T("Friday"),
        DayOfWeek.Saturday => L.T("Saturday"),
        _ => L.T("Sunday"),
    };

    /// <summary>"Friday 26.09".</summary>
    public static string DayLabel(DateOnly day) => DayName(day.DayOfWeek) + " " + day.ToString("dd.MM", CultureInfo.InvariantCulture);

    static DateTime ToUtc(DateTime local) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeZoneInfo.Local);

    /// <summary>Everything My day does on the one-second tick: a planned focus block, where the day went, the morning card.</summary>
    void StepMyDay(DateTime utc, bool atComputer)
    {
        if (_tickCount == 1) BuildThree(); // the list from last time, once the scoreboard has its place
        StepPlannedFocus(utc);
        StepApps(atComputer);
        FollowScoreboard();
        if (_tickCount % 15 != 0) return;
        if (S.Three.RollOver(DateOnly.FromDateTime(DateTime.Now)))
        {
            _w.SaveSettings();
            BuildThree();
        }
        if (!_w.Demo && !_w.Snapshotting) StepMorning(atComputer); // a demo or a snapshot shows the game, not the morning card
        if (_tickCount % 300 == 0) SaveMyDay();
    }

    void SaveMyDay() => _apps.Save();

    /// <summary>
    /// My day's signals: "morning", "waitreport" (the card), "waitreport-open" (the stats window's page), "three",
    /// "three:first|second|third", "standup-copy", "focus-at-cancel".
    /// </summary>
    bool SignalMyDay(string msg)
    {
        switch (msg)
        {
            case "morning": ShowMorning(); return true;
            case "waitreport": ShowWaitReport(lastWeek: false); return true;
            case "waitreport-open": OpenWaitReport(); return true;
            case "three": OpenThree(); return true;
            case "standup-copy": CopyStandup(); return true;
            case "focus-at-cancel": CancelPlannedFocus(); return true;
        }
        if (msg.StartsWith("three:", StringComparison.Ordinal))
        {
            SetThree(msg[6..].Split('|'));
            return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ the morning card

    void StepMorning(bool atComputer)
    {
        if (!S.MorningCard || _morningBusy || _card != null) return;
        _morning.ShownOn = S.MorningShown;
        bool busy = _fullScreen || InMeeting || _focus.Focusing;
        if (!_morning.Step(DateTime.Now, atComputer && (_idle ?? 0) < 60, busy, EndOfDay.ParseTime(S.WorkEnd))) return;
        S.MorningShown = _morning.ShownOn;
        _w.SaveSettings();
        ShowMorning();
    }

    /// <summary>At work → My day → Show the morning card, or the first activity of a working day.</summary>
    public void ShowMorningNow() => ShowMorning();

    /// <summary>Gathers the standup notes (git and gh run in the background, 30 seconds at most) and shows the morning card.</summary>
    async void ShowMorning()
    {
        if (_morningBusy) return;
        _morningBusy = true;
        try
        {
            var now = DateTime.Now;
            var today = DateOnly.FromDateTime(now);
            if (S.Three.RollOver(today))
            {
                _w.SaveSettings();
                BuildThree();
            }
            var notesTask = StandupAsync(today);
            var roomTask = RoomToFocusAsync(DateTime.UtcNow);
            var notes = await notesTask;
            var (room, meetingsToday) = await roomTask;
            WaitReport? weekly = null;
            _morning.ReportWeek = S.WaitReportWeek;
            if (_w.Waits.Entries.Count > 0 && WaitReport.LastWeek(_w.Waits.Entries, today, TimeZoneInfo.Local) is { Empty: false } report && _morning.TakeWeeklyReport(today))
            {
                weekly = report;
                S.WaitReportWeek = _morning.ReportWeek;
                _w.SaveSettings();
            }
            ShowCard(CoachKind.Morning, MorningContent(today, notes, room, meetingsToday, weekly));
        }
        finally
        {
            _morningBusy = false;
        }
    }

    CardContent MorningContent(DateOnly today, StandupNotes notes, FreeStretch? room, int? meetingsToday, WaitReport? weekly)
    {
        var content = new CardContent
        {
            Title = DayLabel(today),
            Hint = meetingsToday switch
            {
                null => null,
                0 => L.T("no meetings today"),
                1 => L.T("one meeting today"),
                int n => L.F("{0} meetings today", n),
            },
        };

        var standup = new CardSection { Title = L.F("Standup notes · {0}", DayLabel(notes.Day)) };
        foreach (var (text, dim) in StandupLines(notes, forCard: true)) standup.Lines.Add((text, dim));
        standup.Actions.Add(new CardAction(L.T("Copy the notes"), () =>
        {
            CopyToClipboard(StandupText(notes));
            _w.Stats.Add("work.standup");
            return L.T("Copied ✓");
        }, Main: true));
        content.Sections.Add(standup);

        if (S.CalendarUrl != null)
        {
            var focus = new CardSection { Title = L.T("Room to focus") };
            if (room is FreeStretch s)
            {
                bool now = s.Start <= DateTime.UtcNow.AddMinutes(2);
                string free = AppTime.HoursMinutes((int)s.Length.TotalMinutes);
                focus.Line(now ? L.F("{0} free from now, focus now?", free) : L.F("{0} free from {1}, focus then?", free, Clock(s.Start)));
                int minutes = FreeTime.FocusMinutesFor(s, S.FocusMinutes);
                focus.Actions.Add(new CardAction(now ? L.F("Focus for {0} minutes", minutes) : L.F("Focus at {0}", Clock(s.Start)), () =>
                {
                    if (now)
                    {
                        StartFocus(minutes);
                        return L.T("Focusing now ✓");
                    }
                    PlanFocus(s.Start, minutes);
                    return L.F("Focus at {0}", Clock(s.Start)) + " ✓";
                }, Main: true));
            }
            else focus.Hint(L.T("no 25 minutes free between meetings today"));
            content.Sections.Add(focus);
        }

        var three = new CardSection { Title = L.T("Today's three"), Checklist = S.Three.Items.Count > 0 ? S.Three : null, Tick = ToggleThree };
        if (S.Three.Items.Count == 0) three.Hint(L.T("up to three things to get done today"));
        else if (S.Three.Items.All(i => i.Carried)) three.Hint(L.T("carried over from last time"));
        three.Actions.Add(new CardAction(S.Three.Items.Count == 0 ? L.T("Write them…") : L.T("Change…"), () =>
        {
            OpenThree();
            return null;
        }, Main: S.Three.Items.Count == 0));
        content.Sections.Add(three);

        if (weekly != null)
        {
            var waits = new CardSection { Title = L.T("Last week's waits") };
            waits.Line(L.F("Waited {0} · played {1} of it", Duration(weekly.All.Seconds), Duration(weekly.All.Played)));
            if (weekly.Slower.Count > 0) waits.Line(WaitReport.TrendLine(weekly.Slower[0]));
            waits.Actions.Add(new CardAction(L.T("The wait report"), () =>
            {
                ShowWaitReport(lastWeek: true);
                return null;
            }));
            content.Sections.Add(waits);
        }
        return content;
    }

    // ------------------------------------------------------------------ the standup notes

    /// <summary>What the standup notes say: the last working day's commits by repo, pull requests merged, meetings and focus blocks.</summary>
    sealed class StandupNotes
    {
        public required DateOnly Day;
        public List<RepoCommits> Repos { get; } = new();
        public List<MergedPull> Pulls { get; } = new();
        public List<string> Meetings { get; } = new();
        public long MeetingCount, Focus, ActiveSeconds;
        public bool NoRepos;
    }

    /// <summary>Reads the standup notes: git (and gh when installed) for each repo side by side, the calendar and the day history.</summary>
    async Task<StandupNotes> StandupAsync(DateOnly today)
    {
        var (fromLocal, toLocal) = WorkDays.StandupRange(today);
        DateTime fromUtc = ToUtc(fromLocal), toUtc = ToUtc(toLocal);
        var day = WorkDays.LastWorkingDay(today);
        var notes = new StandupNotes
        {
            Day = day,
            MeetingCount = _w.Stats.OverDays("work.meetings", day, today),
            Focus = _w.Stats.OverDays("work.focus", day, today),
            ActiveSeconds = _w.Stats.OverDays("work.seconds", day, today),
        };
        var repos = Repos;
        notes.NoRepos = repos.Count == 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var commits = Task.WhenAll(repos.Select(r => GitRepos.CommitsAsync(r, fromUtc, toUtc, cts.Token)));
            bool gh = repos.Count > 0 && Cli.Has("gh");
            var pulls = gh ? Task.WhenAll(repos.Select(r => GitRepos.MergedPullsAsync(r, fromUtc, toUtc, cts.Token))) : Task.FromResult(Array.Empty<List<MergedPull>>());
            var meetings = _calendar is IcsCalendar calendar ? Task.Run(() => calendar.Between(fromUtc, toUtc)) : Task.FromResult(new List<Meeting>());
            notes.Repos.AddRange(await commits);
            // two folders of one GitHub repo (worktrees, clones) list the same pull requests
            notes.Pulls.AddRange((await pulls).SelectMany(p => p).GroupBy(p => p.Url.Length > 0 ? p.Url : p.Number.ToString(CultureInfo.InvariantCulture)).Select(g => g.First()).OrderBy(p => p.MergedUtc));
            notes.Meetings.AddRange((await meetings).Where(m => m.End > m.Start).Select(Title).Distinct());
        }
        catch (Exception)
        {
            // a repo or the calendar that would not answer: the notes say what they have
        }
        return notes;
    }

    /// <summary>The standup notes line by line: (text, dim). On the card the commits are cut to a few per repo.</summary>
    static List<(string Text, bool Dim)> StandupLines(StandupNotes notes, bool forCard)
    {
        var lines = new List<(string, bool)>();
        if (notes.NoRepos) lines.Add((AddReposHint, true));
        foreach (var repo in notes.Repos)
        {
            if (repo.Problem != null) lines.Add((L.F("{0}: couldn't read it ({1})", repo.Repo, Short(repo.Problem, 50)), true));
            else if (repo.NoEmail) lines.Add((L.F("{0}: git has no user.email to find your commits by", repo.Repo), true));
            if (repo.Commits.Count == 0) continue;
            lines.Add((repo.Commits.Count == 1 ? L.F("{0}: 1 commit", repo.Repo) : L.F("{0}: {1} commits", repo.Repo, repo.Commits.Count), false));
            int shown = forCard ? CommitsShown : int.MaxValue;
            foreach (var c in repo.Commits.Take(shown)) lines.Add(("  – " + (forCard ? Short(c.Subject, 64) : c.Subject), false));
            if (repo.Commits.Count > shown) lines.Add((L.F("  and {0} more", repo.Commits.Count - shown), true));
        }
        if (!notes.NoRepos && notes.Repos.All(r => r.Commits.Count == 0))
            lines.Add((L.T("no commits by you that day"), true));
        foreach (var p in notes.Pulls.Take(forCard ? PullsShown : int.MaxValue))
            lines.Add((L.F("Merged: #{0} {1}", p.Number, forCard ? Short(p.Title, 56) : p.Title), false));
        if (notes.Meetings.Count > 0)
            lines.Add((L.F("In meetings: {0}", Short(string.Join(", ", notes.Meetings), forCard ? 70 : 400)), false));
        else if (notes.MeetingCount > 0)
            lines.Add((L.F("Meetings: {0}", notes.MeetingCount), false));
        if (notes.Focus > 0) lines.Add((L.F("Focus blocks: {0}", notes.Focus), false));
        return lines;
    }

    /// <summary>The standup notes as plain text for the standup chat.</summary>
    static string StandupText(StandupNotes notes)
    {
        var sb = new StringBuilder();
        sb.Append(L.F("Standup notes · {0}", DayLabel(notes.Day))).Append('\n');
        foreach (var (text, dim) in StandupLines(notes, forCard: false))
            if (!dim || text.StartsWith("  ", StringComparison.Ordinal)) sb.Append(text).Append('\n');
        return sb.ToString();
    }

    /// <summary>At work → My day → Copy standup notes: reads them and puts them on the clipboard.</summary>
    public async void CopyStandup()
    {
        var notes = await StandupAsync(DateOnly.FromDateTime(DateTime.Now));
        CopyToClipboard(StandupText(notes));
        _w.Stats.Add("work.standup");
        Say(L.T("Standup notes copied"), L.F("{0} · paste them in the standup chat", DayLabel(notes.Day)), Blue, "pop", 0.5);
    }

    async void CopyToClipboard(string text)
    {
        try
        {
            if (_w.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
        }
        catch (Exception)
        {
            // the clipboard is busy: nothing to be done
        }
    }

    // ------------------------------------------------------------------ room to focus

    /// <summary>The end of today's working day in UTC: the time set, or 18:00.</summary>
    static DateTime DayEndUtc(string? workEnd)
    {
        var end = EndOfDay.ParseTime(workEnd) ?? MorningCue.DefaultEnd;
        return ToUtc(DateTime.Today + end.ToTimeSpan());
    }

    /// <summary>The meetings of the rest of today, from the calendar (read now if need be, off the UI thread).</summary>
    async Task<List<Meeting>?> MeetingsRestOfTodayAsync(DateTime utc)
    {
        if (S.CalendarUrl == null) return null;
        for (int i = 0; i < 20 && _calendar == null && (_reading || _calendarProblem == null); i++) await Task.Delay(500); // the first read at start
        if (_calendar is not IcsCalendar calendar) return null;
        var endOfDay = ToUtc(DateTime.Today.AddDays(1));
        try
        {
            return await Task.Run(() => calendar.Between(utc.AddHours(-12), endOfDay));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The longest free stretch of the rest of the working day, and how many meetings today still has.</summary>
    async Task<(FreeStretch? Room, int? MeetingsToday)> RoomToFocusAsync(DateTime utc)
    {
        if (await MeetingsRestOfTodayAsync(utc) is not { } meetings) return (null, null);
        var until = DayEndUtc(S.WorkEnd);
        int later = meetings.Count(m => m.Start >= utc.AddMinutes(-5) && m.Start < ToUtc(DateTime.Today.AddDays(1)));
        return (FreeTime.Longest(meetings, utc, until), later);
    }

    /// <summary>The free stretches of the rest of the working day, for Today at work.</summary>
    public IEnumerable<FreeStretch> FreeStretchesToday()
    {
        var utc = DateTime.UtcNow;
        return S.CalendarUrl == null ? Enumerable.Empty<FreeStretch>() : FreeTime.Between(_meetings.Meetings, utc, DayEndUtc(S.WorkEnd), FreeTime.Listable);
    }

    // ------------------------------------------------------------------ a focus block planned for later

    /// <summary>When the focus block accepted on the morning card starts, or null.</summary>
    public DateTime? PlannedFocus => S.FocusAt is DateTime at ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : null;

    public void PlanFocus(DateTime startUtc, int minutes)
    {
        S.FocusAt = startUtc;
        S.FocusAtMinutes = minutes;
        _w.SaveSettings();
        Say(L.F("Focus at {0}", Clock(startUtc)), L.F("{0} minutes · a notice when it starts", minutes), Violet, "score", 0.5);
        _w.RefreshTray();
    }

    public void CancelPlannedFocus()
    {
        if (S.FocusAt == null) return;
        S.FocusAt = null;
        _w.SaveSettings();
        _w.RefreshTray();
    }

    /// <summary>Starts the planned focus block when its time comes; one missed by more than 20 minutes (the PC slept) is dropped.</summary>
    void StepPlannedFocus(DateTime utc)
    {
        if (PlannedFocus is not DateTime at || utc < at) return;
        S.FocusAt = null;
        _w.SaveSettings();
        if (utc - at > TimeSpan.FromMinutes(20) || _focus.Phase == FocusPhase.Focus) return;
        StartFocus(S.FocusAtMinutes > 0 ? S.FocusAtMinutes : null);
    }

    // ------------------------------------------------------------------ the wait report

    /// <summary>At work → My day → The wait report…: the page in the stats window.</summary>
    public void OpenWaitReport() => StatsWindow.ShowFor(_w, waits: true);

    /// <summary>The week's waits on a card: last week (Monday to Sunday) from the morning card, the last seven days otherwise.</summary>
    public void ShowWaitReport(bool lastWeek)
    {
        var report = lastWeek
            ? WaitReport.LastWeek(_w.Waits.Entries, DateOnly.FromDateTime(DateTime.Now), TimeZoneInfo.Local)
            : WaitReport.Build(_w.Waits.Entries, DateTime.UtcNow);
        var content = new CardContent
        {
            Step = L.F("Wait report · {0}", report.Week(TimeZoneInfo.Local)).ToUpperInvariant(),
            Title = report.Empty ? L.T("Nothing waited on") : L.F("Waited {0} in all", Duration(report.All.Seconds)),
            Hint = report.Empty ? L.T("builds, tests, CI and coding agents show here once you wait on them") : report.PlayedLine(),
        };
        content.Summary.AddRange(report.SumLines());
        if (report.Slowest.Count > 0)
        {
            var slowest = new CardSection { Title = L.T("Slowest") };
            foreach (var c in report.Slowest) slowest.Line(WaitReport.SlowLine(c));
            content.Sections.Add(slowest);
        }
        if (report.Slower.Count > 0)
        {
            var slower = new CardSection { Title = L.T("Getting slower") };
            foreach (var t in report.Slower) slower.Line(WaitReport.TrendLine(t));
            content.Sections.Add(slower);
        }
        content.Buttons.Add(new CardAction(L.T("Copy the report"), () =>
        {
            CopyToClipboard(report.Text(TimeZoneInfo.Local));
            return L.T("Copied ✓");
        }, Main: !report.Empty));
        content.Buttons.Add(new CardAction(L.T("In the stats window…"), () =>
        {
            OpenWaitReport();
            return null;
        }));
        ShowCard(CoachKind.Waits, content);
    }

    // ------------------------------------------------------------------ settings

    public void SetMorningCard(bool on)
    {
        S.MorningCard = on;
        _w.SaveSettings();
        _w.RefreshTray();
    }
}
