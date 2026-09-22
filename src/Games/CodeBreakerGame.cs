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
/// wrong place. Every colour also carries a symbol, so the pegs read without colour. Only the panel takes the mouse.
/// </summary>
public sealed class CodeBreakerGame : MiniGame
{
    const int Pegs = CodeBreakerRules.Pegs, Colours = CodeBreakerRules.Colors, MaxGuesses = CodeBreakerRules.MaxGuesses;
    // the panel in its own units, scaled to the arena as a whole
    const double W = 220, Pad = 14, CodeH = 36, RowH = 32, HoleR = 10.5, PinR = 3.4, PegR = 11.5;
    const double RowsTop = Pad + CodeH + 10, PaletteY = RowsTop + MaxGuesses * RowH + 10 + 20, ButtonTop = PaletteY + 26, ButtonH = 30;
    const double H = ButtonTop + ButtonH + Pad, FeedbackX = 184, PaletteStep = 32;
    const double RippleTime = 0.3;
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
    Vec2 _rippleAt;
    double _rippleT = -1;
    CodeBreakerRules _rules = new(Rng);
    Vec2 _origin;
    Vec2? _summoned;
    double _scale = 1;
    int _selected = -1;

    // demo: the solver thinks in its own colour order, mapped through _perm so its openings vary
    CodeBreakerSolver? _solver;
    readonly int[] _perm = new int[Colours];
    int _solverSeen, _demoWait, _demoIdle;
    int[]? _plan;

    public CodeBreakerGame(IGameHost host) : base(host)
    {
        _board.RenderTransform = new TransformGroup { Children = { _size, _move } };
        Layer.Children.Add(_board);
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

    // ------------------------------------------------------------------ layout

    public override void Layout()
    {
        var a = Host.Arena;
        _scale = Clamp(a.Height * 0.6 / H, 0.75, 2);
        _scale = Math.Min(_scale, Math.Min((a.Height - 20) / H, (a.Width - 20) / W)); // tiny arenas
        _origin = PlacePanel(W * _scale, H * _scale);
        _size.ScaleX = _size.ScaleY = _scale;
        _move.X = _origin.X;
        _move.Y = _origin.Y;
        Draw(); // the overlay calls Layout when colour-blind mode is toggled
        Host.HudChanged();
    }

    /// <summary>The panel's top-left: centered (or where it was summoned), else beside the HUD; always inside the arena.</summary>
    Vec2 PlacePanel(double w, double h)
    {
        var a = Host.Arena;
        var hud = Host.HudBounds.Inflate(14);
        var mid = _summoned ?? new Vec2(a.Center.X, a.Center.Y);
        Vec2 Fit(double x, double y) => new(
            Clamp(x, a.Left + 10, Math.Max(a.Left + 10, a.Right - w - 10)),
            Clamp(y, a.Top + 10, Math.Max(a.Top + 10, a.Bottom - h - 10)));
        var tries = new[]
        {
            Fit(mid.X - w / 2, mid.Y - h / 2), Fit(mid.X - w / 2, hud.Bottom), Fit(mid.X - w / 2, hud.Top - h),
            Fit(hud.Right, mid.Y - h / 2), Fit(hud.Left - w, mid.Y - h / 2),
        };
        foreach (var t in tries)
            if (!new Rect(t.X, t.Y, w, h).Intersects(hud)) return t;
        return tries[0];
    }

    public override void Summon(Vec2 p)
    {
        _summoned = p;
        Layout();
    }

    Rect Panel => new(_origin.X, _origin.Y, W * _scale, H * _scale);

    static double HoleX(int i) => 48 + 33 * i;

    /// <summary>Guess 0 sits at the bottom, as on the real board.</summary>
    static double RowY(int guess) => RowsTop + (MaxGuesses - 1 - guess) * RowH + RowH / 2;

    static double PaletteX(int c) => W / 2 + (c - (Colours - 1) / 2.0) * PaletteStep;

    static Rect Button => new(Pad, ButtonTop, W - 2 * Pad, ButtonH);

    // ------------------------------------------------------------------ input

    public override void CollectHitShapes(List<HitShape> into) => into.Add(HitShape.Box(Panel));

    public override bool PointerDown(Vec2 p, bool right)
    {
        var q = (p - _origin) / _scale;
        if (_rules.Over)
        {
            if (!right) NewCode();
            return false;
        }
        for (int c = 0; c < Colours; c++)
            if (!right && (q - new Vec2(PaletteX(c), PaletteY)).Length <= PegR + 4)
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
                ClickHole(i, right);
                return false;
            }
        if (!right && Button.Contains(q.ToPoint()) && RowFull) Check();
        return false;
    }

