using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>
/// Pull back the bow, mind the wind, hit targets and balloons. 10 arrows per round. Over the LAN two
/// players take turns, arrow by arrow (see ArcheryDuel.cs).
/// </summary>
public sealed partial class ArcheryGame : MiniGame
{
    const double MaxPull = 150, MinPull = 18, ArrowLen = 84, Gravity = 980, Step = 1.0 / 240, GrabR = 80;
    const double TargetW = 40, TargetH = 130, BalloonR = 20;
    const int ArrowsPerRound = 10;

    static readonly Color Gold = Color.FromRgb(255, 209, 102);
    static readonly Color[] Confetti = { Gold, Color.FromRgb(239, 71, 111), Color.FromRgb(6, 214, 160), Colors.White };
    static readonly Color[] PopBurst = { Colors.White, Color.FromRgb(230, 230, 240) };
    static readonly Color[] BalloonColors =
    {
        Color.FromRgb(239, 71, 111), Color.FromRgb(17, 138, 178), Color.FromRgb(6, 190, 140), Color.FromRgb(150, 90, 220),
    };

    sealed class Arrow
    {
        public required Sprite Sprite;
        public Vec2 Tip;
        public Vec2 Vel;
        public bool Flying = true;
        public double StuckAge, Angle;
        public Target? In;
        public Vec2 InOffset;
        public IntPtr Hwnd;
        public int SeenGen;
    }

    sealed class Target
    {
        public required Sprite Sprite;
        public Vec2 Pos;
        public double BaseY, BobAmp, BobPhase, BobT, Age, Flash;
        public double LeaveIn = double.NaN;
    }

    sealed class Balloon
    {
        public required Sprite Sprite;
        public Vec2 Pos;
        public double Phase, Speed, Age, AtCeiling;
        public bool Gold;
    }

    readonly Canvas _targetLayer = new(), _balloonLayer = new(), _arrowLayer = new();
    readonly Canvas _guideLayer = new() { IsHitTestVisible = false };
    readonly Sprite _bow = new();
    readonly Path _string = new() { Stroke = Art.Brush("#EDE6D6"), StrokeThickness = 1.6 };
    readonly Canvas _nock = ArrowShape();
    readonly TranslateTransform _nockTr = new();
    readonly TextBlock _infoText = new()
    {
        FontFamily = Fx.Font, FontSize = 11, FontWeight = FontWeight.SemiBold,
        Foreground = Brushes.White, TextAlignment = TextAlignment.Center,
    };
    readonly Ellipse[] _dots = new Ellipse[12];
    readonly List<Arrow> _arrows = new();
    readonly List<Target> _targets = new();
    readonly List<Balloon> _balloons = new();

    Vec2 _bowPos;
    int _face = 1, _round, _arrowsLeft, _roundScore;
    bool _pulling, _movingBow, _roundOver = true;
    Vec2 _pull, _moveOffset;
    double _wind, _time, _acc, _active, _roundEndTimer = -1, _lastNockX = double.NaN;

