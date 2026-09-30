using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using DeskArcade.Engine;
using DeskArcade.Net;

namespace DeskArcade.Games;

/// <summary>
/// Quiz Night: four answers to every question, one of them right, and the faster you click the right one the more it
/// scores (1000 falling to 500 over the answer time). A leaderboard follows each question and a podium the last one.
/// Alone, today's ten (the same questions for everyone that day, <see cref="QuizDeck.Daily"/>) or a practice round from a
/// pack, against the computer at its level; with co-workers, one of them hosts a room of up to eight (set up in
/// <see cref="RoomWindow"/>, where the host also picks the pack, a pack of your own included) and everyone answers on
/// their own screen. The quiz itself runs in <see cref="QuizTable"/>; this is the board over the desktop, with a grip (or
/// a right-drag) to move it.
/// </summary>
public sealed partial class QuizGame : MiniGame, IRoomGame, IRoomOptions
{
    const double HeaderH = 46, Pad = 18, TileGap = 14;
    const int QuestionsPerQuiz = 10;
    static readonly Color Gold = Themes.ClassicGold;
    static readonly Color Right = Color.FromRgb(61, 200, 110);
    static readonly Color Wrong = Color.FromRgb(232, 72, 85);
    /// <summary>The four answer colours, each with its own shape: triangle, diamond, circle, square.</summary>
    static readonly Color[] TileColors =
    {
        Color.FromRgb(226, 54, 76), Color.FromRgb(47, 123, 224), Color.FromRgb(232, 165, 21), Color.FromRgb(47, 168, 79),
    };

    readonly Canvas _root = new(), _canvas = new(), _top = new();
    readonly TranslateTransform _rootTr = new();
    readonly DragHandle _handle;
    readonly List<(Rect Box, Action Click)> _clickables = new();
    readonly RoomLink _room = new() { Game = "quiz", Capacity = RoomLink.MaxCapacity };
    readonly QuizTable _table;
    readonly Avalonia.Threading.DispatcherTimer _hostTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    readonly System.Diagnostics.Stopwatch _hostClock = new();
    readonly Rectangle _bar = new() { Height = 6, RadiusX = 3, RadiusY = 3, IsHitTestVisible = false };
    readonly TextBlock _clock = new() { FontFamily = Fx.Font, FontSize = 18, FontWeight = FontWeight.Black, IsHitTestVisible = false };
    readonly List<bool> _marks = new(); // today's ten: right or wrong, question by question

    Vec2 _origin;
    Rect _area;
    double _barW;
    string _drawn = "";
    int _shownPhase = -1, _shownIndex = -1, _shownGame = -1, _lastTick = -1, _doneGame = -1, _markedIndex = -1, _winStreak, _lossStreak;
    int[] _boardFrom = Array.Empty<int>(); // each seat's score before the question, for the leaderboard's count-up
    int[] _rankFrom = Array.Empty<int>();  // and its row before it
    bool _demo, _menuShown;
    double _demoAt = -1;
    long _demoOverAt;
    string _packId = QuizPack.Mixed;
    QuizPack? _ownPack;
    int _questionCount = QuestionsPerQuiz;
    string? _lastShare;

    public QuizGame(IGameHost host) : base(host)
    {
        _table = new QuizTable(_room, Rng);
        _root.RenderTransformOrigin = RelativePoint.TopLeft;
        _root.RenderTransform = _rootTr;
        _root.Children.Add(_canvas);
        _root.Children.Add(_top);
        Layer.Children.Add(_root);
        _handle = new DragHandle(host, Id, Title);
        Layer.Children.Add(_handle.Visual);
        _room.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(Changed);
        _room.MessageArrived += () => Avalonia.Threading.Dispatcher.UIThread.Post(Host.Wake);
        _table.Changed += Changed;
        _hostTimer.Tick += (_, _) => HostTick();
    }

    public override string Id => "quiz";
    public override string Title => "Quiz Night";
    public RoomLink Room => _room;
    public string MinVersion => "1.8.6";
    public int MaxPlayers => RoomLink.MaxCapacity;
    public int MinPlayers => 2;
    public override bool SupportsLan => true;
    public override bool HasCpuLevels => true;
    public bool Playing => _table.View is { Stage: not QuizPhase.Over } && _table.Mode != QuizTable.TableMode.Idle;

