using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>What stopped a cannonball: a castle's block, a window between the castles, the floor, or the edge of the screen.</summary>
public enum ImpactKind { Block, Window, Ground, Out }

/// <summary>Where a cannonball stopped, and for a hit on a castle which one (0 or 1) and which block.</summary>
public readonly record struct Impact(ImpactKind Kind, Vec2 At, int Castle = -1, int Block = -1)
{
    public bool HitCastle => Kind == ImpactKind.Block;
}

/// <summary>
/// The desk a duel is fought over: the closed box of the arena and the windows standing between the two castles.
/// A window stops a ball on its top edge or its sides, from the top edge down to the floor.
/// </summary>
public sealed record CannonField(Rect Arena, IReadOnlyList<Rect> Windows);

/// <summary>
/// One castle of stone blocks on a grid of <see cref="Cols"/> columns, counted from the back: a tower, the keep that
/// carries the flag, and three columns of front wall with the cannon on top. Every column stands on the ground, so a
/// column is just its height. A ball cracks the block it hits; a second hit anywhere in a cracked column knocks it out
/// from the lower of the two hits, with every block above. The flag stands on the keep and falls when the keep loses a block (a ball
/// flies through the cloth: shots that sail long over the keep are simply long). A castle faces the other one
/// (<see cref="Facing"/> +1: its front is on the right), and its blocks are numbered the same way on both screens of a
/// LAN duel.
/// </summary>
public sealed class Castle
{
    public const int Cols = 5, Rows = 6, KeepCol = 1, CannonCol = 4, Blocks = Rows * Cols;
    /// <summary>The flag above the keep, in blocks: its pole's height, and the cloth at the top of the pole, flying toward the front.</summary>
    public const double FlagHeight = 1.7, ClothWidth = 1.1, ClothHeight = 0.7;

    /// <summary>How tall each column stands in a whole castle, from the back tower through the keep to the front walls.</summary>
    public static readonly int[] FullHeights = { 5, 6, 3, 3, 3 };

    readonly int[] _heights = (int[])FullHeights.Clone();
    readonly bool[] _cracked = new bool[Blocks];

    public Castle(int facing) => Facing = facing >= 0 ? 1 : -1;

    public int Facing { get; }
    /// <summary>The left edge of the castle's footprint and the ground it stands on, in overlay DIPs.</summary>
    public double Left { get; private set; }
    public double BaseY { get; private set; }
    /// <summary>The side of one block.</summary>
    public double Size { get; private set; } = 20;
    public double Width => Cols * Size;
    public bool FlagDown => _heights[KeepCol] < FullHeights[KeepCol];

    /// <summary>Blocks still standing.</summary>
    public int Standing
    {
        get
        {
            int n = 0;
            foreach (int h in _heights) n += h;
            return n;
        }
    }

    public static int WholeCount
    {
        get
        {
            int n = 0;
            foreach (int h in FullHeights) n += h;
            return n;
        }
    }

    public void Place(double left, double baseY, double size)
    {
        Left = left;
        BaseY = baseY;
        Size = size;
    }

    public void Move(Vec2 d)
    {
        Left += d.X;
        BaseY += d.Y;
    }

    public int Height(int col) => _heights[col];

    public static int IndexOf(int col, int row) => row * Cols + col;

    public bool Has(int block)
    {
        if (block < 0 || block >= Blocks) return false;
        return block / Cols < _heights[block % Cols];
    }

    /// <summary>The left edge of a column: the back of the castle is away from the other castle.</summary>
    double ColumnX(int col) => Facing > 0 ? Left + col * Size : Left + (Cols - 1 - col) * Size;

    public Rect BlockRect(int block)
    {
        int r = block / Cols, c = block % Cols;
        return new Rect(ColumnX(c), BaseY - (r + 1) * Size, Size, Size);
    }

    /// <summary>The foot of the flag's pole, on top of the keep.</summary>
    public Vec2 FlagFoot => new(ColumnX(KeepCol) + Size / 2, BaseY - _heights[KeepCol] * Size);

    /// <summary>Where the barrel turns: on top of the front wall, or lower when blocks under the cannon are gone.</summary>
    public Vec2 CannonPivot => new(ColumnX(CannonCol) + Size / 2, BaseY - _heights[CannonCol] * Size - Size * 0.42);