    public ArcheryGame(IGameHost host) : base(host)
    {
        var halo = Art.Circle(0, 0, GrabR, Art.Brush(12, 255, 255, 255), Art.Brush(60, 255, 255, 255), 1.5);
        halo.StrokeDashArray = new AvaloniaList<double> { 4, 6 };
        _bow.Children.Insert(0, halo);

        const string limbs = "M-4,-64 C20,-54 26,-22 9,-8 L9,8 C26,22 20,54 -4,64";
        _bow.Rotor.Children.Add(Art.PathOf(limbs, null, Art.Brush("#5A3318"), 6));
        _bow.Rotor.Children.Add(Art.PathOf(limbs, null, Art.Brush("#B07A45"), 2.5));
        _bow.Rotor.Children.Add(Art.At(new Rectangle { Width = 9, Height = 22, RadiusX = 3, RadiusY = 3, Fill = Art.Brush("#2A1709") }, 5, -11));
        _bow.Rotor.Children.Add(_string);
        _nock.RenderTransformOrigin = RelativePoint.TopLeft;
        _nock.RenderTransform = _nockTr;
        _bow.Rotor.Children.Add(_nock);

        var info = new Border
        {
            Width = 180, CornerRadius = new CornerRadius(8), Background = Art.Brush(235, 18, 20, 28),
            Padding = new Thickness(6, 2, 6, 3), Child = _infoText, IsHitTestVisible = false,
        };
        _bow.Children.Add(Art.At(info, -90, GrabR + 6));

        for (int i = 0; i < _dots.Length; i++)
        {
            _dots[i] = new Ellipse
            {
                Width = 6, Height = 6, Fill = Brushes.White, IsVisible = false,
                RenderTransformOrigin = RelativePoint.TopLeft, RenderTransform = new TranslateTransform(),
            };
            _guideLayer.Children.Add(_dots[i]);
        }

        Layer.Children.Add(_targetLayer);
        Layer.Children.Add(_balloonLayer);
        Layer.Children.Add(_arrowLayer);
        Layer.Children.Add(_bow);
        Layer.Children.Add(_guideLayer);
        DuelSetup();
    }

