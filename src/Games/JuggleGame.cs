using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>
/// Keepy-uppy: click the ball to kick it; don't let it touch the ground. A run, from the first kick to the drop,
/// is one round, so it can be raced against the computer or a co-worker.
/// </summary>
public sealed class JuggleGame : MiniGame
{
    const double R = 30, KickReach = 24, Step = 1.0 / 240, BaseGravity = 1500;

    /// <summary>What a decent run scores: about a dozen kicks and a star.</summary>
    public const int Baseline = 15;

    /// <summary>About how long such a run takes, in seconds.</summary>
    public const double RunSeconds = 20;

    static readonly Color Gold = Color.FromRgb(255, 209, 102);
    static readonly Color[] Dust = { Colors.White, Color.FromRgb(200, 210, 225) };
    static readonly Color[] GoldBurst = { Gold, Color.FromRgb(255, 240, 180), Color.FromRgb(255, 170, 40) };

    sealed class Star
    {
        public required Sprite Sprite;
        public required Control Glint;
        public required RotateTransform Turn;
        public Vec2 Pos;
        public double Age;
        public bool Landed; // popped in, twinkling on its own
    }

    readonly BallBody _ball = new(R) { Gravity = BaseGravity, Restitution = 0.55, AirDrag = 0.1 };
    readonly Sprite _sprite = new();        // where the ball is
    readonly Sprite _art = Art.SoccerBall(R); // the ball itself, rotating inside the squash
    readonly ScaleTransform _squash = new(1, 1);
    readonly List<Star> _stars = new();
    Anims.Tween? _squashTween;

    int _juggles, _runScore, _nextStarAt = 5;
    double _time, _acc, _lastKick = -1, _lastBounceSound;
    bool _placed;

    public JuggleGame(IGameHost host) : base(host)
    {
        var body = new Canvas { RenderTransformOrigin = RelativePoint.TopLeft, RenderTransform = _squash };
        body.Children.Add(_art);
        _sprite.Children.Add(body);
        Layer.Children.Add(_sprite);
    }

    public override string Id => "juggle";
    public override string Title => "Keepy-Uppy";
    public override Sprite CreateIcon() => Art.SoccerBall(9);

    public override HudInfo Hud => new(
        _runScore.ToString(),
        _juggles > 0 ? (_stars.Count > 0 ? L.F("Juggles {0} · grab the star!", _juggles) : L.F("Juggles {0} · don't let it drop", _juggles)) : L.T("Click the ball to kick it up"),
        L.F("Best {0}", Host.Settings.BestJuggle));

    public override void Layout()
    {
        var a = Host.Arena;
        if (!_placed || _ball.Pos.X < a.Left || _ball.Pos.X > a.Right || _ball.Pos.Y > a.Bottom)
        {
            _ball.Place(new Vec2(a.Left + a.Width * 0.5, a.Bottom - R));
            _placed = true;
        }
        DrawBall();
        Host.HudChanged();
    }

    // ------------------------------------------------------------------ race

    public override bool SupportsLan => true;
    public override (int Score, bool Active)? Race => (_runScore, _juggles > 0);
    public override int RaceBaseline => Baseline;
    public override int RaceBest => Host.Settings.BestJuggle;
    public override double RaceSeconds => RunSeconds;

    /// <summary>The rival started a run: serve the ball straight up from where it lies, as a click on it would.</summary>
    public override void StartRace()
    {
        if (_juggles > 0) return;
        _lastKick = -1;
        Kick(new Vec2(_ball.Pos.X, _ball.Pos.Y + R));
        Host.Wake();
    }

    // ------------------------------------------------------------------ input

    public override void CollectHitShapes(List<HitShape> into) => into.Add(HitShape.Circle(_ball.Pos, R + KickReach));

    public override bool PointerDown(Vec2 p, bool right)
    {
        if ((p - _ball.Pos).Length <= R + KickReach) Kick(p);
        return false;
    }

