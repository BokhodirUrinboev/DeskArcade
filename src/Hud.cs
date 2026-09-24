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
/// Scoreboard. A compact pill (game icon, score, best) until you click it; then the full board with
/// game tabs, which shrinks back shortly after the mouse leaves. Drag either form to move it.
/// </summary>
public sealed class Hud : Border
{
    const double ExpandedWidth = 272;
    const double IdleOpacity = 0.93;

    readonly Dictionary<string, MiniGame> _games = new();
    readonly Dictionary<string, Border> _tabs = new();

    // full board
    readonly Grid _board = new() { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto") };
    readonly TextBlock _score = Text(30, FontWeight.Black, "#FFFFFF");
    readonly TextBlock _title = Text(11, FontWeight.Bold, "#9AA4B2");
    readonly TextBlock _best = Text(12, FontWeight.SemiBold, "#FFD166");
    readonly TextBlock _line = Text(12, FontWeight.Normal, "#C9D1DC");
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
    readonly Ellipse _pillDot = new() { Width = 8, Height = 8, IsVisible = false };
    readonly Ellipse _pillTaskDot = new() { Width = 8, Height = 8, IsVisible = false };

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
    bool _expanded, _pressed, _dragging;
    Vec2 _pressAt, _dragOffset;

    public event Action<string>? GameClicked;
    /// <summary>A press started on the scoreboard (the overlay must keep taking the mouse until it ends).</summary>
    public event Action? InteractionStarted;
    /// <summary>The scoreboard was dragged to a new place.</summary>
    public event Action? Moved;

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

        // --- full board: tabs / score + title + best / context line / claude chip
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
        _board.Children.Add(tabs);

        var mid = new DockPanel { Margin = new Thickness(2, 0, 0, 0) };
        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        _title.HorizontalAlignment = HorizontalAlignment.Right;
        _best.HorizontalAlignment = HorizontalAlignment.Right;
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

        var chipRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _chipDot.Margin = new Thickness(0, 0, 5, 0);
        _chipDot.VerticalAlignment = VerticalAlignment.Center;
        chipRow.Children.Add(_chipDot);
        chipRow.Children.Add(_chipText);
        _chip = new Border
        {
            CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 2, 8, 3), Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left, Child = chipRow, IsVisible = false,
        };
        Grid.SetRow(_chip, 3);
        _board.Children.Add(_chip);

        _taskDot.Margin = new Thickness(0, 0, 5, 0);
        _taskDot.VerticalAlignment = VerticalAlignment.Center;
        _taskText.TextTrimming = TextTrimming.CharacterEllipsis;
        _taskChip = new Border
        {
            CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 2, 8, 3), Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left, IsVisible = false,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _taskDot, _taskText } },
        };
        Grid.SetRow(_taskChip, 4);
        _board.Children.Add(_taskChip);
        _board.Transitions = new Transitions
        {
            new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(140) },
        };

        // --- compact pill: icon, score, best, claude dot
        _pillIcon.VerticalAlignment = VerticalAlignment.Center;
        _pillScore.Margin = new Thickness(6, 0, 0, 1);
        _pillScore.VerticalAlignment = VerticalAlignment.Center;
        _pillBest.Margin = new Thickness(10, 1, 0, 0);
        _pillBest.VerticalAlignment = VerticalAlignment.Center;
        _pillDot.Margin = new Thickness(10, 0, 0, 0);
        _pillDot.VerticalAlignment = VerticalAlignment.Center;
        _pill.Children.Add(_pillIcon);
        _pill.Children.Add(_pillScore);
        _pill.Children.Add(_pillBest);
        _pill.Children.Add(_pillDot);
        _pillTaskDot.Margin = new Thickness(6, 0, 0, 0);
        _pillTaskDot.VerticalAlignment = VerticalAlignment.Center;
        _pill.Children.Add(_pillTaskDot);

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

    /// <summary>True while a press or drag on the scoreboard is in progress.</summary>
    public bool IsInteracting => _pressed;

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

        _pillIcon.Children.Clear();
        if (_games.TryGetValue(id, out var game))
        {
            var icon = game.CreateIcon();
            icon.Set(new Vec2(12, 12));
            _pillIcon.Children.Add(icon);
        }
    }

    public void Show(HudInfo info)
    {
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
    }

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
        if (!_expanded || _pressed) return;
        _expanded = false;
        ApplyState();
    }

    /// <summary>The mouse is no longer over the scoreboard: shrink it back after a short delay.</summary>
    public void PointerLeft()
    {
        if (_pressed) return;
        Opacity = IdleOpacity;
        if (_expanded && !_collapseTimer.IsEnabled) _collapseTimer.Start();
    }

    void ApplyState()
    {
        _board.IsVisible = _expanded;
        _pill.IsVisible = !_expanded;
        Width = _expanded ? ExpandedWidth : double.NaN;
        CornerRadius = new CornerRadius(_expanded ? 14 : 16);
        Padding = _expanded ? new Thickness(10, 8, 12, 9) : new Thickness(6, 4, 12, 4);
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
