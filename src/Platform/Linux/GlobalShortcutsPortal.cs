using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace DeskArcade.Platform.Linux;

/// <summary>
/// Ctrl+Alt+G/N/B on Wayland through the XDG Desktop Portal GlobalShortcuts interface (GNOME 48+, KDE Plasma):
/// the compositor asks the user once to confirm the shortcuts, then reports each press as an Activated signal.
/// Best effort throughout: no session bus, no portal, no GlobalShortcuts backend or a declined dialog
/// just make <see cref="StartAsync"/> return false.
/// </summary>
public sealed class GlobalShortcutsPortal : IDisposable
{
    const string Service = "org.freedesktop.portal.Desktop";
    const string DesktopPath = "/org/freedesktop/portal/desktop";
    const string ShortcutsInterface = "org.freedesktop.portal.GlobalShortcuts";
    const string RequestInterface = "org.freedesktop.portal.Request";
    const string SessionInterface = "org.freedesktop.portal.Session";
    const string RegistryInterface = "org.freedesktop.host.portal.Registry";
    const string AppId = "deskarcade"; // basename of the installed deskarcade.desktop

    readonly (string Id, HotkeyAction Action, string Description, string Trigger)[] Shortcuts;

    delegate void BodyWriter(ref MessageWriter writer);

    readonly Action<HotkeyAction> _onActivated;
    readonly string? _address;
    readonly CancellationTokenSource _cts = new();
    readonly object _gate = new();
    Connection? _connection;
    volatile string? _session;
    bool _disposed;

    /// <param name="onActivated">Runs on a D-Bus thread for every press; post to the UI thread from there.</param>
    /// <param name="address">D-Bus address of the session bus; null finds it the usual way.</param>
    public GlobalShortcutsPortal(Action<HotkeyAction> onActivated, HotkeySet? keys = null, string? address = null)
    {
        _onActivated = onActivated;
        keys ??= HotkeySet.Default;
        Shortcuts = new[]
        {
            ("toggle", HotkeyAction.ToggleOverlay, "Show or hide the overlay", keys.PortalTrigger(HotkeyAction.ToggleOverlay)),
            ("next", HotkeyAction.NextGame, "Next game", keys.PortalTrigger(HotkeyAction.NextGame)),
            ("summon", HotkeyAction.Summon, "Bring the ball, bow, paddle or mallet to the cursor", keys.PortalTrigger(HotkeyAction.Summon)),
        };
        _address = address;
    }

