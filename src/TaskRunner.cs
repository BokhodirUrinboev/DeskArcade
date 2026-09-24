using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace DeskArcade;

/// <summary>
/// "DeskArcade --while dotnet test": runs a command in the terminal it was typed in and tells the overlay
/// when it starts and how it ended, so the scoreboard shows it running and chimes when it passes or fails.
/// The command's output and exit code pass straight through, so it can stand in for the command in scripts.
/// </summary>
public static class TaskRunner
{
    const int MaxLabel = 28;

    public static int Run(string[] command)
    {
        // Desk Arcade is a GUI program on Windows: borrow the terminal's console so the command writes to it
        if (OperatingSystem.IsWindows()) AttachConsole(AttachParentProcess);
        if (command.Length == 0)
        {
            Console.Error.WriteLine("usage: DeskArcade --while <command> [arguments...]");
            return 2;
        }

        bool windows = OperatingSystem.IsWindows();
        string line = ShellLine(command, windows);
        Ipc.Send("task:" + Label(line));

        int code;
        try
        {
            var psi = windows
                ? new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe") { Arguments = $"/d /s /c \"{line}\"" }
                : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", line } };
            psi.UseShellExecute = false;
            // Ctrl+C reaches the command as well: let it stop, then report how it ended
            Console.CancelKeyPress += (_, e) => e.Cancel = true;
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("the shell did not start");
            process.WaitForExit();
            code = process.ExitCode;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"DeskArcade: could not run the command: {e.Message}");
            code = 127;
        }

        Ipc.Send("task-end:" + code);
        return code;
    }

    /// <summary>
    /// The shell line to run. One argument is a line of its own ("npm test &amp;&amp; npm run build") and runs as
    /// typed; several are quoted back together so an argument with spaces stays one argument.
    /// </summary>
    public static string ShellLine(string[] args, bool windows)
    {
        if (args.Length == 1) return args[0];
        return string.Join(' ', args.Select(a => windows ? QuoteWindows(a) : QuotePosix(a)));
    }

    /// <summary>What the scoreboard calls the command: the line itself, on one line and cut to fit.</summary>
    public static string Label(string line)
    {
        var sb = new StringBuilder();
        foreach (char c in line.Trim())
            sb.Append(char.IsControl(c) ? ' ' : c);
        string label = sb.ToString();
        return label.Length <= MaxLabel ? label : label[..(MaxLabel - 1)].TrimEnd() + "…";
    }

    static string QuoteWindows(string arg)
    {
        if (arg.Length > 0 && !arg.Any(c => c is ' ' or '\t' or '"')) return arg;
        var sb = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\')
            {
                slashes++;
                continue;
            }
            // backslashes before a quote are doubled, and the quote itself escaped (CommandLineToArgvW rules)
            sb.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            slashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', slashes * 2).Append('"');
        return sb.ToString();
    }

    static string QuotePosix(string arg) =>
        arg.Length > 0 && arg.All(c => char.IsAsciiLetterOrDigit(c) || "-_./=:,+@%".Contains(c))
            ? arg
            : "'" + arg.Replace("'", "'\\''") + "'";

    const int AttachParentProcess = -1;

    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int processId);
}
