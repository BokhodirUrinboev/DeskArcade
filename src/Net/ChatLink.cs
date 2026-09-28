using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DeskArcade.Net;

public enum ChatSend { Sent, Empty, TooSoon, TooMany }

public enum ChatEventKind { None, Message, Delivered, Refused, Knock }

/// <summary>What an incoming chat message meant: a message to show, a receipt for one of ours, a refusal, or a knock.</summary>
public readonly record struct ChatEvent(ChatEventKind Kind, int Id = 0, string Text = "");

/// <summary>
/// Chat between the two players of a <see cref="LanLink"/>, and nobody else: only the co-worker you are paired with can
/// write to you, nothing is stored, and it is off until you open the chat. UI-free, so the format, the cleaning, the
/// limits and the receipts can be tested without a network.
/// <para>
/// Wire format: "ch|msg|{id}|{text}" (the text may contain '|'), acknowledged by "ch|got|{id}", or by "ch|off|{id}" when
/// the other side has chat turned off. UDP may lose any of them, so a message is sent again every
/// <see cref="ResendSeconds"/> until its receipt arrives, and given up after <see cref="GiveUpSeconds"/>; the receiver
/// acknowledges every copy but shows each id once.
/// </para>
/// <para>
/// Limits: a sender writes at most one message every <see cref="SendEvery"/> seconds with <see cref="MaxInFlight"/> on
/// the way; a receiver takes a burst of <see cref="Burst"/> and then one a second, and leaves the rest unacknowledged
/// (so the sender sees them go undelivered). Text is cleaned on both sides: control and direction-override characters
/// out, whitespace runs to one space, at most <see cref="MaxLength"/> characters.
/// </para>
/// </summary>
public sealed class ChatLink
{
    public const string Tag = "ch";
    public const int MaxLength = 280, MaxInFlight = 4, RememberIds = 256, Burst = 6;
    public const double SendEvery = 0.4, ResendSeconds = 0.6, GiveUpSeconds = 20, RefillSeconds = 1;

    sealed class Outgoing
    {
        public required int Id;
        public required string Text;
        public double First, Last;
    }

    readonly Action<string> _send;
    readonly List<Outgoing> _out = new();
    readonly HashSet<int> _seen = new();
    readonly Queue<int> _seenOrder = new();
    int _nextId;
    double _lastSent = double.NegativeInfinity, _tokens = Burst, _tokensAt;

    /// <param name="firstId">The id before the first message's; random by default, so a new session's ids don't collide with the last one's.</param>
    public ChatLink(Action<string> send, int? firstId = null)
    {
        _send = send;
        _nextId = firstId ?? Random.Shared.Next(1, 500_000_000);
    }

    /// <summary>Messages still waiting for their receipt.</summary>
    public bool Busy => _out.Count > 0;

    /// <summary>
    /// Makes text safe to show: control characters and the bidirectional overrides that can make text read differently
    /// from what it is are dropped, tabs and line breaks become spaces, runs of spaces one, and it is cut to
    /// <see cref="MaxLength"/> without splitting a character.
    /// </summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(Math.Min(text.Length, MaxLength + 8));
        bool space = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                space = sb.Length > 0;
                continue;
            }
            if (char.IsControl(c) || c is >= '‪' and <= '‮' || c is >= '⁦' and <= '⁩' || c is '‎' or '‏' or '؜') continue;
            if (space) sb.Append(' ');
            space = false;
            sb.Append(c);
            if (sb.Length >= MaxLength) break;
        }
        if (sb.Length > MaxLength) sb.Length = MaxLength;
        if (sb.Length > 0 && char.IsHighSurrogate(sb[^1])) sb.Length--; // cut between the halves of a character
        return sb.ToString();
    }

    public ChatSend Send(string text, double now, out int id)
    {
        id = 0;
        text = Clean(text);
        if (text.Length == 0) return ChatSend.Empty;
        if (now - _lastSent < SendEvery) return ChatSend.TooSoon;
        if (_out.Count >= MaxInFlight) return ChatSend.TooMany;
        id = _nextId = _nextId >= 999_999_999 ? 1 : _nextId + 1;
        var o = new Outgoing { Id = id, Text = text, First = now, Last = now };
        _out.Add(o);
        _lastSent = now;
        Transmit(o);
        return ChatSend.Sent;
    }

    /// <summary>Sends again what is still unacknowledged; returns the ids given up on (never delivered).</summary>
    public List<int> Tick(double now)
    {
        var gaveUp = new List<int>();
        for (int i = _out.Count - 1; i >= 0; i--)
        {
            var o = _out[i];
            if (now - o.First >= GiveUpSeconds)
            {
                _out.RemoveAt(i);
                gaveUp.Add(o.Id);
            }
            else if (now - o.Last >= ResendSeconds)
            {
                o.Last = now;
                Transmit(o);
            }
        }
        gaveUp.Reverse();
        return gaveUp;
    }

    /// <param name="accepting">Chat is turned on here: a message is taken; otherwise it is refused and reported as a knock.</param>
    public ChatEvent Handle(string message, bool accepting, double now)
    {
        var f = message.Split('|', 4);
        if (f.Length < 3 || f[0] != Tag || !int.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) || id <= 0) return default;
        switch (f[1])
        {
            case "msg" when f.Length == 4:
                if (!accepting)
                {
                    _send($"{Tag}|off|{id}");
                    return new ChatEvent(ChatEventKind.Knock, id);
                }
                if (_seen.Contains(id))
                {
                    _send($"{Tag}|got|{id}"); // a copy of one we have: its receipt went missing
                    return default;
                }
                if (!Spend(now)) return default; // too many too fast: left unacknowledged
                _send($"{Tag}|got|{id}");
                Remember(id);
                string text = Clean(f[3]);
                return text.Length == 0 ? default : new ChatEvent(ChatEventKind.Message, id, text);
            case "got":
                return _out.RemoveAll(o => o.Id == id) > 0 ? new ChatEvent(ChatEventKind.Delivered, id) : default;
            case "off":
                return _out.RemoveAll(o => o.Id == id) > 0 ? new ChatEvent(ChatEventKind.Refused, id) : default;
        }
        return default;
    }

    /// <summary>A new LAN session: nothing on the way, nothing remembered.</summary>
    public void Reset()
    {
        _out.Clear();
        _seen.Clear();
        _seenOrder.Clear();
        _tokens = Burst;
    }

    bool Spend(double now)
    {
        _tokens = Math.Min(Burst, _tokens + (now - _tokensAt) / RefillSeconds);
        _tokensAt = now;
        if (_tokens < 1) return false;
        _tokens--;
        return true;
    }

    void Remember(int id)
    {
        _seen.Add(id);
        _seenOrder.Enqueue(id);
        while (_seenOrder.Count > RememberIds) _seen.Remove(_seenOrder.Dequeue());
    }

    void Transmit(Outgoing o) => _send(string.Create(CultureInfo.InvariantCulture, $"{Tag}|msg|{o.Id}|{o.Text}"));
}

/// <summary>One-click reactions a player can send the co-worker; only the index crosses the network ("rx|index").</summary>
public enum Reaction { ThumbsUp, Party, Coffee, Laugh, Fire, Heart, Star, Lunch }
