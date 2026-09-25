using System;
using System.Collections.Generic;
using System.Globalization;

namespace DeskArcade.Net;

/// <summary>What a co-worker can post to your pet: something to eat, or a toy to play with for a while.</summary>
public enum PetGift { Treat, Ball, Yarn, Bone }

/// <summary>The four pet-mail messages: a parcel, its receipt, "the pet opened it", and the receipt for that.</summary>
public enum PetMailKind { Parcel, Got, Opened, Thanks }

public readonly record struct PetMailMessage(PetMailKind Kind, int Id, PetGift Gift = PetGift.Treat);

/// <summary>A parcel that has arrived and waits for the pet to be on screen.</summary>
public readonly record struct IncomingParcel(int Id, PetGift Gift, string From);

public enum PetMailSend { Sent, TooSoon, TooMany }

public enum PetMailEventKind { None, Parcel, Delivered, Loved }

public readonly record struct PetMailEvent(PetMailEventKind Kind, int Id = 0, PetGift Gift = PetGift.Treat);

/// <summary>
/// Pet mail over the <see cref="LanLink"/>: a co-worker posts your pet a treat or a toy. UI-free, so the format, the
/// validation, the rate limit and the queue can be tested without a network.
/// <para>
/// Wire format: "pm|gift|{id}|{treat|ball|yarn|bone}", acknowledged by "pm|got|{id}"; when the pet opens it,
/// "pm|open|{id}", acknowledged by "pm|thx|{id}". UDP may lose any of them, so each is sent again every
/// <see cref="ResendSeconds"/> until its receipt arrives (or <see cref="GiveUpSeconds"/> pass), and the receiver
/// acknowledges every copy but acts on each id once.
/// </para>
/// <para>
/// Limits: a sender posts at most one parcel every <see cref="SendEvery"/> seconds and has at most
/// <see cref="MaxInFlight"/> on the way; a receiver keeps at most <see cref="MaxWaiting"/> unopened parcels, takes a
/// new one at most every <see cref="ReceiveEvery"/> seconds, and only believes "opened" for a parcel it sent.
/// </para>
/// </summary>
public sealed class PetMail
{
    public const string Tag = "pm";
    public const double SendEvery = 30, ReceiveEvery = 10, ResendSeconds = 0.5, GiveUpSeconds = 60;
    public const int MaxInFlight = 2, MaxWaiting = 3, Remember = 64, MaxId = 999_999_999;

    static readonly string[] GiftNames = { "treat", "ball", "yarn", "bone" };
    static readonly string[] KindNames = { "gift", "got", "open", "thx" };

    sealed class Outgoing
    {
        public required PetMailMessage Message;
        public double FirstSent, LastSent;
    }

    readonly Action<string> _send;
    readonly List<Outgoing> _out = new();
    readonly Queue<IncomingParcel> _waiting = new();
    readonly HashSet<int> _seenParcels = new(), _seenOpens = new();
    readonly Queue<int> _seenParcelOrder = new(), _seenOpenOrder = new();
    readonly Dictionary<int, PetGift> _sent = new(); // what we posted, so a "loved it" can name it (and a made-up one is ignored)
    readonly Queue<int> _sentOrder = new();
    double _lastSent = double.NegativeInfinity, _lastAccepted = double.NegativeInfinity;
    int _nextId;

    /// <param name="send">Sends one message to the peer, e.g. <see cref="LanLink.Send"/>.</param>
    /// <param name="firstId">The first parcel's id; random by default, so two runs of the app do not reuse ids.</param>
    public PetMail(Action<string> send, int? firstId = null)
    {
        _send = send;
        _nextId = firstId ?? Random.Shared.Next(1, 1 << 24);
    }

    /// <summary>True while something waits for a receipt: the owner keeps calling <see cref="Tick"/> until it is false.</summary>
    public bool Busy => _out.Count > 0;

    /// <summary>Parcels that have arrived and wait for the pet.</summary>
    public int Waiting => _waiting.Count;

    /// <summary>Parcels sent and not yet acknowledged.</summary>
    public int InFlight
    {
        get
        {
            int n = 0;
            foreach (var o in _out) if (o.Message.Kind == PetMailKind.Parcel) n++;
            return n;
        }
    }

    // ------------------------------------------------------------------ the format

    public static string GiftName(PetGift gift) => GiftNames[(int)gift];

    public static string Encode(PetMailMessage m)
    {
        string head = string.Create(CultureInfo.InvariantCulture, $"{Tag}|{KindNames[(int)m.Kind]}|{m.Id}");
        return m.Kind == PetMailKind.Parcel ? head + "|" + GiftName(m.Gift) : head;
    }

    /// <summary>Reads a "pm|…" message; false for anything else or anything malformed (a bad id, an unknown gift, a stray field).</summary>
    public static bool TryDecode(string? message, out PetMailMessage m)
    {
        m = default;
        if (string.IsNullOrEmpty(message) || message.Length > 64) return false;
        var f = message.Split('|');
        if (f.Length < 3 || f[0] != Tag) return false;
        int kind = Array.IndexOf(KindNames, f[1]);
        if (kind < 0 || !TryId(f[2], out int id)) return false;
        if ((PetMailKind)kind == PetMailKind.Parcel)
        {
            if (f.Length != 4) return false;
            int gift = Array.IndexOf(GiftNames, f[3]);
            if (gift < 0) return false;
            m = new PetMailMessage(PetMailKind.Parcel, id, (PetGift)gift);
            return true;
        }
        if (f.Length != 3) return false;
        m = new PetMailMessage((PetMailKind)kind, id);
        return true;
    }

