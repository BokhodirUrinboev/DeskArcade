using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>
/// Code Breaker (Mastermind): crack a hidden code of four pegs in six colours in ten guesses. Pick a colour in
/// the palette and click a hole in the current row (or keep clicking a hole to cycle its colour; right-click
/// empties it), then Check. A black pin is a right colour in the right place, a white pin a right colour in the
/// wrong place. Every colour also carries a symbol, so the pegs read without colour. The grip above the board
/// (or a right-drag off the holes) moves it. A code is a round: over the LAN, or against the computer, the
/// fewer guesses win, and a code that gets away counts as one more than the board holds. Only the board and
/// the grip take the mouse.
/// </summary>
public sealed class CodeBreakerGame : MiniGame
{
    const int Pegs = CodeBreakerRules.Pegs, Colours = CodeBreakerRules.Colors, MaxGuesses = CodeBreakerRules.MaxGuesses;
    // the panel in its own units, scaled to the arena as a whole
    const double W = 220, Pad = 14, CodeH = 36, RowH = 32, HoleR = 10.5, PinR = 3.4, PegR = 11.5;
    const double RowsTop = Pad + CodeH + 10, PaletteY = RowsTop + MaxGuesses * RowH + 10 + 20, ButtonTop = PaletteY + 26, ButtonH = 30;
    const double H = ButtonTop + ButtonH + Pad, FeedbackX = 184, PaletteStep = 32;
    const double RippleTime = 0.3;
    public const double DropTime = 0.32, PinTime = 0.25, PinLead = 0.15, PinStagger = 0.1, RevealTime = 0.4, RevealLead = 0.3, RevealStagger = 0.12;
    const int FastGuesses = 4; // "Mind reader" counts codes broken in this many guesses or fewer

    static readonly Color Gold = Color.FromRgb(255, 209, 102);
    static readonly Color[] PegColors =
    {
        Color.FromRgb(230, 57, 70), Color.FromRgb(46, 170, 90), Color.FromRgb(40, 110, 220),
        Color.FromRgb(255, 205, 40), Color.FromRgb(150, 80, 210), Color.FromRgb(240, 240, 240),
    };
    static readonly IBrush DarkInk = Art.Brush(200, 20, 24, 34), LightInk = Art.Brush(230, 255, 255, 255);

    readonly Canvas _board = new() { IsHitTestVisible = false, RenderTransformOrigin = RelativePoint.TopLeft };
    readonly ScaleTransform _size = new(1, 1);
    readonly TranslateTransform _move = new();
    readonly int[] _row = { -1, -1, -1, -1 };
    // a ring that spreads from the peg or pins just touched: feedback, and it marks the frames as play time
    readonly Ellipse _ripple = new() { Stroke = Art.Brush(Gold), StrokeThickness = 2, IsHitTestVisible = false, IsVisible = false };
    // the pegs, pins and caps on the board right now, by place, and where each sits: the animations move them
    readonly Dictionary<string, (Sprite Sprite, Vec2 At)> _sprites = new();
    readonly HashSet<string> _pending = new(); // pins whose pop hasn't begun: drawn tiny until it does
    readonly double[] _reveal = new double[Pegs]; // each code slot's flip, 0 → 1, while the code is revealed
    readonly DragHandle _handle;
    Anims.Tween? _rippleTween;
    Vec2 _rippleAt;
    CodeBreakerRules _rules = new(Rng);
    Vec2 _origin;
    double _scale = 1;
    int _selected = -1;
    bool _placed, _racing, _revealing;

    // demo: the solver thinks in its own colour order, mapped through _perm so its openings vary
    CodeBreakerSolver? _solver;
    readonly int[] _perm = new int[Colours];
    int _solverSeen, _demoWait, _demoIdle;
    int[]? _plan;