    /// <summary>The pivot of the cannon in a whole castle (the highest it ever stands).</summary>
    public Vec2 FullPivot => new(ColumnX(CannonCol) + Size / 2, BaseY - FullHeights[CannonCol] * Size - Size * 0.42);

    /// <summary>The top of the flag in a whole castle: the castle's full height.</summary>
    public double FullTop => BaseY - (FullHeights[KeepCol] + FlagHeight) * Size;

    /// <summary>A standing block that has taken one hit already.</summary>
    public bool Cracked(int block) => Has(block) && _cracked[block];

    /// <summary>
    /// A ball hits a block. In a sound column it cracks; in a column that is cracked already the column gives way from
    /// the lower of the two hits, and everything above comes down. Returns how many fell (0 when it only cracked).
    /// </summary>
    public int Hit(int block)
    {
        if (!Has(block)) return 0;
        int col = block % Cols, row = block / Cols;
        for (int r = 0; r < _heights[col]; r++)
            if (_cracked[IndexOf(col, r)]) return Knock(IndexOf(col, Math.Min(r, row)));
        _cracked[block] = true;
        return 0;
    }

    /// <summary>The lowest cracked block standing in a column, or −1 when the column is sound.</summary>
    public int CrackIn(int col)
    {
        for (int r = 0; r < _heights[col]; r++)
            if (_cracked[IndexOf(col, r)]) return IndexOf(col, r);
        return -1;
    }

    /// <summary>Knocks a block out whatever its state; the blocks above it in its column come down with it. Returns how many fell.</summary>
    public int Knock(int block)
    {
        if (!Has(block)) return 0;
        int r = block / Cols, c = block % Cols, fell = _heights[c] - r;
        _heights[c] = r;
        return fell;
    }
}

/// <summary>
/// A cannonball in the air: gravity pulls it down and the wind pushes it sideways, one fixed step at a time, until it
/// hits a castle's block, a window, the floor, or leaves the screen at a side. The game and the computer's
/// aim both fly balls this way, so what the computer plans is what happens.
/// </summary>
public sealed class Flight
{
    public const double Step = 1.0 / 240, MaxSeconds = 15;

    public Flight(Vec2 from, Vec2 vel, double radius)
    {
        Pos = from;
        Vel = vel;
        Radius = radius;
    }

    public Vec2 Pos;
    public Vec2 Vel;
    public double Radius { get; }
    public double Time { get; private set; }
    public Impact? Result { get; private set; }

    /// <summary>Advances one step of <paramref name="h"/> seconds; returns the impact once the ball has stopped.</summary>
    public Impact? Advance(double h, double wind, CannonField field, IReadOnlyList<Castle> castles)
    {
        if (Result != null) return Result;
        var prev = Pos;
        Vel.X += wind * h;
        Vel.Y += CannonRules.Gravity * h;
        Pos += Vel * h;
        Time += h;

        // castles first: of everything the ball overlaps now, the part nearest where it came from
        double best = double.MaxValue;
        Impact? hit = null;
        for (int i = 0; i < castles.Count; i++)
        {
            var c = castles[i];
            for (int b = 0; b < Castle.Blocks; b++)
            {
                if (!c.Has(b)) continue;
                var box = c.BlockRect(b);
                if (!Touches(box)) continue;
                double d = (Center(box) - prev).LengthSquared;
                if (d >= best) continue;
                best = d;
                hit = new Impact(ImpactKind.Block, Pos, i, b);
            }
        }
        if (hit == null)
        {
            var a = field.Arena;
            foreach (var w in field.Windows)
                if (Touches(w))
                {
                    hit = new Impact(ImpactKind.Window, Pos);
                    break;
                }
            if (hit == null && Pos.Y + Radius >= a.Bottom) hit = new Impact(ImpactKind.Ground, new Vec2(Pos.X, a.Bottom - Radius));
            else if (hit == null && (Pos.X < a.Left - Radius || Pos.X > a.Right + Radius || Time > MaxSeconds)) hit = new Impact(ImpactKind.Out, Pos);
        }
        Result = hit;
        return hit;
    }

    bool Touches(Rect box) =>
        Pos.X + Radius > box.Left && Pos.X - Radius < box.Right && Pos.Y + Radius > box.Top && Pos.Y - Radius < box.Bottom;

