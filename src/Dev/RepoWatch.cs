using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeskArcade.Dev;

/// <summary>Where a repository lives, which decides the tool that reads it.</summary>
public enum RepoHost { GitHub, GitLab }

/// <summary>A repository's origin: its host kind, server ("github.com") and path ("owner/name", "group/sub/name").</summary>
public sealed record RemoteRepo(RepoHost Host, string Server, string Path)
{
    /// <summary>The command-line tool for it: gh or glab.</summary>
    public string Tool => Host == RepoHost.GitHub ? "gh" : "glab";

    /// <summary>
    /// Reads <c>git remote get-url origin</c>: https://github.com/o/r.git, git@github.com:o/r.git,
    /// ssh://git@gitlab.example.com:2222/g/sub/r.git. A server with "github" in its name is GitHub, one with "gitlab"
    /// GitLab; null for anything else.
    /// </summary>
    public static RemoteRepo? Parse(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        url = url.Trim();
        string server, path;
        if (url.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host.Length == 0) return null;
            server = uri.Host;
            path = Uri.UnescapeDataString(uri.AbsolutePath);
        }
        else
        {
            // scp-like: [user@]host:path
            int colon = url.IndexOf(':');
            if (colon <= 0) return null;
            server = url[..colon];
            int at = server.LastIndexOf('@');
            if (at >= 0) server = server[(at + 1)..];
            path = url[(colon + 1)..];
        }
        path = path.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        if (server.Length == 0 || !path.Contains('/')) return null;
        server = server.ToLowerInvariant();
        RepoHost? host = server.Contains("github", StringComparison.Ordinal) ? RepoHost.GitHub
            : server.Contains("gitlab", StringComparison.Ordinal) ? RepoHost.GitLab : null;
        return host is RepoHost h ? new RemoteRepo(h, server, path) : null;
    }
}

/// <summary>How the latest CI of a branch stands.</summary>
public enum CiState { None, Running, Passed, Failed }

/// <summary>
/// The latest CI of a branch: its state, a key that changes with a new run (the commit for GitHub, where one push starts
/// several workflows; the pipeline for GitLab), when it started and ended, its page and its workflows' names.
/// </summary>
public sealed record CiRun(CiState State, string Key, DateTime? StartUtc, DateTime? EndUtc, string Url, string Names);

/// <summary>One of your own pull (merge) requests, as far as the notices need it.</summary>
public sealed record PullRequest(int Number, string Title, string State, bool Approved, int Approvals, int Comments, string Url)
{
    public bool Merged => State == "merged";
}

/// <summary>Something that happened to your pull request between two polls.</summary>
public enum PrEventKind { Approved, Commented, Merged }

public sealed record PrEvent(PrEventKind Kind, PullRequest Pr);

/// <summary>What one poll of a repo found.</summary>
/// <param name="Problem">Why it could not be read (not a git repo, no gh, not signed in), for the menu.</param>
public sealed record RepoSnapshot(string Branch, RemoteRepo? Remote, CiRun? Ci, IReadOnlyList<string> Reviews, IReadOnlyList<PullRequest> Mine, string? Problem = null);

/// <summary>A poll's result: the snapshot, and what is news since the last poll (nothing on the first).</summary>
/// <param name="CiFinished">A run that passed or failed since the last poll, for the chime.</param>
public sealed record RepoUpdate(RepoSnapshot Snapshot, IReadOnlyList<PrEvent> Events, CiRun? CiFinished, bool First);

/// <summary>Reads what gh prints with --json.</summary>
public static class GitHubJson
{
    /// <summary>gh run list fields read by <see cref="LatestRun"/>.</summary>
    public const string RunFields = "databaseId,status,conclusion,workflowName,headSha,createdAt,updatedAt,url,event";
    public const string ReviewFields = "number,title,url";
    public const string MineFields = "number,title,state,reviewDecision,url,author,comments,reviews";

    static readonly string[] FailedConclusions = { "failure", "timed_out", "startup_failure", "cancelled", "action_required", "stale" };

