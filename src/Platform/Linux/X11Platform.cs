using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using DeskArcade.Engine;

namespace DeskArcade.Platform.Linux;

/// <summary>
/// Ubuntu / X11 overlay plumbing (also used under Wayland through XWayland).
/// Click-through uses the XShape *input* region: only interactive areas take the mouse.
/// Window tops come from _NET_CLIENT_LIST_STACKING; under Wayland only X11 apps are visible there.
/// Every Xlib call here is asynchronous and error-tolerant: a window that vanished, or no window manager at all
/// (xvfb), makes the server answer with an error that <see cref="IgnoreErrors"/> swallows.
/// </summary>
public sealed class X11Platform : IDesktopPlatform
{
    static readonly X11.XErrorHandler IgnoreErrors = (_, _) => 0; // a vanished window must not kill the process
    static readonly Stopwatch Clock = Stopwatch.StartNew();

    /// <summary>The StatusNotifierItem tray needs this name on the session bus (KDE, XFCE, GNOME with AppIndicator).</summary>
    public const string TrayWatcher = "org.kde.StatusNotifierWatcher";

    readonly IntPtr _dpy;
    readonly IntPtr _root;
    readonly long _selfPid = Environment.ProcessId;
    readonly IntPtr _atomClientList, _atomWmState, _atomHidden, _atomWmType, _atomTypeNormal, _atomTypeDialog;
    readonly IntPtr _atomFrameExtents, _atomGtkFrameExtents, _atomWmPid, _atomWmDesktop, _atomCurrentDesktop, _atomTakeFocus;
    readonly IntPtr _atomBypassCompositor, _atomUserTime;
    readonly long[] _overlayStates = Array.Empty<long>();

    Window? _overlay;
    IntPtr _win;
    X11.XRectangle[]? _lastShape;
    readonly PointerStaleness _staleness = new(TimeSpan.FromSeconds(1.5));
    volatile bool _hasTray = true;

    IntPtr _hkDpy;
    Thread? _hkThread;
    volatile bool _hkRunning;
    readonly int[] _keycodes = new int[3];
    HotkeySet _keys = HotkeySet.Default;
    Action<HotkeyAction>? _onHotkey;
    GlobalShortcutsPortal? _portal;
    volatile bool _disposed;

    public X11Platform()
    {
        CheckTray();
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
        _atomBypassCompositor = Atom("_NET_WM_BYPASS_COMPOSITOR");
        _atomUserTime = Atom("_NET_WM_USER_TIME");
        _overlayStates = Ewmh.OverlayStates.Select(name => (long)Atom(name)).ToArray();
    }

    public string Name => _dpy == IntPtr.Zero ? "x11 (no display)" : "x11";

    IntPtr Atom(string name) => X11.XInternAtom(_dpy, name, false);

    // ------------------------------------------------------------------ overlay

    /// <summary>
    /// Runs right after Show(), so the map request is already on its way but the window manager has most likely not
    /// acted on it yet: the hints and properties set here queue up ahead of its reaction, and the client messages
    /// reach it once it manages the window. Called again on every remap, because Mutter deletes _NET_WM_STATE and
    /// _NET_WM_DESKTOP when a window is withdrawn and other managers reset the hints.
    /// </summary>
    public void AttachOverlay(Window overlay)
    {
        _overlay = overlay;
        var handle = overlay.TryGetPlatformHandle();
        if (_dpy == IntPtr.Zero || handle == null) return;
        _win = handle.Handle;
        IntPtr previousFocus = FocusedWindow(); // usually still the terminal at this point
        NeverTakeFocus();
        KeepAboveEverywhere();
        _lastShape = null;
        ApplyShape(Array.Empty<X11.XRectangle>()); // start fully click-through
        _staleness.Reset();
        GiveFocusBackLater(previousFocus);
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
        // _NET_WM_USER_TIME 0: "never interacted with", which the focus-stealing rules of Mutter, KWin and xfwm4
        // read as "do not focus this window when it maps"
        X11.XChangeProperty(_dpy, _win, _atomUserTime, X11.XA_CARDINAL, 32, X11.PropModeReplace, new long[] { 0 }, 1);
        X11.XFlush(_dpy);
    }

