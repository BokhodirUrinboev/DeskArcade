namespace DeskArcade;

/// <summary>
/// Every achievement. Games only report counters through <c>Host.Stats</c>; titles and descriptions are
/// English and translated when shown. Counter names are "gameId.event".
/// </summary>
public static class Achievements
{
    public static readonly Achievement[] All =
    {
        new("coffee-break", "general", "Coffee break", "Play for 30 minutes in total", "play.minutes", 30),
        new("marathon", "general", "Marathon", "Play for 5 hours in total", "play.minutes", 300),
        new("explorer", "general", "Explorer", "Play 6 different games", "play.games", 6),
        new("pair-programmer", "general", "Pair programmer", "Be playing when Claude finishes 10 times", "claude.done", 10),

        new("hoops-100", "hoops", "Hundred baskets", "Score 100 baskets", "hoops.baskets", 100),
        new("hoops-swish", "hoops", "Nothing but net", "Score 25 swishes", "hoops.swishes", 25),
        new("hoops-streak", "hoops", "Unstoppable", "Make 10 baskets in a row", "hoops.streak", 10),

        new("archery-bullseye", "archery", "Bullseye", "Hit 10 bullseyes", "archery.bullseyes", 10),
        new("archery-balloons", "archery", "Party pooper", "Pop 50 balloons", "archery.balloons", 50),
        new("archery-round", "archery", "Sharpshooter", "Score 80 points in one round", "archery.round", 80),
        new("archery-duel", "archery", "Duelist", "Win an Archery match over the LAN", "archery.duelwins", 1),

        new("juggle-25", "juggle", "Keepy-uppy pro", "Score 25 points in one run", "juggle.run", 25),
        new("juggle-stars", "juggle", "Star catcher", "Catch 20 stars", "juggle.stars", 20),

        new("golf-ace", "golf", "Hole in one", "Sink a hole in one", "golf.aces", 1),
        new("golf-holes", "golf", "Course regular", "Finish 50 holes", "golf.holes", 50),
        new("golf-under", "golf", "Under par", "Finish a round under par", "golf.underpar", 1),
        new("golf-duel", "golf", "Match play", "Win a Mini Golf match over the LAN", "golf.duelwins", 1),

        new("bugs-200", "bugs", "Exterminator", "Squash 200 bugs", "bugs.squashed", 200),
        new("bugs-combo", "bugs", "Combo breaker", "Reach a ×4 combo", "bugs.combo", 4),
        new("bugs-round", "bugs", "Zero bugs", "Score 60 points in one round", "bugs.round", 60),

        new("cans-100", "cans", "Tin smasher", "Knock down 100 cans", "cans.knocked", 100),
        new("cans-stack", "cans", "Fairground champion", "Reach stack 5", "cans.stack", 5),

        new("bricks-500", "bricks", "Demolition crew", "Break 500 bricks", "bricks.broken", 500),
        new("bricks-level", "bricks", "Wall breaker", "Reach level 4", "bricks.level", 4),

        new("bubbles-300", "bubbles", "Bubble wrap", "Pop 300 bubbles", "bubbles.popped", 300),
        new("bubbles-wave", "bubbles", "Wave rider", "Reach wave 5", "bubbles.wave", 5),

        new("hockey-win", "hockey", "First win", "Beat the CPU", "hockey.wins", 1),
        new("hockey-goals", "hockey", "Sniper", "Score 50 goals", "hockey.goals", 50),
        new("hockey-champion", "hockey", "Champion", "Beat CPU level 5", "hockey.level", 6),
        new("hockey-series", "hockey", "Series winner", "Win a best-of-3 series over the LAN", "hockey.series", 1),

        new("pong-win", "pong", "Rally master", "Win a game of Pong", "pong.wins", 1),
        new("pong-level", "pong", "Paddle legend", "Beat the Pong CPU at level 5", "pong.level", 6),

        new("pet-friend", "pet", "Best friends", "Pet your desktop pet 50 times", "pet.pets", 50),
        new("pet-taxi", "pet", "Taxi", "Carry your pet 10 times", "pet.carries", 10),
        new("pet-tricks", "pet", "Show-off", "Watch your pet do 25 tricks", "pet.tricks", 25),

        new("tower-15", "tower", "Skyscraper", "Build a tower 15 blocks high", "tower.height", 15),
        new("tower-perfect", "tower", "Perfectionist", "Make 10 perfect drops", "tower.perfect", 10),

        new("slingshot-blocks", "slingshot", "Wrecking ball", "Knock down 200 blocks", "slingshot.blocks", 200),
        new("slingshot-clear", "slingshot", "Clean sweep", "Clear 10 towers", "slingshot.cleared", 10),

        new("whack-200", "whack", "Bug whacker", "Whack 200 bugs", "whack.hits", 200),
        new("whack-round", "whack", "Lightning hands", "Score 50 points in one round", "whack.round", 50),

        new("darts-180", "darts", "One hundred and eighty", "Score 180 with three darts", "darts.180s", 1),
        new("darts-legs", "darts", "Checked out", "Finish 5 games of 501", "darts.legs", 5),

        new("clay-100", "clay", "Clay breaker", "Hit 100 clay targets", "clay.hits", 100),
        new("clay-double", "clay", "Double trouble", "Hit two targets with one shot", "clay.doubles", 1),

        new("plinko-jackpot", "plinko", "Jackpot", "Land 5 discs in the jackpot slot", "plinko.jackpots", 5),
        new("plinko-100", "plinko", "Disc dropper", "Drop 100 discs", "plinko.discs", 100),

        new("checkers-win", "checkers", "Crowned", "Win a game of Checkers", "checkers.wins", 1),
        new("chess-win", "chess", "Checkmate", "Win a game of Chess", "chess.wins", 1),
        new("connect4-win", "connect4", "Four in a row", "Win a game of Connect Four", "connect4.wins", 1),
        new("tictactoe-win", "tictactoe", "Three in a row", "Win a game of Tic-tac-toe", "tictactoe.wins", 1),
        new("seabattle-win", "seabattle", "Admiral", "Win a game of Sea Battle", "seabattle.wins", 1),
        new("durak-win", "durak", "Not the fool", "Get rid of your cards before someone else in Durak", "durak.wins", 1),
        new("durak-ten", "durak", "Card shark", "Escape being the durak 10 times", "durak.wins", 10),
        new("lan-win", "general", "Office rival", "Beat a co-worker over the LAN", "lan.wins", 1),
        new("daily-first", "general", "Daily player", "Finish a daily challenge", "daily.done", 1),
        new("daily-week", "general", "Seven in a row", "Finish the daily challenge 7 days in a row", "daily.streak", 7),
    };
}
