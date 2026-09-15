using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Simple;
using Avalonia.Threading;

namespace DeskArcade;

public sealed class App : Application
{
    OverlayWindow? _overlay;

    public override void Initialize() => Styles.Add(new SimpleTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime)
        {
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                Program.LogCrash(e.Exception);
                e.Handled = true; // a glitch in one frame should not kill the overlay
            };
            _overlay = new OverlayWindow(Program.Args);
            _overlay.Start();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
