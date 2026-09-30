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

/// <summary>What a co-worker is up to: free, in a focus block or in a meeting; Off when they turn invites off or quit.</summary>
public enum PresenceState { Free, Focusing, Meeting, Off }

/// <summary>
/// "I'm focusing, 12 minutes left", sent every few minutes while invites are on: the state, and the minutes it lasts
/// (for someone free, the minutes until their next meeting; 0 when nothing is planned). Id makes each one new.
/// </summary>
public sealed record Presence(string Id, string FromId, string From, PresenceState State, int MinutesLeft);

/// <summary>A knock on a co-worker's door (their copy's id), with one line of text: "about the API, 5 min".</summary>
public sealed record Knock(string Id, string FromId, string From, string ToId, string Text);

/// <summary>
/// The answers to a knock: the three the person gives (come over, in ten minutes, after lunch), and the two their copy
/// gives by itself: seen (the card is up) and queued (they are busy; <see cref="KnockReply.Minutes"/> until their break).
/// </summary>
public enum KnockAnswer { ComeOver, InTen, AfterLunch, Seen, Queued }

/// <summary>An answer to a knock, from the one knocked on (FromId) to the one who knocked (ToId).</summary>
public sealed record KnockReply(string KnockId, string FromId, string From, string ToId, KnockAnswer Answer, int Minutes);

/// <summary>
/// Coffee, lunch and walk invites for everyone on the local network who has invites on (tray → At work → Invites from
/// co-workers), and on the same channel knocks on one co-worker's door and the presence that lets a knock wait for a
/// break. An invite and its answers are broadcast on UDP port <see cref="Port"/>, each sent a few times since a
/// datagram can be lost; they carry the sender's user name and nothing else, a presence adds focusing, in a meeting or
/// free and for how many minutes (never a meeting's title), and a knock its line of text. Nothing is sent or heard
/// while invites are off, and nothing leaves the local network. Separate from the two-player <see cref="LanLink"/>,
/// so it works whoever is playing with whom.
/// Wire format: "DA1|iv|id|fromId|name|kind|minutes", "DA1|ir|inviteId|fromId|name|y/n/f" and "DA1|ix|inviteId|fromId";
/// since 1.8.7 "DA1|ps|id|fromId|name|f/c/m/o|minutes" (free, focusing, meeting, off), "DA1|kn|id|fromId|name|toId|text"
/// and "DA1|ka|knockId|fromId|name|toId|c/t/l/s/q|minutes". A copy ignores kinds it does not know (1.8.5 and 1.8.6 drop
/// the three new ones without a word) and fields after the ones it knows, so a kind only ever grows at the end; a change
/// an older copy must not read gets a new kind.
/// </summary>
public sealed class OfficeInvites : IDisposable
{
    public const int Port = 47823;
    const string Magic = "DA1";
    static readonly int[] InviteRepeatsMs = { 0, 1500, 4000 };
    static readonly int[] ReplyRepeatsMs = { 0, 1200 };
    public static readonly int[] Minutes = { 0, 5, 15, 30 };
    /// <summary>The longest knock text, and the most minutes a presence or an answer says.</summary>
    public const int MaxText = 100, MaxMinutes = 720;

    readonly object _gate = new();
    volatile bool _announced; // a presence went out, so a goodbye is owed
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
    /// <summary>Raised on a background thread for each co-worker's presence (theirs, not ours).</summary>
    public event Action<Presence>? PresenceReceived;
    /// <summary>Raised on a background thread for each knock on our door.</summary>
    public event Action<Knock>? KnockReceived;
    /// <summary>Raised on a background thread for each answer to one of our knocks.</summary>
    public event Action<KnockReply>? KnockReplyReceived;

    /// <summary>This copy's id: the one knocks are addressed to.</summary>
    public string MyId => _myId;

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
        // a copy that told the others it was here says goodbye, so it leaves their "Knock on…" list at once
        if (_udp is UdpClient udp && _announced) Broadcast(udp, Encode(new Presence(NewId(), _myId, Clean(_myName()), PresenceState.Off, 0)));
        _announced = false;
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

    static string NewId() => Guid.NewGuid().ToString("N")[..10];

    /// <summary>Tells everyone with invites on what we are up to (twice, a moment apart); false while invites are off.</summary>
    public bool SendPresence(PresenceState state, int minutesLeft)
    {
        if (_udp == null) return false;
        _announced = state != PresenceState.Off;
        Repeat(Encode(new Presence(NewId(), _myId, Clean(_myName()), state, Math.Clamp(minutesLeft, 0, MaxMinutes))), ReplyRepeatsMs);
        return true;
    }

    /// <summary>Knocks on a co-worker's door (a few times over the next seconds) and returns the knock, or null while invites are off.</summary>
    public Knock? SendKnock(string toId, string text)
    {
        if (_udp == null) return null;
        var knock = new Knock(NewId(), _myId, Clean(_myName()), Clean(toId), CleanText(text));
        Repeat(Encode(knock), InviteRepeatsMs);
        return knock;
    }