    /// <summary>
    /// _NET_WM_STATE ABOVE, STICKY, SKIP_TASKBAR and SKIP_PAGER, _NET_WM_DESKTOP "all", and _NET_WM_BYPASS_COMPOSITOR 2,
    /// because the transparency only exists while the compositor draws us. Each goes both ways: the property on the
    /// window for a manager that reads it when it maps the window, and the EWMH client message to the root for one
    /// that is already managing it (the property is ignored by then, and Mutter wipes it on every withdraw).
    /// The window type is NORMAL, written out because Avalonia leaves it unset. The other types are worse for a
    /// shaped, unfocusable, always-on-top window: Mutter and KWin both stack an ABOVE normal window in a layer over
    /// docks, whereas DOCK windows are tied to their struts, hidden under fullscreen windows and never moved between
    /// monitors by the manager, NOTIFICATION and SPLASH windows are placed by the manager where it likes and get no
    /// _NET_WM_STATE handling at all in Mutter, and none of them changes the focus rules that the input hint already
    /// settles.
    /// </summary>
    void KeepAboveEverywhere()
    {
        X11.XChangeProperty(_dpy, _win, _atomWmType, X11.XA_ATOM, 32, X11.PropModeReplace, new[] { (long)_atomTypeNormal }, 1);
        long[] states = Ewmh.MergeStates(ReadLongs(_win, _atomWmState), _overlayStates);
        X11.XChangeProperty(_dpy, _win, _atomWmState, X11.XA_ATOM, 32, X11.PropModeReplace, states, states.Length);
        X11.XChangeProperty(_dpy, _win, _atomWmDesktop, X11.XA_CARDINAL, 32, X11.PropModeReplace, new[] { Ewmh.AllDesktops }, 1);
        X11.XChangeProperty(_dpy, _win, _atomBypassCompositor, X11.XA_CARDINAL, 32, X11.PropModeReplace, new[] { Ewmh.BypassCompositorNever }, 1);

        foreach (var (first, second) in Ewmh.Pairs(_overlayStates))
            SendToRoot(_atomWmState, Ewmh.StateMessage(Ewmh.StateAdd, first, second));
        SendToRoot(_atomWmDesktop, Ewmh.DesktopMessage(Ewmh.AllDesktops));
        X11.XFlush(_dpy);
    }

    /// <summary>An EWMH client message about our window, delivered to the window manager through the root window.</summary>
    void SendToRoot(IntPtr messageType, long[] data)
    {
        long[] ev = Ewmh.ClientMessage((long)_win, (long)messageType, data);
        X11.XSendEvent(_dpy, _root, false, (IntPtr)(X11.SubstructureRedirectMask | X11.SubstructureNotifyMask), ev);
    }

    IntPtr FocusedWindow() => X11.XGetInputFocus(_dpy, out IntPtr focus, out _) != 0 ? focus : X11.None;

    bool IsViewable(IntPtr window) =>
        X11.XGetWindowAttributes(_dpy, window, out var attributes) != 0 && attributes.MapState == X11.IsViewable;

    /// <summary>
    /// A window manager that focuses every new window regardless of the hints would take the keyboard away from the
    /// terminal. Looks twice after the map, once the manager has had time to act: if we hold the focus, it goes back
    /// to the window that had it before, or to the pointer root when that window is gone.
    /// </summary>
    void GiveFocusBackLater(IntPtr previous)
    {
        IntPtr win = _win;
        foreach (double seconds in new[] { 0.25, 1.0 })
            DispatcherTimer.RunOnce(() => GiveFocusBack(win, previous), TimeSpan.FromSeconds(seconds));
    }

    void GiveFocusBack(IntPtr win, IntPtr previous)
    {
        if (_disposed || _dpy == IntPtr.Zero || win != _win) return;
        if (X11.XGetInputFocus(_dpy, out IntPtr focus, out _) == 0 || focus != win) return;
        if (Ewmh.IsRealWindow((long)previous) && previous != win && IsViewable(previous))
            X11.XSetInputFocus(_dpy, previous, X11.RevertToParent, X11.CurrentTime);
        else
            X11.XSetInputFocus(_dpy, X11.PointerRoot, X11.RevertToPointerRoot, X11.CurrentTime);
        X11.XFlush(_dpy);
    }

