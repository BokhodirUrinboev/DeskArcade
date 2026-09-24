using System;
using Avalonia;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>
/// The Air Hockey table without any UI: the puck, both mallets, the walls and goal mouths, and the
/// computer player. The left mallet ("me") defends the left goal, the right one ("cpu") the right goal;
/// over the LAN a person drives the right mallet instead. <see cref="HockeyGame"/> draws it and runs the
/// match; tests drive it directly.
/// </summary>
public sealed class HockeyTable
{
    public const double PuckR = 22, MalletR = 36, PostR = 7, Step = 1.0 / 240;
    const double MaxPuck = 2600, Friction = 0.35, WallBounce = 0.88, MalletBounce = 0.92, GoalFraction = 0.34;

    /// <summary>Mallet speed per skill (Easy … Expert) at the start of a session, in px/s.</summary>
    public static readonly double[] BaseSpeeds = { 560, 700, 860, 1020 };
    /// <summary>Every match the computer loses adds this much to its speed, up to <see cref="MaxCpuSpeed"/>.</summary>
    public const double SpeedPerWin = 70, MaxCpuSpeed = 1350;
    static readonly double[] Reactions = { 0.35, 0.2, 0.08, 0 };
    static readonly double[] Errors = { 30, 16, 6, 0 };

    readonly Random _rng;
    double _acc, _stuckT, _aimCpu, _aimMe, _cpuBackOff, _cpuReactT, _cpuErr;
    bool _puckOnCpuSide;

    public HockeyTable(Rect arena, Random? rng = null)
    {
        Arena = arena;
        _rng = rng ?? new Random();
        Me = Home(false);
        Cpu = Home(true);
    }

    public Rect Arena { get; set; }
    public Vec2 Puck, PuckVel, Me, MeVel, Cpu, CpuVel;
    /// <summary>False while a goal is being celebrated and the next serve waits: the puck is off the table.</summary>
    public bool PuckInPlay { get; set; } = true;
    /// <summary>The computer's base strength, 1 (Easy) to 4 (Expert): tray → CPU difficulty.</summary>
    public int Skill { get; set; } = 2;
    /// <summary>The computer's level within a session: it starts at 1 and its mallet gets faster with every match it loses.</summary>
    public int Level { get; set; } = 1;

    /// <summary>A goal: true when the left player ("me") scored in the right-hand goal.</summary>
    public event Action<bool>? Goal;
    /// <summary>A hit worth a sound: name, volume, pitch.</summary>
    public event Action<string, double, double>? Hit;

    public double GoalTop => Arena.Center.Y - Arena.Height * GoalFraction / 2;
    public double GoalBottom => Arena.Center.Y + Arena.Height * GoalFraction / 2;
    public double HomeInset => Math.Max(110, Arena.Width * 0.08);
    public double CpuSpeed => SpeedFor(Skill, Level);

    /// <summary>How fast the computer's mallet moves (px/s) at a skill, after the session's wins have sped it up.</summary>
    public static double SpeedFor(int skill, int level) =>
        Math.Min(MaxCpuSpeed, BaseSpeeds[Math.Clamp(skill, 1, BaseSpeeds.Length) - 1] + SpeedPerWin * Math.Max(0, level - 1));

    /// <summary>Seconds the computer waits, watching from its goal, when the puck comes over to its half.</summary>
    public static double ReactionFor(int skill) => Reactions[Math.Clamp(skill, 1, Reactions.Length) - 1];

    /// <summary>How far (px) the computer's approach can be off the line that would send the puck where it aims.</summary>
    public static double ErrorFor(int skill) => Errors[Math.Clamp(skill, 1, Errors.Length) - 1];

    /// <summary>Where a serve puts the puck: at rest on the left (−1), in the middle (0) or on the right (1).</summary>
    public Vec2 ServeSpot(int side) => new(Arena.Center.X + side * Arena.Width * 0.22, Arena.Center.Y);

    public Vec2 Home(bool cpu) => new(cpu ? Arena.Right - HomeInset : Arena.Left + HomeInset, Arena.Center.Y);

    /// <summary>Keeps a mallet on its own half and on the table.</summary>
    public Vec2 ClampSide(Vec2 p, bool cpu)
    {
        var a = Arena;
        double cx = a.Center.X;
        p.X = cpu ? Math.Clamp(p.X, cx + MalletR, a.Right - MalletR) : Math.Clamp(p.X, a.Left + MalletR, cx - MalletR);
        p.Y = Math.Clamp(p.Y, a.Top + MalletR, a.Bottom - MalletR);
        return p;
    }

    /// <summary>Puts the puck at rest on the left (−1), in the middle (0) or on the right (1).</summary>
    public void PlacePuck(int side)
    {
        Puck = ServeSpot(side);
        PuckVel = default;
        _stuckT = 0;
    }

    /// <summary>Where the computer's mallet goes this frame; <paramref name="holdHome"/> parks it in front of its goal.</summary>
    public Vec2 CpuMove(double dt, bool holdHome)
    {
        if (_cpuBackOff > 0) _cpuBackOff -= dt;
        if (_cpuReactT > 0) _cpuReactT -= dt;
        var target = holdHome || _cpuBackOff > 0 ? Home(true) : AiTarget(Cpu, true, _cpuReactT > 0);
        return MoveToward(Cpu, ClampSide(target, true), CpuSpeed * dt);
    }

