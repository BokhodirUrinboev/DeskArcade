using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using DeskArcade.Net;

namespace DeskArcade.Games;

/// <summary>
/// A quiz as one copy of Desk Arcade takes part in it, without any UI: alone against the computer, or in a
/// <see cref="RoomLink"/> room as its host or as a guest. The host runs the <see cref="QuizMatch"/> and sends every player
/// their own <see cref="QuizView"/> ("qv|json") about four times a second and at once when something changes, the question
/// in that player's language. Guests send numbered actions ("qa|game|seq|kind|…": their language, their pick) and send
/// them again until a view acknowledges them; the host takes each player's actions strictly in order, so a repeat or one
/// that overtook another never counts twice. Between quizzes the room opens again, so a late co-worker can join the next.
/// </summary>
public sealed class QuizTable
{
    public enum TableMode { Idle, Solo, Hosting, Guest }

    public const double SendEvery = 0.25, ResendEvery = 0.3;

    readonly RoomLink _room;
    readonly Random _rng;
    readonly List<(int Seq, string Message)> _outbox = new(); // guest: actions not yet acknowledged
    readonly int[] _lastSeq = new int[RoomLink.MaxCapacity];  // host: the last action handled, by room seat
    readonly string[] _roomLang = Enumerable.Repeat("en", RoomLink.MaxCapacity).ToArray(); // host: each room seat's language
    int[] _roomSeatOf = Array.Empty<int>(); // host: game seat → room seat (−1 for a computer player)
    string[] _names = Array.Empty<string>();
    Func<string, string> _title = _ => "";
    int _game, _nextSeq, _langAsked = -1;
    (int Game, int Question, int Place) _picked = (-1, -1, -1); // guest: my pick, until the host's view has it
    double _sendT, _resendT, _sinceView;

    public QuizTable(RoomLink room, Random rng)
    {
        _room = room;
        _rng = rng;
    }

    public RoomLink Room => _room;
    public TableMode Mode { get; private set; }
    /// <summary>The quiz itself, for the host and alone; null for a guest.</summary>
    public QuizMatch? Match { get; private set; }
    /// <summary>What this player sees; null until a quiz starts.</summary>
    public QuizView? View { get; private set; }
    /// <summary>This player's language, for the questions: "en", "ru" or "uz".</summary>
    public string Language { get; set; } = "en";
    public bool Daily { get; private set; }
    public int Game => _game;

    /// <summary>Raised when <see cref="View"/> changes.</summary>
    public event Action? Changed;

    /// <summary>Seconds left in the phase: counted from the host's clock, or down from the last view a guest was sent.</summary>
    public double Left => Mode == TableMode.Guest ? Math.Max(0, (View?.Left ?? 0) - _sinceView) : Match?.Left ?? 0;

    /// <summary>My pick on the current question: the view's, or for a guest the one sent and not yet in a view.</summary>
    public int MyPick => View is not { } v ? -1
        : v.Mine >= 0 ? v.Mine
        : _picked.Game == v.Game && _picked.Question == v.Index ? _picked.Place : -1;

    // ------------------------------------------------------------------ starting and leaving

    /// <summary>A quiz with no network: me (seat 0) and computer players.</summary>
    public void StartSolo(IReadOnlyList<QuizRound> rounds, Func<string, string> title, string[] names, bool[] cpu, int level, bool daily)
    {
        _room.Stop();
        Mode = TableMode.Solo;
        Daily = daily;
        _names = names;
        _title = title;
        _roomSeatOf = cpu.Select(c => c ? -1 : 0).ToArray();
        _game++;
        Match = new QuizMatch(rounds, cpu, level, _rng);
        Refresh();
    }

    public void Host(string? code = null)
    {
        Match = null;
        View = null;
        Daily = false;
        _room.Host(code);
        Mode = _room.IsHost ? TableMode.Hosting : TableMode.Idle;
        Changed?.Invoke();
    }