    static bool TryId(string text, out int id) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id is > 0 and <= MaxId && text.Length <= 9 && text[0] != '0';

    // ------------------------------------------------------------------ sending

    /// <summary>Posts <paramref name="gift"/>, unless the last parcel went less than <see cref="SendEvery"/> seconds ago
    /// (<paramref name="wait"/> says how long is left) or too many are still on the way.</summary>
    public PetMailSend Send(PetGift gift, double now, out double wait)
    {
        wait = 0;
        if (now - _lastSent < SendEvery)
        {
            wait = SendEvery - (now - _lastSent);
            return PetMailSend.TooSoon;
        }
        if (InFlight >= MaxInFlight) return PetMailSend.TooMany;
        _lastSent = now;
        int id = _nextId;
        _nextId = _nextId >= MaxId ? 1 : _nextId + 1;
        RememberGift(_sent, _sentOrder, id, gift);
        Queue(new PetMailMessage(PetMailKind.Parcel, id, gift), now);
        return PetMailSend.Sent;
    }

    /// <summary>The pet opened parcel <paramref name="id"/>: tell the sender (their "{name}'s pet loved it").</summary>
    public void Opened(int id, double now)
    {
        if (id is > 0 and <= MaxId) Queue(new PetMailMessage(PetMailKind.Opened, id), now);
    }

    /// <summary>Sends again whatever still waits for a receipt, every <see cref="ResendSeconds"/>; gives up after <see cref="GiveUpSeconds"/>.</summary>
    public void Tick(double now)
    {
        _out.RemoveAll(o => now - o.FirstSent > GiveUpSeconds);
        foreach (var o in _out)
        {
            if (now - o.LastSent < ResendSeconds) continue;
            o.LastSent = now;
            _send(Encode(o.Message));
        }
    }

    void Queue(PetMailMessage m, double now)
    {
        _out.Add(new Outgoing { Message = m, FirstSent = now, LastSent = now });
        _send(Encode(m));
    }

    // ------------------------------------------------------------------ receiving

    /// <summary>
    /// Offers one message from the peer <paramref name="from"/>. A new parcel joins the waiting queue (see
    /// <see cref="TryTake"/>); a receipt ends the resending; "opened" for one of our parcels comes back as
    /// <see cref="PetMailEventKind.Loved"/>. Anything malformed, repeated or over the limits is ignored.
    /// </summary>
    public PetMailEvent Handle(string message, string from, double now)
    {
        if (!TryDecode(message, out var m)) return default;
        switch (m.Kind)
        {
            case PetMailKind.Parcel:
                _send(Encode(new PetMailMessage(PetMailKind.Got, m.Id))); // every copy: the first receipt may have been lost
                if (_seenParcels.Contains(m.Id)) return default;
                RememberId(_seenParcels, _seenParcelOrder, m.Id);
                if (_waiting.Count >= MaxWaiting || now - _lastAccepted < ReceiveEvery) return default; // a flood: dropped
                _lastAccepted = now;
                _waiting.Enqueue(new IncomingParcel(m.Id, m.Gift, Clean(from)));
                return new PetMailEvent(PetMailEventKind.Parcel, m.Id, m.Gift);
            case PetMailKind.Got:
                return Acknowledged(PetMailKind.Parcel, m.Id) && _sent.TryGetValue(m.Id, out var gift)
                    ? new PetMailEvent(PetMailEventKind.Delivered, m.Id, gift)
                    : default;
            case PetMailKind.Opened:
                _send(Encode(new PetMailMessage(PetMailKind.Thanks, m.Id)));
                if (!_sent.TryGetValue(m.Id, out var opened) || _seenOpens.Contains(m.Id)) return default;
                RememberId(_seenOpens, _seenOpenOrder, m.Id);
                return new PetMailEvent(PetMailEventKind.Loved, m.Id, opened);
            case PetMailKind.Thanks:
                Acknowledged(PetMailKind.Opened, m.Id);
                return default;
        }
        return default;
    }

    bool Acknowledged(PetMailKind kind, int id) => _out.RemoveAll(o => o.Message.Kind == kind && o.Message.Id == id) > 0;

    /// <summary>The next parcel for the pet, oldest first.</summary>
    public bool TryTake(out IncomingParcel parcel) => _waiting.TryDequeue(out parcel);

    /// <summary>
    /// A new LAN session: forget what was on the way and which ids were seen (the peer may be someone else now).
    /// Parcels that already arrived stay in the queue for the pet, and the send rate limit still holds.
    /// </summary>
    public void ResetLink()
    {
        _out.Clear();
        _seenParcels.Clear();
        _seenParcelOrder.Clear();
        _seenOpens.Clear();
        _seenOpenOrder.Clear();
        _sent.Clear();
        _sentOrder.Clear();
    }

    /// <summary>A sender's name for the parcel's tag: printable, and short enough to fit.</summary>
    static string Clean(string name)
    {
        var chars = new List<char>();
        foreach (char c in name ?? "")
            if (!char.IsControl(c) && chars.Count < 24) chars.Add(c);
        string s = new string(chars.ToArray()).Trim();
        return s.Length > 0 ? s : "?";
    }

    static void RememberId(HashSet<int> set, Queue<int> order, int id)
    {
        set.Add(id);
        order.Enqueue(id);
        while (order.Count > Remember) set.Remove(order.Dequeue());
    }

    static void RememberGift(Dictionary<int, PetGift> map, Queue<int> order, int id, PetGift gift)
    {
        map[id] = gift;
        order.Enqueue(id);
        while (order.Count > Remember) map.Remove(order.Dequeue());
    }
}
