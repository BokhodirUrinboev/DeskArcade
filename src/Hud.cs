using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DeskArcade.Engine;

namespace DeskArcade;

public enum ClaudeStatus { Unknown, Working, Done, Attention }

/// <summary>A command run with "DeskArcade --while".</summary>
public enum TaskStatus { None, Running, Passed, Failed }

/// <summary>
/// Scoreboard. A compact pill (game icon, score, best, who you're playing and whose turn it is, a ☰ menu) until you
/// click it; then the full board with game tabs, which shrinks back shortly after the mouse leaves. Drag either form
/// to move it.
/// </summary>
public sealed class Hud : Border
{
    const double ExpandedWidth = 272;
    const double IdleOpacity = 0.93;

    readonly Dictionary<string, MiniGame> _games = new();
    readonly Dictionary<string, Border> _tabs = new();

    // full board
    readonly Grid _board = new() { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto") };
    readonly TextBlock _score = Text(30, FontWeight.Black, "#FFFFFF");
    readonly TextBlock _title = Text(11, FontWeight.Bold, "#9AA4B2");
    readonly TextBlock _best = Text(12, FontWeight.SemiBold, "#FFD166");
    readonly TextBlock _line = Text(12, FontWeight.Normal, "#C9D1DC");
    readonly Border _oppChip;
    readonly Ellipse _oppDot = new() { Width = 8, Height = 8 };
    readonly TextBlock _oppText = Text(11, FontWeight.SemiBold, "#FFFFFF");
    readonly Border _chip;
    readonly Ellipse _chipDot = new() { Width = 8, Height = 8 };
    readonly TextBlock _chipText = Text(11, FontWeight.SemiBold, "#FFFFFF");
    readonly Border _taskChip;
    readonly Ellipse _taskDot = new() { Width = 8, Height = 8 };
    readonly TextBlock _taskText = Text(11, FontWeight.SemiBold, "#FFFFFF");

    // compact pill
    readonly StackPanel _pill = new() { Orientation = Orientation.Horizontal };
    readonly Canvas _pillIcon = new() { Width = 24, Height = 24, IsHitTestVisible = false };
    readonly TextBlock _pillScore = Text(18, FontWeight.Black, "#FFFFFF");
    readonly TextBlock _pillBest = Text(11, FontWeight.SemiBold, "#FFD166");
    readonly Border _pillOpp;
    readonly Ellipse _pillOppDot = new() { Width = 7, Height = 7 };
    readonly TextBlock _pillOppText = Text(11, FontWeight.SemiBold, "#FFFFFF");
    readonly Ellipse _pillDot = new() { Width = 8, Height = 8, IsVisible = false };
    readonly Ellipse _pillTaskDot = new() { Width = 8, Height = 8, IsVisible = false };
    readonly TextBlock _menuButton = Text(15, FontWeight.Bold, "#C9D1DC");
    readonly ScaleTransform _scoreScale = new(), _oppScale = new();
    readonly Anims _anims = new();

    readonly TranslateTransform _pos = new();
    readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };

    ClaudeStatus _status;
    bool _blinkOn;
    int _flashes;
    DateTime? _claudeSince;
    TimeSpan? _waited;
    TaskStatus _task;
    string _taskLabel = "";
    DateTime? _taskSince;
    TimeSpan? _taskTook;
    int _taskCode, _taskFlashes;
    bool _expanded, _pressed, _dragging, _menuOpen, _shownOnce;
    Vec2 _pressAt, _dragOffset;
    Opponent? _opponent;
    string _lastScore = "";

    public event Action<string>? GameClicked;
    /// <summary>A press started on the scoreboard, or its menu opened (the overlay must keep taking the mouse until it ends).</summary>
    public event Action? InteractionStarted;
    /// <summary>The scoreboard's menu closed: the overlay can go back to its usual hit shapes.</summary>
    public event Action? InteractionEnded;
    /// <summary>A scoreboard animation started: the overlay should run frames until <see cref="Update"/> says it is over.</summary>
    public event Action? Animating;
    /// <summary>The scoreboard was dragged to a new place.</summary>
    public event Action? Moved;

