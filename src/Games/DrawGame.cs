using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using DeskArcade.Engine;
using static DeskArcade.Games.DrawRules;

namespace DeskArcade.Games;

/// <summary>What one player sees of a Draw &amp; Guess table: the drawing, the scores, the guesses, and the word only when theirs to see.</summary>
public sealed class DrawView : CardRoomView
{
    public int Phase { get; set; }
    public int Drawer { get; set; }
    public int TurnNumber { get; set; }
    public int Turns { get; set; }
    public int Hints { get; set; }
    public double TimeLeft { get; set; }
    public List<string> Strokes { get; set; } = new();
    public List<GuessLine> Feed { get; set; } = new();
    public int[] Scores { get; set; } = Array.Empty<int>();
    public int[] TurnPoints { get; set; } = Array.Empty<int>();
    public bool[] Guessed { get; set; } = Array.Empty<bool>();
    /// <summary>The word in English, Russian and Uzbek: for the drawer, and for everyone once the turn is over.</summary>
    public string[] Word { get; set; } = Array.Empty<string>();
    /// <summary>The three words to pick from, each in the three languages: for the drawer while choosing.</summary>
    public string[][] Choices { get; set; } = Array.Empty<string[]>();
    /// <summary>The hint in each language ("_ a _ _"): for everyone else while it is drawn.</summary>
    public string[] Hint { get; set; } = Array.Empty<string>();

    public bool Drawing => Phase == (int)Stage.Drawing;
    public bool Choosing => Phase == (int)Stage.Choosing;
    public bool Revealed => Phase == (int)Stage.Reveal;
    public bool IDraw => Drawing && Seat == Drawer;
    public bool IChoose => Choosing && Seat == Drawer;
    public bool CanGuess => Drawing && Seat != Drawer && Seat < Guessed.Length && !Guessed[Seat];
    public int PlaceOf(int seat) => 1 + Scores.Count(s => s > Scores[seat]);

    static string[] Forms(DrawWord w) => new[] { w.In("en"), w.In("ru"), w.In("uz") };

    public static DrawView Of(DrawRules r, IReadOnlyList<DrawWord> words, int seat)
    {
        bool drawer = seat == r.Drawer, open = r.Phase is Stage.Reveal or Stage.Over;
        var word = r.Current;
        return new DrawView
        {
            Phase = (int)r.Phase, Drawer = r.Drawer, TurnNumber = r.TurnNumber, Turns = r.Players * r.Rounds, Hints = r.Hints, TimeLeft = r.TimeLeft,
            Strokes = r.Strokes.Select(s => s.Encode()).ToList(),
            // a "close" is for its guesser's eyes only: the others see a wrong guess
            Feed = r.Feed.Select(f => f.Kind == GuessKind.Close && f.Seat != seat ? f with { Kind = GuessKind.Wrong } : f).ToList(),
            Scores = r.Scores.ToArray(), TurnPoints = r.TurnPoints.ToArray(), Guessed = r.Guessed.ToArray(), Over = r.Over,
            Word = word != null && (drawer || open || r.Guessed[seat]) ? Forms(word) : Array.Empty<string>(),
            Choices = drawer && r.Phase == Stage.Choosing ? r.Choices.Select(i => Forms(words[i])).ToArray() : Array.Empty<string[]>(),
            Hint = word != null && r.Phase == Stage.Drawing && !drawer ? Forms(word).Select(f => HintOf(f, r.Hints)).ToArray() : Array.Empty<string>(),
        };
    }
}

/// <summary>
/// Draw &amp; Guess (see <see cref="DrawRules"/>): one player draws a word on a board over the desktop with the mouse,
/// and the others type their guesses in the typing window. Rooms of up to eight co-workers on the local network, the
/// drawing reaching the others stroke by stroke; alone, two or three computer players join in. When a computer draws,
/// it draws one of the pictures it knows (<see cref="DrawPictures"/>) a stroke at a time, and you guess. The computer
/// players cannot see a drawing: they guess once a little has been drawn, sooner at a higher level, and now and then
/// get it wrong first. The board has a grip and remembers where it was put.
/// </summary>
public sealed class DrawGame : CardRoomGame<DrawView>, IKeySink
{
    const double Board = 460, BoardX = 24, BoardY = 60, PanelX = 510;
    /// <summary>The colours: ink, red, blue, green, orange, brown.</summary>
    public static readonly Color[] Palette =
    {
        Color.FromRgb(29, 33, 41), Color.FromRgb(216, 51, 63), Color.FromRgb(47, 128, 237), Color.FromRgb(46, 168, 79),
        Color.FromRgb(242, 154, 31), Color.FromRgb(139, 90, 43),
    };
    static readonly double[] Widths = { 2.5, 4, 6.5, 9 };

