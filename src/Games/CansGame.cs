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
/// Can Knockdown: throw the ball from behind the foul line and knock the pyramid off its shelf.
/// Clear a stack to get a bigger one; run out of balls with cans still standing and the game is over.
/// A game, from the first throw to that game over, is one race round against the computer or a co-worker.
/// </summary>
public sealed class CansGame : MiniGame
{
    const double BallR = 17, CanR = 17, CanW = 30, CanH = 40, CanSpacing = 35, Step = 1.0 / 240, Gravity = 1900;
    const double ShelfThick = 10, FoulGap = 380, BallMass = 2.2, CanMass = 1, Reach = BallR + 16;

    /// <summary>What a decent game scores: two stacks cleared with a spare ball each, and a dent in the third.</summary>
    public const int Baseline = 30;

    /// <summary>About how long such a game takes, in seconds.</summary>
    public const double GameSeconds = 45;

    static readonly Color Gold = Color.FromRgb(255, 209, 102);
    static readonly Color[] Labels =
    {
        Color.FromRgb(230, 57, 70), Color.FromRgb(29, 111, 196), Color.FromRgb(46, 160, 90), Color.FromRgb(244, 140, 30),
    };
    static readonly Color[] Confetti = { Gold, Color.FromRgb(239, 71, 111), Color.FromRgb(6, 214, 160), Colors.White };

    sealed class Can
    {
        public required Sprite Sprite;
        public Vec2 Pos, Vel, Home;
        public double Angle, Spin, StillT, FadeT, Wobble;
        public bool Dynamic, Resting, Down, Golden, Landing;
        public Can? SupportA, SupportB;
        public Anims.Tween? WobbleTween;
        public TranslateTransform? Sheen; // the golden can's glint, sweeping across it
        public ScaleTransform? StarScale;
    }

    readonly BallBody _ball = new(BallR) { Gravity = Gravity, Restitution = 0.45, RollFriction = 1.8 };
    readonly Sprite _ballSprite = MakeBall(BallR);
    readonly Canvas _canLayer = new() { IsHitTestVisible = false };
    readonly Rectangle _plank = new()
    {
        Height = ShelfThick, RadiusX = 2, RadiusY = 2, Fill = Art.Brush("#8B5A2B"), Stroke = Art.Brush("#4E3116"), StrokeThickness = 1,
        IsHitTestVisible = false,
    };
    readonly Rectangle _plankShadow = new() { Height = 4, Fill = Art.Brush(80, 0, 0, 0), IsHitTestVisible = false };
    readonly Line _foul = new()
    {
        Stroke = Art.Brush(110, 255, 255, 255), StrokeThickness = 2, StrokeDashArray = new AvaloniaList<double> { 4, 4 },
        IsVisible = false, IsHitTestVisible = false,
    };
    readonly List<Can> _cans = new();
    readonly List<(double t, Vec2 p)> _trail = new();
    readonly Dictionary<string, double> _lastSound = new();

    double _shelfX1, _shelfX2, _shelfY, _spotX, _foulX;
    double _time, _acc, _sinceThrow, _clearIn = -1, _judgeIn = -1;
    int _stack, _ballsLeft, _score, _build;
    bool _placed, _holding, _inFlight, _gameOver, _shelfOnRight, _beatBest, _roundActive;
    Vec2 _grabOffset;

    public CansGame(IGameHost host) : base(host)
    {
        Layer.Children.Add(_foul);
        Layer.Children.Add(_plankShadow);
        Layer.Children.Add(_plank);
        Layer.Children.Add(_canLayer);
        Layer.Children.Add(_ballSprite);
    }

    public override string Id => "cans";
    public override string Title => "Can Knockdown";

    public override Sprite CreateIcon()
    {
        var icon = MakeCan(Labels[0], false, out _, out _);
        icon.Scale = 0.5;
        return icon;
    }

    int CansLeft => _cans.Count(c => !c.Down);

