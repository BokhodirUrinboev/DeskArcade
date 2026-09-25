using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>
/// Bubble Pop: bubbles bounce around the closed box of your screen (and off window tops). Click one to
/// split it into two smaller bubbles; the smallest ones pop. Clear each wave before the clock runs out.
/// </summary>
public sealed class BubblesGame : MiniGame
{
    const double Gravity = 900, Step = 1.0 / 240, HSpeed = 150, HitPad = 8, ComboWindow = 0.9, WaveBreak = 1.4;

    /// <summary>A fair round for a decent player: waves 1 and 2 cleared with a few combos and some time bonus, and part of wave 3.</summary>
    public const int FairRound = 260;

    static readonly double[] Radius = { 15, 25, 38, 54 };
    static readonly double[] BounceHeight = { 170, 270, 390, 520 };
    static readonly int[] Points = { 5, 3, 2, 1 };
    static readonly Color[] Tints =
    {
        Color.FromRgb(239, 71, 111), Color.FromRgb(255, 209, 102), Color.FromRgb(6, 214, 160), Color.FromRgb(80, 160, 255),
    };
    static readonly Color Gold = Color.FromRgb(255, 209, 102);

    sealed class Bubble
    {
        public required Sprite Sprite;
        public required ScaleTransform Wobble;
        public Anims.Tween? Tween;
        public Vec2 Pos, Vel;
        public int Size;
        public double Age;
        public bool Starter;
    }

    readonly Canvas _bubbleLayer = new() { IsHitTestVisible = false };
    readonly List<Bubble> _bubbles = new();

    int _wave, _score, _combo, _shownSecond = -1;
    double _time, _acc, _timeLeft, _lastPop = -10, _waveIn = -1, _starterIn = -1;
    bool _placed, _active, _beatBest;

    public BubblesGame(IGameHost host) : base(host)
    {
        Layer.Children.Add(_bubbleLayer);
    }

    public override string Id => "bubbles";
    public override string Title => "Bubble Pop";
    public override Sprite CreateIcon() => MakeBubble(9, Tints[3], out _);

    int SecondsLeft => (int)Math.Ceiling(Math.Max(0, _timeLeft));

    public override HudInfo Hud => new(
        _score.ToString(),
        !_active ? L.T("Pop the bubble to start · smaller bubbles score more")
            : _waveIn > 0 ? L.F("Wave {0} cleared!", _wave)
            : _combo > 1
                ? L.F("Wave {0} · {1}s left · bubbles {2} · combo ×{3}", _wave, SecondsLeft, _bubbles.Count, Math.Min(_combo, 3))
                : L.F("Wave {0} · {1}s left · bubbles {2}", _wave, SecondsLeft, _bubbles.Count),
        L.F("Best {0}", Host.Settings.BestBubbles));

    // ------------------------------------------------------------------ game flow

    public override void Layout()
    {
        var a = Host.Arena;
        if (!_placed)
        {
            _placed = true;
            SpawnStarter();
        }
        foreach (var b in _bubbles)
        {
            double r = Radius[b.Size];
            b.Pos.X = Clamp(b.Pos.X, a.Left + r, a.Right - r);
            b.Pos.Y = b.Starter ? a.Bottom - r : Clamp(b.Pos.Y, a.Top + r, a.Bottom - r);
            b.Sprite.Set(b.Pos);
        }
        if (!_active && _starterIn < 0 && _bubbles.Count == 0) SpawnStarter();
        Host.HudChanged();
    }

    void SpawnStarter()
    {
        if (_bubbles.Count > 0) return;
        var a = Host.Arena;
        var b = SpawnBubble(3, new Vec2(a.Left + a.Width * 0.62, a.Bottom - Radius[3]), default);
        b.Starter = true;
        b.Sprite.Children.Add(Art.At(new TextBlock
        {
            Text = L.T("pop me"), Width = Radius[3] * 2, TextAlignment = TextAlignment.Center,
            FontFamily = Fx.Font, FontSize = 13, FontWeight = FontWeight.Bold, Foreground = Brushes.White,
        }, -Radius[3], -9));
        Breathe(b);
    }

    public override bool SupportsLan => true;
    public override (int Score, bool Active)? Race => (_score, _active);
    public override int RaceBaseline => FairRound;
    public override int RaceBest => Host.Settings.BestBubbles;
    public override double RaceSeconds => TypicalRoundSeconds;

    /// <summary>About how long a round lasts: waves 1 and 2 cleared with time to spare, wave 3 played out to the clock.</summary>
    public static double TypicalRoundSeconds => Math.Round(0.6 * WaveSeconds(1) + 0.7 * WaveSeconds(2) + WaveSeconds(3) + 2 * WaveBreak);

