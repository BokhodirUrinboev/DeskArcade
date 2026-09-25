using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;

namespace DeskArcade.Games;

/// <summary>
/// The pure rules behind the pet's life, kept apart from the drawing so they can be tested:
/// growing up (a stage from age and play, a body scale per stage and a second trick at the top stage),
/// the favourite nap spot (a small tally of the apps whose windows it napped on) and the pet's own voice volume
/// (a level from the menus, and softer calls late in the evening).
/// </summary>
public static class PetLife
{
    // ------------------------------------------------------------------ growing up

    /// <summary>The life stages, in order: 0 young, 1 grown-up, 2 wise.</summary>
    public static readonly string[] StageNames = { "Young", "Grown-up", "Wise" };

    public const int Young = 0, Grown = 1, Wise = 2;

    /// <summary>Days since adoption and play points needed for each stage (both are needed).</summary>
    public const int GrownDays = 7, WiseDays = 28;
    public const long GrownPlay = 150, WisePlay = 800;

    /// <summary>Play points: a pet, a treat eaten, a ball brought back.</summary>
    public const int PetPoints = 1, TreatPoints = 2, FetchPoints = 3;

    /// <summary>The per-kind play counter in <see cref="Stats"/>.</summary>
    public static string PlayCounter(string kind) => "pet.play." + kind;

    /// <summary>The per-kind stage reached so far (so each growing-up is celebrated once).</summary>
    public static string StageCounter(string kind) => "pet.stage." + kind;

    /// <summary>The highest stage any pet has reached, for the achievements.</summary>
    public const string StageAchievementCounter = "pet.stage";

    /// <summary>Whole days from the adoption date to <paramref name="now"/> (local dates; never negative).</summary>
    public static int AgeDays(DateTime adopted, DateTime now) => Math.Max(0, (now.Date - adopted.Date).Days);

    /// <summary>The stage a pet has reached: it needs both the days and the play for each one.</summary>
    public static int StageOf(int ageDays, long play) =>
        ageDays >= WiseDays && play >= WisePlay ? Wise : ageDays >= GrownDays && play >= GrownPlay ? Grown : Young;

    /// <summary>
    /// The body scale for a stage: a little smaller when young, a touch bigger when wise. Kept within 0.9–1.08 so the
    /// click area (a fixed circle round the body) and the room checks on window tops still hold.
    /// </summary>
    public static double ScaleOf(int stage) => stage switch { <= Young => 0.9, Grown => 1.0, _ => 1.08 };

    /// <summary>What the next stage needs, or null at the top: days and play points.</summary>
    public static (int Days, long Play)? NextStage(int stage) => stage switch
    {
        Young => (GrownDays, GrownPlay),
        Grown => (WiseDays, WisePlay),
        _ => null,
    };

    /// <summary>
    /// Play points from the older, shared counters, for a pet adopted before the pets kept their own: pets, treats and
    /// fetches, weighted like new play.
    /// </summary>
    public static long PlayFromTotals(long pets, long treats, long fetches) =>
        Math.Max(0, pets) * PetPoints + Math.Max(0, treats) * TreatPoints + Math.Max(0, fetches) * FetchPoints;

    /// <summary>
    /// The second trick a wise pet learns, and how long it lasts. Each is only a pose (made from the parts every
    /// drawing already has), so it plays over the LAN and with reduced motion like the habits do: the cat sits up and
    /// waves, the dog plays dead, the duck dabbles tail-up, the bunny stands up on the lookout, the penguin bows, the fox
    /// does its mousing dive, the hamster rolls up into a ball, the turtle sunbathes, the parrot dances, the frog does a
    /// big croak, the owl stretches both wings and the dragon blows smoke rings.
    /// </summary>
    public static (string Name, double Seconds) SecondTrickFor(string kind) => kind switch
    {
        "dog" => ("playdead", 2.4), "duck" => ("dabble", 1.6), "bunny" => ("lookout", 1.8), "penguin" => ("bow", 1.5),
        "fox" => ("mousing", 1.3), "hamster" => ("roll", 1.4), "turtle" => ("sunbathe", 2.4), "parrot" => ("dance", 1.8),
        "frog" => ("bigcroak", 1.6), "owl" => ("wingstretch", 1.6), "dragon" => ("rings", 1.9),
        _ => ("wave", 1.6),
    };

    /// <summary>How long a second trick lasts by its name, or 0 when <paramref name="act"/> is not one.</summary>
    public static double SecondTrickSeconds(string act)
    {
        foreach (var kind in PetGame.Kinds)
        {
            var (name, seconds) = SecondTrickFor(kind);
            if (name == act) return seconds;
        }
        return 0;
    }

    // ------------------------------------------------------------------ nap spots

    /// <summary>Naps on an app's windows before it counts as the favourite.</summary>
    public const int FavouriteAfter = 3;

    /// <summary>How many apps the tally keeps, and the total at which all counts are halved (so a new favourite can take over).</summary>
    public const int KeepSpots = 8, HalveAt = 200;

