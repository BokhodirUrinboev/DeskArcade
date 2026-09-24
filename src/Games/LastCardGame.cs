using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using DeskArcade.Engine;
using DeskArcade.Net;
using static DeskArcade.Games.LastCardRules;

namespace DeskArcade.Games;

/// <summary>What one player sees of a Last Card game: their own hand, and only counts of everyone else's.</summary>
public sealed class LastCardView
{
    public int Game { get; set; }
    public int Seat { get; set; }
    public string[] Names { get; set; } = Array.Empty<string>();
    public bool[] Cpu { get; set; } = Array.Empty<bool>();
    public List<int> Hand { get; set; } = new();
    public int[] Counts { get; set; } = Array.Empty<int>();
    public int Top { get; set; }
    public int Color { get; set; }
    public int Turn { get; set; }
    public int Direction { get; set; } = 1;
    public int Deck { get; set; }
    public bool Drew { get; set; }
    public int DrawnCard { get; set; } = -1;
    public bool[] Called { get; set; } = Array.Empty<bool>();
    public bool Over { get; set; }
    public int Winner { get; set; } = -1;
    public int Caught { get; set; } = -1;
    public int Catches { get; set; }
    public int Version { get; set; }
    /// <summary>The last action number the host has handled from this player.</summary>
    public int Ack { get; set; }

    public int Players => Names.Length;
    public bool MyTurn => !Over && Turn == Seat;

    public static LastCardView Of(LastCardRules r, int seat, int game, string[] names, bool[] cpu, int ack) => new()
    {
        Game = game, Seat = seat, Names = names, Cpu = cpu, Hand = r.Hands[seat].ToList(),
        Counts = r.Hands.Select(h => h.Count).ToArray(), Top = r.Top, Color = r.Color, Turn = r.Turn, Direction = r.Direction,
        Deck = r.DrawPile.Count, Drew = r.Drew && r.Turn == seat, DrawnCard = r.Turn == seat ? r.DrawnCard : -1,
        Called = r.Called.ToArray(), Over = r.Over, Winner = r.Winner, Caught = r.Caught, Catches = r.Catches,
        Version = r.Version, Ack = ack,
    };

    /// <summary>The same check as <see cref="LastCardRules.CanPlay"/>, from what this player can see.</summary>
    public bool CanPlay(int card)
    {
        if (!MyTurn || !Hand.Contains(card)) return false;
        if (Drew && card != DrawnCard) return false;
        bool fits = IsWild(card) || ColorOf(card) == Color || SameFace(card, Top);
        return fits && (KindOf(card) != Kind.WildDrawFour || Hand.All(c => ColorOf(c) != Color));
    }

    static bool SameFace(int a, int b) =>
        KindOf(a) == KindOf(b) && (KindOf(a) != Kind.Number || NumberOf(a) == NumberOf(b));
}

/// <summary>
/// Last Card (see <see cref="LastCardRules"/>), an UNO-style game for 2–4 players: against computer players, or
/// with co-workers in a room on the local network (<see cref="RoomLink"/>), exactly as <see cref="DurakGame"/>
/// runs its rooms. The host runs the game and sends each player only their own view ("ls|json"); players send
/// their moves ("la|seq|kind|card|colour") in order and re-send them until the host's view acknowledges them.
/// Computer players fill empty seats and take over for anyone who drops out. Room setup happens in
/// <see cref="RoomWindow"/>.
/// </summary>
public sealed class LastCardGame : MiniGame, IRoomGame
{
    public const double CardW = 62, CardH = 92;
    const double CpuDelay = 0.9, SendEvery = 0.25, ResendEvery = 0.3, CpuCallChance = 0.85;
    static readonly Color Gold = Avalonia.Media.Color.FromRgb(255, 209, 102);
    static readonly Color Felt = Avalonia.Media.Color.FromRgb(38, 44, 70);
    static readonly Color[] CardColors =
    {
        Avalonia.Media.Color.FromRgb(222, 56, 56), Avalonia.Media.Color.FromRgb(246, 196, 32),
        Avalonia.Media.Color.FromRgb(46, 168, 84), Avalonia.Media.Color.FromRgb(36, 112, 214),
    };
    static readonly string[] ColorNames = { "red", "yellow", "green", "blue" };

    enum Mode { Idle, Solo, Hosting, Guest }

    readonly Canvas _canvas = new();
    readonly List<(Rect Box, Action Click)> _clickables = new();
    readonly RoomLink _room = new() { Game = "lastcard" };

    Mode _mode;
    LastCardRules? _rules;
    LastCardView? _view;
    string[] _names = Array.Empty<string>();
    bool[] _cpu = Array.Empty<bool>();
    int[] _seatOfGuest = Array.Empty<int>(); // host: room seat → game seat (−1: not playing)
    int[] _lastSeq = Array.Empty<int>();       // host: last action number handled per game seat
    bool[] _dropped = Array.Empty<bool>();     // host: seats the computer took over after their player dropped out
    readonly List<(int Seq, string Message)> _outbox = new(); // guest: our actions, re-sent until acknowledged
    readonly Avalonia.Threading.DispatcherTimer _hostTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    readonly System.Diagnostics.Stopwatch _hostClock = new();
    int _game, _nextSeq, _drawnVersion = -1, _pendingWild = -1, _seenCatches, _seenTop = -1;
    double _cpuT, _sendT, _resendT, _demoT, _inviteT;
    string _drawnPanel = "";
    int _bridgedSession = -1;
    bool _announced, _demo;
    Rect _area;

