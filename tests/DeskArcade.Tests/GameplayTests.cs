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

public class DurakRulesTests
{
    static int C(int suit, int rank) => DurakRules.Card(suit, rank);

    /// <summary>Plays computer turns for every seat until the game ends; returns the number of actions.</summary>
    static int PlayOut(DurakRules g, int maxActions = 5000)
    {
        int actions = 0;
        while (!g.Over && actions < maxActions)
        {
            bool acted = false;
            // the defender answers first, then attackers in seat order, like a real table
            foreach (int seat in new[] { g.Defender }.Concat(Enumerable.Range(0, g.Players)))
            {
                if (g.CpuAction(seat) is not { } a) continue;
                Assert.True(g.Act(seat, a.Kind, a.Card, a.Index), $"CPU action {a.Kind} by seat {seat} was refused");
                acted = true;
                actions++;
                break;
            }
            Assert.True(acted, "nobody could act but the game isn't over");
            int cards = g.Hands.Sum(h => h.Count) + g.Deck.Count + g.Table.Sum(p => p.Beaten ? 2 : 1) + g.Discarded;
            Assert.Equal(DurakRules.DeckSize, cards);
        }
        return actions;
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    public void ComputerPlayersFinishAGameWithEveryCardAccountedFor(int players)
    {
        for (int seed = 0; seed < 25; seed++)
        {
            var g = new DurakRules(players, new Random(seed));
            Assert.All(g.Hands, h => Assert.Equal(DurakRules.HandSize, h.Count));
            Assert.Equal(DurakRules.DeckSize - players * DurakRules.HandSize, g.Deck.Count);
            PlayOut(g);
            Assert.True(g.Over, $"seed {seed} didn't finish");
            Assert.True(g.Durak == -1 || g.Hands[g.Durak].Count > 0);
        }
    }

    [Fact]
    public void TheLowestTrumpLeadsAndTheNextPlayerDefends()
    {
        var g = new DurakRules(4, new Random(7));
        int lowest = Enumerable.Range(0, 4)
            .SelectMany(p => g.Hands[p].Where(c => DurakRules.Suit(c) == g.TrumpSuit).Select(c => (p, r: DurakRules.Rank(c))))
            .OrderBy(t => t.r).Select(t => t.p).DefaultIfEmpty(0).First();
        Assert.Equal(lowest, g.Attacker);
        Assert.Equal((lowest + 1) % 4, g.Defender);
        Assert.Equal(5, g.Limit); // the first bout takes at most five cards
    }

    [Fact]
    public void CardsBeatByHigherSameSuitOrByTrump()
    {
        var g = new DurakRules(2, new Random(1));
        int t = g.TrumpSuit, plain = (t + 1) % 4;
        Assert.True(g.Beats(C(plain, 10), C(plain, 7)));
        Assert.False(g.Beats(C(plain, 7), C(plain, 10)));
        Assert.True(g.Beats(C(t, 6), C(plain, 14)));      // any trump beats a plain card
        Assert.False(g.Beats(C(plain, 14), C(t, 6)));     // no plain card beats a trump
        Assert.True(g.Beats(C(t, 9), C(t, 7)));
        Assert.False(g.Beats(C((t + 2) % 4, 14), C(plain, 6))); // another plain suit never beats
    }

    [Fact]
    public void OnlyRanksOnTheTableMayBeThrownInAndOnlyTheAttackerLeads()
    {
        var g = new DurakRules(3, new Random(3));
        int a = g.Attacker, d = g.Defender, other = 3 - a - d;
        Assert.DoesNotContain(g.Hands[other], c => g.CanAttack(other, c)); // nobody but the attacker opens
        Assert.DoesNotContain(g.Hands[d], c => g.CanAttack(d, c));         // the defender never attacks
        int lead = g.Hands[a][0];
        Assert.True(g.Attack(a, lead));
        foreach (int c in g.Hands[other])
            Assert.Equal(DurakRules.Rank(c) == DurakRules.Rank(lead), g.CanAttack(other, c));
    }

    [Fact]
    public void TakingGivesTheDefenderTheTableAndSkipsTheirAttack()
    {
        var g = new DurakRules(3, new Random(5));
        int a = g.Attacker, d = g.Defender;
        int before = g.Hands[d].Count;
        Assert.True(g.Attack(a, g.Hands[a][0]));
        Assert.True(g.Take(d));
        foreach (int s in Enumerable.Range(0, 3).Where(s => s != d)) g.Pass(s);
        Assert.True(g.Hands[d].Count >= before + 1);
        Assert.Empty(g.Table);
        Assert.Equal((d + 1) % 3, g.Attacker); // the defender who took doesn't attack next
    }

    [Fact]
    public void DoneOnlyCountsOnceTheDefenderHasAnsweredAndANewRankReopensThrowingIn()
    {
        for (int seed = 0; seed < 200; seed++)
        {
            var g = new DurakRules(2, new Random(seed));
            int a = g.Attacker, d = g.Defender;
            int lead = g.Hands[a][0];
            g.Attack(a, lead);
            Assert.False(g.Pass(a)); // the card is still unbeaten: "done" means nothing yet
            int answer = g.Hands[d].FirstOrDefault(c => g.Beats(c, lead), -1);
            if (answer < 0 || !g.Hands[a].Any(c => DurakRules.Rank(c) == DurakRules.Rank(answer))) continue;
            Assert.True(g.Defend(d, 0, answer));
            Assert.Single(g.Table); // the bout waits: the attacker holds the defense card's rank
            Assert.Contains(g.Hands[a], c => g.CanAttack(a, c) && DurakRules.Rank(c) == DurakRules.Rank(answer));
            return;
        }
        Assert.Fail("no seed gave the attacker a card of the defense's rank");
    }

    [Fact]
    public void ABeatenBoutGoesToTheDiscardsAndTheDefenderAttacksNext()
    {
        for (int seed = 0; seed < 200; seed++)
        {
            var g = new DurakRules(2, new Random(seed));
            int a = g.Attacker, d = g.Defender;
            int lead = g.Hands[a].OrderBy(DurakRules.Rank).First(c => DurakRules.Suit(c) != g.TrumpSuit || true);
            g.Attack(a, lead);
            int answer = g.Hands[d].FirstOrDefault(c => g.Beats(c, lead), -1);
            if (answer < 0) continue;
            Assert.True(g.Defend(d, 0, answer));
            Assert.True(g.Pass(a));
            Assert.Equal(2, g.Discarded);
            Assert.Equal(d, g.Attacker);
            Assert.All(g.Hands, h => Assert.Equal(DurakRules.HandSize, h.Count)); // both drew back up to six
            return;
        }
        Assert.Fail("no seed let the defender beat the first card");
    }
}

[Collection("room")]
public class RoomLinkTests
{
    static bool WaitFor(Func<bool> condition, int ms = 6000)
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
    public async System.Threading.Tasks.Task ThreeGuestsJoinByCodeAFourthIsTurnedAwayAndMessagesFlowBothWays()
    {
        using var host = new RoomLink { MyName = "host" };
        host.Host("TEST");
        Assert.Equal(RoomState.Hosting, host.State);

        var found = await RoomLink.FindRooms(TimeSpan.FromSeconds(0.8));
        var room = Assert.Single(found, r => r.Code == "TEST");
        Assert.True(room.Open);
        Assert.Equal(1, room.Players);

        var guests = Enumerable.Range(1, 3).Select(i => new RoomLink { MyName = $"guest{i}" }).ToList();
        try
        {
            foreach (var g in guests)
            {
                g.Join("test"); // codes are case-insensitive
                Assert.True(WaitFor(() => g.State == RoomState.Joined), $"{g.MyName} never joined: {g.State} {g.Refusal}; host seats {string.Join(",", host.Seats().Select(s => s.Seat + ":" + s.Name))}");
            }
            Assert.Equal(new[] { 0, 1, 2, 3 }, host.Seats().Select(s => s.Seat));
            Assert.True(WaitFor(() => guests[0].Seats().Count == 4), "the roster didn't reach the guests");
            Assert.Equal("guest3", guests[0].Seats()[3].Name);

            using var extra = new RoomLink { MyName = "late" };
            extra.Join("TEST");
            Assert.True(WaitFor(() => extra.State == RoomState.Lost));
            Assert.Equal("full", extra.Refusal);

            guests[1].SendToHost("da|1|attack|5|-1");
            (int Seat, string Body) got = default;
            Assert.True(WaitFor(() => host.TryReceive(out got)));
            Assert.Equal((2, "da|1|attack|5|-1"), got);

            host.SendTo(3, "ds|{\"x\":1}|more");
            Assert.True(WaitFor(() => guests[2].TryReceive(out got)));
            Assert.Equal((0, "ds|{\"x\":1}|more"), got);
            Assert.False(guests[0].TryReceive(out _)); // only the addressed guest gets it

            guests[0].Stop();
            Assert.True(WaitFor(() => host.Seats().All(s => s.Seat != 1)), "the host didn't notice the guest leave");
        }
        finally
        {
            foreach (var g in guests) g.Dispose();
        }
    }

