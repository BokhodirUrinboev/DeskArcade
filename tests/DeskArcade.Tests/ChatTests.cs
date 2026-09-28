using System.Collections.Generic;
using System.Linq;
using DeskArcade.Net;
using Xunit;

namespace DeskArcade.Tests;

public class ChatLinkTests
{
    readonly List<string> _wire = new();

    ChatLink Link(int first = 100) => new(_wire.Add, first);

    [Theory]
    [InlineData("  hello   there \n friend\t ", "hello there friend")]
    [InlineData("a\u0007b\u0000c", "abc")]
    [InlineData("pay ‮gnp.exe", "pay gnp.exe")] // a right-to-left override would make the text read backwards
    [InlineData("⁦x⁩‎", "x")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData(" \r\n ", "")]
    public void CleaningKeepsOnlyPlainReadableText(string? raw, string clean) => Assert.Equal(clean, ChatLink.Clean(raw));

    [Fact]
    public void LongTextIsCutWithoutSplittingACharacter()
    {
        Assert.Equal(ChatLink.MaxLength, ChatLink.Clean(new string('x', 1000)).Length);
        string emoji = new string('x', ChatLink.MaxLength - 1) + "😀"; // the emoji's second half would fall past the limit
        string cut = ChatLink.Clean(emoji);
        Assert.Equal(ChatLink.MaxLength - 1, cut.Length);
        Assert.False(char.IsHighSurrogate(cut[^1]));
    }

    [Fact]
    public void SendingWritesTheMessageAndItsId()
    {
        var link = Link(100);
        Assert.Equal(ChatSend.Sent, link.Send("lunch at 1?", 0, out int id));
        Assert.Equal(101, id);
        Assert.Equal("ch|msg|101|lunch at 1?", _wire.Single());
        Assert.True(link.Busy);
    }

    [Fact]
    public void EmptyTooSoonAndTooManyAreRefused()
    {
        var link = Link();
        Assert.Equal(ChatSend.Empty, link.Send("   ", 0, out _));
        Assert.Equal(ChatSend.Sent, link.Send("one", 0, out _));
        Assert.Equal(ChatSend.TooSoon, link.Send("two", 0.1, out _));
        for (int i = 1; i < ChatLink.MaxInFlight; i++) Assert.Equal(ChatSend.Sent, link.Send("more", i, out _));
        Assert.Equal(ChatSend.TooMany, link.Send("one too many", 10, out _));
    }

    [Fact]
    public void UnansweredMessagesAreResentAndThenGivenUp()
    {
        var link = Link();
        link.Send("hi", 0, out int id);
        _wire.Clear();
        Assert.Empty(link.Tick(0.1));
        Assert.Empty(_wire); // not yet
        link.Tick(ChatLink.ResendSeconds);
        Assert.Single(_wire);
        Assert.Equal(new[] { id }, link.Tick(ChatLink.GiveUpSeconds));
        Assert.False(link.Busy);
    }

    [Fact]
    public void AReceiptEndsTheResending()
    {
        var sender = Link();
        sender.Send("hi", 0, out int id);
        Assert.Equal(new ChatEvent(ChatEventKind.Delivered, id), sender.Handle($"ch|got|{id}", true, 0.2));
        Assert.False(sender.Busy);
        Assert.Equal(default, sender.Handle($"ch|got|{id}", true, 0.3)); // a second receipt says nothing new
    }

    [Fact]
    public void AMessageIsShownOnceAndAcknowledgedEveryTime()
    {
        var receiver = Link();
        var e = receiver.Handle("ch|msg|7|see you | at the desk", true, 0);
        Assert.Equal(ChatEventKind.Message, e.Kind);
        Assert.Equal("see you | at the desk", e.Text); // a bar inside the text is fine
        Assert.Equal("ch|got|7", _wire.Last());
        Assert.Equal(default, receiver.Handle("ch|msg|7|see you | at the desk", true, 1));
        Assert.Equal(2, _wire.Count(w => w == "ch|got|7"));
    }

    [Fact]
    public void WithChatOffAMessageIsRefusedAndKnocks()
    {
        var receiver = Link();
        var e = receiver.Handle("ch|msg|9|hey", accepting: false, 0);
        Assert.Equal(ChatEventKind.Knock, e.Kind);
        Assert.Equal("ch|off|9", _wire.Single());

        var sender = Link();
        sender.Send("hey", 0, out int id);
        Assert.Equal(new ChatEvent(ChatEventKind.Refused, id), sender.Handle($"ch|off|{id}", true, 0.1));
        Assert.False(sender.Busy);
    }

    [Fact]
    public void AFloodIsLeftUnacknowledged()
    {
        var receiver = Link();
        int shown = 0;
        for (int i = 1; i <= ChatLink.Burst + 5; i++)
            if (receiver.Handle($"ch|msg|{i}|spam {i}", true, 0).Kind == ChatEventKind.Message) shown++;
        Assert.Equal(ChatLink.Burst, shown);
        Assert.Equal(ChatLink.Burst, _wire.Count); // the rest get no receipt, so their sender sees them fail
        Assert.Equal(ChatEventKind.Message, receiver.Handle("ch|msg|99|later", true, 2 * ChatLink.RefillSeconds).Kind);
    }

    [Theory]
    [InlineData("ch|msg|x|hi")]
    [InlineData("ch|msg|-4|hi")]
    [InlineData("ch|msg|5")]
    [InlineData("ch|what|5|hi")]
    [InlineData("pm|gift|5|treat")]
    [InlineData("ch|msg|5|\u0001\u0002")] // nothing left once cleaned
    public void MalformedMessagesAreIgnored(string message) => Assert.NotEqual(ChatEventKind.Message, Link().Handle(message, true, 0).Kind);

    [Fact]
    public void ResetForgetsEverything()
    {
        var link = Link();
        link.Send("hi", 0, out _);
        link.Handle("ch|msg|3|yo", true, 0);
        link.Reset();
        Assert.False(link.Busy);
        Assert.Equal(ChatEventKind.Message, link.Handle("ch|msg|3|yo", true, 1).Kind); // a new session may reuse the id
    }
}
