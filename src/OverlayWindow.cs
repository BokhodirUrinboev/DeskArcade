using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using DeskArcade.Engine;
using DeskArcade.Games;
using DeskArcade.Net;
using DeskArcade.Platform;

namespace DeskArcade;

/// <summary>
/// Full-monitor, transparent, topmost window that never takes focus. Only the hit shapes the games
/// report (ball, hoop, bow, scoreboard) receive the mouse; the platform layer passes the rest through.
/// </summary>
public sealed class OverlayWindow : Window, IGameHost
{
    readonly bool _demo;
    readonly string? _startGame;
    readonly Canvas _root = new() { Background = Brushes.Transparent };
    readonly Canvas _gameLayer = new();
    readonly Canvas _hudLayer = new();
    readonly List<MiniGame> _games = new();
    readonly List<HitShape> _hitShapes = new();
    readonly List<HitShape> _pushedHitShapes = new();
    readonly List<NativeWindowInfo> _nativeWindows = new();
    readonly List<(IntPtr, Rect)> _windowRects = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly CancellationTokenSource _cts = new();
    readonly DispatcherTimer _platformTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    readonly DispatcherTimer _blinkTimer = new() { Interval = TimeSpan.FromMilliseconds(550) };
    readonly DispatcherTimer _hudHoverTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    readonly IDesktopPlatform _platform;
    DispatcherTimer? _demoTimer;

    Hud _hud = null!;
    RaceMode _race = null!;
    readonly TextBlock _raceLabel = new()
    {
        FontFamily = Fx.Font, FontSize = 14, FontWeight = FontWeight.Bold, Foreground = Brushes.White, IsVisible = false, IsHitTestVisible = false,
        Background = Engine.Art.Brush(200, 18, 20, 28), Padding = new Thickness(8, 3),
    };
    Tray? _tray;
    bool _loopOn, _captured, _quitting, _pushedCapture;
    double _lastTick, _idle;
    Vec2 _eventPointer;
    UpdateInfo? _update;
    DateTime? _claudeSince;
    double _playedWhileClaude; // seconds of play since Claude started working, for the summary
    bool _paused;
    bool _checkingUpdates;
    double _lastAchievementAt = -10;
    int _achievementRow;

    public Settings Settings { get; } = Settings.Load();
    public Stats Stats { get; } = Stats.Load();
    public Sound Sound { get; }
    public Fx Fx { get; } = new();
    public Platforms Platforms { get; } = new();
    public LanLink Lan { get; } = new();
    OfficeBoard _board = null!;
    BoardEntry _boardEntry = new(OfficeBoard.InstanceId, "", "", new Dictionary<string, long>());
    readonly DispatcherTimer _boardTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    double _playStreak, _lastPlayed; // seconds of play since the last break reminder; clock time of the last played frame
    public Daily Daily { get; }
    public Rect Arena { get; private set; }
    public Vec2 Pointer { get; private set; }
    public Rect HudBounds => _hud?.Area ?? default;
    public IReadOnlyList<MiniGame> Games => _games;
    public MiniGame? Current { get; private set; }
    public bool OverlayVisible => IsVisible;
    public UpdateInfo? AvailableUpdate => _update;

    public bool AutostartEnabled
    {
        get
        {
            try { return _platform.AutostartEnabled; }
            catch { return false; }
        }
        set
        {
            try { _platform.AutostartEnabled = value; }
            catch { /* read-only profile: ignore */ }
        }
    }

    bool HudBusy => _hud?.IsInteracting ?? false;