    QuizView? View => _table.View;
    bool InRoom => _table.Mode is QuizTable.TableMode.Hosting or QuizTable.TableMode.Guest;
    bool Solo => _table.Mode == QuizTable.TableMode.Solo;

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        s.Rotor.Children.Add(Art.At(new Rectangle { Width = 9, Height = 9, RadiusX = 2, RadiusY = 2, Fill = Art.Brush(Art.Safe(TileColors[0])) }, -10, -10));
        s.Rotor.Children.Add(Art.At(new Rectangle { Width = 9, Height = 9, RadiusX = 2, RadiusY = 2, Fill = Art.Brush(TileColors[1]) }, 1, -10));
        s.Rotor.Children.Add(Art.At(new Rectangle { Width = 9, Height = 9, RadiusX = 2, RadiusY = 2, Fill = Art.Brush(TileColors[2]) }, -10, 1));
        s.Rotor.Children.Add(Art.At(new Rectangle { Width = 9, Height = 9, RadiusX = 2, RadiusY = 2, Fill = Art.Brush(Art.Safe(TileColors[3])) }, 1, 1));
        s.Rotor.Children.Add(Art.At(new TextBlock { Text = "?", FontSize = 12, FontWeight = FontWeight.Black, Foreground = Brushes.White }, -3.5, -8.5));
        return s;
    }

    public override HudInfo Hud => new(
        View is { } v ? v.Scores[v.Seat].ToString(CultureInfo.InvariantCulture) : "—",
        Status(),
        L.F("Best {0}", Host.Stats.Get("quiz.best")));

    /// <summary>The scoreboard's chip: the computer alone; in a room the leader, or the runner-up when I lead.</summary>
    public override Opponent? Opponent
    {
        get
        {
            if (View is not { } v || _table.Mode == QuizTable.TableMode.Idle) return null;
            if (Solo) return new Opponent(L.T("CPU"), true, CpuLevel, null);
            var others = Enumerable.Range(0, v.Players).Where(s => s != v.Seat).OrderByDescending(s => v.Scores[s]).ToList();
            return others.Count == 0 ? null : new Opponent(v.Names[others[0]], v.Cpu[others[0]], 0, null);
        }
    }

    string Status()
    {
        if (_table.Mode == QuizTable.TableMode.Guest && _room.State == RoomState.Joining) return L.F("Joining room {0}…", _room.Code);
        if (_table.Mode == QuizTable.TableMode.Guest && _room.State == RoomState.Lost) return L.T("The room closed · set up a new game");
        if (_table.Mode == QuizTable.TableMode.Hosting && View == null) return L.F("Room {0} · {1} at the table", _room.Code, _room.Seats().Count(x => x.Connected));
        if (View is not { } v) return _table.Mode == QuizTable.TableMode.Guest ? L.F("In room {0} · waiting for the host to start", _room.Code) : L.T("Pick today's ten or a practice round");
        return v.Stage switch
        {
            QuizPhase.Read => L.F("Question {0} of {1}", v.Index + 1, v.Count),
            QuizPhase.Answer when _table.MyPick >= 0 => L.T("Locked in · waiting for the others"),
            QuizPhase.Answer => L.T("Pick an answer!"),
            QuizPhase.Reveal when v.Mine == v.Right => L.F("Right! +{0}", v.Gained[v.Seat]),
            QuizPhase.Reveal => v.Mine < 0 ? L.T("Time's up") : L.T("Not this time"),
            QuizPhase.Board => L.T("Leaderboard"),
            _ => Place(v) == 1 ? L.T("You won the quiz!") : L.F("You finished {0}", Ordinal(Place(v))),
        };
    }

    static int Place(QuizView v) => QuizMatch.Places(v.Scores)[v.Seat];

    static string Ordinal(int place) => place switch
    {
        1 => L.T("first"),
        2 => L.T("second"),
        3 => L.T("third"),
        _ => L.F("{0}th", place),
    };

    // ------------------------------------------------------------------ packs

    /// <summary>The packs to choose from: the mixed bag, the four built in, and the good ones in the quiz folder (plus one opened from a file).</summary>
    List<QuizPack> Packs()
    {
        var list = new List<QuizPack> { QuizPack.MixedPack };
        list.AddRange(QuizPack.BuiltIn);
        foreach (string file in QuizPack.FolderFiles())
            if (QuizPack.LoadFile(file).Pack is { } p) list.Add(p);
        if (_ownPack != null && list.All(p => p.Id != _ownPack.Id)) list.Add(_ownPack);
        return list;
    }

    QuizPack CurrentPack => _ownPack is { } own && own.Id == _packId ? own : QuizPack.ById(_packId) ?? QuizPack.MixedPack;

    string PackTitle(QuizPack pack) => pack.Title(L.Code);

    void NextPack()
    {
        var packs = Packs();
        int i = packs.FindIndex(p => p.Id == _packId);
        var next = packs[(i + 1) % packs.Count];
        _packId = next.Id;
        if (next.Own) _ownPack = next;
        Host.Sound.Play("click", 0.4, 1.2);
        Changed();
    }

    /// <summary>Sets the pack a quiz asks from (the setup window's choice).</summary>
    public void UsePack(QuizPack pack)
    {
        _packId = pack.Id;
        if (pack.Own) _ownPack = pack;
        Changed();
    }

    // ------------------------------------------------------------------ starting and ending

    string[] CpuNames(int cpus) => Enumerable.Range(1, cpus).Select(i => cpus == 1 ? L.T("CPU") : L.F("CPU {0}", i)).ToArray();

    /// <summary>A practice round from the chosen pack against <paramref name="cpus"/> computer players, no network.</summary>
    public void StartSolo(int cpus)
    {
        cpus = Math.Clamp(cpus, 1, 3);
        var pack = CurrentPack;
        _marks.Clear();
        _table.Language = L.Code;
        _table.StartSolo(QuizDeck.Pick(pack, _questionCount, Rng), pack.Title, new[] { L.T("You") }.Concat(CpuNames(cpus)).ToArray(),
            new[] { false }.Concat(Enumerable.Repeat(true, cpus)).ToArray(), CpuLevel, daily: false);
        _hostTimer.Stop();
        Host.Sound.Play("whoosh", 0.4, 1.2);
    }

    /// <summary>Today's ten against the computer at its level; once a day.</summary>
    void StartDaily()
    {
        if (DailyDone) return;
        _marks.Clear();
        _table.Language = L.Code;
        _table.StartSolo(QuizDeck.Daily(Daily.Today), _ => L.T("Today's ten"), new[] { L.T("You"), L.T("CPU") }, new[] { false, true }, CpuLevel, daily: true);
        _hostTimer.Stop();
        Host.Sound.Play("whoosh", 0.4, 1.2);
    }

    bool DailyDone => Host.Stats.Today("quiz.daily") > 0;

    public void HostRoom(string? code = null)
    {
        _table.Language = L.Code;
        _table.Host(code);
        if (_table.Mode != QuizTable.TableMode.Hosting)
            Host.Fx.Popup(new Vec2(_area.Center.X, _area.Top + 80), L.T("Couldn't open a room"), Colors.White, 24, 2.4, L.F("UDP port {0} is in use", RoomLink.Port));
        _hostClock.Restart();
        _hostTimer.Start();
        Changed();
    }

    public void JoinRoom(string code, System.Net.IPEndPoint? address)
    {
        _hostTimer.Stop();
        _table.Language = L.Code;
        _table.Join(code, address);
        Changed();
    }

    public void LeaveRoom()
    {
        _hostTimer.Stop();
        _table.Leave();
        Changed();
    }

    public void StartRoom(int players)
    {
        if (_table.Mode != QuizTable.TableMode.Hosting) return;
        var pack = CurrentPack;
        _marks.Clear();
        _table.Language = L.Code;
        if (_table.StartRoom(Math.Clamp(players, 1, MaxPlayers), QuizDeck.Pick(pack, _questionCount, Rng), pack.Title, LanLink.MyName,
                i => L.F("CPU {0}", i), CpuLevel))
        {
            _hostClock.Restart();
            _hostTimer.Start();
            Host.Sound.Play("whoosh", 0.4, 1.2);
        }
    }

    /// <summary>The host's quiz runs on its own timer, so it goes on while the overlay is still or showing another game.</summary>
    void HostTick()
    {
        double dt = Math.Min(_hostClock.Elapsed.TotalSeconds, 0.5);
        _hostClock.Restart();
        if (_table.Mode != QuizTable.TableMode.Hosting)
        {
            _hostTimer.Stop();
            return;
        }
        _table.Tick(dt);
    }

    void Changed()
    {
        _drawn = "";
        Host.HudChanged();
        Host.Wake();
    }

    /// <summary>Raised when the player asks to host or join a room; the overlay opens the setup window.</summary>
    public event Action? SetupRequested;

    void OpenSetup() => SetupRequested?.Invoke();

    // ------------------------------------------------------------------ the frame

    public override bool Update(double dt)
    {
        if (_handle.Dragging)
        {
            _origin = _handle.Move(Host.Pointer, _area.Size);
            Layout();
        }
        if (_table.Mode is QuizTable.TableMode.Solo or QuizTable.TableMode.Guest) _table.Tick(dt); // a host ticks on its timer
        if (_demo) DemoPlay();
        if (DrawKey() != _drawn) Draw();
        bool timing = View is { } v && v.Stage != QuizPhase.Over && _table.Mode != QuizTable.TableMode.Idle && !Lost;
        if (timing) Tick();
        bool animating = Anims.Update(dt);
        return _handle.Dragging || animating || timing || _table.Mode == QuizTable.TableMode.Guest && (_room.State == RoomState.Joining || _table.Sending);
    }

    bool Lost => _table.Mode == QuizTable.TableMode.Guest && _room.State == RoomState.Lost;

    /// <summary>The timer bar and the seconds left, every frame while a question runs; a quiet tick in the last five seconds.</summary>
    void Tick()
    {
        if (View is not { } v) return;
        double left = _table.Left, length = Math.Max(0.01, v.Length);
        bool answering = v.Stage == QuizPhase.Answer;
        _bar.Width = Math.Max(0, _barW * Math.Clamp(left / length, 0, 1));
        _bar.IsVisible = v.Stage is QuizPhase.Read or QuizPhase.Answer;
        var t = Themes.Current;
        _bar.Fill = Art.Brush(answering && left < 5 ? Art.Safe(Wrong) : answering ? t.Accent : Art.Blend(t.HudFront, t.Ink, 0.5));
        int whole = (int)Math.Ceiling(left);
        _clock.IsVisible = answering;
        if (whole == _lastTick) return;
        _lastTick = whole;
        _clock.Text = whole.ToString(CultureInfo.InvariantCulture);
        _clock.Foreground = Art.Brush(left < 5 ? Art.Safe(Wrong) : t.HudFront);
        _clock.Measure(Size.Infinity);
        Canvas.SetLeft(_clock, _area.Width - Pad - _clock.DesiredSize.Width);
        if (answering && whole is > 0 and <= 5 && _table.MyPick < 0) Host.Sound.Play("click", 0.25, 1.4);
    }

    string DrawKey()
    {
        var v = View;
        string room = InRoom ? $"{_room.State}|{_room.Code}|{string.Join(",", _room.Seats().Select(s => s.Name + s.Connected))}" : "";
        return v == null
            ? $"{_table.Mode}|{room}|{_packId}|{DailyDone}|{_area.Size}|{Themes.Current.Id}|{L.Code}"
            : $"{_table.Mode}|{room}|{v.Game}|{v.Version}|{v.Ack}|{_table.MyPick}|{_area.Size}|{Themes.Current.Id}|{L.Code}";
    }

    // ------------------------------------------------------------------ demo

    public override void DemoTick()
    {
        _demo = true;
        if (_table.Mode == QuizTable.TableMode.Idle) StartSolo(3);
        else if (View is { Stage: QuizPhase.Over } v && _table.Mode != QuizTable.TableMode.Guest)
        {
            // like a person, look at the podium before playing again
            if (_demoOverAt == 0) _demoOverAt = Environment.TickCount64;
            else if (Environment.TickCount64 - _demoOverAt > 5000)
            {
                _demoOverAt = 0;
                if (_table.Mode == QuizTable.TableMode.Hosting) StartRoom(v.Players);
                else StartSolo(3);
            }
        }
    }

    /// <summary>The demo player answers after a few seconds, right about seven times in ten (a guest can only guess).</summary>
    void DemoPlay()
    {
        if (View is not { Stage: QuizPhase.Answer } v || _table.MyPick >= 0)
        {
            _demoAt = -1;
            return;
        }
        if (_demoAt < 0) _demoAt = 1.5 + Rng.NextDouble() * 5;
        if (v.Length - _table.Left < _demoAt) return;
        int right = _table.Match?.Current.RightPlace ?? -1;
        int pick = right >= 0 && Rng.NextDouble() < 0.7 ? right : Rng.Next(4);
        Choose(pick);
    }

    // ------------------------------------------------------------------ input

    void Choose(int place)
    {
        if (!_table.Pick(place)) return;
        Host.Sound.Play("board", 0.45, 1.5);
        Changed();
    }

    public override void Layout()
    {
        var a = Host.Arena;
        double w = Math.Clamp(a.Width - 40, 480, 760), h = Math.Clamp(a.Height - 80, 380, 470);
        w = Math.Min(w, a.Width - 8);
        h = Math.Min(h, a.Height - DragHandle.Height - 16);
        if (!_handle.Dragging) _origin = _handle.Saved() ?? new Vec2(a.Center.X - w / 2, a.Top + Math.Max(DragHandle.Height + 12, (a.Height - h) * 0.3));
        _origin = ClampOrigin(_origin, new Size(w, h));
        _area = new Rect(_origin.X, _origin.Y, w, h);
        _rootTr.X = _origin.X;
        _rootTr.Y = _origin.Y;
        _handle.Show(_area);
        if (!_handle.Dragging) Host.HudChanged();
    }

    Vec2 ClampOrigin(Vec2 o, Size size)
    {
        var a = Host.Arena;
        double top = a.Top + DragHandle.Height + 10;
        return new Vec2(
            Clamp(o.X, a.Left + 4, Math.Max(a.Left + 4, a.Right - size.Width - 4)),
            Clamp(o.Y, top, Math.Max(top, a.Bottom - size.Height - 4)));
    }

    public override void PositionsReset() => _handle.Hide();

    public override void CollectHitShapes(List<HitShape> into)
    {
        into.Add(HitShape.Box(_area));
        into.Add(_handle.Hit);
    }

    public override bool PointerDown(Vec2 p, bool right)
    {
        if (right || _handle.Contains(p))
        {
            _handle.Begin(p, _origin, anywhere: true);
            return true;
        }
        var q = (p - _origin).ToPoint();
        for (int i = _clickables.Count - 1; i >= 0; i--)
            if (_clickables[i].Box.Contains(q))
            {
                _clickables[i].Click();
                return false;
            }
        return false;
    }

    public override void PointerUp(Vec2 p) => _handle.End(_origin);

    public override void PointerCancel() => _handle.Cancel();

    public override void Summon(Vec2 p)
    {
        _handle.Save(ClampOrigin(new Vec2(p.X - _area.Width / 2, p.Y - 30), _area.Size));
        Layout();
    }

    public override void Deactivate()
    {
        _handle.Cancel();
        Anims.Finish();
    }

    public override void ThemeChanged() => _drawn = "";

    // ------------------------------------------------------------------ what happens when

    /// <summary>The effects of arriving in a phase: sounds, points, the stats, and at the end the rivalries.</summary>
    void Arrive(QuizView v)
    {
        var tileAt = TileRects(v);
        switch (v.Stage)
        {
            case QuizPhase.Read:
                Host.Sound.Play("whoosh", 0.3, 1.3);
                break;
            case QuizPhase.Answer:
                Host.Sound.Play("pop", 0.4, 1.1);
                break;
            case QuizPhase.Reveal:
                bool right = v.Mine == v.Right && v.Mine >= 0;
                if (_markedIndex != v.Index)
                {
                    _markedIndex = v.Index;
                    if (v.Daily) _marks.Add(right);
                    if (right)
                    {
                        Host.Stats.Add("quiz.right");
                        if (v.Took <= QuizMatch.QuickSeconds) Host.Stats.Add("quiz.quick");
                    }
                }
                if (v.Mine >= 0 && v.Mine < tileAt.Length)
                {
                    var r = tileAt[v.Mine];
                    var at = new Vec2(_origin.X + r.Center.X, _origin.Y + r.Top);
                    if (right) Host.Fx.Popup(at, L.F("+{0}", v.Gained[v.Seat]), Gold, 34, 1.4, v.Took <= QuizMatch.QuickSeconds ? L.T("lightning fast!") : null);
                    else Host.Fx.Popup(at, L.T("wrong"), Colors.White, 26, 1.2);
                }
                else Host.Fx.Popup(new Vec2(_area.Center.X, _area.Top + _area.Height * 0.4), L.T("Time's up"), Colors.White, 28, 1.2);
                Host.Sound.Play(right ? "score" : "buzzer", right ? 0.6 : 0.25);
                break;
            case QuizPhase.Over:
                Finish(v);
                break;
        }
    }

    void Finish(QuizView v)
    {
        if (_doneGame == v.Game) return;
        _doneGame = v.Game;
        int me = v.Seat, place = Place(v);
        bool others = Enumerable.Range(0, v.Players).Any(s => s != me && !v.Cpu[s]);
        Host.Stats.Add("quiz.games");
        Host.Stats.Max("quiz.best", v.Scores[me]);
        if (v.Rights[me] == v.Count && v.Count >= 5) Host.Stats.Add("quiz.perfect");
        if (place == 1 && v.Players > 1)
        {
            Host.Stats.Add("quiz.wins");
            if (others) Host.Stats.Add("lan.wins");
        }
        // a rivalry with every co-worker at the table, never with the computer
        if (InRoom)
            for (int s = 0; s < v.Players; s++)
                if (s != me && !v.Cpu[s]) Host.RecordResult(Id, v.Names[s], Math.Sign(v.Scores[me] - v.Scores[s]));
        string? level = Solo && !others ? LevelStep(place == 1) : null;
        if (v.Daily)
        {
            Host.Stats.Add("quiz.daily");
            Host.Stats.Max("quiz.dailyright", v.Rights[me]);
            Host.Stats.Max("quiz.dailytenths", (long)Math.Round(v.Time * 10));
            int grid = 1 << QuizDeck.DailyCount;
            for (int i = 0; i < _marks.Count && i < QuizDeck.DailyCount; i++)
                if (_marks[i]) grid |= 1 << i;
            Host.Stats.Max("quiz.dailygrid", grid);
        }
        _lastShare = L.F("Quiz Night · {0} points · {1} of {2} right", v.Scores[me], v.Rights[me], v.Count);
        var at = new Vec2(_area.Center.X, _area.Top + 40);
        if (place == 1)
        {
            Host.Fx.Popup(at, L.T("YOU WIN!"), Gold, 42, 2.6, level ?? L.F("{0} of {1} right", v.Rights[me], v.Count));
            Host.Fx.Burst(at, Themes.Current.Confetti, 40, 520, 700, 7, 1.1);
            Host.Sound.Play("best", 0.8);
        }
        else
        {
            Host.Fx.Popup(at, L.F("You finished {0}", Ordinal(place)), Colors.White, 34, 2.4, level ?? L.F("{0} of {1} right", v.Rights[me], v.Count));
            Host.Sound.Play("done", 0.5);
        }
        Host.HudChanged();
    }

    /// <summary>Two wins in a row move the computer up a level, two losses move it down; the demo leaves the setting alone.</summary>
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

    /// <summary>
    /// Today's ten as a line to paste, with a square for each question ("Quiz Night daily · 8/10 in 41 s"), once it is
    /// done; otherwise the last quiz finished, if any.
    /// </summary>
    public override string? ShareText
    {
        get
        {
            if (DailyDone)
            {
                long right = Host.Stats.Today("quiz.dailyright"), grid = Host.Stats.Today("quiz.dailygrid");
                double seconds = Host.Stats.Today("quiz.dailytenths") / 10.0;
                string line = L.F("Quiz Night daily · {0}/{1} in {2} s", right, QuizDeck.DailyCount, Math.Round(seconds).ToString(CultureInfo.InvariantCulture));
                if (grid == 0) return line;
                return line + "\n" + string.Concat(Enumerable.Range(0, QuizDeck.DailyCount).Select(i => (grid & (1L << i)) != 0 ? "🟩" : "🟥"));
            }
            return _lastShare;
        }
    }

    // ------------------------------------------------------------------ drawing

    /// <summary>Lays the board out afresh for what there is to show; the phase's entrance animations start when the phase is new.</summary>
    void Draw()
    {
        _drawn = DrawKey();
        _canvas.Children.Clear();
        _top.Children.Clear();
        _clickables.Clear();
        _lastTick = -1;
        var t = Themes.Current;
        var w = _area.Width;
        var h = _area.Height;
        _canvas.Children.Add(new Border
        {
            Width = w, Height = h, CornerRadius = new CornerRadius(18), IsHitTestVisible = false,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(244, t.Ink.R, t.Ink.G, t.Ink.B), 0),
                    new GradientStop(Color.FromArgb(244, (byte)Math.Min(255, t.Ink.R + 14), (byte)Math.Min(255, t.Ink.G + 14), (byte)Math.Min(255, t.Ink.B + 22)), 1),
                },
            },
            BorderBrush = Art.Brush(Color.FromArgb(170, t.Accent.R, t.Accent.G, t.Accent.B)), BorderThickness = new Thickness(2),
            BoxShadow = new BoxShadows(new BoxShadow { OffsetY = 6, Blur = 22, Color = Color.FromArgb(110, 0, 0, 0) }),
        });

        var v = View;
        if (v == null || _table.Mode == QuizTable.TableMode.Idle || Lost)
        {
            _shownPhase = _shownIndex = _shownGame = -1;
            DrawMenu();
            return;
        }
        _menuShown = false;
        bool fresh = v.Game != _shownGame || v.Index != _shownIndex || v.Phase != _shownPhase;
        if (v.Game != _shownGame)
        {
            _boardFrom = new int[v.Players];
            _rankFrom = Enumerable.Range(0, v.Players).ToArray();
        }
        DrawHeader(v);
        switch (v.Stage)
        {
            case QuizPhase.Read:
                DrawQuestion(v, fresh, big: true);
                break;
            case QuizPhase.Answer or QuizPhase.Reveal:
                DrawQuestion(v, fresh && v.Stage == QuizPhase.Read, big: false);
                DrawTiles(v, fresh);
                DrawFooter(v);
                break;
            case QuizPhase.Board:
                DrawBoard(v, fresh);
                break;
            default:
                DrawPodium(v, fresh);
                break;
        }
        if (fresh)
        {
            _shownGame = v.Game;
            _shownIndex = v.Index;
            _shownPhase = v.Phase;
            Arrive(v);
            if (v.Stage == QuizPhase.Board) // the next board counts up from here
            {
                _boardFrom = v.Scores.ToArray();
                _rankFrom = Ranked(v).Select((seat, row) => (seat, row)).OrderBy(x => x.seat).Select(x => x.row).ToArray();
            }
        }
        Tick();
    }

    static List<int> Ranked(QuizView v) => Enumerable.Range(0, v.Players).OrderByDescending(s => v.Scores[s]).ThenBy(s => s).ToList();

    void DrawHeader(QuizView v)
    {
        var t = Themes.Current;
        double w = _area.Width;
        Label(v.Pack, Pad, 14, 13, Art.Blend(t.HudFront, t.Ink, 0.3), FontWeight.SemiBold, maxWidth: w * 0.34);
        if (v.Stage != QuizPhase.Over)
            Label(L.F("Question {0} of {1}", v.Index + 1, v.Count), w / 2, 12, 15, t.HudFront, FontWeight.Bold, center: true);
        else Label(L.T("Final scores"), w / 2, 12, 15, t.HudFront, FontWeight.Bold, center: true);
        _barW = w - 2 * Pad;
        _canvas.Children.Add(Art.At(new Rectangle { Width = _barW, Height = 6, RadiusX = 3, RadiusY = 3, Fill = Art.Brush(Color.FromArgb(50, t.HudFront.R, t.HudFront.G, t.HudFront.B)), IsHitTestVisible = false }, Pad, HeaderH - 6));
        _top.Children.Add(Art.At(_bar, Pad, HeaderH - 6));
        _top.Children.Add(Art.At(_clock, w - Pad - 20, 10));
    }

    /// <summary>The question: big and alone while it is read, smaller above the answers after.</summary>
    void DrawQuestion(QuizView v, bool animate, bool big)
    {
        var t = Themes.Current;
        double w = _area.Width, top = HeaderH + 10;
        double bottom = big ? _area.Height - 40 : TilesTop - 8;
        var text = new TextBlock
        {
            Text = v.Question, FontFamily = Fx.Font, FontSize = big ? 28 : 22, FontWeight = FontWeight.Bold, Foreground = Art.Brush(t.HudFront),
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Width = w - 2 * Pad - 20, IsHitTestVisible = false,
        };
        text.Measure(new Size(text.Width, double.PositiveInfinity));
        double th = text.DesiredSize.Height;
        if (!big && th > bottom - top)
        {
            text.FontSize = 18; // a long question fits in smaller type
            text.Measure(new Size(text.Width, double.PositiveInfinity));
            th = text.DesiredSize.Height;
        }
        double y = top + Math.Max(0, (bottom - top - th) / 2);
        _canvas.Children.Add(Art.At(text, Pad + 10, y));
        if (big)
            Label(v.Index == 0 ? L.T("Get ready…") : L.T("Read the question…"), w / 2, y + th + 14, 14, Art.Blend(t.HudFront, t.Ink, 0.35), FontWeight.SemiBold, center: true);
        if (!animate || Fx.ReducedMotion) return;
        var sc = new ScaleTransform(0.92, 0.92);
        text.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        text.RenderTransform = sc;
        text.Opacity = 0;
        Anims.Add(0.35, k =>
        {
            text.Opacity = k;
            sc.ScaleX = sc.ScaleY = 0.92 + 0.08 * k;
        }, Ease.OutBack);
    }

    double TilesTop => _area.Height * 0.42;

    /// <summary>Where the four answers go, two by two, in board coordinates.</summary>
    Rect[] TileRects(QuizView v)
    {
        double w = _area.Width, top = TilesTop, bottom = _area.Height - (InRoom ? 44 : 22);
        double tw = (w - 2 * Pad - TileGap) / 2, th = (bottom - top - TileGap) / 2;
        return Enumerable.Range(0, 4).Select(i => new Rect(Pad + (i % 2) * (tw + TileGap), top + (i / 2) * (th + TileGap), tw, th)).ToArray();
    }

    void DrawTiles(QuizView v, bool fresh)
    {
        var rects = TileRects(v);
        int mine = _table.MyPick;
        bool open = v.Stage == QuizPhase.Answer && mine < 0;
        bool reveal = v.Stage == QuizPhase.Reveal;
        for (int i = 0; i < 4 && i < v.Answers.Length; i++)
        {
            int place = i;
            var r = rects[i];
            var color = Art.Safe(TileColors[i]);
            bool isRight = reveal && v.Right == i, picked = mine == i;
            double opacity = reveal ? (isRight ? 1 : picked ? 0.75 : 0.32) : mine >= 0 && !picked ? 0.4 : 1;
            var tile = new Canvas { Width = r.Width, Height = r.Height, IsHitTestVisible = false, Opacity = opacity };
            tile.Children.Add(new Border
            {
                Width = r.Width, Height = r.Height, CornerRadius = new CornerRadius(12),
                Background = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(Art.Blend(color, Colors.White, 0.12), 0), new GradientStop(Art.Blend(color, Colors.Black, 0.12), 1) },
                },
                BorderBrush = isRight ? Art.Brush(Colors.White) : picked ? Art.Brush(Themes.Current.Gold) : Art.Brush(Art.Blend(color, Colors.Black, 0.35)),
                BorderThickness = new Thickness(isRight || picked ? 4 : 1.5),
                BoxShadow = new BoxShadows(new BoxShadow { OffsetY = 3, Blur = 8, Color = Color.FromArgb(90, 0, 0, 0) }),
            });
            double s = Math.Min(15, r.Height * 0.2);
            tile.Children.Add(Mark(i, 14 + s, r.Height / 2, s, Brushes.White));
            var text = new TextBlock
            {
                Text = v.Answers[i], FontFamily = Fx.Font, FontSize = 19, FontWeight = FontWeight.Bold, Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap, Width = r.Width - 3 * s - 38, MaxHeight = r.Height - 8, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            text.Measure(new Size(text.Width, r.Height));
            if (text.DesiredSize.Height > r.Height - 12)
            {
                text.FontSize = 15;
                text.Measure(new Size(text.Width, r.Height));
            }
            tile.Children.Add(Art.At(text, 28 + 2 * s, (r.Height - text.DesiredSize.Height) / 2));
            if (reveal)
            {
                if (isRight) tile.Children.Add(Art.PathOf($"M{Art.F(r.Width - 38)},{Art.F(r.Height / 2)} l8,9 l16,-19", null, Brushes.White, 5));
                if (InRoom && v.Picks.Length == 4 && v.Picks[i] > 0)
                    tile.Children.Add(Art.At(new TextBlock { Text = "×" + v.Picks[i].ToString(CultureInfo.InvariantCulture), FontFamily = Fx.Font, FontSize = 14, FontWeight = FontWeight.Black, Foreground = Brushes.White },
                        r.Width - (isRight ? 78 : 40), 8));
            }
            _canvas.Children.Add(Art.At(tile, r.X, r.Y));
            if (open) _clickables.Add((r, () => Choose(place)));
            if (!fresh || Fx.ReducedMotion) continue;
            var sc = new ScaleTransform();
            tile.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
            tile.RenderTransform = sc;
            if (v.Stage == QuizPhase.Answer)
            {
                sc.ScaleX = sc.ScaleY = 0.6;
                tile.Opacity = 0;
                Anims.Add(0.3, k =>
                {
                    sc.ScaleX = sc.ScaleY = 0.6 + 0.4 * k;
                    tile.Opacity = Math.Min(1, k * 1.6) * opacity;
                }, Ease.OutBack, delay: i * 0.06);
            }
            else if (isRight) Anims.Add(0.5, k => sc.ScaleX = sc.ScaleY = 1 + 0.07 * k, Ease.Pulse, delay: 0.1);
        }
    }

    /// <summary>Who has answered, as a row of dots (in a room), and what I'm waiting for.</summary>
    void DrawFooter(QuizView v)
    {
        if (!InRoom) return;
        var t = Themes.Current;
        double y = _area.Height - 30;
        int answered = Enumerable.Range(0, v.Players).Count(s => v.Answered[s]);
        int waiting = Enumerable.Range(0, v.Players).Count(s => !v.Away[s]);
        Label(L.F("{0} of {1} answered", answered, waiting), Pad, y - 2, 13, Art.Blend(t.HudFront, t.Ink, 0.3), FontWeight.SemiBold);
        double x = _area.Width - Pad - v.Players * 20;
        for (int s = 0; s < v.Players; s++)
        {
            var fill = v.Away[s] ? Art.Brush(Color.FromArgb(40, 255, 255, 255)) : v.Answered[s] ? Art.Brush(t.Gold) : Art.Brush(Color.FromArgb(70, t.HudFront.R, t.HudFront.G, t.HudFront.B));
            _canvas.Children.Add(Art.Circle(x + s * 20 + 8, y + 8, 7, fill, s == v.Seat ? Art.Brush(t.HudFront) : null, 1.5));
        }
    }

    /// <summary>The leaderboard after a question: rows slide from where they were to their new places, and the scores count up.</summary>
    void DrawBoard(QuizView v, bool fresh)
    {
        var t = Themes.Current;
        var order = Ranked(v);
        var places = QuizMatch.Places(v.Scores);
        int rows = Math.Min(order.Count, 8);
        double space = _area.Height - HeaderH - 60, rowH = Math.Min(54, space / Math.Max(1, rows));
        double top = HeaderH + 30 + Math.Max(0, (space - rowH * rows) / 2);
        double w = _area.Width - 2 * Pad, best = Math.Max(1, v.Scores.Max());
        Label(L.T("Leaderboard"), _area.Width / 2, top - 26, 14, Art.Blend(t.HudFront, t.Ink, 0.3), FontWeight.Bold, center: true);
        for (int row = 0; row < rows; row++)
        {
            int s = order[row];
            bool me = s == v.Seat;
            double lh = rowH - 8, mid = lh / 2;
            var line = new Canvas { Width = w, Height = lh, IsHitTestVisible = false };
            line.Children.Add(new Border
            {
                Width = w, Height = lh, CornerRadius = new CornerRadius(10),
                Background = Art.Brush(me ? Color.FromArgb(70, t.Accent.R, t.Accent.G, t.Accent.B) : Color.FromArgb(28, 255, 255, 255)),
            });
            // place, name, a bar for the score, the points just won, the score
            double barX = 200, barMax = w - barX - 170;
            var bar = new Rectangle { Height = 12, RadiusX = 6, RadiusY = 6, Fill = Art.Brush(me ? t.Gold : Art.Blend(t.Accent, t.HudFront, 0.2)) };
            line.Children.Add(Art.At(bar, barX, mid - 6));
            line.Children.Add(Art.At(Text(places[s].ToString(CultureInfo.InvariantCulture), 18, t.HudFront, FontWeight.Black), 14, mid - 12));
            var name = Text(v.Names[s] + (v.Cpu[s] && InRoom ? " · " + L.T("CPU") : ""), 16, t.HudFront, me ? FontWeight.Black : FontWeight.SemiBold);
            name.MaxWidth = barX - 50;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            line.Children.Add(Art.At(name, 42, mid - 11));
            var score = Text("", 18, t.HudFront, FontWeight.Black);
            line.Children.Add(Art.At(score, w - 76, mid - 12));
            if (v.Gained[s] > 0)
                line.Children.Add(Art.At(Text("+" + v.Gained[s].ToString(CultureInfo.InvariantCulture), 14, t.Gold, FontWeight.Bold), w - 150, mid - 10));
            var move = new TranslateTransform();
            line.RenderTransform = move;
            _canvas.Children.Add(Art.At(line, Pad, top + row * rowH));
            int from = s < _boardFrom.Length ? _boardFrom[s] : 0, to = v.Scores[s];
            void Show(double k)
            {
                int shown = (int)Math.Round(from + (to - from) * k);
                score.Text = shown.ToString(CultureInfo.InvariantCulture);
                bar.Width = shown <= 0 ? 0 : Math.Max(12, barMax * shown / best);
            }
            if (!fresh || Fx.ReducedMotion)
            {
                Show(1);
                continue;
            }
            int wasRow = s < _rankFrom.Length ? Math.Min(_rankFrom[s], rows) : row;
            double dy = (wasRow - row) * rowH;
            Show(0);
            move.Y = dy;
            Anims.Add(0.6, k => move.Y = dy * (1 - k), Ease.InOutCubic, delay: 0.25);
            Anims.Add(0.9, Show, Ease.OutCubic, delay: 0.2);
        }
        if (order.Count > rows && !order.Take(rows).Contains(v.Seat))
            Label(L.F("You: {0} · {1}", Ordinal(places[v.Seat]), v.Scores[v.Seat]), _area.Width / 2, _area.Height - 24, 13, t.Gold, FontWeight.Bold, center: true);
    }

    /// <summary>The podium: the top three on blocks that rise (second, first, third), then what to do next.</summary>
    void DrawPodium(QuizView v, bool fresh)
    {
        var t = Themes.Current;
        var order = Ranked(v);
        var places = QuizMatch.Places(v.Scores);
        double cx = _area.Width / 2, floor = _area.Height - 96, bw = Math.Min(150, (_area.Width - 80) / 3);
        var slots = new[] { (Row: 1, Dx: -bw - 8, H: 96.0), (Row: 0, Dx: 0.0, H: 136.0), (Row: 2, Dx: bw + 8, H: 70.0) };
        var medal = new[] { Color.FromRgb(255, 205, 64), Color.FromRgb(200, 208, 220), Color.FromRgb(214, 140, 76) };
        foreach (var (row, dx, height) in slots)
        {
            if (row >= order.Count) continue;
            int s = order[row];
            int place = places[s];
            var block = new Canvas { Width = bw, Height = height, IsHitTestVisible = false };
            block.Children.Add(new Border
            {
                Width = bw, Height = height, CornerRadius = new CornerRadius(10, 10, 2, 2),
                Background = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(Art.Blend(t.Accent, Colors.White, 0.15), 0), new GradientStop(Art.Blend(t.Accent, Colors.Black, 0.35), 1) },
                },
            });
            var num = Text(place.ToString(CultureInfo.InvariantCulture), 34, Colors.White, FontWeight.Black);
            num.Measure(Size.Infinity);
            block.Children.Add(Art.At(num, (bw - num.DesiredSize.Width) / 2, 6));
            var score = Text(v.Scores[s].ToString(CultureInfo.InvariantCulture), 15, Colors.White, FontWeight.Bold);
            score.Measure(Size.Infinity);
            if (height > 70) block.Children.Add(Art.At(score, (bw - score.DesiredSize.Width) / 2, height - 26));
            var grow = new ScaleTransform(1, 1);
            block.RenderTransformOrigin = new RelativePoint(0.5, 1, RelativeUnit.Relative);
            block.RenderTransform = grow;
            double x = cx + dx - bw / 2;
            _canvas.Children.Add(Art.At(block, x, floor - height));
            var who = new StackPanel { Width = bw + 20, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Center };
            who.Children.Add(new Ellipse { Width = 26, Height = 26, Fill = Art.Brush(medal[Math.Clamp(place - 1, 0, 2)]), Stroke = Brushes.White, StrokeThickness = 2, HorizontalAlignment = HorizontalAlignment.Center });
            var name = Text(v.Names[s], 15, s == v.Seat ? t.Gold : t.HudFront, FontWeight.Black);
            name.HorizontalAlignment = HorizontalAlignment.Center;
            name.MaxWidth = bw + 16;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            who.Children.Add(name);
            if (height <= 70) who.Children.Add(new TextBlock { Text = v.Scores[s].ToString(CultureInfo.InvariantCulture), FontFamily = Fx.Font, FontSize = 12, FontWeight = FontWeight.Bold, Foreground = Art.Brush(t.HudFront), HorizontalAlignment = HorizontalAlignment.Center });
            who.Measure(Size.Infinity);
            _canvas.Children.Add(Art.At(who, x - 10, floor - height - who.DesiredSize.Height - 4));
            if (!fresh || Fx.ReducedMotion) continue;
            double delay = row switch { 2 => 0.1, 1 => 0.45, _ => 0.85 };
            grow.ScaleY = 0.01;
            who.Opacity = 0;
            Anims.Add(0.55, k => grow.ScaleY = Math.Max(0.01, k), Ease.OutBack, delay: delay);
            Anims.Add(0.3, k => who.Opacity = k, delay: delay + 0.4);
        }
        int me = v.Seat;
        string summary = L.F("{0} of {1} right · {2} s", v.Rights[me], v.Count, Math.Round(v.Time).ToString(CultureInfo.InvariantCulture));
        if (!order.Take(3).Contains(me)) summary = L.F("You: {0} · {1}", Ordinal(places[me]), v.Scores[me]) + " · " + summary;
        Label(summary, cx, floor + 10, 14, Art.Blend(t.HudFront, t.Ink, 0.2), FontWeight.SemiBold, center: true);
        double y = _area.Height - 56;
        switch (_table.Mode)
        {
            case QuizTable.TableMode.Solo:
                if (v.Daily)
                {
                    Button(L.T("Practice round"), cx - 110, y, 200, () => StartSolo(3));
                    Button(L.T("Other quizzes…"), cx + 110, y, 200, BackToMenu);
                }
                else
                {
                    Button(L.T("Play again"), cx - 110, y, 200, () => StartSolo(v.Players - 1));
                    Button(L.T("Other quizzes…"), cx + 110, y, 200, BackToMenu);
                }
                break;
            case QuizTable.TableMode.Hosting:
                Button(L.T("Another quiz"), cx - 110, y, 200, () => StartRoom(v.Players));
                Button(L.T("Leave the room"), cx + 110, y, 200, LeaveRoom);
                break;
            default:
                Label(L.T("The host can start another quiz"), cx - 110, y + 11, 13, t.HudFront, FontWeight.SemiBold, center: true);
                Button(L.T("Leave the room"), cx + 120, y, 200, LeaveRoom);
                break;
        }
    }

    void BackToMenu()
    {
        _table.Close();
        Changed();
    }

    /// <summary>The start: today's ten, a practice round from a pack, a room; in a room, who is there and how to start.</summary>
    void DrawMenu()
    {
        var t = Themes.Current;
        double cx = _area.Width / 2, y = 26;
        Label(L.T(Title), cx, y, 38, t.HudFront, FontWeight.Black, center: true);
        Label(L.T("Four answers, one right · the faster you click it, the more it scores"), cx, y + 54, 15, Art.Blend(t.HudFront, t.Ink, 0.3), FontWeight.SemiBold, center: true);
        // the four answer colours and shapes, popping in the first time the menu shows
        bool pop = !_menuShown && !Fx.ReducedMotion;
        _menuShown = true;
        for (int i = 0; i < 4; i++)
        {
            var tile = new Canvas { Width = 34, Height = 34, IsHitTestVisible = false };
            tile.Children.Add(new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(8), Background = Art.Brush(Art.Safe(TileColors[i])) });
            tile.Children.Add(Mark(i, 17, 17, 9, Brushes.White));
            _canvas.Children.Add(Art.At(tile, cx - 2 * 44 + i * 44 + 5, y + 86));
            if (!pop) continue;
            var sc = new ScaleTransform(0.01, 0.01);
            tile.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
            tile.RenderTransform = sc;
            Anims.Add(0.35, k => sc.ScaleX = sc.ScaleY = Math.Max(0.01, k), Ease.OutBack, delay: 0.1 + i * 0.07);
        }
        y += 142;
        if (Lost)
        {
            Label(L.T("The room closed · set up a new game"), cx, y, 16, t.Gold, FontWeight.Bold, center: true);
            Button(L.T("Leave the room"), cx, y + 50, 220, LeaveRoom);
            return;
        }
        if (_table.Mode == QuizTable.TableMode.Hosting)
        {
            var seats = _room.Seats().Where(s => s.Connected).ToList();
            Label(L.F("Room {0}", _room.Code), cx, y, 26, t.Gold, FontWeight.Black, center: true);
            Label(string.Join(" · ", seats.Select(s => s.Seat == 0 ? L.F("{0} (you, host)", s.Name) : s.Name)), cx, y + 38, 14, t.HudFront, FontWeight.SemiBold, center: true, maxWidth: _area.Width - 40);
            Label(L.F("Pack: {0} · {1} questions", PackTitle(CurrentPack), Math.Min(_questionCount, CurrentPack.Items.Count)), cx, y + 62, 14, Art.Blend(t.HudFront, t.Ink, 0.25), FontWeight.SemiBold, center: true);
            y += 100;
            if (seats.Count < 2) Label(L.T("Waiting for a co-worker to join…"), cx, y + 10, 15, t.HudFront, FontWeight.SemiBold, center: true);
            else
                for (int cpus = 0; cpus <= Math.Min(2, RoomLink.MaxCapacity - seats.Count); cpus++)
                {
                    int players = seats.Count + cpus;
                    string text = cpus == 0 ? L.T("Start the quiz") : cpus == 1 ? L.T("Start + 1 computer") : L.F("Start + {0} computers", cpus);
                    Button(text, cx - 190 + cpus * 190, y, 180, () => StartRoom(players));
                }
            Button(L.T("Room setup…"), cx - 110, y + 60, 200, OpenSetup);
            Button(L.T("Leave the room"), cx + 110, y + 60, 200, LeaveRoom);
            return;
        }
        if (_table.Mode == QuizTable.TableMode.Guest)
        {
            Label(Status(), cx, y + 10, 16, t.Gold, FontWeight.Bold, center: true);
            Label(string.Join(" · ", _room.Seats().Where(s => s.Connected).Select(s => s.Name)), cx, y + 44, 14, t.HudFront, FontWeight.SemiBold, center: true, maxWidth: _area.Width - 40);
            Button(L.T("Leave the room"), cx, y + 90, 220, LeaveRoom);
            return;
        }
        if (DailyDone)
        {
            Label(L.F("Today's ten: {0}/{1} in {2} s · a new ten tomorrow", Host.Stats.Today("quiz.dailyright"), QuizDeck.DailyCount,
                Math.Round(Host.Stats.Today("quiz.dailytenths") / 10.0).ToString(CultureInfo.InvariantCulture)), cx, y + 10, 15, t.Gold, FontWeight.Bold, center: true);
        }
        else Button(L.T("Today's ten"), cx, y, 260, StartDaily, hot: true);
        y += 66;
        var chip = Label("‹ " + PackTitle(CurrentPack) + " ›", cx - 120, y + 10, 15, t.HudFront, FontWeight.Bold, center: true);
        chip.Measure(Size.Infinity);
        var chipRect = new Rect(cx - 120 - chip.DesiredSize.Width / 2 - 14, y + 2, chip.DesiredSize.Width + 28, 36);
        _canvas.Children.Insert(1, Art.At(new Border
        {
            Width = chipRect.Width, Height = chipRect.Height, CornerRadius = new CornerRadius(18),
            Background = Art.Brush(Color.FromArgb(60, t.Accent.R, t.Accent.G, t.Accent.B)), BorderBrush = Art.Brush(t.Accent), BorderThickness = new Thickness(1.5),
            IsHitTestVisible = false,
        }, chipRect.X, chipRect.Y));
        _clickables.Add((chipRect, NextPack));
        Button(L.T("Practice round"), cx + 120, y, 200, () => StartSolo(3));
        y += 66;
        Button(L.T("Play with co-workers…"), cx, y, 260, OpenSetup);
        Label(L.F("vs the CPU · {0}", L.T(LevelNames[CpuLevel - 1])), cx, _area.Height - 30, 12, Art.Blend(t.HudFront, t.Ink, 0.4), FontWeight.SemiBold, center: true);
    }

    // ------------------------------------------------------------------ pieces

    static TextBlock Text(string text, double size, Color color, FontWeight weight) =>
        new() { Text = text, FontFamily = Fx.Font, FontSize = size, FontWeight = weight, Foreground = Art.Brush(color), IsHitTestVisible = false };

    TextBlock Label(string text, double x, double y, double size, Color color, FontWeight weight, bool center = false, double maxWidth = 0)
    {
        var t = Text(text, size, color, weight);
        if (maxWidth > 0)
        {
            t.MaxWidth = maxWidth;
            t.TextTrimming = TextTrimming.CharacterEllipsis;
        }
        if (center)
        {
            t.Measure(Size.Infinity);
            x -= Math.Min(t.DesiredSize.Width, maxWidth > 0 ? maxWidth : double.MaxValue) / 2;
        }
        _canvas.Children.Add(Art.At(t, x, y));
        return t;
    }

    /// <summary>A button, drawn above the rest.</summary>
    void Button(string text, double cx, double y, double width, Action click, bool hot = false)
    {
        var t = Themes.Current;
        var b = new Border
        {
            Width = width, Height = 40, CornerRadius = new CornerRadius(20), IsHitTestVisible = false,
            Background = hot ? Art.Brush(t.Accent) : Art.Brush(235, 255, 209, 102),
            BorderBrush = hot ? Brushes.White : Art.Brush("#8A6D1F"), BorderThickness = new Thickness(hot ? 2 : 1.5),
            Child = new TextBlock
            {
                Text = text, FontFamily = Fx.Font, FontSize = 15, FontWeight = FontWeight.Bold, Foreground = hot ? Brushes.White : Art.Brush("#2A2008"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            },
        };
        _top.Children.Add(Art.At(b, cx - width / 2, y));
        _clickables.Add((new Rect(cx - width / 2, y, width, 40), click));
    }

    /// <summary>The shape that goes with an answer's place: triangle, diamond, circle, square (so colour is never the only clue).</summary>
    static Control Mark(int place, double x, double y, double r, IBrush fill) => place switch
    {
        0 => Art.PathOf($"M{Art.F(x)},{Art.F(y - r)} L{Art.F(x + r)},{Art.F(y + r * 0.8)} L{Art.F(x - r)},{Art.F(y + r * 0.8)} Z", fill),
        1 => Art.PathOf($"M{Art.F(x)},{Art.F(y - r)} L{Art.F(x + r)},{Art.F(y)} L{Art.F(x)},{Art.F(y + r)} L{Art.F(x - r)},{Art.F(y)} Z", fill),
        2 => Art.Circle(x, y, r * 0.9, fill),
        _ => Art.PathOf($"M{Art.F(x - r * 0.8)},{Art.F(y - r * 0.8)} h{Art.F(r * 1.6)} v{Art.F(r * 1.6)} h{Art.F(-r * 1.6)} Z", fill),
    };
}
