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

namespace DeskArcade.Games;

/// <summary>
/// What the two card tables share, free of UI so it can be tested: how a deal is timed, where a hand fans out,
/// who sits next round the table and whom the scoreboard's chip names.
/// </summary>
public static class CardTable
{
    /// <summary>How long one dealt card takes to reach its place, and how far apart the cards of a deal leave the deck.</summary>
    public const double DealSeconds = 0.18, DealGap = 0.05;
    /// <summary>How long a hand takes to re-fan, and how long a played card flies to the table.</summary>
    public const double SlideSeconds = 0.18, PlaySeconds = 0.28;

    /// <summary>
    /// When the <paramref name="k"/>-th card of <paramref name="seat"/> leaves the deck in a deal to
    /// <paramref name="players"/>: one card to each seat in turn, round after round, <see cref="DealGap"/> apart.
    /// </summary>
    public static double DealDelay(int seat, int k, int players) => (k * Math.Max(1, players) + seat) * DealGap;

    /// <summary>The whole deal in the order the cards go out: the seat, the card's index in that hand, and its delay.</summary>
    public static List<(int Seat, int Index, double Delay)> DealPlan(int players, int handSize)
    {
        var plan = new List<(int, int, double)>();
        for (int k = 0; k < handSize; k++)
            for (int seat = 0; seat < players; seat++)
                plan.Add((seat, k, DealDelay(seat, k, players)));
        return plan;
    }

    /// <summary>
    /// The left edge of each card in a hand of <paramref name="count"/> centred on <paramref name="centerX"/>: the
    /// cards sit <paramref name="gap"/> apart until the row would be wider than <paramref name="maxWidth"/>, then they overlap.
    /// </summary>
    public static double[] Fan(int count, double cardW, double maxWidth, double centerX, double gap = 6)
    {
        if (count <= 0) return Array.Empty<double>();
        double step = Math.Min(cardW + gap, (maxWidth - cardW) / Math.Max(1, count - 1));
        double x0 = centerX - (cardW + step * (count - 1)) / 2;
        var xs = new double[count];
        for (int i = 0; i < count; i++) xs[i] = x0 + i * step;
        return xs;
    }

    /// <summary>The seat after <paramref name="seat"/>, going round by <paramref name="direction"/> (+1 or −1) and skipping seats that are out.</summary>
    public static int NextSeat(int seat, int players, int direction = 1, bool[]? outSeats = null)
    {
        for (int i = 1; i <= players; i++)
        {
            int s = ((seat + direction * i) % players + players) % players;
            if (outSeats == null || s >= outSeats.Length || !outSeats[s]) return s;
        }
        return seat;
    }

    /// <summary>
    /// Whom the scoreboard's chip shows: against computer players, the computer; in a room, the player whose turn
    /// it is, or the next player round the table when it is mine (and once the game is over, <paramref name="myTurn"/> null).
    /// </summary>
    public static Opponent? Chip(string[] names, bool solo, int turnSeat, int nextSeat, bool? myTurn)
    {
        if (solo) return new Opponent(L.T("CPU"), true, 0, myTurn);
        int seat = myTurn == false ? turnSeat : nextSeat;
        return seat >= 0 && seat < names.Length ? new Opponent(names[seat], false, 0, myTurn) : null;
    }
}

/// <summary>
/// The cards a table shows: sprites keyed by card (0 and up for faces, negative numbers for the backs of the
/// other players' cards) that slide to wherever the next layout puts them, so a hand re-fans and a played card
/// flies instead of jumping. Positions are card centres in table coordinates; the layer moves with the table.
/// A layout runs from <see cref="Begin"/> to <see cref="End"/>, which hands back the cards it didn't place.
/// </summary>
public sealed class TableCards
{
    public sealed class Card
    {
        internal Card(int key, Sprite sprite, Control visual)
        {
            Key = key;
            Sprite = sprite;
            Visual = visual;
        }

        public int Key { get; }
        public Sprite Sprite { get; }
        /// <summary>The face or back itself, for a highlight or a dimmed look.</summary>
        public Control Visual { get; }
        /// <summary>Where the card is now, and where it is heading.</summary>
        public Vec2 Pos, Target;
        public double Angle, TargetAngle, Scale = 1, TargetScale = 1;
        /// <summary>Effects on top of the position: raised by, turned by, grown by.</summary>
        public double Lift, Spin, Bulge;
        internal Anims.Tween? Move;
        internal int Gen;
        internal bool Leaving;

        public void Apply()
        {
            Sprite.Set(new Vec2(Pos.X, Pos.Y - Lift), Angle + Spin);
            Sprite.Scale = Scale * (1 + Bulge);
        }
    }

    readonly Anims _anims;
    readonly Dictionary<int, Card> _cards = new();
    readonly List<Card> _order = new();
    int _gen;

    public TableCards(Anims anims) => _anims = anims;

    public Canvas Layer { get; } = new() { IsHitTestVisible = false };

    public bool Has(int key) => _cards.TryGetValue(key, out var c) && !c.Leaving;
    public Card? Get(int key) => _cards.TryGetValue(key, out var c) && !c.Leaving ? c : null;
    /// <summary>The cards the last layout placed, in its order.</summary>
    public IEnumerable<Card> Shown => _order.Where(c => !c.Leaving);

    /// <summary>Starts a layout: every card placed before <see cref="End"/> stays, the rest are handed back there.</summary>
    public void Begin()
    {
        _gen++;
        _order.Clear();
    }

    /// <summary>
    /// Puts the card at <paramref name="target"/>, sliding there from wherever it is. A card not on the table yet is
    /// made with <paramref name="make"/> (a control with a Width and Height) and starts at <paramref name="from"/>, or
    /// in place; with a <paramref name="delay"/> it stays hidden until it sets off.
    /// </summary>
    public Card Place(int key, Func<Control> make, Vec2 target, double angle = 0, double scale = 1, Vec2? from = null, double fromScale = 1,
        double seconds = CardTable.SlideSeconds, Func<double, double>? ease = null, double delay = 0, Action? done = null)
    {
        if (!_cards.TryGetValue(key, out var card) || card.Leaving)
        {
            if (card != null) Drop(card);
            var visual = make();
            var sprite = new Sprite { IsHitTestVisible = false };
            sprite.Rotor.Children.Add(Art.At(visual, -visual.Width / 2, -visual.Height / 2));
            card = new Card(key, sprite, visual) { Pos = from ?? target, Angle = angle, Scale = from != null ? fromScale : scale };
            if (delay > 0) sprite.Opacity = 0;
            card.Apply();
            Layer.Children.Add(sprite);
            _cards[key] = card;
        }
        if (card.Gen != _gen)
        {
            card.Gen = _gen;
            _order.Add(card);
        }
        Slide(card, target, angle, scale, seconds, ease, delay, done);
        return card;
    }