    public override HudInfo Hud => new(
        _score.ToString(),
        _gameOver ? L.T("Game over · grab the ball to play again") : L.F("Stack {0} · balls {1} · cans left {2}", _stack, _ballsLeft, CansLeft),
        L.F("Best {0}", Host.Settings.BestCans));

    // ------------------------------------------------------------------ game flow

    public override void Layout()
    {
        var a = Host.Arena;
        if (!_placed)
        {
            _placed = true;
            NewGame();
        }
        else if (_shelfX1 < a.Left || _shelfX2 > a.Right || _shelfY > a.Bottom - 60)
        {
            _stack--; // same stack again, placed for the new screen
            NextStack();
        }
        Draw();
        Host.HudChanged();
    }

    public override void Deactivate()
    {
        _holding = false;
        _foul.IsVisible = false;
    }

    public override bool SupportsLan => true;
    public override (int Score, bool Active)? Race => (_score, _roundActive);
    public override int RaceBaseline => Baseline;
    public override int RaceBest => Host.Settings.BestCans;
    public override double RaceSeconds => GameSeconds;

    /// <summary>The rival started a game: a fresh stack if the last game is over, and this game counts from now.</summary>
    public override void StartRace()
    {
        if (_roundActive) return;
        if (_gameOver) NewGame();
        BeginRound();
        Host.Wake();
    }

    void BeginRound()
    {
        _roundActive = true;
        Host.RoundStarted();
    }

    void NewGame()
    {
        _score = 0;
        _stack = 0;
        _gameOver = false;
        _beatBest = false;
        NextStack();
    }

    /// <summary>The shape of the n-th stack: rows of cans (3, then 4 from the third stack, 5 from the fifth) and balls to throw at it.</summary>
    public static (int Rows, int Balls) StackShape(int stack)
    {
        int rows = 3 + Math.Min(2, Math.Max(0, stack - 1) / 2);
        return (rows, rows >= 5 ? 4 : 3);
    }

    void NextStack()
    {
        _stack++;
        Host.Stats.Max("cans.stack", _stack);
        foreach (var c in _cans) _canLayer.Children.Remove(c.Sprite);
        _cans.Clear();
        var (rows, balls) = StackShape(_stack);
        _ballsLeft = balls;
        PlaceShelf(rows);
        BuildPyramid(rows);
        ReturnBall();
        Host.HudChanged();
    }

    void PlaceShelf(int rows)
    {
        var a = Host.Arena;
        double width = rows * CanSpacing + 40;
        var hud = Host.HudBounds.Inflate(30);
        for (int tries = 0; tries < 20; tries++)
        {
            _shelfOnRight = Rng.NextDouble() < 0.5;
            double cx = _shelfOnRight
                ? a.Left + a.Width * (0.66 + Rng.NextDouble() * 0.18)
                : a.Left + a.Width * (0.16 + Rng.NextDouble() * 0.18);
            cx = Clamp(cx, a.Left + width / 2 + 30, a.Right - width / 2 - 30);
            _shelfY = a.Top + a.Height * (0.4 + Rng.NextDouble() * 0.3);
            _shelfX1 = cx - width / 2;
            _shelfX2 = cx + width / 2;
            var stackArea = new Rect(_shelfX1, _shelfY - rows * CanH - 10, width, rows * CanH + 20);
            if (!stackArea.Intersects(hud)) break;
        }

        _spotX = _shelfOnRight ? a.Left + a.Width * 0.14 : a.Right - a.Width * 0.14;
        _foulX = Clamp(_shelfOnRight ? _shelfX1 - FoulGap : _shelfX2 + FoulGap, a.Left + 120, a.Right - 120);
        if (_shelfOnRight ? _spotX > _foulX - 40 : _spotX < _foulX + 40)
            _spotX = _shelfOnRight ? _foulX - 80 : _foulX + 80;

        Canvas.SetLeft(_plank, _shelfX1);
        Canvas.SetTop(_plank, _shelfY);
        _plank.Width = width;
        Canvas.SetLeft(_plankShadow, _shelfX1 + 4);
        Canvas.SetTop(_plankShadow, _shelfY + ShelfThick);
        _plankShadow.Width = width - 8;
        _foul.StartPoint = new Point(_foulX, a.Top);
        _foul.EndPoint = new Point(_foulX, a.Bottom);
    }