    /// <summary>
    /// Mutter (GNOME, Xorg and Wayland alike) does not place a window that is larger than the working
    /// area on the monitor it was asked for: it moves it to a monitor where it fits, which on a
    /// two-monitor desktop is the one without the top bar. A working-area-sized window stays put and
    /// can still be moved between monitors.
    /// </summary>
    public bool OverlayFitsWorkArea => true;

    /// <summary>
    /// The tray is a StatusNotifierItem over D-Bus, which only shows up where an org.kde.StatusNotifierWatcher runs:
    /// KDE, XFCE with its plugin, GNOME with the AppIndicator extension. Stock GNOME has none, and then the menu is
    /// only on the scoreboard. Answered by a background check that gives up after 1.5 s; true whenever unsure.
    /// </summary>
    public bool HasTray => _hasTray;

    void CheckTray()
    {
        Task.Run(async () =>
        {
            try
            {
                if (await SessionBus.NameHasOwnerAsync(TrayWatcher, TimeSpan.FromSeconds(1.5)).ConfigureAwait(false) == false)
                    _hasTray = false;
            }
            catch (Exception)
            {
                // no session bus or a broken one: assume the tray works, as it did before this check existed
            }
        });
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

    /// <summary>
    /// On Xorg the X server owns the pointer and this is always right. Under Wayland, XWayland only hears about the
    /// pointer while it is over an X11 surface; over a native app the position it reports freezes where the pointer
    /// left. XWayland gives no sign of that, and a pointer resting on an X11 app looks exactly the same, so this is
    /// deliberately cautious: only on a Wayland session, only outside our own input regions (where the compositor
    /// routes the pointer to us and our own pointer events agree anyway), and only after 1.5 s without any change.
    /// The overlay then falls back to the last pointer event it received itself, which is all it would have had
    /// anyway had the pointer really left; the one cost is a still pointer over an X11 app that stops being tracked
    /// until it moves again.
    /// </summary>
    public bool TryGetCursor(out PixelPoint screenPoint)
    {
        screenPoint = default;
        if (_dpy == IntPtr.Zero) return false;
        if (!X11.XQueryPointer(_dpy, _root, out _, out _, out int x, out int y, out _, out _, out _)) return false;
        screenPoint = new PixelPoint(x, y);
        if (!IsWaylandSession) return true;
        return _staleness.Trust(screenPoint, OverOwnInput(screenPoint), Clock.Elapsed);
    }

    /// <summary>Whether a screen point lies in the overlay's current input shape (device pixels from the window origin).</summary>
    bool OverOwnInput(PixelPoint p)
    {
        if (_overlay == null || _lastShape == null) return true; // unknown: trust the position
        var origin = _overlay.Position;
        int lx = p.X - origin.X, ly = p.Y - origin.Y;
        foreach (var r in _lastShape)
            if (lx >= r.X && ly >= r.Y && lx < r.X + r.Width && ly < r.Y + r.Height) return true;
        return false;
    }

    // ------------------------------------------------------------------ other windows

    /// <summary>
    /// Polled every 100-350 ms, so each window costs as few round trips as possible: XGetWindowAttributes first, which
    /// settles minimized windows and those on other workspaces (both unmapped under Mutter, KWin and xfwm4) for one
    /// request and doubles as the geometry, then the properties in order of how many windows they rule out, with
    /// _NET_WM_WINDOW_TYPE last because it only excludes docks and the like. Atoms are interned once, in the constructor.
    /// </summary>
    public void EnumerateWindows(List<NativeWindowInfo> result)
    {
        if (_dpy == IntPtr.Zero) return;
        long[] stacking = ReadLongs(_root, _atomClientList); // bottom to top
        long[] currentDesktop = ReadLongs(_root, _atomCurrentDesktop);

        for (int i = stacking.Length - 1; i >= 0; i--)
        {
            var w = (IntPtr)stacking[i];
            if (w == _win) continue;
            if (X11.XGetWindowAttributes(_dpy, w, out var attributes) == 0 || attributes.MapState != X11.IsViewable) continue;

            long[] pid = ReadLongs(w, _atomWmPid);
            if (pid.Length > 0 && pid[0] == _selfPid) continue;
            if (ReadLongs(w, _atomWmState).Contains((long)_atomHidden)) continue;

            long[] desktop = ReadLongs(w, _atomWmDesktop);
            if (desktop.Length > 0 && currentDesktop.Length > 0 && (desktop[0] & 0xFFFFFFFF) != 0xFFFFFFFF && desktop[0] != currentDesktop[0])
                continue;

            if (!X11.XTranslateCoordinates(_dpy, w, _root, 0, 0, out int x, out int y, out _)) continue;
            int wd = attributes.Width, ht = attributes.Height;

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

            long[] types = ReadLongs(w, _atomWmType);
            if (types.Length > 0 && !types.Contains((long)_atomTypeNormal) && !types.Contains((long)_atomTypeDialog)) continue;
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

    static bool IsWaylandSession =>
        string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase) ||
        Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 };

    /// <summary>
    /// Ctrl+Alt+G/N/B. On Xorg they are grabbed on the root window. On Wayland the compositor keeps global keys
    /// to itself, so they are requested in the background through the XDG GlobalShortcuts portal (GNOME 48+, KDE Plasma),
    /// which asks the user once. Without the portal, or when the user declines, it falls back to the X11 grab, which
    /// XWayland only honours while an X11 window has focus; custom shortcuts running "deskarcade --signal ..." still work.
    /// </summary>
    public bool RegisterHotkeys(Action<HotkeyAction> onHotkey, HotkeySet keys)
    {
        _onHotkey = onHotkey;
        _keys = keys;
        if (_hkDpy != IntPtr.Zero || _portal != null) return true;
        if (!IsWaylandSession) return GrabHotkeys();

        var portal = _portal = new GlobalShortcutsPortal(RaiseHotkey, keys);
        Task.Run(() => portal.StartAsync()).ContinueWith(started =>
        {
            if (started.IsCompletedSuccessfully && started.Result) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed || _portal != portal) return;
                _portal = null;
                GrabHotkeys();
            });
        }, TaskScheduler.Default);
        return true;
    }

    bool GrabHotkeys()
    {
        try { _hkDpy = X11.XOpenDisplay(IntPtr.Zero); }
        catch (DllNotFoundException) { return false; }
        if (_hkDpy == IntPtr.Zero) return false;

        IntPtr root = X11.XDefaultRootWindow(_hkDpy);
        // lowercase letters' keysyms are their ASCII codes (order matches HotkeyAction)
        long[] keysyms = { char.ToLowerInvariant(_keys.Key(HotkeyAction.ToggleOverlay)), char.ToLowerInvariant(_keys.Key(HotkeyAction.NextGame)), char.ToLowerInvariant(_keys.Key(HotkeyAction.Summon)) };
        uint mods = (_keys.Ctrl ? X11.ControlMask : 0) | (_keys.Alt ? X11.Mod1Mask : 0) | (_keys.Shift ? X11.ShiftMask : 0);
        uint[] extras = { 0, X11.LockMask, X11.Mod2Mask, X11.LockMask | X11.Mod2Mask };
        for (int i = 0; i < keysyms.Length; i++)
        {
            _keycodes[i] = X11.XKeysymToKeycode(_hkDpy, (IntPtr)keysyms[i]);
            foreach (uint extra in extras)
                X11.XGrabKey(_hkDpy, _keycodes[i], mods | extra, root, true, X11.GrabModeAsync, X11.GrabModeAsync);
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
                    RaiseHotkey((HotkeyAction)index);
                }
                Thread.Sleep(40);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ev);
        }
    }

    void RaiseHotkey(HotkeyAction action) => Dispatcher.UIThread.Post(() => _onHotkey?.Invoke(action));

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
            string exe = Program.LaunchPath;
            Directory.CreateDirectory(Path.GetDirectoryName(AutostartFile)!);
            File.WriteAllText(AutostartFile,
                "[Desktop Entry]\nType=Application\nName=Desk Arcade\n" +
                $"Exec=\"{exe}\"\nIcon=deskarcade\nX-GNOME-Autostart-enabled=true\nNoDisplay=false\n");
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _portal?.Dispose(); // closes the portal session and its D-Bus connection
        _portal = null;
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