    void Slide(Card card, Vec2 target, double angle, double scale, double seconds, Func<double, double>? ease, double delay, Action? done)
    {
        bool heading = card.Move is { Finished: false } && card.Target == target && card.TargetAngle == angle && card.TargetScale == scale;
        if (heading) return;
        card.Move?.Cancel();
        card.Target = target;
        card.TargetAngle = angle;
        card.TargetScale = scale;
        if (card.Pos == target && card.Angle == angle && card.Scale == scale)
        {
            done?.Invoke();
            return;
        }
        var p0 = card.Pos;
        double a0 = card.Angle, s0 = card.Scale;
        var sprite = card.Sprite;
        card.Move = _anims.Add(seconds, k =>
        {
            if (sprite.Opacity < 1) sprite.Opacity = 1;
            card.Pos = p0 + (target - p0) * k;
            card.Angle = a0 + (angle - a0) * k;
            card.Scale = s0 + (scale - s0) * k;
            card.Apply();
        }, ease, done, delay);
    }

    /// <summary>Takes the card off the table: it slides to <paramref name="to"/> (or fades where it is) and goes.</summary>
    public void Remove(int key, Vec2? to = null, double scale = 1, double seconds = CardTable.SlideSeconds, Func<double, double>? ease = null, double delay = 0)
    {
        if (!_cards.TryGetValue(key, out var card) || card.Leaving) return;
        card.Leaving = true;
        card.Move?.Cancel();
        Layer.Children.Remove(card.Sprite);
        Layer.Children.Add(card.Sprite); // on top while it goes
        var p0 = card.Pos;
        var target = to ?? card.Pos;
        double s0 = card.Scale;
        card.Move = _anims.Add(seconds, k =>
        {
            card.Pos = p0 + (target - p0) * k;
            card.Scale = s0 + (scale - s0) * k;
            card.Sprite.Opacity = to == null ? 1 - k : Math.Min(1, (1 - k) * 4);
            card.Apply();
        }, ease, () => Drop(card), delay);
    }

    void Drop(Card card)
    {
        card.Move?.Cancel();
        Layer.Children.Remove(card.Sprite);
        if (_cards.TryGetValue(card.Key, out var current) && current == card) _cards.Remove(card.Key);
    }

    /// <summary>Ends a layout: its cards go front to back in the order it placed them, and the ones it didn't place come back.</summary>
    public List<Card> End()
    {
        for (int i = 0; i < _order.Count; i++)
        {
            int at = Layer.Children.IndexOf(_order[i].Sprite);
            if (at >= 0 && at != i) Layer.Children.Move(at, i);
        }
        return _cards.Values.Where(c => !c.Leaving && c.Gen != _gen).ToList();
    }

    public void Clear()
    {
        foreach (var card in _cards.Values) card.Move?.Cancel();
        _cards.Clear();
        _order.Clear();
        Layer.Children.Clear();
    }

    /// <summary>The cards bob up one after another, left to right: a little wave for a win.</summary>
    public void Wave(double lift = 14)
    {
        int i = 0;
        foreach (var card in Shown.ToList())
            _anims.Add(0.45, k => { card.Lift = lift * k; card.Apply(); }, Ease.Pulse, delay: Fx.ReducedMotion ? 0 : 0.06 * i++);
    }

    /// <summary>A quick full turn (a Reverse landing on the pile).</summary>
    public void Spin(Card card) =>
        _anims.Add(0.5, k => { card.Spin = 360 * k; card.Apply(); }, Ease.OutCubic, () => { card.Spin = 0; card.Apply(); });

    /// <summary>A quick swell (a Skip or a draw card landing on the pile).</summary>
    public void Flash(Card card, double by = 0.18) => _anims.Add(0.35, k => { card.Bulge = by * k; card.Apply(); }, Ease.Pulse);
}

/// <summary>What one player sees of a Durak game: their own hand, and only counts of everyone else's.</summary>
public sealed class DurakView
{
    public int Game { get; set; }
    public int Seat { get; set; }
    public string[] Names { get; set; } = Array.Empty<string>();
    public bool[] Cpu { get; set; } = Array.Empty<bool>();
    public List<int> Hand { get; set; } = new();
    public int[] Counts { get; set; } = Array.Empty<int>();
    /// <summary>Attack and defense card per pair (defense −1 while unbeaten).</summary>
    public List<int[]> Table { get; set; } = new();
    public int TrumpCard { get; set; }
    public int TrumpSuit { get; set; }
    public int Deck { get; set; }
    public int Discarded { get; set; }
    public int Attacker { get; set; }
    public int Defender { get; set; }
    public bool Taking { get; set; }
    public bool[] Done { get; set; } = Array.Empty<bool>();
    public bool[] Out { get; set; } = Array.Empty<bool>();
    public int Limit { get; set; }
    public bool Over { get; set; }
    public int Durak { get; set; } = -1;
    public int Version { get; set; }
    /// <summary>The last action number the host has handled from this player.</summary>
    public int Ack { get; set; }

    public int Players => Names.Length;
    public int Unbeaten => Table.Count(p => p[1] < 0);

    public static DurakView Of(DurakRules r, int seat, int game, string[] names, bool[] cpu, int ack) => new()
    {
        Game = game, Seat = seat, Names = names, Cpu = cpu, Hand = r.Hands[seat].ToList(),
        Counts = r.Hands.Select(h => h.Count).ToArray(), Table = r.Table.Select(p => new[] { p.Attack, p.Defense }).ToList(),
        TrumpCard = r.TrumpCard, TrumpSuit = r.TrumpSuit, Deck = r.Deck.Count, Discarded = r.Discarded,
        Attacker = r.Attacker, Defender = r.Defender, Taking = r.Taking, Done = r.Done.ToArray(), Out = r.Out.ToArray(),
        Limit = r.Limit, Over = r.Over, Durak = r.Durak, Version = r.Version, Ack = ack,
    };

    public bool Beats(int defense, int attack) =>
        DurakRules.Suit(defense) == DurakRules.Suit(attack) ? DurakRules.Rank(defense) > DurakRules.Rank(attack) : DurakRules.Suit(defense) == TrumpSuit;

