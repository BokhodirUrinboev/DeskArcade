using System;
using System.Runtime.InteropServices;

namespace DeskArcade.Platform.Mac;

/// <summary>CoreFoundation basics for reading the CGWindowList dictionaries.</summary>
internal static class CoreFoundation
{
    const string Lib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    const nint NumberSInt64Type = 4;  // kCFNumberSInt64Type
    const nint NumberDoubleType = 13; // kCFNumberDoubleType

    [DllImport(Lib)] public static extern void CFRelease(IntPtr cf);
    [DllImport(Lib)] public static extern nint CFArrayGetCount(IntPtr array);
    [DllImport(Lib)] public static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);
    [DllImport(Lib)] public static extern IntPtr CFDictionaryGetValue(IntPtr dictionary, IntPtr key);
    [DllImport(Lib)] static extern nuint CFGetTypeID(IntPtr cf);
    [DllImport(Lib)] static extern nuint CFNumberGetTypeID();
    [DllImport(Lib)] static extern nuint CFDictionaryGetTypeID();
    [DllImport(Lib)] static extern byte CFNumberGetValue(IntPtr number, nint type, out long value);
    [DllImport(Lib)] static extern byte CFNumberGetValue(IntPtr number, nint type, out double value);

    public static bool IsDictionary(IntPtr cf) => cf != IntPtr.Zero && CFGetTypeID(cf) == CFDictionaryGetTypeID();

    static bool IsNumber(IntPtr cf) => cf != IntPtr.Zero && CFGetTypeID(cf) == CFNumberGetTypeID();

    /// <summary>A CFNumber value of a dictionary (values are borrowed, nothing to release).</summary>
    public static bool TryGetLong(IntPtr dictionary, IntPtr key, out long value)
    {
        value = 0;
        IntPtr number = CFDictionaryGetValue(dictionary, key);
        return IsNumber(number) && CFNumberGetValue(number, NumberSInt64Type, out value) != 0;
    }

    public static bool TryGetDouble(IntPtr dictionary, IntPtr key, out double value)
    {
        value = 0;
        IntPtr number = CFDictionaryGetValue(dictionary, key);
        return IsNumber(number) && CFNumberGetValue(number, NumberDoubleType, out value) != 0;
    }
}
