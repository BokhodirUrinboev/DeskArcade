using System;
using System.Collections.Generic;
using System.Linq;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>A cup standing on a window top (or the taskbar): its centre, the height of its rim, and whether it is still up.</summary>
public sealed class Cup
{
    public const double TopHalf = 18, BottomHalf = 13, Height = 40, Lip = 2.5;
    public double X, RimY;
    public bool Standing = true;
    public IntPtr On;

    /// <summary>
    /// Whether a ball of radius <paramref name="r"/> falling from <paramref name="from"/> to <paramref name="to"/> drops
    /// into the cup: its centre crosses the rim going down, well inside the lips.
    /// </summary>
    public bool Catches(Vec2 from, Vec2 to, double r) =>
        Standing && to.Y > from.Y && from.Y <= RimY && to.Y >= RimY && Math.Abs(to.X - X) < TopHalf - Lip - r * 0.4;

    /// <summary>The two rim lips, which a ball bounces off.</summary>
    public (Vec2 Left, Vec2 Right) Lips => (new Vec2(X - TopHalf, RimY), new Vec2(X + TopHalf, RimY));
}

/// <summary>
/// Ping-Pong Cups' round: ten balls at a rack of six cups standing in a row. A ball that drops into a cup scores ten and
/// the cup comes off; one that bounced on the way (off the floor or a window top) is a bounce shot: twenty, and the
/// cup beside it comes off too. Clear the rack and every ball still in hand is worth five more, and a new rack of six
/// goes up. The round ends when the balls run out. UI-free, so the scoring can be tested; the game does the flying.
/// </summary>
public sealed class CupsRound
{
    public const int Balls = 10, RackSize = 6, CupPoints = 10, BouncePoints = 20, SpareBall = 5;
    public const double Spacing = 40;

    public List<Cup> Cups { get; } = new();
    public int BallsLeft { get; private set; } = Balls;
    public int Score { get; private set; }
    public int Sunk { get; private set; }
    public int Racks { get; private set; }
    public int Throws { get; private set; }
    public int RackThrows { get; private set; } // throws at the rack now standing
    public bool Over => BallsLeft <= 0;
    public int Standing => Cups.Count(c => c.Standing);

    /// <summary>Stands a new rack of six, centred at <paramref name="x"/> on a surface at <paramref name="y"/>.</summary>
    public void Rack(double x, double y, IntPtr on = default)
    {
        Cups.Clear();
        for (int i = 0; i < RackSize; i++)
            Cups.Add(new Cup { X = x + (i - (RackSize - 1) / 2.0) * Spacing, RimY = y - Cup.Height, On = on });
        Racks++;
        RackThrows = 0;
    }

    /// <summary>A ball is thrown; false when there are none left.</summary>
    public bool Throw()
    {
        if (Over) return false;
        BallsLeft--;
        Throws++;
        RackThrows++;
        return true;
    }

    /// <summary>
    /// The ball dropped into <paramref name="cup"/>: the points, and the cups that came off (a bounce shot takes the
    /// nearest standing neighbour too). A cleared rack pays for the balls still in hand.
    /// </summary>
    public (int Points, List<Cup> Off, bool Cleared) Sink(Cup cup, bool bounced)
    {
        if (!cup.Standing) return (0, new List<Cup>(), false);
        var off = new List<Cup> { cup };
        cup.Standing = false;
        int points = bounced ? BouncePoints : CupPoints;
        if (bounced)
        {
            var next = Cups.Where(c => c.Standing).OrderBy(c => Math.Abs(c.X - cup.X)).FirstOrDefault();
            if (next != null)
            {
                next.Standing = false;
                off.Add(next);
            }
        }
        Sunk += off.Count;
        bool cleared = Standing == 0;
        if (cleared) points += BallsLeft * SpareBall;
        Score += points;
        return (points, off, cleared);
    }
}
