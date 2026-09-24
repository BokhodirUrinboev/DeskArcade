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
using static DeskArcade.Games.InternsWorld;

namespace DeskArcade.Games;

/// <summary>
/// Interns (see <see cref="InternsWorld"/>): a trapdoor lets a line of office interns out onto your window tops,
/// or just above the taskbar, and they walk wherever their feet take them. Get enough of them to the exit door
/// on the taskbar past the open manholes and the long drops. Pick a tool (umbrella, blocker, builder) and click
/// an intern to give it to them; click a blocker to let them walk on. Dragging a window moves everyone on it,
/// so a window can be a bridge. Each level has more interns, more manholes and fewer spare tools; your best
/// is the highest level cleared. A level is a round: over the LAN, or against the computer, the more interns
/// saved win. Only the interns, the hatch and the toolbar take the mouse.
/// </summary>
public sealed class InternsGame : MiniGame
{
    const double HatchW = 44, HatchH = 12, DoorW = 28, DoorH = 42, ButtonW = 104, ButtonH = 32, ButtonGap = 6, HitReach = 16;
    const double LidSlide = HatchW * 0.85, LidTime = 0.4, FadeTime = 0.35, PopTime = 0.3, GlowTime = 0.6;
    const double BannerIn = 0.45, BannerHold = 2.7, BannerOut = 0.5;

    static readonly Color Gold = Color.FromRgb(255, 209, 102);
    static readonly Color Shirt = Color.FromRgb(236, 240, 246);
    static readonly Color Pants = Color.FromRgb(52, 62, 86);
    static readonly Color Skin = Color.FromRgb(242, 194, 155);
    static readonly Tool[] Tools = { Tool.Umbrella, Tool.Blocker, Tool.Builder };

    enum Phase { Ready, Playing, Result }

    sealed class Look
    {
        public required Canvas Holder;
        public required ScaleTransform Flip, Pop;
        public required TranslateTransform Move;
        public required Control LegsA, LegsB, Arms, Umbrella, Brick;
        public required Ellipse Ring;
        public bool UmbrellaUp;
    }

    readonly Canvas _scenery = new() { IsHitTestVisible = false };
    readonly Canvas _bricks = new() { IsHitTestVisible = false };
    readonly Canvas _people = new() { IsHitTestVisible = false };
    readonly Canvas _toolbar = new() { IsHitTestVisible = false };
    readonly Canvas _hatch = new() { IsHitTestVisible = false };
    readonly Canvas _banners = new() { IsHitTestVisible = false };
    readonly TranslateTransform _lid = new(); // the trapdoor's lid slides aside to open
    readonly Dictionary<int, Look> _looks = new();
    readonly List<(Rect Box, Action Click)> _buttons = new();

    InternsWorld? _world;
    Phase _phase;
    Tool _tool = Tool.Umbrella;
    Rectangle? _doorLight, _doorGlow;
    int _level = 1, _drawnBricks, _seenGen = -1;
    double _timeLeft, _blockersOnlyFor, _resultFor, _demoWait;
    bool _passed, _racing;
    Rect _arenaAtLevel;
    Vec2? _toolbarAt;

    public InternsGame(IGameHost host) : base(host)
    {
        Layer.Children.Add(_scenery);
        Layer.Children.Add(_bricks);
        Layer.Children.Add(_hatch);
        Layer.Children.Add(_people);
        Layer.Children.Add(_toolbar);
        Layer.Children.Add(_banners);
    }