    /// <summary>
    /// The key a window is remembered by: the name of the app (its process name, lower case, without ".exe"), never
    /// the window title, which can hold a document or page name. Null when there is nothing usable.
    /// </summary>
    public static string? SpotKey(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return null;
        string key = processName.Trim().ToLowerInvariant();
        if (key.EndsWith(".exe", StringComparison.Ordinal)) key = key[..^4];
        key = new string(key.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ' ').ToArray()).Trim();
        if (key.Length > 40) key = key[..40];
        return key.Length == 0 ? null : key;
    }

    /// <summary>Counts a nap on <paramref name="spot"/>; keeps the tally small and lets old favourites fade.</summary>
    public static void RecordNap(IDictionary<string, int> tally, string spot)
    {
        tally[spot] = (tally.TryGetValue(spot, out int n) ? n : 0) + 1;
        if (tally.Values.Sum() >= HalveAt)
        {
            foreach (var key in tally.Keys.ToList())
            {
                int half = tally[key] / 2;
                if (half <= 0 && key != spot) tally.Remove(key);
                else tally[key] = Math.Max(1, half);
            }
        }
        while (tally.Count > KeepSpots)
        {
            // the least-napped spot goes (never the one just napped on)
            var drop = tally.Where(kv => kv.Key != spot).OrderBy(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key;
            tally.Remove(drop);
        }
    }

    /// <summary>The spot napped on most (at least <see cref="FavouriteAfter"/> times); ties go to the first name.</summary>
    public static string? FavouriteSpot(IReadOnlyDictionary<string, int>? tally)
    {
        if (tally == null) return null;
        string? best = null;
        int bestCount = FavouriteAfter - 1;
        foreach (var (key, count) in tally.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (count <= bestCount) continue;
            best = key;
            bestCount = count;
        }
        return best;
    }

    /// <summary>
    /// Where to nap on the favourite app's windows: the widest top among <paramref name="tops"/> that belongs to it,
    /// is wide enough to lie on, has room above it for the pet and is clear of the scoreboard. <paramref name="x"/> is
    /// the spot on it nearest the pet. False when none of its windows is visible or none has room.
    /// </summary>
    public static bool TryNapSpot(IReadOnlyList<DeskArcade.Engine.Platform> tops, Func<IntPtr, string?> spotOf, string favourite, Rect arena, Rect hud,
        double petX, double petHeight, double halfW, out DeskArcade.Engine.Platform top, out double x)
    {
        top = default;
        x = 0;
        double bestWidth = 0;
        foreach (var p in tops)
        {
            if (p.Hwnd == IntPtr.Zero || p.Y - petHeight - 12 < arena.Top) continue;
            double lo = Math.Max(p.X1 + halfW + 10, arena.Left + halfW), hi = Math.Min(p.X2 - halfW - 10, arena.Right - halfW);
            if (hi - lo < 8 || hi - lo <= bestWidth) continue;
            double at = Math.Clamp(petX, lo, hi);
            if (hud.Contains(new Point(at, p.Y - petHeight / 2)))
            {
                // under the scoreboard: try the other end of the window top
                at = at - lo < hi - at ? hi : lo;
                if (hud.Contains(new Point(at, p.Y - petHeight / 2))) continue;
            }
            if (spotOf(p.Hwnd) != favourite) continue; // checked last: it may ask the system for the app
            bestWidth = hi - lo;
            top = p;
            x = at;
        }
        return bestWidth > 0;
    }

    // ------------------------------------------------------------------ the pet's voice

    /// <summary>The pet volume levels, from the tray and the ☰ menu: 0 off, 1 quiet, 2 normal (the default), 3 loud.</summary>
    public static readonly string[] VolumeNames = { "Off", "Quiet", "Normal", "Loud" };

    public const int DefaultVolume = 2;

    /// <summary>How loud the pet's calls are at a level, on top of the main volume. Out-of-range levels count as normal.</summary>
    public static double VolumeFactor(int level) => level switch { 0 => 0, 1 => 0.4, 3 => 1.5, _ => 1 };

    /// <summary>How soft the calls are at night, when the pet is drowsy (see <see cref="PetGame.IsNight"/>).</summary>
    public const double NightFactor = 0.5;

    /// <summary>
    /// Softer calls late in the evening: full volume until 20:00, fading to <see cref="NightFactor"/> by 22:00,
    /// and that soft through the night until 06:00.
    /// </summary>
    public static double EveningFactor(TimeOnly t)
    {
        if (PetGame.IsNight(t)) return NightFactor;
        var dusk = new TimeOnly(20, 0);
        if (t < dusk) return 1;
        double k = (t - dusk).TotalHours / 2; // 0 at 20:00, 1 at 22:00
        return 1 - (1 - NightFactor) * Math.Clamp(k, 0, 1);
    }

    /// <summary>The pet's voice gain: the level times the evening softening.</summary>
    public static double VoiceGain(int level, TimeOnly t) => VolumeFactor(level) * EveningFactor(t);
}