    /// <summary>
    /// Where the cans of a pyramid of <paramref name="rows"/> rows stand, bottom row first and left to right, centered
    /// on <paramref name="cx"/> with the bottom row on a shelf at <paramref name="shelfY"/>.
    /// </summary>
    public static List<Vec2> PyramidHomes(int rows, double cx, double shelfY)
    {
        var homes = new List<Vec2>();
        for (int k = 0; k < rows; k++)
        {
            int n = rows - k;
            for (int i = 0; i < n; i++) homes.Add(new Vec2(cx + (i - (n - 1) / 2.0) * CanSpacing, shelfY - CanH / 2 - k * CanH));
        }
        return homes;
    }

    /// <summary>When the n-th can of a new stack starts its drop: one after another, bottom row first.</summary>
    public static double DropDelay(int index) => 0.07 * index;

    /// <summary>A stack is built by dropping its cans onto the shelf one by one, each bouncing to a stop.</summary>
    void BuildPyramid(int rows)
    {
        int build = ++_build;
        double cx = (_shelfX1 + _shelfX2) / 2;
        var homes = PyramidHomes(rows, cx, _shelfY);
        var below = new List<Can>();
        int index = 0, last = homes.Count - 1;
        for (int k = 0; k < rows; k++)
        {
            int n = rows - k;
            var row = new List<Can>(n);
            for (int i = 0; i < n; i++)
            {
                var home = homes[index];
                bool golden = k == rows - 1;
                var can = new Can
                {
                    Sprite = MakeCan(Labels[(k + i) % Labels.Length], golden, out var sheen, out var starScale), Pos = home, Home = home, Golden = golden,
                    SupportA = k > 0 ? below[i] : null, SupportB = k > 0 ? below[i + 1] : null, Landing = true, Sheen = sheen, StarScale = starScale,
                };
                _canLayer.Children.Add(can.Sprite);
                _cans.Add(can);
                row.Add(can);
                DropIn(can, index, index == last ? build : 0);
                index++;
            }
            below = row;
        }
        Draw();
    }

    /// <param name="lastOf">The build this can completes (its stack is then ready), or 0 for the others.</param>
    void DropIn(Can can, int index, int lastOf)
    {
        var a = Host.Arena;
        var home = can.Home;
        double start = Math.Max(a.Top + CanH, home.Y - 260);
        can.Pos = new Vec2(home.X, start);
        can.Sprite.Opacity = 0;
        Anims.Add(0.5, k =>
        {
            can.Sprite.Opacity = 1;
            can.Pos = new Vec2(home.X, start + (home.Y - start) * k);
        }, Ease.OutBounce, () =>
        {
            can.Pos = home;
            can.Landing = false;
            PlayThrottled("thump", 0.18, 1.3 + Rng.NextDouble() * 0.2);
            if (lastOf != 0 && lastOf == _build) StackReady();
        }, DropDelay(index));
    }

    /// <summary>The whole stack stands: the golden can catches the light a few times.</summary>
    void StackReady()
    {
        for (int i = 0; i < 3; i++) Anims.After(0.2 + i * 1.1, Glint);
    }

    void Glint()
    {
        foreach (var c in _cans)
        {
            if (!c.Golden || c.Down || c.Sheen is not { } sheen || c.StarScale is not { } star) continue;
            Anims.Add(0.7, k =>
            {
                sheen.X = -CanW + CanW * 2 * k;
                star.ScaleX = star.ScaleY = 1 + 0.7 * Ease.Pulse(k);
            }, Ease.InOutQuad);
        }
    }

    void ReturnBall()
    {
        _inFlight = false;
        _ball.Place(new Vec2(_spotX, Host.Arena.Bottom - BallR));
    }

