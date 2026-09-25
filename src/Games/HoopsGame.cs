using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>Grab the basketball, flick it, sink it. Streaks set the ball on fire.</summary>
public sealed partial class HoopsGame : MiniGame
{
    const double BallR = 26, RimLen = 112, BoardUp = 105, BoardDown = 25, NetDepth = 62, LipR = 4.5;
    const double Step = 1.0 / 240;

    static readonly Color[] Confetti =
    {
        Color.FromRgb(255, 209, 102), Color.FromRgb(239, 71, 111), Color.FromRgb(6, 214, 160),
        Color.FromRgb(17, 138, 178), Color.FromRgb(255, 255, 255),
    };
    static readonly Color[] Flames = { Color.FromRgb(255, 214, 10), Color.FromRgb(255, 140, 20), Color.FromRgb(240, 60, 20) };

    readonly BallBody _ball = new(BallR) { Gravity = 2000, Restitution = 0.7 };
    Sprite _ballSprite = ThemedBall();
    readonly Canvas _back = new(), _front = new();
    readonly TranslateTransform _backTr = new(), _frontTr = new();
    readonly ScaleTransform _backSc = new(), _frontSc = new();
    readonly Path _net = new() { Stroke = Art.Brush(225, 245, 247, 250), StrokeThickness = 1.6, StrokeJoin = PenLineJoin.Round };
    readonly Ellipse _threeLine = new()
    {
        Stroke = Art.Brush(80, 255, 209, 102), StrokeThickness = 2, StrokeDashArray = new AvaloniaList<double> { 5, 7 },
        IsVisible = false, IsHitTestVisible = false,
    };
    readonly List<(double t, Vec2 p)> _trail = new();
    readonly Dictionary<string, double> _lastSound = new();

    double _boardX, _rimY, _dir = -1, _bob, _time, _acc, _flameTimer, _hoopPulse = 1;
    double _netSwing, _netSwingV, _netStretch, _netStretchV;
    bool _placed, _holding, _draggingHoop, _throwLive, _scored, _touched, _bestAnnounced;
    Vec2 _grabOffset;
    Vec2 _releasePos;
    int _score, _streak;

    public HoopsGame(IGameHost host) : base(host)
    {
        _back.RenderTransformOrigin = RelativePoint.TopLeft;
        _front.RenderTransformOrigin = RelativePoint.TopLeft;
        _back.RenderTransform = new TransformGroup { Children = { _backSc, _backTr } };
        _front.RenderTransform = new TransformGroup { Children = { _frontSc, _frontTr } };

        // Local hoop space: board face at x=0, rim extends toward +x, rim height at y=0.
        _back.Children.Add(Art.PathOf("M-11,-64 L-40,-64 L-40,-6 M-40,-50 L-11,-22", null, Art.Brush("#7C8591"), 4));
        var boardFill = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        };
        boardFill.GradientStops.Add(new GradientStop(Color.FromRgb(0xF7, 0xF9, 0xFC), 0));
        boardFill.GradientStops.Add(new GradientStop(Color.FromRgb(0xC2, 0xCB, 0xD6), 1));
        _back.Children.Add(Art.At(new Rectangle
        {
            Width = 11, Height = BoardUp + BoardDown, RadiusX = 2, RadiusY = 2,
            Fill = boardFill, Stroke = Art.Brush("#6B7480"), StrokeThickness = 1,
        }, -11, -BoardUp));
        _back.Children.Add(Art.At(new Rectangle { Width = 3, Height = 46, Fill = Art.Brush("#E03A3A") }, -3, -46));
        _back.Children.Add(Art.At(new Rectangle { Width = 10, Height = 7, Fill = Art.Brush("#9C3A0C") }, 0, -3.5));
        _back.Children.Add(Art.PathOf($"M6,0 A{Art.F((RimLen - 6) / 2)},8 0 0 1 {Art.F(RimLen)},0", null, Art.Brush("#B8420E"), 4));

        _front.Children.Add(_net);
        _front.Children.Add(Art.PathOf($"M6,0 A{Art.F((RimLen - 6) / 2)},8 0 0 0 {Art.F(RimLen)},0", null, Art.Brush("#F26A1B"), 4.5));
        _front.Children.Add(Art.Circle(RimLen, 0, 3.2, Art.Brush("#FF8A3D")));

