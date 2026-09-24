using System;
using Avalonia;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>
/// Pong on the screen edges, without any UI: a paddle at each side of the screen, a ball that bounces off
/// the top and bottom, and a point when it gets past a paddle. The left paddle ("me") is the player's; the
/// right one is the computer's, or the other player's over the LAN. <see cref="PongGame"/> draws it.
/// </summary>
public sealed class PongTable
{
    public const double BallR = 11, PaddleW = 16, PaddleH = 130, Inset = 34, Step = 1.0 / 240;
    const double ServeSpeed = 620, MaxSpeed = 1700, SpeedUp = 1.06, MaxAngle = 60;

    /// <summary>Paddle speed per skill (Easy … Expert) at the start of a session, in px/s.</summary>
    public static readonly double[] BaseSpeeds = { 380, 520, 680, 840 };
    /// <summary>Every match the computer loses adds this much to its speed, up to <see cref="MaxCpuSpeed"/>.</summary>
    public const double SpeedPerWin = 60, MaxCpuSpeed = 1150;
    static readonly double[] Errors = { 1.15, 0.9, 0.55, 0.25 };
    static readonly double[] Reactions = { 0.3, 0.18, 0.08, 0 };

    readonly Random _rng;
    double _acc, _cpuError, _cpuReactT;

    public PongTable(Rect arena, Random? rng = null)
    {
        Arena = arena;
        _rng = rng ?? new Random();
        MeY = ThemY = arena.Center.Y;
        Ball = new Vec2(arena.Center.X, arena.Center.Y);
    }

    public Rect Arena { get; set; }
    public Vec2 Ball, BallVel;
    /// <summary>Paddle centres (y) and their speeds (px/s, used for spin).</summary>
    public double MeY, ThemY, MeVel, ThemVel;
    public bool BallInPlay { get; set; }
    /// <summary>The computer's base strength, 1 (Easy) to 4 (Expert): tray → CPU difficulty.</summary>
    public int Skill { get; set; } = 2;
    /// <summary>The computer's level within a session: it starts at 1 and moves faster and aims better with every match it loses.</summary>
    public int Level { get; set; } = 1;
    /// <summary>Paddle hits in the current rally.</summary>
    public int Rally { get; private set; }

    /// <summary>A point: true when the left player ("me") scored.</summary>
    public event Action<bool>? Point;
    /// <summary>A hit worth a sound: name, volume, pitch.</summary>
    public event Action<string, double, double>? Hit;

    public double MeX => Arena.Left + Inset;
    public double ThemX => Arena.Right - Inset;
    double CpuSpeed => SpeedFor(Skill, Level);

    /// <summary>How fast the computer's paddle moves (px/s) at a skill, after the session's wins have sped it up.</summary>
    public static double SpeedFor(int skill, int level) =>
        Math.Min(MaxCpuSpeed, BaseSpeeds[Math.Clamp(skill, 1, BaseSpeeds.Length) - 1] + SpeedPerWin * Math.Max(0, level - 1));

    /// <summary>How far the computer can misjudge a return, as a share of the paddle's height; it shrinks as the session goes on.</summary>
    public static double ErrorFor(int skill, int level) =>
        Errors[Math.Clamp(skill, 1, Errors.Length) - 1] * Math.Max(0.25, 1 - 0.08 * Math.Max(0, level - 1));

    /// <summary>Seconds the computer stands still after the ball is sent its way.</summary>
    public static double ReactionFor(int skill) => Reactions[Math.Clamp(skill, 1, Reactions.Length) - 1];

    public double ClampPaddle(double y) => Math.Clamp(y, Arena.Top + PaddleH / 2, Arena.Bottom - PaddleH / 2);

    /// <summary>Puts the ball in the middle and sends it toward the left (−1) or right (1) player.</summary>
    public void Serve(int toward)
    {
        Ball = new Vec2(Arena.Center.X, Arena.Center.Y + (_rng.NextDouble() - 0.5) * Arena.Height * 0.3);
        double angle = (_rng.NextDouble() * 2 - 1) * 30 * Math.PI / 180;
        BallVel = new Vec2(Math.Cos(angle) * toward, Math.Sin(angle)) * ServeSpeed;
        BallInPlay = true;
        Rally = 0;
        _cpuError = 0;
        _cpuReactT = 0;
    }

