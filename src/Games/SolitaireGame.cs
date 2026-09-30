using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using DeskArcade.Engine;
using static DeskArcade.Games.SolitaireRules;

namespace DeskArcade.Games;

/// <summary>
/// Solitaire on a felt laid over the desktop, in the game the chips under the felt pick: Klondike, draw one
/// (<see cref="SolitaireRules"/>), FreeCell with Microsoft's numbered deals (<see cref="FreeCellRules"/>; the deal chip
/// opens a number pad to play a deal someone shared), or Spider in one, two or four suits (<see cref="SpiderRules"/>).
/// Click the stock to turn a card (Klondike) or deal a row (Spider). Click a card to send it where it fits (home to its
/// foundation first), or drag it, or a run, onto the pile you want. Undo takes a move back; New deal, and switching games
/// mid-deal, ask twice. Once nothing is left to work out, the rest go home by themselves; in FreeCell a card goes home as
/// soon as nothing on the table could still need it. Your best is the fewest moves, per game. The grip above the felt (or
/// a right-drag) moves the whole layout. A deal is a round: over the LAN, or against the computer, the bigger share sent
/// home wins (out of 52, Spider's cards counting half), counted when the deal is solved or given up for a new one; two
/// FreeCell players over the LAN get the same numbered deals. Only the cards, the empty piles, the buttons, the chips and
/// the grip take the mouse.
/// </summary>
public sealed class SolitaireGame : MiniGame
{
    const double CardW = 64, CardH = 88, Gap = 10, Pad = 12, RowGap = 16, FanHidden = 9, FanUp = 30;
    const double TableauTop = CardH + RowGap;
    // tall enough for a long pile at a tighter fan; a longer one squeezes its fan to fit
    const double BoardH = TableauTop + 6 * FanHidden + 12 * 22 + CardH;
    const double PanelH = BoardH + Pad * 2;
    /// <summary>Klondike's felt: the cards keep Klondike's size in every game, so a wider table is wider on screen.</summary>
    const double KlondikePanelW = Piles * CardW + (Piles - 1) * Gap + Pad * 2;
    const double ButtonW = CardW + Gap, ButtonH = 26, DragStart = 5, FinishStep = 0.1, ConfirmTime = 2.5;
    const double ChipH = 24, ChipGap = 6, ChipRow = ChipGap + ChipH, KeyW = 46, KeyH = 32, KeyGap = 6, StockStep = 3;
    public const double GlideTime = 0.22, DealStagger = 0.04, CascadeSeconds = 2.2, CascadeStagger = 0.08;
    const double WideDealStagger = 0.025, RowDealStagger = 0.05, FlySeconds = 0.35;
    const int CascadeCards = 16;
    const string KindKey = "solitaire.variant", SpiderKey = "solitaire.spider";

    static readonly Color Gold = Color.FromRgb(255, 209, 102);
    static readonly Color Felt = Color.FromRgb(22, 92, 60);
    static readonly string[] SuitGlyphs = { "♠", "♣", "♦", "♥" };

    /// <summary>A card (or empty pile) on screen, in board coordinates, bottom to top.</summary>
    readonly record struct Placed(Spot Spot, int Depth, Rect Rect, Control? El);

    readonly Canvas _board = new() { IsHitTestVisible = false };
    readonly Canvas _cascade = new() { IsHitTestVisible = false };
    readonly ScaleTransform _zoom = new(1, 1);
    readonly Dictionary<int, Border> _faces = new();
    readonly Dictionary<Control, int> _faceCards = new(); // each face element's card
    readonly List<Border> _backs = new();
    readonly List<Placed> _layout = new();
    readonly List<(Rect Rect, Action Click)> _buttons = new();
    readonly Dictionary<Control, Vec2> _before = new(); // where each face-up card sat as a redraw began
    readonly List<Control> _moving = new(); // the cards gliding somewhere in this redraw: they go on top
    readonly Dictionary<Control, Anims.Tween> _glides = new();
    readonly DragHandle _handle;
    IPatience _rules;
    PatienceKind _kind, _lastSpider;
    PatienceKind? _askKind; // the game a chip asks "Sure?" about
    Vec2 _origin;
    double _scale = 1, _elapsed, _finishIn, _confirmNew, _confirmKind;
    long _clockFrom;
    bool _placed, _clockOn, _over, _racing, _dealing, _dealPending = true, _rowDeal, _seeded;
    int _homeHigh, _demoWait, _demoDraws, _demoIdle, _demoStep, _lanRound, _session = -1;
    string? _keypad; // the deal number being typed while the number pad is open
    List<Move>? _freeCellPlan;
    List<Move?>? _spiderPlan;

    // a press on a card: what it picked up, and whether it turned into a drag
    (Spot Spot, int Count, Vec2 At, List<(Control El, double X, double Y)> Els)? _press;
    bool _dragging;

    public SolitaireGame(IGameHost host) : base(host)
    {
        _board.RenderTransformOrigin = RelativePoint.TopLeft;
        _board.RenderTransform = _zoom;
        Layer.Children.Add(_board);
        Layer.Children.Add(_cascade);
        _handle = new DragHandle(host, Id, Title);
        Layer.Children.Add(_handle.Visual);
        var levels = host.Settings.Levels;
        _kind = (PatienceKind)Math.Clamp(levels.TryGetValue(KindKey, out int kind) ? kind : 0, 0, (int)PatienceKind.Spider4);
        _lastSpider = levels.TryGetValue(SpiderKey, out int spider) && Patience.IsSpider((PatienceKind)spider) ? (PatienceKind)spider : PatienceKind.Spider1;
        _rules = NewRules();
    }

