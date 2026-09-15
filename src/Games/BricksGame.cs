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
/// Brick Breaker: bounce the ball off the paddle into a wall of bricks. The screen is a closed box,
/// so the ball rebounds off both sides and the top; only touching the floor loses a ball.
/// </summary>
public sealed class BricksGame : MiniGame
{
    const double BallR = 10, PaddleW = 150, PaddleH = 16, PaddleLift = 34, BrickW = 66, BrickH = 24, Gap = 6;
    const double StartSpeed = 720, MaxSpeed = 1350, Step = 1.0 / 240, LaneH = 130, MaxBounceDeg = 62;
    const int StartBalls = 3;

    static readonly Color Gold = Color.FromRgb(255, 209, 102);
    static readonly Color Steel = Color.FromRgb(170, 180, 195);
    static readonly Color[] RowColors =
    {
        Color.FromRgb(230, 57, 70), Color.FromRgb(244, 162, 97), Color.FromRgb(233, 196, 106),
        Color.FromRgb(42, 157, 143), Color.FromRgb(69, 123, 157), Color.FromRgb(142, 108, 207),
    };
    static readonly Color[] Confetti = { Gold, Color.FromRgb(239, 71, 111), Color.FromRgb(6, 214, 160), Colors.White };

    sealed class Brick
    {
        public required Rectangle El;
        public Rect Box;
        public Color Color;
        public int Hits, Points;
        public bool IsGold, Broken;
        public double Fade;
    }

    readonly Canvas _brickLayer = new() { IsHitTestVisible = false };
    readonly Rectangle _paddle = new()
    {
        Width = PaddleW, Height = PaddleH, RadiusX = 8, RadiusY = 8, IsHitTestVisible = false,
        Fill = Vertical(Color.FromRgb(235, 241, 248), Color.FromRgb(120, 134, 152)), Stroke = Art.Brush("#3B4654"), StrokeThickness = 1,
    };
    readonly Rectangle _glow = new()
    {
        Width = PaddleW + 20, Height = PaddleH + 18, RadiusX = 14, RadiusY = 14, IsHitTestVisible = false,
        Stroke = Art.Brush(120, 255, 255, 255), StrokeThickness = 1.5, StrokeDashArray = new AvaloniaList<double> { 3, 5 },
    };
    readonly Sprite _ballSprite = MakeBall(BallR);
    readonly List<Brick> _bricks = new();
    readonly Dictionary<string, double> _lastSound = new();

    Vec2 _ball, _vel;
    double _paddleX, _time, _acc, _speed, _nextIn = -1;
    int _score, _balls, _level;
    bool _placed, _live, _gameOver, _beatBest, _demo;

    public BricksGame(IGameHost host) : base(host)
    {
        Layer.Children.Add(_brickLayer);
        Layer.Children.Add(_glow);
        Layer.Children.Add(_paddle);
        Layer.Children.Add(_ballSprite);
    }

