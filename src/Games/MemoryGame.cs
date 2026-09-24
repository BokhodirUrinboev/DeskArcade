using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>
/// Memory (pairs): 24 cards lie face down over the desktop. Click a card to flip it, then a second one: a pair
/// stays face up, a mismatch turns back after a moment. Each two flips are one move; find all 12 pairs in as
/// few moves as you can. The clock starts with the first flip. The grip above the cards (or a right-drag)
/// moves them. A deal is a round: over the LAN, or against the computer, the fewer moves win. Only the cards
/// and the grip take the mouse.
/// </summary>
public sealed class MemoryGame : MiniGame
{
    const int Cols = 6, Rows = 4, Faces = 12;
    const double CardW = 70, CardH = 90, Gap = 12, Corner = 7, MismatchPause = 0.8;
    const double BoardW = Cols * CardW + (Cols - 1) * Gap, BoardH = Rows * CardH + (Rows - 1) * Gap;
    const double RememberChance = 0.7; // the demo player's memory: how often a seen card sticks
    public const double FlipTime = 0.24, DealTime = 0.32, DealStagger = 0.035, HopTime = 0.45;

    static readonly Color Gold = Color.FromRgb(255, 209, 102);
    static readonly Color Paper = Color.FromRgb(246, 243, 235);
    static readonly IBrush Ink = Art.Brush("#2A2F3A");

    sealed class Card
    {
        public required Canvas El, Back, Face;
        public required Rectangle Glow;
        public required ScaleTransform Turn, Size;
        public required TranslateTransform Move;
        public Vec2 Center;
        public int DrawnFace = -1;
        public bool Up, ShowingFace;
        public double Progress; // through the flip under way, 0 → 1, the side swapping halfway
        public Anims.Tween? Flip;
    }

    readonly Card[] _cards = new Card[Cols * Rows];
    readonly Dictionary<int, int> _seen = new(); // demo: card → face it remembers
    readonly DragHandle _handle;
    MemoryRules _rules = new(Rng, Faces);
    Anims.Tween? _mismatch;
    Vec2 _origin;
    double _scale = 1, _elapsed;
    long _clockFrom;
    bool _placed, _dealt, _dealing, _started, _clockOn, _over, _racing, _drawnColorBlind;
    int _demoWait, _demoIdle;

    public MemoryGame(IGameHost host) : base(host)
    {
        for (int i = 0; i < _cards.Length; i++)
        {
            var turn = new ScaleTransform(1, 1);
            var size = new ScaleTransform(1, 1);
            var move = new TranslateTransform();
            var card = new Card
            {
                El = new Canvas { IsHitTestVisible = false, RenderTransformOrigin = RelativePoint.TopLeft, RenderTransform = new TransformGroup { Children = { turn, size, move } } },
                Back = new Canvas { IsHitTestVisible = false },
                Face = new Canvas { IsHitTestVisible = false, IsVisible = false },
                Glow = Box(-CardW / 2 + 1.5, -CardH / 2 + 1.5, CardW - 3, CardH - 3, Corner - 1, null, Art.Brush(Gold), 3),
                Turn = turn, Size = size, Move = move,
            };
            DrawBack(card.Back);
            card.El.Children.Add(card.Back);
            card.El.Children.Add(card.Face);
            _cards[i] = card;
            Layer.Children.Add(card.El);
        }
        _handle = new DragHandle(host, Id, Title);
        Layer.Children.Add(_handle.Visual);
    }

