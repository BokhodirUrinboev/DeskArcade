using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DeskArcade.Engine;
using DeskArcade.Net;

namespace DeskArcade.Games;

/// <summary>What every player of a room card game sees, besides the game's own part of the view.</summary>
public abstract class CardRoomView
{
    public int Game { get; set; }
    public int Seat { get; set; }
    public string[] Names { get; set; } = Array.Empty<string>();
    /// <summary>Seats the computer plays now (a person who dropped out included).</summary>
    public bool[] Cpu { get; set; } = Array.Empty<bool>();
    /// <summary>Seats a person sat down in when the game started, even if the computer has taken over since.</summary>
    public bool[] Human { get; set; } = Array.Empty<bool>();
    public bool Over { get; set; }
    public int Version { get; set; }
    /// <summary>The last action number the host has handled from this player.</summary>
    public int Ack { get; set; }

    public int Players => Names.Length;
}

/// <summary>
/// A move in a room game: its kind, up to two numbers, and a little text (a stroke, a guess), as a player's action
/// message carries them. The text is cleaned of "|" and line breaks on the way.
/// </summary>
public readonly record struct RoomMove(string Kind, int A = -1, int B = -1, string Text = "");

/// <summary>
/// What is due at a table when nobody is waited on: a computer's move for <see cref="Seat"/>, or the host's own step
/// (<see cref="Seat"/> −1: the next hand, the next street of a run-out), after <see cref="Pause"/> seconds.
/// </summary>
public readonly record struct TableDue(int Seat, double Pause);

/// <summary>
/// A card game for a room of co-workers (<see cref="RoomLink"/>) or against computer players, run the way
/// <see cref="LastCardGame"/> and <see cref="DurakGame"/> run theirs, for the games that came after them (Poker, Hearts):
/// the host runs the rules and sends each player only their own view ("&lt;tag&gt;s|json") about four times a second;
/// players send numbered moves ("&lt;tag&gt;a|seq|kind|a|b") and re-send them until the view acknowledges them, and the
/// host takes each player's moves strictly in order. The host's game runs on its own timer, so it goes on while the
/// host's overlay shows something else. Computer players fill the empty seats and take over for anyone who drops out.
/// Over the two-player link the host's table opens a room and invites the other player ("&lt;tag&gt;k|code").
/// The table has a grip (or right-drag it) and remembers where it was put; everything is drawn in table coordinates
/// and the cards slide to their places (<see cref="TableCards"/>). When nothing is due and nothing moves, no frames.
/// </summary>
public abstract class CardRoomGame<TView> : MiniGame, IRoomGame where TView : CardRoomView
{
    protected enum RoomMode { Idle, Solo, Hosting, Guest }

    const double SendEvery = 0.25, ResendEvery = 0.3, DemoEvery = 0.9;
    protected static readonly Color Gold = Color.FromRgb(255, 209, 102);
    protected static readonly Color Soft = Color.FromRgb(205, 225, 212);

    readonly Canvas _root = new(), _canvas = new(), _top = new();
    readonly TranslateTransform _rootTr = new();
    readonly DragHandle _handle;
    readonly List<(Rect Box, Action Click)> _clickables = new();
    readonly RoomLink _room;
    readonly List<(int Seq, string Message)> _outbox = new(); // guest: our moves, re-sent until acknowledged
    readonly Avalonia.Threading.DispatcherTimer _hostTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    readonly System.Diagnostics.Stopwatch _hostClock = new();

    int[] _seatOfGuest = Array.Empty<int>(); // host: room seat → game seat (−1: not playing)
    int[] _lastSeq = Array.Empty<int>();       // host: last action number handled per game seat
    bool[] _dropped = Array.Empty<bool>();     // host: seats the computer took over after their player dropped out
    int _nextSeq, _drawnVersion = -1, _drawnGame = -1, _bridgedSession = -1;
    double _wait = -1, _sendT, _resendT, _demoT, _inviteT;
    (int Seat, int Version) _dueKey = (-2, -1);
    (Task<RoomMove?> Task, int Seat, int Version)? _thinking;
    string _drawnPanel = "";
    long _demoOverAt;
    bool _pressing;
    Rect _area;
    Rect _table;
    Vec2 _origin;
    Size _drawnSize;

    protected CardRoomGame(IGameHost host) : base(host)
    {
        Cards = new TableCards(Anims);
        _room = new RoomLink { Game = Id, Capacity = MaxPlayers };
        _root.RenderTransformOrigin = RelativePoint.TopLeft;
        _root.RenderTransform = _rootTr;
        _root.Children.Add(_canvas);
        _root.Children.Add(Cards.Layer);
        _root.Children.Add(_top);
        Layer.Children.Add(_root);
        _handle = new DragHandle(host, Id, Title);
        Layer.Children.Add(_handle.Visual);
        _room.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(Changed);
        _room.MessageArrived += () => Avalonia.Threading.Dispatcher.UIThread.Post(Host.Wake);
        _hostTimer.Tick += (_, _) => HostTick();
    }

