using System;
using System.Collections.Generic;
using System.Linq;
using DeskArcade.Net;

namespace DeskArcade.Office;

/// <summary>A co-worker heard on the network: their copy's id, their name, and what they said they were up to, when.</summary>
public sealed record Coworker(string Id, string Name, PresenceState State, int MinutesLeft, DateTime SeenUtc)
{
    /// <summary>The minutes their state still lasts, counted down since it was heard (0 when open-ended).</summary>
    public int MinutesLeftAt(DateTime utc) =>
        MinutesLeft <= 0 ? 0 : Math.Max(0, MinutesLeft - (int)Math.Floor((utc - SeenUtc).TotalMinutes));
}

/// <summary>The co-workers heard lately (tray or ☰ → At work → Knock on…), by their copies' presence messages.</summary>
public sealed class Coworkers
{
    /// <summary>Someone not heard for this long has gone (a presence comes every few minutes).</summary>
    public static readonly TimeSpan Recent = TimeSpan.FromMinutes(10);
    const int Max = 64;

    readonly Dictionary<string, Coworker> _byId = new();

    /// <summary>A presence heard; true when the list as shown changes (someone new, someone gone, a new state).</summary>
    public bool Heard(Presence p, DateTime utc)
    {
        if (p.State == PresenceState.Off) return _byId.Remove(p.FromId);
        bool changed = !_byId.TryGetValue(p.FromId, out var old) || old.State != p.State || old.Name != p.From || utc - old.SeenUtc > Recent;
        _byId[p.FromId] = new Coworker(p.FromId, p.From, p.State, p.MinutesLeft, utc);
        if (_byId.Count > Max) _byId.Remove(_byId.Values.MinBy(c => c.SeenUtc)!.Id);
        return changed;
    }

    /// <summary>Everyone heard within <see cref="Recent"/>, by name.</summary>
    public IReadOnlyList<Coworker> Seen(DateTime utc) =>
        _byId.Values.Where(c => utc - c.SeenUtc <= Recent)
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id, StringComparer.Ordinal).ToList();

    public Coworker? Find(string id) => _byId.TryGetValue(id, out var c) ? c : null;
}

/// <summary>A knock on our door and when it came.</summary>
public sealed record HeldKnock(Knock Knock, DateTime AtUtc);

/// <summary>
/// Knocks on our door that wait for the break: a few at most, a few from any one co-worker, none older than a
/// morning, and each only once however often it was sent.
/// </summary>
public sealed class KnockQueue
{
    public const int Max = 8, MaxPerSender = 3;
    public static readonly TimeSpan KeepFor = TimeSpan.FromHours(4);

    readonly List<HeldKnock> _held = new();
    readonly HashSet<string> _known = new();
    readonly Queue<string> _knownOrder = new();

    public int Count => _held.Count;

    public IReadOnlyList<HeldKnock> Held => _held;

    /// <summary>Whether a knock is new: false for one heard before (its repeats), which then counts as heard.</summary>
    public bool FirstTime(Knock k)
    {
        if (!_known.Add(k.FromId + "|" + k.Id)) return false;
        _knownOrder.Enqueue(k.FromId + "|" + k.Id);
        if (_knownOrder.Count > 200) _known.Remove(_knownOrder.Dequeue());
        return true;
    }

    /// <summary>Keeps a knock for the break; the oldest make way when there are too many.</summary>
    public void Hold(Knock k, DateTime utc)
    {
        var mine = _held.Where(h => h.Knock.FromId == k.FromId).ToList();
        if (mine.Count >= MaxPerSender) _held.Remove(mine[0]);
        _held.Add(new HeldKnock(k, utc));
        if (_held.Count > Max) _held.RemoveAt(0);
    }

    /// <summary>The knocks still worth answering, oldest first; the queue is empty afterwards.</summary>
    public List<HeldKnock> TakeAll(DateTime utc)
    {
        var fresh = _held.Where(h => utc - h.AtUtc <= KeepFor).ToList();
        _held.Clear();
        return fresh;
    }
}

/// <summary>
/// Knock first: rather than walk over to a co-worker in a focus block, knock with a line of text. This side of it has
/// no UI: it sends our presence every few minutes (and at once when it changes), lists the co-workers heard, holds
/// knocks on our door while we focus or are in a meeting and lets them out at the break, answers for us that a knock
/// is seen or queued, and follows the knocks we sent until they are answered. The overlay shows what it raises.
/// Everything runs on the caller's thread: <see cref="Receive"/> takes the channel's messages, <see cref="Step"/> the time.
/// </summary>
public sealed class Knocking
{
    public static readonly TimeSpan PresenceEvery = TimeSpan.FromMinutes(3), DeliveryWait = TimeSpan.FromSeconds(12), KeepSent = TimeSpan.FromHours(4);

