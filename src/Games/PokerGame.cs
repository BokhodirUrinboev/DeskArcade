using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using DeskArcade.Engine;
using static DeskArcade.Games.PokerHand;

namespace DeskArcade.Games;

/// <summary>What one player sees of a poker table: every stack and bet, their own two cards, and others' only when shown.</summary>
public sealed class PokerView : CardRoomView
{
    public int Hand { get; set; }
    public int HandLimit { get; set; }
    public int HandsToNextLevel { get; set; }
    public int SmallBlind { get; set; }
    public int BigBlind { get; set; }
    public int NextBigBlind { get; set; }
    public int Dealer { get; set; }
    public int Turn { get; set; } = -1;
    public int Street { get; set; }
    public bool HandOver { get; set; }
    public bool Showdown { get; set; }
    public bool RunningOut { get; set; }
    public int[] Stacks { get; set; } = Array.Empty<int>();
    public int[] Bets { get; set; } = Array.Empty<int>();
    public bool[] Folded { get; set; } = Array.Empty<bool>();
    public bool[] AllIn { get; set; } = Array.Empty<bool>();
    public bool[] Out { get; set; } = Array.Empty<bool>();
    public string[] Acts { get; set; } = Array.Empty<string>();
    public int[] Places { get; set; } = Array.Empty<int>();
    /// <summary>Each seat's two cards as this player may see them: their own, and others' once shown (else empty).</summary>
    public int[][] Cards { get; set; } = Array.Empty<int[]>();
    /// <summary>Whether each seat was dealt in this hand.</summary>
    public bool[] Dealt { get; set; } = Array.Empty<bool>();
    public int[] ShowOrder { get; set; } = Array.Empty<int>();
    public List<int> Board { get; set; } = new();
    /// <summary>The chips gathered in the middle from the streets before this one.</summary>
    public int Pot { get; set; }
    public int CurrentBet { get; set; }
    public int ToCall { get; set; }
    public int MinRaiseTo { get; set; }
    public int MaxRaiseTo { get; set; }
    public bool CanRaise { get; set; }
    public List<PokerPot> Results { get; set; } = new();

    public bool MyTurn => !Over && !HandOver && Turn == Seat;
    /// <summary>Everything in the middle, this street's bets included.</summary>
    public int PotTotal => Pot + Bets.Sum();

    public static PokerView Of(PokerRules r, int seat) => new()
    {
        Hand = r.HandNumber, HandLimit = r.HandLimit, HandsToNextLevel = r.HandsToNextLevel, SmallBlind = r.SmallBlind, BigBlind = r.BigBlind,
        NextBigBlind = PokerRules.BigBlindAt(r.Level + 1), Dealer = r.Dealer, Turn = r.Turn, Street = (int)r.Phase, HandOver = r.HandOver,
        Showdown = r.Showdown, RunningOut = r.RunningOut, Stacks = r.Stacks.ToArray(), Bets = r.Bets.ToArray(), Folded = r.Folded.ToArray(),
        AllIn = r.AllIn.ToArray(), Out = r.Out.ToArray(), Acts = r.Acts.ToArray(), Places = r.Places.ToArray(), Over = r.Over,
        Cards = Enumerable.Range(0, r.Players).Select(s => s == seat || r.Shown[s] || r.Revealed && !r.Folded[s] ? r.Hole[s].ToArray() : Array.Empty<int>()).ToArray(),
        Dealt = r.Hole.Select(h => h.Length > 0).ToArray(), ShowOrder = r.ShowOrder.ToArray(), Board = r.Board.ToList(),
        Pot = r.HandOver ? 0 : r.Committed.Sum() - r.Bets.Sum(), CurrentBet = r.CurrentBet, ToCall = r.Owed(seat),
        MinRaiseTo = r.MinRaiseTo(seat), MaxRaiseTo = r.MaxRaiseTo(seat), CanRaise = r.CanRaise(seat),
        Results = r.Results.Select(p => new PokerPot { Amount = p.Amount, Eligible = p.Eligible, Winners = p.Winners, Value = p.Value, Returned = p.Returned }).ToList(),
    };
}

/// <summary>
/// Poker (see <see cref="PokerRules"/>): Texas hold'em for chips, never money, for 2 to 6 players, against computer
/// players at four levels (<see cref="PokerAi"/>) or with co-workers in a room (<see cref="CardRoomGame{TView}"/>).
/// A game goes on until one player has every chip, or for 20 hands over lunch. Fold, check or call with a click; raise
/// with the slider, ½ pot, pot or all in, and − and + to fine-tune. A hint (on by default, a chip on the table) names
/// your hand and your chance against the players still in. Cards are dealt from the deck to every seat, bets slide
/// to the middle at the end of each street and the pot to its winner, and the winning five light up.
/// </summary>
public sealed class PokerGame : CardRoomGame<PokerView>
{
    const double CardW = 56, CardH = 80, SmallScale = 0.62, TwoPi = Math.PI * 2;
    static readonly IBrush GoldBrush = Art.Brush(Gold), Edge = Art.Brush("#3B3B45");
    const string HintSetting = "poker.hint", LengthSetting = "poker.hands";
    /// <summary>The lunch-break game's length.</summary>
    public const int LunchHands = 20;

