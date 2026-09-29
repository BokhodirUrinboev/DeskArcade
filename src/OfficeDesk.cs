using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using DeskArcade.Engine;
using DeskArcade.Office;
using DeskArcade.Platform;

namespace DeskArcade;

/// <summary>
/// At work (tray or ☰ → At work): what Desk Arcade does for the working day besides the games. It steps aside while a
/// full-screen app or a presentation is on; reminds to rest the eyes, stretch and drink water by time at the computer;
/// warns before meetings from an .ics calendar; runs focus blocks with the games as the break; keeps timers and sticky
/// notes; answers coffee and lunch invites from co-workers; chimes when a download or a program finishes; and says when
/// the working day is over. Everything here stays on this PC except the invites, which are opt-in.
/// A one-second timer drives it; frames only run while a card or note moves.
/// </summary>
public sealed partial class OfficeDesk : IDisposable
{
    const double FullScreenCheckSeconds = 2, CalendarReadMinutes = 15, MeetingWindowMinutes = 10;
    const int MaxTimers = 10;

    static readonly Color Blue = Color.FromRgb(120, 200, 255), Green = Color.FromRgb(61, 220, 132), Gold = Color.FromRgb(255, 209, 102);
    static readonly Color Orange = Color.FromRgb(255, 176, 32), Red = Color.FromRgb(255, 107, 107), Violet = Color.FromRgb(179, 136, 255);

    static readonly HttpClient Http = CreateHttp();

    readonly OverlayWindow _w;
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly BodyBreaks _breaks = new();
    readonly FocusTimer _focus = new();
    readonly MeetingWatch _meetings = new();
    readonly EndOfDay _day = new();
    double _lastTick, _fullCheckAt = double.NegativeInfinity, _meetingsAt = double.NegativeInfinity, _activeCarry;
    int _tickCount;

    // presence: the system's idle time, or the cursor when the system cannot tell
    double? _idle;
    PixelPoint _lastCursor;
    double _cursorMovedAt;

    // the calendar
    IcsCalendar? _calendar;
    DateTime _calendarReadAt = DateTime.MinValue;
    bool _reading, _rereadWanted, _expanding, _meetingHidePending;
    int _calendarGeneration; // a new calendar makes a read of the old one worthless
    string? _calendarProblem;

    // stepping aside, and peeking out while hidden
    bool _fullScreen, _asideFull, _asideMeeting, _asideFocus, _restoreAfterAside;
    DateTime _peekUntil = DateTime.MinValue;
    readonly List<(string Title, string Sub, Color Color, string? Sound, DateTime Expires)> _held = new();

    // the card on screen (a break, breathing, the end of the day)
    CoachCard? _card;

    public OfficeDesk(OverlayWindow w)
    {
        _w = w;
        _day.WrappedOn = S.WorkEndWrapped;
        _day.NudgedOn = S.WorkEndNudged;
        _tick.Tick += (_, _) => Tick();
        InitInvites();
    }

    Settings S => _w.Settings;
    double Now => _clock.Elapsed.TotalSeconds;

    public void Start()
    {
        _lastTick = Now;
        _tick.Start();
        BuildNotes();
        StartDownloads();
        if (S.OfficeInvites) _invites.Start();
        if (!string.IsNullOrWhiteSpace(S.CalendarUrl)) ReadCalendar();
    }

    public void Dispose()
    {
        _tick.Stop();
        _invites.Dispose();
        _downloadTimer.Stop();
    }

    // ------------------------------------------------------------------ state for the rest of the overlay

    /// <summary>A focus block with "hold chat and invites" on: chat bubbles, reactions and invites wait for the break.</summary>
    public bool Quiet => _focus.Focusing && S.FocusQuiet;

    /// <summary>A break card is up: the game is paused under it and takes no clicks.</summary>
    public bool HoldsGame => _card != null && _w.OverlayVisible && !_w.IsPeeking;

    /// <summary>A note is being dragged: the overlay keeps taking the mouse until it is let go.</summary>
    public bool IsInteracting => _dragging != null;

    public FocusPhase FocusPhase => _focus.Phase;
    public bool FullScreen => _fullScreen;

    public void CollectHitShapes(List<HitShape> into)
    {
        if (_card != null) into.Add(HitShape.Box(_card.Area));
        if (_inviteCard != null) into.Add(HitShape.Box(_inviteCardRect));
        CollectNoteShapes(into);
    }

    /// <summary>One frame; true while a card or a note still moves.</summary>
    public bool Update(double dt)
    {
        bool busy = false;
        if (_card != null)
        {
            if (_card.Update(dt)) busy = true;
            else CloseCard();
        }
        busy |= UpdateNotes(dt);
        return busy;
    }

