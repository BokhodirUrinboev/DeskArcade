using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DeskArcade.Net;

public enum LanRole { None, Host, Guest }

public enum LanState { Off, Waiting, Connected }

/// <summary>A host found on the network by <see cref="LanLink.FindHosts"/>.</summary>
public sealed record LanHost(IPEndPoint Address, string Name, string GameId, bool Busy);

/// <summary>
/// A two-player link over the local network: no server, no account. A host listens on UDP port
/// <see cref="Port"/>; a guest broadcasts "hello" and pairs with the first host that answers. After that,
/// games exchange short text messages ("kind|payload") with <see cref="Send"/> and drain them with
/// <see cref="TryReceive"/>. Silence for <see cref="TimeoutSeconds"/> drops the link. Hosts also answer
/// "find" probes so a lobby can list them, and emotes travel beside the game messages.
/// Events fire on a background thread; the overlay marshals them to the UI thread.
/// </summary>
public sealed class LanLink : IDisposable
{
    public const int Port = 47820;
    const string Magic = "DA1";
    const double TimeoutSeconds = 3, PingSeconds = 0.5, HelloSeconds = 0.7, GameSeconds = 1;
    const int MaxInbox = 2000; // while the overlay is hidden nothing drains the inbox: keep only the latest

    readonly ConcurrentQueue<string> _inbox = new();
    readonly object _gate = new();
    UdpClient? _udp;
    CancellationTokenSource? _cts;
    IPEndPoint? _peer, _target; // _target: the one host a guest asked to join, or null for the first to answer
    DateTime _lastHeard, _lastSent, _lastGameSent;

    public LanRole Role { get; private set; }
    public LanState State { get; private set; }
    /// <summary>Game the session is for; the guest adopts the host's.</summary>
    public string GameId { get; private set; } = "";
    public string PeerName { get; private set; } = "";
    public bool Connected => State == LanState.Connected;

    /// <summary>Raised when <see cref="State"/> changes.</summary>
    public event Action? StateChanged;
    /// <summary>Raised whenever a game message arrives (so the overlay can wake up and render).</summary>
    public event Action? MessageArrived;
    /// <summary>Raised when the peer sends an emote (an index into <see cref="Emotes"/>).</summary>
    public event Action<int>? EmoteReceived;
    /// <summary>Raised on the guest when the host switches to another game.</summary>
    public event Action<string>? GameChanged;
    /// <summary>
    /// Raised when the peer reports something it did, so it can be drawn as a ghost marker: where (as
    /// fractions of the peer's arena) and how many points it scored (0 for a miss, negative for a penalty).
    /// </summary>
    public event Action<double, double, int>? ActionReceived;

    /// <summary>Counts connections, so games can tell a new session from the one they already set up.</summary>
    public int Session { get; private set; }

    /// <summary>Short messages players can send each other. Only the index crosses the network.</summary>
    public static readonly string[] Emotes = { "gg", "One more?", "Nice shot!", "Your move!", "Ha!" };

    public static string MyName => Environment.UserName is { Length: > 0 } u ? u : Environment.MachineName;

    public void Host(string gameId)
    {
        Stop();
        try
        {
            var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
            Begin(udp, LanRole.Host, gameId);
        }
        catch (SocketException)
        {
            SetState(LanState.Off); // port taken: another copy is already hosting on this machine
        }
    }

    /// <summary>Joins <paramref name="target"/>, or the first host on the network to answer.</summary>
    public void Join(IPEndPoint? target = null)
    {
        Stop();
        var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        _target = target;
        Begin(udp, LanRole.Guest, "");
    }

    /// <summary>"192.168.1.20" or "192.168.1.20:47820"; host names work too.</summary>
    public static IPEndPoint? ParseAddress(string text)
    {
        text = text.Trim();
        int port = Port;
        int colon = text.LastIndexOf(':');
        if (colon > 0 && int.TryParse(text[(colon + 1)..], out int p) && p is > 0 and < 65536)
        {
            port = p;
            text = text[..colon];
        }
        if (IPAddress.TryParse(text, out var ip)) return new IPEndPoint(ip, port);
        try
        {
            var found = Dns.GetHostAddresses(text).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            return found == null ? null : new IPEndPoint(found, port);
        }
        catch (SocketException)
        {
            return null;
        }
    }

