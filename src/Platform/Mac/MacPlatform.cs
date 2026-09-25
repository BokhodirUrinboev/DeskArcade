using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using DeskArcade.Engine;

namespace DeskArcade.Platform.Mac;

/// <summary>
/// macOS overlay plumbing, all through P/Invoke: AppKit via the Objective-C runtime, CoreGraphics for the cursor
/// and the window list, Carbon for global hotkeys, AudioQueue for sound. An NSWindow has no input shape, so, as on
/// Windows, the whole window ignores the mouse and ignoresMouseEvents is switched off only while the cursor is
/// over something interactive.
/// </summary>
/// <remarks>
/// Coordinates. Avalonia.Native (read in the 11.3.9 and 11.3.22 sources: Screens.mm, TopLevelImpl.mm,
/// WindowBaseImpl.mm, Helpers.cs) reports Screen.Bounds, Screen.WorkingArea and Window.Position in points, with the
/// origin at the top-left of the primary screen (NSScreen.screens[0], the one with the menu bar) and y growing
/// downwards; Screen.Scaling is always 1, and PointToClient(PixelPoint) takes that same space and returns DIPs,
/// which on macOS are points too. So every "PixelPoint" and "PixelRect" on macOS is whole points, never
/// backing-store pixels. CoreGraphics global display coordinates (CGEventGetLocation, kCGWindowBounds) are
/// points with the origin at the top-left of the main display, the same space, so no conversion is needed
/// beyond rounding to whole points, which is what Avalonia does with its own values.
/// </remarks>
public sealed class MacPlatform : IDesktopPlatform
{
    const string LaunchAgentLabel = "com.imperiumgames.deskarcade";
    const uint HotkeySignature = 0x446B4172; // 'DkAr'
    const nint FloatingWindowLevel = 3;     // NSFloatingWindowLevel
    const nint ActivationPolicyRegular = 0;
    const nint ActivationPolicyAccessory = 1;

    // NSWindowCollectionBehavior: canJoinAllSpaces | stationary | ignoresCycle | fullScreenAuxiliary
    const nint OverlayCollectionBehavior = (1 << 0) | (1 << 4) | (1 << 6) | (1 << 8);

    readonly long _selfPid = Environment.ProcessId;
    Window? _overlay;
    IntPtr _nsWindow;
    DispatcherTimer? _hitTimer;
    HitShape[] _regions = Array.Empty<HitShape>();
    bool _capture;
    bool _clickThrough = true;
    bool _cursorFailed;

    CoreGraphics.WindowKeys? _windowKeys;
    bool _windowsFailed;

    GCHandle _self; // user data of the Carbon handler
    IntPtr _hotkeyHandler;
    readonly IntPtr[] _hotkeyRefs = new IntPtr[3];
    bool _hotkeysRegistered;
    Action<HotkeyAction>? _onHotkey;

    public string Name => "macos";

    // ------------------------------------------------------------------ overlay