    public override string Id => "solitaire";
    public override string Title => "Solitaire";

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        s.Rotor.Children.Add(Art.At(new Rectangle { Width = 11, Height = 15, RadiusX = 2, RadiusY = 2, Fill = Art.Brush(Color.FromRgb(30, 54, 128)), Stroke = Brushes.White, StrokeThickness = 1 }, -10, -9));
        s.Rotor.Children.Add(Art.At(new Rectangle { Width = 11, Height = 15, RadiusX = 2, RadiusY = 2, Fill = Brushes.White, Stroke = Art.Brush("#3B3B45"), StrokeThickness = 1 }, -3, -6));
        s.Rotor.Children.Add(Art.At(new TextBlock { Text = "♥", FontSize = 10, Foreground = Art.Brush(Color.FromRgb(208, 40, 52)) }, -1, -5));
        return s;
    }

    public override HudInfo Hud
    {
        get
        {
            long best = Host.Stats.Get(BestKey);
            string score = _rules is SpiderRules spider ? $"{spider.SuitsDone}/{SpiderRules.Suits13}" : $"{_rules.Home}/{_rules.Total}";
            string line = _over ? L.F("Solved in {0} moves · New deal plays again", _rules.Moves)
                : _rules.Moves > 0 ? Prefix + L.F("Moves {0} · {1}", _rules.Moves, Clock())
                : _kind switch
                {
                    PatienceKind.Klondike => L.T("Click the stock to turn a card · click or drag cards to move them · right-drag moves the felt"),
                    PatienceKind.FreeCell => Prefix + L.T("Click or drag cards to move them · a free cell holds any one card · right-drag moves the felt"),
                    _ => Prefix + L.T("Build each suit down from king to ace · click the stock to deal a row · right-drag moves the felt"),
                };
            return new HudInfo(score, line, best > 0 ? L.F("Fewest moves {0}", best) : L.T("Best —"));
        }
    }

    /// <summary>What the scoreboard puts before the moves: the FreeCell deal's number, or Spider's suits.</summary>
    string Prefix => _rules switch
    {
        FreeCellRules fc => L.F("Deal #{0}", fc.Deal) + " · ",
        SpiderRules => SuitsName(_kind) + " · ",
        _ => "",
    };

    static string SuitsName(PatienceKind kind) => kind switch
    {
        PatienceKind.Spider1 => L.T("One suit"),
        PatienceKind.Spider2 => L.T("Two suits"),
        _ => L.T("Four suits"),
    };

    /// <summary>The fewest moves, kept for each game (and for Spider, each number of suits) apart.</summary>
    string BestKey => _kind switch
    {
        PatienceKind.Klondike => "solitaire.best",
        PatienceKind.FreeCell => "solitaire.freecellbest",
        _ => "solitaire.spiderbest" + Patience.SpiderSuits(_kind).ToString(CultureInfo.InvariantCulture),
    };

    string Clock()
    {
        int s = (int)Seconds;
        return $"{s / 60}:{s % 60:00}";
    }

    double Seconds => _elapsed + (_clockOn ? (Environment.TickCount64 - _clockFrom) / 1000.0 : 0);

    public override string? ShareText => _rules switch
    {
        _ when _rules.Moves == 0 => null,
        FreeCellRules fc => _over ? L.F("FreeCell #{0} · won in {1} moves", fc.Deal, fc.Moves) : L.F("FreeCell #{0} · {1} of 52 cards home", fc.Deal, fc.Home),
        SpiderRules sp => _over ? L.F("Spider, {0} · won in {1} moves", SuitsName(_kind).ToLowerInvariant(), sp.Moves) : null,
        _ => _over ? L.F("Solitaire · solved in {0} moves · {1}", _rules.Moves, Clock()) : null,
    };

    // ------------------------------------------------------------------ worked out ahead

    /// <summary>When each card of a new deal leaves the stock, so the tableau fans out card by card.</summary>
    public static double DealDelay(int card) => card * DealStagger;

    /// <summary>
    /// The order the cards were dealt in: row by row, pile 0 gets one, pile 6 seven, so the card at
    /// <paramref name="position"/> (from the bottom) of <paramref name="pile"/> was the n-th out of the deck.
    /// </summary>
    public static int DealIndex(int pile, int position)
    {
        int n = 0;
        for (int row = 0; row < position; row++) n += Piles - row;
        return n + pile - position;
    }

    double DealDelayOf(int pile, int position) => _kind == PatienceKind.Klondike
        ? DealDelay(DealIndex(pile, position))
        : Patience.DealOrder(_kind, pile, position) * WideDealStagger;

    // ------------------------------------------------------------------ deals

    bool LanOn => Host.Lan.Connected;

    /// <summary>The n-th FreeCell deal of a LAN race: the same on both screens that day.</summary>
    public static int LanDeal(DateTime day, int round) =>
        (int)((uint)MinesweeperRules.DailySeed(day, "freecell-lan", round) % FreeCellRules.ClassicDeals) + 1;

    IPatience NewRules()
    {
        _seeded = false;
        switch (_kind)
        {
            case PatienceKind.Klondike:
                return new SolitaireRules(Rng);
            case PatienceKind.FreeCell:
                if (!LanOn) return new FreeCellRules(Rng.Next(1, FreeCellRules.ClassicDeals + 1));
                CheckSession();
                _seeded = true;
                return new FreeCellRules(LanDeal(DateTime.Today, ++_lanRound));
            default:
                return new SpiderRules(Patience.SpiderSuits(_kind), Rng);
        }
    }

    /// <summary>True when the pairing changed: both screens then count the race's deals from the start.</summary>
    void CheckSession()
    {
        int session = LanOn ? Host.Lan.Session : -1;
        if (session == _session) return;
        _session = session;
        _lanRound = 0;
    }

    bool InProgress => _rules.Moves > 0 && !_over;

    // ------------------------------------------------------------------ races

    public override bool SupportsLan => true;
    public override (int Score, bool Active)? Race => (RaceScore, _racing);
    public override int RaceBaseline => 26;
    public override int RaceMax => SolitaireRules.DeckSize;
    public override int RaceBest => (int)Host.Stats.Get("solitaire.home");
    public override double RaceSeconds => 240;

    int RaceScore => Patience.RaceScore(_rules.Home, _rules.Total);

    /// <summary>The rival's round began: a fresh deal, unless this one is untouched (and, for FreeCell over the LAN, the race's), and the round is on.</summary>
    public override void StartRace()
    {
        if (_racing) return;
        if (_rules.Moves > 0 || _over || (_kind == PatienceKind.FreeCell && LanOn && !_seeded)) Deal();
        BeginRound();
    }

    void BeginRound()
    {
        _racing = true;
        Host.RoundStarted();
        Host.HudChanged();
    }

    /// <summary>The deal is solved or given up: what went home is the round's score.</summary>
    void EndRound()
    {
        if (!_racing) return;
        _racing = false;
        Host.Stats.Max("solitaire.home", RaceScore);
        Host.RoundEnded(RaceScore);
    }

    // ------------------------------------------------------------------ layout

    int Columns => Patience.Columns(_kind);

    /// <summary>The felt's inside width: the columns, and in FreeCell the buttons' gap between the free cells and the foundations.</summary>
    double BoardW => Columns * CardW + (Columns - 1) * Gap + (_kind == PatienceKind.FreeCell ? ButtonW + Gap : 0);

    double PanelW => BoardW + Pad * 2;

    /// <summary>The felt and the chips under it.</summary>
    double FullH => PanelH + ChipRow;

    /// <summary>Where the tableau starts: FreeCell's cascades sit centred under its wider top row.</summary>
    double TableauX => (BoardW - (Columns * CardW + (Columns - 1) * Gap)) / 2;

    public override void Layout()
    {
        Measure();
        Render();
        Host.HudChanged();
    }

    /// <summary>The scale for the arena and the table, and the felt kept inside the arena.</summary>
    void Measure()
    {
        var a = Host.Arena;
        _scale = Clamp(Math.Min(a.Width * 0.42 / KlondikePanelW, a.Height * 0.86 / FullH), 0.55, 1.4);
        _scale = Math.Min(_scale, Math.Min((a.Width - 20) / PanelW, (a.Height - 20) / FullH)); // tiny arenas
        if (!_placed)
        {
            _placed = true;
            _origin = _handle.Saved() ?? PlaceBoard(PanelW * _scale, FullH * _scale);
        }
        _origin = Fit(_origin);
        Place();
    }

    /// <summary>The board's top-left: centered, else beside the HUD; always inside the arena.</summary>
    Vec2 PlaceBoard(double w, double h)
    {
        var a = Host.Arena;
        var hud = Host.HudBounds.Inflate(14);
        var mid = new Vec2(a.Center.X, a.Center.Y);
        var tries = new[]
        {
            Fit(new Vec2(mid.X - w / 2, mid.Y - h / 2)), Fit(new Vec2(hud.Right, mid.Y - h / 2)), Fit(new Vec2(hud.Left - w, mid.Y - h / 2)),
            Fit(new Vec2(mid.X - w / 2, hud.Bottom)), Fit(new Vec2(mid.X - w / 2, hud.Top - h)),
        };
        foreach (var t in tries)
            if (!new Rect(t.X, t.Y, w, h).Intersects(hud)) return t;
        return tries[0];
    }

    Vec2 Fit(Vec2 o)
    {
        var a = Host.Arena;
        double w = PanelW * _scale, h = FullH * _scale;
        return new Vec2(
            Clamp(o.X, a.Left + 10, Math.Max(a.Left + 10, a.Right - w - 10)),
            Clamp(o.Y, a.Top + 10, Math.Max(a.Top + 10, a.Bottom - h - 10)));
    }

    /// <summary>Puts the felt at its origin and size, and the grip above it.</summary>
    void Place()
    {
        _zoom.ScaleX = _zoom.ScaleY = _scale;
        Canvas.SetLeft(_board, _origin.X);
        Canvas.SetTop(_board, _origin.Y);
        _handle.Show(new Rect(_origin.X, _origin.Y, PanelW * _scale, PanelH * _scale));
    }

    public override void PositionsReset() => _placed = false;

    /// <summary>The card backs take the theme's colour at once; the felt follows at the next layout, which the overlay asks for.</summary>
    public override void ThemeChanged()
    {
        foreach (var back in _backs) DurakGame.PaintBack(back);
    }

    public override void Activate()
    {
        if (_clockOn) _clockFrom = Environment.TickCount64;
        // a FreeCell table left from before the pairing: the race's deals start now
        if (_kind == PatienceKind.FreeCell && LanOn && !_seeded && !InProgress)
        {
            _rules = NewRules();
            _dealPending = true;
        }
        Layout();
    }

    public override void Deactivate()
    {
        if (_clockOn) _elapsed = Seconds; // the clock pauses while away; Activate restarts it
        _handle.Cancel();
        _press = null;
        _dragging = false;
        _askKind = null;
        Anims.Finish();
    }

    public override void Summon(Vec2 p)
    {
        _origin = p - new Vec2(PanelW * _scale / 2, FullH * _scale / 2);
        Layout();
        _handle.Save(_origin);
    }

    /// <summary>Board coordinates (unscaled, felt padding excluded) of an overlay point.</summary>
    Vec2 ToBoard(Vec2 p) => new((p.X - _origin.X) / _scale - Pad, (p.Y - _origin.Y) / _scale - Pad);

    static double ColX(int col) => col * (CardW + Gap);

    Rect Slot(Spot s) => (_kind, s.Zone) switch
    {
        (_, Zone.Tableau) => new Rect(TableauX + ColX(s.Index), TableauTop, CardW, CardH),
        (PatienceKind.Klondike, Zone.Stock) => new Rect(ColX(0), 0, CardW, CardH),
        (PatienceKind.Klondike, Zone.Waste) => new Rect(ColX(1), 0, CardW, CardH),
        (PatienceKind.Klondike, Zone.Foundation) => new Rect(ColX(3 + s.Index), 0, CardW, CardH),
        (PatienceKind.FreeCell, Zone.Cell) => new Rect(ColX(s.Index), 0, CardW, CardH),
        (PatienceKind.FreeCell, Zone.Foundation) => new Rect(ColX(4 + s.Index) + ButtonW + Gap, 0, CardW, CardH),
        (_, Zone.Stock) => new Rect(ColX(0), 0, CardW, CardH),
        (_, Zone.Foundation) => new Rect(ColX(2 + s.Index), 0, CardW, CardH),
        _ => default,
    };

    /// <summary>Where New deal and Undo sit: in the top row's gap (beside the waste, between the cells and foundations, beside Spider's stock).</summary>
    double ButtonX => _kind switch
    {
        PatienceKind.Klondike => ColX(2) + (CardW - ButtonW) / 2,
        PatienceKind.FreeCell => ColX(4),
        _ => ColX(1) + (CardW - ButtonW) / 2,
    };

    int Foundations => Patience.IsSpider(_kind) ? SpiderRules.Suits13 : Suits;

    // ------------------------------------------------------------------ drawing

    /// <summary>
    /// Redraws the felt, the empty piles, the buttons, the chips and every card from the rules. A face-up card whose
    /// place changed glides there from where it was (from under the pointer, after a drag); a new deal fans out, a row
    /// dealt in Spider comes off the stock, and a whole suit taken off flies to its pile.
    /// </summary>
    void Render()
    {
        _before.Clear();
        foreach (var p in _layout)
            if (p.El != null && _faceCards.ContainsKey(p.El)) _before[p.El] = new Vec2(Canvas.GetLeft(p.El), Canvas.GetTop(p.El));
        _board.Children.Clear();
        _layout.Clear();
        _buttons.Clear();
        _moving.Clear();
        var cloth = Themes.Current.Felt ?? Felt;
        _board.Children.Add(Art.At(new Rectangle
        {
            Width = PanelW, Height = PanelH, RadiusX = 16, RadiusY = 16,
            Fill = Art.Brush(Color.FromArgb(215, cloth.R, cloth.G, cloth.B)), Stroke = Art.Brush(90, 255, 255, 255), StrokeThickness = 1.5,
        }, 0, 0));

        // empty piles: outlines, with a suit on each foundation and a "take the waste back" arrow on Klondike's stock
        switch (_kind)
        {
            case PatienceKind.Klondike:
                Outline(Spot.Stock, _rules.Cards(Spot.Waste).Count > 0 ? "↻" : "");
                Outline(Spot.Waste, "");
                for (int f = 0; f < Suits; f++) Outline(Spot.Foundation(f), SuitGlyphs[f]);
                for (int p = 0; p < Piles; p++) Outline(Spot.Tableau(p), "K");
                break;
            case PatienceKind.FreeCell:
                for (int c = 0; c < FreeCellRules.FreeCells; c++) Outline(Spot.Cell(c), "");
                for (int f = 0; f < Suits; f++) Outline(Spot.Foundation(f), SuitGlyphs[f]);
                for (int p = 0; p < FreeCellRules.Cascades; p++) Outline(Spot.Tableau(p), "");
                break;
            default:
                Outline(Spot.Stock, "");
                for (int f = 0; f < SpiderRules.Suits13; f++) Outline(Spot.Foundation(f), "");
                for (int p = 0; p < SpiderRules.Columns; p++) Outline(Spot.Tableau(p), "");
                break;
        }

        int backs = 0;
        Border Back()
        {
            if (backs == _backs.Count) _backs.Add(DurakGame.CardBack(CardW, CardH));
            return _backs[backs++];
        }

        var stockSlot = Slot(Spot.Stock);
        if (_rules is SpiderRules spider)
        {
            // a back for every row still to deal, stacked down a little
            for (int d = 0; d < spider.DealsLeft; d++) Put(Spot.Stock, 0, new Rect(stockSlot.X, stockSlot.Y + d * StockStep, CardW, CardH), Back());
        }
        else if (_kind == PatienceKind.Klondike && _rules.Cards(Spot.Stock).Count > 0) Put(Spot.Stock, 0, stockSlot, Back());
        if (_kind == PatienceKind.Klondike && _rules.Cards(Spot.Waste).Count > 0) Put(Spot.Waste, 1, Slot(Spot.Waste), Face(_rules.Cards(Spot.Waste)[^1]));
        for (int f = 0; f < Foundations; f++)
            if (_rules.Cards(Spot.Foundation(f)).Count > 0) Put(Spot.Foundation(f), 1, Slot(Spot.Foundation(f)), Face(_rules.Cards(Spot.Foundation(f))[^1]));
        if (_kind == PatienceKind.FreeCell)
            for (int c = 0; c < FreeCellRules.FreeCells; c++)
                if (_rules.Cards(Spot.Cell(c)) is { Count: 1 } cell) Put(Spot.Cell(c), 1, Slot(Spot.Cell(c)), Face(cell[0]));

        // a new deal comes off the stock (FreeCell has none: off the top of the felt, between the cells and the foundations)
        var deck = _kind == PatienceKind.FreeCell ? new Vec2(ButtonX + Pad, Pad) : new Vec2(stockSlot.X + Pad, stockSlot.Y + Pad);
        double dealEnd = 0;
        for (int p = 0; p < Columns; p++)
        {
            var spot = Spot.Tableau(p);
            var pile = _rules.Cards(spot);
            int hidden = _rules.Hidden(spot), up = pile.Count - hidden;
            // squeeze the face-up fan when a long pile would run off the felt
            double room = BoardH - TableauTop - CardH - hidden * FanHidden;
            double fan = up > 1 ? Math.Min(FanUp, room / (up - 1)) : FanUp;
            double y = TableauTop, x = TableauX + ColX(p);
            for (int i = 0; i < pile.Count; i++)
            {
                bool faceUp = i >= hidden;
                var el = faceUp ? Face(pile[i]) : Back();
                Put(spot, pile.Count - i, new Rect(x, y, CardW, CardH), el, faceUp);
                if (_dealPending)
                {
                    double delay = DealDelayOf(p, i);
                    Glide(el, deck, new Vec2(x + Pad, y + Pad), delay);
                    dealEnd = Math.Max(dealEnd, delay);
                }
                else if (_rowDeal && i == pile.Count - 1)
                {
                    Glide(el, deck, new Vec2(x + Pad, y + Pad), p * RowDealStagger);
                    _moving.Add(el);
                }
                y += faceUp ? fan : FanHidden;
            }
        }
        if (_dealPending || _rowDeal)
        {
            double busy = _dealPending ? dealEnd : (Columns - 1) * RowDealStagger;
            _dealPending = _rowDeal = false;
            _dealing = true;
            Anims.After(busy + GlideTime, () => _dealing = false);
        }

        // the two buttons sit in a gap of the top row
        double bx = ButtonX;
        bool confirming = _confirmNew > 0;
        Button(new Rect(bx, 8, ButtonW, ButtonH), confirming ? L.T("Sure?") : L.T("New deal"), confirming, NewDealClicked);
        if (_rules.CanUndo && !_over) Button(new Rect(bx, 8 + ButtonH + 10, ButtonW, ButtonH), L.T("Undo"), false, UndoClicked);

        foreach (var el in _moving) // a card on the move passes over the rest
        {
            _board.Children.Remove(el);
            _board.Children.Add(el);
        }
        FlyHome();
        DrawChips();
        DrawKeypad();
    }

    /// <summary>The cards of a suit Spider just took off the table fly from the column to its pile, where only the king stays in sight.</summary>
    void FlyHome()
    {
        if (_rules is not SpiderRules) return;
        int n = 0;
        foreach (var (el, from) in _before)
        {
            if (_layout.Any(q => q.El == el) || !_faceCards.TryGetValue(el, out int card)) continue;
            int f = Enumerable.Range(0, Foundations).FirstOrDefault(i => _rules.Cards(Spot.Foundation(i)).Contains(card), -1);
            if (f < 0) continue;
            var slot = Slot(Spot.Foundation(f));
            var to = new Vec2(slot.X + Pad, slot.Y + Pad);
            _board.Children.Add(el);
            Canvas.SetLeft(el, from.X);
            Canvas.SetTop(el, from.Y);
            Anims.Tween? mine = null;
            mine = Anims.Add(FlySeconds, k =>
            {
                Canvas.SetLeft(el, from.X + (to.X - from.X) * k);
                Canvas.SetTop(el, from.Y + (to.Y - from.Y) * k);
            }, Ease.InOutQuad, () =>
            {
                if (_glides.TryGetValue(el, out var t) && t == mine) _glides.Remove(el);
                if (!_layout.Any(q => q.El == el)) _board.Children.Remove(el);
            }, n++ * 0.03);
            _glides[el] = mine;
            Host.Wake();
        }
    }

    void Outline(Spot s, string mark)
    {
        var r = Slot(s);
        _board.Children.Add(At(new Rectangle
        {
            Width = CardW, Height = CardH, RadiusX = 7, RadiusY = 7, Fill = Art.Brush(40, 0, 0, 0),
            Stroke = Art.Brush(110, 255, 255, 255), StrokeThickness = 1.5, StrokeDashArray = new AvaloniaList<double> { 4, 3 },
        }, r.X, r.Y));
        if (mark.Length > 0)
        {
            var t = new TextBlock { Text = mark, FontFamily = Fx.Font, FontSize = 28, FontWeight = FontWeight.Bold, Foreground = Art.Brush(80, 255, 255, 255) };
            t.Measure(Size.Infinity);
            _board.Children.Add(At(t, r.X + (CardW - t.DesiredSize.Width) / 2, r.Y + (CardH - t.DesiredSize.Height) / 2));
        }
        _layout.Add(new Placed(s, 0, r, null));
    }

    void Put(Spot s, int depth, Rect r, Control el, bool movable = true)
    {
        _board.Children.Add(At(el, r.X, r.Y));
        _layout.Add(new Placed(s, movable ? depth : -1, r, el));
        var to = new Vec2(r.X + Pad, r.Y + Pad);
        if (!_before.TryGetValue(el, out var from) || (from - to).Length < 0.5) return;
        Glide(el, from, to);
        _moving.Add(el);
    }

    Border Face(int card)
    {
        if (!_faces.TryGetValue(card, out var face))
        {
            var (suit, rank) = _rules.Face(card);
            _faces[card] = face = DurakGame.CardFace(suit, rank, CardW, CardH);
            _faceCards[face] = card;
        }
        return face;
    }

    void Button(Rect r, string text, bool hot, Action click)
    {
        _board.Children.Add(At(new Border
        {
            Width = r.Width, Height = r.Height, CornerRadius = new CornerRadius(r.Height / 2),
            Background = hot ? Art.Brush(Color.FromRgb(255, 107, 107)) : Art.Brush(235, 255, 209, 102),
            BorderBrush = Art.Brush("#8A6D1F"), BorderThickness = new Thickness(1.2),
            Child = new TextBlock
            {
                Text = text, FontFamily = Fx.Font, FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Art.Brush("#2A2008"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            },
        }, r.X, r.Y));
        _buttons.Add((r, click));
    }

    /// <summary>
    /// The chips under the felt: the three games (the one on the table lit), then the FreeCell deal's number (click it to
    /// type one in) or Spider's suits (click to go through one, two and four).
    /// </summary>
    void DrawChips()
    {
        double x = -Pad + 4;
        bool asking = _confirmKind > 0 && _askKind != null;
        foreach (var (kind, label) in new[] { (PatienceKind.Klondike, L.T("Klondike")), (PatienceKind.FreeCell, L.T("FreeCell")), (PatienceKind.Spider1, L.T("Spider")) })
        {
            bool on = kind == PatienceKind.Spider1 ? Patience.IsSpider(_kind) : _kind == kind;
            var target = kind == PatienceKind.Spider1 ? _lastSpider : kind;
            bool sure = !on && asking && _askKind == target;
            x = Chip(x, sure ? L.T("Sure?") : label, on, sure, () => KindClicked(target)).Right + 6;
        }
        x += 8;
        if (_rules is FreeCellRules fc)
            Chip(x, LanOn && _seeded ? L.T("LAN race") + " · #" + fc.Deal.ToString(CultureInfo.InvariantCulture) : L.F("Deal #{0}", fc.Deal), false, false, DealChipClicked);
        else if (Patience.IsSpider(_kind))
        {
            var next = NextSuits(_kind);
            bool sure = asking && _askKind == next;
            Chip(x, sure ? L.T("Sure?") : "♠ " + SuitsName(_kind), false, sure, () => KindClicked(next));
        }
    }

    static PatienceKind NextSuits(PatienceKind kind) => kind switch
    {
        PatienceKind.Spider1 => PatienceKind.Spider2,
        PatienceKind.Spider2 => PatienceKind.Spider4,
        _ => PatienceKind.Spider1,
    };

    Rect Chip(double x, string text, bool on, bool hot, Action click)
    {
        var theme = Themes.Current;
        var t = new TextBlock
        {
            Text = text, FontFamily = Fx.Font, FontSize = 11, FontWeight = FontWeight.Bold,
            Foreground = on || hot ? Art.Brush("#2A2008") : Art.Brush(Art.Blend(theme.HudFront, theme.HudBack, 0.1)),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        t.Measure(Size.Infinity);
        var r = new Rect(x, BoardH + Pad + ChipGap, Math.Ceiling(t.DesiredSize.Width) + 22, ChipH);
        var gold = Themes.Themed(Gold);
        _board.Children.Add(At(new Border
        {
            Width = r.Width, Height = r.Height, CornerRadius = new CornerRadius(ChipH / 2), Child = t,
            Background = hot ? Art.Brush(Color.FromRgb(255, 107, 107)) : on ? Art.Brush(gold) : Art.Brush(Color.FromArgb(215, theme.Ink.R, theme.Ink.G, theme.Ink.B)),
            BorderBrush = on ? Art.Brush(Art.Blend(gold, Colors.Black, 0.45)) : Art.Brush(Color.FromArgb(120, theme.Accent.R, theme.Accent.G, theme.Accent.B)),
            BorderThickness = new Thickness(1),
        }, r.X, r.Y));
        _buttons.Add((r, click));
        return r;
    }

    /// <summary>The number pad for a FreeCell deal number, over the middle of the felt: the digits so far, then 1–9, ⌫, 0 and ✓.</summary>
    void DrawKeypad()
    {
        if (_keypad == null) return;
        var theme = Themes.Current;
        double w = KeyW * 3 + KeyGap * 2 + 24, h = 62 + (KeyH + KeyGap) * 4 + 6;
        var panel = new Rect(BoardW / 2 - w / 2, TableauTop + 8, w, h);
        _board.Children.Add(At(new Border
        {
            Width = w, Height = h, CornerRadius = new CornerRadius(12), Background = Art.Brush(Color.FromArgb(250, theme.Ink.R, theme.Ink.G, theme.Ink.B)),
            BorderBrush = Art.Brush(Themes.Themed(Gold)), BorderThickness = new Thickness(1.5),
            BoxShadow = new BoxShadows(new BoxShadow { OffsetY = 4, Blur = 14, Color = Color.FromArgb(120, 0, 0, 0) }),
        }, panel.X, panel.Y));
        _buttons.Add((panel, () => { })); // the pad itself takes a click between its keys
        var front = Art.Brush(theme.HudFront);
        _board.Children.Add(At(new TextBlock { Text = L.T("Deal number"), FontFamily = Fx.Font, FontSize = 11, FontWeight = FontWeight.Bold, Foreground = front, Opacity = 0.75 }, panel.X + 12, panel.Y + 8));
        _board.Children.Add(At(new TextBlock
        {
            Text = "#" + (_keypad.Length > 0 ? _keypad : "…"), FontFamily = Fx.Font, FontSize = 22, FontWeight = FontWeight.Black,
            Foreground = Art.Brush(Themes.Themed(Gold)),
        }, panel.X + 12, panel.Y + 24));
        string[] keys = { "1", "2", "3", "4", "5", "6", "7", "8", "9", "⌫", "0", "✓" };
        for (int i = 0; i < keys.Length; i++)
        {
            string key = keys[i];
            var r = new Rect(panel.X + 12 + i % 3 * (KeyW + KeyGap), panel.Y + 62 + i / 3 * (KeyH + KeyGap), KeyW, KeyH);
            bool ok = key == "✓";
            _board.Children.Add(At(new Border
            {
                Width = r.Width, Height = r.Height, CornerRadius = new CornerRadius(8),
                Background = ok ? Art.Brush(Themes.Themed(Gold)) : Art.Brush(Color.FromArgb(40, 255, 255, 255)),
                BorderBrush = Art.Brush(Color.FromArgb(90, 255, 255, 255)), BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = key, FontFamily = Fx.Font, FontSize = 15, FontWeight = FontWeight.Bold, Foreground = ok ? Art.Brush("#2A2008") : front,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            }, r.X, r.Y));
            _buttons.Add((r, () => KeyClicked(key)));
        }
    }

    static T At<T>(T el, double x, double y) where T : Control => Art.At(el, x + Pad, y + Pad);

    // ------------------------------------------------------------------ motion

    /// <summary>A card slides from <paramref name="from"/> to <paramref name="to"/> (felt coordinates), after <paramref name="delay"/>.</summary>
    void Glide(Control el, Vec2 from, Vec2 to, double delay = 0)
    {
        if (_glides.TryGetValue(el, out var old)) old.Cancel();
        Canvas.SetLeft(el, from.X);
        Canvas.SetTop(el, from.Y);
        Anims.Tween? mine = null;
        mine = Anims.Add(GlideTime, k =>
        {
            Canvas.SetLeft(el, from.X + (to.X - from.X) * k);
            Canvas.SetTop(el, from.Y + (to.Y - from.Y) * k);
        }, Ease.OutCubic, () =>
        {
            if (_glides.TryGetValue(el, out var t) && t == mine) _glides.Remove(el);
        }, delay);
        _glides[el] = mine;
        Host.Wake();
    }

    /// <summary>The classic end: the court cards spring off the foundations and bounce along the bottom of the screen, briefly.</summary>
    void WinCascade()
    {
        if (Fx.ReducedMotion) return;
        var a = Host.Arena;
        double cw = CardW * _scale, ch = CardH * _scale;
        for (int i = 0; i < CascadeCards; i++)
        {
            int f = i % Foundations, depth = i / Foundations;
            var pile = _rules.Cards(Spot.Foundation(f));
            if (depth >= pile.Count) continue;
            var (suit, rank) = _rules.Face(pile[^(depth + 1)]);
            var slot = ToOverlay(Slot(Spot.Foundation(f)));
            var vel = new Vec2((Rng.NextDouble() < 0.5 ? -1 : 1) * (150 + Rng.NextDouble() * 250), -(60 + Rng.NextDouble() * 220));
            var path = SolitaireCascade.Path(new Vec2(slot.X, slot.Y), vel, cw, ch, a, 1500, 0.75, CascadeSeconds, 1 / 60.0);
            var el = DurakGame.CardFace(suit, rank, CardW, CardH);
            el.RenderTransformOrigin = RelativePoint.TopLeft;
            el.RenderTransform = new ScaleTransform(_scale, _scale);
            el.IsHitTestVisible = false;
            el.Opacity = 0;
            _cascade.Children.Add(Art.At(el, path[0].X, path[0].Y));
            Anims.Add(CascadeSeconds, k =>
            {
                var p = path[Math.Min(path.Count - 1, (int)(k * (path.Count - 1)))];
                Canvas.SetLeft(el, p.X);
                Canvas.SetTop(el, p.Y);
                el.Opacity = k < 0.8 ? 1 : (1 - k) / 0.2;
            }, Ease.Linear, () => _cascade.Children.Remove(el), CascadeStagger * i);
        }
        Host.Wake();
    }

    // ------------------------------------------------------------------ input

    public override void CollectHitShapes(List<HitShape> into)
    {
        foreach (var p in _layout) into.Add(HitShape.Box(ToOverlay(p.Rect)));
        foreach (var b in _buttons) into.Add(HitShape.Box(ToOverlay(b.Rect)));
        into.Add(_handle.Hit);
    }

    Rect ToOverlay(Rect r) => new(_origin.X + (r.X + Pad) * _scale, _origin.Y + (r.Y + Pad) * _scale, r.Width * _scale, r.Height * _scale);

    public override bool PointerDown(Vec2 p, bool right)
    {
        if (right || _handle.Contains(p))
        {
            _handle.Begin(p, _origin, anywhere: true); // the grip, or a right-drag anywhere on the felt
            return true;
        }
        if (_press != null || _dealing) return false;
        var b = ToBoard(p);
        for (int i = _buttons.Count - 1; i >= 0; i--) // the number pad lies over the cards and is drawn last
            if (_buttons[i].Rect.Contains(b.ToPoint()))
            {
                _buttons[i].Click();
                return false;
            }
        if (_keypad != null)
        {
            _keypad = null; // a click off the number pad puts it away
            Changed();
            return false;
        }
        if (_over) return false;
        var hit = Top(b);
        if (hit is not { } h) return false;
        if (h.Spot.Zone == Zone.Stock)
        {
            StockClicked();
            return false;
        }
        if (h.Depth <= 0 || h.El == null) return false; // an empty pile or a face-down card
        if (h.Depth > _rules.Movable(h.Spot))
        {
            Host.Sound.Play("thunk", 0.2); // FreeCell and Spider only move a run from the top
            return false;
        }
        var els = _layout.Where(q => q.Spot == h.Spot && q.El != null && q.Depth > 0 && q.Depth <= h.Depth)
            .Select(q => (q.El!, q.Rect.X, q.Rect.Y)).ToList();
        foreach (var (el, _, _) in els)
            if (_glides.Remove(el, out var glide)) glide.Cancel(); // a card picked up mid-glide follows the hand instead
        _press = (h.Spot, h.Depth, p, els);
        _dragging = false;
        return true;
    }

    /// <summary>Klondike turns a card; Spider deals a row, once every column has a card.</summary>
    bool StockClicked()
    {
        if (_rules is SpiderRules spider && !spider.CanDeal)
        {
            if (spider.Stock.Count > 0)
            {
                var r = ToOverlay(Slot(Spot.Stock));
                Host.Fx.Popup(new Vec2(r.Center.X, r.Top - 10), L.T("Fill every column before dealing"), Colors.White, 15, 1.3);
                Host.Sound.Play("thunk", 0.2);
            }
            return false;
        }
        int home = _rules.Home;
        if (!_rules.Draw()) return false;
        Started();
        if (_rules is SpiderRules)
        {
            _rowDeal = true;
            Host.Sound.Play("whoosh", 0.3, 1.2);
            if (_rules.Home > home) SuitTakenOff();
            if (_rules.Won) Win();
        }
        else Host.Sound.Play("board", 0.25, 1.6);
        Changed();
        return true;
    }

    /// <summary>The topmost card or empty pile under a board point.</summary>
    Placed? Top(Vec2 b)
    {
        for (int i = _layout.Count - 1; i >= 0; i--)
            if (_layout[i].Rect.Contains(b.ToPoint())) return _layout[i];
        return null;
    }

    public override void PointerUp(Vec2 p)
    {
        _handle.End(_origin);
        if (_press is not { } press) return;
        _press = null;
        Move? move;
        Vec2 drop = ToBoard(p);
        if (_dragging)
        {
            // drop on whichever pile the dragged card's middle is over, or the one under the pointer
            var d = (p - press.At) / _scale;
            drop = new Vec2(press.Els[0].X + CardW / 2 + d.X, press.Els[0].Y + CardH / 2 + d.Y);
            move = Target(drop, press.Spot, press.Count) ?? Target(ToBoard(p), press.Spot, press.Count);
        }
        else move = _rules.Best(press.Spot, press.Count);
        bool dragged = _dragging;
        _dragging = false;
        if (move is { } m && Play(m)) return;
        if (!dragged) Host.Sound.Play("thunk", 0.2); // a click on a card that fits nowhere
        if (_rules is FreeCellRules fc && RoomFor(fc, press.Spot, press.Count, dragged ? drop : null) is int room)
            Host.Fx.Popup(p - new Vec2(0, 30), L.F("Room to move {0} cards at once", room), Colors.White, 15, 1.4);
        Render(); // a dropped run glides back to its pile
        Host.Wake();
    }

    /// <summary>
    /// FreeCell: a run that would fit where it was dropped (or anywhere, for a click) but is longer than the free cells and
    /// empty cascades allow: how many cards could move at once there. Null when size wasn't the trouble.
    /// </summary>
    int? RoomFor(FreeCellRules fc, Spot from, int count, Vec2? at)
    {
        for (int t = 0; t < FreeCellRules.Cascades; t++)
        {
            var to = Spot.Tableau(t);
            if (at is { } b && !FanArea(to).Inflate(Gap / 2).Contains(b.ToPoint())) continue;
            if (fc.TooBig(new Move(from, count, to))) return fc.MaxMove(to);
        }
        return null;
    }

    /// <summary>A tableau pile's whole fan, down to a card's length below its top card.</summary>
    Rect FanArea(Spot s)
    {
        var slot = Slot(s);
        double bottom = _layout.Where(q => q.Spot == s).Select(q => q.Rect.Bottom).DefaultIfEmpty(slot.Bottom).Max();
        return new Rect(slot.X, TableauTop, CardW, Math.Max(bottom - TableauTop, CardH));
    }

    Move? Target(Vec2 b, Spot from, int count)
    {
        foreach (var q in _layout.AsEnumerable().Reverse())
        {
            // a pile's whole fan counts, down to a card's length below its top card
            var area = q.Rect;
            if (q.Spot.Zone == Zone.Tableau) area = new Rect(area.X, TableauTop, CardW, Math.Max(area.Bottom - TableauTop, CardH));
            if (!area.Inflate(Gap / 2).Contains(b.ToPoint())) continue;
            if (q.Spot.Zone is not (Zone.Tableau or Zone.Foundation or Zone.Cell) || q.Spot == from) continue;
            var m = new Move(from, count, q.Spot);
            if (_rules.IsLegal(m)) return m;
        }
        return null;
    }

    public override void PointerCancel()
    {
        _handle.Cancel();
        _press = null;
        _dragging = false;
        Render();
    }

    bool Play(Move m)
    {
        int home = _rules.Home;
        if (!_rules.Apply(m)) return false;
        Started();
        if (m.To.Zone == Zone.Foundation) WentHome(m.To);
        else if (_rules.Home > home) SuitTakenOff();
        else Host.Sound.Play("board", 0.3, 1.3);
        if (_rules.Won) Win();
        if (_rules is FreeCellRules) _finishIn = FinishStep * 2; // the cards no longer needed follow a beat later
        Changed();
        return true;
    }

    /// <summary>FreeCell sends a card home by itself, as part of the move before it.</summary>
    void PlayAuto(Move m)
    {
        if (_rules is not FreeCellRules fc || !fc.Autoplay(m)) return;
        WentHome(m.To);
        if (_rules.Won) Win();
        Changed();
    }

    /// <summary>A card reached a foundation: sparks, a note that climbs with the pile, and the counter for the daily challenge.</summary>
    void WentHome(Spot foundation)
    {
        CountHome();
        var r = ToOverlay(Slot(foundation));
        var at = new Vec2(r.Center.X, r.Center.Y);
        Host.Fx.Burst(at, new[] { Gold, Colors.White }, 8, 180, 300, 4, 0.4);
        Host.Sound.Play("score", 0.3, 0.9 + _rules.Cards(foundation).Count * 0.03);
        Host.ShareAction(at, 1);
    }

    /// <summary>Spider took a whole suit off the table.</summary>
    void SuitTakenOff()
    {
        CountHome();
        var spot = Spot.Foundation(Math.Max(0, ((SpiderRules)_rules).SuitsDone - 1));
        var r = ToOverlay(Slot(spot));
        var at = new Vec2(r.Center.X, r.Center.Y);
        Host.Fx.Burst(at, new[] { Gold, Colors.White, Themes.Current.Accent }, 22, 320, 420, 5, 0.7);
        Host.Fx.Popup(at + new Vec2(0, r.Height * 0.8), L.T("Suit complete!"), Gold, 20, 1.2);
        Host.Sound.Play("fire", 0.45);
        Host.ShareAction(at, Ranks);
    }

    /// <summary>Counts cards home for the stats (and the daily challenge); undo and redo can't farm it.</summary>
    void CountHome()
    {
        if (_rules.Home <= _homeHigh) return;
        Host.Stats.Add("solitaire.cards", _rules.Home - _homeHigh);
        _homeHigh = _rules.Home;
    }

    void Started()
    {
        if (_over) return;
        if (!_racing) BeginRound(); // the first move starts a round
        if (_clockOn) return;
        _clockOn = true;
        _elapsed = 0;
        _clockFrom = Environment.TickCount64;
    }

    void Changed()
    {
        Render();
        Host.HudChanged();
        Host.Wake();
    }

    void NewDealClicked()
    {
        if (_kind == PatienceKind.FreeCell && LanOn && _seeded && _rules.Moves == 0 && !_over)
        {
            Host.Sound.Play("thunk", 0.2); // already a fresh deal of the race: skipping one would put the two screens out of step
            return;
        }
        if (InProgress && _confirmNew <= 0)
        {
            _confirmNew = ConfirmTime; // a deal in progress: the second click deals
            Changed();
            return;
        }
        Deal();
    }

    void UndoClicked()
    {
        if (!_rules.Undo()) return;
        Host.Sound.Play("whoosh", 0.2, 1.4);
        _freeCellPlan = null; // the demo works its plan out again from here
        _spiderPlan = null;
        Changed();
    }

    /// <summary>A chip for another game: mid-deal it asks "Sure?" first, then the table changes and deals.</summary>
    void KindClicked(PatienceKind kind)
    {
        if (kind == _kind) return;
        if (InProgress && (_askKind != kind || _confirmKind <= 0))
        {
            _askKind = kind;
            _confirmKind = ConfirmTime;
            Changed();
            return;
        }
        SwitchTo(kind);
    }

    void SwitchTo(PatienceKind kind)
    {
        _askKind = null;
        _confirmKind = 0;
        _keypad = null;
        _kind = kind;
        Host.Settings.Levels[KindKey] = (int)kind;
        if (Patience.IsSpider(kind))
        {
            _lastSpider = kind;
            Host.Settings.Levels[SpiderKey] = (int)kind;
        }
        Host.SaveSettings();
        _faces.Clear(); // a card number shows a different face in each game
        _faceCards.Clear();
        _layout.Clear();
        Measure(); // the table is another width
        Deal();
    }

    void DealChipClicked()
    {
        if (LanOn && _seeded) return; // the race picks the deals
        _keypad = _keypad == null ? "" : null;
        Host.Sound.Play("click", 0.3);
        Changed();
    }

    void KeyClicked(string key)
    {
        if (_keypad == null) return;
        Host.Sound.Play("key", 0.3);
        switch (key)
        {
            case "⌫":
                if (_keypad.Length > 0) _keypad = _keypad[..^1];
                break;
            case "✓":
                if (int.TryParse(_keypad, NumberStyles.None, CultureInfo.InvariantCulture, out int deal) && deal >= 1 && deal <= FreeCellRules.MaxDeal)
                {
                    _keypad = null;
                    Deal(deal);
                    return;
                }
                Host.Sound.Play("thunk", 0.2);
                break;
            default:
                string next = (_keypad + key).TrimStart('0');
                if (next.Length > 0 && int.Parse(next, CultureInfo.InvariantCulture) <= FreeCellRules.MaxDeal) _keypad = next;
                break;
        }
        Changed();
    }

    /// <summary>A new deal of the game on the table (<paramref name="freeCell"/>: that numbered FreeCell deal).</summary>
    void Deal(int? freeCell = null)
    {
        EndRound(); // a deal given up counts with what went home
        if (freeCell is { } number)
        {
            _rules = new FreeCellRules(number);
            _seeded = false;
        }
        else _rules = NewRules();
        _over = _clockOn = false;
        _elapsed = 0;
        _confirmNew = _finishIn = 0;
        _homeHigh = 0;
        _demoDraws = _demoStep = 0;
        _freeCellPlan = null;
        _spiderPlan = null;
        _dealPending = true;
        Host.Sound.Play("whoosh", 0.3);
        Changed();
    }

    void Win()
    {
        _over = true;
        _elapsed = Seconds;
        _clockOn = false;
        long before = Host.Stats.Get(BestKey);
        Host.Stats.Add("solitaire.wins");
        if (_kind == PatienceKind.FreeCell) Host.Stats.Add("solitaire.freecell");
        if (Patience.IsSpider(_kind)) Host.Stats.Add("solitaire.spider");
        Host.Stats.Min(BestKey, _rules.Moves);
        EndRound();
        bool best = before == 0 || _rules.Moves < before;
        var at = new Vec2(_origin.X + (BoardW / 2 + Pad) * _scale, _origin.Y + (TableauTop + 120) * _scale);
        Host.Fx.Popup(at, best ? L.T("NEW BEST!") : L.T("SOLVED!"), Gold, 40, 2.8, L.F("{0} moves · {1}", _rules.Moves, Clock()));
        Host.Fx.Burst(at, Themes.Current.Confetti, 50, 560, 700, 7, 1.2);
        Host.Sound.Play("best", 0.8);
        WinCascade();
    }

    // ------------------------------------------------------------------ frame

    public override bool Update(double dt)
    {
        bool busy = Anims.Update(dt);
        if (_handle.Dragging)
        {
            _origin = _handle.Move(Host.Pointer, new Size(PanelW * _scale, FullH * _scale));
            Place();
            busy = true;
        }
        if (_press is { } press)
        {
            var d = Host.Pointer - press.At;
            if (!_dragging && d.Length > DragStart)
            {
                _dragging = true;
                foreach (var (el, _, _) in press.Els) // lift the run above everything else
                {
                    _board.Children.Remove(el);
                    _board.Children.Add(el);
                }
            }
            if (_dragging)
                foreach (var (el, x, y) in press.Els)
                {
                    Canvas.SetLeft(el, x + Pad + d.X / _scale);
                    Canvas.SetTop(el, y + Pad + d.Y / _scale);
                }
            busy = true;
        }
        if (_confirmNew > 0 && (_confirmNew -= dt) <= 0) Changed(); // the "Sure?" lapses
        if (_confirmKind > 0 && (_confirmKind -= dt) <= 0)
        {
            _askKind = null;
            Changed();
        }
        if (_rules is FreeCellRules fc)
        {
            // cards nothing could need go home by themselves; once the cascades all run down, so does the rest
            if (_press == null && !_dealing && !_over && (fc.SafeHome() ?? (fc.CanFinish ? fc.NextHome() : null)) is { } auto)
            {
                if ((_finishIn -= dt) <= 0)
                {
                    _finishIn = FinishStep;
                    PlayAuto(auto);
                }
                busy = true;
            }
        }
        else if (_rules.CanFinish && _press == null && !_dealing)
        {
            if ((_finishIn -= dt) <= 0)
            {
                _finishIn = FinishStep; // the rest go home one after another, each gliding up as the next sets off
                if (_rules.NextHome() is { } m) Play(m);
            }
            busy = true;
        }
        return busy || _confirmNew > 0 || _confirmKind > 0;
    }

    // ------------------------------------------------------------------ demo

    public override void DemoTick()
    {
        if (_dealing || _demoWait-- > 0) return;
        _demoWait = 3 + Rng.Next(3);
        if (_over)
        {
            if (++_demoIdle > 5) Deal();
            return;
        }
        _demoIdle = 0;
        switch (_rules)
        {
            case FreeCellRules fc:
                FreeCellDemo(fc);
                break;
            case SpiderRules spider:
                SpiderDemo(spider);
                break;
            case SolitaireRules klondike:
                KlondikeDemo(klondike);
                break;
        }
    }

    /// <summary>Plays a plain strategy: home first, then moves that turn up a hidden card, then the waste, then draw.</summary>
    void KlondikeDemo(SolitaireRules rules)
    {
        if (rules.CanFinish) return; // it finishes by itself
        var move = KlondikeMove(rules);
        if (move is { } m)
        {
            _demoDraws = 0;
            Play(m);
            return;
        }
        // stuck: a few trips through the stock with nothing to play means a new deal
        if (++_demoDraws > (rules.Stock.Count + rules.Waste.Count + 1) * 2) Deal();
        else if (rules.Draw())
        {
            Started();
            Changed();
        }
    }

    static Move? KlondikeMove(SolitaireRules rules)
    {
        if (rules.NextHome() is { } home) return home;
        for (int p = 0; p < Piles; p++)
        {
            int n = rules.Movable(Spot.Tableau(p));
            if (n == 0) continue;
            if (rules.Hidden(p) > 0 && rules.Best(Spot.Tableau(p), n) is { To.Zone: Zone.Tableau } m) return m; // turns a card up
        }
        if (rules.Waste.Count > 0 && rules.Best(Spot.Waste, 1) is { } w) return w;
        return null;
    }

    /// <summary>Works the deal out with <see cref="FreeCellSolver"/> and plays it move by move, waiting while cards go home by themselves.</summary>
    void FreeCellDemo(FreeCellRules fc)
    {
        if (fc.SafeHome() != null || fc.CanFinish) return;
        if (_freeCellPlan == null)
        {
            _freeCellPlan = FreeCellSolver.Solve(fc, 20_000) ?? new List<Move>();
            _demoStep = 0;
        }
        if (_demoStep < _freeCellPlan.Count && Play(_freeCellPlan[_demoStep]))
        {
            _demoStep++;
            return;
        }
        Deal(); // no way through that it could see
    }

    /// <summary>Plays out what <see cref="SpiderSolver"/> finds, or else joins what it can and deals; a dead end is a new deal.</summary>
    void SpiderDemo(SpiderRules spider)
    {
        if (_spiderPlan == null)
        {
            _spiderPlan = SpiderSolver.Solve(spider, 1_200) ?? new List<Move?>();
            _demoStep = 0;
        }
        if (_demoStep < _spiderPlan.Count)
        {
            var step = _spiderPlan[_demoStep];
            if (step is { } m ? Play(m) : StockClicked())
            {
                _demoStep++;
                return;
            }
            _spiderPlan = new List<Move?>(); // out of step: play by eye from here
        }
        for (int c = 0; c < SpiderRules.Columns; c++)
        {
            int n = spider.Movable(Spot.Tableau(c));
            bool uncovers = n > 0 && n == spider.Cards(Spot.Tableau(c)).Count - spider.Hidden(Spot.Tableau(c)) && spider.Hidden(Spot.Tableau(c)) > 0;
            if (n > 0 && spider.Best(Spot.Tableau(c), n) is { } m && (uncovers || spider.Cards(m.To).Count > 0) && Play(m)) return;
        }
        if (spider.CanDeal) StockClicked();
        else if (++_demoDraws > 2) Deal();
    }
}

/// <summary>
/// The classic end-of-game card bounce, worked out ahead as a path: a card sets off with a velocity, falls under
/// gravity, bounces off the bottom of the box (losing some bounce each time) and off its sides, and never leaves
/// it. UI-free, so it can be checked.
/// </summary>
public static class SolitaireCascade
{
    /// <param name="start">The card's top-left corner to begin with.</param>
    /// <param name="velocity">Its velocity, in DIPs per second, y growing downwards.</param>
    /// <param name="cardW">The card's width and height on screen: the path keeps the whole card inside <paramref name="box"/>.</param>
    /// <param name="bounce">How much of its downward speed a card keeps off the floor, 0 to 1.</param>
    /// <param name="seconds">How long the path lasts, in <paramref name="step"/>-second steps.</param>
    public static List<Vec2> Path(Vec2 start, Vec2 velocity, double cardW, double cardH, Rect box, double gravity, double bounce, double seconds, double step)
    {
        double right = Math.Max(box.Left, box.Right - cardW), bottom = Math.Max(box.Top, box.Bottom - cardH);
        var p = new Vec2(Math.Clamp(start.X, box.Left, right), Math.Clamp(start.Y, box.Top, bottom));
        var v = velocity;
        var path = new List<Vec2> { p };
        for (double t = 0; t < seconds; t += step)
        {
            v.Y += gravity * step;
            p += v * step;
            if (p.Y > bottom)
            {
                p.Y = bottom;
                v.Y = -Math.Abs(v.Y) * bounce;
            }
            else if (p.Y < box.Top)
            {
                p.Y = box.Top;
                v.Y = Math.Abs(v.Y) * bounce;
            }
            if (p.X < box.Left)
            {
                p.X = box.Left;
                v.X = Math.Abs(v.X);
            }
            else if (p.X > right)
            {
                p.X = right;
                v.X = -Math.Abs(v.X);
            }
            path.Add(p);
        }
        return path;
    }
}
