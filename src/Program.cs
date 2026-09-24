using System;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;

namespace DeskArcade;

public static class Program
{
    const string MutexBaseName = "DeskArcade.SingleInstance.v1";

    public static string[] Args { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// "--profile NAME" runs an isolated copy (own single-instance lock, signal channel and settings),
    /// e.g. for testing a new build while the installed game keeps running. Empty = the normal game.
    /// </summary>
    public static string Profile { get; private set; } = "";

    /// <summary>
    /// The command that starts Desk Arcade again, for autostart and the Claude Code hooks: the AppImage file
    /// itself (not its temporary /tmp/.mount_… directory, which is gone after exit), the installed launcher,
    /// or this executable.
    /// </summary>
    public static string LaunchPath =>
        Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } appImage && File.Exists(appImage) ? appImage
        : OperatingSystem.IsLinux() && File.Exists("/usr/bin/deskarcade") ? "/usr/bin/deskarcade"
        : Environment.ProcessPath ?? "DeskArcade";

    public static string InstanceSuffix => Profile.Length == 0 ? "" : "." + Profile;

    [STAThread]
    public static int Main(string[] args)
    {
        // everything after --while belongs to the command, so only the options before it count
        int run = Array.FindIndex(args, a => a.Equals("--while", StringComparison.OrdinalIgnoreCase));
        string[] command = run >= 0 ? args[(run + 1)..] : Array.Empty<string>();
        if (run >= 0) args = args[..run];

        int pi = Array.FindIndex(args, a => a.Equals("--profile", StringComparison.OrdinalIgnoreCase));
        if (pi >= 0 && pi + 1 < args.Length)
            Profile = new string(args[pi + 1].Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').Take(32).ToArray());

        // Hook / CLI mode: forward a message to the running overlay and exit fast (no UI toolkit started).
        //   DeskArcade --signal working|done|attention|show|hide|toggle|next|summon|expand|stats|quit
        int sig = Array.FindIndex(args, a => a.Equals("--signal", StringComparison.OrdinalIgnoreCase));
        if (sig >= 0)
        {
            Ipc.Send(sig + 1 < args.Length ? args[sig + 1] : "show");
            return 0; // never fail a hook just because the game is closed
        }

        //   DeskArcade --while <command> [arguments...]   runs the command and shows it on the scoreboard
        if (run >= 0) return TaskRunner.Run(command);

        using var mutex = new Mutex(true, MutexBaseName + InstanceSuffix, out bool isFirst);
        if (!isFirst)
        {
            Ipc.Send("show");
            return 0;
        }

        Args = args;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // popups (the scoreboard's ☰ menu, tooltips) are drawn inside the overlay instead of as windows of their
            // own, which would fight the click-through shape and, on X11, the focus rules
            .With(new X11PlatformOptions { OverlayPopups = true })
            .With(new Win32PlatformOptions { OverlayPopups = true })
            .With(new AvaloniaNativePlatformOptions { OverlayPopups = true })
            .LogToTrace();

    public static void LogCrash(object error)
    {
        try
        {
            Directory.CreateDirectory(Settings.DataDirectory);
            File.AppendAllText(Path.Combine(Settings.DataDirectory, "crash.log"),
                $"[{DateTime.Now:O}] {error}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* nothing sensible left to do */ }
    }
}
