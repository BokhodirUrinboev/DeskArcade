using System;
using System.Runtime.InteropServices;

namespace DeskArcade.Platform.Mac;

/// <summary>
/// CoreGraphics cursor position and window list. Both use global display coordinates: points, origin at the
/// top-left of the main display (the one with the menu bar), y growing downwards.
/// </summary>
internal static class CoreGraphics
{
    public const string Lib = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    public const uint WindowListOptionOnScreenOnly = 1 << 0;
    public const uint WindowListExcludeDesktopElements = 1 << 4;
    public const uint NullWindowId = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct CGPoint { public double X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct CGSize { public double Width, Height; }

    [StructLayout(LayoutKind.Sequential)]
    public struct CGRect
    {
        public CGPoint Origin;
        public CGSize Size;
    }

    /// <summary>The kCGWindow* keys of a window-info dictionary.</summary>
    public readonly record struct WindowKeys(IntPtr Number, IntPtr Layer, IntPtr Bounds, IntPtr Alpha, IntPtr OwnerPid);

    [DllImport(Lib)] public static extern IntPtr CGEventCreate(IntPtr source);
    [DllImport(Lib)] public static extern CGPoint CGEventGetLocation(IntPtr cgEvent);
    [DllImport(Lib)] public static extern IntPtr CGWindowListCopyWindowInfo(uint option, uint relativeToWindow);
    [DllImport(Lib)] public static extern byte CGRectMakeWithDictionaryRepresentation(IntPtr dictionary, out CGRect rect);

    /// <summary>The keys are exported CFStringRef constants, so read them from the framework rather than recreating them.</summary>
    public static WindowKeys? LoadWindowKeys()
    {
        if (!NativeLibrary.TryLoad(Lib, out IntPtr lib)) return null;
        IntPtr Key(string name) => NativeLibrary.TryGetExport(lib, name, out IntPtr address) ? Marshal.ReadIntPtr(address) : IntPtr.Zero;
        var keys = new WindowKeys(Key("kCGWindowNumber"), Key("kCGWindowLayer"), Key("kCGWindowBounds"),
            Key("kCGWindowAlpha"), Key("kCGWindowOwnerPID"));
        bool complete = keys.Number != IntPtr.Zero && keys.Layer != IntPtr.Zero && keys.Bounds != IntPtr.Zero
            && keys.Alpha != IntPtr.Zero && keys.OwnerPid != IntPtr.Zero;
        return complete ? keys : null;
    }
}