    public override string Id => "memory";
    public override string Title => "Memory";

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        var back = Themes.Current.Mine;
        s.Rotor.Children.Add(Box(-10, -8, 11, 15, 2, Art.Brush(Art.Blend(back, Colors.Black, 0.5)), Art.Brush(back), 1.2));
        s.Rotor.Children.Add(Box(-1, -6, 11, 15, 2, Art.Brush(Paper), Ink, 1));
        var heart = Geometry.Parse(HeartPath);
        heart.Transform = new MatrixTransform(Matrix.CreateScale(0.2, 0.2) * Matrix.CreateTranslation(4.5, 1.5)); // in geometry space, no origin to mind
        s.Rotor.Children.Add(new Path { Data = heart, Fill = Art.Brush(Color.FromRgb(236, 72, 153)) });
        return s;
    }

    public override HudInfo Hud
    {
        get
        {
            long best = Host.Stats.Get("memory.best");
            return new HudInfo(
                $"{_rules.Found}/{_rules.Pairs}",
                _over ? L.F("All pairs in {0} moves · click a card to deal again", _rules.Moves)
                    : _started ? L.F("Moves {0} · pairs left {1}", _rules.Moves, _rules.Pairs - _rules.Found)
                    : L.T("Click a card to flip it · find all the pairs · right-drag moves the cards"),
                best > 0 ? L.F("Fewest moves {0}", best) : L.T("Best —"));
        }
    }

    // ------------------------------------------------------------------ flips, worked out ahead

    /// <summary>How wide a card looks partway through a flip: it squeezes to an edge halfway and opens again.</summary>
    public static double FlipWidth(double progress) => Math.Max(0.02, Math.Abs(Math.Cos(Math.PI * progress)));

    /// <summary>Whether the far side is the one showing at this point of a flip.</summary>
    public static bool FlipShowsFarSide(double progress) => progress >= 0.5;

    /// <summary>
    /// The plan for a flip: from the start, or, for a card turned back partway through one (<paramref name="inFlight"/>
    /// is how far it got), from the point that keeps its width, so it carries on rather than jumping, and only
    /// takes the time it had already spent.
    /// </summary>
    public static (double Start, double Seconds) FlipPlan(double? inFlight)
    {
        if (inFlight is not { } p) return (0, FlipTime);
        p = Math.Clamp(p, 0, 1);
        return (1 - p, FlipTime * p);
    }

    /// <summary>When each card of a new deal leaves the deck, so the grid deals in card by card.</summary>
    public static double DealDelay(int card) => card * DealStagger;

    // ------------------------------------------------------------------ races

    public override bool SupportsLan => true;
    public override (int Score, bool Active)? Race => (_rules.Moves, _racing);
    public override bool RaceLowerIsBetter => true;
    public override int RaceBaseline => 22;
    public override int RaceBest => (int)Host.Stats.Get("memory.best");
    public override double RaceSeconds => 75;

    /// <summary>The rival's round began: a fresh deal, unless this one is untouched, and the round is on.</summary>
    public override void StartRace()
    {
        if (_racing) return;
        if (_started || _over) Deal();
        BeginRound();
    }

    void BeginRound()
    {
        _racing = true;
        Host.RoundStarted();
        Host.HudChanged();
    }

    // ------------------------------------------------------------------ layout

    public override void Layout()
    {
        var a = Host.Arena;
        _scale = Clamp(Math.Min(a.Width * 0.32 / BoardW, a.Height * 0.5 / BoardH), 0.55, 1.6);
        _scale = Math.Min(_scale, Math.Min((a.Width - 20) / BoardW, (a.Height - 20) / BoardH)); // tiny arenas
        double w = BoardW * _scale, h = BoardH * _scale;
        if (!_placed)
        {
            _placed = true;
            _origin = _handle.Saved() ?? PlaceBoard(w, h);
        }
        _origin = Fit(_origin, w, h);
        Place();
        if (_drawnColorBlind != Art.ColorBlind) // the overlay calls Layout when colour-blind mode is toggled
        {
            _drawnColorBlind = Art.ColorBlind;
            foreach (var c in _cards)
                if (c.DrawnFace >= 0) DrawFace(c, c.DrawnFace);
        }
        if (!_dealt)
        {
            _dealt = true;
            DealIn();
        }
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
            Fit(new Vec2(mid.X - w / 2, mid.Y - h / 2), w, h), Fit(new Vec2(mid.X - w / 2, hud.Bottom), w, h), Fit(new Vec2(mid.X - w / 2, hud.Top - h), w, h),
            Fit(new Vec2(hud.Right, mid.Y - h / 2), w, h), Fit(new Vec2(hud.Left - w, mid.Y - h / 2), w, h),
        };
        foreach (var t in tries)
            if (!new Rect(t.X, t.Y, w, h).Intersects(hud)) return t;
        return tries[0];
    }

    Vec2 Fit(Vec2 o, double w, double h)
    {
        var a = Host.Arena;
        return new Vec2(
            Clamp(o.X, a.Left + 10, Math.Max(a.Left + 10, a.Right - w - 10)),
            Clamp(o.Y, a.Top + 10, Math.Max(a.Top + 10, a.Bottom - h - 10)));
    }

    /// <summary>Puts every card at its place in the grid and the grip above it.</summary>
    void Place()
    {
        for (int i = 0; i < _cards.Length; i++)
        {
            var c = _cards[i];
            c.Center = _origin + new Vec2((i % Cols * (CardW + Gap) + CardW / 2) * _scale, (i / Cols * (CardH + Gap) + CardH / 2) * _scale);
            c.Size.ScaleX = c.Size.ScaleY = _scale;
            c.Move.X = c.Center.X;
            c.Move.Y = c.Center.Y;
        }
        _handle.Show(new Rect(_origin.X, _origin.Y, BoardW * _scale, BoardH * _scale));
    }

    public override void PositionsReset() => _placed = false;

    public override void Activate()
    {
        if (_clockOn) _clockFrom = Environment.TickCount64;
        Layout();
    }

    public override void Deactivate()
    {
        if (_clockOn) _elapsed += (Environment.TickCount64 - _clockFrom) / 1000.0; // the clock pauses while away
        _handle.Cancel();
        if (_rules.Mismatch != null)
        {
            _mismatch?.Cancel();
            _mismatch = null;
            _rules.Settle();
        }
        Sync();
        Anims.Finish();
    }

    public override void Summon(Vec2 p)
    {
        _origin = p - new Vec2(BoardW * _scale / 2, BoardH * _scale / 2);
        Layout();
        _handle.Save(_origin);
    }

    double Seconds => _elapsed + (_clockOn ? (Environment.TickCount64 - _clockFrom) / 1000.0 : 0);

    int CardAt(Vec2 p)
    {
        double x = (p.X - _origin.X) / _scale, y = (p.Y - _origin.Y) / _scale;
        int col = (int)Math.Floor(x / (CardW + Gap)), row = (int)Math.Floor(y / (CardH + Gap));
        if (col < 0 || col >= Cols || row < 0 || row >= Rows) return -1;
        bool inCard = x - col * (CardW + Gap) <= CardW && y - row * (CardH + Gap) <= CardH; // the gaps stay click-through
        return inCard ? row * Cols + col : -1;
    }

    // ------------------------------------------------------------------ input

    public override void CollectHitShapes(List<HitShape> into)
    {
        double hw = CardW * _scale / 2, hh = CardH * _scale / 2;
        foreach (var c in _cards) into.Add(HitShape.Box(new Rect(c.Center.X - hw, c.Center.Y - hh, hw * 2, hh * 2)));
        into.Add(_handle.Hit);
    }

    public override bool PointerDown(Vec2 p, bool right)
    {
        if (right || _handle.Contains(p))
        {
            _handle.Begin(p, _origin, anywhere: true); // the grip, or a right-drag on the cards
            return true;
        }
        int i = CardAt(p);
        if (i < 0 || _dealing) return false;
        if (_over)
        {
            Deal();
            return false;
        }
        if (_rules.Mismatch != null) // a click during the pause turns the mismatch back at once
        {
            _mismatch?.Cancel();
            _mismatch = null;
            _rules.Settle();
        }
        FlipCard(i);
        Sync();
        return false;
    }

    public override void PointerUp(Vec2 p) => _handle.End(_origin);

    public override void PointerCancel() => _handle.Cancel();

    void FlipCard(int i)
    {
        int first = _rules.Open;
        var result = _rules.Flip(i);
        if (result == MemoryRules.FlipResult.Ignored) return;
        if (!_started)
        {
            _started = _clockOn = true;
            _elapsed = 0;
            _clockFrom = Environment.TickCount64;
            if (!_racing) BeginRound(); // the first flip starts a round
        }
        Host.Sound.Play("board", 0.3, 1.5 + Rng.NextDouble() * 0.2);
        switch (result)
        {
            case MemoryRules.FlipResult.Match:
                Host.Stats.Add("memory.pairs");
                Host.Sound.Play("score", 0.45, 1.1);
                Host.ShareAction(_cards[i].Center, 1);
                var face = Tint(_rules.FaceOf(i));
                foreach (int k in new[] { first, i })
                {
                    Host.Fx.Burst(_cards[k].Center, new[] { Gold, face, Colors.White }, 10, 220, 300, 4, 0.5);
                    Hop(_cards[k]);
                }
                if (_rules.Won) Win();
                break;
            case MemoryRules.FlipResult.Mismatch:
                _mismatch = Anims.After(MismatchPause + FlipTime, SettleMismatch);
                break;
        }
        Host.HudChanged();
    }

    void SettleMismatch()
    {
        _mismatch = null;
        if (!_rules.Settle()) return;
        Host.Sound.Play("board", 0.2, 1.1);
        Sync();
    }

    /// <summary>A matched card, once its flip has landed, hops up a little and its gold edge lights up.</summary>
    void Hop(Card c)
    {
        c.Glow.Opacity = 0;
        Anims.Add(HopTime, k =>
        {
            double p = Ease.Pulse(k);
            c.Move.Y = c.Center.Y - 12 * p;
            c.Size.ScaleX = c.Size.ScaleY = _scale * (1 + 0.1 * p);
            c.Glow.Opacity = Math.Min(1, k * 2);
        }, Ease.Linear, null, FlipTime);
    }

    void Win()
    {
        _over = true;
        _clockOn = false;
        _elapsed = Seconds;
        int moves = _rules.Moves, secs = (int)Math.Round(_elapsed);
        long before = Host.Stats.Get("memory.best");
        Host.Stats.Add("memory.wins");
        Host.Stats.Min("memory.best", moves);
        if (_racing)
        {
            _racing = false;
            Host.RoundEnded(moves);
        }
        var at = new Vec2(_origin.X + BoardW * _scale / 2, _origin.Y + BoardH * _scale * 0.35);
        bool best = before == 0 || moves < before;
        Host.Fx.Popup(at, best ? L.T("NEW BEST!") : L.T("ALL PAIRS!"), Gold, 40, 2.6, L.F("{0} moves · {1}s", moves, secs));
        Host.Fx.Burst(at, Themes.Current.Confetti, 44, 540, 700, 7, 1.1);
        Host.Sound.Play("best", 0.8);
    }

    void Deal()
    {
        Anims.Clear(); // flips, hops and the mismatch pause of the old deal
        _mismatch = null;
        _rules = new MemoryRules(Rng, Faces);
        _over = _started = _clockOn = _racing = _dealing = false;
        _elapsed = 0;
        _seen.Clear();
        for (int i = 0; i < _cards.Length; i++)
        {
            var c = _cards[i];
            c.Flip = null;
            c.Progress = 0;
            c.Turn.ScaleX = 1;
            c.Up = false;
            c.Glow.IsVisible = false;
            ShowSide(i);
        }
        Place();
        Host.Sound.Play("whoosh", 0.3);
        DealIn();
        Host.HudChanged();
    }

    /// <summary>The cards fly out of a deck on the first card's spot to their places, one after another.</summary>
    void DealIn()
    {
        _dealing = true;
        for (int i = 0; i < _cards.Length; i++)
        {
            var c = _cards[i];
            bool last = i == _cards.Length - 1;
            c.El.Opacity = 0;
            Anims.Add(DealTime, k =>
            {
                var deck = _cards[0].Center;
                c.Move.X = deck.X + (c.Center.X - deck.X) * k;
                c.Move.Y = deck.Y + (c.Center.Y - deck.Y) * k;
                c.El.Opacity = Math.Min(1, k * 2);
            }, Ease.OutCubic, last ? () => _dealing = false : null, DealDelay(i));
        }
        Host.Wake();
    }

    /// <summary>Starts a flip on every card whose side no longer matches the rules.</summary>
    void Sync()
    {
        for (int i = 0; i < _cards.Length; i++)
        {
            var c = _cards[i];
            var state = _rules.StateOf(i);
            c.Glow.IsVisible = state == MemoryRules.Card.Matched;
            bool up = state != MemoryRules.Card.Down;
            if (up == c.Up) continue;
            c.Up = up;
            StartFlip(i);
        }
        Host.Wake();
    }

    void StartFlip(int i)
    {
        var c = _cards[i];
        var (start, seconds) = FlipPlan(c.Flip == null ? null : c.Progress); // turning back mid-flip carries on from the same width
        c.Flip?.Cancel();
        c.Flip = Anims.Add(seconds, k =>
        {
            c.Progress = start + (1 - start) * k;
            if (FlipShowsFarSide(c.Progress)) ShowSide(i);
            c.Turn.ScaleX = FlipWidth(c.Progress);
        }, Ease.Linear, () =>
        {
            c.Flip = null;
            c.Progress = 0;
            c.Turn.ScaleX = 1;
            ShowSide(i);
        });
    }

    void ShowSide(int i)
    {
        var c = _cards[i];
        // a new deal draws a face when it is next turned up; checked even if the face side never went out of
        // view (a click right after the deal reverses a card before it shows its back)
        if (c.Up && c.DrawnFace != _rules.FaceOf(i)) DrawFace(c, _rules.FaceOf(i));
        if (c.ShowingFace == c.Up) return;
        c.ShowingFace = c.Up;
        c.Back.IsVisible = !c.Up;
        c.Face.IsVisible = c.Up;
    }

    public override bool Update(double dt)
    {
        if (_handle.Dragging)
        {
            _origin = _handle.Move(Host.Pointer, new Size(BoardW * _scale, BoardH * _scale));
            Place();
        }
        return Anims.Update(dt) || _handle.Dragging;
    }

    public override void ThemeChanged()
    {
        foreach (var c in _cards) DrawBack(c.Back);
    }

    // ------------------------------------------------------------------ demo

    /// <summary>Plays like a person with a decent but leaky memory: it forgets some of the cards it has seen.</summary>
    public override void DemoTick()
    {
        if (_dealing || _demoWait-- > 0) return;
        _demoWait = 2 + Rng.Next(4);
        if (_over)
        {
            if (++_demoIdle > 6) PointerDown(_cards[0].Center, false);
            return;
        }
        _demoIdle = 0;
        if (_rules.Mismatch != null) return; // let it turn back, as a person would
        int pick = DemoPick();
        if (pick < 0) return;
        PointerDown(_cards[pick].Center, false);
        foreach (int k in _seen.Keys.Where(k => _rules.StateOf(k) == MemoryRules.Card.Matched).ToList()) _seen.Remove(k);
        if (_rules.StateOf(pick) == MemoryRules.Card.Up && Rng.NextDouble() < RememberChance) _seen[pick] = _rules.FaceOf(pick);
    }

    int DemoPick()
    {
        var down = Enumerable.Range(0, _cards.Length).Where(k => _rules.StateOf(k) == MemoryRules.Card.Down).ToList();
        if (down.Count == 0) return -1;
        int open = _rules.Open;
        if (open >= 0)
        {
            int face = _rules.FaceOf(open);
            foreach (int k in down)
                if (_seen.TryGetValue(k, out int f) && f == face) return k;
        }
        else
        {
            foreach (int k in down)
                foreach (int m in down)
                    if (k < m && _seen.TryGetValue(k, out int fk) && _seen.TryGetValue(m, out int fm) && fk == fm) return k;
        }
        var unknown = down.Where(k => !_seen.ContainsKey(k)).ToList();
        var from = unknown.Count > 0 ? unknown : down;
        return from[Rng.Next(from.Count)];
    }

    // ------------------------------------------------------------------ visuals

    /// <summary>The patterned back, in the theme's own colour.</summary>
    static void DrawBack(Canvas into)
    {
        into.Children.Clear();
        var mine = Themes.Current.Mine;
        into.Children.Add(Box(-CardW / 2, -CardH / 2, CardW, CardH, Corner, Art.Brush(Art.Blend(mine, Colors.Black, 0.55)), Art.Brush(Art.Blend(mine, Colors.Black, 0.75)), 1.5));
        var lattice = new StringBuilder();
        for (double d = -CardH; d <= CardW; d += 10)
        {
            lattice.Append($"M{Art.F(-CardW / 2 + d)},{Art.F(-CardH / 2)} l{Art.F(CardH)},{Art.F(CardH)} ");
            lattice.Append($"M{Art.F(CardW / 2 - d)},{Art.F(-CardH / 2)} l{Art.F(-CardH)},{Art.F(CardH)} ");
        }
        var lines = Art.PathOf(lattice.ToString(), null, Art.Brush(Color.FromArgb(90, mine.R, mine.G, mine.B)), 1.2);
        lines.Clip = new RectangleGeometry(new Rect(-CardW / 2 + 6, -CardH / 2 + 6, CardW - 12, CardH - 12));
        into.Children.Add(lines);
        into.Children.Add(Box(-CardW / 2 + 5, -CardH / 2 + 5, CardW - 10, CardH - 10, Corner - 3, null, Art.Brush(Color.FromArgb(200, mine.R, mine.G, mine.B)), 1.5));
        into.Children.Add(Art.PathOf("M0,-12 L9,0 L0,12 L-9,0 Z", Art.Brush(Art.Blend(mine, Colors.Black, 0.55)), Art.Brush(mine), 2));
    }

    void DrawFace(Card c, int face)
    {
        c.DrawnFace = face;
        var into = c.Face;
        into.Children.Clear();
        into.Children.Add(Box(-CardW / 2, -CardH / 2, CardW, CardH, Corner, Art.Brush(Paper), Art.Brush("#C9C2B0"), 1.5));
        DrawPicture(into, face);
        into.Children.Add(c.Glow);
    }

    const string HeartPath = "M0,20 C-26,2 -22,-20 -8,-18 C-3,-17 0,-12 0,-9 C0,-12 3,-17 8,-18 C22,-20 26,2 0,20 Z";

    /// <summary>The main colour of each face, for the match burst.</summary>
    static Color Tint(int face) => face switch
    {
        0 => Art.Safe(Color.FromRgb(214, 48, 49)),
        1 => Color.FromRgb(255, 176, 0),
        2 => Color.FromRgb(236, 72, 153),
        3 => Color.FromRgb(40, 44, 56),
        4 => Art.Safe(Color.FromRgb(46, 160, 90)),
        5 => Color.FromRgb(0, 160, 170),
        6 => Colors.White,
        7 => Color.FromRgb(29, 111, 196),
        8 => Color.FromRgb(170, 185, 215),
        9 => Color.FromRgb(255, 221, 0),
        10 => Color.FromRgb(160, 118, 84),
        _ => Color.FromRgb(150, 80, 210),
    };

    /// <summary>
    /// Twelve pictures that differ by shape first (colour only helps): apple, star, heart, spade, club, diamond,
    /// ball, fish, moon, lightning, cat and flower. Drawn around (0, 0), about 48 px across.
    /// </summary>
    static void DrawPicture(Canvas into, int face)
    {
        var fill = Art.Brush(Tint(face));
        var edge = Art.Brush(Art.Blend(Tint(face), Colors.Black, 0.45));
        switch (face)
        {
            case 0: // apple
                into.Children.Add(Art.PathOf("M0,-12 C-8,-20 -24,-16 -22,0 C-20,16 -8,24 0,18 C8,24 20,16 22,0 C24,-16 8,-20 0,-12 Z", fill, edge, 1.5));
                into.Children.Add(Art.PathOf("M0,-12 Q1,-20 4,-24", null, Art.Brush("#6B3E1E"), 2.5));
                into.Children.Add(Art.PathOf("M3,-18 Q12,-27 17,-19 Q9,-14 3,-18 Z", Art.Brush(Art.Safe(Color.FromRgb(60, 170, 80)))));
                into.Children.Add(Art.At(new Ellipse { Width = 6, Height = 9, Fill = Art.Brush(110, 255, 255, 255) }, -14, -8));
                break;
            case 1: // star
                into.Children.Add(Art.PathOf(Art.StarPath(0, 2, 24, 10), fill, edge, 1.5));
                break;
            case 2: // heart
                into.Children.Add(Art.PathOf(HeartPath, fill, edge, 1.5));
                break;
            case 3: // spade
                into.Children.Add(Art.PathOf("M0,-22 C-24,-4 -22,14 -8,12 C-4,11 -2,8 -1,6 L-6,20 L6,20 L1,6 C2,8 4,11 8,12 C22,14 24,-4 0,-22 Z", fill));
                break;
            case 4: // club
                foreach (var (x, y) in new[] { (0.0, -10.0), (-10.0, 3.0), (10.0, 3.0) }) into.Children.Add(Art.Circle(x, y, 8.5, fill));
                into.Children.Add(Art.Circle(0, 0, 5, fill));
                into.Children.Add(Art.PathOf("M-2,2 L-6,20 L6,20 L2,2 Z", fill));
                break;
            case 5: // diamond
                into.Children.Add(Art.PathOf("M0,-24 L17,0 L0,24 L-17,0 Z", fill, edge, 1.5));
                break;
            case 6: // ball
                var ball = Art.SoccerBall(19);
                ball.IsHitTestVisible = false;
                into.Children.Add(ball);
                break;
            case 7: // fish
                into.Children.Add(Art.PathOf("M-13,0 L-25,-11 L-22,0 L-25,11 Z", fill, edge, 1.2));
                into.Children.Add(Art.PathOf("M-16,0 C-8,-15 12,-15 20,0 C12,15 -8,15 -16,0 Z", fill, edge, 1.5));
                into.Children.Add(Art.Circle(10, -3, 3, Brushes.White));
                into.Children.Add(Art.Circle(10.8, -3, 1.5, Ink));
                into.Children.Add(Art.PathOf("M-2,-8 Q3,0 -2,8", null, edge, 1.5));
                break;
            case 8: // moon
                into.Children.Add(Art.PathOf("M4,-21.6 A22,22 0 1 0 4,21.6 A26,26 0 0 1 4,-21.6 Z", fill, edge, 1.5));
                break;
            case 9: // lightning
                into.Children.Add(Art.PathOf("M5,-25 L-13,4 L-1,4 L-6,25 L13,-6 L1,-6 Z", fill, Art.Brush("#8A6D00"), 1.5));
                break;
            case 10: // cat
                into.Children.Add(Art.PathOf("M-16,-2 L-14,-22 L-3,-12 Z M16,-2 L14,-22 L3,-12 Z", fill, edge, 1.5));
                into.Children.Add(Art.Circle(0, 3, 17, fill, edge, 1.5));
                into.Children.Add(Art.At(new Ellipse { Width = 5, Height = 7, Fill = Ink }, -9, -3));
                into.Children.Add(Art.At(new Ellipse { Width = 5, Height = 7, Fill = Ink }, 4, -3));
                into.Children.Add(Art.PathOf("M-2.5,6 L2.5,6 L0,9 Z", Art.Brush("#F28DB2")));
                into.Children.Add(Art.PathOf("M-4,10 L-15,8 M-4,11 L-15,13 M4,10 L15,8 M4,11 L15,13", null, Ink, 1));
                break;
            default: // flower
                for (int k = 0; k < 5; k++)
                {
                    var (x, y) = Art.Polar(11, -90 + 72 * k);
                    into.Children.Add(Art.Circle(x, y, 9, fill, edge, 1.2));
                }
                into.Children.Add(Art.Circle(0, 0, 7, Art.Brush("#FFD23F"), Art.Brush("#B7791F"), 1.2));
                break;
        }
    }

    static Rectangle Box(double x, double y, double w, double h, double r, IBrush? fill, IBrush? stroke, double thick) =>
        Art.At(new Rectangle { Width = w, Height = h, RadiusX = r, RadiusY = r, Fill = fill, Stroke = stroke, StrokeThickness = thick, IsHitTestVisible = false }, x, y);
}