    void AddScore(int pts)
    {
        _score += pts;
        var s = Host.Settings;
        if (_score > s.BestCans)
        {
            s.BestCans = _score;
            _beatBest = true;
            Host.SaveSettings();
        }
    }

    void StackCleared()
    {
        int bonus = _ballsLeft * 5;
        AddScore(bonus);
        var at = new Vec2((_shelfX1 + _shelfX2) / 2, _shelfY - 140);
        if (bonus > 0) Host.ShareAction(at, bonus);
        Host.Fx.Popup(at, L.T("CLEAR!"), Gold, 40, 1.8, bonus > 0 ? L.F("+{0} for spare balls", bonus) : L.T("next stack"));
        Host.Fx.Burst(at, Confetti, 36, 500, 700, 7, 1.0);
        Host.Sound.Play("fire", 0.8);
        NextStack();
    }

    void GameOver()
    {
        _gameOver = true;
        _roundActive = false;
        Host.RoundEnded(_score);
        var a = Host.Arena;
        var at = new Vec2(a.Center.X, a.Top + a.Height * 0.3);
        Host.Fx.Popup(at, _beatBest ? L.T("NEW BEST!") : L.T("GAME OVER"), _beatBest ? Gold : Colors.White, 38, 2.4, L.F("{0} points · reached stack {1}", _score, _stack));
        Host.Sound.Play(_beatBest ? "best" : "buzzer", _beatBest ? 0.8 : 0.4);
        ReturnBall();
        Host.HudChanged();
    }

    // ------------------------------------------------------------------ input

    public override void CollectHitShapes(List<HitShape> into)
    {
        if (!_inFlight) into.Add(HitShape.Circle(_ball.Pos, Reach));
    }

    public override bool PointerDown(Vec2 p, bool right)
    {
        if (_inFlight || (p - _ball.Pos).Length > Reach) return false;
        if (_gameOver) NewGame();
        if (_ballsLeft <= 0) return false;
        _holding = true;
        _grabOffset = _ball.Pos - p;
        _trail.Clear();
        _foul.IsVisible = true;
        return true;
    }

    public override void PointerUp(Vec2 p)
    {
        if (!_holding) return;
        _holding = false;
        _foul.IsVisible = false;
        var v = ThrowVelocity();
        if (v.Length < 200)
        {
            _ball.Place(_ball.Pos, v); // just dropped it: no ball used
            return;
        }
        _ball.Spin = -v.X * 0.3;
        Throw(v);
        Host.Sound.Play("whoosh", Math.Min(1, v.Length / 3000) * 0.5);
    }

    public override void PointerCancel()
    {
        if (!_holding) return;
        _holding = false;
        _foul.IsVisible = false;
        _ball.Place(_ball.Pos);
    }

    /// <summary>A ball leaves the hand: the first throw of a game starts its race round.</summary>
    void Throw(Vec2 v)
    {
        _ball.Place(_ball.Pos, v);
        _inFlight = true;
        _sinceThrow = 0;
        _ballsLeft--;
        if (!_roundActive) BeginRound();
        Glint();
        Host.HudChanged();
    }

    Vec2 ClampToThrowZone(Vec2 p)
    {
        var a = Host.Arena;
        p.X = _shelfOnRight ? Clamp(p.X, a.Left + BallR, _foulX - BallR) : Clamp(p.X, _foulX + BallR, a.Right - BallR);
        p.Y = Clamp(p.Y, a.Top + BallR, a.Bottom - BallR);
        return p;
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
        return v.Length > 3600 ? v * (3600 / v.Length) : v;
    }

    public override void Summon(Vec2 p)
    {
        if (_inFlight || _holding) return;
        _ball.Place(ClampToThrowZone(p));
    }

    // ------------------------------------------------------------------ simulation

