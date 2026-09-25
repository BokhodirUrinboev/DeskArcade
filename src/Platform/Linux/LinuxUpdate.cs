using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace DeskArcade.Platform.Linux;

/// <summary>
/// The Linux side of in-app updates: which kind of install this copy is, and how a downloaded .deb or AppImage goes in.
/// A .deb needs root, which only PolicyKit (pkexec) or sudo in a terminal may grant; the game itself never runs anything
/// as root. The privileged step runs inside a plain user shell that outlives this process, because the package's prerm
/// closes the running game before its files change; that shell then starts the new version and leaves its exit code in
/// a file beside the download. Everything that builds a command line is a pure function, so the quoting can be tested
/// without pkexec or a display.
/// </summary>
public static class LinuxUpdate
{
    /// <summary>The launcher the .deb installs, and what starts the game again after a .deb update.</summary>
    public const string DebLauncher = "/usr/bin/deskarcade";

    /// <summary>The name the wrapper leaves the install's exit code under, next to the download.</summary>
    public const string StatusFileName = "install.status";

    /// <summary>The terminals tried, in order, when there is no pkexec: sudo asks for the password in the window.</summary>
    public static readonly string[] Terminals = { "x-terminal-emulator", "gnome-terminal", "konsole" };

    /// <summary>Where the .deb puts its files. Matched from the root, so an AppImage's copy under its own mount does not count.</summary>
    static readonly string[] DebFolders = { "/opt/deskarcade/", "/usr/lib/deskarcade/" };

    const UnixFileMode Executable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    // ------------------------------------------------------------------ what this copy is

    /// <summary>
    /// Works out how this copy was installed from the facts alone: the .deb when the launch path is its launcher or the
    /// process runs from its folder, the AppImage when $APPIMAGE names a file that exists, a Flatpak when the sandbox
    /// marks are there, otherwise a manual copy.
    /// </summary>
    public static InstallKind Detect(string launchPath, string? processPath, string? appImage, bool appImageExists, bool flatpakInfoExists, string? flatpakId)
    {
        if (launchPath == DebLauncher || (processPath != null && Array.Exists(DebFolders, f => processPath.StartsWith(f, StringComparison.Ordinal))))
            return InstallKind.Deb;
        if (!string.IsNullOrEmpty(appImage) && appImageExists) return InstallKind.AppImage;
        if (flatpakInfoExists || !string.IsNullOrEmpty(flatpakId)) return InstallKind.Flatpak;
        return InstallKind.Manual;
    }

    // ------------------------------------------------------------------ command lines

    /// <summary>Single-quotes text for POSIX sh; a quote inside becomes '\'' (close, one escaped quote, open again).</summary>
    public static string ShellQuote(string text) => "'" + text.Replace("'", "'\\''") + "'";

    /// <summary>
    /// What runs as root: apt-get resolves dependencies and accepts a downgrade, dpkg is the fallback where apt-get is
    /// missing. Debconf is told not to ask, since nothing could answer it.
    /// </summary>
    public static string InstallCommand(string debPath) =>
        $"export DEBIAN_FRONTEND=noninteractive; apt-get install -y --allow-downgrades {ShellQuote(debPath)} || dpkg -i {ShellQuote(debPath)}";

    /// <summary>The privileged step through PolicyKit. pkexec exits 126 when the prompt is dismissed and 127 when not authorized.</summary>
    public static string PkexecCommand(string debPath) => $"pkexec sh -c {ShellQuote(InstallCommand(debPath))}";

    /// <summary>The same step through sudo, for a terminal window where it can ask for the password.</summary>
    public static string SudoCommand(string debPath) => $"sudo sh -c {ShellQuote(InstallCommand(debPath))}";

