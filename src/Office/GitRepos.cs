using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeskArcade.Dev;

namespace DeskArcade.Office;

/// <summary>A commit: its hash, when it was written (the author date, UTC) and its first line.</summary>
public sealed record Commit(string Hash, DateTime WhenUtc, string Subject);

/// <summary>The commits of one repo in the standup notes; <paramref name="NoEmail"/> when git has no user.email to go by.</summary>
public sealed record RepoCommits(string Repo, string Folder, IReadOnlyList<Commit> Commits, bool NoEmail = false, string? Problem = null);

/// <summary>A pull request of yours that was merged.</summary>
public sealed record MergedPull(int Number, string Title, string Url, DateTime MergedUtc);

/// <summary>
/// What "git before you go" finds in a repo: files changed and not committed, commits on local branches that no remote
/// has (ahead of the upstream, on a branch never pushed, or in a repo with no remote at all), and stashes.
/// </summary>
public sealed record RepoState(string Repo, string Folder, int Changed, int Unpushed, int Stashes, bool NoRemote, string? Problem = null)
{
    /// <summary>Nothing to do before going home.</summary>
    public bool Clean => Problem == null && Changed == 0 && Unpushed == 0 && Stashes == 0;
}

/// <summary>
/// Reads the repo folders the player added, through git (and gh for pull requests, when it is installed and signed in),
/// with the user's own configuration: the standup notes' commits by the user's git email, and what is left uncommitted,
/// unpushed or stashed at the end of the day. Nothing is changed in the repos, and nothing is fetched.
/// </summary>
public static class GitRepos
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>The folder's name, for the cards ("DeskArcade").</summary>
    public static string NameOf(string folder)
    {
        string trimmed = folder.TrimEnd('/', '\\');
        string name = Path.GetFileName(trimmed);
        return name.Length > 0 ? name : trimmed;
    }

    static Task<CliResult> Git(string folder, CancellationToken ct, params string[] args) => Cli.RunAsync("git", args, folder, Timeout, ct);

    static string Unix(DateTime utc) => "@" + new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    static IEnumerable<string> Lines(string text) => text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    // ------------------------------------------------------------------ the standup notes

    /// <summary>
    /// The commits in <paramref name="folder"/> by its git user.email written from <paramref name="fromUtc"/> up to (not
    /// including) <paramref name="toUtc"/>, on any local or remote-tracking branch, oldest first, merges left out.
    /// </summary>
    public static async Task<RepoCommits> CommitsAsync(string folder, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        string repo = NameOf(folder);
        if (!Directory.Exists(folder)) return new RepoCommits(repo, folder, Array.Empty<Commit>(), Problem: "folder not found");
        var email = await Git(folder, ct, "config", "user.email").ConfigureAwait(false);
        string me = email.Ok ? email.Output.Trim() : "";
        if (me.Length == 0) return new RepoCommits(repo, folder, Array.Empty<Commit>(), NoEmail: true);
        // --since goes by the commit date, which is never before the author date, so nothing written in the range is
        // missed; the author date then decides (a commit rebased this morning still counts for Friday)
        var log = await Git(folder, ct, "log", "--branches", "--remotes", "--no-merges", "--since=" + Unix(fromUtc),
            "--format=%H%x1f%ae%x1f%at%x1f%s").ConfigureAwait(false);
        if (!log.Ok) return new RepoCommits(repo, folder, Array.Empty<Commit>(), Problem: FirstLine(log.Error) ?? "git log failed");
        var commits = new List<Commit>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in Lines(log.Output))
        {
            var parts = line.Split('\u001f');
            if (parts.Length < 4 || !seen.Add(parts[0])) continue;
            if (!string.Equals(parts[1].Trim(), me, StringComparison.OrdinalIgnoreCase)) continue;
            if (!long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds)) continue;
            var when = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
            if (when < fromUtc || when >= toUtc) continue;
            commits.Add(new Commit(parts[0], when, string.Join('\u001f', parts[3..]).Trim()));
        }
        return new RepoCommits(repo, folder, commits.OrderBy(c => c.WhenUtc).ToList());
    }

    /// <summary>
    /// Your pull requests merged from <paramref name="fromUtc"/> up to <paramref name="toUtc"/> in the GitHub repo
    /// <paramref name="folder"/> belongs to, through gh (the caller checks that gh is installed). Empty when gh is not
    /// signed in or the folder is not a GitHub repo.
    /// </summary>
    public static async Task<List<MergedPull>> MergedPullsAsync(string folder, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        string since = fromUtc.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var result = await Cli.RunAsync("gh", new[]
        {
            "pr", "list", "--state", "merged", "--author", "@me", "--limit", "30", "--search", "merged:>=" + since,
            "--json", "number,title,url,mergedAt",
        }, folder, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        return result.Ok ? ParseMergedPulls(result.Output, fromUtc, toUtc) : new List<MergedPull>();
    }

    /// <summary>gh's JSON (number, title, url, mergedAt), the ones merged in the range, in the order merged.</summary>
    public static List<MergedPull> ParseMergedPulls(string json, DateTime fromUtc, DateTime toUtc)
    {
        var pulls = new List<MergedPull>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return pulls;
            foreach (var pr in doc.RootElement.EnumerateArray())
            {
                if (pr.ValueKind != JsonValueKind.Object) continue;
                if (!pr.TryGetProperty("mergedAt", out var merged) || merged.ValueKind != JsonValueKind.String) continue;
                if (!DateTime.TryParse(merged.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)) continue;
                if (at < fromUtc || at >= toUtc) continue;
                int number = pr.TryGetProperty("number", out var n) && n.TryGetInt32(out int num) ? num : 0;
                string title = pr.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
                string url = pr.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() ?? "" : "";
                pulls.Add(new MergedPull(number, title.Trim(), url, at));
            }
        }
        catch (JsonException)
        {
            // not what gh prints: nothing to show
        }
        return pulls.OrderBy(p => p.MergedUtc).ToList();
    }

    // ------------------------------------------------------------------ git before you go

    /// <summary>What is left uncommitted, unpushed or stashed in <paramref name="folder"/>.</summary>
    public static async Task<RepoState> StateAsync(string folder, CancellationToken ct = default)
    {
        string repo = NameOf(folder);
        if (!Directory.Exists(folder)) return new RepoState(repo, folder, 0, 0, 0, false, "folder not found");
        var status = await Git(folder, ct, "status", "--porcelain=v1", "--untracked-files=normal").ConfigureAwait(false);
        if (!status.Ok) return new RepoState(repo, folder, 0, 0, 0, false, FirstLine(status.Error) ?? "not a git repository");
        int changed = Lines(status.Output).Count();

        var remotes = await Git(folder, ct, "remote").ConfigureAwait(false);
        bool noRemote = remotes.Ok && !Lines(remotes.Output).Any();

        // commits on any local branch that no remote-tracking branch has: ahead of the upstream, on a branch never
        // pushed, or all of them when there is no remote at all; each counted once however many branches hold it
        var ahead = await Git(folder, ct, "rev-list", "--count", "--branches", "--not", "--remotes").ConfigureAwait(false);
        int unpushed = ahead.Ok && int.TryParse(ahead.Output.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : 0;

        var stash = await Git(folder, ct, "stash", "list").ConfigureAwait(false);
        int stashes = stash.Ok ? Lines(stash.Output).Count() : 0;
        return new RepoState(repo, folder, changed, unpushed, stashes, noRemote);
    }

    /// <summary>The state of each repo, read side by side.</summary>
    public static async Task<List<RepoState>> StatesAsync(IEnumerable<string> folders, CancellationToken ct = default) =>
        (await Task.WhenAll(folders.Distinct(StringComparer.OrdinalIgnoreCase).Select(f => StateAsync(f, ct))).ConfigureAwait(false)).ToList();

    static string? FirstLine(string text) => Lines(text).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
}
