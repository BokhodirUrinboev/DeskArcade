using System;
using System.Collections.Generic;
using System.Linq;
using DeskArcade.Games;
using DeskArcade.Net;
using Xunit;

namespace DeskArcade.Tests;

/// <summary>Pet mail: the message format, what a receiver refuses, the sender's rate limit, the queue and the resending.</summary>
public class PetMailTests
{
    /// <summary>Two mailboxes wired to each other through lists, so a test decides what gets delivered (and what is lost).</summary>
    sealed class Pair
    {
        public readonly List<string> ToB = new(), ToA = new();
        public readonly PetMail A, B;

        public Pair()
        {
            A = new PetMail(ToB.Add, firstId: 100);
            B = new PetMail(ToA.Add, firstId: 500);
        }

        public List<PetMailEvent> DeliverToB(double now) => Drain(ToB, B, "anna", now);
        public List<PetMailEvent> DeliverToA(double now) => Drain(ToA, A, "boris", now);

        static List<PetMailEvent> Drain(List<string> wire, PetMail to, string from, double now)
        {
            var copy = wire.ToList();
            wire.Clear();
            return copy.Select(m => to.Handle(m, from, now)).Where(e => e.Kind != PetMailEventKind.None).ToList();
        }
    }

    [Theory]
    [InlineData(PetMailKind.Parcel, 1, PetGift.Treat, "pm|gift|1|treat")]
    [InlineData(PetMailKind.Parcel, 42, PetGift.Ball, "pm|gift|42|ball")]
    [InlineData(PetMailKind.Parcel, 999999999, PetGift.Yarn, "pm|gift|999999999|yarn")]
    [InlineData(PetMailKind.Parcel, 7, PetGift.Bone, "pm|gift|7|bone")]
    [InlineData(PetMailKind.Got, 7, PetGift.Treat, "pm|got|7")]
    [InlineData(PetMailKind.Opened, 7, PetGift.Treat, "pm|open|7")]
    [InlineData(PetMailKind.Thanks, 7, PetGift.Treat, "pm|thx|7")]
    public void MessagesRoundTrip(PetMailKind kind, int id, PetGift gift, string wire)
    {
        var m = new PetMailMessage(kind, id, gift);
        Assert.Equal(wire, PetMail.Encode(m));
        Assert.True(PetMail.TryDecode(wire, out var back));
        Assert.Equal(m, back);
    }