    /// <summary>
    /// The latest CI of a branch from <c>gh run list --branch B --json …</c> (newest first): every run of the newest
    /// commit together, running while any runs, failed when any failed, passed when all are through.
    /// </summary>
    public static CiRun? LatestRun(string json)
    {
        using var doc = Parse(json);
        if (doc == null || doc.RootElement.ValueKind != JsonValueKind.Array) return null;
        var runs = doc.RootElement.EnumerateArray().ToList();
        if (runs.Count == 0) return new CiRun(CiState.None, "", null, null, "", "");
        string sha = Str(runs[0], "headSha");
        var mine = runs.Where(r => Str(r, "headSha") == sha).ToList();
        bool running = mine.Any(r => Str(r, "status") != "completed");
        bool failed = mine.Any(r => FailedConclusions.Contains(Str(r, "conclusion")));
        var state = running ? CiState.Running : failed ? CiState.Failed : CiState.Passed;
        var starts = mine.Select(r => Time(r, "createdAt")).OfType<DateTime>().ToList();
        var ends = mine.Select(r => Time(r, "updatedAt")).OfType<DateTime>().ToList();
        string names = string.Join(", ", mine.Select(r => Str(r, "workflowName")).Where(n => n.Length > 0).Distinct());
        return new CiRun(state, sha, starts.Count > 0 ? starts.Min() : null, running || ends.Count == 0 ? null : ends.Max(), Str(runs[0], "url"), names);
    }

    /// <summary>The titles of the pull requests waiting for your review, from <c>gh pr list --search "review-requested:@me"</c>.</summary>
    public static List<string>? Reviews(string json)
    {
        using var doc = Parse(json);
        if (doc == null || doc.RootElement.ValueKind != JsonValueKind.Array) return null;
        return doc.RootElement.EnumerateArray().Select(p => $"#{Int(p, "number")} {Str(p, "title")}").ToList();
    }

    /// <summary>
    /// Your pull requests from <c>gh pr list --author @me --state all --json …</c>. Comments and reviews count only
    /// when someone else wrote them (the pull request's author is you).
    /// </summary>
    public static List<PullRequest>? Mine(string json)
    {
        using var doc = Parse(json);
        if (doc == null || doc.RootElement.ValueKind != JsonValueKind.Array) return null;
        var list = new List<PullRequest>();
        foreach (var p in doc.RootElement.EnumerateArray())
        {
            string me = p.TryGetProperty("author", out var a) ? Str(a, "login") : "";
            bool Others(JsonElement item) => !(item.TryGetProperty("author", out var who) && Str(who, "login") == me && me.Length > 0);
            var comments = Array(p, "comments").Where(Others).ToList();
            var reviews = Array(p, "reviews").Where(Others).ToList();
            int approvals = reviews.Count(r => Str(r, "state") == "APPROVED");
            int talk = comments.Count + reviews.Count(r => Str(r, "state") != "APPROVED" || Str(r, "body").Length > 0);
            string state = Str(p, "state").ToLowerInvariant() switch { "merged" => "merged", "closed" => "closed", _ => "open" };
            list.Add(new PullRequest(Int(p, "number"), Str(p, "title"), state, Str(p, "reviewDecision") == "APPROVED" || approvals > 0, approvals, talk, Str(p, "url")));
        }
        return list;
    }

    internal static JsonDocument? Parse(string json)
    {
        try { return JsonDocument.Parse(json); }
        catch (JsonException) { return null; }
    }

    internal static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    internal static int Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : 0;

