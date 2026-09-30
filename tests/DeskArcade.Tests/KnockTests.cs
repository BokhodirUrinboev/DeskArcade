using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DeskArcade.Net;
using DeskArcade.Office;
using Xunit;

namespace DeskArcade.Tests;

/// <summary>Knock first: the presence and knock messages, the co-workers heard, the queue that waits for the break.</summary>
public class KnockMessageTests
{
    static readonly DateTime T0 = new(2026, 10, 14, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void PresenceKnocksAndAnswersRoundTrip()
    {
        var presence = new Presence("p1", "me01", "ada", PresenceState.Focusing, 23);
        Assert.Equal("DA1|ps|p1|me01|ada|c|23", OfficeInvites.Encode(presence));
        Assert.Equal(presence, OfficeInvites.Decode(OfficeInvites.Encode(presence)));
        foreach (var state in Enum.GetValues<PresenceState>())
            Assert.Equal(state, Assert.IsType<Presence>(OfficeInvites.Decode(OfficeInvites.Encode(presence with { State = state }))).State);

        var knock = new Knock("k1", "you2", "grace", "me01", "about the API, 5 min");
        Assert.Equal("DA1|kn|k1|you2|grace|me01|about the API, 5 min", OfficeInvites.Encode(knock));
        Assert.Equal(knock, OfficeInvites.Decode(OfficeInvites.Encode(knock)));

        foreach (var answer in Enum.GetValues<KnockAnswer>())
        {
            var reply = new KnockReply("k1", "me01", "ada", "you2", answer, answer == KnockAnswer.Queued ? 12 : 0);
            Assert.Equal(reply, OfficeInvites.Decode(OfficeInvites.Encode(reply)));
        }
        Assert.Equal("DA1|ka|k1|me01|ada|you2|t|0", OfficeInvites.Encode(new KnockReply("k1", "me01", "ada", "you2", KnockAnswer.InTen, 0)));
    }

    [Fact]
    public void TheTextStaysOneShortLine()
    {
        string wire = OfficeInvites.Encode(new Knock("k", "you", "grace", "me", "API | auth\nnow " + new string('x', 200)));
        var back = Assert.IsType<Knock>(OfficeInvites.Decode(wire));
        Assert.StartsWith("API / auth now x", back.Text);
        Assert.Equal(OfficeInvites.MaxText, back.Text.Length);
        Assert.Equal(7, wire.Split('|').Length);
    }

    [Theory]
    [InlineData("DA1|ps|p|me|ada|x|5")]      // no such state
    [InlineData("DA1|ps|p|me|ada|c|9999")]   // more minutes than a day
    [InlineData("DA1|ps|p|me||c|5")]        // no name
    [InlineData("DA1|kn|k|you|grace|me|")]   // no text
    [InlineData("DA1|kn|k|you|grace||hi")]   // to nobody
    [InlineData("DA1|ka|k|me|ada|you|z|0")]  // no such answer
    [InlineData("DA1|ka|k|me|ada|you|c")]    // a field short
    [InlineData("DA2|kn|k|you|grace|me|hi")] // a future format
    [InlineData("DA1|zz|k|you|grace|me|hi")] // a kind this copy does not know
    public void RejectsMalformedMessages(string text) => Assert.Null(OfficeInvites.Decode(text));

    [Fact]
    public void LaterVersionsMayAddFields()
    {
        // a kind only grows at the end: a field a later version adds is skipped, the rest still read
        var p = Assert.IsType<Presence>(OfficeInvites.Decode("DA1|ps|p|me|ada|m|40|something-new"));
        Assert.Equal(PresenceState.Meeting, p.State);
        Assert.IsType<Knock>(OfficeInvites.Decode("DA1|kn|k|you|grace|me|hi|later"));
        Assert.IsType<KnockReply>(OfficeInvites.Decode("DA1|ka|k|me|ada|you|l|0|later"));
    }

    [Fact]
    public void OlderCopiesIgnoreTheNewKinds()
    {
        // 1.8.5's reader, as it shipped: the three kinds it knew by name, anything else dropped without a word
        static object? Old(string text)
        {
            var f = text.Split('|');
            if (f.Length < 4 || f[0] != "DA1" || f[2].Length is 0 or > 40 || f[3].Length is 0 or > 40) return null;
            return f[1] is "iv" or "ir" or "ix" ? f[1] : null;
        }
        Assert.Null(Old(OfficeInvites.Encode(new Presence("p", "me", "ada", PresenceState.Free, 0))));
        Assert.Null(Old(OfficeInvites.Encode(new Knock("k", "me", "ada", "you", "hi"))));
        Assert.Null(Old(OfficeInvites.Encode(new KnockReply("k", "you", "grace", "me", KnockAnswer.ComeOver, 0))));
        Assert.NotNull(Old(OfficeInvites.Encode(new Invite("i", "me", "ada", InviteKind.Coffee, 5))));
    }

    [Fact]
    public void CoworkersComeChangeAndGo()
    {
        var list = new Coworkers();
        Assert.True(list.Heard(new Presence("1", "b2", "bob", PresenceState.Free, 0), T0));
        Assert.True(list.Heard(new Presence("2", "a1", "ada", PresenceState.Focusing, 25), T0));
        Assert.False(list.Heard(new Presence("3", "a1", "ada", PresenceState.Focusing, 22), T0.AddMinutes(3))); // the same, later
        Assert.Equal(new[] { "ada", "bob" }, list.Seen(T0.AddMinutes(4)).Select(c => c.Name));
        var ada = list.Find("a1")!;
        Assert.Equal(22, ada.MinutesLeft);
        Assert.Equal(17, ada.MinutesLeftAt(T0.AddMinutes(8.5)));
        Assert.Equal(0, ada.MinutesLeftAt(T0.AddHours(2)));
        Assert.True(list.Heard(new Presence("4", "a1", "ada", PresenceState.Meeting, 30), T0.AddMinutes(5)));
        // bob said nothing for ten minutes: gone from the list; ada turned invites off: gone at once
        Assert.Equal(new[] { "ada" }, list.Seen(T0.AddMinutes(11)).Select(c => c.Name));
        Assert.True(list.Heard(new Presence("5", "a1", "ada", PresenceState.Off, 0), T0.AddMinutes(6)));
        Assert.Equal(new[] { "bob" }, list.Seen(T0.AddMinutes(6)).Select(c => c.Name));
    }

    [Fact]
    public void TheQueueKeepsAFewKnocks_OnceEach_ForAMorning()
    {
        var q = new KnockQueue();
        var first = new Knock("k1", "you", "grace", "me", "about the API");
        Assert.True(q.FirstTime(first));
        Assert.False(q.FirstTime(first)); // its repeat
        q.Hold(first, T0);
        for (int i = 2; i <= 5; i++) q.Hold(new Knock("k" + i, "you", "grace", "me", "again " + i), T0.AddMinutes(i));
        Assert.Equal(KnockQueue.MaxPerSender, q.Count); // grace's oldest made way
        Assert.Equal(new[] { "k3", "k4", "k5" }, q.Held.Select(h => h.Knock.Id));
        for (int i = 0; i < 10; i++) q.Hold(new Knock("o" + i, "other" + i, "someone", "me", "hi"), T0.AddMinutes(10 + i));
        Assert.Equal(KnockQueue.Max, q.Count);

        var q2 = new KnockQueue();
        q2.Hold(new Knock("old", "you", "grace", "me", "yesterday's"), T0);
        q2.Hold(new Knock("new", "bob", "bob", "me", "today's"), T0.AddHours(4));
        Assert.Equal(new[] { "new" }, q2.TakeAll(T0.AddHours(4.5)).Select(h => h.Knock.Id)); // the old one is past answering
        Assert.Equal(0, q2.Count);
    }
}

/// <summary>Two copies over loopback, on a port of their own: a knock during a focus block waits for the break.</summary>
[Collection("lan")]
public class KnockLoopbackTests
{
    const int TestPort = 47931; // not the real port: a copy of Desk Arcade running on this PC must not hear these

