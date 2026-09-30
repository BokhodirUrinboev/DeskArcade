using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using DeskArcade.Engine;
using static DeskArcade.Games.PipelineRules;

namespace DeskArcade.Games;

/// <summary>
/// Pipeline (see <see cref="PipelineRules"/>): lay pipe from the <b>commit</b> to the <b>deploy</b> before the build flows.
/// Click a cell to lay the front piece of the queue (on the left of the grid) there; a piece laid over another replaces it
/// and costs time. When the countdown runs out the build flows through the pipes; reach the deploy and the next level
/// comes, longer and faster, with more in the way. Test and review pieces on the way score extra. "Deploy now" lets the
/// build rush through when the route is ready. A run, from level 1 to the first leak, is a race against the computer
/// rival or a co-worker (on the same levels over the LAN). The grid has a grip and remembers where it was put.
/// </summary>
public sealed class PipelineGame : MiniGame
{
    const double Cell = 50, QueueW = 70, Gap = 12, TopBar = 34;
    static readonly Color Water = Color.FromRgb(77, 200, 255), PipeInk = Color.FromRgb(170, 180, 196);

    readonly Canvas _root = new();
    readonly Canvas _grid = new() { IsHitTestVisible = false }, _flow = new() { IsHitTestVisible = false }, _chrome = new() { IsHitTestVisible = false };
    readonly TranslateTransform _move = new();
    readonly DragHandle _handle;
    readonly Border _countBar = new() { Height = 8, CornerRadius = new CornerRadius(4) };
    readonly TextBlock _status = new() { FontFamily = Fx.Font, FontSize = 13, FontWeight = FontWeight.Bold, Foreground = Brushes.White };
    PipelineRules? _rules;
    Vec2 _origin;
    int _level = 1, _runScore, _lanRound, _seed;
    bool _racing, _running, _demo;
    double _demoT, _nextLevelIn = -1;

    public PipelineGame(IGameHost host) : base(host)
    {
        _root.RenderTransform = _move;
        _root.Children.Add(_chrome);
        _root.Children.Add(_grid);
        _root.Children.Add(_flow);
        Layer.Children.Add(_root);
        _handle = new DragHandle(host, Id, Title);
        Layer.Children.Add(_handle.Visual);
    }

    public override string Id => "pipeline";
    public override string Title => "Pipeline";

    /// <summary>The run's score: the levels deployed, and the one in play (a deployed level is already in the run's).</summary>
    int Total => _runScore + (_rules is { State: not Phase.Deployed } r ? r.Score : 0);

