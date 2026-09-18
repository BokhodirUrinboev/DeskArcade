using System;
using System.Runtime.InteropServices;

namespace DeskArcade.Platform.Mac;

/// <summary>Carbon global hotkeys (still the supported way to get a system-wide shortcut without extra permissions).</summary>
internal static class Carbon
{
    const string Lib = "/System/Library/Frameworks/Carbon.framework/Carbon";

    public const int NoErr = 0;
    public const int EventNotHandledErr = -9874;

    public const uint EventClassKeyboard = 0x6B657962;     // 'keyb'
    public const uint EventHotKeyPressed = 5;              // kEventHotKeyPressed
    public const uint EventParamDirectObject = 0x2D2D2D2D; // '----'
    public const uint TypeEventHotKeyId = 0x686B6964;      // 'hkid'

    public const uint OptionKey = 1 << 11;
    public const uint ControlKey = 1 << 12;

    // kVK_ANSI_* virtual key codes: physical key positions on an ANSI (US) keyboard
    public const uint ShiftKey = 1 << 9;

    /// <summary>The ANSI virtual key code (kVK_ANSI_*) of a letter; they follow the keyboard layout, not the alphabet.</summary>
    public static uint KeyCode(char letter) => "ASDFHGZXCV?BQWERYT?????????????OU?IP?LJ?K????NM".IndexOf(char.ToUpperInvariant(letter)) is int i and >= 0
        ? (uint)i
        : 0x05; // G

    [StructLayout(LayoutKind.Sequential)]
    public struct EventTypeSpec
    {
        public uint EventClass;
        public uint EventKind;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EventHotKeyID
    {
        public uint Signature;
        public uint Id;
    }

    [DllImport(Lib)] public static extern IntPtr GetApplicationEventTarget();

    [DllImport(Lib)]
    public static extern int InstallEventHandler(IntPtr target, IntPtr handler, nuint typeCount, ref EventTypeSpec types,
        IntPtr userData, out IntPtr handlerRef);

    [DllImport(Lib)] public static extern int RemoveEventHandler(IntPtr handlerRef);

    [DllImport(Lib)]
    public static extern int RegisterEventHotKey(uint keyCode, uint modifiers, EventHotKeyID id, IntPtr target, uint options,
        out IntPtr hotKeyRef);

    [DllImport(Lib)] public static extern int UnregisterEventHotKey(IntPtr hotKeyRef);

    [DllImport(Lib)]
    public static extern int GetEventParameter(IntPtr carbonEvent, uint name, uint desiredType, IntPtr actualType,
        nuint bufferSize, IntPtr actualSize, out EventHotKeyID data);
}
