using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace DeskArcade.Office;

/// <summary>
/// A meeting from the player's calendar: its title and when it starts and ends, in UTC. Meetings stay on this PC: they
/// are shown on the scoreboard and in notices, and never sent over the LAN.
/// </summary>
public sealed record Meeting(string Title, DateTime Start, DateTime End)
{
    /// <summary>One occurrence of one meeting, for remembering which warnings were already given.</summary>
    public string Key => Start.Ticks.ToString(CultureInfo.InvariantCulture) + "|" + Title;

    /// <summary>The Teams, Zoom, Google Meet, Webex or Jitsi link to join it (see <see cref="MeetingLinks"/>), or null.</summary>
    public string? JoinUrl { get; init; }
}

/// <summary>
/// An iCalendar (.ics) file, as Outlook ("Publish a calendar" → ICS link), Google Calendar ("Secret address in iCal
/// format") and most other calendars publish one: its timed events, with their repeats (RRULE, RDATE), the dates left
/// out of a repeat (EXDATE), single occurrences that were moved or cancelled (RECURRENCE-ID), and time zones, from the
/// system's own list, from a table of the common IANA and Windows names, or from the file's VTIMEZONE blocks. All-day
/// events and cancelled ones are left out: they are not meetings.
/// </summary>
public sealed class IcsCalendar
{
    /// <summary>The local time zone that floating times (no zone, no Z) are in; tests set it.</summary>
    public TimeZoneInfo Local { get; set; } = TimeZoneInfo.Local;

    readonly List<Event> _events = new();
    readonly Zones _zones;

    IcsCalendar(Zones zones) => _zones = zones;

    /// <summary>How many timed events (repeating or not, moved occurrences included) the file holds.</summary>
    public int EventCount => _events.Count;

    // ------------------------------------------------------------------ reading the file

    sealed class Prop
    {
        public required string Name;
        public required Dictionary<string, string> Params;
        public required string Value;
        public string? Param(string key) => Params.TryGetValue(key, out var v) ? v : null;
    }

    sealed class Component
    {
        public required string Name;
        public readonly List<Prop> Props = new();
        public readonly List<Component> Children = new();
        public Prop? Get(string name) => Props.FirstOrDefault(p => p.Name == name);
        public IEnumerable<Prop> All(string name) => Props.Where(p => p.Name == name);
    }

    /// <summary>A date or date-time as written in the file: the wall clock, and whether it is UTC, in a zone, or a date.</summary>
    readonly record struct CalTime(DateTime Wall, bool Utc, string? Zone, bool IsDate);

    sealed class Event
    {
        public required string Uid;
        public required string Title;
        public required CalTime Start;
        public TimeSpan Duration;
        public RRule? Rule;
        public readonly List<CalTime> Extra = new();   // RDATE
        public readonly List<CalTime> Except = new();  // EXDATE
        public CalTime? RecurrenceId;
        public bool Cancelled;
        public string? JoinUrl;
    }

    /// <summary>Real calendars nest three deep (VCALENDAR, VTIMEZONE, STANDARD); anything deeper is skipped.</summary>
    const int MaxDepth = 8;

    /// <summary>Reads an .ics text. Never throws: lines, blocks and events it cannot read are skipped.</summary>
    public static IcsCalendar Parse(string text)
    {
        var root = new Component { Name = "ROOT" };
        var stack = new Stack<Component>();
        stack.Push(root);
        int skipped = 0; // BEGINs past MaxDepth, whose ENDs are skipped too
        foreach (string line in Unfold(text))
        {
            if (ParseLine(line) is not Prop prop) continue;
            if (prop.Name == "BEGIN")
            {
                if (skipped > 0 || stack.Count > MaxDepth)
                {
                    skipped++;
                    continue;
                }
                var child = new Component { Name = prop.Value.Trim().ToUpperInvariant() };
                stack.Peek().Children.Add(child);
                stack.Push(child);
            }
            else if (prop.Name == "END")
            {
                if (skipped > 0) skipped--;
                else if (stack.Count > 1) stack.Pop();
            }
            else if (skipped == 0) stack.Peek().Props.Add(prop);
        }

        var all = Flatten(root);
        var zones = new Zones();
        foreach (var tz in all.Where(c => c.Name == "VTIMEZONE"))
        {
            try
            {
                if (tz.Get("TZID")?.Value is { Length: > 0 } id) zones.Add(id, VTimezone.From(tz));
            }
            catch (Exception)
            {
                // a zone this reader cannot follow: its events fall back to local time
            }
        }

        var calendar = new IcsCalendar(zones);
        foreach (var ev in all.Where(c => c.Name == "VEVENT"))
        {
            try
            {
                if (calendar.ReadEvent(ev) is Event e) calendar._events.Add(e);
            }
            catch (Exception)
            {
                // one broken event does not cost the rest of the calendar
            }
        }
        return calendar;
    }

