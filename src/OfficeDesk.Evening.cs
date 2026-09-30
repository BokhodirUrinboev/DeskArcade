using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeskArcade.Office;

namespace DeskArcade;

/// <summary>
/// My day, in the evening: the end-of-day card with the day's summary, today's three to tick (the rest carries over
/// to tomorrow), git before you go (the added repos with work not committed, not pushed or stashed; half an hour
/// earlier on a Friday) and where the day went.
/// </summary>
public sealed partial class OfficeDesk
{
    bool _eveningBusy;

    /// <summary>Today's end of the working day: the time set, half an hour earlier on a Friday when repos are to be checked.</summary>
    TimeOnly? WorkEndToday => EndOfDay.EndOn(DateOnly.FromDateTime(DateTime.Now), EndOfDay.ParseTime(S.WorkEnd), S.GitBeforeYouGo && Repos.Count > 0);

    /// <summary>Reads the repos (in the background, 25 seconds at most) and shows the end-of-day card.</summary>
    async void ShowDayEnd()
    {
        if (_eveningBusy) return;
        _eveningBusy = true;
        try
        {
            List<RepoState>? states = null;
            var repos = Repos;
            if (S.GitBeforeYouGo && repos.Count > 0)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                try
                {
                    states = await GitRepos.StatesAsync(repos, cts.Token);
                }
                catch (Exception)
                {
                    states = null; // git would not answer: the card goes without
                }
            }
            ShowCard(CoachKind.DayEnd, DayEndContent(states));
            CountCleanDay(states);
        }
        finally
        {
            _eveningBusy = false;
        }
    }

    CardContent DayEndContent(List<RepoState>? states)
    {
        var content = new CardContent();
        content.Summary.AddRange(DaySummary());

        if (S.Three.Items.Count > 0)
        {
            var three = new CardSection { Title = L.T("Today's three"), Checklist = S.Three, Tick = ToggleThree };
            if (!S.Three.AllDone) three.Hint(L.T("tick what got done · the rest carries over to tomorrow"));
            content.Sections.Add(three);
        }

        if (S.GitBeforeYouGo)
        {
            var git = new CardSection { Title = L.T("Git before you go") };
            foreach (var (text, dim) in GitLines(states, Repos.Count)) git.Lines.Add((text, dim));
            content.Sections.Add(git);
        }

        if (S.TrackApps && AppsLine(DateOnly.FromDateTime(DateTime.Now)) is string apps)
            content.Sections.Add(new CardSection { Title = L.T("Where the day went") }.Line(apps));
        return content;
    }

    /// <summary>"DeskArcade: 2 commits not pushed", one line per repo with something left; (text, dim).</summary>
    static List<(string Text, bool Dim)> GitLines(List<RepoState>? states, int repoCount)
    {
        var lines = new List<(string, bool)>();
        if (repoCount == 0)
        {
            lines.Add((AddReposHint, true));
            return lines;
        }
        if (states == null)
        {
            lines.Add((L.T("git didn't answer in time"), true));
            return lines;
        }
        foreach (var s in states.Where(s => !s.Clean).Take(6))
            lines.Add((RepoLine(s), s.Problem != null));
        if (states.Count > 0 && states.All(s => s.Clean))
            lines.Add((states.Count == 1 ? L.F("{0}: all committed and pushed ✓", states[0].Repo) : L.F("All {0} repos committed and pushed ✓", states.Count), false));
        return lines;
    }

    /// <summary>"DeskArcade: 3 uncommitted changes, 2 commits not pushed, 1 stash".</summary>
    public static string RepoLine(RepoState s)
    {
        if (s.Problem != null) return L.F("{0}: couldn't read it ({1})", s.Repo, Short(s.Problem, 50));
        var parts = new List<string>();
        if (s.Changed > 0) parts.Add(s.Changed == 1 ? L.T("1 uncommitted change") : L.F("{0} uncommitted changes", s.Changed));
        if (s.Unpushed > 0)
        {
            string unpushed = s.Unpushed == 1 ? L.T("1 commit not pushed") : L.F("{0} commits not pushed", s.Unpushed);
            parts.Add(s.NoRemote ? L.F("{0} (no remote)", unpushed) : unpushed);
        }
        if (s.Stashes > 0) parts.Add(s.Stashes == 1 ? L.T("1 stash") : L.F("{0} stashes", s.Stashes));
        return L.F("{0}: {1}", s.Repo, string.Join(", ", parts));
    }

    /// <summary>Every repo committed and pushed at the end of the day counts toward "Clean desk", once a day.</summary>
    void CountCleanDay(List<RepoState>? states)
    {
        if (states is not { Count: > 0 } || !states.All(s => s.Clean)) return;
        string today = WorkDays.Key(DateOnly.FromDateTime(DateTime.Now));
        if (S.GitCleanOn == today) return;
        S.GitCleanOn = today;
        _w.SaveSettings();
        _w.Stats.Add("work.pushed");
    }

    /// <summary>A card of My day was put away: after the end of the day, what is left of today's three carries over.</summary>
    void MyDayCardFinished(CoachKind kind, CoachResult result)
    {
        if (kind != CoachKind.DayEnd || S.Three.Items.Count == 0) return;
        var (_, carried) = S.Three.CarryOver();
        _w.SaveSettings();
        BuildThree();
        if (carried > 0) Say(carried == 1 ? L.T("1 thing carried over to tomorrow") : L.F("{0} things carried over to tomorrow", carried), L.T("it waits under the scoreboard"), Violet, null);
    }

    public void SetGitBeforeYouGo(bool on)
    {
        S.GitBeforeYouGo = on;
        _w.SaveSettings();
        _w.RefreshTray();
    }

    /// <summary>The repo folders' names, for Today at work.</summary>
    public IReadOnlyList<string> RepoNames => Repos.Select(GitRepos.NameOf).ToList();
}