    public override bool Update(double dt)
    {
        _time += dt;
        bool busy = _holding | Anims.Update(dt);

        if (_holding)
        {
            var target = ClampToThrowZone(Host.Pointer + _grabOffset);
            _ball.Pos = target;
            _ball.Vel = default;
            _trail.Add((_time, target));
            while (_trail.Count > 0 && _time - _trail[0].t > 0.15) _trail.RemoveAt(0);
        }

        _acc += dt;
        while (_acc >= Step)
        {
            _acc -= Step;
            SimStep(Step);
        }

        if (_inFlight)
        {
            busy = true;
            _sinceThrow += dt;
            if ((_ball.Asleep && _sinceThrow > 0.6) || _sinceThrow > 4.5)
            {
                _inFlight = false;
                if (_clearIn < 0)
                {
                    if (_ballsLeft > 0) ReturnBall();
                    else _judgeIn = 1.2; // let the cans settle before deciding
                }
            }
        }

        for (int i = _cans.Count - 1; i >= 0; i--)
        {
            var c = _cans[i];
            if (c.Dynamic && !c.Resting) busy = true;
            if (!c.Down || !c.Resting) continue;
            c.FadeT += dt;
            busy = true;
            if (c.FadeT > 1.2) c.Sprite.Opacity = Math.Max(0, 1 - (c.FadeT - 1.2) / 0.5);
            if (c.FadeT > 1.7)
            {
                _canLayer.Children.Remove(c.Sprite);
                _cans.RemoveAt(i);
            }
        }

        if (_clearIn >= 0)
        {
            busy = true;
            if ((_clearIn -= dt) < 0)
            {
                _clearIn = -1;
                StackCleared();
            }
        }
        if (_judgeIn >= 0)
        {
            busy = true;
            if ((_judgeIn -= dt) < 0)
            {
                _judgeIn = -1;
                if (_clearIn < 0 && CansLeft > 0) GameOver();
            }
        }

        busy |= !_ball.Asleep;
        Draw();
        return busy;
    }

    void SimStep(double h)
    {
        if (!_holding)
        {
            var imp = new Impacts();
            _ball.Step(h, Host, ref imp);
            double shelfHit = _ball.CollideSegment(new Vec2(_shelfX1, _shelfY + ShelfThick / 2), new Vec2(_shelfX2, _shelfY + ShelfThick / 2), ShelfThick / 2, 0.35);
            if (imp.Floor > 200) PlayThrottled("bounce", Math.Min(0.8, imp.Floor / 1800));
            if (shelfHit > 80) PlayThrottled("board", Math.Min(0.8, shelfHit / 1200));
        }

        foreach (var c in _cans)
        {
            // lose support when a can underneath has been knocked out of place
            if (!c.Dynamic && !c.Landing && (Displaced(c.SupportA) || Displaced(c.SupportB)))
            {
                Wake(c);
                c.Vel = new Vec2((Rng.NextDouble() - 0.5) * 80, 0);
                c.Spin = (Rng.NextDouble() - 0.5) * 200;
            }
            if (c.Dynamic && !c.Resting && c.FadeT == 0) StepCan(c, h);
        }

        if (!_holding)
            foreach (var c in _cans)
                if (c.FadeT == 0 && !c.Landing) ResolveBallCan(c);
        for (int i = 0; i < _cans.Count; i++)
            for (int j = i + 1; j < _cans.Count; j++)
                ResolveCans(_cans[i], _cans[j]);

        foreach (var c in _cans)
        {
            if (c.Down || !c.Dynamic) continue;
            if (c.Pos.Y < _shelfY + 30 && c.Pos.X >= _shelfX1 - 12 && c.Pos.X <= _shelfX2 + 12) continue;
            c.Down = true;
            int pts = c.Golden ? 3 : 1;
            AddScore(pts);
            Host.Stats.Add("cans.knocked");
            Host.ShareAction(c.Pos, pts);
            Host.Fx.Popup(c.Pos - new Vec2(0, 30), $"+{pts}", c.Golden ? Gold : Colors.White, c.Golden ? 28 : 22, 0.8);
            Host.HudChanged();
            if (_clearIn < 0 && CansLeft == 0) _clearIn = 0.7;
        }
    }