    /// <summary>Every block under <paramref name="root"/>, parents before their children.</summary>
    static List<Component> Flatten(Component root)
    {
        var all = new List<Component>();
        var todo = new Stack<Component>();
        for (int i = root.Children.Count - 1; i >= 0; i--) todo.Push(root.Children[i]);
        while (todo.Count > 0)
        {
            var c = todo.Pop();
            all.Add(c);
            for (int i = c.Children.Count - 1; i >= 0; i--) todo.Push(c.Children[i]);
        }
        return all;
    }

    Event? ReadEvent(Component ev)
    {
        if (ev.Get("DTSTART") is not Prop dtStart || ParseTime(dtStart) is not CalTime start || start.IsDate) return null;
        var e = new Event
        {
            Uid = ev.Get("UID")?.Value.Trim() ?? Guid.NewGuid().ToString("N"),
            Title = CleanTitle(Unescape(ev.Get("SUMMARY")?.Value ?? "")),
            Start = start,
            Cancelled = string.Equals(ev.Get("STATUS")?.Value.Trim(), "CANCELLED", StringComparison.OrdinalIgnoreCase),
            JoinUrl = MeetingLinks.Find(name => ev.All(name).Select(p => p.Value)),
        };
        if (ev.Get("DTEND") is Prop dtEnd && ParseTime(dtEnd) is CalTime end)
            e.Duration = ToUtc(end) - ToUtc(start);
        else if (ev.Get("DURATION")?.Value is string duration && ParseDuration(duration) is TimeSpan d)
            e.Duration = d;
        if (e.Duration < TimeSpan.Zero || e.Duration > TimeSpan.FromDays(2)) e.Duration = TimeSpan.Zero;
        if (ev.Get("RRULE")?.Value is string rule) e.Rule = RRule.Parse(rule);
        foreach (var p in ev.All("RDATE")) e.Extra.AddRange(ParseTimes(p));
        foreach (var p in ev.All("EXDATE")) e.Except.AddRange(ParseTimes(p));
        if (ev.Get("RECURRENCE-ID") is Prop rid && ParseTime(rid) is CalTime id) e.RecurrenceId = id;
        return e;
    }

    /// <summary>One line of at most 60 characters; empty when the event has no title (the overlay then says "Meeting").</summary>
    static string CleanTitle(string title)
    {
        var sb = new StringBuilder();
        foreach (char c in title.Trim()) sb.Append(char.IsControl(c) ? ' ' : c);
        string s = sb.ToString().Trim();
        return s.Length <= 60 ? s : s[..59].TrimEnd() + "…";
    }

    /// <summary>Joins folded lines (a line that starts with a space or a tab continues the one before).</summary>
    static IEnumerable<string> Unfold(string text)
    {
        var current = new StringBuilder();
        bool any = false;
        foreach (string raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (raw.Length > 0 && raw[0] is ' ' or '\t')
            {
                current.Append(raw, 1, raw.Length - 1);
                continue;
            }
            if (any) yield return current.ToString();
            current.Clear().Append(raw);
            any = true;
        }
        if (any) yield return current.ToString();
    }

    /// <summary>NAME;PARAM=value;PARAM="quoted:value":VALUE</summary>
    static Prop? ParseLine(string line)
    {
        int i = 0;
        while (i < line.Length && line[i] is not (';' or ':')) i++;
        if (i == 0 || i >= line.Length) return null;
        string name = line[..i].Trim().ToUpperInvariant();
        var ps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (i < line.Length && line[i] == ';')
        {
            int keyStart = ++i;
            while (i < line.Length && line[i] is not ('=' or ';' or ':')) i++;
            string key = line[keyStart..i].Trim();
            string value = "";
            if (i < line.Length && line[i] == '=')
            {
                i++;
                var sb = new StringBuilder();
                bool quoted = false;
                while (i < line.Length && (quoted || line[i] is not (';' or ':')))
                {
                    if (line[i] == '"') quoted = !quoted;
                    else sb.Append(line[i]);
                    i++;
                }
                value = sb.ToString();
            }
            if (key.Length > 0) ps[key] = value;
        }
        if (i >= line.Length || line[i] != ':') return null;
        return new Prop { Name = name, Params = ps, Value = line[(i + 1)..] };
    }