    public OverlayWindow(string[] args)
    {
        _demo = args.Contains("--demo");
        int gi = Array.IndexOf(args, "--game");
        if (gi >= 0 && gi + 1 < args.Length) _startGame = args[gi + 1];

        Title = "Desk Arcade";
        SystemDecorations = SystemDecorations.None;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = Brushes.Transparent;
        CanResize = false;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        try { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://DeskArcade/assets/DeskArcade.png"))); }
        catch { /* icon is cosmetic */ }

        _root.Children.Add(_gameLayer);
        _root.Children.Add(Fx.Layer);
        _root.Children.Add(_hudLayer);
        Content = _root;

        L.Apply(Settings.Language);
        Stats.Unlocked += OnAchievement;
        Daily = new Daily(Settings, Stats);
        _platform = DesktopPlatform.Create();
        Sound = new Sound(_platform) { Enabled = Settings.Sound, Volume = Settings.Volume };
        Stats.CounterChanged += counter =>
        {
            if (counter != Daily.For(Daily.Today).Counter || !Daily.Check(Daily.Today)) return;
            SaveSettings();
            int streak = Daily.Streak(Daily.Today);
            Dispatcher.UIThread.Post(() =>
            {
                Sound.Play("best", 0.8);
                Notice(L.T("Daily challenge done!"), streak > 1 ? L.F("{0} days in a row", streak) : L.T("come back tomorrow for a new one"), Color.FromRgb(255, 209, 102));
                _tray?.Refresh();
            });
        };
        Platforms.Enabled = Settings.Platforms;
        Fx.ReducedMotion = Settings.ReducedMotion;
        Engine.Art.ColorBlind = Settings.ColorBlind;
        Themes.Apply(Settings.Theme, DateTime.Today);
        _board = new OfficeBoard(() => _boardEntry);
        _boardTimer.Tick += (_, _) => RefreshBoardEntry();

        _platformTimer.Tick += (_, _) => RefreshPlatforms();
        _blinkTimer.Tick += (_, _) => _hud?.Blink();
        _hudHoverTimer.Tick += (_, _) => CheckHudHover();
        _statsTimer.Tick += (_, _) =>
        {
            Stats.Save();
            if (Themes.Apply(Settings.Theme, DateTime.Today)) OnThemeChanged(); // "seasonal" at the turn of a month
        };
        _root.PointerPressed += OnPointerPressed;
        _root.PointerReleased += OnPointerReleased;
        _root.PointerMoved += (_, e) => _eventPointer = e.GetPosition(_root);
        _root.PointerCaptureLost += OnPointerCaptureLost;

        Opened += (_, _) =>
        {
            _platform.AttachOverlay(this);
            PlaceOnMonitor();
        };
        PositionChanged += (_, _) =>
        {
            if (!IsVisible || _hud == null) return;
            UpdateArena();
            PlaceHud();
            Current?.Layout();
            Wake();
        };
    }

    public void Start()
    {
        _games.Add(new HoopsGame(this));
        _games.Add(new ArcheryGame(this));
        _games.Add(new JuggleGame(this));
        _games.Add(new GolfGame(this));
        _games.Add(new BugsGame(this));
        _games.Add(new CansGame(this));
        _games.Add(new BricksGame(this));
        _games.Add(new BubblesGame(this));
        _games.Add(new HockeyGame(this));
        _games.Add(new PongGame(this));
        _games.Add(new ClayGame(this));
        _games.Add(new WhackGame(this));
        _games.Add(new PlinkoGame(this));
        _games.Add(new TowerGame(this));
        _games.Add(new SlingshotGame(this));
        _games.Add(new CheckersGame(this));
        _games.Add(new ChessGame(this));
        _games.Add(new ConnectFourGame(this));
        _games.Add(new TicTacToeGame(this));
        _games.Add(new SeaBattleGame(this));
        var durak = new DurakGame(this);
        durak.SetupRequested += () => DurakRoomWindow.ShowFor(this, durak);
        _games.Add(durak);
        _games.Add(new BowlingGame(this));
        _games.Add(new PoolGame(this));
        _games.Add(new PetGame(this));

        _hud = new Hud(_games);
        _hud.GameClicked += SwitchGame;
        _hud.InteractionStarted += () =>
        {
            PushHitShapes();
            Wake();
        };
        _hud.Moved += () =>
        {
            Settings.HudX = _hud.Position.X - Arena.Left;
            Settings.HudY = _hud.Position.Y - Arena.Top;
            SaveSettings();
            PushHitShapes();
        };
        _hud.SizeChanged += (_, e) =>
        {
            // expand toward the middle of the screen, so a board near the right edge stays on screen
            double grow = e.NewSize.Width - e.PreviousSize.Width;
            if (e.PreviousSize.Width > 0 && Math.Abs(grow) > 0.5 && _hud.Position.X + e.PreviousSize.Width / 2 > Arena.Center.X)
                _hud.Position = new Vec2(_hud.Position.X - grow, _hud.Position.Y);
            ClampHud();
            PushHitShapes();
            Wake();
        };
        _hudLayer.Children.Add(_hud);
        _hudLayer.Children.Add(_raceLabel);
        _race = new RaceMode(this);

        PlaceWindow();
        UpdateArena();
        Show();
        SwitchGame(_startGame ?? Settings.Game);
        _tray = new Tray(this, Icon);

        Ipc.StartServer(msg => Dispatcher.UIThread.Post(() => OnSignal(msg)), _cts.Token);
        Lan.StateChanged += () => Dispatcher.UIThread.Post(OnLanStateChanged);
        Lan.MessageArrived += () => Dispatcher.UIThread.Post(Wake);
        Lan.GameChanged += id => Dispatcher.UIThread.Post(() => SwitchGame(id));
        Lan.ActionReceived += (x, y, pts) => Dispatcher.UIThread.Post(() => ShowRivalAction(x, y, pts));
        Lan.EmoteReceived += i => Dispatcher.UIThread.Post(() =>
        {
            Sound.Play("best", 0.35, 1.5);
            Notice(L.T(LanLink.Emotes[i]), L.F("from {0}", Lan.PeerName), Color.FromRgb(255, 209, 102));
        });
        Shortcuts.Current = HotkeySet.From(Settings.ShortcutModifiers, Settings.ShortcutKeys);
        _platform.RegisterHotkeys(OnHotkey, Shortcuts.Current);
        _platformTimer.Start();
        _blinkTimer.Start();
        _hudHoverTimer.Start();
        _statsTimer.Start();
        if (Settings.ShareLeaderboard) StartBoard();
        if (Settings.CheckForUpdates && (Settings.LastUpdateCheck is not DateTime lastCheck || DateTime.UtcNow - lastCheck > TimeSpan.FromHours(20)))
            DispatcherTimer.RunOnce(() => CheckForUpdates(manual: false), TimeSpan.FromSeconds(25));

        if (Settings.FirstRun)
        {
            Settings.FirstRun = false;
            SaveSettings();
            DispatcherTimer.RunOnce(() =>
            {
                Fx.Popup(new Vec2(Arena.Left + Arena.Width / 2, Arena.Top + Arena.Height * 0.45), L.T("Welcome to Desk Arcade"),
                    Color.FromRgb(255, 209, 102), 34, 6, L.F("click the scoreboard to pick a game · {0} show/hide · {1} next game", Shortcuts.Label(HotkeyAction.ToggleOverlay), Shortcuts.Label(HotkeyAction.NextGame)));
                Wake();
            }, TimeSpan.FromSeconds(2.2));
        }

        if (_demo)
        {
            _demoTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _demoTimer.Tick += (_, _) =>
            {
                Current?.DemoTick();
                Wake();
            };
            _demoTimer.Start();
        }
    }

    // ------------------------------------------------------------------ placement

    static string ScreenKey(Screen s) => $"{s.Bounds.X},{s.Bounds.Y},{s.Bounds.Width},{s.Bounds.Height}";

    Screen? CurrentScreen()
    {
        var all = Screens.All;
        if (all.Count == 0) return null;
        return all.FirstOrDefault(s => ScreenKey(s) == Settings.MonitorName) ?? Screens.Primary ?? all[0];
    }

    void PlaceWindow()
    {
        var scr = CurrentScreen();
        if (scr == null) return;
        var bounds = _platform.OverlayFitsWorkArea ? scr.WorkingArea : scr.Bounds;
        Position = bounds.Position;
        Width = bounds.Width / scr.Scaling;
        Height = bounds.Height / scr.Scaling;
        // Mutter judges a move with the window's size at that moment, so a window still sized for a taller
        // monitor can be refused a monitor with a top bar. Repeat the move now that the size fits.
        Position = bounds.Position;
    }

    void UpdateArena()
    {
        var scr = CurrentScreen();
        var window = new Rect(0, 0, Width, Height);
        if (scr == null)
        {
            Arena = window;
        }
        else
        {
            var wa = scr.WorkingArea;
            Vec2 tl = this.PointToClient(wa.TopLeft);
            Vec2 br = this.PointToClient(wa.BottomRight);
            var arena = new Rect(tl.ToPoint(), br.ToPoint()).Intersect(window);
            Arena = arena.Width < 200 || arena.Height < 200 ? window : arena;
        }
        Fx.Bounds = Arena;
    }

    void PlaceOnMonitor()
    {
        PlaceWindow();
        UpdateArena();
        PlaceHud();
        Current?.Layout();
        RefreshPlatforms();
        PushHitShapes();
        Wake();
    }

    void PlaceHud()
    {
        var area = _hud.Area;
        _hud.Position = new Vec2(
            Arena.Left + (Settings.HudX ?? Arena.Width - area.Width - 28),
            Arena.Top + (Settings.HudY ?? 150));
        ClampHud();
    }

    void ClampHud()
    {
        var area = _hud.Area;
        _hud.Position = new Vec2(
            Math.Clamp(area.X, Arena.Left, Math.Max(Arena.Left, Arena.Right - area.Width)),
            Math.Clamp(area.Y, Arena.Top, Math.Max(Arena.Top, Arena.Bottom - area.Height)));
    }

    /// <summary>Backup for a missed pointer-leave: shrink the expanded board once the cursor is elsewhere.</summary>
    void CheckHudHover()
    {
        if (!IsVisible || !_hud.IsExpanded || _hud.IsInteracting) return;
        if (!_platform.TryGetCursor(out var px)) return;
        if (!_hud.Area.Inflate(6).Contains(this.PointToClient(px))) _hud.PointerLeft();
    }

    void RefreshPlatforms()
    {
        if (!IsVisible) return;
        _platformTimer.Interval = TimeSpan.FromMilliseconds(_loopOn ? 100 : 350);
        _nativeWindows.Clear();
        _windowRects.Clear();
        if (Platforms.Enabled)
        {
            try { _platform.EnumerateWindows(_nativeWindows); }
            catch { /* window list is best effort */ }
        }
        foreach (var w in _nativeWindows)
        {
            Vec2 tl = this.PointToClient(w.Bounds.TopLeft);
            Vec2 br = this.PointToClient(w.Bounds.BottomRight);
            _windowRects.Add((w.Id, new Rect(tl.ToPoint(), br.ToPoint())));
        }
        if (Platforms.Refresh(_windowRects, Arena)) Wake();
    }

    // ------------------------------------------------------------------ input

    void UpdatePointer()
    {
        if (_captured || HudBusy)
        {
            Pointer = _eventPointer; // the captured pointer keeps reporting even outside our shapes
            return;
        }
        Pointer = _platform.TryGetCursor(out var px) ? this.PointToClient(px) : _eventPointer;
    }

    void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || Current == null || _captured) return;
        if (_paused)
        {
            Resume();
            return; // the click only wakes the game
        }
        var point = e.GetCurrentPoint(_root);
        bool right = point.Properties.IsRightButtonPressed;
        if (!right && !point.Properties.IsLeftButtonPressed) return;
        _eventPointer = point.Position;
        Pointer = _eventPointer;
        if (Current.PointerDown(_eventPointer, right))
        {
            _captured = true;
            e.Pointer.Capture(_root);
        }
        e.Handled = true;
        PushHitShapes();
        Wake();
    }

    void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_captured) return;
        _eventPointer = e.GetPosition(_root);
        _captured = false;
        e.Pointer.Capture(null);
        Current?.PointerUp(_eventPointer);
        PushHitShapes();
        Wake();
    }

    void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!_captured) return;
        _captured = false;
        Current?.PointerUp(_eventPointer);
        PushHitShapes();
        Wake();
    }

    /// <summary>Tell the platform which areas take the mouse (only when that changed).</summary>
    void PushHitShapes()
    {
        _hitShapes.Clear();
        if (IsVisible)
        {
            Current?.CollectHitShapes(_hitShapes);
            if (_hud != null) _hitShapes.Add(HitShape.Box(_hud.Area));
        }
        bool capture = _captured || HudBusy;
        if (capture == _pushedCapture && _hitShapes.SequenceEqual(_pushedHitShapes)) return;
        _pushedHitShapes.Clear();
        _pushedHitShapes.AddRange(_hitShapes);
        _pushedCapture = capture;
        _platform.SetInputRegions(_hitShapes, capture);
    }

    void OnHotkey(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.ToggleOverlay: ToggleOverlay(); break;
            case HotkeyAction.NextGame: NextGame(); break;
            case HotkeyAction.Summon: SummonToCursor(); break;
        }
    }

    // ------------------------------------------------------------------ frame loop

    public void Wake()
    {
        _idle = 0;
        if (_loopOn || !IsVisible) return;
        _loopOn = true;
        _lastTick = _clock.Elapsed.TotalSeconds;
        RequestAnimationFrame(OnFrame);
    }

    void OnFrame(TimeSpan _)
    {
        if (!_loopOn) return;
        double now = _clock.Elapsed.TotalSeconds;
        double dt = Math.Clamp(now - _lastTick, 0, 0.05);
        _lastTick = now;

        UpdatePointer();
        bool busy = _captured || HudBusy;
        if (Current != null && !_paused)
        {
            bool playing = Current.Update(dt) || _captured;
            if (playing)
            {
                Stats.AddTime(Current.Id, dt); // only time spent actually playing
                if (_claudeSince != null) _playedWhileClaude += dt;
                CountTowardBreak(now, dt);
            }
            busy |= playing;
        }
        busy |= Fx.Update(dt);
        PushHitShapes();

        if (busy) _idle = 0;
        else if ((_idle += dt) > 0.6) _loopOn = false; // nothing moving: stop rendering, zero CPU

        if (_loopOn && IsVisible) RequestAnimationFrame(OnFrame);
        else _loopOn = false;
    }

    // ------------------------------------------------------------------ IGameHost + commands

    public void HudChanged()
    {
        if (Current != null) _hud.Show(Current.Hud);
    }

    public void SaveSettings() => Settings.Save();

    static string HintFor(string id) => id switch
    {
        "hoops" => L.T("drag the ball and flick it into the hoop"),
        "archery" => L.T("drag back from the bow, release to shoot"),
        "juggle" => L.T("click the ball to kick it — don't let it drop"),
        "golf" => L.T("drag back from the ball to putt it into the cup"),
        "bugs" => L.T("squash the bugs — spare the ladybugs"),
        "cans" => L.T("throw the ball from behind the line"),
        "bricks" => L.T("click the paddle to launch — the mouse steers it"),
        "bubbles" => L.T("click a bubble to split it — clear them all in time"),
        "hockey" => L.T("drag your mallet and score in the right-hand goal"),
        "pong" => L.T("drag your paddle up and down — get the ball past the right edge"),
        "checkers" => L.T("click a piece, then the square it should move to"),
        "chess" => L.T("click a piece, then the square it should move to"),
        "connect4" => L.T("click a column to drop a disc — four in a row wins"),
        "tictactoe" => L.T("click a square — three in a row wins"),
        "seabattle" => L.T("click the enemy grid to start, then fire — a hit shoots again"),
        "durak" => L.T("play the computer, or set up a room for up to four co-workers"),
        "clay" => L.T("click the trap machine, then shoot the clays at the top of their arc"),
        "slingshot" => L.T("drag back from the slingshot and let go — knock the tower down"),
        "pet" => L.T("click the pet to pet it — drag to carry and throw it"),
        "tower" => L.T("click the sliding block to drop it — stack as high as you can"),
        "plinko" => L.T("click the strip to drop a disc — the gold slot is the jackpot"),
        "whack" => L.T("whack the bugs as they peek out — spare the ladybugs"),
        "bowling" => L.T("drag back from the ball and let go — knock all ten pins down"),
        "pool" => L.T("drag back from the cue ball to shoot — pot every ball in as few shots as you can"),
        _ => "",
    };

    public void SwitchGame(string id)
    {
        var next = _games.FirstOrDefault(g => g.Id == id) ?? _games[0];
        if (next == Current) return;
        if (Lan.Connected && Lan.Role == LanRole.Guest && next.Id != Lan.GameId)
        {
            // the host picks the game; a guest switching on its own would leave the two screens out of step
            Notice(L.T("The host picks the game"), L.F("{0} is hosting", Lan.PeerName), Color.FromRgb(170, 180, 195));
            return;
        }
        _captured = false;
        Current?.Deactivate();
        _gameLayer.Children.Clear();
        Current = next;
        _gameLayer.Children.Add(next.Layer);
        next.Activate();

        Settings.Game = next.Id;
        SaveSettings();
        if (Lan.Role == LanRole.Host) Lan.SendGame(next.Id); // the guest follows, or joins into this game
        _race.Reset();
        _hud.SetGame(next.Id, next.Title);
        HudChanged();
        _tray?.Refresh();
        if (IsVisible)
            Fx.Popup(new Vec2(Arena.Left + Arena.Width / 2, Arena.Top + Arena.Height * 0.28), L.T(next.Title),
                Color.FromRgb(255, 209, 102), 46, 1.8, HintFor(next.Id));
        PushHitShapes();
        Wake();
    }

    public void NextGame()
    {
        if (!IsVisible) SetOverlayVisible(true);
        int i = Current == null ? -1 : _games.IndexOf(Current);
        SwitchGame(_games[(i + 1) % _games.Count].Id);
    }

    public void ToggleOverlay() => SetOverlayVisible(!IsVisible);

    public void SetOverlayVisible(bool visible)
    {
        if (visible == IsVisible) return;
        if (visible)
        {
            Show();
            _platform.AttachOverlay(this); // some window managers reset hints on remap
            PlaceOnMonitor();
        }
        else
        {
            _captured = false;
            _loopOn = false;
            Hide();
        }
        _tray?.Refresh();
    }

    public void SummonToCursor()
    {
        if (!IsVisible) SetOverlayVisible(true);
        UpdatePointer();
        Current?.Summon(Pointer);
        PushHitShapes();
        Wake();
    }

    public void ApplySettings()
    {
        Sound.Enabled = Settings.Sound;
        Sound.Volume = Settings.Volume;
        Platforms.Enabled = Settings.Platforms;
        Fx.ReducedMotion = Settings.ReducedMotion;
        if (Engine.Art.ColorBlind != Settings.ColorBlind)
        {
            Engine.Art.ColorBlind = Settings.ColorBlind;
            Current?.Layout(); // redraw pieces in the new colours
            _hud.SetClaude(_hud.Status, _claudeSince);
        }
        RefreshPlatforms();
        SaveSettings();
        _tray?.Refresh();
        Wake();
    }

    public void MoveToNextMonitor()
    {
        var all = Screens.All;
        if (all.Count < 2) return;
        var current = CurrentScreen();
        int i = current == null ? -1 : all.ToList().FindIndex(s => ScreenKey(s) == ScreenKey(current));
        Settings.MonitorName = ScreenKey(all[(i + 1) % all.Count]);
        SaveSettings();
        if (!IsVisible) SetOverlayVisible(true);
        else PlaceOnMonitor();
    }

    public void ResetPositions()
    {
        Settings.ResetPositions();
        SaveSettings();
        PlaceHud();
        Current?.Layout();
        PushHitShapes();
        Wake();
    }

    public void ResetScores()
    {
        Settings.ResetScores();
        SaveSettings();
        HudChanged();
    }

    public async void CopyHookConfig()
    {
        string exe = Program.LaunchPath.Replace('\\', '/');
        string Cmd(string signal) => $"\\\"{exe}\\\" --signal {signal}";
        string json = $$"""
            {
              "hooks": {
                "UserPromptSubmit": [ { "hooks": [ { "type": "command", "command": "{{Cmd("working")}}" } ] } ],
                "Stop": [ { "hooks": [ { "type": "command", "command": "{{Cmd("done")}}" } ] } ],
                "Notification": [ { "hooks": [ { "type": "command", "command": "{{Cmd("attention")}}" } ] } ]
              }
            }
            """;
        try
        {
            if (Clipboard != null) await Clipboard.SetTextAsync(json);
            Notice(L.T("Hook config copied"), L.T("merge it into ~/.claude/settings.json"), Color.FromRgb(255, 209, 102));
        }
        catch { /* clipboard busy */ }
    }

    void Notice(string title, string sub, Color color)
    {
        if (!IsVisible) return;
        // popups drift upward, so start well clear of the scoreboard (or above it when it sits low)
        var b = _hud.Area;
        double y = b.Bottom + 150 < Arena.Bottom ? b.Bottom + 110 : b.Top - 40;
        Fx.Popup(new Vec2(b.Left + b.Width / 2, y), title, color, 26, 2.8, sub);
        Wake();
    }

    void OnSignal(string msg)
    {
        switch (msg)
        {
            case "working" or "start":
                ClaudeWorking();
                break;
            case "done" or "stop":
                ClaudeAlert(ClaudeStatus.Done, "done", L.T("Claude is done"), Color.FromRgb(61, 220, 132));
                break;
            case "attention" or "notify":
                ClaudeAlert(ClaudeStatus.Attention, "attention", L.T("Claude needs you"), Color.FromRgb(255, 107, 107));
                break;
            case "idle": _hud.SetClaude(ClaudeStatus.Unknown); break;
            case "show": SetOverlayVisible(true); break;
            case "hide": SetOverlayVisible(false); break;
            case "toggle": ToggleOverlay(); break;
            case "next": NextGame(); break;
            case "summon": SummonToCursor(); break;
            case "expand": _hud.Expand(); break;
            case "stats": OpenStats(); break;
            case "lan-host": HostLan(); break;
            case "lan-join": JoinLan(); break;
            case "lan-leave": LeaveLan(); break;
            case "lan-find": OpenLobby(); break;
            case "shortcuts": OpenShortcuts(); break;
            case "quit": Quit(); break;
            case "durak-rooms": OpenDurakRooms(); break;
            default: DurakSignal(msg); break;
        }
    }

    /// <summary>
    /// Scriptable Durak rooms, for tests and shortcuts: "durak-solo:N" (N computer players),
    /// "durak-host[:code]", "durak-join:code" and "durak-start:N" (N seats in all).
    /// </summary>
    void DurakSignal(string msg)
    {
        if (_games.OfType<DurakGame>().FirstOrDefault() is not { } durak) return;
        int colon = msg.IndexOf(':');
        string verb = colon < 0 ? msg : msg[..colon], arg = colon < 0 ? "" : msg[(colon + 1)..];
        int.TryParse(arg, out int n);
        switch (verb)
        {
            case "durak-solo": durak.StartSolo(Math.Clamp(n, 1, 3)); break;
            case "durak-host": durak.HostRoom(arg.Length > 0 ? Net.RoomLink.CleanCode(arg) : null); break;
            case "durak-join": durak.JoinRoom(arg, null); break;
            case "durak-start": durak.StartRoom(Math.Clamp(n, 2, Net.RoomLink.MaxSeats)); break;
            case "durak-leave": durak.LeaveRoom(); break;
            default: return;
        }
        SetOverlayVisible(true);
        SwitchGame(durak.Id);
    }

    // ------------------------------------------------------------------ LAN multiplayer

    /// <summary>Hosts the current game, or Air Hockey if the current one is single-player only.</summary>
    public void HostLan()
    {
        if (Current?.SupportsLan != true) SwitchGame(_games.First(g => g.SupportsLan).Id);
        Lan.Host(Current!.Id);
        if (Lan.State == LanState.Off)
            Notice(L.T("Can't host"), L.F("UDP port {0} is in use", LanLink.Port), Color.FromRgb(255, 107, 107));
    }

    public void JoinLan(System.Net.IPEndPoint? address = null) => Lan.Join(address);

    public void OpenLobby() => LobbyWindow.ShowFor(this);

    public void OpenDurakRooms()
    {
        if (_games.OfType<DurakGame>().FirstOrDefault() is { } durak) DurakRoomWindow.ShowFor(this, durak);
    }

    public void OpenShortcuts() => ShortcutsWindow.ShowFor(this);

    /// <summary>"Today: Make 15 baskets in Hoops (4/15) · streak 2", for the tray and the stats window.</summary>
    public string DailyLine
    {
        get
        {
            var day = Daily.Today;
            var c = Daily.Current(day);
            string text = L.F("Today: {0} ({1}/{2})", L.F(c.Text, c.Target), Daily.Progress(day), c.Target);
            if (Daily.Done(day)) text += " ✓";
            int streak = Daily.Streak(day);
            return streak > 0 ? L.F("{0} · streak {1}", text, streak) : text;
        }
    }

    public void PlayDaily() => SwitchGame(Daily.For(Daily.Today).GameId);

    /// <summary>
    /// Windows installs: downloads the new installer and runs it silently. The installer asks this copy to
    /// quit and starts the new version afterwards. Elsewhere, or if anything fails, opens the release page.
    /// </summary>
    public async void InstallUpdate()
    {
        if (_update is not UpdateInfo u) return;
        if (!UpdateChecker.CanInstall)
        {
            UpdateChecker.OpenInBrowser(u.Url);
            return;
        }
        SetOverlayVisible(true);
        Notice(L.F("Downloading version {0}…", u.Version.ToString(3)), L.T("the game restarts when it is done"), Color.FromRgb(77, 163, 255));
        string? installer = await UpdateChecker.DownloadInstallerAsync(u);
        if (installer == null)
        {
            Notice(L.T("Couldn't download the update"), L.T("opening the download page instead"), Color.FromRgb(255, 107, 107));
            UpdateChecker.OpenInBrowser(u.Url);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(installer, "/SILENT /SUPPRESSMSGBOXES /NORESTART") { UseShellExecute = true });
        }
        catch
        {
            UpdateChecker.OpenInBrowser(u.Url); // e.g. the elevation prompt was declined
        }
    }

    public void SetPet(string kind)
    {
        Settings.PetKind = kind;
        SaveSettings();
        foreach (var pet in _games.OfType<PetGame>()) pet.Rebuild();
        _hud.SetGame(Current!.Id, Current.Title); // the scoreboard icon too
        SwitchGame("pet");
        _tray?.Refresh();
    }

    /// <summary>Saves and registers new shortcuts; returns a line for the Shortcuts window to show.</summary>
    public string ApplyShortcuts(HotkeySet keys)
    {
        Settings.ShortcutModifiers = keys.Modifiers.ToString();
        Settings.ShortcutKeys = keys.Keys;
        SaveSettings();
        string saved = string.Join(" · ", Enum.GetValues<HotkeyAction>().Select(keys.Label));
        if (!_platform.HotkeysApplyLive) return L.F("Saved: {0}. They take effect the next time Desk Arcade starts.", saved);
        Shortcuts.Current = keys;
        _tray?.Rebuild();
        if (_platform.RegisterHotkeys(OnHotkey, keys)) return L.F("Saved: {0}", saved);
        return L.F("Saved, but another app already uses one of these ({0}). Try other letters or keys.", saved);
    }

    public void SendEmote(int index)
    {
        if (!Lan.Connected) return;
        Lan.SendEmote(index);
        Notice(L.T(LanLink.Emotes[index]), L.F("sent to {0}", Lan.PeerName), Color.FromRgb(170, 180, 195));
    }

    public void LeaveLan() => Lan.Stop();

    public string LanStatus => Lan.State switch
    {
        LanState.Waiting when Lan.Role == LanRole.Host => L.T("Waiting for a player to join…") + " " + LanLink.LocalAddresses(),
        LanState.Waiting => L.T("Looking for a host…"),
        LanState.Connected => L.F("Playing with {0}", Lan.PeerName),
        _ => L.T("Not connected"),
    };

    /// <summary>Race mode's line under the scoreboard (the rival's live score), or null to hide it.</summary>
    public void RoundStarted() => _race.LocalStart();

    public void RoundEnded(int score) => _race.LocalEnd(score);

    static readonly Color RivalColor = Color.FromRgb(255, 92, 108);

    public void ShareAction(Vec2 at, int points)
    {
        if (!Lan.Connected || Arena.Width <= 0 || Arena.Height <= 0) return;
        Lan.SendAction(Math.Clamp((at.X - Arena.Left) / Arena.Width, 0, 1), Math.Clamp((at.Y - Arena.Top) / Arena.Height, 0, 1), points);
    }

    /// <summary>The rival clicked, popped or whacked something: a ghost ring where they did, with what it scored.</summary>
    void ShowRivalAction(double x, double y, int points)
    {
        if (!IsVisible || !Lan.Connected) return;
        var at = new Vec2(Arena.Left + x * Arena.Width, Arena.Top + y * Arena.Height);
        Fx.Marker(at, RivalColor);
        if (points != 0) Fx.Popup(at - new Vec2(0, 30), points > 0 ? $"+{points}" : $"−{-points}", RivalColor, 18, 0.8);
        Wake();
    }

    public void SetRaceLabel(string? text)
    {
        _raceLabel.IsVisible = text != null;
        if (text == null) return;
        _raceLabel.Text = text;
        var b = _hud.Area;
        Canvas.SetLeft(_raceLabel, b.Left);
        Canvas.SetTop(_raceLabel, b.Bottom + 6 < Arena.Bottom - 30 ? b.Bottom + 6 : b.Top - 30);
    }

    public void RaceResult(string title, string sub, Color color, bool won)
    {
        var at = new Vec2(Arena.Center.X, Arena.Top + Arena.Height * 0.3);
        Fx.Popup(at, title, color, 40, 2.6, sub);
        if (won) Fx.Burst(at, new[] { color, Colors.White }, 40, 520, 650, 7, 1.0);
        Sound.Play(won ? "best" : "buzzer", won ? 0.8 : 0.4);
        Wake();
    }

    void OnLanStateChanged()
    {
        _race.Reset();
        if (Lan.Connected)
        {
            if (Lan.Role == LanRole.Guest) SwitchGame(Lan.GameId);
            SetOverlayVisible(true);
            Notice(L.T("Connected"), L.F("Playing with {0}", Lan.PeerName), Color.FromRgb(61, 220, 132));
        }
        else if (Lan.State == LanState.Waiting && Lan.Role == LanRole.Host)
            Notice(L.T("Hosting"), L.T("Waiting for a player to join…"), Color.FromRgb(77, 163, 255));
        Current?.Layout();
        HudChanged();
        _tray?.Refresh();
        Wake();
    }

    void Resume()
    {
        if (!_paused) return;
        _paused = false;
        Fx.Popup(new Vec2(Arena.Center.X, Arena.Top + Arena.Height * 0.3), L.T("Resumed"), Color.FromRgb(61, 220, 132), 30, 1.0);
        Wake();
    }

    void ClaudeWorking()
    {
        if (_hud.Status != ClaudeStatus.Working)
        {
            _claudeSince = DateTime.UtcNow;
            _playedWhileClaude = 0;
        }
        Resume();
        _hud.SetClaude(ClaudeStatus.Working, _claudeSince);
        if (Settings.ClaudeAutoShow && !IsVisible) SetOverlayVisible(true);
    }

    void ClaudeAlert(ClaudeStatus status, string sound, string title, Color color)
    {
        TimeSpan? waited = _claudeSince is DateTime since ? DateTime.UtcNow - since : null;
        bool backToWork = status == ClaudeStatus.Done && Settings.BackToWork && _playedWhileClaude >= 5;
        if (backToWork) title = L.T("Claude is done · back to work");
        _claudeSince = null;
        _hud.SetClaude(status, null, waited);
        if (status == ClaudeStatus.Done && IsVisible && Current != null) Stats.Add("claude.done");
        if (Settings.ClaudeNotify)
        {
            Sound.Play(sound, 0.9);
            string sub = backToWork ? L.T("the game will still be here later") : status == ClaudeStatus.Done ? L.T("your turn!") : L.T("check the terminal");
            if (waited is TimeSpan w && w.TotalSeconds >= 5)
                sub = _playedWhileClaude >= 5
                    ? L.F("{0} · Claude worked {1}, you played {2}", sub, Hud.FormatWait(w), Hud.FormatWait(TimeSpan.FromSeconds(_playedWhileClaude)))
                    : L.F("{0} · waited {1}", sub, Hud.FormatWait(w));
            Notice(title, sub, color);
        }
        if (Settings.ClaudePause && IsVisible && !_paused && Current != null)
        {
            _paused = true;
            _captured = false;
            Fx.Popup(new Vec2(Arena.Center.X, Arena.Top + Arena.Height * 0.42), L.T("PAUSED"), Colors.White, 34, 3.0, L.T("click the game to resume"));
        }
        if (Settings.ClaudeAutoHide && IsVisible)
        {
            // leave the notice on screen for a moment, then get out of the way (hiding also pauses the game)
            DispatcherTimer.RunOnce(() =>
            {
                if (_hud.Status == status) SetOverlayVisible(false);
            }, TimeSpan.FromSeconds(Settings.ClaudeNotify ? 2.5 : 0.2));
        }
    }

    public void SetVolume(double volume)
    {
        Settings.Volume = volume;
        ApplySettings();
        Sound.Play("score", 0.7);
    }

    public void SetLanguage(string code)
    {
        Settings.Language = code;
        SaveSettings();
        L.Apply(code);
        if (Current != null) _hud.SetGame(Current.Id, Current.Title);
        HudChanged();
        _tray?.Rebuild();
    }

    public void OpenStats() => StatsWindow.ShowFor(this);

    // ------------------------------------------------------------------ office leaderboard, breaks, themes

    public void OpenLeaderboard() => LeaderboardWindow.ShowFor(this);

    /// <summary>Everyone sharing today, this player first.</summary>
    public List<BoardEntry> BoardEntries()
    {
        RefreshBoardEntry();
        return _board.Entries(_boardEntry.Day);
    }

    public void SetShareLeaderboard(bool share)
    {
        Settings.ShareLeaderboard = share;
        SaveSettings();
        if (share) StartBoard();
        else
        {
            _boardTimer.Stop();
            _board.Stop();
        }
        _tray?.Refresh();
    }

    void StartBoard()
    {
        RefreshBoardEntry();
        _board.Start();
        _boardTimer.Start();
    }

    /// <summary>Snapshots today's scores on the UI thread; the board shares the snapshot from its own thread.</summary>
    void RefreshBoardEntry() => _boardEntry = new BoardEntry(OfficeBoard.InstanceId, LanLink.MyName, DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        OfficeBoard.Scores(Stats.Today));

    public void SetBreakMinutes(int minutes)
    {
        Settings.BreakMinutes = minutes;
        _playStreak = 0;
        SaveSettings();
        _tray?.Refresh();
    }

    /// <summary>Counts play toward the break reminder; five minutes without playing counts as a break.</summary>
    void CountTowardBreak(double now, double dt)
    {
        if (now - _lastPlayed > 300) _playStreak = 0;
        _lastPlayed = now;
        if (Settings.BreakMinutes <= 0 || (_playStreak += dt) < Settings.BreakMinutes * 60) return;
        _playStreak = 0;
        Sound.Play("attention", 0.6);
        Notice(L.F("You've played {0} minutes", Settings.BreakMinutes), L.T("time for a short break · stretch and look away from the screen"),
            Color.FromRgb(120, 200, 255));
    }

    public void SetTheme(string id)
    {
        Settings.Theme = id;
        SaveSettings();
        if (Themes.Apply(id, DateTime.Today)) OnThemeChanged();
        _tray?.Refresh();
    }

    void OnThemeChanged()
    {
        foreach (var game in _games) game.ThemeChanged();
        Current?.Layout();
        Wake();
    }

    public void ResetStats()
    {
        Stats.Reset();
        Notice(L.T("Stats reset"), L.T("achievements start over"), Color.FromRgb(255, 209, 102));
    }

    public async void CheckForUpdates(bool manual)
    {
        if (_checkingUpdates) return;
        _checkingUpdates = true;
        try
        {
            var update = await UpdateChecker.CheckAsync();
            Settings.LastUpdateCheck = DateTime.UtcNow;
            SaveSettings();
            _update = update;
            _tray?.Refresh();
            if (update != null)
            {
                if (manual) UpdateChecker.OpenInBrowser(update.Url);
                Notice(L.F("Desk Arcade {0} is available", update.Version.ToString(3)),
                    manual ? L.T("opening the download page") : L.T("download it from the tray menu"), Color.FromRgb(255, 209, 102));
            }
            else if (manual)
            {
                Notice(L.T("You're up to date"), L.F("version {0}", UpdateChecker.Current.ToString(3)), Color.FromRgb(61, 220, 132));
            }
        }
        finally
        {
            _checkingUpdates = false;
        }
    }

    void OnAchievement(Achievement a)
    {
        Sound.Play("best", 0.7);
        if (!IsVisible) return;
        double now = _clock.Elapsed.TotalSeconds;
        _achievementRow = now - _lastAchievementAt < 3.5 ? _achievementRow + 1 : 0; // stack unlocks that land together
        _lastAchievementAt = now;
        var at = new Vec2(Arena.Center.X, Arena.Top + Arena.Height * 0.18 + _achievementRow * 78);
        var gold = Color.FromRgb(255, 209, 102);
        Fx.Popup(at, L.T("Achievement unlocked"), gold, 28, 3.2, L.T(a.Title));
        Fx.Burst(at, new[] { gold, Colors.White }, 30, 460, 600, 6, 1.0);
        Wake();
    }

    public void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        Lan.Stop();
        _board.Stop();
        _boardTimer.Stop();
        SaveSettings();
        _cts.Cancel();
        _loopOn = false;
        _platformTimer.Stop();
        _blinkTimer.Stop();
        _hudHoverTimer.Stop();
        _statsTimer.Stop();
        _demoTimer?.Stop();
        Stats.Save();
        _tray?.Dispose();
        Sound.Dispose();
        _platform.Dispose();
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
        else
            Environment.Exit(0);
    }
}