    /// <summary>Places the selected colour; a hole that already has it (or no colour picked yet) cycles on instead.</summary>
    void ClickHole(int i, bool right)
    {
        if (right) _row[i] = -1;
        else if (_selected >= 0 && _row[i] != _selected) _row[i] = _selected;
        else _selected = _row[i] = (_row[i] + 1) % Colours;
        Host.Sound.Play("board", 0.3, 1.2 + 0.08 * Math.Max(0, _row[i]));
        Ripple(HoleX(i), RowY(Guess));
        Changed();
    }

    void Check()
    {
        var (black, white) = _rules.Submit(_row);
        Array.Fill(_row, -1);
        Host.Stats.Add("codebreaker.guesses");
        Ripple(FeedbackX, RowY(Guess - 1));
        var at = new Vec2(_origin.X + FeedbackX * _scale, _origin.Y + RowY(Guess - 1) * _scale);
        if (_rules.Won)
        {
            Host.Stats.Add("codebreaker.wins");
            Host.Stats.Min("codebreaker.best", Guess);
            if (Guess <= FastGuesses) Host.Stats.Add("codebreaker.fast");
            var top = new Vec2(Panel.Center.X, Panel.Top + Panel.Height * 0.3);
            Host.Fx.Popup(top, L.T("CODE CRACKED!"), Gold, 38, 2.6, L.F("{0} of {1} guesses", Guess, MaxGuesses));
            Host.Fx.Burst(top, Themes.Current.Confetti, 44, 540, 700, 7, 1.1);
            Host.Sound.Play("best", 0.8);
        }
        else if (_rules.Lost)
        {
            Host.Fx.Popup(new Vec2(Panel.Center.X, Panel.Top + Panel.Height * 0.3), L.T("OUT OF GUESSES"), Colors.White, 34, 2.6,
                L.T("here is the code · click the board for a new code"));
            Host.Sound.Play("buzzer", 0.45);
        }
        else
        {
            Host.Sound.Play("thunk", 0.4 + 0.1 * black, 1 + 0.05 * (black + white));
            if (black + white > 0) Host.Fx.Burst(at, new[] { Colors.White, Gold }, 4 + 2 * black, 120, 200, 3, 0.35);
        }
        Changed();
    }

    void NewCode()
    {
        _rules = new CodeBreakerRules(Rng);
        Array.Fill(_row, -1);
        _selected = -1;
        _solver = null;
        _plan = null;
        Host.Sound.Play("whoosh", 0.3);
        Changed();
    }

    void Changed()
    {
        Draw();
        Host.HudChanged();
        Host.Wake();
    }

    void Ripple(double x, double y)
    {
        _rippleAt = new Vec2(x, y);
        _rippleT = 0;
        PlaceRipple();
        Host.Wake();
    }

    void PlaceRipple()
    {
        double r = HoleR + 2 + (Fx.ReducedMotion ? 0 : 8 * _rippleT);
        _ripple.Width = _ripple.Height = r * 2;
        Canvas.SetLeft(_ripple, _rippleAt.X - r);
        Canvas.SetTop(_ripple, _rippleAt.Y - r);
        _ripple.Opacity = 0.9 * (1 - _rippleT);
        _ripple.IsVisible = true;
    }

