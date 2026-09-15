using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using DeskArcade.Engine;
using Microsoft.Win32;

namespace DeskArcade.Platform.Windows;

/// <summary>
/// Windows overlay plumbing. The window is layered (alpha 255) so the compositor keeps its
/// per-pixel transparency, and WS_EX_TRANSPARENT is switched off only while the cursor is over
/// something interactive, so every other click reaches the windows underneath.
/// </summary>
public sealed class WindowsPlatform : IDesktopPlatform
{
    const int OverlayExStyle = Win32.WS_EX_LAYERED | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "DeskArcade";

    static readonly HashSet<string> IgnoredClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow", "ApplicationFrameWindow_Hidden", "XamlExplorerHostIslandWindow",
    };

    readonly uint _selfPid = (uint)Environment.ProcessId;
    Window? _overlay;
    IntPtr _hwnd;
    DispatcherTimer? _hitTimer;
    HitShape[] _regions = Array.Empty<HitShape>();
    bool _capture;
    bool _clickThrough = true;

    Win32.WndProc? _wndProc; // must stay referenced while the message window exists
    IntPtr _msgHwnd;
    Action<HotkeyAction>? _onHotkey;

    public string Name => "windows";

    public void AttachOverlay(Window overlay)
    {
        _overlay = overlay;
        _hwnd = overlay.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (_hwnd == IntPtr.Zero) return;
        _clickThrough = true;
        ApplyStyles();
        _hitTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(25), DispatcherPriority.Input, (_, _) => UpdateClickThrough());
        _hitTimer.Start();
    }

    /// <summary>Cheap when nothing changed; also repairs styles if the toolkit rewrote them.</summary>
    void ApplyStyles()
    {
        int ex = Win32.GetWindowLong(_hwnd, Win32.GWL_EXSTYLE);
        int want = ex | OverlayExStyle;
        want = _clickThrough ? want | Win32.WS_EX_TRANSPARENT : want & ~Win32.WS_EX_TRANSPARENT;
        if (want == ex) return;
        bool addingLayered = (ex & Win32.WS_EX_LAYERED) == 0;
        Win32.SetWindowLong(_hwnd, Win32.GWL_EXSTYLE, want);
        if (addingLayered) Win32.SetLayeredWindowAttributes(_hwnd, 0, 255, Win32.LWA_ALPHA);
    }

    void UpdateClickThrough()
    {
        if (_overlay is not { IsVisible: true } || _hwnd == IntPtr.Zero) return;
        bool interactive = _capture;
        if (!interactive && Win32.GetCursorPos(out var pt))
        {
            Vec2 p = _overlay.PointToClient(new PixelPoint(pt.X, pt.Y));
            foreach (var region in _regions)
            {
                if (region.Contains(p))
                {
                    interactive = true;
                    break;
                }
            }
        }
        _clickThrough = !interactive;
        ApplyStyles();
    }

    public bool OverlayFitsWorkArea => false;

    public void SetInputRegions(IReadOnlyList<HitShape> regions, bool captureActive)
    {
        _regions = [.. regions];
        _capture = captureActive;
        if (captureActive) UpdateClickThrough();
    }

    public bool TryGetCursor(out PixelPoint screenPoint)
    {
        bool ok = Win32.GetCursorPos(out var pt);
        screenPoint = new PixelPoint(pt.X, pt.Y);
        return ok;
    }

    public void EnumerateWindows(List<NativeWindowInfo> result)
    {
        Win32.EnumWindows((h, _) =>
        {
            if (!Win32.IsWindowVisible(h) || Win32.IsIconic(h)) return true;
            Win32.GetWindowThreadProcessId(h, out uint pid);
            if (pid == _selfPid) return true;
            int ex = Win32.GetWindowLong(h, Win32.GWL_EXSTYLE);
            if ((ex & Win32.WS_EX_TOOLWINDOW) != 0 && (ex & Win32.WS_EX_APPWINDOW) == 0) return true;
            if ((ex & Win32.WS_EX_TRANSPARENT) != 0) return true;
            if (Win32.GetWindowTextLength(h) == 0 || Win32.IsCloaked(h)) return true;
            if (IgnoredClasses.Contains(Win32.ClassName(h))) return true;
            if (!Win32.TryGetFrameBounds(h, out var rc)) return true;
            if (rc.Right - rc.Left < 80 || rc.Bottom - rc.Top < 40) return true;
            result.Add(new NativeWindowInfo(h, new PixelRect(rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top)));
            return true;
        }, IntPtr.Zero);
    }

    public bool RegisterHotkeys(Action<HotkeyAction> onHotkey)
    {
        _onHotkey = onHotkey;
        if (_msgHwnd == IntPtr.Zero)
        {
            // A message-only window on the UI thread: the toolkit's message loop dispatches WM_HOTKEY to it.
            _wndProc = HotkeyWndProc;
            var wc = new Win32.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<Win32.WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = Win32.GetModuleHandle(null),
                lpszClassName = "DeskArcade.Hotkeys",
            };
            Win32.RegisterClassEx(ref wc);
            _msgHwnd = Win32.CreateWindowEx(0, wc.lpszClassName, "", 0, 0, 0, 0, 0, Win32.HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            if (_msgHwnd == IntPtr.Zero) return false;
        }
        const uint mods = Win32.MOD_CONTROL | Win32.MOD_ALT | Win32.MOD_NOREPEAT;
        bool ok = Win32.RegisterHotKey(_msgHwnd, 1, mods, 0x47); // G
        ok &= Win32.RegisterHotKey(_msgHwnd, 2, mods, 0x4E);     // N
        ok &= Win32.RegisterHotKey(_msgHwnd, 3, mods, 0x42);     // B
        return ok;
    }

    IntPtr HotkeyWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Win32.WM_HOTKEY)
        {
            _onHotkey?.Invoke(wParam.ToInt32() switch
            {
                1 => HotkeyAction.ToggleOverlay,
                2 => HotkeyAction.NextGame,
                _ => HotkeyAction.Summon,
            });
            return IntPtr.Zero;
        }
        return Win32.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public IAudioOutput? OpenAudio(int sampleRate) => WaveOutAudio.TryOpen(sampleRate);

    public bool AutostartEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue(RunValue, false);
        }
    }

    public void Dispose()
    {
        _hitTimer?.Stop();
        if (_msgHwnd != IntPtr.Zero)
        {
            for (int id = 1; id <= 3; id++) Win32.UnregisterHotKey(_msgHwnd, id);
            Win32.DestroyWindow(_msgHwnd);
            _msgHwnd = IntPtr.Zero;
        }
    }
}
