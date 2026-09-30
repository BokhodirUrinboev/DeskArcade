using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using DeskArcade.Office;

namespace DeskArcade;

/// <summary>Waiting for things other than a --while command: downloads, a program, a file, a web address.</summary>
public sealed partial class OfficeDesk
{
    const int MaxWaits = 5, MaxFolderEntries = 3000;
    const double WaitCheckSeconds = 2, UrlCheckSeconds = 5, UrlGiveUpHours = 2;

    sealed class Wait
    {
        public required string Kind;   // "pid", "file" or "url"
        public required string Target;
        public required string Label;
        public double Since;
        public DateTime StartUtc;       // for the wait report
        public double PlayedAtStart;
        public double CheckedAt = -99;
        public FileWait? File;
        public bool Busy;              // a web check is out
        public bool Done;
    }

    readonly List<Wait> _waits = new();
    readonly DownloadWatch _downloads = new();
    readonly DispatcherTimer _downloadTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    bool _downloadsPrimed, _scanning;
    // when each download in progress was first seen, by the name it will have ("" when not known yet), for the wait report
    readonly Dictionary<string, (DateTime Start, double Played)> _downloadStarts = new(StringComparer.OrdinalIgnoreCase);
    readonly Queue<(DateTime Start, double Played)> _unnamedStarts = new();

    /// <summary>A finished wait goes into the wait log as a download: how long it took and how much of it was played.</summary>
    void RecordWait(string label, DateTime startUtc, double playedAtStart, bool? passed, string source)
    {
        double seconds = (DateTime.UtcNow - startUtc).TotalSeconds;
        double played = Math.Max(0, _w.Stats.TotalSeconds - playedAtStart);
        _w.Waits.Add(new Dev.WaitEntry(Dev.WaitKind.Download, label, startUtc, seconds, passed, played, source));
    }

    /// <summary>Notes when downloads start, and logs the ones that finished with how long they took.</summary>
    void RecordDownloads(List<string> started, List<string> finished)
    {
        var now = (DateTime.UtcNow, _w.Stats.TotalSeconds);
        foreach (string name in started)
        {
            if (name.Length == 0)
            {
                if (_unnamedStarts.Count < 20) _unnamedStarts.Enqueue(now);
            }
            else _downloadStarts.TryAdd(name, now);
        }
        foreach (string name in finished)
        {
            if (_downloadStarts.Remove(name, out var start) || _unnamedStarts.TryDequeue(out start))
                RecordWait(name, start.Start, start.Played, null, "download");
        }
        if (_downloadStarts.Count > 50) _downloadStarts.Clear(); // cancelled ones never finish
    }

    // ------------------------------------------------------------------ downloads

    /// <summary>Tray → At work → Tell me when downloads finish.</summary>
    public void SetWatchDownloads(bool on)
    {
        S.WatchDownloads = on;
        _w.SaveSettings();
        StartDownloads();
        if (on) Say(L.T("Watching downloads"), Short(DownloadsFolder, 48), Blue, null);
        _w.RefreshTray();
    }

    public string DownloadsFolder => string.IsNullOrWhiteSpace(S.DownloadsFolder) ? DownloadWatch.DefaultFolder() : S.DownloadsFolder!;

    public void SetDownloadsFolder(string? folder)
    {
        S.DownloadsFolder = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();
        _w.SaveSettings();
        _downloadsPrimed = false;
    }

    void StartDownloads()
    {
        if (!_downloadTimerHooked)
        {
            _downloadTimerHooked = true;
            _downloadTimer.Tick += (_, _) => ScanDownloads();
        }
        _downloadsPrimed = false;
        if (S.WatchDownloads) _downloadTimer.Start();
        else _downloadTimer.Stop();
    }

    bool _downloadTimerHooked;

    /// <summary>One look at the Downloads folder, on a worker thread (a network drive can be slow).</summary>
    async void ScanDownloads()
    {
        if (_scanning) return;
        _scanning = true;
        string folder = DownloadsFolder;
        try
        {
            var (partials, files) = await Task.Run(() => ListFolder(folder));
            var (started, finished) = _downloads.Step(partials, files, DateTime.UtcNow);
            RecordDownloads(started, finished);
            if (!_downloadsPrimed)
            {
                _downloadsPrimed = true; // what was already downloading when the watch began is only noted
                return;
            }
            foreach (string name in finished.Take(3))
                Say(L.T("Download finished"), Short(name, 48), Green, "done", 0.8);
        }
        catch (Exception)
        {
            // the folder is gone or unreadable: try again next time
        }
        finally
        {
            _scanning = false;
        }
    }

