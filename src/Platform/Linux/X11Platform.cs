using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using DeskArcade.Engine;

namespace DeskArcade.Platform.Linux;

/// <summary>
/// Ubuntu / X11 overlay plumbing (also used under Wayland through XWayland).
/// Click-through uses the XShape *input* region: only interactive areas take the mouse.
/// Window tops come from _NET_CLIENT_LIST_STACKING; under Wayland only X11 apps are visible there.
/// </summary>
public sealed class X11Platform : IDesktopPlatform
{
    static readonly X11.XErrorHandler IgnoreErrors = (_, _) => 0; // a vanished window must not kill the process

    readonly IntPtr _dpy;
    readonly IntPtr _root;
    readonly long _selfPid = Environment.ProcessId;
    readonly IntPtr _atomClientList, _atomWmState, _atomHidden, _atomWmType, _atomTypeNormal, _atomTypeDialog;
    readonly IntPtr _atomFrameExtents, _atomGtkFrameExtents, _atomWmPid, _atomWmDesktop, _atomCurrentDesktop, _atomTakeFocus;

    Window? _overlay;
    IntPtr _win;
    X11.XRectangle[]? _lastShape;

    IntPtr _hkDpy;
    Thread? _hkThread;
    volatile bool _hkRunning;
    readonly int[] _keycodes = new int[3];
    Action<HotkeyAction>? _onHotkey;

    public X11Platform()
    {
        try
        {
            _dpy = X11.XOpenDisplay(IntPtr.Zero);
        }
        catch (DllNotFoundException)
        {
            _dpy = IntPtr.Zero;
        }
        if (_dpy == IntPtr.Zero) return;

        X11.XSetErrorHandler(IgnoreErrors);
        _root = X11.XDefaultRootWindow(_dpy);
        _atomClientList = Atom("_NET_CLIENT_LIST_STACKING");
        _atomWmState = Atom("_NET_WM_STATE");
        _atomHidden = Atom("_NET_WM_STATE_HIDDEN");
        _atomWmType = Atom("_NET_WM_WINDOW_TYPE");
        _atomTypeNormal = Atom("_NET_WM_WINDOW_TYPE_NORMAL");
        _atomTypeDialog = Atom("_NET_WM_WINDOW_TYPE_DIALOG");
        _atomFrameExtents = Atom("_NET_FRAME_EXTENTS");
        _atomGtkFrameExtents = Atom("_GTK_FRAME_EXTENTS");
        _atomWmPid = Atom("_NET_WM_PID");
        _atomWmDesktop = Atom("_NET_WM_DESKTOP");
        _atomCurrentDesktop = Atom("_NET_CURRENT_DESKTOP");
        _atomTakeFocus = Atom("WM_TAKE_FOCUS");
    }

    public string Name => _dpy == IntPtr.Zero ? "x11 (no display)" : "x11";

    IntPtr Atom(string name) => X11.XInternAtom(_dpy, name, false);

    // ------------------------------------------------------------------ overlay

    public void AttachOverlay(Window overlay)
    {
        _overlay = overlay;
        var handle = overlay.TryGetPlatformHandle();
        if (_dpy == IntPtr.Zero || handle == null) return;
        _win = handle.Handle;
        NeverTakeFocus();
        _lastShape = null;
        ApplyShape(Array.Empty<X11.XRectangle>()); // start fully click-through
    }

    /// <summary>Input hint off and no WM_TAKE_FOCUS: clicking the overlay leaves keyboard focus in your terminal.</summary>
    void NeverTakeFocus()
    {
        IntPtr hints = X11.XGetWMHints(_dpy, _win);
        if (hints == IntPtr.Zero) hints = X11.XAllocWMHints();
        if (hints != IntPtr.Zero)
        {
            Marshal.WriteInt64(hints, 0, Marshal.ReadInt64(hints, 0) | X11.InputHint);
            Marshal.WriteInt32(hints, 8, 0);
            X11.XSetWMHints(_dpy, _win, hints);
            X11.XFree(hints);
        }

        if (X11.XGetWMProtocols(_dpy, _win, out IntPtr protocols, out int count) != 0 && protocols != IntPtr.Zero)
        {
            var kept = new List<IntPtr>(count);
            for (int i = 0; i < count; i++)
            {
                IntPtr atom = Marshal.ReadIntPtr(protocols, i * IntPtr.Size);
                if (atom != _atomTakeFocus) kept.Add(atom);
            }
            X11.XFree(protocols);
            if (kept.Count != count) X11.XSetWMProtocols(_dpy, _win, kept.ToArray(), kept.Count);
        }
        X11.XFlush(_dpy);
    }

