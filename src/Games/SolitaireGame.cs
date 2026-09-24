using System;
using System.Collections.Generic;
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
/// Klondike solitaire, draw one, on a felt laid over the desktop. Click the stock to turn a card. Click a card to
/// send it where it fits (home to its foundation first), or drag it, or a face-up run, onto the pile you want.
/// Undo takes a move back; New deal asks twice. Once every card is face up, the rest go home by themselves.
/// Your best is the fewest moves. The grip above the felt (or a right-drag) moves the whole layout. A deal is a
/// round: over the LAN, or against the computer, the more cards sent home win, counted when the deal is solved
/// or given up for a new one. Only the cards, the empty piles, the buttons and the grip take the mouse.
/// </summary>
public sealed class SolitaireGame : MiniGame
{
    const double CardW = 64, CardH = 88, Gap = 10, Pad = 12, RowGap = 16, FanHidden = 9, FanUp = 30;
    const double BoardW = Piles * CardW + (Piles - 1) * Gap, TableauTop = CardH + RowGap;
    // tall enough for a long pile at a tighter fan; a longer one squeezes its fan to fit
    const double BoardH = TableauTop + 6 * FanHidden + 12 * 22 + CardH;
    const double PanelW = BoardW + Pad * 2, PanelH = BoardH + Pad * 2;
    const double ButtonW = CardW + Gap, ButtonH = 26, DragStart = 5, FinishStep = 0.1, ConfirmTime = 2.5;
    public const double GlideTime = 0.22, DealStagger = 0.04, CascadeSeconds = 2.2, CascadeStagger = 0.08;
    const int CascadeCards = 16;

    static readonly Color Gold = Color.FromRgb(255, 209, 102);
    static readonly Color Felt = Color.FromRgb(22, 92, 60);
    static readonly string[] SuitGlyphs = { "♠", "♣", "♦", "♥" };

    /// <summary>A card (or empty pile) on screen, in board coordinates, bottom to top.</summary>
    readonly record struct Placed(Spot Spot, int Depth, Rect Rect, Control? El);