    internal static DateTime? Time(JsonElement e, string name) =>
        DateTime.TryParse(Str(e, name), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    internal static IEnumerable<JsonElement> Array(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();
}

/// <summary>Reads what the GitLab API answers through <c>glab api</c>.</summary>
public static class GitLabJson
{
    static readonly string[] RunningStates = { "created", "waiting_for_resource", "preparing", "pending", "running", "scheduled" };

    /// <summary>The latest pipeline of a branch, from <c>projects/:id/pipelines?ref=B</c> (newest first).</summary>
    public static CiRun? LatestPipeline(string json)
    {
        using var doc = GitHubJson.Parse(json);
        if (doc == null || doc.RootElement.ValueKind != JsonValueKind.Array) return null;
        var first = doc.RootElement.EnumerateArray().FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object) return new CiRun(CiState.None, "", null, null, "", "");
        string status = GitHubJson.Str(first, "status");
        var state = RunningStates.Contains(status) ? CiState.Running
            : status is "success" or "skipped" or "manual" ? CiState.Passed : CiState.Failed;
        string key = first.TryGetProperty("id", out var id) ? id.ToString() : GitHubJson.Str(first, "sha");
        return new CiRun(state, key, GitHubJson.Time(first, "created_at"), state == CiState.Running ? null : GitHubJson.Time(first, "updated_at"),
            GitHubJson.Str(first, "web_url"), L.T("pipeline"));
    }

    /// <summary>The titles of merge requests waiting for your review, from <c>merge_requests?reviewer_username=me</c>.</summary>
    public static List<string>? Reviews(string json)
    {
        using var doc = GitHubJson.Parse(json);
        if (doc == null || doc.RootElement.ValueKind != JsonValueKind.Array) return null;
        return doc.RootElement.EnumerateArray().Select(m => $"!{GitHubJson.Int(m, "iid")} {GitHubJson.Str(m, "title")}").ToList();
    }

    /// <summary>Your merge requests from <c>merge_requests?author_username=me</c>, with the approvals read for the open ones.</summary>
    public static List<PullRequest>? Mine(string json, IReadOnlyDictionary<int, int>? approvals = null)
    {
        using var doc = GitHubJson.Parse(json);
        if (doc == null || doc.RootElement.ValueKind != JsonValueKind.Array) return null;
        var list = new List<PullRequest>();
        foreach (var m in doc.RootElement.EnumerateArray())
        {
            int iid = GitHubJson.Int(m, "iid");
            string state = GitHubJson.Str(m, "state") switch { "merged" => "merged", "closed" or "locked" => "closed", _ => "open" };
            int approved = approvals != null && approvals.TryGetValue(iid, out int n) ? n : 0;
            list.Add(new PullRequest(iid, GitHubJson.Str(m, "title"), state, approved > 0, approved, GitHubJson.Int(m, "user_notes_count"), GitHubJson.Str(m, "web_url")));
        }
        return list;
    }

    /// <summary>How many have approved, from <c>merge_requests/:iid/approvals</c>.</summary>
    public static int Approvals(string json)
    {
        using var doc = GitHubJson.Parse(json);
        if (doc == null || doc.RootElement.ValueKind != JsonValueKind.Object) return 0;
        return GitHubJson.Array(doc.RootElement, "approved_by").Count();
    }

    /// <summary>The signed-in user's name, from <c>glab api user</c>.</summary>
    public static string User(string json)
    {
        using var doc = GitHubJson.Parse(json);
        return doc == null ? "" : GitHubJson.Str(doc.RootElement, "username");
    }
}

/// <summary>Finds what changed on your pull requests between two polls.</summary>
public static class PrDiff
{
    /// <summary>One event per pull request at most, the biggest news first: merged, then approved, then commented on.</summary>
    public static List<PrEvent> Between(IReadOnlyList<PullRequest> before, IReadOnlyList<PullRequest> after)
    {
        var events = new List<PrEvent>();
        foreach (var now in after)
        {
            var was = before.FirstOrDefault(p => p.Number == now.Number);
            if (was == null) continue; // new since the last poll (you just opened it): nothing to tell
            if (now.Merged && !was.Merged) events.Add(new PrEvent(PrEventKind.Merged, now));
            else if (now.Approvals > was.Approvals || now.Approved && !was.Approved) events.Add(new PrEvent(PrEventKind.Approved, now));
            else if (now.Comments > was.Comments) events.Add(new PrEvent(PrEventKind.Commented, now));
        }
        return events;
    }
}

/// <summary>
/// Follows one repo folder through gh or glab, whichever its origin needs: the latest CI of the checked-out branch, the
/// reviews waiting for you, and news on your own pull requests. Everything runs with the user's own sign-in, read-only;
/// Desk Arcade holds no token. The first poll only takes note; later polls report what changed since the one before.
/// </summary>
public sealed class RepoWatch
{
    public const int MineLimit = 10;
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(25);