        Layer.Children.Add(_threeLine);
        Layer.Children.Add(_back);
        Layer.Children.Add(_ballSprite);
        Layer.Children.Add(_front);
        HorseLayer();
        UpdateNet();
    }

    public override string Id => "hoops";
    public override string Title => "Hoops";
    public override bool SupportsLan => true;
    public override Sprite CreateIcon() => Art.Basketball(9);

    static Sprite ThemedBall() => Art.Basketball(BallR, Themes.Current.Ball, Themes.Current.BallSeam);

    public override void ThemeChanged()
    {
        _ballSprite = Swap(_ballSprite, ThemedBall());
        _rivalBall = Swap(_rivalBall, ThemedBall());
        _ballSprite.Set(_ball.Pos, _ball.Angle);
    }

    /// <summary>Puts <paramref name="next"/> where <paramref name="old"/> was in the layer; the game positions it on its next frame.</summary>
    Sprite Swap(Sprite old, Sprite next)
    {
        next.IsHitTestVisible = old.IsHitTestVisible;
        next.IsVisible = old.IsVisible;
        Layer.Children[Layer.Children.IndexOf(old)] = next;
        return next;
    }

    public override HudInfo Hud => HorseOn ? HorseHud : new(
        _score.ToString(),
        _streak >= 3 ? L.F("Streak {0} · ON FIRE ×2", _streak) : _streak > 0 ? L.F("Streak {0} · keep going!", _streak) : L.T("Drag the ball, flick it into the hoop"),
        L.F("Best streak {0}", Host.Settings.BestHoopsStreak));

    double RimY => _rimY + _bob;
    Vec2 RimCenter => new(_boardX + _dir * (RimLen + 6) / 2, RimY);
    double ThreeDist => Math.Max(520, Host.Arena.Width * 0.34);

    public override void Layout()
    {
        var a = Host.Arena;
        _boardX = Host.Settings.HoopX ?? a.Right - 70;
        _rimY = Host.Settings.HoopY ?? a.Top + a.Height * 0.4;
        ClampHoop();
        HorseCheckSession();
        if (!_placed || _ball.Pos.X < a.Left || _ball.Pos.X > a.Right || _ball.Pos.Y > a.Bottom)
        {
            _ball.Place(new Vec2(a.Left + a.Width * 0.3, a.Bottom - BallR));
            _placed = true;
        }
        _ballSprite.Set(_ball.Pos, _ball.Angle);
        Host.HudChanged();
    }

    public override void Deactivate()
    {
        _holding = _draggingHoop = false;
        _threeLine.IsVisible = false;
    }

    void ClampHoop()
    {
        var a = Host.Arena;
        _boardX = Clamp(_boardX, a.Left + 60, a.Right - 60);
        _rimY = Clamp(_rimY, a.Top + BoardUp + 20, a.Bottom - NetDepth - 80);
        _dir = _boardX > a.Left + a.Width / 2 ? -1 : 1;
        SetHoopPulse(_hoopPulse);
        _backTr.X = _frontTr.X = _boardX;
        _backTr.Y = _frontTr.Y = RimY;
    }

    /// <summary>The hoop's size: mirrored to face the court, and swollen a little by the turn cue.</summary>
    void SetHoopPulse(double pulse)
    {
        _hoopPulse = pulse;
        _backSc.ScaleX = _frontSc.ScaleX = _dir * pulse;
        _backSc.ScaleY = _frontSc.ScaleY = pulse;
    }

    Rect HoopRect()
    {
        double left = _dir > 0 ? _boardX - 50 : _boardX - RimLen - 8;
        return new Rect(left, RimY - BoardUp - 14, RimLen + 58, BoardUp + NetDepth + 22);
    }

    public override void CollectHitShapes(List<HitShape> into)
    {
        into.Add(HitShape.Circle(_ball.Pos, BallR + 14));
        into.Add(HitShape.Box(HoopRect()));
    }

    // ------------------------------------------------------------------ input

    public override bool PointerDown(Vec2 p, bool right)
    {
        if ((p - _ball.Pos).Length <= BallR + 14)
        {
            if (!HorseMayGrab()) return false;
            Grab(p);
            return true;
        }
        if (HoopRect().Contains(p.ToPoint()))
        {
            _draggingHoop = true;
            _grabOffset = new Vec2(_boardX, _rimY) - p;
            return true;
        }
        return false;
    }

    void Grab(Vec2 p)
    {
        if (_throwLive && !_scored) BreakStreak();
        _throwLive = false;
        _holding = true;
        _grabOffset = _ball.Pos - p;
        _trail.Clear();
        _ball.Place(_ball.Pos);
        var c = RimCenter;
        _threeLine.Width = _threeLine.Height = ThreeDist * 2;
        Canvas.SetLeft(_threeLine, c.X - ThreeDist);
        Canvas.SetTop(_threeLine, c.Y - ThreeDist);
        _threeLine.IsVisible = true;
    }

    void BreakStreak()
    {
        if (_streak >= 3) Host.Fx.Popup(_ball.Pos - new Vec2(0, 60), L.T("streak over"), Color.FromRgb(200, 210, 225), 20, 1.0);
        _streak = 0;
        _bestAnnounced = false;
        Host.HudChanged();
    }

    public override void PointerUp(Vec2 p)
    {
        if (_draggingHoop)
        {
            _draggingHoop = false;
            Host.Settings.HoopX = _boardX;
            Host.Settings.HoopY = _rimY;
            Host.SaveSettings();
            return;
        }
        if (!_holding) return;
        _holding = false;
        _threeLine.IsVisible = false;
        Release(ThrowVelocity());
    }

    void Release(Vec2 v)
    {
        _ball.Place(_ball.Pos, v);
        _ball.Spin = -v.X * 0.25;
        _releasePos = _ball.Pos;
        _petThrows++;
        _throwLive = v.Length > 150;
        if (_throwLive) HorseReleased(_releasePos);
        _scored = false;
        _touched = false;
        if (v.Length > 700) Host.Sound.Play("whoosh", Math.Min(1, v.Length / 3000) * 0.5);
    }

    Vec2 ThrowVelocity()
    {
        if (_trail.Count < 2) return default;
        var last = _trail[^1];
        var first = _trail[0];
        foreach (var s in _trail)
        {
            if (last.t - s.t <= 0.07)
            {
                first = s;
                break;
            }
        }
        double dt = last.t - first.t;
        if (dt < 0.008) return default;
        Vec2 v = (last.p - first.p) / dt;
        return v.Length > 3800 ? v * (3800 / v.Length) : v;
    }

    public override void Summon(Vec2 p)
    {
        if (_holding) return;
        if (_throwLive && !_scored) BreakStreak();
        _throwLive = false;
        var a = Host.Arena;
        _ball.Place(new Vec2(Clamp(p.X, a.Left + BallR, a.Right - BallR), Clamp(p.Y, a.Top + BallR, a.Bottom - BallR)));
    }

    // ------------------------------------------------------------------ simulation

    public override bool Update(double dt)
    {
        _time += dt;
        bool busy = false;

        if (_draggingHoop)
        {
            var target = Host.Pointer + _grabOffset;
            _boardX = target.X;
            _rimY = target.Y;
            ClampHoop();
            busy = true;
        }

        if (_holding)
        {
            var a = Host.Arena;
            var target = Host.Pointer + _grabOffset;
            target.X = Clamp(target.X, a.Left + BallR, a.Right - BallR);
            target.Y = Clamp(target.Y, a.Top + BallR, a.Bottom - BallR);
            _ball.Pos = target;
            _ball.Vel = default;
            _trail.Add((_time, target));
            while (_trail.Count > 0 && _time - _trail[0].t > 0.15) _trail.RemoveAt(0);
            busy = true;
        }
        else
        {
            if (_streak >= 5 && !_ball.Asleep)
            {
                _bob = Math.Sin(_time * 1.3) * 55; // hoop on the move
                ClampHoop();
            }
            else if (_streak < 5 && _bob != 0)
            {
                _bob *= Math.Max(0, 1 - dt * 4);
                if (Math.Abs(_bob) < 0.5) _bob = 0;
                ClampHoop();
                busy = true;
            }

            _acc += dt;
            while (_acc >= Step)
            {
                _acc -= Step;
                SimStep(Step);
            }
            busy |= !_ball.Asleep;

            if (_streak >= 3 && !_ball.Asleep && (_flameTimer -= dt) <= 0)
            {
                _flameTimer = 1 / 45.0;
                var r = Random.Shared;
                Host.Fx.Spawn(_ball.Pos + new Vec2(r.NextDouble() * 20 - 10, r.NextDouble() * 20 - 10),
                    new Vec2(r.NextDouble() * 60 - 30 - _ball.Vel.X * 0.08, -60 - r.NextDouble() * 90 - _ball.Vel.Y * 0.08),
                    Flames[r.Next(Flames.Length)], 9 + r.NextDouble() * 10, 0.3 + r.NextDouble() * 0.2, -250);
            }
        }

        busy |= AnimateNet(dt);
        _ballSprite.Set(_ball.Pos, _ball.Angle);
        HorseUpdate(dt);
        busy |= Anims.Update(dt);
        return busy || HorseOn;
    }

    void SimStep(double h)
    {
        double prevY = _ball.Pos.Y;
        var imp = new Impacts();
        _ball.Step(h, Host, ref imp);
        if (_ball.Asleep) return;

        double face = _boardX, rimY = RimY;
        double boardCx = face - _dir * 5.5;
        double hitBoard = _ball.CollideSegment(new Vec2(boardCx, rimY - BoardUp + 5), new Vec2(boardCx, rimY + BoardDown - 5), 5.5, 0.62);
        double hitLip = _ball.CollidePoint(new Vec2(face + _dir * RimLen, rimY), LipR, 0.5);
        double hitBack = _ball.CollidePoint(new Vec2(face + _dir * 6, rimY), LipR, 0.5);

        if (hitBoard > 40)
        {
            _touched = true;
            PlayThrottled("board", Math.Min(1, hitBoard / 1200));
        }
        double rim = Math.Max(hitLip, hitBack);
        if (rim > 40)
        {
            _touched = true;
            PlayThrottled("rim", Math.Min(1, rim / 900));
            _netSwingV += 60 * Math.Min(1, rim / 600);
        }

        double lx = (_ball.Pos.X - face) * _dir;
        if (_throwLive && !_scored && prevY < rimY && _ball.Pos.Y >= rimY && _ball.Vel.Y > 0 && lx > 6 + LipR && lx < RimLen - LipR)
            Score();

        if (lx > 0 && lx < RimLen && _ball.Pos.Y > rimY && _ball.Pos.Y < rimY + NetDepth)
        {
            _ball.Vel *= 1 - 2.2 * h; // the net catches the ball a little
            _ball.Vel.X += (face + _dir * RimLen / 2 - _ball.Pos.X) * 6 * h;
        }

        if (imp.Floor > 160) PlayThrottled("bounce", Math.Min(1, imp.Floor / 1500));
        if (imp.Wall > 160) PlayThrottled("bounce", Math.Min(0.7, imp.Wall / 2000), 1.25);
    }

    void Score()
    {
        _scored = true;
        HorseScored();
        var c = RimCenter;
        double dist = (_releasePos - c).Length;
        bool dunk = dist < 190, three = !dunk && dist >= ThreeDist;
        bool swish = !_touched && !dunk;
        int pts = (dunk ? 1 : three ? 3 : 2) + (swish ? 1 : 0);

        var s = Host.Settings;
        _streak++;
        int mult = _streak >= 3 ? 2 : 1;
        int gained = pts * mult;
        _score += gained;
        Host.Stats.Add("hoops.baskets");
        if (swish) Host.Stats.Add("hoops.swishes");
        Host.Stats.Max("hoops.streak", _streak);
        bool beatBest = _streak > s.BestHoopsStreak;
        if (beatBest) s.BestHoopsStreak = _streak;
        if (_score > s.BestHoopsScore) s.BestHoopsScore = _score;
        Host.SaveSettings();

        string label = dunk ? L.T("DUNK!") : three ? (swish ? L.T("SWISH THREE!") : L.T("THREE POINTER!")) : swish ? L.T("SWISH!") : L.T("NICE SHOT!");
        Host.Fx.Popup(new Vec2(c.X, RimY - 80), $"+{gained}", mult > 1 ? Color.FromRgb(255, 120, 40) : Color.FromRgb(255, 209, 102), 42, 1.3, label);
        Host.Fx.Burst(new Vec2(c.X, RimY + 24), Confetti, swish ? 28 : 16, 420, 700, 6, 0.9);
        Host.Sound.Play("swish", 0.9);
        Host.Sound.Play("score", 0.55);
        _netStretchV += 7;

        if (_streak == 3)
        {
            Host.Fx.Popup(new Vec2(c.X, RimY - 160), L.T("ON FIRE!"), Color.FromRgb(255, 90, 40), 34, 1.6, L.T("points ×2"));
            Host.Sound.Play("fire", 0.8);
        }
        else if (_streak == 5)
        {
            Host.Fx.Popup(new Vec2(c.X, RimY - 160), L.T("MOVING HOOP!"), Color.FromRgb(120, 200, 255), 30, 1.6);
        }
        if (beatBest && !_bestAnnounced && _streak >= 3)
        {
            _bestAnnounced = true;
            Host.Fx.Popup(new Vec2(c.X, RimY - 220), L.T("NEW BEST STREAK"), Color.FromRgb(6, 214, 160), 26, 1.8);
            Host.Sound.Play("best", 0.7);
        }
        Host.HudChanged();
    }

    // ------------------------------------------------------------------ visuals

    bool AnimateNet(double dt)
    {
        bool settled = Math.Abs(_netSwing) < 0.05 && Math.Abs(_netSwingV) < 0.5 &&
                       Math.Abs(_netStretch) < 0.002 && Math.Abs(_netStretchV) < 0.02;
        if (settled)
        {
            if (_netSwing != 0 || _netStretch != 0)
            {
                _netSwing = _netStretch = _netSwingV = _netStretchV = 0;
                UpdateNet();
            }
            return false;
        }
        _netSwingV += (-_netSwing * 90 - _netSwingV * 6) * dt;
        _netSwing += _netSwingV * dt;
        _netStretchV += (-_netStretch * 120 - _netStretchV * 9) * dt;
        _netStretch += _netStretchV * dt;
        UpdateNet();
        return true;
    }

    void UpdateNet()
    {
        const int strings = 6, rows = 4;
        double depth = NetDepth * (1 + _netStretch);
        var pts = new Point[strings, rows + 1];
        for (int i = 0; i < strings; i++)
        {
            double u = (double)i / (strings - 1);
            double tx = 8 + (RimLen - 12) * u;
            double ty = 5 * Math.Sin(Math.PI * u);
            double bx = RimLen / 2 + (tx - RimLen / 2) * 0.5;
            for (int k = 0; k <= rows; k++)
            {
                double f = (double)k / rows;
                pts[i, k] = new Point(tx + (bx - tx) * f + _netSwing * f * f, ty + (depth - ty) * f);
            }
        }

        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            for (int i = 0; i < strings; i++)
            {
                ctx.BeginFigure(pts[i, 0], false);
                for (int k = 1; k <= rows; k++) ctx.LineTo(pts[i, k]);
                ctx.EndFigure(false);
            }
            for (int i = 0; i < strings - 1; i++)
            {
                for (int k = 0; k < rows; k++)
                {
                    ctx.BeginFigure(pts[i, k], false);
                    ctx.LineTo(pts[i + 1, k + 1]);
                    ctx.EndFigure(false);
                    ctx.BeginFigure(pts[i + 1, k], false);
                    ctx.LineTo(pts[i, k + 1]);
                    ctx.EndFigure(false);
                }
            }
        }
        _net.Data = g;
    }

    void PlayThrottled(string name, double vol, double pitch = 1)
    {
        if (_lastSound.TryGetValue(name, out double t) && _time - t < 0.06) return;
        _lastSound[name] = _time;
        Host.Sound.Play(name, vol, pitch);
    }

    public override void DemoTick()
    {
        if (_holding || (!_ball.Asleep && !_ball.Grounded)) return;
        bool over = _horse.Now == HorseMatch.Phase.Over;
        if (HorseOn && !over && (!MyShot || _shotOpen)) return;
        if (HorseOn && over) HorseMayGrab(); // starts the rematch
        var a = Host.Arena;
        var r = Random.Shared;
        double fx = _dir < 0 ? 0.15 + r.NextDouble() * 0.35 : 0.5 + r.NextDouble() * 0.35;
        var from = HorseOn && Matching ? SpotPosition() : new Vec2(a.Left + a.Width * fx, a.Bottom - 250 - r.NextDouble() * 250);
        var target = RimCenter + new Vec2(r.NextDouble() * 24 - 12, 0);
        if (HorseOn && r.NextDouble() < 0.35) target.X += 90; // H-O-R-S-E demo: miss now and then so letters happen
        double T = 1.0 + r.NextDouble() * 0.25;
        var v = new Vec2((target.X - from.X) / T, (target.Y - from.Y - 0.5 * _ball.Gravity * T * T) / T) * 1.03;
        if (_throwLive && !_scored) BreakStreak();
        _ball.Place(from);
        Release(v);
    }
}
