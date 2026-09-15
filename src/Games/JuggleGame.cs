using System;
using System.Collections.Generic;
using Avalonia.Media;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>Keepy-uppy: click the ball to kick it; don't let it touch the ground.</summary>
public sealed class JuggleGame : MiniGame
{
    const double R = 30, KickReach = 24, Step = 1.0 / 240, BaseGravity = 1500;

    static readonly Color Gold = Color.FromRgb(255, 209, 102);
    static readonly Color[] Dust = { Colors.White, Color.FromRgb(200, 210, 225) };
    static readonly Color[] GoldBurst = { Gold, Color.FromRgb(255, 240, 180), Color.FromRgb(255, 170, 40) };

    sealed class Star
    {
        public required Sprite Sprite;
        public Vec2 Pos;
        public double Age;
    }

    readonly BallBody _ball = new(R) { Gravity = BaseGravity, Restitution = 0.55, AirDrag = 0.1 };
    readonly Sprite _sprite = Art.SoccerBall(R);
    readonly List<Star> _stars = new();

    int _juggles, _runScore, _nextStarAt = 5;
    double _time, _acc, _lastKick = -1, _lastBounceSound;
    bool _placed;

    public JuggleGame(IGameHost host) : base(host)
    {
        Layer.Children.Add(_sprite);
    }

    public override string Id => "juggle";
    public override string Title => "Keepy-Uppy";
    public override Sprite CreateIcon() => Art.SoccerBall(9);

    public override HudInfo Hud => new(
        _runScore.ToString(),
        _juggles > 0 ? $"Juggles {_juggles}" + (_stars.Count > 0 ? " · grab the star!" : " · don't let it drop") : "Click the ball to kick it up",
        $"Best {Host.Settings.BestJuggle}");

    public override void Layout()
    {
        var a = Host.Arena;
        if (!_placed || _ball.Pos.X < a.Left || _ball.Pos.X > a.Right || _ball.Pos.Y > a.Bottom)
        {
            _ball.Place(new Vec2(a.Left + a.Width * 0.5, a.Bottom - R));
            _placed = true;
        }
        _sprite.Set(_ball.Pos, _ball.Angle);
        Host.HudChanged();
    }

    public override void CollectHitShapes(List<HitShape> into) => into.Add(HitShape.Circle(_ball.Pos, R + KickReach));

    public override bool PointerDown(Vec2 p, bool right)
    {
        if ((p - _ball.Pos).Length <= R + KickReach) Kick(p);
        return false;
    }

    void Kick(Vec2 p)
    {
        if (_time - _lastKick < 0.09) return;
        _lastKick = _time;

        double dx = Clamp((_ball.Pos.X - p.X) / (R + KickReach), -1, 1);
        double up = 980 + 120 * (1 - Math.Abs(dx)) + Rng.NextDouble() * 60;
        _ball.Place(_ball.Pos, new Vec2(dx * 560 + _ball.Vel.X * 0.2, -up));
        _ball.Spin = -dx * 720;

        if (_juggles == 0) _runScore = 0;
        _juggles++;
        _runScore++;
        _ball.Gravity = Math.Min(2700, BaseGravity + _juggles * 28);

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

    void Drop()
    {
        int juggles = _juggles;
        _juggles = 0;
        _ball.Gravity = BaseGravity;
        _nextStarAt = 5;
        foreach (var star in _stars) Layer.Children.Remove(star.Sprite);
        _stars.Clear();

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
                Host.Fx.Popup(at, "NEW BEST!", Gold, 32, 1.6, $"{_runScore} points");
                Host.Fx.Burst(at, GoldBurst, 30, 450, 700, 6, 1.0);
                Host.Sound.Play("best", 0.8);
            }
            else
            {
                Host.Fx.Popup(at, "Dropped!", Color.FromRgb(255, 130, 130), 28, 1.4, $"{_runScore} points");
                Host.Sound.Play("buzzer", 0.45);
            }
        }
        Host.HudChanged();
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
            sprite.Set(p);
            Layer.Children.Add(sprite);
            _stars.Add(new Star { Sprite = sprite, Pos = p });
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

    public override bool Update(double dt)
    {
        _time += dt;
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
            star.Sprite.Scale = 1 + 0.12 * Math.Sin(star.Age * 6);
            star.Sprite.Opacity = star.Age > 7 && (int)(star.Age * 8) % 2 == 0 ? 0.35 : 1;

            if ((star.Pos - _ball.Pos).Length < R + 18)
            {
                _runScore += 3;
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

        _sprite.Set(_ball.Pos, _ball.Angle);
        return !_ball.Asleep;
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