    /// <summary>The overlay was shown for real (the shortcut, the tray) while it only peeked out.</summary>
    public void OverlayShown()
    {
        _peekUntil = DateTime.MinValue;
        _restoreAfterAside = false;
    }

    /// <summary>The overlay was hidden: a card or an invite on it goes too, rather than peeking back out.</summary>
    public void OverlayHidden()
    {
        _peekUntil = DateTime.MinValue;
        CancelDrag();
        if (_card != null) CloseCard(later: true);
        HideInviteCard();
    }

    /// <summary>Shows the overlay for real, also when it only peeks out for a card or a notice.</summary>
    void ShowForReal()
    {
        if (!_w.OverlayVisible || _w.IsPeeking) _w.SetOverlayVisible(true);
    }

    // ------------------------------------------------------------------ the one-second tick

    void Tick()
    {
        double now = Now, dt = Math.Clamp(now - _lastTick, 0, 600);
        _lastTick = now;
        _tickCount++;
        var utc = DateTime.UtcNow;

        _idle = ReadIdle(now);
        bool atComputer = (_idle ?? 0) < BodyBreaks.ActiveIdleLimit;
        if (atComputer) CountActive(dt);

        if (now - _fullCheckAt >= FullScreenCheckSeconds)
        {
            _fullCheckAt = now;
            CheckFullScreen();
        }

        StepMeetings(utc, now);
        StepFocus(utc);
        StepTimers(utc);
        StepNotes(utc);
        StepWaits(dt);
        StepBreaks(dt);
        if (_tickCount % 15 == 0) StepEndOfDay(atComputer);
        if (_tickCount % 30 == 0) _w.RefreshTray(); // the status line's minutes
        SaveNotesIfMoved();
        FlushHeld(utc);
        UpdateOfficeLine(utc);
        UpdatePeek();
    }

    double? ReadIdle(double now)
    {
        double? idle = null;
        try { idle = _w.Desktop.IdleSeconds(); }
        catch (Exception) { idle = null; }
        if (idle != null) return idle;
        // the system cannot tell: the cursor moving (or the overlay being played) is the only sign of life
        if (_w.Desktop.TryGetCursor(out var cursor) && cursor != _lastCursor)
        {
            _lastCursor = cursor;
            _cursorMovedAt = now;
        }
        return now - _cursorMovedAt;
    }

    /// <summary>Time at the computer today, added to the stats a minute at a time.</summary>
    void CountActive(double dt)
    {
        _activeCarry += dt;
        if (_activeCarry < 60) return;
        long whole = (long)_activeCarry;
        _activeCarry -= whole;
        _w.Stats.Add("work.seconds", whole);
    }

    /// <summary>Seconds at the computer today (for the summary), including what is not added yet.</summary>
    public double ActiveToday => _w.Stats.Today("work.seconds") + _activeCarry;

    // ------------------------------------------------------------------ presenting and full screen

    void CheckFullScreen()
    {
        bool full = false;
        if (S.HideWhenFullScreen && !_w.Demo && !_w.Snapshotting && _w.MonitorBounds is PixelRect monitor)
        {
            try { full = _w.Desktop.IsFullScreenOn(monitor); }
            catch (Exception) { full = false; }
        }
        if (full == _fullScreen) return;
        _fullScreen = full;
        if (full)
        {
            CancelDrag();
            if (_card != null) CloseCard(later: true);
            HideInviteCard();
            _peekUntil = DateTime.MinValue;
            if (_w.IsPeeking) _w.SetPeek(false);
        }
        Aside(ref _asideFull, full);
    }

    /// <summary>
    /// Presenting, a meeting or a focus block wants the overlay out of the way. Each new reason hides it if it is up
    /// (even when it was shown by hand after an earlier one hid it), and the last one gone brings it back.
    /// </summary>
    void Aside(ref bool reason, bool on)
    {
        bool before = _asideFull || _asideMeeting || _asideFocus;
        reason = on;
        bool after = _asideFull || _asideMeeting || _asideFocus;
        if (on && _w.OverlayVisible && !_w.IsPeeking)
        {
            _restoreAfterAside = true;
            _w.SetOverlayVisible(false);
        }
        else if (before && !after)
        {
            if (_restoreAfterAside) ShowForReal(); // also when a notice happens to be peeking out right now
            _restoreAfterAside = false;
        }
    }