    // ------------------------------------------------------------------ what a game supplies

    /// <summary>Two letters that start this game's messages, e.g. "pk".</summary>
    protected abstract string Tag { get; }
    /// <summary>The computer players a --demo run deals in.</summary>
    protected abstract int DemoCpus { get; }
    protected abstract bool HasRules { get; }
    /// <summary>A new game for <paramref name="players"/> seats (names and computers are set already).</summary>
    protected abstract void NewRules(int players);
    protected abstract void DropRules();
    protected abstract int RulesVersion { get; }
    protected abstract bool RulesAct(int seat, RoomMove move);
    /// <summary>The game's part of <paramref name="seat"/>'s view; the common part is filled in afterwards.</summary>
    protected abstract TView ViewOf(int seat);
    /// <summary>What is due now that isn't a person's to do, or null while the table waits on a person (or is over).</summary>
    protected abstract TableDue? NextDue();
    /// <summary>The host's own step (<see cref="TableDue.Seat"/> −1): true if something changed.</summary>
    protected abstract bool AutoStep();
    /// <summary>A computer's move for <paramref name="seat"/>; a slow one thinks on another thread and finishes later.</summary>
    protected abstract Task<RoomMove?> Think(int seat);
    /// <summary>A --demo guest's move from its view alone (it has no rules), or null to wait.</summary>
    protected abstract RoomMove? GuestDemoMove(TView v);
    /// <summary>The seat the table waits on, for the scoreboard's chip (−1: nobody in particular).</summary>
    protected abstract int TurnOf(TView v);
    /// <summary>Whether the table waits on this player now.</summary>
    protected abstract bool MyMove(TView v);
    /// <summary>The effects of what just happened in the view, and the end of the game; called on every frame with a view.</summary>
    protected abstract void Announce(TView v);
    /// <summary>Lays out the table for a view (between <see cref="TableCards.Begin"/> and <see cref="TableCards.End"/>).</summary>
    protected abstract void DrawTable(TView v, TView? prev, bool newDeal);
    /// <summary>The line under the title on the start panel.</summary>
    protected abstract string Tagline { get; }
    /// <summary>The solo games the start panel offers: button text and computer players.</summary>
    protected abstract IEnumerable<(string Text, int Cpus)> SoloChoices { get; }

    public abstract int MinPlayers { get; }
    public abstract int MaxPlayers { get; }

    /// <summary>The start panel's extra line of settings (the game's length...), at <paramref name="y"/>.</summary>
    protected virtual void DrawStartExtras(double y) { }
    /// <summary>A card the new layout didn't place: it fades where it is unless the game sends it somewhere.</summary>
    protected virtual void Leave(TableCards.Card card, TView v, TView? prev) => Cards.Remove(card.Key, seconds: 0.15);
    /// <summary>A move was taken at this table (by a person or a computer): a sound, say.</summary>
    protected virtual void Moved(int seat, RoomMove move) => Host.Sound.Play("board", 0.4, 1.4);
    /// <summary>A press on the table that should keep the mouse (a slider): true to capture it.</summary>
    protected virtual bool PressAt(Point p) => false;
    /// <summary>The captured press moved to <paramref name="p"/> (table coordinates).</summary>
    protected virtual void DragTo(Point p) { }
    /// <summary>The captured press ended.</summary>
    protected virtual void Released() { }
    /// <summary>The table's size, before it is fitted to the screen.</summary>
    protected virtual Size TableSize => new(940, 580);

    // ------------------------------------------------------------------ what a game can use

    protected TableCards Cards { get; }
    protected RoomMode Mode { get; private set; }
    protected TView? View { get; private set; }
    /// <summary>The view the table was last drawn for.</summary>
    protected TView? Shown { get; private set; }
    protected string[] Names { get; private set; } = Array.Empty<string>();
    protected bool[] CpuSeats { get; private set; } = Array.Empty<bool>();
    protected bool[] HumanSeats { get; private set; } = Array.Empty<bool>();
    protected int GameNumber { get; private set; }
    protected bool Demo { get; private set; }
    /// <summary>The table on the screen, and the same in table coordinates (everything is drawn in these).</summary>
    protected Rect Area => _area;
    protected Rect Table => _table;
    protected Canvas Felt => _canvas;
    protected Canvas Top => _top;
    protected int MySeat => Mode is RoomMode.Hosting or RoomMode.Solo ? 0 : View?.Seat ?? -1;
    protected bool Solo => Mode == RoomMode.Solo;
    /// <summary>The computer plays this seat: a computer player, one that took over, or the --demo's own seat.</summary>
    protected bool IsComputer(int seat) =>
        seat >= 0 && seat < CpuSeats.Length && (CpuSeats[seat] || Demo && seat == 0 && Mode is RoomMode.Solo or RoomMode.Hosting);

