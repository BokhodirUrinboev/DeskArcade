using System;
using System.Runtime.InteropServices;

namespace DeskArcade.Platform.Linux;

/// <summary>The handful of Xlib / XShape calls the overlay needs (64-bit Linux).</summary>
internal static class X11
{
    const string LibX11 = "libX11.so.6";
    const string LibXext = "libXext.so.6";

    public const int ShapeInput = 2;
    public const int ShapeSet = 0;
    public const int Unsorted = 0;
    public const int KeyPress = 2;
    public const int GrabModeAsync = 1;
    public const long InputHint = 1;
    public const uint LockMask = 1 << 1;
    public const uint ControlMask = 1 << 2;
    public const uint Mod1Mask = 1 << 3; // Alt
    public const uint Mod2Mask = 1 << 4; // NumLock
    public const int KeyEventKeycodeOffset = 84; // XKeyEvent.keycode on LP64

    [StructLayout(LayoutKind.Sequential)]
    public struct XRectangle
    {
        public short X, Y;
        public ushort Width, Height;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int XErrorHandler(IntPtr display, IntPtr errorEvent);

    [DllImport(LibX11)] public static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport(LibX11)] public static extern int XCloseDisplay(IntPtr display);
    [DllImport(LibX11)] public static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport(LibX11)] public static extern int XFlush(IntPtr display);
    [DllImport(LibX11)] public static extern int XSync(IntPtr display, bool discard);
    [DllImport(LibX11)] public static extern IntPtr XSetErrorHandler(XErrorHandler handler);
    [DllImport(LibX11)] public static extern IntPtr XInternAtom(IntPtr display, string name, bool onlyIfExists);
    [DllImport(LibX11)] public static extern int XFree(IntPtr data);

    [DllImport(LibX11)]
    public static extern int XGetWindowProperty(IntPtr display, IntPtr window, IntPtr property, IntPtr offset, IntPtr length,
        bool delete, IntPtr requestedType, out IntPtr actualType, out int actualFormat, out IntPtr itemCount,
        out IntPtr bytesAfter, out IntPtr data);

    [DllImport(LibX11)]
    public static extern int XGetGeometry(IntPtr display, IntPtr drawable, out IntPtr root, out int x, out int y,
        out uint width, out uint height, out uint border, out uint depth);

    [DllImport(LibX11)]
    public static extern bool XTranslateCoordinates(IntPtr display, IntPtr src, IntPtr dest, int srcX, int srcY,
        out int destX, out int destY, out IntPtr child);

    [DllImport(LibX11)]
    public static extern bool XQueryPointer(IntPtr display, IntPtr window, out IntPtr root, out IntPtr child,
        out int rootX, out int rootY, out int winX, out int winY, out uint mask);

    [DllImport(LibX11)] public static extern IntPtr XGetWMHints(IntPtr display, IntPtr window);
    [DllImport(LibX11)] public static extern IntPtr XAllocWMHints();
    [DllImport(LibX11)] public static extern int XSetWMHints(IntPtr display, IntPtr window, IntPtr hints);
    [DllImport(LibX11)] public static extern int XGetWMProtocols(IntPtr display, IntPtr window, out IntPtr protocols, out int count);
    [DllImport(LibX11)] public static extern int XSetWMProtocols(IntPtr display, IntPtr window, IntPtr[] protocols, int count);

    [DllImport(LibX11)] public static extern byte XKeysymToKeycode(IntPtr display, IntPtr keysym);
    [DllImport(LibX11)] public static extern int XGrabKey(IntPtr display, int keycode, uint modifiers, IntPtr grabWindow, bool ownerEvents, int pointerMode, int keyboardMode);
    [DllImport(LibX11)] public static extern int XUngrabKey(IntPtr display, int keycode, uint modifiers, IntPtr grabWindow);
    [DllImport(LibX11)] public static extern int XPending(IntPtr display);
    [DllImport(LibX11)] public static extern int XNextEvent(IntPtr display, IntPtr eventBuffer);

    [DllImport(LibXext)]
    public static extern void XShapeCombineRectangles(IntPtr display, IntPtr window, int destKind, int xOffset, int yOffset,
        XRectangle[] rectangles, int count, int operation, int ordering);
}