    static Vec2 Center(Rect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    /// <summary>Flies a ball to wherever it stops; <paramref name="path"/> collects its positions if given.</summary>
    public static Impact Fly(Flight f, double wind, CannonField field, IReadOnlyList<Castle> castles, List<Vec2>? path = null)
    {
        while (true)
        {
            path?.Add(f.Pos);
            if (f.Advance(Step, wind, field, castles) is { } impact) return impact;
        }
    }

    /// <summary>
    /// Where a ball crosses the height <paramref name="y"/> on its way down, ignoring everything in the way; a ball
    /// that leaves the screen first reports where it left.
    /// </summary>
    public static double XAtHeight(Flight f, double wind, double y, Rect arena)
    {
        var none = new CannonField(new Rect(arena.X - 1e5, arena.Y - 1e5, arena.Width + 2e5, arena.Height + 2e5), Array.Empty<Rect>());
        while (true)
        {
            var prev = f.Pos;
            f.Advance(Step, wind, none, Array.Empty<Castle>());
            if (f.Vel.Y > 0 && f.Pos.Y >= y)
            {
                double k = f.Pos.Y - prev.Y > 1e-9 ? (y - prev.Y) / (f.Pos.Y - prev.Y) : 1;
                return prev.X + (f.Pos.X - prev.X) * Math.Clamp(k, 0, 1);
            }
            if (f.Pos.X < arena.Left || f.Pos.X > arena.Right || f.Time > MaxSeconds) return f.Pos.X;
        }
    }
}

/// <summary>
/// Cannon Castles, free of UI: two castles (0 on the left, facing right; 1 on the right) take turns, one ball each,
/// in a wind that changes after every shot. Whoever's flag goes down loses. The shooter's screen flies the ball and
/// decides what it hit; <see cref="Land"/> records it, with the wind for the next shot, on both screens of a duel.
/// </summary>
public sealed class CannonRules
{
    public const double Gravity = 900, MaxWind = 150, MinAngle = 0, MaxAngle = 80;
    /// <summary>The weakest shot, as a share of the strongest; and the barrel's length, in blocks.</summary>
    public const double MinPowerShare = 0.3, Barrel = 0.95;

    /// <param name="starter">The side that shoots first.</param>
    /// <param name="wind">The wind for the first shot (a duel opens calm).</param>
    public CannonRules(int starter, double wind = 0)
    {
        Turn = starter == 1 ? 1 : 0;
        Wind = wind;
    }

    public Castle[] Castles { get; } = { new(1), new(-1) };
    public int Turn { get; private set; }
    /// <summary>Shots taken in this game, by both sides.</summary>
    public int Shots { get; private set; }
    public int[] ShotsBy { get; } = new int[2];
    /// <summary>Blocks each side knocked out of the other's castle.</summary>
    public int[] Knocked { get; } = new int[2];
    /// <summary>Sideways push on the ball, in DIPs per second squared; positive blows to the right.</summary>
    public double Wind { get; private set; }

    /// <summary>The side whose flag still stands once the other's is down; null while both fly.</summary>
    public int? Winner => Castles[0].FlagDown ? 1 : Castles[1].FlagDown ? 0 : null;
    public bool Over => Winner != null;

    /// <summary>
    /// Records the shot of the side on turn: the castle it hit (−1 for a miss) and the block, and the wind for the
    /// next shot. Returns how many blocks fell (0 when it only cracked one). The turn passes to the other side unless
    /// the game is over; a shot from the side not on turn is ignored.
    /// </summary>
    public int Land(int shooter, int castle, int block, double nextWind)
    {
        if (Over || shooter != Turn) return 0;
        int fell = 0;
        if (castle is 0 or 1)
        {
            fell = Castles[castle].Hit(block);
            if (castle != shooter) Knocked[shooter] += fell;
        }
        ShotsBy[shooter]++;
        Shots++;
        Wind = Math.Clamp(nextWind, -MaxWind, MaxWind);
        if (!Over) Turn = 1 - shooter;
        return fell;
    }

    /// <summary>A fresh wind for the next shot, in whole units so both screens of a duel show the same number.</summary>
    public static double RandomWind(Random rng) => Math.Round((rng.NextDouble() * 2 - 1) * MaxWind);

    /// <summary>The wind as the scoreboard shows it: "calm", or an arrow and a strength.</summary>
    public static string WindText(double wind) =>
        Math.Abs(wind) < 8 ? L.T("calm") : (wind > 0 ? "→ " : "← ") + (Math.Abs(wind) / 30).ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>Which way the barrel points for an angle above the horizontal, toward the castle's front.</summary>
    public static Vec2 Direction(int facing, double angle)
    {
        double a = Math.Clamp(angle, MinAngle, MaxAngle) * Math.PI / 180;
        return new Vec2(facing * Math.Cos(a), -Math.Sin(a));
    }