    static double BoardW => QueueW + Gap + Width * Cell;
    static double BoardH => TopBar + Height * Cell;
    Rect Board => new(_origin.X, _origin.Y, BoardW, BoardH);
    Rect GridRect => new(_origin.X + QueueW + Gap, _origin.Y + TopBar, Width * Cell, Height * Cell);
    Rect HurryButton => new(_origin.X + BoardW - 118, _origin.Y + 4, 118, 24);

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        s.Rotor.Children.Add(Art.PathOf("M-10,4 L-2,4 Q3,4 3,-1 L3,-10", null, Art.Brush(PipeInk), 4.5));
        s.Rotor.Children.Add(Art.PathOf("M-10,4 L-2,4 Q3,4 3,-1 L3,-4", null, Art.Brush(Water), 2.2));
        s.Rotor.Children.Add(Art.Circle(7, 7, 3.5, Art.Brush("#3DDC84")));
        return s;
    }

    public override HudInfo Hud
    {
        get
        {
            long best = Host.Stats.Get("pipeline.best");
            string line = _rules == null || !_running && _rules.Over
                ? L.T("Click the grid to start · lay pipe from the commit to the deploy")
                : _rules.State == Phase.Building ? L.F("Level {0} · the build flows in {1} s · click a cell to lay the next piece", _level, (int)Math.Ceiling(_rules.Countdown))
                : L.F("Level {0} · flowing · keep laying ahead of it", _level);
            return new HudInfo(Total.ToString(CultureInfo.InvariantCulture), line, best > 0 ? L.F("Best {0}", best) : L.T("Best —"));
        }
    }

    public override string? ShareText => _rules is { State: Phase.Leaked } && !_running
        ? L.F("Pipeline · deployed {0} levels, {1} points 🚰", _level - 1, Total) : null;

    // ------------------------------------------------------------------ races

    bool LanOn => Host.Lan.Connected;
    public override bool SupportsLan => true;
    public override (int Score, bool Active)? Race => (Total, _racing);
    public override int RaceBaseline => 900;
    public override int RaceBest => (int)Host.Stats.Get("pipeline.best");
    public override double RaceSeconds => 180;

    public override void StartRace()
    {
        if (_racing) return;
        NewRun();
    }

    /// <summary>A new run from level 1; over the LAN both sides get the same levels, from the day and the round.</summary>
    void NewRun()
    {
        _level = 1;
        _runScore = 0;
        _seed = LanOn ? MinesweeperRules.DailySeed(DateTime.Today, "pipeline-lan", ++_lanRound) : Rng.Next();
        _running = true;
        _racing = true;
        Host.RoundStarted();
        NewLevel();
    }

    void NewLevel()
    {
        _rules = new PipelineRules(_level, _seed + _level * 7919);
        _nextLevelIn = -1;
        DrawAll();
        Host.Fx.Popup(new Vec2(GridRect.Center.X, GridRect.Top + 40), L.F("LEVEL {0}", _level), Themes.Themed(Themes.ClassicGold), 30, 1.3,
            L.F("the build flows in {0} s", (int)_rules.Countdown));
        Host.Sound.Play("score", 0.4, 0.9);
        Host.HudChanged();
        Host.Wake();
    }

    // ------------------------------------------------------------------ layout

    public override void Layout()
    {
        var a = Host.Arena;
        _origin = _handle.Saved() ?? new Vec2(a.Center.X - BoardW / 2, a.Center.Y - BoardH / 2);
        _origin = new Vec2(Clamp(_origin.X, a.Left + 8, Math.Max(a.Left + 8, a.Right - BoardW - 8)),
            Clamp(_origin.Y, a.Top + DragHandle.Height + 12, Math.Max(a.Top + DragHandle.Height + 12, a.Bottom - BoardH - 4)));
        _move.X = _origin.X;
        _move.Y = _origin.Y;
        _handle.Show(Board);
        DrawAll();
        Host.HudChanged();
    }

    public override void PositionsReset() => Layout();

    public override void Summon(Vec2 p)
    {
        _origin = p - new Vec2(BoardW / 2, BoardH / 2);
        _handle.Save(_origin);
        Layout();
    }

    public override void ThemeChanged() => DrawAll();

    // ------------------------------------------------------------------ input

    public override void CollectHitShapes(List<HitShape> into)
    {
        into.Add(HitShape.Box(Board));
        into.Add(_handle.Hit);
    }

    public override bool PointerDown(Vec2 p, bool right)
    {
        if (right || _handle.Contains(p))
        {
            _handle.Begin(p, _origin, anywhere: true);
            return true;
        }
        if (_rules == null || !_running)
        {
            NewRun();
            return false;
        }
        if (HurryButton.Contains(p.ToPoint()))
        {
            if (_rules.Over) return false;
            _rules.Hurry();
            Host.Sound.Play("whoosh", 0.4, 0.8);
            return false;
        }
        var g = GridRect;
        if (!g.Contains(p.ToPoint())) return false;
        int x = (int)((p.X - g.Left) / Cell), y = (int)((p.Y - g.Top) / Cell);
        Lay(x, y);
        return false;
    }

    void Lay(int x, int y)
    {
        if (_rules?.Place(x, y) is not { } e)
        {
            Host.Sound.Play("board", 0.2, 0.7);
            return;
        }
        Host.Stats.Add("pipeline.pieces");
        Host.Sound.Play(e.Kind == "replaced" ? "thunk" : "click", e.Kind == "replaced" ? 0.35 : 0.3, 1.1);
        if (e.Kind == "replaced")
            Host.Fx.Popup(CellCenter(x, y) - new Vec2(0, 20), L.F("replaced · −{0} s", (int)ReplaceCost), Colors.White, 14, 0.8);
        DrawAll();
        Host.HudChanged();
    }

    public override void PointerUp(Vec2 p) => _handle.End(_origin);

    public override void PointerCancel() => _handle.Cancel();

    Vec2 CellCenter(int x, int y) => new(GridRect.Left + (x + 0.5) * Cell, GridRect.Top + (y + 0.5) * Cell);

    // ------------------------------------------------------------------ time

    public override bool Update(double dt)
    {
        if (_handle.Dragging)
        {
            _origin = _handle.Move(Host.Pointer, new Size(BoardW, BoardH));
            _move.X = _origin.X;
            _move.Y = _origin.Y;
            _handle.Show(Board);
        }
        bool anim = Anims.Update(dt);
        if (_rules == null || !_running) return anim || _handle.Dragging;
        if (_nextLevelIn >= 0)
        {
            if ((_nextLevelIn -= dt) < 0)
            {
                _level++;
                NewLevel();
            }
            return true;
        }
        int before = (int)Math.Ceiling(_rules.Countdown);
        foreach (var e in _rules.Step(dt)) OnEvent(e);
        if (_demo) DemoPlay(dt);
        UpdateChrome();
        DrawFlow();
        if ((int)Math.Ceiling(_rules.Countdown) != before) Host.HudChanged();
        return true;
    }

    void OnEvent(Event e)
    {
        switch (e.Kind)
        {
            case "flow":
                Host.Sound.Play("attention", 0.35);
                Host.Fx.Popup(new Vec2(GridRect.Center.X, GridRect.Top + 30), L.T("THE BUILD IS FLOWING"), Color.FromRgb(77, 200, 255), 24, 1.2);
                Host.HudChanged();
                break;
            case "enter":
                Host.Sound.Play(e.Points > PipePoints ? "star" : "pop", e.Points > PipePoints ? 0.35 : 0.12, 1.4);
                if (e.Points > PipePoints)
                    Host.Fx.Popup(CellCenter(e.X, e.Y) - new Vec2(0, 20), _rules!.BadgeAt(e.X, e.Y) switch
                    {
                        Badge.Test => L.F("tests pass +{0}", e.Points),
                        Badge.Review => L.F("review approved +{0}", e.Points),
                        _ => $"+{e.Points}",
                    }, Themes.Themed(Themes.ClassicGold), 16, 0.9);
                Host.ShareAction(CellCenter(e.X, e.Y), e.Points);
                Host.HudChanged();
                break;
            case "deploy":
                Host.Stats.Add("pipeline.deploys");
                Host.Stats.Max("pipeline.level", _level);
                Host.Sound.Play("fire", 0.6);
                var at = CellCenter(e.X, e.Y);
                Host.Fx.Burst(at, Themes.Current.Confetti, 30, 420, 700, 6, 0.9);
                Host.Fx.Popup(new Vec2(GridRect.Center.X, GridRect.Top + 50), L.T("DEPLOYED!"), Themes.Themed(Themes.ClassicGold), 36, 1.6, L.F("+{0} · level {1} next", e.Points, _level + 1));
                _runScore += _rules!.Score;
                _nextLevelIn = 1.8;
                Host.HudChanged();
                break;
            case "leak":
                Leaked(e);
                break;
        }
    }

    void Leaked(Event e)
    {
        var rules = _rules!;
        int total = Total;
        _running = false;
        long before = Host.Stats.Get("pipeline.best");
        Host.Stats.Max("pipeline.best", total);
        if (_racing)
        {
            _racing = false;
            Host.RoundEnded(total);
        }
        var at = PipelineRules.In(e.X, e.Y) ? CellCenter(e.X, e.Y) : new Vec2(GridRect.Center.X, GridRect.Center.Y);
        Host.Fx.Burst(at, new[] { Water, Colors.White }, 24, 300, 900, 5, 0.9);
        bool best = total > before && total > 0;
        Host.Fx.Popup(new Vec2(GridRect.Center.X, GridRect.Top + 50), best ? L.T("NEW BEST!") : L.T("THE BUILD LEAKED"), best ? Themes.Themed(Themes.ClassicGold) : Colors.White, 36, 2.4,
            L.F("{0} levels deployed · {1} points · click the grid for another run", _level - 1, total));
        Host.Sound.Play(best ? "best" : "buzzer", 0.5);
        _demoT = 3;
        Host.HudChanged();
    }

    // ------------------------------------------------------------------ demo

    public override void DemoTick()
    {
        _demo = true;
        if (!_running && (_demoT -= 0.15) <= 0) NewRun();
    }

    /// <summary>The demo lays each piece where the route wants it, dumps the rest off the route, and hurries once the route is ready.</summary>
    void DemoPlay(double dt)
    {
        if (_rules is not { Over: false } r || (_demoT -= dt) > 0) return;
        _demoT = 0.55;
        var route = r.ShortestPath();
        if (route == null) return;
        var want = new List<(int X, int Y, Pipe Pipe)>();
        for (int i = 0; i < route.Count; i++)
        {
            var prev = i == 0 ? r.Start : route[i - 1];
            var next = i == route.Count - 1 ? r.End : route[i + 1];
            var pipe = Joining(Towards(route[i], prev), Towards(route[i], next));
            var have = r.PipeAt(route[i].X, route[i].Y);
            if (have != pipe && !(have == Pipe.Cross && pipe is Pipe.Horizontal or Pipe.Vertical)) want.Add((route[i].X, route[i].Y, pipe));
        }
        if (want.Count == 0)
        {
            r.Hurry();
            return;
        }
        var front = r.Queue[0].Pipe;
        var spot = want.FirstOrDefault(w => (w.Pipe == front || front == Pipe.Cross && w.Pipe is Pipe.Horizontal or Pipe.Vertical) && r.CanPlace(w.X, w.Y));
        if (spot.Pipe != Pipe.None)
        {
            Lay(spot.X, spot.Y);
            return;
        }
        var onRoute = route.ToHashSet();
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                if (r.CanPlace(x, y) && !onRoute.Contains((x, y)) && r.PipeAt(x, y) == Pipe.None)
                {
                    Lay(x, y);
                    return;
                }
    }

    // ------------------------------------------------------------------ drawing

    void DrawAll()
    {
        DrawChrome();
        DrawGrid();
        DrawFlow();
    }

    void DrawChrome()
    {
        _chrome.Children.Clear();
        var t = Themes.Current;
        _chrome.Children.Add(Art.At(new Rectangle { Width = BoardW, Height = BoardH, RadiusX = 12, RadiusY = 12, Fill = Art.Brush(Color.FromArgb(232, t.HudBack.R, t.HudBack.G, t.HudBack.B)), Stroke = Art.Brush(t.Accent), StrokeThickness = 1.5 }, 0, 0));
        _chrome.Children.Add(Art.At(new TextBlock { Text = L.T("NEXT"), FontFamily = Fx.Font, FontSize = 10, FontWeight = FontWeight.Black, Foreground = Art.Brush(t.Gold) }, 16, TopBar - 2));
        _chrome.Children.Add(Art.At(_status, 12, 8));
        var track = new Border { Width = 170, Height = 8, CornerRadius = new CornerRadius(4), Background = Art.Brush(50, 255, 255, 255) };
        _chrome.Children.Add(Art.At(track, QueueW + Gap + 150, 13));
        _chrome.Children.Add(Art.At(_countBar, QueueW + Gap + 150, 13));
        var hurry = HurryButton;
        _chrome.Children.Add(Art.At(new Border
        {
            Width = hurry.Width, Height = hurry.Height, CornerRadius = new CornerRadius(7), Background = Art.Brush(Art.Safe(Color.FromRgb(61, 186, 120))),
            Child = new TextBlock { Text = L.T("Deploy now ▶"), FontFamily = Fx.Font, FontSize = 12, FontWeight = FontWeight.Bold, Foreground = Brushes.White, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
        }, hurry.Left - _origin.X, hurry.Top - _origin.Y));
        // the queue: the front piece at the bottom, bigger
        if (_rules != null)
            for (int i = 0; i < _rules.Queue.Count; i++)
            {
                double size = i == 0 ? 58 : 44, cx = QueueW / 2, cy = TopBar + Height * Cell - 36 - i * 62;
                var tile = new Canvas();
                tile.Children.Add(Art.At(new Rectangle { Width = size, Height = size, RadiusX = 6, RadiusY = 6, Fill = Art.Brush(i == 0 ? Color.FromArgb(70, t.Accent.R, t.Accent.G, t.Accent.B) : Color.FromArgb(30, 255, 255, 255)), Stroke = Art.Brush(i == 0 ? t.Accent : Color.FromArgb(60, 255, 255, 255)), StrokeThickness = 1.2 }, -size / 2, -size / 2));
                DrawPipe(tile, _rules.Queue[i].Pipe, _rules.Queue[i].Badge, size);
                _chrome.Children.Add(Art.At(tile, cx, cy));
            }
        UpdateChrome();
    }

    void UpdateChrome()
    {
        if (_rules == null)
        {
            _status.Text = L.T("PIPELINE · click to start");
            _countBar.Width = 0;
            return;
        }
        _status.Text = _rules.State switch
        {
            Phase.Building => L.F("LEVEL {0} · flows in {1}", _level, (int)Math.Ceiling(_rules.Countdown)),
            Phase.Flowing => L.F("LEVEL {0} · flowing", _level),
            Phase.Deployed => L.T("DEPLOYED"),
            _ => L.T("LEAKED"),
        };
        double k = _rules.State == Phase.Building ? _rules.Countdown / CountdownFor(_level) : 0;
        _countBar.Width = 170 * Math.Clamp(k, 0, 1);
        _countBar.Background = Art.Brush(k < 0.25 ? Color.FromRgb(255, 107, 107) : Color.FromRgb(77, 200, 255));
    }

    void DrawGrid()
    {
        _grid.Children.Clear();
        if (_rules == null) return;
        double gx = QueueW + Gap, gy = TopBar;
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                var cell = new Canvas();
                bool blocked = _rules.Blocked(x, y);
                cell.Children.Add(Art.At(new Rectangle { Width = Cell - 2, Height = Cell - 2, RadiusX = 4, RadiusY = 4, Fill = Art.Brush(blocked ? Color.FromArgb(200, 70, 60, 60) : Color.FromArgb(34, 255, 255, 255)) }, -Cell / 2 + 1, -Cell / 2 + 1));
                if (blocked) cell.Children.Add(Art.PathOf($"M-12,-12 L12,12 M12,-12 L-12,12", null, Art.Brush(140, 255, 120, 120), 2.5));
                else if (_rules.IsStart(x, y)) DrawEnd(cell, L.T("commit"), _rules.StartOut, Color.FromRgb(77, 163, 255));
                else if (_rules.IsEnd(x, y)) DrawEnd(cell, L.T("deploy"), _rules.EndIn, Color.FromRgb(61, 220, 132));
                else DrawPipe(cell, _rules.PipeAt(x, y), _rules.BadgeAt(x, y), Cell);
                _grid.Children.Add(Art.At(cell, gx + (x + 0.5) * Cell, gy + (y + 0.5) * Cell));
            }
    }

    /// <summary>The water in the pipes the build has reached, the cell it is in filling as it goes.</summary>
    void DrawFlow()
    {
        _flow.Children.Clear();
        if (_rules == null || _rules.Path.Count == 0 && _rules.State != Phase.Flowing) return;
        double gx = QueueW + Gap, gy = TopBar;
        var water = Art.Brush(Water);
        for (int i = 0; i < _rules.Path.Count; i++)
        {
            var (x, y, from) = _rules.Path[i];
            bool head = i == _rules.Path.Count - 1 && _rules.State == Phase.Flowing;
            var outSide = Exit(_rules.PipeAt(x, y), from) ?? Opposite(from);
            var c = new Vec2(gx + (x + 0.5) * Cell, gy + (y + 0.5) * Cell);
            double k = head ? _rules.HeadProgress : 1;
            var a = c + Dir(from) * (Cell / 2);
            var b = c + Dir(outSide) * (Cell / 2);
            // in to the middle, then out: a filled share k of the way
            var mid = k < 0.5 ? a + (c - a) * (k * 2) : c;
            string d = $"M{Art.F(a.X)},{Art.F(a.Y)} L{Art.F(mid.X)},{Art.F(mid.Y)}";
            if (k > 0.5)
            {
                var end = c + (b - c) * ((k - 0.5) * 2);
                d += $" L{Art.F(end.X)},{Art.F(end.Y)}";
            }
            _flow.Children.Add(Art.PathOf(d, null, water, 7));
        }
    }

    static Vec2 Dir(Side s) => s switch { Side.North => new Vec2(0, -1), Side.South => new Vec2(0, 1), Side.East => new Vec2(1, 0), _ => new Vec2(-1, 0) };

    /// <summary>A pipe piece drawn around the centre of a cell of <paramref name="size"/>, with a small tag for a test or a review.</summary>
    static void DrawPipe(Canvas into, Pipe pipe, Badge badge, double size)
    {
        if (pipe == Pipe.None) return;
        double h = size / 2;
        var ink = Art.Brush(PipeInk);
        double thick = size * 0.28;
        foreach (var side in Openings(pipe))
        {
            var d = Dir(side) * h;
            into.Children.Add(Art.PathOf($"M0,0 L{Art.F(d.X)},{Art.F(d.Y)}", null, ink, thick));
        }
        into.Children.Add(Art.Circle(0, 0, thick / 2, ink));
        if (pipe == Pipe.Cross) into.Children.Add(Art.Circle(0, 0, thick * 0.34, Art.Brush("#5A6272")));
        if (badge == Badge.None) return;
        var tag = new Border
        {
            CornerRadius = new CornerRadius(4), Padding = new Thickness(3, 0), Background = Art.Brush(badge == Badge.Test ? Color.FromRgb(61, 186, 120) : Color.FromRgb(179, 136, 255)),
            Child = new TextBlock { Text = badge == Badge.Test ? L.T("test") : L.T("review"), FontFamily = Fx.Font, FontSize = size * 0.2, FontWeight = FontWeight.Black, Foreground = Brushes.White },
        };
        tag.Measure(Size.Infinity);
        into.Children.Add(Art.At(tag, -tag.DesiredSize.Width / 2, -tag.DesiredSize.Height / 2));
    }

    static void DrawEnd(Canvas into, string label, Side side, Color color)
    {
        var d = Dir(side) * (Cell / 2);
        into.Children.Add(Art.PathOf($"M0,0 L{Art.F(d.X)},{Art.F(d.Y)}", null, Art.Brush(PipeInk), Cell * 0.28));
        into.Children.Add(Art.At(new Rectangle { Width = Cell - 12, Height = Cell - 12, RadiusX = 8, RadiusY = 8, Fill = Art.Brush(Art.Safe(color)) }, -Cell / 2 + 6, -Cell / 2 + 6));
        var text = new TextBlock { Text = label, FontFamily = Fx.Font, FontSize = 9.5, FontWeight = FontWeight.Black, Foreground = Brushes.White, Width = Cell - 12, TextAlignment = TextAlignment.Center };
        into.Children.Add(Art.At(text, -Cell / 2 + 6, -7));
    }
}
