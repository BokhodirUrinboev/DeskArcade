using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DeskArcade.Office;

/// <summary>
/// Downloads finishing in the Downloads folder. Browsers write a download to a partial file first (Chrome and Edge
/// "report.pdf.crdownload", Firefox "report.pdf.part", Safari "report.pdf.download", Opera ".opdownload") and rename it
/// when it is done. A partial file that goes away while its finished file is there is a finished download; one that
/// goes away without it was cancelled.
/// </summary>
public sealed class DownloadWatch
{
    static readonly string[] PartialEndings = { ".crdownload", ".part", ".partial", ".download", ".opdownload" };

    /// <summary>How recently a file must have changed to be taken for the download whose partial name told nothing.</summary>
    public static readonly TimeSpan RecentEnough = TimeSpan.FromSeconds(30);

    HashSet<string> _partials = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> InProgress => _partials;

    public static bool IsPartial(string name) => PartialEndings.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The finished name a partial name promises ("report.pdf.crdownload" → "report.pdf"), or null when it promises
    /// none (Chrome's "Unconfirmed 123456.crdownload", before the name is known).
    /// </summary>
    public static string? FinalName(string partial)
    {
        string? ending = PartialEndings.FirstOrDefault(e => partial.EndsWith(e, StringComparison.OrdinalIgnoreCase));
        if (ending == null) return null;
        string name = partial[..^ending.Length];
        if (name.StartsWith("Unconfirmed ", StringComparison.OrdinalIgnoreCase) || name.Length == 0) return null;
        return name;
    }

    /// <summary>
    /// One look at the folder: the partial files in it now, and the finished files with when they last changed.
    /// Returns the downloads that started and those that finished since the last look.
    /// </summary>
    public (List<string> Started, List<string> Finished) Step(IEnumerable<string> partials, IReadOnlyDictionary<string, DateTime> files, DateTime nowUtc)
    {
        var now = new HashSet<string>(partials, StringComparer.OrdinalIgnoreCase);
        var started = now.Where(p => !_partials.Contains(p)).Select(p => FinalName(p) ?? "").ToList();
        var finished = new List<string>();
        foreach (string gone in _partials.Where(p => !now.Contains(p)))
        {
            if (FinalName(gone) is string name && files.ContainsKey(name))
            {
                finished.Add(name);
                continue;
            }
            // the name was not known yet: the newest file that has just changed, if any; otherwise it was cancelled
            var recent = files.Where(f => nowUtc - f.Value <= RecentEnough && !finished.Contains(f.Key))
                .OrderByDescending(f => f.Value).Select(f => f.Key).FirstOrDefault();
            if (recent != null) finished.Add(recent);
        }
        _partials = now;
        return (started, finished);
    }

    /// <summary>The Downloads folder: the XDG user dir on Linux, ~/Downloads elsewhere.</summary>
    public static string DefaultFolder()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsLinux())
        {
            try
            {
                string config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(home, ".config");
                string dirs = Path.Combine(config, "user-dirs.dirs");
                if (File.Exists(dirs) && XdgDownloadDir(File.ReadAllText(dirs), home) is string dir) return dir;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return Path.Combine(home, "Downloads");
    }

    /// <summary>XDG_DOWNLOAD_DIR="$HOME/Downloads" from user-dirs.dirs.</summary>
    public static string? XdgDownloadDir(string text, string home)
    {
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (!line.StartsWith("XDG_DOWNLOAD_DIR=", StringComparison.Ordinal)) continue;
            string value = line["XDG_DOWNLOAD_DIR=".Length..].Trim().Trim('"');
            return value.Replace("$HOME", home, StringComparison.Ordinal);
        }
        return null;
    }
}

/// <summary>
/// "deskarcade --wait-file report.pdf": the file is ready once it exists, has kept the same size for a few seconds and
/// no browser is still writing a partial file next to it.
/// </summary>
public sealed class FileWait
{
    public const double SettleSeconds = 5;

    long _size = -1;
    double _stableFor;

    /// <param name="exists">The file is there.</param>
    /// <param name="size">Its size now.</param>
    /// <param name="partialNextToIt">A browser's partial file for it is there too ("report.pdf.part").</param>
    public bool Step(double dt, bool exists, long size, bool partialNextToIt)
    {
        if (!exists || partialNextToIt)
        {
            _size = -1;
            _stableFor = 0;
            return false;
        }
        if (size != _size)
        {
            _size = size;
            _stableFor = 0;
            return false;
        }
        _stableFor += dt;
        return _stableFor >= SettleSeconds;
    }
}