    readonly Canvas _board = new() { IsHitTestVisible = false };
    readonly Canvas _cascade = new() { IsHitTestVisible = false };
    readonly ScaleTransform _zoom = new(1, 1);
    readonly Dictionary<int, Border> _faces = new();
    readonly HashSet<Control> _faceEls = new();
    readonly List<Border> _backs = new();
    readonly List<Placed> _layout = new();
    readonly List<(Rect Rect, Action Click)> _buttons = new();
    readonly Dictionary<Control, Vec2> _before = new(); // where each face-up card sat as a redraw began
    readonly List<Control> _moving = new(); // the cards gliding somewhere in this redraw: they go on top
    readonly Dictionary<Control, Anims.Tween> _glides = new();
    readonly DragHandle _handle;
    SolitaireRules _rules = new(Rng);
    Vec2 _origin;
    double _scale = 1, _elapsed, _finishIn, _confirmNew;
    long _clockFrom;
    bool _placed, _clockOn, _over, _racing, _dealing, _dealPending = true;
    int _homeHigh, _demoWait, _demoDraws, _demoIdle;

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
            long best = Host.Stats.Get("solitaire.best");
            return new HudInfo(
                $"{_rules.OnFoundations}/{DeckSize}",
                _over ? L.F("Solved in {0} moves · New deal plays again", _rules.Moves)
                    : _rules.Moves > 0 ? L.F("Moves {0} · {1}", _rules.Moves, Clock())
                    : L.T("Click the stock to turn a card · click or drag cards to move them · right-drag moves the felt"),
                best > 0 ? L.F("Fewest moves {0}", best) : L.T("Best —"));
        }
    }

    string Clock()
    {
        int s = (int)Seconds;
        return $"{s / 60}:{s % 60:00}";
    }

    double Seconds => _elapsed + (_clockOn ? (Environment.TickCount64 - _clockFrom) / 1000.0 : 0);

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

    // ------------------------------------------------------------------ races

    public override bool SupportsLan => true;
    public override (int Score, bool Active)? Race => (_rules.OnFoundations, _racing);
    public override int RaceBaseline => 26;
    public override int RaceMax => SolitaireRules.DeckSize;
    public override int RaceBest => (int)Host.Stats.Get("solitaire.home");
    public override double RaceSeconds => 240;

    /// <summary>The rival's round began: a fresh deal, unless this one is untouched, and the round is on.</summary>
    public override void StartRace()
    {
        if (_racing) return;
        if (_rules.Moves > 0 || _over) Deal();
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
        Host.Stats.Max("solitaire.home", _rules.OnFoundations);
        Host.RoundEnded(_rules.OnFoundations);
    }

    // ------------------------------------------------------------------ layout

    public override void Layout()
    {
        var a = Host.Arena;
        _scale = Clamp(Math.Min(a.Width * 0.42 / PanelW, a.Height * 0.86 / PanelH), 0.55, 1.4);
        _scale = Math.Min(_scale, Math.Min((a.Width - 20) / PanelW, (a.Height - 20) / PanelH)); // tiny arenas
        if (!_placed)
        {
            _placed = true;
            _origin = _handle.Saved() ?? PlaceBoard(PanelW * _scale, PanelH * _scale);
        }
        _origin = Fit(_origin);
        Place();
        Render();
        Host.HudChanged();
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
        double w = PanelW * _scale, h = PanelH * _scale;
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

    public override void Activate()
    {
        if (_clockOn) _clockFrom = Environment.TickCount64;
        Layout();
    }

    public override void Deactivate()
    {
        if (_clockOn) _elapsed = Seconds; // the clock pauses while away; Activate restarts it
        _handle.Cancel();
        _press = null;
        _dragging = false;
        Anims.Finish();
    }

    public override void Summon(Vec2 p)
    {
        _origin = p - new Vec2(PanelW * _scale / 2, PanelH * _scale / 2);
        Layout();
        _handle.Save(_origin);
    }

    /// <summary>Board coordinates (unscaled, felt padding excluded) of an overlay point.</summary>
    Vec2 ToBoard(Vec2 p) => new((p.X - _origin.X) / _scale - Pad, (p.Y - _origin.Y) / _scale - Pad);

    static double ColX(int col) => col * (CardW + Gap);

    static Rect Slot(Spot s) => s.Zone switch
    {
        Zone.Stock => new Rect(ColX(0), 0, CardW, CardH),
        Zone.Waste => new Rect(ColX(1), 0, CardW, CardH),
        Zone.Foundation => new Rect(ColX(3 + s.Index), 0, CardW, CardH),
        _ => new Rect(ColX(s.Index), TableauTop, CardW, CardH),
    };

    // ------------------------------------------------------------------ drawing

    /// <summary>
    /// Redraws the felt, the empty piles, the buttons and every card from the rules. A face-up card whose place
    /// changed glides there from where it was (from under the pointer, after a drag); a new deal fans out of the stock.
    /// </summary>
    void Render()
    {
        _before.Clear();
        foreach (var p in _layout)
            if (p.El != null && _faceEls.Contains(p.El)) _before[p.El] = new Vec2(Canvas.GetLeft(p.El), Canvas.GetTop(p.El));
        _board.Children.Clear();
        _layout.Clear();
        _buttons.Clear();
        _moving.Clear();
        _board.Children.Add(Art.At(new Rectangle
        {
            Width = PanelW, Height = PanelH, RadiusX = 16, RadiusY = 16,
            Fill = Art.Brush(Color.FromArgb(215, Felt.R, Felt.G, Felt.B)), Stroke = Art.Brush(90, 255, 255, 255), StrokeThickness = 1.5,
        }, 0, 0));

        // empty piles: outlines, with a suit on each foundation and a "take the waste back" arrow on the stock
        Outline(Spot.Stock, _rules.Waste.Count > 0 ? "↻" : "");
        Outline(Spot.Waste, "");
        for (int f = 0; f < Suits; f++) Outline(Spot.Foundation(f), SuitGlyphs[f]);
        for (int p = 0; p < Piles; p++) Outline(Spot.Tableau(p), "K");

        int backs = 0;
        Border Back()
        {
            if (backs == _backs.Count) _backs.Add(DurakGame.CardBack(CardW, CardH));
            return _backs[backs++];
        }

        if (_rules.Stock.Count > 0) Put(Spot.Stock, 0, Slot(Spot.Stock), Back());
        if (_rules.Waste.Count > 0) Put(Spot.Waste, 1, Slot(Spot.Waste), Face(_rules.Waste[^1]));
        for (int f = 0; f < Suits; f++)
            if (_rules.Foundation(f).Count > 0) Put(Spot.Foundation(f), 1, Slot(Spot.Foundation(f)), Face(_rules.Foundation(f)[^1]));

        var stock = Slot(Spot.Stock);
        var deck = new Vec2(stock.X + Pad, stock.Y + Pad);
        for (int p = 0; p < Piles; p++)
        {
            var pile = _rules.Tableau(p);
            int hidden = _rules.Hidden(p), up = pile.Count - hidden;
            // squeeze the face-up fan when a long pile would run off the felt
            double room = BoardH - TableauTop - CardH - hidden * FanHidden;
            double fan = up > 1 ? Math.Min(FanUp, room / (up - 1)) : FanUp;
            double y = TableauTop;
            for (int i = 0; i < pile.Count; i++)
            {
                bool faceUp = i >= hidden;
                var spot = Spot.Tableau(p);
                var el = faceUp ? Face(pile[i]) : Back();
                Put(spot, pile.Count - i, new Rect(ColX(p), y, CardW, CardH), el, faceUp);
                if (_dealPending) Glide(el, deck, new Vec2(ColX(p) + Pad, y + Pad), DealDelay(DealIndex(p, i)));
                y += faceUp ? fan : FanHidden;
            }
        }
        if (_dealPending)
        {
            _dealPending = false;
            _dealing = true;
            Anims.After(DealDelay(DealIndex(Piles - 1, Piles - 1)) + GlideTime, () => _dealing = false);
        }

        // the two buttons sit in the gap between the waste and the foundations
        double bx = ColX(2) + (CardW - ButtonW) / 2;
        bool confirming = _confirmNew > 0;
        Button(new Rect(bx, 8, ButtonW, ButtonH), confirming ? L.T("Sure?") : L.T("New deal"), confirming, NewDealClicked);
        if (_rules.CanUndo && !_over) Button(new Rect(bx, 8 + ButtonH + 10, ButtonW, ButtonH), L.T("Undo"), false, UndoClicked);

        foreach (var el in _moving) // a card on the move passes over the rest
        {
            _board.Children.Remove(el);
            _board.Children.Add(el);
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
            _faces[card] = face = DurakGame.CardFace(Suit(card), SolitaireRules.Rank(card), CardW, CardH);
            _faceEls.Add(face);
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
            int suit = i % Suits, rank = Ranks - i / Suits;
            var slot = ToOverlay(Slot(Spot.Foundation(suit)));
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
        foreach (var (rect, click) in _buttons)
            if (rect.Contains(b.ToPoint()))
            {
                click();
                return false;
            }
        if (_over) return false;
        var hit = Top(b);
        if (hit is not { } h) return false;
        if (h.Spot.Zone == Zone.Stock)
        {
            if (_rules.Draw())
            {
                Started();
                Host.Sound.Play("board", 0.25, 1.6);
                Changed();
            }
            return false;
        }
        if (h.Depth <= 0 || h.El == null) return false; // an empty pile or a face-down card
        var els = _layout.Where(q => q.Spot == h.Spot && q.El != null && q.Depth > 0 && q.Depth <= h.Depth)
            .Select(q => (q.El!, q.Rect.X, q.Rect.Y)).ToList();
        foreach (var (el, _, _) in els)
            if (_glides.Remove(el, out var glide)) glide.Cancel(); // a card picked up mid-glide follows the hand instead
        _press = (h.Spot, h.Depth, p, els);
        _dragging = false;
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
        if (_dragging)
        {
            // drop on whichever pile the dragged card's middle is over, or the one under the pointer
            var d = (p - press.At) / _scale;
            var mid = new Vec2(press.Els[0].X + CardW / 2 + d.X, press.Els[0].Y + CardH / 2 + d.Y);
            move = Target(mid, press.Spot, press.Count) ?? Target(ToBoard(p), press.Spot, press.Count);
        }
        else move = _rules.Best(press.Spot, press.Count);
        bool dragged = _dragging;
        _dragging = false;
        if (move is { } m && Play(m)) return;
        if (!dragged) Host.Sound.Play("thunk", 0.2); // a click on a card that fits nowhere
        Render(); // a dropped run glides back to its pile
        Host.Wake();
    }

    Move? Target(Vec2 b, Spot from, int count)
    {
        foreach (var q in _layout.AsEnumerable().Reverse())
        {
            // a pile's whole fan counts, down to a card's length below its top card
            var area = q.Rect;
            if (q.Spot.Zone == Zone.Tableau) area = new Rect(area.X, TableauTop, CardW, Math.Max(area.Bottom - TableauTop, CardH));
            if (!area.Inflate(Gap / 2).Contains(b.ToPoint())) continue;
            if (q.Spot.Zone is not (Zone.Tableau or Zone.Foundation) || q.Spot == from) continue;
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
        if (!_rules.Apply(m)) return false;
        Started();
        if (m.To.Zone == Zone.Foundation)
        {
            if (_rules.OnFoundations > _homeHigh) // undo and redo can't farm the counter
            {
                _homeHigh = _rules.OnFoundations;
                Host.Stats.Add("solitaire.cards");
            }
            var r = ToOverlay(Slot(m.To));
            var at = new Vec2(r.Center.X, r.Center.Y);
            Host.Fx.Burst(at, new[] { Gold, Colors.White }, 8, 180, 300, 4, 0.4);
            Host.Sound.Play("score", 0.3, 0.9 + _rules.Foundation(m.To.Index).Count * 0.03);
            Host.ShareAction(at, 1);
        }
        else Host.Sound.Play("board", 0.3, 1.3);
        if (_rules.Won) Win();
        Changed();
        return true;
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
        if (_rules.Moves > 0 && !_over && _confirmNew <= 0)
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
        Changed();
    }

    void Deal()
    {
        EndRound(); // a deal given up counts with what went home
        _rules = new SolitaireRules(Rng);
        _over = _clockOn = false;
        _elapsed = 0;
        _confirmNew = _finishIn = 0;
        _homeHigh = 0;
        _demoDraws = 0;
        _dealPending = true;
        Host.Sound.Play("whoosh", 0.3);
        Changed();
    }

    void Win()
    {
        _over = true;
        _elapsed = Seconds;
        _clockOn = false;
        long before = Host.Stats.Get("solitaire.best");
        Host.Stats.Add("solitaire.wins");
        Host.Stats.Min("solitaire.best", _rules.Moves);
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
            _origin = _handle.Move(Host.Pointer, new Size(PanelW * _scale, PanelH * _scale));
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
        if (_rules.CanFinish && _press == null && !_dealing)
        {
            if ((_finishIn -= dt) <= 0)
            {
                _finishIn = FinishStep; // the rest go home one after another, each gliding up as the next sets off
                if (_rules.NextHome() is { } m) Play(m);
            }
            busy = true;
        }
        return busy || _confirmNew > 0;
    }

    // ------------------------------------------------------------------ demo

    /// <summary>Plays a plain strategy: home first, then moves that turn up a hidden card, then the waste, then draw.</summary>
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
        if (_rules.CanFinish) return; // it finishes by itself
        var move = DemoMove();
        if (move is { } m)
        {
            _demoDraws = 0;
            Play(m);
            return;
        }
        // stuck: a few trips through the stock with nothing to play means a new deal
        if (++_demoDraws > (_rules.Stock.Count + _rules.Waste.Count + 1) * 2) Deal();
        else if (_rules.Draw())
        {
            Started();
            Changed();
        }
    }

    Move? DemoMove()
    {
        if (_rules.NextHome() is { } home) return home;
        for (int p = 0; p < Piles; p++)
        {
            int n = _rules.Movable(Spot.Tableau(p));
            if (n == 0) continue;
            if (_rules.Hidden(p) > 0 && _rules.Best(Spot.Tableau(p), n) is { To.Zone: Zone.Tableau } m) return m; // turns a card up
        }
        if (_rules.Waste.Count > 0 && _rules.Best(Spot.Waste, 1) is { } w) return w;
        return null;
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