    static (List<string> Partials, Dictionary<string, DateTime> Files) ListFolder(string folder)
    {
        var partials = new List<string>();
        var files = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(folder)) return (partials, files);
        int n = 0;
        foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
        {
            if (++n > MaxFolderEntries) break;
            if (DownloadWatch.IsPartial(entry.Name)) partials.Add(entry.Name); // Safari's .download is a folder
            else if (entry is FileInfo) files[entry.Name] = entry.LastWriteTimeUtc;
        }
        return (partials, files);
    }

    // ------------------------------------------------------------------ deskarcade --wait-pid / --wait-file / --wait-url

    void AddWait(Wait wait)
    {
        if (_waits.Count >= MaxWaits) _waits.RemoveAt(0);
        wait.Since = Now;
        wait.StartUtc = DateTime.UtcNow;
        wait.PlayedAtStart = _w.Stats.TotalSeconds;
        _waits.Add(wait);
        if (!_fullScreen) ShowForReal(); // asking to wait means "let me play meanwhile"
        Say(L.F("Waiting for {0}", Short(wait.Label, 40)), L.T("a chime when it is done · play meanwhile"), Blue, "score", 0.5);
    }

    public void WaitForProcess(int pid)
    {
        string name;
        try
        {
            using var p = Process.GetProcessById(pid);
            name = p.ProcessName;
        }
        catch (Exception)
        {
            Say(L.F("No program with id {0}", pid), L.T("it may have finished already"), Red, null);
            return;
        }
        AddWait(new Wait { Kind = "pid", Target = pid.ToString(System.Globalization.CultureInfo.InvariantCulture), Label = L.F("{0} ({1})", name, pid) });
    }

    public void WaitForFile(string path)
    {
        path = path.Trim().Trim('"');
        if (path.Length == 0) return;
        try { path = Path.GetFullPath(path); }
        catch (Exception) { return; }
        AddWait(new Wait { Kind = "file", Target = path, Label = Path.GetFileName(path), File = new FileWait() });
    }

    public void WaitForUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            Say(L.T("That is not a web address"), L.T("e.g. http://localhost:8080/health"), Red, null);
            return;
        }
        AddWait(new Wait { Kind = "url", Target = uri.ToString(), Label = uri.IsDefaultPort ? uri.Host : uri.Host + ":" + uri.Port });
    }

    void StepWaits(double dt)
    {
        if (_waits.Count == 0) return;
        double now = Now;
        foreach (var wait in _waits.ToList())
        {
            if (wait.Done || now - wait.CheckedAt < (wait.Kind == "url" ? UrlCheckSeconds : WaitCheckSeconds)) continue;
            double since = wait.CheckedAt < 0 ? dt : now - wait.CheckedAt;
            wait.CheckedAt = now;
            switch (wait.Kind)
            {
                case "pid":
                    if (!ProcessRunning(int.Parse(wait.Target, System.Globalization.CultureInfo.InvariantCulture))) Finish(wait, L.F("{0} has finished", wait.Label));
                    break;
                case "file":
                    if (!wait.Busy) CheckFile(wait, since);
                    break;
                case "url":
                    if (now - wait.Since > UrlGiveUpHours * 3600)
                    {
                        _waits.Remove(wait);
                        RecordWait(wait.Label, wait.StartUtc, wait.PlayedAtStart, false, wait.Kind);
                        Say(L.F("{0} never answered", wait.Label), L.F("gave up after {0} hours", UrlGiveUpHours), Red, "attention");
                    }
                    else if (!wait.Busy) CheckUrl(wait);
                    break;
            }
        }
    }

    static bool ProcessRunning(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (Exception)
        {
            return false; // gone, or no longer ours to look at
        }
    }

    /// <summary>One look at a waited-for file, on a worker thread: a network share that stopped answering must not freeze the overlay.</summary>
    async void CheckFile(Wait wait, double since)
    {
        wait.Busy = true;
        (bool Exists, long Size, bool Partial) seen;
        try
        {
            seen = await Task.Run(() =>
            {
                var info = new FileInfo(wait.Target);
                bool partial = new[] { ".crdownload", ".part", ".partial", ".download" }.Any(e => File.Exists(wait.Target + e) || Directory.Exists(wait.Target + e));
                return (info.Exists, info.Exists ? info.Length : 0L, partial);
            });
        }
        catch (Exception)
        {
            seen = (false, 0, false);
        }
        finally
        {
            wait.Busy = false;
        }
        if (!wait.Done && _waits.Contains(wait) && wait.File!.Step(since, seen.Exists, seen.Size, seen.Partial)) Finish(wait, L.F("{0} is ready", wait.Label));
    }

    async void CheckUrl(Wait wait)
    {
        wait.Busy = true;
        bool up = false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            using var response = await Http.GetAsync(wait.Target, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            up = response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            up = false; // not up yet
        }
        finally
        {
            wait.Busy = false;
        }
        if (up && !wait.Done && _waits.Contains(wait)) Finish(wait, L.F("{0} is up", wait.Label));
    }

    void Finish(Wait wait, string title)
    {
        wait.Done = true;
        _waits.Remove(wait);
        RecordWait(wait.Label, wait.StartUtc, wait.PlayedAtStart, wait.Kind == "url" ? true : null, wait.Kind);
        Say(title, L.F("after {0}", Hud.FormatWait(TimeSpan.FromSeconds(Now - wait.Since))), Green, "done", 0.9);
    }

    public void CancelWaits()
    {
        _waits.Clear();
        _w.RefreshTray();
    }

    public int WaitCount => _waits.Count;
}