    static bool Displaced(Can? support) => support != null && support.Dynamic && (support.Pos - support.Home).Length > 6;

    static void Wake(Can c)
    {
        c.Dynamic = true;
        c.Resting = false;
        c.StillT = 0;
    }

    /// <summary>A can that was brushed rather than knocked rocks on its base: the tilt at a point of the wobble, in degrees.</summary>
    public static double WobbleAngle(double k) => 9 * Math.Sin(k * Math.PI * 4) * (1 - k);

    void Wobble(Can c)
    {
        if (c.WobbleTween is { Finished: false }) return;
        c.WobbleTween = Anims.Add(0.6, k => c.Wobble = WobbleAngle(k), Ease.Linear, () => c.Wobble = 0);
    }

    static double HalfHeight(Can c)
    {
        double s = Math.Abs(Math.Sin(c.Angle * Math.PI / 180));
        return CanH / 2 * (1 - s) + CanW / 2 * s;
    }

    void StepCan(Can c, double h)
    {
        var a = Host.Arena;
        double half = HalfHeight(c);
        double prevBottom = c.Pos.Y + half;
        c.Vel.Y += Gravity * h;
        c.Vel *= 1 - 0.1 * h;
        c.Pos += c.Vel * h;
        c.Angle += c.Spin * h;

        if (c.Pos.X < a.Left + CanR) { c.Pos.X = a.Left + CanR; c.Vel.X = Math.Abs(c.Vel.X) * 0.4; }
        else if (c.Pos.X > a.Right - CanR) { c.Pos.X = a.Right - CanR; c.Vel.X = -Math.Abs(c.Vel.X) * 0.4; }
        if (c.Pos.Y < a.Top + CanR) { c.Pos.Y = a.Top + CanR; c.Vel.Y = Math.Abs(c.Vel.Y) * 0.4; }

        double bottom = c.Pos.Y + half;
        double ground = double.NaN;
        if (c.Vel.Y >= 0)
        {
            if (prevBottom <= _shelfY + 2 && bottom >= _shelfY && c.Pos.X >= _shelfX1 && c.Pos.X <= _shelfX2) ground = _shelfY;
            else if (Host.Platforms.FindLanding(c.Pos.X, prevBottom, bottom, out var top)) ground = top.Y;
            else if (bottom >= a.Bottom) ground = a.Bottom;
        }
        if (double.IsNaN(ground))
        {
            c.StillT = 0;
            return;
        }

        c.Pos.Y = ground - half;
        if (c.Vel.Y > 250) PlayThrottled("rim", Math.Min(0.5, c.Vel.Y / 2500), 1.35 + Rng.NextDouble() * 0.2);
        c.Vel.Y = -c.Vel.Y * 0.25;
        if (Math.Abs(c.Vel.Y) < 60) c.Vel.Y = 0;
        c.Vel.X *= 0.85;
        double upright = Math.Round(c.Angle / 90) * 90; // settle standing or lying on its side
        c.Spin = (upright - c.Angle) * 10;
        if (c.Vel.Length < 12 && Math.Abs(upright - c.Angle) < 2)
        {
            c.StillT += h;
            if (c.StillT > 0.4)
            {
                c.Resting = true;
                c.Vel = default;
                c.Spin = 0;
                c.Angle = upright;
            }
        }
        else
        {
            c.StillT = 0;
        }
    }

