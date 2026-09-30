using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace DeskArcade.Office;

/// <summary>
/// Where the day went (At work → My day, off by default): the program in front, noted once a minute while someone is
/// at the computer, as minutes per program per day. Only the program's name is kept, never a window title, and only
/// for the last <see cref="KeepDays"/> days, in apps.json on this PC. Browsers count as one "browser" and terminals as
/// one "terminal".
/// </summary>
public sealed class AppTime
{
    public const int KeepDays = 30;
    /// <summary>The keys that stand for a kind of program rather than one; shown translated.</summary>
    public const string Browser = "browser", Terminal = "terminal";

    static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    readonly SortedDictionary<string, Dictionary<string, int>> _days = new(StringComparer.Ordinal);
    readonly string _path;
    bool _dirty;

    AppTime(string path) => _path = path;

    /// <param name="path">Where apps.json lives; by default next to settings.json.</param>
    public static AppTime Load(string? path = null)
    {
        var log = new AppTime(path ?? Path.Combine(Settings.DataDirectory, "apps.json"));
        try
        {
            if (File.Exists(log._path) && JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(File.ReadAllText(log._path), Json) is { } days)
                foreach (var (day, apps) in days)
                    if (apps != null && DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                        log._days[day] = apps.Where(kv => kv.Value > 0 && !string.IsNullOrWhiteSpace(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // a damaged file: start again
        }
        return log;
    }

    /// <summary>A log that is never saved, for tests.</summary>
    public static AppTime InMemory() => new("");

    static string Key(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The days kept, oldest first.</summary>
    public IEnumerable<DateOnly> Days => _days.Keys.Select(k => DateOnly.ParseExact(k, "yyyy-MM-dd", CultureInfo.InvariantCulture));

    /// <summary>A minute of <paramref name="app"/> in front on <paramref name="day"/>; days more than <see cref="KeepDays"/> before it are forgotten.</summary>
    public void AddMinute(DateOnly day, string app, int minutes = 1)
    {
        app = app.Trim();
        if (app.Length == 0 || minutes <= 0) return;
        string key = Key(day);
        if (!_days.TryGetValue(key, out var apps)) _days[key] = apps = new Dictionary<string, int>(StringComparer.Ordinal);
        apps[app] = (apps.TryGetValue(app, out int m) ? m : 0) + minutes;
        string oldest = Key(day.AddDays(-KeepDays + 1));
        foreach (string old in _days.Keys.Where(k => string.CompareOrdinal(k, oldest) < 0).ToList()) _days.Remove(old);
        _dirty = true;
    }

    /// <summary>Minutes per program on <paramref name="day"/>.</summary>
    public IReadOnlyDictionary<string, int> OnDay(DateOnly day) =>
        _days.TryGetValue(Key(day), out var apps) ? apps : new Dictionary<string, int>();

    /// <summary>The programs of a day, most minutes first (by name on a tie); the rest past <paramref name="top"/> summed under null.</summary>
    public List<(string? App, int Minutes)> Top(DateOnly day, int top)
    {
        var sorted = OnDay(day).OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        var list = sorted.Take(top).Select(kv => ((string?)kv.Key, kv.Value)).ToList();
        int rest = sorted.Skip(top).Sum(kv => kv.Value);
        if (rest > 0) list.Add((null, rest));
        return list;
    }

    /// <summary>"4:10": hours and minutes.</summary>
    public static string HoursMinutes(int minutes) => $"{minutes / 60}:{minutes % 60:00}";

    /// <summary>
    /// Every day kept as CSV for a timesheet: date, program, minutes, hours (two decimals), one row per program per
    /// day, oldest day first and the most minutes first within a day. <paramref name="name"/> shows a key as a name.
    /// </summary>
    public string Csv(Func<string, string>? name = null)
    {
        name ??= s => s;
        var sb = new StringBuilder("date,app,minutes,hours\r\n");
        foreach (var (day, apps) in _days)
            foreach (var (app, minutes) in apps.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal))
                sb.Append(day).Append(',').Append(CsvField(name(app))).Append(',')
                    .Append(minutes.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append((minutes / 60.0).ToString("0.00", CultureInfo.InvariantCulture)).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>A CSV field, quoted when it holds a comma, a quote or a line break (RFC 4180).</summary>
    public static string CsvField(string text) =>
        text.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;

    public void Save()
    {
        if (!_dirty || _path.Length == 0) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_days, Json));
            _dirty = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }

    // ------------------------------------------------------------------ names

    static readonly Dictionary<string, string> Names = BuildNames();

    static Dictionary<string, string> BuildNames()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Map(string name, params string[] processes)
        {
            foreach (string p in processes) names[p] = name;
        }
        Map("Code", "code", "code - insiders", "code-insiders", "visual studio code");
        Map("VSCodium", "codium", "vscodium");
        Map("Cursor", "cursor");
        Map("Windsurf", "windsurf");
        Map("Zed", "zed", "zed-editor");
        Map("Visual Studio", "devenv");
        Map("Rider", "rider64", "rider");
        Map("IntelliJ IDEA", "idea64", "idea", "intellij idea");
        Map("PyCharm", "pycharm64", "pycharm");
        Map("WebStorm", "webstorm64", "webstorm");
        Map("GoLand", "goland64", "goland");
        Map("CLion", "clion64", "clion");
        Map("DataGrip", "datagrip64", "datagrip");
        Map("PhpStorm", "phpstorm64", "phpstorm");
        Map("Android Studio", "studio64", "android studio");
        Map("Xcode", "xcode");
        Map("Sublime Text", "sublime_text", "sublime text");
        Map("Notepad++", "notepad++");
        Map("Notepad", "notepad");
        Map("Vim", "vim", "gvim", "nvim", "nvim-qt", "neovide");
        Map("Emacs", "emacs");
        Map(Browser, "chrome", "google chrome", "google-chrome", "chromium", "chromium-browser", "msedge", "microsoft edge",
            "firefox", "firefox-esr", "firefox-bin", "brave", "brave browser", "opera", "vivaldi", "vivaldi-bin", "safari", "arc",
            "iexplore", "librewolf", "waterfox", "zen", "yandex", "browser");
        Map(Terminal, "windowsterminal", "wt", "cmd", "powershell", "pwsh", "conhost", "openconsole", "mintty", "gnome-terminal",
            "gnome-terminal-server", "gnome-terminal-", "kgx", "ptyxis", "konsole", "xterm", "terminal", "iterm2", "alacritty",
            "wezterm", "wezterm-gui", "kitty", "tilix", "terminator", "warp", "ghostty", "hyper", "tabby");
        Map("Teams", "ms-teams", "teams", "msteams", "microsoft teams", "microsoft teams (work or school)", "teams-for-linux");
        Map("Slack", "slack");
        Map("Zoom", "zoom", "zoom.us", "zoom meetings");
        Map("Telegram", "telegram", "telegram desktop");
        Map("Discord", "discord");
        Map("Skype", "skype");
        Map("WhatsApp", "whatsapp");
        Map("Webex", "webex", "ciscocollabhost");
        Map("Outlook", "outlook", "olk", "hxoutlook", "microsoft outlook");
        Map("Thunderbird", "thunderbird");
        Map("Mail", "mail");
        Map("Word", "winword", "microsoft word");
        Map("Excel", "excel", "microsoft excel");
        Map("PowerPoint", "powerpnt", "microsoft powerpoint");
        Map("OneNote", "onenote", "microsoft onenote");
        Map("LibreOffice", "soffice", "soffice.bin");
        Map("Explorer", "explorer");
        Map("Files", "nautilus");
        Map("Finder", "finder");
        Map("Dolphin", "dolphin");
        Map("Figma", "figma");
        Map("Postman", "postman");
        Map("Obsidian", "obsidian");
        Map("Notion", "notion");
        Map("Spotify", "spotify");
        Map("Desk Arcade", "deskarcade");
        return names;
    }

    /// <summary>What is on screen when nobody is really working: the lock screen, a screen saver. Not counted.</summary>
    static readonly HashSet<string> NotWork = new(StringComparer.OrdinalIgnoreCase) { "lockapp", "logonui", "screensaverengine", "idle", "system" };

    /// <summary>
    /// The name a process is summed under: a friendly name for common programs ("Code", "Teams"), "browser" or
    /// "terminal" for any browser or terminal, the app bundle's name on macOS when the process is a bare Electron
    /// ("Visual Studio Code"), and the process name as it is otherwise. Null for the lock screen and nothing at all.
    /// </summary>
    public static string? Friendly(string? processName, string? path = null)
    {
        string name = (processName ?? "").Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (path != null && (name.Length == 0 || name.Equals("electron", StringComparison.OrdinalIgnoreCase)) && BundleName(path) is string bundle) name = bundle;
        if (name.Length == 0 || NotWork.Contains(name)) return null;
        if (Names.TryGetValue(name, out string? friendly)) return friendly;
        if (name.StartsWith("gnome-terminal", StringComparison.OrdinalIgnoreCase)) return Terminal; // /proc cuts names at 15 characters
        return name;
    }

    /// <summary>"Visual Studio Code" for ".../Visual Studio Code.app/Contents/MacOS/Electron"; null outside an app bundle.</summary>
    public static string? BundleName(string path)
    {
        int app = path.IndexOf(".app/", StringComparison.OrdinalIgnoreCase);
        if (app < 0) return null;
        int start = path.LastIndexOf('/', app) + 1;
        string name = path[start..app];
        return name.Length > 0 ? name : null;
    }
}
