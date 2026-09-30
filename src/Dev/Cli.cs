using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DeskArcade.Dev;

/// <summary>What a command-line tool said: its exit code and its output (null when it could not be started or timed out).</summary>
public sealed record CliResult(int? ExitCode, string Output, string Error)
{
    public bool Ok => ExitCode == 0;

    public static readonly CliResult NotRun = new(null, "", "");
}

/// <summary>
/// Runs the developer tools Desk Arcade reads from (git, gh, glab) without a window, in a folder, with a time limit,
/// and collects what they print. They run with the user's own sign-in (gh auth, glab auth, git credentials), so Desk
/// Arcade never holds a token. Nothing here runs unless a feature that needs it is turned on.
/// </summary>
public static class Cli
{
    /// <summary>The longest output kept from one run; the rest is dropped.</summary>
    const int MaxOutput = 4 * 1024 * 1024;

    /// <summary>Tests swap this out to answer with recorded output instead of running anything.</summary>
    public static Func<string, IReadOnlyList<string>, string?, TimeSpan, CancellationToken, Task<CliResult>> Runner { get; set; } = RunProcessAsync;

    /// <summary>Runs <paramref name="tool"/> with <paramref name="args"/> in <paramref name="folder"/> (or the current folder).</summary>
    public static Task<CliResult> RunAsync(string tool, IReadOnlyList<string> args, string? folder = null, TimeSpan? timeout = null, CancellationToken ct = default) =>
        Runner(tool, args, folder, timeout ?? TimeSpan.FromSeconds(20), ct);

    static readonly Dictionary<string, bool> Found = new();

    /// <summary>Whether <paramref name="tool"/> is on the PATH (cached; a tool installed later is seen after a restart).</summary>
    public static bool Has(string tool)
    {
        lock (Found)
        {
            if (Found.TryGetValue(tool, out bool known)) return known;
            bool found = Locate(tool) != null;
            Found[tool] = found;
            return found;
        }
    }

    /// <summary>The full path of <paramref name="tool"/> on the PATH, trying Windows' executable extensions.</summary>
    public static string? Locate(string tool)
    {
        if (Path.IsPathRooted(tool)) return File.Exists(tool) ? tool : null;
        string[] extensions = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat", "" } : new[] { "" };
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string ext in extensions)
            {
                try
                {
                    string candidate = Path.Combine(dir.Trim('"'), tool + ext);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException)
                {
                    // a malformed PATH entry
                }
            }
        }
        return null;
    }

    static async Task<CliResult> RunProcessAsync(string tool, IReadOnlyList<string> args, string? folder, TimeSpan timeout, CancellationToken ct)
    {
        string? exe = Locate(tool);
        if (exe == null) return CliResult.NotRun;
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (folder != null) psi.WorkingDirectory = folder;
        foreach (string a in args) psi.ArgumentList.Add(a);
        // never wait on a prompt: no pager, no colour codes, no credential or editor questions
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_PAGER"] = "cat";
        psi.Environment["PAGER"] = "cat";
        psi.Environment["GH_PROMPT_DISABLED"] = "1";
        psi.Environment["GH_NO_UPDATE_NOTIFIER"] = "1";
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment["GLAB_NO_PROMPT"] = "1";

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException();
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            return CliResult.NotRun;
        }
        using (process)
        {
            process.StandardInput.Close();
            var output = ReadCapped(process.StandardOutput);
            var error = ReadCapped(process.StandardError);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception) { /* already gone */ }
                return new CliResult(null, "", "timed out");
            }
            return new CliResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
    }

    static async Task<string> ReadCapped(StreamReader reader)
    {
        var sb = new StringBuilder();
        var buffer = new char[8192];
        int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            if (sb.Length < MaxOutput) sb.Append(buffer, 0, Math.Min(read, MaxOutput - sb.Length));
        return sb.ToString();
    }
}