    /// <summary>Answers a knock on our door: one of the three answers, or seen and queued (with the minutes until the break).</summary>
    public void Reply(Knock knock, KnockAnswer answer, int minutes = 0)
    {
        if (_udp == null) return;
        Repeat(Encode(new KnockReply(knock.Id, _myId, Clean(_myName()), knock.FromId, answer, Math.Clamp(minutes, 0, MaxMinutes))), ReplyRepeatsMs);
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

    /// <summary>A knock's text on one line: a bar becomes a slash (it separates the fields), control characters go.</summary>
    static string CleanText(string s) =>
        new string(s.Replace('|', '/').Select(c => char.IsControl(c) ? ' ' : c).Take(MaxText).ToArray()).Trim();

    static string KindCode(InviteKind k) => k switch { InviteKind.Lunch => "lunch", InviteKind.Walk => "walk", _ => "coffee" };

    static string StateCode(PresenceState s) => s switch
    {
        PresenceState.Focusing => "c", PresenceState.Meeting => "m", PresenceState.Off => "o", _ => "f",
    };

    static string AnswerCode(KnockAnswer a) => a switch
    {
        KnockAnswer.ComeOver => "c", KnockAnswer.InTen => "t", KnockAnswer.AfterLunch => "l", KnockAnswer.Seen => "s", _ => "q",
    };

    public static string Encode(Presence p) =>
        string.Create(CultureInfo.InvariantCulture, $"{Magic}|ps|{Clean(p.Id)}|{Clean(p.FromId)}|{Clean(p.From)}|{StateCode(p.State)}|{p.MinutesLeft}");

    public static string Encode(Knock k) => $"{Magic}|kn|{Clean(k.Id)}|{Clean(k.FromId)}|{Clean(k.From)}|{Clean(k.ToId)}|{CleanText(k.Text)}";

    public static string Encode(KnockReply r) =>
        string.Create(CultureInfo.InvariantCulture, $"{Magic}|ka|{Clean(r.KnockId)}|{Clean(r.FromId)}|{Clean(r.From)}|{Clean(r.ToId)}|{AnswerCode(r.Answer)}|{r.Minutes}");

    static bool TryMinutes(string s, out int minutes) =>
        int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out minutes) && minutes <= MaxMinutes;

    public static string Encode(Invite i) =>
        string.Create(CultureInfo.InvariantCulture, $"{Magic}|iv|{Clean(i.Id)}|{Clean(i.FromId)}|{Clean(i.From)}|{KindCode(i.Kind)}|{i.InMinutes}");

    public static string Encode(InviteReply r) =>
        $"{Magic}|ir|{Clean(r.InviteId)}|{Clean(r.FromId)}|{Clean(r.From)}|" + r.Answer switch { InviteAnswer.Yes => "y", InviteAnswer.Focusing => "f", _ => "n" };

    /// <summary>
    /// An invite, an answer, a called-off invite id (a string), a presence, a knock or an answer to one, or null for
    /// anything else (a kind this copy does not know among them).
    /// </summary>
    public static object? Decode(string text)
    {
        var f = text.Split('|');
        if (f.Length < 4 || f[0] != Magic || f[2].Length is 0 or > 40 || f[3].Length is 0 or > 40) return null;
        switch (f[1])
        {
            // the kinds from 1.8.7 on read the fields they know and ignore any a later version adds after them
            case "ps" when f.Length >= 7 && f[4].Length is > 0 and <= 40:
                PresenceState? state = f[5] switch
                {
                    "f" => PresenceState.Free, "c" => PresenceState.Focusing, "m" => PresenceState.Meeting, "o" => PresenceState.Off, _ => null,
                };
                return state != null && TryMinutes(f[6], out int left) ? new Presence(f[2], f[3], f[4], state.Value, left) : null;
            case "kn" when f.Length >= 7 && f[4].Length is > 0 and <= 40 && f[5].Length is > 0 and <= 40 && f[6].Length <= MaxText:
                string line = f[6].Trim();
                return line.Length == 0 || line.Any(char.IsControl) ? null : new Knock(f[2], f[3], f[4], f[5], line);
            case "ka" when f.Length >= 8 && f[4].Length is > 0 and <= 40 && f[5].Length is > 0 and <= 40:
                KnockAnswer? said = f[6] switch
                {
                    "c" => KnockAnswer.ComeOver, "t" => KnockAnswer.InTen, "l" => KnockAnswer.AfterLunch, "s" => KnockAnswer.Seen,
                    "q" => KnockAnswer.Queued, _ => null,
                };
                return said != null && TryMinutes(f[7], out int until) ? new KnockReply(f[2], f[3], f[4], f[5], said.Value, until) : null;
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
                case Presence presence when presence.FromId != _myId:
                    PresenceReceived?.Invoke(presence);
                    break;
                case Knock knock when knock.ToId == _myId && knock.FromId != _myId:
                    KnockReceived?.Invoke(knock);
                    break;
                case KnockReply answer when answer.ToId == _myId && answer.FromId != _myId:
                    KnockReplyReceived?.Invoke(answer);
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
