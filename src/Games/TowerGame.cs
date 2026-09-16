using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>
/// Tower Stack: a block slides back and forth above the tower; click it to drop it. Whatever hangs over
/// the edge is sliced off, so the tower narrows until a block misses. The tower stands on a wide window
/// top (and rides along with it) or on the taskbar.
/// </summary>
public sealed class TowerGame : MiniGame
{
    const double Step = 1.0 / 240, Gravity = 2400, StartW = 170, BlockH = 26, PlinthW = 196, PlinthH = 16;
    const double HitPad = 24, HoverGap = 70, SlideSpan = 210, BaseSpeed = 230, SpeedPerBlock = 11, MaxSpeed = 640;
    const double PerfectPx = 5, WidenPx = 10, SinkMargin = 220, SinkSeconds = 0.35, MinBaseW = 260, FadeIn = 0.15;
    const int PerfectsToWiden = 3;

    static readonly Color Gold = Color.FromRgb(255, 209, 102);
    static readonly Color[] Confetti = { Gold, Color.FromRgb(239, 71, 111), Color.FromRgb(6, 214, 160), Colors.White };

    enum Phase { Ready, Playing, Over }

    sealed class Block
    {
        public required Control El;
        public double X1, X2; // relative to the base centre
        public int Level;     // 0 rests on the plinth
    }

    /// <summary>A cut-off overhang, a missed block or part of a toppled tower.</summary>
    sealed class Piece
    {
        public required Sprite Sprite;
        public Vec2 Pos, Vel;
        public double HalfW, HalfH, Angle, Spin, Age, Life;
    }

    readonly Canvas _pieceLayer = new() { IsHitTestVisible = false };
    readonly Canvas _blockLayer = new() { IsHitTestVisible = false };
    readonly Canvas _plinth = MakePlinth();
    readonly Sprite _mover = new() { IsHitTestVisible = false };
    readonly Canvas _moverBody = new();
    readonly Rectangle _glow = new()
    {
        RadiusX = 10, RadiusY = 10, IsHitTestVisible = false,
        Stroke = Art.Brush(120, 255, 255, 255), StrokeThickness = 1.5, StrokeDashArray = new AvaloniaList<double> { 3, 5 },
    };
    readonly List<Block> _blocks = new();
    readonly List<Piece> _pieces = new();
    readonly Dictionary<string, double> _lastSound = new();

    Phase _phase;
    IntPtr _baseHwnd;
    double _baseX, _baseY, _sink, _sinkT = -1, _time, _acc;
    double _moveX, _moveTop, _moveW, _moveVy, _moveDir = 1, _moverAge, _nextSide = 1, _demoAim;
    Color _moveColor;
    int _height, _streak, _sinks, _seenGen = -1, _demoWait;
    long _bestAtStart;
    bool _placed, _standing, _hasMover, _falling, _free, _demo;

    public TowerGame(IGameHost host) : base(host)
    {
        _mover.Children.Insert(0, _glow);
        _mover.Rotor.Children.Add(_moverBody);
        Layer.Children.Add(_pieceLayer);
        Layer.Children.Add(_blockLayer);
        Layer.Children.Add(_plinth); // above the blocks, so the bottom block sinks into it
        Layer.Children.Add(_mover);
    }

