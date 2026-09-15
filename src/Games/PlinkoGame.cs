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
/// Plinko: click the strip on top of the peg board to drop a disc and watch it bounce down into a scoring
/// slot. Ten discs per round; the gold slot in the middle is the jackpot. The board is its own closed box,
/// stands on the floor and moves by right-dragging its header.
/// </summary>
public sealed class PlinkoGame : MiniGame
{
    // Everything on the board lives in board-local units; the board as a whole is scaled on short screens.
    const double BoardW = 380, BoardH = 560, Corner = 18, HeaderH = 46, Pad = 14;
    const double XL = Pad, XR = BoardW - Pad, DropY = HeaderH / 2, SlotW = (XR - XL) / Slots;
    const double DiscR = 11, PegR = 4, PegTop = 92, RowGap = 34, SlotTop = 446, FloorY = 522, DividerHalf = 1.5;
    const double Gravity = 1300, MaxSpeed = 950, PegBounce = 0.5, WallBounce = 0.5, DiscBounce = 0.5, FloorBounce = 0.3;
    const double Step = 1.0 / 240, FadeTime = 0.6, RoundEndDelay = 0.7, SettleTime = 0.45;
    const int Slots = 9, PegRows = 10, DiscsPerRound = 10, MaxFalling = 3, JackpotSlot = 4;

    static readonly int[] Values = { 10, 25, 50, 100, 250, 100, 50, 25, 10 };
    static readonly Color Gold = Color.FromRgb(255, 209, 102);
    static readonly Color[] Confetti = { Gold, Color.FromRgb(239, 71, 111), Color.FromRgb(6, 214, 160), Colors.White };
    static readonly Color[] DiscColors =
    {
        Color.FromRgb(239, 71, 111), Color.FromRgb(80, 160, 255), Color.FromRgb(6, 214, 160), Color.FromRgb(170, 120, 230),
    };

    sealed class Peg
    {
        public required Ellipse Glow;
        public Vec2 Pos;
        public double Flash;
    }

    sealed class Disc
    {
        public required Sprite Sprite;
        public Vec2 Pos, Vel;
        public int Slot = -1; // set once the disc is below the divider tops and can't change slot any more
        public double Age, Still, LandedT, Fade;
        public bool Landed, Settled;
    }

    readonly Canvas _board = new() { Width = BoardW, Height = BoardH };
    readonly ScaleTransform _boardScale = new();
    readonly TranslateTransform _boardShift = new();
    readonly Canvas _discLayer = new() { IsHitTestVisible = false };
    readonly Border _header = new()
    {
        Width = BoardW, Height = HeaderH, CornerRadius = new CornerRadius(Corner, Corner, 0, 0),
        Background = Vertical(Color.FromArgb(235, 56, 66, 92), Color.FromArgb(235, 34, 40, 58)),
    };
    readonly TextBlock _label = new()
    {
        Width = BoardW, Padding = new Thickness(Pad, 0), TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        FontFamily = Fx.Font, FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Art.Brush(225, 232, 238, 248),
        IsHitTestVisible = false,
    };
    readonly Sprite _preview = MakeDisc(Colors.White);
    readonly Rectangle[] _slotBacks = new Rectangle[Slots];
    readonly double[] _slotFlash = new double[Slots];
    readonly List<Peg> _pegs = new();
    readonly List<Peg> _litPegs = new();
    readonly List<Disc> _discs = new();
    readonly Dictionary<string, double> _lastSound = new();

    Vec2 _origin, _moveOffset;
    double _scale = 1, _fitScale = -1, _time, _acc, _roundEndIn = -1;
    int _score, _jackpots, _discsLeft = DiscsPerRound, _colorIndex, _demoWait;
    long _bestAtStart;
    bool _placed, _moving, _onFloor, _roundOver;