    public CodeBreakerGame(IGameHost host) : base(host)
    {
        _board.RenderTransform = new TransformGroup { Children = { _size, _move } };
        Layer.Children.Add(_board);
        _handle = new DragHandle(host, Id, Title);
        Layer.Children.Add(_handle.Visual);
        Draw();
    }

    public override string Id => "codebreaker";
    public override string Title => "Code Breaker";

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        s.Rotor.Children.Add(new Rectangle { Width = 22, Height = 18, RadiusX = 3, RadiusY = 3, Fill = Art.Brush(235, 22, 28, 40), Stroke = Art.Brush("#5A6275"), StrokeThickness = 1, [Canvas.LeftProperty] = -11.0, [Canvas.TopProperty] = -9.0 });
        for (int i = 0; i < 3; i++) s.Rotor.Children.Add(Art.Circle(-6 + i * 5.5, -2.5, 2.6, Art.Brush(Art.Safe(PegColors[i]))));
        s.Rotor.Children.Add(Art.Circle(-3, 4.5, 1.6, Art.Brush("#111"), Brushes.White, 0.8));
        s.Rotor.Children.Add(Art.Circle(1.5, 4.5, 1.6, Brushes.White));
        return s;
    }

    bool RowFull => Array.IndexOf(_row, -1) < 0;
    bool RowEmpty => Array.TrueForAll(_row, c => c < 0);
    int Guess => _rules.Guesses.Count;

    public override HudInfo Hud
    {
        get
        {
            long best = Host.Stats.Get("codebreaker.best");
            return new HudInfo(
                $"{Guess}/{MaxGuesses}",
                _rules.Won ? L.T("Code cracked · click the board for a new code")
                    : _rules.Lost ? L.T("Out of guesses · click the board for a new code")
                    : RowFull ? L.F("Guess {0}/{1} · click Check", Guess + 1, MaxGuesses)
                    : L.F("Guess {0}/{1} · pick a colour, then click a hole", Guess + 1, MaxGuesses),
                best > 0 ? L.F("Fewest guesses {0}", best) : L.T("Best —"));
        }
    }

    // ------------------------------------------------------------------ worked out ahead

    /// <summary>What a round counts: the guesses it took, or one more than the board holds for a code that got away.</summary>
    public static int RaceScoreFor(int guesses, bool lost) => lost ? MaxGuesses + 1 : guesses;

    /// <summary>When the k-th feedback pin pops in after Check: one by one, the first a beat after the row lands.</summary>
    public static double PinDelay(int k) => PinLead + PinStagger * k;

    /// <summary>
    /// A code slot partway through its reveal: the cap turns edge-on over the first half and the peg turns out
    /// over the second, each width 0 → 1, and only one of them ever shows.
    /// </summary>
    public static (double Cap, double Peg) RevealWidths(double progress) =>
        progress < 0.5 ? (Math.Cos(Math.PI * progress), 0) : (0, -Math.Cos(Math.PI * progress));

    // ------------------------------------------------------------------ races

    public override bool SupportsLan => true;
    public override (int Score, bool Active)? Race => (RaceScoreFor(Guess, _rules.Lost), _racing);
    public override bool RaceLowerIsBetter => true;
    public override int RaceBaseline => 5;
    public override int RaceBest => (int)Host.Stats.Get("codebreaker.best");
    public override double RaceSeconds => 60;

    /// <summary>The rival's round began: a fresh code, unless this one is untouched, and the round is on.</summary>
    public override void StartRace()
    {
        if (_racing) return;
        if (Guess > 0 || _rules.Over || !RowEmpty) NewCode();
        BeginRound();
    }

    void BeginRound()
    {
        _racing = true;
        Host.RoundStarted();
        Host.HudChanged();
    }

    void EndRound()
    {
        if (!_racing) return;
        _racing = false;
        Host.RoundEnded(RaceScoreFor(Guess, _rules.Lost));
    }

    // ------------------------------------------------------------------ layout

    public override void Layout()
    {
        var a = Host.Arena;
        _scale = Clamp(a.Height * 0.6 / H, 0.75, 2);
        _scale = Math.Min(_scale, Math.Min((a.Height - 20) / H, (a.Width - 20) / W)); // tiny arenas
        double w = W * _scale, h = H * _scale;
        if (!_placed)
        {
            _placed = true;
            _origin = _handle.Saved() ?? PlacePanel(w, h);
        }
        _origin = Fit(_origin, w, h);
        Place();
        Draw(); // the overlay calls Layout when colour-blind mode is toggled
        Host.HudChanged();
    }

    /// <summary>The panel's top-left: centered, else beside the HUD; always inside the arena.</summary>
    Vec2 PlacePanel(double w, double h)
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

    /// <summary>Puts the board at its origin and size, and the grip above it.</summary>
    void Place()
    {
        _size.ScaleX = _size.ScaleY = _scale;
        _move.X = _origin.X;
        _move.Y = _origin.Y;
        _handle.Show(Panel);
    }

    public override void PositionsReset() => _placed = false;

    public override void Summon(Vec2 p)
    {
        _origin = p - new Vec2(W * _scale / 2, H * _scale / 2);
        Layout();
        _handle.Save(_origin);
    }

    public override void Deactivate()
    {
        _handle.Cancel();
        Anims.Finish();
    }

    Rect Panel => new(_origin.X, _origin.Y, W * _scale, H * _scale);

    static double HoleX(int i) => 48 + 33 * i;

    /// <summary>Guess 0 sits at the bottom, as on the real board.</summary>
    static double RowY(int guess) => RowsTop + (MaxGuesses - 1 - guess) * RowH + RowH / 2;

    static double PaletteX(int c) => W / 2 + (c - (Colours - 1) / 2.0) * PaletteStep;

    static Rect Button => new(Pad, ButtonTop, W - 2 * Pad, ButtonH);

    // ------------------------------------------------------------------ input

    public override void CollectHitShapes(List<HitShape> into)
    {
        into.Add(HitShape.Box(Panel));
        into.Add(_handle.Hit);
    }

    public override bool PointerDown(Vec2 p, bool right)
    {
        if (_handle.Contains(p))
        {
            _handle.Begin(p, _origin, anywhere: true);
            return true;
        }
        var q = (p - _origin) / _scale;
        if (_rules.Over)
        {
            if (right)
            {
                _handle.Begin(p, _origin, anywhere: true);
                return true;
            }
            NewCode();
            return false;
        }
        if (right)
        {
            // a right-click on a hole of the row being filled empties it; a right-drag anywhere else moves the board
            for (int i = 0; i < Pegs; i++)
                if ((q - new Vec2(HoleX(i), RowY(Guess))).Length <= HoleR + 5)
                {
                    ClickHole(i, true);
                    return false;
                }
            _handle.Begin(p, _origin, anywhere: true);
            return true;
        }
        for (int c = 0; c < Colours; c++)
            if ((q - new Vec2(PaletteX(c), PaletteY)).Length <= PegR + 4)
            {
                _selected = c;
                Ripple(PaletteX(c), PaletteY);
                Host.Sound.Play("board", 0.2, 1.8);
                Changed();
                return false;
            }
        for (int i = 0; i < Pegs; i++)
            if ((q - new Vec2(HoleX(i), RowY(Guess))).Length <= HoleR + 5)
            {
                ClickHole(i, false);
                return false;
            }
        if (Button.Contains(q.ToPoint()) && RowFull) Check();
        return false;
    }

    public override void PointerUp(Vec2 p) => _handle.End(_origin);

    public override void PointerCancel() => _handle.Cancel();

    /// <summary>Places the selected colour; a hole that already has it (or no colour picked yet) cycles on instead.</summary>
    void ClickHole(int i, bool right)
    {
        if (right) _row[i] = -1;
        else if (_selected >= 0 && _row[i] != _selected) _row[i] = _selected;
        else _selected = _row[i] = (_row[i] + 1) % Colours;
        if (_row[i] >= 0 && Guess == 0 && !_racing) BeginRound(); // the first peg of a fresh code starts a round
        Host.Sound.Play("board", 0.3, 1.2 + 0.08 * Math.Max(0, _row[i]));
        Ripple(HoleX(i), RowY(Guess));
        Changed();
        if (_row[i] >= 0) Drop($"p{Guess}.{i}");
    }

    void Check()
    {
        int g = Guess;
        var (black, white) = _rules.Submit(_row);
        Array.Fill(_row, -1);
        Host.Stats.Add("codebreaker.guesses");
        for (int k = 0; k < black + white; k++) _pending.Add($"f{g}.{k}");
        Ripple(FeedbackX, RowY(g));
        var at = new Vec2(_origin.X + FeedbackX * _scale, _origin.Y + RowY(g) * _scale);
        Host.ShareAction(at, black);
        if (_rules.Won)
        {
            Host.Stats.Add("codebreaker.wins");
            Host.Stats.Min("codebreaker.best", Guess);
            if (Guess <= FastGuesses) Host.Stats.Add("codebreaker.fast");
            var top = new Vec2(Panel.Center.X, Panel.Top + Panel.Height * 0.3);
            Host.Fx.Popup(top, L.T("CODE CRACKED!"), Gold, 38, 2.6, L.F("{0} of {1} guesses", Guess, MaxGuesses));
            Host.Fx.Burst(top, Themes.Current.Confetti, 44, 540, 700, 7, 1.1);
            Host.Sound.Play("best", 0.8);
            Reveal();
            EndRound();
        }
        else if (_rules.Lost)
        {
            Host.Fx.Popup(new Vec2(Panel.Center.X, Panel.Top + Panel.Height * 0.3), L.T("OUT OF GUESSES"), Colors.White, 34, 2.6,
                L.T("here is the code · click the board for a new code"));
            Host.Sound.Play("buzzer", 0.45);
            Reveal();
            EndRound();
        }
        else
        {
            Host.Sound.Play("thunk", 0.4 + 0.1 * black, 1 + 0.05 * (black + white));
            if (black + white > 0) Host.Fx.Burst(at, new[] { Colors.White, Gold }, 4 + 2 * black, 120, 200, 3, 0.35);
        }
        Changed();
        for (int k = 0; k < black + white; k++) PopPin($"f{g}.{k}", k);
    }

    void NewCode()
    {
        Anims.Clear(); // drops, pops and the reveal of the old code
        _revealing = false;
        _pending.Clear();
        _rippleTween = null;
        _ripple.IsVisible = false;
        _rules = new CodeBreakerRules(Rng);
        Array.Fill(_row, -1);
        _selected = -1;
        _solver = null;
        _plan = null;
        _racing = false;
        Host.Sound.Play("whoosh", 0.3);
        Changed();
    }

    void Changed()
    {
        Draw();
        Host.HudChanged();
        Host.Wake();
    }

    // ------------------------------------------------------------------ motion

    /// <summary>A peg just placed drops into its hole from a little above, landing with a small bounce.</summary>
    void Drop(string key)
    {
        void Apply(double k)
        {
            if (!_sprites.TryGetValue(key, out var s)) return;
            s.Sprite.Scale = 0.4 + 0.6 * k;
            s.Sprite.Set(s.At - new Vec2(0, 14 * (1 - k)));
        }
        if (!Fx.ReducedMotion) Apply(0);
        Anims.Add(DropTime, Apply, Ease.OutBack);
        Host.Wake();
    }

    /// <summary>The k-th feedback pin of a checked row pops in, a beat after the one before.</summary>
    void PopPin(string key, int k)
    {
        Anims.Add(PinTime, kk =>
        {
            if (_pending.Remove(key)) Host.Sound.Play("board", 0.15, 1.8 + 0.1 * k);
            if (_sprites.TryGetValue(key, out var s)) s.Sprite.Scale = Math.Max(0.01, kk);
        }, Ease.OutBack, null, PinDelay(k));
        Host.Wake();
    }

    /// <summary>The game is over: each cap flips over to show the peg under it, left to right.</summary>
    void Reveal()
    {
        _revealing = true;
        for (int i = 0; i < Pegs; i++)
        {
            int slot = i;
            _reveal[i] = 0;
            double delay = RevealLead + RevealStagger * slot;
            Anims.After(delay + RevealTime / 2, () => Host.Sound.Play("board", 0.25, 1.4 + 0.1 * slot));
            Anims.Add(RevealTime, k =>
            {
                _reveal[slot] = k;
                ApplyReveal(slot);
            }, Ease.Linear, slot == Pegs - 1 ? () =>
            {
                _revealing = false;
                Draw();
            } : null, delay);
        }
        Host.Wake();
    }

    void ApplyReveal(int i)
    {
        var (cap, peg) = RevealWidths(_reveal[i]);
        if (_sprites.TryGetValue($"k{i}", out var c))
        {
            c.Sprite.IsVisible = cap > 0.02;
            c.Sprite.FlipX = Math.Max(0.02, cap);
        }
        if (_sprites.TryGetValue($"c{i}", out var p))
        {
            p.Sprite.IsVisible = peg > 0.02;
            p.Sprite.FlipX = Math.Max(0.02, peg);
        }
    }

    void Ripple(double x, double y)
    {
        _rippleAt = new Vec2(x, y);
        _rippleTween?.Cancel();
        PlaceRipple(0);
        _rippleTween = Anims.Add(RippleTime, PlaceRipple, Ease.OutQuad, () =>
        {
            _rippleTween = null;
            _ripple.IsVisible = false;
        });
        Host.Wake();
    }

    void PlaceRipple(double k)
    {
        double r = HoleR + 2 + (Fx.ReducedMotion ? 0 : 8 * k);
        _ripple.Width = _ripple.Height = r * 2;
        Canvas.SetLeft(_ripple, _rippleAt.X - r);
        Canvas.SetTop(_ripple, _rippleAt.Y - r);
        _ripple.Opacity = 0.9 * (1 - k);
        _ripple.IsVisible = true;
    }

    // the overlay counts play time on the frames Update reports as busy: the ripple and the pegs on the move
    public override bool Update(double dt)
    {
        if (_handle.Dragging)
        {
            _origin = _handle.Move(Host.Pointer, Panel.Size);
            Place();
        }
        return Anims.Update(dt) || _handle.Dragging;
    }

    // ------------------------------------------------------------------ demo

    /// <summary>Plays with the solver, one peg at a time: pick the colour, place it, and Check when the row is full.</summary>
    public override void DemoTick()
    {
        if (_demoWait-- > 0) return;
        _demoWait = 1 + Rng.Next(3);
        if (_rules.Over)
        {
            if (++_demoIdle > 12) Click(new Vec2(W / 2, H / 2));
            return;
        }
        _demoIdle = 0;
        if (_solver == null)
        {
            _solver = new CodeBreakerSolver();
            _solverSeen = 0;
            for (int c = 0; c < Colours; c++) _perm[c] = c;
            for (int c = Colours - 1; c > 0; c--)
            {
                int j = Rng.Next(c + 1);
                (_perm[c], _perm[j]) = (_perm[j], _perm[c]);
            }
        }
        for (; _solverSeen < Guess; _solverSeen++) // every guess so far, the player's own included
        {
            var g = _rules.Guesses[_solverSeen].Select(c => Array.IndexOf(_perm, c)).ToArray();
            _solver.Record(g, _rules.Feedback[_solverSeen].Black, _rules.Feedback[_solverSeen].White);
            _plan = null;
        }
        _plan ??= _solver.NextGuess().Select(c => _perm[c]).ToArray();
        int hole = Enumerable.Range(0, Pegs).FirstOrDefault(i => _row[i] != _plan[i], -1);
        if (hole < 0) Click(new Vec2(Button.Center.X, Button.Center.Y));
        else if (_selected != _plan[hole]) Click(new Vec2(PaletteX(_plan[hole]), PaletteY)); // pick the colour first, as a person would
        else Click(new Vec2(HoleX(hole), RowY(Guess)));
    }

    void Click(Vec2 local) => PointerDown(_origin + local * _scale, false);

    // ------------------------------------------------------------------ visuals

    void Draw()
    {
        var into = _board.Children;
        into.Clear();
        _sprites.Clear();
        into.Add(Box(0, 0, W, H, 10, Art.Brush(232, 22, 28, 40), Art.Brush("#3A4252"), 1.5));

        // the hidden code, under caps until the game is over (both while the caps flip over)
        into.Add(Box(Pad, Pad, W - 2 * Pad, CodeH, 6, Art.Brush(255, 14, 18, 26), null, 0));
        for (int i = 0; i < Pegs; i++)
        {
            double x = HoleX(i), y = Pad + CodeH / 2;
            if (!_rules.Over || _revealing) Cap($"k{i}", x, y);
            if (_rules.Over) Peg($"c{i}", x, y, HoleR + 1, _rules.Code[i]);
            if (_revealing) ApplyReveal(i);
        }

        if (!_rules.Over)
            into.Add(Box(Pad - 4, RowY(Guess) - RowH / 2 + 2, W - 2 * Pad + 8, RowH - 4, 6, Art.Brush(28, 255, 209, 102), Art.Brush(170, 255, 209, 102), 1.5));
        for (int g = 0; g < MaxGuesses; g++)
        {
            double y = RowY(g);
            into.Add(Text((g + 1).ToString(), Pad - 6, y - 8, 20, 11, g == Guess && !_rules.Over ? "#FFD166" : "#6E7788"));
            int[]? pegs = g < Guess ? _rules.Guesses[g] : g == Guess && !_rules.Over ? _row : null;
            for (int i = 0; i < Pegs; i++)
            {
                if (pegs != null && pegs[i] >= 0) Peg($"p{g}.{i}", HoleX(i), y, HoleR, pegs[i]);
                else into.Add(Art.Circle(HoleX(i), y, HoleR * 0.45, Art.Brush("#0C0F16"), Art.Brush("#3A4252"), 1));
            }
            var (black, white) = g < Guess ? _rules.Feedback[g] : (0, 0);
            for (int k = 0; k < Pegs; k++)
            {
                double px = FeedbackX + (k % 2 - 0.5) * 12, py = y + (k / 2 - 0.5) * 12;
                if (k < black + white) Pin($"f{g}.{k}", px, py, k < black);
                else into.Add(Art.Circle(px, py, 1.6, Art.Brush("#3A4252")));
            }
        }

        for (int c = 0; c < Colours; c++)
        {
            if (c == _selected) into.Add(Art.Circle(PaletteX(c), PaletteY, PegR + 3.5, null, Art.Brush(Gold), 2.5));
            Peg($"l{c}", PaletteX(c), PaletteY, PegR, c);
        }

        bool on = RowFull && !_rules.Over;
        var b = Button;
        into.Add(Box(b.X, b.Y, b.Width, b.Height, 6, on ? Art.Brush(Gold) : Art.Brush("#2E3441"), on ? Art.Brush("#B7791F") : Art.Brush("#3A4252"), 1.5));
        into.Add(Text(L.T("Check"), b.X, b.Y + 5, b.Width, 15, on ? "#2A1E00" : "#6E7788"));
        into.Add(_ripple);
    }

    /// <summary>A sprite on the board at (<paramref name="x"/>, <paramref name="y"/>), kept under <paramref name="key"/> for the animations.</summary>
    Sprite Place(string key, double x, double y)
    {
        var s = new Sprite { IsHitTestVisible = false };
        s.Set(new Vec2(x, y));
        _board.Children.Add(s);
        _sprites[key] = (s, new Vec2(x, y));
        return s;
    }

    /// <summary>A cap over a slot of the hidden code.</summary>
    void Cap(string key, double x, double y)
    {
        var s = Place(key, x, y);
        s.Children.Add(Art.Circle(0, 0, HoleR + 1, Art.Brush("#4A5264"), Art.Brush("#2A2F3A"), 1.5));
        s.Children.Add(Text("?", -10, -10, 20, 14, "#AEB6C2"));
    }

    /// <summary>A feedback pin: black for a right colour in the right place, white for a right colour elsewhere.</summary>
    void Pin(string key, double x, double y, bool black)
    {
        var s = Place(key, x, y);
        s.Children.Add(black
            ? Art.Circle(0, 0, PinR, Art.Brush("#111318"), Art.Brush("#C9CED8"), 1.2)
            : Art.Circle(0, 0, PinR, Brushes.White, Art.Brush("#8A93A3"), 0.8));
        if (_pending.Contains(key)) s.Scale = 0.01;
    }

    /// <summary>A peg in colour <paramref name="c"/> with that colour's symbol: dot, ring, cross, bar, triangle or square.</summary>
    void Peg(string key, double x, double y, double r, int c)
    {
        var into = Place(key, x, y).Children;
        var color = Art.Safe(PegColors[c]);
        into.Add(Art.Circle(1, 1.5, r, Art.Brush(90, 0, 0, 0)));
        into.Add(Art.Circle(0, 0, r, Art.Brush(color), Art.Brush(Art.Blend(color, Colors.Black, 0.45)), 1.2));
        into.Add(Art.At(new Ellipse { Width = r * 0.7, Height = r * 0.45, Fill = Art.Brush(90, 255, 255, 255), IsHitTestVisible = false }, -r * 0.62, -r * 0.7));
        var ink = c is 2 or 4 || c == 0 && !Art.ColorBlind ? LightInk : DarkInk;
        double k = r * 0.4;
        into.Add(c switch
        {
            0 => Art.Circle(0, 0, r * 0.3, ink),
            1 => Art.Circle(0, 0, k, null, ink, r * 0.17),
            2 => Art.PathOf($"M{Art.F(-k)},{Art.F(-k)} L{Art.F(k)},{Art.F(k)} M{Art.F(k)},{Art.F(-k)} L{Art.F(-k)},{Art.F(k)}", null, ink, r * 0.2),
            3 => Art.PathOf($"M{Art.F(-r * 0.48)},0 L{Art.F(r * 0.48)},0", null, ink, r * 0.24),
            4 => Art.PathOf($"M0,{Art.F(-k * 1.1)} L{Art.F(k)},{Art.F(k * 0.8)} L{Art.F(-k)},{Art.F(k * 0.8)} Z", ink),
            _ => Box(-k * 0.85, -k * 0.85, k * 1.7, k * 1.7, 0.5, null, ink, r * 0.16),
        });
    }

    static Rectangle Box(double x, double y, double w, double h, double r, IBrush? fill, IBrush? stroke, double thick) =>
        Art.At(new Rectangle { Width = w, Height = h, RadiusX = r, RadiusY = r, Fill = fill, Stroke = stroke, StrokeThickness = thick, IsHitTestVisible = false }, x, y);

    static TextBlock Text(string text, double x, double y, double w, double size, string color) => Art.At(new TextBlock
    {
        Text = text, FontFamily = Fx.Font, FontSize = size, FontWeight = FontWeight.Bold, Foreground = Art.Brush(color),
        Width = w, TextAlignment = TextAlignment.Center, IsHitTestVisible = false,
    }, x, y);
}