    public static double SpeedFor(double power, double maxSpeed) => maxSpeed * (MinPowerShare + (1 - MinPowerShare) * Math.Clamp(power, 0, 1));

    public static double BallRadius(double size) => size * 0.3;

    /// <summary>
    /// The fastest ball that still turns below the top of the screen when fired straight up from a cannon at
    /// <paramref name="pivotY"/> (a closed box: nothing leaves through the ceiling), within sensible bounds.
    /// </summary>
    public static double MaxSpeedFor(double pivotY, double arenaTop) =>
        Math.Clamp(Math.Sqrt(2 * Gravity * Math.Max(1, pivotY - arenaTop - 24)), 420, 1500);

    /// <summary>A ball leaving the castle's cannon at an angle and power (0–1).</summary>
    public static Flight Launch(Castle c, double angle, double power, double maxSpeed)
    {
        var dir = Direction(c.Facing, angle);
        return new Flight(c.CannonPivot + dir * (c.Size * Barrel), dir * SpeedFor(power, maxSpeed), BallRadius(c.Size));
    }

    // ------------------------------------------------------------------ the LAN

    /// <summary>
    /// A finished shot for the other screen: "sh|shot|castle|block|wind", where castle is −1 for a miss, 0 for the
    /// shooter's own castle and 1 for the other one's, so it reads the same from either side.
    /// </summary>
    public static string ShotMessage(int shot, int shooter, int castle, int block, double nextWind) =>
        string.Create(CultureInfo.InvariantCulture, $"sh|{shot}|{(castle < 0 ? -1 : castle == shooter ? 0 : 1)}|{block}|{nextWind:0}");

    /// <summary>Reads a shot from the other screen, where the shooter is side 1: castle comes back as 0, 1 or −1 on this screen.</summary>
    public static bool TryReadShot(string body, out int shot, out int castle, out int block, out double nextWind)
    {
        shot = block = 0;
        castle = -1;
        nextWind = 0;
        var f = body.Split('|');
        if (f.Length != 5 || f[0] != "sh") return false;
        var inv = CultureInfo.InvariantCulture;
        if (!int.TryParse(f[1], NumberStyles.Integer, inv, out shot) || !int.TryParse(f[2], NumberStyles.Integer, inv, out int rel)
            || !int.TryParse(f[3], NumberStyles.Integer, inv, out block) || !double.TryParse(f[4], NumberStyles.Float, inv, out nextWind))
            return false;
        if (rel is < -1 or > 1 || rel >= 0 && block is < 0 or >= Castle.Blocks) return false;
        castle = rel < 0 ? -1 : rel == 0 ? 1 : 0;
        return true;
    }

    /// <summary>
    /// A flying ball as a fraction of the way from the shooter's cannon to the target's (across) and of the screen's
    /// height (up from the shooter's cannon), so it can be drawn between the same two cannons on the other screen.
    /// </summary>
    public static (double U, double V) GhostOut(Vec2 ball, Vec2 fromPivot, Vec2 toPivot, double height)
    {
        double span = toPivot.X - fromPivot.X;
        return (Math.Abs(span) < 1 ? 0 : (ball.X - fromPivot.X) / span, height <= 0 ? 0 : (ball.Y - fromPivot.Y) / height);
    }

    public static Vec2 GhostIn(double u, double v, Vec2 fromPivot, Vec2 toPivot, double height) =>
        new(fromPivot.X + u * (toPivot.X - fromPivot.X), fromPivot.Y + v * height);
}

/// <summary>
/// The computer's gunner. It aims at the keep under the other castle's flag, solving the flight for its angle, but
/// it misjudges: by a slant that holds for the whole game (its eye), by a little on every shot, and in how much of
/// the wind it allows for. After each shot it corrects by part of how far it was off, so it closes in shot by shot;
/// the higher levels misjudge less and correct more. A shot stopped by a window makes it lob higher from then on.
/// </summary>
public sealed class CannonCpu
{
    static readonly double[] Slant = { 320, 220, 140, 80 }, Noise = { 130, 85, 55, 35 };
    static readonly double[] WindSense = { 0.2, 0.55, 0.8, 0.95 }, LearnRate = { 0.4, 0.55, 0.7, 0.85 };
    /// <summary>How long the computer thinks before it turns the barrel, per level (Easy … Expert).</summary>
    public static readonly double[] ThinkSeconds = { 1.3, 1.1, 0.9, 0.75 };