    public override string Id => "bricks";
    public override string Title => "Brick Breaker";

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        for (int row = 0; row < 2; row++)
            for (int i = 0; i < 3; i++)
                s.Rotor.Children.Add(Art.At(new Rectangle { Width = 6, Height = 4, RadiusX = 1, RadiusY = 1, Fill = Art.Brush(RowColors[row * 3 + i]) },
                    -10 + i * 7, -10 + row * 5));
        s.Rotor.Children.Add(Art.Circle(2, 3, 2.6, Brushes.White));
        s.Rotor.Children.Add(Art.At(new Rectangle { Width = 14, Height = 3, RadiusX = 1.5, RadiusY = 1.5, Fill = Art.Brush("#CDD6E0") }, -7, 7));
        return s;
    }

    int BricksLeft => _bricks.Count(b => !b.Broken);
    double PaddleTop => Host.Arena.Bottom - PaddleLift - PaddleH / 2;
    Rect PaddleRect => new(_paddleX - PaddleW / 2, PaddleTop, PaddleW, PaddleH);
    Rect LaunchArea => PaddleRect.Inflate(new Thickness(14, 34, 14, 14));

    public override HudInfo Hud => new(
        _score.ToString(),
        _gameOver ? "Game over · click the paddle to play again"
            : _live ? $"Level {_level} · balls {_balls} · bricks {BricksLeft}"
            : $"Level {_level} · balls {_balls} · click the paddle to launch",
        $"Best {Host.Settings.BestBricks}");

    // ------------------------------------------------------------------ game flow

    public override void Layout()
    {
        var a = Host.Arena;
        if (!_placed)
        {
            _placed = true;
            _paddleX = a.Center.X;
            NewGame();
        }
        else if (_bricks.Any(b => !b.Broken && !a.Contains(b.Box)))
        {
            BuildWall(); // the screen changed: rebuild this level to fit
        }
        _paddleX = Clamp(_paddleX, a.Left + PaddleW / 2, a.Right - PaddleW / 2);
        if (!_live) ParkBall();
        Draw();
        Host.HudChanged();
    }

    public override void Deactivate()
    {
        if (!_live) return;
        _live = false; // pausing by switching games doesn't cost a ball
        ParkBall();
    }

    void NewGame()
    {
        _score = 0;
        _balls = StartBalls;
        _level = 0;
        _gameOver = _beatBest = false;
        NextLevel();
    }

    void NextLevel()
    {
        _level++;
        _live = false;
        BuildWall();
        ParkBall();
        Host.HudChanged();
    }

    void BuildWall()
    {
        foreach (var b in _bricks) _brickLayer.Children.Remove(b.El);
        _bricks.Clear();

        var a = Host.Arena;
        int rows = Math.Min(3 + _level, 8);
        double wallW = Math.Min(a.Width - 80, 1180);
        int cols = Math.Max(4, (int)((wallW + Gap) / (BrickW + Gap)));
        double left = a.Center.X - (cols * (BrickW + Gap) - Gap) / 2;
        double top = a.Top + Math.Max(60, a.Height * 0.09);
        var hud = Host.HudBounds.Inflate(12);

        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                var box = new Rect(left + c * (BrickW + Gap), top + r * (BrickH + Gap), BrickW, BrickH);
                if (box.Intersects(hud)) continue;
                bool steel = _level >= 2 && (r * 3 + c) % 7 == 0;
                bool gold = !steel && Rng.NextDouble() < 0.06;
                var color = gold ? Gold : steel ? Steel : RowColors[r % RowColors.Length];
                var el = new Rectangle
                {
                    Width = BrickW, Height = BrickH, RadiusX = 4, RadiusY = 4, IsHitTestVisible = false,
                    Fill = Vertical(Art.Blend(color, Colors.White, 0.3), Art.Blend(color, Colors.Black, 0.25)),
                    Stroke = Art.Brush(Art.Blend(color, Colors.Black, 0.5)), StrokeThickness = 1,
                };
                Canvas.SetLeft(el, box.X);
                Canvas.SetTop(el, box.Y);
                _brickLayer.Children.Add(el);
                _bricks.Add(new Brick
                {
                    El = el, Box = box, Color = color, IsGold = gold,
                    Hits = steel ? 2 : 1, Points = gold ? 10 : steel ? 3 : rows - r,
                });
            }
        }
    }

    void ParkBall()
    {
        _ball = new Vec2(_paddleX, PaddleTop - BallR - 1);
        _vel = default;
    }

    void Launch()
    {
        double angle = (Rng.NextDouble() * 50 - 25) * Math.PI / 180;
        _speed = Math.Min(MaxSpeed, StartSpeed + (_level - 1) * 60);
        _vel = new Vec2(Math.Sin(angle), -Math.Cos(angle)) * _speed;
        _live = true;
        Host.Sound.Play("kick", 0.6, 1.5);
        Host.HudChanged();
    }

    void AddScore(int pts)
    {
        _score += pts;
        if (_score <= Host.Settings.BestBricks) return;
        Host.Settings.BestBricks = _score;
        _beatBest = true;
        Host.SaveSettings();
    }

    void LoseBall()
    {
        _live = false;
        _balls--;
        Host.Fx.Burst(_ball, new[] { Colors.White, Color.FromRgb(160, 170, 185) }, 14, 300, 600, 5, 0.5);
        if (_balls <= 0)
        {
            _gameOver = true;
            var a = Host.Arena;
            var at = new Vec2(a.Center.X, a.Top + a.Height * 0.35);
            Host.Fx.Popup(at, _beatBest ? "NEW BEST!" : "GAME OVER", _beatBest ? Gold : Colors.White, 38, 2.4, $"{_score} points · level {_level}");
            if (_beatBest) Host.Fx.Burst(at, Confetti, 40, 520, 700, 7, 1.1);
            Host.Sound.Play(_beatBest ? "best" : "buzzer", _beatBest ? 0.8 : 0.4);
        }
        else
        {
            Host.Fx.Popup(_ball - new Vec2(0, 60), "ball lost", Color.FromRgb(255, 150, 150), 24, 1.1, $"{_balls} left");
            Host.Sound.Play("buzzer", 0.3);
        }
        ParkBall();
        Host.HudChanged();
    }

    void LevelCleared()
    {
        _live = false;
        int bonus = 25 * _level;
        AddScore(bonus);
        var at = new Vec2(Host.Arena.Center.X, Host.Arena.Top + Host.Arena.Height * 0.35);
        Host.Fx.Popup(at, "LEVEL CLEAR!", Gold, 40, 1.8, $"+{bonus} bonus");
        Host.Fx.Burst(at, Confetti, 36, 500, 700, 7, 1.0);
        Host.Sound.Play("fire", 0.8);
        _nextIn = 1.4;
        ParkBall();
        Host.HudChanged();
    }

    // ------------------------------------------------------------------ input

    public override void CollectHitShapes(List<HitShape> into)
    {
        var a = Host.Arena;
        // While a ball is live the paddle lane takes the mouse, so the paddle keeps tracking it everywhere.
        if (_live) into.Add(HitShape.Box(new Rect(a.Left, a.Bottom - LaneH, a.Width, LaneH)));
        else into.Add(HitShape.Box(LaunchArea));
    }

    public override bool PointerDown(Vec2 p, bool right)
    {
        if (_live || _nextIn > 0 || !LaunchArea.Contains(p.ToPoint())) return false;
        if (_gameOver) NewGame();
        Launch();
        return false;
    }

    public override void Summon(Vec2 p)
    {
        if (_live) return;
        var a = Host.Arena;
        _paddleX = Clamp(p.X, a.Left + PaddleW / 2, a.Right - PaddleW / 2);
        ParkBall();
        Draw();
    }

    // ------------------------------------------------------------------ simulation

    public override bool Update(double dt)
    {
        _time += dt;
        var a = Host.Arena;
        bool busy = _live;

        if (_live)
        {
            double target = _demo ? _ball.X + Math.Sin(_time * 2.3) * 40 : Host.Pointer.X;
            _paddleX = Clamp(target, a.Left + PaddleW / 2, a.Right - PaddleW / 2);
            _acc += dt;
            while (_acc >= Step && _live)
            {
                _acc -= Step;
                SimStep(Step);
            }
        }
        else
        {
            _acc = 0;
        }

        if (_nextIn > 0)
        {
            busy = true;
            if ((_nextIn -= dt) <= 0)
            {
                _nextIn = -1;
                NextLevel();
            }
        }

        for (int i = _bricks.Count - 1; i >= 0; i--)
        {
            var b = _bricks[i];
            if (!b.Broken) continue;
            busy = true;
            b.Fade += dt;
            b.El.Opacity = Math.Max(0, 1 - b.Fade / 0.25);
            if (b.Fade < 0.25) continue;
            _brickLayer.Children.Remove(b.El);
            _bricks.RemoveAt(i);
        }

        Draw();
        return busy;
    }

    void SimStep(double h)
    {
        var a = Host.Arena;
        _ball += _vel * h;

        // closed box: sides and ceiling bounce
        bool wall = false;
        if (_ball.X - BallR < a.Left) { _ball.X = a.Left + BallR; _vel.X = Math.Abs(_vel.X); wall = true; }
        else if (_ball.X + BallR > a.Right) { _ball.X = a.Right - BallR; _vel.X = -Math.Abs(_vel.X); wall = true; }
        if (_ball.Y - BallR < a.Top) { _ball.Y = a.Top + BallR; _vel.Y = Math.Abs(_vel.Y); wall = true; }
        if (wall)
        {
            KeepSteep();
            PlayThrottled("rim", 0.25, 2.2);
        }

        double top = PaddleTop;
        if (_vel.Y > 0 && _ball.Y + BallR >= top && _ball.Y < top + PaddleH && Math.Abs(_ball.X - _paddleX) <= PaddleW / 2 + BallR * 0.7)
        {
            double off = Clamp((_ball.X - _paddleX) / (PaddleW / 2), -1, 1);
            double angle = off * MaxBounceDeg * Math.PI / 180;
            _speed = Math.Min(MaxSpeed, _speed + 8);
            _vel = new Vec2(Math.Sin(angle), -Math.Cos(angle)) * _speed;
            _ball.Y = top - BallR;
            PlayThrottled("board", 0.6, 1.1);
        }

        foreach (var b in _bricks)
            if (!b.Broken && HitBrick(b)) break;

        if (_ball.Y + BallR >= a.Bottom) LoseBall();
    }

    /// <summary>Stops the ball from getting stuck bouncing almost horizontally between the walls.</summary>
    void KeepSteep()
    {
        double min = _speed * 0.28;
        if (Math.Abs(_vel.Y) >= min) return;
        _vel.Y = (_vel.Y > 0 ? 1 : -1) * min;
        _vel = _vel.Normalized() * _speed;
    }

    bool HitBrick(Brick b)
    {
        var r = b.Box;
        var closest = new Vec2(Clamp(_ball.X, r.Left, r.Right), Clamp(_ball.Y, r.Top, r.Bottom));
        var d = _ball - closest;
        if (d.LengthSquared > BallR * BallR) return false;

        bool horizontal;
        if (d.LengthSquared > 1e-9)
        {
            horizontal = Math.Abs(d.X) > Math.Abs(d.Y);
        }
        else
        {
            double l = _ball.X - r.Left, rt = r.Right - _ball.X, t = _ball.Y - r.Top, bt = r.Bottom - _ball.Y;
            horizontal = Math.Min(l, rt) < Math.Min(t, bt);
            d = horizontal ? new Vec2(l < rt ? -1 : 1, 0) : new Vec2(0, t < bt ? -1 : 1);
        }

        if (horizontal)
        {
            double side = d.X < 0 ? -1 : 1;
            _ball.X = side < 0 ? r.Left - BallR : r.Right + BallR;
            if (_vel.X * side < 0) _vel.X = -_vel.X;
        }
        else
        {
            double side = d.Y < 0 ? -1 : 1;
            _ball.Y = side < 0 ? r.Top - BallR : r.Bottom + BallR;
            if (_vel.Y * side < 0) _vel.Y = -_vel.Y;
        }
        Damage(b);
        return true;
    }

    void Damage(Brick b)
    {
        if (--b.Hits > 0)
        {
            b.El.Opacity = 0.6;
            PlayThrottled("rim", 0.5, 1.5);
            return;
        }
        b.Broken = true;
        AddScore(b.Points);
        var center = new Vec2(b.Box.Center.X, b.Box.Center.Y);
        Host.Fx.Burst(center, new[] { b.Color, Colors.White }, b.IsGold ? 20 : 8, 260, 900, 5, 0.5);
        if (b.IsGold) Host.Fx.Popup(center - new Vec2(0, 20), $"+{b.Points}", Gold, 26, 0.9);
        PlayThrottled("pop", b.IsGold ? 0.9 : 0.6, 0.8 + Rng.NextDouble() * 0.5);
        _speed = Math.Min(MaxSpeed, _speed + 6);
        _vel = _vel.Normalized() * _speed;
        Host.HudChanged();
        if (BricksLeft == 0) LevelCleared();
    }

    // ------------------------------------------------------------------ visuals

    void Draw()
    {
        Canvas.SetLeft(_paddle, _paddleX - PaddleW / 2);
        Canvas.SetTop(_paddle, PaddleTop);
        Canvas.SetLeft(_glow, _paddleX - PaddleW / 2 - 10);
        Canvas.SetTop(_glow, PaddleTop - 9);
        _glow.IsVisible = !_live && _nextIn <= 0;
        _ballSprite.Set(_ball);
    }

    void PlayThrottled(string name, double vol, double pitch = 1)
    {
        if (_lastSound.TryGetValue(name, out double t) && _time - t < 0.05) return;
        _lastSound[name] = _time;
        Host.Sound.Play(name, vol, pitch);
    }

    static LinearGradientBrush Vertical(Color top, Color bottom)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        };
        brush.GradientStops.Add(new GradientStop(top, 0));
        brush.GradientStops.Add(new GradientStop(bottom, 1));
        return brush;
    }

    static Sprite MakeBall(double r)
    {
        var s = new Sprite { IsHitTestVisible = false };
        s.Children.Add(Art.Circle(0, 0, r * 1.9, Art.Brush(40, 150, 220, 255)));
        var body = new RadialGradientBrush { GradientOrigin = new RelativePoint(0.35, 0.3, RelativeUnit.Relative) };
        body.GradientStops.Add(new GradientStop(Colors.White, 0));
        body.GradientStops.Add(new GradientStop(Color.FromRgb(190, 230, 255), 0.6));
        body.GradientStops.Add(new GradientStop(Color.FromRgb(80, 150, 210), 1));
        s.Children.Add(Art.Circle(0, 0, r, body, Art.Brush("#25507A"), 1));
        return s;
    }

    public override void DemoTick()
    {
        _demo = true;
        if (_live || _nextIn > 0) return;
        if (_gameOver) NewGame();
        Launch();
    }
}