    [Fact]
    public void EveryGiftRoundTrips()
    {
        foreach (var gift in Enum.GetValues<PetGift>())
        {
            Assert.True(PetMail.TryDecode(PetMail.Encode(new PetMailMessage(PetMailKind.Parcel, 3, gift)), out var m));
            Assert.Equal(gift, m.Gift);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("pm")]
    [InlineData("pm|gift")]
    [InlineData("pm|gift|5")]                    // a parcel without its gift
    [InlineData("pm|gift|5|cake")]               // no such gift
    [InlineData("pm|gift|5|Treat")]              // gift names are lower-case
    [InlineData("pm|gift|5|treat|extra")]        // a stray field
    [InlineData("pm|got|5|treat")]               // only a parcel carries a gift
    [InlineData("pm|gift|0|treat")]              // ids start at 1
    [InlineData("pm|gift|-3|treat")]
    [InlineData("pm|gift|+3|treat")]
    [InlineData("pm|gift|007|treat")]            // no leading zeros: one id, one spelling
    [InlineData("pm|gift|1000000000|treat")]     // too long
    [InlineData("pm|gift|99999999999999999999|treat")]
    [InlineData("pm|gift|1.5|treat")]
    [InlineData("pm|gift| 5|treat")]
    [InlineData("pm|hug|5")]                     // no such message
    [InlineData("pt|cat|0.5|0.5|1|sit|")]        // a pet visit, not mail
    [InlineData("pm|gift|5|treattreattreattreattreattreattreattreattreattreattreattreat")]
    public void MalformedMessagesAreRejected(string? message)
    {
        Assert.False(PetMail.TryDecode(message, out _));
        var mail = new PetMail(_ => { }, firstId: 1);
        Assert.Equal(PetMailEventKind.None, mail.Handle(message ?? "", "anna", 0).Kind);
        Assert.Equal(0, mail.Waiting);
    }

    [Fact]
    public void AParcelArrivesIsOpenedAndTheSenderHearsItWasLoved()
    {
        var p = new Pair();
        Assert.Equal(PetMailSend.Sent, p.A.Send(PetGift.Yarn, 0, out _));
        Assert.True(p.A.Busy);

        var got = p.DeliverToB(0.1);
        Assert.Equal(new[] { new PetMailEvent(PetMailEventKind.Parcel, 100, PetGift.Yarn) }, got);
        Assert.Equal(1, p.B.Waiting);
        Assert.True(p.B.TryTake(out var parcel));
        Assert.Equal(new IncomingParcel(100, PetGift.Yarn, "anna"), parcel);

        // the receipt stops the resending
        Assert.Equal(new[] { new PetMailEvent(PetMailEventKind.Delivered, 100, PetGift.Yarn) }, p.DeliverToA(0.2));
        Assert.False(p.A.Busy);
        p.A.Tick(5);
        Assert.Empty(p.ToB);

        // the pet opens it: the sender learns which gift was loved, and the receiver stops telling once thanked
        p.B.Opened(parcel.Id, 3);
        Assert.True(p.B.Busy);
        Assert.Equal(new[] { new PetMailEvent(PetMailEventKind.Loved, 100, PetGift.Yarn) }, p.DeliverToA(3.1));
        Assert.Empty(p.DeliverToB(3.2));
        Assert.False(p.B.Busy);
    }

    [Fact]
    public void LostMessagesAreSentAgainAndRepeatsCountOnce()
    {
        var p = new Pair();
        p.A.Send(PetGift.Treat, 0, out _);
        p.ToB.Clear(); // lost
        p.A.Tick(0.2);
        Assert.Empty(p.ToB); // not yet
        p.A.Tick(PetMail.ResendSeconds + 0.01);
        Assert.Single(p.ToB);
        p.A.Tick(2 * PetMail.ResendSeconds + 0.02);
        Assert.Equal(2, p.ToB.Count); // two copies arrive

        var events = p.DeliverToB(1.5);
        Assert.Single(events);             // but the pet gets one parcel
        Assert.Equal(1, p.B.Waiting);
        Assert.Equal(2, p.ToA.Count);      // and both copies are acknowledged
        Assert.Single(p.DeliverToA(1.6));  // "delivered" once
        Assert.False(p.A.Busy);
    }

    [Fact]
    public void SendingGivesUpOnASilentPeer()
    {
        var p = new Pair();
        p.A.Send(PetGift.Ball, 0, out _);
        p.A.Tick(PetMail.GiveUpSeconds - 1);
        Assert.True(p.A.Busy);
        p.A.Tick(PetMail.GiveUpSeconds + 0.1);
        Assert.False(p.A.Busy);
    }

    [Fact]
    public void OneParcelEveryThirtySeconds()
    {
        var sent = new List<string>();
        var mail = new PetMail(sent.Add, firstId: 1);
        Assert.Equal(PetMailSend.Sent, mail.Send(PetGift.Treat, 10, out _));
        Assert.Equal(PetMailSend.TooSoon, mail.Send(PetGift.Ball, 25, out double wait));
        Assert.Equal(PetMail.SendEvery - 15, wait, 6);
        Assert.Equal(PetMailSend.TooSoon, mail.Send(PetGift.Ball, 10 + PetMail.SendEvery - 0.01, out _));
        Assert.Single(sent); // refused sends put nothing on the wire
        Assert.Equal(PetMailSend.Sent, mail.Send(PetGift.Ball, 10 + PetMail.SendEvery, out _));
        Assert.Equal(new[] { "pm|gift|1|treat", "pm|gift|2|ball" }, sent);
    }

    [Fact]
    public void AtMostAFewParcelsOnTheWay()
    {
        var mail = new PetMail(_ => { }, firstId: 1);
        double t = 0;
        for (int i = 0; i < PetMail.MaxInFlight; i++, t += PetMail.SendEvery)
            Assert.Equal(PetMailSend.Sent, mail.Send(PetGift.Treat, t, out _));
        Assert.Equal(PetMail.MaxInFlight, mail.InFlight);
        Assert.Equal(PetMailSend.TooMany, mail.Send(PetGift.Treat, t, out _));
        // one arrives: room for another
        mail.Handle("pm|got|1", "anna", t);
        Assert.Equal(PetMailSend.Sent, mail.Send(PetGift.Treat, t, out _));
    }

    [Fact]
    public void TheReceiverKeepsAFewAndIgnoresAFlood()
    {
        var acks = new List<string>();
        var mail = new PetMail(acks.Add, firstId: 1);
        double t = 0;
        for (int id = 1; id <= PetMail.MaxWaiting; id++, t += PetMail.ReceiveEvery)
            Assert.Equal(PetMailEventKind.Parcel, mail.Handle($"pm|gift|{id}|treat", "anna", t).Kind);
        Assert.Equal(PetMail.MaxWaiting, mail.Waiting);
        Assert.Equal(PetMailEventKind.None, mail.Handle("pm|gift|90|ball", "anna", t + 100).Kind); // the queue is full
        Assert.True(mail.TryTake(out var first));
        Assert.Equal(1, first.Id); // oldest first
        // a parcel right on the heels of the last one is dropped, even with room
        Assert.Equal(PetMailEventKind.Parcel, mail.Handle("pm|gift|91|ball", "anna", t + 200).Kind);
        Assert.True(mail.TryTake(out _));
        Assert.Equal(PetMailEventKind.None, mail.Handle("pm|gift|92|ball", "anna", t + 201).Kind);
        // every parcel is acknowledged, kept or not, so an honest sender stops resending
        Assert.Contains("pm|got|90", acks);
        Assert.Contains("pm|got|92", acks);
    }

    [Fact]
    public void LovedOnlyForParcelsWeSent()
    {
        var sent = new List<string>();
        var mail = new PetMail(sent.Add, firstId: 10);
        Assert.Equal(PetMailEventKind.None, mail.Handle("pm|open|10", "anna", 0).Kind); // nothing sent yet
        mail.Send(PetGift.Bone, 1, out _);
        Assert.Equal(PetMailEventKind.None, mail.Handle("pm|open|11", "anna", 2).Kind);
        Assert.Equal(new PetMailEvent(PetMailEventKind.Loved, 10, PetGift.Bone), mail.Handle("pm|open|10", "anna", 2));
        Assert.Equal(PetMailEventKind.None, mail.Handle("pm|open|10", "anna", 2.5).Kind); // a repeat
        Assert.Equal(PetMailEventKind.None, mail.Handle("pm|got|77", "anna", 3).Kind);   // a receipt for nothing
        Assert.Contains("pm|thx|10", sent);
    }

    [Fact]
    public void ANewSessionForgetsTheWireButKeepsTheParcels()
    {
        var mail = new PetMail(_ => { }, firstId: 1);
        mail.Send(PetGift.Treat, 0, out _);
        mail.Handle("pm|gift|5|yarn", "anna", 0);
        mail.ResetLink();
        Assert.False(mail.Busy);
        Assert.Equal(1, mail.Waiting); // it arrived: the pet still gets it
        Assert.Equal(PetMailSend.TooSoon, mail.Send(PetGift.Treat, 1, out _)); // and reconnecting is no way round the limit
        Assert.Equal(PetMailEventKind.Parcel, mail.Handle("pm|gift|5|yarn", "boris", 50).Kind); // a new peer may reuse an id
    }

    [Fact]
    public void TheSendersNameIsTidiedForTheTag()
    {
        var mail = new PetMail(_ => { }, firstId: 1);
        mail.Handle("pm|gift|1|treat", "  a\u0007very-long-name-that-goes-on-and-on  ", 0);
        mail.Handle("pm|gift|2|treat", "", 20);
        Assert.True(mail.TryTake(out var one));
        Assert.True(mail.TryTake(out var two));
        Assert.DoesNotContain('\u0007', one.From);
        Assert.True(one.From.Length <= 24);
        Assert.Equal("?", two.From);
    }

    [Fact]
    public void EveryAnimalPlaysWithEveryToyUsingMovesItHas()
    {
        var tricks = PetGame.Kinds.Select(k => PetGame.TrickFor(k).Name).ToHashSet();
        foreach (var kind in PetGame.Kinds)
        {
            foreach (var gift in new[] { PetGift.Yarn, PetGift.Bone })
            {
                var acts = Enumerable.Range(0, 4).Select(turn => PetGame.ToyActOf(kind, gift, turn)).ToList();
                Assert.All(acts, a => Assert.True(PetGame.ActLength(a) > 0, $"{kind} has no '{a}'"));
                Assert.All(acts, a => Assert.DoesNotContain(a, tricks)); // a trick may leap about; play stays put
                Assert.True(acts.Distinct().Count() > 1, $"{kind} plays with the {gift} the same way every time");
            }
            Assert.NotEqual(PetGame.ToyActOf(kind, PetGift.Yarn, 0), PetGame.ToyActOf(kind, PetGift.Bone, 0));
        }
    }

    [Fact]
    public void GiftAchievementsCountSendingAndReceiving()
    {
        var counters = Achievements.All.Where(a => a.GameId == "pet").Select(a => a.Counter).ToList();
        Assert.Contains("pet.giftssent", counters);
        Assert.Contains("pet.parcels", counters);
    }
}