    /// <summary>TEXT values: \n, \, \; and \\.</summary>
    static string Unescape(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 >= s.Length)
            {
                sb.Append(s[i]);
                continue;
            }
            char next = s[++i];
            sb.Append(next is 'n' or 'N' ? ' ' : next);
        }
        return sb.ToString();
    }

    static CalTime? ParseTime(Prop p) => ParseTimes(p).Select(t => (CalTime?)t).FirstOrDefault();

    /// <summary>A DTSTART-like value, or a comma-separated list of them (EXDATE, RDATE).</summary>
    static IEnumerable<CalTime> ParseTimes(Prop p)
    {
        string? zone = p.Param("TZID");
        bool dateOnly = string.Equals(p.Param("VALUE"), "DATE", StringComparison.OrdinalIgnoreCase);
        foreach (string part in p.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(p.Param("VALUE"), "PERIOD", StringComparison.OrdinalIgnoreCase)) continue;
            if (ParseStamp(part, out var wall, out bool utc, out bool isDate))
                yield return new CalTime(wall, utc, utc ? null : zone, isDate || dateOnly);
        }
    }

    /// <summary>"20260929", "20260929T100000" or "20260929T100000Z".</summary>
    public static bool ParseStamp(string s, out DateTime wall, out bool utc, out bool isDate)
    {
        utc = s.EndsWith('Z') || s.EndsWith('z');
        if (utc) s = s[..^1];
        isDate = s.Length == 8;
        string format = isDate ? "yyyyMMdd" : s.Length == 13 ? "yyyyMMdd'T'HHmm" : "yyyyMMdd'T'HHmmss";
        bool ok = DateTime.TryParseExact(s, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out wall);
        wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        return ok;
    }

    /// <summary>An iCalendar DURATION: "PT30M", "PT1H30M", "P1D", "P1W", "-PT5M".</summary>
    public static TimeSpan? ParseDuration(string s)
    {
        s = s.Trim().ToUpperInvariant();
        int sign = 1;
        if (s.StartsWith('-')) { sign = -1; s = s[1..]; }
        else if (s.StartsWith('+')) s = s[1..];
        if (!s.StartsWith('P')) return null;
        var total = TimeSpan.Zero;
        bool time = false, any = false;
        int number = 0;
        bool digits = false;
        foreach (char c in s[1..])
        {
            if (char.IsAsciiDigit(c))
            {
                number = number * 10 + (c - '0');
                digits = true;
                if (number > 100_000) return null; // no meeting lasts that long, and bigger numbers overflow
                continue;
            }
            if (c == 'T') { time = true; continue; }
            if (!digits) return null;
            total += c switch
            {
                'W' when !time => TimeSpan.FromDays(7 * number),
                'D' when !time => TimeSpan.FromDays(number),
                'H' when time => TimeSpan.FromHours(number),
                'M' when time => TimeSpan.FromMinutes(number),
                'S' when time => TimeSpan.FromSeconds(number),
                _ => TimeSpan.MinValue,
            };
            if (total == TimeSpan.MinValue || total > TimeSpan.FromDays(3650)) return null;
            number = 0;
            digits = false;
            any = true;
        }
        return any && !digits ? total * sign : null;
    }

    // ------------------------------------------------------------------ occurrences

    DateTime ToUtc(CalTime t) => t.Utc ? t.Wall : _zones.ToUtc(t.Wall, t.Zone, Local);

    /// <summary>
    /// Every meeting that is on at some time between <paramref name="fromUtc"/> and <paramref name="toUtc"/> (one that
    /// started earlier and is still going counts), sorted by start. Repeats are expanded; moved occurrences replace
    /// the ones they move; cancelled ones and the dates left out of a repeat are skipped.
    /// </summary>
    public List<Meeting> Between(DateTime fromUtc, DateTime toUtc)
    {
        var result = new List<Meeting>();
        var moved = new HashSet<(string, DateTime)>();
        var links = new Dictionary<string, string>(); // a moved occurrence without a link keeps the series' link
        foreach (var e in _events)
        {
            if (e.RecurrenceId == null && e.JoinUrl != null) links.TryAdd(e.Uid, e.JoinUrl);
            try
            {
                if (e.RecurrenceId is CalTime rid) moved.Add((e.Uid, ToUtc(rid)));
            }
            catch (Exception)
            {
                // a date no zone can place: the occurrence it moved stays where it was
            }
        }

        foreach (var e in _events)
        {
            try
            {
                Occurrences(e, fromUtc, toUtc, moved, result, e.JoinUrl ?? links.GetValueOrDefault(e.Uid));
            }
            catch (Exception)
            {
                // a repeat or a zone this reader chokes on: that event is left out, the rest stay
            }
        }
        result.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : string.CompareOrdinal(a.Title, b.Title));
        return result;
    }

    /// <summary>One event's meetings in the window: a moved occurrence as it is, a repeat expanded less its exceptions.</summary>
    void Occurrences(Event e, DateTime fromUtc, DateTime toUtc, HashSet<(string, DateTime)> moved, List<Meeting> result, string? link)
    {
        if (e.RecurrenceId != null)
        {
            if (!e.Cancelled) Add(result, e, ToUtc(e.Start), fromUtc, toUtc, link);
            return;
        }
        if (e.Cancelled) return;
        var except = new HashSet<DateTime>(e.Except.Where(t => !t.IsDate).Select(ToUtc));
        var exceptDays = new HashSet<DateOnly>(e.Except.Where(t => t.IsDate).Select(t => DateOnly.FromDateTime(t.Wall)));
        var starts = new List<CalTime>();
        if (e.Rule is RRule rule)
        {
            // expand in the event's own wall clock, with a margin of two days for any zone's offset
            DateTime from = DateTime.SpecifyKind(fromUtc - e.Duration - TimeSpan.FromDays(2), DateTimeKind.Unspecified);
            DateTime to = DateTime.SpecifyKind(toUtc + TimeSpan.FromDays(2), DateTimeKind.Unspecified);
            foreach (var wall in rule.Expand(e.Start.Wall, from, to, w => ToUtc(e.Start with { Wall = w })))
                starts.Add(e.Start with { Wall = wall });
        }
        else starts.Add(e.Start);
        starts.AddRange(e.Extra.Where(t => !t.IsDate));
        var seen = new HashSet<DateTime>();
        foreach (var time in starts)
        {
            var start = ToUtc(time);
            if (!seen.Add(start) || except.Contains(start) || exceptDays.Contains(DateOnly.FromDateTime(time.Wall)) || moved.Contains((e.Uid, start)))
                continue;
            Add(result, e, start, fromUtc, toUtc, link);
        }
    }

    static void Add(List<Meeting> into, Event e, DateTime start, DateTime fromUtc, DateTime toUtc, string? link)
    {
        var end = start + e.Duration;
        if (start > toUtc || (end > start ? end <= fromUtc : start < fromUtc)) return;
        into.Add(new Meeting(e.Title, DateTime.SpecifyKind(start, DateTimeKind.Utc), DateTime.SpecifyKind(end, DateTimeKind.Utc)) { JoinUrl = link });
    }

    // ------------------------------------------------------------------ time zones

    /// <summary>A VTIMEZONE block: the STANDARD and DAYLIGHT onsets with the offset each one brings in.</summary>
    sealed class VTimezone
    {
        readonly List<(DateTime Start, TimeSpan From, TimeSpan To, RRule? Rule, List<DateTime> Dates)> _parts = new();
        readonly Dictionary<int, List<(DateTime Onset, TimeSpan To)>> _years = new(); // the onsets of the year before and the year

        public static VTimezone From(Component tz)
        {
            var z = new VTimezone();
            foreach (var part in tz.Children.Where(c => c.Name is "STANDARD" or "DAYLIGHT"))
            {
                if (part.Get("DTSTART")?.Value is not string s || !ParseStamp(s, out var start, out _, out _)) continue;
                if (ParseOffset(part.Get("TZOFFSETFROM")?.Value) is not TimeSpan from || ParseOffset(part.Get("TZOFFSETTO")?.Value) is not TimeSpan to)
                    continue;
                var rule = part.Get("RRULE")?.Value is string r ? RRule.Parse(r) : null;
                var dates = part.All("RDATE").SelectMany(p => ParseTimes(p)).Select(t => t.Wall).ToList();
                z._parts.Add((start, from, to, rule, dates));
            }
            return z;
        }

        /// <summary>"+0500", "-0430" or "+053000".</summary>
        static TimeSpan? ParseOffset(string? s)
        {
            if (s == null) return null;
            s = s.Trim();
            if (s.Length is not (5 or 7) || s[0] is not ('+' or '-') || !s[1..].All(char.IsAsciiDigit)) return null;
            int h = int.Parse(s[1..3], CultureInfo.InvariantCulture), m = int.Parse(s[3..5], CultureInfo.InvariantCulture);
            int sec = s.Length == 7 ? int.Parse(s[5..7], CultureInfo.InvariantCulture) : 0;
            var t = new TimeSpan(h, m, sec);
            return s[0] == '-' ? -t : t;
        }

        /// <summary>The offset in force at a wall-clock time: the one brought in by the latest onset at or before it.</summary>
        public TimeSpan? OffsetAt(DateTime wall)
        {
            if (_parts.Count == 0) return null;
            DateTime best = DateTime.MinValue;
            TimeSpan? offset = null;
            foreach (var (onset, to) in Onsets(wall.Year))
            {
                if (onset > wall || onset <= best) continue;
                best = onset;
                offset = to;
            }
            // before every onset in the file: the offset the earliest one moves away from
            return offset ?? _parts.OrderBy(p => p.Start).First().From;
        }

        /// <summary>Every clock change from the start of the year before <paramref name="year"/> to its end, cached.</summary>
        List<(DateTime Onset, TimeSpan To)> Onsets(int year)
        {
            if (_years.TryGetValue(year, out var known)) return known;
            var from = new DateTime(Math.Max(1, year - 1), 1, 1);
            var to = new DateTime(Math.Min(9998, year), 12, 31, 23, 59, 59);
            var list = new List<(DateTime, TimeSpan)>();
            foreach (var (start, _, offset, rule, dates) in _parts)
            {
                if (start <= to) list.Add((start, offset));
                if (rule != null) list.AddRange(rule.Expand(start, from, to, null).Select(o => (o, offset)));
                list.AddRange(dates.Select(d => (d, offset)));
            }
            if (_years.Count > 64) _years.Clear();
            _years[year] = list;
            return list;
        }
    }

    /// <summary>Turns a zone's wall clock into UTC: the system's zones, a name table, the file's VTIMEZONEs, else floating.</summary>
    sealed class Zones
    {
        readonly Dictionary<string, VTimezone> _files = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, TimeZoneInfo?> _system = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string id, VTimezone zone) => _files[Clean(id)] = zone;

        static string Clean(string id) => id.Trim().Trim('"');

        public DateTime ToUtc(DateTime wall, string? zone, TimeZoneInfo local)
        {
            wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
            if (zone == null) return Convert(wall, local);
            string id = Clean(zone);
            if (System(id) is TimeZoneInfo tz) return Convert(wall, tz);
            if (_files.TryGetValue(id, out var file) && file.OffsetAt(wall) is TimeSpan offset)
                return DateTime.SpecifyKind(wall - offset, DateTimeKind.Utc);
            return Convert(wall, local);
        }

        static DateTime Convert(DateTime wall, TimeZoneInfo tz)
        {
            // a wall time skipped by a clock change (02:30 on the spring-forward night) happens an hour later
            if (tz.IsInvalidTime(wall)) wall = wall.AddHours(1);
            return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(wall, tz), DateTimeKind.Utc);
        }

        /// <summary>
        /// The system zone for an id as written ("Europe/Berlin", "W. Europe Standard Time", Mozilla's
        /// "/mozilla.org/…/Europe/Berlin"), or through the name table when this system uses the other naming (with
        /// invariant globalization .NET cannot convert IANA and Windows names by itself).
        /// </summary>
        TimeZoneInfo? System(string id)
        {
            if (_system.TryGetValue(id, out var known)) return known;
            TimeZoneInfo? found = null;
            foreach (string candidate in Candidates(id))
            {
                try
                {
                    found = TimeZoneInfo.FindSystemTimeZoneById(candidate);
                    break;
                }
                catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
                {
                }
            }
            _system[id] = found;
            return found;
        }

        static IEnumerable<string> Candidates(string id)
        {
            yield return id;
            string[] parts = id.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 2) yield return string.Join('/', parts[^2..]);
            string iana = parts.Length > 2 ? string.Join('/', parts[^2..]) : id;
            if (ZoneNames.WindowsOf(iana) is string windows) yield return windows;
            if (ZoneNames.IanaOf(id) is string ianaName) yield return ianaName;
        }
    }
}