    sealed class Copy : IDisposable
    {
        public readonly OfficeInvites Channel;
        public readonly Knocking Knocking;
        public (PresenceState State, int Minutes) Now = (PresenceState.Free, 0);
        public readonly ConcurrentQueue<object> Inbox = new();
        public readonly List<IReadOnlyList<HeldKnock>> Shown = new();
        public readonly List<KnockReply> Replies = new();

        public Copy(string id, string name)
        {
            Channel = new OfficeInvites(id, () => name, TestPort);
            Knocking = new Knocking(Channel, () => Now);
            Channel.PresenceReceived += Inbox.Enqueue;
            Channel.KnockReceived += Inbox.Enqueue;
            Channel.KnockReplyReceived += Inbox.Enqueue;
            Knocking.Show += Shown.Add;
            Knocking.Replied += (_, r) => Replies.Add(r);
            Channel.Start();
        }

        /// <summary>What the overlay's one-second tick and its UI thread do: take in the messages, then step.</summary>
        public void Pump()
        {
            while (Inbox.TryDequeue(out var message)) Knocking.Receive(message, DateTime.UtcNow);
            Knocking.Step(DateTime.UtcNow);
        }

        public void Dispose() => Channel.Dispose();
    }

    static void PumpUntil(Func<bool> done, params Copy[] copies)
    {
        var until = DateTime.UtcNow.AddSeconds(6);
        while (!done() && DateTime.UtcNow < until)
        {
            foreach (var c in copies) c.Pump();
            Thread.Sleep(20);
        }
        Assert.True(done(), "timed out");
    }