    PokerRules? _rules;
    int _raiseTo, _raiseFor = -1;
    string _hintKey = "", _hintText = "";
    (int Game, int Hand) _countedHand, _paidHand;
    int _finishedGame = -1;
    string? _share;
    readonly int[] _betGen = new int[PokerRules.MaxPlayers];
    readonly int[] _betShown = new int[PokerRules.MaxPlayers];
    int _potGen, _potShown = -1;
    Rect _track;

    public PokerGame(IGameHost host) : base(host) { }

    public override string Id => "poker";
    public override string Title => "Poker";
    public override int MinPlayers => PokerRules.MinPlayers;
    public override int MaxPlayers => PokerRules.MaxPlayers;
    protected override string Tag => "pk";
    protected override int DemoCpus => 4;
    protected override Size TableSize => new(980, 600);
    protected override string Tagline => L.T("Texas hold'em for chips · the best five cards win the pot");
    protected override string PanelExtras => $"{HandLimit}";

    protected override IEnumerable<(string Text, int Cpus)> SoloChoices => new[]
    {
        (L.T("Heads-up"), 1), (L.F("{0} players", 3), 2), (L.F("{0} players", 4), 3), (L.F("{0} players", 6), 5),
    };

    bool HintOn => !Host.Settings.Levels.TryGetValue(HintSetting, out int h) || h != 0;
    int HandLimit => Host.Settings.Levels.TryGetValue(LengthSetting, out int n) && n > 0 ? n : 0;

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        s.Rotor.Children.Add(Art.At(new Rectangle { Width = 11, Height = 15, RadiusX = 2, RadiusY = 2, Fill = Brushes.White, Stroke = Art.Brush("#555"), StrokeThickness = 1, RenderTransform = new RotateTransform(-12) }, -10, -9));
        s.Rotor.Children.Add(Art.At(new Rectangle { Width = 11, Height = 15, RadiusX = 2, RadiusY = 2, Fill = Brushes.White, Stroke = Art.Brush("#555"), StrokeThickness = 1, RenderTransform = new RotateTransform(10) }, -3, -9));
        s.Rotor.Children.Add(Art.At(new TextBlock { Text = "A", FontSize = 8, FontWeight = FontWeight.Black, Foreground = Art.Brush("#1A1A22") }, -1, -8));
        s.Rotor.Children.Add(Art.Circle(5, 5, 5.5, Art.Brush("#D23C3C"), Brushes.White, 1.2));
        s.Rotor.Children.Add(Art.Circle(5, 5, 2.5, null, Brushes.White, 0.8));
        return s;
    }

    public override HudInfo Hud => new(
        View == null ? "—" : View.Stacks[View.Seat].ToString("N0", CultureInfo.InvariantCulture),
        Status(),
        L.F("Wins {0}", Host.Stats.Get("poker.wins")));

    public override string? ShareText => _share;

    // ------------------------------------------------------------------ the rules

    protected override bool HasRules => _rules != null;
    protected override int RulesVersion => _rules?.Version ?? -1;
    protected override void DropRules() => _rules = null;

    protected override void NewRules(int players)
    {
        _rules = new PokerRules(players, Rng, HandLimit, firstDealer: Rng.Next(players));
        _raiseFor = -1;
    }

    protected override bool RulesAct(int seat, RoomMove move) => _rules != null && _rules.Act(seat, move.Kind, move.A);

    protected override PokerView ViewOf(int seat) => PokerView.Of(_rules!, seat);

    /// <summary>The solo player is out and only watching: the computers hurry.</summary>
    bool Watching => Solo && _rules is { } r && r.Out[0];

    protected override TableDue? NextDue()
    {
        if (_rules is not { Over: false } r) return null;
        double hurry = Watching ? 0.3 : 1;
        if (r.HandOver) return new TableDue(-1, (r.Showdown ? 3.2 + 0.45 * r.ShowOrder.Count(s => r.Shown[s]) : 2.2) * hurry);
        if (r.RunningOut) return new TableDue(-1, 1.3 * hurry);
        if (r.Turn >= 0 && IsComputer(r.Turn)) return new TableDue(r.Turn, (0.75 + r.Version % 5 * 0.12) * hurry);
        return null;
    }

    protected override bool AutoStep() => _rules != null && (_rules.HandOver ? _rules.NextHand() : _rules.Step());

    protected override Task<RoomMove?> Think(int seat)
    {
        var spot = PokerAi.SpotFor(_rules!, seat);
        int level = CpuLevel;
        if (level < 4) return Task.FromResult<RoomMove?>(ToMove(PokerAi.Decide(spot, level, Rng)));
        var rng = new Random(Rng.Next()); // the shared generator stays on this thread
        return Task.Run(() => (RoomMove?)ToMove(PokerAi.Decide(spot, 4, rng)));
    }

    static RoomMove ToMove(PokerMove m) => new(m.Kind, m.Amount);

    protected override RoomMove? GuestDemoMove(PokerView v)
    {
        if (!v.MyTurn) return null;
        if (v.ToCall == 0) return v.CanRaise && Rng.NextDouble() < 0.2 ? new RoomMove("raise", v.MinRaiseTo) : new RoomMove("check");
        return v.ToCall <= v.BigBlind * 4 || Rng.NextDouble() < 0.4 ? new RoomMove("call") : new RoomMove("fold");
    }

    protected override int TurnOf(PokerView v) => v.Turn;
    protected override bool MyMove(PokerView v) => v.MyTurn;

    protected override void Moved(int seat, RoomMove move)
    {
        switch (move.Kind)
        {
            case "fold": Host.Sound.Play("whoosh", 0.3, 1.2); break;
            case "check": Host.Sound.Play("board", 0.35, 1.6); break;
            default: Host.Sound.Play("click", 0.5, move.Kind == "call" ? 1.3 : 1.0); break;
        }
    }

    // ------------------------------------------------------------------ status and results

    string Status()
    {
        if (RoomStatus() is { } room) return room;
        if (View is not { } v) return L.T("Pick a table to start a game");
        int me = v.Seat;
        if (v.Over) return v.Places[me] == 1 ? L.T("You won the table · click New game") : L.F("{0} won the table · click New game", Name(Array.IndexOf(v.Places, 1)));
        if (v.Out[me] && !v.Dealt[me]) return L.F("You're out in place {0} · the others play on", v.Places[me]);
        if (v.HandOver) return ResultLine(v);
        if (v.RunningOut) return L.T("All in · the board runs out");
        if (v.MyTurn) return v.ToCall > 0 ? L.F("Your turn · {0} to call", v.ToCall) : L.T("Your turn · check or bet");
        if (v.Folded[me]) return L.T("You folded · watching the hand");
        return v.Turn >= 0 ? L.F("{0}'s turn", Name(v.Turn)) : "";
    }

    /// <summary>Who took the main pot: "Anna wins 240 with Pair of kings", or a split.</summary>
    string ResultLine(PokerView v)
    {
        var pot = v.Results.FirstOrDefault(p => !p.Returned);
        if (pot == null) return "";
        string who = pot.Winners.Length == 1 ? WinnerName(v, pot.Winners[0]) : string.Join(", ", pot.Winners.Select(w => WinnerName(v, w)));
        if (pot.Winners.Length > 1) return L.F("{0} split {1} with {2}", who, pot.Amount, Describe(pot.Value));
        if (pot.Value == 0) return pot.Winners[0] == v.Seat ? L.F("You win {0}", pot.Amount) : L.F("{0} wins {1}", who, pot.Amount);
        return pot.Winners[0] == v.Seat ? L.F("You win {0} with {1}", pot.Amount, Describe(pot.Value)) : L.F("{0} wins {1} with {2}", who, pot.Amount, Describe(pot.Value));
    }

    string WinnerName(PokerView v, int seat) => seat == v.Seat ? L.T("You") : Name(seat);

    protected override void Announce(PokerView v)
    {
        int me = v.Seat;
        if (v.Dealt[me] && (v.Game, v.Hand) != _countedHand)
        {
            _countedHand = (v.Game, v.Hand);
            Host.Stats.Add("poker.hands");
        }
        if (v.HandOver && (v.Game, v.Hand) != _paidHand)
        {
            _paidHand = (v.Game, v.Hand);
            var mine = v.Results.Where(p => !p.Returned && p.Winners.Contains(me)).ToList();
            if (mine.Count > 0)
            {
                Host.Stats.Add("poker.pots");
                if (v.AllIn.Where((a, s) => a && !v.Folded[s]).Any() && v.Showdown) Host.Stats.Add("poker.allinwins");
                Host.Sound.Play("score", 0.5);
            }
        }
        // a co-worker who went out early hears how the others did once the table is over
        if (v.Over && _rivalsPending == v.Game)
        {
            _rivalsPending = -1;
            RecordRivals(v, s => -v.Places[s]);
        }
        if (!(v.Over || v.Out[me]) || _finishedGame == v.Game) return;
        _finishedGame = v.Game;
        int place = v.Places[me], players = v.Players;
        var at = new Vec2(Area.Center.X, Area.Top + Area.Height * 0.3);
        Host.Stats.Add("poker.games");
        if (place == 1)
        {
            Host.Stats.Add("poker.wins");
            if (!Solo) Host.Stats.Add("lan.wins");
            Host.Fx.Popup(at, L.T("YOU WIN THE TABLE!"), Gold, 42, 2.8, L.F("every chip after {0} hands", v.Hand));
            Host.Fx.Burst(at, Themes.Current.Confetti, 50, 540, 700, 7, 1.1);
            Host.Sound.Play("best", 0.8);
            Cards.Wave();
            _share = v.HandLimit > 0
                ? L.F("Poker · won a {0}-player lunch-break table with {1} chips", players, v.Stacks[me])
                : L.F("Poker · won a {0}-player table in {1} hands", players, v.Hand);
        }
        else
        {
            Host.Fx.Popup(at, L.F("PLACE {0} OF {1}", place, players), Colors.White, 36, 2.6, L.T("better luck next deal"));
            Host.Sound.Play("buzzer", 0.35);
            _share = L.F("Poker · place {0} of {1} after {2} hands", place, players, v.Hand);
        }
        if (v.Over) RecordRivals(v, s => -v.Places[s]);
        else _rivalsPending = v.Game; // out early: the rivalries are settled when the table is
    }

    int _rivalsPending = -1;

    // ------------------------------------------------------------------ places on the table (table coordinates)

    Vec2 Middle => new(Table.Width / 2, Table.Height / 2 - 16);
    double BoardY => Middle.Y - 4;
    double MyCardsY => Table.Height - CardH / 2 - 28;
    Vec2 PotPos => new(Middle.X, BoardY - CardH / 2 - 26);
    Vec2 DeckPos => new(Middle.X - 3.3 * (CardW + 8), BoardY);
    Vec2 BoardPos(int i) => new(Middle.X + (i - 2) * (CardW + 8), BoardY);

    /// <summary>A seat's name plate: me at the bottom, the others round an ellipse in seat order (the next seat to my left).</summary>
    Vec2 PlatePos(PokerView v, int seat)
    {
        if (seat == v.Seat) return new Vec2(Middle.X - 190, Table.Height - 50);
        double angle = TwoPi * ((seat - v.Seat + v.Players) % v.Players) / v.Players;
        double rx = Table.Width / 2 - 96, ry = Table.Height / 2 - 70;
        return new Vec2(Middle.X - rx * Math.Sin(angle), Middle.Y + 6 + ry * Math.Cos(angle));
    }

    /// <summary>Where a seat's two cards lie: my own big at the bottom, the others' small, between their plate and the middle.</summary>
    Vec2 CardPos(PokerView v, int seat, int k)
    {
        if (seat == v.Seat) return new Vec2(Middle.X - 31 + k * 62, MyCardsY);
        var p = PlatePos(v, seat);
        var toward = Middle - p;
        double len = Math.Max(1, toward.Length);
        var dir = toward * (1 / len);
        // clear of the plate, which is wider than it is tall
        return p + dir * (58 + 40 * Math.Abs(dir.X)) + new Vec2((k - 0.5) * 22, (k - 0.5) * 4);
    }

    Vec2 BetPos(PokerView v, int seat)
    {
        if (seat == v.Seat) return new Vec2(Middle.X, MyCardsY - CardH / 2 - 30);
        var from = PlatePos(v, seat);
        return from + (Middle - from) * 0.5;
    }

    static int BackKey(int seat, int k) => -1 - (seat * 2 + k);
    int BetKey(int seat) => 1000 + seat * 100 + _betGen[seat] % 100;
    int PotKey => 2000 + _potGen % 500;
    const int ButtonKey = 4000;

    // ------------------------------------------------------------------ drawing

    protected override void DrawTable(PokerView v, PokerView? prev, bool newDeal)
    {
        bool newHand = newDeal || prev == null || prev.Hand != v.Hand;
        _buttonFrom = null;
        if (newHand && !newDeal)
        {
            _buttonFrom = Cards.Get(ButtonKey)?.Pos;
            Cards.Clear(); // the last hand's cards and chips go at once; the new ones are dealt
            Cards.Begin();
        }
        if (newHand)
        {
            Array.Clear(_betShown);
            _potShown = -1;
        }
        DrawDeck(v);
        DrawSeats(v, prev, newHand);
        DrawBoard(v, prev, newHand);
        DrawChips(v, prev, newHand);
        DrawWinners(v);
        DrawControls(v);
    }

    void DrawDeck(PokerView v)
    {
        var d = DeckPos;
        for (int i = 2; i >= 0; i--)
            Place(DurakGame.CardBack(CardW, CardH), d.X - CardW / 2 - i * 1.5, d.Y - CardH / 2 - i * 1.5);
        for (int i = 0; i < 5; i++)
        {
            var b = BoardPos(i);
            Felt.Children.Add(Art.At(new Rectangle
            {
                Width = CardW, Height = CardH, RadiusX = 7, RadiusY = 7, Stroke = Art.Brush(Color.FromArgb(60, 255, 255, 255)), StrokeThickness = 1.5,
                StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 3 }, IsHitTestVisible = false,
            }, b.X - CardW / 2, b.Y - CardH / 2));
        }
    }

    void DrawSeats(PokerView v, PokerView? prev, bool newHand)
    {
        int n = v.Players;
        // the deal: two cards to each seat in turn, from the left of the button
        var dealOrder = Enumerable.Range(1, n).Select(i => (v.Dealer + i) % n).Where(s => v.Dealt[s]).ToList();
        for (int s = 0; s < n; s++)
        {
            DrawPlate(v, s);
            if (!v.Dealt[s] || v.Folded[s] && s != v.Seat && !(v.HandOver && v.Cards[s].Length > 0)) continue;
            bool mine = s == v.Seat;
            if (mine && v.Folded[s]) continue; // mine went to the muck
            int order = Math.Max(0, dealOrder.IndexOf(s));
            for (int k = 0; k < 2; k++)
            {
                var at = CardPos(v, s, k);
                double angle = mine ? (k - 0.5) * 6 : (k - 0.5) * 10;
                double scale = mine ? 1 : SmallScale;
                double delay = newHand ? CardTable.DealDelay(order, k, dealOrder.Count) : 0;
                if (v.Cards[s].Length == 2)
                {
                    int card = v.Cards[s][k];
                    int shownAt = Array.IndexOf(v.ShowOrder, s);
                    bool flip = !mine && !Cards.Has(card);
                    if (Cards.Has(card)) Cards.Place(card, () => Face(card), at, angle, scale);
                    else if (flip && !newHand)
                        Cards.Place(card, () => Face(card), at, angle, scale, from: at, fromScale: scale * 0.1, seconds: 0.25, ease: Ease.OutBack,
                            delay: Stagger(v.HandOver && !v.RunningOut && v.Showdown ? 0.3 + Math.Max(0, shownAt) * 0.45 : 0));
                    else
                        Cards.Place(card, () => Face(card), at, angle, scale, from: DeckPos, fromScale: SmallScale, seconds: CardTable.DealSeconds, ease: Ease.OutCubic, delay: Stagger(delay));
                }
                else
                {
                    int key = BackKey(s, k);
                    if (Cards.Has(key)) Cards.Place(key, () => DurakGame.CardBack(CardW, CardH), at, angle, scale);
                    else Cards.Place(key, () => DurakGame.CardBack(CardW, CardH), at, angle, scale, from: DeckPos, fromScale: SmallScale,
                        seconds: CardTable.DealSeconds, ease: Ease.OutCubic, delay: Stagger(delay));
                }
            }
        }
        // the button slides round from one hand to the next
        var plate = PlatePos(v, v.Dealer);
        var buttonAt = v.Dealer == v.Seat ? plate + new Vec2(84, -6) : plate + new Vec2(70, -24);
        Cards.Place(ButtonKey, DealerButton, buttonAt, from: _buttonFrom, seconds: 0.45, ease: Ease.InOutCubic);
    }

    Vec2? _buttonFrom;

    /// <summary>How long a showdown takes to turn its hands up, before the pot moves.</summary>
    static double RevealTime(PokerView v) => v.Showdown ? 0.6 + v.ShowOrder.Count(s => v.Cards[s].Length > 0) * 0.45 : 0.4;

    Border Face(int card) => DurakGame.CardFace(Suit(card), Rank(card), CardW, CardH);

    void DrawPlate(PokerView v, int s)
    {
        var p = PlatePos(v, s);
        bool turn = v.Turn == s && !v.HandOver && !v.Over;
        bool gone = v.Out[s] && !v.Dealt[s];
        double w = 136, h = 48;
        var ink = Themes.Current.Ink;
        var plate = new Border
        {
            Width = w, Height = h, CornerRadius = new CornerRadius(12), IsHitTestVisible = false,
            Background = Art.Brush(Color.FromArgb(gone || v.Folded[s] && !v.HandOver ? (byte)120 : (byte)210, ink.R, ink.G, ink.B)),
            BorderBrush = turn ? GoldBrush : Art.Brush(Color.FromArgb(90, 255, 255, 255)), BorderThickness = new Thickness(turn ? 2.5 : 1),
        };
        Place(plate, p.X - w / 2, p.Y - h / 2);
        string name = s == v.Seat ? L.T("You") : v.Names[s] + (v.Cpu[s] && v.Human[s] && !Solo ? " · " + L.T("CPU") : "");
        Label(Clip(name, 17), p.X, p.Y - h / 2 + 5, 14, gone ? Color.FromRgb(150, 150, 150) : Colors.White, center: true);
        string chips = gone ? L.F("out · place {0}", v.Places[s]) : L.F("{0} chips", v.Stacks[s].ToString("N0", CultureInfo.InvariantCulture));
        Label(chips, p.X, p.Y - h / 2 + 25, 13, gone ? Color.FromRgb(150, 150, 150) : Gold, center: true);
        string act = ActText(v, s);
        if (act.Length > 0)
        {
            var tag = new Border
            {
                CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 1), IsHitTestVisible = false,
                Background = Art.Brush(v.Acts[s] is "fold" ? Color.FromArgb(200, 90, 90, 100) : v.Acts[s] is "raise" or "bet" or "allin" ? Color.FromArgb(230, 200, 70, 50) : Color.FromArgb(220, 40, 120, 80)),
                Child = new TextBlock { Text = act, FontFamily = Fx.Font, FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Brushes.White },
            };
            tag.Measure(Size.Infinity);
            Place(tag, p.X - tag.DesiredSize.Width / 2, p.Y + h / 2 - 4, Top);
        }
        if (turn && s != v.Seat) Label(L.T("thinking…"), p.X, p.Y - h / 2 - 18, 12, Gold, center: true, into: Top);
    }

    static string Clip(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    string ActText(PokerView v, int s)
    {
        if (v.HandOver || v.Out[s] && !v.Dealt[s]) return "";
        if (v.Folded[s]) return L.T("Fold");
        return v.Acts[s] switch
        {
            "check" => L.T("Check"),
            "call" => L.F("Call {0}", v.Bets[s]),
            "bet" => L.F("Bet {0}", v.Bets[s]),
            "raise" => L.F("Raise to {0}", v.Bets[s]),
            _ => v.AllIn[s] ? L.T("All in") : "",
        };
    }

    void DrawBoard(PokerView v, PokerView? prev, bool newHand)
    {
        int fresh = 0;
        for (int i = 0; i < v.Board.Count; i++)
        {
            int card = v.Board[i];
            if (Cards.Has(card)) Cards.Place(card, () => Face(card), BoardPos(i));
            else Cards.Place(card, () => Face(card), BoardPos(i), from: DeckPos, seconds: 0.3, ease: Ease.OutCubic, delay: Stagger(fresh++ * 0.12 + (newHand ? 0.4 : 0)));
        }
    }

    void DrawChips(PokerView v, PokerView? prev, bool newHand)
    {
        // each bet as a stack of chips in front of its seat; a bigger bet comes as a new stack from the seat
        for (int s = 0; s < v.Players; s++)
        {
            int bet = v.Bets[s];
            if (bet <= 0) continue;
            if (bet != _betShown[s])
            {
                if (_betShown[s] > 0) Cards.Remove(BetKey(s), seconds: 0.2); // the smaller stack goes as the bigger comes
                _betGen[s]++;
                _betShown[s] = bet;
                var from = s == v.Seat ? new Vec2(Middle.X, MyCardsY) : PlatePos(v, s);
                int amount = bet;
                Cards.Place(BetKey(s), () => ChipStack(amount), BetPos(v, s), from: from, fromScale: 0.6, seconds: 0.32, ease: Ease.OutCubic);
            }
            else Cards.Place(BetKey(s), () => ChipStack(bet), BetPos(v, s));
        }
        for (int s = 0; s < v.Players; s++)
            if (v.Bets[s] == 0) _betShown[s] = 0;
        // the pot in the middle
        if (v.Pot > 0)
        {
            if (v.Pot != _potShown)
            {
                if (_potShown > 0) Cards.Remove(PotKey, seconds: 0.25, delay: 0.25);
                _potGen++;
                _potShown = v.Pot;
                int pot = v.Pot;
                Cards.Place(PotKey, () => ChipStack(pot, L.F("Pot {0}", pot.ToString("N0", CultureInfo.InvariantCulture))), PotPos, from: PotPos, fromScale: 0.8, seconds: 0.2,
                    ease: Ease.OutBack, delay: Stagger(0.28));
            }
            else Cards.Place(PotKey, () => ChipStack(v.Pot), PotPos);
        }
        // at the end of the hand each pot slides to its winners
        if (!v.HandOver) return;
        int k = 0;
        double wait = RevealTime(v);
        foreach (var pot in v.Results)
        {
            for (int w = 0; w < pot.Winners.Length; w++)
            {
                int seat = pot.Winners[w];
                int share = pot.Amount / pot.Winners.Length + (w < pot.Amount % pot.Winners.Length ? 1 : 0);
                int key = 3000 + k++;
                var to = seat == v.Seat ? new Vec2(Middle.X - 190, Table.Height - 96) : PlatePos(v, seat) + (Middle - PlatePos(v, seat)) * 0.22;
                if (Cards.Has(key)) Cards.Place(key, () => ChipStack(share), to);
                else
                {
                    Cards.Place(key, () => ChipStack(share), to, from: PotPos, seconds: 0.55, ease: Ease.InOutCubic, delay: Stagger(wait));
                    Anims.After(Stagger(wait) + 0.5, () => Host.Sound.Play("click", 0.45, 0.9));
                }
            }
        }
    }

    /// <summary>A little stack of chips (more chips for more) and the amount beside it.</summary>
    static Control ChipStack(int amount, string? label = null)
    {
        var colors = new[] { Color.FromRgb(210, 50, 60), Color.FromRgb(40, 110, 210), Color.FromRgb(40, 150, 80), Color.FromRgb(30, 30, 36), Color.FromRgb(150, 70, 200) };
        int discs = Math.Clamp((int)Math.Log10(Math.Max(1, amount)) + 1, 1, 5);
        var canvas = new Canvas { Width = 26, Height = 18 + discs * 3, IsHitTestVisible = false };
        for (int i = 0; i < discs; i++)
        {
            double y = canvas.Height - 13 - i * 3;
            canvas.Children.Add(Art.At(new Ellipse { Width = 24, Height = 12, Fill = Art.Brush(colors[(amount / 7 + i) % colors.Length]), Stroke = Brushes.White, StrokeThickness = 1.2 }, 1, y));
            canvas.Children.Add(Art.At(new Ellipse { Width = 14, Height = 6, Stroke = Art.Brush(Color.FromArgb(170, 255, 255, 255)), StrokeThickness = 1 }, 6, y + 3));
        }
        var text = new TextBlock
        {
            Text = label ?? amount.ToString("N0", CultureInfo.InvariantCulture), FontFamily = Fx.Font, FontSize = 12, FontWeight = FontWeight.Black,
            Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0),
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { canvas, new Border { Background = Art.Brush(150, 0, 0, 0), CornerRadius = new CornerRadius(7), Padding = new Thickness(3, 1, 5, 1), VerticalAlignment = VerticalAlignment.Center, Child = text } } };
        row.Measure(Size.Infinity);
        return new Border { Width = Math.Ceiling(row.DesiredSize.Width), Height = Math.Ceiling(row.DesiredSize.Height), Child = row, IsHitTestVisible = false };
    }

    static Control DealerButton()
    {
        var c = new Canvas { Width = 22, Height = 22, IsHitTestVisible = false };
        c.Children.Add(Art.Circle(11, 12, 10, Art.Brush(80, 0, 0, 0)));
        c.Children.Add(Art.Circle(11, 11, 10, Brushes.White, Art.Brush("#555"), 1.2));
        var t = new TextBlock { Text = "D", FontFamily = Fx.Font, FontSize = 12, FontWeight = FontWeight.Black, Foreground = Art.Brush("#222") };
        t.Measure(Size.Infinity);
        c.Children.Add(Art.At(t, 11 - t.DesiredSize.Width / 2, 11 - t.DesiredSize.Height / 2));
        return c;
    }

    /// <summary>At a showdown the winning five light up and lift, once their hands are turned; the rest dim.</summary>
    void DrawWinners(PokerView v)
    {
        if (!v.HandOver || !v.Showdown || v.Board.Count < 5) return;
        var pot = v.Results.FirstOrDefault(p => !p.Returned);
        if (pot == null) return;
        var lit = new HashSet<int>();
        foreach (int w in pot.Winners)
            if (v.Cards[w].Length == 2) lit.UnionWith(BestFive(v.Cards[w].Concat(v.Board).ToList()));
        if (lit.Count == 0) return;
        double wait = Stagger(0.5 + v.ShowOrder.Count(s => v.Cards[s].Length > 0) * 0.45);
        foreach (var card in Cards.Shown.Where(c => c.Key is >= 0 and < 52).ToList())
        {
            var face = (Border)card.Visual;
            if (lit.Contains(card.Key))
            {
                Anims.Add(0.35, k =>
                {
                    face.BorderBrush = GoldBrush;
                    face.BorderThickness = new Thickness(3);
                    card.Lift = 12 * k;
                    card.Apply();
                }, Ease.OutBack, delay: wait);
            }
            else Anims.Add(0.3, k => face.Opacity = 1 - 0.4 * k, delay: wait);
        }
    }

    // ------------------------------------------------------------------ controls

    void DrawControls(PokerView v)
    {
        var t = Table;
        Label(Status(), Middle.X, BoardY + CardH / 2 + 14, 15, Gold, center: true, into: Top);
        // the blinds and the hand, bottom left; the hint's chip above them
        string info = L.F("Blinds {0}/{1}", v.SmallBlind, v.BigBlind) + " · " + (v.HandLimit > 0 ? L.F("hand {0} of {1}", v.Hand, v.HandLimit) : L.F("hand {0}", v.Hand));
        if (!v.Over && (v.HandLimit == 0 || v.Hand + v.HandsToNextLevel <= v.HandLimit))
            info += " · " + (v.HandsToNextLevel <= 1 ? L.F("{0} next hand", v.NextBigBlind) : L.F("{0} in {1} hands", v.NextBigBlind, v.HandsToNextLevel));
        Label(info, 26, t.Bottom - 26, 12, Soft);
        Button(HintOn ? L.T("Hint: on") : L.T("Hint: off"), 72, t.Bottom - 60, 100, () =>
        {
            Host.Settings.Levels[HintSetting] = HintOn ? 0 : 1;
            Host.SaveSettings();
            Host.Sound.Play("click", 0.3, 1.4);
        }, height: 26, font: 12);
        if (HintOn && Hint(v) is { Length: > 0 } hint) Label(hint, Middle.X, BoardY + CardH / 2 + 38, 14, Colors.White, center: true, into: Top);

        if (v.Over || v.Out[v.Seat] && !v.Dealt[v.Seat])
        {
            DrawEndButtons(t.Bottom - 150);
            return;
        }
        if (!v.MyTurn || Sending) return;
        if (_raiseFor != v.Version)
        {
            _raiseFor = v.Version;
            _raiseTo = v.MinRaiseTo;
        }
        _raiseTo = Math.Clamp(_raiseTo, v.MinRaiseTo, v.MaxRaiseTo);
        double right = t.Right - 20, width = 330, left = right - width, x0 = left + width / 6;
        double bottom = t.Bottom - 18;
        // fold · check or call · raise
        Button(L.T("Fold"), x0, bottom - 44, 102, () => Act(new RoomMove("fold")), height: 42);
        if (v.ToCall == 0) Button(L.T("Check"), x0 + 110, bottom - 44, 102, () => Act(new RoomMove("check")), height: 42);
        else
        {
            int call = Math.Min(v.ToCall, v.Stacks[v.Seat]);
            Button(call >= v.Stacks[v.Seat] ? L.F("All in {0}", call) : L.F("Call {0}", call), x0 + 110, bottom - 44, 102, () => Act(new RoomMove("call")), height: 42, font: 14);
        }
        if (!v.CanRaise) return;
        bool all = _raiseTo >= v.MaxRaiseTo;
        string raise = all ? L.F("All in {0}", v.MaxRaiseTo) : v.CurrentBet == 0 ? L.F("Bet {0}", _raiseTo) : L.F("Raise to {0}", _raiseTo);
        Button(raise, x0 + 220, bottom - 44, 102, () => Act(all ? new RoomMove("allin") : new RoomMove("raise", _raiseTo)), hot: true, height: 42, font: 13);
        // the amount: − slider +
        double sy = bottom - 78;
        Button("−", left + 14, sy - 12, 28, () => Nudge(v, -1), height: 24, font: 16);
        Button("+", right - 14, sy - 12, 28, () => Nudge(v, +1), height: 24, font: 16);
        _track = new Rect(left + 36, sy - 10, width - 72, 20);
        DrawSlider(v);
        // quick amounts
        int potSize = v.PotTotal + v.ToCall;
        var quick = new (string Text, int To)[]
        {
            (L.T("½ pot"), v.CurrentBet + potSize / 2), (L.T("Pot"), v.CurrentBet + potSize), (L.T("All in"), v.MaxRaiseTo),
        };
        for (int i = 0; i < quick.Length; i++)
        {
            int to = Math.Clamp(quick[i].To, v.MinRaiseTo, v.MaxRaiseTo);
            Button(quick[i].Text, x0 + i * 110, sy - 46, 96, () => _raiseTo = to, height: 26, font: 12);
        }
    }

    void Nudge(PokerView v, int dir)
    {
        int step = Math.Max(1, v.BigBlind);
        int to = _raiseTo + dir * step;
        if (dir < 0 && _raiseTo >= v.MaxRaiseTo) to = (v.MaxRaiseTo - 1) / step * step; // from all in, back to a round amount
        _raiseTo = Math.Clamp(to, v.MinRaiseTo, v.MaxRaiseTo);
        Host.Sound.Play("click", 0.2, 1.6);
    }

    void DrawSlider(PokerView v)
    {
        var r = _track;
        Felt.Children.Add(Art.At(new Rectangle { Width = r.Width, Height = 6, RadiusX = 3, RadiusY = 3, Fill = Art.Brush(140, 0, 0, 0), IsHitTestVisible = false }, r.X, r.Center.Y - 3));
        double k = v.MaxRaiseTo > v.MinRaiseTo ? (double)(_raiseTo - v.MinRaiseTo) / (v.MaxRaiseTo - v.MinRaiseTo) : 1;
        Felt.Children.Add(Art.At(new Rectangle { Width = r.Width * k, Height = 6, RadiusX = 3, RadiusY = 3, Fill = Art.Brush(Themes.Current.Accent), IsHitTestVisible = false }, r.X, r.Center.Y - 3));
        Top.Children.Add(Art.Circle(r.X + r.Width * k, r.Center.Y, 9, GoldBrush, Brushes.White, 2));
    }

    protected override bool PressAt(Point p)
    {
        if (View is not { MyTurn: true, CanRaise: true } || Sending || !_track.Inflate(new Thickness(6, 8)).Contains(p)) return false;
        DragTo(p);
        return true;
    }

    protected override void DragTo(Point p)
    {
        if (View is not { MyTurn: true } v || _track.Width <= 0) return;
        double k = Math.Clamp((p.X - _track.X) / _track.Width, 0, 1);
        int step = Math.Max(1, v.BigBlind);
        int to = k >= 0.995 ? v.MaxRaiseTo : v.MinRaiseTo + (int)Math.Round(k * (v.MaxRaiseTo - v.MinRaiseTo) / step) * step;
        to = Math.Clamp(to, v.MinRaiseTo, v.MaxRaiseTo);
        if (to == _raiseTo) return;
        _raiseTo = to;
        Draw();
    }

    /// <summary>"Pair of kings · about 62 % to win against 2 players": the hand so far and its chance against random hands.</summary>
    string Hint(PokerView v)
    {
        int me = v.Seat;
        if (v.Cards[me].Length != 2 || v.Folded[me] || v.HandOver || v.Over) return "";
        int opponents = Enumerable.Range(0, v.Players).Count(s => s != me && v.Dealt[s] && !v.Folded[s]);
        string key = $"{v.Game}|{v.Hand}|{v.Board.Count}|{opponents}";
        if (key == _hintKey) return _hintText;
        _hintKey = key;
        var hole = v.Cards[me];
        string name = v.Board.Count == 0 ? DescribeHole(hole[0], hole[1]) : Describe(Evaluate(hole.Concat(v.Board).ToArray()));
        if (opponents == 0) return _hintText = name;
        // a steady number for the same cards: the deals come from a generator seeded by them
        var rng = new Random(hole[0] * 53 + hole[1] + v.Board.Sum() * 7 + opponents);
        int pct = (int)Math.Round(Equity(hole, v.Board, opponents, rng, 900) * 100);
        _hintText = opponents == 1 ? L.F("{0} · about {1} % to win against 1 player", name, pct) : L.F("{0} · about {1} % to win against {2} players", name, pct, opponents);
        return _hintText;
    }

    protected override void DrawStartExtras(double y)
    {
        if (Mode == RoomMode.Guest) return;
        string text = HandLimit > 0 ? L.F("Game: lunch break, {0} hands", HandLimit) : L.T("Game: until one player has every chip");
        Button(text, Table.Center.X, y, 400, () =>
        {
            Host.Settings.Levels[LengthSetting] = HandLimit > 0 ? 0 : LunchHands;
            Host.SaveSettings();
            Host.Sound.Play("click", 0.3, 1.3);
            Changed();
        });
    }

    /// <summary>A card no longer placed: a folded hand slides to the middle, shown hands' backs wait for their turn to show.</summary>
    protected override void Leave(TableCards.Card card, PokerView v, PokerView? prev)
    {
        int key = card.Key;
        if (key is >= 1000 and < 2000 && prev != null)
        {
            Cards.Remove(key, PotPos, 0.7, 0.35, Ease.InCubic); // the street's bets go to the pot
            return;
        }
        if (key is >= 2000 and < 3000 && v.HandOver)
        {
            Cards.Remove(key, seconds: 0.2, delay: Stagger(RevealTime(v))); // the pot stays until it is paid out
            return;
        }
        if (key < 0 && prev != null)
        {
            int seat = (-key - 1) / 2;
            if (seat < v.Players && v.Folded[seat] && !prev.Folded[seat])
            {
                Cards.Remove(key, Middle, 0.3, 0.3, Ease.InCubic);
                return;
            }
            int shownAt = Array.IndexOf(v.ShowOrder, seat);
            double delay = v.HandOver && v.Showdown && !prev.RunningOut && shownAt >= 0 ? Stagger(0.3 + shownAt * 0.45) : 0;
            Cards.Remove(key, seconds: 0.12, delay: delay);
            return;
        }
        if (key is >= 0 and < 52 && prev != null && v.Folded[v.Seat] && prev.Cards[v.Seat].Contains(key))
        {
            Cards.Remove(key, Middle, 0.4, 0.3, Ease.InCubic);
            return;
        }
        base.Leave(card, v, prev);
    }
}