/// <summary>
/// The EWMH pieces of the overlay window that need no display: atom names, message bodies and the wire layout of an
/// XClientMessageEvent on LP64. Kept pure so the unit tests can check them.
/// </summary>
public static class Ewmh
{
    /// <summary>_NET_WM_STATE atoms the overlay always carries: above everything, on every workspace, in no list.</summary>
    public static readonly string[] OverlayStates =
    {
        "_NET_WM_STATE_ABOVE", "_NET_WM_STATE_STICKY", "_NET_WM_STATE_SKIP_TASKBAR", "_NET_WM_STATE_SKIP_PAGER",
    };

    /// <summary>_NET_WM_DESKTOP value for "all workspaces".</summary>
    public const long AllDesktops = 0xFFFFFFFF;

    /// <summary>_NET_WM_BYPASS_COMPOSITOR: 2 asks the compositor never to unredirect the window.</summary>
    public const long BypassCompositorNever = 2;

    /// <summary>_NET_WM_STATE client message actions.</summary>
    public const long StateRemove = 0, StateAdd = 1, StateToggle = 2;

    /// <summary>Source indication: 1 is a normal application, 2 a pager or the user.</summary>
    public const long SourceApplication = 1;

    /// <summary>XEvent is 24 longs on LP64; the XClientMessageEvent fields sit at these indices.</summary>
    public const int EventLongs = 24, TypeIndex = 0, WindowIndex = 4, MessageTypeIndex = 5, FormatIndex = 6, DataIndex = 7;