    public RoomLink Room => _room;
    public string MinVersion => "1.8.6";
    public override bool SupportsLan => true;
    public override bool HasCpuLevels => true;
    public bool Playing => View != null && !View.Over;

    /// <summary>The scoreboard's chip: the computer (and its level), or in a room whom the table waits on (the next player when it is me).</summary>
    public override Opponent? Opponent
    {
        get
        {
            if (View is not { } v || Mode == RoomMode.Idle || Mode == RoomMode.Guest && _room.State == RoomState.Lost) return null;
            bool? mine = v.Over ? null : MyMove(v);
            if (Solo) return new Opponent(L.T("CPU"), true, CpuLevel, mine);
            return CardTable.Chip(v.Names, false, v.Over ? -1 : TurnOf(v), CardTable.NextSeat(v.Seat, v.Players), mine);
        }
    }

    protected string Name(int seat) => View != null && seat >= 0 && seat < View.Names.Length ? View.Names[seat] : "?";

    /// <summary>The line for the room itself (joining, closed, waiting for the host), or null once a game is on.</summary>
    protected string? RoomStatus()
    {
        if (Mode == RoomMode.Guest && _room.State == RoomState.Joining) return L.F("Joining room {0}…", _room.Code);
        if (Mode == RoomMode.Guest && _room.State == RoomState.Lost) return L.T("The room closed · set up a new game");
        if (Mode == RoomMode.Hosting && !HasRules) return L.F("Room {0} · {1} at the table", _room.Code, _room.Seats().Count(x => x.Connected));
        if (View == null && Mode == RoomMode.Guest) return L.F("In room {0} · waiting for the host to start", _room.Code);
        return null;
    }

    /// <summary>Rivalries: one result per co-worker who sat down at the table, by who finished ahead (<paramref name="standing"/>: higher is better).</summary>
    protected void RecordRivals(TView v, Func<int, double> standing)
    {
        if (Solo) return;
        for (int s = 0; s < v.Players && s < v.Human.Length; s++)
            if (s != v.Seat && v.Human[s]) Host.RecordResult(Id, v.Names[s], Math.Sign(standing(v.Seat) - standing(s)));
    }

    // ------------------------------------------------------------------ starting and ending

    public void StartSolo(int cpus)
    {
        _room.Stop();
        Mode = RoomMode.Solo;
        int players = Math.Clamp(cpus + 1, MinPlayers, MaxPlayers);
        Names = new[] { L.T("You") }.Concat(Enumerable.Range(1, players - 1).Select(i => L.F("CPU {0}", i))).ToArray();
        CpuSeats = Enumerable.Range(0, players).Select(i => i > 0).ToArray();
        HumanSeats = Enumerable.Range(0, players).Select(i => i == 0).ToArray();
        _lastSeq = new int[players];
        _dropped = new bool[players];
        NewGame();
    }

    public void HostRoom(string? code = null)
    {
        DropRules();
        View = null;
        _thinking = null;
        _room.Host(code);
        Mode = _room.IsHost ? RoomMode.Hosting : RoomMode.Idle;
        if (!_room.IsHost)
            Host.Fx.Popup(new Vec2(_area.Center.X, _area.Top + 80), L.T("Couldn't open a room"), Colors.White, 24, 2.4,
                L.F("UDP port {0} is in use", RoomLink.Port));
        Changed();
    }

    public void JoinRoom(string code, System.Net.IPEndPoint? address)
    {
        DropRules();
        View = null;
        _thinking = null;
        _outbox.Clear();
        _nextSeq = 0;
        Mode = RoomMode.Guest;
        _room.Join(code, address);
        Changed();
    }

    public void LeaveRoom()
    {
        _room.Stop();
        DropRules();
        View = null;
        _thinking = null;
        Mode = RoomMode.Idle;
        Changed();
    }

    public void StartRoom(int players)
    {
        if (Mode != RoomMode.Hosting || !_room.IsHost) return;
        _room.Open = false; // close the room first, so nobody joins or leaves between the roster and the deal
        foreach (var gone in _room.Seats().Where(s => !s.Connected && s.Seat > 0)) _room.Kick(gone.Seat);
        var seats = _room.Seats().Where(s => s.Connected).Take(MaxPlayers).ToList();
        int humans = seats.Count;
        players = Math.Clamp(Math.Max(players, humans), MinPlayers, MaxPlayers);
        Names = seats.Select(s => s.Seat == 0 ? LanLink.MyName : s.Name)
            .Concat(Enumerable.Range(1, players - humans).Select(i => L.F("CPU {0}", i))).ToArray();
        CpuSeats = Enumerable.Range(0, players).Select(i => i >= humans).ToArray();
        HumanSeats = Enumerable.Range(0, players).Select(i => i < humans).ToArray();
        _seatOfGuest = Enumerable.Repeat(-1, RoomLink.MaxCapacity).ToArray();
        for (int i = 0; i < seats.Count; i++) _seatOfGuest[seats[i].Seat] = i;
        _lastSeq = new int[players];
        _dropped = new bool[players];
        NewGame();
        _hostClock.Restart();
        _hostTimer.Start();
    }

