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
/// Shortcut Trainer: "Go to definition", "Rename symbol", <c>:wq</c> — press the keys (the typing window takes them,
/// modifiers and all) or type them. A drill is ten shortcuts from one set (VS Code, JetBrains, Vim, the shell, the
/// desktop), in this desktop's keys; a miss shows the answer and has to be pressed right before going on, and the ones
/// missed come back more often in later drills (see <see cref="ShortcutDrills.Pick"/>). A drill is a round: against the
/// computer rival or a co-worker over the LAN, the higher score wins. The grip moves the board.
/// </summary>
public sealed class ShortcutGame : MiniGame, IChordSink
{
    const double W = 520, Pad = 14, ChipY = 10, ChipH = 22, FuseY = 42, FuseH = 4, CardY = 54, CardH = 176, FootY = CardY + CardH + 10;
    const double ButtonH = 26, H = FootY + ButtonH + Pad, NextDelay = 0.75;
    const string SetKey = "keys.set", MissPrefix = "keys.miss.", SetsDoneKey = "keys.setsdone";

    static readonly FontFamily Mono = new("Cascadia Mono, Consolas, SF Mono, Menlo, JetBrains Mono, DejaVu Sans Mono, Liberation Mono, Ubuntu Mono, monospace");
    static readonly Color Good = Color.FromRgb(90, 210, 130), Bad = Color.FromRgb(240, 96, 96);