    /// <summary>
    /// Creates a portal session and binds the shortcuts, which waits for as long as the user leaves the dialog open.
    /// Never throws. True once the shortcuts are bound; on false the portal has already shut itself down.
    /// </summary>
    public async Task<bool> StartAsync()
    {
        try
        {
            string? address = _address ?? SessionBus.Address();
            if (address == null) return await FailAsync().ConfigureAwait(false);
            var connection = new Connection(address);
            lock (_gate)
            {
                if (_disposed || _connection != null)
                {
                    connection.Dispose();
                    return false;
                }
                _connection = connection;
            }
            await connection.ConnectAsync().ConfigureAwait(false);
            string sender = connection.UniqueName!.TrimStart(':').Replace('.', '_');

            await RegisterAppAsync(connection).ConfigureAwait(false);
            await connection.AddMatchAsync(
                new MatchRule { Type = MessageType.Signal, Sender = Service, Path = DesktopPath, Interface = ShortcutsInterface, Member = "Activated" },
                static (message, _) => ReadActivated(message),
                (error, activated, _, _) => OnActivated(error, activated),
                flags: ObserverFlags.None, emitOnCapturedContext: false).ConfigureAwait(false);

            string sessionToken = NewToken();
            var created = await RequestAsync(connection, sender, token => MethodCall(connection, DesktopPath, ShortcutsInterface, "CreateSession", "a{sv}",
                (ref MessageWriter writer) => WriteOptions(ref writer, ("handle_token", token), ("session_handle_token", sessionToken)))).ConfigureAwait(false);
            if (created == null || !created.TryGetValue("session_handle", out VariantValue handle)) return await FailAsync().ConfigureAwait(false);
            string session = handle.Type == VariantValueType.ObjectPath ? handle.GetObjectPathAsString() : handle.GetString(); // "s" in the spec, "o" in spirit
            lock (_gate) _session = session;

            var bound = await RequestAsync(connection, sender, token => MethodCall(connection, DesktopPath, ShortcutsInterface, "BindShortcuts", "oa(sa{sv})sa{sv}",
                (ref MessageWriter writer) =>
                {
                    writer.WriteObjectPath(session);
                    ArrayStart list = writer.WriteArrayStart(DBusType.Struct);
                    foreach (var shortcut in Shortcuts)
                    {
                        writer.WriteStructureStart();
                        writer.WriteString(shortcut.Id);
                        WriteOptions(ref writer, ("description", shortcut.Description), ("preferred_trigger", shortcut.Trigger));
                    }
                    writer.WriteArrayEnd(list);
                    writer.WriteString(""); // parent_window: none
                    WriteOptions(ref writer, ("handle_token", token));
                })).ConfigureAwait(false);
            // null: the user cancelled the dialog; an empty list: they left every shortcut unassigned
            if (bound == null || (bound.TryGetValue("shortcuts", out VariantValue list) && list.Type == VariantValueType.Array && list.Count == 0))
                return await FailAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            return await FailAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Calls a portal method that answers through a Request object and returns the Response results, or null unless
    /// the response is 0 (success). Listens on the expected request path before calling, as the Request docs ask.
    /// </summary>
    async Task<Dictionary<string, VariantValue>?> RequestAsync(Connection connection, string sender, Func<string, MessageBuffer> createCall)
    {
        string token = NewToken();
        string expected = $"{DesktopPath}/request/{sender}/{token}";
        var response = new TaskCompletionSource<(uint Code, Dictionary<string, VariantValue> Results)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = await ListenForResponseAsync(connection, expected, response).ConfigureAwait(false);
        string handle = await connection.CallMethodAsync(createCall(token), static (message, _) => message.GetBodyReader().ReadObjectPathAsString()).ConfigureAwait(false);
        // portals older than 0.9 chose their own request path
        using var moved = handle == expected ? null : await ListenForResponseAsync(connection, handle, response).ConfigureAwait(false);
        var (code, results) = await response.Task.WaitAsync(_cts.Token).ConfigureAwait(false);
        return code == 0 ? results : null;
    }

    static ValueTask<IDisposable> ListenForResponseAsync(Connection connection, string path,
        TaskCompletionSource<(uint Code, Dictionary<string, VariantValue> Results)> response) =>
        connection.AddMatchAsync(
            new MatchRule { Type = MessageType.Signal, Sender = Service, Path = path, Interface = RequestInterface, Member = "Response" },
            static (message, _) => ReadResponse(message),
            static (error, value, _, state) =>
            {
                var pending = (TaskCompletionSource<(uint Code, Dictionary<string, VariantValue> Results)>)state!;
                if (error != null) pending.TrySetException(error);
                else pending.TrySetResult(value);
            },
            flags: ObserverFlags.EmitOnConnectionDispose, handlerState: response, emitOnCapturedContext: false);

    // Signal readers run inside Tmds.DBus, where an exception drops the whole connection: check the signature first.
    static (uint Code, Dictionary<string, VariantValue> Results) ReadResponse(Message message)
    {
        if (message.SignatureAsString != "ua{sv}") return (2, new Dictionary<string, VariantValue>());
        var reader = message.GetBodyReader();
        return (reader.ReadUInt32(), reader.ReadDictionaryOfStringToVariantValue());
    }

    static (string Session, string Id) ReadActivated(Message message)
    {
        if (message.SignatureAsString != "osta{sv}") return ("", "");
        var reader = message.GetBodyReader();
        return (reader.ReadObjectPathAsString(), reader.ReadString());
    }

    void OnActivated(Exception? error, (string Session, string Id) activated)
    {
        if (error != null || activated.Session != _session) return;
        foreach (var shortcut in Shortcuts)
            if (shortcut.Id == activated.Id) _onActivated(shortcut.Action);
    }

    /// <summary>
    /// Unsandboxed apps have no app id; registering one lets the portal name Desk Arcade in its dialog and keep the
    /// bindings under it. It has to precede the other portal calls. Older portals lack the Registry and it may refuse
    /// an id it cannot match to a .desktop file: carry on without one then.
    /// </summary>
    static async Task RegisterAppAsync(Connection connection)
    {
        try
        {
            await connection.CallMethodAsync(MethodCall(connection, DesktopPath, RegistryInterface, "Register", "sa{sv}",
                static (ref MessageWriter writer) =>
                {
                    writer.WriteString(AppId);
                    WriteOptions(ref writer);
                })).ConfigureAwait(false);
        }
        catch (DBusException)
        {
        }
    }

    static MessageBuffer MethodCall(Connection connection, string path, string @interface, string member, string? signature, BodyWriter body)
    {
        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(Service, path, @interface, member, signature);
            body(ref writer);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>An a{sv} with string values.</summary>
    static void WriteOptions(ref MessageWriter writer, params (string Key, string Value)[] options)
    {
        ArrayStart dict = writer.WriteDictionaryStart();
        foreach (var (key, value) in options)
        {
            writer.WriteDictionaryEntryStart();
            writer.WriteString(key);
            writer.WriteVariantString(value);
        }
        writer.WriteDictionaryEnd(dict);
    }

    /// <summary>A request or session token: an object path element, unique enough for one connection.</summary>
    static string NewToken() => "deskarcade" + Random.Shared.Next().ToString("x8");

    async Task<bool> FailAsync()
    {
        await ShutdownAsync().ConfigureAwait(false);
        return false;
    }

    /// <summary>Closes the portal session, waiting at most 300 ms for the reply, then the connection. Runs once.</summary>
    async Task ShutdownAsync()
    {
        Connection? connection;
        string? session;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            connection = _connection;
            session = _session;
            _connection = null;
            _session = null;
        }
        _cts.Cancel();
        if (connection == null) return;
        if (session != null)
        {
            try
            {
                Task close = connection.CallMethodAsync(MethodCall(connection, session, SessionInterface, "Close", null, static (ref MessageWriter _) => { }));
                await Task.WhenAny(close, Task.Delay(300)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // the connection is already gone, and the portal closes the session along with it
            }
        }
        connection.Dispose();
    }

    public void Dispose() => ShutdownAsync().Wait();
}

/// <summary>The session bus without a portal: where it is, and whether a well-known name is currently owned.</summary>
internal static class SessionBus
{
    const string BusService = "org.freedesktop.DBus";
    const string BusPath = "/org/freedesktop/DBus";

    /// <summary>The session bus address, or null when the session has none.</summary>
    public static string? Address()
    {
        if (Tmds.DBus.Protocol.Address.Session is { Length: > 0 } address) return address; // qualified: Address() above shadows the type
        string? runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (runtimeDir is not { Length: > 0 }) return null;
        string socket = Path.Combine(runtimeDir, "bus");
        return File.Exists(socket) ? "unix:path=" + socket : null;
    }

    /// <summary>
    /// Asks the bus driver whether <paramref name="name"/> has an owner. Null when there is no session bus or the
    /// bus did not answer within <paramref name="timeout"/>; throws only on a broken connection. Inside a Flatpak the
    /// proxy answers for names the manifest may talk to, so no extra permission is needed for a --talk-name.
    /// </summary>
    public static async Task<bool?> NameHasOwnerAsync(string name, TimeSpan timeout)
    {
        string? address = Address();
        if (address == null) return null;
        using var connection = new Connection(address);
        Task<bool> query = QueryAsync(connection, name);
        if (await Task.WhenAny(query, Task.Delay(timeout)).ConfigureAwait(false) != query)
        {
            _ = query.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); // disposing the connection fails it
            return null;
        }
        return await query.ConfigureAwait(false);
    }

    static async Task<bool> QueryAsync(Connection connection, string name)
    {
        await connection.ConnectAsync().ConfigureAwait(false);
        var writer = connection.GetMessageWriter();
        MessageBuffer call;
        try
        {
            writer.WriteMethodCallHeader(BusService, BusPath, BusService, "NameHasOwner", "s");
            writer.WriteString(name);
            call = writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
        return await connection.CallMethodAsync(call, static (message, _) => message.GetBodyReader().ReadBool()).ConfigureAwait(false);
    }
}