    /// <summary>Deals a new game with the same seats.</summary>
    protected void NewGame()
    {
        // action numbers carry on across games, so a late copy of a move from the last one can't count in this one
        if (_lastSeq.Length != Names.Length) _lastSeq = new int[Names.Length];
        if (_dropped.Length != Names.Length) _dropped = new bool[Names.Length];
        _thinking = null;
        GameNumber++;
        NewRules(Names.Length);
        RefreshView();
        Host.Sound.Play("whoosh", 0.4, 1.4);
    }

    /// <summary>"New game" on a finished table: the host deals again, a guest asks the host to.</summary>
    protected void NewGameClicked()
    {
        if (Mode is RoomMode.Solo or RoomMode.Hosting && HasRules) NewGame();
        else if (Mode == RoomMode.Guest) SendAction(new RoomMove("again"));
    }

    /// <summary>"Other games…" after a solo game, or leaving the room.</summary>
    protected void LeaveTable()
    {
        if (Solo)
        {
            DropRules();
            View = null;
            Mode = RoomMode.Idle;
            Changed();
        }
        else LeaveRoom();
    }

    // ------------------------------------------------------------------ moves

    /// <summary>This player's move: played on the table here, or sent to the host.</summary>
    protected void Act(RoomMove move)
    {
        if (Mode == RoomMode.Guest) SendAction(move);
        else if (HasRules && View != null && RulesAct(MySeat, move))
        {
            Moved(MySeat, move);
            RefreshView();
        }
        else Host.Fx.Popup(Host.Pointer - new Vec2(0, 40), L.T("not allowed right now"), Colors.White, 18, 1.0);
    }

    void SendAction(RoomMove move)
    {
        _nextSeq = Math.Max(_nextSeq, View?.Ack ?? 0) + 1;
        string text = new((move.Text ?? "").Where(c => c != '|' && !char.IsControl(c)).ToArray());
        var message = string.Create(CultureInfo.InvariantCulture, $"{Tag}a|{_nextSeq}|{move.Kind}|{move.A}|{move.B}") + (text.Length > 0 ? "|" + text : "");
        _outbox.Add((_nextSeq, message));
        _room.SendToHost(message);
        _resendT = 0;
        Moved(View?.Seat ?? -1, move);
    }

    /// <summary>Guest: moves sent that the host hasn't acknowledged yet.</summary>
    protected bool Sending => _outbox.Count > 0;

    // ------------------------------------------------------------------ simulation

    void HostTick()
    {
        double dt = Math.Min(_hostClock.Elapsed.TotalSeconds, 0.5);
        _hostClock.Restart();
        if (Mode != RoomMode.Hosting || !HasRules)
        {
            _hostTimer.Stop();
            return;
        }
        HostUpdate(dt);
        CpuTurns(dt);
    }

    public override bool Update(double dt)
    {
        if (_handle.Dragging)
        {
            _origin = _handle.Move(Host.Pointer, _area.Size);
            Layout();
        }
        if (_pressing) DragTo((Host.Pointer - _origin).ToPoint());
        bool inviting = LanBridge(dt);
        if (Mode == RoomMode.Guest) GuestUpdate(dt);
        else if (Mode == RoomMode.Solo) CpuTurns(dt); // a room's host plays on its own timer (HostTick)
        if (View != null ? (View.Game, View.Version) != (_drawnGame, _drawnVersion) : PanelKey() != _drawnPanel) Draw();
        if (View != null) Announce(View);
        bool animating = Anims.Update(dt);
        // frames only while something is due (a computer's move, the next hand, a slide, a drag, an invitation); a
        // table waiting on the player is still, and Changed() wakes the overlay when anything happens
        return _handle.Dragging || _pressing || animating || inviting || Mode switch
        {
            RoomMode.Solo => _thinking != null || HasRules && NextDue() != null || Demo,
            RoomMode.Guest => _room.State == RoomState.Joining || _outbox.Count > 0 || Demo && _room.State == RoomState.Joined,
            _ => false,
        };
    }