    public void SetInputRegions(IReadOnlyList<HitShape> regions, bool captureActive)
    {
        if (_win == IntPtr.Zero || _overlay == null) return;
        double s = _overlay.RenderScaling;
        var rects = new List<X11.XRectangle>();
        if (captureActive)
        {
            rects.Add(Rect(0, 0, _overlay.Bounds.Width * s, _overlay.Bounds.Height * s));
        }
        else
        {
            foreach (var region in regions)
            {
                var b = region.Bounds;
                if (!region.Round)
                {
                    rects.Add(Rect(b.X * s, b.Y * s, b.Width * s, b.Height * s));
                    continue;
                }
                // approximate the circle with horizontal slabs that fully cover it
                const int slabs = 4;
                double r = b.Width / 2, cx = b.Center.X, cy = b.Center.Y, h = b.Height / slabs;
                for (int k = 0; k < slabs; k++)
                {
                    double y0 = -r + k * h, y1 = y0 + h;
                    double nearest = y0 <= 0 && y1 >= 0 ? 0 : Math.Min(Math.Abs(y0), Math.Abs(y1));
                    double half = Math.Sqrt(Math.Max(0, r * r - nearest * nearest));
                    rects.Add(Rect((cx - half) * s, (cy + y0) * s, half * 2 * s, h * s));
                }
            }
        }
        ApplyShape(rects.ToArray());
    }

    static X11.XRectangle Rect(double x, double y, double w, double h) => new()
    {
        X = (short)Math.Clamp(Math.Floor(x), short.MinValue, short.MaxValue),
        Y = (short)Math.Clamp(Math.Floor(y), short.MinValue, short.MaxValue),
        Width = (ushort)Math.Clamp(Math.Ceiling(w) + 1, 0, ushort.MaxValue),
        Height = (ushort)Math.Clamp(Math.Ceiling(h) + 1, 0, ushort.MaxValue),
    };

    void ApplyShape(X11.XRectangle[] rects)
    {
        if (_lastShape != null && _lastShape.AsSpan().SequenceEqual(rects)) return;
        _lastShape = rects;
        X11.XShapeCombineRectangles(_dpy, _win, X11.ShapeInput, 0, 0, rects, rects.Length, X11.ShapeSet, X11.Unsorted);
        X11.XFlush(_dpy);
    }

    public bool TryGetCursor(out PixelPoint screenPoint)
    {
        screenPoint = default;
        if (_dpy == IntPtr.Zero) return false;
        if (!X11.XQueryPointer(_dpy, _root, out _, out _, out int x, out int y, out _, out _, out _)) return false;
        screenPoint = new PixelPoint(x, y);
        return true;
    }

    // ------------------------------------------------------------------ other windows

    public void EnumerateWindows(List<NativeWindowInfo> result)
    {
        if (_dpy == IntPtr.Zero) return;
        long[] stacking = ReadLongs(_root, _atomClientList); // bottom to top
        long[] currentDesktop = ReadLongs(_root, _atomCurrentDesktop);

        for (int i = stacking.Length - 1; i >= 0; i--)
        {
            var w = (IntPtr)stacking[i];
            if (w == _win) continue;

            long[] pid = ReadLongs(w, _atomWmPid);
            if (pid.Length > 0 && pid[0] == _selfPid) continue;

            long[] types = ReadLongs(w, _atomWmType);
            if (types.Length > 0 && !types.Contains((long)_atomTypeNormal) && !types.Contains((long)_atomTypeDialog)) continue;
            if (ReadLongs(w, _atomWmState).Contains((long)_atomHidden)) continue;

            long[] desktop = ReadLongs(w, _atomWmDesktop);
            if (desktop.Length > 0 && currentDesktop.Length > 0 && (desktop[0] & 0xFFFFFFFF) != 0xFFFFFFFF && desktop[0] != currentDesktop[0])
                continue;

            if (X11.XGetGeometry(_dpy, w, out _, out _, out _, out uint width, out uint height, out _, out _) == 0) continue;
            if (!X11.XTranslateCoordinates(_dpy, w, _root, 0, 0, out int x, out int y, out _)) continue;
            int wd = (int)width, ht = (int)height;

            long[] frame = ReadLongs(w, _atomFrameExtents); // server-side decorations: left, right, top, bottom
            if (frame.Length == 4)
            {
                x -= (int)frame[0];
                y -= (int)frame[2];
                wd += (int)(frame[0] + frame[1]);
                ht += (int)(frame[2] + frame[3]);
            }
            long[] gtk = ReadLongs(w, _atomGtkFrameExtents); // client-side decorations: invisible shadow margins
            if (gtk.Length == 4)
            {
                x += (int)gtk[0];
                y += (int)gtk[2];
                wd -= (int)(gtk[0] + gtk[1]);
                ht -= (int)(gtk[2] + gtk[3]);
            }
            if (wd < 80 || ht < 40) continue;
            result.Add(new NativeWindowInfo(w, new PixelRect(x, y, wd, ht)));
        }
    }