    string? _glabUser;
    (string Key, CiState State)? _announced;

    public RepoWatch(string folder) => Folder = folder;

    public string Folder { get; }

    /// <summary>The folder's own name, which the CI lane and the notices use.</summary>
    public string Name
    {
        get
        {
            string f = Folder.TrimEnd('/', '\\');
            int slash = f.LastIndexOfAny(new[] { '/', '\\' });
            return slash >= 0 ? f[(slash + 1)..] : f;
        }
    }

    /// <summary>The last poll's snapshot; null before the first.</summary>
    public RepoSnapshot? Last { get; private set; }

    public async Task<RepoUpdate> PollAsync(CancellationToken ct = default)
    {
        var snapshot = await ReadAsync(ct).ConfigureAwait(false);
        bool first = Last == null || Last.Problem != null && Last.Mine.Count == 0 && Last.Ci == null;
        var events = !first && snapshot.Problem == null ? PrDiff.Between(Last!.Mine, snapshot.Mine) : new List<PrEvent>();
        CiRun? finished = null;
        if (snapshot.Ci is { State: CiState.Passed or CiState.Failed } ci)
        {
            var mark = (ci.Key, ci.State);
            if (!first && _announced != mark) finished = ci;
            _announced = mark;
        }
        // a failed read keeps what was known, so the next good poll compares with that
        if (snapshot.Problem == null || Last == null) Last = snapshot;
        else Last = Last with { Problem = snapshot.Problem };
        return new RepoUpdate(snapshot, events, finished, first);
    }

    async Task<RepoSnapshot> ReadAsync(CancellationToken ct)
    {
        var none = System.Array.Empty<string>();
        var noPrs = System.Array.Empty<PullRequest>();
        if (!Directory.Exists(Folder)) return new RepoSnapshot("", null, null, none, noPrs, L.T("the folder is gone"));
        var head = await Cli.RunAsync("git", new[] { "rev-parse", "--abbrev-ref", "HEAD" }, Folder, Timeout, ct).ConfigureAwait(false);
        if (!head.Ok) return new RepoSnapshot("", null, null, none, noPrs, head.ExitCode == null && !Cli.Has("git") ? L.T("git is not installed") : L.T("not a git repository"));
        string branch = head.Output.Trim();
        var origin = await Cli.RunAsync("git", new[] { "remote", "get-url", "origin" }, Folder, Timeout, ct).ConfigureAwait(false);
        var remote = origin.Ok ? RemoteRepo.Parse(origin.Output) : null;
        if (remote == null) return new RepoSnapshot(branch, null, null, none, noPrs, L.T("its origin is not on GitHub or GitLab"));
        if (!Cli.Has(remote.Tool)) return new RepoSnapshot(branch, remote, null, none, noPrs, L.F("{0} is not installed", remote.Tool));
        return remote.Host == RepoHost.GitHub ? await ReadGitHubAsync(branch, remote, ct).ConfigureAwait(false) : await ReadGitLabAsync(branch, remote, ct).ConfigureAwait(false);
    }

    async Task<RepoSnapshot> ReadGitHubAsync(string branch, RemoteRepo remote, CancellationToken ct)
    {
        string repo = remote.Server == "github.com" ? remote.Path : remote.Server + "/" + remote.Path;
        CiRun? ci = null;
        if (branch != "HEAD")
        {
            var runs = await Gh(new[] { "run", "list", "-R", repo, "--branch", branch, "--limit", "20", "--json", GitHubJson.RunFields }, ct).ConfigureAwait(false);
            if (!runs.Ok) return Problem(branch, remote, runs);
            ci = GitHubJson.LatestRun(runs.Output);
        }
        var reviews = await Gh(new[] { "pr", "list", "-R", repo, "--search", "is:open review-requested:@me", "--limit", "30", "--json", GitHubJson.ReviewFields }, ct).ConfigureAwait(false);
        var mine = await Gh(new[] { "pr", "list", "-R", repo, "--author", "@me", "--state", "all", "--limit", MineLimit.ToString(CultureInfo.InvariantCulture), "--json", GitHubJson.MineFields }, ct).ConfigureAwait(false);
        if (!mine.Ok) return Problem(branch, remote, mine);
        return new RepoSnapshot(branch, remote, ci, (reviews.Ok ? GitHubJson.Reviews(reviews.Output) : null) ?? new List<string>(),
            GitHubJson.Mine(mine.Output) ?? new List<PullRequest>());
    }

