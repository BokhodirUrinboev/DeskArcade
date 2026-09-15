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
    readonly IDesktopPlatform _platform;
    DispatcherTimer? _demoTimer;

    Hud _hud = null!;
    Tray? _tray;
    bool _loopOn, _captured, _quitting, _pushedCapture;
    double _lastTick, _idle;
    Vec2 _eventPointer;

    public Settings Settings { get; } = Settings.Load();
    public Sound Sound { get; }
    public Fx Fx { get; } = new();
    public Platforms Platforms { get; } = new();
    public Rect Arena { get; private set; }
    public Vec2 Pointer { get; private set; }
    public Rect HudBounds => _hud?.Area ?? default;
    public IReadOnlyList<MiniGame> Games => _games;
    public MiniGame? Current { get; private set; }
    public bool OverlayVisible => IsVisible;

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

        _platform = DesktopPlatform.Create();
        Sound = new Sound(_platform) { Enabled = Settings.Sound, Volume = Settings.Volume };
        Platforms.Enabled = Settings.Platforms;

        _platformTimer.Tick += (_, _) => RefreshPlatforms();
        _blinkTimer.Tick += (_, _) => _hud?.Blink();
        _hudHoverTimer.Tick += (_, _) => CheckHudHover();
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

        PlaceWindow();
        UpdateArena();
        Show();
        SwitchGame(_startGame ?? Settings.Game);
        _tray = new Tray(this, Icon);

        Ipc.StartServer(msg => Dispatcher.UIThread.Post(() => OnSignal(msg)), _cts.Token);
        _platform.RegisterHotkeys(OnHotkey);
        _platformTimer.Start();
        _blinkTimer.Start();
        _hudHoverTimer.Start();

        if (Settings.FirstRun)
        {
            Settings.FirstRun = false;
            SaveSettings();
            DispatcherTimer.RunOnce(() =>
            {
                Fx.Popup(new Vec2(Arena.Left + Arena.Width / 2, Arena.Top + Arena.Height * 0.45), "Welcome to Desk Arcade",
                    Color.FromRgb(255, 209, 102), 34, 6, "click the scoreboard to pick a game · Ctrl+Alt+G show/hide · Ctrl+Alt+N next game");
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
        if (Current != null) busy |= Current.Update(dt);
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
        "hoops" => "drag the ball and flick it into the hoop",
        "archery" => "drag back from the bow, release to shoot",
        "juggle" => "click the ball to kick it — don't let it drop",
        "golf" => "drag back from the ball to putt it into the cup",
        "bugs" => "squash the bugs — spare the ladybugs",
        "cans" => "throw the ball from behind the line",
        "bricks" => "click the paddle to launch — the mouse steers it",
        "bubbles" => "click a bubble to split it — clear them all in time",
        "hockey" => "drag your mallet and score in the right-hand goal",
        _ => "",
    };

    public void SwitchGame(string id)
    {
        var next = _games.FirstOrDefault(g => g.Id == id) ?? _games[0];
        if (next == Current) return;
        _captured = false;
        Current?.Deactivate();
        _gameLayer.Children.Clear();
        Current = next;
        _gameLayer.Children.Add(next.Layer);
        next.Activate();

        Settings.Game = next.Id;
        SaveSettings();
        _hud.SetGame(next.Id, next.Title);
        HudChanged();
        _tray?.Refresh();
        if (IsVisible)
            Fx.Popup(new Vec2(Arena.Left + Arena.Width / 2, Arena.Top + Arena.Height * 0.28), next.Title,
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
        Platforms.Enabled = Settings.Platforms;
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
        string exe = OperatingSystem.IsLinux() && File.Exists("/usr/bin/deskarcade")
            ? "/usr/bin/deskarcade"
            : (Environment.ProcessPath ?? "DeskArcade").Replace('\\', '/');
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
            Notice("Hook config copied", "merge it into ~/.claude/settings.json", Color.FromRgb(255, 209, 102));
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
                _hud.SetClaude(ClaudeStatus.Working);
                break;
            case "done" or "stop":
                ClaudeAlert(ClaudeStatus.Done, "done", "Claude is done", Color.FromRgb(61, 220, 132), "your turn!");
                break;
            case "attention" or "notify":
                ClaudeAlert(ClaudeStatus.Attention, "attention", "Claude needs you", Color.FromRgb(255, 107, 107), "check the terminal");
                break;
            case "idle": _hud.SetClaude(ClaudeStatus.Unknown); break;
            case "show": SetOverlayVisible(true); break;
            case "hide": SetOverlayVisible(false); break;
            case "toggle": ToggleOverlay(); break;
            case "next": NextGame(); break;
            case "summon": SummonToCursor(); break;
            case "expand": _hud.Expand(); break;
            case "quit": Quit(); break;
        }
    }

    void ClaudeAlert(ClaudeStatus status, string sound, string title, Color color, string sub)
    {
        _hud.SetClaude(status);
        if (!Settings.ClaudeNotify) return;
        Sound.Play(sound, 0.9);
        Notice(title, sub, color);
    }

    public void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        SaveSettings();
        _cts.Cancel();
        _loopOn = false;
        _platformTimer.Stop();
        _blinkTimer.Stop();
        _hudHoverTimer.Stop();
        _demoTimer?.Stop();
        _tray?.Dispose();
        Sound.Dispose();
        _platform.Dispose();
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
        else
            Environment.Exit(0);
    }
}
