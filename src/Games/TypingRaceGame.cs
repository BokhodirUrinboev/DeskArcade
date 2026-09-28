using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using DeskArcade.Engine;
using DeskArcade.Net;

namespace DeskArcade.Games;

/// <summary>
/// Typing Race: a text on a panel over the desktop and two little cars on lanes above it, yours and the rival's. Click
/// the text, and after a count of three type it: every right key moves your car, a wrong one shows in red and has to
/// be taken back with Backspace before the text goes on. The first car over the line wins; your words per minute and
/// accuracy are what the stats keep. The rival is the computer's typist at the CPU level (tray → CPU difficulty, and
/// it moves up after two wins in a row and down after two losses), or a co-worker over the LAN typing the same text.
/// The chip in the corner picks the text: English, Russian, Uzbek or code. The overlay never takes the keyboard, so
/// while you race a small typing window opens under the panel and takes it (see <see cref="TypingPad"/>); Esc or a
/// click elsewhere hands it back, and a click on the text takes it again. The grip (or a right-drag) moves the panel.
/// </summary>
public sealed partial class TypingRaceGame : MiniGame, IKeySink
{
    const double PanelW = 700, HeaderH = 42, LaneTop = 46, LaneH = 30, TrackX0 = 142, TrackX1 = PanelW - 96, TextPad = 20;
    const double TextTop = LaneTop + 2 * LaneH + 14, CountdownSeconds = 3, GiveUpAfter = 60;
    const double ProseSize = 21, CodeSize = 17;

    static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Menlo, SF Mono, DejaVu Sans Mono, Liberation Mono, Ubuntu Mono, monospace");
    static readonly Color Gold = Color.FromRgb(255, 209, 102);
    static readonly Color Red = Color.FromRgb(232, 62, 80);

    enum Phase { Idle, Countdown, Racing, Over }

    sealed class Car
    {
        public required Canvas El;
        public required TranslateTransform Move;
        public required TextBlock Name, Tag;
        public double Shown; // the drawn progress, easing toward the real one
    }