    static IReadOnlyList<DrawWord>? _words;
    DrawRules? _rules;
    readonly System.Diagnostics.Stopwatch _clock = new();
    double _lastStep;
    int _color, _size = 1;
    readonly List<(double X, double Y)> _live = new();
    Polyline? _liveLine;
    string _typed = "";
    Rect _guessBox;
    double _viewAt;
    int _seenFeed, _feedGame = -1, _announcedTurn = -1, _finishedGame = -1;
    string? _share;

    // the computers' plans for the turn: when each will guess (and what), and the picture being drawn
    readonly Dictionary<int, List<(double At, string Text)>> _plans = new();
    int _planTurn = -1, _cpuStroke;
    double _cpuStrokeAt;

    public DrawGame(IGameHost host) : base(host) => _clock.Start();

    public override string Id => "draw";
    public override string Title => "Draw & Guess";
    public override int MinPlayers => 2;
    public override int MaxPlayers => 8;
    protected override string Tag => "dg";
    protected override int DemoCpus => 2;
    protected override Size TableSize => new(980, 620);
    protected override string Tagline => L.T("Draw the word for the others to guess · type your guesses");
    protected override IEnumerable<(string Text, int Cpus)> SoloChoices => new[] { (L.F("With {0} computers", 2), 2), (L.F("With {0} computers", 3), 3) };

    /// <summary>The words, from draw/words.json built into the program.</summary>
    public static IReadOnlyList<DrawWord> Words => _words ??= LoadWords();

    static IReadOnlyList<DrawWord> LoadWords()
    {
        using var stream = typeof(DrawGame).Assembly.GetManifestResourceStream("draw/words.json");
        if (stream == null) return Array.Empty<DrawWord>();
        using var doc = JsonDocument.Parse(stream);
        string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        return doc.RootElement.EnumerateArray().Select(e => new DrawWord(Str(e, "en"), Str(e, "ru"), Str(e, "uz"), Str(e, "tag")))
            .Where(w => w.En.Length > 0 && w.Ru.Length > 0 && w.Uz.Length > 0).ToList();
    }