    /// <summary>Whether the bout still waits on <paramref name="seat"/> to throw in or say done (as <see cref="DurakRules.AwaitsAttacker"/>).</summary>
    public bool Awaits(int seat) =>
        !Over && seat != Defender && seat >= 0 && seat < Out.Length && seat < Done.Length && seat < Counts.Length &&
        !Out[seat] && Table.Count > 0 && !Done[seat] && Counts[seat] > 0 && (Taking || Unbeaten == 0);

    /// <summary>
    /// The seat the bout waits on, for the scoreboard: the attacker to lead, the defender to answer, then the
    /// attackers round the table to throw in or say done; −1 once the game is over.
    /// </summary>
    public int TurnSeat()
    {
        if (Over || Players == 0) return -1;
        if (Table.Count == 0) return Attacker;
        if (!Taking && Unbeaten > 0) return Defender;
        for (int i = 0; i < Players; i++)
        {
            int s = (Attacker + i) % Players;
            if (Awaits(s)) return s;
        }
        return Defender;
    }

    /// <summary>Whether this player has something to do right now.</summary>
    public bool MyTurn => !Over && Seat < Out.Length && !Out[Seat] && (TurnSeat() == Seat || Awaits(Seat));
}

/// <summary>
/// Durak (see <see cref="DurakRules"/>) for 2–4 players: against computer players, or with co-workers in a
/// room on the local network (<see cref="RoomLink"/>). The host of a room runs the game and sends each
/// player only their own view ("ds|json"); players send their moves ("da|seq|kind|card|index") in order and
/// re-send them until the host's view acknowledges them; the host takes each player's moves strictly in
/// sequence, so none is lost or applied twice. The host's game runs on its own timer, so it goes on for
/// everyone even while the host's overlay is hidden or showing another game. Computer players fill empty seats and take over the seat
/// of anyone who drops out. Room setup happens in <see cref="RoomWindow"/>, since typing a room code
/// needs a window that can take the keyboard. The table has a grip (or right-drag it) and remembers where
/// it was put; the cards are laid out in table coordinates and slide to their places (<see cref="TableCards"/>).
/// </summary>
public sealed class DurakGame : MiniGame, IRoomGame
{
    public const double CardW = 66, CardH = 94;
    const double CpuDelay = 0.8, SendEvery = 0.25, ResendEvery = 0.3, BackScale = 0.55;
    static readonly string[] SuitGlyphs = { "♠", "♣", "♦", "♥" };
    static readonly Color Gold = Color.FromRgb(255, 209, 102);
    static readonly Color Felt = Color.FromRgb(22, 92, 60);
    static readonly IBrush GoldBrush = Art.Brush(Gold), CardEdge = Art.Brush("#3B3B45");

    enum Mode { Idle, Solo, Hosting, Guest }

    readonly Canvas _root = new(), _canvas = new(), _top = new();
    readonly TranslateTransform _rootTr = new();
    readonly TableCards _cards;
    readonly DragHandle _handle;
    readonly List<(Rect Box, Action Click)> _clickables = new();
    readonly RoomLink _room = new();

    Mode _mode;
    DurakRules? _rules;
    DurakView? _view, _shown;
    string[] _names = Array.Empty<string>();
    bool[] _cpu = Array.Empty<bool>();
    int[] _seatOfGuest = Array.Empty<int>(); // host: room seat → game seat (−1: not playing)
    int[] _lastSeq = Array.Empty<int>();       // host: last action number handled per game seat
    bool[] _dropped = Array.Empty<bool>();     // host: seats the computer took over after their player dropped out
    readonly List<(int Seq, string Message)> _outbox = new(); // guest: our actions, re-sent until acknowledged
    readonly Avalonia.Threading.DispatcherTimer _hostTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    readonly System.Diagnostics.Stopwatch _hostClock = new();
    int _game, _target = -1, _nextSeq, _drawnVersion = -1;
    double _cpuT, _sendT, _resendT, _demoT, _inviteT;
    string _drawnPanel = "";
    int _bridgedSession = -1; // the LAN session this table last opened a room for
    bool _announced, _demo;
    Rect _area;  // the table on the screen
    Rect _table; // the same, in table coordinates (everything is drawn in these and the root is moved)
    Vec2 _origin;
    Size _drawnSize;

    public DurakGame(IGameHost host) : base(host)
    {
        _cards = new TableCards(Anims);
        _root.RenderTransformOrigin = RelativePoint.TopLeft;
        _root.RenderTransform = _rootTr;
        _root.Children.Add(_canvas);
        _root.Children.Add(_cards.Layer);
        _root.Children.Add(_top);
        Layer.Children.Add(_root);
        _handle = new DragHandle(host, Id, Title);
        Layer.Children.Add(_handle.Visual);
        _room.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(OnRoomChanged);
        _room.MessageArrived += () => Avalonia.Threading.Dispatcher.UIThread.Post(Host.Wake);
        _hostTimer.Tick += (_, _) => HostTick();
    }

    /// <summary>The host's game loop: guests' moves, computer turns and views, whatever the overlay shows.</summary>
    void HostTick()
    {
        double dt = _hostClock.Elapsed.TotalSeconds;
        _hostClock.Restart();
        if (_mode != Mode.Hosting || _rules == null)
        {
            _hostTimer.Stop();
            return;
        }
        HostUpdate(Math.Min(dt, 0.5));
        CpuTurns(Math.Min(dt, 0.5));
    }

    public override string Id => "durak";
    public override string Title => "Durak";
    public RoomLink Room => _room;
    public string MinVersion => "1.6";