    public void Join(string code, IPEndPoint? address = null)
    {
        Match = null;
        View = null;
        Daily = false;
        _outbox.Clear();
        _nextSeq = 0;
        _picked = (-1, -1, -1);
        Mode = TableMode.Guest;
        _room.Join(code, address);
        Changed?.Invoke();
    }

    public void Leave()
    {
        _room.Stop();
        Match = null;
        View = null;
        Mode = TableMode.Idle;
        Changed?.Invoke();
    }

    /// <summary>Back to nothing after a quiz alone (a room is left with <see cref="Leave"/>).</summary>
    public void Close()
    {
        if (Mode != TableMode.Solo) return;
        Match = null;
        View = null;
        Mode = TableMode.Idle;
        Changed?.Invoke();
    }

    /// <summary>
    /// Host: a quiz for everyone in the room, computer players filling up to <paramref name="players"/> seats. The room
    /// closes while it runs. <paramref name="cpuName"/> names the n-th computer player (from 1).
    /// </summary>
    public bool StartRoom(int players, IReadOnlyList<QuizRound> rounds, Func<string, string> title, string myName, Func<int, string> cpuName, int level)
    {
        if (Mode != TableMode.Hosting || !_room.IsHost) return false;
        _room.Open = false; // close the room first, so nobody joins or leaves between the roster and the first question
        foreach (var gone in _room.Seats().Where(s => !s.Connected && s.Seat > 0)) _room.Kick(gone.Seat);
        var seats = _room.Seats().Where(s => s.Connected).ToList();
        int humans = seats.Count;
        players = Math.Clamp(Math.Max(players, humans), 1, RoomLink.MaxCapacity);
        _names = seats.Select(s => s.Seat == 0 ? myName : s.Name).Concat(Enumerable.Range(1, players - humans).Select(cpuName)).ToArray();
        var cpu = Enumerable.Range(0, players).Select(i => i >= humans).ToArray();
        _roomSeatOf = Enumerable.Range(0, players).Select(i => i < humans ? seats[i].Seat : -1).ToArray();
        _title = title;
        Daily = false;
        _game++;
        Array.Clear(_lastSeq); // action numbers start again with every quiz (actions name their quiz)
        Match = new QuizMatch(rounds, cpu, level, _rng);
        _sendT = SendEvery;
        Refresh();
        return true;
    }

    // ------------------------------------------------------------------ playing