/// <summary>
/// The common zones in both namings (IANA "Europe/Berlin", Windows "W. Europe Standard Time"), after CLDR's
/// windowsZones table: .NET only converts between them with ICU, which this app runs without.
/// </summary>
public static class ZoneNames
{
    static readonly (string Iana, string Windows)[] Table =
    {
        ("Etc/UTC", "UTC"), ("UTC", "UTC"), ("Etc/GMT", "UTC"),
        ("America/New_York", "Eastern Standard Time"), ("America/Toronto", "Eastern Standard Time"), ("America/Detroit", "Eastern Standard Time"),
        ("America/Chicago", "Central Standard Time"), ("America/Winnipeg", "Central Standard Time"),
        ("America/Denver", "Mountain Standard Time"), ("America/Edmonton", "Mountain Standard Time"), ("America/Phoenix", "US Mountain Standard Time"),
        ("America/Los_Angeles", "Pacific Standard Time"), ("America/Vancouver", "Pacific Standard Time"),
        ("America/Anchorage", "Alaskan Standard Time"), ("Pacific/Honolulu", "Hawaiian Standard Time"),
        ("America/Halifax", "Atlantic Standard Time"), ("America/St_Johns", "Newfoundland Standard Time"),
        ("America/Mexico_City", "Central Standard Time (Mexico)"), ("America/Bogota", "SA Pacific Standard Time"),
        ("America/Lima", "SA Pacific Standard Time"), ("America/Caracas", "Venezuela Standard Time"),
        ("America/Santiago", "Pacific SA Standard Time"), ("America/Sao_Paulo", "E. South America Standard Time"),
        ("America/Argentina/Buenos_Aires", "Argentina Standard Time"), ("America/Buenos_Aires", "Argentina Standard Time"),
        ("Atlantic/Reykjavik", "Greenwich Standard Time"), ("Europe/London", "GMT Standard Time"), ("Europe/Dublin", "GMT Standard Time"),
        ("Europe/Lisbon", "GMT Standard Time"), ("Europe/Berlin", "W. Europe Standard Time"), ("Europe/Amsterdam", "W. Europe Standard Time"),
        ("Europe/Rome", "W. Europe Standard Time"), ("Europe/Vienna", "W. Europe Standard Time"), ("Europe/Stockholm", "W. Europe Standard Time"),
        ("Europe/Zurich", "W. Europe Standard Time"), ("Europe/Oslo", "W. Europe Standard Time"), ("Europe/Paris", "Romance Standard Time"),
        ("Europe/Madrid", "Romance Standard Time"), ("Europe/Brussels", "Romance Standard Time"), ("Europe/Copenhagen", "Romance Standard Time"),
        ("Europe/Warsaw", "Central European Standard Time"), ("Europe/Zagreb", "Central European Standard Time"),
        ("Europe/Prague", "Central Europe Standard Time"), ("Europe/Budapest", "Central Europe Standard Time"),
        ("Europe/Belgrade", "Central Europe Standard Time"), ("Europe/Athens", "GTB Standard Time"), ("Europe/Bucharest", "GTB Standard Time"),
        ("Europe/Helsinki", "FLE Standard Time"), ("Europe/Kyiv", "FLE Standard Time"), ("Europe/Kiev", "FLE Standard Time"),
        ("Europe/Riga", "FLE Standard Time"), ("Europe/Vilnius", "FLE Standard Time"), ("Europe/Tallinn", "FLE Standard Time"),
        ("Europe/Sofia", "FLE Standard Time"), ("Europe/Istanbul", "Turkey Standard Time"), ("Europe/Minsk", "Belarus Standard Time"),
        ("Europe/Moscow", "Russian Standard Time"), ("Europe/Samara", "Russia Time Zone 3"), ("Asia/Yekaterinburg", "Ekaterinburg Standard Time"),
        ("Asia/Tashkent", "West Asia Standard Time"), ("Asia/Samarkand", "West Asia Standard Time"), ("Asia/Dushanbe", "West Asia Standard Time"),
        ("Asia/Ashgabat", "West Asia Standard Time"), ("Asia/Almaty", "Central Asia Standard Time"), ("Asia/Bishkek", "Central Asia Standard Time"),
        ("Asia/Baku", "Azerbaijan Standard Time"), ("Asia/Tbilisi", "Georgian Standard Time"), ("Asia/Yerevan", "Caucasus Standard Time"),
        ("Asia/Dubai", "Arabian Standard Time"), ("Asia/Riyadh", "Arab Standard Time"), ("Asia/Baghdad", "Arabic Standard Time"),
        ("Asia/Tehran", "Iran Standard Time"), ("Asia/Kabul", "Afghanistan Standard Time"), ("Asia/Karachi", "Pakistan Standard Time"),
        ("Asia/Kolkata", "India Standard Time"), ("Asia/Calcutta", "India Standard Time"), ("Asia/Kathmandu", "Nepal Standard Time"),
        ("Asia/Dhaka", "Bangladesh Standard Time"), ("Asia/Bangkok", "SE Asia Standard Time"), ("Asia/Jakarta", "SE Asia Standard Time"),
        ("Asia/Ho_Chi_Minh", "SE Asia Standard Time"), ("Asia/Shanghai", "China Standard Time"), ("Asia/Hong_Kong", "China Standard Time"),
        ("Asia/Taipei", "Taipei Standard Time"), ("Asia/Singapore", "Singapore Standard Time"), ("Asia/Kuala_Lumpur", "Singapore Standard Time"),
        ("Asia/Manila", "Singapore Standard Time"), ("Asia/Tokyo", "Tokyo Standard Time"), ("Asia/Seoul", "Korea Standard Time"),
        ("Asia/Jerusalem", "Israel Standard Time"), ("Asia/Novosibirsk", "N. Central Asia Standard Time"),
        ("Asia/Vladivostok", "Vladivostok Standard Time"), ("Australia/Perth", "W. Australia Standard Time"),
        ("Australia/Adelaide", "Cen. Australia Standard Time"), ("Australia/Darwin", "AUS Central Standard Time"),
        ("Australia/Brisbane", "E. Australia Standard Time"), ("Australia/Sydney", "AUS Eastern Standard Time"),
        ("Australia/Melbourne", "AUS Eastern Standard Time"), ("Pacific/Auckland", "New Zealand Standard Time"),
        ("Africa/Cairo", "Egypt Standard Time"), ("Africa/Johannesburg", "South Africa Standard Time"), ("Africa/Lagos", "W. Central Africa Standard Time"),
        ("Africa/Nairobi", "E. Africa Standard Time"), ("Africa/Casablanca", "Morocco Standard Time"),
    };

