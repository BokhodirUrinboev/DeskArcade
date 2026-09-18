using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeskArcade;

public sealed record UpdateInfo(Version Version, string Url, IReadOnlyList<ReleaseAsset>? Assets = null);

/// <summary>A file attached to a release; <see cref="Sha256"/> comes from GitHub's asset digest when it has one.</summary>
public sealed record ReleaseAsset(string Name, string Url, string? Sha256);

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
            var assets = new List<ReleaseAsset>();
            if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var a in list.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? href = a.TryGetProperty("browser_download_url", out var d) ? d.GetString() : null;
                    string? digest = a.TryGetProperty("digest", out var g) ? g.GetString() : null;
                    if (name != null && href != null)
                        assets.Add(new ReleaseAsset(name, href, digest is { } x && x.StartsWith("sha256:", StringComparison.Ordinal) ? x[7..] : null));
                }
            return latest != null && latest > Current ? new UpdateInfo(latest, url, assets) : null;
        }
        catch
        {
            return null; // offline, rate-limited or GitHub changed: try again another day
        }
    }

    /// <summary>True when this copy came from the Windows installer, which can upgrade it in place.</summary>
    public static bool CanInstall =>
        OperatingSystem.IsWindows() && File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    /// <summary>The installer matching this copy: ARM64, self-contained ("standalone") or the regular one.</summary>
    public static string InstallerName(Version v) =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? $"DeskArcade-Setup-{v.ToString(3)}-arm64.exe"
        : File.Exists(Path.Combine(AppContext.BaseDirectory, "coreclr.dll")) ? $"DeskArcade-Setup-{v.ToString(3)}-standalone.exe"
        : $"DeskArcade-Setup-{v.ToString(3)}.exe";

    /// <summary>
    /// Downloads the matching installer to the temp folder and checks it against GitHub's SHA-256 digest when
    /// the release has one. Null when there is no matching installer or the download or check fails.
    /// </summary>
    public static async Task<string?> DownloadInstallerAsync(UpdateInfo update, CancellationToken ct = default)
    {
        var asset = update.Assets?.FirstOrDefault(a => a.Name == InstallerName(update.Version));
        if (asset == null) return null;
        string path = Path.Combine(Path.GetTempPath(), asset.Name);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"DeskArcade/{Current}");
            await using (var file = File.Create(path))
            await using (var body = await http.GetStreamAsync(asset.Url, ct))
                await body.CopyToAsync(file, ct);
            if (asset.Sha256 is { } expected)
            {
                await using var check = File.OpenRead(path);
                string actual = Convert.ToHexString(await SHA256.HashDataAsync(check, ct));
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(path);
                    return null;
                }
            }
            return path;
        }
        catch
        {
            return null;
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
