using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>
/// Bit Flip: numbers fall down a well, in decimal and later in hex; click the bits along the bottom to make the lowest
/// one before it lands (see <see cref="BitFlipRules"/>). Each bit shows its place value under it and the bits' value
/// shows above them, so it teaches as it goes. A game is a round: against the computer rival or a co-worker over the
/// LAN (the same numbers on both screens), the higher score wins. The grip moves the board.
/// </summary>
public sealed class BitFlipGame : MiniGame
{
    const int MaxBits = 16;
    const double BitW = 32, BitH = 44, BitGap = 4, NibbleGap = 8, Pad = 14;
    const double BitsWidth = MaxBits * BitW + (MaxBits - 1) * BitGap + (MaxBits / 4 - 1) * NibbleGap;
    const double W = BitsWidth + 2 * Pad, HeadY = 10, WellY = 40, WellH = 300, TargetH = 34;
    const double ReadoutY = WellY + WellH + 8, BitsY = ReadoutY + 24, PlaceY = BitsY + BitH + 3, FootY = PlaceY + 20, ButtonH = 26, H = FootY + ButtonH + Pad;

    static readonly FontFamily Mono = new("Cascadia Mono, Consolas, SF Mono, Menlo, JetBrains Mono, DejaVu Sans Mono, Liberation Mono, Ubuntu Mono, monospace");
    static readonly Color On = Color.FromRgb(90, 220, 150), Off = Color.FromRgb(46, 50, 62), Lost = Color.FromRgb(240, 90, 90);

    enum Phase { Ready, Playing, Over }

    sealed class BitView
    {
        public required Border Box;
        public required TextBlock Digit;
        public required TextBlock Place;
        public required ScaleTransform Pop;
        public Rect Rect;
    }

