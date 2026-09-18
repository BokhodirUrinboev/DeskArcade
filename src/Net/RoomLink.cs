using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DeskArcade.Net;

public enum RoomState { Off, Hosting, Joining, Joined, Lost }

/// <summary>A room found on the network by <see cref="RoomLink.FindRooms"/>.</summary>
public sealed record RoomInfo(string Code, string Host, int Players, int Seats, bool Open, IPEndPoint Address);

/// <summary>One seat in a room: 0 is the host.</summary>
public sealed record RoomSeat(int Seat, string Name, bool Connected);

/// <summary>
/// A room for up to <see cref="MaxSeats"/> players on the local network, separate from the two-player
/// <see cref="LanLink"/>. The host picks a short room code; players join by the code (or pick the room from
/// a list), so several rooms can run on one network. The host relays everything: guests only talk to the
/// host. A small listener on UDP <see cref="Port"/> (shared by every room on a PC) answers "find" and
/// "join"; the room itself runs on its own socket.
/// Wire format (after "DA1|"): rfind; rhere|code|host|players|seats|open; rjoin|code|name|id (the random
/// id makes the broadcast and loopback copies of one join count once); rok|code|seat; rno|code|reason;
/// rr|code|name,name,…|connected flags (the roster, also the host's heartbeat); rg|code|seat|body
/// (guest → host); rh|code|body (host → guest); rbye|code|seat; rend|code.
/// </summary>
public sealed class RoomLink : IDisposable
{
    public const int Port = 47822, MaxSeats = 4;
    const string Magic = "DA1";
    const double TimeoutSeconds = 6;
    const string CodeLetters = "ABCDEFGHJKLMNPQRSTUVWXYZ"; // no I or O, which look like 1 and 0

    sealed class Guest
    {
        public required IPEndPoint Address;
        public required string Name;
        /// <summary>The guest's random id for this join: a join arrives both by broadcast and by loopback, from different addresses.</summary>
        public required string Nonce;
        public DateTime Heard;
        public bool Connected = true;
    }

    readonly object _gate = new();
    readonly ConcurrentQueue<(int Seat, string Body)> _inbox = new();
    readonly Dictionary<int, Guest> _guests = new();
    UdpClient? _udp, _listener;
    CancellationTokenSource? _cts;
    IPEndPoint? _host;
    IPEndPoint? _target;
    string _nonce = "";
    string[] _roster = Array.Empty<string>();
    bool[] _connected = Array.Empty<bool>();
    DateTime _lastHeard;

    public RoomState State { get; private set; }
    public string Code { get; private set; } = "";
    public int MySeat { get; private set; } = -1;
    public bool IsHost => State == RoomState.Hosting;
    /// <summary>Hosts accept new players only while open; closing happens when the game starts.</summary>
    public bool Open { get; set; } = true;
    public string MyName { get; set; } = LanLink.MyName;

    /// <summary>Raised on a background thread when the state or the roster changes.</summary>
    public event Action? Changed;
    /// <summary>Raised on a background thread when a game message arrives.</summary>
    public event Action? MessageArrived;

    /// <summary>Everyone in the room, by seat (the host is seat 0).</summary>
    public List<RoomSeat> Seats()
    {
        lock (_gate)
        {
            if (IsHost)
            {
                var list = new List<RoomSeat> { new(0, MyName, true) };
                list.AddRange(_guests.OrderBy(g => g.Key).Select(g => new RoomSeat(g.Key, g.Value.Name, g.Value.Connected)));
                return list;
            }
            return _roster.Select((n, i) => new RoomSeat(i, n, i < _connected.Length && _connected[i])).ToList();
        }
    }

    public static string NewCode(Random rng) => new(Enumerable.Range(0, 4).Select(_ => CodeLetters[rng.Next(CodeLetters.Length)]).ToArray());

    public static string CleanCode(string text) => new(text.ToUpperInvariant().Where(char.IsLetterOrDigit).Take(8).ToArray());

    // ------------------------------------------------------------------ host

    public void Host(string? code = null)
    {
        Stop();
        UdpClient? udp = null;
        try
        {
            var listener = NewListener();
            udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
            lock (_gate)
            {
                _udp = udp;
                _listener = listener;
                Code = code ?? NewCode(Random.Shared);
                MySeat = 0;
                Open = true;
            }
            Begin(RoomState.Hosting);
        }
        catch (SocketException)
        {
            udp?.Dispose();
            Stop();
        }
    }