    async Task<RepoSnapshot> ReadGitLabAsync(string branch, RemoteRepo remote, CancellationToken ct)
    {
        string project = "projects/" + Uri.EscapeDataString(remote.Path);
        if (_glabUser == null)
        {
            var user = await Glab(remote, "user", ct).ConfigureAwait(false);
            if (!user.Ok) return Problem(branch, remote, user);
            _glabUser = GitLabJson.User(user.Output);
        }
        string me = Uri.EscapeDataString(_glabUser);
        CiRun? ci = null;
        if (branch != "HEAD")
        {
            var pipelines = await Glab(remote, $"{project}/pipelines?ref={Uri.EscapeDataString(branch)}&per_page=1", ct).ConfigureAwait(false);
            if (!pipelines.Ok) return Problem(branch, remote, pipelines);
            ci = GitLabJson.LatestPipeline(pipelines.Output);
        }
        var reviews = await Glab(remote, $"{project}/merge_requests?state=opened&reviewer_username={me}&per_page=30", ct).ConfigureAwait(false);
        var mine = await Glab(remote, $"{project}/merge_requests?author_username={me}&order_by=updated_at&per_page={MineLimit}", ct).ConfigureAwait(false);
        if (!mine.Ok) return Problem(branch, remote, mine);
        var approvals = new Dictionary<int, int>();
        foreach (var open in (GitLabJson.Mine(mine.Output) ?? new List<PullRequest>()).Where(m => m.State == "open").Take(5))
        {
            var a = await Glab(remote, $"{project}/merge_requests/{open.Number}/approvals", ct).ConfigureAwait(false);
            if (a.Ok) approvals[open.Number] = GitLabJson.Approvals(a.Output);
        }
        return new RepoSnapshot(branch, remote, ci, (reviews.Ok ? GitLabJson.Reviews(reviews.Output) : null) ?? new List<string>(),
            GitLabJson.Mine(mine.Output, approvals) ?? new List<PullRequest>());
    }

    Task<CliResult> Gh(string[] args, CancellationToken ct) => Cli.RunAsync("gh", args, Folder, Timeout, ct);

    Task<CliResult> Glab(RemoteRepo remote, string endpoint, CancellationToken ct)
    {
        var args = new List<string> { "api", endpoint };
        if (remote.Server != "gitlab.com") args.AddRange(new[] { "--hostname", remote.Server });
        return Cli.RunAsync("glab", args, Folder, Timeout, ct);
    }

    /// <summary>A failed read, with the tool's first line of complaint ("gh auth login", "HTTP 404").</summary>
    static RepoSnapshot Problem(string branch, RemoteRepo remote, CliResult result)
    {
        string line = (result.Error + "\n" + result.Output).Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        string why = result.ExitCode == null ? L.F("{0} did not answer", remote.Tool)
            : line.Contains("auth login", StringComparison.OrdinalIgnoreCase) || line.Contains("not logged", StringComparison.OrdinalIgnoreCase) || line.Contains("401", StringComparison.Ordinal)
                ? L.F("{0} is not signed in", remote.Tool)
                : AgentStep.Cut(line.Length > 0 ? line : L.F("{0} gave an error", remote.Tool), 80);
        return new RepoSnapshot(branch, remote, null, System.Array.Empty<string>(), System.Array.Empty<PullRequest>(), why);
    }
}
