using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace DeskArcade;

/// <summary>
/// Named-pipe channel so a second process (e.g. a Claude Code hook running
/// "DeskArcade.exe --signal done") can talk to the running overlay.
/// </summary>
public static class Ipc
{
    static string PipeName => "DeskArcade.Signal.v1" + Program.InstanceSuffix;

    /// <summary>Messages whose text after the prefix keeps its spelling: a --while label, a path, a web address, a note.</summary>
    static readonly string[] KeepCase = { "task:", "wait-file:", "wait-url:", "timer:", "note:", "chat:" };

    /// <summary>The message as the overlay handles it: trimmed, and lower-cased unless its text must keep its spelling.</summary>
    public static string Normalize(string line)
    {
        var msg = line.Trim();
        foreach (string prefix in KeepCase)
            if (msg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return prefix + msg[prefix.Length..];
        return msg.ToLowerInvariant();
    }

    public static bool Send(string message, int timeoutMs = 150)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeoutMs);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(message);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void StartServer(Action<string> onMessage, CancellationToken ct)
    {
        Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(ct);
                    using var reader = new StreamReader(server);
                    string? line;
                    while ((line = await reader.ReadLineAsync(ct)) != null)
                    {
                        var msg = Normalize(line);
                        if (msg.Length > 0) onMessage(msg);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { await Task.Delay(250, CancellationToken.None); }
            }
        }, ct);
    }
}