    void ResolveBallCan(Can c)
    {
        Vec2 d = c.Pos - _ball.Pos;
        double dist = d.Length, min = BallR + CanR;
        if (dist >= min || dist < 1e-6) return;
        Vec2 n = d / dist;
        double overlap = min - dist;
        double rel = Vec2.Dot(_ball.Vel - c.Vel, n);

        if (!c.Dynamic || c.Resting)
        {
            if (rel < 50)
            {
                _ball.Pos -= n * overlap; // a gentle touch just rests against the can
                if (rel > 0)
                {
                    _ball.Vel -= n * (rel * 1.3);
                    if (rel > 8) Wobble(c);
                }
                return;
            }
            Wake(c);
        }

        double total = BallMass + CanMass;
        _ball.Pos -= n * (overlap * CanMass / total);
        c.Pos += n * (overlap * BallMass / total);
        if (rel <= 0) return;
        double impulse = 1.35 * rel / (1 / BallMass + 1 / CanMass);
        _ball.Vel -= n * (impulse / BallMass);
        c.Vel += n * (impulse / CanMass);
        c.Spin += (n.X * _ball.Vel.Y - n.Y * _ball.Vel.X) * 0.4 + (Rng.NextDouble() - 0.5) * 300;
        _ball.Wake();
        PlayThrottled("rim", Math.Min(0.8, rel / 1400), 1.2 + Rng.NextDouble() * 0.3);
    }

    void ResolveCans(Can a, Can b)
    {
        if (a.FadeT > 0 || b.FadeT > 0 || a.Landing || b.Landing) return;
        bool aMoving = a.Dynamic && !a.Resting, bMoving = b.Dynamic && !b.Resting;
        if (!aMoving && !bMoving) return;
        Vec2 d = b.Pos - a.Pos;
        double dist = d.Length, min = CanR * 2;
        if (dist >= min || dist < 1e-6) return;
        Vec2 n = d / dist;
        double overlap = min - dist;
        double rel = Vec2.Dot(a.Vel - b.Vel, n);
        if (!aMoving && rel > 60) { Wake(a); aMoving = true; }
        if (!bMoving && rel > 60) { Wake(b); bMoving = true; }

        if (aMoving && bMoving)
        {
            a.Pos -= n * (overlap / 2);
            b.Pos += n * (overlap / 2);
            if (rel > 0)
            {
                double j = 1.3 * rel / 2;
                a.Vel -= n * j;
                b.Vel += n * j;
            }
        }
        else if (aMoving)
        {
            a.Pos -= n * overlap;
            if (rel > 0) a.Vel -= n * (rel * 1.3);
            if (rel > 8) Wobble(b); // brushed, not knocked
        }
        else
        {
            b.Pos += n * overlap;
            if (rel > 0) b.Vel += n * (rel * 1.3);
            if (rel > 8) Wobble(a);
        }
    }

    // ------------------------------------------------------------------ visuals

    void Draw()
    {
        _ballSprite.Set(_ball.Pos, _ball.Angle);
        foreach (var c in _cans) c.Sprite.Set(c.Pos, c.Angle + c.Wobble);
    }

    void PlayThrottled(string name, double vol, double pitch = 1)
    {
        if (_lastSound.TryGetValue(name, out double t) && _time - t < 0.05) return;
        _lastSound[name] = _time;
        Host.Sound.Play(name, vol, pitch);
    }