    [Fact]
    public void AKnockDuringAFocusBlockShowsAtTheBreak()
    {
        using var ada = new Copy("ada01", "ada");
        using var bob = new Copy("bob02", "bob");
        Assert.True(ada.Channel.Running && bob.Channel.Running);
        bob.Now = (PresenceState.Focusing, 20);

        // their presence: ada sees bob focusing
        PumpUntil(() => ada.Knocking.Coworkers.Find("bob02")?.State == PresenceState.Focusing, ada, bob);
        var target = ada.Knocking.Coworkers.Find("bob02")!;
        Assert.Equal("bob", target.Name);
        Assert.Equal(20, target.MinutesLeft);

        // the knock is queued on bob's side, and ada hears so, with the minutes until his break
        var sent = ada.Knocking.KnockOn(target, "about the API, 5 min", DateTime.UtcNow);
        Assert.NotNull(sent);
        PumpUntil(() => ada.Replies.Any(r => r.Answer == KnockAnswer.Queued), ada, bob);
        Assert.Equal(1, bob.Knocking.Waiting.Count);
        Assert.Empty(bob.Shown);
        Assert.Equal(20, ada.Replies.Single(r => r.Answer == KnockAnswer.Queued).Minutes);
        Thread.Sleep(1500); // the knock's repeats arrive and change nothing
        ada.Pump();
        bob.Pump();
        Assert.Equal(1, bob.Knocking.Waiting.Count);

        // the focus block ends: at the break the card shows it
        bob.Now = (PresenceState.Free, 0);
        bob.Pump();
        var shown = Assert.Single(Assert.Single(bob.Shown));
        Assert.Equal("about the API, 5 min", shown.Knock.Text);
        Assert.Equal("ada", shown.Knock.From);
        Assert.Equal(0, bob.Knocking.Waiting.Count);

        // bob answers, and ada gets the answer
        bob.Knocking.Answer(shown.Knock, KnockAnswer.ComeOver);
        PumpUntil(() => ada.Replies.Any(r => r.Answer == KnockAnswer.ComeOver), ada, bob);
        Assert.Equal(KnockAnswer.ComeOver, sent!.Answer);

        // bob turns invites off: ada's list loses him at once
        PumpUntil(() => ada.Knocking.Coworkers.Find("bob02")?.State == PresenceState.Free, ada, bob);
        bob.Channel.Stop();
        PumpUntil(() => ada.Knocking.Coworkers.Find("bob02") == null, ada);
    }

    [Fact]
    public void SomeoneFreeSeesTheKnockAtOnce()
    {
        using var ada = new Copy("ada03", "ada");
        using var bob = new Copy("bob04", "bob");
        PumpUntil(() => ada.Knocking.Coworkers.Find("bob04") != null, ada, bob);
        ada.Knocking.KnockOn(ada.Knocking.Coworkers.Find("bob04")!, "lunch?", DateTime.UtcNow);
        PumpUntil(() => bob.Shown.Count > 0 && ada.Replies.Any(r => r.Answer == KnockAnswer.Seen), ada, bob);
        Assert.Equal("lunch?", Assert.Single(Assert.Single(bob.Shown)).Knock.Text);
        Assert.Equal(0, bob.Knocking.Waiting.Count);
    }
}
