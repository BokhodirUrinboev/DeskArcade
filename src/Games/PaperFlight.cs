using System;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>
/// The physics of Paper Toss, kept free of UI so it can be tested: a crumpled paper ball under gravity, heavy air
/// drag and the desk fan's wind; the wastebasket's rim lips, walls and bottom; the "it's in" rule; and an aim
/// solver that finds a launch velocity dropping the paper into the bin's opening under a given wind.
/// A bin is described by its base: the middle of its bottom, standing on a window top or the taskbar.
/// </summary>
public static class PaperFlight
{
    public const double R = 14, Gravity = 1500, Drag = 1.1, WindAccel = 70, MaxWind = 6, Step = 1.0 / 240, MaxThrow = 3600;
    public const double BinTopW = 70, BinBottomW = 54, BinH = 80, LipR = 3, WallThick = 2.5, BottomThick = 2;
    const double LipBounce = 0.35, WallBounce = 0.3, BottomBounce = 0.15, MaxFlight = 3.5, MinFlight = 0.3;

    /// <summary>What the paper hit on the bin this step (impact speeds, 0 for no hit).</summary>
    public readonly record struct Contact(double Rim, double Wall, double Bottom);

    /// <summary>The wind limit for the next throw: calm to start with, gustier the longer the streak.</summary>
    public static double WindLimit(int streak) => Math.Min(MaxWind, 1.5 + 0.75 * streak);

    /// <summary>A basket is worth 1, a swish (no rim or wall touched) 1 more.</summary>
    public static int Points(bool swish) => swish ? 2 : 1;

    /// <summary>One step of the forces on a flying paper ball: wind pushes it sideways, drag slows it toward still air.</summary>
    public static void Accelerate(ref Vec2 vel, double wind, double h)
    {
        vel.X += wind * WindAccel * h;
        vel.Y += Gravity * h;
        vel *= 1 - Drag * h;
    }

    /// <summary>One step of free flight (the same integration the game gets from <see cref="BallBody.Step"/>).</summary>
    public static void Fly(ref Vec2 pos, ref Vec2 vel, double wind, double h)
    {
        Accelerate(ref vel, wind, h);
        pos += vel * h;
    }

    public static Vec2 LipLeft(Vec2 bin) => new(bin.X - BinTopW / 2, bin.Y - BinH);
    public static Vec2 LipRight(Vec2 bin) => new(bin.X + BinTopW / 2, bin.Y - BinH);

    /// <summary>The middle of the bin's opening, where the aim solver sends the paper.</summary>
    public static Vec2 Opening(Vec2 bin) => new(bin.X, bin.Y - BinH + 2);

    /// <summary>Half the bin's width at height <paramref name="y"/> (it tapers toward the bottom).</summary>
    static double HalfWidthAt(Vec2 bin, double y)
    {
        double k = Math.Clamp((y - (bin.Y - BinH)) / BinH, 0, 1);
        return (BinTopW + (BinBottomW - BinTopW) * k) / 2;
    }

    /// <summary>Bounces the paper off the bin: the lips are round, the walls work from inside and outside.</summary>
    public static Contact Collide(BallBody paper, Vec2 bin)
    {
        Vec2 tl = LipLeft(bin), tr = LipRight(bin);
        Vec2 bl = new(bin.X - BinBottomW / 2, bin.Y - BottomThick), br = new(bin.X + BinBottomW / 2, bin.Y - BottomThick);
        // lips first: they are a little fatter than the walls, so a hit at the top counts as the rim
        double rim = Math.Max(paper.CollidePoint(tl, LipR, LipBounce), paper.CollidePoint(tr, LipR, LipBounce));
        double wall = Math.Max(paper.CollideSegment(tl, bl, WallThick, WallBounce), paper.CollideSegment(tr, br, WallThick, WallBounce));
        double bottom = paper.CollideSegment(bl, br, BottomThick, BottomBounce);
        return new Contact(rim, wall, bottom);
    }

    /// <summary>In: the paper is inside the walls, below the rim and falling, so it can no longer bounce out.</summary>
    public static bool IsIn(Vec2 pos, Vec2 vel, Vec2 bin)
    {
        double rimY = bin.Y - BinH;
        if (vel.Y <= 0 || pos.Y < rimY + 4 || pos.Y > bin.Y) return false;
        return Math.Abs(pos.X - bin.X) < HalfWidthAt(bin, pos.Y) - WallThick;
    }

    /// <summary>
    /// A launch velocity from <paramref name="from"/> whose flight passes through <paramref name="target"/> falling
    /// steeply (at least <paramref name="minSteep"/> down per unit across, so it clears the lips) under
    /// <paramref name="wind"/>. Of the flight times that work it picks the gentlest throw. False when no throw up to
    /// <paramref name="maxSpeed"/> gets there without the paper touching <paramref name="ceiling"/>.
    /// </summary>
    public static bool SolveThrow(Vec2 from, Vec2 target, double wind, out Vec2 velocity,
        double maxSpeed = MaxThrow, double ceiling = double.NegativeInfinity, double minSteep = 1.2)
    {
        // Flight is linear in the launch velocity: after n steps pos = from + v0 * A[n] + B[n], where B is the
        // path of a paper released at rest (gravity and wind only) and A is how far each unit of v0 carries.
        int steps = (int)(MaxFlight / Step);
        var a = new double[steps + 1];
        var by = new double[steps + 1];
        double decay = 1 - Drag * Step, f = 1, sumA = 0;
        Vec2 restVel = default, restPos = default;
        var acc = new Vec2(wind * WindAccel, Gravity);

        velocity = default;
        double best = double.MaxValue;
        for (int n = 1; n <= steps; n++)
        {
            f *= decay;
            sumA += Step * f;
            restVel = (restVel + acc * Step) * decay;
            restPos += restVel * Step;
            a[n] = sumA;
            by[n] = restPos.Y;
            if (n * Step < MinFlight) continue;

            Vec2 v0 = (target - from - restPos) / sumA;
            double speed = v0.Length;
            if (speed > maxSpeed || speed >= best) continue;
            Vec2 arrive = v0 * f + restVel;
            if (arrive.Y <= 0 || arrive.Y < minSteep * Math.Abs(arrive.X)) continue;
            if (!BelowCeiling(from.Y, v0.Y, a, by, n, ceiling + R)) continue;
            best = speed;
            velocity = v0;
        }
        return best < double.MaxValue;
    }

    static bool BelowCeiling(double y0, double vy, double[] a, double[] by, int n, double top)
    {
        if (double.IsNegativeInfinity(top)) return true;
        for (int i = 1; i <= n; i++)
            if (y0 + vy * a[i] + by[i] < top) return false;
        return true;
    }
}