    /// <summary>
    /// Computers' moves and the host's own steps, each after its pause. Alone, this runs from the frames, which only
    /// run while something is due; in a room it runs on the host's timer, since a seat becomes the computer's when its
    /// player drops out. A slow computer (the Expert at Poker) thinks on another thread and is picked up here when done.
    /// </summary>
    void CpuTurns(double dt)
    {
        if (!HasRules) return;
        if (_thinking is { } t)
        {
            if (!t.Task.IsCompleted) return;
            _thinking = null;
            if (t.Version == RulesVersion && t.Task.IsCompletedSuccessfully && t.Task.Result is { } m) Apply(t.Seat, m);
            return;
        }
        if (NextDue() is not { } due)
        {
            _dueKey = (-2, -1);
            return;
        }
        var key = (due.Seat, RulesVersion);
        if (key != _dueKey)
        {
            _dueKey = key;
            _wait = Fx.ReducedMotion ? Math.Min(due.Pause, 0.5) : due.Pause;
        }
        if ((_wait -= dt) > 0) return;
        _dueKey = (-2, -1);
        if (due.Seat < 0)
        {
            if (AutoStep()) RefreshView();
            return;
        }
        var task = Think(due.Seat);
        if (task.IsCompleted)
        {
            if (task.IsCompletedSuccessfully && task.Result is { } move) Apply(due.Seat, move);
        }
        else _thinking = (task, due.Seat, RulesVersion);
    }

    void Apply(int seat, RoomMove move)
    {
        if (!RulesAct(seat, move)) return;
        Moved(seat, move);
        RefreshView(); // Changed() wakes the overlay, so the next pause is timed
    }

    /// <summary>The host's (or the solo player's) own view, made afresh, and the room told at once.</summary>
    protected void RefreshView()
    {
        if (!HasRules) return;
        View = Fill(ViewOf(0), 0);
        _sendT = SendEvery;
        Changed();
    }

    TView Fill(TView v, int seat)
    {
        v.Game = GameNumber;
        v.Seat = seat;
        v.Names = Names;
        v.Cpu = CpuSeats.ToArray();
        v.Human = HumanSeats;
        v.Version = RulesVersion;
        v.Ack = seat < _lastSeq.Length ? _lastSeq[seat] : 0;
        return v;
    }

    void HostUpdate(double dt)
    {
        while (_room.TryReceive(out var msg))
        {
            if (!HasRules || msg.Seat >= _seatOfGuest.Length || _seatOfGuest[msg.Seat] is not (>= 0 and var seat)) continue;
            var f = msg.Body.Split('|');
            // moves are taken strictly in order: a repeat or one that jumped ahead waits for the re-send
            if (f.Length is not (5 or 6) || f[0] != Tag + "a" || !int.TryParse(f[1], out int seq) || seq != _lastSeq[seat] + 1 ||
                !int.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int a) ||
                !int.TryParse(f[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int b))
                continue;
            _lastSeq[seat] = seq; // handled, whether or not the rules accept it; the view tells the player
            var move = new RoomMove(f[2], a, b, f.Length == 6 ? f[5] : "");
            if (move.Kind == "again")
            {
                if (View is { Over: true }) NewGame();
            }
            else if (RulesAct(seat, move)) Moved(seat, move);
            RefreshView();
        }
        if (!HasRules) return;

        // a player who dropped out is played by the computer until they come back
        var seats = _room.Seats();
        for (int roomSeat = 1; roomSeat < _seatOfGuest.Length; roomSeat++)
        {
            if (_seatOfGuest[roomSeat] is not (>= 0 and var g)) continue;
            bool connected = seats.Any(s => s.Seat == roomSeat && s.Connected);
            if (!connected && !CpuSeats[g])
            {
                CpuSeats[g] = _dropped[g] = true;
                Host.Fx.Popup(new Vec2(_area.Center.X, _area.Top + 60), L.F("{0} left · the computer plays for them", Names[g]), Colors.White, 20, 2.0);
                RefreshView();
            }
            else if (connected && _dropped[g])
            {
                CpuSeats[g] = _dropped[g] = false;
                Host.Fx.Popup(new Vec2(_area.Center.X, _area.Top + 60), L.F("{0} is back", Names[g]), Colors.White, 20, 2.0);
                RefreshView();
            }
        }
        if ((_sendT += dt) < SendEvery) return;
        _sendT = 0;
        foreach (var s in seats.Where(s => s.Seat > 0 && s.Connected))
            if (s.Seat < _seatOfGuest.Length && _seatOfGuest[s.Seat] is >= 0 and var g)
                _room.SendTo(s.Seat, Tag + "s|" + JsonSerializer.Serialize(Fill(ViewOf(g), g)));
    }

