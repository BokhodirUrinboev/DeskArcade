using System;

namespace DeskArcade;

/// <summary>How the global shortcuts are written on this OS: Control+Option on macOS, Ctrl+Alt elsewhere.</summary>
public static class Shortcuts
{
    public static string Label(char key) =>
        (OperatingSystem.IsMacOS() ? "Ctrl+Option+" : "Ctrl+Alt+") + char.ToUpperInvariant(key);
}