    /// <summary>Where the computer's paddle goes this frame: toward where the ball will arrive, with a human-ish error.</summary>
    public double CpuMove(double dt)
    {
        if (_cpuReactT > 0)
        {
            _cpuReactT -= dt;
            return ThemY; // hasn't reacted yet
        }
        double target = Arena.Center.Y;
        if (BallInPlay && BallVel.X > 0)
            target = PredictY(ThemX - PaddleW / 2 - BallR) + _cpuError;
        double step = CpuSpeed * dt, d = target - ThemY;
        return ClampPaddle(Math.Abs(d) <= step ? target : ThemY + Math.Sign(d) * step);
    }

    /// <summary>The ball's height when it reaches <paramref name="x"/>, folding its path at the top and bottom.</summary>
    public double PredictY(double x)
    {
        if (BallVel.X == 0) return Ball.Y;
        double t = (x - Ball.X) / BallVel.X;
        if (t < 0) return Ball.Y;
        double top = Arena.Top + BallR, span = Arena.Bottom - BallR - top;
        if (span <= 0) return Ball.Y;
        double y = Ball.Y + BallVel.Y * t - top;
        double m = ((y % (2 * span)) + 2 * span) % (2 * span);
        return top + (m <= span ? m : 2 * span - m);
    }

    /// <summary>Moves both paddles to their new heights through the sub-steps and runs the ball.</summary>
    public void Advance(double dt, double meTo, double themTo)
    {
        meTo = ClampPaddle(meTo);
        themTo = ClampPaddle(themTo);
        double meFrom = MeY, themFrom = ThemY;
        if (dt > 0)
        {
            MeVel = (meTo - meFrom) / dt;
            ThemVel = (themTo - themFrom) / dt;
        }
        _acc += dt;
        int steps = (int)(_acc / Step);
        _acc -= steps * Step;
        for (int i = 1; i <= steps; i++)
        {
            double k = (double)i / steps;
            MeY = meFrom + (meTo - meFrom) * k;
            ThemY = themFrom + (themTo - themFrom) * k;
            if (BallInPlay) SimStep(Step);
        }
        MeY = meTo;
        ThemY = themTo;
    }

    void SimStep(double h)
    {
        Ball += BallVel * h;
        var a = Arena;
        if (Ball.Y - BallR < a.Top && BallVel.Y < 0)
        {
            Ball.Y = a.Top + BallR;
            BallVel.Y = -BallVel.Y;
            Hit?.Invoke("rim", 0.3, 1.9);
        }
        else if (Ball.Y + BallR > a.Bottom && BallVel.Y > 0)
        {
            Ball.Y = a.Bottom - BallR;
            BallVel.Y = -BallVel.Y;
            Hit?.Invoke("rim", 0.3, 1.9);
        }

        if (BallVel.X < 0) Paddle(MeX, MeY, MeVel, 1);
        else Paddle(ThemX, ThemY, ThemVel, -1);

        if (Ball.X < a.Left - BallR) Scored(false);
        else if (Ball.X > a.Right + BallR) Scored(true);
    }

    /// <summary>Bounces the ball off a paddle; <paramref name="dir"/> is the way it sends the ball.</summary>
    void Paddle(double x, double y, double vel, int dir)
    {
        double face = x + dir * PaddleW / 2;
        bool reached = dir > 0 ? Ball.X - BallR <= face && Ball.X > x - PaddleW : Ball.X + BallR >= face && Ball.X < x + PaddleW;
        if (!reached || Math.Abs(Ball.Y - y) > PaddleH / 2 + BallR) return;
        double offset = Math.Clamp((Ball.Y - y) / (PaddleH / 2), -1, 1);
        double speed = Math.Min(MaxSpeed, BallVel.Length * SpeedUp);
        double angle = offset * MaxAngle * Math.PI / 180;
        BallVel = new Vec2(Math.Cos(angle) * dir * speed, Math.Sin(angle) * speed + vel * 0.15);
        Ball.X = face + dir * BallR;
        Rally++;
        // the computer misjudges some returns and is slow off the mark, more so at the easier levels
        _cpuError = dir > 0 ? (_rng.NextDouble() * 2 - 1) * (PaddleH * 0.9) * ErrorFor(Skill, Level) : 0;
        if (dir > 0) _cpuReactT = ReactionFor(Skill);
        Hit?.Invoke("board", Math.Min(0.9, 0.35 + speed / 3000), 1.2 + Math.Min(0.6, Rally * 0.03));
    }

    void Scored(bool left)
    {
        BallInPlay = false;
        BallVel = default;
        Point?.Invoke(left);
    }
}