    void GuestUpdate(double dt)
    {
        TView? latest = null;
        while (_room.TryReceive(out var msg))
        {
            if (!msg.Body.StartsWith(Tag + "s|", StringComparison.Ordinal)) continue;
            TView? v = null;
            try { v = JsonSerializer.Deserialize<TView>(msg.Body[(Tag.Length + 2)..]); }
            catch (JsonException) { }
            catch (NotSupportedException) { }
            if (v != null && v.Seat >= 0 && v.Players >= MinPlayers && v.Seat < v.Players) latest = v; // a view that can be ours
        }
        // UDP can deliver out of order: only ever move forward
        if (latest != null && (View == null || (latest.Game, latest.Version, latest.Ack).CompareTo((View.Game, View.Version, View.Ack)) > 0))
        {
            View = latest;
            Changed();
        }
        if (View != null) _outbox.RemoveAll(o => o.Seq <= View.Ack);
        if (_outbox.Count > 0 && (_resendT += dt) >= ResendEvery)
        {
            _resendT = 0;
            foreach (var (_, message) in _outbox) _room.SendToHost(message);
        }
        if (Demo && _outbox.Count == 0 && View is { Over: false } mine && (_demoT -= dt) <= 0)
        {
            _demoT = DemoEvery;
            if (GuestDemoMove(mine) is { } move) SendAction(move);
        }
    }

    /// <summary>
    /// Over the two-player link (tray → Play over LAN): the host's table opens a room and keeps inviting the other
    /// player, whose copy joins it by itself. True while an invitation is still going out.
    /// </summary>
    bool LanBridge(double dt)
    {
        var lan = Host.Lan;
        if (!lan.Connected) return false;
        while (lan.TryReceive(out var msg))
        {
            var f = msg.Split('|');
            if (f.Length != 2 || f[0] != Tag + "k" || lan.Role != LanRole.Guest || lan.PeerAddress is not { } ip) continue;
            string code = RoomLink.CleanCode(f[1]);
            bool inIt = Mode == RoomMode.Guest && _room.Code == code && _room.State is RoomState.Joining or RoomState.Joined;
            // a game against the computer that is still going is not thrown away; the host keeps asking
            if (code.Length > 0 && !inIt && !(Mode == RoomMode.Solo && Playing)) JoinRoom(code, new System.Net.IPEndPoint(ip, RoomLink.Port));
        }
        if (lan.Role != LanRole.Host) return false;
        if (Mode == RoomMode.Idle && _bridgedSession != lan.Session)
        {
            _bridgedSession = lan.Session; // once per connection: leaving the room leaves it
            HostRoom();
            _inviteT = 0;
        }
        if (Mode != RoomMode.Hosting || HasRules) return false;
        string peer = RoomLink.SeatName(lan.PeerName);
        if (_room.Seats().Any(s => s.Seat > 0 && s.Connected && s.Name == peer)) return false;
        if ((_inviteT -= dt) <= 0)
        {
            _inviteT = 1;
            lan.Send(Tag + "k|" + _room.Code);
        }
        return true;
    }

    /// <summary>Something changed: draw again at the next frame, and wake the overlay for it.</summary>
    protected void Changed()
    {
        _drawnVersion = -1;
        Host.HudChanged();
        Host.Wake();
    }

    // ------------------------------------------------------------------ demo

    public override void DemoTick()
    {
        Demo = true;
        if (Mode == RoomMode.Idle) StartSolo(DemoCpus);
        else if (View is { Over: true } && Mode != RoomMode.Guest && HasRules)
        {
            // like a person, look at the result before dealing again (and let the room's players see it)
            if (_demoOverAt == 0) _demoOverAt = Environment.TickCount64;
            else if (Environment.TickCount64 - _demoOverAt > 6000) NewGame();
        }
        else _demoOverAt = 0;
    }

    // ------------------------------------------------------------------ layout and input

    /// <summary>The table sits along the bottom of the screen until it is dragged somewhere, which is remembered.</summary>
    public override void Layout()
    {
        var a = Host.Arena;
        double w = Math.Min(a.Width - 40, TableSize.Width), h = Math.Min(a.Height - 40, TableSize.Height);
        if (!_handle.Dragging) _origin = _handle.Saved() ?? new Vec2(a.Center.X - w / 2, a.Bottom - h - 16);
        _origin = ClampOrigin(_origin, new Size(w, h));
        _area = new Rect(_origin.X, _origin.Y, w, h);
        _table = new Rect(0, 0, w, h);
        _rootTr.X = _origin.X;
        _rootTr.Y = _origin.Y;
        _handle.Show(_area);
        if (_drawnSize != _area.Size) Draw();
        if (!_handle.Dragging) Host.HudChanged(); // the scoreboard's text does not depend on where the table is
    }

    Vec2 ClampOrigin(Vec2 o, Size size)
    {
        var a = Host.Arena;
        double top = a.Top + DragHandle.Height + 10;
        return new Vec2(
            Clamp(o.X, a.Left + 4, Math.Max(a.Left + 4, a.Right - size.Width - 4)),
            Clamp(o.Y, top, Math.Max(top, a.Bottom - size.Height - 4)));
    }

    public override void CollectHitShapes(List<HitShape> into)
    {
        into.Add(HitShape.Box(_area));
        into.Add(_handle.Hit);
    }