    static UdpClient NewListener()
    {
        var l = new UdpClient(AddressFamily.InterNetwork) { ExclusiveAddressUse = false };
        l.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        l.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
        return l;
    }

    /// <summary>Host: sends <paramref name="body"/> to the guest in <paramref name="seat"/>.</summary>
    public void SendTo(int seat, string body)
    {
        IPEndPoint? to;
        lock (_gate) to = _guests.TryGetValue(seat, out var g) && g.Connected ? g.Address : null;
        if (to != null && _udp is { } udp) TrySend(udp, to, $"rh|{Code}|{body}");
    }

    /// <summary>Host: forgets a seat (the game has no place for it).</summary>
    public void Kick(int seat)
    {
        IPEndPoint? to = null;
        lock (_gate)
            if (_guests.Remove(seat, out var g)) to = g.Address;
        if (to != null && _udp is { } udp) TrySend(udp, to, $"rno|{Code}|kicked");
        RaiseChanged();
    }

    // ------------------------------------------------------------------ guest

    /// <summary>Joins the room with <paramref name="code"/>, found by broadcast, or at <paramref name="address"/>.</summary>
    public void Join(string code, IPEndPoint? address = null)
    {
        Stop();
        var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        lock (_gate)
        {
            _udp = udp;
            Code = CleanCode(code);
            _target = address;
            _nonce = Guid.NewGuid().ToString("N")[..10];
            MySeat = -1;
        }
        Begin(RoomState.Joining);
    }

    /// <summary>Guest: sends <paramref name="body"/> to the host.</summary>
    public void SendToHost(string body)
    {
        if (State == RoomState.Joined && _host is { } host && _udp is { } udp) TrySend(udp, host, $"rg|{Code}|{MySeat}|{body}");
    }

    /// <summary>Messages for the game: (seat it came from, body); guests see the host as seat 0.</summary>
    public bool TryReceive(out (int Seat, string Body) message) => _inbox.TryDequeue(out message);

    // ------------------------------------------------------------------ discovery

