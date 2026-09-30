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
/// Word Guess: a hidden five-letter word in six tries. Type a guess (click the board and the typing window takes the
/// keys) or click the drawn keyboard; each letter turns green, yellow or gray (see <see cref="WordGuessRound"/>). The
/// daily word is the same for everyone that day in each language and builds a streak across the languages; practice
/// words come one after another. A practice word is a round: against the computer rival, or a co-worker over the LAN (the
/// same word when both play the same language), the fewer guesses win. Chips pick the mode and the language (each click goes
/// on to the next: English, Russian, Uzbek); the grip moves the board.
/// </summary>
public sealed class WordGuessGame : MiniGame, IKeySink
{
    enum Mode { Daily, Practice }

    // the panel in its own units, scaled to the arena as a whole
    const double W = 340, Pad = 12, ChipY = 10, ChipH = 22, StatusY = 38, GridTop = 62, Tile = 44, Gap = 6;
    const double GridW = WordList.Length * Tile + (WordList.Length - 1) * Gap, GridX = (W - GridW) / 2;
    const double KeysTop = GridTop + WordGuessRound.Tries * (Tile + Gap) + 8, KeyH = 38, KeyGap = 5;
    const double FlipTime = 0.32, FlipStagger = 0.2, MaxKeyUnit = 30;

    const string LangKey = "words.lang", ModeKey = "words.daily", LastWonKey = "words.lastwon", StreakKey = "words.streak";

    static readonly Color Green = Color.FromRgb(83, 160, 78), Yellow = Color.FromRgb(201, 170, 52), Gray = Color.FromRgb(62, 64, 70);
    // colour-blind mode: orange and blue, which tell apart without red and green
    static readonly Color BlindGreen = Color.FromRgb(245, 121, 58), BlindYellow = Color.FromRgb(96, 170, 240);

    sealed class TileView
    {
        public required Border Box;
        public required TextBlock Letter;
        public required ScaleTransform Flip;
        public required TranslateTransform Hop;
    }

    sealed class KeyView
    {
        public required string Letter; // "" for Enter, "⌫" for Backspace
        public required Rect Rect;
        public required Border Box;
        public required TextBlock Text;
    }

