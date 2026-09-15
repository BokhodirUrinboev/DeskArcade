using System;
using System.Runtime.InteropServices;
using System.Text;

namespace DeskArcade.Platform.Windows;

internal static class Win32
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_APPWINDOW = 0x00040000;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const uint LWA_ALPHA = 0x2;

    public const uint WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_NOREPEAT = 0x4000;
    public static readonly IntPtr HWND_MESSAGE = new(-3);

    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public const int DWMWA_CLOAKED = 14;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int index, int value);
    [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint colorKey, byte alpha, uint flags);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT r, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int v, int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string? name);

    public static bool TryGetFrameBounds(IntPtr hWnd, out RECT r)
    {
        if (DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf<RECT>()) == 0)
            return true;
        return GetWindowRect(hWnd, out r);
    }

    public static bool IsCloaked(IntPtr hWnd) =>
        DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int v, sizeof(int)) == 0 && v != 0;

    public static string ClassName(IntPtr hWnd)
    {
        var sb = new StringBuilder(128);
        GetClassName(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    // ---- waveOut ----
    public const int WAVE_MAPPER = -1;
    public const int WHDR_DONE = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    public struct WAVEFORMATEX
    {
        public ushort wFormatTag, nChannels;
        public uint nSamplesPerSec, nAvgBytesPerSec;
        public ushort nBlockAlign, wBitsPerSample, cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength, dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags, dwLoops;
        public IntPtr lpNext, reserved;
    }

    [DllImport("winmm.dll")] public static extern int waveOutOpen(out IntPtr hwo, int device, ref WAVEFORMATEX fmt, IntPtr cb, IntPtr inst, int flags);
    [DllImport("winmm.dll")] public static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr hdr, int size);
    [DllImport("winmm.dll")] public static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr hdr, int size);
    [DllImport("winmm.dll")] public static extern int waveOutWrite(IntPtr hwo, IntPtr hdr, int size);
    [DllImport("winmm.dll")] public static extern int waveOutReset(IntPtr hwo);
    [DllImport("winmm.dll")] public static extern int waveOutClose(IntPtr hwo);
}