    public PlinkoGame(IGameHost host) : base(host)
    {
        _board.RenderTransformOrigin = RelativePoint.TopLeft;
        _board.RenderTransform = new TransformGroup { Children = { _boardScale, _boardShift } };
        BuildBoard();
        Layer.Children.Add(_board);

        // The overlay only wakes its frame loop for clicks; hovering the strip has to wake it to move the preview disc.
        _header.PointerEntered += (_, _) => Host.Wake();
        _header.PointerMoved += (_, _) => Host.Wake();
        _header.PointerExited += (_, _) => Host.Wake();
        L.Changed += UpdateLabel;
        UpdateLabel();
    }

    public override string Id => "plinko";
    public override string Title => "Plinko";

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        s.Rotor.Children.Add(Art.At(new Rectangle
        {
            Width = 18, Height = 20, RadiusX = 3, RadiusY = 3, Fill = Art.Brush(235, 28, 34, 50), Stroke = Art.Brush("#9AA6BA"), StrokeThickness = 1,
        }, -9, -10));
        var peg = Art.Brush("#E4EAF4");
        foreach (var (x, y) in new[] { (-3.0, -1.5), (3.0, -1.5), (-6.0, 2.5), (0.0, 2.5), (6.0, 2.5) })
            s.Rotor.Children.Add(Art.Circle(x, y, 1.1, peg));
        foreach (double x in new[] { -3.4, 2.6 })
            s.Rotor.Children.Add(Art.At(new Rectangle { Width = 0.8, Height = 3.5, Fill = peg }, x, 5.5));
        s.Rotor.Children.Add(Art.At(new Rectangle { Width = 5, Height = 2, Fill = Art.Brush(Gold) }, -2.5, 7));
        s.Rotor.Children.Add(Art.Circle(1.5, -6, 2.4, Art.Brush(DiscColors[0])));
        return s;
    }

    int FallingCount => _discs.Count(d => !d.Landed);
    bool CanDrop => _roundOver || (_discsLeft > 0 && FallingCount < MaxFalling);
    Rect StripRect => new(_origin.X, _origin.Y, BoardW * _scale, HeaderH * _scale);
    Vec2 ToScreen(Vec2 local) => _origin + local * _scale;
    Vec2 ToLocal(Vec2 screen) => (screen - _origin) / _scale;

    public override HudInfo Hud => new(
        _score.ToString(),
        _roundOver ? L.T("Round over · click the drop strip to play again") : L.F("Discs {0} · jackpots {1}", _discsLeft, _jackpots),
        L.F("Best {0}", Host.Stats.Get("plinko.best")));

    // ------------------------------------------------------------------ game flow

    public override void Layout()
    {
        var a = Host.Arena;
        double fit = FitScale();
        if (!_placed)
        {
            _placed = true;
            _fitScale = _scale = fit;
            PlaceNearRight();
            NewRound();
        }
        else if (fit != _fitScale)
        {
            _fitScale = _scale = fit; // the screen changed size
        }
        if (_onFloor) _origin.Y = a.Bottom - BoardH * _scale;
        _origin = ClampOrigin(_origin);
        PlaceBoard();
        UpdateLabel();
        Host.HudChanged();
    }

    public override void Deactivate()
    {
        _moving = false;
        _preview.IsVisible = false;
    }

    double FitScale()
    {
        var a = Host.Arena;
        // leave room above the board for popups and the scoreboard
        return Clamp(Math.Min((a.Height - 160) / BoardH, (a.Width - 40) / BoardW), 0.45, 1);
    }

    void PlaceNearRight()
    {
        var a = Host.Arena;
        var hud = Host.HudBounds.Inflate(16);
        _onFloor = true;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            double w = BoardW * _scale, h = BoardH * _scale;
            for (double x = a.Right - w - 60; x >= a.Left + 20; x -= 30)
            {
                if (new Rect(x, a.Bottom - h, w, h).Intersects(hud)) continue;
                _origin = new Vec2(x, a.Bottom - h);
                return;
            }
            _scale = Math.Max(0.45, _scale * 0.85); // the scoreboard is in the way everywhere: try a smaller board
        }
        _origin = new Vec2(a.Right - BoardW * _scale - 60, a.Bottom - BoardH * _scale);
    }

    Vec2 ClampOrigin(Vec2 o)
    {
        var a = Host.Arena;
        o.X = Clamp(o.X, a.Left, Math.Max(a.Left, a.Right - BoardW * _scale));
        o.Y = Clamp(o.Y, a.Top, Math.Max(a.Top, a.Bottom - BoardH * _scale));
        return o;
    }

    void NewRound()
    {
        _score = _jackpots = 0;
        _discsLeft = DiscsPerRound;
        _roundOver = false;
        _roundEndIn = -1;
        _bestAtStart = Host.Stats.Get("plinko.best");
        UpdateLabel();
        Host.HudChanged();
    }

    void EndRound()
    {
        _roundOver = true;
        bool best = _score > _bestAtStart;
        var at = ToScreen(new Vec2(BoardW / 2, BoardH * 0.3));
        Host.Fx.Popup(at, best ? L.T("NEW BEST!") : L.T("ROUND OVER"), best ? Gold : Colors.White, 36, 2.2,
            L.F("{0} points · {1} jackpots", _score, _jackpots));
        if (best)
        {
            Host.Fx.Burst(at, Confetti, 40, 520, 700, 7, 1.1);
            Host.Sound.Play("best", 0.8);
        }
        else
        {
            Host.Sound.Play("fire", 0.6);
        }
        _demoWait = 10;
        UpdateLabel();
        Host.HudChanged();
    }

    bool Drop(double localX)
    {
        if (_discsLeft <= 0 || FallingCount >= MaxFalling) return false;
        var d = new Disc
        {
            Sprite = MakeDisc(DiscColors[_colorIndex++ % DiscColors.Length]),
            Pos = new Vec2(Clamp(localX, XL + DiscR, XR - DiscR), DropY),
            Vel = new Vec2((Rng.NextDouble() - 0.5) * 20, 0),
        };
        d.Sprite.Set(d.Pos);
        _discLayer.Children.Add(d.Sprite);
        _discs.Add(d);
        _discsLeft--;
        Host.Stats.Add("plinko.discs");
        Host.Sound.Play("pop", 0.45, 1.3);
        Host.HudChanged();
        return true;
    }

    void Land(Disc d)
    {
        d.Landed = true;
        int slot = d.Slot, value = Values[slot];
        _score += value;
        Host.Stats.Max("plinko.best", _score);
        _slotFlash[slot] = 1;
        var at = ToScreen(new Vec2(XL + (slot + 0.5) * SlotW, SlotTop - 16));
        PlayThrottled("thunk", 0.45, 1.25);
        if (slot == JackpotSlot)
        {
            _jackpots++;
            Host.Stats.Add("plinko.jackpots");
            Host.Fx.Popup(at, $"+{value}", Gold, 34, 1.4, L.T("JACKPOT!"));
            Host.Fx.Burst(at, Confetti, 40, 480, 700, 7, 1.1);
            Host.Sound.Play("best", 0.8);
        }
        else
        {
            Host.Fx.Popup(at, $"+{value}", SlotColor(slot), 22 + Math.Min(6, value / 20), 0.9);
            PlayThrottled("score", 0.35, 0.8 + value / 250.0);
        }
        Host.HudChanged();
    }

    // ------------------------------------------------------------------ input

    public override void CollectHitShapes(List<HitShape> into) => into.Add(HitShape.Box(StripRect));

    public override bool PointerDown(Vec2 p, bool right)
    {
        if (!StripRect.Contains(p.ToPoint())) return false;
        if (right)
        {
            _moving = true;
            _moveOffset = _origin - p;
            _preview.IsVisible = false;
            return true;
        }
        if (_roundEndIn >= 0) return false;
        if (_roundOver) NewRound();
        Drop(ToLocal(p).X);
        return false;
    }

    public override void PointerUp(Vec2 p)
    {
        if (!_moving) return;
        _moving = false;
        var a = Host.Arena;
        _origin = ClampOrigin(p + _moveOffset);
        _onFloor = a.Bottom - (_origin.Y + BoardH * _scale) < 28; // let go close to the floor: stand on it
        if (_onFloor) _origin.Y = a.Bottom - BoardH * _scale;
        PlaceBoard();
    }

    public override void Summon(Vec2 p)
    {
        if (_moving) return;
        _origin = ClampOrigin(p - new Vec2(BoardW * _scale / 2, HeaderH * _scale / 2));
        _onFloor = Host.Arena.Bottom - (_origin.Y + BoardH * _scale) < 1;
        PlaceBoard();
    }

    // ------------------------------------------------------------------ simulation

    public override bool Update(double dt)
    {
        _time += dt;
        bool busy = _moving;

        if (_moving)
        {
            _origin = ClampOrigin(Host.Pointer + _moveOffset);
            PlaceBoard();
        }

        if (_discs.Exists(d => !d.Settled))
        {
            busy = true;
            _acc += dt;
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

        for (int i = _discs.Count - 1; i >= 0; i--)
        {
            var d = _discs[i];
            if (d.Settled)
            {
                busy = true;
                d.Fade += dt;
                double k = d.Fade / FadeTime;
                if (k >= 1)
                {
                    _discLayer.Children.Remove(d.Sprite);
                    _discs.RemoveAt(i);
                    continue;
                }
                d.Sprite.Opacity = 1 - k;
                d.Sprite.Scale = 1 - 0.3 * k;
            }
            d.Sprite.Set(d.Pos);
        }

        busy |= UpdateFlashes(dt);

        if (!_roundOver && _roundEndIn < 0 && _discsLeft == 0 && FallingCount == 0) _roundEndIn = RoundEndDelay;
        if (_roundEndIn >= 0)
        {
            busy = true;
            if ((_roundEndIn -= dt) < 0)
            {
                _roundEndIn = -1;
                EndRound();
            }
        }

        UpdatePreview();
        return busy;
    }

    void SimStep(double h)
    {
        // discs push each other apart first, so the walls and slot limits below get the last word
        for (int i = 0; i < _discs.Count; i++)
            for (int j = i + 1; j < _discs.Count; j++)
                CollideDiscs(_discs[i], _discs[j]);

        foreach (var d in _discs)
        {
            if (d.Settled) continue;
            d.Age += h;
            d.Vel.Y += Gravity * h;
            double speed = d.Vel.Length;
            if (speed > MaxSpeed) d.Vel *= MaxSpeed / speed;
            d.Pos += d.Vel * h;
            if (d.Slot < 0) StepField(d, h);
            else StepSlot(d, h);
        }
    }

    void StepField(Disc d, double h)
    {
        foreach (var peg in _pegs)
        {
            if (Math.Abs(peg.Pos.Y - d.Pos.Y) >= DiscR + PegR) continue;
            double hit = Bounce(d, peg.Pos, PegR, PegBounce, true);
            if (hit <= 0) continue;
            if (peg.Flash <= 0) _litPegs.Add(peg);
            peg.Flash = 1;
            if (hit > 60) PlayThrottled("rim", Math.Min(0.22, hit / 2600), 2.3 + Rng.NextDouble() * 0.5);
        }

        if (d.Pos.Y + DiscR > SlotTop - DividerHalf)
        {
            for (int k = 1; k < Slots; k++)
            {
                double x = XL + k * SlotW;
                if (Math.Abs(d.Pos.X - x) >= DiscR + DividerHalf) continue;
                var closest = new Vec2(x, Clamp(d.Pos.Y, SlotTop, FloorY));
                if (Bounce(d, closest, DividerHalf, 0.4, false) > 80) PlayThrottled("rim", 0.15, 1.8);
            }
        }

        // closed box: side walls, the lid above the strip, and (only as a safety net) the floor
        if (d.Pos.X < XL + DiscR)
        {
            d.Pos.X = XL + DiscR;
            if (d.Vel.X < 0) WallHit(d, -d.Vel.X);
        }
        else if (d.Pos.X > XR - DiscR)
        {
            d.Pos.X = XR - DiscR;
            if (d.Vel.X > 0) WallHit(d, d.Vel.X);
        }
        if (d.Pos.Y < DiscR + 3)
        {
            d.Pos.Y = DiscR + 3;
            if (d.Vel.Y < 0) d.Vel.Y = -d.Vel.Y * WallBounce;
        }
        if (d.Pos.Y > FloorY - DiscR) d.Pos.Y = FloorY - DiscR;

        if (d.Pos.Y - DiscR > SlotTop)
        {
            d.Slot = SlotAt(d.Pos.X);
            return;
        }

        // a disc that has come to rest on something gets a shove, so it can't hang on the board forever
        if (d.Vel.LengthSquared < 30 * 30)
        {
            d.Still += h;
            if (d.Still > 0.3)
            {
                d.Still = 0;
                d.Vel = new Vec2((Rng.NextDouble() < 0.5 ? -1 : 1) * (80 + Rng.NextDouble() * 60), -90);
            }
        }
        else
        {
            d.Still = 0;
        }
        if (d.Age > 12)
        {
            d.Slot = SlotAt(d.Pos.X);
            d.Pos.Y = Math.Max(d.Pos.Y, SlotTop + DiscR + 1);
        }
    }

    void WallHit(Disc d, double speed)
    {
        d.Vel.X = d.Pos.X < BoardW / 2 ? speed * WallBounce : -speed * WallBounce;
        if (speed > 120) PlayThrottled("board", Math.Min(0.25, speed / 2400), 1.6);
    }

    void StepSlot(Disc d, double h)
    {
        double left = d.Slot == 0 ? XL + DiscR : XL + d.Slot * SlotW + DividerHalf + DiscR;
        double right = d.Slot == Slots - 1 ? XR - DiscR : XL + (d.Slot + 1) * SlotW - DividerHalf - DiscR;
        if (d.Pos.X < left)
        {
            d.Pos.X = left;
            d.Vel.X = Math.Abs(d.Vel.X) * 0.4;
        }
        else if (d.Pos.X > right)
        {
            d.Pos.X = right;
            d.Vel.X = -Math.Abs(d.Vel.X) * 0.4;
        }

        double lid = SlotTop + DiscR; // once inside, a disc stays in its slot
        if (d.Pos.Y < lid)
        {
            d.Pos.Y = lid;
            if (d.Vel.Y < 0) d.Vel.Y = 0;
        }
        if (d.Pos.Y + DiscR >= FloorY)
        {
            d.Pos.Y = FloorY - DiscR;
            if (d.Vel.Y > 0)
            {
                if (!d.Landed) Land(d);
                d.Vel.Y = d.Vel.Y > 120 ? -d.Vel.Y * FloorBounce : 0;
            }
            d.Vel.X *= 1 - Math.Min(1, 6 * h);
        }
        if (d.Landed && (d.LandedT += h) > SettleTime) d.Settled = true;
    }

    static int SlotAt(double x) => (int)Clamp(Math.Floor((x - XL) / SlotW), 0, Slots - 1);

    /// <summary>Bounces a disc off a static circle. Returns the impact speed, or 0 when there is no hit.</summary>
    static double Bounce(Disc d, Vec2 c, double cr, double restitution, bool nudge)
    {
        Vec2 delta = d.Pos - c;
        double min = DiscR + cr, dist2 = delta.LengthSquared;
        if (dist2 >= min * min) return 0;
        double dist = Math.Sqrt(dist2);
        Vec2 n = dist < 1e-6 ? new Vec2(Rng.NextDouble() < 0.5 ? -1 : 1, 0) : delta / dist;
        d.Pos = c + n * min;
        double vn = Vec2.Dot(d.Vel, n);
        if (vn >= 0) return 0;
        d.Vel -= n * ((1 + restitution) * vn);
        if (nudge)
        {
            // a small random push so no two drops play out the same, and a disc can't balance on top of a peg
            double push = (Rng.NextDouble() - 0.5) * 60;
            if (n.Y < -0.94) push += (push < 0 ? -1 : 1) * 35;
            d.Vel.X += push;
        }
        return -vn;
    }

    static void CollideDiscs(Disc a, Disc b)
    {
        if (a.Settled || b.Settled) return;
        Vec2 delta = b.Pos - a.Pos;
        double min = DiscR * 2, dist2 = delta.LengthSquared;
        if (dist2 >= min * min) return;
        double dist = Math.Sqrt(dist2);
        Vec2 n = dist < 1e-6 ? new Vec2(1, 0) : delta / dist;
        double push = (min - dist) / 2;
        a.Pos -= n * push;
        b.Pos += n * push;
        double rel = Vec2.Dot(a.Vel - b.Vel, n);
        if (rel <= 0) return;
        double j = (1 + DiscBounce) * rel / 2;
        a.Vel -= n * j;
        b.Vel += n * j;
    }

    bool UpdateFlashes(double dt)
    {
        for (int i = _litPegs.Count - 1; i >= 0; i--)
        {
            var peg = _litPegs[i];
            peg.Flash = Math.Max(0, peg.Flash - dt * 3.5);
            peg.Glow.Opacity = peg.Flash;
            if (peg.Flash <= 0) _litPegs.RemoveAt(i);
        }
        bool any = _litPegs.Count > 0;
        for (int k = 0; k < Slots; k++)
        {
            if (_slotFlash[k] <= 0) continue;
            _slotFlash[k] = Math.Max(0, _slotFlash[k] - dt * 2);
            _slotBacks[k].Opacity = SlotBaseOpacity(k) + 0.45 * _slotFlash[k];
            any = true;
        }
        return any;
    }

    // ------------------------------------------------------------------ visuals

    void PlaceBoard()
    {
        if (_boardScale.ScaleX != _scale) _boardScale.ScaleX = _boardScale.ScaleY = _scale;
        _boardShift.X = _origin.X;
        _boardShift.Y = _origin.Y;
    }

    void UpdatePreview()
    {
        bool show = !_moving && _roundEndIn < 0 && StripRect.Contains(Host.Pointer.ToPoint());
        if (show)
        {
            _preview.Set(new Vec2(Clamp(ToLocal(Host.Pointer).X, XL + DiscR, XR - DiscR), DropY));
            _preview.Opacity = CanDrop ? 0.6 : 0.25;
        }
        if (_preview.IsVisible != show) _preview.IsVisible = show;
    }

    void UpdateLabel() =>
        _label.Text = _roundOver ? L.T("click for a new round") : L.T("click to drop · right-drag to move");

    void BuildBoard()
    {
        var c = _board.Children;
        c.Add(new Border
        {
            Width = BoardW, Height = BoardH, CornerRadius = new CornerRadius(Corner), Background = Art.Brush(205, 16, 20, 31), IsHitTestVisible = false,
        });

        for (int k = 0; k < Slots; k++)
        {
            var color = SlotColor(k);
            _slotBacks[k] = Art.At(new Rectangle
            {
                Width = SlotW - DividerHalf * 2, Height = FloorY - SlotTop, Fill = Art.Brush(color), Opacity = SlotBaseOpacity(k), IsHitTestVisible = false,
            }, XL + k * SlotW + DividerHalf, SlotTop);
            c.Add(_slotBacks[k]);
            c.Add(Art.At(new TextBlock
            {
                Text = Values[k].ToString(), Width = SlotW, TextAlignment = TextAlignment.Center, FontFamily = Fx.Font,
                FontSize = k == JackpotSlot ? 13 : 12, FontWeight = FontWeight.Bold, Foreground = Art.Brush(color), IsHitTestVisible = false,
            }, XL + k * SlotW, FloorY + 7));
        }

        var rail = Art.Brush(80, 255, 255, 255);
        c.Add(Art.At(new Rectangle { Width = 2, Height = FloorY - HeaderH, Fill = rail, IsHitTestVisible = false }, XL - 2, HeaderH));
        c.Add(Art.At(new Rectangle { Width = 2, Height = FloorY - HeaderH, Fill = rail, IsHitTestVisible = false }, XR, HeaderH));
        c.Add(Art.At(new Rectangle { Width = XR - XL + 4, Height = 2, Fill = Art.Brush(120, 255, 255, 255), IsHitTestVisible = false }, XL - 2, FloorY));
        var divider = Art.Brush(215, 215, 222, 235);
        for (int k = 1; k < Slots; k++)
        {
            c.Add(Art.At(new Rectangle
            {
                Width = DividerHalf * 2, Height = FloorY - SlotTop + DividerHalf, RadiusX = DividerHalf, RadiusY = DividerHalf, Fill = divider,
                IsHitTestVisible = false,
            }, XL + k * SlotW - DividerHalf, SlotTop - DividerHalf));
        }

        var pegFill = Art.Brush(240, 228, 234, 246);
        var pegEdge = Art.Brush(140, 0, 0, 0);
        var glowFill = Art.Brush(170, 255, 226, 150);
        for (int row = 0; row < PegRows; row++)
        {
            double y = PegTop + row * RowGap;
            // Full rows sit over the dividers and have half-pegs on the walls; offset rows skip the pegs nearest
            // the walls, where the gap would be too narrow for a disc and it could wedge.
            bool full = (PegRows - 1 - row) % 2 == 0;
            int from = full ? 0 : 1, to = full ? Slots : Slots - 2;
            for (int k = from; k <= to; k++)
            {
                var p = new Vec2(XL + (full ? k : k + 0.5) * SlotW, y);
                var glow = Art.Circle(p.X, p.Y, PegR * 2.6, glowFill);
                glow.Opacity = 0;
                glow.IsHitTestVisible = false;
                var peg = Art.Circle(p.X, p.Y, PegR, pegFill, pegEdge, 0.8);
                peg.IsHitTestVisible = false;
                c.Add(glow);
                c.Add(peg);
                _pegs.Add(new Peg { Glow = glow, Pos = p });
            }
        }

        c.Add(_header);
        c.Add(Art.At(new Rectangle { Width = BoardW, Height = 1, Fill = Art.Brush(90, 255, 255, 255), IsHitTestVisible = false }, 0, HeaderH));
        c.Add(Art.At(_label, 0, HeaderH / 2 - 9));
        c.Add(_discLayer);
        _preview.IsVisible = false;
        c.Add(_preview);
        c.Add(new Border
        {
            Width = BoardW, Height = BoardH, CornerRadius = new CornerRadius(Corner), BorderBrush = Art.Brush(120, 255, 255, 255),
            BorderThickness = new Thickness(1.5), IsHitTestVisible = false,
        });
    }

    static Color SlotColor(int k) => Values[k] switch
    {
        250 => Gold,
        100 => Color.FromRgb(239, 71, 111),
        50 => Color.FromRgb(80, 160, 255),
        25 => Color.FromRgb(6, 214, 160),
        _ => Color.FromRgb(150, 165, 190),
    };

    static double SlotBaseOpacity(int k) => k == JackpotSlot ? 0.3 : 0.12;

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

    static Sprite MakeDisc(Color color)
    {
        var s = new Sprite { IsHitTestVisible = false };
        s.Children.Add(Art.Circle(1.5, 2.5, DiscR, Art.Brush(60, 0, 0, 0)));
        var body = new RadialGradientBrush { GradientOrigin = new RelativePoint(0.35, 0.3, RelativeUnit.Relative) };
        body.GradientStops.Add(new GradientStop(Art.Blend(color, Colors.White, 0.55), 0));
        body.GradientStops.Add(new GradientStop(color, 0.6));
        body.GradientStops.Add(new GradientStop(Art.Blend(color, Colors.Black, 0.35), 1));
        s.Children.Add(Art.Circle(0, 0, DiscR, body, Art.Brush(Art.Blend(color, Colors.Black, 0.5)), 1.2));
        s.Children.Add(Art.Circle(0, 0, DiscR * 0.55, null, Art.Brush(110, 255, 255, 255), 1));
        return s;
    }

    public override void DemoTick()
    {
        if (_moving || _roundEndIn >= 0) return;
        if (_roundOver)
        {
            if (--_demoWait <= 0) NewRound(); // let the round-end popup show first
            return;
        }
        if (FallingCount < 2 && Rng.NextDouble() < 0.5) Drop(XL + DiscR + Rng.NextDouble() * (XR - XL - DiscR * 2));
    }
}
