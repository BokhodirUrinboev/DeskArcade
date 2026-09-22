using System;
using System.Globalization;

namespace DeskArcade;

/// <summary>
/// One challenge a day, the same for everyone on that date: a stats counter to raise by a target amount
/// (baskets, bugs, clays…). Progress is measured from the counter's value when the day's challenge was first
/// seen. Completing it on consecutive days builds a streak. State lives in <see cref="Settings"/>.
/// </summary>
public sealed class Daily
{
    public sealed record Challenge(string GameId, string Counter, int Target, string Text);

    /// <summary>Texts are English keys for <see cref="L.F"/>, with {0} for the target.</summary>
    public static readonly Challenge[] Pool =
    {
        new("hoops", "hoops.baskets", 15, "Make {0} baskets in Hoops"),
        new("archery", "archery.balloons", 10, "Pop {0} balloons in Archery"),
        new("bugs", "bugs.squashed", 40, "Squash {0} bugs"),
        new("hockey", "hockey.goals", 7, "Score {0} goals in Air Hockey"),
        new("clay", "clay.hits", 15, "Break {0} clays"),
        new("bricks", "bricks.broken", 60, "Break {0} bricks"),
        new("cans", "cans.knocked", 30, "Knock down {0} cans"),
        new("bubbles", "bubbles.popped", 50, "Pop {0} bubbles"),
        new("whack", "whack.hits", 40, "Whack {0} bugs"),
        new("golf", "golf.holes", 9, "Play {0} holes of Mini Golf"),
        new("tower", "tower.perfect", 5, "Make {0} perfect drops in Tower Stack"),
        new("slingshot", "slingshot.blocks", 40, "Knock down {0} blocks with the Slingshot"),
        new("plinko", "plinko.discs", 20, "Drop {0} Plinko discs"),
        new("hoops", "hoops.swishes", 5, "Swish {0} shots in Hoops"),
        new("checkers", "checkers.captures", 8, "Capture {0} pieces in Checkers"),
        new("chess", "chess.captures", 6, "Capture {0} pieces in Chess"),
        new("seabattle", "seabattle.sunk", 5, "Sink {0} ships in Sea Battle"),
        new("bowling", "bowling.pins", 60, "Knock down {0} pins in Bowling"),
        new("pool", "pool.potted", 15, "Pot {0} balls in Pool"),
    };

    readonly Settings _settings;
    readonly Stats _stats;

    public Daily(Settings settings, Stats stats)
    {
        _settings = settings;
        _stats = stats;
    }

    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    public static Challenge For(DateOnly day) => Pool[(int)((long)day.DayNumber * 7919 % Pool.Length)];

    static string Key(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The day's challenge; on a new day it starts counting from the counter's current value.</summary>
    public Challenge Current(DateOnly day)
    {
        var c = For(day);
        if (_settings.DailyDate != Key(day))
        {
            _settings.DailyDate = Key(day);
            _settings.DailyBase = _stats.Get(c.Counter);
            _settings.DailyDone = false;
        }
        return c;
    }

    public long Progress(DateOnly day)
    {
        var c = Current(day);
        return Math.Clamp(_stats.Get(c.Counter) - _settings.DailyBase, 0, c.Target);
    }

    public bool Done(DateOnly day)
    {
        Current(day);
        return _settings.DailyDone;
    }

    /// <summary>Days in a row with the challenge done, counting today or yesterday as the latest.</summary>
    public int Streak(DateOnly day) =>
        _settings.DailyLastDone == Key(day) || _settings.DailyLastDone == Key(day.AddDays(-1)) ? _settings.DailyStreak : 0;

    /// <summary>True the moment the day's challenge is completed (once per day).</summary>
    public bool Check(DateOnly day)
    {
        var c = Current(day);
        if (_settings.DailyDone || Progress(day) < c.Target) return false;
        _settings.DailyDone = true;
        _settings.DailyStreak = _settings.DailyLastDone == Key(day.AddDays(-1)) ? _settings.DailyStreak + 1 : 1;
        _settings.DailyLastDone = Key(day);
        _stats.Add("daily.done");
        _stats.Max("daily.streak", _settings.DailyStreak);
        return true;
    }
}
