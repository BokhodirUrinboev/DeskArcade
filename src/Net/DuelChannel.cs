using System;
using System.Collections.Generic;
using System.Globalization;

namespace DeskArcade.Net;

/// <summary>
/// Reliable, ordered events for turn-based LAN games (Mini Golf and Archery duels) over the lossy
/// <see cref="LanLink"/>. Every event gets the next sequence number and is re-sent until the peer's
/// cumulative acknowledgement covers it; the receiver delivers each number once and in order, so both
/// screens apply the same events in the same order. UI-free, so it can be tested with a lossy fake link.
/// Wire format: "{prefix}e|seq|body" and "{prefix}a|seq", where body may itself contain '|'.
/// </summary>
public sealed class DuelChannel
{
    public const double ResendSeconds = 0.3;

    readonly Action<string> _send;
    readonly string _event, _ack;
    readonly List<(int Seq, string Body)> _pending = new();
    int _sent, _applied;
    double _resendT;

    /// <param name="prefix">A short tag per game ("g" for golf), so two games never read each other's events.</param>
    public DuelChannel(string prefix, Action<string> send)
    {
        _send = send;
        _event = prefix + "e";
        _ack = prefix + "a";
    }

    /// <summary>Events sent but not yet acknowledged.</summary>
    public int Pending => _pending.Count;

    /// <summary>Forgets everything: call when a LAN session starts, on both sides.</summary>
    public void Reset()
    {
        _pending.Clear();
        _sent = _applied = 0;
        _resendT = 0;
    }

    public void Send(string body)
    {
        _sent++;
        _pending.Add((_sent, body));
        Transmit(_sent, body);
    }

    /// <summary>Re-sends unacknowledged events every <see cref="ResendSeconds"/>.</summary>
    public void Tick(double dt)
    {
        if (_pending.Count == 0 || (_resendT += dt) < ResendSeconds) return;
        _resendT = 0;
        foreach (var (seq, body) in _pending) Transmit(seq, body);
    }

    /// <summary>
    /// Offers one incoming LAN message. Returns false if it isn't for this channel; otherwise adds any
    /// newly delivered event body to <paramref name="delivered"/> and returns true.
    /// </summary>
    public bool Handle(string message, List<string> delivered)
    {
        var f = message.Split('|', 3);
        if (f.Length >= 2 && f[0] == _ack && int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int acked))
        {
            _pending.RemoveAll(p => p.Seq <= acked);
            return true;
        }
        if (f.Length < 3 || f[0] != _event || !int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int seq)) return false;
        if (seq == _applied + 1)
        {
            _applied = seq;
            delivered.Add(f[2]);
        }
        // a repeat or a gap: acknowledge what we have, and the sender re-sends the rest in order
        _send($"{_ack}|{_applied}");
        return true;
    }

    void Transmit(int seq, string body) => _send($"{_event}|{seq}|{body}");
}