    readonly Canvas _board = new();
    readonly ScaleTransform _zoom = new(1, 1);
    readonly Rectangle _back = new() { RadiusX = 12, RadiusY = 12, StrokeThickness = 1.5, IsHitTestVisible = false };
    readonly Rectangle _chip = new() { RadiusX = 11, RadiusY = 11, Height = 22, IsHitTestVisible = false };
    readonly TextBlock _chipText = Label(12, FontWeight.Bold);
    readonly TextBlock _status = Label(13, FontWeight.SemiBold);
    readonly TextBlock _live = Label(13, FontWeight.Bold);
    readonly Rectangle _giveUp = new() { RadiusX = 9, RadiusY = 9, Height = 20, IsHitTestVisible = false };
    readonly TextBlock _giveUpText = Label(11, FontWeight.SemiBold);
    readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap, Width = PanelW - 2 * TextPad, IsHitTestVisible = false };
    readonly Line[] _tracks = { new(), new() };
    readonly Canvas[] _flags = { new(), new() };
    readonly Car _me, _rival;
    readonly DragHandle _handle;

    TypingKind _kind;
    TypingRun _run = new("");
    CpuTypist? _cpu;
    Phase _phase;
    Vec2 _origin;
    double _scale = 1, _t, _panelH = 200, _lastBeep, _sinceFinish;
    double? _myFinish, _rivalFinish;
    bool? _won; // once the race is decided
    bool _placed, _gaveUp, _rivalGaveUp, _resultShown, _demo, _painted;
    int _passage = -1, _lastWpm, _rivalPos, _winStreak, _lossStreak;
    Rect _chipRect, _giveUpRect;

    public TypingRaceGame(IGameHost host) : base(host)
    {
        _kind = TypingTexts.FromSetting(host.Settings.TypingText, L.Code);
        _board.RenderTransformOrigin = RelativePoint.TopLeft;
        _board.RenderTransform = new TransformGroup { Children = { _zoom, new TranslateTransform() } };
        _board.Children.Add(_back);
        _board.Children.Add(_chip);
        _board.Children.Add(_chipText);
        _board.Children.Add(_status);
        _board.Children.Add(_giveUp);
        _board.Children.Add(_giveUpText);
        _board.Children.Add(_live);
        for (int lane = 0; lane < 2; lane++)
        {
            double y = LaneTop + lane * LaneH + LaneH - 7;
            _tracks[lane].StartPoint = new Point(TrackX0 - 6, y);
            _tracks[lane].EndPoint = new Point(TrackX1, y);
            _tracks[lane].StrokeThickness = 1.5;
            _tracks[lane].StrokeDashArray = new AvaloniaList<double> { 3, 4 };
            _board.Children.Add(_tracks[lane]);
            Art.At(_flags[lane], TrackX1, LaneTop + lane * LaneH + 3);
            _board.Children.Add(_flags[lane]);
        }
        _me = NewCar(0);
        _rival = NewCar(1);
        _text.Inlines = new InlineCollection();
        Art.At(_text, TextPad, TextTop);
        _board.Children.Add(_text);
        Layer.Children.Add(_board);
        _handle = new DragHandle(host, Id, Title);
        Layer.Children.Add(_handle.Visual);
        DuelSetup();
        ThemeChanged();
        NewText();
    }

    public override string Id => "typing";
    public override string Title => "Typing Race";
    public override bool SupportsLan => true;
    public override bool HasCpuLevels => true;
    public override Opponent? Opponent => new(RivalName, !LanOn, LanOn ? 0 : CpuLevel, null);

    bool LanOn => Host.Lan.Connected;
    bool IsGuest => LanOn && Host.Lan.Role == LanRole.Guest;
    string RivalName => LanOn ? Host.Lan.PeerName : L.T("CPU");

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        var t = Themes.Current;
        s.Rotor.Children.Add(Art.At(new Rectangle { Width = 20, Height = 12, RadiusX = 2, RadiusY = 2, Fill = Art.Brush(Art.Blend(t.HudFront, Colors.Black, 0.55)) }, -10, -4));
        for (int row = 0; row < 2; row++)
            for (int col = 0; col < 4; col++)
                s.Rotor.Children.Add(Art.At(new Rectangle { Width = 3, Height = 3, Fill = Art.Brush(t.HudFront) }, -8 + col * 4.4 + row, -2 + row * 4));
        s.Rotor.Children.Add(Art.PathOf("M-9,-9 L-2,-9 L-5,-6 Z", Art.Brush(Art.Safe(t.Mine))));
        s.Rotor.Children.Add(Art.PathOf("M2,-9 L9,-9 L6,-6 Z", Art.Brush(Art.Safe(t.Rival))));
        return s;
    }

    public override HudInfo Hud
    {
        get
        {
            long best = Host.Stats.Get("typing.best");
            string line = _phase switch
            {
                Phase.Countdown => L.T("Get ready…"),
                Phase.Racing when _won == true => L.T("You won!"),
                Phase.Racing when _myFinish != null => L.F("Finished · waiting for {0}", RivalName),
                Phase.Racing when !Host.HasKeyboard(this) && !_demo => L.T("Click the text to keep typing"),
                Phase.Racing => L.F("Type! · {0}% accuracy", TypingRun.Percent(_run.Accuracy)),
                _ when IsGuest => L.F("Click the text to ask {0} for a race", RivalName),
                _ => L.F("Click the text to race · {0}", TypingTexts.Label(_kind)),
            };
            return new HudInfo(L.F("{0} WPM", LiveWpm), line, best > 0 ? L.F("Best WPM {0}", best) : L.T("Best —"));
        }
    }

    /// <summary>Your speed so far in this race, or the last race's.</summary>
    int LiveWpm => _phase == Phase.Racing && _myFinish == null ? TypingRun.WholeWpm(_run.Typed, _t) : _lastWpm;

    // ------------------------------------------------------------------ layout

    public override void Layout()
    {
        var a = Host.Arena;
        _scale = Clamp(Math.Min(a.Width * 0.46 / PanelW, (a.Width - 20) / PanelW), 0.6, 1.45);
        if (!_placed)
        {
            _placed = true;
            _origin = _handle.Saved() ?? new Vec2(a.Center.X - PanelW * _scale / 2, a.Top + a.Height * 0.3);
        }
        DuelCheckSession();
        if (_drawnColorBlind != Art.ColorBlind) ThemeChanged();
        Place();
        Host.HudChanged();
    }

    bool _drawnColorBlind;

    public override void PositionsReset() => _placed = false;

    void Place()
    {
        var a = Host.Arena;
        double w = PanelW * _scale, h = _panelH * _scale;
        _origin = new Vec2(
            Clamp(_origin.X, a.Left + 8, Math.Max(a.Left + 8, a.Right - w - 8)),
            Clamp(_origin.Y, a.Top + DragHandle.Height + 12, Math.Max(a.Top + DragHandle.Height + 12, a.Bottom - h - 8)));
        _zoom.ScaleX = _zoom.ScaleY = _scale;
        Canvas.SetLeft(_board, _origin.X);
        Canvas.SetTop(_board, _origin.Y);
        _handle.Show(new Rect(_origin.X, _origin.Y, w, h));
    }

    Rect PanelRect => new(_origin.X, _origin.Y, PanelW * _scale, _panelH * _scale);

    public override void Summon(Vec2 p)
    {
        _origin = p - new Vec2(PanelW * _scale / 2, 30);
        Place();
        _handle.Save(_origin);
        if (Host.HasKeyboard(this)) Host.CaptureKeyboard(this, PanelRect, L.T(Title)); // the typing window follows
    }

    public override void Activate()
    {
        _kind = IsGuest ? _kind : TypingTexts.FromSetting(Host.Settings.TypingText, L.Code);
        if (_phase == Phase.Idle) NewText();
        base.Activate();
    }

    public override void Deactivate()
    {
        _handle.Cancel();
        Host.ReleaseKeyboard(this);
        if (_phase is Phase.Countdown or Phase.Racing)
        {
            if (LanOn) DuelGaveUp();
            _phase = Phase.Idle; // a race left for another game is dropped, not lost
            NewText();
        }
        Anims.Finish();
    }

    // ------------------------------------------------------------------ input

    public override void CollectHitShapes(List<HitShape> into)
    {
        into.Add(HitShape.Box(PanelRect));
        into.Add(_handle.Hit);
    }

    public override bool PointerDown(Vec2 p, bool right)
    {
        if (right || _handle.Contains(p))
        {
            _handle.Begin(p, _origin, anywhere: true);
            return true;
        }
        var local = new Point((p.X - _origin.X) / _scale, (p.Y - _origin.Y) / _scale);
        if (_chipRect.Contains(local) && _phase is Phase.Idle or Phase.Over)
        {
            if (IsGuest) Host.Fx.Popup(p - new Vec2(0, 24), L.F("{0} picks the text", RivalName), Colors.White, 16, 1.2);
            else CycleKind();
            return false;
        }
        if (_giveUpRect.Contains(local) && _phase == Phase.Racing && _myFinish == null)
        {
            GiveUp();
            return false;
        }
        switch (_phase)
        {
            case Phase.Idle or Phase.Over:
                if (IsGuest) DuelAskRace();
                else StartRace(NextPassage(), LanOn ? _round + 1 : 0);
                break;
            case Phase.Countdown or Phase.Racing when _myFinish == null && !_gaveUp:
                Host.CaptureKeyboard(this, PanelRect, L.T(Title)); // back to typing
                break;
        }
        return false;
    }

    public override void PointerUp(Vec2 p)
    {
        if (!_handle.Dragging) return;
        _handle.End(_origin);
        if (Host.HasKeyboard(this)) Host.CaptureKeyboard(this, PanelRect, L.T(Title)); // the typing window follows the panel
    }

    public override void PointerCancel() => _handle.Cancel();

    void CycleKind()
    {
        _kind = TypingTexts.Next(_kind);
        Host.Settings.TypingText = TypingTexts.SettingOf(_kind);
        Host.SaveSettings();
        Host.Sound.Play("key", 0.5, 1.2);
        _phase = Phase.Idle;
        NewText();
        Host.HudChanged();
    }

    // ------------------------------------------------------------------ typing (the typing window calls these)

    public void TextTyped(string text)
    {
        if (!Typing) return;
        foreach (char c in text) TypeOne(c);
    }

    public void KeyPressed(TypingKey key)
    {
        switch (key)
        {
            case TypingKey.Escape:
                Host.ReleaseKeyboard(this);
                break;
            case TypingKey.Enter when Typing:
                TypeOne('\n');
                break;
            case TypingKey.Backspace when Typing && _run.Backspace():
            case TypingKey.WordBackspace when Typing && _run.ClearWrong():
                Host.Sound.Play("key", 0.35, 0.8);
                DrawText();
                break;
        }
        Host.HudChanged();
    }

    public void KeyboardLost() => Host.HudChanged();

    bool Typing => _phase == Phase.Racing && _myFinish == null && !_gaveUp;

    void TypeOne(char c)
    {
        var result = _run.Type(c);
        if (result == TypeResult.Ignored) return;
        if (result == TypeResult.Correct)
        {
            Host.Stats.Add("typing.chars");
            Host.Sound.Play(c == '\n' ? "key-return" : "key", c == '\n' ? 0.5 : 0.32, 0.92 + Rng.NextDouble() * 0.16);
            if (_run.Done) Finish();
        }
        else Host.Sound.Play("key-bad", 0.4);
        DrawText();
        Host.HudChanged();
    }

    // ------------------------------------------------------------------ the race

    int NextPassage()
    {
        int count = TypingTexts.Passages(_kind).Count;
        if (count <= 1) return 0;
        int next;
        do next = Rng.Next(count);
        while (next == _passage);
        return next;
    }

    /// <summary>Between races: a fresh text on the panel to look at, the cars back on the start line.</summary>
    void NewText()
    {
        var passages = TypingTexts.Passages(_kind);
        if (_passage < 0 || _passage >= passages.Count) _passage = NextPassage();
        _run = new TypingRun(passages[_passage]);
        _cpu = null;
        _myFinish = _rivalFinish = null;
        _rivalPos = 0;
        _me.Shown = _rival.Shown = 0;
        _me.Tag.Text = _rival.Tag.Text = "";
        DrawText();
        DrawCars();
    }

    /// <param name="round">Over the LAN, the race's number (the guest takes the host's); 0 against the computer.</param>
    void StartRace(int passage, int round)
    {
        var passages = TypingTexts.Passages(_kind);
        _passage = Math.Clamp(passage, 0, passages.Count - 1);
        _run = new TypingRun(passages[_passage]);
        _phase = Phase.Countdown;
        _t = -CountdownSeconds;
        _lastBeep = double.NaN;
        _myFinish = _rivalFinish = null;
        _gaveUp = _rivalGaveUp = _resultShown = false;
        _won = null;
        _sinceFinish = 0;
        _rivalPos = 0;
        _me.Shown = _rival.Shown = 0;
        _me.Tag.Text = _rival.Tag.Text = "";
        _cpu = LanOn ? null : new CpuTypist(_run.Text, CpuLevel, Rng);
        if (LanOn) DuelRaceStarted(round);
        DrawText();
        DrawCars();
        Host.Sound.Play("whoosh", 0.3);
        Host.CaptureKeyboard(this, PanelRect, L.T(Title));
        Host.HudChanged();
        Host.Wake();
    }

    void Countdown(double dt)
    {
        _t += dt;
        double left = Math.Ceiling(-_t);
        if (left != _lastBeep && left > 0)
        {
            _lastBeep = left;
            Host.Fx.Popup(TextCenter, ((int)left).ToString(System.Globalization.CultureInfo.InvariantCulture), Themes.Themed(Gold), 58, 0.8);
            Host.Sound.Play("board", 0.45, 1.8);
        }
        if (_t < 0) return;
        _t = 0;
        _phase = Phase.Racing;
        Host.Fx.Popup(TextCenter, L.T("GO!"), Themes.Themed(Gold), 58, 0.9);
        Host.Sound.Play("score", 0.6);
        Host.HudChanged();
    }

    void Finish()
    {
        _myFinish = _t;
        _lastWpm = TypingRun.WholeWpm(_run.Typed, _t);
        int accuracy = TypingRun.Percent(_run.Accuracy);
        Host.ReleaseKeyboard(this);
        Host.Stats.Add("typing.races");
        Host.Stats.Max("typing.best", _lastWpm);
        if (_run.Mistakes == 0) Host.Stats.Add("typing.perfect");
        if (_kind == TypingKind.Code) Host.Stats.Add("typing.code");
        Host.Sound.Play("ding", 0.6);
        _me.Tag.Text = L.F("{0} WPM", _lastWpm);
        Host.Fx.Popup(TextCenter, L.F("{0} WPM", _lastWpm), Themes.Themed(Gold), 40, 2.2, L.F("{0}% accuracy · {1}s", accuracy, TypingRun.Format(_t)));
        if (LanOn) DuelFinished();
        Decide();
        Host.HudChanged();
    }

    void GiveUp()
    {
        _gaveUp = true;
        Host.ReleaseKeyboard(this);
        if (LanOn) DuelGaveUp();
        Decide();
        Host.HudChanged();
    }

    /// <summary>Once the winner is clear, says so (once): the first over the line, or whoever is still going when the other gave up.</summary>
    void Decide()
    {
        if (_resultShown) return;
        bool? won = null;
        if (_gaveUp) won = false;
        else if (_rivalGaveUp) won = true;
        else if (_myFinish is { } mine && _rivalFinish is { } theirs) won = mine <= theirs;
        else if (_myFinish is { } m && RivalBeaten(m)) won = true; // the computer's time is known ahead; a co-worker had their chance
        else if (_rivalFinish is { } r && _myFinish == null && _t > r) won = false;
        if (won is not { } w) return;
        _resultShown = true;
        _won = w;
        var at = new Vec2(PanelRect.Center.X, PanelRect.Top - 10);
        if (w)
        {
            Host.Stats.Add("typing.wins");
            if (LanOn) Host.Stats.Add("lan.wins");
            string sub = LanOn ? L.F("ahead of {0}", RivalName) : L.F("ahead of the CPU at {0} WPM", _cpu?.Wpm ?? 0);
            if (!LanOn && LevelStep(won: true) is string up) sub += " · " + up;
            Host.Fx.Popup(at, L.T("YOU WIN!"), Themes.Themed(Gold), 42, 2.6, sub);
            Host.Fx.Burst(at, Themes.Current.Confetti, 40, 520, 700, 7, 1.1);
            Host.Sound.Play("best", 0.8);
        }
        else
        {
            string sub = _gaveUp ? L.T("you gave up") : LanOn ? L.F("{0} crossed the line first", RivalName) : L.F("the CPU typed {0} WPM", _cpu?.Wpm ?? 0);
            if (!LanOn && LevelStep(won: false) is string down) sub += " · " + down;
            Host.Fx.Popup(at, LanOn ? L.F("{0} WINS", RivalName) : L.T("CPU WINS"), Colors.White, 38, 2.4, sub);
            Host.Sound.Play("buzzer", 0.4);
        }
    }

    /// <summary>Two wins in a row move the computer up a level, two losses move it down; demo races leave the setting alone.</summary>
    string? LevelStep(bool won)
    {
        if (_demo) return null;
        if (won)
        {
            _lossStreak = 0;
            if (++_winStreak < 2 || CpuLevel >= LevelNames.Length) return null;
            _winStreak = 0;
            CpuLevel++;
            return L.F("the CPU moves up to {0}", L.T(LevelNames[CpuLevel - 1]));
        }
        _winStreak = 0;
        if (++_lossStreak < 2 || CpuLevel <= 1) return null;
        _lossStreak = 0;
        CpuLevel--;
        return L.F("the CPU goes easier: {0}", L.T(LevelNames[CpuLevel - 1]));
    }

    /// <summary>The race is over once both sides are done (finished or gave up), or a minute after the first one finished.</summary>
    void CheckOver(double dt)
    {
        bool mineDone = _myFinish != null || _gaveUp;
        bool theirsDone = _rivalFinish != null || _rivalGaveUp;
        if (mineDone || theirsDone) _sinceFinish += dt;
        if (!(mineDone && theirsDone) && _sinceFinish < GiveUpAfter) return;
        if (!mineDone)
        {
            _gaveUp = true; // walked away from it: counts as given up
            if (LanOn) DuelGaveUp();
        }
        Decide();
        _phase = Phase.Over;
        Host.ReleaseKeyboard(this);
        Host.HudChanged();
    }

    public override bool Update(double dt)
    {
        if (_handle.Dragging)
        {
            _origin = _handle.Move(Host.Pointer, new Size(PanelW * _scale, _panelH * _scale));
            Place();
        }
        bool busy = DuelUpdate(dt);
        switch (_phase)
        {
            case Phase.Countdown:
                Countdown(dt);
                busy = true;
                break;
            case Phase.Racing:
                _t += dt;
                if (_cpu != null)
                {
                    _rivalPos = _cpu.PosAt(_t);
                    if (_rivalFinish == null && _t >= _cpu.FinishSeconds)
                    {
                        _rivalFinish = _cpu.FinishSeconds;
                        _rival.Tag.Text = L.F("{0} WPM", _cpu.Wpm);
                        if (_myFinish == null) Host.Sound.Play("ding", 0.35, 0.8);
                        Decide();
                    }
                }
                Decide();
                CheckOver(dt);
                if (_myFinish == null) Host.HudChanged(); // the live WPM
                busy = true;
                break;
        }
        busy |= DrawCars(dt);
        PaintStatus();
        return Anims.Update(dt) || _handle.Dragging || busy;
    }

    Vec2 TextCenter => new(_origin.X + PanelW * _scale / 2, _origin.Y + (TextTop + (_panelH - TextTop) / 2) * _scale);

    // ------------------------------------------------------------------ demo

    public override void DemoTick()
    {
        _demo = true;
        switch (_phase)
        {
            case Phase.Idle or Phase.Over when !IsGuest:
                StartRace(NextPassage(), LanOn ? _round + 1 : 0);
                break;
            case Phase.Racing when Typing:
                // about 55 words a minute: a key or two every tick, now and then a slip taken back
                if (_run.Wrong.Length > 0) KeyPressed(TypingKey.Backspace);
                else if (Rng.NextDouble() < 0.04) TypeOne('#');
                else
                    for (int k = Rng.Next(1, 3); k > 0 && !_run.Done; k--)
                    {
                        char want = _run.Text[_run.Pos];
                        if (want == '\n') KeyPressed(TypingKey.Enter);
                        else TypeOne(want);
                    }
                break;
        }
    }

    // ------------------------------------------------------------------ drawing

    static TextBlock Label(double size, FontWeight weight) => new() { FontFamily = Fx.Font, FontSize = size, FontWeight = weight, IsHitTestVisible = false };

    Car NewCar(int lane)
    {
        var move = new TranslateTransform();
        var car = new Car
        {
            El = new Canvas { IsHitTestVisible = false, RenderTransform = move },
            Move = move,
            Name = Label(12, FontWeight.Bold),
            Tag = Label(11, FontWeight.Bold),
        };
        Art.At(car.Name, 16, LaneTop + lane * LaneH + 6);
        car.Name.Width = TrackX0 - 30;
        car.Name.TextTrimming = TextTrimming.CharacterEllipsis;
        _board.Children.Add(car.Name);
        _board.Children.Add(car.El);
        _board.Children.Add(car.Tag);
        Art.At(car.Tag, TrackX1 + 16, LaneTop + lane * LaneH + 8);
        return car;
    }

    /// <summary>A small racing car seen from the side, nose to the right, standing on its lane's line.</summary>
    static void DrawCar(Canvas into, Color body)
    {
        into.Children.Clear();
        var dark = Art.Brush(Art.Blend(body, Colors.Black, 0.55));
        into.Children.Add(Art.PathOf("M-17,1 L-16,-4 L-7,-5 L-3,-10 L7,-10 L11,-5 L17,-3 L18,2 L-17,2 Z", Art.Brush(body), dark, 1));
        into.Children.Add(Art.PathOf("M-1.5,-9 L6,-9 L9,-5 L-4,-5 Z", Art.Brush(Color.FromArgb(220, 200, 230, 255))));
        into.Children.Add(Art.PathOf("M-15,-2 L16,-2", null, Art.Brush(Color.FromArgb(150, 255, 255, 255)), 1.2));
        foreach (double x in new[] { -10.0, 10.0 })
        {
            into.Children.Add(Art.Circle(x, 2.5, 4, Art.Brush("#1D2029")));
            into.Children.Add(Art.Circle(x, 2.5, 1.6, Art.Brush("#B8BFCC")));
        }
    }

    static void DrawFlag(Canvas into, Color pole)
    {
        into.Children.Clear();
        into.Children.Add(Art.At(new Rectangle { Width = 1.5, Height = 22, Fill = Art.Brush(pole) }, 0, 0));
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 4; c++)
                into.Children.Add(Art.At(new Rectangle { Width = 3.5, Height = 3.5, Fill = (r + c) % 2 == 0 ? Brushes.White : Art.Brush("#20232C") }, 1.5 + c * 3.5, r * 3.5));
    }

    public override void ThemeChanged()
    {
        var t = Themes.Current;
        _drawnColorBlind = Art.ColorBlind;
        _back.Fill = Art.Brush(Color.FromArgb(238, t.Ink.R, t.Ink.G, t.Ink.B));
        _back.Stroke = Art.Brush(Color.FromArgb(150, t.Accent.R, t.Accent.G, t.Accent.B));
        _chip.Fill = Art.Brush(Color.FromArgb(70, t.Accent.R, t.Accent.G, t.Accent.B));
        _chip.Stroke = Art.Brush(t.Accent);
        _chip.StrokeThickness = 1;
        _giveUp.Stroke = Art.Brush(Color.FromArgb(160, t.HudFront.R, t.HudFront.G, t.HudFront.B));
        _giveUp.StrokeThickness = 1;
        _giveUpText.Foreground = Art.Brush(t.HudFront);
        _chipText.Foreground = _status.Foreground = Art.Brush(t.HudFront);
        _live.Foreground = Art.Brush(t.Gold);
        foreach (var track in _tracks) track.Stroke = Art.Brush(Color.FromArgb(90, t.HudFront.R, t.HudFront.G, t.HudFront.B));
        foreach (var flag in _flags) DrawFlag(flag, t.HudFront);
        DrawCar(_me.El, Art.Safe(t.Mine));
        DrawCar(_rival.El, Art.Safe(t.Rival));
        _me.Name.Foreground = _me.Tag.Foreground = Art.Brush(Art.Blend(Art.Safe(t.Mine), Colors.White, 0.35));
        _rival.Name.Foreground = _rival.Tag.Foreground = Art.Brush(Art.Blend(Art.Safe(t.Rival), Colors.White, 0.35));
        _painted = false;
        DrawText();
        PaintStatus();
    }

    /// <summary>The header: the text chip, the status in the middle, the live speed and (while racing) the give-up button.</summary>
    void PaintStatus()
    {
        string chip = TypingTexts.Label(_kind);
        string status = _phase switch
        {
            Phase.Countdown => L.T("Get ready…"),
            Phase.Racing when _gaveUp => L.T("You gave up"),
            Phase.Racing when _won == true => L.F("You won! {0} is still typing…", RivalName),
            Phase.Racing when _won == false && _myFinish == null => L.F("{0} won · finish the text for your speed", RivalName),
            Phase.Racing when _myFinish != null => L.F("Finished! Waiting for {0}…", RivalName),
            Phase.Racing when !Host.HasKeyboard(this) && !_demo => L.T("Paused · click the text to keep typing"),
            Phase.Racing => L.F("{0}% accuracy", TypingRun.Percent(_run.Accuracy)),
            Phase.Over when _won == true => L.T("You won! Click the text for the next race"),
            Phase.Over => L.T("Click the text for the next race"),
            _ when IsGuest => L.F("Click the text to ask {0} for a race", RivalName),
            _ => L.T("Click the text to start"),
        };
        string live = L.F("{0} WPM", LiveWpm);
        bool giveUp = Typing;
        if (_painted && chip == _chipText.Text && status == _status.Text && live == _live.Text && giveUp == _giveUp.IsVisible) return;
        _painted = true;
        _chipText.Text = chip;
        _status.Text = status;
        _live.Text = live;
        _giveUp.IsVisible = _giveUpText.IsVisible = giveUp;

        _chipText.Measure(Size.Infinity);
        double chipW = _chipText.DesiredSize.Width + 22;
        _chipRect = new Rect(14, 10, chipW, 22);
        _chip.Width = chipW;
        Art.At(_chip, _chipRect.X, _chipRect.Y);
        Art.At(_chipText, _chipRect.X + 11, _chipRect.Y + 3);

        _live.Measure(Size.Infinity);
        double liveX = PanelW - 16 - _live.DesiredSize.Width;
        Art.At(_live, liveX, 13);
        _giveUpText.Text = L.T("Give up");
        _giveUpText.Measure(Size.Infinity);
        double giveW = _giveUpText.DesiredSize.Width + 18;
        _giveUpRect = giveUp ? new Rect(liveX - 12 - giveW, 11, giveW, 20) : default;
        _giveUp.Width = giveW;
        Art.At(_giveUp, liveX - 12 - giveW, 11);
        Art.At(_giveUpText, liveX - 12 - giveW + 9, 13);

        _status.Measure(Size.Infinity);
        double left = _chipRect.Right + 12, right = (giveUp ? _giveUpRect.Left : liveX) - 12;
        _status.MaxWidth = Math.Max(40, right - left);
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        Art.At(_status, left + Math.Max(0, (right - left - Math.Min(_status.DesiredSize.Width, right - left)) / 2), 13);
    }

    /// <summary>
    /// The text: what is typed in the player's colour, the red run of mistakes, the next character underlined and the
    /// rest of the current word brighter than what follows. Line breaks show as ↵ so they can be typed.
    /// </summary>
    void DrawText()
    {
        var t = Themes.Current;
        bool code = _kind == TypingKind.Code;
        _text.FontFamily = code ? Mono : Fx.Font;
        _text.FontSize = code ? CodeSize : ProseSize;
        var inlines = _text.Inlines!;
        inlines.Clear();
        string text = _run.Text;
        int pos = Math.Min(_run.Pos, text.Length);
        var done = Art.Brush(Art.Blend(Art.Safe(t.Mine), Colors.White, 0.45));
        var word = Art.Brush(t.HudFront);
        var rest = Art.Brush(Art.Blend(t.HudFront, t.Ink, 0.45));
        if (pos > 0) inlines.Add(new Run(Shown(text[..pos])) { Foreground = done });
        if (_run.Wrong.Length > 0)
            inlines.Add(new Run(Shown(_run.Wrong).Replace(' ', '·')) { Foreground = Brushes.White, Background = Art.Brush(Art.Safe(Red)) });
        if (pos < text.Length)
        {
            var (_, end) = _run.CurrentWord();
            end = Math.Max(end, pos + 1);
            inlines.Add(new Run(Shown(text[pos].ToString()))
            {
                Foreground = word, TextDecorations = TextDecorations.Underline,
                Background = Art.Brush(Color.FromArgb(_phase == Phase.Racing ? (byte)90 : (byte)0, t.Accent.R, t.Accent.G, t.Accent.B)),
            });
            if (end > pos + 1) inlines.Add(new Run(Shown(text[(pos + 1)..end])) { Foreground = word });
            if (end < text.Length) inlines.Add(new Run(Shown(text[end..])) { Foreground = rest });
        }
        _text.Measure(new Size(PanelW - 2 * TextPad, double.PositiveInfinity));
        double h = TextTop + _text.DesiredSize.Height + 18;
        if (Math.Abs(h - _panelH) > 0.5)
        {
            _panelH = h;
            _back.Width = PanelW;
            _back.Height = _panelH;
            if (_placed) Place();
        }
        _back.Width = PanelW;
        _back.Height = _panelH;
    }

    static string Shown(string s) => s.Replace("\n", "↵\n");

    /// <summary>Puts the cars where the typists are, easing there so a burst of keys glides; true while one is still moving.</summary>
    bool DrawCars(double dt = 1)
    {
        double length = Math.Max(1, _run.Text.Length);
        double mine = _run.Pos / length, theirs = Math.Min(1, _rivalPos / length);
        bool moving = Glide(_me, mine, 0, dt);
        moving |= Glide(_rival, theirs, 1, dt);
        _me.Name.Text = L.T("You");
        _rival.Name.Text = LanOn ? RivalName : L.F("CPU · {0}", L.T(LevelNames[CpuLevel - 1]));
        return moving;
    }

    bool Glide(Car car, double to, int lane, double dt)
    {
        double k = Fx.ReducedMotion ? 1 : 1 - Math.Exp(-dt * 14);
        car.Shown += (to - car.Shown) * k;
        if (Math.Abs(to - car.Shown) < 0.0005) car.Shown = to;
        car.Move.X = TrackX0 + (TrackX1 - TrackX0 - 20) * car.Shown;
        car.Move.Y = LaneTop + lane * LaneH + LaneH - 11;
        return car.Shown != to;
    }
}