    public override string Id => "interns";
    public override string Title => "Interns";

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        var holder = new Canvas();
        DrawPerson(holder, out _, out _, out _, out _, out _);
        holder.RenderTransform = new ScaleTransform(0.95, 0.95);
        s.Rotor.Children.Add(Art.At(holder, 0, 10));
        return s;
    }

    public override HudInfo Hud
    {
        get
        {
            long best = Host.Stats.Get("interns.level");
            var w = _world;
            string line = w == null ? "" : _phase switch
            {
                Phase.Ready => L.F("Level {0} · save {1} of {2} · click the hatch to open it", _level, w.Needed, w.Total),
                Phase.Playing => L.F("Level {0} · out {1}/{2} · lost {3} · {4}s left", _level, w.Released, w.Total, w.Lost, (int)Math.Ceiling(_timeLeft)),
                _ => _passed ? L.F("Level {0} cleared · click the hatch for the next one", _level)
                    : L.F("Saved {0} of {1} needed · click the hatch to try again", w.Saved, w.Needed),
            };
            return new HudInfo(w == null ? "—" : $"{w.Saved}/{w.Needed}", line, best > 0 ? L.F("Best level {0}", best) : L.T("Best —"));
        }
    }

    // ------------------------------------------------------------------ races

    /// <summary>About how long a level with this many interns takes: they come out one by one, then walk the screen.</summary>
    public static double RoundSeconds(int interns) => 30 + interns * 4;

    /// <summary>The count the computer measures itself against: the most saved on any level, but never more than this level lets out.</summary>
    public static int RivalReference(long bestSaved, int total) => (int)Math.Min(Math.Max(0, bestSaved), total);

    public override bool SupportsLan => true;
    public override (int Score, bool Active)? Race => (_world?.Saved ?? 0, _racing);
    public override int RaceBaseline => _world?.Needed ?? 8;
    public override int RaceBest => RivalReference(Host.Stats.Get("interns.bestsaved"), _world?.Total ?? 24);
    public override double RaceSeconds => RoundSeconds(_world?.Total ?? 10);

    /// <summary>The rival's round began: the hatch opens on this level (or the next one, after a result).</summary>
    public override void StartRace()
    {
        if (_racing) return;
        OpenHatch();
    }

    void EndRound(int saved)
    {
        if (!_racing) return;
        _racing = false;
        Host.RoundEnded(saved);
    }

    // ------------------------------------------------------------------ levels

    public override void Layout()
    {
        var a = Host.Arena;
        // a new level when there is none, or the screen changed size under this one
        if (_world == null || Math.Abs(a.Width - _arenaAtLevel.Width) > 2 || Math.Abs(a.Bottom - _arenaAtLevel.Bottom) > 2)
        {
            if (_world == null) _level = (int)Math.Max(1, Host.Stats.Get("interns.level") + 1);
            NewLevel();
        }
        DrawToolbar();
        Host.HudChanged();
    }

    public override void Deactivate() => Anims.Finish();

    /// <summary>
    /// Lays out level <see cref="_level"/>: a trapdoor above a window top (from level 2, when one is wide enough)
    /// or just above the floor, the exit on the floor across the screen, and manholes in between. A level given
    /// up partway counts its round with what was saved.
    /// </summary>
    void NewLevel()
    {
        if (_world != null) EndRound(_world.Saved);
        var a = Host.Arena;
        _arenaAtLevel = a;
        int total = Math.Min(8 + 2 * _level, 24);
        int needed = (int)Math.Ceiling(total * Math.Min(0.6 + 0.05 * _level, 0.85));
        var tops = Host.Platforms.Items.Where(p => p.X2 - p.X1 >= 140 && p.Y > a.Top + 80 && p.Y < a.Bottom - 90).ToList();
        bool high = _level >= 2 && tops.Count > 0 && Rng.NextDouble() < 0.75;
        double spawnX, spawnY;
        IntPtr on = default;
        if (high)
        {
            var p = tops[Rng.Next(tops.Count)];
            spawnX = p.X1 + 30 + Rng.NextDouble() * (p.X2 - p.X1 - 60);
            spawnY = p.Y - 26;
            on = p.Hwnd;
        }
        else
        {
            bool leftSide = Rng.NextDouble() < 0.5;
            spawnX = leftSide ? a.Left + a.Width * (0.12 + Rng.NextDouble() * 0.12) : a.Right - a.Width * (0.12 + Rng.NextDouble() * 0.12);
            spawnY = a.Bottom - 34;
        }
        bool exitRight = spawnX < a.Center.X;
        double exitX = exitRight ? a.Right - a.Width * (0.08 + Rng.NextDouble() * 0.1) : a.Left + a.Width * (0.08 + Rng.NextDouble() * 0.1);
        double dir = exitRight ? 1 : -1;

        // manholes between the trapdoor and the exit, clear of both
        int pitCount = Math.Min(1 + _level / 2, 4);
        double from = Math.Min(spawnX, exitX) + 80, to = Math.Max(spawnX, exitX) - 80;
        var pits = new List<(double, double)>();
        for (int i = 0; i < pitCount && to - from > 120; i++)
        {
            double slot = (to - from) / pitCount;
            double w = 44 + Rng.NextDouble() * 20;
            double x = from + slot * i + Rng.NextDouble() * Math.Max(0, slot - w - 30) + 15;
            pits.Add((x, x + w));
        }
        int builders = pits.Count + (_level < 4 ? 2 : 1);
        int umbrellas = high ? total : Math.Max(1, 4 - _level / 2);
        _world = new InternsWorld(a.Left, a.Right, a.Bottom, pits, spawnX, spawnY, on, dir, exitX, total, needed,
            umbrellas, 2, builders);
        _phase = Phase.Ready;
        _timeLeft = 60 + total * 6;
        _blockersOnlyFor = _resultFor = 0;
        _drawnBricks = 0;
        _seenGen = Host.Platforms.Generation;
        foreach (var look in _looks.Values) _people.Children.Remove(look.Holder);
        _looks.Clear();
        _bricks.Children.Clear();
        DrawScenery();
        DrawHatch(false);
        DrawToolbar();
        Host.HudChanged();
        Host.Wake();
    }

    void OpenHatch()
    {
        if (_world == null) return;
        if (_phase == Phase.Result)
        {
            if (_passed) _level++;
            NewLevel();
        }
        if (_phase != Phase.Ready) return;
        _phase = Phase.Playing;
        _world.Open = true;
        DrawHatch(true);
        SlideLid(true);
        Host.Sound.Play("whoosh", 0.35, 0.8);
        _racing = true;
        Host.RoundStarted();
        Host.HudChanged();
        Host.Wake();
    }

    void EndLevel()
    {
        var w = _world!;
        _phase = Phase.Result;
        _passed = w.Saved >= w.Needed;
        string sub = L.F("{0} of {1} saved · {2} needed", w.Saved, w.Total, w.Needed);
        if (_passed)
        {
            Host.Stats.Max("interns.level", _level);
            if (w.Saved == w.Total) Host.Stats.Add("interns.perfect");
            Banner(w.Saved == w.Total ? L.T("EVERYONE MADE IT!") : L.F("LEVEL {0} CLEARED", _level), sub, Gold);
            var at = new Vec2(Host.Arena.Center.X, Host.Arena.Top + Host.Arena.Height * 0.2);
            Host.Fx.Burst(at, Themes.Current.Confetti, 40, 520, 700, 7, 1.0);
            Host.Sound.Play("best", 0.8);
        }
        else
        {
            Banner(L.T("NOT ENOUGH"), sub, Colors.White);
            Host.Sound.Play("buzzer", 0.4);
        }
        Host.Stats.Max("interns.bestsaved", w.Saved);
        EndRound(w.Saved);
        DrawHatch(false);
        SlideLid(false);
        Host.HudChanged();
    }

    // ------------------------------------------------------------------ frame

    public override bool Update(double dt)
    {
        var w = _world;
        if (w == null) return false;
        FollowWindows(w);
        bool anim = Anims.Update(dt);
        if (_phase == Phase.Result) _resultFor += dt;
        if (_phase != Phase.Playing) return anim;
        dt = Math.Min(dt, 0.05);
        var windows = Host.Platforms.Items.Select(p => new Ledge(p.Y, p.X1, p.X2, p.Hwnd)).ToList();
        foreach (var it in w.Step(dt, windows))
        {
            var at = new Vec2(it.X, it.Y - 8);
            if (it.State == State.Saved)
            {
                Host.Stats.Add("interns.saved");
                Host.Sound.Play("pop", 0.35, 1.4);
                Host.Fx.Popup(at - new Vec2(0, 16), "+1", Gold, 16, 0.7);
                Host.ShareAction(at, 1);
                GlowDoor();
            }
            else
            {
                Host.Sound.Play(it.Splat ? "thunk" : "whoosh", 0.35, it.Splat ? 0.9 : 1.6);
                Host.Fx.Burst(at, new[] { Shirt, Pants, Skin }, 10, 160, 500, 3, 0.5);
            }
            if (_looks.Remove(it.Id, out var gone)) _people.Children.Remove(gone.Holder);
            Host.HudChanged();
        }
        DrawBricks(w);
        DrawPeople(w);
        _timeLeft -= dt;
        _blockersOnlyFor = w.OnlyBlockersLeft ? _blockersOnlyFor + dt : 0;
        if (w.Finished || _timeLeft <= 0 || _blockersOnlyFor > 2.5) EndLevel();
        else if ((int)Math.Ceiling(_timeLeft) != (int)Math.Ceiling(_timeLeft + dt)) Host.HudChanged();
        return true;
    }

    /// <summary>Carries the interns, the trapdoor and nothing else when a window moves.</summary>
    void FollowWindows(InternsWorld w)
    {
        var plats = Host.Platforms;
        if (plats.Generation == _seenGen) return;
        _seenGen = plats.Generation;
        foreach (var p in plats.Items.Select(p => p.Hwnd).Distinct())
        {
            var d = plats.DeltaOf(p);
            if (d == default) continue;
            w.Carry(p, d.X, d.Y);
            if (w.SpawnOn == p)
            {
                w.MoveSpawn(d.X, d.Y);
                DrawHatch(_phase == Phase.Playing);
            }
        }
    }

    // ------------------------------------------------------------------ input

    public override void CollectHitShapes(List<HitShape> into)
    {
        foreach (var (box, _) in _buttons) into.Add(HitShape.Box(box));
        if (_world is not { } w) return;
        into.Add(HitShape.Box(HatchBox(w)));
        foreach (var it in w.Interns)
            if (it.State is not (State.Saved or State.Dead)) into.Add(HitShape.Circle(new Vec2(it.X, it.Y - 8), HitReach));
    }

    Rect HatchBox(InternsWorld w) => new(w.SpawnX - HatchW / 2 - 6, w.SpawnY - HatchH - 8, HatchW + 12, HatchH + 16);

    public override bool PointerDown(Vec2 p, bool right)
    {
        foreach (var (box, click) in _buttons)
            if (box.Contains(p.ToPoint()))
            {
                click();
                return false;
            }
        if (_world is not { } w) return false;
        if (_phase == Phase.Playing && w.At(p.X, p.Y, HitReach + 2) is { } who)
        {
            // right-click cycles the tool, so the mouse needn't travel to the toolbar
            if (right)
            {
                _tool = Tools[(Array.IndexOf(Tools, _tool) + 1) % Tools.Length];
                DrawToolbar();
                return false;
            }
            if (w.Assign(who, _tool))
            {
                Host.Sound.Play("board", 0.35, 1.6);
                Host.Stats.Add("interns.tools");
                DrawToolbar();
            }
            else Host.Fx.Popup(p - new Vec2(0, 30), w.Count(_tool) == 0 ? L.T("none left") : L.T("not now"), Colors.White, 15, 0.8);
            return false;
        }
        if (HatchBox(w).Contains(p.ToPoint()) && _phase != Phase.Playing) OpenHatch();
        return false;
    }

    public override void Summon(Vec2 p)
    {
        _toolbarAt = p;
        DrawToolbar();
    }

    // ------------------------------------------------------------------ demo

    /// <summary>Plays like a careful beginner: umbrellas for long falls, a builder at the lip of each manhole.</summary>
    public override void DemoTick()
    {
        if (_world is not { } w) return;
        if (_phase != Phase.Playing)
        {
            if (_phase == Phase.Ready || _resultFor > 3) OpenHatch();
            return;
        }
        if ((_demoWait -= 1) > 0) return;
        _demoWait = 3;
        foreach (var it in w.Interns)
        {
            // an umbrella for a long fall onto something, not for a drop down a manhole
            if (it.State == State.Falling && !it.Umbrella && w.Count(Tool.Umbrella) > 0 && it.Y < w.Floor - 10 && it.Y - it.FallFrom > SafeFall * 0.5)
            {
                w.Assign(it, Tool.Umbrella);
                DrawToolbar();
                return;
            }
            if (it.State != State.Walking || Math.Abs(it.Y - w.Floor) > 1 || w.Count(Tool.Builder) == 0) continue;
            if (w.Interns.Any(o => o.State == State.Building)) continue;
            foreach (var (x1, x2) in w.Pits)
            {
                double lip = it.Dir > 0 ? x1 - it.X : it.X - x2;
                if (lip is > 4 and < 14)
                {
                    w.Assign(it, Tool.Builder);
                    DrawToolbar();
                    return;
                }
            }
        }
    }

    // ------------------------------------------------------------------ motion

    /// <summary>The lid slides off the trapdoor to open it (with a little overshoot) and back to close it.</summary>
    void SlideLid(bool open)
    {
        double from = open ? 0 : -LidSlide, to = open ? -LidSlide : 0;
        _lid.X = from;
        Anims.Add(LidTime, k => _lid.X = from + (to - from) * k, open ? Ease.OutBack : Ease.OutCubic);
        Host.Wake();
    }

    /// <summary>An umbrella springs open over a falling intern.</summary>
    void PopUmbrella(Look look)
    {
        look.Pop.ScaleX = look.Pop.ScaleY = 0.2;
        Anims.Add(PopTime, k => look.Pop.ScaleX = look.Pop.ScaleY = 0.2 + 0.8 * k, Ease.OutBack);
        Host.Sound.Play("pop", 0.25, 0.9);
    }

    /// <summary>The exit door lights up as an intern goes through.</summary>
    void GlowDoor()
    {
        if (_doorLight is not { } light || _doorGlow is not { } glow) return;
        Anims.Add(GlowTime, k =>
        {
            double p = Ease.Pulse(k);
            glow.Opacity = p;
            light.Opacity = 0.85 + 0.15 * p;
        }, Ease.Linear);
    }

    /// <summary>The level's verdict on a banner across the top of the screen: it springs in, stays a while and fades.</summary>
    void Banner(string title, string sub, Color color)
    {
        var a = Host.Arena;
        var panel = new StackPanel { IsHitTestVisible = false };
        panel.Children.Add(new TextBlock
        {
            Text = title, FontFamily = Fx.Font, FontSize = 34, FontWeight = FontWeight.Black, Foreground = Art.Brush(Art.Safe(color)),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = sub, FontFamily = Fx.Font, FontSize = 15, FontWeight = FontWeight.Bold, Foreground = Art.Brush("#E6EAF2"),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        var scale = new ScaleTransform(0.6, 0.6);
        var border = new Border
        {
            Background = Art.Brush(235, 20, 24, 34), BorderBrush = Art.Brush(Art.Safe(color)), BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(14), Padding = new Thickness(28, 12, 28, 14), Child = panel, IsHitTestVisible = false,
            RenderTransformOrigin = RelativePoint.Center, RenderTransform = scale, Opacity = 0,
        };
        border.Measure(Size.Infinity);
        var ds = border.DesiredSize;
        _banners.Children.Add(Art.At(border, a.Center.X - ds.Width / 2, a.Top + a.Height * 0.2 - ds.Height / 2));
        Anims.Add(BannerIn, k =>
        {
            border.Opacity = Math.Min(1, k * 1.5);
            scale.ScaleX = scale.ScaleY = 0.6 + 0.4 * k;
        }, Ease.OutBack);
        Anims.Add(BannerOut, k =>
        {
            border.Opacity = 1 - k;
            scale.ScaleX = scale.ScaleY = 1 + 0.1 * k;
        }, Ease.InQuad, () => _banners.Children.Remove(border), BannerHold);
        Host.Wake();
    }

    // ------------------------------------------------------------------ drawing

    void DrawScenery()
    {
        _scenery.Children.Clear();
        var w = _world!;
        // open manholes: a dark hole with hazard stripes along its rim
        foreach (var (x1, x2) in w.Pits)
        {
            _scenery.Children.Add(Art.At(new Rectangle { Width = x2 - x1, Height = 14, RadiusX = 4, RadiusY = 4, Fill = Art.Brush("#0E0F14") }, x1, w.Floor - 3));
            var stripes = new System.Text.StringBuilder();
            for (double x = x1 - 10; x < x2 + 10; x += 8) stripes.Append(string.Create(CultureInfo.InvariantCulture, $"M{x},{w.Floor - 3} l4,0 l-4,-4 l-4,0 Z "));
            _scenery.Children.Add(Art.PathOf(stripes.ToString(), Art.Brush(Gold)));
            _scenery.Children.Add(Art.At(new Rectangle { Width = x2 - x1 + 20, Height = 4, Stroke = Art.Brush("#1B1B22"), StrokeThickness = 1 }, x1 - 10, w.Floor - 7));
        }
        // the exit: a door with a green sign above it, and a glow that lights up as someone goes through
        double ex = w.ExitX - DoorW / 2, ey = w.Floor - DoorH;
        _scenery.Children.Add(Art.At(new Rectangle { Width = DoorW + 8, Height = DoorH + 4, RadiusX = 3, RadiusY = 3, Fill = Art.Brush("#6B4A2B") }, ex - 4, ey - 4));
        _scenery.Children.Add(Art.At(new Rectangle { Width = DoorW, Height = DoorH, Fill = Art.Brush("#1B1B22") }, ex, ey));
        _doorLight = Art.At(new Rectangle { Width = DoorW - 6, Height = DoorH - 4, Fill = Art.Brush(Color.FromRgb(255, 226, 150)), Opacity = 0.85 }, ex + 3, ey + 4);
        _scenery.Children.Add(_doorLight);
        _doorGlow = Art.At(new Rectangle { Width = DoorW + 16, Height = DoorH + 12, RadiusX = 6, RadiusY = 6, Stroke = Art.Brush(Gold), StrokeThickness = 3, Opacity = 0 }, ex - 8, ey - 8);
        _scenery.Children.Add(_doorGlow);
        var sign = new Border
        {
            Background = Art.Brush(Color.FromRgb(30, 150, 80)), CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 0),
            Child = new TextBlock { Text = L.T("EXIT"), FontFamily = Fx.Font, FontSize = 10, FontWeight = FontWeight.Black, Foreground = Brushes.White },
        };
        sign.Measure(Size.Infinity);
        _scenery.Children.Add(Art.At(sign, w.ExitX - sign.DesiredSize.Width / 2, ey - 22));
    }

    /// <summary>The trapdoor: a dark opening with a lid over it that sits aside while it is open.</summary>
    void DrawHatch(bool open)
    {
        _hatch.Children.Clear();
        var w = _world!;
        double x = w.SpawnX - HatchW / 2, y = w.SpawnY - HatchH;
        _hatch.Children.Add(Art.At(new Rectangle { Width = HatchW, Height = HatchH, RadiusX = 3, RadiusY = 3, Fill = Art.Brush("#0E0F14"), Stroke = Art.Brush("#1B1B22"), StrokeThickness = 1.5 }, x, y));
        if (open)
            _hatch.Children.Add(Art.PathOf(string.Create(CultureInfo.InvariantCulture, $"M{x + 2},{y + HatchH} l-6,12 M{x + HatchW - 2},{y + HatchH} l6,12"), null, Art.Brush("#8A90A2"), 3));
        var lid = new Canvas { IsHitTestVisible = false, RenderTransform = _lid };
        lid.Children.Add(Art.At(new Rectangle { Width = HatchW, Height = HatchH, RadiusX = 3, RadiusY = 3, Fill = Art.Brush("#3A3F4E"), Stroke = Art.Brush("#1B1B22"), StrokeThickness = 1.5 }, x, y));
        lid.Children.Add(Art.At(new Rectangle { Width = HatchW - 8, Height = 3, Fill = Art.Brush("#8A90A2") }, x + 4, y + HatchH - 3));
        _hatch.Children.Add(lid);
        _lid.X = open ? -LidSlide : 0;
        if (!open && _phase != Phase.Playing)
        {
            var hint = _phase == Phase.Result && !_passed ? L.T("click to try again") : _phase == Phase.Result ? L.T("click for the next level") : L.T("click to open");
            var t = new TextBlock { Text = hint, FontFamily = Fx.Font, FontSize = 12, FontWeight = FontWeight.Bold, Foreground = Art.Brush(Gold) };
            t.Measure(Size.Infinity);
            _hatch.Children.Add(Art.At(t, w.SpawnX - t.DesiredSize.Width / 2, y - 20));
        }
    }

    void DrawBricks(InternsWorld w)
    {
        for (; _drawnBricks < w.Bricks.Count; _drawnBricks++)
        {
            var b = w.Bricks[_drawnBricks];
            _bricks.Children.Add(Art.At(new Rectangle { Width = b.X2 - b.X1, Height = BrickRise, Fill = Art.Brush("#B5652B"), Stroke = Art.Brush("#6E3A15"), StrokeThickness = 0.8 }, b.X1, b.Y));
        }
    }

    void DrawPeople(InternsWorld w)
    {
        foreach (var it in w.Interns)
        {
            if (it.State is State.Saved or State.Dead) continue;
            if (!_looks.TryGetValue(it.Id, out var look)) _looks[it.Id] = look = NewLook();
            look.Move.X = it.X;
            look.Move.Y = it.Y;
            look.Flip.ScaleX = it.Dir < 0 ? -1 : 1;
            bool step = it.State == State.Walking && (int)(it.Age * 7) % 2 == 1;
            look.LegsA.IsVisible = !step;
            look.LegsB.IsVisible = step;
            look.Arms.IsVisible = it.State == State.Blocking;
            bool brolly = it.Umbrella && it.State == State.Falling;
            if (brolly && !look.UmbrellaUp) PopUmbrella(look);
            look.UmbrellaUp = brolly;
            look.Umbrella.IsVisible = brolly;
            look.Brick.IsVisible = it.State == State.Building;
            look.Ring.IsVisible = it.State == State.Blocking;
        }
    }

    /// <summary>A new intern's figure, fading in as they drop out of the hatch.</summary>
    Look NewLook()
    {
        var holder = new Canvas { IsHitTestVisible = false, Opacity = 0 };
        var ring = new Ellipse { Width = 26, Height = 8, Stroke = Art.Brush(Color.FromRgb(255, 107, 107)), StrokeThickness = 2, IsVisible = false };
        holder.Children.Add(Art.At(ring, -13, -4));
        DrawPerson(holder, out var legsA, out var legsB, out var arms, out var umbrella, out var brick);
        var pop = new ScaleTransform(1, 1);
        umbrella.RenderTransformOrigin = new RelativePoint(0.5, 1, RelativeUnit.Relative); // it opens from the handle
        umbrella.RenderTransform = pop;
        var flip = new ScaleTransform(1, 1);
        var move = new TranslateTransform();
        holder.RenderTransform = new TransformGroup { Children = { flip, move } };
        _people.Children.Add(holder);
        Anims.Add(FadeTime, k => holder.Opacity = k, Ease.OutQuad);
        return new Look { Holder = holder, Flip = flip, Pop = pop, Move = move, LegsA = legsA, LegsB = legsB, Arms = arms, Umbrella = umbrella, Brick = brick, Ring = ring };
    }

    /// <summary>An intern around their feet at (0, 0), facing right: white shirt, red tie, dark trousers.</summary>
    static void DrawPerson(Canvas into, out Control legsA, out Control legsB, out Control arms, out Control umbrella, out Control brick)
    {
        var pants = Art.Brush(Pants);
        legsA = Art.PathOf("M-1.5,-7 L-3.5,0 M1.5,-7 L3.5,0", null, pants, 2.4);
        legsB = Art.PathOf("M-1.5,-7 L0,0 M1.5,-7 L1,0", null, pants, 2.4);
        legsB.IsVisible = false;
        into.Children.Add(legsA);
        into.Children.Add(legsB);
        into.Children.Add(Art.At(new Rectangle { Width = 8, Height = 9, RadiusX = 2, RadiusY = 2, Fill = Art.Brush(Shirt), Stroke = Art.Brush("#8A93A6"), StrokeThickness = 0.8 }, -4, -15.5));
        into.Children.Add(Art.PathOf("M0,-15 L1,-11 L0,-9 L-1,-11 Z", Art.Brush(Color.FromRgb(214, 48, 49))));
        arms = Art.At(new Rectangle { Width = 18, Height = 2.2, Fill = Art.Brush(Shirt), Stroke = Art.Brush("#8A93A6"), StrokeThickness = 0.5, IsVisible = false }, -9, -14.5);
        into.Children.Add(arms);
        into.Children.Add(Art.Circle(0.5, -19, 3.6, Art.Brush(Skin)));
        into.Children.Add(Art.PathOf("M-3,-20 Q0,-24.5 4,-20.5 Q1,-21.5 -3,-20 Z", Art.Brush("#3B2A1E")));
        into.Children.Add(Art.Circle(2, -19.3, 0.6, Art.Brush("#1B1B22")));
        umbrella = Art.PathOf("M-10,-26 Q0,-36 10,-26 Z M0,-26 L0,-16", Art.Brush(Color.FromRgb(214, 48, 49)), Art.Brush("#1B1B22"), 1);
        umbrella.IsVisible = false;
        into.Children.Add(umbrella);
        brick = Art.At(new Rectangle { Width = 7, Height = 4, Fill = Art.Brush("#B5652B"), IsVisible = false }, 3, -12);
        into.Children.Add(brick);
    }

    void DrawToolbar()
    {
        _toolbar.Children.Clear();
        _buttons.Clear();
        if (_world is not { } w) return;
        var a = Host.Arena;
        double total = (ButtonW + ButtonGap) * (Tools.Length + 1) - ButtonGap;
        var hud = Host.HudBounds.Inflate(8);
        // above the floor, clear of the tallest stairs, so it doesn't sit on the title bars of windows
        var at = _toolbarAt ?? new Vec2(a.Center.X, a.Bottom - StairSteps * BrickRise - 60 - ButtonH / 2);
        double x = Clamp(at.X - total / 2, a.Left + 8, a.Right - total - 8), y = Clamp(at.Y - ButtonH / 2, a.Top + 8, a.Bottom - ButtonH - 60);
        if (_toolbarAt == null && new Rect(x, y, total, ButtonH).Intersects(hud)) y = hud.Top - ButtonH - 6;
        foreach (var tool in Tools)
        {
            var t = tool;
            string label = tool switch
            {
                Tool.Umbrella => L.F("Umbrella {0}", w.Count(tool)),
                Tool.Blocker => L.F("Blocker {0}", w.Count(tool)),
                _ => L.F("Builder {0}", w.Count(tool)),
            };
            Button(new Rect(x, y, ButtonW, ButtonH), label, tool == _tool, w.Count(tool) > 0, () =>
            {
                _tool = t;
                DrawToolbar();
            });
            x += ButtonW + ButtonGap;
        }
        Button(new Rect(x, y, ButtonW, ButtonH), L.T("Restart"), false, true, () =>
        {
            NewLevel();
            DrawToolbar();
        });
    }

    void Button(Rect r, string text, bool selected, bool enabled, Action click)
    {
        _toolbar.Children.Add(Art.At(new Border
        {
            Width = r.Width, Height = r.Height, CornerRadius = new CornerRadius(r.Height / 2),
            Background = selected ? Art.Brush(Gold) : Art.Brush(225, 24, 27, 38),
            BorderBrush = selected ? Art.Brush("#8A6D1F") : Art.Brush(90, 255, 255, 255), BorderThickness = new Thickness(1.5),
            Opacity = enabled ? 1 : 0.5,
            Child = new TextBlock
            {
                Text = text, FontFamily = Fx.Font, FontSize = 13, FontWeight = FontWeight.Bold,
                Foreground = selected ? Art.Brush("#2A2008") : Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        }, r.X, r.Y));
        _buttons.Add((r, click));
    }
}
