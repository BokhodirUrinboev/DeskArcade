using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DeskArcade.Office;

/// <summary>The video-call services a meeting's Join button can open.</summary>
public enum MeetingService { Teams, Zoom, Meet, Webex, Jitsi }

/// <summary>
/// Finds the link to join a meeting in a calendar event: Microsoft Teams, Zoom (zoomgov too), Google Meet, Webex or
/// Jitsi (meet.jit.si, 8x8.vc and self-hosted servers named jitsi…). It looks in the event's properties in a fixed
/// order, the location first, then the notes (plain and HTML), then the link fields Outlook and Google add, and takes
/// the first link that is one of these; help pages, dial-in numbers and meeting options are skipped. Links wrapped by
/// Outlook's Safe Links or Google's redirect are unwrapped.
/// </summary>
public static class MeetingLinks
{
    /// <summary>Where to look, in order: the first property holding a meeting link wins.</summary>
    public static readonly string[] Properties =
    {
        "LOCATION", "DESCRIPTION", "X-ALT-DESC", "URL", "X-MICROSOFT-SKYPETEAMSMEETINGURL",
        "X-MICROSOFT-ONLINEMEETINGCONFLINK", "X-GOOGLE-CONFERENCE", "CONFERENCE",
    };

    const int MaxLength = 2048;

    static readonly Regex MeetCode = new(@"^/[a-z]{3}-[a-z]{4}-[a-z]{3}(/|$)", RegexOptions.CultureInvariant);
    static readonly Regex Room = new(@"^[A-Za-z0-9][A-Za-z0-9._~%\-]*$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The join link of an event, given how to read each of its properties by name (their raw values, still escaped as
    /// in the file); null when none of them holds one.
    /// </summary>
    public static string? Find(Func<string, IEnumerable<string>> valuesOf)
    {
        foreach (string name in Properties)
            foreach (string value in valuesOf(name))
                if (FirstIn(value) is string link) return link;
        return null;
    }

    /// <summary>The first meeting link in a property value (text or HTML, iCalendar escapes and all), or null.</summary>
    public static string? FirstIn(string value)
    {
        string text = Html(Text(value));
        int at = 0;
        while (at < text.Length)
        {
            int start = text.IndexOf("http", at, StringComparison.OrdinalIgnoreCase);
            if (start < 0) break;
            int end = start;
            while (end < text.Length && !Ends(text[end])) end++;
            at = Math.Max(end, start + 4);
            string candidate = text[start..end].TrimEnd('.', ',', ';', ':', '!', '?', '\'', '*');
            if (candidate.Length > MaxLength) continue;
            if (Classify(candidate) is not null) return Unwrap(candidate) ?? candidate;
        }
        return null;
    }

    /// <summary>Which service a link joins, or null when it is not a meeting link (a help page, a dial-in number, a map).</summary>
    public static MeetingService? Classify(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return null;
        if (Unwrap(link) is string inner) return inner == link ? null : Classify(inner);
        string host = uri.Host.ToLowerInvariant().TrimEnd('.');
        string path = uri.AbsolutePath;
        string lower = path.ToLowerInvariant();

        if (Under(host, "teams.microsoft.com") || Under(host, "teams.microsoft.us") || Under(host, "teams.live.com"))
        {
            if (host.StartsWith("dialin.", StringComparison.Ordinal)) return null;
            if (lower.StartsWith("/l/meetup-join/", StringComparison.Ordinal) || lower.StartsWith("/meet/", StringComparison.Ordinal)
                || lower.StartsWith("/l/meet/", StringComparison.Ordinal)
                || lower.StartsWith("/dl/launcher/", StringComparison.Ordinal) && uri.Query.Contains("meetup-join", StringComparison.OrdinalIgnoreCase))
                return MeetingService.Teams;
            return null;
        }
        if (Under(host, "zoom.us") || Under(host, "zoomgov.com") || Under(host, "zoom.com"))
        {
            if (host.StartsWith("support.", StringComparison.Ordinal) || host.StartsWith("explore.", StringComparison.Ordinal)) return null;
            foreach (string p in new[] { "/j/", "/my/", "/s/", "/w/", "/wc/" })
                if (lower.StartsWith(p, StringComparison.Ordinal) && lower.Length > p.Length) return MeetingService.Zoom;
            return null;
        }
        if (host == "meet.google.com")
            return MeetCode.IsMatch(lower) || lower.StartsWith("/lookup/", StringComparison.Ordinal) ? MeetingService.Meet : null;
        if (Under(host, "webex.com"))
        {
            if (host.StartsWith("help.", StringComparison.Ordinal) || host.StartsWith("www.", StringComparison.Ordinal) || host == "webex.com") return null;
            if (lower.Contains("j.php", StringComparison.Ordinal) || lower.StartsWith("/meet/", StringComparison.Ordinal)
                || lower.StartsWith("/join/", StringComparison.Ordinal) || lower.Contains("/joinservice/", StringComparison.Ordinal)
                || lower.StartsWith("/wbxmjs/", StringComparison.Ordinal) || lower.StartsWith("/webappng/sites/", StringComparison.Ordinal))
                return MeetingService.Webex;
            return null;
        }
        // Jitsi: the public server, 8x8's hosted one (a tenant, then the room), and servers of its own named jitsi…
        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (host == "meet.jit.si") return segments.Length == 1 && IsRoom(segments[0]) ? MeetingService.Jitsi : null;
        if (Under(host, "8x8.vc")) return segments.Length is 1 or 2 && segments.All(IsRoom) ? MeetingService.Jitsi : null;
        if (host.Split('.').Any(label => label.StartsWith("jitsi", StringComparison.Ordinal)))
            return segments.Length == 1 && IsRoom(segments[0]) ? MeetingService.Jitsi : null;
        return null;
    }

    /// <summary>The name a Join button can show for the service.</summary>
    public static string Name(MeetingService service) => service switch
    {
        MeetingService.Teams => "Teams", MeetingService.Zoom => "Zoom", MeetingService.Meet => "Google Meet",
        MeetingService.Webex => "Webex", _ => "Jitsi",
    };

    /// <summary>A room name, not a page of the server (config.js, static/…, a help page).</summary>
    static bool IsRoom(string segment) =>
        Room.IsMatch(segment) && !segment.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
        && !segment.EndsWith(".html", StringComparison.OrdinalIgnoreCase) && !segment.EndsWith(".css", StringComparison.OrdinalIgnoreCase)
        && segment.ToLowerInvariant() is not ("static" or "libs" or "images" or "sounds" or "css" or "fonts" or "lang" or "http-bind" or "xmpp-websocket");

    static bool Under(string host, string domain) => host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);

