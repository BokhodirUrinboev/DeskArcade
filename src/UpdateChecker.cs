using System;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeskArcade;

public sealed record UpdateInfo(Version Version, string Url);

/// <summary>
/// Asks GitHub Releases whether a newer version is published. The request carries nothing but the
/// app version in the User-Agent.
/// </summary>
public static class UpdateChecker
{
    public const string Repository = "BokhodirUrinboev/DeskArcade";

    public static string ReleasesUrl => $"https://github.com/{Repository}/releases/latest";

    /// <summary>The running version, from the informational version stamped at build time (e.g. "1.3.0+sha").</summary>
    public static Version Current { get; } =
        Parse(typeof(UpdateChecker).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)
        ?? Normalize(typeof(UpdateChecker).Assembly.GetName().Version ?? new Version(0, 0, 0));

    /// <returns>The newer release, or null when this version is current or the check failed.</returns>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"DeskArcade/{Current}");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var response = await http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct);
            if (!response.IsSuccessStatusCode) return null;

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = json.RootElement;
            var latest = Parse(root.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null);
            string url = root.TryGetProperty("html_url", out var html) ? html.GetString() ?? ReleasesUrl : ReleasesUrl;
            return latest != null && latest > Current ? new UpdateInfo(latest, url) : null;
        }
        catch
        {
            return null; // offline, rate-limited or GitHub changed: try again another day
        }
    }

    /// <summary>"v1.3.0", "1.3.0+abc" or "1.3" to a three-part version.</summary>
    public static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim().TrimStart('v', 'V');
        int cut = text.IndexOfAny(new[] { '+', '-', ' ' });
        if (cut >= 0) text = text[..cut];
        return Version.TryParse(text, out var v) ? Normalize(v) : null;
    }

    static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    public static void OpenInBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS()) Process.Start("open", url);
            else Process.Start("xdg-open", url);
        }
        catch
        {
            // no browser available
        }
    }
}