    readonly Canvas _board = new() { RenderTransformOrigin = RelativePoint.TopLeft };
    readonly ScaleTransform _size = new(1, 1);
    readonly TranslateTransform _move = new();
    readonly Border _back = new() { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1.5), Width = W, IsHitTestVisible = false };
    readonly TileView[,] _tiles = new TileView[WordGuessRound.Tries, WordList.Length];
    readonly TranslateTransform[] _rowShake = new TranslateTransform[WordGuessRound.Tries];
    readonly List<KeyView> _keys = new();
    readonly Canvas _keyLayer = new() { IsHitTestVisible = false };
    readonly (Border Box, TextBlock Text) _modeChip = Chip(), _langChip = Chip(), _newChip = Chip();
    readonly TextBlock _status = new() { FontFamily = Fx.Font, FontSize = 12.5, FontWeight = FontWeight.SemiBold, Width = W - 2 * Pad, TextAlignment = TextAlignment.Center, IsHitTestVisible = false };
    readonly DragHandle _handle;
    Rect _modeRect, _langRect, _newRect;

    WordList _list = null!;
    WordGuessRound _round = null!;
    Mode _mode;
    int _lang, _session = -1, _lanRound, _revealRow = -1, _revealed, _keysFor = -1;
    string _typed = "";
    string? _flash;
    double _flashLeft;
    bool _placed, _racing, _demo;
    Vec2 _origin;
    double _scale = 1, _h;
    (int Guesses, bool Won, string Code, string Grid)? _lastPractice;

    public WordGuessGame(IGameHost host) : base(host)
    {
        _board.RenderTransform = new TransformGroup { Children = { _size, _move } };
        BuildView();
        _handle = new DragHandle(host, Id, Title);
        Layer.Children.Add(_board);
        Layer.Children.Add(_handle.Visual);
        int saved = Get(LangKey);
        _lang = saved > 0 ? Math.Clamp(saved - 1, 0, WordList.Codes.Length - 1) : Math.Max(0, Array.IndexOf(WordList.Codes, L.Code));
        _list = WordList.For(Code);
        if (Get(ModeKey) == 1) NewPractice();
        else OpenDaily();
        L.Changed += Refresh;
    }

    public override string Id => "words";
    public override string Title => "Word Guess";

    string Code => WordList.Codes[_lang];
    bool LanOn => Host.Lan.Connected;
    Mode Shown => LanOn ? Mode.Practice : _mode;
    static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);
    int Get(string key) => Host.Settings.Levels.TryGetValue(key, out int v) ? v : 0;
    void Set(string key, int value) => Host.Settings.Levels[key] = value;
    string DailyNoteKey => "words.today." + Code;
    int Streak => SpotBugStreak.Showing(Get(LastWonKey), Get(StreakKey), Today.DayNumber);
    string[] Typed => WordList.Tiles(Code, _typed);
    bool Revealing => _revealRow >= 0;
    /// <summary>The word is over and its last row has turned over: the verdict can show.</summary>
    bool Finished => _round.Over && !Revealing;

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        var colors = new[] { Green, Yellow, Gray, Green };
        for (int i = 0; i < 4; i++)
            s.Rotor.Children.Add(Art.At(new Avalonia.Controls.Shapes.Rectangle
            {
                Width = 9, Height = 9, RadiusX = 1.5, RadiusY = 1.5, Fill = Art.Brush(Art.Safe(colors[i])), Stroke = Art.Brush(120, 0, 0, 0), StrokeThickness = 0.8,
            }, -10 + i % 2 * 11, -10 + i / 2 * 11));
        return s;
    }

    public override HudInfo Hud
    {
        get
        {
            long best = Host.Stats.Get("words.best");
            string bestText = best > 0 ? L.F("Fewest guesses {0}", best) : L.T("Best —");
            string score = $"{_round.Guesses.Count}/{WordGuessRound.Tries}";
            string line = Finished && _round.Won ? Shown == Mode.Daily ? Streak > 0 ? L.F("Solved · streak {0} · come back tomorrow", Streak) : L.T("Solved · come back tomorrow") : L.T("Solved · click New word")
                : Finished ? Shown == Mode.Daily ? L.T("The word got away · come back tomorrow") : L.T("The word got away · click New word")
                : !Host.HasKeyboard(this) && !_demo ? L.T("Click the board to type, or click the letters")
                : L.F("Guess {0}/{1} · type a five-letter word, Enter checks it", _round.Guesses.Count + 1, WordGuessRound.Tries);
            return new HudInfo(score, line, bestText);
        }
    }

    /// <summary>Today's daily word once it is over, else the last practice word finished.</summary>
    public override string? ShareText =>
        Shown == Mode.Daily && Finished ? WordGuessRound.ShareLine(WordList.DailyNumber(Today), Code, _round)
        : _lastPractice is { } p ? $"{L.T("Word Guess")} · {p.Code.ToUpperInvariant()} · {(p.Won ? p.Guesses.ToString(CultureInfo.InvariantCulture) : "X")}/{WordGuessRound.Tries}\n{p.Grid}"
        : null;

    // ------------------------------------------------------------------ races

    /// <summary>What a word counts in a race: the guesses it took, or one more than the board holds when it got away.</summary>
    public static int RaceScoreFor(int guesses, bool lost) => lost ? WordGuessRound.Tries + 1 : guesses;

    public override bool SupportsLan => true;
    public override (int Score, bool Active)? Race => (Shown == Mode.Practice ? RaceScoreFor(_round.Guesses.Count, _round.Lost) : 0, _racing);
    public override bool RaceLowerIsBetter => true;
    public override int RaceBaseline => 4;
    public override int RaceMin => 1;
    public override int RaceMax => WordGuessRound.Tries + 1;
    public override int RaceBest => (int)Host.Stats.Get("words.best");
    public override double RaceSeconds => 120;

    /// <summary>The rival started a round: a fresh practice word (unless this one is untouched) and the round is on.</summary>
    public override void StartRace()
    {
        if (_racing) return;
        if (Shown == Mode.Daily || _round.Guesses.Count > 0 || _round.Over || _typed.Length > 0) NewPractice();
        BeginRound();
    }

    void BeginRound()
    {
        if (Shown != Mode.Practice || _racing) return;
        _racing = true;
        Host.RoundStarted();
    }

    void EndRound()
    {
        if (!_racing) return;
        _racing = false;
        Host.RoundEnded(RaceScoreFor(_round.Guesses.Count, _round.Lost));
    }

    bool CheckSession()
    {
        int session = LanOn ? Host.Lan.Session : -1;
        if (session == _session) return false;
        _session = session;
        _lanRound = 0;
        return true;
    }

    // ------------------------------------------------------------------ words

    /// <summary>A practice word: seeded over the LAN, so both screens get the same one in the same language.</summary>
    void NewPractice()
    {
        CheckSession();
        EndRound();
        _mode = Mode.Practice;
        if (!LanOn) RememberMode();
        var rng = LanOn ? new Random(MinesweeperRules.DailySeed(DateTime.Today, "words-lan-" + Code, ++_lanRound)) : Rng;
        Start(new WordGuessRound(_list.Random(rng)));
    }

    /// <summary>Today's word in the chosen language, with the guesses already made today put back.</summary>
    void OpenDaily()
    {
        EndRound();
        _mode = Mode.Daily;
        RememberMode();
        var round = new WordGuessRound(_list.Daily(Today));
        if (Host.Settings.GameNotes.TryGetValue(DailyNoteKey, out string? note) && note.Split(':') is [var day, var guesses]
            && day == Today.DayNumber.ToString(CultureInfo.InvariantCulture))
            foreach (string g in guesses.Split(',', StringSplitOptions.RemoveEmptyEntries))
                round.Submit(WordList.Tiles(Code, g), words: null); // taken once already
        Start(round);
    }

    void Start(WordGuessRound round)
    {
        Anims.Clear();
        _round = round;
        _typed = "";
        _revealRow = -1;
        _flash = null;
        ResetTiles();
        Refresh();
        Host.HudChanged();
    }

    void RememberMode()
    {
        int mode = _mode == Mode.Practice ? 1 : 0;
        if (Get(ModeKey) == mode) return;
        Set(ModeKey, mode);
        Host.SaveSettings();
    }

    void SaveDaily()
    {
        Host.Settings.GameNotes[DailyNoteKey] = Today.DayNumber.ToString(CultureInfo.InvariantCulture) + ":" + string.Join(",", _round.Guesses.Select(g => string.Concat(g)));
        Host.SaveSettings();
    }

    // ------------------------------------------------------------------ typing

    public void TextTyped(string text)
    {
        if (_round.Over || Revealing) return;
        var before = Typed;
        var after = WordList.Tiles(Code, _typed + text);
        if (after.Length > WordList.Length) after = after[..WordList.Length];
        if (after.SequenceEqual(before))
        {
            if (text.Any(char.IsLetter) && before.Length < WordList.Length) Flash(L.F("Switch the keyboard to {0}", LanguageName));
            return;
        }
        _typed = string.Concat(after);
        if (before.Length == 0 && _round.Guesses.Count == 0) BeginRound(); // the first letter of a fresh word starts a round
        Host.Sound.Play("key", 0.25, 1.1 + 0.04 * after.Length);
        if (after.Length > before.Length) Pop(_round.Guesses.Count, after.Length - 1);
        Refresh();
    }

    public void KeyPressed(TypingKey key)
    {
        switch (key)
        {
            case TypingKey.Backspace:
                if (Revealing || _typed.Length == 0) return;
                var t = Typed;
                _typed = string.Concat(t[..^1]);
                Host.Sound.Play("key", 0.2, 0.9);
                Refresh();
                break;
            case TypingKey.WordBackspace:
                if (Revealing) return;
                _typed = "";
                Refresh();
                break;
            case TypingKey.Enter:
                Submit();
                break;
            case TypingKey.Escape:
                Host.ReleaseKeyboard(this);
                Host.HudChanged();
                break;
        }
    }

    public void KeyboardLost() => Host.HudChanged();

    string LanguageName => L.Languages.FirstOrDefault(l => l.Code == Code).Name ?? Code;

    void Submit()
    {
        if (_round.Over || Revealing) return;
        int row = _round.Guesses.Count;
        switch (_round.Submit(Typed, _list))
        {
            case WordSubmit.TooShort:
                Shake(row);
                Flash(L.T("Not enough letters"));
                return;
            case WordSubmit.NotAWord:
                Shake(row);
                Flash(L.T("Not in the word list"));
                return;
            case WordSubmit.Accepted:
                break;
            default:
                return;
        }
        _typed = "";
        Host.Stats.Add("words.guesses");
        if (Shown == Mode.Daily) SaveDaily();
        Host.Sound.Play("whoosh", 0.25, 1.3);
        Reveal(row);
        Host.HudChanged();
    }

    /// <summary>The row's tiles flip over one by one to show their marks; the verdict comes after the last.</summary>
    void Reveal(int row)
    {
        _revealRow = row;
        _revealed = 0;
        for (int i = 0; i < WordList.Length; i++)
        {
            int slot = i;
            var tile = _tiles[row, slot];
            Anims.Add(FlipTime, k =>
            {
                tile.Flip.ScaleY = Fx.ReducedMotion ? 1 : Math.Abs(Math.Cos(Math.PI * k));
                if (k >= 0.5 && _revealed <= slot)
                {
                    _revealed = slot + 1;
                    var mark = _round.Marks[row][slot];
                    Host.Sound.Play("board", 0.2, mark == WordMark.Green ? 1.6 : mark == WordMark.Yellow ? 1.35 : 1.0);
                    Refresh();
                }
            }, Ease.Linear, slot == WordList.Length - 1 ? () => Revealed(row) : null, slot * FlipStagger);
        }
        Host.Wake();
    }

    void Revealed(int row)
    {
        _revealRow = -1;
        Refresh();
        var top = BoardPoint(W / 2, GridTop + row * (Tile + Gap));
        if (_round.Won)
        {
            int n = _round.Guesses.Count;
            Host.Stats.Add("words.won");
            Host.Stats.Min("words.best", n);
            if (n <= 2) Host.Stats.Add("words.two");
            if (Shown == Mode.Daily) WonDaily();
            string praise = n switch
            {
                1 => L.T("GENIUS!"),
                2 => L.T("MAGNIFICENT!"),
                3 => L.T("IMPRESSIVE!"),
                4 => L.T("SPLENDID!"),
                5 => L.T("GREAT!"),
                _ => L.T("PHEW!"),
            };
            Host.Fx.Popup(top, praise, Themes.Themed(Themes.ClassicGold), 36, 2.4, L.F("{0} of {1} guesses", n, WordGuessRound.Tries));
            Host.Fx.Burst(top, Themes.Current.Confetti, 40, 520, 700, 7, 1.1);
            Host.Sound.Play("best", 0.75);
            Hop(row);
        }
        else if (_round.Lost)
        {
            Host.Fx.Popup(top, string.Concat(_round.Answer).ToUpper(CultureInfo.CurrentCulture), Colors.White, 34, 3, L.T("the word was"));
            Host.Sound.Play("buzzer", 0.4);
        }
        else return;
        Host.Stats.Add("words.played");
        if (Shown == Mode.Practice) _lastPractice = (_round.Guesses.Count, _round.Won, Code, _round.Grid());
        EndRound();
        Host.HudChanged();
    }

    void WonDaily()
    {
        int today = Today.DayNumber;
        if (Get(LastWonKey) == today) return; // another language's daily already counted today
        int streak = SpotBugStreak.After(Get(LastWonKey), Get(StreakKey), today, found: true);
        Set(StreakKey, streak);
        Set(LastWonKey, today);
        Host.Stats.Add("words.daily");
        Host.Stats.Max("words.streak", streak);
        Host.SaveSettings();
    }

    void Flash(string text)
    {
        _flash = text;
        _flashLeft = 1.8;
        Host.Sound.Play("key-bad", 0.35, 0.9);
        Refresh();
        Host.Wake();
    }

    // ------------------------------------------------------------------ motion

    void Pop(int row, int col)
    {
        var tile = _tiles[row, col];
        Anims.Add(0.12, k => tile.Flip.ScaleX = tile.Flip.ScaleY = 1 + 0.12 * Math.Sin(Math.PI * k), Ease.Linear);
        Host.Wake();
    }

    void Shake(int row)
    {
        var shake = _rowShake[row];
        Anims.Add(0.4, k => shake.X = Fx.ReducedMotion ? 0 : 7 * Math.Sin(k * Math.PI * 6) * (1 - k), Ease.Linear, () => shake.X = 0);
        Host.Wake();
    }

    /// <summary>A solved row: the tiles hop one after another.</summary>
    void Hop(int row)
    {
        if (Fx.ReducedMotion) return;
        for (int i = 0; i < WordList.Length; i++)
        {
            var hop = _tiles[row, i].Hop;
            Anims.Add(0.36, k => hop.Y = -14 * Math.Sin(Math.PI * k), Ease.OutQuad, () => hop.Y = 0, 0.1 * i);
        }
    }

    // ------------------------------------------------------------------ layout

    public override void Layout()
    {
        var a = Host.Arena;
        BuildKeys();
        _scale = Clamp(a.Height * 0.72 / _h, 0.7, 1.4);
        _scale = Math.Min(_scale, Math.Min((a.Height - 40) / _h, (a.Width - 20) / W));
        if (!_placed)
        {
            _placed = true;
            _origin = _handle.Saved() ?? new Vec2(a.Center.X - W * _scale / 2, a.Center.Y - _h * _scale / 2);
        }
        if (CheckSession() || LanOn && _mode == Mode.Daily) NewPractice();
        Place();
        Refresh();
        Host.HudChanged();
    }

    void Place()
    {
        var a = Host.Arena;
        double w = W * _scale, h = _h * _scale, top = a.Top + DragHandle.Height + 12;
        _origin = new Vec2(Clamp(_origin.X, a.Left + 8, Math.Max(a.Left + 8, a.Right - w - 8)), Clamp(_origin.Y, top, Math.Max(top, a.Bottom - h - 8)));
        _size.ScaleX = _size.ScaleY = _scale;
        _move.X = _origin.X;
        _move.Y = _origin.Y;
        _handle.Show(Panel);
    }

    Rect Panel => new(_origin.X, _origin.Y, W * _scale, _h * _scale);
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
        if (_modeRect.Contains(q))
        {
            if (LanOn) return false;
            Host.Sound.Play("key", 0.3, 1.2);
            if (Shown == Mode.Daily) NewPractice();
            else OpenDaily();
            return false;
        }
        if (_newRect.Contains(q) && Shown == Mode.Practice)
        {
            if (LanOn && _round.Guesses.Count == 0 && !_round.Over) return false; // already a fresh word of the race
            Host.Sound.Play("board", 0.3, 1.3);
            NewPractice();
            if (LanOn) BeginRound();
            return false;
        }
        if (_langRect.Contains(q))
        {
            // one chip for the language: each click goes on to the next
            if (Revealing || LanOn && _racing) return false;
            _lang = (_lang + 1) % WordList.Codes.Length;
            Set(LangKey, _lang + 1);
            Host.SaveSettings();
            _list = WordList.For(Code);
            Host.Sound.Play("key", 0.3, 1.1);
            BuildKeys();
            Place();
            if (Shown == Mode.Daily) OpenDaily();
            else NewPractice();
            return false;
        }
        foreach (var key in _keys)
        {
            if (!key.Rect.Contains(q)) continue;
            if (key.Letter == "") KeyPressed(TypingKey.Enter);
            else if (key.Letter == "⌫") KeyPressed(TypingKey.Backspace);
            else TextTyped(key.Letter);
            return false;
        }
        if (!_round.Over)
        {
            Host.CaptureKeyboard(this, Panel, L.T(Title));
            Host.HudChanged();
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
        bool flashing = _flash != null;
        if (flashing && (_flashLeft -= dt) <= 0)
        {
            _flash = null;
            Refresh();
        }
        return Anims.Update(dt) || _handle.Dragging || flashing;
    }

    // ------------------------------------------------------------------ demo

    double _demoT, _demoWait = 1;
    string[]? _demoPlan;

    /// <summary>
    /// Plays by itself: picks a word that fits every mark so far (the first one at random), types it a letter at a time
    /// and presses Enter; after a word it takes a new practice word, now and then in another language.
    /// </summary>
    public override void DemoTick()
    {
        _demo = true;
        if ((_demoT += 0.15) < _demoWait || Revealing) return;
        _demoT = 0;
        _demoWait = 0.25 + Rng.NextDouble() * 0.35;
        if (_round.Over)
        {
            _demoWait = 3.5;
            if (_demoPlan != null)
            {
                _demoPlan = null;
                return; // a moment to see the solved board
            }
            if (Rng.NextDouble() < 0.3)
            {
                _lang = (_lang + 1) % WordList.Codes.Length;
                _list = WordList.For(Code);
                BuildKeys();
                Place();
            }
            NewPractice();
            return;
        }
        if (_demoPlan == null || Typed.Length == 0 && !Fits(_demoPlan))
        {
            var fits = _list.Answers.Select(a => WordList.Tiles(Code, a)).Where(Fits).ToList();
            _demoPlan = fits.Count > 0 ? fits[Rng.Next(fits.Count)] : _list.Random(Rng);
        }
        var typed = Typed;
        if (typed.Length < WordList.Length) TextTyped(_demoPlan[typed.Length]);
        else
        {
            KeyPressed(TypingKey.Enter);
            _demoWait = 2;
        }
    }

    /// <summary>A word that would have got exactly the marks every guess so far got.</summary>
    bool Fits(string[] word)
    {
        for (int g = 0; g < _round.Guesses.Count; g++)
            if (!WordGuessRound.Mark(word, _round.Guesses[g]).SequenceEqual(_round.Marks[g])) return false;
        return true;
    }

    // ------------------------------------------------------------------ the board

    static (Border Box, TextBlock Text) Chip()
    {
        var text = new TextBlock { FontFamily = Fx.Font, FontSize = 12, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        return (new Border { Height = ChipH, CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(1), Padding = new Thickness(10, 0), Child = text, IsHitTestVisible = false }, text);
    }

    void BuildView()
    {
        _board.Children.Add(_back);
        foreach (var c in new[] { _modeChip, _langChip, _newChip }) _board.Children.Add(c.Box);
        _board.Children.Add(Art.At(_status, Pad, StatusY));
        for (int r = 0; r < WordGuessRound.Tries; r++)
        {
            _rowShake[r] = new TranslateTransform();
            var row = new Canvas { RenderTransform = _rowShake[r], IsHitTestVisible = false };
            for (int i = 0; i < WordList.Length; i++)
            {
                var flip = new ScaleTransform(1, 1);
                var hop = new TranslateTransform();
                var letter = new TextBlock
                {
                    FontFamily = Fx.Font, FontSize = 22, FontWeight = FontWeight.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
                var box = new Border
                {
                    Width = Tile, Height = Tile, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(2), Child = letter, IsHitTestVisible = false,
                    RenderTransformOrigin = RelativePoint.Center, RenderTransform = new TransformGroup { Children = { flip, hop } },
                };
                _tiles[r, i] = new TileView { Box = box, Letter = letter, Flip = flip, Hop = hop };
                row.Children.Add(Art.At(box, GridX + i * (Tile + Gap), GridTop + r * (Tile + Gap)));
            }
            _board.Children.Add(row);
        }
        _board.Children.Add(_keyLayer);
    }

    void ResetTiles()
    {
        foreach (var t in _tiles)
        {
            t.Flip.ScaleX = t.Flip.ScaleY = 1;
            t.Hop.Y = 0;
        }
        foreach (var s in _rowShake) s.X = 0;
    }

    /// <summary>
    /// The drawn keyboard for the language: letter rows centred, Enter and ⌫ at the ends of the last letter row (Uzbek
    /// keeps its own letters on a row of their own under it). Keys are as wide as the widest row allows.
    /// </summary>
    void BuildKeys()
    {
        if (_keysFor == _lang) return;
        _keysFor = _lang;
        _keyLayer.Children.Clear();
        _keys.Clear();
        var letters = WordList.KeyRows(Code);
        var rows = letters.Select(r => r.Select(l => (Label: l, Units: l.Length > 1 ? 1.4 : 1.0)).ToList()).ToList();
        int last = Code == "uz" ? 2 : rows.Count - 1;
        rows[last].Insert(0, ("", 1.5));
        rows[last].Add(("⌫", 1.5));
        double inner = W - 2 * Pad;
        double unit = Math.Min(MaxKeyUnit, rows.Min(r => (inner - (r.Count - 1) * KeyGap) / r.Sum(k => k.Units)));
        for (int r = 0; r < rows.Count; r++)
        {
            double width = rows[r].Sum(k => k.Units) * unit + (rows[r].Count - 1) * KeyGap;
            double x = (W - width) / 2, y = KeysTop + r * (KeyH + KeyGap);
            foreach (var (label, units) in rows[r])
            {
                double w = units * unit;
                var text = new TextBlock
                {
                    FontFamily = Fx.Font, FontSize = label.Length == 0 ? 11 : 15, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
                var box = new Border { Width = w, Height = KeyH, CornerRadius = new CornerRadius(5), Child = text, IsHitTestVisible = false };
                _keyLayer.Children.Add(Art.At(box, x, y));
                _keys.Add(new KeyView { Letter = label, Rect = new Rect(x, y, w, KeyH), Box = box, Text = text });
                x += w + KeyGap;
            }
        }
        _h = KeysTop + rows.Count * (KeyH + KeyGap) - KeyGap + Pad;
        _back.Height = _h;
    }

    Color MarkColor(WordMark m) => m switch
    {
        WordMark.Green => Art.ColorBlind ? BlindGreen : Green,
        WordMark.Yellow => Art.ColorBlind ? BlindYellow : Yellow,
        _ => Gray,
    };

    /// <summary>Brings every piece up to date with the round: chips, status line, tiles and keys.</summary>
    void Refresh()
    {
        if (_round == null) return;
        BuildKeys();
        var t = Themes.Current;
        var panel = Art.Blend(t.Ink, Color.FromRgb(18, 20, 26), 0.5);
        _back.Background = Art.Brush(Color.FromArgb(240, panel.R, panel.G, panel.B));
        _back.BorderBrush = Art.Brush(Art.Blend(t.Accent, t.Ink, 0.45));

        // the chips: mode, then the language (a click goes on to the next); New word on the right while practising
        double x = Pad;
        PaintChip(_modeChip, Shown == Mode.Daily ? L.F("Daily #{0}", WordList.DailyNumber(Today)) : L.T("Practice"), on: true, enabled: !LanOn);
        _modeRect = PlaceChip(_modeChip, ref x);
        x += 6;
        PaintChip(_langChip, Code.ToUpperInvariant() + " ▸", on: false, enabled: !(LanOn && _racing));
        _langRect = PlaceChip(_langChip, ref x);
        _newChip.Box.IsVisible = Shown == Mode.Practice;
        PaintChip(_newChip, L.T("New word"), on: Finished, enabled: true);
        _newChip.Box.Measure(Size.Infinity);
        double nx = W - Pad - _newChip.Box.DesiredSize.Width;
        _newRect = _newChip.Box.IsVisible ? PlaceChip(_newChip, ref nx) : default;

        _status.Text = _flash ?? StatusLine();
        _status.Foreground = Art.Brush(_flash != null ? t.Gold : Art.Blend(t.HudFront, t.Ink, 0.25));

        // the tiles
        var typed = Typed;
        var empty = Art.Brush(Color.FromRgb(58, 60, 66));
        var full = Art.Brush(Color.FromRgb(120, 124, 134));
        var none = Art.Brush(Color.FromArgb(0, 0, 0, 0));
        for (int r = 0; r < WordGuessRound.Tries; r++)
            for (int i = 0; i < WordList.Length; i++)
            {
                var tile = _tiles[r, i];
                string letter = r < _round.Guesses.Count ? _round.Guesses[r][i] : r == _round.Guesses.Count && !_round.Over && i < typed.Length ? typed[i] : "";
                bool marked = r < _round.Guesses.Count && (r != _revealRow || i < _revealed);
                tile.Letter.Text = letter.ToUpper(CultureInfo.CurrentCulture);
                tile.Letter.FontSize = letter.Length > 1 ? 17 : 22;
                if (marked)
                {
                    var c = MarkColor(_round.Marks[r][i]);
                    tile.Box.Background = Art.Brush(c);
                    tile.Box.BorderBrush = Art.Brush(c);
                    tile.Letter.Foreground = Brushes.White;
                }
                else
                {
                    tile.Box.Background = none;
                    tile.Box.BorderBrush = letter.Length > 0 ? full : empty;
                    tile.Letter.Foreground = Art.Brush(Color.FromRgb(236, 238, 242));
                }
            }

        // the keys, coloured by the best mark each letter has had (the row still flipping over not yet)
        var marks = new Dictionary<string, WordMark>();
        for (int g = 0; g < _round.Guesses.Count; g++)
        {
            if (g == _revealRow) continue;
            for (int i = 0; i < WordList.Length; i++)
                if (!marks.TryGetValue(_round.Guesses[g][i], out var m) || _round.Marks[g][i] > m) marks[_round.Guesses[g][i]] = _round.Marks[g][i];
        }
        foreach (var key in _keys)
        {
            key.Text.Text = key.Letter == "" ? L.T("Enter") : key.Letter.ToUpper(CultureInfo.CurrentCulture);
            Color back = marks.TryGetValue(key.Letter, out var m) ? MarkColor(m) : Color.FromRgb(118, 122, 132);
            if (marks.ContainsKey(key.Letter) && m == WordMark.Gray) back = Color.FromRgb(46, 48, 54);
            key.Box.Background = Art.Brush(back);
            key.Text.Foreground = Art.Brush(marks.TryGetValue(key.Letter, out var g2) && g2 == WordMark.Gray ? Color.FromRgb(130, 134, 144) : Colors.White);
        }
        Host.Wake();
    }

    string StatusLine()
    {
        if (Shown == Mode.Daily)
            return Finished
                ? Streak > 0 ? L.F("Daily word done · streak {0} · ☰ → Share", Streak) : L.T("Daily word done · ☰ → Share")
                : L.T("The daily word · the same for everyone today");
        if (LanOn) return L.T("Race on the LAN · the same word when you both play one language");
        return Finished ? L.T("Click New word for another") : L.T("A practice word · as many as you like");
    }

    void PaintChip((Border Box, TextBlock Text) chip, string text, bool on, bool enabled)
    {
        var t = Themes.Current;
        chip.Text.Text = text;
        chip.Box.Background = Art.Brush(on ? Art.Blend(t.Accent, t.Ink, 0.35) : Art.Blend(t.Ink, Colors.Black, 0.2));
        chip.Box.BorderBrush = Art.Brush(on ? t.Accent : Art.Blend(t.Accent, t.Ink, 0.7));
        chip.Text.Foreground = Art.Brush(enabled ? t.HudFront : Art.Blend(t.HudFront, t.Ink, 0.6));
    }

    static Rect PlaceChip((Border Box, TextBlock Text) chip, ref double x)
    {
        chip.Box.Measure(Size.Infinity);
        double w = chip.Box.DesiredSize.Width;
        Art.At(chip.Box, x, ChipY);
        var rect = new Rect(x, ChipY, w, ChipH);
        x += w + 5;
        return rect;
    }
}
