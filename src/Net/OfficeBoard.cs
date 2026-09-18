using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DeskArcade.Net;

/// <summary>One player's line on the office leaderboard: their name and today's scores.</summary>
public sealed record BoardEntry(string Name, string Day, IReadOnlyDictionary<string, long> Scores);

/// <summary>
/// The office leaderboard: while sharing is on, every running copy broadcasts its player's name and today's
/// scores on UDP port <see cref="Port"/> every <see cref="ShareSeconds"/> seconds, and collects everyone
/// else's. Nothing leaves the local network, and nothing is sent while sharing is off. It is separate from
/// the two-player <see cref="LanLink"/>, so it works whether or not anyone is playing together.
/// Wire format: "DA1|lb|name|yyyy-MM-dd|key=value,key=value" and "DA1|lbq" (asks everyone to share now).
/// </summary>
public sealed class OfficeBoard : IDisposable
{
    public const int Port = 47821;
    const string Magic = "DA1";
    const double ShareSeconds = 20, ForgetSeconds = 90;

    /// <summary>The scores on the board, in display order: the counter(s) behind each category.</summary>
    public static readonly (string Key, string Title, string[] Counters)[] Categories =
    {
        ("streak", "Best hoops streak", new[] { "hoops.streak" }),
        ("baskets", "Baskets", new[] { "hoops.baskets" }),
        ("hockey", "Air Hockey wins", new[] { "hockey.wins", "hockey.lanwins" }),
        ("pong", "Pong wins", new[] { "pong.wins" }),
        ("bugs", "Bugs squashed", new[] { "bugs.squashed", "whack.hits" }),
        ("tower", "Tallest tower", new[] { "tower.height" }),
        ("lan", "LAN wins", new[] { "lan.wins" }),
        ("minutes", "Minutes played", new[] { "play.ms" }), // milliseconds; shown in minutes
    };

    readonly object _gate = new();
    readonly Dictionary<string, (BoardEntry Entry, DateTime Seen)> _others = new();
    readonly Func<BoardEntry> _mine;
    UdpClient? _udp;
    CancellationTokenSource? _cts;

    /// <param name="mine">This player's current entry. Called from background threads, so it should return a ready-made snapshot.</param>
    public OfficeBoard(Func<BoardEntry> mine) => _mine = mine;

    public bool Running => _udp != null;

    /// <summary>Raised (on a background thread) when someone's scores arrive.</summary>
    public event Action? Changed;

    public void Start()
    {
        if (_udp != null) return;
        try
        {
            var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true, ExclusiveAddressUse = false };
            // several copies on one PC (--profile) can all listen
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
            var cts = new CancellationTokenSource();
            _udp = udp;
            _cts = cts;
            Task.Run(() => ReceiveLoop(udp, cts.Token));
            Task.Run(() => ShareLoop(udp, cts.Token));
            Broadcast(udp, "lbq"); // ask everyone for their scores now rather than in 20 seconds
        }
        catch (SocketException)
        {
            Stop(); // the port is unavailable: the board just stays empty
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _udp?.Dispose();
        _udp = null;
        _cts = null;
        lock (_gate) _others.Clear();
    }

    /// <summary>Everyone heard from recently whose scores are for <paramref name="day"/>, plus this player.</summary>
    public List<BoardEntry> Entries(string day)
    {
        var list = new List<BoardEntry> { _mine() };
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            foreach (var (key, (entry, seen)) in _others.ToList())
            {
                if ((now - seen).TotalSeconds > ForgetSeconds) _others.Remove(key);
                else if (entry.Day == day && entry.Name != list[0].Name) list.Add(entry);
            }
        }
        return list;
    }

    /// <summary>Today's scores for every category, from a counter reader such as <c>Stats.Today</c>.</summary>
    public static Dictionary<string, long> Scores(Func<string, long> today) =>
        Categories.ToDictionary(c => c.Key, c => c.Key == "minutes" ? today("play.ms") / 60000 : c.Counters.Sum(today));

    /// <summary>Ranks everyone on one category, best first, leaving out zeros.</summary>
    public static List<(string Name, long Score)> Rank(IEnumerable<BoardEntry> entries, string key) =>
        entries.Select(e => (e.Name, Score: e.Scores.TryGetValue(key, out long v) ? v : 0))
            .Where(r => r.Score > 0)
            .OrderByDescending(r => r.Score).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static string Encode(BoardEntry e) =>
        $"{Magic}|lb|{Clean(e.Name)}|{e.Day}|" + string.Join(",", e.Scores.Where(kv => kv.Value > 0).Select(kv => $"{Clean(kv.Key)}={kv.Value}"));

    static string Clean(string s) => new(s.Where(c => c is not ('|' or ',' or '=') && !char.IsControl(c)).Take(40).ToArray());

    public static BoardEntry? Decode(string text)
    {
        var f = text.Split('|');
        if (f.Length != 5 || f[0] != Magic || f[1] != "lb" || f[2].Length == 0 ||
            !DateTime.TryParseExact(f[3], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return null;
        var scores = new Dictionary<string, long>();
        foreach (var pair in f[4].Split(',', StringSplitOptions.RemoveEmptyEntries).Take(32))
        {
            var kv = pair.Split('=');
            if (kv.Length == 2 && kv[0].Length > 0 && long.TryParse(kv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) && v > 0)
                scores[kv[0]] = Math.Min(v, 1_000_000_000);
        }
        return new BoardEntry(f[2], f[3], scores);
    }

    async Task ReceiveLoop(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await udp.ReceiveAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }
            if (r.Buffer.Length > 2048) continue;

            string text = Encoding.UTF8.GetString(r.Buffer);
            if (text == $"{Magic}|lbq")
            {
                Share(udp);
                continue;
            }
            if (Decode(text) is not BoardEntry entry) continue;
            lock (_gate) _others[entry.Name + "@" + r.RemoteEndPoint.Address] = (entry, DateTime.UtcNow);
            Changed?.Invoke();
        }
    }

    async Task ShareLoop(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Share(udp);
            try { await Task.Delay(TimeSpan.FromSeconds(ShareSeconds), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    void Share(UdpClient udp)
    {
        try { Broadcast(udp, Encode(_mine())[(Magic.Length + 1)..]); }
        catch (Exception) { /* building the entry or sending failed: try again next time */ }
    }

    static void Broadcast(UdpClient udp, string message)
    {
        var bytes = Encoding.UTF8.GetBytes(Magic + "|" + message);
        foreach (var to in new[] { new IPEndPoint(IPAddress.Broadcast, Port), new IPEndPoint(IPAddress.Loopback, Port) })
        {
            try { udp.Send(bytes, bytes.Length, to); }
            catch (SocketException) { }
            catch (ObjectDisposedException) { return; }
        }
    }

    public void Dispose() => Stop();
}
