using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using DeskArcade.Engine;
using DeskArcade.Games;
using DeskArcade.Net;
using Xunit;

namespace DeskArcade.Tests;

public class HockeyTableTests
{
    static readonly Rect Arena = new(0, 0, 1920, 1040);
    const double Touch = HockeyTable.PuckR + HockeyTable.MalletR;

    static bool Cornered(HockeyTable t) =>
        t.Puck.X > Arena.Right - 150 && (t.Puck.Y < Arena.Top + 150 || t.Puck.Y > Arena.Bottom - 150);

    [Theory]
    [InlineData(1)]  // top-right corner
    [InlineData(-1)] // bottom-right corner
    public void TheCpuNeverCoversACorneredPuckAndLetsItOut(int corner)
    {
        var t = new HockeyTable(Arena, new Random(1));
        t.Puck = new Vec2(Arena.Right - HockeyTable.PuckR, corner > 0 ? Arena.Top + HockeyTable.PuckR : Arena.Bottom - HockeyTable.PuckR);
        const double dt = 1.0 / 60;
        double freedAt = -1;
        for (int frame = 0; frame < 60 * 8; frame++)
        {
            t.Advance(dt, t.Me, t.CpuMove(dt, holdHome: false));
            t.Unstick(dt, humanRival: false, active: true);
            Assert.True((t.Cpu - t.Puck).Length >= Touch - 1, $"frame {frame}: the CPU mallet covers the puck");
            if (freedAt < 0 && !Cornered(t)) freedAt = frame * dt;
        }
        Assert.True(freedAt >= 0 && freedAt < 4, $"the puck stayed in the corner (freed at {freedAt:0.00}s)");
    }

    [Fact]
    public void AMalletDrivenIntoAPinnedPuckStopsAtContact()
    {
        var t = new HockeyTable(Arena, new Random(2));
        var corner = new Vec2(Arena.Left + HockeyTable.PuckR, Arena.Top + HockeyTable.PuckR);
        t.Puck = corner;
        for (int frame = 0; frame < 120; frame++)
        {
            t.Advance(1.0 / 60, corner, t.Cpu); // the player drags straight into the corner
            Assert.True((t.Me - t.Puck).Length >= Touch - 1, $"frame {frame}: the mallet slid over the puck");
        }
    }

    [Fact]
    public void APuckIntoTheRightGoalScoresForTheLeftPlayer()
    {
        var t = new HockeyTable(Arena, new Random(3));
        bool? scored = null;
        t.Goal += left => scored = left;
        t.Puck = new Vec2(Arena.Right - 200, Arena.Center.Y);
        t.PuckVel = new Vec2(2000, 0);
        t.Cpu = new Vec2(Arena.Right - 100, Arena.Top + 100); // out of the way
        for (int frame = 0; frame < 60 && scored == null; frame++) t.Advance(1.0 / 60, t.Me, t.Cpu);
        Assert.True(scored);
        Assert.False(t.PuckInPlay);
    }
}

[Collection("lan")]
public class LanGameFollowTests
{
    static bool WaitFor(Func<bool> condition, int ms = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            System.Threading.Thread.Sleep(20);
        }
        return condition();
    }

    [Fact]
    public void AGuestJoinsIntoTheGameTheHostSwitchedToWhileWaitingAndFollowsLaterSwitchesOnce()
    {
        using var host = new LanLink();
        using var guest = new LanLink();
        host.Host("hockey");
        host.SendGame("chess"); // switched games before anyone joined

        var switches = new List<string>();
        guest.GameChanged += id => { lock (switches) switches.Add(id); };
        guest.Join(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, LanLink.Port));
        Assert.True(WaitFor(() => host.Connected && guest.Connected), "the two links never paired");
        Assert.Equal("chess", guest.GameId);

        host.SendGame("bubbles");
        Assert.True(WaitFor(() => { lock (switches) return switches.Contains("bubbles"); }));
        System.Threading.Thread.Sleep(1600); // the host repeats its game every second
        lock (switches) Assert.Equal(new[] { "bubbles" }, switches);
        Assert.Equal("bubbles", guest.GameId);
    }

    [Fact]
    public void RivalActionsArriveAsFractionsOfTheArena()
    {
        using var host = new LanLink();
        using var guest = new LanLink();
        host.Host("whack");
        guest.Join(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, LanLink.Port));
        Assert.True(WaitFor(() => host.Connected && guest.Connected));

        (double X, double Y, int Pts)? got = null;
        host.ActionReceived += (x, y, p) => got = (x, y, p);
        guest.SendAction(0.25, 0.75, -5);
        Assert.True(WaitFor(() => got != null));
        Assert.Equal((0.25, 0.75, -5), got!.Value);
        Assert.False(host.TryReceive(out _)); // actions never reach the game's queue
    }
}