    public override void StartRace()
    {
        if (_active) return;
        foreach (var b in _bubbles.ToArray()) Pop(b, false); // the starter goes, sprite and all
        StartGame();
    }

    void StartGame()
    {
        _active = true;
        Host.RoundStarted();
        _score = _combo = 0;
        _wave = 1;
        _beatBest = false;
        _timeLeft = WaveSeconds(1);
        _shownSecond = -1;
        foreach (var b in _bubbles) b.Starter = false;
        Host.Sound.Play("fire", 0.6);
    }

    /// <summary>The clock for a wave: 24 s for the first, 8 s more for each after it, up to a minute.</summary>
    public static double WaveSeconds(int wave) => Math.Min(60, 16 + 8 * wave);

    void SpawnWave(int wave)
    {
        var a = Host.Arena;
        var sizes = new List<int>();
        for (int i = 0; i < Math.Min(4, 1 + (wave - 1) / 2); i++) sizes.Add(3);
        if (wave % 2 == 0) sizes.Add(2);
        if (wave >= 5) sizes.Add(1);
        for (int i = 0; i < sizes.Count; i++)
        {
            var p = new Vec2(a.Left + a.Width * (i + 1) / (sizes.Count + 1), a.Top + Radius[3] + 40 + Rng.NextDouble() * 80);
            SpawnBubble(sizes[i], p, new Vec2(i % 2 == 0 ? HSpeed : -HSpeed, 0));
        }
        _timeLeft = WaveSeconds(wave);
        Host.Stats.Max("bubbles.wave", wave);
        _shownSecond = -1;
        Host.Fx.Popup(new Vec2(a.Center.X, a.Top + a.Height * 0.3), L.F("WAVE {0}", wave), Colors.White, 36, 1.2);
        Host.HudChanged();
    }

    Bubble SpawnBubble(int size, Vec2 pos, Vec2 vel)
    {
        var sprite = MakeBubble(Radius[size], Tints[size], out var wobble);
        var b = new Bubble { Sprite = sprite, Wobble = wobble, Pos = pos, Vel = vel, Size = size };
        b.Sprite.Scale = 0.01;
        b.Sprite.Set(pos);
        _bubbleLayer.Children.Add(b.Sprite);
        _bubbles.Add(b);
        return b;
    }

    void AddScore(int pts)
    {
        _score += pts;
        if (_score <= Host.Settings.BestBubbles) return;
        Host.Settings.BestBubbles = _score;
        _beatBest = true;
        Host.SaveSettings();
    }

    void Pop(Bubble b, bool scored)
    {
        _bubbles.Remove(b);
        Burst(b);
        var tint = Tints[b.Size];
        Host.Sound.Play("pop", 0.7, 1.5 - b.Size * 0.2);
        if (!scored) return;
        Host.Stats.Add("bubbles.popped");

        _combo = _time - _lastPop < ComboWindow ? _combo + 1 : 1;
        _lastPop = _time;
        int mult = Math.Min(_combo, 3);
        int pts = Points[b.Size] * mult;
        AddScore(pts);
        Host.ShareAction(b.Pos, pts);
        Host.Fx.Popup(b.Pos - new Vec2(0, Radius[b.Size] + 10), mult > 1 ? $"+{pts} ×{mult}" : $"+{pts}", tint, 22 + b.Size * 3, 0.8);

        if (b.Size > 0)
        {
            int child = b.Size - 1;
            double kick = -Math.Sqrt(2 * Gravity * BounceHeight[child]) * 0.8;
            SpawnBubble(child, b.Pos + new Vec2(-Radius[child] * 0.6, 0), new Vec2(-HSpeed, kick));
            SpawnBubble(child, b.Pos + new Vec2(Radius[child] * 0.6, 0), new Vec2(HSpeed, kick));
        }
        Host.HudChanged();
        if (_active && _bubbles.Count == 0) WaveCleared();
    }

    void WaveCleared()
    {
        int bonus = SecondsLeft * 2;
        AddScore(bonus);
        var a = Host.Arena;
        var at = new Vec2(a.Center.X, a.Top + a.Height * 0.3);
        Host.ShareAction(at, bonus);
        Host.Fx.Popup(at, L.T("WAVE CLEAR!"), Gold, 40, 1.6, L.F("+{0} time bonus", bonus));
        Host.Fx.Burst(at, Tints, 36, 500, 700, 7, 1.0);
        Host.Sound.Play("fire", 0.8);
        _waveIn = WaveBreak;
        Host.HudChanged();
    }