    [Fact]
    public void AWrongCodeFindsNoRoom()
    {
        using var host = new RoomLink();
        host.Host("ABCD");
        using var guest = new RoomLink();
        guest.Join("ZZZZ");
        Assert.True(WaitFor(() => guest.State == RoomState.Lost, 12000));
        Assert.Equal("notfound", guest.Refusal);
    }

    [Fact]
    public async System.Threading.Tasks.Task EachRoomPlaysOneGame()
    {
        using var durak = new RoomLink();
        durak.Host("DURK");
        using var lastCard = new RoomLink { Game = "lastcard" };
        lastCard.Host("LAST");

        // the list names each room's game; a Durak room still answers without one, as older copies expect
        var found = await RoomLink.FindRooms(TimeSpan.FromSeconds(0.8));
        Assert.Equal(RoomLink.Durak, Assert.Single(found, r => r.Code == "DURK").Game);
        Assert.Equal("lastcard", Assert.Single(found, r => r.Code == "LAST").Game);

        using var wrong = new RoomLink(); // a Durak player typing the Last Card room's code
        wrong.Join("LAST");
        Assert.True(WaitFor(() => wrong.State == RoomState.Lost));
        Assert.Equal("game", wrong.Refusal);

        using var right = new RoomLink { Game = "lastcard" };
        right.Join("LAST");
        Assert.True(WaitFor(() => right.State == RoomState.Joined));
    }
}

public class PetVoiceTests
{
    [Fact]
    public void EveryPetClipIsSynthesizedAudibleAndUnclipped()
    {
        using var sound = new Sound(new DeskArcade.Platform.NullPlatform());
        foreach (var name in PetGame.ClipsUsed().Distinct())
        {
            var clip = sound.Samples(name);
            Assert.True(clip != null, $"no clip named {name}");
            Assert.True(clip!.Length > 1000, $"{name} is too short");
            Assert.All(clip, x => Assert.True(float.IsFinite(x), $"{name} has a bad sample"));
            float peak = clip.Max(Math.Abs);
            Assert.InRange(peak, 0.2f, 0.8f);
        }
    }

    [Fact]
    public void EveryHabitHasALength()
    {
        foreach (var kind in PetGame.Kinds)
            foreach (var habit in PetGame.HabitsOf(kind))
                Assert.True(PetGame.ActLength(habit) > 0, $"{kind}: {habit}");
    }
}
