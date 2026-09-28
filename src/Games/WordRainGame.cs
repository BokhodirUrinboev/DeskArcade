using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>
/// Word Rain: words fall from the top of the screen and a typewriter on the taskbar shoots them down. Type a word and a
/// beam from the typewriter zaps it; the first letter picks the lowest word it starts, and the rest of the keys go to
/// that word until it is done (Backspace lets go of it). A word that lands on a window top rests there a moment, riding
/// along if the window is dragged, then drips off; one that reaches the taskbar costs one of three lives. Every ten
/// words is a level: more words, falling faster, and longer. The words come from the same choice as Typing Race's
/// texts (the chip beside the typewriter): English, Russian, Uzbek or code. A game is a round, raced against the
/// computer or a co-worker over the LAN. The overlay never takes the keyboard, so while you play a small typing window
/// sits over the taskbar and takes it; when it loses the keyboard the rain stops until a click on the typewriter.
/// </summary>
public sealed class WordRainGame : MiniGame, IKeySink
{
    const double TurretW = 88, TurretH = 52, RestSeconds = 2.4, WordH = 30, BeamLife = 0.3;

    static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Menlo, SF Mono, DejaVu Sans Mono, Liberation Mono, Ubuntu Mono, monospace");
    static readonly Color Gold = Color.FromRgb(255, 209, 102);

    sealed class Drop
    {
        public required RainWord Word;
        public required Canvas El;
        public required Rectangle Pill;
        public required TextBlock Text;
        public double Width;
        public int DrawnTyped = -1;
        public bool DrawnTarget;
    }

    readonly Dictionary<int, Drop> _drops = new();
    readonly Canvas _words = new();
    readonly Canvas _turret = new() { IsHitTestVisible = false };
    readonly Canvas _hearts = new() { IsHitTestVisible = false };
    readonly Rectangle _chip = new() { RadiusX = 10, RadiusY = 10, Height = 20, StrokeThickness = 1, IsHitTestVisible = false };
    readonly TextBlock _chipText = new() { FontFamily = Fx.Font, FontSize = 11, FontWeight = FontWeight.Bold, IsHitTestVisible = false };
    WordRainRules _rules = new();
    TypingKind _kind;
    bool _playing, _racing, _demo, _over, _wasPaused;
    double _spawnT, _turretX = double.NaN, _scale = 1;
    int _heartsShown = -1, _demoIdle;
    Rect _turretRect, _chipRect;

    public WordRainGame(IGameHost host) : base(host)
    {
        _kind = TypingTexts.FromSetting(host.Settings.TypingText, L.Code);
        Layer.Children.Add(_words);
        Layer.Children.Add(_turret);
        Layer.Children.Add(_hearts);
        Layer.Children.Add(_chip);
        Layer.Children.Add(_chipText);
        ThemeChanged();
    }