    public override string Id => "archery";
    public override string Title => "Archery";
    public override bool SupportsLan => true;

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        foreach (var (r, c) in new[] { (10.0, "#F4F4F4"), (7.5, "#2B6CD8"), (5.0, "#E0413A"), (2.5, "#FFD23F") })
            s.Rotor.Children.Add(Art.Circle(0, 0, r, Art.Brush(c), Art.Brush("#1B1B1B"), 0.6));
        return s;
    }

    string WindText => Math.Abs(_wind) < 15 ? L.T("calm") : (_wind > 0 ? "→ " : "← ") + (Math.Abs(_wind) / 60).ToString("0.0");

    public override HudInfo Hud => DuelOn ? DuelHud : new(
        _roundScore.ToString(),
        _roundOver ? L.T("Round over · pull the bow to play again") : L.F("Round {0} · {1} arrows · wind {2}", _round, _arrowsLeft, WindText),
        L.F("Best round {0}", Host.Settings.BestArchery));

    void Changed()
    {
        _infoText.Text = _roundOver ? L.T("pull back to start a round") : L.F("{0} arrows · wind {1}", _arrowsLeft, WindText);
        Host.HudChanged();
    }

    public override void CollectHitShapes(List<HitShape> into) => into.Add(HitShape.Circle(_bowPos, GrabR));

    // ------------------------------------------------------------------ setup

    public override void Layout()
    {
        var a = Host.Arena;
        _bowPos = new Vec2(Host.Settings.BowX ?? a.Left + 150, Host.Settings.BowY ?? a.Bottom - 280);
        ClampBow();
        if (_round == 0) NewRound();
        else if (_targets.Any(t => !a.Contains(t.Pos.ToPoint()))) RespawnTargets();
        DuelCheckSession();
        UpdateBowVisual();
        Changed();
    }

    public override void Deactivate()
    {
        _pulling = _movingBow = false;
        HideGuide();
    }

    void ClampBow()
    {
        var a = Host.Arena;
        _bowPos.X = Clamp(_bowPos.X, a.Left + GrabR, a.Right - GrabR);
        _bowPos.Y = Clamp(_bowPos.Y, a.Top + GrabR, a.Bottom - GrabR - 30);
        _face = _bowPos.X < a.Left + a.Width / 2 ? 1 : -1;
    }

    /// <param name="wind">The wind for this round; by default it picks one (calm in round 1, stronger later).</param>
    void NewRound(double? wind = null)
    {
        _round++;
        _arrowsLeft = ArrowsPerRound;
        _roundScore = 0;
        _roundOver = false;
        _roundEndTimer = -1;
        _wind = wind ?? (_round == 1 ? 0 : (Rng.NextDouble() * 2 - 1) * Math.Min(260, 60 + _round * 40));
        for (int i = _arrows.Count - 1; i >= 0; i--)
            if (!_arrows[i].Flying) RemoveArrow(_arrows[i]);
        RespawnTargets();
        Changed();
    }

    void RespawnTargets()
    {
        foreach (var t in _targets) _targetLayer.Children.Remove(t.Sprite);
        _targets.Clear();
        foreach (var ar in _arrows.Where(x => x.In != null).ToList()) RemoveArrow(ar);
        for (int i = 0; i < 3; i++) SpawnTarget();
    }

    void EndRound()
    {
        _roundOver = true;
        _roundEndTimer = -1;
        var s = Host.Settings;
        Host.Stats.Max("archery.round", _roundScore);
        if (DuelOn)
        {
            if (_roundScore > s.BestArchery) s.BestArchery = _roundScore;
            Host.SaveSettings();
            Changed(); // the duel announces the result once both players are done
            return;
        }
        bool best = _roundScore > s.BestArchery;
        var at = new Vec2(Host.Arena.Left + Host.Arena.Width / 2, Host.Arena.Top + Host.Arena.Height * 0.3);
        if (best)
        {
            s.BestArchery = _roundScore;
            Host.SaveSettings();
            Host.Fx.Popup(at, L.T("NEW BEST ROUND!"), Gold, 40, 2.2, L.F("{0} points", _roundScore));
            Host.Fx.Burst(at, Confetti, 40, 520, 700, 7, 1.1);
            Host.Sound.Play("best", 0.8);
        }
        else
        {
            Host.Fx.Popup(at, L.T("ROUND OVER"), Colors.White, 36, 2.0, L.F("{0} points · best {1}", _roundScore, s.BestArchery));
            Host.Sound.Play("buzzer", 0.4);
        }
        Changed();
    }

    // ------------------------------------------------------------------ spawning

    void SpawnTarget()
    {
        var a = Host.Arena;
        var hud = Host.HudBounds.Inflate(new Thickness(70, 90));
        double minX, maxX;
        if (_face > 0) { minX = _bowPos.X + 420; maxX = a.Right - 70; }
        else { minX = a.Left + 70; maxX = _bowPos.X - 420; }
        if (maxX - minX < 150) { minX = a.Left + 70; maxX = a.Right - 70; }

        int bobbing = _targets.Count(t => t.BobAmp > 0);
        int maxBobbing = _round >= 4 ? 2 : _round >= 2 ? 1 : 0;

        for (int tries = 0; tries < 40; tries++)
        {
            var p = new Vec2(minX + Rng.NextDouble() * (maxX - minX), a.Top + 120 + Rng.NextDouble() * Math.Max(10, a.Height - 280));
            if (hud.Contains(p.ToPoint()) || (p - _bowPos).Length < 350) continue;
            if (_targets.Any(t => double.IsNaN(t.LeaveIn) && (t.Pos - p).Length < 200)) continue;

            int face = p.X > _bowPos.X ? -1 : 1; // the painted side looks at the bow
            var t = new Target { Sprite = MakeTarget(face), Pos = p, BaseY = p.Y, BobPhase = Rng.NextDouble() * 6 };
            if (bobbing < maxBobbing) t.BobAmp = 40 + Rng.NextDouble() * 30;
            t.Sprite.Set(p);
            t.Sprite.Scale = 0.01;
            _targetLayer.Children.Add(t.Sprite);
            _targets.Add(t);
            return;
        }
    }

    void SpawnBalloon()
    {
        var a = Host.Arena;
        double minX = _face > 0 ? Math.Max(a.Left + 60, _bowPos.X + 300) : a.Left + 60;
        double maxX = _face > 0 ? a.Right - 60 : Math.Min(a.Right - 60, _bowPos.X - 300);
        if (maxX <= minX) { minX = a.Left + 60; maxX = a.Right - 60; }
        bool gold = Rng.NextDouble() < 0.15;
        var b = new Balloon
        {
            Sprite = Art.Balloon(BalloonR, gold ? Gold : BalloonColors[Rng.Next(BalloonColors.Length)]),
            Pos = new Vec2(minX + Rng.NextDouble() * (maxX - minX), a.Bottom - BalloonR * 3.6),
            Phase = Rng.NextDouble() * 6, Speed = 55 + Rng.NextDouble() * 35, Gold = gold,
        };
        b.Sprite.IsHitTestVisible = false;
        b.Sprite.Opacity = 0;
        b.Sprite.Set(b.Pos);
        _balloonLayer.Children.Add(b.Sprite);
        _balloons.Add(b);
    }

    // ------------------------------------------------------------------ input

    public override bool PointerDown(Vec2 p, bool right)
    {
        if ((p - _bowPos).Length > GrabR) return false;
        if (right)
        {
            _movingBow = true;
            _moveOffset = _bowPos - p;
            return true;
        }
        if (!DuelMayShoot()) return false;
        if (_roundOver) NewRound();
        if (_arrowsLeft <= 0) return false;
        _pulling = true;
        _pull = default;
        _active = 5;
        return true;
    }

    public override void PointerUp(Vec2 p)
    {
        if (_movingBow)
        {
            _movingBow = false;
            SaveBow();
            return;
        }
        if (!_pulling) return;
        _pulling = false;
        HideGuide();
        double len = _pull.Length;
        if (len >= MinPull) Fire(-_pull / len, len / MaxPull);
        UpdateBowVisual();
    }

    public override void Summon(Vec2 p)
    {
        _bowPos = p;
        ClampBow();
        SaveBow();
        UpdateBowVisual();
    }

    void SaveBow()
    {
        Host.Settings.BowX = _bowPos.X;
        Host.Settings.BowY = _bowPos.Y;
        Host.SaveSettings();
    }

    Vec2 LaunchPoint(Vec2 dir, double power) => _bowPos + dir * (ArrowLen - 4 - power * MaxPull * 0.55);
    static double LaunchSpeed(double power) => 520 + 2080 * power;

    void Fire(Vec2 dir, double power)
    {
        DuelFired();
        var ar = new Arrow { Sprite = MakeArrowSprite(), Tip = LaunchPoint(dir, power), Vel = dir * LaunchSpeed(power) };
        ar.Angle = Math.Atan2(dir.Y, dir.X) * 180 / Math.PI;
        ar.Sprite.Set(ar.Tip, ar.Angle);
        _arrowLayer.Children.Add(ar.Sprite);
        _arrows.Add(ar);

        var stuck = _arrows.Where(x => !x.Flying && x.In == null).ToList();
        for (int i = 0; i < stuck.Count - 20; i++) RemoveArrow(stuck[i]);

        _arrowsLeft--;
        _active = 5;
        Host.Sound.Play("twang", 0.5 + 0.5 * power);
        Host.Sound.Play("whoosh", 0.35 * power);
        if (_balloons.Count < 2 && Rng.NextDouble() < 0.35) SpawnBalloon();
        Changed();
    }

    // ------------------------------------------------------------------ simulation

    public override bool Update(double dt)
    {
        _time += dt;
        _active = Math.Max(0, _active - dt);

        if (_movingBow)
        {
            _bowPos = Host.Pointer + _moveOffset;
            ClampBow();
        }
        if (_pulling)
        {
            var pull = Host.Pointer - _bowPos;
            if (pull.Length > MaxPull) pull *= MaxPull / pull.Length;
            _pull = pull;
            _active = 5;
            UpdateGuide();
        }

        _acc += dt;
        while (_acc >= Step)
        {
            _acc -= Step;
            SimStep(Step);
        }

        bool busy = _pulling || _movingBow || _active > 0 || _balloons.Count > 0 || _roundEndTimer >= 0;
        busy |= UpdateTargets(dt);
        busy |= UpdateBalloons(dt);
        busy |= UpdateArrows(dt);

        if (_roundEndTimer >= 0 && (_roundEndTimer -= dt) < 0) EndRound();

        UpdateBowVisual();
        DuelUpdate(dt);
        busy |= Anims.Update(dt);
        return busy || DuelOn;
    }

    void SimStep(double h)
    {
        var arena = Host.Arena;
        for (int i = _arrows.Count - 1; i >= 0; i--)
        {
            var ar = _arrows[i];
            if (!ar.Flying) continue;

            var prev = ar.Tip;
            ar.Vel.Y += Gravity * h;
            ar.Vel.X += _wind * h;
            ar.Tip += ar.Vel * h;

            for (int b = _balloons.Count - 1; b >= 0; b--)
                if (SegmentDistance(prev, ar.Tip, _balloons[b].Pos) < BalloonR * 1.15) Pop(_balloons[b]);

            Target? hitTarget = null;
            double hitS = 2;
            foreach (var t in _targets)
            {
                if (!double.IsNaN(t.LeaveIn) || t.Age < 0.2) continue;
                double d0 = prev.X - t.Pos.X, d1 = ar.Tip.X - t.Pos.X;
                if (d0 * d1 > 0 || d0 == d1) continue;
                double s = d0 / (d0 - d1);
                double y = prev.Y + s * (ar.Tip.Y - prev.Y);
                if (Math.Abs(y - t.Pos.Y) <= TargetH / 2 && s < hitS)
                {
                    hitS = s;
                    hitTarget = t;
                }
            }

            double platS = 2;
            bool onPlat = Host.Platforms.FindCrossing(prev, ar.Tip, out var plat, out var platAt);
            if (onPlat) platS = (platAt - prev).Length / Math.Max(1e-6, (ar.Tip - prev).Length);

            if (hitTarget != null && hitS <= platS)
                StickInTarget(ar, hitTarget, prev + (ar.Tip - prev) * hitS);
            else if (onPlat)
                StickAt(ar, platAt, plat.Hwnd);
            else if (ar.Tip.Y >= arena.Bottom)
                StickAt(ar, prev + (ar.Tip - prev) * ((arena.Bottom - prev.Y) / (ar.Tip.Y - prev.Y)), IntPtr.Zero);
            else if (CrossesBox(prev, ar.Tip, arena, out var wallAt))
                StickAt(ar, wallAt, IntPtr.Zero); // closed box: arrows thunk into the screen edges
            else if (ar.Tip.X < arena.Left - 60 || ar.Tip.X > arena.Right + 60 || ar.Tip.Y < arena.Top - 60)
            {
                RemoveArrow(ar);
                CheckRoundEnd();
            }
        }
    }

    void StickInTarget(Arrow ar, Target t, Vec2 hit)
    {
        var dir = ar.Vel.Normalized();
        ar.Flying = false;
        ar.Tip = hit + dir * 10;
        ar.In = t;
        ar.InOffset = ar.Tip - t.Pos;

        double d = Math.Abs(hit.Y - t.Pos.Y) / (TargetH / 2);
        int pts = d <= 0.12 ? 10 : d <= 0.32 ? 8 : d <= 0.52 ? 6 : d <= 0.76 ? 4 : 2;
        if (!_roundOver) _roundScore += pts;

        var color = pts switch
        {
            10 => Gold,
            8 => Color.FromRgb(255, 110, 100),
            6 => Color.FromRgb(110, 170, 255),
            4 => Color.FromRgb(200, 205, 215),
            _ => Colors.White,
        };
        Host.Fx.Popup(hit - new Vec2(0, 44), $"+{pts}", color, pts == 10 ? 40 : 30, 1.1, pts == 10 ? L.T("BULLSEYE!") : null);
        Host.Sound.Play("thunk", 0.9);
        if (pts == 10)
        {
            Host.Sound.Play("score", 0.7);
            Host.Fx.Burst(hit, Confetti, 24, 420, 700, 6, 0.9);
            Host.Stats.Add("archery.bullseyes");
        }
        t.Flash = 1;
        t.LeaveIn = 1.1;
        Changed();
        CheckRoundEnd();
    }

    void StickAt(Arrow ar, Vec2 at, IntPtr hwnd)
    {
        var dir = ar.Vel.Normalized();
        ar.Flying = false;
        ar.Tip = at + dir * 8;
        ar.Hwnd = hwnd;
        ar.SeenGen = Host.Platforms.Generation;
        Host.Sound.Play("thunk", 0.35, 1.3);
        CheckRoundEnd();
    }

    void Pop(Balloon b)
    {
        int pts = b.Gold ? 15 : 5;
        if (!_roundOver) _roundScore += pts;
        Host.Fx.Popup(b.Pos - new Vec2(0, 36), $"+{pts}", b.Gold ? Gold : Colors.White, b.Gold ? 36 : 28, 1.0, b.Gold ? L.T("GOLDEN!") : L.T("POP!"));
        Host.Fx.Burst(b.Pos, b.Gold ? Confetti : PopBurst, 16, 380, 500, 5, 0.5);
        Host.Sound.Play("pop", 0.9);
        Host.Stats.Add("archery.balloons");
        _balloonLayer.Children.Remove(b.Sprite);
        _balloons.Remove(b);
        Changed();
    }

    void CheckRoundEnd()
    {
        if (!_roundOver && _arrowsLeft == 0 && _roundEndTimer < 0 && !_arrows.Any(a => a.Flying))
            _roundEndTimer = 0.8;
    }

    void RemoveArrow(Arrow ar)
    {
        _arrowLayer.Children.Remove(ar.Sprite);
        _arrows.Remove(ar);
    }

    bool UpdateTargets(double dt)
    {
        bool busy = false;
        for (int i = _targets.Count - 1; i >= 0; i--)
        {
            var t = _targets[i];
            t.Age += dt;
            if (t.BobAmp > 0 && _active > 0)
            {
                t.BobT += dt;
                t.Pos.Y = t.BaseY + Math.Sin(t.BobPhase + t.BobT * 1.4) * t.BobAmp;
            }
            t.Flash = Math.Max(0, t.Flash - dt * 4);
            double appear = Math.Min(1, t.Age / 0.25);
            double scale = (1 - (1 - appear) * (1 - appear)) * (1 + 0.1 * t.Flash);
            double opacity = 1;

            if (!double.IsNaN(t.LeaveIn))
            {
                t.LeaveIn -= dt;
                if (t.LeaveIn < 0) opacity = Math.Max(0, 1 + t.LeaveIn / 0.3);
                if (t.LeaveIn <= -0.3)
                {
                    foreach (var ar in _arrows.Where(x => x.In == t).ToList()) RemoveArrow(ar);
                    _targetLayer.Children.Remove(t.Sprite);
                    _targets.RemoveAt(i);
                    SpawnTarget();
                    busy = true;
                    continue;
                }
                busy = true;
            }
            busy |= t.Age < 0.3 || t.Flash > 0;
            t.Sprite.Set(t.Pos);
            t.Sprite.Scale = Math.Max(0.01, scale);
            t.Sprite.Opacity = opacity;
        }
        return busy;
    }

    bool UpdateBalloons(double dt)
    {
        var a = Host.Arena;
        for (int i = _balloons.Count - 1; i >= 0; i--)
        {
            var b = _balloons[i];
            double sway = Math.Sin(_time * 1.3 + b.Phase);
            b.Age += dt;
            b.Pos.Y -= b.Speed * dt;
            b.Pos.X = Clamp(b.Pos.X + (sway * 14 + _wind * 0.08) * dt, a.Left + BalloonR, a.Right - BalloonR);
            // closed box: balloons bump into the top of the screen, linger, then fade away
            double ceiling = a.Top + BalloonR * 1.2;
            if (b.Pos.Y <= ceiling)
            {
                b.Pos.Y = ceiling;
                b.AtCeiling += dt;
            }
            b.Sprite.Opacity = Math.Min(Math.Min(1, b.Age / 0.4), Math.Max(0, 1 - (b.AtCeiling - 2) / 0.6));
            b.Sprite.Set(b.Pos, sway * 6);
            if (b.AtCeiling >= 2.6)
            {
                _balloonLayer.Children.Remove(b.Sprite);
                _balloons.RemoveAt(i);
            }
        }
        return _balloons.Count > 0;
    }

    bool UpdateArrows(double dt)
    {
        bool busy = false;
        var plats = Host.Platforms;
        for (int i = _arrows.Count - 1; i >= 0; i--)
        {
            var ar = _arrows[i];
            if (ar.Flying)
            {
                ar.Angle = Math.Atan2(ar.Vel.Y, ar.Vel.X) * 180 / Math.PI;
                ar.Sprite.Set(ar.Tip, ar.Angle);
                busy = true;
            }
            else if (ar.In != null)
            {
                ar.Sprite.Set(ar.In.Pos + ar.InOffset, ar.Angle);
                ar.Sprite.Opacity = ar.In.Sprite.Opacity;
            }
            else
            {
                if (ar.Hwnd != IntPtr.Zero && ar.SeenGen != plats.Generation)
                {
                    ar.SeenGen = plats.Generation;
                    ar.Tip += plats.DeltaOf(ar.Hwnd); // ride along with the window
                }
                ar.StuckAge += dt;
                ar.Sprite.Set(ar.Tip, ar.Angle);
                if (ar.StuckAge > 5) ar.Sprite.Opacity = Math.Max(0, 6 - ar.StuckAge);
                if (ar.StuckAge >= 6) RemoveArrow(ar);
                else busy = true;
            }
        }
        return busy;
    }

    // ------------------------------------------------------------------ visuals

    void UpdateBowVisual()
    {
        double len = _pulling ? _pull.Length : 0;
        bool aiming = _pulling && len >= MinPull;
        double angle = aiming ? Math.Atan2(-_pull.Y, -_pull.X) * 180 / Math.PI : (_face > 0 ? -10 : 190);
        _bow.Set(_bowPos, angle);

        double nockX = -4 - (aiming ? len * 0.55 : 0);
        if (nockX != _lastNockX)
        {
            _lastNockX = nockX;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(-4, -64), false);
                ctx.LineTo(new Point(nockX, 0));
                ctx.LineTo(new Point(-4, 64));
                ctx.EndFigure(false);
            }
            _string.Data = g;
            _nockTr.X = nockX + ArrowLen;
        }
        _nock.IsVisible = _pulling || (!_roundOver && _arrowsLeft > 0);
    }

    void UpdateGuide()
    {
        double len = _pull.Length;
        if (len < MinPull)
        {
            HideGuide();
            return;
        }
        var dir = -_pull / len;
        double power = len / MaxPull;
        var start = LaunchPoint(dir, power);
        var v = dir * LaunchSpeed(power);
        for (int i = 0; i < _dots.Length; i++)
        {
            double t = (i + 1) * 0.035;
            var p = start + v * t + new Vec2(_wind, Gravity) * (0.5 * t * t);
            var tr = (TranslateTransform)_dots[i].RenderTransform!;
            tr.X = p.X - 3;
            tr.Y = p.Y - 3;
            _dots[i].Opacity = 0.75 * (1 - (double)i / _dots.Length);
            _dots[i].IsVisible = true;
        }
    }

    void HideGuide()
    {
        foreach (var d in _dots) d.IsVisible = false;
    }

    static Canvas ArrowShape()
    {
        var c = new Canvas();
        c.Children.Add(Art.PathOf($"M{Art.F(-ArrowLen)},0 L-10,0", null, Art.Brush("#D8B58A"), 2.6));
        c.Children.Add(Art.PathOf("M0,0 L-15,-5 L-11,0 L-15,5 Z", Art.Brush("#C9D1DC"), Art.Brush("#59616B"), 1));
        double tail = -ArrowLen;
        c.Children.Add(Art.PathOf(
            $"M{Art.F(tail + 2)},0 L{Art.F(tail - 6)},-7 L{Art.F(tail + 15)},-1 Z M{Art.F(tail + 2)},0 L{Art.F(tail - 6)},7 L{Art.F(tail + 15)},1 Z",
            Art.Brush("#E0413A")));
        return c;
    }

    static Sprite MakeArrowSprite()
    {
        var s = new Sprite { IsHitTestVisible = false };
        s.Rotor.Children.Add(ArrowShape());
        return s;
    }

    static Sprite MakeTarget(int face)
    {
        var s = new Sprite { IsHitTestVisible = false };
        s.Rotor.Children.Add(Art.At(new Ellipse { Width = TargetW + 8, Height = TargetH + 8, Fill = Art.Brush("#6B4020") },
            -(TargetW + 8) / 2 - face * 6, -(TargetH + 8) / 2));
        foreach (var (f, color) in new[] { (1.0, "#F4F4F4"), (0.76, "#262A33"), (0.52, "#2B6CD8"), (0.32, "#E0413A"), (0.12, "#FFD23F") })
        {
            double w = TargetW * Math.Max(f, 0.3), h = TargetH * f;
            s.Rotor.Children.Add(Art.At(new Ellipse { Width = w, Height = h, Fill = Art.Brush(color), Stroke = Art.Brush(90, 0, 0, 0), StrokeThickness = 0.8 },
                -w / 2, -h / 2));
        }
        return s;
    }

    /// <summary>Where the segment a→b leaves the box through its left, right or top edge.</summary>
    static bool CrossesBox(Vec2 a, Vec2 b, Rect box, out Vec2 at)
    {
        double s = 2;
        if (b.X < box.Left && a.X >= box.Left) s = Math.Min(s, (box.Left - a.X) / (b.X - a.X));
        if (b.X > box.Right && a.X <= box.Right) s = Math.Min(s, (box.Right - a.X) / (b.X - a.X));
        if (b.Y < box.Top && a.Y >= box.Top) s = Math.Min(s, (box.Top - a.Y) / (b.Y - a.Y));
        at = a + (b - a) * Math.Min(s, 1);
        return s <= 1;
    }

    static double SegmentDistance(Vec2 a, Vec2 b, Vec2 p)
    {
        Vec2 ab = b - a;
        double len2 = ab.LengthSquared;
        double t = len2 < 1e-9 ? 0 : Math.Clamp(Vec2.Dot(p - a, ab) / len2, 0, 1);
        return (p - (a + ab * t)).Length;
    }

    public override void DemoTick()
    {
        if (_pulling || _arrows.Any(a => a.Flying) || _roundEndTimer >= 0) return;
        if (DuelOn && (_match.Over || !_match.MyTurn || _shotOpen))
        {
            if (_match.Over && DuelHost) HostNewRound();
            return;
        }
        if (_roundOver) NewRound();
        var target = _targets.FirstOrDefault(t => double.IsNaN(t.LeaveIn) && t.Age > 0.3);
        if (target == null || _arrowsLeft <= 0) return;

        double T = 0.55 + Rng.NextDouble() * 0.2;
        var aimAt = target.Pos + new Vec2(0, (Rng.NextDouble() - 0.5) * TargetH * 0.6);
        var v = new Vec2((aimAt.X - _bowPos.X) / T - 0.5 * _wind * T, (aimAt.Y - _bowPos.Y - 0.5 * Gravity * T * T) / T);
        double power = Math.Clamp((v.Length - 520) / 2080, 0.05, 1);
        _active = 5;
        Fire(v.Normalized(), power);
    }
}