    public override string Id => "tower";
    public override string Title => "Tower Stack";

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        s.Rotor.Children.Add(Art.At(new Rectangle { Width = 18, Height = 3, RadiusX = 1, RadiusY = 1, Fill = Art.Brush("#AEB4BD") }, -9, 7));
        double[] widths = { 16, 13, 11 }, lefts = { -8, -6, -7 };
        for (int i = 0; i < 3; i++)
            s.Rotor.Children.Add(Art.At(new Rectangle { Width = widths[i], Height = 4, RadiusX = 1, RadiusY = 1, Fill = Art.Brush(BlockColor(i * 3)) },
                lefts[i], 3 - i * 4));
        s.Rotor.Children.Add(Art.At(new Rectangle { Width = 10, Height = 4, RadiusX = 1, RadiusY = 1, Fill = Art.Brush(BlockColor(9)) }, 0, -12));
        return s;
    }

    public override HudInfo Hud => new(
        _height.ToString(),
        _phase == Phase.Over ? L.T("Game over · click the block to play again") : L.F("Height {0} · click the block to drop it", _height),
        L.F("Best {0}", Host.Stats.Get("tower.best")));

    double PlinthTop => _baseY - PlinthH;
    double TopRelOf(Block b) => -(b.Level + 1) * BlockH + _sink;
    double TowerTopRel => _blocks.Count > 0 ? TopRelOf(_blocks[^1]) : 0;
    double HoverTopRel => Math.Max(TowerTopRel - HoverGap - BlockH, Host.Arena.Top + 4 - PlinthTop);
    double MoverTopRel => _falling ? _moveTop : HoverTopRel;
    Rect MoverRect => new(_baseX + _moveX - _moveW / 2, PlinthTop + MoverTopRel, _moveW, BlockH);
    double SlideSpeed => Math.Min(MaxSpeed, BaseSpeed + _height * SpeedPerBlock);
    bool TooHigh => _standing && _blocks.Count >= 2 && PlinthTop + TowerTopRel < Host.Arena.Top + SinkMargin;

    // ------------------------------------------------------------------ game flow

    public override void Layout()
    {
        if (!_placed || _phase != Phase.Playing)
        {
            _placed = true;
            NewGame(); // nothing at stake: pick the best spot on the current screen
            return;
        }
        Reseat();
        Draw();
        Host.HudChanged();
    }

    public override void Deactivate()
    {
        foreach (var piece in _pieces) _pieceLayer.Children.Remove(piece.Sprite);
        _pieces.Clear();
        if (_sinkT >= 0) FinishSink();
        if (!_falling) return;
        _falling = false; // pausing by switching games doesn't cost the block
        if (_free) _phase = Phase.Ready;
        _free = false;
    }

    void NewGame()
    {
        foreach (var b in _blocks) _blockLayer.Children.Remove(b.El);
        _blocks.Clear();
        _phase = Phase.Ready;
        _height = _streak = _sinks = _demoWait = 0;
        _sink = 0;
        _sinkT = -1;
        _free = false;
        _bestAtStart = Host.Stats.Get("tower.best");
        ChooseBase();
        _standing = true;
        _plinth.IsVisible = true;
        AddBlock(-StartW / 2, StartW / 2, 0, BlockColor(0));
        SpawnMover(StartW, true);
        Draw();
        Host.HudChanged();
    }

    /// <summary>The widest window top in the lower part of the screen, otherwise the floor near the middle.</summary>
    void ChooseBase()
    {
        var a = Host.Arena;
        var hud = Host.HudBounds.Inflate(16);
        Engine.Platform? best = null;
        foreach (var p in Host.Platforms.Items)
        {
            if (p.X2 - p.X1 < MinBaseW || p.Y < a.Top + a.Height * 0.3 || Column((p.X1 + p.X2) / 2, p.Y).Intersects(hud)) continue;
            if (best is not Engine.Platform b || p.X2 - p.X1 > b.X2 - b.X1) best = p;
        }
        if (best is Engine.Platform top)
        {
            SetBase(top.Hwnd, (top.X1 + top.X2) / 2, top.Y);
            return;
        }
        foreach (double f in new[] { 0.5, 0.4, 0.6, 0.28, 0.72 })
        {
            double x = a.Left + a.Width * f;
            if (Column(x, a.Bottom).Intersects(hud)) continue;
            SetBase(IntPtr.Zero, x, a.Bottom);
            return;
        }
        SetBase(IntPtr.Zero, a.Center.X, a.Bottom);
    }

    /// <summary>Where the tower and the sliding block can reach above a base.</summary>
    Rect Column(double x, double baseY)
    {
        double top = Host.Arena.Top + 90, half = SlideSpan + StartW / 2;
        return new Rect(x - half, top, half * 2, Math.Max(1, baseY - top));
    }

    void SetBase(IntPtr hwnd, double x, double y)
    {
        var a = Host.Arena;
        _baseHwnd = hwnd;
        _baseX = Clamp(x, a.Left + PlinthW / 2 + 4, a.Right - PlinthW / 2 - 4);
        _baseY = y;
        _seenGen = Host.Platforms.Generation;
    }

    bool Supported()
    {
        if (_baseHwnd == IntPtr.Zero) return true;
        var a = Host.Arena;
        if (_baseX - PlinthW / 2 < a.Left || _baseX + PlinthW / 2 > a.Right) return false;
        foreach (var p in Host.Platforms.Items)
            if (p.Hwnd == _baseHwnd && Math.Abs(p.Y - _baseY) < 3 && _baseX >= p.X1 && _baseX <= p.X2) return true;
        return false;
    }

    /// <summary>Ride along when the base window moves; topple when it closes or its top is covered.</summary>
    void FollowBase()
    {
        var plats = Host.Platforms;
        if (plats.Generation == _seenGen) return;
        _seenGen = plats.Generation;
        if (!_standing || _baseHwnd == IntPtr.Zero) return;
        var d = plats.DeltaOf(_baseHwnd);
        _baseX += d.X;
        _baseY += d.Y;
        if (Supported()) return;
        if (_phase == Phase.Ready)
        {
            NewGame(); // nothing built yet: just find a new spot
            return;
        }
        bool playing = _phase == Phase.Playing;
        Topple();
        if (playing) GameOver();
    }

    /// <summary>After a screen change mid-game: keep the tower on its window (or the floor) if possible.</summary>
    void Reseat()
    {
        var a = Host.Arena;
        _seenGen = Host.Platforms.Generation;
        if (_baseHwnd == IntPtr.Zero)
        {
            _baseY = a.Bottom;
            _baseX = Clamp(_baseX, a.Left + PlinthW / 2 + 4, a.Right - PlinthW / 2 - 4);
            return;
        }
        if (Supported()) return;
        foreach (var p in Host.Platforms.Items)
        {
            if (p.Hwnd != _baseHwnd || p.X2 - p.X1 < PlinthW) continue;
            _baseY = p.Y;
            _baseX = Clamp(_baseX, p.X1 + PlinthW / 2, p.X2 - PlinthW / 2);
            return;
        }
        Topple();
        GameOver();
    }

    void AddBlock(double x1, double x2, int level, Color color)
    {
        var el = MakeBlock(x2 - x1, color);
        _blockLayer.Children.Add(el);
        _blocks.Add(new Block { El = el, X1 = x1, X2 = x2, Level = level });
    }

    void SpawnMover(double w, bool parked)
    {
        _moveW = w;
        _moveColor = BlockColor(_height + 1);
        _moverAge = 0;
        _moveVy = 0;
        _falling = false;
        _hasMover = true;
        if (parked)
        {
            _moveX = 0;
        }
        else
        {
            var (lo, hi) = SlideRange(w);
            _moveDir = _nextSide; // enter from alternating sides
            _nextSide = -_nextSide;
            _moveX = _moveDir > 0 ? lo : hi;
            if (_demo) PickDemoAim();
        }
        _moverBody.Children.Clear();
        _moverBody.Children.Add(Art.At(MakeBlock(w, _moveColor), -w / 2, -BlockH / 2));
        _glow.Width = w + 20;
        _glow.Height = BlockH + 18;
        Canvas.SetLeft(_glow, -(w + 20) / 2);
        Canvas.SetTop(_glow, -(BlockH + 18) / 2);
        _glow.IsVisible = parked;
        _mover.Opacity = 0;
        _mover.IsVisible = true;
    }

    /// <summary>Slide limits for the block centre, relative to the base and kept inside the arena.</summary>
    (double lo, double hi) SlideRange(double w)
    {
        var a = Host.Arena;
        double lo = Math.Max(-SlideSpan, a.Left + w / 2 + 2 - _baseX);
        double hi = Math.Min(SlideSpan, a.Right - w / 2 - 2 - _baseX);
        return hi < lo ? ((lo + hi) / 2, (lo + hi) / 2) : (lo, hi);
    }

    void Drop()
    {
        if (!_hasMover || _falling || _phase == Phase.Over || _blocks.Count == 0) return;
        _free = _phase == Phase.Ready; // the first block waits centred over the tower, so it isn't a skill drop
        _phase = Phase.Playing;
        _moveTop = HoverTopRel;
        _moveVy = 0;
        _falling = true;
        _glow.IsVisible = false;
        PlayThrottled("whoosh", 0.25, 1.4);
    }

    void Land(Block top, double surface)
    {
        _falling = false;
        double fx1 = _moveX - _moveW / 2, fx2 = _moveX + _moveW / 2;
        double topCenter = (top.X1 + top.X2) / 2, off = _moveX - topCenter;
        double x1 = Math.Max(fx1, top.X1), x2 = Math.Min(fx2, top.X2);
        double y = PlinthTop + surface - BlockH; // absolute top of the landed block
        if (x2 - x1 <= 0.5)
        {
            Miss(off >= 0 ? 1 : -1);
            return;
        }

        bool perfect = !_free && Math.Abs(off) < PerfectPx;
        if (_free || perfect)
        {
            x1 = topCenter - _moveW / 2; // snap into place, keeping any bonus width
            x2 = topCenter + _moveW / 2;
        }
        else
        {
            if (fx1 < x1) Chop(fx1, x1, y, -1);
            if (fx2 > x2) Chop(x2, fx2, y, 1);
            PlayThrottled("board", 0.5, 0.75);
        }

        _height++;
        AddBlock(x1, x2, top.Level + 1, _moveColor);
        Host.Stats.Max("tower.height", _height);
        Host.Stats.Max("tower.best", _height);
        PlayThrottled("thunk", 0.6, 1 + Math.Min(0.4, _height * 0.015));

        double nextW = x2 - x1;
        if (perfect)
        {
            _streak++;
            Host.Stats.Add("tower.perfect");
            Host.Sound.Play("star", 0.6, 1 + 0.1 * _streak);
            bool grown = false;
            if (_streak >= PerfectsToWiden)
            {
                _streak = 0;
                grown = nextW < StartW;
                nextW = Math.Min(StartW, nextW + WidenPx);
                if (grown) Host.Sound.Play("score", 0.5);
            }
            var at = new Vec2(_baseX + topCenter, y);
            Host.Fx.Popup(at - new Vec2(0, 30), L.T("PERFECT!"), Gold, 26, 0.9, grown ? L.T("wider block") : null);
            Host.Fx.Burst(at, new[] { Gold, Colors.White }, 10, 240, 600, 4, 0.5);
        }
        else if (!_free)
        {
            _streak = 0;
        }
        _free = false;
        SpawnMover(nextW, false);
        Host.HudChanged();
    }

    void Chop(double x1, double x2, double top, double side)
    {
        double w = x2 - x1;
        SpawnPiece(MakeBlock(w, _moveColor), w, BlockH, new Vec2(_baseX + (x1 + x2) / 2, top + BlockH / 2),
            new Vec2(side * (60 + Rng.NextDouble() * 60), -40), side * (120 + Rng.NextDouble() * 140), 1.1);
    }

    void Miss(double side)
    {
        SpawnPiece(MakeBlock(_moveW, _moveColor), _moveW, BlockH, new Vec2(_baseX + _moveX, PlinthTop + _moveTop + BlockH / 2),
            new Vec2(side * 50, _moveVy * 0.7), side * 60, 1.5);
        _hasMover = false;
        _mover.IsVisible = false;
        PlayThrottled("whoosh", 0.4, 0.8);
        GameOver();
    }

    void Topple()
    {
        if (!_standing) return;
        _standing = false;
        var a = Host.Arena;
        double dir = _baseX < a.Left + a.Width * 0.25 ? 1 : _baseX > a.Right - a.Width * 0.25 ? -1 : Rng.NextDouble() < 0.5 ? -1 : 1;
        double plinthTop = PlinthTop;

        if (_hasMover && (_falling || _phase == Phase.Playing))
        {
            SpawnPiece(MakeBlock(_moveW, _moveColor), _moveW, BlockH, new Vec2(_baseX + _moveX, plinthTop + MoverTopRel + BlockH / 2),
                new Vec2(dir * 60, _falling ? _moveVy : 0), dir * 90, 1.5);
            _hasMover = false;
            _falling = false;
            _mover.IsVisible = false;
        }
        foreach (var b in _blocks)
        {
            _blockLayer.Children.Remove(b.El);
            double lift = b.Level + 1, w = b.X2 - b.X1;
            SpawnPiece(b.El, w, BlockH, new Vec2(_baseX + (b.X1 + b.X2) / 2, plinthTop + TopRelOf(b) + BlockH / 2),
                new Vec2(dir * (30 + lift * 12 + Rng.NextDouble() * 50), -Rng.NextDouble() * 120), dir * (40 + lift * 6 + Rng.NextDouble() * 120), 1.8);
        }
        _blocks.Clear();
        _sink = 0;
        _sinkT = -1;
        _plinth.IsVisible = false;
        SpawnPiece(MakePlinth(), PlinthW, PlinthH, new Vec2(_baseX, _baseY - PlinthH / 2), new Vec2(dir * 40, -60), dir * 30, 1.8);
        Host.Sound.Play("whoosh", 0.7, 0.7);
    }

    void GameOver()
    {
        _phase = Phase.Over;
        _streak = _demoWait = 0;
        _free = false;
        bool best = _height > _bestAtStart;
        var at = new Vec2(_baseX, PlinthTop + TowerTopRel - 170);
        Host.Fx.Popup(at, best ? L.T("NEW BEST!") : L.T("GAME OVER"), best ? Gold : Colors.White, 38, 2.4, L.F("Height {0}", _height));
        if (best) Host.Fx.Burst(at, Confetti, 40, 520, 700, 7, 1.1);
        Host.Sound.Play(best ? "best" : "buzzer", best ? 0.8 : 0.4);
        if (!_hasMover) SpawnMover(StartW, true);
        Host.HudChanged();
    }

    // ------------------------------------------------------------------ input

    public override void CollectHitShapes(List<HitShape> into)
    {
        if (_hasMover && !_falling) into.Add(HitShape.Box(MoverRect.Inflate(HitPad)));
    }

    public override bool PointerDown(Vec2 p, bool right)
    {
        if (!_hasMover || _falling || !MoverRect.Inflate(HitPad).Contains(p.ToPoint())) return false;
        if (_phase == Phase.Over)
        {
            NewGame();
            Host.Sound.Play("pop", 0.5);
        }
        else
        {
            Drop();
        }
        return false;
    }

    public override void Summon(Vec2 p)
    {
        if (_phase == Phase.Playing) return;
        var a = Host.Arena;
        NewGame();
        // the nearest wide window top under the cursor, otherwise the floor below it
        Engine.Platform? best = null;
        foreach (var plat in Host.Platforms.Items)
        {
            if (plat.X2 - plat.X1 < MinBaseW || p.X < plat.X1 || p.X > plat.X2 || plat.Y < p.Y - 40 || plat.Y < a.Top + SinkMargin + 3 * BlockH) continue;
            if (best is not Engine.Platform b || plat.Y < b.Y) best = plat;
        }
        if (best is Engine.Platform top) SetBase(top.Hwnd, Clamp(p.X, top.X1 + PlinthW / 2, top.X2 - PlinthW / 2), top.Y);
        else SetBase(IntPtr.Zero, p.X, a.Bottom);
        Draw();
    }

    // ------------------------------------------------------------------ simulation

    public override bool Update(double dt)
    {
        _time += dt;
        FollowBase();
        if (_hasMover) _moverAge += dt;

        if (_phase == Phase.Playing || _falling || _sinkT >= 0 || _pieces.Count > 0)
        {
            _acc = Math.Min(_acc + dt, 0.1);
            while (_acc >= Step)
            {
                _acc -= Step;
                SimStep(Step);
            }
        }
        else
        {
            _acc = 0;
        }

        Draw();
        return _phase == Phase.Playing || _falling || _sinkT >= 0 || _pieces.Count > 0 || (_hasMover && _moverAge < FadeIn);
    }

    void SimStep(double h)
    {
        // closed box: once the tower nears the top of the screen, lower it one block at a time
        if (_sinkT >= 0) AdvanceSink(h);
        else if (_phase == Phase.Playing && TooHigh) _sinkT = 0;

        if (_hasMover && _phase == Phase.Playing)
        {
            if (_falling) StepFalling(h);
            else StepSlide(h);
        }

        for (int i = _pieces.Count - 1; i >= 0; i--)
        {
            var p = _pieces[i];
            p.Age += h;
            if (p.Age < p.Life)
            {
                StepPiece(p, h);
                continue;
            }
            _pieceLayer.Children.Remove(p.Sprite);
            _pieces.RemoveAt(i);
        }
    }

    void StepSlide(double h)
    {
        double speed = SlideSpeed;
        var (lo, hi) = SlideRange(_moveW);
        _moveX += _moveDir * speed * h;
        if (_moveX >= hi) { _moveX = hi; _moveDir = -1; }
        else if (_moveX <= lo) { _moveX = lo; _moveDir = 1; }

        if (!_demo || _blocks.Count == 0 || _moverAge < 0.25) return;
        var top = _blocks[^1];
        double target = Clamp((top.X1 + top.X2) / 2 + _demoAim, lo, hi);
        double off = Math.Abs(_moveX - target);
        if (off <= 6 && off <= Math.Max(1.5, speed * h)) Drop();
    }

    void StepFalling(double h)
    {
        if (_blocks.Count == 0) return;
        _moveVy += Gravity * h;
        _moveTop += _moveVy * h;
        var top = _blocks[^1];
        double surface = TopRelOf(top);
        if (_moveTop + BlockH >= surface) Land(top, surface);
    }

    void AdvanceSink(double h)
    {
        _sinkT = Math.Min(1, _sinkT + h / SinkSeconds);
        if (_sinkT >= 1 || _blocks.Count == 0)
        {
            FinishSink();
            return;
        }
        _sink = BlockH * _sinkT * _sinkT * (3 - 2 * _sinkT);
        _blocks[0].El.Opacity = 1 - _sinkT;
    }

    void FinishSink()
    {
        _sinkT = -1;
        _sink = 0;
        if (_blocks.Count < 2) return;
        _blockLayer.Children.Remove(_blocks[0].El);
        _blocks.RemoveAt(0);
        foreach (var b in _blocks) b.Level--;
        _sinks++;
    }

    Piece SpawnPiece(Control visual, double w, double h, Vec2 centre, Vec2 vel, double spin, double life)
    {
        var s = new Sprite { IsHitTestVisible = false };
        s.Rotor.Children.Add(Art.At(visual, -w / 2, -h / 2));
        s.Set(centre, 0);
        _pieceLayer.Children.Add(s);
        var piece = new Piece { Sprite = s, Pos = centre, Vel = vel, HalfW = w / 2, HalfH = h / 2, Spin = spin, Life = life };
        _pieces.Add(piece);
        return piece;
    }

    void StepPiece(Piece p, double h)
    {
        var a = Host.Arena;
        double rad = p.Angle * Math.PI / 180, cos = Math.Abs(Math.Cos(rad)), sin = Math.Abs(Math.Sin(rad));
        double halfW = p.HalfW * cos + p.HalfH * sin, halfH = p.HalfW * sin + p.HalfH * cos;
        double prevBottom = p.Pos.Y + halfH;
        p.Vel.Y += Gravity * h;
        p.Pos += p.Vel * h;
        p.Angle += p.Spin * h;

        // closed box: pieces bounce off the screen edges and come to rest on the floor or a window top
        if (p.Pos.X - halfW < a.Left) { p.Pos.X = a.Left + halfW; p.Vel.X = Math.Abs(p.Vel.X) * 0.4; }
        else if (p.Pos.X + halfW > a.Right) { p.Pos.X = a.Right - halfW; p.Vel.X = -Math.Abs(p.Vel.X) * 0.4; }
        if (p.Pos.Y - halfH < a.Top) { p.Pos.Y = a.Top + halfH; p.Vel.Y = Math.Abs(p.Vel.Y) * 0.3; }
        if (p.Vel.Y <= 0) return;

        double ground = double.NaN;
        if (Host.Platforms.FindLanding(p.Pos.X, prevBottom, p.Pos.Y + halfH, out var plat)) ground = plat.Y;
        else if (p.Pos.Y + halfH >= a.Bottom) ground = a.Bottom;
        if (double.IsNaN(ground)) return;

        if (p.Vel.Y > 400) PlayThrottled("thunk", Math.Min(0.3, p.Vel.Y / 5000), 0.7);
        p.Pos.Y = ground - halfH;
        p.Vel.Y = -p.Vel.Y * 0.2;
        p.Vel.X *= 0.6;
        p.Spin *= 0.3;
    }

    // ------------------------------------------------------------------ visuals

    void Draw()
    {
        double plinthTop = PlinthTop;
        Canvas.SetLeft(_plinth, _baseX - PlinthW / 2);
        Canvas.SetTop(_plinth, plinthTop);
        foreach (var b in _blocks)
        {
            Canvas.SetLeft(b.El, _baseX + b.X1);
            Canvas.SetTop(b.El, plinthTop + TopRelOf(b));
        }
        if (_hasMover)
        {
            _mover.Set(new Vec2(_baseX + _moveX, plinthTop + MoverTopRel + BlockH / 2));
            _mover.Opacity = Math.Min(1, _moverAge / FadeIn);
        }
        foreach (var p in _pieces)
        {
            p.Sprite.Set(p.Pos, p.Angle);
            double hold = p.Life * 0.4;
            p.Sprite.Opacity = p.Age < hold ? 1 : Math.Max(0, 1 - (p.Age - hold) / (p.Life - hold));
        }
    }

    void PlayThrottled(string name, double vol, double pitch = 1)
    {
        if (_lastSound.TryGetValue(name, out double t) && _time - t < 0.05) return;
        _lastSound[name] = _time;
        Host.Sound.Play(name, vol, pitch);
    }

    /// <summary>Block colours step through a hue gradient as the tower grows.</summary>
    static Color BlockColor(int n)
    {
        double hue = (200 + n * 10) % 360 / 60.0, s = 0.64, l = 0.56;
        double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(hue % 2 - 1)), m = l - c / 2;
        var (r, g, b) = (int)hue switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    static Canvas MakeBlock(double w, Color c)
    {
        var el = new Canvas { Width = w, Height = BlockH, IsHitTestVisible = false };
        el.Children.Add(new Rectangle
        {
            Width = w, Height = BlockH, RadiusX = 3, RadiusY = 3,
            Fill = Vertical(Art.Blend(c, Colors.White, 0.3), Art.Blend(c, Colors.Black, 0.2)),
            Stroke = Art.Brush(Art.Blend(c, Colors.Black, 0.5)), StrokeThickness = 1,
        });
        if (w > 12)
            el.Children.Add(Art.At(new Rectangle { Width = w - 8, Height = 3, RadiusX = 1.5, RadiusY = 1.5, Fill = Art.Brush(85, 255, 255, 255) }, 4, 3));
        return el;
    }

    static Canvas MakePlinth()
    {
        var c = new Canvas { Width = PlinthW, Height = PlinthH, IsHitTestVisible = false };
        var edge = Art.Brush("#3E444D");
        c.Children.Add(Art.At(new Rectangle
        {
            Width = PlinthW - 4, Height = 5, RadiusX = 2, RadiusY = 2, Stroke = edge, StrokeThickness = 1,
            Fill = Vertical(Color.FromRgb(150, 157, 167), Color.FromRgb(104, 111, 121)),
        }, 2, PlinthH - 5));
        c.Children.Add(Art.At(new Rectangle
        {
            Width = PlinthW - 28, Height = 7, Stroke = edge, StrokeThickness = 1,
            Fill = Vertical(Color.FromRgb(128, 135, 145), Color.FromRgb(92, 99, 109)),
        }, 14, 5));
        c.Children.Add(new Rectangle
        {
            Width = PlinthW, Height = 6, RadiusX = 2, RadiusY = 2, Stroke = edge, StrokeThickness = 1,
            Fill = Vertical(Color.FromRgb(214, 219, 226), Color.FromRgb(160, 167, 177)),
        });
        return c;
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

    // ------------------------------------------------------------------ demo

    void PickDemoAim()
    {
        // stay careful until the tower has been lowered a few times, then get sloppy so the game ends
        bool sloppy = _sinks >= 3 || _height >= 45;
        double chance = sloppy ? 0.6 : 0.25, spread = sloppy ? 30 + _moveW * 0.5 : 14;
        _demoAim = Rng.NextDouble() < chance ? (Rng.NextDouble() < 0.5 ? -1 : 1) * (6 + Rng.NextDouble() * spread) : 0;
    }

    public override void DemoTick()
    {
        _demo = true;
        if (_falling) return;
        if (_phase == Phase.Ready && ++_demoWait >= 4) Drop();
        else if (_phase == Phase.Over && _pieces.Count == 0 && ++_demoWait >= 12) NewGame();
    }
}