    /// <summary>A knock we sent, and what became of it.</summary>
    public sealed class Sent
    {
        public required Knock Knock;
        public required string ToName;
        public required DateTime AtUtc;
        public bool Delivered, Warned;
        public KnockAnswer? Answer;
    }

    readonly OfficeInvites _channel;
    readonly Func<(PresenceState State, int Minutes)> _now;
    readonly Dictionary<string, Sent> _sent = new();
    PresenceState? _told;
    DateTime _toldAt = DateTime.MinValue;

    /// <param name="channel">The invites channel the messages go over.</param>
    /// <param name="now">What we are up to right now: free, focusing or in a meeting, and for how many more minutes.</param>
    public Knocking(OfficeInvites channel, Func<(PresenceState State, int Minutes)> now)
    {
        _channel = channel;
        _now = now;
    }

    public Coworkers Coworkers { get; } = new();

    /// <summary>Knocks on our door waiting for the break.</summary>
    public KnockQueue Waiting { get; } = new();

    /// <summary>Knocks to show on the card now: one that came while we were free, or the ones that waited, at the break.</summary>
    public event Action<IReadOnlyList<HeldKnock>>? Show;

    /// <summary>An answer to one of our knocks: seen, queued (with the minutes until their break), or their answer.</summary>
    public event Action<Sent, KnockReply>? Replied;

    /// <summary>Nothing came back for one of our knocks: their Desk Arcade is closed, or its invites are off.</summary>
    public event Action<Sent>? Unanswered;

    /// <summary>Someone came, went or changed state.</summary>
    public event Action? CoworkersChanged;

    static bool Busy(PresenceState state) => state is PresenceState.Focusing or PresenceState.Meeting;

    /// <summary>
    /// Once a second: our presence when it changed or is due, the waiting knocks when the break has come, and a word
    /// about knocks nobody received.
    /// </summary>
    public void Step(DateTime utc)
    {
        if (!_channel.Running)
        {
            _told = null; // invites off: say it all again when they come back on
            return;
        }
        var (state, minutes) = _now();
        if ((state != _told || utc - _toldAt >= PresenceEvery) && _channel.SendPresence(state, minutes))
        {
            _told = state;
            _toldAt = utc;
        }
        if (!Busy(state) && Waiting.Count > 0 && Waiting.TakeAll(utc) is { Count: > 0 } due) Show?.Invoke(due);
        foreach (var sent in _sent.Values.ToList())
        {
            if (!sent.Delivered && sent.Answer == null && !sent.Warned && utc - sent.AtUtc >= DeliveryWait)
            {
                sent.Warned = true;
                Unanswered?.Invoke(sent);
            }
            if (utc - sent.AtUtc > KeepSent) _sent.Remove(sent.Knock.Id);
        }
    }

    /// <summary>Knocks on a co-worker's door; null while invites are off or when the text is empty.</summary>
    public Sent? KnockOn(Coworker to, string text, DateTime utc)
    {
        if (string.IsNullOrWhiteSpace(text) || _channel.SendKnock(to.Id, text) is not Knock knock) return null;
        var sent = new Sent { Knock = knock, ToName = to.Name, AtUtc = utc };
        _sent[knock.Id] = sent;
        return sent;
    }

    /// <summary>Our answer to a knock on our door: come over, in ten minutes or after lunch.</summary>
    public void Answer(Knock knock, KnockAnswer answer) => _channel.Reply(knock, answer);

    /// <summary>A message from the channel (a presence, a knock on our door, an answer to ours); anything else is ignored.</summary>
    public void Receive(object message, DateTime utc)
    {
        switch (message)
        {
            case Presence p:
                if (Coworkers.Heard(p, utc)) CoworkersChanged?.Invoke();
                break;
            case Knock k when k.ToId == _channel.MyId && Waiting.FirstTime(k):
                var (state, minutes) = _now();
                if (Busy(state))
                {
                    Waiting.Hold(k, utc);
                    _channel.Reply(k, KnockAnswer.Queued, minutes);
                }
                else
                {
                    _channel.Reply(k, KnockAnswer.Seen);
                    Show?.Invoke(new[] { new HeldKnock(k, utc) });
                }
                break;
            case KnockReply r when _sent.TryGetValue(r.KnockId, out var sent) && r.FromId == sent.Knock.ToId:
                if (r.Answer is KnockAnswer.Seen or KnockAnswer.Queued)
                {
                    if (sent.Delivered || sent.Answer != null) return; // already heard, or already answered
                    sent.Delivered = true;
                }
                else
                {
                    if (sent.Answer == r.Answer) return;
                    sent.Answer = r.Answer;
                }
                Replied?.Invoke(sent, r);
                break;
        }
    }
}
