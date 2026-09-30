using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace DeskArcade.Dev;

/// <summary>
/// Coding agents and CI on the overlay (tray or ☰ → Coding agents &amp; CI): the sessions the agents' hooks report,
/// the status lanes scripts light, and the CI and pull requests of the repo folders added. It keeps the scoreboard's
/// lines current, chimes and says so when a turn, a lane or a CI run ends, records every finished wait in the wait log,
/// and polls gh or glab every two minutes while a repo is added. A 15-second timer greys and forgets what went quiet;
/// nothing else runs while idle.
/// </summary>
public sealed class DevDesk : IDisposable
{
    public static readonly TimeSpan PollEvery = TimeSpan.FromMinutes(2);

    static readonly Color Green = Color.FromRgb(61, 220, 132), Red = Color.FromRgb(255, 107, 107), Blue = Color.FromRgb(77, 163, 255);
    static readonly Color Gold = Color.FromRgb(255, 209, 102), Violet = Color.FromRgb(179, 136, 255);

    readonly OverlayWindow _w;
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(15) };
    readonly DispatcherTimer _poll = new() { Interval = PollEvery };
    readonly Dictionary<string, RepoWatch> _watches = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, IReadOnlyList<string>> _reviews = new(StringComparer.OrdinalIgnoreCase);
    readonly CancellationTokenSource _cts = new();
    TurnCard? _card;
    bool _polling;

    public DevDesk(OverlayWindow w)
    {
        _w = w;
        _tick.Tick += (_, _) => Tick();
        _poll.Tick += (_, _) => Poll();
        L.Changed += OnLanguage;
    }

    public AgentSessions Sessions { get; } = new();
    public StatusLanes Lanes { get; } = new();

    /// <summary>The repo folders followed, with the last poll's snapshot of each (null before its first poll).</summary>
    public IEnumerable<(string Folder, RepoSnapshot? Last)> Repos => S.Repos.Select(f => (f, _watches.TryGetValue(f, out var w) ? w.Last : null));

    Settings S => _w.Settings;
    bool Shown => _w.OverlayVisible && !_w.IsPeeking;

    public void Start()
    {
        _tick.Start();
        SyncRepos();
        Refresh();
    }

    public void Dispose()
    {
        _tick.Stop();
        _poll.Stop();
        _cts.Cancel();
        L.Changed -= OnLanguage;
    }

    void OnLanguage() => Refresh();

    // ------------------------------------------------------------------ signals

    /// <summary>
    /// Agent lines (agent:…), status lines (status:…), the plain working / done / attention / idle of scripts and older
    /// senders, and repo-add:FOLDER, repo-remove:FOLDER, ci-poll, lanes-clear and sessions-clear for scripts and tests.
    /// False for anything else.
    /// </summary>
    public bool Signal(string msg)
    {
        if (AgentSignal.Parse(msg) is { } agent)
        {
            OnAgent(agent);
            return true;
        }
        if (StatusMessage.Parse(msg) is { } status)
        {
            OnStatus(status);
            return true;
        }
        switch (msg)
        {
            case "working" or "start" or "done" or "stop" or "attention" or "notify" or "idle":
                OnAgent(new AgentSignal(AgentSignal.StateOf(msg)!, AgentSignal.DefaultAgent));
                return true;
            case "ci-poll":
                Poll();
                return true;
            case "lanes-clear":
                ClearLanes();
                return true;
            case "sessions-clear":
                ClearSessions();
                return true;
        }
        if (msg.StartsWith("repo-add:", StringComparison.OrdinalIgnoreCase))
        {
            AddRepo(msg["repo-add:".Length..]);
            return true;
        }
        if (msg.StartsWith("repo-remove:", StringComparison.OrdinalIgnoreCase))
        {
            RemoveRepo(msg["repo-remove:".Length..]);
            return true;
        }
        return false;
    }

    /// <summary>Adds time played to every session working and every lane running (the "you played" of their notices).</summary>
    public void AddPlayed(double seconds)
    {
        if (Sessions.Count == 0 && Lanes.Count == 0) return;
        var now = DateTime.UtcNow;
        Sessions.AddPlayed(seconds, now);
        Lanes.AddPlayed(seconds, now);
    }

    // ------------------------------------------------------------------ coding agents

    void OnAgent(AgentSignal signal)
    {
        var now = DateTime.UtcNow;
        var change = Sessions.Apply(signal, now, S.AgentSteps);
        string? flash = null;
        switch (change.Kind)
        {
            case AgentChangeKind.Started:
                _w.ResumeGame();
                _w.Stats.Max("agents.together", Sessions.WorkingCount(now));
                if (S.ClaudeAutoShow && !Shown) _w.SetOverlayVisible(true);
                break;
            case AgentChangeKind.Resumed:
                _w.ResumeGame();
                break;
            case AgentChangeKind.Finished:
                Finished(change.Session!, change.Turn);
                flash = change.Session!.Key;
                break;
            case AgentChangeKind.None or AgentChangeKind.Step:
                // a step changes one line: no need to touch the tray
                _w.Scoreboard.RenderDev();
                return;
        }
        Refresh(flash);
    }

    /// <summary>A turn ended, done or needing you: the wait log, the chime and notice naming the folder, and the settings.</summary>
    void Finished(AgentSession s, AgentTurn? turn)
    {
        bool done = s.State == AgentState.Done;
        string name = Sessions.NameOf(s), agent = AgentNames.Display(s.Agent);
        double played = turn?.Played ?? 0;
        if (turn != null) _w.Waits.Add(new WaitEntry(WaitKind.Agent, name, turn.StartUtc, turn.Took.TotalSeconds, null, played, s.Agent));
        if (done && s.Agent == AgentSignal.DefaultAgent && Shown && _w.Current != null) _w.Stats.Add("claude.done");

        bool backToWork = done && S.BackToWork && played >= 5;
        string title = !done ? L.F("{0} needs you", name) : backToWork ? L.F("{0} is done · back to work", name) : L.F("{0} is done", name);
        bool card = false;
        if (S.ClaudeNotify)
        {
            _w.Sound.Play(done ? "done" : "attention", 0.9);
            string sub = backToWork ? L.T("the game will still be here later") : done ? L.T("your turn!") : L.T("check the terminal");
            if (turn?.Took is TimeSpan w && w.TotalSeconds >= 5)
                sub = played >= 5
                    ? L.F("{0} · {1} worked {2}, you played {3}", sub, agent, Hud.FormatWait(w), Hud.FormatWait(TimeSpan.FromSeconds(played)))
                    : L.F("{0} · waited {1}", sub, Hud.FormatWait(w));
            var color = done ? Green : Red;
            card = done && turn != null && S.AgentSteps && (turn.Files.Count > 0 || turn.Commands.Count > 0) && Shown;
            if (card) ShowCard(title, sub, color, TurnCard.Lines(turn!));
            else _w.Notice(title, sub, color);
        }
        if (S.ClaudePause) _w.PauseGame(L.T("PAUSED"), L.T("click the game to resume"), Colors.White);
        if (S.ClaudeAutoHide && Shown)
        {
            // leave the notice (or the card) on screen for a moment, then get out of the way
            var state = s.State;
            DispatcherTimer.RunOnce(() =>
            {
                if (s.State == state && Sessions.All.Contains(s)) _w.SetOverlayVisible(false);
            }, TimeSpan.FromSeconds(card ? 6 : S.ClaudeNotify ? 2.5 : 0.2));
        }
    }

    void ShowCard(string title, string sub, Color color, IReadOnlyList<string> lines)
    {
        _card?.Close();
        _card = new TurnCard(title, sub, color, lines);
        _card.ShowIn(_w.OfficeLayer, _w.HudBounds, _w.Arena);
    }

    /// <summary>Tray → Coding agents &amp; CI → Claude Code → Show what Claude is doing.</summary>
    public void SetAgentSteps(bool on)
    {
        S.AgentSteps = on;
        _w.SaveSettings();
        if (on) _w.Notice(L.T("Copy the hook config again"), L.T("it adds two hooks that pass on each step"), Gold);
        Refresh();
    }

    /// <summary>Forgets the sessions that are not working.</summary>
    public void ClearSessions()
    {
        Sessions.ClearFinished(DateTime.UtcNow);
        Refresh();
    }

    /// <summary>Copies a ready config for an agent (see <see cref="AgentConfigs"/>) to the clipboard.</summary>
    public async void CopyConfig(string id)
    {
        var config = AgentConfigs.Build(id, Program.LaunchPath, Program.Profile, S.AgentSteps);
        try
        {
            if (_w.Clipboard != null) await _w.Clipboard.SetTextAsync(config.Text);
            _w.Notice(L.F("{0} config copied", config.Name), config.Where, Gold);
        }
        catch (Exception)
        {
            // the clipboard is busy: the menu is still there
        }
    }

    // ------------------------------------------------------------------ status lanes

    void OnStatus(StatusMessage m)
    {
        var now = DateTime.UtcNow;
        var change = Lanes.Apply(m, now);
        string? flash = null;
        if (change.Kind == LaneChangeKind.Started) _w.Stats.Max("lanes.lit", Lanes.Count);
        if (change is { Kind: LaneChangeKind.Finished, Lane: { } lane })
        {
            bool passed = lane.State == LaneState.Passed;
            if (change.Took is TimeSpan took) _w.Waits.Add(new WaitEntry(WaitKind.Lane, lane.Name, lane.Since, took.TotalSeconds, passed, lane.Played, lane.Source.Length > 0 ? lane.Source : null));
            string sub = lane.Name;
            if (change.Took is TimeSpan t && t.TotalSeconds >= 1) sub = L.F("{0} · took {1}", sub, Hud.FormatWait(t));
            if (change.Took != null && lane.Played >= 5) sub = L.F("{0} · you played {1}", sub, Hud.FormatWait(TimeSpan.FromSeconds(lane.Played)));
            if (lane.Note.Length > 0) sub += " · " + lane.Note;
            _w.Sound.Play(passed ? "done" : "attention", 0.8);
            _w.Notice(passed ? L.T("Passed") : L.T("Failed"), sub, passed ? Green : Red);
            flash = "lane:" + lane.Name;
        }
        if (change.Kind == LaneChangeKind.None)
        {
            _w.Scoreboard.RenderDev();
            return;
        }
        Refresh(flash);
    }

    /// <summary>Clears every lane a script lit (CI stays while its repo is followed).</summary>
    public void ClearLanes()
    {
        Lanes.ClearScripts();
        Refresh();
    }

    // ------------------------------------------------------------------ CI and pull requests

    /// <summary>Whether any repo is followed and gh or glab is there to read it.</summary>
    public static bool HasTool => Cli.Has("gh") || Cli.Has("glab");

    /// <summary>Tray → Coding agents &amp; CI → Add a repo folder…</summary>
    public async void PickRepo()
    {
        try
        {
            var folders = await _w.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = L.T("Pick a repository folder"), AllowMultiple = false });
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is string path) AddRepo(path);
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or IOException)
        {
            _w.Notice(L.T("Couldn't open a folder picker"), L.T("or run: deskarcade --signal repo-add:<folder>"), Red);
        }
    }

    public async void AddRepo(string folder)
    {
        folder = folder.Trim().Trim('"');
        string full;
        try { full = Path.GetFullPath(folder); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return; }
        if (!Directory.Exists(full))
        {
            _w.Notice(L.T("That folder is not there"), AgentStep.Cut(full, 60), Red);
            return;
        }
        // the repository's top folder, so a subfolder picked by mistake still works
        var top = await Cli.RunAsync("git", new[] { "rev-parse", "--show-toplevel" }, full);
        if (!top.Ok)
        {
            _w.Notice(top.ExitCode == null ? L.T("git is not installed") : L.T("Not a git repository"), AgentStep.Cut(full, 60), Red);
            return;
        }
        try { full = Path.GetFullPath(top.Output.Trim()); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return; }
        var watch = new RepoWatch(full);
        if (S.Repos.Contains(full, StringComparer.OrdinalIgnoreCase))
        {
            _w.Notice(L.F("Already following {0}", watch.Name), AgentStep.Cut(full, 60), Blue);
            return;
        }
        S.Repos.Add(full);
        _w.SaveSettings();
        SyncRepos();
        _w.RebuildTray();
        var origin = await Cli.RunAsync("git", new[] { "remote", "get-url", "origin" }, full);
        var remote = origin.Ok ? RemoteRepo.Parse(origin.Output) : null;
        string sub = remote == null ? L.T("its origin is not on GitHub or GitLab")
            : !Cli.Has(remote.Tool) ? L.F("{0} is not installed", remote.Tool)
            : L.F("CI and pull requests through {0}, every 2 minutes", remote.Tool);
        _w.Notice(L.F("Following {0}", watch.Name), sub, Blue);
    }

    public void RemoveRepo(string folder)
    {
        folder = folder.Trim().Trim('"');
        string? known = S.Repos.FirstOrDefault(r => string.Equals(r, folder, StringComparison.OrdinalIgnoreCase)
            || string.Equals(new RepoWatch(r).Name, folder, StringComparison.OrdinalIgnoreCase));
        if (known == null) return;
        S.Repos.Remove(known);
        _w.SaveSettings();
        SyncRepos();
        _w.RebuildTray();
        _w.Notice(L.F("Stopped following {0}", new RepoWatch(known).Name), AgentStep.Cut(known, 60), Blue);
    }

    /// <summary>A watcher for every repo in the settings, and the poll running only while there is one.</summary>
    void SyncRepos()
    {
        foreach (string folder in S.Repos.Where(f => !_watches.ContainsKey(f)).ToList()) _watches[folder] = new RepoWatch(folder);
        foreach (string gone in _watches.Keys.Where(f => !S.Repos.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            _watches.Remove(gone);
            _reviews.Remove(gone);
        }
        // CI lanes are named after their repo only when there are several
        var names = _watches.Values.Select(CiLaneName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var lane in Lanes.All.Where(l => l.Ci && !names.Contains(l.Name)).ToList()) Lanes.Remove(lane.Name);
        if (_watches.Count > 0)
        {
            if (!_poll.IsEnabled)
            {
                _poll.Start();
                DispatcherTimer.RunOnce(Poll, TimeSpan.FromSeconds(3));
            }
        }
        else _poll.Stop();
        Refresh();
    }

    string CiLaneName(RepoWatch watch) => _watches.Count <= 1 ? "CI" : "CI " + AgentStep.Cut(watch.Name, 24);

    /// <summary>One look at every repo, one after another, on the gh or glab the user signed in to.</summary>
    async void Poll()
    {
        if (_polling || _watches.Count == 0) return;
        _polling = true;
        try
        {
            foreach (var watch in _watches.Values.ToList())
            {
                RepoUpdate update;
                try
                {
                    update = await watch.PollAsync(_cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                if (!_watches.ContainsValue(watch)) continue; // removed meanwhile
                ApplyRepo(watch, update);
            }
            _w.RefreshTray();
        }
        finally
        {
            _polling = false;
        }
    }

    void ApplyRepo(RepoWatch watch, RepoUpdate u)
    {
        var snap = u.Snapshot;
        if (snap.Problem != null) return; // the menu says why; what was on the scoreboard stays until it goes quiet
        var now = DateTime.UtcNow;
        string laneName = CiLaneName(watch);
        string tool = snap.Remote?.Tool ?? "";
        if (snap.Ci is not { State: not CiState.None } ci) Lanes.Remove(laneName);
        else
        {
            var lane = Lanes.Find(laneName);
            var laneState = ci.State switch { CiState.Running => LaneState.Running, CiState.Passed => LaneState.Passed, _ => LaneState.Failed };
            string state = laneState switch { LaneState.Running => "running", LaneState.Passed => "passed", _ => "failed" };
            // a run still going is news every poll; a finished one only once, so an old result can go grey
            if (laneState == LaneState.Running || lane == null || lane.State != laneState || !lane.Ci)
            {
                if (lane is { Ci: false }) Lanes.Remove(laneName); // a script's lane of the same name gives way
                var change = Lanes.Apply(new StatusMessage(state, laneName, snap.Branch, tool), now, ci.StartUtc, ci: true);
                if (change.Kind == LaneChangeKind.Started) _w.Stats.Max("lanes.lit", Lanes.Count);
            }
        }
        if (u.CiFinished is CiRun run) AnnounceCi(watch, run, laneName, snap.Branch);
        foreach (var e in u.Events) AnnouncePr(watch, e, snap.Remote?.Host ?? RepoHost.GitHub);
        _reviews[watch.Folder] = _watches.Count > 1 ? snap.Reviews.Select(r => $"{watch.Name} {r}").ToList() : snap.Reviews;
        Refresh(u.CiFinished != null ? "lane:" + laneName : null, tray: false);
    }

    void AnnounceCi(RepoWatch watch, CiRun run, string laneName, string branch)
    {
        bool passed = run.State == CiState.Passed;
        double played = Lanes.Find(laneName)?.Played ?? 0;
        TimeSpan? took = run.StartUtc is DateTime a && run.EndUtc is DateTime b && b > a ? b - a : null;
        if (took is TimeSpan t) _w.Waits.Add(new WaitEntry(WaitKind.Ci, "CI · " + branch, run.StartUtc!.Value, t.TotalSeconds, passed, played, watch.Folder));
        if (passed) _w.Stats.Add("ci.passed");
        string sub = branch;
        if (run.Names.Length > 0) sub += " · " + AgentStep.Cut(run.Names, 40);
        if (took is TimeSpan d) sub = L.F("{0} · took {1}", sub, Hud.FormatWait(d));
        if (played >= 5) sub = L.F("{0} · you played {1}", sub, Hud.FormatWait(TimeSpan.FromSeconds(played)));
        _w.Sound.Play(passed ? "done" : "attention", 0.85);
        _w.Notice(passed ? L.F("CI passed · {0}", watch.Name) : L.F("CI failed · {0}", watch.Name), sub, passed ? Green : Red);
    }

    void AnnouncePr(RepoWatch watch, PrEvent e, RepoHost host)
    {
        string number = (host == RepoHost.GitLab ? "!" : "#") + e.Pr.Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string title = e.Kind switch
        {
            PrEventKind.Merged => L.F("{0} was merged", number),
            PrEventKind.Approved => L.F("{0} was approved", number),
            _ => L.F("New comment on {0}", number),
        };
        _w.Sound.Play(e.Kind == PrEventKind.Merged ? "best" : "score", 0.7);
        _w.Notice(title, AgentStep.Cut(e.Pr.Title, 60) + " · " + watch.Name, e.Kind == PrEventKind.Commented ? Violet : Green);
    }

    // ------------------------------------------------------------------ the scoreboard and the menu

    void Tick()
    {
        var now = DateTime.UtcNow;
        bool gone = Sessions.Prune(now) | Lanes.Prune(now);
        Refresh(tray: gone);
    }

    /// <summary>Puts the sessions, lanes and reviews on the scoreboard (and, by default, brings the tray's status line up to date).</summary>
    public void Refresh(string? flash = null, bool tray = true)
    {
        var reviews = _reviews.Values.SelectMany(r => r).ToList();
        _w.Scoreboard.SetDev(Sessions, Lanes, reviews, flash);
        if (tray) _w.RefreshTray();
    }

    /// <summary>The menu's first line: "Sessions 2 · lanes 1 · repos 1".</summary>
    public string StatusLine => Sessions.Count == 0 && Lanes.Count == 0 && S.Repos.Count == 0
        ? L.T("No agents, lanes or repos yet")
        : L.F("Sessions {0} · lanes {1} · repos {2}", Sessions.Count, Lanes.Count, S.Repos.Count);

    /// <summary>A repo's line in the menu: "api · GitHub · main", or what is wrong with it.</summary>
    public static string RepoLine(string folder, RepoSnapshot? last)
    {
        string name = new RepoWatch(folder).Name;
        if (last == null) return name;
        if (last.Problem != null) return $"{name} · {last.Problem}";
        string host = last.Remote?.Host == RepoHost.GitLab ? "GitLab" : "GitHub";
        return $"{name} · {host} · {last.Branch}";
    }
}
