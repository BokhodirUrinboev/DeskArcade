using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using DeskArcade.Engine;

namespace DeskArcade.Platform;

public enum HotkeyAction { ToggleOverlay, NextGame, Summon }

/// <summary>
/// A top-level application window, in screen pixels (points on macOS, like Avalonia's screen coordinates there), and the
/// id of the process that owns it (0 when the platform cannot tell), so the pet can remember an app by name.
/// </summary>
public readonly record struct NativeWindowInfo(IntPtr Id, PixelRect Bounds, int ProcessId = 0);

public interface IAudioOutput : IDisposable
{
    /// <summary>True when the device can take another buffer right now.</summary>
    bool CanWrite { get; }

    /// <summary>Queue 16-bit mono samples. May block until the device has room.</summary>
    void Write(short[] samples);
}

/// <summary>Everything the overlay needs from the operating system.</summary>
public interface IDesktopPlatform : IDisposable
{
    string Name { get; }

    /// <summary>Make the window a focus-less, click-through overlay. Call after it has a native handle.</summary>
    void AttachOverlay(Window overlay);

    /// <summary>
    /// True when the overlay should cover the monitor's working area instead of the whole monitor.
    /// The games only ever use the working area, so nothing is lost; some window managers refuse to keep
    /// a window that is larger than the working area on the monitor it asked for.
    /// </summary>
    bool OverlayFitsWorkArea { get; }

    /// <summary>
    /// Areas (overlay DIPs) that take mouse input; the rest passes through.
    /// While <paramref name="captureActive"/> the whole overlay takes input (a drag is in progress).
    /// </summary>
    void SetInputRegions(IReadOnlyList<HitShape> regions, bool captureActive);

    /// <summary>Global mouse position in screen pixels (points on macOS), when the platform can report it.</summary>
    bool TryGetCursor(out PixelPoint screenPoint);

    /// <summary>Visible application windows other than ours, topmost first.</summary>
    void EnumerateWindows(List<NativeWindowInfo> result);

    /// <summary>
    /// Registers <paramref name="keys"/> (Ctrl+Alt+G / N / B by default; Control+Option on macOS). Returns false if
    /// global hotkeys are unavailable or taken. Windows re-registers when called again; the other platforms keep the
    /// first set until the next start (see <see cref="HotkeysApplyLive"/>).
    /// </summary>
    bool RegisterHotkeys(Action<HotkeyAction> onHotkey, HotkeySet keys);

    /// <summary>Whether a second <see cref="RegisterHotkeys"/> call takes effect straight away.</summary>
    bool HotkeysApplyLive => false;

    /// <summary>
    /// Whether this desktop can show our tray icon. Linux asks the session bus (the tray is a StatusNotifierItem,
    /// which needs a watcher that stock GNOME lacks); everywhere else the tray is a given. While false, the
    /// scoreboard's ☰ menu is the only menu.
    /// </summary>
    bool HasTray => true;

    IAudioOutput? OpenAudio(int sampleRate);

    bool AutostartEnabled { get; set; }

    /// <summary>
    /// Seconds since the last keyboard or mouse input anywhere on the desktop, for the breaks that count time at the
    /// computer; null when the platform cannot tell (the overlay then watches the cursor instead).
    /// </summary>
    double? IdleSeconds() => null;

    /// <summary>
    /// True while a full-screen window (a slide show, a video, a game, a browser in full screen) fills the monitor
    /// given in screen pixels (points on macOS), or the system says a presentation is on. The overlay steps aside then.
    /// </summary>
    bool IsFullScreenOn(PixelRect monitor) => false;

    /// <summary>
    /// The id of the process that owns the window in front (the foreground or active window, the frontmost app), for
    /// "where the day went"; null when there is none or the platform cannot tell. Only the process is looked at,
    /// never a window title.
    /// </summary>
    int? ForegroundProcessId() => null;
}

public static class DesktopPlatform
{
    public static IDesktopPlatform Create()
    {
        if (OperatingSystem.IsWindows()) return new Windows.WindowsPlatform();
        if (OperatingSystem.IsLinux()) return new Linux.X11Platform();
        if (OperatingSystem.IsMacOS()) return new Mac.MacPlatform();
        return new NullPlatform();
    }
}

/// <summary>Fallback that simply does nothing (the overlay still renders).</summary>
public sealed class NullPlatform : IDesktopPlatform
{
    public string Name => "none";
    public void AttachOverlay(Window overlay) { }
    public bool OverlayFitsWorkArea => false;
    public void SetInputRegions(IReadOnlyList<HitShape> regions, bool captureActive) { }
    public bool TryGetCursor(out PixelPoint screenPoint) { screenPoint = default; return false; }
    public void EnumerateWindows(List<NativeWindowInfo> result) { }
    public bool RegisterHotkeys(Action<HotkeyAction> onHotkey, HotkeySet keys) => false;
    public IAudioOutput? OpenAudio(int sampleRate) => null;
    public bool AutostartEnabled { get => false; set { } }
    public int? ForegroundProcessId() => null;
    public void Dispose() { }
}