    long[] ReadLongs(IntPtr window, IntPtr property)
    {
        if (X11.XGetWindowProperty(_dpy, window, property, IntPtr.Zero, 4096, false, IntPtr.Zero,
                out _, out int format, out IntPtr count, out _, out IntPtr data) != 0 || data == IntPtr.Zero)
            return Array.Empty<long>();
        try
        {
            if (format != 32) return Array.Empty<long>();
            var values = new long[(int)count];
            for (int i = 0; i < values.Length; i++) values[i] = Marshal.ReadInt64(data, i * 8);
            return values;
        }
        finally
        {
            X11.XFree(data);
        }
    }

    // ------------------------------------------------------------------ hotkeys

    /// <summary>
    /// Grabs Ctrl+Alt+G/N/B on the root window. Works on Xorg sessions; on Wayland the compositor
    /// keeps global keys to itself, so use GNOME custom shortcuts running "deskarcade --signal ..." instead.
    /// </summary>
    public bool RegisterHotkeys(Action<HotkeyAction> onHotkey)
    {
        _onHotkey = onHotkey;
        if (_hkDpy != IntPtr.Zero) return true;
        try { _hkDpy = X11.XOpenDisplay(IntPtr.Zero); }
        catch (DllNotFoundException) { return false; }
        if (_hkDpy == IntPtr.Zero) return false;

        IntPtr root = X11.XDefaultRootWindow(_hkDpy);
        long[] keysyms = { 0x67, 0x6e, 0x62 }; // g, n, b (order matches HotkeyAction)
        uint[] extras = { 0, X11.LockMask, X11.Mod2Mask, X11.LockMask | X11.Mod2Mask };
        for (int i = 0; i < keysyms.Length; i++)
        {
            _keycodes[i] = X11.XKeysymToKeycode(_hkDpy, (IntPtr)keysyms[i]);
            foreach (uint extra in extras)
                X11.XGrabKey(_hkDpy, _keycodes[i], X11.ControlMask | X11.Mod1Mask | extra, root, true, X11.GrabModeAsync, X11.GrabModeAsync);
        }
        X11.XSync(_hkDpy, false);

        _hkRunning = true;
        _hkThread = new Thread(HotkeyLoop) { IsBackground = true, Name = "DeskArcade.Hotkeys" };
        _hkThread.Start();
        return true;
    }

    void HotkeyLoop()
    {
        IntPtr ev = Marshal.AllocHGlobal(256);
        try
        {
            while (_hkRunning)
            {
                while (_hkRunning && X11.XPending(_hkDpy) > 0)
                {
                    X11.XNextEvent(_hkDpy, ev);
                    if (Marshal.ReadInt32(ev, 0) != X11.KeyPress) continue;
                    int index = Array.IndexOf(_keycodes, Marshal.ReadInt32(ev, X11.KeyEventKeycodeOffset));
                    if (index < 0) continue;
                    var action = (HotkeyAction)index;
                    Dispatcher.UIThread.Post(() => _onHotkey?.Invoke(action));
                }
                Thread.Sleep(40);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ev);
        }
    }

    // ------------------------------------------------------------------ audio & autostart

    public IAudioOutput? OpenAudio(int sampleRate) => PulseAudio.TryOpen(sampleRate);

    static string AutostartFile => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "autostart", "deskarcade.desktop");

    public bool AutostartEnabled
    {
        get => File.Exists(AutostartFile);
        set
        {
            if (!value)
            {
                if (File.Exists(AutostartFile)) File.Delete(AutostartFile);
                return;
            }
            string exe = File.Exists("/usr/bin/deskarcade") ? "/usr/bin/deskarcade" : Environment.ProcessPath ?? "deskarcade";
            Directory.CreateDirectory(Path.GetDirectoryName(AutostartFile)!);
            File.WriteAllText(AutostartFile,
                "[Desktop Entry]\nType=Application\nName=Desk Arcade\n" +
                $"Exec=\"{exe}\"\nIcon=deskarcade\nX-GNOME-Autostart-enabled=true\nNoDisplay=false\n");
        }
    }

    public void Dispose()
    {
        _hkRunning = false;
        _hkThread?.Join(300);
        if (_hkDpy != IntPtr.Zero)
        {
            X11.XCloseDisplay(_hkDpy);
            _hkDpy = IntPtr.Zero;
        }
        if (_dpy != IntPtr.Zero) X11.XCloseDisplay(_dpy);
    }
}
