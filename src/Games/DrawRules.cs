using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace DeskArcade.Games;

/// <summary>A word to draw, in English, Russian and Uzbek; each may list other accepted answers after "|" ("dog|puppy").</summary>
public sealed record DrawWord(string En, string Ru, string Uz, string Tag = "")
{
    /// <summary>Every accepted answer in every language, normalised (see <see cref="DrawRules.Normalize"/>).</summary>
    public IEnumerable<string> Answers => new[] { En, Ru, Uz }.SelectMany(s => s.Split('|')).Select(DrawRules.Normalize).Where(a => a.Length > 0).Distinct();

    /// <summary>The word as shown in a language ("en", "ru", "uz"): its first form.</summary>
    public string In(string code) => (code switch { "ru" => Ru, "uz" => Uz, _ => En }).Split('|')[0];
}

/// <summary>
/// A stroke of the drawing: its colour and brush (indexes into the game's palette) and its points, packed as bytes: x
/// and y each 0–255 across the board. On the wire it is "colour.size.base64", small enough that a whole drawing fits
/// in every view the host sends.
/// </summary>
public sealed record DrawStroke(int Color, int Size, byte[] Points)
{
    public int Count => Points.Length / 2;

    public string Encode() => string.Create(CultureInfo.InvariantCulture, $"{Color}.{Size}.{Convert.ToBase64String(Points)}");

    public static DrawStroke? Decode(string text)
    {
        var f = text.Split('.', 3);
        if (f.Length != 3 || !int.TryParse(f[0], out int color) || !int.TryParse(f[1], out int size)) return null;
        try
        {
            var points = Convert.FromBase64String(f[2]);
            return points.Length is >= 2 and <= DrawRules.MaxPointsPerStroke * 2 && points.Length % 2 == 0
                ? new DrawStroke(Math.Clamp(color, 0, 15), Math.Clamp(size, 0, 3), points) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// A stroke from points on a board of <paramref name="w"/> × <paramref name="h"/>: scaled to 0–255, with the points
    /// that add nothing dropped (Ramer–Douglas–Peucker, within <paramref name="tolerance"/> of the line), and cut to
    /// <see cref="DrawRules.MaxPointsPerStroke"/>.
    /// </summary>
    public static DrawStroke FromPoints(IReadOnlyList<(double X, double Y)> raw, double w, double h, int color, int size, double tolerance = 1.2)
    {
        var scaled = raw.Select(p => (X: Math.Clamp(p.X / Math.Max(1, w) * 255, 0, 255), Y: Math.Clamp(p.Y / Math.Max(1, h) * 255, 0, 255))).ToList();
        var kept = Simplify(scaled, tolerance);
        if (kept.Count > DrawRules.MaxPointsPerStroke) kept = kept.Take(DrawRules.MaxPointsPerStroke).ToList();
        var bytes = new byte[kept.Count * 2];
        for (int i = 0; i < kept.Count; i++)
        {
            bytes[i * 2] = (byte)Math.Round(kept[i].X);
            bytes[i * 2 + 1] = (byte)Math.Round(kept[i].Y);
        }
        return new DrawStroke(color, size, bytes);
    }

    /// <summary>The points of a polyline that matter: Ramer–Douglas–Peucker, keeping both ends.</summary>
    public static List<(double X, double Y)> Simplify(IReadOnlyList<(double X, double Y)> points, double tolerance)
    {
        if (points.Count <= 2) return points.ToList();
        var keep = new bool[points.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int A, int B)>();
        stack.Push((0, points.Count - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            double best = 0;
            int index = -1;
            for (int i = a + 1; i < b; i++)
            {
                double d = Distance(points[i], points[a], points[b]);
                if (d > best)
                {
                    best = d;
                    index = i;
                }
            }
            if (index < 0 || best <= tolerance) continue;
            keep[index] = true;
            stack.Push((a, index));
            stack.Push((index, b));
        }
        return points.Where((_, i) => keep[i]).ToList();
    }

    static double Distance((double X, double Y) p, (double X, double Y) a, (double X, double Y) b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, len = dx * dx + dy * dy;
        if (len < 1e-9) return Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));
        double t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len, 0, 1);
        double x = a.X + t * dx - p.X, y = a.Y + t * dy - p.Y;
        return Math.Sqrt(x * x + y * y);
    }
}

/// <summary>What a guess was.</summary>
public enum GuessKind { Wrong, Close, Right }

/// <summary>A line in the guess feed: who, what they typed (hidden for a right answer), and how it went.</summary>
public sealed record GuessLine(int Seat, string Text, GuessKind Kind);