    public LastCardGame(IGameHost host) : base(host)
    {
        Layer.Children.Add(_canvas);
        _room.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(Changed);
        _room.MessageArrived += () => Avalonia.Threading.Dispatcher.UIThread.Post(Host.Wake);
        _hostTimer.Tick += (_, _) => HostTick();
    }

    public override string Id => "lastcard";
    public override string Title => "Last Card";
    public RoomLink Room => _room;
    public string MinVersion => "1.7.2";
    public override bool SupportsLan => true;
    public bool Playing => _view != null && !_view.Over;

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        s.Rotor.Children.Add(Art.At(new Rectangle { Width = 11, Height = 16, RadiusX = 2, RadiusY = 2, Fill = Art.Brush(CardColors[3]), Stroke = Brushes.White, StrokeThickness = 1, RenderTransform = new RotateTransform(-14) }, -10, -8));
        s.Rotor.Children.Add(Art.At(new Rectangle { Width = 11, Height = 16, RadiusX = 2, RadiusY = 2, Fill = Art.Brush(CardColors[0]), Stroke = Brushes.White, StrokeThickness = 1, RenderTransform = new RotateTransform(12) }, -1, -8));
        s.Rotor.Children.Add(Art.At(new TextBlock { Text = "1", FontSize = 9, FontWeight = FontWeight.Black, Foreground = Brushes.White }, 2, -6));
        return s;
    }

    public override HudInfo Hud => new(
        _view == null ? "—" : _view.Hand.Count.ToString(CultureInfo.InvariantCulture),
        Status(),
        L.F("Wins {0}", Host.Stats.Get("lastcard.wins")));

    string Name(int seat) => _view != null && seat >= 0 && seat < _view.Names.Length ? _view.Names[seat] : "?";

    static string ColorName(int color) => color is >= 0 and < ColorCount ? L.T(ColorNames[color]) : "";

    string Status()
    {
        if (_mode == Mode.Guest && _room.State == RoomState.Joining) return L.F("Joining room {0}…", _room.Code);
        if (_mode == Mode.Guest && _room.State == RoomState.Lost) return L.T("The room closed · set up a new game");
        if (_mode == Mode.Hosting && _rules == null) return L.F("Room {0} · {1} at the table", _room.Code, _room.Seats().Count(x => x.Connected));
        if (_view == null) return _mode == Mode.Guest ? L.F("In room {0} · waiting for the host to start", _room.Code) : L.T("Click Last Card on the table to start a game");
        var v = _view;
        if (v.Over) return v.Winner == v.Seat ? L.T("You went out first · click New game") : L.F("{0} went out first · click New game", Name(v.Winner));
        if (!v.MyTurn) return L.F("{0}'s turn", Name(v.Turn));
        if (_pendingWild >= 0) return L.T("Pick a colour for your wild card");
        if (v.Drew) return L.T("Play the card you drew, or pass");
        return L.F("Your turn · play on the {0} {1}, or draw from the pile", ColorName(v.Color), FaceName(v.Top));
    }

    static string FaceName(int card) => KindOf(card) switch
    {
        Kind.Number => NumberOf(card).ToString(CultureInfo.InvariantCulture),
        Kind.Skip => L.T("Skip"),
        Kind.Reverse => L.T("Reverse"),
        Kind.DrawTwo => "+2",
        Kind.WildDrawFour => "+4",
        _ => L.T("Wild"),
    };

    // ------------------------------------------------------------------ starting and ending

    /// <summary>A game against <paramref name="cpus"/> computer players, no network.</summary>
    public void StartSolo(int cpus)
    {
        _room.Stop();
        _mode = Mode.Solo;
        _names = new[] { L.T("You") }.Concat(Enumerable.Range(1, cpus).Select(i => L.F("CPU {0}", i))).ToArray();
        _cpu = new[] { false }.Concat(Enumerable.Repeat(true, cpus)).ToArray();
        _lastSeq = new int[_names.Length];
        _dropped = new bool[_names.Length];
        NewDeal();
    }

    public void HostRoom(string? code = null)
    {
        _rules = null;
        _view = null;
        _announced = false;
        _room.Host(code);
        _mode = _room.IsHost ? Mode.Hosting : Mode.Idle;
        if (!_room.IsHost)
            Host.Fx.Popup(new Vec2(_area.Center.X, _area.Top + 80), L.T("Couldn't open a room"), Colors.White, 24, 2.4,
                L.F("UDP port {0} is in use", RoomLink.Port));
        Changed();
    }

    public void JoinRoom(string code, System.Net.IPEndPoint? address)
    {
        _rules = null;
        _view = null;
        _outbox.Clear();
        _nextSeq = 0;
        _announced = false;
        _mode = Mode.Guest;
        _room.Join(code, address);
        Changed();
    }

    public void LeaveRoom()
    {
        _room.Stop();
        _rules = null;
        _view = null;
        _mode = Mode.Idle;
        Changed();
    }

    public void StartRoom(int players)
    {
        if (_mode != Mode.Hosting || !_room.IsHost) return;
        _room.Open = false; // close the room first, so nobody joins or leaves between the roster and the deal
        foreach (var gone in _room.Seats().Where(s => !s.Connected && s.Seat > 0)) _room.Kick(gone.Seat);
        var seats = _room.Seats().Where(s => s.Connected).ToList();
        int humans = seats.Count;
        players = Math.Clamp(Math.Max(players, humans), MinPlayers, RoomLink.MaxSeats);
        _names = seats.Select(s => s.Seat == 0 ? LanLink.MyName : s.Name)
            .Concat(Enumerable.Range(1, players - humans).Select(i => L.F("CPU {0}", i))).ToArray();
        _cpu = Enumerable.Range(0, players).Select(i => i >= humans).ToArray();
        _seatOfGuest = Enumerable.Repeat(-1, RoomLink.MaxSeats).ToArray();
        for (int i = 0; i < seats.Count; i++) _seatOfGuest[seats[i].Seat] = i;
        _lastSeq = new int[players];
        _dropped = new bool[players];
        NewDeal();
        _hostClock.Restart();
        _hostTimer.Start();
    }

    void NewDeal()
    {
        _rules = new LastCardRules(_names.Length, Rng);
        // action numbers carry on across deals, so a late copy of a move from the last deal can't count in this one
        if (_lastSeq.Length != _names.Length) _lastSeq = new int[_names.Length];
        if (_dropped.Length != _names.Length) _dropped = new bool[_names.Length];
        _game++;
        _announced = false;
        _pendingWild = -1;
        _cpuT = CpuDelay;
        RefreshView();
        Host.Sound.Play("whoosh", 0.4, 1.4);
    }

    void NewGameClicked()
    {
        if (_mode is Mode.Solo or Mode.Hosting && _rules != null) NewDeal();
        else if (_mode == Mode.Guest) SendAction("again", -1, -1);
    }

    /// <summary>The effects of what just happened in the view: a new card on the pile, a forgotten call, the end.</summary>
    void Announce(LastCardView v)
    {
        if (v.Catches > _seenCatches)
        {
            _seenCatches = v.Catches;
            if (v.Caught >= 0)
            {
                Host.Fx.Popup(new Vec2(_area.Center.X, _area.Top + _area.Height * 0.36),
                    v.Caught == v.Seat ? L.T("You forgot to say last card!") : L.F("{0} forgot to say last card!", Name(v.Caught)),
                    Colors.White, 24, 2.2, L.T("+2 cards"));
                Host.Sound.Play("buzzer", 0.3);
            }
        }
        if (v.Top != _seenTop)
        {
            if (_seenTop >= 0 && KindOf(v.Top) == Kind.WildDrawFour) Host.Sound.Play("thunk", 0.4);
            _seenTop = v.Top;
        }
        if (!v.Over || _announced) return;
        _announced = true;
        var at = new Vec2(_area.Center.X, _area.Top + _area.Height * 0.3);
        Host.Stats.Add("lastcard.games");
        if (v.Winner == v.Seat)
        {
            Host.Stats.Add("lastcard.wins");
            if (_mode != Mode.Solo) Host.Stats.Add("lan.wins");
            Host.Fx.Popup(at, L.T("YOU WIN!"), Gold, 42, 2.6, L.T("first to play their last card"));
            Host.Fx.Burst(at, Themes.Current.Confetti, 40, 520, 700, 7, 1.0);
            Host.Sound.Play("best", 0.8);
        }
        else
        {
            Host.Fx.Popup(at, L.F("{0} WINS", Name(v.Winner).ToUpperInvariant()), Colors.White, 36, 2.6, L.T("better luck next deal"));
            Host.Sound.Play("buzzer", 0.4);
        }
    }

    // ------------------------------------------------------------------ actions

    void HandCardClicked(int card)
    {
        if (_view is not { } v || !v.CanPlay(card)) return;
        if (IsWild(card))
        {
            _pendingWild = _pendingWild == card ? -1 : card; // the colour buttons appear
            Changed();
            return;
        }
        _pendingWild = -1;
        Do("play", card, -1);
    }

    void Do(string kind, int card, int color)
    {
        if (kind == "play") Host.Stats.Add("lastcard.played");
        if (kind == "play" && KindOf(card) == Kind.WildDrawFour) Host.Stats.Add("lastcard.plusfours");
        if (_mode == Mode.Guest) SendAction(kind, card, color);
        else if (_rules != null && _rules.Act(_view!.Seat, kind, card, color))
        {
            Host.Sound.Play(kind == "draw" ? "whoosh" : "board", 0.45, 1.5);
            _cpuT = DelayAfter(_view.Seat);
            RefreshView();
        }
        else Host.Fx.Popup(Host.Pointer - new Vec2(0, 40), L.T("not allowed right now"), Colors.White, 18, 1.0);
    }

    /// <summary>Guest: queues a move; moves go out in order and are re-sent until the host's view acknowledges them.</summary>
    void SendAction(string kind, int card, int color)
    {
        _nextSeq = Math.Max(_nextSeq, _view?.Ack ?? 0) + 1;
        var message = string.Create(CultureInfo.InvariantCulture, $"la|{_nextSeq}|{kind}|{card}|{color}");
        _outbox.Add((_nextSeq, message));
        _room.SendToHost(message);
        _resendT = 0;
        Host.Sound.Play("board", 0.35, 1.5);
    }

    // ------------------------------------------------------------------ simulation

    void HostTick()
    {
        double dt = Math.Min(_hostClock.Elapsed.TotalSeconds, 0.5);
        _hostClock.Restart();
        if (_mode != Mode.Hosting || _rules == null)
        {
            _hostTimer.Stop();
            return;
        }
        HostUpdate(dt);
        CpuTurns(dt);
    }

    public override bool Update(double dt)
    {
        bool inviting = LanBridge(dt);
        if (_mode == Mode.Guest) GuestUpdate(dt);
        else if (_mode == Mode.Solo) CpuTurns(dt); // a room's host plays on its own timer (HostTick)
        if (_view != null ? _view.Version != _drawnVersion : PanelKey() != _drawnPanel) Draw();
        if (_view != null) Announce(_view);
        return inviting || _mode switch
        {
            Mode.Solo => _rules is { Over: false } || _demo,
            Mode.Guest => _room.State == RoomState.Joining || _outbox.Count > 0 || _demo && _room.State == RoomState.Joined,
            _ => false,
        };
    }

    int MySeat => _mode is Mode.Hosting or Mode.Solo ? 0 : _view?.Seat ?? -1;

    /// <summary>How long the computers wait after a person's move: longer while that person could still say "last card".</summary>
    double DelayAfter(int seat) => _rules is { } r && r.Hands[seat].Count == 1 && !r.Called[seat] ? CpuDelay * 2.5 : CpuDelay;

    void CpuTurns(double dt)
    {
        if (_rules is not { Over: false } r || (_cpuT -= dt) > 0) return;
        _cpuT = CpuDelay * (0.7 + Rng.NextDouble() * 0.6);
        int seat = r.Turn;
        if (!_cpu[seat] && !(_demo && seat == MySeat)) return;
        if (r.CpuAction(seat) is not { } a) return;
        r.Act(seat, a.Kind, a.Card, a.Color);
        // computers say "last card" most of the time; now and then one forgets and gets caught
        if (r.Hands[seat].Count == 1 && !r.Called[seat] && Rng.NextDouble() < CpuCallChance) r.Act(seat, "call");
        Host.Sound.Play(a.Kind == "draw" ? "whoosh" : "board", 0.35, 1.3 + Rng.NextDouble() * 0.3);
        RefreshView();
    }

    void RefreshView()
    {
        if (_rules == null) return;
        _view = LastCardView.Of(_rules, 0, _game, _names, _cpu, _lastSeq[0]);
        if (!_view.MyTurn) _pendingWild = -1;
        _sendT = SendEvery; // tell the room at once
        Changed();
    }

    void HostUpdate(double dt)
    {
        while (_room.TryReceive(out var msg))
        {
            if (_rules == null || msg.Seat >= _seatOfGuest.Length || _seatOfGuest[msg.Seat] is not (>= 0 and var seat)) continue;
            var f = msg.Body.Split('|');
            // moves are taken strictly in order: a repeat or one that jumped ahead waits for the re-send
            if (f.Length != 5 || f[0] != "la" || !int.TryParse(f[1], out int seq) || seq != _lastSeq[seat] + 1 ||
                !int.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int card) ||
                !int.TryParse(f[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int color))
                continue;
            _lastSeq[seat] = seq; // handled, whether or not the rules accept it; the view tells the player
            if (f[2] == "again" && _rules.Over) NewDeal();
            else if (_rules.Act(seat, f[2], card, color))
            {
                Host.Sound.Play("board", 0.4, 1.4);
                _cpuT = DelayAfter(seat);
            }
            RefreshView();
        }
        if (_rules == null) return;

        // a player who dropped out is played by the computer until they come back
        var seats = _room.Seats();
        for (int roomSeat = 1; roomSeat < _seatOfGuest.Length; roomSeat++)
        {
            if (_seatOfGuest[roomSeat] is not (>= 0 and var g)) continue;
            bool connected = seats.Any(s => s.Seat == roomSeat && s.Connected);
            if (!connected && !_cpu[g])
            {
                _cpu[g] = _dropped[g] = true;
                Host.Fx.Popup(new Vec2(_area.Center.X, _area.Top + 60), L.F("{0} left · the computer plays for them", _names[g]), Colors.White, 20, 2.0);
                RefreshView();
            }
            else if (connected && _dropped[g])
            {
                _cpu[g] = _dropped[g] = false;
                Host.Fx.Popup(new Vec2(_area.Center.X, _area.Top + 60), L.F("{0} is back", _names[g]), Colors.White, 20, 2.0);
                RefreshView();
            }
        }
        if ((_sendT += dt) < SendEvery) return;
        _sendT = 0;
        foreach (var s in seats.Where(s => s.Seat > 0 && s.Connected))
            if (s.Seat < _seatOfGuest.Length && _seatOfGuest[s.Seat] is >= 0 and var g)
                _room.SendTo(s.Seat, "ls|" + JsonSerializer.Serialize(LastCardView.Of(_rules, g, _game, _names, _cpu, _lastSeq[g])));
    }

    void GuestUpdate(double dt)
    {
        LastCardView? latest = null;
        while (_room.TryReceive(out var msg))
        {
            if (!msg.Body.StartsWith("ls|", StringComparison.Ordinal)) continue;
            try { latest = JsonSerializer.Deserialize<LastCardView>(msg.Body[3..]); }
            catch (JsonException) { }
        }
        // UDP can deliver out of order: only ever move forward
        if (latest != null && (_view == null || (latest.Game, latest.Version, latest.Ack).CompareTo((_view.Game, _view.Version, _view.Ack)) > 0))
        {
            if (_view != null && latest.Game != _view.Game)
            {
                _announced = false;
                _seenCatches = 0;
            }
            _view = latest;
            if (!_view.MyTurn) _pendingWild = -1;
            Changed();
        }
        if (_view != null) _outbox.RemoveAll(o => o.Seq <= _view.Ack);
        if (_outbox.Count > 0 && (_resendT += dt) >= ResendEvery)
        {
            _resendT = 0;
            foreach (var (_, message) in _outbox) _room.SendToHost(message);
        }
        if (_demo && _outbox.Count == 0 && _view is { Over: false } v && (_demoT -= dt) <= 0)
        {
            _demoT = CpuDelay;
            GuestDemo(v);
        }
    }

    /// <summary>
    /// Last Card over the two-player link (tray → Play over LAN), as Durak does it: the host's table opens a room
    /// and keeps inviting the other player ("lk|code"), whose copy joins it by itself. True while inviting.
    /// </summary>
    bool LanBridge(double dt)
    {
        var lan = Host.Lan;
        if (!lan.Connected) return false;
        while (lan.TryReceive(out var msg))
        {
            var f = msg.Split('|');
            if (f.Length != 2 || f[0] != "lk" || lan.Role != LanRole.Guest || lan.PeerAddress is not { } ip) continue;
            string code = RoomLink.CleanCode(f[1]);
            bool inIt = _mode == Mode.Guest && _room.Code == code && _room.State is RoomState.Joining or RoomState.Joined;
            if (code.Length > 0 && !inIt && !(_mode == Mode.Solo && Playing)) JoinRoom(code, new System.Net.IPEndPoint(ip, RoomLink.Port));
        }
        if (lan.Role != LanRole.Host) return false;
        if (_mode == Mode.Idle && _bridgedSession != lan.Session)
        {
            _bridgedSession = lan.Session; // once per connection: leaving the room leaves it
            HostRoom();
            _inviteT = 0;
        }
        if (_mode != Mode.Hosting || _rules != null) return false;
        string peer = RoomLink.SeatName(lan.PeerName);
        if (_room.Seats().Any(s => s.Seat > 0 && s.Connected && s.Name == peer)) return false;
        if ((_inviteT -= dt) <= 0)
        {
            _inviteT = 1;
            lan.Send("lk|" + _room.Code);
        }
        return true;
    }

    void Changed()
    {
        _drawnVersion = -1;
        Host.HudChanged();
        Host.Wake();
    }

    // ------------------------------------------------------------------ demo

    public override void DemoTick()
    {
        _demo = true;
        if (_mode == Mode.Idle) StartSolo(2);
        else if (_rules is { Over: true } && _mode != Mode.Guest)
        {
            // like a person, look at the result before dealing again (and let the room's players see it)
            if (_demoOverAt == 0) _demoOverAt = Environment.TickCount64;
            else if (Environment.TickCount64 - _demoOverAt > 3000) NewDeal();
        }
        else _demoOverAt = 0;
    }

    long _demoOverAt;

    void GuestDemo(LastCardView v)
    {
        if (!v.MyTurn) return;
        int card = v.Hand.Where(v.CanPlay).DefaultIfEmpty(-1).First();
        if (card >= 0) SendAction("play", card, IsWild(card) ? Rng.Next(ColorCount) : -1);
        else SendAction(v.Drew ? "pass" : "draw", -1, -1);
    }

    public override void Deactivate() => _pendingWild = -1;

    // ------------------------------------------------------------------ layout and drawing

    public override void Layout()
    {
        var a = Host.Arena;
        double w = Math.Min(a.Width - 40, 920), h = Math.Min(a.Height - 40, 560);
        _area = new Rect(a.Center.X - w / 2, a.Bottom - h - 16, w, h);
        Draw();
        Host.HudChanged();
    }

    public override void CollectHitShapes(List<HitShape> into)
    {
        foreach (var (box, _) in _clickables) into.Add(HitShape.Box(box));
    }

    public override bool PointerDown(Vec2 p, bool right)
    {
        for (int i = _clickables.Count - 1; i >= 0; i--)
            if (_clickables[i].Box.Contains(p.ToPoint()))
            {
                _clickables[i].Click();
                Draw();
                return false;
            }
        return false;
    }

    public override void Summon(Vec2 p) => Layout();

    string PanelKey() => $"{_mode}|{_room.State}|{_room.Code}|{string.Join(",", _room.Seats().Select(s => s.Name + s.Connected))}";

    void Draw()
    {
        _drawnVersion = _view?.Version ?? -1;
        _drawnPanel = _view == null ? PanelKey() : "";
        _canvas.Children.Clear();
        _clickables.Clear();
        var a = _area;
        _canvas.Children.Add(Art.At(new Border
        {
            Width = a.Width, Height = a.Height, CornerRadius = new CornerRadius(22), Opacity = 0.94,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Art.Blend(Felt, Colors.White, 0.1), 0), new GradientStop(Felt, 1) },
            },
            BorderBrush = Art.Brush("#161A2C"), BorderThickness = new Thickness(3), IsHitTestVisible = false,
        }, a.Left, a.Top));

        if (_view == null)
        {
            DrawStartPanel();
            return;
        }
        var v = _view;
        DrawOpponents(v);
        DrawPiles(v);
        DrawHand(v);
        DrawButtons(v);
    }

    void DrawStartPanel()
    {
        var a = _area;
        Label(L.T(Title), a.Center.X, a.Top + 70, 40, Colors.White, center: true);
        Label(L.T("Match the colour or the number · the first to play their last card wins"), a.Center.X, a.Top + 128, 16, Avalonia.Media.Color.FromRgb(200, 208, 235), center: true);
        if (_mode is Mode.Guest or Mode.Hosting) Label(Status(), a.Center.X, a.Top + 170, 16, Gold, center: true);
        double y = a.Top + 220;
        if (_mode == Mode.Hosting)
        {
            int humans = _room.Seats().Count(s => s.Connected);
            if (humans < 2) Label(L.T("Waiting for a co-worker to join…"), a.Center.X, y - 6, 15, Avalonia.Media.Color.FromRgb(200, 208, 235), center: true);
            else
                for (int cpus = 0; cpus <= Math.Min(2, RoomLink.MaxSeats - humans); cpus++)
                {
                    int seats = humans + cpus;
                    string text = cpus == 0 ? L.T("Start the game") : cpus == 1 ? L.T("Start + 1 computer") : L.F("Start + {0} computers", cpus);
                    Button(text, a.Center.X - 190 + cpus * 190, y, 180, () => StartRoom(seats));
                }
            Button(L.T("Room setup…"), a.Center.X, y + 70, 260, OpenSetup);
            Button(L.T("Leave the room"), a.Center.X, y + 130, 200, LeaveRoom);
            return;
        }
        if (_mode == Mode.Guest && _room.State is RoomState.Joining or RoomState.Joined)
        {
            Button(L.T("Leave the room"), a.Center.X, y + 70, 200, LeaveRoom); // the host starts the game
            return;
        }
        for (int cpus = 1; cpus <= 3; cpus++)
        {
            int n = cpus;
            Button(cpus == 1 ? L.T("Play 1 computer") : L.F("Play {0} computers", cpus), a.Center.X - 170 + (cpus - 1) * 170, y, 160, () => StartSolo(n));
        }
        Button(L.T("Play with co-workers…"), a.Center.X, y + 70, 260, OpenSetup);
        if (_mode is Mode.Guest or Mode.Hosting) Button(L.T("Leave the room"), a.Center.X, y + 130, 200, LeaveRoom);
    }

    void OpenSetup() => SetupRequested?.Invoke();

    /// <summary>Raised when the player asks to host or join a room; the overlay opens the setup window.</summary>
    public event Action? SetupRequested;

    void DrawOpponents(LastCardView v)
    {
        var a = _area;
        var others = Enumerable.Range(1, v.Players - 1).Select(i => (v.Seat + i) % v.Players).ToList();
        double slot = a.Width / Math.Max(1, others.Count);
        for (int i = 0; i < others.Count; i++)
        {
            int s = others[i];
            double cx = a.Left + slot * (i + 0.5);
            bool turn = !v.Over && v.Turn == s;
            Label(v.Names[s] + (v.Cpu[s] && _mode != Mode.Solo ? " · " + L.T("CPU") : ""), cx, a.Top + 12, 15, turn ? Gold : Colors.White, center: true);
            if (turn) Label(L.T("playing…"), cx, a.Top + 32, 12, Gold, center: true);
            int n = v.Counts[s];
            for (int k = 0; k < Math.Min(n, 10); k++)
                Place(CardBack(CardW * 0.5, CardH * 0.5), cx - Math.Min(n, 10) * 6 + k * 12 - CardW * 0.25, a.Top + 52);
            Label(n.ToString(CultureInfo.InvariantCulture), cx + Math.Min(n, 10) * 6 + 20, a.Top + 66, 14, Colors.White);
            if (n == 1 && v.Called[s]) Label(L.T("LAST CARD!"), cx, a.Top + 104, 14, Avalonia.Media.Color.FromRgb(255, 120, 120), center: true);
        }
    }

    void DrawPiles(LastCardView v)
    {
        var a = _area;
        double cy = a.Top + a.Height * 0.44, cx = a.Center.X;
        // the draw pile: click it on your turn to draw
        double px = cx - CardW - 36, py = cy - CardH / 2;
        if (v.Deck > 1) Place(CardBack(CardW, CardH), px + 3, py + 3);
        if (v.Deck > 0) Place(CardBack(CardW, CardH), px, py);
        bool canDraw = v.MyTurn && !v.Drew && _pendingWild < 0;
        if (canDraw)
        {
            _canvas.Children.Add(Art.At(new Rectangle { Width = CardW + 8, Height = CardH + 8, RadiusX = 9, RadiusY = 9, Stroke = Art.Brush(Gold), StrokeThickness = 3, IsHitTestVisible = false }, px - 4, py - 4));
            _clickables.Add((new Rect(px, py, CardW, CardH), () => Do("draw", -1, -1)));
        }
        Label(L.F("pile {0}", v.Deck), px + 6, py + CardH + 10, 13, Colors.White);

        // the discard, ringed in the colour to follow (a Wild shows the colour its player picked)
        double dx = cx + 20, dy = cy - CardH / 2;
        var ring = CardColors[Math.Clamp(v.Color, 0, ColorCount - 1)];
        _canvas.Children.Add(Art.At(new Rectangle { Width = CardW + 16, Height = CardH + 16, RadiusX = 12, RadiusY = 12, Fill = Art.Brush(Avalonia.Media.Color.FromArgb(90, ring.R, ring.G, ring.B)), Stroke = Art.Brush(ring), StrokeThickness = 3, IsHitTestVisible = false }, dx - 8, dy - 8));
        var top = CardFace(v.Top, CardW, CardH);
        top.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        top.RenderTransform = new RotateTransform(v.Top % 7 - 3);
        Place(top, dx, dy);
        Label(ColorName(v.Color), dx + CardW / 2, dy + CardH + 12, 13, ring, center: true);
        Label(v.Direction > 0 ? "↻" : "↺", dx + CardW + 30, cy - 18, 28, Avalonia.Media.Color.FromRgb(200, 208, 235));
    }

    void DrawHand(LastCardView v)
    {
        var a = _area;
        var hand = v.Hand.OrderBy(c => IsWild(c) ? ColorCount : ColorOf(c)).ThenBy(c => (int)KindOf(c)).ThenBy(NumberOf).ToList();
        if (hand.Count == 0) return;
        double maxW = a.Width - 260, step = Math.Min(CardW + 6, (maxW - CardW) / Math.Max(1, hand.Count - 1));
        double total = CardW + step * (hand.Count - 1);
        double x0 = a.Center.X - total / 2, y = a.Bottom - CardH - 22;
        for (int i = 0; i < hand.Count; i++)
        {
            int card = hand[i];
            double x = x0 + i * step;
            bool playable = v.CanPlay(card);
            double cy = playable ? y - 14 : y;
            var face = CardFace(card, CardW, CardH);
            if (card == _pendingWild)
            {
                face.BorderBrush = Art.Brush(Gold);
                face.BorderThickness = new Thickness(3);
                cy -= 10;
            }
            if (!playable && v.MyTurn) face.Opacity = 0.7;
            Place(face, x, cy);
            double width = i == hand.Count - 1 ? CardW : step;
            _clickables.Add((new Rect(x, cy, width, CardH), () => HandCardClicked(card)));
        }
        if (_pendingWild >= 0) DrawColorPicker(y - 70);
    }

    void DrawColorPicker(double y)
    {
        double cx = _area.Center.X, r = 22;
        for (int c = 0; c < ColorCount; c++)
        {
            int color = c;
            double x = cx + (c - 1.5) * 64;
            _canvas.Children.Add(Art.Circle(x, y, r, Art.Brush(CardColors[c]), Brushes.White, 3));
            ColorMark(_canvas, c, x, y, 8, Brushes.White);
            _clickables.Add((new Rect(x - r, y - r, r * 2, r * 2), () =>
            {
                int wild = _pendingWild;
                _pendingWild = -1;
                Do("play", wild, color);
            }));
        }
    }

    void DrawButtons(LastCardView v)
    {
        var a = _area;
        double y = a.Bottom - CardH - 78;
        Label(Status(), a.Center.X, y - 34, 15, Gold, center: true);
        if (_mode == Mode.Guest && _room.State == RoomState.Lost)
        {
            Button(L.T("Leave the room"), a.Center.X, y, 200, LeaveRoom);
            return;
        }
        if (v.Over)
        {
            if (_mode != Mode.Guest || _room.State == RoomState.Joined) Button(L.T("New game"), a.Center.X - 110, y, 180, NewGameClicked);
            Button(_mode == Mode.Solo ? L.T("Other games…") : L.T("Leave the room"), a.Center.X + 110, y, 180, () =>
            {
                if (_mode == Mode.Solo) { _rules = null; _view = null; _mode = Mode.Idle; Changed(); }
                else LeaveRoom();
            });
            return;
        }
        // "last card!": on your turn with two cards, or right after going down to one
        int mine = v.Hand.Count;
        if (!v.Called[v.Seat] && (mine == 1 || mine == 2 && v.MyTurn))
            Button(L.T("Last card!"), a.Left + 120, y + 40, 170, () => Do("call", -1, -1), hot: true);
        if (v.MyTurn && v.Drew) Button(L.T("Pass"), a.Right - 110, y + 40, 150, () => Do("pass", -1, -1));
    }

    // ------------------------------------------------------------------ cards

    /// <summary>
    /// A card: its colour with a white oval in the middle holding the number or symbol, and a shape in the
    /// corners for each colour (circle, triangle, square, diamond), so colour is never the only clue.
    /// </summary>
    public static Border CardFace(int card, double w, double h)
    {
        int color = ColorOf(card);
        bool wild = color == Wild;
        var ink = wild ? Brushes.Black : Art.Brush(CardColors[color]);
        var inner = new Canvas { Width = w, Height = h };
        if (wild)
        {
            // a wild's oval is quartered in the four colours
            for (int q = 0; q < ColorCount; q++)
            {
                double a0 = q * 90 - 45, a1 = a0 + 90;
                var (x0, y0) = Art.Polar(1, a0);
                var (x1, y1) = Art.Polar(1, a1);
                var wedge = Art.PathOf(string.Create(CultureInfo.InvariantCulture,
                    $"M{w / 2},{h / 2} L{w / 2 + x0 * w * 0.34},{h / 2 + y0 * h * 0.36} A{w * 0.34},{h * 0.36} 0 0 1 {w / 2 + x1 * w * 0.34},{h / 2 + y1 * h * 0.36} Z"),
                    Art.Brush(CardColors[q]));
                inner.Children.Add(wedge);
            }
        }
        else inner.Children.Add(Art.At(new Ellipse { Width = w * 0.72, Height = h * 0.66, Fill = Brushes.White, RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative), RenderTransform = new RotateTransform(28) }, w * 0.14, h * 0.17));

        string corner = KindOf(card) switch
        {
            Kind.Number => NumberOf(card).ToString(CultureInfo.InvariantCulture),
            Kind.DrawTwo => "+2",
            Kind.WildDrawFour => "+4",
            Kind.Skip => "⊘",
            Kind.Reverse => "⇄",
            _ => "W",
        };
        switch (KindOf(card))
        {
            case Kind.Skip:
                inner.Children.Add(Art.Circle(w / 2, h / 2, w * 0.2, null, ink, 5));
                inner.Children.Add(Art.PathOf(string.Create(CultureInfo.InvariantCulture, $"M{w / 2 - w * 0.14},{h / 2 + w * 0.14} L{w / 2 + w * 0.14},{h / 2 - w * 0.14}"), null, ink, 5));
                break;
            case Kind.Reverse:
                inner.Children.Add(Art.PathOf(string.Create(CultureInfo.InvariantCulture,
                    $"M{w * 0.3},{h * 0.44} L{w * 0.62},{h * 0.44} L{w * 0.62},{h * 0.38} L{w * 0.74},{h * 0.47} L{w * 0.62},{h * 0.56} L{w * 0.62},{h * 0.5} L{w * 0.3},{h * 0.5} Z " +
                    $"M{w * 0.7},{h * 0.56} L{w * 0.38},{h * 0.56} L{w * 0.38},{h * 0.62} L{w * 0.26},{h * 0.53} L{w * 0.38},{h * 0.44} L{w * 0.38},{h * 0.5} L{w * 0.7},{h * 0.5} Z"), ink));
                break;
            case Kind.Wild:
                break;
            default:
                var big = new TextBlock { Text = corner, FontFamily = Fx.Font, FontSize = h * 0.34, FontWeight = FontWeight.Black, Foreground = wild ? Brushes.White : ink };
                big.Measure(Size.Infinity);
                inner.Children.Add(Art.At(big, (w - big.DesiredSize.Width) / 2, (h - big.DesiredSize.Height) / 2));
                break;
        }
        var small = KindOf(card) is Kind.Skip or Kind.Reverse ? "" : corner; // the symbol glyphs aren't in every font
        if (small.Length > 0)
        {
            inner.Children.Add(Art.At(new TextBlock { Text = small, FontFamily = Fx.Font, FontSize = h * 0.16, FontWeight = FontWeight.Black, Foreground = Brushes.White }, 5, 2));
            inner.Children.Add(Art.At(new TextBlock { Text = small, FontFamily = Fx.Font, FontSize = h * 0.16, FontWeight = FontWeight.Black, Foreground = Brushes.White, RenderTransform = new RotateTransform(180) }, w - 5 - h * 0.1 * small.Length, h - h * 0.2 - 2));
        }
        if (!wild)
        {
            ColorMark(inner, color, w - 10, 11, 4.5, Brushes.White);
            ColorMark(inner, color, 10, h - 11, 4.5, Brushes.White);
        }
        return new Border
        {
            Width = w, Height = h, CornerRadius = new CornerRadius(8), BorderBrush = Brushes.White, BorderThickness = new Thickness(2.5),
            Background = wild ? Art.Brush("#1B1B22") : Art.Brush(CardColors[color]), Child = inner, IsHitTestVisible = false,
            BoxShadow = new BoxShadows(new BoxShadow { OffsetX = 1, OffsetY = 3, Blur = 6, Color = Avalonia.Media.Color.FromArgb(90, 0, 0, 0) }),
        };
    }

    /// <summary>The shape that stands for a colour: circle (red), triangle (yellow), square (green), diamond (blue).</summary>
    static void ColorMark(Canvas into, int color, double x, double y, double r, IBrush fill)
    {
        string path = color switch
        {
            0 => "",
            1 => $"M{x},{y - r} L{x + r},{y + r * 0.8} L{x - r},{y + r * 0.8} Z",
            2 => $"M{x - r * 0.85},{y - r * 0.85} h{r * 1.7} v{r * 1.7} h{-r * 1.7} Z",
            _ => $"M{x},{y - r} L{x + r},{y} L{x},{y + r} L{x - r},{y} Z",
        };
        if (color == 0) into.Children.Add(Art.Circle(x, y, r * 0.9, fill));
        else into.Children.Add(Art.PathOf(string.Create(CultureInfo.InvariantCulture, $"{path}"), fill));
    }

    /// <summary>The back: a dark card with the four colours in a tilted band, so it never reads as a face.</summary>
    public static Border CardBack(double w, double h)
    {
        var inner = new Canvas { Width = w, Height = h, ClipToBounds = true };
        var band = new StackPanel { Orientation = Orientation.Horizontal, RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative), RenderTransform = new RotateTransform(-35) };
        foreach (var c in CardColors) band.Children.Add(new Rectangle { Width = w * 0.16, Height = h * 1.4, Fill = Art.Brush(c) });
        inner.Children.Add(Art.At(band, w / 2 - w * 0.32, -h * 0.2));
        inner.Children.Add(Art.At(new Ellipse { Width = w * 0.46, Height = w * 0.46, Fill = Art.Brush("#1B1B22"), Stroke = Brushes.White, StrokeThickness = Math.Max(1, w * 0.04) }, w * 0.27, h / 2 - w * 0.23));
        return new Border
        {
            Width = w, Height = h, CornerRadius = new CornerRadius(w * 0.12), BorderBrush = Brushes.White, BorderThickness = new Thickness(Math.Max(1.5, w * 0.035)),
            Background = Art.Brush("#1B1B22"), Child = inner, IsHitTestVisible = false,
            BoxShadow = new BoxShadows(new BoxShadow { OffsetX = 1, OffsetY = 2, Blur = 4, Color = Avalonia.Media.Color.FromArgb(80, 0, 0, 0) }),
        };
    }

    // ------------------------------------------------------------------ pieces

    void Place(Control c, double x, double y) => _canvas.Children.Add(Art.At(c, x, y));

    void Label(string text, double x, double y, double size, Color color, bool center = false)
    {
        var t = new TextBlock { Text = text, FontFamily = Fx.Font, FontSize = size, FontWeight = FontWeight.Bold, Foreground = Art.Brush(color), IsHitTestVisible = false };
        if (center)
        {
            t.Measure(Size.Infinity);
            x -= t.DesiredSize.Width / 2;
        }
        Place(t, x, y);
    }

    void Button(string text, double cx, double y, double width, Action click, bool hot = false)
    {
        var b = new Border
        {
            Width = width, Height = 40, CornerRadius = new CornerRadius(20),
            Background = hot ? Art.Brush(CardColors[0]) : Art.Brush(235, 255, 209, 102),
            BorderBrush = hot ? Brushes.White : Art.Brush("#8A6D1F"), BorderThickness = new Thickness(hot ? 2 : 1.5), IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = text, FontFamily = Fx.Font, FontSize = 15, FontWeight = FontWeight.Bold, Foreground = hot ? Brushes.White : Art.Brush("#2A2008"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Place(b, cx - width / 2, y);
        _clickables.Add((new Rect(cx - width / 2, y, width, 40), click));
    }
}