    void TimeUp()
    {
        _active = false;
        Host.RoundEnded(_score);
        foreach (var b in _bubbles.ToArray()) Pop(b, false);
        var a = Host.Arena;
        var at = new Vec2(a.Center.X, a.Top + a.Height * 0.3);
        Host.Fx.Popup(at, _beatBest ? L.T("NEW BEST!") : L.T("TIME!"), _beatBest ? Gold : Colors.White, 38, 2.4, L.F("{0} points · wave {1}", _score, _wave));
        if (_beatBest) Host.Fx.Burst(at, Tints, 40, 520, 700, 7, 1.1);
        Host.Sound.Play(_beatBest ? "best" : "buzzer", _beatBest ? 0.8 : 0.4);
        _starterIn = 2.2;
        Host.HudChanged();
    }

    // ------------------------------------------------------------------ input

    public override void CollectHitShapes(List<HitShape> into)
    {
        foreach (var b in _bubbles) into.Add(HitShape.Circle(b.Pos, Radius[b.Size] + HitPad));
    }

    public override bool PointerDown(Vec2 p, bool right)
    {
        Bubble? hit = null;
        double nearest = double.MaxValue;
        foreach (var b in _bubbles)
        {
            double d = (b.Pos - p).Length;
            if (d <= Radius[b.Size] + HitPad && d < nearest)
            {
                nearest = d;
                hit = b;
            }
        }
        if (hit == null) return false;
        if (!_active) StartGame();
        Pop(hit, true);
        return false;
    }

    public override void Summon(Vec2 p)
    {
        if (_active) return;
        var a = Host.Arena;
        foreach (var b in _bubbles)
        {
            if (!b.Starter) continue;
            b.Pos = new Vec2(Clamp(p.X, a.Left + Radius[b.Size], a.Right - Radius[b.Size]), a.Bottom - Radius[b.Size]);
            b.Sprite.Set(b.Pos);
        }
    }

    // ------------------------------------------------------------------ simulation

    public override bool Update(double dt)
    {
        _time += dt;
        bool busy = false;

        if (_active)
        {
            busy = true;
            if (_waveIn > 0)
            {
                if ((_waveIn -= dt) <= 0)
                {
                    _waveIn = -1;
                    SpawnWave(++_wave);
                }
            }
            else
            {
                _timeLeft -= dt;
                if (SecondsLeft != _shownSecond)
                {
                    _shownSecond = SecondsLeft;
                    Host.HudChanged();
                    if (_shownSecond is > 0 and <= 5) Host.Sound.Play("rim", 0.25, 2.5);
                }
                if (_timeLeft <= 0) TimeUp();
            }

            _acc += dt;
            while (_acc >= Step)
            {
                _acc -= Step;
                foreach (var b in _bubbles) StepBubble(b, Step);
            }
        }
        else
        {
            _acc = 0;
            if (_starterIn > 0)
            {
                busy = true;
                if ((_starterIn -= dt) <= 0)
                {
                    _starterIn = -1;
                    SpawnStarter();
                }
            }
        }

        foreach (var b in _bubbles)
        {
            b.Age += dt;
            double appear = Fx.ReducedMotion ? 1 : Math.Min(1, b.Age / 0.2);
            b.Sprite.Scale = Math.Max(0.01, appear);
            b.Sprite.Set(b.Pos);
            busy |= appear < 1;
        }
        return Anims.Update(dt) || busy;
    }

    void StepBubble(Bubble b, double h)
    {
        var a = Host.Arena;
        double r = Radius[b.Size];
        double prevBottom = b.Pos.Y + r;
        b.Vel.Y += Gravity * h;
        b.Pos += b.Vel * h;

        // closed box: bounce off the sides and the top of the screen
        if (b.Pos.X - r < a.Left) { b.Pos.X = a.Left + r; b.Vel.X = Math.Abs(b.Vel.X); }
        else if (b.Pos.X + r > a.Right) { b.Pos.X = a.Right - r; b.Vel.X = -Math.Abs(b.Vel.X); }
        if (b.Pos.Y - r < a.Top) { b.Pos.Y = a.Top + r; b.Vel.Y = Math.Abs(b.Vel.Y) * 0.9; }
        if (b.Vel.Y < 0) return;

        double ground = double.NaN;
        if (Host.Platforms.FindLanding(b.Pos.X, prevBottom, b.Pos.Y + r, out var top)) ground = top.Y;
        else if (b.Pos.Y + r >= a.Bottom) ground = a.Bottom;
        if (double.IsNaN(ground)) return;

        b.Pos.Y = ground - r;
        b.Vel.Y = -Math.Sqrt(2 * Gravity * BounceHeight[b.Size]); // every bounce reaches the same height
        Wobble(b);
    }

    // ------------------------------------------------------------------ animation

