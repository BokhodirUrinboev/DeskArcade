using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using DeskArcade.Engine;

namespace DeskArcade.Platform;

public enum HotkeyAction { ToggleOverlay, NextGame, Summon }

/// <summary>A top-level application window, in screen pixels (points on macOS, like Avalonia's screen coordinates there).</summary>
public readonly record struct NativeWindowInfo(IntPtr Id, PixelRect Bounds);

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

    /// <summary>Ctrl+Alt+G / N / B (Control+Option on macOS). Returns false if global hotkeys are unavailable.</summary>
    bool RegisterHotkeys(Action<HotkeyAction> onHotkey);

    IAudioOutput? OpenAudio(int sampleRate);

    bool AutostartEnabled { get; set; }
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
    public bool RegisterHotkeys(Action<HotkeyAction> onHotkey) => false;
    public IAudioOutput? OpenAudio(int sampleRate) => null;
    public bool AutostartEnabled { get => false; set { } }
    public void Dispose() { }
}
