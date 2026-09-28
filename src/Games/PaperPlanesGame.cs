using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>
/// Paper Planes: a folded plane waits on a stack of paper in the corner. Press on it and drag back, away from where it
/// should go (a dotted line shows the start of its path), and let go: it glides across the desktop (see
/// <see cref="PaperPlaneFlight"/>), swoops if thrown flat and fast, stalls if thrown too steep, rises in the warm air
/// above your windows and lands on a window top or the taskbar. A throw scores the metres it flew, with a bonus for
/// touching down on the landing strip painted at the far end of the taskbar. Three throws make a round: a race
/// against the computer or a co-worker over the LAN. Ctrl+Alt+B moves the stack to the cursor.
/// </summary>
public sealed class PaperPlanesGame : MiniGame
{
    const double PullToSpeed = 4.2, Rest = 1.3;

    readonly Canvas _plane = new() { IsHitTestVisible = false };
    readonly RotateTransform _turn = new();
    readonly Canvas _pad = new() { IsHitTestVisible = false };
    readonly Canvas _strip = new() { IsHitTestVisible = false };
    readonly Canvas _preview = new() { IsHitTestVisible = false };
    readonly Canvas _bestFlag = new() { IsHitTestVisible = false, IsVisible = false };
    PaperPlaneFlight? _flight;
    Vec2 _launch, _pressAt;
    double _padX = double.NaN, _scale = 1, _rest, _trailT, _bestX;
    bool _aiming, _racing, _demo;
    int _throw, _points, _best, _demoWait;
    (double X1, double X2) _stripX;

    public PaperPlanesGame(IGameHost host) : base(host)
    {
        _plane.RenderTransform = _turn;
        Layer.Children.Add(_strip);
        Layer.Children.Add(_pad);
        Layer.Children.Add(_bestFlag);
        Layer.Children.Add(_preview);
        Layer.Children.Add(_plane);
        DrawPlane();
        DrawPad();
    }

