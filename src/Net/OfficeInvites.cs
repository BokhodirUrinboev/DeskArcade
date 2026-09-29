using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DeskArcade.Net;

public enum InviteKind { Coffee, Lunch, Walk }

public enum InviteAnswer { Yes, No, Focusing }

/// <summary>"Coffee in 5 minutes?" from a co-worker: who asked (their copy's random id and their name), what, and when.</summary>
public sealed record Invite(string Id, string FromId, string From, InviteKind Kind, int InMinutes);

/// <summary>An answer to an invite: yes, not now, or "focusing" (the answer a focus block gives by itself).</summary>
public sealed record InviteReply(string InviteId, string FromId, string From, InviteAnswer Answer);

/// <summary>
/// Coffee, lunch and walk invites for everyone on the local network who has invites on (tray → At work → Invites from
/// co-workers). An invite and its answers are broadcast on UDP port <see cref="Port"/>, each sent a few times since a
/// datagram can be lost; they carry the sender's user name and nothing else. Nothing is sent or heard while invites
/// are off, and nothing leaves the local network. Separate from the two-player <see cref="LanLink"/>, so it works
/// whoever is playing with whom.
/// Wire format: "DA1|iv|id|fromId|name|kind|minutes", "DA1|ir|inviteId|fromId|name|y/n/f" and "DA1|ix|inviteId|fromId".
/// </summary>
public sealed class OfficeInvites : IDisposable
{
    public const int Port = 47823;
    const string Magic = "DA1";
    static readonly int[] InviteRepeatsMs = { 0, 1500, 4000 };
    static readonly int[] ReplyRepeatsMs = { 0, 1200 };
    public static readonly int[] Minutes = { 0, 5, 15, 30 };

    readonly object _gate = new();
    readonly HashSet<string> _seen = new();
    readonly Queue<string> _seenOrder = new();
    readonly string _myId;
    readonly Func<string> _myName;
    readonly int _port;
    UdpClient? _udp;
    CancellationTokenSource? _cts;

    /// <param name="myId">This copy's random id (the office leaderboard's), so its own broadcasts are ignored.</param>
    /// <param name="myName">The user name to sign invites and answers with.</param>
    /// <param name="port">Another port only for tests, so they never meet a running copy.</param>
    public OfficeInvites(string myId, Func<string> myName, int port = Port)
    {
        _myId = myId;
        _myName = myName;
        _port = port;
    }

    public bool Running => _udp != null;

    /// <summary>Raised on a background thread for each new invite from someone else.</summary>
    public event Action<Invite>? InviteReceived;
    /// <summary>Raised on a background thread for each answer to one of our invites.</summary>
    public event Action<InviteReply>? ReplyReceived;
    /// <summary>Raised on a background thread when the sender calls an invite off (its id).</summary>
    public event Action<string>? Cancelled;