    /// <summary>The Windows name for an IANA zone, or null when the table does not have it.</summary>
    public static string? WindowsOf(string iana)
    {
        foreach (var (i, w) in Table)
            if (string.Equals(i, iana, StringComparison.OrdinalIgnoreCase)) return w;
        return null;
    }

    /// <summary>The first IANA zone for a Windows name, or null when the table does not have it.</summary>
    public static string? IanaOf(string windows)
    {
        foreach (var (i, w) in Table)
            if (string.Equals(w, windows, StringComparison.OrdinalIgnoreCase)) return i;
        return null;
    }
}

/// <summary>
/// An RRULE, the repeat of a calendar event (and of a time zone's clock changes), expanded day by day in the event's
/// own wall clock: FREQ DAILY, WEEKLY, MONTHLY or YEARLY with INTERVAL, COUNT, UNTIL, BYDAY (with an ordinal such as
/// 2TU or -1FR), BYMONTHDAY, BYMONTH, BYSETPOS and WKST. Faster repeats (HOURLY and below) are not meetings and are
/// ignored.
/// </summary>
public sealed class RRule
{
    public enum Frequency { Daily, Weekly, Monthly, Yearly }

    const int MaxDays = 366 * 60; // how many days one expansion walks at most

    public Frequency Freq { get; private init; }
    public int Interval { get; private init; } = 1;
    public int? Count { get; private init; }
    DateTime? _until;
    bool _untilUtc;
    readonly List<(int Ordinal, DayOfWeek Day)> _byDay = new();
    readonly List<int> _byMonthDay = new(), _byMonth = new(), _bySetPos = new();
    DayOfWeek _weekStart = DayOfWeek.Monday;