    /// <summary>Asks the network for rooms and collects the answers that arrive within <paramref name="wait"/>.</summary>
    public static async Task<List<RoomInfo>> FindRooms(TimeSpan wait, IPEndPoint? address = null)
    {
        var rooms = new Dictionary<string, RoomInfo>();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        foreach (var to in Targets(address)) TrySend(udp, to, "rfind");
        using var cts = new CancellationTokenSource(wait);
        try
        {
            while (true)
            {
                var r = await udp.ReceiveAsync(cts.Token);
                var f = Encoding.UTF8.GetString(r.Buffer).Split('|');
                if (f.Length == 7 && f[0] == Magic && f[1] == "rhere" && int.TryParse(f[4], out int n) && int.TryParse(f[5], out int seats))
                    rooms[f[2]] = new RoomInfo(f[2], f[3], n, seats, f[6] == "1", r.RemoteEndPoint);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        return rooms.Values.OrderBy(r => r.Code).ToList();
    }

    static IEnumerable<IPEndPoint> Targets(IPEndPoint? address) => address != null
        ? new[] { new IPEndPoint(address.Address, Port) }
        : new[] { new IPEndPoint(IPAddress.Broadcast, Port), new IPEndPoint(IPAddress.Loopback, Port) };

    // ------------------------------------------------------------------ plumbing

    void Begin(RoomState state)
    {
        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _cts = cts;
            _lastHeard = DateTime.UtcNow;
        }
        SetState(state);
        if (_udp is { } udp) Task.Run(() => ReceiveLoop(udp, cts.Token));
        if (_listener is { } listener) Task.Run(() => ReceiveLoop(listener, cts.Token));
        Task.Run(() => TimerLoop(cts.Token));
    }

    public void Stop()
    {
        UdpClient? udp;
        List<IPEndPoint> guests;
        IPEndPoint? host;
        bool wasHost;
        string code;
        lock (_gate)
        {
            udp = _udp;
            wasHost = IsHost;
            guests = _guests.Values.Where(g => g.Connected).Select(g => g.Address).ToList();
            host = State == RoomState.Joined ? _host : null;
            code = Code;
        }
        if (udp != null)
        {
            if (wasHost) foreach (var g in guests) TrySend(udp, g, $"rend|{code}");
            else if (host != null) TrySend(udp, host, $"rbye|{code}|{MySeat}");
        }
        lock (_gate)
        {
            _cts?.Cancel();
            _udp?.Dispose();
            _listener?.Dispose();
            _udp = _listener = null;
            _cts = null;
            _guests.Clear();
            _host = _target = null;
            _roster = Array.Empty<string>();
            _connected = Array.Empty<bool>();
            MySeat = -1;
            Code = "";
            while (_inbox.TryDequeue(out _)) { }
        }
        SetState(RoomState.Off);
    }

    async Task ReceiveLoop(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await udp.ReceiveAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }
            if (r.Buffer.Length > 8192 || ct.IsCancellationRequested) continue;
            string text = Encoding.UTF8.GetString(r.Buffer);
            if (!text.StartsWith(Magic + "|", StringComparison.Ordinal)) continue;
            try { Handle(r.RemoteEndPoint, text[(Magic.Length + 1)..], ct); }
            catch (Exception) { /* a malformed message is ignored */ }
        }
    }

    void Handle(IPEndPoint from, string msg, CancellationToken ct)
    {
        var f = msg.Split('|', 4);
        string kind = f[0];
        if (IsHost) HostHandle(from, kind, f);
        else GuestHandle(from, kind, f, ct);
    }

    void HostHandle(IPEndPoint from, string kind, string[] f)
    {
        var udp = _udp;
        if (udp == null) return;
        if (kind == "rfind")
        {
            int count;
            lock (_gate) count = 1 + _guests.Count(g => g.Value.Connected);
            TrySend(udp, from, $"rhere|{Code}|{Clean(MyName)}|{count}|{MaxSeats}|{(Open ? 1 : 0)}");
            return;
        }
        if (kind == "rjoin" && f.Length >= 4)
        {
            if (f[1] != Code) return; // another room's player
            int seat = -1;
            string? refusal = null;
            string nonce = f[3];
            var replyTo = from;
            lock (_gate)
            {
                var known = _guests.FirstOrDefault(g => g.Value.Nonce == nonce);
                if (known.Value != null)
                {
                    seat = known.Key; // a repeated join (a lost answer, or the loopback copy): same seat and address
                    replyTo = known.Value.Address;
                    known.Value.Connected = true;
                    known.Value.Heard = DateTime.UtcNow;
                }
                else if (!Open) refusal = "started";
                else if (_guests.Count + 1 >= MaxSeats) refusal = "full";
                else
                {
                    seat = Enumerable.Range(1, MaxSeats - 1).First(s => !_guests.ContainsKey(s));
                    _guests[seat] = new Guest { Address = from, Name = Clean(f[2]), Nonce = nonce, Heard = DateTime.UtcNow };
                }
            }
            TrySend(udp, refusal == null ? replyTo : from, refusal == null ? $"rok|{Code}|{seat}" : $"rno|{Code}|{refusal}");
            if (refusal == null)
            {
                SendRoster();
                RaiseChanged();
            }
            return;
        }
        if (f.Length < 3 || f[1] != Code || !int.TryParse(f[2], out int from_seat)) return;
        lock (_gate)
        {
            if (!_guests.TryGetValue(from_seat, out var g) || !g.Address.Equals(from)) return;
            g.Heard = DateTime.UtcNow;
            if (!g.Connected)
            {
                g.Connected = true;
                ThreadPool.QueueUserWorkItem(_ => { SendRoster(); RaiseChanged(); });
            }
        }
        if (kind == "rbye")
        {
            lock (_gate)
                if (Open) _guests.Remove(from_seat);
                else if (_guests.TryGetValue(from_seat, out var g)) g.Connected = false;
            SendRoster();
            RaiseChanged();
        }
        else if (kind == "rg" && f.Length == 4 && f[3] != "ping")
        {
            _inbox.Enqueue((from_seat, f[3]));
            while (_inbox.Count > 2000) _inbox.TryDequeue(out _);
            MessageArrived?.Invoke();
        }
    }

    void GuestHandle(IPEndPoint from, string kind, string[] f, CancellationToken ct)
    {
        if (f.Length < 2 || f[1] != Code || ct.IsCancellationRequested) return;
        if (State == RoomState.Joining)
        {
            if (_target != null && !from.Address.Equals(_target.Address)) return;
            if (kind == "rok" && f.Length >= 3 && int.TryParse(f[2], out int seat))
            {
                lock (_gate)
                {
                    if (ct.IsCancellationRequested) return; // stopped meanwhile
                    _host = from;
                    MySeat = seat;
                    _lastHeard = DateTime.UtcNow;
                }
                SetState(RoomState.Joined);
            }
            else if (kind == "rno")
            {
                Refusal = f.Length >= 3 ? f[2] : "";
                SetState(RoomState.Lost);
            }
            return;
        }
        if (_host == null || !from.Equals(_host)) return;
        lock (_gate) _lastHeard = DateTime.UtcNow;
        switch (kind)
        {
            case "rr" when f.Length >= 3:
                var parts = (f[2] + (f.Length == 4 ? "|" + f[3] : "")).Split('|');
                lock (_gate)
                {
                    _roster = parts[0].Split(',');
                    _connected = parts.Length > 1 ? parts[1].Select(c => c == '1').ToArray() : _roster.Select(_ => true).ToArray();
                }
                RaiseChanged();
                break;
            case "rh" when f.Length >= 3:
                _inbox.Enqueue((0, f.Length == 4 ? f[2] + "|" + f[3] : f[2]));
                while (_inbox.Count > 2000) _inbox.TryDequeue(out _);
                MessageArrived?.Invoke();
                break;
            case "rno":
                Refusal = f.Length >= 3 ? f[2] : "";
                SetState(RoomState.Lost);
                break;
            case "rend":
                Refusal = "closed";
                SetState(RoomState.Lost);
                break;
        }
    }

    /// <summary>Why the host turned us away or the room ended: "full", "started", "kicked", "closed" or "timeout".</summary>
    public string Refusal { get; private set; } = "";

    void SendRoster()
    {
        var udp = _udp;
        if (udp == null || !IsHost) return;
        List<(int Seat, IPEndPoint Address, bool Connected)> targets;
        string names, flags;
        lock (_gate)
        {
            int top = _guests.Count == 0 ? 0 : _guests.Keys.Max();
            var seats = Enumerable.Range(0, top + 1).ToList();
            names = string.Join(",", seats.Select(s => s == 0 ? Clean(MyName) : _guests.TryGetValue(s, out var g) ? g.Name : ""));
            flags = new string(seats.Select(s => s == 0 || _guests.TryGetValue(s, out var g) && g.Connected ? '1' : '0').ToArray());
            targets = _guests.Select(g => (g.Key, g.Value.Address, g.Value.Connected)).ToList();
        }
        foreach (var t in targets.Where(t => t.Connected)) TrySend(udp, t.Address, $"rr|{Code}|{names}|{flags}");
    }

    async Task TimerLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(0.35), ct); }
            catch (OperationCanceledException) { return; }
            var udp = _udp;
            if (udp == null) continue;
            var now = DateTime.UtcNow;
            switch (State)
            {
                case RoomState.Joining:
                    foreach (var to in Targets(_target)) TrySend(udp, to, $"rjoin|{Code}|{Clean(MyName)}|{_nonce}");
                    if ((now - _lastHeard).TotalSeconds > TimeoutSeconds * 1.5)
                    {
                        Refusal = "notfound";
                        SetState(RoomState.Lost);
                    }
                    break;
                case RoomState.Joined:
                    if ((now - _lastHeard).TotalSeconds > TimeoutSeconds)
                    {
                        Refusal = "timeout";
                        SetState(RoomState.Lost);
                    }
                    else if (_host is { } host) TrySend(udp, host, $"rg|{Code}|{MySeat}|ping");
                    break;
                case RoomState.Hosting:
                    bool dropped = false;
                    lock (_gate)
                        foreach (var (seat, g) in _guests.Where(g => g.Value.Connected && (now - g.Value.Heard).TotalSeconds > TimeoutSeconds).ToList())
                        {
                            // before the game, a silent guest frees the seat; during it, the game keeps the seat for them
                            if (Open) _guests.Remove(seat);
                            else g.Connected = false;
                            dropped = true;
                        }
                    if (dropped) RaiseChanged();
                    SendRoster(); // doubles as the host's heartbeat, a little faster than needed
                    break;
            }
        }
    }

    static string Clean(string s) => new(s.Where(c => c is not ('|' or ',') && !char.IsControl(c)).Take(24).ToArray());

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

    void SetState(RoomState s)
    {
        lock (_gate)
        {
            if (State == s) return;
            State = s;
        }
        RaiseChanged();
    }

    void RaiseChanged() => Changed?.Invoke();

    public void Dispose() => Stop();
}
