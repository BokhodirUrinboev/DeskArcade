using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace DeskArcade.Platform.Mac;

/// <summary>
/// The few Objective-C runtime calls needed to adjust AppKit objects Avalonia already created.
/// Use only from the main (UI) thread. A message an object does not understand raises an Objective-C
/// exception that aborts the process, so anything beyond plain NSWindow / NSApplication API is checked
/// with <see cref="RespondsTo"/> first.
/// </summary>
internal static class ObjC
{
    const string LibObjC = "/usr/lib/libobjc.A.dylib";

    static readonly Dictionary<string, IntPtr> Selectors = new(StringComparer.Ordinal);

    [DllImport(LibObjC)] public static extern IntPtr objc_getClass(string name);
    [DllImport(LibObjC)] static extern IntPtr sel_registerName(string name);

    // objc_msgSend needs one declaration per signature. None of these return a struct, so the same entry
    // point is correct on x64 and arm64.
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] static extern IntPtr SendReturnPtr(IntPtr receiver, IntPtr selector);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] static extern nint SendReturnInt(IntPtr receiver, IntPtr selector);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] static extern byte SendReturnBool(IntPtr receiver, IntPtr selector);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] static extern byte SendReturnBool(IntPtr receiver, IntPtr selector, IntPtr arg);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] static extern void SendInt(IntPtr receiver, IntPtr selector, nint arg);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] static extern void SendBool(IntPtr receiver, IntPtr selector, byte arg);

    public static IntPtr Sel(string name)
    {
        if (!Selectors.TryGetValue(name, out IntPtr sel)) Selectors[name] = sel = sel_registerName(name);
        return sel;
    }

    public static bool RespondsTo(IntPtr obj, string selector) =>
        obj != IntPtr.Zero && SendReturnBool(obj, Sel("respondsToSelector:"), Sel(selector)) != 0;

    public static bool IsKindOf(IntPtr obj, string className)
    {
        if (obj == IntPtr.Zero) return false;
        IntPtr cls = objc_getClass(className);
        return cls != IntPtr.Zero && SendReturnBool(obj, Sel("isKindOfClass:"), cls) != 0;
    }

    public static IntPtr GetObject(IntPtr obj, string selector) => SendReturnPtr(obj, Sel(selector));
    public static nint GetInteger(IntPtr obj, string selector) => SendReturnInt(obj, Sel(selector));
    public static bool GetBool(IntPtr obj, string selector) => SendReturnBool(obj, Sel(selector)) != 0;
    public static void SetInteger(IntPtr obj, string selector, nint value) => SendInt(obj, Sel(selector), value);
    public static void SetBool(IntPtr obj, string selector, bool value) => SendBool(obj, Sel(selector), value ? (byte)1 : (byte)0);
}