    public override bool PointerDown(Vec2 p, bool right)
    {
        if (right || _handle.Contains(p))
        {
            _handle.Begin(p, _origin, anywhere: true); // the grip, or a right-drag anywhere on the table
            return true;
        }
        var q = (p - _origin).ToPoint();
        if (PressAt(q))
        {
            _pressing = true;
            return true;
        }
        for (int i = _clickables.Count - 1; i >= 0; i--)
            if (_clickables[i].Box.Contains(q))
            {
                _clickables[i].Click();
                Draw();
                return false;
            }
        return false;
    }

    public override void PointerUp(Vec2 p)
    {
        if (_pressing)
        {
            _pressing = false;
            Released();
        }
        _handle.End(_origin);
    }

    public override void PointerCancel()
    {
        _pressing = false;
        _handle.Cancel();
    }

    public override void Summon(Vec2 p)
    {
        _handle.Save(ClampOrigin(new Vec2(p.X - _table.Width / 2, p.Y - _table.Height / 2), _area.Size));
        Layout();
    }

    public override void Deactivate()
    {
        _pressing = false;
        _handle.Cancel();
        Anims.Finish();
    }

    public override void ThemeChanged()
    {
        foreach (var card in Cards.Shown)
            if (card.Visual is Border { Tag: DurakGame.BackTag } back) DurakGame.PaintBack(back);
        _drawnSize = default; // the felt is drawn afresh at the next layout
    }

    // ------------------------------------------------------------------ drawing

    string PanelKey() => $"{Mode}|{_room.State}|{_room.Code}|{PanelExtras}|{string.Join(",", _room.Seats().Select(s => s.Name + s.Connected))}";

    /// <summary>What else the start panel depends on (a setting it shows).</summary>
    protected virtual string PanelExtras => "";

    /// <summary>Draws the table again now (after a change only this player sees: a card picked, a toggle).</summary>
    protected void Draw()
    {
        _drawnVersion = View?.Version ?? -1;
        _drawnGame = View?.Game ?? -1;
        _drawnPanel = View == null ? PanelKey() : "";
        _drawnSize = _area.Size;
        _canvas.Children.Clear();
        _top.Children.Clear();
        _clickables.Clear();
        var a = _table;
        var cloth = Themes.Current.Felt ?? Color.FromRgb(22, 92, 60);
        _canvas.Children.Add(Art.At(new Border
        {
            Width = a.Width, Height = a.Height, CornerRadius = new CornerRadius(a.Height * 0.2), Opacity = 0.94,
            Background = new RadialGradientBrush
            {
                Center = new RelativePoint(0.5, 0.45, RelativeUnit.Relative), GradientOrigin = new RelativePoint(0.5, 0.4, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(0.6, RelativeUnit.Relative), RadiusY = new RelativeScalar(0.7, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Art.Blend(cloth, Colors.White, 0.12), 0), new GradientStop(Art.Blend(cloth, Colors.Black, 0.12), 1) },
            },
            BorderBrush = Art.Brush(Art.Blend(cloth, Colors.Black, 0.55)), BorderThickness = new Thickness(5), IsHitTestVisible = false,
        }, a.Left, a.Top));
        _canvas.Children.Add(Art.At(new Border
        {
            Width = a.Width - 22, Height = a.Height - 22, CornerRadius = new CornerRadius(a.Height * 0.2 - 10),
            BorderBrush = Art.Brush(Color.FromArgb(50, 255, 255, 255)), BorderThickness = new Thickness(1.5), IsHitTestVisible = false,
        }, a.Left + 11, a.Top + 11));

        if (View == null)
        {
            Cards.Clear();
            Shown = null;
            DrawStartPanel();
            return;
        }
        var v = View;
        var prev = Shown;
        Shown = v;
        bool newDeal = prev == null || prev.Game != v.Game || prev.Seat != v.Seat;
        if (newDeal) Cards.Clear();
        Cards.Begin();
        DrawTable(v, newDeal ? null : prev, newDeal);
        foreach (var card in Cards.End()) Leave(card, v, newDeal ? null : prev);
    }