    /// <summary>Builds the ☰ menu's items each time it opens.</summary>
    public Func<IEnumerable<Control>>? MenuItems { get; set; }

    public Hud(IEnumerable<MiniGame> games)
    {
        Background = Art.Brush(240, 18, 20, 28);
        BorderBrush = Art.Brush(70, 255, 255, 255);
        BorderThickness = new Thickness(1);
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        RenderTransformOrigin = RelativePoint.TopLeft;
        RenderTransform = _pos;
        Opacity = IdleOpacity;

        // --- full board: tabs / score + title + best / context line / opponent chip / claude chip / task chip
        var tabs = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var game in games)
        {
            _games[game.Id] = game;
            var tab = new Border
            {
                Width = 34, Height = 28, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 4, 4),
                Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand), Child = IconHost(game, 34, 28),
            };
            ToolTip.SetTip(tab, L.T(game.Title));
            string id = game.Id;
            tab.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                GameClicked?.Invoke(id);
            };
            _tabs[id] = tab;
            tabs.Children.Add(tab);
        }
        var menuTab = new Border
        {
            Width = 34, Height = 28, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 4, 4),
            Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand),
            Child = new TextBlock
            {
                Text = "☰", FontFamily = Fx.Font, FontSize = 15, FontWeight = FontWeight.Bold, Foreground = Art.Brush("#C9D1DC"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
            },
        };
        ToolTip.SetTip(menuTab, L.T("Menu"));
        menuTab.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            OpenMenu();
        };
        tabs.Children.Add(menuTab);
        _board.Children.Add(tabs);

        var mid = new DockPanel { Margin = new Thickness(2, 0, 0, 0) };
        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = ExpandedWidth - 120 };
        _title.HorizontalAlignment = HorizontalAlignment.Right;
        _title.TextTrimming = TextTrimming.CharacterEllipsis;
        _best.HorizontalAlignment = HorizontalAlignment.Right;
        _best.TextTrimming = TextTrimming.CharacterEllipsis; // long lines ("Best 17 darts · …") must not squeeze the score out
        right.Children.Add(_title);
        right.Children.Add(_best);
        DockPanel.SetDock(right, Dock.Right);
        mid.Children.Add(right);
        mid.Children.Add(_score);
        Grid.SetRow(mid, 1);
        _board.Children.Add(mid);

        _line.Margin = new Thickness(2, 0, 0, 0);
        _line.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetRow(_line, 2);
        _board.Children.Add(_line);

        _oppText.TextTrimming = TextTrimming.CharacterEllipsis;
        _oppText.MaxWidth = ExpandedWidth - 60;
        _oppChip = Chip(_oppDot, _oppText);
        Grid.SetRow(_oppChip, 3);
        _board.Children.Add(_oppChip);

        _chip = Chip(_chipDot, _chipText);
        Grid.SetRow(_chip, 4);
        _board.Children.Add(_chip);

        _taskText.TextTrimming = TextTrimming.CharacterEllipsis;
        _taskChip = Chip(_taskDot, _taskText);
        Grid.SetRow(_taskChip, 5);
        _board.Children.Add(_taskChip);
        _board.Transitions = new Transitions
        {
            new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(140) },
        };

        // --- compact pill: icon, score, best, opponent / turn, claude dot, task dot, menu
        _pillIcon.VerticalAlignment = VerticalAlignment.Center;
        _pillScore.Margin = new Thickness(6, 0, 0, 1);
        _pillScore.VerticalAlignment = VerticalAlignment.Center;
        _pillScore.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        _pillScore.RenderTransform = _scoreScale;
        _pillBest.Margin = new Thickness(10, 1, 0, 0);
        _pillBest.VerticalAlignment = VerticalAlignment.Center;
        _pillOppDot.Margin = new Thickness(0, 0, 4, 0);
        _pillOppDot.VerticalAlignment = VerticalAlignment.Center;
        _pillOpp = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 1, 7, 2), Margin = new Thickness(9, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, IsVisible = false, // hit-testable, so its tooltip can show the whole line
            RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative), RenderTransform = _oppScale,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _pillOppDot, _pillOppText } },
        };
        _pillDot.Margin = new Thickness(10, 0, 0, 0);
        _pillDot.VerticalAlignment = VerticalAlignment.Center;
        _pillTaskDot.Margin = new Thickness(6, 0, 0, 0);
        _pillTaskDot.VerticalAlignment = VerticalAlignment.Center;
        _menuButton.Text = "☰";
        _menuButton.Margin = new Thickness(9, 0, 0, 1);
        _menuButton.Padding = new Thickness(2, 0);
        _menuButton.VerticalAlignment = VerticalAlignment.Center;
        _menuButton.Background = Brushes.Transparent; // takes the press itself, so it opens the menu instead of the board
        _menuButton.Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(_menuButton, L.T("Menu"));
        _menuButton.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            OpenMenu();
        };
        _pill.Children.Add(_pillIcon);
        _pill.Children.Add(_pillScore);
        _pill.Children.Add(_pillBest);
        _pill.Children.Add(_pillOpp);
        _pill.Children.Add(_pillDot);
        _pill.Children.Add(_pillTaskDot);
        _pill.Children.Add(_menuButton);

        Child = new Panel { Children = { _pill, _board } };
        ApplyState();
        SetClaude(ClaudeStatus.Unknown);

        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            Collapse();
        };
        PointerEntered += (_, _) =>
        {
            _collapseTimer.Stop();
            Opacity = 1;
        };
        PointerExited += (_, _) => PointerLeft();
        PointerPressed += OnDown;
        PointerMoved += OnMove;
        PointerReleased += OnUp;
        PointerCaptureLost += (_, _) => EndPress();
    }

    static Border Chip(Ellipse dot, TextBlock text)
    {
        dot.Margin = new Thickness(0, 0, 5, 0);
        dot.VerticalAlignment = VerticalAlignment.Center;
        return new Border
        {
            CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 2, 8, 3), Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left, IsVisible = false,
            Child = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { dot, text } },
        };
    }

    public Vec2 Position
    {
        get => new(_pos.X, _pos.Y);
        set
        {
            _pos.X = Math.Round(value.X);
            _pos.Y = Math.Round(value.Y);
        }
    }

    /// <summary>Where the scoreboard sits in overlay coordinates.</summary>
    public Rect Area => new(_pos.X, _pos.Y,
        Bounds.Width > 0 ? Bounds.Width : _expanded ? ExpandedWidth : 150,
        Bounds.Height > 0 ? Bounds.Height : _expanded ? 120 : 34);

    public bool IsExpanded => _expanded;

    /// <summary>True while a press or drag on the scoreboard is in progress, or its menu is open.</summary>
    public bool IsInteracting => _pressed || _menuOpen;

    public ClaudeStatus Status => _status;

    public TaskStatus Task => _task;

    // ------------------------------------------------------------------ content

    public void SetGame(string id, string title)
    {
        foreach (var (key, tab) in _tabs)
            tab.Background = key == id ? Art.Brush(70, 255, 255, 255) : Brushes.Transparent;
        _title.Text = L.T(title).ToUpperInvariant();
        foreach (var (key, tab) in _tabs) ToolTip.SetTip(tab, L.T(_games[key].Title));
        UpdateClaudeText();
        _shownOnce = false; // a new game's first score is not a change worth a bump

        _pillIcon.Children.Clear();
        if (_games.TryGetValue(id, out var game))
        {
            var icon = game.CreateIcon();
            icon.Set(new Vec2(12, 12));
            _pillIcon.Children.Add(icon);
        }
    }

    /// <param name="opponent">Who the player is up against and whose turn it is; null in a solo game.</param>
    public void Show(HudInfo info, Opponent? opponent = null)
    {
        bool scored = _shownOnce && info.Score != _lastScore;
        _shownOnce = true;
        _lastScore = info.Score;
        _score.Text = info.Score;
        _pillScore.Text = info.Score;
        _line.Text = info.Line;
        _best.Text = info.Best;
        // the pill has room for the number only: "Best 17 darts" shows as 17, "Best —" as —
        var words = info.Best.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string shown = words.Length > 0 ? words[^1] : "";
        for (int i = words.Length - 1; i >= 0; i--)
        {
            if (!words[i].Any(char.IsDigit)) continue;
            shown = words[i];
            break;
        }
        _pillBest.Text = "★ " + shown;
        ToolTip.SetTip(_best, info.Best);
        ToolTip.SetTip(_line, info.Line);
        ToolTip.SetTip(_pillBest, info.Best);
        if (scored) Bump(_scoreScale, 0.3);
        SetOpponent(opponent);
    }

    /// <summary>The chip after the score: "CPU · Hard", "vs Alice", or whose turn it is when the game has turns.</summary>
    void SetOpponent(Opponent? opp)
    {
        bool myTurnNow = opp?.MyTurn == true && (_opponent?.MyTurn != true || _opponent.Name != opp.Name);
        _opponent = opp;
        _oppChip.IsVisible = _pillOpp.IsVisible = opp != null;
        if (opp == null) return;

        string name = opp.IsCpu ? L.T("CPU") : Short(opp.Name, 12);
        string level = opp.IsCpu && opp.Level > 0 ? L.T(MiniGame.LevelNames[opp.Level - 1]) : "";
        string who = level.Length > 0 ? L.F("CPU · {0}", level) : name;
        string turn = opp.MyTurn switch { true => L.T("Your turn"), false => L.F("{0}'s turn", name), _ => "" };
        // the board line names the other side once: "vs CPU · Hard · Your turn", "CPU's turn · Hard", "vs Alice"
        _oppText.Text = opp.MyTurn switch
        {
            true => L.F("vs {0}", who) + " · " + turn,
            false => level.Length > 0 ? turn + " · " + level : turn,
            _ => L.F("vs {0}", who),
        };
        _pillOppText.Text = turn.Length > 0 ? turn : who;
        ToolTip.SetTip(_pillOpp, opp.MyTurn == null ? _oppText.Text : L.F("vs {0}", who) + " · " + turn);
        ToolTip.SetTip(_oppChip, _oppText.Text);

        (string dot, string bg) = opp.MyTurn switch
        {
            true => ("#3DDC84", "#113A24"),
            false => ("#FFB020", "#3A2E12"),
            _ => opp.IsCpu ? ("#4DA3FF", "#1C2A40") : ("#FF5C6C", "#3A1C22"),
        };
        var fill = Art.Brush(Art.Safe(Color.Parse(dot)));
        _oppDot.Fill = _pillOppDot.Fill = fill;
        _oppChip.Background = _pillOpp.Background = Art.Brush(bg);
        _oppDot.Opacity = _pillOppDot.Opacity = 1;
        if (myTurnNow) Bump(_oppScale, 0.25);
    }

    static string Short(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>A quick pop of the score or the turn chip: a bit bigger, then back, in a third of a second.</summary>
    void Bump(ScaleTransform scale, double amount)
    {
        if (Fx.ReducedMotion) return;
        _anims.Add(0.36, k => scale.ScaleX = scale.ScaleY = 1 + amount * (1 - k), Ease.OutCubic, () => scale.ScaleX = scale.ScaleY = 1);
        Animating?.Invoke();
    }

    /// <summary>Advances the scoreboard's own animations; true while one runs.</summary>
    public bool Update(double dt) => _anims.Update(dt);

    /// <param name="since">When Claude started working (shows a running timer).</param>
    /// <param name="waited">How long the finished task took (shown on "done").</param>
    public void SetClaude(ClaudeStatus status, DateTime? since = null, TimeSpan? waited = null)
    {
        _status = status;
        _claudeSince = since;
        _waited = waited;
        _flashes = status is ClaudeStatus.Done or ClaudeStatus.Attention && !Fx.ReducedMotion ? 8 : 0;
        (string dot, string bg) = status switch
        {
            ClaudeStatus.Working => ("#FFB020", "#3A2E12"),
            ClaudeStatus.Done => ("#3DDC84", "#113A24"),
            ClaudeStatus.Attention => ("#FF5C5C", "#3F1616"),
            _ => ("#888888", "#222222"),
        };
        _chip.IsVisible = _pillDot.IsVisible = status != ClaudeStatus.Unknown;
        _chipDot.Fill = _pillDot.Fill = Art.Brush(Art.Safe(Color.Parse(dot)));
        _chip.Background = Art.Brush(bg);
        _chip.Opacity = _pillDot.Opacity = 1;
        UpdateClaudeText();
    }

    void UpdateClaudeText()
    {
        string text = _status switch
        {
            ClaudeStatus.Working when _claudeSince is DateTime since => L.F("Claude working · {0}", FormatWait(DateTime.UtcNow - since)),
            ClaudeStatus.Working => L.T("Claude working"),
            ClaudeStatus.Done when _waited is TimeSpan w && w.TotalSeconds >= 5 => L.F("Claude done after {0}", FormatWait(w)),
            ClaudeStatus.Done => L.T("Claude done"),
            ClaudeStatus.Attention => L.T("Claude needs you"),
            _ => "",
        };
        _chipText.Text = text;
        ToolTip.SetTip(_pillDot, text);
    }

    /// <param name="label">The command, as the scoreboard shows it.</param>
    /// <param name="since">When it started (shows a running timer).</param>
    /// <param name="took">How long it ran (shown once it has ended).</param>
    /// <param name="exitCode">How it ended, shown when it failed.</param>
    public void SetTask(TaskStatus status, string label = "", DateTime? since = null, TimeSpan? took = null, int exitCode = 0)
    {
        _task = status;
        _taskLabel = label;
        _taskSince = since;
        _taskTook = took;
        _taskCode = exitCode;
        _taskFlashes = status is TaskStatus.Passed or TaskStatus.Failed && !Fx.ReducedMotion ? 8 : 0;
        (string dot, string bg) = status switch
        {
            TaskStatus.Running => ("#4DA3FF", "#132A45"),
            TaskStatus.Passed => ("#3DDC84", "#113A24"),
            TaskStatus.Failed => ("#FF5C5C", "#3F1616"),
            _ => ("#888888", "#222222"),
        };
        _taskChip.IsVisible = _pillTaskDot.IsVisible = status != TaskStatus.None;
        _taskDot.Fill = _pillTaskDot.Fill = Art.Brush(Art.Safe(Color.Parse(dot)));
        _taskChip.Background = Art.Brush(bg);
        _taskChip.Opacity = _pillTaskDot.Opacity = 1;
        UpdateTaskText();
    }

    void UpdateTaskText()
    {
        string text = _task switch
        {
            TaskStatus.Running when _taskSince is DateTime since => $"{_taskLabel} · {FormatWait(DateTime.UtcNow - since)}",
            TaskStatus.Passed when _taskTook is TimeSpan t => L.F("{0} passed · {1}", _taskLabel, FormatWait(t)),
            TaskStatus.Passed => L.F("{0} passed", _taskLabel),
            TaskStatus.Failed => L.F("{0} failed · exit {1}", _taskLabel, _taskCode),
            _ => _taskLabel,
        };
        _taskText.Text = text;
        _taskText.MaxWidth = ExpandedWidth - 50;
        ToolTip.SetTip(_pillTaskDot, text);
    }

    /// <summary>"4:05" or "1:02:03".</summary>
    public static string FormatWait(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    /// <summary>Called on a slow timer: pulses the status without needing a render loop.</summary>
    public void Blink()
    {
        _blinkOn = !_blinkOn;
        if (_status == ClaudeStatus.Working)
        {
            _chipDot.Opacity = _pillDot.Opacity = _blinkOn ? 1 : 0.35;
            if (_claudeSince != null) UpdateClaudeText();
        }
        else if (_flashes > 0)
        {
            _flashes--;
            _chip.Opacity = _pillDot.Opacity = _blinkOn ? 1 : 0.3;
        }
        else
        {
            _chipDot.Opacity = _chip.Opacity = _pillDot.Opacity = 1;
        }

        if (_task == TaskStatus.Running)
        {
            _taskDot.Opacity = _pillTaskDot.Opacity = _blinkOn ? 1 : 0.35;
            UpdateTaskText();
        }
        else if (_taskFlashes > 0)
        {
            _taskFlashes--;
            _taskChip.Opacity = _pillTaskDot.Opacity = _blinkOn ? 1 : 0.3;
        }
        else
        {
            _taskDot.Opacity = _taskChip.Opacity = _pillTaskDot.Opacity = 1;
        }

        // waiting on the other side: the turn dot breathes
        _oppDot.Opacity = _pillOppDot.Opacity = _opponent?.MyTurn == false && !_blinkOn && !Fx.ReducedMotion ? 0.3 : 1;
    }

    // ------------------------------------------------------------------ menu

    /// <summary>Opens the ☰ menu under the scoreboard (also reachable from the tray, where there is one).</summary>
    public void OpenMenu()
    {
        if (_menuOpen || MenuItems == null) return;
        var flyout = new MenuFlyout { Placement = PlacementMode.Bottom };
        foreach (var item in MenuItems()) flyout.Items.Add(item);
        flyout.Opened += (_, _) =>
        {
            _menuOpen = true;
            _collapseTimer.Stop();
            Opacity = 1;
            InteractionStarted?.Invoke();
        };
        flyout.Closed += (_, _) =>
        {
            _menuOpen = false;
            InteractionEnded?.Invoke();
            if (!IsPointerOver) PointerLeft();
        };
        flyout.ShowAt(this);
    }

    // ------------------------------------------------------------------ expand / collapse

    public void Expand()
    {
        _collapseTimer.Stop();
        if (_expanded) return;
        _expanded = true;
        _board.Opacity = 0;
        ApplyState();
        Dispatcher.UIThread.Post(() => _board.Opacity = 1);
    }

    public void Collapse()
    {
        if (!_expanded || _pressed || _menuOpen) return;
        _expanded = false;
        ApplyState();
    }

    /// <summary>The mouse is no longer over the scoreboard: shrink it back after a short delay.</summary>
    public void PointerLeft()
    {
        if (_pressed || _menuOpen) return;
        Opacity = IdleOpacity;
        if (_expanded && !_collapseTimer.IsEnabled) _collapseTimer.Start();
    }

    void ApplyState()
    {
        _board.IsVisible = _expanded;
        _pill.IsVisible = !_expanded;
        Width = _expanded ? ExpandedWidth : double.NaN;
        CornerRadius = new CornerRadius(_expanded ? 14 : 16);
        Padding = _expanded ? new Thickness(10, 8, 12, 9) : new Thickness(6, 4, 10, 4);
        Cursor = new Cursor(_expanded ? StandardCursorType.SizeAll : StandardCursorType.Hand);
    }

    // ------------------------------------------------------------------ press, click, drag

    void OnDown(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || Parent is not Visual parent || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _pressed = true;
        _dragging = false;
        _pressAt = e.GetPosition(parent);
        _dragOffset = Position - _pressAt;
        _collapseTimer.Stop();
        e.Pointer.Capture(this);
        e.Handled = true;
        InteractionStarted?.Invoke();
    }

    void OnMove(object? sender, PointerEventArgs e)
    {
        if (!_pressed || Parent is not Visual parent) return;
        Vec2 p = e.GetPosition(parent);
        if (!_dragging && (p - _pressAt).Length > 4) _dragging = true;
        if (_dragging) Position = p + _dragOffset;
    }

    void OnUp(object? sender, PointerReleasedEventArgs e)
    {
        if (!_pressed) return;
        e.Handled = true;
        bool click = !_dragging;
        e.Pointer.Capture(null);
        EndPress();
        if (click) Expand();
    }

    void EndPress()
    {
        if (!_pressed) return;
        bool dragged = _dragging;
        _pressed = _dragging = false;
        if (dragged) Moved?.Invoke();
        if (!IsPointerOver) PointerLeft();
    }

    static Canvas IconHost(MiniGame game, double w, double h)
    {
        var host = new Canvas { Width = w, Height = h, IsHitTestVisible = false };
        var icon = game.CreateIcon();
        icon.Set(new Vec2(w / 2, h / 2));
        host.Children.Add(icon);
        return host;
    }

    static TextBlock Text(double size, FontWeight weight, string color) => new()
    {
        FontFamily = Fx.Font, FontSize = size, FontWeight = weight, Foreground = Art.Brush(color),
    };
}