    /// <summary>Tray → At work → Hide while presenting or in full screen.</summary>
    public void SetHideWhenFullScreen(bool on)
    {
        S.HideWhenFullScreen = on;
        _w.SaveSettings();
        _fullCheckAt = double.NegativeInfinity;
        CheckFullScreen();
        _w.RefreshTray();
    }

    // ------------------------------------------------------------------ notices, held while presenting

    /// <summary>
    /// A notice under the scoreboard, with a sound. With the overlay hidden it peeks out for a moment (no game, no
    /// scoreboard); during a presentation it waits, silently, until the presentation is over.
    /// </summary>
    void Say(string title, string sub, Color color, string? sound = "attention", double volume = 0.7)
    {
        if (_fullScreen)
        {
            if (_held.Count < 20) _held.Add((title, sub, color, sound, DateTime.UtcNow.AddMinutes(30)));
            return;
        }
        if (sound != null) _w.Sound.Play(sound, volume);
        if (!_w.OverlayVisible || _w.IsPeeking) Peek(4.5);
        _w.Notice(title, sub, color);
    }

    void FlushHeld(DateTime utc)
    {
        if (_fullScreen || _held.Count == 0) return;
        var held = _held.Where(h => h.Expires > utc).TakeLast(3).ToList();
        _held.Clear();
        for (int i = 0; i < held.Count; i++)
        {
            var h = held[i];
            string? sound = i == 0 ? h.Sound : null; // one sound for the lot
            DispatcherTimer.RunOnce(() => Say(h.Title, h.Sub, h.Color, sound), TimeSpan.FromSeconds(1 + i * 3.2));
        }
    }

    void Peek(double seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        if (until > _peekUntil) _peekUntil = until;
        UpdatePeek();
    }

    void UpdatePeek()
    {
        bool want = !_fullScreen && (_card != null || _inviteCard != null || _dragging != null || DateTime.UtcNow < _peekUntil);
        if (want && !_w.OverlayVisible) _w.SetPeek(true);
        else if (!want && _w.IsPeeking) _w.SetPeek(false);
    }

    // ------------------------------------------------------------------ the scoreboard's at-work line

    static string Clock(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local).ToString("HH:mm", CultureInfo.InvariantCulture);

    static string Title(Meeting m) => m.Title.Length > 0 ? m.Title : L.T("Meeting");

