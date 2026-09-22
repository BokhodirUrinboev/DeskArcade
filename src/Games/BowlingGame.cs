using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>
/// Ten-pin bowling on a lane along the bottom of the screen, seen from above. Press on the ball, drag back
/// away from the pins and let go: the drag sets the line and the power. Ten frames with strikes, spares and
/// the 10th-frame bonus balls, scored on a sheet above the lane (see BowlingScore.cs). Over the LAN it is
/// a race: both players bowl a game and the higher score wins.
/// </summary>
public sealed class BowlingGame : MiniGame
{
    /// <summary>The longest drag that counts, and how far in from the lane's left end the ball waits.</summary>
    public const double MaxPull = 170, StartInset = 36;
    const double LaneW = 110, GutterW = 24, Approach = 70, Pit = 44, MaxLane = 1500;
    const double BallR = 11.5, PinR = 6.5, PinSpacing = 31, RowGap = 27, BallMass = 4, PinDrag = 10;
    const double MinPull = 14, MaxSpeed = 2000, MinSpeed = 380, MaxAngle = 12, Reach = 34;
    const double KnockDist = 3, PitWait = 1.5, RollTimeout = 12, FadeOut = 0.35, FadeIn = 0.3;
    const double FrameW = 44, TenthW = 62, MarkH = 17, TotalH = 22;

    static readonly Color Gold = Color.FromRgb(255, 209, 102);
    static readonly Color[] Confetti = { Gold, Colors.White, Color.FromRgb(239, 71, 111), Color.FromRgb(77, 163, 255) };
    static readonly IBrush Ink = Art.Brush("#1C1F26");
    static readonly IBrush SheetText = Art.Brush("#F2F4F8");

    enum Phase { Ready, Rolling, Sweep }

    sealed class Pin
    {
        public required Disc Body;
        public required Sprite Sprite;
        public required Control Standing;
        public required Control Toppled;
        public Vec2 Home;
        public bool Knocked, Gone;
        public double Angle;
    }

    readonly DiscTable _table = new() { Restitution = 0.75, CushionRestitution = 0.45, Friction = 35, Damping = 0.05 };
    readonly BowlingScore _score = new();
    readonly Canvas _laneLayer = new() { IsHitTestVisible = false };
    readonly Canvas _pinLayer = new() { IsHitTestVisible = false };
    readonly Canvas _sheet = new() { IsHitTestVisible = false };
    readonly Line _guide = new()
    {
        Stroke = Art.Brush(200, 255, 255, 255), StrokeThickness = 2.5, StrokeDashArray = new AvaloniaList<double> { 3, 3 },
        IsVisible = false, IsHitTestVisible = false,
    };
    readonly Ellipse _guideEnd = new() { Width = 10, Height = 10, Stroke = Art.Brush(220, 255, 255, 255), StrokeThickness = 2, IsVisible = false, IsHitTestVisible = false };
    readonly Pin[] _pins = new Pin[BowlingScore.Pins];
    readonly Disc _ball;
    Sprite _ballSprite = MakeBall();
    readonly TextBlock[][] _marks = new TextBlock[BowlingScore.Frames][];
    readonly TextBlock[] _totals = new TextBlock[BowlingScore.Frames];
    readonly Rectangle[] _frameBoxes = new Rectangle[BowlingScore.Frames];
    readonly Dictionary<string, double> _lastSound = new();

    // lane geometry, in arena coordinates
    double _laneLeft, _laneEnd, _laneTop, _laneBottom, _cy, _startX, _headX;
    double _time, _acc, _rollT, _pitT, _sweepT, _ballAngle;
    int _strikeRun;
    bool _freshBefore;
    bool _placed, _aiming, _active, _gameOver, _resetRack, _ballInPit;
    Phase _phase;
    Vec2 _pull;

    public BowlingGame(IGameHost host) : base(host)
    {
        _ball = _table.Add(default, BallR, BallMass, -1);
        for (int i = 0; i < _pins.Length; i++)
        {
            var body = _table.Add(default, PinR, 1, i);
            body.Drag = PinDrag;
            var standing = MakeStandingPin();
            var toppled = MakeToppledPin();
            toppled.IsVisible = false;
            var sprite = new Sprite { IsHitTestVisible = false };
            sprite.Rotor.Children.Add(toppled);
            sprite.Children.Add(standing);
            _pinLayer.Children.Add(sprite);
            _pins[i] = new Pin { Body = body, Sprite = sprite, Standing = standing, Toppled = toppled };
        }
        _table.Collided += OnCollided;
        _table.Cushion += (d, speed) =>
        {
            if (speed > 120 && d != _ball) PlayThrottled("board", Math.Min(0.4, speed / 3000), 0.8);
        };
        BuildSheet();

        Layer.Children.Add(_laneLayer);
        Layer.Children.Add(_sheet);
        Layer.Children.Add(_pinLayer);
        Layer.Children.Add(_guide);
        Layer.Children.Add(_guideEnd);
        Layer.Children.Add(_ballSprite);
    }