public class DuelChannelTests
{
    /// <summary>Two channels joined by a link that drops messages at random.</summary>
    sealed class LossyPair
    {
        readonly Random _rng;
        readonly double _loss;
        readonly Queue<string> _toA = new(), _toB = new();
        public readonly DuelChannel A, B;
        public readonly List<string> GotA = new(), GotB = new();

        public LossyPair(int seed, double loss)
        {
            _rng = new Random(seed);
            _loss = loss;
            A = new DuelChannel("t", m => { if (_rng.NextDouble() >= _loss) _toB.Enqueue(m); });
            B = new DuelChannel("t", m => { if (_rng.NextDouble() >= _loss) _toA.Enqueue(m); });
        }

        public void Run(double seconds)
        {
            for (double t = 0; t < seconds; t += 0.05)
            {
                A.Tick(0.05);
                B.Tick(0.05);
                while (_toB.Count > 0) Assert.True(B.Handle(_toB.Dequeue(), GotB));
                while (_toA.Count > 0) Assert.True(A.Handle(_toA.Dequeue(), GotA));
            }
        }
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.3)]
    [InlineData(0.6)]
    public void EventsArriveOnceAndInOrderOverALossyLink(double loss)
    {
        var pair = new LossyPair(42, loss);
        var sentA = Enumerable.Range(1, 20).Select(i => $"st|{i}|a|b").ToList();
        var sentB = Enumerable.Range(1, 15).Select(i => $"ar|{i}").ToList();
        foreach (var e in sentA) pair.A.Send(e);
        foreach (var e in sentB) pair.B.Send(e);
        pair.Run(60);
        Assert.Equal(sentA, pair.GotB);
        Assert.Equal(sentB, pair.GotA);
        Assert.Equal(0, pair.A.Pending);
        Assert.Equal(0, pair.B.Pending);
    }

    [Fact]
    public void OtherMessagesAreLeftAlone()
    {
        var channel = new DuelChannel("g", _ => { });
        var got = new List<string>();
        Assert.False(channel.Handle("s|0.5|0.5|1", got));
        Assert.False(channel.Handle("ae|1|x", got)); // another game's prefix
        Assert.Empty(got);
    }
}

public class DuelMatchTests
{
    [Fact]
    public void GolfPlayersAlternateStrokesAndTheOneWhoIsInWaits()
    {
        var host = new GolfMatch(iStartOddHoles: true);
        var guest = new GolfMatch(iStartOddHoles: false);
        void Stroke(bool byHost, int strokes, bool sunk, int par)
        {
            host.Stroke(byHost, strokes, sunk, par);
            guest.Stroke(!byHost, strokes, sunk, par);
        }

        Assert.True(host.MyTurn);
        Assert.False(guest.MyTurn);
        Stroke(byHost: true, 1, false, 3);
        Assert.False(host.MyTurn);
        Assert.True(guest.MyTurn);
        Stroke(byHost: false, 1, true, 2); // guest holes in one on a par 2
        Assert.True(host.MyTurn);
        Assert.False(guest.MyTurn);       // the guest is in and waits
        Stroke(byHost: true, 2, false, 3);
        Assert.True(host.MyTurn);          // the guest is in, so the host keeps putting
        Stroke(byHost: true, 3, true, 3);  // par for the host, −1 for the guest

        Assert.Equal(2, host.Hole);
        Assert.Equal((0, 1), (host.MyHoles, host.TheirHoles));
        Assert.Equal((1, 0), (guest.MyHoles, guest.TheirHoles));
        Assert.False(host.MyTurn);         // the guest starts the even holes
        Assert.True(guest.MyTurn);
    }

    [Fact]
    public void GolfMatchEndsAfterNineHolesAndHolesWonDecideIt()
    {
        var m = new GolfMatch(iStartOddHoles: true);
        for (int hole = 1; hole <= GolfMatch.Holes; hole++)
        {
            bool iWin = hole <= 5;
            m.Stroke(true, iWin ? 2 : 4, true, 3);
            m.Stroke(false, iWin ? 4 : 2, true, 3);
        }
        Assert.True(m.Over);
        Assert.Equal((5, 4), (m.MyHoles, m.TheirHoles));
        Assert.True(m.Won);
        Assert.False(m.MyTurn);
    }