    /// <summary>
    /// The kick for a click <paramref name="dx"/> across the ball (-1 left edge … 1 right edge): the ball goes up,
    /// away from the click, keeping a little of its sideways speed <paramref name="vx"/>.
    /// </summary>
    public static Vec2 KickVelocity(double dx, double vx, double extraUp)
    {
        dx = Clamp(dx, -1, 1);
        double up = 980 + 120 * (1 - Math.Abs(dx)) + extraUp;
        return new Vec2(dx * 560 + vx * 0.2, -up);
    }

    void Kick(Vec2 p)
    {
        if (_time - _lastKick < 0.09) return;
        _lastKick = _time;

        double dx = (_ball.Pos.X - p.X) / (R + KickReach);
        _ball.Place(_ball.Pos, KickVelocity(dx, _ball.Vel.X, Rng.NextDouble() * 60));
        _ball.Spin = -Clamp(dx, -1, 1) * 720;

        if (_juggles == 0)
        {
            _runScore = 0;
            Host.RoundStarted();
        }
        _juggles++;
        _runScore++;
        Host.Stats.Max("juggle.run", _runScore);
        Host.ShareAction(_ball.Pos, 1);
        _ball.Gravity = Math.Min(2700, BaseGravity + _juggles * 28);

        Squash();
        Host.Sound.Play("kick", 0.8, 0.9 + Rng.NextDouble() * 0.2);
        Host.Fx.Burst(new Vec2(_ball.Pos.X, _ball.Pos.Y + R), Dust, 6, 180, 500, 4, 0.35);
        if (_juggles % 10 == 0)
        {
            Host.Fx.Popup(_ball.Pos - new Vec2(0, 80), $"{_juggles}!", Gold, 34, 1.0);
            Host.Sound.Play("score", 0.6);
        }
        if (_juggles >= _nextStarAt)
        {
            SpawnStar();
            _nextStarAt = _juggles + 6;
        }
        Host.HudChanged();
    }

    /// <summary>The ball flattens under the foot and springs back, a touch taller than round for a moment.</summary>
    void Squash()
    {
        _squashTween?.Cancel();
        _squashTween = Anims.Add(0.38, k =>
        {
            double s = 0.3 * (1 - k);
            _squash.ScaleX = 1 + s;
            _squash.ScaleY = 1 - s;
        }, Ease.OutBack);
    }

    void Drop()
    {
        int juggles = _juggles;
        _juggles = 0;
        _ball.Gravity = BaseGravity;
        _nextStarAt = 5;
        foreach (var star in _stars) Layer.Children.Remove(star.Sprite);
        _stars.Clear();
        Host.RoundEnded(_runScore);

        var s = Host.Settings;
        bool best = _runScore > s.BestJuggle;
        if (best)
        {
            s.BestJuggle = _runScore;
            Host.SaveSettings();
        }
        if (juggles >= 3)
        {
            var at = _ball.Pos - new Vec2(0, 90);
            if (best)
            {
                Host.Fx.Popup(at, L.T("NEW BEST!"), Gold, 32, 1.6, L.F("{0} points", _runScore));
                Host.Fx.Burst(at, GoldBurst, 30, 450, 700, 6, 1.0);
                Host.Sound.Play("best", 0.8);
                Flourish();
            }
            else
            {
                Host.Fx.Popup(at, L.T("Dropped!"), Color.FromRgb(255, 130, 130), 28, 1.4, L.F("{0} points", _runScore));
                Host.Sound.Play("buzzer", 0.45);
            }
        }
        Host.HudChanged();
    }

    /// <summary>A new best: the ball swells with pride and three golden rings roll out from it.</summary>
    void Flourish()
    {
        Anims.Add(0.7, k => _sprite.Scale = 1 + 0.35 * Ease.Pulse(k), Ease.Linear, () => _sprite.Scale = 1);
        for (int i = 0; i < 3; i++)
        {
            double size = 150 + i * 50;
            Anims.After(0.12 + i * 0.2, () => Host.Fx.Marker(_ball.Pos, Gold, 24, size, 0.9));
        }
    }