    public override string Id => "bowling";
    public override string Title => "Bowling";
    public override bool SupportsLan => true;
    public override (int Score, bool Active)? Race => (_score.Total, _active);

    public override void StartRace()
    {
        if (_active) return;
        NewGame();
        StartGame();
    }

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        s.Rotor.Children.Add(Art.Circle(-3, 3, 7.5, Art.Brush(Art.Blend(Themes.Current.Mine, Colors.Black, 0.35)), Ink, 1));
        s.Rotor.Children.Add(Art.Circle(-5, 1, 1.3, Ink));
        s.Rotor.Children.Add(Art.Circle(-1.5, 0.5, 1.3, Ink));
        s.Rotor.Children.Add(Art.Circle(-3, 4.5, 1.5, Ink));
        foreach (var (x, y) in new[] { (7.0, -8.0), (4.0, -4.5), (10.0, -4.5) })
        {
            s.Rotor.Children.Add(Art.Circle(x, y, 3, Brushes.White, Art.Brush("#8A9099"), 0.7));
            s.Rotor.Children.Add(Art.Circle(x, y, 1.6, null, Art.Brush("#D62839"), 0.9));
        }
        return s;
    }

    public override void ThemeChanged()
    {
        int at = Layer.Children.IndexOf(_ballSprite);
        var ball = MakeBall();
        ball.IsVisible = _ballSprite.IsVisible;
        Layer.Children[at] = ball;
        _ballSprite = ball;
        Draw();
    }

    public override HudInfo Hud => new(
        _score.Total.ToString(),
        _gameOver ? L.T("Game over · drag back from the ball to bowl again")
            : !_active ? L.T("Drag back from the ball and let go to bowl")
            : L.F("Frame {0} · ball {1} · pins {2}", _score.Frame + 1, _score.Ball + 1, _score.PinsStanding),
        L.F("Best {0}", Host.Stats.Get("bowling.best")));

    // ------------------------------------------------------------------ lane

    public override void Layout()
    {
        var a = Host.Arena;
        double oldLeft = _laneLeft, oldEnd = _laneEnd, oldCy = _cy;
        bool first = !_placed;

        (_laneLeft, double length) = LaneSpan(a);
        _laneEnd = _laneLeft + length - Pit;
        _laneBottom = a.Bottom - 10 - GutterW;
        _laneTop = _laneBottom - LaneW;
        _cy = (_laneTop + _laneBottom) / 2;
        _startX = _laneLeft + StartInset;
        _headX = _laneEnd - 28 - 3 * RowGap;
        _table.Bounds = new Rect(_laneLeft, _laneTop - GutterW, length, LaneW + GutterW * 2);

        for (int i = 0; i < _pins.Length; i++)
        {
            var (row, col) = RowCol(i);
            var pin = _pins[i];
            var home = new Vec2(_headX + row * RowGap, _cy + (col - row / 2.0) * PinSpacing);
            if (!first) pin.Body.Pos = pin.Knocked ? pin.Body.Pos + new Vec2(_laneEnd - oldEnd, _cy - oldCy) : home;
            pin.Home = home;
        }

        if (first)
        {
            _placed = true;
            NewGame();
        }
        else if (_phase == Phase.Rolling && !_ballInPit)
        {
            // the ball keeps its place along the lane
            double k = (_ball.Pos.X - oldLeft) / Math.Max(1, oldEnd - oldLeft);
            _ball.Pos = new Vec2(_laneLeft + k * (_laneEnd - _laneLeft), _ball.Pos.Y + _cy - oldCy);
        }
        else if (_phase == Phase.Ready)
        {
            _ball.Pos = new Vec2(_startX, Clamp(_ball.Pos.Y + _cy - oldCy, _laneTop + BallR + 4, _laneBottom - BallR - 4));
        }

        DrawLane(length);
        PlaceSheet();
        Draw();
        Host.HudChanged();
    }

    public override void Deactivate()
    {
        _aiming = false;
        HideGuide();
    }

    /// <summary>Where the lane starts and how long it is (pit included): centred, but on a narrow screen
    /// moved right and shortened so a full-power drag still fits between the ball and the screen edge.</summary>
    public static (double Left, double Length) LaneSpan(Rect a)
    {
        double length = Math.Min(a.Width - 80, MaxLane);
        double left = Math.Max(a.Left + (a.Width - length) / 2, a.Left + MaxPull + 24 - StartInset);
        return (left, Math.Min(length, a.Right - 10 - left));
    }

    /// <summary>Pins 1-10 in the standard triangle: row 0 is the headpin, row 3 the back row (7-10).</summary>
    static (int Row, int Col) RowCol(int i) => i switch
    {
        0 => (0, 0),
        1 or 2 => (1, i - 1),
        3 or 4 or 5 => (2, i - 3),
        _ => (3, i - 6),
    };

    void NewGame()
    {
        _score.Reset();
        _gameOver = false;
        _active = false;
        _strikeRun = 0;
        RackAll();
        ReturnBall(_cy);
        UpdateSheet();
        Host.HudChanged();
    }

    void StartGame()
    {
        _active = true;
        Host.RoundStarted();
        Host.HudChanged();
    }

    void RackAll()
    {
        foreach (var pin in _pins)
        {
            pin.Knocked = pin.Gone = false;
            pin.Body.Sunk = false;
            pin.Body.Pos = pin.Home;
            pin.Body.Vel = default;
            pin.Standing.IsVisible = true;
            pin.Toppled.IsVisible = false;
            pin.Sprite.IsVisible = true;
            pin.Sprite.Opacity = 1;
        }
    }

    void ReturnBall(double y)
    {
        _phase = Phase.Ready;
        _ball.Sunk = _ball.Ghost = false;
        _ball.Vel = default;
        _ball.Pos = new Vec2(_startX, Clamp(y, _laneTop + BallR + 4, _laneBottom - BallR - 4));
        _ballInPit = false;
        _ballSprite.IsVisible = true;
    }

    // ------------------------------------------------------------------ input

    public override void CollectHitShapes(List<HitShape> into)
    {
        if (_phase == Phase.Ready) into.Add(HitShape.Circle(_ball.Pos, Reach));
    }

    public override bool PointerDown(Vec2 p, bool right)
    {
        if (_phase != Phase.Ready || (p - _ball.Pos).Length > Reach) return false;
        if (_gameOver)
        {
            NewGame(); // the ball jumps back to the start: grab it again to bowl
            return false;
        }
        _aiming = true;
        _pull = default;
        return true;
    }

    public override void PointerUp(Vec2 p)
    {
        if (!_aiming) return;
        _aiming = false;
        HideGuide();
        if (Aim(_pull, out var dir, out double speed)) Roll(dir, speed);
    }

    public override void PointerCancel()
    {
        if (!_aiming) return;
        _aiming = false; // cut off mid-drag: no ball bowled
        HideGuide();
    }

    /// <summary>A drag back from the ball, turned into a line down the lane (within a few degrees) and a speed.</summary>
    static bool Aim(Vec2 pull, out Vec2 dir, out double speed)
    {
        dir = default;
        speed = 0;
        double len = pull.Length;
        if (len < MinPull || -pull.X / len < 0.3) return false; // too short, or not pulled away from the pins
        double deg = Math.Clamp(Math.Atan2(-pull.Y, -pull.X) * 180 / Math.PI, -MaxAngle, MaxAngle);
        var (x, y) = Art.Polar(1, deg);
        dir = new Vec2(x, y);
        speed = MinSpeed + (MaxSpeed - MinSpeed) * Math.Pow(Math.Min(len, MaxPull) / MaxPull, 1.2);
        return true;
    }

    void Roll(Vec2 dir, double speed)
    {
        if (_phase != Phase.Ready || _gameOver) return;
        if (!_active) StartGame();
        _freshBefore = _score.FreshRack;
        _ball.Vel = dir * speed;
        _phase = Phase.Rolling;
        _rollT = 0;
        _pitT = -1;
        Host.Sound.Play("kick", 0.35 + 0.3 * speed / MaxSpeed, 0.7);
        Host.HudChanged();
    }

    public override void Summon(Vec2 p)
    {
        if (_phase != Phase.Ready || _aiming) return;
        _ball.Pos = new Vec2(_startX, Clamp(p.Y, _laneTop + BallR + 4, _laneBottom - BallR - 4));
        Draw();
    }

    // ------------------------------------------------------------------ simulation

    public override bool Update(double dt)
    {
        _time += dt;
        if (_aiming)
        {
            var pull = Host.Pointer - _ball.Pos;
            if (pull.Length > MaxPull) pull *= MaxPull / pull.Length;
            _pull = pull;
            UpdateGuide();
        }

        if (_phase == Phase.Rolling)
        {
            _rollT += dt;
            _acc = Math.Min(_acc + dt, 0.1);
            while (_acc >= DiscTable.Step)
            {
                _acc -= DiscTable.Step;
                _table.StepOnce(DiscTable.Step);
                CheckBall();
            }
            _ballAngle += _ball.Vel.X * dt * 0.6;
            CheckPins();
            if (_pitT >= 0) _pitT += dt;
            if (_table.AllStill || _pitT >= PitWait || _rollT >= RollTimeout) Resolve();
        }
        else if (_phase == Phase.Sweep)
        {
            Sweep(dt);
        }

        Draw();
        return _aiming || _phase != Phase.Ready;
    }

    /// <summary>A ball over the edge drops into the gutter and runs straight down it; past the pins it drops into the pit.</summary>
    void CheckBall()
    {
        if (_ball.Sunk) return;
        if (!_ball.Ghost && _ball.Pos.X < _laneEnd && (_ball.Pos.Y < _laneTop || _ball.Pos.Y > _laneBottom))
        {
            _ball.Ghost = true;
            _ball.Pos.Y = _ball.Pos.Y < _cy ? _laneTop - GutterW / 2 : _laneBottom + GutterW / 2;
            _ball.Vel = new Vec2(Math.Max(220, Math.Abs(_ball.Vel.X) * 0.9), 0);
            PlayThrottled("thunk", 0.3, 0.6);
        }
        if (_ball.Pos.X > _laneEnd + BallR)
        {
            _ball.Sunk = true;
            _ballInPit = true;
            _ballSprite.IsVisible = false;
            _pitT = 0;
            PlayThrottled("bounce", 0.35, 0.6);
        }
    }

    void CheckPins()
    {
        foreach (var pin in _pins)
        {
            if (pin.Knocked || pin.Gone) continue;
            var p = pin.Body.Pos;
            bool offLane = p.Y < _laneTop || p.Y > _laneBottom || p.X > _laneEnd;
            if (!offLane && (p - pin.Home).Length <= KnockDist) continue;
            pin.Knocked = true;
            var v = pin.Body.Vel;
            pin.Angle = (v.LengthSquared > 1 ? Math.Atan2(v.Y, v.X) * 180 / Math.PI : Rng.NextDouble() * 360) + (Rng.NextDouble() - 0.5) * 40;
            pin.Standing.IsVisible = false;
            pin.Toppled.IsVisible = true;
        }
    }

    void OnCollided(Disc a, Disc b, double speed)
    {
        if (speed < 60) return;
        bool ball = a == _ball || b == _ball;
        if (ball) PlayThrottled("thunk", Math.Min(0.8, speed / 1500), 0.75 + Rng.NextDouble() * 0.1);
        else PlayThrottled("click", Math.Min(0.7, speed / 1400), 0.55 + Rng.NextDouble() * 0.15);
    }

    /// <summary>The roll has settled: count the pins it knocked down and score the ball.</summary>
    void Resolve()
    {
        CheckPins();
        int knocked = 0;
        bool headDown = false;
        foreach (var pin in _pins)
        {
            pin.Body.Vel = default;
            if (pin.Gone || !pin.Knocked) continue;
            knocked++;
            if (pin.Body.Tag == 0) headDown = true;
        }
        _ball.Vel = default;
        _ball.Sunk = true;
        _ballSprite.IsVisible = false;
        bool gutter = _ball.Ghost && knocked == 0;

        var kind = _score.Roll(knocked);
        Host.Stats.Add("bowling.pins", knocked);
        bool strike = kind == BowlingScore.Kind.Strike, spare = kind == BowlingScore.Kind.Spare;
        var at = new Vec2(_headX + RowGap * 1.5, _table.Bounds.Top - 40);

        if (strike)
        {
            _strikeRun++;
            Host.Stats.Add("bowling.strikes");
            bool turkey = _strikeRun % 3 == 0;
            if (turkey) Host.Stats.Add("bowling.turkeys");
            Host.Fx.Popup(at, turkey ? L.T("TURKEY!") : L.T("STRIKE!"), Gold, 36, 1.5, turkey ? L.T("three strikes in a row") : null);
            Host.Fx.Burst(at + new Vec2(0, 30), Confetti, turkey ? 40 : 26, 420, 600, 6, 1.0);
            Host.Sound.Play(turkey ? "best" : "score", 0.8);
        }
        else
        {
            _strikeRun = 0;
            if (spare)
            {
                Host.Stats.Add("bowling.spares");
                Host.Fx.Popup(at, L.T("SPARE!"), Colors.White, 32, 1.3);
                Host.Sound.Play("star", 0.6);
            }
            else if (gutter)
            {
                Host.Fx.Popup(at, L.T("Gutter"), Color.FromRgb(255, 160, 160), 26, 1.1);
            }
            else
            {
                bool split = _freshBefore && headDown && IsSplit();
                Host.Fx.Popup(at, knocked.ToString(), Colors.White, 28, 1.0, split ? L.T("split") : null);
            }
        }

        _resetRack = _score.GameOver || _score.FreshRack;
        _phase = Phase.Sweep;
        _sweepT = 0;
        UpdateSheet();
        if (_score.GameOver) EndGame();
        Host.HudChanged();
    }

    /// <summary>Standing pins in two or more separate groups (no neighbour between them) after the headpin went down.</summary>
    bool IsSplit()
    {
        Span<int> group = stackalloc int[BowlingScore.Pins];
        group.Fill(-1);
        int groups = 0;
        Span<int> stack = stackalloc int[BowlingScore.Pins];
        for (int i = 0; i < _pins.Length; i++)
        {
            if (_pins[i].Knocked || _pins[i].Gone || group[i] >= 0) continue;
            int top = 0;
            stack[top++] = i;
            group[i] = groups;
            while (top > 0)
            {
                int k = stack[--top];
                for (int j = 0; j < _pins.Length; j++)
                {
                    if (group[j] >= 0 || _pins[j].Knocked || _pins[j].Gone) continue;
                    if ((_pins[j].Home - _pins[k].Home).Length > PinSpacing * 1.1) continue;
                    group[j] = groups;
                    stack[top++] = j;
                }
            }
            groups++;
        }
        return groups >= 2;
    }

    /// <summary>The sweep: knocked pins fade away; a fresh rack fades back in; then the ball comes back.</summary>
    void Sweep(double dt)
    {
        double before = _sweepT;
        _sweepT += dt;
        if (_sweepT < FadeOut)
        {
            double k = 1 - _sweepT / FadeOut;
            foreach (var pin in _pins)
                if (pin.Knocked && !pin.Gone || _resetRack) pin.Sprite.Opacity = k;
            return;
        }
        if (before < FadeOut)
        {
            foreach (var pin in _pins)
            {
                if (_resetRack || pin.Gone) continue;
                if (!pin.Knocked)
                {
                    pin.Body.Pos = pin.Home; // a pin that only wobbled is set straight again
                    continue;
                }
                pin.Gone = true; // swept off the deck; the standing pins wait for the second ball
                pin.Body.Sunk = true;
                pin.Sprite.IsVisible = false;
            }
            if (_resetRack)
            {
                RackAll();
                foreach (var pin in _pins) pin.Sprite.Opacity = 0;
            }
        }
        double t = Math.Min(1, (_sweepT - FadeOut) / FadeIn);
        if (_resetRack)
            foreach (var pin in _pins) pin.Sprite.Opacity = t;
        if (t < 1) return;
        ReturnBall(_cy);
        Host.HudChanged();
    }

    void EndGame()
    {
        int total = _score.Total;
        _active = false;
        _gameOver = true;
        Host.RoundEnded(total);
        long before = Host.Stats.Get("bowling.best");
        Host.Stats.Add("bowling.games");
        Host.Stats.Max("bowling.best", total);
        bool best = total > before;
        var a = Host.Arena;
        var at = new Vec2(a.Center.X, a.Top + a.Height * 0.3);
        Host.Fx.Popup(at, best ? L.T("NEW BEST!") : L.T("GAME OVER"), best ? Gold : Colors.White, 38, 2.4, L.F("{0} points", total));
        if (best)
        {
            Host.Fx.Burst(at, Confetti, 40, 520, 700, 7, 1.1);
            Host.Sound.Play("best", 0.8);
        }
        else
        {
            Host.Sound.Play("done", 0.5);
        }
    }

    // ------------------------------------------------------------------ visuals

    void Draw()
    {
        _ballSprite.Set(_ball.Pos, _ballAngle);
        foreach (var pin in _pins)
            if (!pin.Gone) pin.Sprite.Set(pin.Body.Pos, pin.Angle);
    }

    void UpdateGuide()
    {
        if (!Aim(_pull, out var dir, out double speed))
        {
            HideGuide();
            return;
        }
        double reach = 90 + 260 * (speed - MinSpeed) / (MaxSpeed - MinSpeed);
        var end = _ball.Pos + dir * (BallR + reach);
        _guide.StartPoint = (_ball.Pos + dir * (BallR + 4)).ToPoint();
        _guide.EndPoint = end.ToPoint();
        Canvas.SetLeft(_guideEnd, end.X - 5);
        Canvas.SetTop(_guideEnd, end.Y - 5);
        _guide.IsVisible = _guideEnd.IsVisible = true;
    }

    void HideGuide() => _guide.IsVisible = _guideEnd.IsVisible = false;

    void DrawLane(double length)
    {
        _laneLayer.Children.Clear();
        var box = _table.Bounds;
        double laneLen = _laneEnd - _laneLeft;

        // gutters above and below, then the pit behind the pins
        var gutterFill = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative) };
        gutterFill.GradientStops.Add(new GradientStop(Color.FromRgb(58, 62, 70), 0));
        gutterFill.GradientStops.Add(new GradientStop(Color.FromRgb(96, 102, 112), 0.5));
        gutterFill.GradientStops.Add(new GradientStop(Color.FromRgb(58, 62, 70), 1));
        _laneLayer.Children.Add(Art.At(new Rectangle { Width = length, Height = box.Height + 6, RadiusX = 6, RadiusY = 6, Fill = Art.Brush(90, 0, 0, 0) }, box.X + 2, box.Y + 1));
        _laneLayer.Children.Add(Art.At(new Rectangle { Width = laneLen, Height = GutterW, RadiusX = 5, RadiusY = 5, Fill = gutterFill }, _laneLeft, box.Top));
        _laneLayer.Children.Add(Art.At(new Rectangle { Width = laneLen, Height = GutterW, RadiusX = 5, RadiusY = 5, Fill = gutterFill }, _laneLeft, _laneBottom));
        _laneLayer.Children.Add(Art.At(new Rectangle { Width = Pit, Height = box.Height, Fill = Art.Brush("#121418"), Stroke = Art.Brush("#2A2E36"), StrokeThickness = 1 }, _laneEnd, box.Top));

        var wood = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative) };
        wood.GradientStops.Add(new GradientStop(Color.FromRgb(214, 170, 112), 0));
        wood.GradientStops.Add(new GradientStop(Color.FromRgb(232, 196, 140), 0.5));
        wood.GradientStops.Add(new GradientStop(Color.FromRgb(222, 180, 122), 1));
        _laneLayer.Children.Add(Art.At(new Rectangle { Width = laneLen, Height = LaneW, Fill = wood }, _laneLeft, _laneTop));
        _laneLayer.Children.Add(Art.At(new Rectangle { Width = Approach, Height = LaneW, Fill = Art.Brush(40, 255, 255, 255) }, _laneLeft, _laneTop));

        // the boards, the foul line, the target arrows and the pin spots
        var boards = new System.Text.StringBuilder();
        for (int i = 1; i < 13; i++)
        {
            double y = _laneTop + i * LaneW / 13;
            boards.Append("M").Append(Art.F(_laneLeft)).Append(',').Append(Art.F(y)).Append(" L").Append(Art.F(_laneEnd)).Append(',').Append(Art.F(y)).Append(' ');
        }
        _laneLayer.Children.Add(Art.PathOf(boards.ToString(), null, Art.Brush(55, 120, 70, 20), 0.8));
        double foul = _laneLeft + Approach;
        _laneLayer.Children.Add(Art.PathOf($"M{Art.F(foul)},{Art.F(_laneTop)} L{Art.F(foul)},{Art.F(_laneBottom)}", null, Art.Brush("#3A2A1A"), 2.5));
        foreach (int k in new[] { -3, -2, -1, 0, 1, 2, 3 })
        {
            double x = foul + Math.Min(laneLen * 0.22, 260) + (3 - Math.Abs(k)) * 16;
            double y = _cy + k * LaneW / 8;
            _laneLayer.Children.Add(Art.PathOf($"M{Art.F(x + 8)},{Art.F(y)} L{Art.F(x - 6)},{Art.F(y - 4)} L{Art.F(x - 6)},{Art.F(y + 4)} Z", Art.Brush(170, 110, 50, 20)));
        }
        foreach (int k in new[] { -2, 0, 2 })
            _laneLayer.Children.Add(Art.Circle(_laneLeft + 18, _cy + k * LaneW / 6, 1.6, Art.Brush(140, 110, 50, 20)));
        foreach (var pin in _pins)
            _laneLayer.Children.Add(Art.Circle(pin.Home.X, pin.Home.Y, 2.5, Art.Brush(90, 110, 50, 20)));
    }

    void BuildSheet()
    {
        double w = 9 * FrameW + TenthW, h = MarkH + TotalH;
        _sheet.Children.Add(Art.At(new Rectangle { Width = w + 8, Height = h + 8, RadiusX = 6, RadiusY = 6, Fill = Art.Brush(170, 16, 20, 28) }, -4, -4));
        double x = 0;
        for (int f = 0; f < BowlingScore.Frames; f++)
        {
            bool tenth = f == BowlingScore.Frames - 1;
            double fw = tenth ? TenthW : FrameW;
            var box = new Rectangle { Width = fw, Height = h, Stroke = Art.Brush(150, 255, 255, 255), StrokeThickness = 1 };
            _frameBoxes[f] = box;
            _sheet.Children.Add(Art.At(box, x, 0));
            int cells = tenth ? 3 : 2;
            double cw = tenth ? fw / 3 : fw / 2;
            _marks[f] = new TextBlock[cells];
            for (int c = 0; c < cells; c++)
            {
                // a normal frame has one small box, in the corner; the 10th has three
                if (tenth || c == 1)
                    _sheet.Children.Add(Art.At(new Rectangle { Width = cw, Height = MarkH, Stroke = Art.Brush(110, 255, 255, 255), StrokeThickness = 1 }, x + c * cw, 0));
                var mark = new TextBlock
                {
                    Width = cw, Height = MarkH, FontFamily = Fx.Font, FontSize = 12, FontWeight = FontWeight.Bold,
                    Foreground = SheetText, TextAlignment = TextAlignment.Center,
                };
                _marks[f][c] = mark;
                _sheet.Children.Add(Art.At(mark, x + c * cw, 0));
            }
            var total = new TextBlock
            {
                Width = fw, Height = TotalH, FontFamily = Fx.Font, FontSize = 14, FontWeight = FontWeight.Bold,
                Foreground = SheetText, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            _totals[f] = total;
            _sheet.Children.Add(Art.At(total, x, MarkH + 2));
            x += fw;
        }
    }

    void PlaceSheet()
    {
        double w = 9 * FrameW + TenthW, h = MarkH + TotalH;
        double y = _table.Bounds.Top - 14 - h;
        double x = _laneLeft + 8;
        var hud = Host.HudBounds.Inflate(10);
        if (new Rect(x - 4, y - 4, w + 8, h + 8).Intersects(hud)) x = _laneEnd + Pit - w - 8; // the HUD sits there: use the far end
        Canvas.SetLeft(_sheet, x);
        Canvas.SetTop(_sheet, y);
    }

    void UpdateSheet()
    {
        var totals = _score.RunningTotals();
        for (int f = 0; f < BowlingScore.Frames; f++)
        {
            var marks = _score.Marks(f);
            for (int c = 0; c < marks.Length; c++)
                if (_marks[f][c].Text != marks[c]) _marks[f][c].Text = marks[c];
            string total = totals[f]?.ToString() ?? "";
            if (_totals[f].Text != total) _totals[f].Text = total;
            bool current = !_score.GameOver && f == _score.Frame && _score.Rolls.Count > 0;
            _frameBoxes[f].Fill = current ? Art.Brush(50, 255, 209, 102) : null;
        }
    }

    void PlayThrottled(string name, double vol, double pitch)
    {
        if (_lastSound.TryGetValue(name, out double t) && _time - t < 0.045) return;
        _lastSound[name] = _time;
        Host.Sound.Play(name, vol, pitch);
    }

    static Sprite MakeBall()
    {
        var s = new Sprite { IsHitTestVisible = false };
        var c = Art.Blend(Themes.Current.Mine, Colors.Black, 0.3);
        s.Children.Insert(0, Art.Circle(1.5, 2.5, BallR, Art.Brush(70, 0, 0, 0)));
        var body = new RadialGradientBrush { GradientOrigin = new RelativePoint(0.35, 0.3, RelativeUnit.Relative) };
        body.GradientStops.Add(new GradientStop(Art.Blend(c, Colors.White, 0.4), 0));
        body.GradientStops.Add(new GradientStop(c, 0.6));
        body.GradientStops.Add(new GradientStop(Art.Blend(c, Colors.Black, 0.45), 1));
        s.Rotor.Children.Add(Art.Circle(0, 0, BallR, body, Ink, 1));
        s.Rotor.Children.Add(Art.Circle(-3.5, -3, 1.8, Ink));
        s.Rotor.Children.Add(Art.Circle(1, -3.8, 1.8, Ink));
        s.Rotor.Children.Add(Art.Circle(-1, 3.5, 2.2, Ink));
        s.Children.Add(Art.Circle(-4, -5, 2.5, Art.Brush(90, 255, 255, 255)));
        return s;
    }

    /// <summary>A standing pin from above: the white head with the red neck stripes around it.</summary>
    static Canvas MakeStandingPin()
    {
        var c = new Canvas();
        c.Children.Add(Art.Circle(1, 1.5, PinR, Art.Brush(60, 0, 0, 0)));
        c.Children.Add(Art.Circle(0, 0, PinR, Brushes.White, Art.Brush("#9AA0A8"), 0.8));
        c.Children.Add(Art.Circle(0, 0, PinR * 0.6, null, Art.Brush("#D62839"), 1.6));
        c.Children.Add(Art.Circle(-0.6, -0.6, PinR * 0.3, Art.Brush("#F4F6F8")));
        return c;
    }

    /// <summary>A pin lying on its side, head toward +x.</summary>
    static Canvas MakeToppledPin()
    {
        var c = new Canvas();
        c.Children.Add(Art.At(new Ellipse { Width = 26, Height = 11, Fill = Art.Brush(60, 0, 0, 0) }, -12, -4));
        c.Children.Add(Art.At(new Ellipse { Width = 20, Height = 11, Fill = Brushes.White, Stroke = Art.Brush("#9AA0A8"), StrokeThickness = 0.8 }, -13, -5.5));
        c.Children.Add(Art.At(new Rectangle { Width = 6, Height = 5, Fill = Brushes.White }, 5, -2.5));
        c.Children.Add(Art.PathOf("M6,-2.8 L6,2.8 M8,-2.6 L8,2.6", null, Art.Brush("#D62839"), 1.2));
        c.Children.Add(Art.Circle(12, 0, 3.6, Brushes.White, Art.Brush("#9AA0A8"), 0.8));
        return c;
    }

    // ------------------------------------------------------------------ demo

    public override void DemoTick()
    {
        if (_phase != Phase.Ready || _aiming || Rng.NextDouble() > 0.3) return;
        if (_gameOver) NewGame();
        _ball.Pos.Y = Clamp(_cy + (Rng.NextDouble() - 0.5) * 36, _laneTop + BallR + 4, _laneBottom - BallR - 4);

        Vec2 target;
        if (_score.FreshRack)
        {
            // the pocket: between the headpin and the 3 pin (or the 2 pin, now and then)
            double side = Rng.NextDouble() < 0.8 ? 1 : -1;
            target = new Vec2(_headX, _cy + side * PinSpacing * 0.3);
        }
        else
        {
            // spare: aim at the middle of what is left
            var sum = new Vec2();
            int n = 0;
            foreach (var pin in _pins)
            {
                if (pin.Knocked || pin.Gone) continue;
                sum += pin.Body.Pos;
                n++;
            }
            target = n > 0 ? sum / n : new Vec2(_headX, _cy);
        }
        var aim = (target - _ball.Pos).Normalized();
        double err = (Rng.NextDouble() + Rng.NextDouble() - 1) * 1.6 * Math.PI / 180;
        var dir = new Vec2(aim.X * Math.Cos(err) - aim.Y * Math.Sin(err), aim.X * Math.Sin(err) + aim.Y * Math.Cos(err));
        Roll(dir, 1300 + Rng.NextDouble() * 600);
    }
}
