using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace DeskArcade.Dev;

/// <summary>
/// The fire-and-forget command-line options for coding agents and scripts. Both hand one line to the running overlay
/// and return at once; neither starts Desk Arcade, and neither fails a hook or a script because it is closed.
/// <code>
/// deskarcade --signal working|done|attention|end|step|did|auto [--agent NAME] [JSON]
/// deskarcade --status NAME running|passed|failed|clear [--note TEXT] [--source TOOL]
/// </code>
/// With --signal, the JSON an agent passes its hook is read from the last argument (Codex's notify) or from stdin
/// (Claude Code, Cursor, Gemini CLI), for at most <see cref="StdinWait"/>; with nothing piped in, nothing is waited for.
/// </summary>
public static class DevCli
{
    /// <summary>The longest a hook waits for its stdin to close.</summary>
    public static readonly TimeSpan StdinWait = TimeSpan.FromMilliseconds(1000);

    /// <summary>The most read from stdin: a PostToolUse can carry a whole file in its response, which is not needed.</summary>
    const int MaxStdin = 1 << 20;

    /// <summary>deskarcade --signal …: <paramref name="at"/> is where --signal is in <paramref name="args"/>.</summary>
    public static int Signal(string[] args, int at)
    {
        string word = at + 1 < args.Length && !args[at + 1].StartsWith("--", StringComparison.Ordinal) ? args[at + 1] : "show";
        string? state = word.Equals("auto", StringComparison.OrdinalIgnoreCase) ? "auto" : AgentSignal.StateOf(word);
        if (state == null)
        {
            Ipc.Send(word); // show, hide, next, a game... as always
            return 0;
        }
        if (Message(state, args, ReadStdin) is string message) Ipc.Send(message);
        return 0;
    }

    /// <summary>
    /// The agent line for a --signal state ("auto" takes it from the payload's event): the agent from --agent, the
    /// payload from the last argument or stdin. Null when there is nothing worth sending.
    /// </summary>
    public static string? Message(string state, string[] args, Func<TimeSpan, string?> stdin)
    {
        int ai = Array.FindIndex(args, a => a.Equals("--agent", StringComparison.OrdinalIgnoreCase));
        string? agent = ai >= 0 && ai + 1 < args.Length ? args[ai + 1] : null;
        string? payload = args.Length > 0 && args[^1].TrimStart().StartsWith('{') ? args[^1] : stdin(StdinWait);
        string? folder;
        try { folder = Environment.CurrentDirectory; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { folder = null; }
        return AgentSignal.FromHook(state, agent, payload, folder)?.ToMessage();
    }

    /// <summary>deskarcade --status …: prints the usage and returns 2 when the arguments are wrong, else 0.</summary>
    public static int Status(string[] args, int at)
    {
        var (message, error) = StatusMessage.FromArgs(args, at);
        if (message == null)
        {
            if (OperatingSystem.IsWindows()) AttachConsole(AttachParentProcess); // a GUI program: borrow the terminal to say why
            Console.Error.WriteLine(error);
            return 2;
        }
        Ipc.Send(message.ToLine());
        return 0;
    }

    /// <summary>
    /// What was piped into stdin, read on a worker for at most <paramref name="wait"/>; null when stdin is a terminal or
    /// nothing, and what arrived so far when the writer never closes it. Desk Arcade is a GUI program on Windows: a pipe
    /// handed to it (Claude Code runs hooks through a shell that does) still reads, and no handle reads as empty.
    /// </summary>
    public static string? ReadStdin(TimeSpan wait)
    {
        try
        {
            if (!Console.IsInputRedirected) return null;
            var input = Console.OpenStandardInput();
            var buffer = new MemoryStream();
            var reading = Task.Run(() =>
            {
                var chunk = new byte[16384];
                int n;
                try
                {
                    while ((n = input.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        lock (buffer)
                        {
                            if (buffer.Length >= MaxStdin) return;
                            buffer.Write(chunk, 0, n);
                        }
                    }
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException or NotSupportedException or UnauthorizedAccessException)
                {
                    // no stdin after all
                }
            });
            reading.Wait(wait);
            lock (buffer)
                return buffer.Length == 0 ? null : Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException or NotSupportedException or AggregateException)
        {
            return null;
        }
    }

    const int AttachParentProcess = -1;

    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int processId);
}