    readonly Random _rng;
    readonly double _slant;
    double _correction, _raise;

    public CannonCpu(int level, Random rng)
    {
        Level = Math.Clamp(level, 1, MiniGame.LevelNames.Length);
        _rng = rng;
        _slant = (rng.NextDouble() < 0.5 ? -1 : 1) * Slant[Level - 1] * (0.6 + rng.NextDouble() * 0.4);
    }

    public int Level { get; }

    /// <summary>What it aims at: the lowest block of the keep that shows above the front wall.</summary>
    public static Vec2 TargetOf(Castle c)
    {
        int row = Math.Clamp(Castle.FullHeights[Castle.CannonCol], 0, c.Height(Castle.KeepCol) - 1);
        var r = c.BlockRect(Castle.IndexOf(Castle.KeepCol, row));
        return new Vec2(r.X + r.Width / 2, r.Y + r.Height / 2);
    }

    /// <summary>Picks an angle and a power (0–1) for a shot from <paramref name="mine"/> at <paramref name="theirs"/>.</summary>
    public (double Angle, double Power) Aim(Castle mine, Castle theirs, double wind, double maxSpeed, CannonField field, IReadOnlyList<Castle> castles)
    {
        int l = Level - 1;
        var target = TargetOf(theirs);
        double aimX = target.X + mine.Facing * (_slant + _correction) + (_rng.NextDouble() * 2 - 1) * Noise[l];
        double sensed = wind * WindSense[l];
        double angle = Math.Clamp(42 + _raise + (_rng.NextDouble() * 2 - 1) * 4, 20, CannonRules.MaxAngle - 3);
        double power = SolvePower(mine, angle, aimX, target.Y, sensed, maxSpeed, field.Arena);
        if (Level < 2) return (angle, power);
        // a window in the way: lob higher until the ball gets over it (as far as it can tell with its sense of the wind)
        for (int tries = 0; tries < 4 && angle < CannonRules.MaxAngle - 3; tries++)
        {
            var impact = Flight.Fly(CannonRules.Launch(mine, angle, power, maxSpeed), sensed, field, castles);
            if (impact.Kind != ImpactKind.Window) break;
            angle = Math.Min(CannonRules.MaxAngle - 3, angle + 9);
            power = SolvePower(mine, angle, aimX, target.Y, sensed, maxSpeed, field.Arena);
        }
        return (angle, power);
    }

    /// <summary>The power that brings the ball down through (x, y), found by halving; 0 or 1 when out of reach.</summary>
    public static double SolvePower(Castle mine, double angle, double x, double y, double wind, double maxSpeed, Rect arena)
    {
        double Past(double p) => mine.Facing * (Flight.XAtHeight(CannonRules.Launch(mine, angle, p, maxSpeed), wind, y, arena) - x);
        if (Past(1) <= 0) return 1;
        if (Past(0) >= 0) return 0;
        double lo = 0, hi = 1;
        for (int i = 0; i < 22; i++)
        {
            double mid = (lo + hi) / 2;
            if (Past(mid) < 0) lo = mid;
            else hi = mid;
        }
        return (lo + hi) / 2;
    }

    /// <summary>
    /// After its shot: how far past the target it came down (negative when short), which the next shot corrects by
    /// part of; or that a window stopped it, which makes it lob higher.
    /// </summary>
    public void Learn(double longBy, bool blocked)
    {
        if (blocked)
        {
            _raise = Math.Min(28, _raise + 10);
            return;
        }
        _correction -= LearnRate[Level - 1] * longBy;
    }

    /// <summary>How far past the target (along the shooter's facing) a ball came down; negative when short.</summary>
    public static double LongBy(Castle mine, Vec2 target, Impact impact) => mine.Facing * (impact.At.X - target.X);

    /// <summary>A shot stopped by a window well short of the target castle.</summary>
    public static bool Blocked(Castle mine, Castle theirs, Vec2 target, Impact impact) =>
        impact.Kind == ImpactKind.Window && LongBy(mine, target, impact) < -theirs.Width;
}