    /// <summary>
    /// A shell fragment that starts the game again once the process <paramref name="pid"/> is gone (it waits up to ten
    /// seconds, then a moment more so the single-instance lock is free), detached through setsid where there is one.
    /// The profile goes with it, so a --profile copy comes back as the same profile.
    /// </summary>
    public static string RelaunchCommand(string launchPath, string profile, int pid)
    {
        string start = ShellQuote(launchPath) + (profile.Length > 0 ? " --profile " + ShellQuote(profile) : "") + " >/dev/null 2>&1 </dev/null &";
        return $"i=0; while kill -0 {pid} 2>/dev/null && [ $i -lt 50 ]; do sleep 0.2; i=$((i+1)); done; sleep 1; "
            + $"if command -v setsid >/dev/null 2>&1; then setsid {start} else {start} fi";
    }

    /// <summary>
    /// The user-level wrapper around the privileged step. It leaves the exit code in <paramref name="statusFile"/>, and on
    /// success removes the download and runs <paramref name="relaunch"/>. With a <paramref name="holdMessage"/> (a terminal
    /// window) it shows that and waits for Enter after a failure, so the error above it can be read.
    /// </summary>
    public static string WrapperScript(string privileged, string debPath, string statusFile, string relaunch, string? holdMessage)
    {
        string onFailure = holdMessage == null ? "" : $"else\n  printf '\\n%s\\n' {ShellQuote(holdMessage)}; read _\n";
        return $"{privileged}\ncode=$?\nprintf '%s\\n' \"$code\" > {ShellQuote(statusFile)}\n"
            + $"if [ \"$code\" -eq 0 ]; then\n  rm -f {ShellQuote(debPath)}\n  {relaunch}\n{onFailure}fi\nexit \"$code\"\n";
    }

    /// <summary>How a terminal takes a program to run: gnome-terminal wants "--", the others "-e".</summary>
    public static string[] TerminalArguments(string terminal, string program) =>
        Path.GetFileName(terminal) == "gnome-terminal" ? new[] { "--", program } : new[] { "-e", program };

