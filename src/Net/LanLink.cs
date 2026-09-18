using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DeskArcade.Net;

public enum LanRole { None, Host, Guest }

public enum LanState { Off, Waiting, Connected }

/// <summary>
/// A two-player link over the local network: no server, no account. A host listens on UDP port
/// <see cref="Port"/>; a guest broadcasts "hello" and pairs with the first host that answers. After that,
/// games exchange short text messages ("kind|payload") with <see cref="Send"/> and drain them with
/// <see cref="TryReceive"/>. Silence for <see cref="TimeoutSeconds"/> drops the link.
/// Events fire on a background thread; the overlay marshals them to the UI thread.
/// </summary>
public sealed class LanLink : IDisposable
{
    public const int Port = 47820;
    const string Magic = "DA1";
    const double TimeoutSeconds = 3, PingSeconds = 0.5, HelloSeconds = 0.7;

    readonly ConcurrentQueue<string> _inbox = new();
    readonly object _gate = new();
    UdpClient? _udp;
    CancellationTokenSource? _cts;
    IPEndPoint? _peer;
    DateTime _lastHeard, _lastSent;

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

    public void Join()
    {
        Stop();
        var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        Begin(udp, LanRole.Guest, "");
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

        if (kind == "hello" && Role == LanRole.Host)
        {
            // first guest wins; the same guest re-sending hello (lost welcome) is answered again
            if (_peer == null || _peer.Equals(from))
            {
                lock (_gate) _peer = from;
                PeerName = body;
                TrySend(udp, from, $"welcome|{GameId}|{MyName}");
                Heard();
            }
            else TrySend(udp, from, "busy|");
            return;
        }
        if (kind == "welcome" && Role == LanRole.Guest && State == LanState.Waiting)
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
        _inbox.Enqueue(msg);
        MessageArrived?.Invoke();
    }

    void Heard()
    {
        _lastHeard = DateTime.UtcNow;
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
                TrySend(udp, broadcast, hello);
                TrySend(udp, loopback, hello);
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
