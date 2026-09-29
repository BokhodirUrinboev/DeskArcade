using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Threading;
using DeskArcade.Net;

namespace DeskArcade;

/// <summary>
/// Chat and reactions with the co-worker on the LAN link, between the overlay and the network. Chat is off until you
/// open it (tray or ☰ → Play over LAN → Chat…) and can be turned off again in the same menu; while it is off, a
/// co-worker's message is refused and you hear once that they want to chat. What was said lives only in memory, for
/// the current pairing. A message that arrives with the chat window closed shows as a bubble under the scoreboard, and
/// a click on it opens the chat. Reactions (<see cref="Reaction"/>) float up the co-worker's screen with your name.
/// </summary>
public sealed class ChatHub
{
    public enum State { Sending, Delivered, NotDelivered, Received, Note }

    public sealed class Line
    {
        public required int Id;
        public required string Text;
        public required State State;
        public bool Mine => State is State.Sending or State.Delivered or State.NotDelivered;
    }

    const int MaxLines = 200;
    const double ReactionEvery = 0.35;

    readonly OverlayWindow _w;
    readonly ChatLink _link;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(ChatLink.ResendSeconds / 2) };
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly List<Line> _lines = new();
    int _session = -1;
    bool _knocked;
    int _held;          // messages that came in during a focus block, shown at the break
    string _heldLast = "";
    double _lastReactionIn = double.NegativeInfinity, _lastReactionOut = double.NegativeInfinity;

    public ChatHub(OverlayWindow w)
    {
        _w = w;
        _link = new ChatLink(w.Lan.Send);
        _timer.Tick += (_, _) => Tick();
        w.Lan.ChatReceived += msg => Dispatcher.UIThread.Post(() => OnChat(msg));
        w.Lan.ReactionReceived += r => Dispatcher.UIThread.Post(() => OnReaction(r));
        w.Lan.StateChanged += () => Dispatcher.UIThread.Post(OnLanState);
    }

    double Now => _clock.Elapsed.TotalSeconds;

    /// <summary>Something to redraw: a line added or its state changed, or the link came or went.</summary>
    public event Action? Changed;

    public IReadOnlyList<Line> Lines => _lines;
    public bool Connected => _w.Lan.Connected;
    public string Peer => _w.Lan.PeerName;
    public bool Enabled => _w.Settings.LanChat;

    public string MenuHeader => Connected && Peer.Length > 0 ? L.F("Chat with {0}…", Peer) : L.T("Chat with a co-worker…");

    /// <summary>Opens the chat window, which turns chat on: opening it is saying yes to it.</summary>
    public void Open()
    {
        if (!Enabled) SetEnabled(true);
        ChatWindow.ShowFor(_w, this);
    }

    public void SetEnabled(bool on)
    {
        _w.Settings.LanChat = on;
        _w.SaveSettings();
        if (on) _knocked = false;
        Changed?.Invoke();
    }

    public ChatSend Send(string text)
    {
        if (!Connected) return ChatSend.Empty;
        var result = _link.Send(text, Now, out int id);
        if (result == ChatSend.Sent)
        {
            Add(new Line { Id = id, Text = ChatLink.Clean(text), State = State.Sending });
            _w.Stats.Add("chat.sent");
            _timer.Start();
        }
        return result;
    }

    public void React(Reaction r)
    {
        if (!Connected) return;
        if (Now - _lastReactionOut < ReactionEvery) return;
        _lastReactionOut = Now;
        _w.Lan.SendReaction(r);
        _w.Stats.Add("chat.reactions");
        _w.ShowReaction(r, L.F("to {0}", Peer), mine: true);
    }

    void OnChat(string msg)
    {
        var e = _link.Handle(msg, Enabled && Connected, Now);
        switch (e.Kind)
        {
            case ChatEventKind.Message:
                Add(new Line { Id = e.Id, Text = e.Text, State = State.Received });
                if (_w.Office.Quiet && !ChatWindow.IsOpen)
                {
                    _held++; // delivered (the receipt went back), shown at the break
                    _heldLast = e.Text;
                }
                else if (!ChatWindow.IsOpen) _w.ShowChatBubble(Peer, e.Text);
                else _w.Sound.Play("chat", 0.3);
                break;
            case ChatEventKind.Delivered:
                SetState(e.Id, State.Delivered);
                break;
            case ChatEventKind.Refused:
                SetState(e.Id, State.NotDelivered);
                Add(new Line { Id = 0, Text = L.F("{0} has chat turned off. It turns on when they open the chat.", Peer), State = State.Note });
                break;
            case ChatEventKind.Knock when !_knocked:
                _knocked = true;
                _w.Sound.Play("chat", 0.35);
                _w.Notice(L.F("{0} wants to chat", Peer), L.T("Play over LAN → Chat… to answer"), Color.FromRgb(77, 163, 255));
                break;
        }
    }

    /// <summary>The focus block is over: what came in during it shows as one bubble.</summary>
    public void ReleaseHeld()
    {
        if (_held == 0) return;
        int held = _held;
        _held = 0;
        if (!Connected || ChatWindow.IsOpen) return;
        _w.ShowChatBubble(Peer, held == 1 ? _heldLast : L.F("{0} messages while you focused · the last: {1}", held, _heldLast));
    }

    void OnReaction(Reaction r)
    {
        if (_w.Office.Quiet) return; // a focus block: a reaction is not worth keeping for later
        if (!Connected || Now - _lastReactionIn < ReactionEvery) return; // a flood of them shows as a trickle
        _lastReactionIn = Now;
        _w.ShowReaction(r, L.F("from {0}", Peer), mine: false);
    }

    void Tick()
    {
        foreach (int id in _link.Tick(Now)) SetState(id, State.NotDelivered);
        if (!_link.Busy) _timer.Stop();
    }

    /// <summary>A new pairing (or none) starts the conversation afresh: nothing said before carries over.</summary>
    void OnLanState()
    {
        int session = Connected ? _w.Lan.Session : -1;
        if (session == _session) return;
        _session = session;
        _link.Reset();
        _lines.Clear();
        _knocked = false;
        _held = 0;
        _timer.Stop();
        Changed?.Invoke();
    }

    void Add(Line line)
    {
        _lines.Add(line);
        if (_lines.Count > MaxLines) _lines.RemoveAt(0);
        Changed?.Invoke();
    }

    void SetState(int id, State state)
    {
        foreach (var line in _lines)
            if (line.Id == id && line.Mine) line.State = state;
        Changed?.Invoke();
    }
}