    /// <summary>The first folder on <paramref name="path"/> (PATH's colon-separated form) holding <paramref name="name"/>, joined with it; null when none does.</summary>
    public static string? FindOnPath(string name, string? path, Func<string, bool> exists)
    {
        foreach (string dir in (path ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = dir.TrimEnd('/') + "/" + name; // Linux paths, whatever OS the tests run on
            if (exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>A path for a notice: the home folder shortened to "~".</summary>
    public static string Tidy(string path, string? home) =>
        !string.IsNullOrEmpty(home) && path.StartsWith(home, StringComparison.Ordinal) && (path.Length == home.Length || path[home.Length] == '/')
            ? "~" + path[home.Length..]
            : path;

    public static string Tidy(string path) => Tidy(path, Environment.GetEnvironmentVariable("HOME"));

    // ------------------------------------------------------------------ .deb

    public enum Outcome { Installed, Dismissed, Failed }

    /// <summary>0 installed it; 126 and 127 mean the prompt was dismissed or refused (or there is no pkexec), so the download stays for a manual install.</summary>
    public static Outcome Classify(int exitCode) => exitCode switch
    {
        0 => Outcome.Installed,
        126 or 127 => Outcome.Dismissed,
        _ => Outcome.Failed,
    };

    /// <summary>A .deb install under way: what asks for the password, and the exit code once known (null when a terminal never reported one).</summary>
    public sealed record DebInstall(string Runner, bool ViaTerminal, Task<int?> ExitCode);

    /// <summary>
    /// Starts installing <paramref name="debPath"/>: through pkexec when there is one, otherwise with sudo in the first terminal
    /// found; null when there is neither. The wrapper script goes beside the package. A terminal does not pass exit codes on,
    /// so that path watches the status file the wrapper writes, for up to fifteen minutes.
    /// </summary>
    public static DebInstall? StartDebInstall(string debPath, string profile, string? holdMessage, string? path = null)
    {
        path ??= Environment.GetEnvironmentVariable("PATH");
        string dir = Path.GetDirectoryName(debPath) ?? ".";
        string status = Path.Combine(dir, StatusFileName), script = Path.Combine(dir, "install.sh");
        string relaunch = RelaunchCommand(DebLauncher, profile, Environment.ProcessId);
        try
        {
            File.Delete(status);
            if (FindOnPath("pkexec", path, File.Exists) != null)
            {
                WriteScript(script, WrapperScript(PkexecCommand(debPath), debPath, status, relaunch, null));
                var psi = new ProcessStartInfo("/bin/sh") { ArgumentList = { script }, UseShellExecute = false };
                if (path != null) psi.Environment["PATH"] = path; // the wrapper finds pkexec where this did
                return Process.Start(psi) is { } process ? new DebInstall("pkexec", false, ExitCodeAsync(process)) : null;
            }
            foreach (string terminal in Terminals)
            {
                if (FindOnPath(terminal, path, File.Exists) is not { } exe) continue;
                WriteScript(script, WrapperScript(SudoCommand(debPath), debPath, status, relaunch, holdMessage));
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
                if (path != null) psi.Environment["PATH"] = path;
                foreach (string a in TerminalArguments(terminal, script)) psi.ArgumentList.Add(a);
                if (Process.Start(psi) is { } process) process.Dispose();
                return new DebInstall(terminal, true, WatchStatusAsync(status, TimeSpan.FromMinutes(15)));
            }
        }
        catch
        {
            // no shell, or the folder went away: the caller shows where the download is
        }
        return null;
    }

    static void WriteScript(string path, string script)
    {
        File.WriteAllText(path, "#!/bin/sh\n" + script);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, Executable);
    }

    static async Task<int?> ExitCodeAsync(Process process)
    {
        using (process)
        {
            await process.WaitForExitAsync();
            return process.ExitCode;
        }
    }

    static async Task<int?> WatchStatusAsync(string statusFile, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (ReadStatus(statusFile) is int code) return code;
            await Task.Delay(500);
        }
        return null;
    }

    static int? ReadStatus(string file)
    {
        try
        {
            return File.Exists(file) && int.TryParse(File.ReadAllText(file).Trim(), out int code) ? code : null;
        }
        catch
        {
            return null; // being written right now
        }
    }

    // ------------------------------------------------------------------ AppImage

    /// <summary>Where a downloaded AppImage ended up: in place of the running file when <see cref="Replaced"/>, otherwise kept under its own name.</summary>
    public sealed record AppImageResult(bool Replaced, string Path);

    /// <summary>
    /// Puts the download in place of the running AppImage: staged beside it as "….new", made executable, then renamed over
    /// it, which is atomic and leaves the running process its mount. When that fails (a read-only folder, an immutable file)
    /// the download is kept beside the old file under its versioned name where possible, else where it was downloaded,
    /// executable either way.
    /// </summary>
    public static AppImageResult InstallAppImage(string downloaded, string running)
    {
        string staged = running + ".new";
        try
        {
            File.Copy(downloaded, staged, overwrite: true);
            MakeExecutable(staged);
            File.Move(staged, running, overwrite: true);
            File.Delete(downloaded);
            return new AppImageResult(true, running);
        }
        catch
        {
            try { File.Delete(staged); } catch { /* never written */ }
        }
        string beside = Path.Combine(Path.GetDirectoryName(running) ?? ".", Path.GetFileName(downloaded));
        if (beside != running)
        {
            try
            {
                File.Copy(downloaded, beside, overwrite: true);
                MakeExecutable(beside);
                File.Delete(downloaded);
                return new AppImageResult(false, beside);
            }
            catch
            {
                try { File.Delete(beside); } catch { /* never written */ }
            }
        }
        try { MakeExecutable(downloaded); } catch { /* still a valid download */ }
        return new AppImageResult(false, downloaded);
    }

    /// <summary>Runs <see cref="RelaunchCommand"/> in a shell of its own, which starts <paramref name="launchPath"/> once this process is gone.</summary>
    public static bool Relaunch(string launchPath, string profile)
    {
        try
        {
            var psi = new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", RelaunchCommand(launchPath, profile, Environment.ProcessId) }, UseShellExecute = false };
            Process.Start(psi)?.Dispose();
            return true;
        }
        catch
        {
            return false;
        }
    }

    static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, Executable);
    }
}