    /// <summary>
    /// The link inside an Outlook Safe Links or Google redirect wrapper; the link itself when it is such a wrapper but
    /// holds no link; null when it is no wrapper.
    /// </summary>
    static string? Unwrap(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)) return null;
        string host = uri.Host.ToLowerInvariant();
        string? key = Under(host, "safelinks.protection.outlook.com") ? "url"
            : (host is "www.google.com" or "google.com") && uri.AbsolutePath == "/url" ? "q"
            : null;
        if (key == null) return null;
        // the query as written (Uri would tidy its escapes), without a #fragment
        int q = link.IndexOf('?');
        string query = q < 0 ? "" : link[(q + 1)..];
        if (query.IndexOf('#') is int hash and >= 0) query = query[..hash];
        foreach (string pair in query.Split('&'))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0 || !pair[..eq].Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            string inner = Uri.UnescapeDataString(pair[(eq + 1)..]).Trim();
            return inner.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? inner : link;
        }
        return link;
    }

    /// <summary>What ends a link in running text: spaces, brackets and quotes, and the separators of an HTML tag.</summary>
    static bool Ends(char c) => char.IsWhiteSpace(c) || c is '<' or '>' or '"' or '\'' or '`' or '(' or ')' or '[' or ']' or '|' or '\\';

    /// <summary>
    /// A TEXT value's escapes undone for finding links: a line break is a space, and an escaped comma or semicolon (a
    /// separator between two locations, "Room 4\, https://…") ends a link, so it gets spaces around it; except the
    /// semicolon that closes an HTML entity ("&amp;amp\;" in an HTML description), which stays part of the link.
    /// </summary>
    static string Text(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new StringBuilder(s.Length + 8);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 >= s.Length)
            {
                sb.Append(s[i]);
                continue;
            }
            char next = s[++i];
            if (next is 'n' or 'N') sb.Append(' ');
            else if (next == ';' && EndsInEntity(sb)) sb.Append(';');
            else if (next is ',' or ';') sb.Append(' ').Append(next).Append(' ');
            else sb.Append(next);
        }
        return sb.ToString();
    }

    /// <summary>Whether the text so far ends in "&amp;amp", "&amp;#38" or the like, waiting for its semicolon.</summary>
    static bool EndsInEntity(StringBuilder sb)
    {
        for (int i = sb.Length - 1, n = 0; i >= 0 && n <= 8; i--, n++)
        {
            if (sb[i] == '&') return n > 0;
            if (!char.IsAsciiLetterOrDigit(sb[i]) && sb[i] != '#') return false;
        }
        return false;
    }

    /// <summary>The few HTML entities a link in an HTML description can hold (&amp;amp; in a query above all).</summary>
    static string Html(string s)
    {
        if (!s.Contains('&')) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            int semi = s[i] == '&' ? s.IndexOf(';', i + 1, Math.Min(10, s.Length - i - 1)) : -1;
            if (semi < 0)
            {
                sb.Append(s[i]);
                continue;
            }
            string entity = s[(i + 1)..semi];
            char? c = entity switch
            {
                "amp" => '&', "lt" => '<', "gt" => '>', "quot" => '"', "apos" => '\'', "nbsp" => ' ',
                _ when entity.StartsWith("#x", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(entity[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hex) && hex is > 0 and < 0xD800 => (char)hex,
                _ when entity.StartsWith('#')
                    && int.TryParse(entity[1..], NumberStyles.None, CultureInfo.InvariantCulture, out int dec) && dec is > 0 and < 0xD800 => (char)dec,
                _ => null,
            };
            if (c == null)
            {
                sb.Append(s[i]);
                continue;
            }
            sb.Append(c.Value);
            i = semi;
        }
        return sb.ToString();
    }
}