    /// <summary>A bounce sets the bubble wobbling: it squashes wide, then tall, and settles.</summary>
    void Wobble(Bubble b)
    {
        b.Tween?.Cancel();
        b.Tween = Anims.Add(0.5, k =>
        {
            double w = 0.16 * Math.Sin(k * Math.PI * 3) * (1 - k);
            b.Wobble.ScaleX = 1 + w;
            b.Wobble.ScaleY = 1 - w;
        }, Ease.Linear, () => Settle(b));
    }

    /// <summary>The starter bubble breathes for a few seconds after it appears, then rests (so the screen can go idle).</summary>
    void Breathe(Bubble b)
    {
        b.Tween?.Cancel();
        b.Tween = Anims.Add(4.2, k => b.Wobble.ScaleX = b.Wobble.ScaleY = 1 + 0.045 * Math.Sin(k * Math.PI * 6), Ease.Linear, () => Settle(b), 0.3);
    }

    static void Settle(Bubble b)
    {
        b.Wobble.ScaleX = b.Wobble.ScaleY = 1;
        b.Tween = null;
    }

    /// <summary>The bubble swells and vanishes, and a ring of droplets flies out and falls.</summary>
    void Burst(Bubble b)
    {
        b.Tween?.Cancel();
        var sprite = b.Sprite;
        Anims.Add(0.16, k =>
        {
            sprite.Scale = 1 + 0.45 * k;
            sprite.Opacity = 1 - k;
        }, Ease.OutQuad, () => _bubbleLayer.Children.Remove(sprite));

        var tint = Tints[b.Size];
        double r = Radius[b.Size];
        int count = 5 + b.Size * 2;
        for (int i = 0; i < count; i++)
        {
            double ang = Math.PI * 2 * (i + Rng.NextDouble() * 0.6) / count;
            var dir = new Vec2(Math.Cos(ang), Math.Sin(ang));
            double reach = r * (1.6 + Rng.NextDouble() * 1.2);
            var tr = new TranslateTransform();
            var drop = Art.Circle(0, 0, 2 + b.Size * 0.8 + Rng.NextDouble() * 1.5, Art.Brush(i % 3 == 0 ? Colors.White : tint));
            drop.RenderTransformOrigin = RelativePoint.TopLeft;
            drop.RenderTransform = tr;
            drop.IsHitTestVisible = false;
            _bubbleLayer.Children.Add(drop);
            var from = b.Pos;
            Anims.Add(0.45 + Rng.NextDouble() * 0.2, k =>
            {
                var p = from + dir * (reach * Ease.OutQuad(k)) + new Vec2(0, 160 * k * k); // out, then down
                tr.X = p.X;
                tr.Y = p.Y;
                drop.Opacity = 1 - k * k;
            }, Ease.Linear, () => _bubbleLayer.Children.Remove(drop));
        }
    }

    // ------------------------------------------------------------------ visuals

    /// <summary>A bubble centred on the origin; <paramref name="wobble"/> squashes its skin about its centre.</summary>
    static Sprite MakeBubble(double r, Color tint, out ScaleTransform wobble)
    {
        var s = new Sprite { IsHitTestVisible = false };
        wobble = new ScaleTransform();
        var skin = new Canvas { RenderTransformOrigin = RelativePoint.TopLeft, RenderTransform = wobble };
        var fill = new RadialGradientBrush { GradientOrigin = new RelativePoint(0.35, 0.3, RelativeUnit.Relative) };
        fill.GradientStops.Add(new GradientStop(Color.FromArgb(40, 255, 255, 255), 0));
        fill.GradientStops.Add(new GradientStop(Color.FromArgb(80, tint.R, tint.G, tint.B), 0.65));
        fill.GradientStops.Add(new GradientStop(Color.FromArgb(200, tint.R, tint.G, tint.B), 1));
        skin.Children.Add(Art.Circle(0, 0, r, fill, Art.Brush(Color.FromArgb(230, tint.R, tint.G, tint.B)), Math.Max(1.2, r * 0.05)));
        skin.Children.Add(Art.At(new Avalonia.Controls.Shapes.Ellipse
        {
            Width = r * 0.55, Height = r * 0.3, Fill = Art.Brush(190, 255, 255, 255), RenderTransform = new RotateTransform(-35),
        }, -r * 0.62, -r * 0.6));
        skin.Children.Add(Art.Circle(r * 0.42, r * 0.4, r * 0.09, Art.Brush(150, 255, 255, 255)));
        s.Rotor.Children.Add(skin);
        return s;
    }

    public override void DemoTick()
    {
        if (_bubbles.Count == 0 || (_active && Rng.NextDouble() < 0.5)) return;
        var b = _bubbles[Rng.Next(_bubbles.Count)];
        PointerDown(b.Pos, false);
    }
}
