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
        new("reversi", "reversi.flips", 40, "Flip {0} discs in Reversi"),
        new("seabattle", "seabattle.sunk", 5, "Sink {0} ships in Sea Battle"),
        new("darts", "darts.trebles", 6, "Hit {0} trebles in Darts"),
        new("toss", "toss.baskets", 10, "Toss {0} paper balls into the bin"),
        new("fishing", "fishing.caught", 8, "Catch {0} fish"),
        new("bowling", "bowling.pins", 60, "Knock down {0} pins in Bowling"),
        new("pool", "pool.potted", 15, "Pot {0} balls in Pool"),
        new("memory", "memory.pairs", 12, "Find {0} pairs in Memory"),
        new("codebreaker", "codebreaker.wins", 2, "Break {0} codes in Code Breaker"),
        new("solitaire", "solitaire.cards", 30, "Send {0} cards home in Solitaire"),
        new("lastcard", "lastcard.played", 25, "Play {0} cards in Last Card"),
        new("interns", "interns.saved", 15, "Get {0} interns to the exit"),
        new("blockfall", "blockfall.lines", 20, "Clear {0} lines in Blockfall"),
        new("sheep", "sheep.penned", 12, "Pen {0} sheep in Sheep Herding"),
        new("cannons", "cannons.blocks", 20, "Knock down {0} castle blocks in Cannon Castles"),
        new("pinball", "pinball.bumpers", 60, "Hit {0} bumpers in Pinball"),
        new("marble", "marble.cups", 5, "Land {0} marbles in the cup in Marble Run"),
        new("typing", "typing.chars", 600, "Type {0} characters in Typing Race"),
        new("rain", "rain.words", 40, "Zap {0} words in Word Rain"),
        new("snake", "snake.apples", 20, "Eat {0} apples in Snakes on Windows"),
        new("asteroids", "asteroids.rocks", 30, "Destroy {0} rocks in Asteroids"),
        new("mines", "mines.cleared", 150, "Open {0} safe cells in Minesweeper"),
        new("sudoku", "sudoku.digits", 40, "Fill in {0} digits in Sudoku"),
        new("mancala", "mancala.sown", 60, "Sow {0} seeds in Mancala"),
        new("blackjack", "blackjack.wins", 8, "Win {0} hands of Blackjack"),
        new("curling", "curling.stones", 16, "Throw {0} stones in Curling"),
        new("planes", "planes.throws", 12, "Throw {0} paper planes"),
        new("cups", "cups.sunk", 10, "Sink {0} cups in Ping-Pong Cups"),
        new("bingo", "bingo.squares", 3, "Dab {0} squares in Bingo of Work"),
        new("kite", "kite.clouds", 15, "Catch {0} clouds with the Kite"),
        new("backgammon", "backgammon.off", 15, "Bear off {0} checkers in Backgammon"),
        new("dominoes", "dominoes.played", 20, "Lay {0} tiles in Dominoes"),
        new("jenga", "jenga.moved", 12, "Move {0} blocks in Window Jenga"),
        new("bridge", "bridge.saved", 10, "Get {0} interns across the Rope Bridge"),
        // 1.8.6 · party games
        new("quiz", "quiz.right", 7, "Answer {0} questions right in Quiz Night"),
        new("draw", "draw.guessed", 3, "Guess {0} words in Draw & Guess"),

        // 1.8.6 · card games
        new("poker", "poker.pots", 5, "Win {0} pots in Poker"),
        new("hearts", "hearts.hands", 4, "Play {0} hands of Hearts"),

        // 1.8.6 · word and key games
        new("words", "words.won", 2, "Solve {0} words in Word Guess"),

        // 1.8.6 · Spot the Bug
        new("spotbug", "spotbug.found", 8, "Find {0} bugs in Spot the Bug"),

        // 1.8.6 · arcade
        new("servers", "servers.served", 40, "Serve {0} requests in Load Balancer"),
        new("pipeline", "pipeline.deploys", 3, "Deploy {0} levels in Pipeline"),

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