    /// <summary>A window id that can take focus: neither None (0) nor PointerRoot (1).</summary>
    public static bool IsRealWindow(long window) => window > 1;

    /// <summary>The states the manager set, followed by the wanted ones it left out; the order and duplicates of the first list are kept.</summary>
    public static long[] MergeStates(long[] existing, long[] wanted)
    {
        var merged = new List<long>(existing);
        foreach (long atom in wanted)
            if (!merged.Contains(atom)) merged.Add(atom);
        return merged.ToArray();
    }

    /// <summary>A _NET_WM_STATE client message carries at most two states, so the wanted list goes out in pairs.</summary>
    public static IEnumerable<(long First, long Second)> Pairs(long[] atoms)
    {
        for (int i = 0; i < atoms.Length; i += 2)
            yield return (atoms[i], i + 1 < atoms.Length ? atoms[i + 1] : 0);
    }

    /// <summary>data.l of a _NET_WM_STATE message: action, one or two states, source indication.</summary>
    public static long[] StateMessage(long action, long first, long second = 0) => new[] { action, first, second, SourceApplication, 0 };

    /// <summary>data.l of a _NET_WM_DESKTOP message: the desktop, source indication.</summary>
    public static long[] DesktopMessage(long desktop) => new[] { desktop, SourceApplication, 0, 0, 0 };

    /// <summary>
    /// An XClientMessageEvent (format 32) as the 24 longs of an XEvent on little-endian LP64: type at 0, window at 4,
    /// message_type at 5, format at 6, data.l[0..4] at 7-11; serial, send_event and display are left for Xlib.
    /// </summary>
    public static long[] ClientMessage(long window, long messageType, long[] data)
    {
        if (data.Length > 5) throw new ArgumentException("a client message carries at most five longs", nameof(data));
        var ev = new long[EventLongs];
        ev[TypeIndex] = X11.ClientMessage;
        ev[WindowIndex] = window;
        ev[MessageTypeIndex] = messageType;
        ev[FormatIndex] = 32;
        data.CopyTo(ev, DataIndex);
        return ev;
    }
}

/// <summary>
/// The stale-pointer rule for Wayland sessions (see <see cref="X11Platform.TryGetCursor"/>): a position is trusted
/// while it keeps changing, while it lies in the overlay's own input regions, and for a grace period after it last
/// changed; after that it is taken for a pointer that has left for a native Wayland window.
/// </summary>
public sealed class PointerStaleness
{
    readonly TimeSpan _grace;
    PixelPoint _last;
    TimeSpan _since;
    bool _any;

    public PointerStaleness(TimeSpan grace) => _grace = grace;

    /// <summary>Forget the history, e.g. when the overlay window is (re)mapped.</summary>
    public void Reset() => _any = false;

    /// <summary>True while <paramref name="reported"/> can be trusted at time <paramref name="now"/>.</summary>
    public bool Trust(PixelPoint reported, bool overOwnInput, TimeSpan now)
    {
        if (!_any || reported != _last || overOwnInput)
        {
            _any = true;
            _last = reported;
            _since = now;
            return true;
        }
        return now - _since <= _grace;
    }
}