    // only the ripple moves; the overlay counts play time on the frames Update reports as busy
    public override bool Update(double dt)
    {
        if (_rippleT < 0) return false;
        if ((_rippleT += dt / RippleTime) >= 1)
        {
            _rippleT = -1;
            _ripple.IsVisible = false;
            return false;
        }
        PlaceRipple();
        return true;
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
        into.Add(Box(0, 0, W, H, 10, Art.Brush(232, 22, 28, 40), Art.Brush("#3A4252"), 1.5));

        // the hidden code, under caps until the game is over
        into.Add(Box(Pad, Pad, W - 2 * Pad, CodeH, 6, Art.Brush(255, 14, 18, 26), null, 0));
        for (int i = 0; i < Pegs; i++)
        {
            double x = HoleX(i), y = Pad + CodeH / 2;
            if (_rules.Over) Peg(x, y, HoleR + 1, _rules.Code[i]);
            else
            {
                into.Add(Art.Circle(x, y, HoleR + 1, Art.Brush("#4A5264"), Art.Brush("#2A2F3A"), 1.5));
                into.Add(Text("?", x - 10, y - 10, 20, 14, "#AEB6C2"));
            }
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
                if (pegs != null && pegs[i] >= 0) Peg(HoleX(i), y, HoleR, pegs[i]);
                else into.Add(Art.Circle(HoleX(i), y, HoleR * 0.45, Art.Brush("#0C0F16"), Art.Brush("#3A4252"), 1));
            }
            var (black, white) = g < Guess ? _rules.Feedback[g] : (0, 0);
            for (int k = 0; k < Pegs; k++)
            {
                double px = FeedbackX + (k % 2 - 0.5) * 12, py = y + (k / 2 - 0.5) * 12;
                if (k < black) into.Add(Art.Circle(px, py, PinR, Art.Brush("#111318"), Art.Brush("#C9CED8"), 1.2));
                else if (k < black + white) into.Add(Art.Circle(px, py, PinR, Brushes.White, Art.Brush("#8A93A3"), 0.8));
                else into.Add(Art.Circle(px, py, 1.6, Art.Brush("#3A4252")));
            }
        }

        for (int c = 0; c < Colours; c++)
        {
            if (c == _selected) into.Add(Art.Circle(PaletteX(c), PaletteY, PegR + 3.5, null, Art.Brush(Gold), 2.5));
            Peg(PaletteX(c), PaletteY, PegR, c);
        }

        bool on = RowFull && !_rules.Over;
        var b = Button;
        into.Add(Box(b.X, b.Y, b.Width, b.Height, 6, on ? Art.Brush(Gold) : Art.Brush("#2E3441"), on ? Art.Brush("#B7791F") : Art.Brush("#3A4252"), 1.5));
        into.Add(Text(L.T("Check"), b.X, b.Y + 5, b.Width, 15, on ? "#2A1E00" : "#6E7788"));
        into.Add(_ripple);
    }

    /// <summary>A peg in colour <paramref name="c"/> with that colour's symbol: dot, ring, cross, bar, triangle or square.</summary>
    void Peg(double x, double y, double r, int c)
    {
        var into = _board.Children;
        var color = Art.Safe(PegColors[c]);
        into.Add(Art.Circle(x + 1, y + 1.5, r, Art.Brush(90, 0, 0, 0)));
        into.Add(Art.Circle(x, y, r, Art.Brush(color), Art.Brush(Art.Blend(color, Colors.Black, 0.45)), 1.2));
        into.Add(Art.At(new Ellipse { Width = r * 0.7, Height = r * 0.45, Fill = Art.Brush(90, 255, 255, 255), IsHitTestVisible = false }, x - r * 0.62, y - r * 0.7));
        var ink = c is 2 or 4 || c == 0 && !Art.ColorBlind ? LightInk : DarkInk;
        double k = r * 0.4;
        into.Add(c switch
        {
            0 => Art.Circle(x, y, r * 0.3, ink),
            1 => Art.Circle(x, y, k, null, ink, r * 0.17),
            2 => Art.PathOf($"M{Art.F(x - k)},{Art.F(y - k)} L{Art.F(x + k)},{Art.F(y + k)} M{Art.F(x + k)},{Art.F(y - k)} L{Art.F(x - k)},{Art.F(y + k)}", null, ink, r * 0.2),
            3 => Art.PathOf($"M{Art.F(x - r * 0.48)},{Art.F(y)} L{Art.F(x + r * 0.48)},{Art.F(y)}", null, ink, r * 0.24),
            4 => Art.PathOf($"M{Art.F(x)},{Art.F(y - k * 1.1)} L{Art.F(x + k)},{Art.F(y + k * 0.8)} L{Art.F(x - k)},{Art.F(y + k * 0.8)} Z", ink),
            _ => Box(x - k * 0.85, y - k * 0.85, k * 1.7, k * 1.7, 0.5, null, ink, r * 0.16),
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
