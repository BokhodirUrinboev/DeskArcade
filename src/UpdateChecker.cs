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
using DeskArcade.Platform.Linux;

namespace DeskArcade;

public sealed record UpdateInfo(Version Version, string Url, IReadOnlyList<ReleaseAsset>? Assets = null);

/// <summary>A file attached to a release; <see cref="Sha256"/> comes from GitHub's asset digest when it has one.</summary>
public sealed record ReleaseAsset(string Name, string Url, string? Sha256);

/// <summary>How this copy was installed, which decides whether the game can update itself and which release asset fits.</summary>
public enum InstallKind
{
    /// <summary>Unpacked by hand, or a layout the game does not know: updates open the release page.</summary>
    Manual,
    /// <summary>The Windows installer, which upgrades in place.</summary>
    WindowsSetup,
    /// <summary>The Debian package; a new .deb installs through PolicyKit.</summary>
    Deb,
    /// <summary>A portable AppImage, which can replace its own file.</summary>
    AppImage,
    /// <summary>A Flatpak sandbox: updating is flatpak's job.</summary>
    Flatpak,
}

/// <summary>A downloaded release asset. <see cref="Verified"/> is false when the release carried no digest to check it against.</summary>
public sealed record DownloadedUpdate(string Path, bool Verified);

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

    /// <summary>How this copy was installed, worked out once from where it runs.</summary>
    public static InstallKind InstallKind { get; } = DetectInstallKind();

    static InstallKind DetectInstallKind()
    {
        if (OperatingSystem.IsWindows())
            return File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe")) ? InstallKind.WindowsSetup : InstallKind.Manual;
        if (!OperatingSystem.IsLinux()) return InstallKind.Manual;
        string? appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        return LinuxUpdate.Detect(Program.LaunchPath, Environment.ProcessPath, appImage, appImage is { Length: > 0 } && File.Exists(appImage),
            File.Exists("/.flatpak-info"), Environment.GetEnvironmentVariable("FLATPAK_ID"));
    }

    /// <summary>True when this copy can upgrade itself in place: the Windows installer, a .deb or an AppImage.</summary>
    public static bool CanInstall => InstallKind is InstallKind.WindowsSetup or InstallKind.Deb or InstallKind.AppImage;

    /// <summary>
    /// The release asset for an install kind and CPU, as the release workflow names them: "deskarcade_1.9.0_amd64.deb",
    /// "DeskArcade-1.9.0-aarch64.AppImage" or a Windows setup. Null for a kind that installs no file.
    /// </summary>
    public static string? AssetName(InstallKind kind, Architecture arch, Version v, bool standalone = false)
    {
        bool arm = arch == Architecture.Arm64;
        string n = v.ToString(3);
        return kind switch
        {
            InstallKind.Deb => $"deskarcade_{n}_{(arm ? "arm64" : "amd64")}.deb",
            InstallKind.AppImage => $"DeskArcade-{n}-{(arm ? "aarch64" : "x86_64")}.AppImage",
            InstallKind.WindowsSetup => arm ? $"DeskArcade-Setup-{n}-arm64.exe"
                : standalone ? $"DeskArcade-Setup-{n}-standalone.exe" : $"DeskArcade-Setup-{n}.exe",
            _ => null,
        };
    }

    /// <summary>The Windows installer matching this copy: ARM64, self-contained ("standalone") or the regular one.</summary>
    public static string InstallerName(Version v) =>
        AssetName(InstallKind.WindowsSetup, RuntimeInformation.ProcessArchitecture, v, File.Exists(Path.Combine(AppContext.BaseDirectory, "coreclr.dll")))!;

    /// <summary>The asset this copy would install, or null when it cannot install one.</summary>
    public static string? AssetNameHere(Version v) =>
        InstallKind == InstallKind.WindowsSetup ? InstallerName(v) : AssetName(InstallKind, RuntimeInformation.ProcessArchitecture, v);

    /// <summary>Where downloads land: an "updates" folder beside the settings, emptied before each download.</summary>
    public static string UpdatesDirectory => Path.Combine(Settings.DataDirectory, "updates");

    /// <summary>
    /// Downloads the asset matching this copy into <see cref="UpdatesDirectory"/> and checks it against GitHub's SHA-256
    /// digest when the release has one; a mismatch deletes the file. Progress comes as whole percentages. Null when there
    /// is no matching asset or the download fails. The file is world-readable, so that apt's unprivileged download user
    /// can open it.
    /// </summary>
    public static async Task<DownloadedUpdate?> DownloadInstallerAsync(UpdateInfo update, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        string? name = AssetNameHere(update.Version);
        var asset = name == null ? null : update.Assets?.FirstOrDefault(a => a.Name == name);
        if (asset == null) return null;
        string dir = UpdatesDirectory, path = Path.Combine(dir, asset.Name);
        try
        {
            Directory.CreateDirectory(dir);
            foreach (string stale in Directory.EnumerateFiles(dir)) File.Delete(stale);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"DeskArcade/{Current}");
            using var response = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? 0;
            await using (var file = File.Create(path))
            await using (var body = await response.Content.ReadAsStreamAsync(ct))
            {
                var buffer = new byte[81920];
                long done = 0;
                int shown = -1, read;
                while ((read = await body.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    int percent = Percent(done, total);
                    if (percent != shown) progress?.Report(shown = percent);
                }
            }
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
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
            return new DownloadedUpdate(path, asset.Sha256 != null);
        }
        catch
        {
            try { File.Delete(path); } catch { /* nothing was written */ }
            return null;
        }
    }

    /// <summary>Whole percent done; 0 while the size is unknown.</summary>
    public static int Percent(long done, long total) => total <= 0 ? 0 : (int)Math.Clamp(done * 100 / total, 0, 100);

    /// <summary>
    /// The notice rule for a download with no scoreboard chip to show it on: one notice as each 25% mark (25, 50, 75) is
    /// passed, none at 100, which the end of the download announces itself.
    /// </summary>
    public static bool CrossedProgressStep(int shown, int percent, int step = 25) => percent < 100 && percent / step > shown / step;

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

    public static void OpenInBrowser(string url) => Open(url);

    /// <summary>Shows a folder in the file manager; false when nothing on this desktop could open it.</summary>
    public static bool OpenFolder(string directory) => Open(directory);

    static bool Open(string target)
    {
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS()) Process.Start("open", target);
            else Process.Start("xdg-open", target);
            return true;
        }
        catch
        {
            return false; // no browser or file manager available
        }
    }
}