    public void Start()
    {
        if (_udp != null) return;
        try
        {
            var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true, ExclusiveAddressUse = false };
            // several copies on one PC (--profile) can all listen
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, _port));
            var cts = new CancellationTokenSource();
            _udp = udp;
            _cts = cts;
            Task.Run(() => ReceiveLoop(udp, cts.Token));
        }
        catch (SocketException)
        {
            Stop(); // the port is unavailable: invites just stay quiet
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _udp?.Dispose();
        _udp = null;
        _cts = null;
    }

    /// <summary>Broadcasts a new invite (a few times over the next seconds) and returns it, or null when invites are off.</summary>
    public Invite? Send(InviteKind kind, int inMinutes)
    {
        if (_udp == null) return null;
        var invite = new Invite(Guid.NewGuid().ToString("N")[..10], _myId, Clean(_myName()), kind, Math.Clamp(inMinutes, 0, 180));
        Repeat(Encode(invite), InviteRepeatsMs);
        return invite;
    }

    public void Reply(Invite invite, InviteAnswer answer)
    {
        if (_udp == null) return;
        Repeat(Encode(new InviteReply(invite.Id, _myId, Clean(_myName()), answer)), ReplyRepeatsMs);
    }

    /// <summary>Calls one of our invites off, so the cards on the others' screens go away.</summary>
    public void Cancel(Invite invite)
    {
        if (_udp == null || invite.FromId != _myId) return;
        Repeat($"{Magic}|ix|{invite.Id}|{_myId}", ReplyRepeatsMs);
    }

    void Repeat(string message, int[] delaysMs)
    {
        var udp = _udp;
        if (udp == null) return;
        var ct = _cts?.Token ?? CancellationToken.None;
        Task.Run(async () =>
        {
            int waited = 0;
            foreach (int at in delaysMs)
            {
                try
                {
                    await Task.Delay(at - waited, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                waited = at;
                Broadcast(udp, message);
            }
        }, ct);
    }

    void Broadcast(UdpClient udp, string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        foreach (var to in new[] { new IPEndPoint(IPAddress.Broadcast, _port), new IPEndPoint(IPAddress.Loopback, _port) })
        {
            try { udp.Send(bytes, bytes.Length, to); }
            catch (SocketException) { }
            catch (ObjectDisposedException) { return; }
        }
    }

    // ------------------------------------------------------------------ wire format

    static string Clean(string s) => new(s.Where(c => c != '|' && !char.IsControl(c)).Take(40).ToArray());

    static string KindCode(InviteKind k) => k switch { InviteKind.Lunch => "lunch", InviteKind.Walk => "walk", _ => "coffee" };

    public static string Encode(Invite i) =>
        string.Create(CultureInfo.InvariantCulture, $"{Magic}|iv|{Clean(i.Id)}|{Clean(i.FromId)}|{Clean(i.From)}|{KindCode(i.Kind)}|{i.InMinutes}");

    public static string Encode(InviteReply r) =>
        $"{Magic}|ir|{Clean(r.InviteId)}|{Clean(r.FromId)}|{Clean(r.From)}|" + r.Answer switch { InviteAnswer.Yes => "y", InviteAnswer.Focusing => "f", _ => "n" };

    /// <summary>An invite, an answer, a called-off invite id (a string), or null for anything else.</summary>
    public static object? Decode(string text)
    {
        var f = text.Split('|');
        if (f.Length < 4 || f[0] != Magic || f[2].Length is 0 or > 40 || f[3].Length is 0 or > 40) return null;
        switch (f[1])
        {
            case "iv" when f.Length == 7 && f[4].Length > 0:
                InviteKind? kind = f[5] switch { "coffee" => InviteKind.Coffee, "lunch" => InviteKind.Lunch, "walk" => InviteKind.Walk, _ => null };
                if (kind == null || !int.TryParse(f[6], NumberStyles.None, CultureInfo.InvariantCulture, out int minutes) || minutes > 180) return null;
                return new Invite(f[2], f[3], f[4], kind.Value, minutes);
            case "ir" when f.Length == 6 && f[4].Length > 0:
                InviteAnswer? answer = f[5] switch { "y" => InviteAnswer.Yes, "n" => InviteAnswer.No, "f" => InviteAnswer.Focusing, _ => null };
                return answer == null ? null : new InviteReply(f[2], f[3], f[4], answer.Value);
            case "ix" when f.Length == 4:
                return f[2];
            default:
                return null;
        }
    }

    // ------------------------------------------------------------------ receiving

    async Task ReceiveLoop(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await udp.ReceiveAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }
            if (r.Buffer.Length > 1024) continue;
            string text = Encoding.UTF8.GetString(r.Buffer);
            if (!FirstTime(text)) continue; // the repeats of something already heard
            switch (Decode(text))
            {
                case Invite invite when invite.FromId != _myId:
                    InviteReceived?.Invoke(invite);
                    break;
                case InviteReply reply when reply.FromId != _myId:
                    ReplyReceived?.Invoke(reply);
                    break;
                case string cancelled when !text.Contains("|" + _myId, StringComparison.Ordinal):
                    Cancelled?.Invoke(cancelled);
                    break;
            }
        }
    }

    bool FirstTime(string message)
    {
        lock (_gate)
        {
            if (!_seen.Add(message)) return false;
            _seenOrder.Enqueue(message);
            if (_seenOrder.Count > 500) _seen.Remove(_seenOrder.Dequeue());
            return true;
        }
    }

    public void Dispose() => Stop();
}