    /// <summary>The same AI for the left mallet (demo mode).</summary>
    public Vec2 DemoMove(double dt) => MoveToward(Me, ClampSide(AiTarget(Me, false, false), false), CpuSpeed * dt);

    /// <summary>
    /// Moves both mallets to where they should be after <paramref name="dt"/>, sweeping them smoothly
    /// through the physics sub-steps so a fast swing still hits the puck.
    /// </summary>
    public void Advance(double dt, Vec2 meTo, Vec2 cpuTo)
    {
        bool cpuSide = Puck.X > Arena.Center.X;
        if (cpuSide != _puckOnCpuSide)
        {
            _puckOnCpuSide = cpuSide; // each time the puck changes sides, pick a new spot to shoot at
            _aimCpu = _rng.NextDouble() * 2 - 1;
            _aimMe = _rng.NextDouble() * 2 - 1;
            _cpuErr = (_rng.NextDouble() * 2 - 1) * ErrorFor(Skill); // and misjudge the approach a little, at the easier levels
            if (cpuSide) _cpuReactT = ReactionFor(Skill);
        }
        Vec2 meFrom = Me, cpuFrom = Cpu;
        meTo = KeepOffPinnedPuck(meTo, false);
        cpuTo = KeepOffPinnedPuck(cpuTo, true);
        if (dt > 0)
        {
            MeVel = Cap((meTo - meFrom) / dt);
            CpuVel = Cap((cpuTo - cpuFrom) / dt);
        }

        _acc += dt;
        int steps = (int)(_acc / Step);
        _acc -= steps * Step;
        for (int i = 1; i <= steps; i++)
        {
            double k = (double)i / steps;
            Me = meFrom + (meTo - meFrom) * k;
            Cpu = cpuFrom + (cpuTo - cpuFrom) * k;
            if (PuckInPlay) SimStep(Step);
        }
        Me = KeepOffPinnedPuck(meTo, false);
        Cpu = KeepOffPinnedPuck(cpuTo, true);
    }

    static Vec2 Cap(Vec2 v) => v.Length > 4000 ? v * (4000 / v.Length) : v;

    void SimStep(double h)
    {
        var a = Arena;
        PuckVel *= 1 - Friction * h;
        if (PuckVel.Length < 15) PuckVel = default;
        Puck += PuckVel * h;

        HitMallet(Me, MeVel);
        HitMallet(Cpu, CpuVel);

        double gTop = GoalTop, gBottom = GoalBottom;
        bool inMouth = Puck.Y > gTop && Puck.Y < gBottom;
        foreach (var post in new[] { new Vec2(a.Left, gTop), new Vec2(a.Left, gBottom), new Vec2(a.Right, gTop), new Vec2(a.Right, gBottom) })
            HitPoint(post, PostR, WallBounce);

        // closed box: every edge bounces, except the goal mouths on the left and right
        if (Puck.X - PuckR < a.Left)
        {
            if (inMouth)
            {
                if (Puck.X <= a.Left + 2) Scored(false);
            }
            else
            {
                Puck.X = a.Left + PuckR;
                if (PuckVel.X < 0) Bounce(ref PuckVel.X);
            }
        }
        else if (Puck.X + PuckR > a.Right)
        {
            if (inMouth)
            {
                if (Puck.X >= a.Right - 2) Scored(true);
            }
            else
            {
                Puck.X = a.Right - PuckR;
                if (PuckVel.X > 0) Bounce(ref PuckVel.X);
            }
        }
        if (Puck.Y - PuckR < a.Top)
        {
            Puck.Y = a.Top + PuckR;
            if (PuckVel.Y < 0) Bounce(ref PuckVel.Y);
        }
        else if (Puck.Y + PuckR > a.Bottom)
        {
            Puck.Y = a.Bottom - PuckR;
            if (PuckVel.Y > 0) Bounce(ref PuckVel.Y);
        }
    }

    void Scored(bool leftPlayer)
    {
        PuckVel = default;
        PuckInPlay = false;
        Goal?.Invoke(leftPlayer);
    }

    void Bounce(ref double v)
    {
        Hit?.Invoke("rim", Math.Min(0.5, Math.Abs(v) / 3000), 1.9);
        v = -v * WallBounce;
    }

    void HitMallet(Vec2 m, Vec2 mv)
    {
        Vec2 d = Puck - m;
        double dist = d.Length, min = PuckR + MalletR;
        if (dist >= min) return;
        Vec2 n = dist < 1e-6 ? new Vec2(1, 0) : d / dist;
        Puck = m + n * min;
        double vn = Vec2.Dot(PuckVel - mv, n);
        if (vn >= 0) return;
        PuckVel -= n * ((1 + MalletBounce) * vn);
        double speed = PuckVel.Length;
        if (speed > MaxPuck) PuckVel *= MaxPuck / speed;
        Hit?.Invoke("board", Math.Min(0.9, -vn / 1800), 1.35);
    }