    /// <summary>Reads "FREQ=WEEKLY;BYDAY=MO,WE;UNTIL=20261231T000000Z"; null when the rule is not one this reads.</summary>
    public static RRule? Parse(string text)
    {
        var parts = new Dictionary<string, string>();
        foreach (var p in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(p => p.Split('=', 2)))
            if (p.Length == 2) parts.TryAdd(p[0].ToUpperInvariant(), p[1].ToUpperInvariant()); // a part given twice: the first counts
        if (!parts.TryGetValue("FREQ", out var freq)) return null;
        Frequency? f = freq switch
        {
            "DAILY" => Frequency.Daily, "WEEKLY" => Frequency.Weekly, "MONTHLY" => Frequency.Monthly, "YEARLY" => Frequency.Yearly, _ => null,
        };
        if (f == null) return null;
        var rule = new RRule
        {
            Freq = f.Value,
            Interval = parts.TryGetValue("INTERVAL", out var iv) && int.TryParse(iv, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) && i > 0 ? Math.Min(i, 1000) : 1,
            Count = parts.TryGetValue("COUNT", out var c) && int.TryParse(c, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0 ? n : null,
        };
        if (parts.TryGetValue("UNTIL", out var until) && IcsCalendar.ParseStamp(until, out var u, out bool utc, out bool isDate))
        {
            rule._until = isDate ? u.AddDays(1).AddTicks(-1) : u;
            rule._untilUtc = utc && !isDate;
        }
        if (parts.TryGetValue("BYDAY", out var byDay))
        {
            foreach (string d in byDay.Split(','))
            {
                if (d.Length < 2 || Day(d[^2..]) is not DayOfWeek day) continue;
                int ordinal = d.Length > 2 && int.TryParse(d[..^2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int o) ? o : 0;
                rule._byDay.Add((ordinal, day));
            }
        }
        Numbers(parts, "BYMONTHDAY", rule._byMonthDay, -31, 31);
        Numbers(parts, "BYMONTH", rule._byMonth, 1, 12);
        Numbers(parts, "BYSETPOS", rule._bySetPos, -366, 366);
        if (parts.TryGetValue("WKST", out var wk) && Day(wk) is DayOfWeek ws) rule._weekStart = ws;
        return rule;
    }

    static void Numbers(Dictionary<string, string> parts, string key, List<int> into, int min, int max)
    {
        if (!parts.TryGetValue(key, out var value)) return;
        foreach (string s in value.Split(','))
            if (int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int v) && v != 0 && v >= min && v <= max)
                into.Add(v);
    }

    static DayOfWeek? Day(string s) => s switch
    {
        "MO" => DayOfWeek.Monday, "TU" => DayOfWeek.Tuesday, "WE" => DayOfWeek.Wednesday, "TH" => DayOfWeek.Thursday,
        "FR" => DayOfWeek.Friday, "SA" => DayOfWeek.Saturday, "SU" => DayOfWeek.Sunday, _ => null,
    };

    /// <summary>
    /// The wall-clock starts of the repeat from <paramref name="start"/> (its DTSTART, which counts as the first) that
    /// fall between <paramref name="from"/> and <paramref name="to"/>. <paramref name="toUtc"/> turns a wall time into
    /// UTC for an UNTIL given in UTC; without it UNTIL is read as wall time.
    /// </summary>
    public IEnumerable<DateTime> Expand(DateTime start, DateTime from, DateTime to, Func<DateTime, DateTime>? toUtc)
    {
        var first = DateOnly.FromDateTime(start);
        var time = start.TimeOfDay;
        // without COUNT a day's place in the repeat does not depend on the days before it, so start near the window
        var day = Count == null ? Max(first, DateOnly.FromDateTime(from).AddDays(-1)) : first;
        var last = DateOnly.FromDateTime(to);
        var cap = day.AddDays(MaxDays); // from where the walk starts: a time zone's rule can date from 1601
        int count = 0;
        var setCache = new Dictionary<(int, int), List<DateOnly>>();
        for (; day <= last && day <= cap; day = day.AddDays(1))
        {
            if (!Matches(day, first, setCache)) continue;
            var wall = day.ToDateTime(TimeOnly.FromTimeSpan(time));
            if (wall < start) continue;
            if (_until is DateTime until && (_untilUtc && toUtc != null ? toUtc(wall) : wall) > until) yield break;
            count++;
            if (Count is int max && count > max) yield break;
            if (wall >= from && wall <= to) yield return wall;
        }
    }

    static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;

    bool Matches(DateOnly d, DateOnly first, Dictionary<(int, int), List<DateOnly>> setCache)
    {
        if (_bySetPos.Count > 0 && Freq is Frequency.Monthly or Frequency.Yearly or Frequency.Weekly)
        {
            if (!InPeriod(d, first)) return false;
            var key = PeriodKey(d);
            if (!setCache.TryGetValue(key, out var set))
                setCache[key] = set = PeriodDays(d).Where(x => Candidate(x, first)).ToList();
            int index = set.IndexOf(d);
            if (index < 0) return false;
            return _bySetPos.Any(p => p > 0 ? index == p - 1 : index == set.Count + p);
        }
        return InPeriod(d, first) && Candidate(d, first);
    }

    /// <summary>Whether the day falls in a period that the INTERVAL keeps (every other week, every third month...).</summary>
    bool InPeriod(DateOnly d, DateOnly first)
    {
        if (d < first) return false;
        long index = Freq switch
        {
            Frequency.Daily => d.DayNumber - first.DayNumber,
            Frequency.Weekly => (WeekStart(d).DayNumber - WeekStart(first).DayNumber) / 7,
            Frequency.Monthly => (d.Year - first.Year) * 12L + d.Month - first.Month,
            _ => d.Year - first.Year,
        };
        return index % Interval == 0;
    }

    DateOnly WeekStart(DateOnly d) => d.AddDays(-(((int)d.DayOfWeek - (int)_weekStart + 7) % 7));

    (int, int) PeriodKey(DateOnly d) => Freq switch
    {
        Frequency.Weekly => (WeekStart(d).DayNumber, 0),
        Frequency.Monthly => (d.Year, d.Month),
        _ => (d.Year, 0),
    };

    IEnumerable<DateOnly> PeriodDays(DateOnly d)
    {
        DateOnly a, b;
        switch (Freq)
        {
            case Frequency.Weekly:
                a = WeekStart(d);
                b = a.AddDays(6);
                break;
            case Frequency.Monthly:
                a = new DateOnly(d.Year, d.Month, 1);
                b = a.AddMonths(1).AddDays(-1);
                break;
            default:
                a = new DateOnly(d.Year, 1, 1);
                b = new DateOnly(d.Year, 12, 31);
                break;
        }
        for (var x = a; x <= b; x = x.AddDays(1)) yield return x;
    }

    /// <summary>The BY* rules for one day, within a period the interval keeps.</summary>
    bool Candidate(DateOnly d, DateOnly first)
    {
        if (_byMonth.Count > 0 && !_byMonth.Contains(d.Month)) return false;
        switch (Freq)
        {
            case Frequency.Daily:
                return (_byMonthDay.Count == 0 || MonthDayMatches(d)) && (_byDay.Count == 0 || _byDay.Any(b => b.Day == d.DayOfWeek));
            case Frequency.Weekly:
                return _byDay.Count == 0 ? d.DayOfWeek == first.DayOfWeek : _byDay.Any(b => b.Day == d.DayOfWeek);
            case Frequency.Monthly:
                if (_byMonthDay.Count > 0) return MonthDayMatches(d) && (_byDay.Count == 0 || _byDay.Any(b => b.Day == d.DayOfWeek));
                if (_byDay.Count > 0) return _byDay.Any(b => DayMatches(d, b.Ordinal, b.Day, inMonth: true));
                return d.Day == first.Day;
            default:
                if (_byMonth.Count == 0 && _byMonthDay.Count == 0 && _byDay.Count == 0) return d.Month == first.Month && d.Day == first.Day;
                if (_byMonth.Count == 0 && _byDay.Count == 0 && d.Month != first.Month) return false;
                if (_byMonthDay.Count > 0) return MonthDayMatches(d) && (_byDay.Count == 0 || _byDay.Any(b => b.Day == d.DayOfWeek));
                if (_byDay.Count > 0) return _byDay.Any(b => DayMatches(d, b.Ordinal, b.Day, inMonth: _byMonth.Count > 0));
                return d.Day == first.Day;
        }
    }

    bool MonthDayMatches(DateOnly d)
    {
        int days = DateTime.DaysInMonth(d.Year, d.Month);
        return _byMonthDay.Any(md => md > 0 ? d.Day == md : d.Day == days + md + 1);
    }

    /// <summary>The weekday, and with an ordinal its place in the month (or year): 2 is the second, -1 the last.</summary>
    static bool DayMatches(DateOnly d, int ordinal, DayOfWeek day, bool inMonth)
    {
        if (d.DayOfWeek != day) return false;
        if (ordinal == 0) return true;
        if (inMonth)
        {
            int nth = (d.Day - 1) / 7 + 1;
            int fromEnd = (DateTime.DaysInMonth(d.Year, d.Month) - d.Day) / 7 + 1;
            return ordinal > 0 ? nth == ordinal : fromEnd == -ordinal;
        }
        int yearNth = (d.DayOfYear - 1) / 7 + 1;
        int yearDays = DateTime.IsLeapYear(d.Year) ? 366 : 365;
        int yearFromEnd = (yearDays - d.DayOfYear) / 7 + 1;
        return ordinal > 0 ? yearNth == ordinal : yearFromEnd == -ordinal;
    }
}