    void DrawStartPanel()
    {
        var a = _table;
        Label(L.T(Title), a.Center.X, a.Top + 60, 40, Colors.White, center: true);
        Label(Tagline, a.Center.X, a.Top + 118, 16, Soft, center: true);
        if (RoomStatus() is { } status) Label(status, a.Center.X, a.Top + 160, 16, Gold, center: true);
        double y = a.Top + 210;
        if (Mode == RoomMode.Hosting)
        {
            int humans = _room.Seats().Count(s => s.Connected);
            if (humans < 2) Label(L.T("Waiting for a co-worker to join…"), a.Center.X, y - 6, 15, Soft, center: true);
            else
            {
                // everyone here, and up to two computers more (as many as the game needs, at the least)
                var options = Enumerable.Range(0, 3).Select(c => Math.Max(humans + c, MinPlayers)).Where(n => n <= MaxPlayers).Distinct().ToList();
                for (int i = 0; i < options.Count; i++)
                {
                    int seats = options[i], cpus = seats - humans;
                    string text = cpus == 0 ? L.T("Start the game") : cpus == 1 ? L.T("Start + 1 computer") : L.F("Start + {0} computers", cpus);
                    Button(text, a.Center.X + (i - (options.Count - 1) / 2.0) * 190, y, 180, () => StartRoom(seats));
                }
            }
            Button(L.T("Room setup…"), a.Center.X, y + 70, 260, () => SetupRequested?.Invoke());
            Button(L.T("Leave the room"), a.Center.X, y + 130, 200, LeaveRoom);
            DrawStartExtras(y + 190);
            return;
        }
        if (Mode == RoomMode.Guest && _room.State is RoomState.Joining or RoomState.Joined)
        {
            Button(L.T("Leave the room"), a.Center.X, y + 70, 200, LeaveRoom); // the host starts the game
            return;
        }
        var choices = SoloChoices.ToList();
        double step = Math.Min(170, (a.Width - 80) / Math.Max(1, choices.Count));
        for (int i = 0; i < choices.Count; i++)
        {
            int n = choices[i].Cpus;
            Button(choices[i].Text, a.Center.X + (i - (choices.Count - 1) / 2.0) * step, y, step - 10, () => StartSolo(n));
        }
        Button(L.T("Play with co-workers…"), a.Center.X, y + 70, 260, () => SetupRequested?.Invoke());
        if (Mode is RoomMode.Guest or RoomMode.Hosting) Button(L.T("Leave the room"), a.Center.X, y + 130, 200, LeaveRoom);
        else DrawStartExtras(y + 130);
    }

    /// <summary>Raised when the player asks to host or join a room; the overlay opens the setup window.</summary>
    public event Action? SetupRequested;

    /// <summary>The buttons of a finished game: deal again, and leave.</summary>
    protected void DrawEndButtons(double y)
    {
        var a = _table;
        if (Mode == RoomMode.Guest && _room.State == RoomState.Lost)
        {
            Button(L.T("Leave the room"), a.Center.X, y, 200, LeaveRoom);
            return;
        }
        if (Mode != RoomMode.Guest || _room.State == RoomState.Joined) Button(L.T("New game"), a.Center.X - 110, y, 180, NewGameClicked);
        Button(Solo ? L.T("Other games…") : L.T("Leave the room"), a.Center.X + 110, y, 180, LeaveTable);
    }

    // ------------------------------------------------------------------ pieces

    /// <summary>Adds a static piece to the table (below the cards), or to <paramref name="into"/>.</summary>
    protected void Place(Control c, double x, double y, Canvas? into = null) => (into ?? _canvas).Children.Add(Art.At(c, x, y));

    protected TextBlock Label(string text, double x, double y, double size, Color color, bool center = false, Canvas? into = null, bool right = false)
    {
        var t = new TextBlock { Text = text, FontFamily = Fx.Font, FontSize = size, FontWeight = FontWeight.Bold, Foreground = Art.Brush(color), IsHitTestVisible = false };
        if (center || right)
        {
            t.Measure(Size.Infinity);
            x -= center ? t.DesiredSize.Width / 2 : t.DesiredSize.Width;
        }
        Place(t, x, y, into);
        return t;
    }

    /// <summary>A button, drawn above the cards; without <paramref name="click"/> it is greyed out.</summary>
    protected Border Button(string text, double cx, double y, double width, Action? click, bool hot = false, double height = 40, double font = 15)
    {
        var b = new Border
        {
            Width = width, Height = height, CornerRadius = new CornerRadius(height / 2),
            Background = click == null ? Art.Brush(150, 120, 120, 120) : hot ? Art.Brush(Themes.Current.Accent) : Art.Brush(235, 255, 209, 102),
            BorderBrush = hot ? Brushes.White : Art.Brush("#8A6D1F"), BorderThickness = new Thickness(hot ? 2 : 1.5), IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = text, FontFamily = Fx.Font, FontSize = font, FontWeight = FontWeight.Bold,
                Foreground = click == null ? Art.Brush("#DDDDDD") : hot ? Brushes.White : Art.Brush("#2A2008"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Place(b, cx - width / 2, y, _top);
        if (click != null) _clickables.Add((new Rect(cx - width / 2, y, width, height), click));
        return b;
    }

    /// <summary>A click area without a drawing of its own (a card in the hand).</summary>
    protected void Clickable(Rect box, Action click) => _clickables.Add((box, click));

    protected static double Stagger(double delay) => Fx.ReducedMotion ? 0 : delay;
}