    static string Short(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    void UpdateOfficeLine(DateTime utc)
    {
        string? text = null, brief = null;
        Color dot = Blue;
        bool urgent = false;
        var current = _meetings.Current(utc);
        var next = _meetings.Next(utc);
        int window = Math.Max(S.MeetingWarnMinutes, 15);
        if (current != null && current.End > current.Start)
        {
            text = L.F("{0} · until {1}", Short(Title(current), 22), Clock(current.End));
            brief = Clock(current.End);
            dot = Red;
        }
        else if (next != null && next.Start - utc <= TimeSpan.FromMinutes(window))
        {
            var left = next.Start - utc;
            text = L.F("{0} in {1}", Short(Title(next), 22), MeetingWatch.Until(left));
            brief = MeetingWatch.Until(left);
            dot = Orange;
            urgent = left <= TimeSpan.FromMinutes(1);
        }
        else if (_focus.Phase == FocusPhase.Focus)
        {
            brief = Hud.FormatWait(_focus.Left(utc));
            text = L.F("Focus {0}/{1} · {2}", _focus.Block, FocusTimer.BlocksPerSet, brief);
            dot = Violet;
        }
        else if (_focus.Phase == FocusPhase.Break)
        {
            brief = Hud.FormatWait(_focus.Left(utc));
            text = L.F("Break · {0}", brief);
            dot = Green;
        }
        else if (S.Timers.OrderBy(t => t.Due).FirstOrDefault() is DeskTimer timer)
        {
            brief = Hud.FormatWait(timer.Due > utc ? timer.Due - utc : TimeSpan.Zero);
            text = L.F("{0} · {1}", Short(TimerLabel(timer), 22), brief);
        }
        else if (_waits.Count > 0)
        {
            var wait = _waits[0];
            brief = Hud.FormatWait(TimeSpan.FromSeconds(Now - wait.Since));
            text = L.F("Waiting for {0} · {1}", Short(wait.Label, 22), brief);
        }
        _w.Scoreboard.SetOffice(text, brief, dot, urgent);
    }

    /// <summary>The first line of the At work menu: what is going on now.</summary>
    public string StatusLine
    {
        get
        {
            var utc = DateTime.UtcNow;
            if (_focus.Phase == FocusPhase.Focus) return L.F("Focusing · {0} left", Hud.FormatWait(_focus.Left(utc)));
            if (_focus.Phase == FocusPhase.Break) return L.F("On a break · {0} left", Hud.FormatWait(_focus.Left(utc)));
            if (_meetings.Current(utc) is Meeting now) return L.F("In {0} until {1}", Short(Title(now), 30), Clock(now.End));
            if (_meetings.Next(utc) is Meeting next && next.Start - utc < TimeSpan.FromHours(12))
                return L.F("Next: {0} at {1}", Short(Title(next), 30), Clock(next.Start));
            return L.F("At the computer today: {0}", Duration(ActiveToday));
        }
    }

    /// <summary>"7 h 42 min" or "25 min".</summary>
    public static string Duration(double seconds)
    {
        var (h, m) = EndOfDay.Split(seconds);
        return h > 0 ? L.F("{0} h {1} min", h, m) : L.F("{0} min", m);
    }

    // ------------------------------------------------------------------ signals ("deskarcade --signal ..." and the CLI)

    /// <summary>At-work signals; false for anything else, which the overlay handles itself.</summary>
    public bool Signal(string msg)
    {
        switch (msg)
        {
            case "breathe": Breathe(); return true;
            case "stretch": ShowCard(CoachKind.Stretch); return true;
            case "eyes": ShowCard(CoachKind.Eye); return true;
            case "water": ShowCard(CoachKind.Water); return true;
            case "drank": DrankWater(); return true;
            case "dayend": ShowCard(CoachKind.DayEnd); return true;
            case "focus": StartFocus(null); return true;
            case "focus-stop": StopFocus(); return true;
            case "timers-cancel": CancelTimers(); return true;
            case "note": NewNote(); return true;
            case "atwork": OpenWindow(); return true;
            case "calendar-read": ReadCalendar(); return true;
            case "coffee": SendInvite(Net.InviteKind.Coffee, 5); return true;
            case "lunch": SendInvite(Net.InviteKind.Lunch, 15); return true;
            case "walk": SendInvite(Net.InviteKind.Walk, 0); return true;
        }
        int colon = msg.IndexOf(':');
        if (colon < 0) return false;
        string verb = msg[..colon], arg = msg[(colon + 1)..];
        switch (verb.ToLowerInvariant())
        {
            case "focus" when int.TryParse(arg, NumberStyles.None, CultureInfo.InvariantCulture, out int minutes):
                StartFocus(Math.Clamp(minutes, 1, 240));
                return true;
            case "timer":
                // "timer:600:Tea" (seconds from the CLI) or "timer:10m"
                int second = arg.IndexOf(':');
                string length = second < 0 ? arg : arg[..second], label = second < 0 ? "" : arg[(second + 1)..];
                TimeSpan? d = int.TryParse(length, NumberStyles.None, CultureInfo.InvariantCulture, out int s) && second >= 0
                    ? s is > 0 and <= 86400 ? TimeSpan.FromSeconds(s) : null
                    : Durations.Parse(length);
                if (d is TimeSpan span) AddTimer(span, label);
                return true;
            case "note":
                AddNote(arg, null);
                return true;
            case "wait-pid" when int.TryParse(arg, NumberStyles.None, CultureInfo.InvariantCulture, out int pid):
                WaitForProcess(pid);
                return true;
            case "wait-file":
                WaitForFile(arg);
                return true;
            case "wait-url":
                WaitForUrl(arg);
                return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ meetings

    static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DeskArcade/" + UpdateChecker.Current.ToString(3));
        return http;
    }

    /// <summary>Where the calendar is: a web link (webcal:// read as https://), a file:// link or a path to a file.</summary>
    static (Uri? Web, string? File) Locate(string text)
    {
        text = text.Trim().Trim('"');
        if (text.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) text = "https://" + text["webcal://".Length..];
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme is "http" or "https") return (uri, null);
            if (uri.IsFile) return (null, uri.LocalPath);
        }
        if (text.StartsWith('~')) text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), text.TrimStart('~', '/', '\\'));
        return (null, text);
    }

    /// <summary>Reads the calendar text. Throws with a short reason when it cannot.</summary>
    static async Task<string> FetchAsync(string where, CancellationToken ct)
    {
        const int MaxBytes = 8 * 1024 * 1024;
        var (web, file) = Locate(where);
        if (web != null)
        {
            using var response = await Http.GetAsync(web, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new IOException(L.F("the calendar answered {0}", (int)response.StatusCode));
            if (response.Content.Headers.ContentLength > MaxBytes) throw new IOException(L.T("the calendar is too big"));
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxBytes) throw new IOException(L.T("the calendar is too big"));
            }
            return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
        if (file == null || !File.Exists(file)) throw new FileNotFoundException(L.T("no such file"));
        if (new FileInfo(file).Length > MaxBytes) throw new IOException(L.T("the calendar is too big"));
        return await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a calendar for the At work window's Check button: how many events it holds and the next meeting, or why it
    /// could not be read. Leaves the calendar in use alone.
    /// </summary>
    public async Task<(bool Ok, string Message)> TryCalendarAsync(string where)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var cal = await Task.Run(async () => IcsCalendar.Parse(await FetchAsync(where, cts.Token).ConfigureAwait(false)));
            if (cal.EventCount == 0) return (false, L.T("No meetings in it. Is it an .ics calendar?"));
            var now = DateTime.UtcNow;
            var next = cal.Between(now, now.AddDays(14)).FirstOrDefault(m => m.Start > now);
            string when = next == null ? L.T("nothing in the next two weeks")
                : L.F("next: {0}, {1} at {2}", Title(next), TimeZoneInfo.ConvertTimeFromUtc(next.Start, TimeZoneInfo.Local).ToString("ddd d MMM", CultureInfo.InvariantCulture), Clock(next.Start));
            return (true, L.F("{0} events · {1}", cal.EventCount, when));
        }
        catch (Exception e)
        {
            return (false, L.F("Couldn't read it: {0}", e is TaskCanceledException ? L.T("no answer in time") : e.Message));
        }
    }

    /// <summary>Saves a new calendar (null or blank turns meetings off) and reads it.</summary>
    public void SetCalendar(string? where)
    {
        S.CalendarUrl = string.IsNullOrWhiteSpace(where) ? null : where.Trim();
        _w.SaveSettings();
        _calendarGeneration++;
        _calendar = null;
        _calendarProblem = null;
        _meetings.Update(Array.Empty<Meeting>());
        if (S.CalendarUrl != null) ReadCalendar();
        _w.RefreshTray();
    }

    public string? CalendarProblem => _calendarProblem;

    /// <summary>
    /// Reads the calendar in the background (the file or the web, and the parsing). One read at a time: asking during
    /// one reads again after it, and a read of a calendar that was replaced meanwhile is thrown away.
    /// </summary>
    async void ReadCalendar()
    {
        if (S.CalendarUrl is not string where) return;
        if (_reading)
        {
            _rereadWanted = true;
            return;
        }
        _reading = true;
        _rereadWanted = false;
        int generation = _calendarGeneration;
        IcsCalendar? read = null;
        string? problem = null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            read = await Task.Run(async () => IcsCalendar.Parse(await FetchAsync(where, cts.Token).ConfigureAwait(false)));
        }
        catch (Exception e)
        {
            // keep the meetings read last time: a laptop off the network still gets its warnings
            problem = e is TaskCanceledException ? L.T("no answer in time") : e.Message;
        }
        _reading = false;
        if (generation == _calendarGeneration && where == S.CalendarUrl)
        {
            if (read != null) _calendar = read;
            _calendarProblem = problem;
            _calendarReadAt = DateTime.UtcNow;
            _meetingsAt = double.NegativeInfinity; // the next tick takes the meetings from what was just read
        }
        else _rereadWanted = true;
        if (_rereadWanted) ReadCalendar();
    }

    /// <summary>The meetings of the next day and a half, expanded off the UI thread (a big feed has many repeats).</summary>
    async void ExpandMeetings(DateTime utc)
    {
        if (_expanding || _calendar is not IcsCalendar calendar) return;
        _expanding = true;
        int generation = _calendarGeneration;
        try
        {
            var meetings = await Task.Run(() => calendar.Between(utc.AddHours(-2), utc.AddHours(36)));
            if (generation == _calendarGeneration && calendar == _calendar) _meetings.Update(meetings);
        }
        catch (Exception)
        {
            // a calendar this reader chokes on: keep what we had
        }
        finally
        {
            _expanding = false;
        }
    }

    void StepMeetings(DateTime utc, double now)
    {
        if (S.CalendarUrl == null) return;
        if (!_reading && utc - _calendarReadAt > TimeSpan.FromMinutes(CalendarReadMinutes)) ReadCalendar();
        if (now - _meetingsAt >= MeetingWindowMinutes * 60 && _calendar != null && !_expanding)
        {
            _meetingsAt = now;
            ExpandMeetings(utc);
        }
        foreach (var (cue, m) in _meetings.Step(utc, S.MeetingWarnMinutes))
        {
            string title = Title(m);
            switch (cue)
            {
                case MeetingCue.Warn:
                    int minutes = Math.Max(1, (int)Math.Round((m.Start - utc).TotalMinutes));
                    Say(L.F("{0} in {1} minutes", title, minutes), L.F("at {0}", Clock(m.Start)), Orange);
                    _w.Stats.Add("work.meetings");
                    break;
                case MeetingCue.Soon:
                    if (S.MeetingPause && _w.OverlayVisible && !_w.IsPeeking && !_fullScreen)
                    {
                        _w.Sound.Play("attention", 0.8);
                        _w.PauseGame(L.F("{0} in a minute", title), L.T("the game is paused · click it to resume"), Orange);
                    }
                    else Say(L.F("{0} in a minute", title), L.F("at {0}", Clock(m.Start)), Orange);
                    break;
                case MeetingCue.Start:
                    Say(L.F("{0} is starting", title), m.End > m.Start ? L.F("until {0}", Clock(m.End)) : L.T("have a good one"), Red, "done");
                    break;
            }
        }
        if (MeetingWantsAside(utc) == _asideMeeting || _meetingHidePending) return;
        if (_asideMeeting)
        {
            Aside(ref _asideMeeting, false);
            return;
        }
        // leave "starting now" on screen for a moment before the overlay goes for the meeting
        _meetingHidePending = true;
        DispatcherTimer.RunOnce(() =>
        {
            _meetingHidePending = false;
            if (MeetingWantsAside(DateTime.UtcNow) && !_asideMeeting) Aside(ref _asideMeeting, true);
        }, TimeSpan.FromSeconds(3));
    }

    bool MeetingWantsAside(DateTime utc) => S.MeetingHide && _meetings.Current(utc) is Meeting m && m.End > m.Start;

    public bool InMeeting => _meetings.Current(DateTime.UtcNow) != null;

    public IEnumerable<Meeting> MeetingsLaterToday => _meetings.LaterToday(DateTime.UtcNow, TimeZoneInfo.Local);

    public void SetMeetingWarn(int minutes)
    {
        S.MeetingWarnMinutes = minutes;
        _w.SaveSettings();
        _w.RefreshTray();
    }

    public void SetMeetingPause(bool on)
    {
        S.MeetingPause = on;
        _w.SaveSettings();
        _w.RefreshTray();
    }

    public void SetMeetingHide(bool on)
    {
        S.MeetingHide = on;
        _w.SaveSettings();
        _w.RefreshTray();
    }

    // ------------------------------------------------------------------ focus blocks

    public void StartFocus(int? minutes)
    {
        int length = minutes ?? S.FocusMinutes;
        _focus.Start(DateTime.UtcNow, length);
        Say(L.F("Focus for {0} minutes", length), L.F("block {0} of {1} · the games wait for the break", _focus.Block, FocusTimer.BlocksPerSet), Violet, "score");
        AsideForFocusSoon();
        BroadcastFocus();
        _w.RefreshTray();
    }

    /// <summary>The overlay goes out of the way once the notice has been read.</summary>
    void AsideForFocusSoon() => DispatcherTimer.RunOnce(() =>
    {
        if (_focus.Focusing && !_asideFocus) Aside(ref _asideFocus, true);
    }, TimeSpan.FromSeconds(3));

    public void StopFocus()
    {
        if (_focus.Phase == FocusPhase.Off) return;
        _focus.Stop();
        if (_asideFocus) Aside(ref _asideFocus, false);
        Say(L.T("Focus ended"), L.T("start another from At work → Focus"), Violet, null);
        ReleaseQuiet();
        BroadcastFocus();
        _w.RefreshTray();
    }

    void StepFocus(DateTime utc)
    {
        switch (_focus.Step(utc, S.FocusMinutes, S.FocusBreakMinutes, S.FocusAuto))
        {
            case FocusEvent.FocusDone:
                _w.Stats.Add("work.focus");
                if (_asideFocus) Aside(ref _asideFocus, false);
                if (!_fullScreen && !_asideMeeting) ShowForReal(); // the break is what the games are for
                int breakMinutes = (int)Math.Round(_focus.Left(utc).TotalMinutes);
                Say(L.T("Focus block done!"), L.F("a {0}-minute break · the games are yours", breakMinutes), Green, "done", 0.9);
                ReleaseQuiet();
                BroadcastFocus();
                _w.RefreshTray();
                break;
            case FocusEvent.NextFocus:
                Say(L.T("Back to focus"), L.F("{0} minutes · block {1} of {2}", S.FocusMinutes, _focus.Block, FocusTimer.BlocksPerSet), Violet, "attention");
                AsideForFocusSoon();
                BroadcastFocus();
                _w.RefreshTray();
                break;
            case FocusEvent.BreakOver:
                Say(L.T("The break is over"), L.T("start the next focus block from At work → Focus"), Violet, "attention");
                _w.RefreshTray();
                break;
        }
    }

    public void SetFocusMinutes(int minutes)
    {
        S.FocusMinutes = minutes;
        _w.SaveSettings();
        _w.RefreshTray();
    }

    public void SetFocusBreakMinutes(int minutes)
    {
        S.FocusBreakMinutes = minutes;
        _w.SaveSettings();
        _w.RefreshTray();
    }

    public void SetFocusAuto(bool on)
    {
        S.FocusAuto = on;
        _w.SaveSettings();
        _w.RefreshTray();
    }

    public void SetFocusQuiet(bool on)
    {
        bool was = Quiet;
        S.FocusQuiet = on;
        _w.SaveSettings();
        if (was && !Quiet) ReleaseQuiet();
        _w.RefreshTray();
    }

    /// <summary>The focus block is over: the chat and the invites that waited come out.</summary>
    void ReleaseQuiet()
    {
        _w.Chat.ReleaseHeld();
        ShowHeldInvites();
    }

    // ------------------------------------------------------------------ timers

    public IReadOnlyList<DeskTimer> Timers => S.Timers;

    static string TimerLabel(DeskTimer t) => t.Label.Length > 0 ? t.Label : L.T("Timer");

    public void AddTimer(TimeSpan length, string label)
    {
        label = new string(label.Where(c => !char.IsControl(c)).Take(40).ToArray()).Trim();
        if (S.Timers.Count >= MaxTimers) S.Timers.RemoveAt(0);
        S.Timers.Add(new DeskTimer(label, DateTime.UtcNow + length));
        _w.SaveSettings();
        string name = label.Length > 0 ? label : L.T("Timer");
        Say(L.F("{0} · {1}", name, Hud.FormatWait(length)), L.F("rings at {0}", Clock(DateTime.UtcNow + length)), Blue, "score", 0.5);
        _w.RefreshTray();
    }

    public void CancelTimers()
    {
        if (S.Timers.Count == 0) return;
        S.Timers.Clear();
        _w.SaveSettings();
        Say(L.T("Timers cancelled"), "", Blue, null);
        _w.RefreshTray();
    }

    void StepTimers(DateTime utc)
    {
        var due = S.Timers.Where(t => t.Due <= utc).ToList();
        if (due.Count == 0) return;
        foreach (var t in due)
        {
            S.Timers.Remove(t);
            _w.Stats.Add("work.timers");
            // a timer that rang while Desk Arcade was closed says so rather than pretend it is on time
            string sub = utc - t.Due > TimeSpan.FromMinutes(2) ? L.F("it rang at {0}", Clock(t.Due)) : L.T("time's up");
            Say(TimerLabel(t), sub, Blue, "done", 0.9);
        }
        _w.SaveSettings();
        _w.RefreshTray();
    }

    // ------------------------------------------------------------------ breaks for the body

    void StepBreaks(double dt)
    {
        bool quiet = _fullScreen || _card != null || _focus.Focusing || InMeeting;
        if (_breaks.Step(dt, _idle, S.EyeBreaks, S.StretchMinutes, S.WaterMinutes, quiet) is BreakKind due)
        {
            _breaks.Taken(due); // it comes back a full interval later, whatever is done with the card
            ShowCard(due switch { BreakKind.Eye => CoachKind.Eye, BreakKind.Stretch => CoachKind.Stretch, _ => CoachKind.Water });
        }
    }

    public void SetEyeBreaks(bool on)
    {
        S.EyeBreaks = on;
        _w.SaveSettings();
        _w.RefreshTray();
    }

    public void SetStretchMinutes(int minutes)
    {
        S.StretchMinutes = minutes;
        _w.SaveSettings();
        _w.RefreshTray();
    }

    public void SetWaterMinutes(int minutes)
    {
        S.WaterMinutes = minutes;
        _w.SaveSettings();
        _w.RefreshTray();
    }

    public void Breathe() => ShowCard(CoachKind.Breathe);

    public void StretchNow() => ShowCard(CoachKind.Stretch);

    public void DrankWater()
    {
        _breaks.Taken(BreakKind.Water);
        _w.Stats.Add("work.water");
        long today = _w.Stats.Today("work.water");
        Say(L.T("Cheers!"), today == 1 ? L.T("the first glass today") : L.F("{0} glasses today", today), Blue, "pop", 0.5);
    }

    /// <summary>Shows a card (replacing one that is up); the overlay peeks out for it when hidden, and the game pauses under it.</summary>
    void ShowCard(CoachKind kind)
    {
        if (_fullScreen) return; // nobody wants a stretch on the projector
        if (_card != null) CloseCard(later: true);
        var card = new CoachCard(kind, this, _w.Arena, DaySummary());
        card.Finished += result =>
        {
            OnCardFinished(kind, result);
            if (_card == card) CloseCard();
        };
        _card = card;
        _w.OfficeLayer.Children.Add(card.Root);
        _w.Sound.Play(kind == CoachKind.DayEnd ? "done" : "attention", 0.55);
        UpdatePeek();
        _w.HitShapesChanged();
    }

    void OnCardFinished(CoachKind kind, CoachResult result)
    {
        switch (kind, result)
        {
            case (CoachKind.Eye, CoachResult.Done):
                _w.Stats.Add("work.eye");
                break;
            case (CoachKind.Stretch, CoachResult.Done):
                _w.Stats.Add("work.stretch");
                _breaks.Taken(BreakKind.Stretch);
                break;
            case (CoachKind.Stretch, CoachResult.Later):
                _breaks.Snooze(BreakKind.Stretch, Math.Max(S.StretchMinutes, 30), 10 * 60);
                break;
            case (CoachKind.Breathe, CoachResult.Done):
                _w.Stats.Add("work.breathe");
                break;
            case (CoachKind.Water, CoachResult.Done):
                DrankWater();
                break;
            case (CoachKind.Water, CoachResult.Later):
                _breaks.Snooze(BreakKind.Water, Math.Max(S.WaterMinutes, 30), 15 * 60);
                break;
        }
        if (result == CoachResult.Done && kind is CoachKind.Eye or CoachKind.Stretch or CoachKind.Breathe)
            _w.Sound.Play("score", 0.5);
    }

    void CloseCard(bool later = false)
    {
        var card = _card;
        if (card == null) return;
        _card = null;
        card.Close(later ? CoachResult.Later : null);
        _w.OfficeLayer.Children.Remove(card.Root);
        UpdatePeek();
        _w.HitShapesChanged();
    }

    // ------------------------------------------------------------------ the end of the day

    /// <summary>The ends of the working day to choose from.</summary>
    public static readonly string[] EndTimes =
    {
        "15:00", "15:30", "16:00", "16:30", "17:00", "17:30", "18:00", "18:30", "19:00", "19:30", "20:00", "21:00",
    };

    public string? WorkEnd => S.WorkEnd;

    public void SetWorkEnd(string? time)
    {
        S.WorkEnd = EndOfDay.ParseTime(time) is TimeOnly t ? EndOfDay.FormatTime(t) : null;
        _w.SaveSettings();
        _w.RefreshTray();
    }

    void StepEndOfDay(bool atComputer)
    {
        if (_fullScreen) return; // the wrap-up waits for the presentation to end rather than get lost
        switch (_day.Step(DateTime.Now, EndOfDay.ParseTime(S.WorkEnd), atComputer && (_idle ?? 0) < 300, ActiveToday))
        {
            case DayCue.WrapUp:
                ShowCard(CoachKind.DayEnd);
                break;
            case DayCue.StillHere:
                Say(L.T("Still here?"), L.F("it's {0} · the rest can wait until tomorrow", DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture)), Violet);
                break;
            default:
                return;
        }
        S.WorkEndWrapped = _day.WrappedOn;
        S.WorkEndNudged = _day.NudgedOn;
        _w.SaveSettings();
    }

    /// <summary>Today at work, one line each, for the end-of-day card and the At work window.</summary>
    public List<string> DaySummary()
    {
        var st = _w.Stats;
        var lines = new List<string> { L.F("At the computer: {0}", Duration(ActiveToday)) };
        long focus = st.Today("work.focus"), eye = st.Today("work.eye"), stretch = st.Today("work.stretch"), water = st.Today("work.water");
        long breathe = st.Today("work.breathe"), meetings = st.Today("work.meetings");
        if (focus > 0) lines.Add(L.F("Focus blocks: {0}", focus));
        if (meetings > 0) lines.Add(L.F("Meetings: {0}", meetings));
        lines.Add(L.F("Breaks: {0} for the eyes, {1} stretches", eye, stretch));
        if (breathe > 0) lines.Add(L.F("Breathing minutes: {0}", breathe));
        lines.Add(L.F("Water: {0} glasses", water));
        lines.Add(L.F("Played: {0}", Duration(st.Today("play.ms") / 1000.0)));
        return lines;
    }

    public void OpenWindow() => OfficeWindow.ShowFor(_w, this);
}