    public void AttachOverlay(Window overlay)
    {
        _overlay = overlay;
        try
        {
            _nsWindow = FindNSWindow(overlay.TryGetPlatformHandle());
            if (_nsWindow == IntPtr.Zero) return;
            _clickThrough = true;
            ConfigureWindow();
            ApplyClickThrough(); // start fully click-through
        }
        catch (Exception)
        {
            _nsWindow = IntPtr.Zero; // no AppKit access: the overlay still renders, it just takes the mouse
            return;
        }
        // ~60 Hz: AppKit only reports mouse movement to a window that is not ignoring the mouse
        _hitTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, (_, _) => UpdateClickThrough());
        _hitTimer.Start();
    }

    /// <summary>
    /// Avalonia.Native hands out its NSWindow ("NSWindow" descriptor) for a Window; an embedded top level only has an
    /// NSView, whose window is asked for.
    /// </summary>
    static IntPtr FindNSWindow(IPlatformHandle? handle)
    {
        if (handle == null) return IntPtr.Zero;
        IntPtr window = IntPtr.Zero;
        if (handle.HandleDescriptor == "NSWindow")
            window = handle.Handle;
        else if (handle.HandleDescriptor == "NSView" && ObjC.IsKindOf(handle.Handle, "NSView"))
            window = ObjC.GetObject(handle.Handle, "window");
        if (window == IntPtr.Zero && handle is IMacOSTopLevelPlatformHandle mac)
            window = mac.NSWindow;
        return ObjC.IsKindOf(window, "NSWindow") ? window : IntPtr.Zero;
    }

    void ConfigureWindow()
    {
        ObjC.SetInteger(_nsWindow, "setLevel:", FloatingWindowLevel);
        ObjC.SetInteger(_nsWindow, "setCollectionBehavior:", OverlayCollectionBehavior);
        ObjC.SetBool(_nsWindow, "setHidesOnDeactivate:", false);

        // Leave keyboard focus where the user is typing. setCanBecomeKeyWindow: is Avalonia's own AvnWindow switch;
        // _setPreventsActivation: is the AppKit flag behind non-activating panels, so clicking a ball does not bring
        // Desk Arcade to the front. It is private, so both are only sent when the window answers to them.
        if (ObjC.RespondsTo(_nsWindow, "setCanBecomeKeyWindow:")) ObjC.SetBool(_nsWindow, "setCanBecomeKeyWindow:", false);
        if (ObjC.RespondsTo(_nsWindow, "_setPreventsActivation:")) ObjC.SetBool(_nsWindow, "_setPreventsActivation:", true);
        HideFromDock();
    }

    /// <summary>ShowInTaskbar = false, the macOS way: an accessory app has no Dock icon and no menu bar of its own.</summary>
    static void HideFromDock()
    {
        IntPtr cls = ObjC.objc_getClass("NSApplication");
        IntPtr app = cls == IntPtr.Zero ? IntPtr.Zero : ObjC.GetObject(cls, "sharedApplication");
        if (app == IntPtr.Zero || ObjC.GetInteger(app, "activationPolicy") != ActivationPolicyRegular) return;
        ObjC.SetInteger(app, "setActivationPolicy:", ActivationPolicyAccessory);
    }

    /// <summary>Cheap when nothing changed; also repairs the flag if the toolkit rewrote it.</summary>
    void ApplyClickThrough()
    {
        if (ObjC.GetBool(_nsWindow, "ignoresMouseEvents") != _clickThrough)
            ObjC.SetBool(_nsWindow, "setIgnoresMouseEvents:", _clickThrough);
    }

    void UpdateClickThrough()
    {
        if (_overlay is not { IsVisible: true } || _nsWindow == IntPtr.Zero) return;
        try
        {
            bool interactive = _capture;
            if (!interactive && TryGetCursor(out var px))
            {
                Vec2 p = _overlay.PointToClient(px);
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
            ApplyClickThrough();
        }
        catch (Exception)
        {
            _hitTimer?.Stop(); // give up on click-through rather than fail every frame
            _nsWindow = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Avalonia.Native clamps the size of a window that is not shown yet to the screen's visibleFrame, and AppKit
    /// keeps windows below the menu bar, so a full-screen-sized overlay would come out shorter than asked and be
    /// pushed down, leaving its floor above the Dock. The menu bar and Dock sit above floating windows anyway and the
    /// games only use the working area, so covering exactly the working area loses nothing.
    /// </summary>
    public bool OverlayFitsWorkArea => true;

    public void SetInputRegions(IReadOnlyList<HitShape> regions, bool captureActive)
    {
        _regions = [.. regions];
        _capture = captureActive;
        if (captureActive) UpdateClickThrough();
    }

    /// <summary>Global cursor position in whole points, top-left origin (see the class remarks).</summary>
    public bool TryGetCursor(out PixelPoint screenPoint)
    {
        screenPoint = default;
        if (_cursorFailed) return false;
        try
        {
            IntPtr cgEvent = CoreGraphics.CGEventCreate(IntPtr.Zero);
            if (cgEvent == IntPtr.Zero) return false;
            var location = CoreGraphics.CGEventGetLocation(cgEvent);
            CoreFoundation.CFRelease(cgEvent);
            screenPoint = new PixelPoint((int)Math.Floor(location.X), (int)Math.Floor(location.Y));
            return true;
        }
        catch (Exception)
        {
            _cursorFailed = true;
            return false;
        }
    }

    // ------------------------------------------------------------------ other windows

    /// <summary>
    /// Normal-layer, visible windows of other processes, front to back. Bounds and owner PID do not need the Screen
    /// Recording permission (only window titles do).
    /// </summary>
    public void EnumerateWindows(List<NativeWindowInfo> result)
    {
        if (_windowsFailed) return;
        IntPtr list = IntPtr.Zero;
        try
        {
            _windowKeys ??= CoreGraphics.LoadWindowKeys();
            if (_windowKeys is not { } keys)
            {
                _windowsFailed = true;
                return;
            }
            list = CoreGraphics.CGWindowListCopyWindowInfo(
                CoreGraphics.WindowListOptionOnScreenOnly | CoreGraphics.WindowListExcludeDesktopElements, CoreGraphics.NullWindowId);
            if (list == IntPtr.Zero) return;

            nint count = CoreFoundation.CFArrayGetCount(list);
            for (nint i = 0; i < count; i++)
            {
                IntPtr info = CoreFoundation.CFArrayGetValueAtIndex(list, i);
                if (!CoreFoundation.IsDictionary(info)) continue;
                if (!CoreFoundation.TryGetLong(info, keys.Layer, out long layer) || layer != 0) continue;
                if (!CoreFoundation.TryGetLong(info, keys.OwnerPid, out long pid) || pid == _selfPid) continue;
                if (!CoreFoundation.TryGetDouble(info, keys.Alpha, out double alpha) || alpha <= 0) continue;

                IntPtr bounds = CoreFoundation.CFDictionaryGetValue(info, keys.Bounds);
                if (!CoreFoundation.IsDictionary(bounds) || CoreGraphics.CGRectMakeWithDictionaryRepresentation(bounds, out var rect) == 0)
                    continue;
                int x = (int)Math.Floor(rect.Origin.X), y = (int)Math.Floor(rect.Origin.Y);
                int wd = (int)Math.Ceiling(rect.Origin.X + rect.Size.Width) - x;
                int ht = (int)Math.Ceiling(rect.Origin.Y + rect.Size.Height) - y;
                if (wd < 80 || ht < 40) continue;

                CoreFoundation.TryGetLong(info, keys.Number, out long number);
                result.Add(new NativeWindowInfo((IntPtr)number, new PixelRect(x, y, wd, ht), (int)pid));
            }
        }
        catch (Exception)
        {
            _windowsFailed = true; // window tops are best effort
        }
        finally
        {
            if (list != IntPtr.Zero) CoreFoundation.CFRelease(list);
        }
    }

    // ------------------------------------------------------------------ hotkeys

    /// <summary>
    /// Control+Option+G/N/B through Carbon RegisterEventHotKey. The handler runs on the main thread, which the
    /// Avalonia.Native event loop pumps; no Accessibility or Input Monitoring permission is involved.
    /// </summary>
    public bool RegisterHotkeys(Action<HotkeyAction> onHotkey, HotkeySet keys)
    {
        _onHotkey = onHotkey;
        if (_hotkeyHandler != IntPtr.Zero) return _hotkeysRegistered;
        try
        {
            IntPtr target = Carbon.GetApplicationEventTarget();
            if (target == IntPtr.Zero) return false;

            if (!_self.IsAllocated) _self = GCHandle.Alloc(this);
            var spec = new Carbon.EventTypeSpec { EventClass = Carbon.EventClassKeyboard, EventKind = Carbon.EventHotKeyPressed };
            IntPtr handler;
            unsafe
            {
                handler = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, int>)&OnHotkeyEvent;
            }
            if (Carbon.InstallEventHandler(target, handler, 1, ref spec, GCHandle.ToIntPtr(_self), out IntPtr handlerRef) != Carbon.NoErr)
                return false;
            _hotkeyHandler = handlerRef;

            uint mods = (keys.Ctrl ? Carbon.ControlKey : 0) | (keys.Alt ? Carbon.OptionKey : 0) | (keys.Shift ? Carbon.ShiftKey : 0);
            bool ok = true;
            for (int i = 0; i < _hotkeyRefs.Length; i++) // order matches HotkeyAction
            {
                var id = new Carbon.EventHotKeyID { Signature = HotkeySignature, Id = (uint)i + 1 };
                ok &= Carbon.RegisterEventHotKey(Carbon.KeyCode(keys.Key((HotkeyAction)i)), mods, id, target, 0, out _hotkeyRefs[i]) == Carbon.NoErr;
            }
            _hotkeysRegistered = ok;
            return ok;
        }
        catch (Exception)
        {
            return false;
        }
    }

    [UnmanagedCallersOnly]
    static int OnHotkeyEvent(IntPtr nextHandler, IntPtr carbonEvent, IntPtr userData)
    {
        try
        {
            if (Carbon.GetEventParameter(carbonEvent, Carbon.EventParamDirectObject, Carbon.TypeEventHotKeyId, IntPtr.Zero,
                    (nuint)Marshal.SizeOf<Carbon.EventHotKeyID>(), IntPtr.Zero, out var hotkey) != Carbon.NoErr
                || hotkey.Signature != HotkeySignature || hotkey.Id is < 1 or > 3
                || GCHandle.FromIntPtr(userData).Target is not MacPlatform platform)
                return Carbon.EventNotHandledErr;
            var action = (HotkeyAction)(hotkey.Id - 1);
            Dispatcher.UIThread.Post(() => platform._onHotkey?.Invoke(action));
            return Carbon.NoErr;
        }
        catch
        {
            return Carbon.EventNotHandledErr; // an exception must never unwind into Carbon
        }
    }

    // ------------------------------------------------------------------ audio & autostart

    public IAudioOutput? OpenAudio(int sampleRate) => AudioQueueOutput.TryOpen(sampleRate);

    static string LaunchAgentFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", LaunchAgentLabel + ".plist");

    /// <summary>"/Applications/Desk Arcade.app" when running from .../Desk Arcade.app/Contents/MacOS/.</summary>
    static string? AppBundlePath()
    {
        if (Environment.ProcessPath is not { Length: > 0 } exe) return null;
        var macOS = Directory.GetParent(exe);
        var contents = macOS?.Parent;
        var bundle = contents?.Parent;
        return macOS?.Name == "MacOS" && contents?.Name == "Contents" && bundle != null
               && bundle.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
            ? bundle.FullName
            : null;
    }

    /// <summary>A per-user LaunchAgent that runs at login (listed under System Settings → General → Login Items).</summary>
    public bool AutostartEnabled
    {
        get => File.Exists(LaunchAgentFile);
        set
        {
            if (!value)
            {
                if (File.Exists(LaunchAgentFile)) File.Delete(LaunchAgentFile);
                return;
            }
            // Inside a bundle, let LaunchServices start the .app; otherwise run the executable itself.
            string[] program = AppBundlePath() is { } bundle
                ? ["/usr/bin/open", bundle]
                : [Environment.ProcessPath ?? "DeskArcade"];
            string arguments = string.Concat(program.Select(a => $"\n    <string>{SecurityElement.Escape(a)}</string>"));
            Directory.CreateDirectory(Path.GetDirectoryName(LaunchAgentFile)!);
            File.WriteAllText(LaunchAgentFile,
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
                "<plist version=\"1.0\">\n<dict>\n" +
                $"  <key>Label</key>\n  <string>{LaunchAgentLabel}</string>\n" +
                $"  <key>ProgramArguments</key>\n  <array>{arguments}\n  </array>\n" +
                "  <key>RunAtLoad</key>\n  <true/>\n" +
                "  <key>ProcessType</key>\n  <string>Interactive</string>\n" +
                "</dict>\n</plist>\n");
        }
    }

    public void Dispose()
    {
        _hitTimer?.Stop();
        try
        {
            for (int i = 0; i < _hotkeyRefs.Length; i++)
            {
                if (_hotkeyRefs[i] == IntPtr.Zero) continue;
                Carbon.UnregisterEventHotKey(_hotkeyRefs[i]);
                _hotkeyRefs[i] = IntPtr.Zero;
            }
            if (_hotkeyHandler != IntPtr.Zero)
            {
                Carbon.RemoveEventHandler(_hotkeyHandler);
                _hotkeyHandler = IntPtr.Zero;
            }
        }
        catch (Exception)
        {
            // quitting anyway
        }
        if (_self.IsAllocated) _self.Free();
    }
}