    /// <summary>My answer: the place (0–3) of the answer clicked. False when it can't count (not now, or already answered).</summary>
    public bool Pick(int place)
    {
        if (View is not { } v || v.Stage != QuizPhase.Answer || MyPick >= 0 || place is < 0 or > 3) return false;
        if (Mode == TableMode.Guest)
        {
            _picked = (v.Game, v.Index, place);
            SendAction("pick", v.Index.ToString(System.Globalization.CultureInfo.InvariantCulture), place.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Changed?.Invoke();
            return true;
        }
        if (Match == null || !Match.Answer(0, v.Index, place)) return false;
        Refresh();
        return true;
    }

    /// <summary>Moves things on by <paramref name="dt"/> seconds: the quiz (host, alone), the messages (host, guest).</summary>
    public void Tick(double dt)
    {
        switch (Mode)
        {
            case TableMode.Solo when Match != null:
                Match.Update(dt);
                if (View == null || Match.Version != View.Version) Refresh();
                break;
            case TableMode.Hosting:
                HostTick(dt);
                break;
            case TableMode.Guest:
                GuestTick(dt);
                break;
        }
    }

    void Refresh()
    {
        if (Match == null) return;
        View = QuizView.Of(Match, 0, Language, _game, _names, _title(Language), Daily, 0);
        Changed?.Invoke();
    }

    void HostTick(double dt)
    {
        while (_room.TryReceive(out var msg)) HostHandle(msg.Seat, msg.Body);
        if (Match == null) return;
        var seats = _room.Seats();
        for (int p = 0; p < _roomSeatOf.Length; p++)
            if (_roomSeatOf[p] > 0) Match.SetActive(p, seats.Any(s => s.Seat == _roomSeatOf[p] && s.Connected));
        Match.Update(dt);
        if (Match.Over && !_room.Open) _room.Open = true; // late co-workers can join the next quiz
        bool changed = View == null || Match.Version != View.Version;
        if (changed) Refresh();
        if ((_sendT += dt) < SendEvery && !changed) return;
        _sendT = 0;
        foreach (var s in seats.Where(s => s.Seat > 0 && s.Connected))
        {
            int g = Array.IndexOf(_roomSeatOf, s.Seat);
            if (g < 0) continue;
            string lang = _roomLang[s.Seat];
            _room.SendTo(s.Seat, "qv|" + JsonSerializer.Serialize(QuizView.Of(Match, g, lang, _game, _names, _title(lang), false, _lastSeq[s.Seat])));
        }
    }

    /// <summary>Host: one action from a guest, taken only if it is the next one in order from them, for this quiz.</summary>
    void HostHandle(int roomSeat, string body)
    {
        var f = body.Split('|');
        if (f.Length < 4 || f[0] != "qa" || roomSeat <= 0 || roomSeat >= RoomLink.MaxCapacity ||
            !int.TryParse(f[1], out int game) || game != _game || !int.TryParse(f[2], out int seq) || seq != _lastSeq[roomSeat] + 1)
            return;
        _lastSeq[roomSeat] = seq; // handled, whether or not it counts; the view tells the player
        _sendT = SendEvery;       // and the acknowledgement goes out at once
        switch (f[3])
        {
            case "lang" when f.Length == 5 && QuizPack.Languages.Contains(f[4]):
                _roomLang[roomSeat] = f[4];
                break;
            case "pick" when f.Length == 6 && Match != null && int.TryParse(f[4], out int question) && int.TryParse(f[5], out int place):
                int g = Array.IndexOf(_roomSeatOf, roomSeat);
                if (g >= 0 && Match.Answer(g, question, place)) Refresh();
                break;
        }
    }

    void GuestTick(double dt)
    {
        QuizView? latest = null;
        while (_room.TryReceive(out var msg))
        {
            if (!msg.Body.StartsWith("qv|", StringComparison.Ordinal)) continue;
            try
            {
                if (JsonSerializer.Deserialize<QuizView>(msg.Body[3..]) is { } v && (latest == null || Newer(v, latest))) latest = v;
            }
            catch (JsonException) { }
        }
        // UDP can deliver out of order: only ever move forward
        if (latest != null && (View == null || Newer(latest, View)))
        {
            if (View == null || latest.Game != View.Game)
            {
                _outbox.Clear(); // actions belong to one quiz
                _nextSeq = 0;
            }
            View = latest;
            _sinceView = 0;
            if (View.Lang != QuizPack.AnyLanguage && View.Lang != Language && _langAsked != View.Game)
            {
                _langAsked = View.Game; // ask once a quiz: a pack without my language stays in its own
                SendAction("lang", Language);
            }
            Changed?.Invoke();
        }
        else _sinceView += dt;
        if (View != null) _outbox.RemoveAll(o => o.Seq <= View.Ack);
        if (_outbox.Count > 0 && (_resendT += dt) >= ResendEvery)
        {
            _resendT = 0;
            foreach (var (_, message) in _outbox) _room.SendToHost(message);
        }
    }

    static bool Newer(QuizView a, QuizView b) => (a.Game, a.Version, a.Ack).CompareTo((b.Game, b.Version, b.Ack)) > 0;

    /// <summary>Guest: queues an action; actions go out in order and are sent again until a view acknowledges them.</summary>
    void SendAction(params string[] parts)
    {
        if (View is not { } v) return;
        _nextSeq = Math.Max(_nextSeq, v.Ack) + 1;
        string message = FormattableString.Invariant($"qa|{v.Game}|{_nextSeq}|{string.Join("|", parts)}");
        _outbox.Add((_nextSeq, message));
        _room.SendToHost(message);
        _resendT = 0;
    }

    /// <summary>True while a guest still has actions the host hasn't acknowledged.</summary>
    public bool Sending => _outbox.Count > 0;
}