    /// <summary>Over the two-player link the host's table opens a room that the other player joins (see <see cref="LanBridge"/>).</summary>
    public override bool SupportsLan => true;
    public bool Playing => _view != null && !_view.Over;

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        s.Rotor.Children.Add(Art.At(new Rectangle { Width = 11, Height = 15, RadiusX = 2, RadiusY = 2, Fill = Brushes.White, Stroke = Art.Brush("#555"), StrokeThickness = 1, RenderTransform = new RotateTransform(-12) }, -9, -8));
        s.Rotor.Children.Add(Art.At(new Rectangle { Width = 11, Height = 15, RadiusX = 2, RadiusY = 2, Fill = Brushes.White, Stroke = Art.Brush("#555"), StrokeThickness = 1, RenderTransform = new RotateTransform(12) }, -1, -8));
        s.Rotor.Children.Add(Art.At(new TextBlock { Text = "♥", FontSize = 9, Foreground = Art.Brush("#D0303A") }, 1, -6));
        return s;
    }

    public override HudInfo Hud => new(
        _view == null ? "—" : _view.Hand.Count.ToString(),
        Status(),
        L.F("Wins {0}", Host.Stats.Get("durak.wins")));

    /// <summary>The scoreboard's chip: the computer, or in a room whoever the bout waits on (the next player when it waits on me).</summary>
    public override Opponent? Opponent => _view is { } v && _mode != Mode.Idle
        ? CardTable.Chip(v.Names, _mode == Mode.Solo, v.TurnSeat(), CardTable.NextSeat(v.Seat, v.Players, 1, v.Out), v.Over ? null : v.MyTurn)
        : null;

    string Name(int seat) => _view != null && seat < _view.Names.Length ? _view.Names[seat] : "?";

    string Status()
    {
        if (_mode == Mode.Guest && _room.State == RoomState.Joining) return L.F("Joining room {0}…", _room.Code);
        if (_mode == Mode.Guest && _room.State == RoomState.Lost) return L.T("The room closed · set up a new game");
        if (_mode == Mode.Hosting && _rules == null) return L.F("Room {0} · {1} at the table", _room.Code, _room.Seats().Count(x => x.Connected));
        if (_view == null) return _mode == Mode.Guest ? L.F("In room {0} · waiting for the host to start", _room.Code) : L.T("Click Durak on the table to start a game");
        var v = _view;
        if (v.Over)
            return v.Durak == v.Seat ? L.T("You're the durak · click New game") : v.Durak < 0 ? L.T("A draw · click New game") : L.F("{0} is the durak · click New game", Name(v.Durak));
        if (v.Out[v.Seat]) return L.T("You're out of cards · watching the others finish");
        if (v.Seat == v.Defender)
            return v.Taking ? L.T("You take the cards · the others may still throw in")
                : v.Unbeaten > 0 ? L.T("Beat each card (click a card on the table first to choose) or take") : L.F("{0} may throw in more", Name(v.Attacker));
        if (v.Table.Count == 0)
            return v.Seat == v.Attacker ? L.F("Your attack on {0} · play any card", Name(v.Defender)) : L.F("{0} attacks {1}", Name(v.Attacker), Name(v.Defender));
        return v.Done[v.Seat] ? L.F("{0} is defending", Name(v.Defender)) : L.T("Throw in a card of a rank on the table, or click Done");
    }

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

    /// <summary>Opens a room with <paramref name="code"/>, or a random one.</summary>
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

    /// <summary>Host: starts the game with everyone in the room, adding computer players up to <paramref name="players"/> seats.</summary>
    public void StartRoom(int players)
    {
        if (_mode != Mode.Hosting || !_room.IsHost) return;
        _room.Open = false; // close the room first, so nobody joins or leaves between the roster and the deal
        foreach (var gone in _room.Seats().Where(s => !s.Connected && s.Seat > 0)) _room.Kick(gone.Seat);
        var seats = _room.Seats().Where(s => s.Connected).ToList();
        int humans = seats.Count;
        players = Math.Clamp(Math.Max(players, humans), 2, RoomLink.MaxSeats);
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
        _rules = new DurakRules(_names.Length, Rng);
        // action numbers carry on across deals, so a late copy of a move from the last deal can't count in this one
        if (_lastSeq.Length != _names.Length) _lastSeq = new int[_names.Length];
        if (_dropped.Length != _names.Length) _dropped = new bool[_names.Length];
        _game++;
        _target = -1;
        _announced = false;
        _cpuT = CpuDelay;
        RefreshView();
        Host.Sound.Play("whoosh", 0.4, 1.4);
    }

    void NewGameClicked()
    {
        if (_mode is Mode.Solo or Mode.Hosting && _rules != null) NewDeal();
        else if (_mode == Mode.Guest) SendAction("again", -1, -1);
    }

    void GameOverFx()
    {
        if (_view is not { Over: true } v || _announced) return;
        _announced = true;
        var at = new Vec2(_area.Center.X, _area.Top + _area.Height * 0.3);
        Host.Stats.Add("durak.games");
        if (v.Durak == v.Seat)
        {
            Host.Fx.Popup(at, L.T("YOU'RE THE DURAK!"), Colors.White, 40, 2.6, L.T("better luck next deal"));
            Host.Sound.Play("buzzer", 0.45);
        }
        else
        {
            if (v.Durak >= 0) Host.Stats.Add("durak.wins");
            if (_mode != Mode.Solo && v.Durak >= 0) Host.Stats.Add("lan.wins");
            Host.Fx.Popup(at, v.Durak < 0 ? L.T("A DRAW") : L.T("NOT THE FOOL!"), Gold, 42, 2.6,
                v.Durak < 0 ? L.T("the last cards went out together") : L.F("{0} is the durak", Name(v.Durak)));
            Host.Fx.Burst(at, Themes.Current.Confetti, 40, 520, 700, 7, 1.0);
            Host.Sound.Play("best", 0.8);
            _cards.Wave();
        }
    }

    // ------------------------------------------------------------------ actions

    /// <summary>A click on a card in our hand: defend, attack or throw it in, whichever fits.</summary>
    void HandCardClicked(int card)
    {
        if (_view is not { Over: false } v || v.Out[v.Seat]) return;
        if (v.Seat == v.Defender)
        {
            if (v.Taking) return;
            int index = _target >= 0 && _target < v.Table.Count && v.Table[_target][1] < 0 && v.Beats(card, v.Table[_target][0]) ? _target
                : v.Table.FindIndex(p => p[1] < 0 && v.Beats(card, p[0]));
            if (index < 0)
            {
                Host.Fx.Popup(Host.Pointer - new Vec2(0, 40), L.T("that card can't beat any of them"), Colors.White, 18, 1.0);
                return;
            }
            _target = -1;
            Do("defend", card, index);
        }
        else Do("attack", card, -1);
    }

    void Do(string kind, int card, int index)
    {
        if (_mode == Mode.Guest) SendAction(kind, card, index);
        else if (_rules != null && _rules.Act(_view!.Seat, kind, card, index))
        {
            Host.Sound.Play(kind == "take" ? "whoosh" : "board", 0.45, 1.5);
            _cpuT = CpuDelay;
            RefreshView();
        }
        else Host.Fx.Popup(Host.Pointer - new Vec2(0, 40), L.T("not allowed right now"), Colors.White, 18, 1.0);
    }

    /// <summary>Guest: queues a move; moves go out in order and are re-sent until the host's view acknowledges them.</summary>
    void SendAction(string kind, int card, int index)
    {
        _nextSeq = Math.Max(_nextSeq, _view?.Ack ?? 0) + 1;
        var message = string.Create(CultureInfo.InvariantCulture, $"da|{_nextSeq}|{kind}|{card}|{index}");
        _outbox.Add((_nextSeq, message));
        _room.SendToHost(message);
        _resendT = 0;
        Host.Sound.Play("board", 0.35, 1.5);
    }

    // ------------------------------------------------------------------ simulation

    public override bool Update(double dt)
    {
        if (_handle.Dragging)
        {
            _origin = _handle.Move(Host.Pointer, _area.Size);
            Layout();
        }
        bool inviting = LanBridge(dt);
        if (_mode == Mode.Guest) GuestUpdate(dt);
        else if (_mode == Mode.Solo) CpuTurns(dt); // a room's host plays on its own timer (HostTick)
        if (_view != null ? _view.Version != _drawnVersion : PanelKey() != _drawnPanel) Draw();
        if (_view is { Over: true }) GameOverFx();
        bool animating = Anims.Update(dt);
        // frames are needed only while something is due; arriving messages wake the overlay
        return _handle.Dragging || animating || inviting || _mode switch
        {
            Mode.Solo => _rules is { Over: false } || _demo,
            Mode.Guest => _room.State == RoomState.Joining || _outbox.Count > 0 || _demo && _room.State == RoomState.Joined,
            _ => false,
        };
    }

    void CpuTurns(double dt)
    {
        if (_rules is not { Over: false } r || (_cpuT -= dt) > 0) return;
        _cpuT = CpuDelay * (0.7 + Rng.NextDouble() * 0.6);
        // the defender answers first, then attackers from the main attacker round the table
        var order = new[] { r.Defender }.Concat(Enumerable.Range(0, r.Players).Select(i => (r.Attacker + i) % r.Players));
        foreach (int seat in order.Where(s => _cpu[s] || _demo && s == MySeat))
        {
            if (r.CpuAction(seat) is not { } a) continue;
            r.Act(seat, a.Kind, a.Card, a.Index);
            Host.Sound.Play(a.Kind == "take" ? "whoosh" : "board", 0.35, 1.3 + Rng.NextDouble() * 0.3);
            RefreshView();
            return;
        }
    }

    int MySeat => _mode == Mode.Hosting || _mode == Mode.Solo ? 0 : _view?.Seat ?? -1;

    void RefreshView()
    {
        if (_rules == null) return;
        _view = DurakView.Of(_rules, 0, _game, _names, _cpu, _lastSeq[0]);
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
            if (f.Length != 5 || f[0] != "da" || !int.TryParse(f[1], out int seq) || seq != _lastSeq[seat] + 1 ||
                !int.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int card) ||
                !int.TryParse(f[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
                continue;
            _lastSeq[seat] = seq; // handled, whether or not the rules accept it; the view tells the player
            if (f[2] == "again" && _rules.Over) NewDeal();
            else if (_rules.Act(seat, f[2], card, index))
            {
                Host.Sound.Play("board", 0.4, 1.4);
                _cpuT = CpuDelay;
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
                _room.SendTo(s.Seat, "ds|" + JsonSerializer.Serialize(DurakView.Of(_rules, g, _game, _names, _cpu, _lastSeq[g])));
    }

    void GuestUpdate(double dt)
    {
        DurakView? latest = null;
        while (_room.TryReceive(out var msg))
        {
            if (!msg.Body.StartsWith("ds|", StringComparison.Ordinal)) continue;
            try { latest = JsonSerializer.Deserialize<DurakView>(msg.Body[3..]); }
            catch (JsonException) { }
        }
        // UDP can deliver out of order: only ever move forward
        if (latest != null && (_view == null || (latest.Game, latest.Version, latest.Ack).CompareTo((_view.Game, _view.Version, _view.Ack)) > 0))
        {
            if (_view != null && latest.Game != _view.Game) _announced = false;
            _view = latest;
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
    /// Durak over the two-player link (tray → Play over LAN): the link only carries two-player games, so the
    /// host's table opens a room and keeps inviting the other player, whose copy joins it by itself. Without
    /// this the pair sat at two separate tables and each ended up playing the computer.
    /// Returns true while an invitation is still going out.
    /// </summary>
    bool LanBridge(double dt)
    {
        var lan = Host.Lan;
        if (!lan.Connected) return false;
        while (lan.TryReceive(out var msg))
        {
            var f = msg.Split('|');
            if (f.Length != 2 || f[0] != "dk" || lan.Role != LanRole.Guest || lan.PeerAddress is not { } ip) continue;
            string code = RoomLink.CleanCode(f[1]);
            bool inIt = _mode == Mode.Guest && _room.Code == code && _room.State is RoomState.Joining or RoomState.Joined;
            // a game against the computer that is still going is not thrown away; the host keeps asking
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
            lan.Send("dk|" + _room.Code);
        }
        return true;
    }

    void OnRoomChanged()
    {
        Changed();
        Host.Wake();
    }

    void Changed()
    {
        _drawnVersion = -1;
        Host.HudChanged();
        Host.Wake();
    }

    // ------------------------------------------------------------------ layout and input

    /// <summary>The table sits along the bottom of the screen until it is dragged somewhere, which is remembered.</summary>
    public override void Layout()
    {
        var a = Host.Arena;
        double w = Math.Min(a.Width - 40, 920), h = Math.Min(a.Height - 40, 560);
        if (!_handle.Dragging) _origin = _handle.Saved() ?? new Vec2(a.Center.X - w / 2, a.Bottom - h - 16);
        _origin = ClampOrigin(_origin, new Size(w, h));
        _area = new Rect(_origin.X, _origin.Y, w, h);
        _table = new Rect(0, 0, w, h);
        _rootTr.X = _origin.X;
        _rootTr.Y = _origin.Y;
        _handle.Show(_area);
        if (_drawnSize != _area.Size) Draw();
        Host.HudChanged();
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
        for (int i = _clickables.Count - 1; i >= 0; i--)
            if (_clickables[i].Box.Contains(q))
            {
                _clickables[i].Click();
                Draw();
                return false;
            }
        return false;
    }

    public override void PointerUp(Vec2 p) => _handle.End(_origin);

    public override void PointerCancel() => _handle.Cancel();

    public override void Summon(Vec2 p)
    {
        _handle.Save(ClampOrigin(new Vec2(p.X - _table.Width / 2, p.Y - _table.Height / 2), _area.Size));
        Layout();
    }

    public override void Deactivate()
    {
        _target = -1;
        _handle.Cancel();
        Anims.Finish();
    }

    // ------------------------------------------------------------------ drawing

    /// <summary>What the start panel shows depends on: the room's state and who is in it.</summary>
    string PanelKey() => $"{_mode}|{_room.State}|{_room.Code}|{string.Join(",", _room.Seats().Select(s => s.Name + s.Connected))}";

    /// <summary>
    /// Lays the table out for the current view. The static parts are drawn afresh; the cards are placed through
    /// <see cref="TableCards"/>, which compares with what is on the table already, so dealt cards come from the
    /// deck, a played card flies from the hand, and the cards of a finished bout go to whoever took them.
    /// </summary>
    void Draw()
    {
        _drawnVersion = _view?.Version ?? -1;
        _drawnPanel = _view == null ? PanelKey() : "";
        _drawnSize = _area.Size;
        _canvas.Children.Clear();
        _top.Children.Clear();
        _clickables.Clear();
        var a = _table;

        var felt = new Border
        {
            Width = a.Width, Height = a.Height, CornerRadius = new CornerRadius(22), Opacity = 0.93,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Art.Blend(Felt, Colors.White, 0.08), 0), new GradientStop(Felt, 1) },
            },
            BorderBrush = Art.Brush("#0D3D27"), BorderThickness = new Thickness(3), IsHitTestVisible = false,
        };
        _canvas.Children.Add(Art.At(felt, a.Left, a.Top));

        if (_view == null)
        {
            _cards.Clear();
            _shown = null;
            DrawStartPanel();
            return;
        }
        var v = _view;
        var prev = _shown;
        _shown = v;
        bool newDeal = prev == null || prev.Game != v.Game;
        if (newDeal) _cards.Clear();
        _cards.Begin();
        DrawOpponents(v, prev, newDeal);
        DrawDeck(v);
        DrawHand(v, newDeal);
        DrawTable(v, prev);
        DrawButtons(v);
        foreach (var card in _cards.End()) Leave(card, v, prev);
    }

    void DrawStartPanel()
    {
        var a = _table;
        Label(Title, a.Center.X, a.Top + 70, 40, Colors.White, center: true);
        Label(L.T("The last one holding cards is the durak"), a.Center.X, a.Top + 128, 16, Color.FromRgb(200, 230, 210), center: true);
        if (_mode is Mode.Guest or Mode.Hosting)
            Label(Status(), a.Center.X, a.Top + 170, 16, Gold, center: true);
        double y = a.Top + 220;
        if (_mode == Mode.Hosting)
        {
            // a room is open: start it with whoever joined (a solo game here would close the room on them)
            int humans = _room.Seats().Count(s => s.Connected);
            if (humans < 2)
            {
                Label(L.T("Waiting for a co-worker to join…"), a.Center.X, y - 6, 15, Color.FromRgb(200, 230, 210), center: true);
            }
            else
            {
                for (int cpus = 0; cpus <= Math.Min(2, RoomLink.MaxSeats - humans); cpus++)
                {
                    int seats = humans + cpus;
                    string text = cpus == 0 ? L.T("Start the game") : cpus == 1 ? L.T("Start + 1 computer") : L.F("Start + {0} computers", cpus);
                    Button(text, a.Center.X - 190 + cpus * 190, y, 180, () => StartRoom(seats));
                }
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

    // ---- places on the table (card centres, table coordinates)

    double DeckY => _table.Top + _table.Height / 2 - CardH / 2 - 10;
    Vec2 DeckPos => new(_table.Left + 28 + CardW / 2, DeckY + CardH / 2);
    Vec2 DiscardPos => new(_table.Right - 90, DeckY + CardH / 2);
    Vec2 HandPos => new(_table.Center.X, _table.Bottom - CardH / 2 - 22);

    static List<int> Others(DurakView v) => Enumerable.Range(1, v.Players - 1).Select(i => (v.Seat + i) % v.Players).ToList();

    /// <summary>The middle of another player's cards, by their place along the top of the table.</summary>
    Vec2 SeatPos(DurakView v, int seat)
    {
        var others = Others(v);
        double slot = _table.Width / Math.Max(1, others.Count);
        return new Vec2(_table.Left + slot * (Math.Max(0, others.IndexOf(seat)) + 0.5), _table.Top + 52 + CardH * BackScale / 2);
    }

    static int BackKey(int seat, int k) => -1 - (seat * 16 + k);

    static double Stagger(double delay) => Fx.ReducedMotion ? 0 : delay;

    void DrawOpponents(DurakView v, DurakView? prev, bool newDeal)
    {
        var a = _table;
        var others = Others(v);
        double slot = a.Width / Math.Max(1, others.Count);
        for (int i = 0; i < others.Count; i++)
        {
            int s = others[i];
            double cx = a.Left + slot * (i + 0.5);
            string role = v.Out[s] ? L.T("out") : s == v.Defender ? L.T("defending") : s == v.Attacker ? L.T("attacking") : "";
            Label(v.Names[s] + (v.Cpu[s] && _mode != Mode.Solo ? " · " + L.T("CPU") : ""), cx, a.Top + 12, 15, Colors.White, center: true);
            if (role.Length > 0) Label(role, cx, a.Top + 32, 12, s == v.Defender ? Color.FromRgb(255, 150, 150) : Gold, center: true);
            int n = v.Counts[s], shown = Math.Min(n, 8);
            // cards this player just took off the table pop into their hand once they have flown there
            bool took = prev != null && !newDeal && prev.Table.Count > 0 && v.Table.Count == 0 && v.Discarded == prev.Discarded && prev.Defender == s;
            int fresh = 0;
            for (int k = 0; k < shown; k++)
            {
                int key = BackKey(s, k);
                var at = new Vec2(cx - shown * 7 + k * 14 - CardW * 0.27 + CardW * BackScale / 2, a.Top + 52 + CardH * BackScale / 2);
                if (_cards.Has(key)) _cards.Place(key, () => CardBack(CardW, CardH), at, 0, BackScale);
                else if (took)
                    _cards.Place(key, () => CardBack(CardW, CardH), at, 0, BackScale, from: at, fromScale: 0.2, seconds: 0.15, ease: Ease.OutBack, delay: Stagger(CardTable.PlaySeconds));
                else
                {
                    double delay = newDeal ? CardTable.DealDelay(s, k, v.Players) : fresh++ * CardTable.DealGap;
                    _cards.Place(key, () => CardBack(CardW, CardH), at, 0, BackScale, from: DeckPos, seconds: CardTable.DealSeconds, ease: Ease.OutCubic, delay: Stagger(delay));
                }
            }
            if (n > 0) Label(n.ToString(), cx + shown * 7 + 18, a.Top + 70, 14, Colors.White);
        }
    }

    void DrawDeck(DurakView v)
    {
        var a = _table;
        double x = a.Left + 28, y = DeckY;
        if (v.Deck > 0)
        {
            var trump = CardFace(v.TrumpCard, CardW, CardH);
            trump.RenderTransform = new RotateTransform(90);
            Place(trump, x + 26, y + 6);
            if (v.Deck > 1) Place(CardBack(CardW, CardH), x, y);
        }
        else Label(SuitGlyphs[v.TrumpSuit], x + 22, y + 20, 40, SuitColor(v.TrumpSuit));
        Label(L.F("deck {0}", v.Deck), x, y + CardH + 14, 13, Colors.White);
        Label(L.F("trump {0}", SuitGlyphs[v.TrumpSuit]), x, y + CardH + 32, 13, Gold);
        if (v.Discarded > 0) Label(L.F("discarded {0}", v.Discarded), a.Right - 120, y + CardH + 14, 13, Color.FromRgb(200, 230, 210));
    }

    void DrawTable(DurakView v, DurakView? prev)
    {
        var a = _table;
        int n = v.Table.Count;
        double gap = 18, pairW = CardW + 22, total = n * pairW + Math.Max(0, n - 1) * gap;
        double x0 = a.Center.X - total / 2, y = a.Top + a.Height / 2 - CardH / 2 - 30;
        for (int i = 0; i < n; i++)
        {
            int index = i;
            double x = x0 + i * (pairW + gap);
            int attack = v.Table[i][0], defense = v.Table[i][1];
            bool selectable = v.Seat == v.Defender && !v.Taking && defense < 0;
            PlaceTableCard(v, prev, attack, new Vec2(x + CardW / 2, y + CardH / 2), 0, chosen: selectable && _target == i, defense: false);
            if (selectable) _clickables.Add((new Rect(x, y, CardW, CardH), () => _target = _target == index ? -1 : index));
            if (defense >= 0) PlaceTableCard(v, prev, defense, new Vec2(x + 20 + CardW / 2, y + 22 + CardH / 2), 8, chosen: false, defense: true);
        }
        if (v.Taking) Label(L.F("{0} takes", Name(v.Defender)), a.Center.X, y + CardH + 30, 16, Color.FromRgb(255, 150, 150), center: true);
    }

    /// <summary>A card on the table: from our hand it flies there and lands; from another player it comes from their place.</summary>
    void PlaceTableCard(DurakView v, DurakView? prev, int card, Vec2 at, double angle, bool chosen, bool defense)
    {
        TableCards.Card c;
        if (_cards.Has(card))
        {
            bool played = prev != null && prev.Hand.Contains(card) && !v.Hand.Contains(card);
            c = _cards.Place(card, () => CardFace(card, CardW, CardH), at, angle, seconds: played ? CardTable.PlaySeconds : CardTable.SlideSeconds, ease: played ? Ease.OutBack : null);
        }
        else
        {
            int by = defense ? v.Defender : PlayedBy(v, prev);
            c = _cards.Place(card, () => CardFace(card, CardW, CardH), at, angle, from: by == v.Seat ? HandPos : SeatPos(v, by), fromScale: by == v.Seat ? 1 : BackScale,
                seconds: CardTable.PlaySeconds, ease: Ease.OutBack);
        }
        var face = (Border)c.Visual;
        face.BorderBrush = chosen ? GoldBrush : CardEdge;
        face.BorderThickness = new Thickness(chosen ? 3 : 1.2);
        face.Opacity = 1;
    }

    /// <summary>Who played a card that appeared on the table: whoever has fewer cards than before, else the attacker.</summary>
    int PlayedBy(DurakView v, DurakView? prev)
    {
        if (prev != null && prev.Game == v.Game)
            for (int s = 0; s < v.Players && s < prev.Counts.Length; s++)
                if (s != v.Seat && v.Counts[s] < prev.Counts[s]) return s;
        return v.Attacker != v.Seat ? v.Attacker : v.Defender;
    }

    void DrawHand(DurakView v, bool newDeal)
    {
        var a = _table;
        var hand = v.Hand.OrderBy(c => DurakRules.Suit(c) == v.TrumpSuit).ThenBy(DurakRules.Suit).ThenBy(DurakRules.Rank).ToList();
        if (hand.Count == 0) return;
        var xs = CardTable.Fan(hand.Count, CardW, a.Width - 260, a.Center.X);
        double y = a.Bottom - CardH - 22;
        bool myTurn = !v.Over && !v.Out[v.Seat];
        int fresh = 0;
        for (int i = 0; i < hand.Count; i++)
        {
            int card = hand[i];
            double x = xs[i];
            bool playable = myTurn && Playable(v, card);
            double top = playable ? y - 12 : y;
            var at = new Vec2(x + CardW / 2, top + CardH / 2);
            TableCards.Card c;
            if (_cards.Has(card)) c = _cards.Place(card, () => CardFace(card, CardW, CardH), at); // taken off the table, or just re-fanning
            else
            {
                double delay = newDeal ? CardTable.DealDelay(v.Seat, i, v.Players) : fresh++ * CardTable.DealGap;
                c = _cards.Place(card, () => CardFace(card, CardW, CardH), at, from: DeckPos, seconds: CardTable.DealSeconds, ease: Ease.OutCubic, delay: Stagger(delay));
            }
            c.Visual.Opacity = playable ? 1 : 0.8;
            double width = i == hand.Count - 1 ? CardW : xs[i + 1] - xs[i];
            _clickables.Add((new Rect(x, top, width, CardH), () => HandCardClicked(card)));
        }
    }

    /// <summary>A card that is no longer shown: a beaten bout goes to the discard pile, a taken one to whoever took it; backs just go.</summary>
    void Leave(TableCards.Card card, DurakView v, DurakView? prev)
    {
        int i = card.Key < 0 || prev == null ? -1 : prev.Table.FindIndex(p => p[0] == card.Key || p[1] == card.Key);
        if (i < 0)
        {
            _cards.Remove(card.Key, seconds: 0.12);
            return;
        }
        double delay = Stagger(i * 0.04);
        if (v.Discarded > prev!.Discarded) _cards.Remove(card.Key, DiscardPos, 0.7, CardTable.PlaySeconds, Ease.InCubic, delay);
        else if (prev.Defender == v.Seat) _cards.Remove(card.Key, HandPos, 1, CardTable.PlaySeconds, Ease.OutCubic, delay);
        else _cards.Remove(card.Key, SeatPos(v, prev.Defender), BackScale, CardTable.PlaySeconds, Ease.OutCubic, delay);
    }

    static bool Playable(DurakView v, int card)
    {
        if (v.Seat == v.Defender) return !v.Taking && v.Table.Any(p => p[1] < 0 && v.Beats(card, p[0]));
        if (v.Out[v.Seat] || v.Table.Count >= v.Limit || v.Unbeaten >= v.Counts[v.Defender]) return false;
        if (v.Table.Count == 0) return v.Seat == v.Attacker;
        int rank = DurakRules.Rank(card);
        return v.Table.Any(p => DurakRules.Rank(p[0]) == rank || p[1] >= 0 && DurakRules.Rank(p[1]) == rank);
    }

    void DrawButtons(DurakView v)
    {
        var a = _table;
        double y = a.Bottom - CardH - 70;
        Label(Status(), a.Center.X, y - 34, 15, Gold, center: true, into: _top);
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
        if (v.Out[v.Seat]) return;
        if (v.Seat == v.Defender && !v.Taking && v.Unbeaten > 0) Button(L.T("Take"), a.Right - 110, y + 40, 150, () => Do("take", -1, -1));
        if (v.Seat != v.Defender && v.Table.Count > 0 && !v.Done[v.Seat] && (v.Taking || v.Unbeaten == 0))
            Button(L.T("Done"), a.Right - 110, y + 40, 150, () => Do("pass", -1, -1));
    }

    // ------------------------------------------------------------------ pieces

    /// <summary>Adds a static piece to the table (below the cards), or to <paramref name="into"/>.</summary>
    void Place(Control c, double x, double y, Canvas? into = null) => (into ?? _canvas).Children.Add(Art.At(c, x, y));

    void Label(string text, double x, double y, double size, Color color, bool center = false, Canvas? into = null)
    {
        var t = new TextBlock { Text = text, FontFamily = Fx.Font, FontSize = size, FontWeight = FontWeight.Bold, Foreground = Art.Brush(color), IsHitTestVisible = false };
        if (center)
        {
            t.Measure(Size.Infinity);
            x -= t.DesiredSize.Width / 2;
        }
        Place(t, x, y, into);
    }

    /// <summary>A button, drawn above the cards.</summary>
    void Button(string text, double cx, double y, double width, Action click)
    {
        var b = new Border
        {
            Width = width, Height = 40, CornerRadius = new CornerRadius(20), Background = Art.Brush(235, 255, 209, 102),
            BorderBrush = Art.Brush("#8A6D1F"), BorderThickness = new Thickness(1.5), IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = text, FontFamily = Fx.Font, FontSize = 15, FontWeight = FontWeight.Bold, Foreground = Art.Brush("#2A2008"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Place(b, cx - width / 2, y, _top);
        _clickables.Add((new Rect(cx - width / 2, y, width, 40), click));
    }

    static Color SuitColor(int suit) => suit >= 2 ? Color.FromRgb(208, 40, 52) : Color.FromRgb(25, 25, 30);

    static string RankText(int rank) => rank switch { 1 or 14 => "A", 11 => "J", 12 => "Q", 13 => "K", _ => rank.ToString(CultureInfo.InvariantCulture) };

    public static Border CardFace(int card, double w, double h) => CardFace(DurakRules.Suit(card), DurakRules.Rank(card), w, h);

    /// <summary>A card face for any game: suits ♠ 0, ♣ 1, ♦ 2, ♥ 3; ranks 2–10, J 11, Q 12, K 13 and the ace as 1 or 14.</summary>
    public static Border CardFace(int suit, int rank, double w, double h)
    {
        var color = Art.Brush(SuitColor(suit));
        var inner = new Canvas { Width = w, Height = h };
        string label = RankText(rank);
        inner.Children.Add(Art.At(new TextBlock { Text = label, FontFamily = Fx.Font, FontSize = h * 0.2, FontWeight = FontWeight.Bold, Foreground = color }, 5, 1));
        inner.Children.Add(Art.At(new TextBlock { Text = SuitGlyphs[suit], FontSize = h * 0.17, Foreground = color }, 6, h * 0.22));
        var big = new TextBlock { Text = SuitGlyphs[suit], FontSize = h * 0.42, Foreground = color };
        big.Measure(Size.Infinity);
        inner.Children.Add(Art.At(big, (w - big.DesiredSize.Width) / 2 + 6, (h - big.DesiredSize.Height) / 2 + 6));
        return new Border
        {
            Width = w, Height = h, CornerRadius = new CornerRadius(7), Background = Brushes.White, BorderBrush = Art.Brush("#3B3B45"),
            BorderThickness = new Thickness(1.2), Child = inner, IsHitTestVisible = false,
            RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            BoxShadow = new BoxShadows(new BoxShadow { OffsetX = 1, OffsetY = 3, Blur = 6, Color = Color.FromArgb(90, 0, 0, 0) }),
        };
    }

    public static Border CardBack(double w, double h) => new()
    {
        Width = w, Height = h, CornerRadius = new CornerRadius(6), BorderBrush = Brushes.White, BorderThickness = new Thickness(2),
        IsHitTestVisible = false,
        Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromRgb(40, 70, 160), 0), new GradientStop(Color.FromRgb(20, 36, 96), 1) },
        },
        BoxShadow = new BoxShadows(new BoxShadow { OffsetX = 1, OffsetY = 2, Blur = 4, Color = Color.FromArgb(80, 0, 0, 0) }),
    };

    // ------------------------------------------------------------------ demo

    public override void DemoTick()
    {
        _demo = true;
        if (_mode == Mode.Idle) StartSolo(2);
        else if (_rules is { Over: true } && _mode != Mode.Guest) NewDeal();
    }

    void GuestDemo(DurakView v)
    {
        // no rules object on a guest: play the simplest legal thing
        if (v.Seat == v.Defender && !v.Taking && v.Unbeaten > 0)
        {
            int index = v.Table.FindIndex(p => p[1] < 0);
            int card = v.Hand.Where(c => v.Beats(c, v.Table[index][0])).DefaultIfEmpty(-1).First();
            if (card >= 0) SendAction("defend", card, index);
            else SendAction("take", -1, -1);
        }
        else if (v.Seat != v.Defender && !v.Out[v.Seat])
        {
            int card = v.Hand.FirstOrDefault(c => Playable(v, c), -1);
            if (card >= 0 && (v.Table.Count == 0 || Rng.NextDouble() < 0.5)) SendAction("attack", card, -1);
            else if (v.Table.Count > 0 && !v.Done[v.Seat]) SendAction("pass", -1, -1);
        }
    }
}