    [Fact]
    public void ArchersAlternateAndTheHigherTotalWins()
    {
        var m = new ArcheryMatch(iShootFirst: false);
        Assert.False(m.MyTurn);
        for (int i = 0; i < ArcheryMatch.Arrows; i++)
        {
            m.Arrow(byMe: false, 6);
            Assert.True(m.MyTurn);
            m.Arrow(byMe: true, 8);
        }
        Assert.True(m.Over);
        Assert.Equal((80, 60), (m.MyScore, m.TheirScore));
        Assert.True(m.Won);
        m.Arrow(byMe: true, 10); // no arrows left: ignored
        Assert.Equal(80, m.MyScore);
    }

    [Fact]
    public void AnArcherWithArrowsLeftKeepsShootingOnceTheOtherIsOut()
    {
        var m = new ArcheryMatch(iShootFirst: true);
        for (int i = 0; i < ArcheryMatch.Arrows; i++) m.Arrow(byMe: false, 0); // out of turn, but counted
        Assert.True(m.MyTurn);
        m.Arrow(byMe: true, 2);
        Assert.True(m.MyTurn);
    }
}

public class ThemeTests
{
    [Fact]
    public void SeasonalFollowsTheCalendarAndUnknownIdsFallBackToClassic()
    {
        Assert.Equal(Themes.Halloween, Themes.Resolve("seasonal", new DateTime(2026, 10, 20)));
        Assert.Equal(Themes.Winter, Themes.Resolve("seasonal", new DateTime(2026, 12, 24)));
        Assert.Equal(Themes.Winter, Themes.Resolve("seasonal", new DateTime(2027, 1, 5)));
        Assert.Equal(Themes.Classic, Themes.Resolve("seasonal", new DateTime(2026, 6, 1)));
        Assert.Equal(Themes.Neon, Themes.Resolve("neon", DateTime.Today));
        Assert.Equal(Themes.Classic, Themes.Resolve("no-such-theme", DateTime.Today));
        Assert.Equal(Themes.All.Count + 1, Themes.Choices.Count());
    }
}

public class PongTableTests
{
    static readonly Rect Arena = new(0, 0, 1920, 1040);

    [Fact]
    public void APaddleInTheWayReturnsTheBallAndAMissScoresForTheOtherSide()
    {
        var t = new PongTable(Arena, new Random(1));
        bool? point = null;
        t.Point += left => point = left;
        t.Ball = new Vec2(400, 500);
        t.BallVel = new Vec2(-900, 0);
        t.BallInPlay = true;
        for (int i = 0; i < 60; i++) t.Advance(1.0 / 60, 500, t.ThemY); // my paddle sits right on the ball's path
        Assert.Null(point);
        Assert.True(t.BallVel.X > 0, "the ball wasn't returned");
        Assert.Equal(1, t.Rally);

        t.Ball = new Vec2(400, 500);
        t.BallVel = new Vec2(-900, 0);
        for (int i = 0; i < 60 && point == null; i++) t.Advance(1.0 / 60, Arena.Bottom, t.ThemY); // paddle out of the way
        Assert.False(point); // the right-hand player scored
        Assert.False(t.BallInPlay);
    }

    [Fact]
    public void PredictionMatchesWhereTheBallReallyArrivesAfterWallBounces()
    {
        var t = new PongTable(Arena, new Random(2));
        t.Ball = new Vec2(Arena.Center.X, 200);
        t.BallVel = new Vec2(700, -900); // steep: bounces off the top, then the bottom
        t.BallInPlay = true;
        t.ThemY = Arena.Top + PongTable.PaddleH / 2; // keep the paddle out of the way
        double x = t.ThemX - PongTable.PaddleW / 2 - PongTable.BallR;
        double predicted = t.PredictY(x);
        while (t.Ball.X < x) t.Advance(PongTable.Step, t.MeY, t.ThemY);
        Assert.InRange(t.Ball.Y, predicted - 12, predicted + 12);
    }

