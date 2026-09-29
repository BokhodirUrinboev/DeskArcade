using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using DeskArcade.Office;

namespace DeskArcade;

/// <summary>
/// At work from a terminal: hands a timer, a note, a focus block or something to wait for to the running overlay and
/// returns at once, so the terminal is free again. Desk Arcade is started first when it is not running.
/// <code>
/// deskarcade --timer 10m Tea          a timer, with a label
/// deskarcade --note Call the bank     a sticky note
/// deskarcade --focus [50]             a focus block (the usual length without a number)
/// deskarcade --wait-pid 4242          a chime when that program ends
/// deskarcade --wait-file big.iso      ... when that file is there and stops growing
/// deskarcade --wait-url http://localhost:8080/health   ... when that address answers
/// </code>
/// </summary>
public static class OfficeCli
{
    static readonly string[] Flags = { "--timer", "--note", "--focus", "--wait-pid", "--wait-file", "--wait-url" };

    /// <summary>The message for the overlay, or an error to print; null when the arguments hold none of these options.</summary>
    public static (string? Message, string? Error)? Parse(string[] args)
    {
        int at = Array.FindIndex(args, a => Flags.Contains(a, StringComparer.OrdinalIgnoreCase));
        if (at < 0) return null;
        string flag = args[at].ToLowerInvariant();
        string[] rest = args[(at + 1)..].TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        string text = string.Join(' ', rest).Trim();
        switch (flag)
        {
            case "--timer":
                if (rest.Length == 0 || Durations.Parse(rest[0]) is not TimeSpan length) return (null, "usage: deskarcade --timer <10m | 1h30m | 90s> [label]");
                string label = string.Join(' ', rest[1..]).Trim();
                return (string.Create(CultureInfo.InvariantCulture, $"timer:{(int)length.TotalSeconds}:{label}"), null);
            case "--note":
                return text.Length == 0 ? (null, "usage: deskarcade --note <text>") : ("note:" + text.Replace('\n', ' '), null);
            case "--focus":
                if (rest.Length == 0) return ("focus", null);
                return int.TryParse(rest[0], NumberStyles.None, CultureInfo.InvariantCulture, out int minutes) && minutes is > 0 and <= 240
                    ? (string.Create(CultureInfo.InvariantCulture, $"focus:{minutes}"), null)
                    : (null, "usage: deskarcade --focus [minutes]");
            case "--wait-pid":
                return rest.Length == 1 && int.TryParse(rest[0], NumberStyles.None, CultureInfo.InvariantCulture, out int pid) && pid > 0
                    ? (string.Create(CultureInfo.InvariantCulture, $"wait-pid:{pid}"), null)
                    : (null, "usage: deskarcade --wait-pid <process id>");
            case "--wait-file":
                if (text.Length == 0) return (null, "usage: deskarcade --wait-file <path>");
                try
                {
                    string path = text.StartsWith('~') ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), text.TrimStart('~', '/', '\\')) : text;
                    return ("wait-file:" + Path.GetFullPath(path), null);
                }
                catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    return (null, "that is not a path: " + text);
                }
            default: // --wait-url
                return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
                    ? ("wait-url:" + uri, null)
                    : (null, "usage: deskarcade --wait-url <http://host:port/path>");
        }
    }

    /// <summary>Runs one of the options, when <paramref name="args"/> holds one; false otherwise.</summary>
    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (Parse(args) is not var (message, error)) return false;
        if (OperatingSystem.IsWindows()) AttachConsole(AttachParentProcess);
        if (message == null)
        {
            Console.Error.WriteLine(error);
            exitCode = 2;
            return true;
        }
        if (Ipc.Send(message)) return true;
        if (!StartOverlay())
        {
            Console.Error.WriteLine("DeskArcade: could not start Desk Arcade");
            exitCode = 1;
            return true;
        }
        // the new copy needs a moment before it listens
        for (int i = 0; i < 50; i++)
        {
            Thread.Sleep(300);
            if (Ipc.Send(message)) return true;
        }
        Console.Error.WriteLine("DeskArcade: Desk Arcade did not answer");
        exitCode = 1;
        return true;
    }

    static bool StartOverlay()
    {
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath ?? Program.LaunchPath) { UseShellExecute = false };
            if (Program.Profile.Length > 0)
            {
                psi.ArgumentList.Add("--profile");
                psi.ArgumentList.Add(Program.Profile);
            }
            return Process.Start(psi) != null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    const int AttachParentProcess = -1;

    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int processId);
}