    /// <summary>Broadcasts a probe and collects the hosts that answer within <paramref name="wait"/>.</summary>
    public static async Task<List<LanHost>> FindHosts(TimeSpan wait)
    {
        var hosts = new Dictionary<string, LanHost>();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        TrySend(udp, new IPEndPoint(IPAddress.Broadcast, Port), "find|");
        TrySend(udp, new IPEndPoint(IPAddress.Loopback, Port), "find|");
        using var cts = new CancellationTokenSource(wait);
        try
        {
            while (true)
            {
                var r = await udp.ReceiveAsync(cts.Token);
                var f = Encoding.UTF8.GetString(r.Buffer).Split('|');
                if (f.Length == 5 && f[0] == Magic && f[1] == "here")
                    hosts[f[3] + "@" + r.RemoteEndPoint.Address] = new LanHost(r.RemoteEndPoint, f[3], f[2], f[4] == "1");
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        // the same host can answer on loopback and on the LAN; keep one of each name
        return hosts.Values.GroupBy(h => h.Name).Select(g => g.OrderBy(h => IPAddress.IsLoopback(h.Address.Address)).First()).ToList();
    }

    public void SendEmote(int index) => Send($"em|{index}");

    /// <summary>Tells the peer about a click, pop or whack at (<paramref name="x"/>, <paramref name="y"/>), fractions of our arena.</summary>
    public void SendAction(double x, double y, int points) =>
        Send(string.Create(CultureInfo.InvariantCulture, $"ga|{x:0.####}|{y:0.####}|{points}"));

    /// <summary>
    /// Host: the game the session is for. A connected guest is told at once; a guest that joins later
    /// gets it in the welcome. The host also repeats it every <see cref="GameSeconds"/>, since a single
    /// UDP message can be lost.
    /// </summary>
    public void SendGame(string gameId)
    {
        GameId = gameId;
        Send($"gm|{gameId}");
        _lastGameSent = DateTime.UtcNow;
    }

    /// <summary>This PC's IPv4 addresses on the local network, for "join by address".</summary>
    public static string LocalAddresses()
    {
        try
        {
            var ips = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                            n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                .Select(a => a.ToString());
            return string.Join(", ", ips.Distinct());
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            return "";
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_udp != null && _peer != null) TrySend(_udp, _peer, "bye|");
            _cts?.Cancel();
            _udp?.Dispose();
            _udp = null;
            _cts = null;
            _peer = null;
            _target = null;
            Role = LanRole.None;
            PeerName = "";
            while (_inbox.TryDequeue(out _)) { }
        }
        SetState(LanState.Off);
    }

    /// <summary>Sends one game message ("kind|payload") to the peer, if connected.</summary>
    public void Send(string message)
    {
        UdpClient? udp;
        IPEndPoint? peer;
        lock (_gate)
        {
            udp = _udp;
            peer = State == LanState.Connected ? _peer : null;
        }
        if (udp != null && peer != null && TrySend(udp, peer, message)) _lastSent = DateTime.UtcNow;
    }

    public bool TryReceive(out string message) => _inbox.TryDequeue(out message!);

    void Begin(UdpClient udp, LanRole role, string gameId)
    {
        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _udp = udp;
            _cts = cts;
            Role = role;
            GameId = gameId;
        }
        SetState(LanState.Waiting);
        Task.Run(() => ReceiveLoop(udp, cts.Token));
        Task.Run(() => TimerLoop(udp, cts.Token));
    }

    async Task ReceiveLoop(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await udp.ReceiveAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; } // e.g. ICMP "port unreachable" from a vanished peer