    /// <summary>Whether the computer has a picture of a word.</summary>
    public static bool Pictured(int word) => word >= 0 && word < Words.Count && DrawPictures.All.ContainsKey(Words[word].In("en"));

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        s.Rotor.Children.Add(Art.At(new Rectangle { Width = 18, Height = 15, RadiusX = 2, RadiusY = 2, Fill = Brushes.White, Stroke = Art.Brush("#555"), StrokeThickness = 1 }, -9, -8));
        s.Rotor.Children.Add(Art.PathOf("M-6,3 Q-2,-6 2,0 T7,-4", null, Art.Brush("#D8333F"), 1.6));
        s.Rotor.Children.Add(Art.PathOf("M4,9 L10,1 L12,3 L6,11 Z", Art.Brush("#F29A1F"), Art.Brush("#8B5A2B"), 0.8));
        return s;
    }

    public override HudInfo Hud => new(
        View == null ? "—" : View.Scores[View.Seat].ToString(CultureInfo.InvariantCulture),
        Status(),
        L.F("Wins {0}", Host.Stats.Get("draw.wins")));

    public override string? ShareText => _share;

    // ------------------------------------------------------------------ the rules

    protected override bool HasRules => _rules != null;
    protected override int RulesVersion => _rules?.Version ?? -1;
    protected override void DropRules() => _rules = null;

    protected override void NewRules(int players)
    {
        _rules = new DrawRules(players, Words, Rng, players <= 3 ? 2 : 1, IsComputer, Pictured);
        _lastStep = _clock.Elapsed.TotalSeconds;
        _planTurn = -1;
    }

    protected override bool RulesAct(int seat, RoomMove move)
    {
        var r = _rules!;
        switch (move.Kind)
        {
            case "choose": return r.Choose(seat, move.A);
            case "stroke": return DrawStroke.Decode(move.Text) is { } s && r.AddStroke(seat, s);
            case "undo": return r.Undo(seat);
            case "clear": return r.Clear(seat);
            case "guess": return r.Guess(seat, move.Text) != null;
        }
        return false;
    }

    protected override DrawView ViewOf(int seat) => DrawView.Of(_rules!, Words, seat);

    /// <summary>Seconds into the drawing, on the rules' clock.</summary>
    static double Elapsed(DrawRules r) => DrawSeconds - r.TimeLeft;

    /// <summary>
    /// What is due: a computer's word, stroke or guess when its time has come, else the clock's next tick. The clock only
    /// runs while a game is on, so a finished table asks for nothing.
    /// </summary>
    protected override TableDue? NextDue()
    {
        if (_rules is not { Over: false } r) return null;
        PlanTurn(r);
        if (r.Phase == Stage.Choosing && IsComputer(r.Drawer) && r.TimeLeft <= ChooseSeconds - 1.6) return new TableDue(r.Drawer, 0.05);
        if (r.Phase == Stage.Drawing)
        {
            if (IsComputer(r.Drawer) && _cpuStroke < PictureOf(r).Length && Elapsed(r) >= _cpuStrokeAt) return new TableDue(r.Drawer, 0.05);
            foreach (int s in r.StillGuessing)
                if (IsComputer(s) && _plans.TryGetValue(s, out var plan) && plan.Count > 0 && Elapsed(r) >= plan[0].At && Drawn(r)) return new TableDue(s, 0.05);
        }
        return new TableDue(-1, 0.2);
    }

    /// <summary>A computer only guesses once there is a little to go on: three strokes, or a long one.</summary>
    static bool Drawn(DrawRules r) => r.Strokes.Count >= 3 || r.PointsDrawn >= 60;

    DrawStroke[] PictureOf(DrawRules r) => r.Current is { } w && DrawPictures.All.TryGetValue(w.In("en"), out var p) ? p : Array.Empty<DrawStroke>();

    /// <summary>At the start of a turn: when each computer guesser will guess, by its level, with a wrong guess or two first.</summary>
    void PlanTurn(DrawRules r)
    {
        if (_planTurn == r.TurnNumber * 2 + (r.Phase == Stage.Drawing ? 1 : 0)) return;
        _planTurn = r.TurnNumber * 2 + (r.Phase == Stage.Drawing ? 1 : 0);
        _plans.Clear();
        _cpuStroke = 0;
        _cpuStrokeAt = 1.2;
        if (r.Phase != Stage.Drawing || r.Current is not { } word) return;
        var (from, to, miss) = CpuLevel switch { 1 => (0.5, 0.95, 0.35), 2 => (0.3, 0.8, 0.2), 3 => (0.18, 0.6, 0.1), _ => (0.1, 0.45, 0.03) };
        string lang = L.Code is "ru" or "uz" ? L.Code : "en";
        foreach (int s in r.StillGuessing.Where(IsComputer))
        {
            var plan = new List<(double At, string Text)>();
            double at = DrawSeconds * (from + Rng.NextDouble() * (to - from));
            int wrongs = Rng.Next(0, 3);
            for (int i = 0; i < wrongs; i++)
                plan.Add((at * (0.35 + 0.25 * i + Rng.NextDouble() * 0.15), Words[Rng.Next(Words.Count)].In(lang)));
            if (Rng.NextDouble() >= miss) plan.Add((at, word.In(lang)));
            _plans[s] = plan.OrderBy(p => p.At).ToList();
        }
    }

    protected override bool AutoStep()
    {
        double now = _clock.Elapsed.TotalSeconds, dt = Math.Clamp(now - _lastStep, 0, 1);
        _lastStep = now;
        return _rules != null && _rules.Step(dt).Count > 0;
    }

    protected override Task<RoomMove?> Think(int seat)
    {
        var r = _rules!;
        RoomMove? move = null;
        if (r.Phase == Stage.Choosing && seat == r.Drawer) move = new RoomMove("choose", Rng.Next(r.Choices.Length));
        else if (r.Phase == Stage.Drawing && seat == r.Drawer)
        {
            var picture = PictureOf(r);
            if (_cpuStroke < picture.Length)
            {
                move = new RoomMove("stroke", Text: picture[_cpuStroke].Encode());
                _cpuStroke++;
                _cpuStrokeAt = Elapsed(r) + 1.0 + Rng.NextDouble() * 1.2;
            }
        }
        else if (_plans.TryGetValue(seat, out var plan) && plan.Count > 0)
        {
            move = new RoomMove("guess", Text: plan[0].Text);
            plan.RemoveAt(0);
        }
        return Task.FromResult(move);
    }

    protected override RoomMove? GuestDemoMove(DrawView v)
    {
        if (v.IChoose) return new RoomMove("choose", 0);
        if (v.IDraw && v.Strokes.Count < 6) return new RoomMove("stroke", Text: Squiggle().Encode());
        if (v.CanGuess && Rng.NextDouble() < 0.25) return new RoomMove("guess", Text: Words[Rng.Next(Words.Count)].In("en"));
        return null;
    }

    /// <summary>A demo's scribble: a wavy line somewhere on the board.</summary>
    DrawStroke Squiggle()
    {
        double x = 40 + Rng.NextDouble() * 150, y = 40 + Rng.NextDouble() * 170, a = Rng.NextDouble() * 6;
        var points = Enumerable.Range(0, 14).Select(i => (x + i * 5.0, y + 18 * Math.Sin(a + i * 0.6))).ToList();
        return DrawStroke.FromPoints(points, 255, 255, Rng.Next(Palette.Length), 1);
    }

    protected override int TurnOf(DrawView v) => v.Choosing || v.Drawing ? v.Drawer : -1;
    protected override bool MyMove(DrawView v) => v.IChoose || v.IDraw || v.CanGuess;

    protected override void Moved(int seat, RoomMove move)
    {
        if (move.Kind == "stroke") Host.Sound.Play("swish", 0.08, 1.8);
    }

    // ------------------------------------------------------------------ status and results

    string Lang => L.Code is "ru" or "uz" ? L.Code : "en";

    string WordOf(string[] forms) => forms.Length == 3 ? forms[Lang switch { "ru" => 1, "uz" => 2, _ => 0 }] : "";

    string Status()
    {
        if (RoomStatus() is { } room) return room;
        if (View is not { } v) return L.T("Pick a table to start a game");
        if (v.Over) return v.PlaceOf(v.Seat) == 1 ? L.T("You won · click New game") : L.F("{0} won · click New game", Name(Array.IndexOf(v.Scores, v.Scores.Max())));
        if (v.IChoose) return L.T("Pick a word to draw");
        if (v.Choosing) return L.F("{0} is picking a word", Name(v.Drawer));
        if (v.IDraw) return L.F("Draw: {0}", WordOf(v.Word));
        if (v.Revealed) return L.F("The word was: {0}", WordOf(v.Word));
        if (v.Seat < v.Guessed.Length && v.Guessed[v.Seat]) return L.T("You got it! · the others are still guessing");
        return L.F("{0} is drawing · type your guess", Name(v.Drawer));
    }

    /// <summary>Seconds left on the clock: the rules' own for the host, and for a guest, the view's counted down since it came.</summary>
    double ShownTimeLeft(DrawView v) => _rules != null ? _rules.TimeLeft : Math.Max(0, v.TimeLeft - (_clock.Elapsed.TotalSeconds - _viewAt));

    protected override void Announce(DrawView v)
    {
        int me = v.Seat;
        if (_feedGame != v.Game)
        {
            _feedGame = v.Game;
            _seenFeed = 0;
        }
        // new lines in the feed: a sound for each, and mine said out loud
        int fresh = Math.Max(0, v.Feed.Count - Math.Min(_seenFeed, v.Feed.Count));
        if (fresh > 0)
        {
            foreach (var line in v.Feed.Skip(v.Feed.Count - fresh))
            {
                if (line.Kind == GuessKind.Right)
                {
                    Host.Sound.Play("score", 0.5, line.Seat == me ? 1 : 1.3);
                    if (line.Seat == me)
                    {
                        Host.Stats.Add("draw.guessed");
                        Host.Fx.Popup(new Vec2(Area.Left + BoardX + Board / 2, Area.Top + BoardY + Board / 2), L.T("YOU GOT IT!"), Gold, 34, 1.6,
                            L.F("+{0}", v.TurnPoints[me]));
                        if (v.Feed.Count(f => f.Kind == GuessKind.Right) == 1) Host.Stats.Add("draw.first");
                    }
                    else if (me == v.Drawer) Host.Stats.Add("draw.understood");
                }
                else if (line.Kind == GuessKind.Close && line.Seat == me)
                {
                    Host.Fx.Popup(new Vec2(Area.Left + PanelX + 200, Area.Top + 470), L.T("close!"), Gold, 22, 1.0);
                    Host.Sound.Play("pop", 0.4, 1.2);
                }
                else Host.Sound.Play("click", 0.15, 1.6);
            }
        }
        _seenFeed = v.Feed.Count;
        int turnKey = v.Game * 100 + v.TurnNumber;
        if (v.Revealed && _announcedTurn != turnKey)
        {
            _announcedTurn = turnKey;
            Host.Sound.Play(v.Guessed.Where((_, s) => s != v.Drawer).Any(g => g) ? "done" : "buzzer", 0.4);
        }
        if (!v.Over || _finishedGame == v.Game) return;
        _finishedGame = v.Game;
        int place = v.PlaceOf(me);
        var at = new Vec2(Area.Center.X, Area.Top + Area.Height * 0.3);
        Host.Stats.Add("draw.games");
        if (place == 1)
        {
            Host.Stats.Add("draw.wins");
            if (!Solo) Host.Stats.Add("lan.wins");
            Host.Fx.Popup(at, L.T("YOU WIN!"), Gold, 42, 2.6, L.F("{0} points", v.Scores[me]));
            Host.Fx.Burst(at, Themes.Current.Confetti, 50, 540, 700, 7, 1.1);
            Host.Sound.Play("best", 0.8);
        }
        else
        {
            Host.Fx.Popup(at, L.F("PLACE {0} OF {1}", place, v.Players), Colors.White, 36, 2.4, L.F("{0} points", v.Scores[me]));
            Host.Sound.Play("board", 0.4);
        }
        _share = L.F("Draw & Guess · place {0} of {1} with {2} points ✏️", place, v.Players, v.Scores[me]);
        RecordRivals(v, s => v.Scores[s]);
    }

    // ------------------------------------------------------------------ drawing on the board

    Rect BoardRect => new(BoardX, BoardY, Board, Board);

    protected override bool PressAt(Point p)
    {
        if (View is not { IDraw: true } || !BoardRect.Contains(p)) return false;
        _live.Clear();
        _live.Add((p.X - BoardX, p.Y - BoardY));
        _liveLine = null;
        Draw();
        return true;
    }

    protected override void DragTo(Point p)
    {
        if (View is not { IDraw: true } || _live.Count == 0) return;
        var q = (Math.Clamp(p.X - BoardX, 0, Board), Math.Clamp(p.Y - BoardY, 0, Board));
        var last = _live[^1];
        if (Math.Abs(q.Item1 - last.X) + Math.Abs(q.Item2 - last.Y) < 1.5) return;
        _live.Add(q);
        if (_liveLine != null) _liveLine.Points.Add(new Point(BoardX + q.Item1, BoardY + q.Item2));
        // a long stroke goes out in pieces, so the others see it coming
        if (_live.Count >= 160) SendLive(keepEnd: true);
    }

    protected override void Released() => SendLive(keepEnd: false);

    void SendLive(bool keepEnd)
    {
        if (_live.Count == 0) return;
        var points = _live.Count == 1 ? new List<(double X, double Y)> { _live[0], (_live[0].X + 1, _live[0].Y + 1) } : _live.ToList();
        var end = _live[^1];
        _live.Clear();
        if (keepEnd) _live.Add(end);
        else _liveLine = null;
        Act(new RoomMove("stroke", Text: DrawStroke.FromPoints(points, Board, Board, _color, _size).Encode()));
        Host.Stats.Add("draw.strokes");
    }

    // ------------------------------------------------------------------ guessing (the typing window)

    public void TextTyped(string text)
    {
        if (View is not { CanGuess: true }) return;
        _typed = (_typed + text).Length > 40 ? _typed : _typed + text;
        Draw();
    }

    public void KeyPressed(TypingKey key)
    {
        switch (key)
        {
            case TypingKey.Enter when _typed.Trim().Length > 0 && View is { CanGuess: true }:
                Act(new RoomMove("guess", Text: _typed.Trim()));
                _typed = "";
                break;
            case TypingKey.Backspace when _typed.Length > 0:
                _typed = _typed[..^1];
                break;
            case TypingKey.WordBackspace:
                _typed = _typed.TrimEnd();
                int cut = _typed.LastIndexOf(' ');
                _typed = cut < 0 ? "" : _typed[..(cut + 1)];
                break;
            case TypingKey.Escape:
                Host.ReleaseKeyboard(this);
                break;
        }
        Draw();
    }

    public void KeyboardLost() => Draw();

    void OpenTyping()
    {
        var box = new Rect(Area.Left + _guessBox.X, Area.Top + _guessBox.Y, _guessBox.Width, _guessBox.Height);
        Host.CaptureKeyboard(this, box, L.T("Your guess"));
    }

    // ------------------------------------------------------------------ the table

    protected override void DrawTable(DrawView v, DrawView? prev, bool newDeal)
    {
        if (prev == null || prev.Version != v.Version) _viewAt = _clock.Elapsed.TotalSeconds;
        if (!v.CanGuess)
        {
            _typed = "";
            if (Host.HasKeyboard(this)) Host.ReleaseKeyboard(this);
        }
        DrawBoard(v);
        DrawTopBar(v);
        DrawPlayers(v);
        DrawFeed(v);
        DrawControls(v);
    }

    void DrawBoard(DrawView v)
    {
        var b = BoardRect;
        Place(new Border { Width = b.Width, Height = b.Height, CornerRadius = new CornerRadius(10), Background = Brushes.White, BorderBrush = Art.Brush("#8A93A6"), BorderThickness = new Thickness(2), IsHitTestVisible = false }, b.X, b.Y);
        foreach (string code in v.Strokes)
        {
            if (DrawStroke.Decode(code) is not { } s) continue;
            var line = new Polyline
            {
                Stroke = Art.Brush(Palette[Math.Clamp(s.Color, 0, Palette.Length - 1)]), StrokeThickness = Widths[Math.Clamp(s.Size, 0, Widths.Length - 1)],
                StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round, IsHitTestVisible = false,
            };
            for (int i = 0; i < s.Count; i++) line.Points.Add(new Point(b.X + s.Points[i * 2] / 255.0 * b.Width, b.Y + s.Points[i * 2 + 1] / 255.0 * b.Height));
            if (s.Count == 1) line.Points.Add(new Point(line.Points[0].X + 0.5, line.Points[0].Y + 0.5));
            Felt.Children.Add(line);
        }
        if (v.IDraw && _live.Count > 0)
        {
            // the stroke being drawn, before it goes out
            _liveLine = new Polyline
            {
                Stroke = Art.Brush(Palette[_color]), StrokeThickness = Widths[_size], StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round, IsHitTestVisible = false,
            };
            foreach (var p in _live) _liveLine.Points.Add(new Point(b.X + p.X, b.Y + p.Y));
            Top.Children.Add(_liveLine);
        }
        // what goes over the board between drawings
        if (v.IChoose)
        {
            Shade(b);
            Label(L.T("Pick a word to draw"), b.Center.X, b.Top + 110, 22, Colors.White, center: true, into: Top);
            for (int i = 0; i < v.Choices.Length; i++)
            {
                int option = i;
                Button(WordOf(v.Choices[i]), b.Center.X, b.Top + 160 + i * 58, 260, () => Act(new RoomMove("choose", option)), hot: i == 0);
            }
        }
        else if (v.Choosing)
        {
            Shade(b);
            Label(L.F("{0} is picking a word…", Name(v.Drawer)), b.Center.X, b.Center.Y - 12, 20, Colors.White, center: true, into: Top);
        }
        else if (v.Revealed || v.Over)
        {
            Shade(b);
            Label(v.Over ? L.T("Final scores") : L.T("The word was"), b.Center.X, b.Top + 60, 18, Soft, center: true, into: Top);
            if (!v.Over) Label(WordOf(v.Word), b.Center.X, b.Top + 90, 34, Gold, center: true, into: Top);
            var rows = Enumerable.Range(0, v.Players).OrderByDescending(s => v.Over ? v.Scores[s] : v.TurnPoints[s]).Take(8).ToList();
            for (int i = 0; i < rows.Count; i++)
            {
                int s = rows[i];
                string points = v.Over ? L.F("{0} points", v.Scores[s]) : v.TurnPoints[s] > 0 ? $"+{v.TurnPoints[s]}" : "—";
                Label($"{(s == v.Seat ? L.T("You") : v.Names[s])}   {points}", b.Center.X, b.Top + (v.Over ? 100 : 150) + i * 28, 16, s == v.Seat ? Gold : Colors.White, center: true, into: Top);
            }
        }
    }

    void Shade(Rect b) => Top.Children.Add(Art.At(new Border { Width = b.Width, Height = b.Height, CornerRadius = new CornerRadius(10), Background = Art.Brush(200, 20, 24, 32), IsHitTestVisible = false }, b.X, b.Y));

    void DrawTopBar(DrawView v)
    {
        var b = BoardRect;
        string text = v.IDraw ? WordOf(v.Word) : v.Drawing ? (v.Word.Length == 3 ? WordOf(v.Word) : WordOf(v.Hint)) : "";
        if (text.Length > 0) Label(text, b.Center.X, 14, v.IDraw || v.Word.Length == 3 ? 24 : 22, v.IDraw ? Gold : Colors.White, center: true);
        Label(L.F("Turn {0} of {1}", v.TurnNumber + 1, v.Turns), b.Left, 20, 13, Soft);
        if (v.Drawing || v.Choosing)
        {
            double total = v.Drawing ? DrawSeconds : ChooseSeconds, left = ShownTimeLeft(v);
            Label(((int)Math.Ceiling(left)).ToString(CultureInfo.InvariantCulture), b.Right, 16, 20, left <= 10 ? Color.FromRgb(255, 107, 107) : Colors.White, right: true);
            Place(new Border { Width = b.Width, Height = 5, CornerRadius = new CornerRadius(2.5), Background = Art.Brush(60, 255, 255, 255), IsHitTestVisible = false }, b.Left, b.Top - 9);
            Place(new Border { Width = b.Width * Math.Clamp(left / total, 0, 1), Height = 5, CornerRadius = new CornerRadius(2.5), Background = Art.Brush(Themes.Current.Accent), IsHitTestVisible = false }, b.Left, b.Top - 9);
        }
    }

    void DrawPlayers(DrawView v)
    {
        double y = BoardY;
        Label(L.T("Players"), PanelX, y, 13, Soft);
        y += 22;
        foreach (int s in Enumerable.Range(0, v.Players).OrderByDescending(s => v.Scores[s]))
        {
            bool drawing = s == v.Drawer && (v.Drawing || v.Choosing);
            string mark = drawing ? "✎ " : v.Guessed[s] && v.Drawing ? "✓ " : "";
            string name = s == v.Seat ? L.T("You") : v.Names[s] + (v.Cpu[s] && v.Human[s] && !Solo ? " · " + L.T("CPU") : "");
            Label(mark + (name.Length > 22 ? name[..21] + "…" : name), PanelX, y, 15, v.Guessed[s] && v.Drawing ? Color.FromRgb(61, 220, 132) : drawing ? Gold : Colors.White);
            Label(v.Scores[s].ToString(CultureInfo.InvariantCulture), PanelX + 440, y, 15, Gold, right: true);
            y += 25;
        }
    }

    void DrawFeed(DrawView v)
    {
        double top = BoardY + 36 + Math.Max(4, v.Players) * 25 + 8;
        Label(L.T("Guesses"), PanelX, top, 13, Soft);
        var lines = v.Feed.TakeLast(Math.Max(3, (int)((BoardY + Board - 70 - top) / 22))).ToList();
        double y = top + 22;
        foreach (var line in lines)
        {
            string who = line.Seat == v.Seat ? L.T("You") : Name(line.Seat);
            string text = line.Kind == GuessKind.Right ? L.F("{0} guessed it!", who) : line.Kind == GuessKind.Close ? L.F("{0}: {1} · close!", who, line.Text) : $"{who}: {line.Text}";
            var color = line.Kind == GuessKind.Right ? Color.FromRgb(61, 220, 132) : line.Kind == GuessKind.Close ? Gold : Color.FromRgb(210, 216, 226);
            Label(text.Length > 46 ? text[..45] + "…" : text, PanelX, y, 13.5, color);
            y += 22;
        }
    }

    void DrawControls(DrawView v)
    {
        var t = Table;
        _guessBox = new Rect(PanelX, BoardY + Board - 44, 450, 40);
        if (v.CanGuess)
        {
            bool typing = Host.HasKeyboard(this);
            Place(new Border
            {
                Width = _guessBox.Width, Height = _guessBox.Height, CornerRadius = new CornerRadius(10), IsHitTestVisible = false,
                Background = Brushes.White, BorderBrush = typing ? Art.Brush(Themes.Current.Accent) : Art.Brush("#8A93A6"), BorderThickness = new Thickness(typing ? 2.5 : 1.5),
            }, _guessBox.X, _guessBox.Y, Top);
            string shown = _typed.Length > 0 ? _typed + (typing ? "▏" : "") : typing ? "▏" : L.T("Click here and type your guess · Enter sends it");
            Label(shown, _guessBox.X + 12, _guessBox.Y + 10, 15, _typed.Length > 0 || typing ? Color.FromRgb(29, 33, 41) : Color.FromRgb(120, 128, 140), into: Top);
            Clickable(_guessBox, OpenTyping);
        }
        if (v.IDraw)
        {
            double y = BoardY + Board + 14;
            for (int i = 0; i < Palette.Length; i++)
            {
                int c = i;
                double x = BoardX + 18 + i * 34;
                Top.Children.Add(Art.Circle(x, y + 16, 13, Art.Brush(Palette[i]), Brushes.White, c == _color ? 3 : 1));
                Clickable(new Rect(x - 16, y, 32, 32), () => _color = c);
            }
            for (int k = 0; k < 2; k++)
            {
                int size = k == 0 ? 1 : 3;
                double x = BoardX + 18 + Palette.Length * 34 + 8 + k * 32;
                Top.Children.Add(Art.Circle(x, y + 16, 14, Art.Brush(size == _size ? Themes.Current.Accent : Color.FromArgb(90, 255, 255, 255))));
                Top.Children.Add(Art.Circle(x, y + 16, Widths[size] / 2 + 1, Brushes.White));
                Clickable(new Rect(x - 16, y, 32, 32), () => _size = size);
            }
            Button(L.T("Undo"), BoardX + Board - 118, y + 2, 76, v.Strokes.Count > 0 ? () => Act(new RoomMove("undo")) : null, height: 30, font: 13);
            Button(L.T("Clear"), BoardX + Board - 38, y + 2, 76, v.Strokes.Count > 0 ? () => Act(new RoomMove("clear")) : null, height: 30, font: 13);
        }
        if (v.Over) DrawEndButtons(t.Bottom - 56);
    }

    public override bool Update(double dt)
    {
        bool busy = base.Update(dt);
        // the clock ticks on the board once a second while a turn is timed
        if (View is { Over: false } v && (v.Drawing || v.Choosing))
        {
            int shown = (int)Math.Ceiling(ShownTimeLeft(v));
            if (shown != _shownSecond)
            {
                _shownSecond = shown;
                Draw();
            }
            return true;
        }
        return busy;
    }

    int _shownSecond = -1;
}