    readonly Canvas _board = new() { RenderTransformOrigin = RelativePoint.TopLeft };
    readonly ScaleTransform _size = new(1, 1);
    readonly TranslateTransform _move = new();
    readonly Border _back = new() { Width = W, Height = H, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1.5), IsHitTestVisible = false };
    readonly Border _well = new() { Width = W - 2 * Pad, Height = WellH, CornerRadius = new CornerRadius(8), IsHitTestVisible = false };
    readonly TranslateTransform _wellShake = new();
    readonly Canvas _targetLayer = new() { IsHitTestVisible = false };
    readonly Dictionary<BitTarget, (Border Box, TextBlock Text)> _targets = new();
    readonly BitView[] _bits = new BitView[MaxBits];
    readonly TextBlock _level = Label(12.5, FontWeight.Bold), _lives = Label(15, FontWeight.Bold), _readout = Label(14, FontWeight.Bold), _card = Label(20, FontWeight.Black), _cardSub = Label(13, FontWeight.SemiBold);
    readonly (Border Box, TextBlock Text) _button = Chip(), _clear = Chip();
    readonly DragHandle _handle;
    Rect _buttonRect, _clearRect;

    BitFlipRules _rules = new(new Random(1));
    Phase _phase = Phase.Ready;
    int _session = -1, _lanRound;
    bool _placed, _racing;
    Vec2 _origin;
    double _scale = 1;

    public BitFlipGame(IGameHost host) : base(host)
    {
        _board.RenderTransform = new TransformGroup { Children = { _size, _move } };
        BuildView();
        _handle = new DragHandle(host, Id, Title);
        Layer.Children.Add(_board);
        Layer.Children.Add(_handle.Visual);
        NewGame();
        L.Changed += Refresh;
    }

    public override string Id => "bits";
    public override string Title => "Bit Flip";

    bool LanOn => Host.Lan.Connected;

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        for (int i = 0; i < 4; i++)
        {
            bool on = i is 0 or 2;
            s.Rotor.Children.Add(Art.At(new Border
            {
                Width = 5, Height = 12, CornerRadius = new CornerRadius(1.5), Background = Art.Brush(on ? Art.Safe(On) : Off),
                BorderBrush = Art.Brush("#20242E"), BorderThickness = new Thickness(0.8),
            }, -11 + i * 5.6, -3));
        }
        s.Rotor.Children.Add(Art.At(new TextBlock { Text = "0x", FontSize = 7, FontWeight = FontWeight.Bold, Foreground = Art.Brush("#FFD166") }, -6, -12));
        return s;
    }

    public override HudInfo Hud
    {
        get
        {
            long best = Host.Stats.Get("bits.best");
            string line = _phase switch
            {
                Phase.Ready => L.T("Click Start: flip the bits to make each number before it lands"),
                Phase.Over => L.F("Game over · level {0} · {1} cleared · click New game", _rules.Level, _rules.Cleared),
                _ => L.F("Level {0} · {1} bits · make the lowest number", _rules.Level, _rules.Bits),
            };
            return new HudInfo(_rules.Score.ToString(CultureInfo.InvariantCulture), line, best > 0 ? L.F("Best {0}", best) : L.T("Best —"));
        }
    }

    public override string? ShareText => Host.Stats.Get("bits.best") is > 0 and var best
        ? L.F("Bit Flip · best {0} points · up to {1} bits", best, BitFlipRules.Levels[(int)Math.Clamp(Host.Stats.Get("bits.level"), 1, BitFlipRules.Levels.Count) - 1].Bits) + " 💾"
        : null;

    // ------------------------------------------------------------------ races

    public override bool SupportsLan => true;
    public override (int Score, bool Active)? Race => (_rules.Score, _racing);
    /// <summary>A decent player: through the first few levels, into eight bits.</summary>
    public override int RaceBaseline => 500;
    public override int RaceBest => (int)Host.Stats.Get("bits.best");
    public override double RaceSeconds => 150;

    public override void StartRace()
    {
        if (_racing) return;
        if (_phase != Phase.Ready) NewGame();
        Begin();
    }

    bool CheckSession()
    {
        int session = LanOn ? Host.Lan.Session : -1;
        if (session == _session) return false;
        _session = session;
        _lanRound = 0;
        return true;
    }

    // ------------------------------------------------------------------ the game

    /// <summary>A fresh well, not started (seeded over the LAN, so both screens get the same numbers).</summary>
    void NewGame()
    {
        CheckSession();
        EndRound();
        _rules = new BitFlipRules(LanOn ? new Random(MinesweeperRules.DailySeed(DateTime.Today, "bits-lan", ++_lanRound)) : new Random(Rng.Next()));
        _phase = Phase.Ready;
        _targetLayer.Children.Clear();
        _targets.Clear();
        Anims.Clear();
        _wellShake.X = 0;
        Refresh();
        Host.HudChanged();
    }

    void Begin()
    {
        if (_phase != Phase.Ready) return;
        _phase = Phase.Playing;
        _racing = true;
        Host.RoundStarted();
        Host.Sound.Play("whoosh", 0.3, 1.2);
        Refresh();
        Host.HudChanged();
        Host.Wake();
    }

    void EndRound()
    {
        if (!_racing) return;
        _racing = false;
        Host.RoundEnded(_rules.Score);
    }

    void GameOver()
    {
        _phase = Phase.Over;
        long before = Host.Stats.Get("bits.best");
        Host.Stats.Add("bits.games");
        Host.Stats.Max("bits.best", _rules.Score);
        bool best = _rules.Score > before && _rules.Score > 0;
        var at = BoardPoint(W / 2, WellY + WellH / 2);
        Host.Fx.Popup(at, best ? L.T("NEW BEST!") : L.T("GAME OVER"), best ? Themes.Themed(Themes.ClassicGold) : Colors.White, 38, 2.6,
            L.F("level {0} · {1} cleared · {2} points", _rules.Level, _rules.Cleared, _rules.Score));
        if (best) Host.Fx.Burst(at, Themes.Current.Confetti, 40, 520, 700, 7, 1.1);
        Host.Sound.Play(best ? "best" : "buzzer", best ? 0.7 : 0.4);
        EndRound();
        Refresh();
        Host.HudChanged();
    }

    void Flip(int bit)
    {
        if (_phase != Phase.Playing || bit >= _rules.Bits) return;
        int level = _rules.Level;
        var cleared = _rules.Flip(bit);
        var view = _bits[bit];
        Host.Sound.Play("key", 0.3, (_rules.Value >> bit & 1) == 1 ? 1.3 : 1.0);
        if (!Fx.ReducedMotion) Anims.Add(0.14, k => view.Pop.ScaleX = view.Pop.ScaleY = 1 + 0.1 * Math.Sin(Math.PI * k), Ease.Linear);
        if (cleared != null) Cleared(cleared, level);
        Refresh();
        Host.Wake();
    }

    void Cleared(BitTarget t, int levelBefore)
    {
        var at = BoardPoint(W / 2, TargetTop(t) + TargetH / 2);
        Host.Stats.Add("bits.cleared");
        if (t.Hex) Host.Stats.Add("bits.hex");
        Host.Stats.Max("bits.level", _rules.Level);
        Host.Sound.Play("score", 0.55, 1 + 0.03 * Math.Min(10, _rules.Cleared % BitFlipRules.PerLevel));
        Host.Fx.Popup(at, "+" + _rules.LastPoints.ToString(CultureInfo.InvariantCulture), Themes.Themed(Themes.ClassicGold), 24, 0.9);
        Host.Fx.Burst(at, new[] { Art.Safe(On), Themes.ClassicGold, Colors.White }, 14, 240, 460, 5, 0.6);
        Host.ShareAction(at, _rules.LastPoints);
        if (_targets.Remove(t, out var v)) _targetLayer.Children.Remove(v.Box);
        Host.HudChanged();
        if (_rules.Level > levelBefore)
        {
            var top = BoardPoint(W / 2, WellY + 60);
            Host.Fx.Popup(top, L.F("LEVEL {0}", _rules.Level), Colors.White, 30, 1.8,
                _rules.Bits > BitFlipRules.Levels[levelBefore - 1].Bits ? L.F("{0} bits now", _rules.Bits) : _rules.Current.HexShare > 0 && BitFlipRules.Levels[levelBefore - 1].HexShare == 0 ? L.T("hex joins in") : L.T("faster"));
            Host.Sound.Play("done", 0.5);
        }
    }

    // ------------------------------------------------------------------ layout

    public override void Layout()
    {
        var a = Host.Arena;
        _scale = Clamp(Math.Min(a.Width * 0.42 / W, a.Height * 0.7 / H), 0.6, 1.3);
        _scale = Math.Min(_scale, Math.Min((a.Width - 20) / W, (a.Height - 40) / H));
        if (!_placed)
        {
            _placed = true;
            _origin = _handle.Saved() ?? new Vec2(a.Center.X - W * _scale / 2, a.Center.Y - H * _scale / 2);
        }
        if (CheckSession()) NewGame();
        Place();
        Refresh();
        Host.HudChanged();
    }

    void Place()
    {
        var a = Host.Arena;
        double w = W * _scale, h = H * _scale, top = a.Top + DragHandle.Height + 12;
        _origin = new Vec2(Clamp(_origin.X, a.Left + 8, Math.Max(a.Left + 8, a.Right - w - 8)), Clamp(_origin.Y, top, Math.Max(top, a.Bottom - h - 8)));
        _size.ScaleX = _size.ScaleY = _scale;
        _move.X = _origin.X;
        _move.Y = _origin.Y;
        _handle.Show(Panel);
    }

    Rect Panel => new(_origin.X, _origin.Y, W * _scale, H * _scale);
    Vec2 BoardPoint(double x, double y) => _origin + new Vec2(x * _scale, y * _scale);
    static double TargetTop(BitTarget t) => WellY + 6 + t.Y * (WellH - TargetH - 12);

    public override void PositionsReset() => _placed = false;

    public override void Summon(Vec2 p)
    {
        _origin = p - new Vec2(W * _scale / 2, 20);
        Place();
        _handle.Save(_origin);
    }

    public override void Deactivate()
    {
        _handle.Cancel();
        Anims.Finish();
    }

    // ------------------------------------------------------------------ input

    public override void CollectHitShapes(List<HitShape> into)
    {
        into.Add(HitShape.Box(Panel));
        into.Add(_handle.Hit);
    }

    public override bool PointerDown(Vec2 p, bool right)
    {
        if (_handle.Contains(p) || right)
        {
            _handle.Begin(p, _origin, anywhere: true);
            return true;
        }
        var q = ((p - _origin) / _scale).ToPoint();
        if (_buttonRect.Contains(q))
        {
            Host.Sound.Play("key", 0.3, 1.0);
            if (_phase == Phase.Ready) Begin();
            else
            {
                NewGame();
                Begin();
            }
            return false;
        }
        if (_clearRect.Contains(q) && _phase == Phase.Playing && _rules.Value != 0)
        {
            _rules.Reset();
            Host.Sound.Play("board", 0.3, 1.2);
            Refresh();
            return false;
        }
        for (int b = 0; b < _rules.Bits; b++)
            if (_bits[b].Rect.Inflate(BitGap / 2).Contains(q))
            {
                if (_phase == Phase.Ready) Begin();
                Flip(b);
                return false;
            }
        if (_phase == Phase.Ready) Begin();
        return false;
    }

    public override void PointerUp(Vec2 p) => _handle.End(_origin);
    public override void PointerCancel() => _handle.Cancel();

    public override bool Update(double dt)
    {
        if (_handle.Dragging)
        {
            _origin = _handle.Move(Host.Pointer, Panel.Size);
            Place();
        }
        bool playing = _phase == Phase.Playing;
        if (playing)
        {
            int level = _rules.Level;
            int cleared = _rules.Cleared;
            var landed = _rules.Step(dt);
            foreach (var t in landed) Landed(t);
            if (_rules.Cleared > cleared)
            {
                // the next one down was already made by the bits: it cleared as the one above landed
                foreach (var gone in _targets.Keys.Where(t => !_rules.Targets.Contains(t)).ToList()) Cleared(gone, level);
            }
            if (_rules.Over) GameOver();
            else if (landed.Count > 0) Refresh();
            MoveTargets();
            if (landed.Count > 0) Host.HudChanged();
        }
        return Anims.Update(dt) || _handle.Dragging || playing;
    }

    void Landed(BitTarget t)
    {
        if (_targets.Remove(t, out var v)) _targetLayer.Children.Remove(v.Box);
        var at = BoardPoint(W / 2, WellY + WellH - 10);
        Host.Fx.Popup(at, t.Text(_rules.Bits), Art.Safe(Lost), 26, 1.2, L.T("landed"));
        Host.Sound.Play("thunk", 0.5, 0.8);
        if (!Fx.ReducedMotion) Anims.Add(0.4, k => _wellShake.X = 7 * Math.Sin(k * Math.PI * 6) * (1 - k), Ease.Linear, () => _wellShake.X = 0);
    }

    /// <summary>Puts every falling number where it is now; new ones get their pill, the lowest is lit.</summary>
    void MoveTargets()
    {
        var t = Themes.Current;
        var lowest = _rules.Lowest;
        foreach (var target in _rules.Targets)
        {
            if (!_targets.TryGetValue(target, out var v))
            {
                var text = new TextBlock { FontFamily = Mono, FontSize = 18, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                v = (new Border { Height = TargetH, MinWidth = 84, Padding = new Thickness(14, 0), CornerRadius = new CornerRadius(TargetH / 2), BorderThickness = new Thickness(2), Child = text, IsHitTestVisible = false }, text);
                v.Text.Text = target.Text(_rules.Bits);
                _targets[target] = v;
                _targetLayer.Children.Add(v.Box);
                v.Box.Measure(Size.Infinity);
            }
            bool low = target == lowest;
            v.Box.Background = Art.Brush(low ? Color.FromRgb(40, 36, 18) : Color.FromRgb(28, 32, 42));
            v.Box.BorderBrush = Art.Brush(low ? t.Gold : target.Hex ? Color.FromRgb(120, 150, 220) : Color.FromRgb(90, 96, 112));
            v.Text.Foreground = Art.Brush(low ? t.Gold : Color.FromRgb(225, 229, 236));
            Art.At(v.Box, (W - v.Box.DesiredSize.Width) / 2, TargetTop(target));
        }
    }

    // ------------------------------------------------------------------ demo

    double _demoT, _demoWait = 1;
    BitTarget? _demoOn;

    /// <summary>
    /// Plays by itself, as a person would: reads the lowest number for a moment (longer for more bits and for hex), then
    /// flips the bits that differ one at a time.
    /// </summary>
    public override void DemoTick()
    {
        if ((_demoT += 0.15) < _demoWait) return;
        _demoT = 0;
        _demoWait = 0.25 + Rng.NextDouble() * 0.3;
        switch (_phase)
        {
            case Phase.Ready:
                Begin();
                break;
            case Phase.Over:
                _demoWait = 4;
                NewGame();
                break;
            default:
                if (_rules.Lowest is not { } low) break;
                if (low != _demoOn)
                {
                    _demoOn = low;
                    _demoWait = 0.8 + Rng.NextDouble() * 1.2 + 0.08 * _rules.Bits + (low.Hex ? 0.8 : 0);
                    break;
                }
                var flips = BitFlipRules.Differences(_rules.Value, low.Value, _rules.Bits).ToList();
                if (flips.Count > 0) Flip(flips[Rng.Next(flips.Count)]);
                _demoWait += 0.04 * _rules.Level;
                break;
        }
    }

    // ------------------------------------------------------------------ the board

    static TextBlock Label(double size, FontWeight weight) => new() { FontFamily = Fx.Font, FontSize = size, FontWeight = weight, IsHitTestVisible = false };

    static (Border Box, TextBlock Text) Chip()
    {
        var text = new TextBlock { FontFamily = Fx.Font, FontSize = 12.5, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        return (new Border { Height = ButtonH, MinWidth = 90, CornerRadius = new CornerRadius(13), BorderThickness = new Thickness(1), Padding = new Thickness(12, 0), Child = text, IsHitTestVisible = false }, text);
    }

    void BuildView()
    {
        _board.Children.Add(_back);
        _board.Children.Add(Art.At(_level, Pad, HeadY + 3));
        _board.Children.Add(_lives);
        var well = new Canvas { RenderTransform = _wellShake, IsHitTestVisible = false };
        well.Children.Add(Art.At(_well, Pad, WellY));
        // faint rows of 0s and 1s behind the numbers
        for (int row = 0; row < 7; row++)
            well.Children.Add(Art.At(new TextBlock
            {
                Text = string.Concat(Enumerable.Range(0, 80).Select(i => (i * 7 + row * 13) % 5 < 2 ? '1' : '0')), FontFamily = Mono, FontSize = 12,
                Foreground = Art.Brush(18, 120, 220, 170), IsHitTestVisible = false,
            }, Pad + 12, WellY + 16 + row * 40));
        well.Children.Add(_targetLayer);
        _card.Width = _cardSub.Width = W - 2 * Pad;
        _card.TextAlignment = _cardSub.TextAlignment = TextAlignment.Center;
        well.Children.Add(Art.At(_card, Pad, WellY + WellH / 2 - 34));
        well.Children.Add(Art.At(_cardSub, Pad, WellY + WellH / 2 + 2));
        _board.Children.Add(well);
        _readout.Width = W - 2 * Pad;
        _readout.TextAlignment = TextAlignment.Center;
        _readout.FontFamily = Mono;
        _board.Children.Add(Art.At(_readout, Pad, ReadoutY));
        for (int b = 0; b < MaxBits; b++)
        {
            var pop = new ScaleTransform(1, 1);
            var digit = new TextBlock { FontFamily = Mono, FontSize = 20, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var box = new Border
            {
                Width = BitW, Height = BitH, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1, 1, 1, 3), Child = digit, IsHitTestVisible = false,
                RenderTransformOrigin = RelativePoint.Center, RenderTransform = pop,
            };
            var place = new TextBlock { FontFamily = Mono, FontSize = 9, Width = BitW + 8, TextAlignment = TextAlignment.Center, IsHitTestVisible = false };
            _bits[b] = new BitView { Box = box, Digit = digit, Place = place, Pop = pop };
            _board.Children.Add(box);
            _board.Children.Add(place);
        }
        _board.Children.Add(_clear.Box);
        _board.Children.Add(_button.Box);
    }

    /// <summary>The bits shown, right to left from bit 0, centred, with a wider gap between groups of four.</summary>
    void PlaceBits()
    {
        int n = _rules.Bits;
        double width = n * BitW + (n - 1) * BitGap + ((n - 1) / 4) * NibbleGap;
        double left = (W - width) / 2;
        for (int b = 0; b < MaxBits; b++)
        {
            var v = _bits[b];
            bool shown = b < n;
            v.Box.IsVisible = v.Place.IsVisible = shown;
            if (!shown) continue;
            // groups of four count from the right, as nibbles do
            double x = left + width - (b + 1) * BitW - b * BitGap - (b / 4) * NibbleGap;
            v.Rect = new Rect(x, BitsY, BitW, BitH);
            Art.At(v.Box, x, BitsY);
            Art.At(v.Place, x - 4, PlaceY);
        }
    }

    void Refresh()
    {
        var t = Themes.Current;
        var panel = Art.Blend(t.Ink, Color.FromRgb(18, 20, 26), 0.5);
        _back.Background = Art.Brush(Color.FromArgb(240, panel.R, panel.G, panel.B));
        _back.BorderBrush = Art.Brush(Art.Blend(t.Accent, t.Ink, 0.45));
        _well.Background = Art.Brush(Color.FromRgb(10, 13, 18));
        var dim = Art.Blend(t.HudFront, t.Ink, 0.4);

        _level.Text = L.F("Level {0} · {1} bits", _rules.Level, _rules.Bits) + (_rules.Current.HexShare > 0 ? " · " + L.T("hex too") : "");
        _level.Foreground = Art.Brush(Art.Blend(t.HudFront, t.Ink, 0.15));
        _lives.Text = string.Concat(Enumerable.Range(0, BitFlipRules.Lives).Select(i => i < _rules.LivesLeft ? "♥" : "♡"));
        _lives.Foreground = Art.Brush(Art.Safe(Lost));
        _lives.Measure(Size.Infinity);
        Art.At(_lives, W - Pad - _lives.DesiredSize.Width, HeadY);

        _card.IsVisible = _cardSub.IsVisible = _phase != Phase.Playing;
        _card.Foreground = Art.Brush(Color.FromRgb(240, 242, 246));
        _cardSub.Foreground = Art.Brush(dim);
        if (_phase == Phase.Ready)
        {
            _card.Text = L.T("Make each number with the bits");
            _cardSub.Text = L.T("click a bit to flip it · the lowest number goes first");
        }
        else if (_phase == Phase.Over)
        {
            _card.Text = L.F("{0} points", _rules.Score);
            _cardSub.Text = L.F("level {0} · {1} cleared", _rules.Level, _rules.Cleared);
        }

        PlaceBits();
        var on = Art.Safe(On);
        for (int b = 0; b < _rules.Bits; b++)
        {
            var v = _bits[b];
            bool lit = (_rules.Value >> b & 1) == 1;
            v.Box.Background = Art.Brush(lit ? Art.Blend(on, Colors.Black, 0.25) : Off);
            v.Box.BorderBrush = Art.Brush(lit ? on : Color.FromRgb(30, 33, 42));
            v.Digit.Text = lit ? "1" : "0";
            v.Digit.Foreground = Art.Brush(lit ? Colors.White : Color.FromRgb(120, 126, 140));
            v.Place.Text = (1 << b).ToString(CultureInfo.InvariantCulture);
            v.Place.Foreground = Art.Brush(lit ? on : Color.FromRgb(96, 102, 116));
        }
        int digits = (_rules.Bits + 3) / 4;
        _readout.Text = $"= {_rules.Value.ToString(CultureInfo.InvariantCulture)}" + (_rules.Current.HexShare > 0 ? "  ·  0x" + _rules.Value.ToString("X" + digits, CultureInfo.InvariantCulture) : "");
        _readout.Foreground = Art.Brush(_rules.Lowest is { } low && low.Value == _rules.Value ? t.Gold : Color.FromRgb(200, 206, 216));

        PaintChip(_clear, L.T("Clear bits"), on: false, enabled: _phase == Phase.Playing && _rules.Value != 0);
        _clearRect = PlaceChip(_clear, Pad);
        PaintChip(_button, _phase == Phase.Ready ? L.T("Start") : L.T("New game"), on: _phase != Phase.Playing, enabled: true);
        _button.Box.Measure(Size.Infinity);
        _buttonRect = PlaceChip(_button, W - Pad - _button.Box.DesiredSize.Width);
        MoveTargets();
        Host.Wake();
    }

    static Rect PlaceChip((Border Box, TextBlock Text) chip, double x)
    {
        chip.Box.Measure(Size.Infinity);
        Art.At(chip.Box, x, FootY);
        return new Rect(x, FootY, chip.Box.DesiredSize.Width, ButtonH);
    }

    static void PaintChip((Border Box, TextBlock Text) chip, string text, bool on, bool enabled)
    {
        var t = Themes.Current;
        chip.Text.Text = text;
        chip.Box.Background = Art.Brush(on ? Art.Blend(t.Accent, t.Ink, 0.35) : Art.Blend(t.Ink, Colors.Black, 0.2));
        chip.Box.BorderBrush = Art.Brush(on ? t.Accent : Art.Blend(t.Accent, t.Ink, 0.7));
        chip.Text.Foreground = Art.Brush(enabled ? t.HudFront : Art.Blend(t.HudFront, t.Ink, 0.6));
    }
}
