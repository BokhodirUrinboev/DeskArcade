using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using DeskArcade.Net;
using Xunit;

namespace DeskArcade.Tests;

/// <summary>
/// Pairing over the real UDP link, with a hand-driven socket on the other side: duplicated or re-sent hellos (a
/// broadcast that arrives twice, a welcome that got lost) must not be taken for a guest that dropped out and came back.
/// </summary>
[Collection("lan")] // every LAN test binds UDP port 47820, so they take turns
public class LanRejoinTests
{
    const int TestPort = 47891; // not the real port: a copy of Desk Arcade running on this PC must not hear these
    static readonly IPEndPoint HostPort = new(IPAddress.Loopback, TestPort);

    static void Send(UdpClient udp, string text, IPEndPoint to)
    {
        var bytes = Encoding.UTF8.GetBytes("DA1|" + text);
        udp.Send(bytes, bytes.Length, to);
    }

    static bool WaitFor(Func<bool> condition, int ms = 3000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            Thread.Sleep(20);
        }
        return condition();
    }

    [Fact]
    public void ARepeatedHelloIsAnsweredButStartsNoNewSession()
    {
        using var host = new LanLink(TestPort);
        host.Host("hoops");
        int changes = 0;
        host.StateChanged += () => Interlocked.Increment(ref changes);
        using var guest = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        guest.Client.ReceiveTimeout = 1000;

        Send(guest, "hello|bob", HostPort);
        Assert.True(WaitFor(() => host.Connected));
        int session = host.Session;
        for (int i = 0; i < 3; i++) // the same hello again: a broadcast seen twice, or sent again before the welcome came
        {
            Thread.Sleep(150);
            Send(guest, "hello|bob", HostPort);
        }
        Thread.Sleep(400);
        Assert.Equal(1, changes); // Waiting -> Connected, once
        Assert.Equal(session, host.Session);
        Assert.Equal("bob", host.PeerName);

        int welcomes = 0;
        var from = new IPEndPoint(IPAddress.Any, 0);
        try
        {
            while (true)
                if (Encoding.UTF8.GetString(guest.Receive(ref from)).StartsWith("DA1|welcome|", StringComparison.Ordinal)) welcomes++;
        }
        catch (SocketException) { } // nothing more to read
        Assert.Equal(4, welcomes); // every hello got its welcome, in case the first one was lost
    }

    [Fact]
    public void AGuestThatWentQuietAndSaysHelloAgainIsANewSession()
    {
        using var host = new LanLink(TestPort);
        host.Host("hoops");
        using var guest = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        Send(guest, "hello|bob", HostPort);
        Assert.True(WaitFor(() => host.Connected));
        int session = host.Session;
        // the guest stops hearing us and times out on its side (three seconds), then looks for a host again
        var until = DateTime.UtcNow.AddSeconds(3.2);
        while (DateTime.UtcNow < until)
        {
            Send(guest, "ping|", HostPort);
            Thread.Sleep(400);
        }
        Send(guest, "hello|bob", HostPort);
        Assert.True(WaitFor(() => host.Session == session + 1), "a real rejoin starts a new session");
        Assert.True(host.Connected);
    }

    [Fact]
    public void AGuestTakesTheWelcomeFromAnotherAddressOfTheHostItJoined()
    {
        // a host with more than one address can answer from another one than the guest typed or picked
        UdpClient listen, answer;
        try
        {
            listen = new UdpClient(new IPEndPoint(IPAddress.Loopback, 47890));
            answer = new UdpClient(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 47890));
        }
        catch (SocketException)
        {
            return; // this system has no second loopback address to answer from
        }
        using (listen)
        using (answer)
        using (var guest = new LanLink(47890))
        {
            listen.Client.ReceiveTimeout = 3000;
            guest.Join(new IPEndPoint(IPAddress.Loopback, 47890));
            var from = new IPEndPoint(IPAddress.Any, 0);
            string hello = Encoding.UTF8.GetString(listen.Receive(ref from));
            Assert.StartsWith("DA1|hello|", hello);
            Send(answer, "welcome|chess|alice", from);
            Assert.True(WaitFor(() => guest.Connected), "the guest accepted the welcome");
            Assert.Equal("alice", guest.PeerName);
            Assert.Equal("chess", guest.GameId);
        }
    }
}
