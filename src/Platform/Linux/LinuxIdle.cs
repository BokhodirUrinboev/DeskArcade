using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace DeskArcade.Platform.Linux;

/// <summary>
/// Seconds since the last keyboard or mouse input. On Xorg the MIT-SCREEN-SAVER extension (libXss) answers at once.
/// Under Wayland, XWayland only sees input to X11 apps, so the compositor is asked over D-Bus instead: GNOME's
/// org.gnome.Mutter.IdleMonitor, else org.freedesktop.ScreenSaver's GetSessionIdleTime (KDE Plasma). Those answers
/// come in the background every few seconds; until the first one arrives, or where nothing answers, it is null.
/// </summary>
internal sealed class LinuxIdle : IDisposable
{
    const string LibXss = "libXss.so.1";
    const double RefreshSeconds = 4;

    readonly bool _wayland;
    IntPtr _xss;
    bool _xssTried;
    XssQueryInfo? _query;
    XssAllocInfo? _alloc;
    IntPtr _info;

    readonly object _gate = new();
    double? _dbusSeconds;
    DateTime _dbusAt = DateTime.MinValue;
    bool _dbusBusy, _dbusGaveUp;
    Connection? _connection;
    int _source; // 0 unknown, 1 Mutter, 2 org.freedesktop.ScreenSaver

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int XssQueryInfo(IntPtr display, IntPtr drawable, IntPtr info);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr XssAllocInfo();

    public LinuxIdle(bool wayland) => _wayland = wayland;

    public double? Seconds(IntPtr display, IntPtr root)
    {
        if (!_wayland && display != IntPtr.Zero && FromXss(display, root) is double x) return x;
        return FromDBus();
    }

    /// <summary>XScreenSaverInfo on LP64: window (8), state (4), kind (4), til_or_since (8), idle (8, milliseconds), event_mask (8).</summary>
    double? FromXss(IntPtr display, IntPtr root)
    {
        if (!_xssTried)
        {
            _xssTried = true;
            try
            {
                if (NativeLibrary.TryLoad(LibXss, out _xss) &&
                    NativeLibrary.TryGetExport(_xss, "XScreenSaverQueryInfo", out IntPtr q) &&
                    NativeLibrary.TryGetExport(_xss, "XScreenSaverAllocInfo", out IntPtr a))
                {
                    _query = Marshal.GetDelegateForFunctionPointer<XssQueryInfo>(q);
                    _alloc = Marshal.GetDelegateForFunctionPointer<XssAllocInfo>(a);
                    _info = _alloc();
                }
            }
            catch (Exception)
            {
                _query = null;
            }
        }
        if (_query == null || _info == IntPtr.Zero) return null;
        try
        {
            if (_query(display, root, _info) == 0) return null;
            return (ulong)Marshal.ReadInt64(_info, 24) / 1000.0;
        }
        catch (Exception)
        {
            _query = null;
            return null;
        }
    }

    /// <summary>The last answer from the compositor (asked again in the background when it is a few seconds old).</summary>
    double? FromDBus()
    {
        lock (_gate)
        {
            if (_dbusGaveUp) return null;
            if (!_dbusBusy && (DateTime.UtcNow - _dbusAt).TotalSeconds >= RefreshSeconds)
            {
                _dbusBusy = true;
                Task.Run(RefreshAsync);
            }
            return _dbusSeconds;
        }
    }

    async Task RefreshAsync()
    {
        double? seconds = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var query = QueryAsync();
            if (await Task.WhenAny(query, Task.Delay(Timeout.Infinite, timeout.Token)).ConfigureAwait(false) == query)
                seconds = await query.ConfigureAwait(false);
            else
                _ = query.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception)
        {
            seconds = null;
        }
        lock (_gate)
        {
            _dbusBusy = false;
            _dbusAt = DateTime.UtcNow;
            _dbusSeconds = seconds;
            if (seconds == null && _source == 0) _dbusGaveUp = true; // nothing on this desktop answers: stop asking
        }
    }

    async Task<double?> QueryAsync()
    {
        if (_connection == null)
        {
            string? address = SessionBus.Address();
            if (address == null) return null;
            var connection = new Connection(address);
            await connection.ConnectAsync().ConfigureAwait(false);
            _connection = connection;
        }
        if (_source is 0 or 1)
        {
            try
            {
                ulong ms = await CallAsync(_connection, "org.gnome.Mutter.IdleMonitor", "/org/gnome/Mutter/IdleMonitor/Core",
                    "org.gnome.Mutter.IdleMonitor", "GetIdletime", static (m, _) => m.GetBodyReader().ReadUInt64()).ConfigureAwait(false);
                _source = 1;
                return ms / 1000.0;
            }
            catch (DBusException) when (_source == 0)
            {
            }
        }
        uint s = await CallAsync(_connection, "org.freedesktop.ScreenSaver", "/org/freedesktop/ScreenSaver",
            "org.freedesktop.ScreenSaver", "GetSessionIdleTime", static (m, _) => m.GetBodyReader().ReadUInt32()).ConfigureAwait(false);
        _source = 2;
        return s;
    }

    static Task<T> CallAsync<T>(Connection connection, string service, string path, string @interface, string member, MessageValueReader<T> reader)
    {
        var writer = connection.GetMessageWriter();
        MessageBuffer call;
        try
        {
            writer.WriteMethodCallHeader(service, path, @interface, member, null);
            call = writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
        return connection.CallMethodAsync(call, reader);
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
    }
}