    public override string Id => "rain";
    public override string Title => "Word Rain";

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        var t = Themes.Current;
        foreach (var (x, y, w) in new[] { (-10.0, -10.0, 9.0), (1.0, -5.0, 9.0), (-6.0, 1.0, 8.0) })
            s.Rotor.Children.Add(Art.At(new Rectangle { Width = w, Height = 4.5, RadiusX = 2, RadiusY = 2, Fill = Art.Brush(t.HudFront) }, x, y));
        s.Rotor.Children.Add(Art.PathOf("M-9,10 L-3,6 L3,6 L9,10 Z", Art.Brush(Art.Safe(t.Mine))));
        s.Rotor.Children.Add(Art.PathOf("M0,6 L0,-2", null, Art.Brush(t.Gold), 1.5));
        return s;
    }

    public override HudInfo Hud
    {
        get
        {
            long best = Host.Stats.Get("rain.best");
            string line = _playing && Paused ? L.T("Paused · click the typewriter to go on")
                : _playing ? L.F("Level {0} · lives {1} · combo ×{2}", _rules.Level, _rules.Lives, _rules.Combo)
                : _over ? L.F("Game over · {0} words · click the typewriter to play again", _rules.Zapped)
                : L.F("Click the typewriter to start · {0}", TypingTexts.Label(_kind));
            return new HudInfo(_rules.Score.ToString(System.Globalization.CultureInfo.InvariantCulture), line, best > 0 ? L.F("Best {0}", best) : L.T("Best —"));
        }
    }

    bool Paused => !_demo && !Host.HasKeyboard(this);

    // ------------------------------------------------------------------ races

    public override bool SupportsLan => true;
    public override (int Score, bool Active)? Race => (_rules.Score, _racing);
    public override int RaceBaseline => 1500;
    public override int RaceBest => (int)Host.Stats.Get("rain.best");
    public override double RaceSeconds => 150;

    public override void StartRace()
    {
        if (!_racing) StartGame();
    }

    // ------------------------------------------------------------------ layout

    public override void Layout()
    {
        var a = Host.Arena;
        _scale = Clamp(a.Height / 1000, 0.8, 1.4);
        if (double.IsNaN(_turretX) || _turretX < a.Left || _turretX > a.Right) _turretX = a.Center.X;
        _turretX = Clamp(_turretX, a.Left + 180 * _scale, a.Right - 180 * _scale);
        Canvas.SetLeft(_turret, _turretX);
        Canvas.SetTop(_turret, a.Bottom);
        _turret.RenderTransform = new ScaleTransform(_scale, _scale);
        _turret.RenderTransformOrigin = RelativePoint.TopLeft;
        _turretRect = new Rect(_turretX - TurretW / 2 * _scale, a.Bottom - TurretH * _scale, TurretW * _scale, TurretH * _scale);
        Canvas.SetLeft(_hearts, _turretRect.Left - 78 * _scale);
        Canvas.SetTop(_hearts, a.Bottom - 26 * _scale);
        _hearts.RenderTransform = new ScaleTransform(_scale, _scale);
        _hearts.RenderTransformOrigin = RelativePoint.TopLeft;
        _heartsShown = -1;
        DrawHearts();
        PlaceChip();
        if (_drawnColorBlind != Art.ColorBlind) ThemeChanged();
        Host.HudChanged();
    }

    bool _drawnColorBlind;

    void PlaceChip()
    {
        _chipText.Text = TypingTexts.Label(_kind);
        _chipText.Measure(Size.Infinity);
        double w = _chipText.DesiredSize.Width + 20;
        _chipRect = new Rect(_turretRect.Right + 12, Host.Arena.Bottom - 28, w, 20);
        _chip.Width = w;
        Art.At(_chip, _chipRect.X, _chipRect.Y);
        Art.At(_chipText, _chipRect.X + 10, _chipRect.Y + 3);
        _chip.IsVisible = _chipText.IsVisible = !_playing;
    }

    public override void Activate()
    {
        if (!_playing) _kind = TypingTexts.FromSetting(Host.Settings.TypingText, L.Code);
        base.Activate();
    }

    public override void Deactivate()
    {
        Host.ReleaseKeyboard(this); // the rain waits for a click on the typewriter when the game comes back
        Anims.Finish();
    }

    public override void Summon(Vec2 p)
    {
        _turretX = p.X;
        Layout();
    }

    // ------------------------------------------------------------------ input

    public override void CollectHitShapes(List<HitShape> into)
    {
        into.Add(HitShape.Box(_turretRect.Inflate(4)));
        if (!_playing) into.Add(HitShape.Box(_chipRect.Inflate(3)));
    }

    public override bool PointerDown(Vec2 p, bool right)
    {
        if (!_playing && _chipRect.Inflate(3).Contains(p.ToPoint()))
        {
            _kind = TypingTexts.Next(_kind);
            Host.Settings.TypingText = TypingTexts.SettingOf(_kind);
            Host.SaveSettings();
            Host.Sound.Play("key", 0.5, 1.2);
            PlaceChip();
            Host.HudChanged();
            return false;
        }
        if (!_turretRect.Inflate(4).Contains(p.ToPoint())) return false;
        if (!_playing) StartGame();
        else Host.CaptureKeyboard(this, _turretRect, L.T(Title)); // back to the rain
        Host.HudChanged();
        return false;
    }

    void StartGame()
    {
        foreach (var d in _drops.Values) _words.Children.Remove(d.El);
        _drops.Clear();
        _rules = new WordRainRules();
        _playing = true;
        _over = false;
        _spawnT = 0.6;
        _wasPaused = false;
        _heartsShown = -1;
        DrawHearts();
        PlaceChip();
        if (!_racing)
        {
            _racing = true;
            Host.RoundStarted();
        }
        Host.Sound.Play("whoosh", 0.3);
        Host.CaptureKeyboard(this, _turretRect, L.T(Title));
        Host.HudChanged();
        Host.Wake();
    }

    void GameOver()
    {
        _playing = false;
        _over = true;
        Host.ReleaseKeyboard(this);
        long before = Host.Stats.Get("rain.best");
        Host.Stats.Max("rain.best", _rules.Score);
        Host.Stats.Max("rain.level", _rules.Level);
        if (_racing)
        {
            _racing = false;
            Host.RoundEnded(_rules.Score);
        }
        var at = new Vec2(Host.Arena.Center.X, Host.Arena.Top + Host.Arena.Height * 0.35);
        bool best = _rules.Score > before && _rules.Score > 0;
        Host.Fx.Popup(at, best ? L.T("NEW BEST!") : L.T("GAME OVER"), best ? Themes.Themed(Gold) : Colors.White, 44, 2.6,
            L.F("{0} words · level {1} · {2} points", _rules.Zapped, _rules.Level, _rules.Score));
        if (best) Host.Fx.Burst(at, Themes.Current.Confetti, 44, 540, 700, 7, 1.1);
        Host.Sound.Play(best ? "best" : "buzzer", best ? 0.8 : 0.4);
        foreach (var d in _drops.Values.ToList()) Melt(d);
        PlaceChip();
        Host.HudChanged();
    }

    // ------------------------------------------------------------------ typing (the typing window calls these)

    public void TextTyped(string text)
    {
        if (!_playing) return;
        foreach (char c in text)
        {
            if (!_playing) break;
            TypeOne(c);
        }
    }

    public void KeyPressed(TypingKey key)
    {
        switch (key)
        {
            case TypingKey.Escape:
                Host.ReleaseKeyboard(this);
                break;
            case TypingKey.Backspace or TypingKey.WordBackspace when _playing && _rules.Release():
                Host.Sound.Play("key", 0.3, 0.8);
                break;
        }
        Host.HudChanged();
    }

    public void KeyboardLost() => Host.HudChanged();

    void TypeOne(char c)
    {
        int level = _rules.Level;
        var (hit, word) = _rules.Type(c);
        switch (hit)
        {
            case RainHit.Progress:
                Host.Sound.Play("key", 0.3, 0.92 + Rng.NextDouble() * 0.16);
                break;
            case RainHit.Zapped when word != null && _drops.TryGetValue(word.Id, out var d):
                Zap(d, level);
                break;
            case RainHit.Miss:
                Host.Sound.Play("key-bad", 0.35);
                if (word != null && _drops.TryGetValue(word.Id, out var target)) Shake(target);
                break;
        }
        if (_rules.Level > level)
        {
            Host.Fx.Popup(new Vec2(Host.Arena.Center.X, Host.Arena.Top + Host.Arena.Height * 0.3), L.F("LEVEL {0}", _rules.Level), Themes.Themed(Gold), 44, 1.6,
                L.T("faster, and longer words"));
            Host.Sound.Play("fire", 0.6);
            Host.Stats.Max("rain.level", _rules.Level);
        }
        Host.HudChanged();
    }

    /// <summary>A beam from the typewriter to the word, which bursts; the points float up where it was.</summary>
    void Zap(Drop d, int level)
    {
        var at = new Vec2(d.Word.X, d.Word.Y - WordH * _scale / 2);
        var from = new Vec2(_turretX, Host.Arena.Bottom - TurretH * _scale);
        int points = WordRainRules.Points(d.Word.Text.Length, _rules.Combo, level);
        var t = Themes.Current;
        var beam = new Line
        {
            StartPoint = from.ToPoint(), EndPoint = at.ToPoint(), StrokeThickness = 3 * _scale, IsHitTestVisible = false,
            Stroke = Art.Brush(t.Gold), StrokeLineCap = PenLineCap.Round,
        };
        Layer.Children.Add(beam);
        Anims.Add(BeamLife, k =>
        {
            beam.Opacity = 1 - k;
            beam.StrokeThickness = 3 * _scale * (1 - k * 0.7);
        }, Ease.OutQuad, () => Layer.Children.Remove(beam));
        _drops.Remove(d.Word.Id);
        _words.Children.Remove(d.El);
        Host.Fx.Burst(at, new[] { t.Gold, t.Accent, Colors.White }, 16, 260, 300, 4, 0.6);
        Host.Fx.Popup(at - new Vec2(0, 16), $"+{points}", Themes.Themed(Gold), 20, 0.9);
        Host.Sound.Play("zap", 0.45, 0.9 + Rng.NextDouble() * 0.2);
        Host.Stats.Add("rain.words");
        Host.Stats.Max("rain.combo", _rules.Combo);
        Host.ShareAction(at, points);
    }

    void Shake(Drop d)
    {
        double x = Canvas.GetLeft(d.El);
        Anims.Add(0.22, k => Canvas.SetLeft(d.El, x + Math.Sin(k * Math.PI * 6) * 5 * (1 - k)), Ease.Linear, () => Place(d));
    }

    /// <summary>After the game, the words left fade away where they are.</summary>
    void Melt(Drop d)
    {
        _drops.Remove(d.Word.Id);
        Anims.Add(0.6, k => d.El.Opacity = 1 - k, Ease.OutQuad, () => _words.Children.Remove(d.El));
    }

    // ------------------------------------------------------------------ the rain

    public override bool Update(double dt)
    {
        bool anim = Anims.Update(dt);
        if (!_playing) return anim;
        bool paused = Paused;
        if (paused != _wasPaused)
        {
            _wasPaused = paused;
            _words.Opacity = paused ? 0.55 : 1;
            Host.HudChanged();
        }
        if (paused) return anim; // nothing falls while the keyboard is elsewhere

        if ((_spawnT -= dt) <= 0 && _rules.Words.Count < _rules.MaxWords)
        {
            Spawn();
            _spawnT = _rules.SpawnEvery * (0.8 + Rng.NextDouble() * 0.4);
        }
        double floor = Host.Arena.Bottom;
        foreach (var w in _rules.Words.ToList())
        {
            if (!_drops.TryGetValue(w.Id, out var d)) continue;
            if (w.Resting > 0) Rest(w, dt);
            else
            {
                double prev = w.Y;
                w.Y += w.Speed * _scale * dt;
                if (Host.Platforms.FindLanding(w.X, prev, w.Y, out var hit) && hit.Hwnd != w.LeftFrom && hit.Y < floor - 30)
                {
                    w.Y = hit.Y;
                    w.Resting = RestSeconds;
                    w.RestingOn = hit.Hwnd;
                    Host.Sound.Play("plop", 0.2, 1.4);
                }
            }
            if (w.Y >= floor)
            {
                Splash(d);
                if (_rules.Over) break;
                continue;
            }
            Place(d);
        }
        if (_rules.Over) GameOver();
        return true;
    }

    /// <summary>A word resting on a window top rides along with it, and drips off when its time is up or the window goes.</summary>
    void Rest(RainWord w, double dt)
    {
        var delta = Host.Platforms.DeltaOf(w.RestingOn);
        w.X += delta.X;
        w.Y += delta.Y;
        bool under = Host.Platforms.Items.Any(p => p.Hwnd == w.RestingOn && w.X >= p.X1 && w.X <= p.X2 && Math.Abs(p.Y - w.Y) < 4);
        w.Resting = under ? w.Resting - dt : 0;
        if (w.Resting > 0) return;
        w.Resting = 0;
        w.LeftFrom = w.RestingOn;
        w.RestingOn = IntPtr.Zero;
    }

    void Spawn()
    {
        var a = Host.Arena;
        string text = _rules.Pick(TypingTexts.Words(_kind), Rng);
        var d = NewDrop(text);
        double half = d.Width / 2;
        var hud = Host.HudBounds.Inflate(12);
        double x = a.Center.X;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            x = a.Left + half + 12 + Rng.NextDouble() * Math.Max(1, a.Width - 2 * half - 24);
            bool underHud = x + half > hud.Left && x - half < hud.Right; // it would fall through the scoreboard
            if (!underHud && _rules.Words.All(o => Math.Abs(o.X - x) > half + 40 || o.Y > a.Top + 120)) break;
        }
        var word = _rules.Spawn(text, x, a.Top + WordH * _scale + 4);
        d.Word = word;
        _drops[word.Id] = d;
        _words.Children.Add(d.El);
        Place(d);
        d.El.Opacity = 0;
        Anims.Add(0.3, k => d.El.Opacity = k, Ease.OutQuad);
    }

    /// <summary>A word reached the floor: a splash, a life gone.</summary>
    void Splash(Drop d)
    {
        var at = new Vec2(d.Word.X, Host.Arena.Bottom - 4);
        _rules.Landed(d.Word);
        _drops.Remove(d.Word.Id);
        _words.Children.Remove(d.El);
        Host.Fx.Burst(at, new[] { Color.FromRgb(120, 190, 255), Colors.White, Themes.Current.Accent }, 18, 300, 900, 4, 0.7);
        Host.Fx.Popup(at - new Vec2(0, 40), L.T("SPLASH!"), Color.FromRgb(120, 190, 255), 22, 0.9);
        Host.Sound.Play("splash", 0.45);
        DrawHearts();
        Host.HudChanged();
    }

    public override void DemoTick()
    {
        _demo = true;
        if (!_playing)
        {
            if (++_demoIdle > 8)
            {
                _demoIdle = 0;
                StartGame();
            }
            return;
        }
        if (Rng.NextDouble() < 0.35) return; // a moment to read
        // a person notices a word once it has fallen a little way, not the moment it appears
        double noticed = Host.Arena.Top + 200 * _scale;
        var target = _rules.Target ?? _rules.Words.Where(w => w.Y > noticed).OrderByDescending(w => w.Y).FirstOrDefault();
        if (target == null) return;
        if (Rng.NextDouble() < 0.03) TypeOne('#');
        else TypeOne(target.Text[target.Typed]);
    }

    // ------------------------------------------------------------------ drawing

    Drop NewDrop(string text)
    {
        var pill = new Rectangle { Height = WordH, RadiusX = WordH / 2, RadiusY = WordH / 2, IsHitTestVisible = false };
        var tb = new TextBlock
        {
            FontFamily = _kind == TypingKind.Code ? Mono : Fx.Font, FontSize = 17, FontWeight = FontWeight.Bold,
            IsHitTestVisible = false, Inlines = new InlineCollection(),
        };
        tb.Inlines.Add(new Run(text));
        tb.Measure(Size.Infinity);
        double width = tb.DesiredSize.Width + 24;
        pill.Width = width;
        Art.At(tb, 12, (WordH - tb.DesiredSize.Height) / 2);
        var el = new Canvas { IsHitTestVisible = false, RenderTransformOrigin = RelativePoint.TopLeft, RenderTransform = new ScaleTransform(_scale, _scale) };
        el.Children.Add(pill);
        el.Children.Add(tb);
        return new Drop { Word = null!, El = el, Pill = pill, Text = tb, Width = width * _scale };
    }

    void Place(Drop d)
    {
        var w = d.Word;
        Canvas.SetLeft(d.El, w.X - d.Width / 2);
        Canvas.SetTop(d.El, w.Y - WordH * _scale);
        bool target = _rules.Target == w;
        if (d.DrawnTyped == w.Typed && d.DrawnTarget == target) return;
        d.DrawnTyped = w.Typed;
        d.DrawnTarget = target;
        var t = Themes.Current;
        d.Pill.Fill = Art.Brush(Color.FromArgb(230, t.Ink.R, t.Ink.G, t.Ink.B));
        d.Pill.Stroke = Art.Brush(target ? t.Gold : Color.FromArgb(120, t.Accent.R, t.Accent.G, t.Accent.B));
        d.Pill.StrokeThickness = target ? 2.5 : 1.2;
        var inlines = d.Text.Inlines!;
        inlines.Clear();
        if (w.Typed > 0) inlines.Add(new Run(w.Text[..w.Typed]) { Foreground = Art.Brush(t.Gold) });
        if (w.Typed < w.Text.Length) inlines.Add(new Run(w.Text[w.Typed..]) { Foreground = Art.Brush(t.HudFront) });
    }

    void DrawHearts()
    {
        int lives = _playing || _over ? _rules.Lives : WordRainRules.StartLives;
        if (lives == _heartsShown) return;
        _heartsShown = lives;
        _hearts.Children.Clear();
        for (int i = 0; i < WordRainRules.StartLives; i++)
        {
            var heart = Art.PathOf("M0,6 C-8,0 -7,-6 -3,-6 C-1,-6 0,-4 0,-3 C0,-4 1,-6 3,-6 C7,-6 8,0 0,6 Z",
                Art.Brush(i < lives ? Art.Safe(Color.FromRgb(236, 72, 100)) : Color.FromArgb(90, 160, 160, 170)),
                Art.Brush(Color.FromArgb(140, 0, 0, 0)), 0.8);
            heart.RenderTransform = new ScaleTransform(1.6, 1.6);
            Art.At(heart, 12 + i * 22, 10);
            _hearts.Children.Add(heart);
        }
    }

    public override void ThemeChanged()
    {
        _drawnColorBlind = Art.ColorBlind;
        var t = Themes.Current;
        _chip.Fill = Art.Brush(Color.FromArgb(200, t.Ink.R, t.Ink.G, t.Ink.B));
        _chip.Stroke = Art.Brush(t.Accent);
        _chipText.Foreground = Art.Brush(t.HudFront);
        DrawTurret(t);
        foreach (var d in _drops.Values) d.DrawnTyped = -1;
    }

    /// <summary>The typewriter, standing on the floor: its body and keys, the roller, and a sheet of paper the beams leave from.</summary>
    void DrawTurret(Theme t)
    {
        _turret.Children.Clear();
        var body = Art.Blend(t.Ink, Colors.White, 0.12);
        _turret.Children.Add(Art.PathOf("M-16,-52 L16,-52 L16,-36 L-16,-36 Z", Art.Brush("#F4F1E8"), Art.Brush("#C9C2B0"), 1));
        foreach (double y in new[] { -47.0, -43.0, -39.0 })
            _turret.Children.Add(Art.PathOf($"M-11,{Art.F(y)} L11,{Art.F(y)}", null, Art.Brush("#B5AE9C"), 1));
        _turret.Children.Add(Art.At(new Rectangle { Width = 76, Height = 8, RadiusX = 4, RadiusY = 4, Fill = Art.Brush("#2A2E38") }, -38, -38));
        _turret.Children.Add(Art.At(new Rectangle { Width = 6, Height = 10, RadiusX = 2, RadiusY = 2, Fill = Art.Brush(t.Accent) }, -44, -39));
        _turret.Children.Add(Art.PathOf("M-44,0 L-40,-30 L40,-30 L44,0 Z", Art.Brush(body), Art.Brush(t.Accent), 1.5));
        var key = Art.Brush(Art.Blend(t.HudFront, t.Ink, 0.25));
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 9 - row; col++)
                _turret.Children.Add(Art.Circle(-30 + row * 3.5 + col * 7.2, -23 + row * 7, 2.4, key));
        _turret.Children.Add(Art.At(new Rectangle { Width = 30, Height = 3, RadiusX = 1.5, RadiusY = 1.5, Fill = key }, -15, -4.5));
    }
}