    static LinearGradientBrush Horizontal(params (Color c, double at)[] stops)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        };
        foreach (var (c, at) in stops) brush.GradientStops.Add(new GradientStop(c, at));
        return brush;
    }

    /// <summary>A can, standing on the origin's level, with (for the golden one) the sheen and star its glint moves.</summary>
    static Sprite MakeCan(Color label, bool golden, out TranslateTransform? sheen, out ScaleTransform? starScale)
    {
        var s = new Sprite { IsHitTestVisible = false };
        s.Rotor.Children.Add(Art.At(new Rectangle
        {
            Width = CanW, Height = CanH, RadiusX = 3, RadiusY = 3, Stroke = Art.Brush("#4A4F57"), StrokeThickness = 1,
            Fill = Horizontal((Color.FromRgb(150, 156, 165), 0), (Color.FromRgb(235, 238, 242), 0.35), (Color.FromRgb(120, 126, 135), 1)),
        }, -CanW / 2, -CanH / 2));
        var band = golden ? Gold : label;
        s.Rotor.Children.Add(Art.At(new Rectangle
        {
            Width = CanW, Height = 20,
            Fill = Horizontal((Art.Blend(band, Colors.White, 0.25), 0), (band, 0.4), (Art.Blend(band, Colors.Black, 0.3), 1)),
        }, -CanW / 2, -9));
        s.Rotor.Children.Add(Art.At(new Rectangle { Width = 4, Height = CanH - 6, Fill = Art.Brush(90, 255, 255, 255) }, -CanW / 2 + 6, -CanH / 2 + 3));
        s.Rotor.Children.Add(Art.At(new Ellipse
        {
            Width = CanW - 2, Height = 5, Fill = Art.Brush("#D5D9DE"), Stroke = Art.Brush("#5C626B"), StrokeThickness = 0.8,
        }, -CanW / 2 + 1, -CanH / 2 - 1));
        sheen = null;
        starScale = null;
        if (!golden) return s;

        // a slanted streak of light that the glint sweeps across the can, clipped to it
        sheen = new TranslateTransform(-CanW, 0);
        var streak = new Canvas { Clip = new RectangleGeometry(new Rect(-CanW / 2, -CanH / 2, CanW, CanH)) };
        streak.Children.Add(Art.At(new Rectangle
        {
            Width = 9, Height = CanH + 16, Fill = Art.Brush(140, 255, 255, 255), RenderTransformOrigin = RelativePoint.TopLeft,
            RenderTransform = new TransformGroup { Children = { new RotateTransform(18), sheen } },
        }, -4.5, -CanH / 2 - 8));
        s.Rotor.Children.Add(streak);
        starScale = new ScaleTransform(1, 1);
        var star = new Canvas { RenderTransformOrigin = RelativePoint.TopLeft, RenderTransform = starScale };
        star.Children.Add(Art.PathOf(Art.StarPath(0, 0, 6, 2.6), Brushes.White));
        s.Rotor.Children.Add(Art.At(star, 0, 1));
        return s;
    }

    static Sprite MakeBall(double r)
    {
        var s = new Sprite { IsHitTestVisible = false };
        s.Children.Insert(0, Art.Circle(2, 4, r, Art.Brush(55, 0, 0, 0)));
        var body = new RadialGradientBrush { GradientOrigin = new RelativePoint(0.35, 0.3, RelativeUnit.Relative) };
        body.GradientStops.Add(new GradientStop(Colors.White, 0));
        body.GradientStops.Add(new GradientStop(Color.FromRgb(0xE2, 0xE0, 0xD8), 0.7));
        body.GradientStops.Add(new GradientStop(Color.FromRgb(0xA9, 0xA5, 0x9A), 1));
        s.Rotor.Children.Add(Art.Circle(0, 0, r, body, Art.Brush("#9A968C"), 1));
        double k = r * 0.62;
        string F(double v) => Art.F(v);
        var seams = Art.PathOf(
            $"M{F(-k)},{F(-r * 0.8)} Q{F(-r * 0.2)},0 {F(-k)},{F(r * 0.8)} M{F(k)},{F(-r * 0.8)} Q{F(r * 0.2)},0 {F(k)},{F(r * 0.8)}",
            null, Art.Brush("#D62828"), 1.6);
        seams.StrokeDashArray = new AvaloniaList<double> { 1.2, 1.4 };
        s.Rotor.Children.Add(seams);
        return s;
    }

    public override void DemoTick()
    {
        if (_holding || _inFlight || _clearIn >= 0 || _judgeIn >= 0 || _cans.Any(c => c.Landing)) return;
        if (_gameOver) NewGame();
        if (_ballsLeft <= 0) return;
        var target = _cans.Where(c => !c.Down).OrderByDescending(c => c.Pos.Y).FirstOrDefault();
        if (target == null) return;
        var from = _ball.Pos;
        double T = 0.75 + Rng.NextDouble() * 0.15;
        var aim = target.Pos + new Vec2(0, -6);
        var v = new Vec2((aim.X - from.X) / T, (aim.Y - from.Y - 0.5 * Gravity * T * T) / T) * 1.02;
        Throw(v);
    }
}
