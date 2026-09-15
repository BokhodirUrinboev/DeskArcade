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

    public static string InstanceSuffix => Profile.Length == 0 ? "" : "." + Profile;

    [STAThread]
    public static int Main(string[] args)
    {
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