    void HitPoint(Vec2 c, double r, double bounce)
    {
        Vec2 d = Puck - c;
        double dist = d.Length, min = PuckR + r;
        if (dist >= min || dist < 1e-6) return;
        Vec2 n = d / dist;
        Puck = c + n * min;
        double vn = Vec2.Dot(PuckVel, n);
        if (vn >= 0) return;
        PuckVel -= n * ((1 + bounce) * vn);
        Hit?.Invoke("rim", Math.Min(0.6, -vn / 2000), 1.5);
    }

    /// <summary>
    /// Where a computer-controlled mallet wants to be: defend its goal while the puck is away (or while it is
    /// still <paramref name="reacting"/> to the puck's arrival), otherwise get behind the puck and drive through
    /// it toward the other goal.
    /// </summary>
    Vec2 AiTarget(Vec2 mallet, bool cpu, bool reacting)
    {
        var a = Arena;
        double side = cpu ? 1 : -1;
        bool puckOnMySide = (Puck.X - a.Center.X) * side > 0;
        var home = new Vec2(cpu ? a.Right - HomeInset : a.Left + HomeInset, Math.Clamp(Puck.Y, GoalTop + 20, GoalBottom - 20));
        if (!puckOnMySide || !PuckInPlay || reacting) return home;
        if (PuckVel.X * side > 300 && (mallet.X - Puck.X) * side > 0) return home; // puck already coming at us fast: block

        double aim = cpu ? _aimCpu : _aimMe;
        double aimY = a.Center.Y + aim * (GoalBottom - GoalTop) * 0.4;
        if (Math.Abs(aim) > 0.8) aimY = aim > 0 ? 2 * a.Bottom - aimY : 2 * a.Top - aimY; // bank it off the edge
        var goal = new Vec2(cpu ? a.Left : a.Right, aimY);
        Vec2 dir = (goal - Puck).Normalized();
        Vec2 perp = new(-dir.Y, dir.X);
        Vec2 behind = Puck - dir * (PuckR + MalletR - 4) + perp * (cpu ? _cpuErr : 0);
        if (Vec2.Dot(mallet - Puck, dir) > 0)
        {
            // on the wrong side of the puck: swing around it
            double around = Vec2.Dot(mallet - Puck, perp) >= 0 ? 1 : -1;
            return behind + perp * (around * (PuckR + MalletR + 12));
        }
        return (mallet - behind).Length < 24 ? Puck + dir * 60 : behind;
    }

    /// <summary>
    /// A puck pressed against an edge can't be pushed any further, so a mallet driven into it stops at
    /// contact instead of sliding over it. Without this a mallet parked in a corner swallows the puck.
    /// </summary>
    public Vec2 KeepOffPinnedPuck(Vec2 m, bool cpu)
    {
        if (!PuckInPlay) return m;
        var a = Arena;
        bool inMouth = Puck.Y > GoalTop && Puck.Y < GoalBottom;
        bool pinned = Puck.Y <= a.Top + PuckR + 1 || Puck.Y >= a.Bottom - PuckR - 1 ||
                      !inMouth && (Puck.X <= a.Left + PuckR + 1 || Puck.X >= a.Right - PuckR - 1);
        Vec2 d = m - Puck;
        double min = PuckR + MalletR;
        if (!pinned || d.Length >= min) return m;
        Vec2 n = d.Length < 1e-6 ? (new Vec2(a.Center.X, a.Center.Y) - Puck).Normalized() : d / d.Length;
        return ClampSide(Puck + n * min, cpu);
    }

    /// <summary>
    /// A puck parked out of reach (e.g. in a corner) drifts back toward the middle. With a
    /// <paramref name="humanRival"/> on the right, a puck resting on that side is theirs to play.
    /// </summary>
    public void Unstick(double dt, bool humanRival, bool active)
    {
        if (!active || !PuckInPlay || PuckVel.Length > 30)
        {
            _stuckT = 0;
            return;
        }
        var a = Arena;
        bool cornered = Puck.Y < a.Top + MalletR * 1.4 || Puck.Y > a.Bottom - MalletR * 1.4 ||
                        Puck.X < a.Left + MalletR * 1.4 || Puck.X > a.Right - MalletR * 1.4;
        bool cpuSide = Puck.X > a.Center.X;
        if (!cornered && (!cpuSide || humanRival)) return;
        // the CPU leaning on a cornered puck frees it quickly; anything else gets a few seconds to play it
        bool cpuPinning = cornered && cpuSide && !humanRival && (Cpu - Puck).Length < PuckR + MalletR + 8;
        if ((_stuckT += dt) < (cpuPinning ? 0.8 : 3)) return;
        _stuckT = 0;
        if (cpuSide && !humanRival) _cpuBackOff = 1.0; // step aside so the puck can come out
        PuckVel = (new Vec2(a.Center.X, a.Center.Y) - Puck).Normalized() * 420;
    }

    public static Vec2 MoveToward(Vec2 from, Vec2 to, double maxStep)
    {
        Vec2 d = to - from;
        double len = d.Length;
        return len <= maxStep ? to : from + d * (maxStep / len);
    }
}