            var text = Encoding.UTF8.GetString(r.Buffer);
            if (!text.StartsWith(Magic + "|", StringComparison.Ordinal)) continue;
            Handle(udp, r.RemoteEndPoint, text[(Magic.Length + 1)..]);
        }
    }

    void Handle(UdpClient udp, IPEndPoint from, string msg)
    {
        int bar = msg.IndexOf('|');
        string kind = bar < 0 ? msg : msg[..bar], body = bar < 0 ? "" : msg[(bar + 1)..];

        if (kind == "find" && Role == LanRole.Host)
        {
            TrySend(udp, from, $"here|{GameId}|{MyName}|{(State == LanState.Connected ? 1 : 0)}");
            return;
        }
        if (kind == "hello" && Role == LanRole.Host)
        {
            // first guest wins; the same guest re-sending hello (lost welcome) is answered again
            if (_peer == null || _peer.Equals(from))
            {
                // the guest we are playing with timed out on its side and joined again: it starts its games
                // fresh, so this is a new session here too, or the two sides' duels and races fall out of step
                bool rejoined = State == LanState.Connected && _peer != null;
                lock (_gate) _peer = from;
                PeerName = body;
                TrySend(udp, from, $"welcome|{GameId}|{MyName}");
                Heard();
                if (rejoined)
                {
                    Session++;
                    while (_inbox.TryDequeue(out _)) { }
                    StateChanged?.Invoke();
                }
            }
            else TrySend(udp, from, "busy|");
            return;
        }
        if (kind == "welcome" && Role == LanRole.Guest && State == LanState.Waiting && (_target == null || _target.Address.Equals(from.Address)))
        {
            var parts = body.Split('|', 2);
            lock (_gate) _peer = from;
            GameId = parts[0];
            PeerName = parts.Length > 1 ? parts[1] : "";
            Heard();
            return;
        }
        if (_peer == null || !_peer.Equals(from)) return;

        if (kind == "bye")
        {
            lock (_gate) _peer = Role == LanRole.Host ? null : _peer;
            if (Role == LanRole.Host) SetState(LanState.Waiting); // keep hosting for the next guest
            else Stop();
            return;
        }
        _lastHeard = DateTime.UtcNow;
        if (kind == "ping") return;
        if (kind == "gm" && Role == LanRole.Guest)
        {
            if (body.Length == 0 || body == GameId) return; // the host's periodic reminder: nothing changed
            GameId = body;
            GameChanged?.Invoke(body);
            return;
        }
        if (kind == "ga")
        {
            var f = body.Split('|');
            if (f.Length == 3 && double.TryParse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) &&
                double.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y) && int.TryParse(f[2], out int pts) &&
                x is >= 0 and <= 1 && y is >= 0 and <= 1)
                ActionReceived?.Invoke(x, y, pts);
            return;
        }
        if (kind == "em")
        {
            if (int.TryParse(body, out int emote) && emote >= 0 && emote < Emotes.Length) EmoteReceived?.Invoke(emote);
            return;
        }
        _inbox.Enqueue(msg);
        while (_inbox.Count > MaxInbox) _inbox.TryDequeue(out _);
        MessageArrived?.Invoke();
    }

    void Heard()
    {
        _lastHeard = DateTime.UtcNow;
        if (State != LanState.Connected) Session++;
        SetState(LanState.Connected);
    }

    async Task TimerLoop(UdpClient udp, CancellationToken ct)
    {
        var broadcast = new IPEndPoint(IPAddress.Broadcast, Port);
        var loopback = new IPEndPoint(IPAddress.Loopback, Port); // a second --profile copy on this machine
        string hello = $"hello|{MyName}";
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(0.25), ct); }
            catch (OperationCanceledException) { return; }

            var now = DateTime.UtcNow;
            if (State == LanState.Waiting && Role == LanRole.Guest && (now - _lastSent).TotalSeconds >= HelloSeconds)
            {
                if (_target != null) TrySend(udp, _target, hello);
                else
                {
                    TrySend(udp, broadcast, hello);
                    TrySend(udp, loopback, hello);
                }
                _lastSent = now;
            }
            else if (State == LanState.Connected)
            {
                if ((now - _lastHeard).TotalSeconds > TimeoutSeconds)
                {
                    // the peer went quiet: a host waits for the next guest, a guest keeps looking for a host
                    lock (_gate) _peer = null;
                    SetState(LanState.Waiting);
                }
                else if (Role == LanRole.Host && (now - _lastGameSent).TotalSeconds >= GameSeconds) SendGame(GameId);
                else if ((now - _lastSent).TotalSeconds >= PingSeconds) Send("ping|");
            }
        }
    }

    static bool TrySend(UdpClient udp, IPEndPoint to, string message)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(Magic + "|" + message);
            udp.Send(bytes, bytes.Length, to);
            return true;
        }
        catch
        {
            return false;
        }
    }

    void SetState(LanState s)
    {
        if (State == s) return;
        State = s;
        StateChanged?.Invoke();
    }

    public void Dispose() => Stop();
}