    public override string Id => "planes";
    public override string Title => "Paper Planes";

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        s.Rotor.Children.Add(Art.PathOf("M-10,3 L10,-5 L-2,7 Z", Brushes.White, Art.Brush("#8A93A6"), 1));
        s.Rotor.Children.Add(Art.PathOf("M-2,7 L10,-5 L0,2 Z", Art.Brush("#D8DDE8"), Art.Brush("#8A93A6"), 1));
        return s;
    }

    public override HudInfo Hud
    {
        get
        {
            long best = Host.Stats.Get("planes.round");
            string line = _throw >= PaperPlaneScore.Throws && _flight == null
                ? L.F("Round over · {0} points · click the plane for another round", _points)
                : _flight != null ? L.T("Gliding…")
                : L.F("Throw {0}/{1} · drag back from the plane and let go", _throw + 1, PaperPlaneScore.Throws);
            return new HudInfo(_points.ToString(System.Globalization.CultureInfo.InvariantCulture), line, best > 0 ? L.F("Best {0}", best) : L.T("Best —"));
        }
    }

    // ------------------------------------------------------------------ races

    public override bool SupportsLan => true;
    public override (int Score, bool Active)? Race => (_points, _racing);
    public override int RaceBaseline => 200;
    public override int RaceBest => (int)Host.Stats.Get("planes.round");
    public override double RaceSeconds => 35;

    public override void StartRace()
    {
        if (_racing) return;
        if (_throw > 0) NewRound();
        BeginRound();
    }

    void BeginRound()
    {
        if (_racing) return;
        _racing = true;
        Host.RoundStarted();
    }

    void NewRound()
    {
        _throw = _points = _best = 0;
        _bestFlag.IsVisible = false;
        Host.HudChanged();
    }

    // ------------------------------------------------------------------ layout

    public override void Layout()
    {
        var a = Host.Arena;
        _scale = Clamp(a.Width / 1920, 0.6, 1.6);
        if (double.IsNaN(_padX) || _padX < a.Left || _padX > a.Right) _padX = a.Left + 90;
        _padX = Clamp(_padX, a.Left + 60, a.Left + a.Width * 0.4);
        _launch = new Vec2(_padX + 10, a.Bottom - 70);
        Canvas.SetLeft(_pad, _padX);
        Canvas.SetTop(_pad, a.Bottom);
        _stripX = (a.Right - a.Width * 0.2, a.Right - a.Width * 0.08);
        DrawStrip();
        if (_flight == null) PlaceOnPad();
        Host.HudChanged();
    }

    public override void Summon(Vec2 p)
    {
        if (_flight != null) return;
        _padX = p.X;
        Layout();
    }

    public override void Deactivate()
    {
        _aiming = false;
        _preview.Children.Clear();
    }

    void PlaceOnPad()
    {
        Canvas.SetLeft(_plane, _launch.X);
        Canvas.SetTop(_plane, _launch.Y);
        _turn.Angle = -8;
        _plane.IsVisible = _throw < PaperPlaneScore.Throws;
    }

    // ------------------------------------------------------------------ input

    Rect PlaneRect => new(_launch.X - 30, _launch.Y - 24, 60, 48);

    public override void CollectHitShapes(List<HitShape> into)
    {
        if (_flight == null) into.Add(HitShape.Box(PlaneRect.Union(new Rect(_padX - 40, Host.Arena.Bottom - 50, 80, 50))));
    }

    public override bool PointerDown(Vec2 p, bool right)
    {
        if (_flight != null) return false;
        if (_throw >= PaperPlaneScore.Throws)
        {
            NewRound();
            PlaceOnPad();
            return false;
        }
        _aiming = true;
        _pressAt = p;
        return true;
    }

    public override void PointerUp(Vec2 p)
    {
        if (!_aiming) return;
        _aiming = false;
        _preview.Children.Clear();
        var v = PullVelocity(p);
        if (v.Length < 80) return; // a tap: no throw
        Throw(v);
    }

    public override void PointerCancel()
    {
        _aiming = false;
        _preview.Children.Clear();
    }

    Vec2 PullVelocity(Vec2 p)
    {
        var v = (_pressAt - p) * PullToSpeed;
        if (v.X < 0) v = new Vec2(0, v.Y); // it flies away from the corner
        return v.Length > PaperPlaneFlight.MaxLaunch ? v * (PaperPlaneFlight.MaxLaunch / v.Length) : v;
    }

    IReadOnlyList<PlaneTop> Tops => Host.Platforms.Items.Where(t => t.Y < Host.Arena.Bottom - 4).Select(t => new PlaneTop(t.X1, t.X2, t.Y)).ToList();

    void Throw(Vec2 v)
    {
        if (_throw == 0 && !_racing) BeginRound();
        _flight = new PaperPlaneFlight(_launch, v, Host.Arena, Tops);
        Host.Stats.Add("planes.throws");
        Host.Sound.Play("whoosh", 0.4, 1.2);
        Host.HudChanged();
        Host.Wake();
    }

    // ------------------------------------------------------------------ the flight

    public override bool Update(double dt)
    {
        if (_aiming) Preview(PullVelocity(Host.Pointer));
        if (_flight is { } f)
        {
            if (f.Landed == PlaneLanding.Flying)
            {
                f.Advance(dt);
                Canvas.SetLeft(_plane, f.Pos.X);
                Canvas.SetTop(_plane, f.Pos.Y);
                _turn.Angle = f.Angle * 180 / Math.PI;
                if ((_trailT += dt) > 0.05)
                {
                    _trailT = 0;
                    Host.Fx.Spawn(f.Pos, default, Color.FromArgb(150, 255, 255, 255), 2, 0.6, 0);
                    if (f.InThermal) Host.Fx.Spawn(f.Pos + new Vec2(Rng.Next(-20, 20), 20), new Vec2(0, -60), Color.FromArgb(160, 255, 190, 120), 2.5, 0.7, 0);
                }
                if (f.Landed != PlaneLanding.Flying) Landed(f);
            }
            else if ((_rest -= dt) <= 0) NextThrow();
            return true;
        }
        if (_demo && !_aiming && _throw < PaperPlaneScore.Throws && _demoWait-- <= 0)
        {
            _demoWait = 30;
            double angle = (8 + Rng.NextDouble() * 20) * Math.PI / 180, speed = 360 + Rng.NextDouble() * 80;
            Throw(new Vec2(Math.Cos(angle), -Math.Sin(angle)) * speed);
        }
        return _aiming || _demo;
    }

    /// <summary>The dotted start of the path this throw would take (its first half second).</summary>
    void Preview(Vec2 v)
    {
        _preview.Children.Clear();
        if (v.Length < 80) return;
        var ghost = new PaperPlaneFlight(_launch, v, Host.Arena, Array.Empty<PlaneTop>());
        var dot = Art.Brush(Color.FromArgb(200, 255, 255, 255));
        for (int i = 0; i < 14 && ghost.Landed == PlaneLanding.Flying; i++)
        {
            ghost.Advance(0.045);
            _preview.Children.Add(Art.Circle(ghost.Pos.X, ghost.Pos.Y, 2.4, dot));
        }
    }

    void Landed(PaperPlaneFlight f)
    {
        _rest = Rest;
        int points = PaperPlaneScore.Points(f, _scale, _stripX);
        int metres = PaperPlaneScore.Metres(f.Distance, _scale);
        bool strip = f.Landed == PlaneLanding.Floor && f.Pos.X >= _stripX.X1 && f.Pos.X <= _stripX.X2;
        _points += points;
        _throw++;
        Host.Stats.Max("planes.best", metres);
        if (strip) Host.Stats.Add("planes.perfect");
        Host.ShareAction(f.Pos, points);
        var at = f.Pos - new Vec2(0, 40);
        if (f.Landed == PlaneLanding.Crash)
        {
            Host.Fx.Popup(at, L.T("CRUMPLED!"), Colors.White, 26, 1.3, L.F("{0} m · half points", metres));
            Host.Sound.Play("rustle", 0.5, 0.8);
        }
        else if (strip)
        {
            Host.Fx.Popup(at, L.T("PERFECT LANDING!"), Themes.Themed(Themes.ClassicGold), 30, 1.8, L.F("{0} m +{1}", metres, PaperPlaneScore.StripBonus));
            Host.Fx.Burst(f.Pos, Themes.Current.Confetti, 26, 380, 500, 5, 0.9);
            Host.Sound.Play("fire", 0.6);
        }
        else
        {
            Host.Fx.Popup(at, L.F("{0} m", metres), metres > 80 ? Themes.Themed(Themes.ClassicGold) : Colors.White, 28, 1.3);
            Host.Sound.Play("rustle", 0.35, 1.3);
        }
        if (points > _best)
        {
            _best = points;
            _bestX = f.Pos.X;
            Canvas.SetLeft(_bestFlag, _bestX);
            Canvas.SetTop(_bestFlag, f.Pos.Y);
            _bestFlag.IsVisible = true;
        }
        Host.HudChanged();
    }

    void NextThrow()
    {
        _flight = null;
        if (_throw >= PaperPlaneScore.Throws) RoundOver();
        PlaceOnPad();
        Host.HudChanged();
    }

    void RoundOver()
    {
        long before = Host.Stats.Get("planes.round");
        Host.Stats.Max("planes.round", _points);
        if (_racing)
        {
            _racing = false;
            Host.RoundEnded(_points);
        }
        var at = new Vec2(Host.Arena.Center.X, Host.Arena.Top + Host.Arena.Height * 0.32);
        bool best = _points > before && _points > 0;
        Host.Fx.Popup(at, best ? L.T("NEW BEST!") : L.T("ROUND OVER"), best ? Themes.Themed(Themes.ClassicGold) : Colors.White, 40, 2.4, L.F("{0} points in three throws", _points));
        if (best) Host.Fx.Burst(at, Themes.Current.Confetti, 40, 520, 700, 7, 1.1);
        Host.Sound.Play(best ? "best" : "score", 0.6);
        if (_demo) _demoWait = 40;
    }

    public override void DemoTick()
    {
        _demo = true;
        if (_throw >= PaperPlaneScore.Throws && _flight == null && _demoWait-- <= 0)
        {
            NewRound();
            PlaceOnPad();
        }
    }

    // ------------------------------------------------------------------ drawing

    void DrawPlane()
    {
        _plane.Children.Clear();
        // a dart folded from a sheet, nose to the right: the upper wing, the keel and the lower wing
        _plane.Children.Add(Art.PathOf("M22,0 L-18,-11 L-8,0 Z", Brushes.White, Art.Brush("#8A93A6"), 1));
        _plane.Children.Add(Art.PathOf("M22,0 L-8,0 L-16,7 Z", Art.Brush("#D8DDE8"), Art.Brush("#8A93A6"), 1));
        _plane.Children.Add(Art.PathOf("M22,0 L-18,-11 M-8,0 L22,0", null, Art.Brush("#B7BFCE"), 0.8));
        var mine = Art.Safe(Themes.Current.Mine);
        _plane.Children.Add(Art.PathOf("M-4,-5 L-12,-8", null, Art.Brush(mine), 2));
    }

    void DrawPad()
    {
        _pad.Children.Clear();
        for (int k = 0; k < 5; k++)
            _pad.Children.Add(Art.At(new Rectangle { Width = 52, Height = 5, Fill = Art.Brush(k % 2 == 0 ? "#F4F4F0" : "#E4E6EC"), Stroke = Art.Brush("#A8AFBE"), StrokeThickness = 0.6 }, -26 + k * 0.6, -5 - k * 5));
        var flag = new[] { "M0,0 L0,-26", "M0,-26 L14,-21 L0,-16 Z" };
        _bestFlag.Children.Clear();
        _bestFlag.Children.Add(Art.PathOf(flag[0], null, Art.Brush("#E6E6E6"), 1.5));
        _bestFlag.Children.Add(Art.PathOf(flag[1], Art.Brush(Themes.Current.Gold)));
    }

    /// <summary>The landing strip painted on the taskbar: a grey runway with white dashes and a threshold.</summary>
    void DrawStrip()
    {
        _strip.Children.Clear();
        double y = Host.Arena.Bottom - 7, w = _stripX.X2 - _stripX.X1;
        _strip.Children.Add(Art.At(new Rectangle { Width = w, Height = 8, RadiusX = 2, RadiusY = 2, Fill = Art.Brush(200, 60, 64, 72) }, _stripX.X1, y));
        for (double x = _stripX.X1 + 10; x < _stripX.X2 - 18; x += 26)
            _strip.Children.Add(Art.At(new Rectangle { Width = 14, Height = 2, Fill = Brushes.White }, x, y + 3));
        foreach (double x in new[] { _stripX.X1 + 2, _stripX.X2 - 6 })
            _strip.Children.Add(Art.At(new Rectangle { Width = 3, Height = 8, Fill = Art.Brush(Themes.Current.Gold) }, x, y));
    }

    public override void ThemeChanged()
    {
        DrawPlane();
        DrawPad();
        DrawStrip();
    }
}