/// <summary>
/// Draw &amp; Guess, free of UI: the players take turns to draw. The drawer picks one of three words, then has
/// <see cref="DrawSeconds"/> to draw it while the others type guesses. A right guess scores more the sooner it comes (and
/// the first one a little extra), and the drawer scores for every player who gets it. A guess one letter off is
/// "close" (only its guesser is told). Letters of the word show as hints as the time runs down. The turn ends when
/// everyone has it or the time is up; after everyone has drawn <see cref="Rounds"/> times, the most points win.
/// Answers count in any of the three languages. Time only passes in <see cref="Step"/>.
/// </summary>
public sealed class DrawRules
{
    public const double ChooseSeconds = 12, DrawSeconds = 80, RevealSeconds = 5;
    public const int Options = 3, MaxPointsPerStroke = 400, MaxPointsPerDrawing = 5000, FeedLength = 14;
    public const int GuessBase = 40, GuessSpeed = 60, FirstBonus = 10, DrawerPoints = 15;

    public enum Stage { Choosing, Drawing, Reveal, Over }

    readonly Random _rng;
    readonly IReadOnlyList<DrawWord> _words;
    readonly Func<int, bool> _cpu;
    readonly Func<int, bool> _drawable;
    readonly List<int> _used = new();

    public int Players { get; }
    public int Rounds { get; }
    public Stage Phase { get; private set; } = Stage.Choosing;
    /// <summary>The seat drawing now, and the turn number (0 up to Players × Rounds − 1).</summary>
    public int Drawer { get; private set; }
    public int TurnNumber { get; private set; }
    public int[] Choices { get; private set; } = Array.Empty<int>();
    /// <summary>The word being drawn (an index into the words), or −1 while it is still being chosen.</summary>
    public int Word { get; private set; } = -1;
    public double TimeLeft { get; private set; } = ChooseSeconds;
    public int[] Scores { get; }
    /// <summary>Points each seat took this turn.</summary>
    public int[] TurnPoints { get; }
    public bool[] Guessed { get; }
    public List<DrawStroke> Strokes { get; } = new();
    public List<GuessLine> Feed { get; } = new();
    /// <summary>How many letters of the word are showing as a hint (0, 1 or 2).</summary>
    public int Hints { get; private set; }
    public int Version { get; private set; }
    public bool Over => Phase == Stage.Over;
    public int PointsDrawn => Strokes.Sum(s => s.Count);
    public DrawWord? Current => Word >= 0 ? _words[Word] : null;

    /// <param name="cpu">Whether a seat is the computer's (it only draws words it has a picture of).</param>
    /// <param name="drawable">Whether the computer has a picture of a word.</param>
    public DrawRules(int players, IReadOnlyList<DrawWord> words, Random rng, int rounds, Func<int, bool> cpu, Func<int, bool> drawable)
    {
        Players = players;
        Rounds = Math.Max(1, rounds);
        _words = words;
        _rng = rng;
        _cpu = cpu;
        _drawable = drawable;
        Scores = new int[players];
        TurnPoints = new int[players];
        Guessed = new bool[players];
        StartTurn(0);
    }

    void StartTurn(int turn)
    {
        TurnNumber = turn;
        Drawer = turn % Players;
        Phase = Stage.Choosing;
        TimeLeft = ChooseSeconds;
        Word = -1;
        Hints = 0;
        Strokes.Clear();
        Array.Fill(Guessed, false);
        Array.Clear(TurnPoints);
        Choices = PickChoices(_cpu(Drawer));
        Version++;
    }

    /// <summary>Three words not used yet this game; a computer drawer gets words it can draw.</summary>
    int[] PickChoices(bool computer)
    {
        var pool = Enumerable.Range(0, _words.Count).Where(i => !_used.Contains(i) && (!computer || _drawable(i))).ToList();
        if (pool.Count < Options) pool = Enumerable.Range(0, _words.Count).Where(i => !computer || _drawable(i)).ToList();
        var picked = new List<int>();
        while (picked.Count < Options && pool.Count > 0)
        {
            int k = _rng.Next(pool.Count);
            picked.Add(pool[k]);
            pool.RemoveAt(k);
        }
        return picked.ToArray();
    }

    /// <summary>The drawer picks one of the three words (0–2).</summary>
    public bool Choose(int seat, int option)
    {
        if (Phase != Stage.Choosing || seat != Drawer || option < 0 || option >= Choices.Length) return false;
        Word = Choices[option];
        _used.Add(Word);
        Phase = Stage.Drawing;
        TimeLeft = DrawSeconds;
        Version++;
        return true;
    }

    /// <summary>A finished stroke from the drawer; false when it isn't theirs to draw or the drawing is full.</summary>
    public bool AddStroke(int seat, DrawStroke stroke)
    {
        if (Phase != Stage.Drawing || seat != Drawer || PointsDrawn + stroke.Count > MaxPointsPerDrawing) return false;
        Strokes.Add(stroke);
        Version++;
        return true;
    }

    public bool Undo(int seat)
    {
        if (Phase != Stage.Drawing || seat != Drawer || Strokes.Count == 0) return false;
        Strokes.RemoveAt(Strokes.Count - 1);
        Version++;
        return true;
    }

    public bool Clear(int seat)
    {
        if (Phase != Stage.Drawing || seat != Drawer || Strokes.Count == 0) return false;
        Strokes.Clear();
        Version++;
        return true;
    }

