using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DeskArcade.Dev;
using DeskArcade.Engine;

namespace DeskArcade;

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
    readonly Grid _board = new() { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto") };
    readonly TextBlock _score = Text(30, FontWeight.Black, "#FFFFFF");
    readonly TextBlock _title = Text(11, FontWeight.Bold, "#9AA4B2");
    readonly TextBlock _best = Text(12, FontWeight.SemiBold, "#FFD166");
    readonly TextBlock _line = Text(12, FontWeight.Normal, "#C9D1DC");
    readonly Border _oppChip;
    readonly Ellipse _oppDot = new() { Width = 8, Height = 8 };
    readonly TextBlock _oppText = Text(11, FontWeight.SemiBold, "#FFFFFF");
    readonly StackPanel _agentPanel = new() { IsVisible = false }; // a line per coding agent session
    readonly StackPanel _lanePanel = new() { IsVisible = false };  // status lanes and CI, and the reviews waiting
    readonly List<DevChip> _agentChips = new(), _laneChips = new();
    readonly Border _taskChip;
    readonly Ellipse _taskDot = new() { Width = 8, Height = 8 };
    readonly TextBlock _taskText = Text(11, FontWeight.SemiBold, "#FFFFFF");
    readonly Border _officeChip;
    readonly Ellipse _officeDot = new() { Width = 8, Height = 8 };
    readonly TextBlock _officeText = Text(11, FontWeight.SemiBold, "#FFFFFF");

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
    readonly Ellipse _pillLaneDot = new() { Width = 8, Height = 8, IsVisible = false };
    readonly Border _pillOffice;
    readonly Ellipse _pillOfficeDot = new() { Width = 7, Height = 7 };
    readonly TextBlock _pillOfficeText = Text(11, FontWeight.SemiBold, "#FFFFFF");
    readonly TextBlock _menuButton = Text(15, FontWeight.Bold, "#C9D1DC");
    readonly TextBlock _menuTabText = Text(15, FontWeight.Bold, "#C9D1DC");
    readonly ScaleTransform _scoreScale = new(), _oppScale = new();
    readonly Anims _anims = new();

    readonly TranslateTransform _pos = new();
    readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };

    bool _blinkOn;
    AgentSessions? _sessions;
    StatusLanes? _lanes;
    IReadOnlyList<string> _reviews = Array.Empty<string>();
    DevLook? _agentLook, _laneLook;
    string _flashKey = "";
    int _devFlashes;
    TaskStatus _task;
    string _taskLabel = "";
    DateTime? _taskSince;
    TimeSpan? _taskTook;
    int _taskCode, _taskFlashes;
    bool _officeUrgent;
    bool _expanded, _pressed, _dragging, _menuOpen, _shownOnce;
    Vec2 _pressAt, _dragOffset;
    Opponent? _opponent;
    string _lastScore = "", _currentId = "";

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
        BorderThickness = new Thickness(1);
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        RenderTransformOrigin = RelativePoint.TopLeft;
        RenderTransform = _pos;
        Opacity = IdleOpacity;

        // --- full board: tabs / score + title + best / context line / opponent chip / agent sessions / task chip /
        //     lanes, CI and reviews / at-work chip
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
        _menuTabText.HorizontalAlignment = HorizontalAlignment.Center;
        _menuTabText.VerticalAlignment = VerticalAlignment.Center;
        _menuTabText.IsHitTestVisible = false;
        var menuTab = new Border
        {
            Width = 34, Height = 28, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 4, 4),
            Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand), Child = _menuTabText,
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

        Grid.SetRow(_agentPanel, 4);
        _board.Children.Add(_agentPanel);

        _taskText.TextTrimming = TextTrimming.CharacterEllipsis;
        _taskChip = Chip(_taskDot, _taskText);
        Grid.SetRow(_taskChip, 5);
        _board.Children.Add(_taskChip);

        Grid.SetRow(_lanePanel, 6);
        _board.Children.Add(_lanePanel);

        _officeText.TextTrimming = TextTrimming.CharacterEllipsis;
        _officeText.MaxWidth = ExpandedWidth - 50;
        _officeChip = Chip(_officeDot, _officeText);
        Grid.SetRow(_officeChip, 7);
        _board.Children.Add(_officeChip);
        _board.Transitions = new Transitions
        {
            new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(140) },
        };

        // --- compact pill: icon, score, best, opponent / turn, agents' dot, task dot, lanes' dot, at-work chip, menu
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
        _pillLaneDot.Margin = new Thickness(6, 0, 0, 0);
        _pillLaneDot.VerticalAlignment = VerticalAlignment.Center;
        _pillOfficeDot.Margin = new Thickness(0, 0, 4, 0);
        _pillOfficeDot.VerticalAlignment = VerticalAlignment.Center;
        _pillOffice = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 1, 7, 2), Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, IsVisible = false,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _pillOfficeDot, _pillOfficeText } },
        };
        _menuButton.Text = "☰";
        _menuTabText.Text = "☰";
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
        _pill.Children.Add(_pillLaneDot);
        _pill.Children.Add(_pillOffice);
        _pill.Children.Add(_menuButton);

        Child = new Panel { Children = { _pill, _board } };
        ApplyState();
        ThemeChanged();

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

    /// <summary>True while a press or drag on the scoreboard is in progress, or its menu is open (the overlay then takes the whole mouse).</summary>
    public bool IsInteracting => _pressed || _menuOpen;

    /// <summary>True only while a press or drag is in progress: an open menu needs the mouse, not frames.</summary>
    public bool IsPressed => _pressed;

    public TaskStatus Task => _task;

    // ------------------------------------------------------------------ content

    public void SetGame(string id, string title)
    {
        _currentId = id;
        MarkTab();
        _title.Text = L.T(title).ToUpperInvariant();
        foreach (var (key, tab) in _tabs) ToolTip.SetTip(tab, L.T(_games[key].Title));
        RenderDev();
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
        if (opp == _opponent) return; // records compare by value: nothing to rebuild
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
            _ => opp.Teammate ? L.F("with {0}", who) : L.F("vs {0}", who),
        };
        if (opp.Record is { Length: > 0 } record) _oppText.Text += " · " + record; // the rivalry so far, on the board only
        _pillOppText.Text = turn.Length > 0 ? turn : who;
        ToolTip.SetTip(_pillOpp, opp.MyTurn == null ? _oppText.Text : L.F("vs {0}", who) + " · " + turn);
        ToolTip.SetTip(_oppChip, _oppText.Text);

        // turns keep their traffic-light colours; a rival without turns takes the theme's accent (the CPU) or rival colour
        var theme = Themes.Current;
        (Color dot, Color bg) = opp.MyTurn switch
        {
            true => (Color.Parse("#3DDC84"), Color.Parse("#113A24")),
            false => (Color.Parse("#FFB020"), Color.Parse("#3A2E12")),
            _ => opp.IsCpu ? (theme.Accent, Art.Blend(theme.Accent, theme.HudBack, 0.72)) : (theme.Rival, Art.Blend(theme.Rival, theme.HudBack, 0.72)),
        };
        var fill = Art.Brush(Art.Safe(dot));
        _oppDot.Fill = _pillOppDot.Fill = fill;
        _oppChip.Background = _pillOpp.Background = Art.Brush(bg);
        _oppDot.Opacity = _pillOppDot.Opacity = 1;
        if (myTurnNow) Bump(_oppScale, 0.25);
    }

    static string Short(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>The current game's tab lit in the theme's accent.</summary>
    void MarkTab()
    {
        var accent = Themes.Current.Accent;
        foreach (var (key, tab) in _tabs)
            tab.Background = key == _currentId ? Art.Brush(Color.FromArgb(95, accent.R, accent.G, accent.B)) : Brushes.Transparent;
    }

    /// <summary>
    /// Dresses the scoreboard in <see cref="Themes.Current"/>: its background and text, the accent border and tab,
    /// the gold of the best score and the rival chip. Everything else about it stays as it is.
    /// </summary>
    public void ThemeChanged()
    {
        var t = Themes.Current;
        Background = Art.Brush(Color.FromArgb(240, t.HudBack.R, t.HudBack.G, t.HudBack.B));
        BorderBrush = Art.Brush(Color.FromArgb(120, t.Accent.R, t.Accent.G, t.Accent.B));
        var front = Art.Brush(t.HudFront);
        var soft = Art.Brush(Art.Blend(t.HudFront, t.HudBack, 0.2));
        var dim = Art.Brush(Art.Blend(t.HudFront, t.HudBack, 0.42));
        var gold = Art.Brush(t.Gold);
        _score.Foreground = _pillScore.Foreground = front;
        _best.Foreground = _pillBest.Foreground = gold;
        _line.Foreground = soft;
        _title.Foreground = dim;
        _menuButton.Foreground = _menuTabText.Foreground = soft;
        MarkTab();
        SetOpponent(_opponent);
    }

    /// <summary>A quick pop of the score or the turn chip: a bit bigger, then back, in a third of a second.</summary>
    void Bump(ScaleTransform scale, double amount)
    {
        if (Fx.ReducedMotion) return;
        _anims.Add(0.36, k => scale.ScaleX = scale.ScaleY = 1 + amount * (1 - k), Ease.OutCubic, () => scale.ScaleX = scale.ScaleY = 1);
        Animating?.Invoke();
    }

    /// <summary>Advances the scoreboard's own animations; true while one runs.</summary>
    public bool Update(double dt) => _anims.Update(dt);

    // ------------------------------------------------------------------ coding agents, status lanes, CI

    /// <summary>One line of the agents' or the lanes' group: a chip with a dot and its text, and the step under it.</summary>
    sealed class DevChip
    {
        public readonly Ellipse Dot = new() { Width = 8, Height = 8 };
        public readonly TextBlock Label = Text(11, FontWeight.SemiBold, "#FFFFFF");
        public readonly TextBlock Step = Text(10, FontWeight.Normal, "#AEB6C2");
        public readonly Border Chip;
        public readonly StackPanel Host = new();
        public DevLook? Look;
        public string Key = "";

        public DevChip()
        {
            Label.TextTrimming = TextTrimming.CharacterEllipsis;
            Label.MaxWidth = ExpandedWidth - 50;
            Chip = Hud.Chip(Dot, Label);
            Chip.Margin = new Thickness(0, 4, 0, 0);
            Chip.IsVisible = true;
            Step.TextTrimming = TextTrimming.CharacterEllipsis;
            Step.MaxWidth = ExpandedWidth - 44;
            Step.Margin = new Thickness(20, 1, 0, 0);
            Host.Children.Add(Chip);
            Host.Children.Add(Step);
        }
    }

    /// <summary>The most agent lines the board shows; more fold into "+N more".</summary>
    const int MaxAgentLines = 5;

    /// <summary>
    /// The coding agents' sessions (a line each, with the step under it), the status lanes and CI (a line each, or one
    /// folded chip past three), and the reviews waiting for you. The pill has one dot for the agents and one for the
    /// lanes, in the most urgent state, with every line in their tooltips. <paramref name="flashKey"/> (a session's key,
    /// or "lane:" and a lane's name) flashes the line that just finished.
    /// </summary>
    public void SetDev(AgentSessions sessions, StatusLanes lanes, IReadOnlyList<string> reviews, string? flashKey = null)
    {
        _sessions = sessions;
        _lanes = lanes;
        _reviews = reviews;
        if (flashKey != null)
        {
            _flashKey = flashKey;
            _devFlashes = Fx.ReducedMotion ? 0 : 8;
        }
        RenderDev();
    }

    /// <summary>Redraws the agents' and lanes' lines (their timers, their colours after a colour-blind switch).</summary>
    public void RenderDev()
    {
        if (_sessions == null || _lanes == null) return;
        var now = DateTime.UtcNow;
        RenderAgents(now);
        RenderLanes(now);
    }

    void RenderAgents(DateTime now)
    {
        var all = _sessions!.All;
        bool fold = all.Count > MaxAgentLines;
        int lines = fold ? MaxAgentLines - 1 : all.Count;
        EnsureChips(_agentChips, _agentPanel, lines + (fold ? 1 : 0));
        var tip = new StringBuilder();
        foreach (var s in all)
        {
            if (tip.Length > 0) tip.Append('\n');
            tip.Append(_sessions.Line(s, now));
            string step = AgentSessions.StepLine(s, now);
            if (step.Length > 0) tip.Append("\n   ").Append(step);
        }
        for (int i = 0; i < lines; i++)
        {
            var s = all[i];
            string line = _sessions.Line(s, now);
            Fill(_agentChips[i], line, AgentSessions.StepLine(s, now), s.Look(now), s.Key, line);
        }
        if (fold)
        {
            var rest = all.Skip(lines).ToList();
            var look = rest.Any(s => s.Look(now) == DevLook.Attention) ? DevLook.Attention : rest.Any(s => s.Look(now) == DevLook.Working) ? DevLook.Working : DevLook.Done;
            Fill(_agentChips[lines], L.F("+{0} more", rest.Count), "", look, "", string.Join("\n", rest.Select(s => _sessions.Line(s, now))));
        }
        _agentPanel.IsVisible = all.Count > 0;
        _agentLook = _sessions.Urgent(now);
        _pillDot.IsVisible = _agentLook != null;
        if (_agentLook is DevLook urgent) _pillDot.Fill = Art.Brush(Art.Safe(Color.Parse(Colours(urgent).Dot)));
        ToolTip.SetTip(_pillDot, tip.Length > 0 ? tip.ToString() : null);
    }

    void RenderLanes(DateTime now)
    {
        var lanes = _lanes!;
        int n = lanes.Folded ? 1 : lanes.Count;
        bool reviews = _reviews.Count > 0;
        EnsureChips(_laneChips, _lanePanel, n + (reviews ? 1 : 0));
        if (lanes.Folded) Fill(_laneChips[0], lanes.FoldedLine(now), "", lanes.Urgent(now), "lanes", lanes.AllLines(now));
        else
            for (int i = 0; i < n; i++)
            {
                var lane = lanes.All[i];
                string line = StatusLanes.Line(lane, now);
                Fill(_laneChips[i], line, "", lane.Look(now), "lane:" + lane.Name, line);
            }
        string reviewText = _reviews.Count == 1 ? L.T("1 review") : L.F("{0} reviews", _reviews.Count);
        string reviewTip = reviewText + ":\n" + string.Join("\n", _reviews);
        if (reviews) Fill(_laneChips[n], reviewText, "", null, "reviews", reviewTip);
        _lanePanel.IsVisible = lanes.Count > 0 || reviews;

        _laneLook = lanes.Urgent(now);
        _pillLaneDot.IsVisible = _laneLook != null || reviews;
        _pillLaneDot.Fill = Art.Brush(Art.Safe(Color.Parse(_laneLook is DevLook look ? Colours(look).Dot : ReviewDot)));
        string tip = lanes.AllLines(now);
        if (reviews) tip = tip.Length > 0 ? tip + "\n" + reviewText : reviewText;
        ToolTip.SetTip(_pillLaneDot, tip.Length > 0 ? tip : null);
    }

    static void EnsureChips(List<DevChip> chips, StackPanel panel, int count)
    {
        while (chips.Count < count)
        {
            var chip = new DevChip();
            chips.Add(chip);
            panel.Children.Add(chip.Host);
        }
        for (int i = 0; i < chips.Count; i++) chips[i].Host.IsVisible = i < count;
    }

    /// <param name="look">The state's colours; null for the reviews' violet.</param>
    static void Fill(DevChip chip, string text, string step, DevLook? look, string key, string tip)
    {
        if (chip.Label.Text != text) chip.Label.Text = text;
        if (chip.Step.Text != step) chip.Step.Text = step;
        chip.Step.IsVisible = step.Length > 0;
        chip.Key = key;
        if (chip.Look != look || chip.Dot.Fill == null)
        {
            chip.Look = look;
            var (dot, back) = look is DevLook l ? Colours(l) : (ReviewDot, "#2A2140");
            chip.Dot.Fill = Art.Brush(Art.Safe(Color.Parse(dot)));
            chip.Chip.Background = Art.Brush(back);
            chip.Label.Foreground = Art.Brush(look == DevLook.Stale ? "#B4B9C2" : "#FFFFFF");
        }
        ToolTip.SetTip(chip.Chip, tip);
    }

    const string ReviewDot = "#B388FF";

    /// <summary>The dot and background of a look: amber working, green done or passed, red needing you or failed, blue running, grey quiet.</summary>
    static (string Dot, string Back) Colours(DevLook look) => look switch
    {
        DevLook.Working => ("#FFB020", "#3A2E12"),
        DevLook.Done or DevLook.Passed => ("#3DDC84", "#113A24"),
        DevLook.Attention or DevLook.Failed => ("#FF5C5C", "#3F1616"),
        DevLook.Running => ("#4DA3FF", "#132A45"),
        _ => ("#8A8F98", "#26282E"),
    };

    /// <summary>After a colour-blind switch: the dots take their new colours.</summary>
    public void RecolourDev()
    {
        foreach (var chip in _agentChips.Concat(_laneChips)) chip.Dot.Fill = null;
        RenderDev();
    }

    /// <summary>The live parts of the agents' and lanes' lines, on the slow blink: timers, pulsing dots, a finished line's flash.</summary>
    void BlinkDev()
    {
        if (_sessions == null || _lanes == null) return;
        var now = DateTime.UtcNow;
        if (_sessions.WorkingCount(now) > 0 || _lanes.RunningCount(now) > 0 || _devFlashes > 0) RenderDev();
        bool dim = !_blinkOn && !Fx.ReducedMotion;
        bool flash = _devFlashes > 0 && !_blinkOn;
        foreach (var chip in _agentChips.Concat(_laneChips))
        {
            if (!chip.Host.IsVisible) continue;
            chip.Dot.Opacity = dim && chip.Look is DevLook.Working or DevLook.Running ? 0.35 : 1;
            chip.Chip.Opacity = flash && chip.Key.Length > 0 && chip.Key == _flashKey ? 0.3 : 1;
        }
        bool laneFlash = _flashKey.StartsWith("lane:", StringComparison.Ordinal);
        _pillDot.Opacity = flash && !laneFlash || dim && _agentLook == DevLook.Working ? 0.35 : 1;
        _pillLaneDot.Opacity = flash && laneFlash || dim && _laneLook == DevLook.Running ? 0.35 : 1;
        if (_devFlashes > 0) _devFlashes--;
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

    /// <summary>
    /// The at-work line (see <see cref="OfficeDesk"/>): the next meeting, the focus block or break, a timer. The board
    /// shows <paramref name="text"/> in full; the pill a short <paramref name="brief"/> ("4:59") with the dot, and the
    /// whole line as its tooltip. <paramref name="urgent"/> makes the dot pulse. Null hides both.
    /// </summary>
    public void SetOffice(string? text, string? brief = null, Color? dot = null, bool urgent = false)
    {
        bool show = !string.IsNullOrEmpty(text);
        _officeChip.IsVisible = _pillOffice.IsVisible = show;
        _officeUrgent = show && urgent;
        if (!show) return;
        var c = Art.Safe(dot ?? Color.Parse("#78C8FF"));
        var fill = Art.Brush(c);
        _officeDot.Fill = _pillOfficeDot.Fill = fill;
        var bg = Art.Brush(Art.Blend(c, Color.Parse("#12141C"), 0.78));
        _officeChip.Background = _pillOffice.Background = bg;
        if (_officeText.Text != text) _officeText.Text = text;
        string shortText = brief ?? text!;
        if (_pillOfficeText.Text != shortText) _pillOfficeText.Text = shortText;
        ToolTip.SetTip(_pillOffice, text);
        if (!_officeUrgent) _officeDot.Opacity = _pillOfficeDot.Opacity = 1;
    }

    /// <summary>"4:05" or "1:02:03".</summary>
    public static string FormatWait(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    /// <summary>Called on a slow timer: pulses the status without needing a render loop.</summary>
    public void Blink()
    {
        _blinkOn = !_blinkOn;
        BlinkDev();

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

        if (_officeUrgent) _officeDot.Opacity = _pillOfficeDot.Opacity = _blinkOn || Fx.ReducedMotion ? 1 : 0.3;

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