    readonly Canvas _board = new() { RenderTransformOrigin = RelativePoint.TopLeft };
    readonly ScaleTransform _size = new(1, 1);
    readonly TranslateTransform _move = new();
    readonly Border _back = new() { Width = W, Height = H, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1.5), IsHitTestVisible = false };
    readonly Border _card = new() { Width = W - 2 * Pad, Height = CardH, CornerRadius = new CornerRadius(8), IsHitTestVisible = false };
    readonly (Border Box, TextBlock Text)[] _setChips = new (Border, TextBlock)[ShortcutDrills.Sets.Length];
    readonly Rect[] _setRects = new Rect[ShortcutDrills.Sets.Length];
    readonly TextBlock _os = Label(11, FontWeight.Bold), _count = Label(12, FontWeight.Bold), _action = Label(26, FontWeight.Black), _how = Label(13, FontWeight.SemiBold);
    readonly TextBlock _verdict = Label(14, FontWeight.Bold), _status = Label(12.5, FontWeight.SemiBold);
    readonly StackPanel _caps = new() { Orientation = Orientation.Horizontal, Spacing = 6, IsHitTestVisible = false };
    readonly Border _fuseBack = new() { Width = W - 2 * Pad, Height = FuseH, CornerRadius = new CornerRadius(2), IsHitTestVisible = false };
    readonly Border _fuse = new() { Height = FuseH, CornerRadius = new CornerRadius(2), IsHitTestVisible = false };
    readonly (Border Box, TextBlock Text) _button;
    readonly TranslateTransform _shake = new();
    readonly DragHandle _handle;
    Rect _buttonRect;

    readonly KeyOs _desktop = KeyChord.CurrentOs;
    ShortcutRound _round = null!;
    int _set, _session = -1, _lanRound;
    bool _placed, _racing, _demo, _nextSoon;
    Vec2 _origin;
    double _scale = 1;
    (int FirstTry, int Of, int Score, string Set)? _last;

    public ShortcutGame(IGameHost host) : base(host)
    {
        _board.RenderTransform = new TransformGroup { Children = { _size, _move } };
        _button = Chip();
        BuildView();
        _handle = new DragHandle(host, Id, Title);
        Layer.Children.Add(_board);
        Layer.Children.Add(_handle.Visual);
        _set = Math.Clamp(Get(SetKey), 0, ShortcutDrills.Sets.Length - 1);
        NewDrill();
        L.Changed += Refresh;
    }

    public override string Id => "keys";
    public override string Title => "Shortcut Trainer";

    string Set => ShortcutDrills.Sets[_set];
    bool LanOn => Host.Lan.Connected;
    int Get(string key) => Host.Settings.Levels.TryGetValue(key, out int v) ? v : 0;
    void Put(string key, int value) => Host.Settings.Levels[key] = value;
    int Misses(ShortcutDrill d) => Get(MissPrefix + d.Id);
    bool Typing => Host.HasKeyboard(this) || _demo;

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        var face = Art.Brush("#E8ECF2");
        var edge = Art.Brush("#6B7488");
        s.Rotor.Children.Add(Art.At(new Border { Width = 11, Height = 11, CornerRadius = new CornerRadius(2), Background = face, BorderBrush = edge, BorderThickness = new Thickness(1) }, -12, -5));
        s.Rotor.Children.Add(Art.At(new Border { Width = 11, Height = 11, CornerRadius = new CornerRadius(2), Background = face, BorderBrush = edge, BorderThickness = new Thickness(1) }, 1, -5));
        s.Rotor.Children.Add(Art.At(new TextBlock { Text = "⌃", FontSize = 9, FontWeight = FontWeight.Bold, Foreground = Art.Brush("#1B1F2A") }, -9.5, -5));
        s.Rotor.Children.Add(Art.At(new TextBlock { Text = "K", FontSize = 8, FontWeight = FontWeight.Bold, Foreground = Art.Brush("#1B1F2A") }, 4, -4));
        return s;
    }

    public override HudInfo Hud
    {
        get
        {
            long best = Host.Stats.Get("keys.best");
            string line = _round.Phase switch
            {
                ShortcutPhase.Ready => L.F("Click Start: ten shortcuts from {0}", ShortcutDrills.SetName(Set)),
                ShortcutPhase.Over => L.F("Drill done · {0}/{1} at the first try · {2} points", _round.FirstTry, _round.Drills.Count, _round.Score),
                _ when !Typing => L.T("Paused · click the board to go on"),
                ShortcutPhase.Missed => L.T("Not that one · press the keys shown to go on"),
                _ => L.F("Shortcut {0}/{1} · press the keys", _round.Index + 1, _round.Drills.Count),
            };
            return new HudInfo(_round.Score.ToString(CultureInfo.InvariantCulture), line, best > 0 ? L.F("Best {0}", best) : L.T("Best —"));
        }
    }

    public override string? ShareText => _last is { } l
        ? L.F("Shortcut Trainer · {0} · {1}/{2} at the first try · {3} points", ShortcutDrills.SetName(l.Set), l.FirstTry, l.Of, l.Score) + " ⌨️"
        : null;

    // ------------------------------------------------------------------ races

    public override bool SupportsLan => true;
    public override (int Score, bool Active)? Race => (_round.Score, _racing);
    /// <summary>A decent player: most shortcuts at the first try, a few seconds each.</summary>
    public override int RaceBaseline => 130;
    public override int RaceMax => ShortcutRound.MaxScore(ShortcutRound.Size);
    public override int RaceBest => (int)Host.Stats.Get("keys.best");
    public override double RaceSeconds => 90;

    public override void StartRace()
    {
        if (_racing) return;
        if (_round.Phase != ShortcutPhase.Ready) NewDrill();
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

    // ------------------------------------------------------------------ drills

    /// <summary>Ten shortcuts of the chosen set, the ones missed before more likely; seeded over the LAN.</summary>
    void NewDrill()
    {
        CheckSession();
        EndRound(finished: false);
        var rng = LanOn ? new Random(MinesweeperRules.DailySeed(DateTime.Today, "keys-lan-" + Set, ++_lanRound)) : Rng;
        var pool = ShortcutDrills.For(Set, _desktop);
        // over the LAN the misses would differ between the two screens: every drill has the same chance there
        _round = new ShortcutRound(ShortcutDrills.Pick(pool, ShortcutRound.Size, d => LanOn ? 0 : Misses(d), rng), _desktop);
        _nextSoon = false;
        Refresh();
        Host.HudChanged();
    }

    void Begin()
    {
        if (_round.Phase != ShortcutPhase.Ready) return;
        _round.Start();
        _racing = true;
        Host.RoundStarted();
        Host.Sound.Play("whoosh", 0.3, 1.2);
        if (!_demo) Host.CaptureKeyboard(this, Panel, L.T(Title));
        Refresh();
        Host.HudChanged();
    }

    void EndRound(bool finished)
    {
        if (finished)
        {
            long before = Host.Stats.Get("keys.best");
            Host.Stats.Add("keys.drills");
            Host.Stats.Max("keys.best", _round.Score);
            if (_round.Perfect) Host.Stats.Add("keys.perfect");
            int done = Get(SetsDoneKey) | 1 << _set;
            Put(SetsDoneKey, done);
            Host.Stats.Max("keys.sets", System.Numerics.BitOperations.PopCount((uint)done));
            Host.SaveSettings();
            _last = (_round.FirstTry, _round.Drills.Count, _round.Score, Set);
            bool best = _round.Score > before && _round.Score > 0;
            var at = BoardPoint(W / 2, CardY + 60);
            Host.Fx.Popup(at, best ? L.T("NEW BEST!") : L.T("DRILL DONE"), Themes.Themed(Themes.ClassicGold), 38, 2.4,
                L.F("{0} of {1} at the first try · {2} points", _round.FirstTry, _round.Drills.Count, _round.Score));
            if (best || _round.Perfect) Host.Fx.Burst(at, Themes.Current.Confetti, 40, 520, 700, 7, 1.1);
            Host.Sound.Play(best ? "best" : "done", 0.7);
            Host.ReleaseKeyboard(this);
        }
        if (!_racing) return;
        _racing = false;
        Host.RoundEnded(_round.Score);
    }

    // ------------------------------------------------------------------ keys

    /// <summary>
    /// A chord drill takes every press but Esc (which stops typing). A typed drill lets presses type, and takes only those
    /// with Ctrl or ⌘ held (a wrong answer there), leaving Ctrl+Alt alone, since AltGr types characters with it.
    /// </summary>
    public bool WantsChord(KeyChord chord)
    {
        if (_round.Phase is not (ShortcutPhase.Asking or ShortcutPhase.Missed)) return false;
        if (chord.Key == "Esc" && chord.Mods == KeyMods.None) return false;
        if (!_round.Current.Typed) return true;
        if (chord.Key == "Backspace") return false;
        return chord.Mods.HasFlag(KeyMods.Meta) || chord.Mods.HasFlag(KeyMods.Ctrl) && !chord.Mods.HasFlag(KeyMods.Alt);
    }

    public void ChordPressed(KeyChord chord) => Answer(_round.Press(chord));

    public void TextTyped(string text) => Answer(_round.Type(text));

    public void KeyPressed(TypingKey key)
    {
        switch (key)
        {
            case TypingKey.Backspace:
            case TypingKey.WordBackspace:
                _round.Backspace();
                Refresh();
                break;
            case TypingKey.Escape:
                Host.ReleaseKeyboard(this);
                Host.HudChanged();
                Refresh();
                break;
        }
    }

    public void KeyboardLost()
    {
        Host.HudChanged();
        Refresh();
    }

    void Answer(ShortcutAnswer answer)
    {
        switch (answer)
        {
            case ShortcutAnswer.Progress:
                Host.Sound.Play("key", 0.3, 1.2);
                break;
            case ShortcutAnswer.Wrong:
                Host.Sound.Play("key-bad", 0.5, 0.9);
                Shake();
                break;
            case ShortcutAnswer.Right:
                Got();
                break;
            default:
                return;
        }
        Refresh();
        Host.HudChanged();
    }

    /// <summary>Right (or skipped): the misses kept for the drill move, and the next shortcut comes in a moment.</summary>
    void Got()
    {
        var d = _round.Current;
        if (!LanOn) Put(MissPrefix + d.Id, Math.Clamp(Misses(d) + (_round.MissedThis ? 1 : -1), 0, 5));
        var at = BoardPoint(W / 2, CardY + 120);
        if (_round.LastPoints > 0)
        {
            Host.Stats.Add("keys.right");
            Host.Sound.Play("score", 0.55, 1.1);
            Host.ShareAction(at, _round.LastPoints);
        }
        else Host.Sound.Play("thunk", 0.3, 1.1);
        _nextSoon = true;
        Anims.After(NextDelay, () =>
        {
            _nextSoon = false;
            _round.Next();
            if (_round.Phase == ShortcutPhase.Over) EndRound(finished: true);
            Refresh();
            Host.HudChanged();
        });
        Host.Wake();
    }

    void Shake()
    {
        if (Fx.ReducedMotion) return;
        Anims.Add(0.35, k => _shake.X = 8 * Math.Sin(k * Math.PI * 6) * (1 - k), Ease.Linear, () => _shake.X = 0);
        Host.Wake();
    }

    // ------------------------------------------------------------------ layout

    public override void Layout()
    {
        var a = Host.Arena;
        _scale = Clamp(Math.Min(a.Width * 0.4 / W, a.Height * 0.5 / H), 0.75, 1.4);
        _scale = Math.Min(_scale, Math.Min((a.Width - 20) / W, (a.Height - 40) / H));
        if (!_placed)
        {
            _placed = true;
            _origin = _handle.Saved() ?? new Vec2(a.Center.X - W * _scale / 2, a.Center.Y - H * _scale / 2);
        }
        if (CheckSession()) NewDrill();
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

    public override void PositionsReset() => _placed = false;

    public override void Summon(Vec2 p)
    {
        _origin = p - new Vec2(W * _scale / 2, 20);
        Place();
        _handle.Save(_origin);
    }

    public override void Deactivate()
    {
        Host.ReleaseKeyboard(this);
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
        for (int k = 0; k < _setRects.Length; k++)
        {
            if (!_setRects[k].Contains(q)) continue;
            if (k == _set || _round.Phase is not (ShortcutPhase.Ready or ShortcutPhase.Over)) return false;
            _set = k;
            Put(SetKey, k);
            Host.SaveSettings();
            Host.Sound.Play("key", 0.3, 1.1);
            NewDrill();
            return false;
        }
        if (_buttonRect.Contains(q))
        {
            Host.Sound.Play("key", 0.3, 1.0);
            switch (_round.Phase)
            {
                case ShortcutPhase.Ready:
                    Begin();
                    break;
                case ShortcutPhase.Asking or ShortcutPhase.Missed when !_nextSoon:
                    _round.Skip();
                    Got();
                    Refresh();
                    break;
                case ShortcutPhase.Over:
                    NewDrill();
                    Begin();
                    break;
            }
            return false;
        }
        if (_round.Phase == ShortcutPhase.Ready) Begin();
        else if (_round.Phase is ShortcutPhase.Asking or ShortcutPhase.Missed or ShortcutPhase.Got)
        {
            Host.CaptureKeyboard(this, Panel, L.T(Title));
            Host.HudChanged();
            Refresh();
        }
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
        bool ticking = _round.Phase == ShortcutPhase.Asking && Typing;
        if (ticking)
        {
            _round.Tick(dt);
            PaintFuse();
        }
        return Anims.Update(dt) || _handle.Dragging || ticking;
    }

    // ------------------------------------------------------------------ demo

    double _demoT, _demoWait = 1;
    bool _demoSlip;

    /// <summary>Plays by itself: presses the right keys most of the time (a wrong chord now and then first), and after a drill picks another set.</summary>
    public override void DemoTick()
    {
        _demo = true;
        if ((_demoT += 0.15) < _demoWait || _nextSoon) return;
        _demoT = 0;
        _demoWait = 0.3 + Rng.NextDouble() * 0.5;
        switch (_round.Phase)
        {
            case ShortcutPhase.Ready:
                Begin();
                _demoWait = 1.2;
                break;
            case ShortcutPhase.Over:
                _set = (_set + 1) % ShortcutDrills.Sets.Length;
                NewDrill();
                _demoWait = 1.5;
                break;
            case ShortcutPhase.Asking or ShortcutPhase.Missed:
                var d = _round.Current;
                if (_round.Phase == ShortcutPhase.Asking && _round.Pressed.Count == 0 && _round.Buffer.Length == 0 && !_demoSlip && Rng.NextDouble() < 0.2)
                {
                    _demoSlip = true;
                    ChordPressed(new KeyChord(KeyMods.Ctrl, ((char)('A' + Rng.Next(26))).ToString()));
                    _demoWait = 1.8;
                    break;
                }
                _demoSlip = false;
                if (d.Typed)
                {
                    string answer = d.Answers(_desktop)[0];
                    TextTyped(answer[_round.Buffer.Length].ToString());
                }
                else ChordPressed(d.Chords(_desktop)[0][_round.Pressed.Count]);
                break;
        }
    }

    // ------------------------------------------------------------------ the board

    static TextBlock Label(double size, FontWeight weight) => new()
    {
        FontFamily = Fx.Font, FontSize = size, FontWeight = weight, IsHitTestVisible = false, TextAlignment = TextAlignment.Center,
    };

    static (Border Box, TextBlock Text) Chip()
    {
        var text = new TextBlock { FontFamily = Fx.Font, FontSize = 12, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        return (new Border { Height = ChipH, CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(1), Padding = new Thickness(10, 0), Child = text, IsHitTestVisible = false }, text);
    }

    void BuildView()
    {
        _board.Children.Add(_back);
        for (int k = 0; k < _setChips.Length; k++)
        {
            _setChips[k] = Chip();
            _board.Children.Add(_setChips[k].Box);
        }
        _board.Children.Add(_os);
        _board.Children.Add(Art.At(_fuseBack, Pad, FuseY));
        _board.Children.Add(Art.At(_fuse, Pad, FuseY));
        var card = new Canvas { RenderTransform = _shake, IsHitTestVisible = false };
        card.Children.Add(Art.At(_card, Pad, CardY));
        _count.Width = _action.Width = _how.Width = _verdict.Width = W - 2 * Pad - 20;
        card.Children.Add(Art.At(_count, Pad + 10, CardY + 12));
        card.Children.Add(Art.At(_action, Pad + 10, CardY + 34));
        card.Children.Add(Art.At(_how, Pad + 10, CardY + 72));
        card.Children.Add(_caps);
        card.Children.Add(Art.At(_verdict, Pad + 10, CardY + CardH - 30));
        _board.Children.Add(card);
        _status.TextAlignment = TextAlignment.Left;
        _status.Width = W - 2 * Pad - 150;
        _board.Children.Add(Art.At(_status, Pad, FootY + 5));
        _board.Children.Add(_button.Box);
    }

    void Refresh()
    {
        if (_round == null) return;
        var t = Themes.Current;
        var panel = Art.Blend(t.Ink, Color.FromRgb(18, 20, 26), 0.5);
        _back.Background = Art.Brush(Color.FromArgb(240, panel.R, panel.G, panel.B));
        _back.BorderBrush = Art.Brush(Art.Blend(t.Accent, t.Ink, 0.45));
        _card.Background = Art.Brush(Color.FromRgb(12, 14, 20));
        var dim = Art.Blend(t.HudFront, t.Ink, 0.4);

        double x = Pad;
        for (int k = 0; k < _setChips.Length; k++)
        {
            bool open = _round.Phase is ShortcutPhase.Ready or ShortcutPhase.Over;
            PaintChip(_setChips[k], ShortcutDrills.SetName(ShortcutDrills.Sets[k]), k == _set, open || k == _set);
            _setChips[k].Box.Measure(Size.Infinity);
            Art.At(_setChips[k].Box, x, ChipY);
            _setRects[k] = new Rect(x, ChipY, _setChips[k].Box.DesiredSize.Width, ChipH);
            x += _setChips[k].Box.DesiredSize.Width + 5;
        }
        _os.Text = _desktop switch { KeyOs.Mac => "macOS", KeyOs.Linux => "Linux", _ => "Windows" };
        _os.Foreground = Art.Brush(dim);
        _os.Measure(Size.Infinity);
        Art.At(_os, W - Pad - _os.DesiredSize.Width, ChipY + 4);
        _fuseBack.Background = Art.Brush(Color.FromRgb(40, 44, 54));
        PaintFuse();

        var phase = _round.Phase;
        var d = _round.Current;
        _count.Foreground = Art.Brush(dim);
        _how.Foreground = Art.Brush(dim);
        _action.Foreground = Art.Brush(Color.FromRgb(240, 242, 246));
        _verdict.Text = "";
        if (phase == ShortcutPhase.Ready)
        {
            _count.Text = ShortcutDrills.SetName(Set);
            _action.Text = L.T("Ten shortcuts, as fast as you can");
            _how.Text = L.T("the ones you miss come back more often");
            ShowCaps(Array.Empty<string>(), neutral: true);
        }
        else if (phase == ShortcutPhase.Over)
        {
            _count.Text = ShortcutDrills.SetName(Set);
            _action.Text = L.F("{0} points", _round.Score);
            _how.Text = L.F("{0} of {1} at the first try", _round.FirstTry, _round.Drills.Count);
            ShowCaps(Array.Empty<string>(), neutral: true);
        }
        else
        {
            _count.Text = $"{_round.Index + 1}/{_round.Drills.Count} · {ShortcutDrills.SetName(d.Set)}";
            _action.Text = L.T(d.Action);
            _how.Text = d.Typed ? L.T("type it") : L.T("press the keys");
            if (phase == ShortcutPhase.Got)
            {
                ShowAnswer(d, _round.LastPoints > 0 ? Good : Art.Blend(t.HudFront, t.Ink, 0.2));
                _verdict.Text = _round.LastPoints > 0 ? L.F("Right · +{0}", _round.LastPoints) : L.T("Next time");
                _verdict.Foreground = Art.Brush(_round.LastPoints > 0 ? Good : dim);
            }
            else if (phase == ShortcutPhase.Missed)
            {
                ShowAnswer(d, Bad);
                _verdict.Text = _round.Pressed.Count > 0 || _round.Buffer.Length > 0 ? Pending(d) : L.T("Not that one · press the keys shown");
                _verdict.Foreground = Art.Brush(Bad);
            }
            else
            {
                string pending = Pending(d);
                if (pending.Length > 0) ShowPending(d);
                else ShowCaps(Array.Empty<string>(), neutral: true);
                _verdict.Text = !Typing ? L.T("Click the board to type") : "";
                _verdict.Foreground = Art.Brush(t.Gold);
            }
        }

        _status.Text = phase switch
        {
            ShortcutPhase.Ready => L.F("{0} shortcuts in this set · keys for {1}", ShortcutDrills.For(Set, _desktop).Count, _os.Text),
            ShortcutPhase.Over => L.T("Click New drill for another ten"),
            _ => L.F("Score {0}", _round.Score),
        };
        _status.Foreground = Art.Brush(dim);
        string button = phase switch
        {
            ShortcutPhase.Ready => L.T("Start"),
            ShortcutPhase.Over => L.T("New drill"),
            _ => L.T("Skip"),
        };
        PaintChip(_button, button, on: phase is ShortcutPhase.Ready or ShortcutPhase.Over, enabled: !_nextSoon);
        _button.Box.Measure(Size.Infinity);
        double bw = Math.Max(90, _button.Box.DesiredSize.Width);
        _button.Box.Width = bw;
        Art.At(_button.Box, W - Pad - bw, FootY);
        _buttonRect = new Rect(W - Pad - bw, FootY, bw, ButtonH);
        Host.Wake();
    }

    /// <summary>What has been pressed or typed of the shortcut so far, as text.</summary>
    string Pending(ShortcutDrill d) => d.Typed ? _round.Buffer : _round.Pressed.Count > 0 ? KeyChord.Text(_round.Pressed, _desktop) + " …" : "";

    void ShowPending(ShortcutDrill d)
    {
        if (d.Typed) ShowTyped(_round.Buffer + "▏", Color.FromRgb(220, 224, 232));
        else ShowCaps(_round.Pressed.SelectMany((c, i) => (i > 0 ? new[] { "›" } : Array.Empty<string>()).Concat(c.Caps(_desktop))).Append("…").ToList(), neutral: true);
    }

    void ShowAnswer(ShortcutDrill d, Color tint)
    {
        if (d.Typed)
        {
            ShowTyped(d.Answers(_desktop)[0], tint);
            return;
        }
        var caps = new List<string>();
        var chords = d.Chords(_desktop)[0];
        for (int i = 0; i < chords.Count; i++)
        {
            if (i > 0) caps.Add("›");
            caps.AddRange(chords[i].Caps(_desktop));
        }
        ShowCaps(caps, neutral: false, tint);
    }

    /// <summary>Keycaps in a row, centred in the card; "›" between the chords of a sequence, "…" for more to come.</summary>
    void ShowCaps(IReadOnlyList<string> caps, bool neutral, Color tint = default)
    {
        _caps.Children.Clear();
        foreach (string cap in caps)
        {
            if (cap is "›" or "…")
            {
                _caps.Children.Add(new TextBlock { Text = cap, FontFamily = Fx.Font, FontSize = 20, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center, Foreground = Art.Brush("#7A8294") });
                continue;
            }
            var face = neutral ? Color.FromRgb(58, 62, 74) : Art.Blend(tint, Color.FromRgb(20, 22, 28), 0.35);
            _caps.Children.Add(new Border
            {
                MinWidth = 40, Height = 42, Padding = new Thickness(10, 0), CornerRadius = new CornerRadius(6),
                Background = Art.Brush(face), BorderBrush = Art.Brush(Art.Blend(face, Colors.Black, 0.45)), BorderThickness = new Thickness(1, 1, 1, 4),
                Child = new TextBlock
                {
                    Text = cap, FontFamily = Fx.Font, FontSize = cap.Length > 3 ? 15 : 18, FontWeight = FontWeight.Bold, Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            });
        }
        CenterCaps();
    }

    void ShowTyped(string text, Color tint)
    {
        _caps.Children.Clear();
        _caps.Children.Add(new Border
        {
            MinWidth = 60, Height = 42, Padding = new Thickness(14, 0), CornerRadius = new CornerRadius(6),
            Background = Art.Brush(Color.FromRgb(24, 27, 36)), BorderBrush = Art.Brush(tint), BorderThickness = new Thickness(1.5),
            Child = new TextBlock
            {
                Text = text, FontFamily = Mono, FontSize = 20, FontWeight = FontWeight.Bold, Foreground = Art.Brush(tint),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        });
        CenterCaps();
    }

    void CenterCaps()
    {
        _caps.Measure(Size.Infinity);
        Art.At(_caps, (W - _caps.DesiredSize.Width) / 2, CardY + 100);
    }

    void PaintFuse()
    {
        bool on = _round.Phase is ShortcutPhase.Asking && !_round.MissedThis;
        double left = on ? Math.Max(0, 1 - _round.Seconds / ShortcutRound.BonusSeconds) : 0;
        _fuse.Width = (W - 2 * Pad) * left;
        _fuse.Background = Art.Brush(Art.Blend(Bad, Themes.Current.Gold, left));
    }

    void PaintChip((Border Box, TextBlock Text) chip, string text, bool on, bool enabled)
    {
        var t = Themes.Current;
        chip.Text.Text = text;
        chip.Box.Background = Art.Brush(on ? Art.Blend(t.Accent, t.Ink, 0.35) : Art.Blend(t.Ink, Colors.Black, 0.2));
        chip.Box.BorderBrush = Art.Brush(on ? t.Accent : Art.Blend(t.Accent, t.Ink, 0.7));
        chip.Text.Foreground = Art.Brush(enabled ? t.HudFront : Art.Blend(t.HudFront, t.Ink, 0.6));
    }
}