    /// <summary>A guess from a player who is not drawing and hasn't got it yet; null when it doesn't count.</summary>
    public GuessKind? Guess(int seat, string text)
    {
        if (Phase != Stage.Drawing || seat == Drawer || seat < 0 || seat >= Players || Guessed[seat] || Current is not { } word) return null;
        string guess = Normalize(text);
        if (guess.Length == 0) return null;
        var kind = word.Answers.Contains(guess) ? GuessKind.Right
            : word.Answers.Any(a => a.Length >= 4 && Levenshtein(a, guess) == 1) ? GuessKind.Close : GuessKind.Wrong;
        if (kind == GuessKind.Right)
        {
            bool first = !Guessed.Where((_, s) => s != Drawer).Any(g => g);
            int points = GuessBase + (int)Math.Round(GuessSpeed * TimeLeft / DrawSeconds) + (first ? FirstBonus : 0);
            Guessed[seat] = true;
            Scores[seat] += points;
            TurnPoints[seat] += points;
            Scores[Drawer] += DrawerPoints;
            TurnPoints[Drawer] += DrawerPoints;
        }
        Feed.Add(new GuessLine(seat, kind == GuessKind.Right ? "" : Clean(text), kind));
        if (Feed.Count > FeedLength) Feed.RemoveAt(0);
        Version++;
        if (kind == GuessKind.Right && Enumerable.Range(0, Players).All(s => s == Drawer || Guessed[s])) Reveal();
        return kind;
    }

    /// <summary>Everyone who isn't drawing, and hasn't got it yet.</summary>
    public IEnumerable<int> StillGuessing => Enumerable.Range(0, Players).Where(s => s != Drawer && !Guessed[s]);

    /// <summary>Time passes: the choice made for a drawer who doesn't choose, hints, the end of a turn, the next turn.</summary>
    public List<string> Step(double dt)
    {
        var events = new List<string>();
        if (Over) return events;
        TimeLeft -= dt;
        switch (Phase)
        {
            case Stage.Choosing when TimeLeft <= 0:
                Choose(Drawer, 0);
                events.Add("chosen");
                break;
            case Stage.Drawing:
                int hints = TimeLeft <= DrawSeconds * 0.3 ? 2 : TimeLeft <= DrawSeconds * 0.55 ? 1 : 0;
                if (hints > Hints && Current is { } w && w.In("en").Length >= 4)
                {
                    Hints = hints;
                    Version++;
                    events.Add("hint");
                }
                if (TimeLeft <= 0)
                {
                    Reveal();
                    events.Add("timeout");
                }
                break;
            case Stage.Reveal when TimeLeft <= 0:
                if (TurnNumber + 1 >= Players * Rounds)
                {
                    Phase = Stage.Over;
                    Version++;
                    events.Add("over");
                }
                else
                {
                    StartTurn(TurnNumber + 1);
                    events.Add("turn");
                }
                break;
        }
        return events;
    }

    void Reveal()
    {
        Phase = Stage.Reveal;
        TimeLeft = RevealSeconds;
        Version++;
    }

    /// <summary>
    /// The hint for a word in a language: its letters as blanks, spaces and hyphens kept, and the first <paramref name="hints"/>
    /// of a fixed, word-dependent order of letters shown ("_ a _ _ _").
    /// </summary>
    public static string HintOf(string word, int hints)
    {
        var letters = Enumerable.Range(0, word.Length).Where(i => char.IsLetterOrDigit(word[i])).ToList();
        var order = letters.OrderBy(i => (i * 7919 + word.Length * 31) % 101).ToList();
        var shown = order.Take(Math.Min(hints, Math.Max(0, letters.Count - 2))).ToHashSet();
        var sb = new StringBuilder();
        for (int i = 0; i < word.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(char.IsLetterOrDigit(word[i]) ? shown.Contains(i) ? word[i] : '_' : word[i] == ' ' ? ' ' : word[i]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// A guess or an answer as compared: lower case, ё as е, every apostrophe as ', runs of spaces and hyphens as one space,
    /// and the ends trimmed.
    /// </summary>
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool space = false;
        foreach (char raw in text.Trim().ToLowerInvariant())
        {
            char c = raw switch { 'ё' => 'е', 'ʻ' or 'ʼ' or '‘' or '’' or '`' or '´' => '\'', _ => raw };
            if (char.IsWhiteSpace(c) || c == '-')
            {
                space = sb.Length > 0;
                continue;
            }
            if (!char.IsLetterOrDigit(c) && c != '\'') continue;
            if (space) sb.Append(' ');
            space = false;
            sb.Append(c);
        }
        return sb.ToString();
    }

    static string Clean(string text) => new(text.Where(c => !char.IsControl(c) && c != '|').Take(40).ToArray());

    /// <summary>The edit distance between two words (insertions, deletions and changes of one letter).</summary>
    public static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>Each seat's place by score (1 for the most points; equal scores share a place).</summary>
    public int PlaceOf(int seat) => 1 + Scores.Count(s => s > Scores[seat]);
}