    [Fact]
    public void AStrongCpuReturnsMostShotsAndAWeakOneMissesSome()
    {
        int Returns(int level)
        {
            var t = new PongTable(Arena, new Random(level)) { Level = level };
            int returned = 0;
            for (int shot = 0; shot < 40; shot++)
            {
                t.Serve(1);
                bool? point = null;
                void OnPoint(bool left) => point = left;
                t.Point += OnPoint;
                for (int i = 0; i < 400 && point == null && t.BallVel.X > 0; i++) t.Advance(1.0 / 60, t.MeY, t.CpuMove(1.0 / 60));
                t.Point -= OnPoint;
                if (point == null && t.BallVel.X < 0) returned++;
                t.BallInPlay = false;
            }
            return returned;
        }
        Assert.True(Returns(10) >= 36, "a level-10 CPU should return nearly every serve");
        Assert.True(Returns(1) < Returns(10), "a level-1 CPU should miss more than a level-10 one");
    }
}

public class OfficeBoardTests
{
    [Fact]
    public void TodaysCountersStartOverOnANewDay()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"da-stats-{Guid.NewGuid():N}.json");
        var stats = Stats.Load(path);
        var day = new DateTime(2026, 9, 18, 10, 0, 0);
        stats.Clock = () => day;
        stats.Add("hoops.baskets", 3);
        stats.Max("hoops.streak", 4);
        stats.Max("hoops.streak", 2);
        stats.AddTime("hoops", 90);
        Assert.Equal(3, stats.Today("hoops.baskets"));
        Assert.Equal(4, stats.Today("hoops.streak"));
        Assert.Equal(90_000, stats.Today("play.ms"));

        day = day.AddDays(1);
        Assert.Equal(0, stats.Today("hoops.baskets"));
        stats.Max("hoops.streak", 2);
        Assert.Equal(2, stats.Today("hoops.streak"));
        Assert.Equal(3, stats.Get("hoops.baskets")); // the all-time counters keep going
        Assert.Equal(4, stats.Get("hoops.streak"));
    }

    [Fact]
    public void EntriesSurviveTheWireAndHostileTextIsRejected()
    {
        var scores = OfficeBoard.Scores(c => c switch { "hockey.wins" => 2, "hockey.lanwins" => 1, "play.ms" => 185_000, "hoops.streak" => 7, _ => 0 });
        Assert.Equal(3, scores["hockey"]);
        Assert.Equal(3, scores["minutes"]);
        var entry = new BoardEntry("a1b2", "al|ice,=", "2026-09-18", scores);

        var back = OfficeBoard.Decode(OfficeBoard.Encode(entry));
        Assert.NotNull(back);
        Assert.Equal("alice", back!.Name); // separators are stripped from names
        Assert.Equal("a1b2", back.Id);
        Assert.Equal("2026-09-18", back.Day);
        Assert.Equal(7, back.Scores["streak"]);
        Assert.False(back.Scores.ContainsKey("pong")); // zeros aren't sent

        Assert.Null(OfficeBoard.Decode("DA1|lb|x1|bob|not-a-date|streak=3"));
        Assert.Null(OfficeBoard.Decode("XX1|lb|x1|bob|2026-09-18|streak=3"));
        Assert.Null(OfficeBoard.Decode("DA1|lb|x1||2026-09-18|streak=3"));
        Assert.Null(OfficeBoard.Decode("DA1|lb|bob|2026-09-18|streak=3")); // no id
        var odd = OfficeBoard.Decode("DA1|lb|x1|bob|2026-09-18|streak=-4,baskets=abc,tower=99999999999999")!;
        Assert.False(odd.Scores.ContainsKey("streak"));
        Assert.False(odd.Scores.ContainsKey("baskets"));
        Assert.Equal(1_000_000_000, odd.Scores["tower"]);
    }

    [Fact]
    public void RankingPutsTheBestFirstAndLeavesOutZeros()
    {
        var entries = new[]
        {
            new BoardEntry("c", "carol", "d", new Dictionary<string, long> { ["streak"] = 5 }),
            new BoardEntry("a", "alice", "d", new Dictionary<string, long> { ["streak"] = 9 }),
            new BoardEntry("b", "bob", "d", new Dictionary<string, long> { ["streak"] = 5 }),
            new BoardEntry("d", "dave", "d", new Dictionary<string, long>()),
        };
        Assert.Equal(new[] { ("a", "alice", 9L), ("b", "bob", 5L), ("c", "carol", 5L) }, OfficeBoard.Rank(entries, "streak"));
    }
}