    void SpawnStar()
    {
        if (_stars.Count >= 2) return;
        var a = Host.Arena;
        var hud = Host.HudBounds.Inflate(60);
        for (int tries = 0; tries < 25; tries++)
        {
            var p = new Vec2(a.Left + 80 + Rng.NextDouble() * (a.Width - 160), a.Top + a.Height * (0.15 + Rng.NextDouble() * 0.45));
            if (hud.Contains(p.ToPoint()) || (p - _ball.Pos).Length < 220) continue;
            var sprite = Art.Star(16);
            sprite.IsHitTestVisible = false;
            // a thin four-point glint that turns against the star and fades in and out: the twinkle
            var turn = new RotateTransform();
            var glint = Art.PathOf("M0,-26 L2,-2 L26,0 L2,2 L0,26 L-2,2 L-26,0 L-2,-2 Z", Art.Brush(200, 255, 250, 215));
            glint.RenderTransformOrigin = RelativePoint.TopLeft;
            glint.RenderTransform = turn;
            sprite.Children.Add(glint);
            sprite.Set(p);
            sprite.Scale = 0.01;
            Layer.Children.Add(sprite);
            var star = new Star { Sprite = sprite, Glint = glint, Turn = turn, Pos = p };
            _stars.Add(star);
            Anims.Add(0.45, k => sprite.Scale = Math.Max(0.01, k), Ease.OutBack, () => star.Landed = true);
            Host.Sound.Play("star", 0.25, 1.6);
            Host.HudChanged();
            return;
        }
    }

    public override void Summon(Vec2 p)
    {
        var a = Host.Arena;
        if (_juggles > 0) Drop();
        _ball.Place(new Vec2(Clamp(p.X, a.Left + R, a.Right - R), Clamp(p.Y, a.Top + R, a.Bottom - R)));
    }

    // ------------------------------------------------------------------ simulation

    public override bool Update(double dt)
    {
        _time += dt;
        bool anim = Anims.Update(dt);
        _acc += dt;
        while (_acc >= Step)
        {
            _acc -= Step;
            SimStep(Step);
        }

        for (int i = _stars.Count - 1; i >= 0; i--)
        {
            var star = _stars[i];
            star.Age += dt;
            star.Sprite.Set(star.Pos, star.Age * 90);
            if (star.Landed) star.Sprite.Scale = 1 + 0.12 * Math.Sin(star.Age * 6);
            star.Glint.Opacity = 0.35 + 0.65 * Math.Abs(Math.Sin(star.Age * 4));
            star.Turn.Angle = -star.Age * 60;
            star.Sprite.Opacity = star.Age > 7 && (int)(star.Age * 8) % 2 == 0 ? 0.35 : 1;

            if ((star.Pos - _ball.Pos).Length < R + 18)
            {
                _runScore += 3;
                Host.Stats.Add("juggle.stars");
                Host.Stats.Max("juggle.run", _runScore);
                Host.ShareAction(star.Pos, 3);
                Host.Fx.Popup(star.Pos - new Vec2(0, 30), "+3", Gold, 30, 1.0);
                Host.Fx.Burst(star.Pos, GoldBurst, 18, 350, 400, 5, 0.6);
                Host.Sound.Play("star", 0.8);
            }
            else if (star.Age < 9)
            {
                continue;
            }
            Layer.Children.Remove(star.Sprite);
            _stars.RemoveAt(i);
            Host.HudChanged();
        }

        DrawBall();
        return !_ball.Asleep || anim || _stars.Count > 0;
    }

    void DrawBall()
    {
        _sprite.Set(_ball.Pos);
        _art.Set(default, _ball.Angle);
    }

    void SimStep(double h)
    {
        var imp = new Impacts();
        _ball.Step(h, Host, ref imp);
        double hit = Math.Max(imp.Floor, imp.Wall);
        if (hit > 140 && _time - _lastBounceSound > 0.06)
        {
            _lastBounceSound = _time;
            Host.Sound.Play("kick", Math.Min(0.6, hit / 2200), 0.7);
        }
        if (imp.TouchedGround && _juggles > 0 && _time - _lastKick > 0.1) Drop();
    }

    public override void DemoTick()
    {
        var a = Host.Arena;
        bool falling = _ball.Vel.Y > 0 && _ball.Pos.Y > a.Top + a.Height * 0.5;
        if (_ball.Asleep || falling)
            Kick(_ball.Pos + new Vec2(Rng.NextDouble() * 30 - 15, 20));
    }
}
