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
        new("its-compiling", "general", "It's compiling", "Be playing when a --while command finishes 10 times", "task.done", 10),
        new("race-cpu", "general", "Pacesetter", "Beat the computer in 10 races", "race.cpuwins", 10),

        new("work-eyes", "work", "Far sighted", "Take 50 eye breaks", "work.eye", 50),
        new("work-stretch", "work", "Limber", "Finish 25 stretch breaks", "work.stretch", 25),
        new("work-water", "work", "Hydrated", "Drink 50 glasses of water", "work.water", 50),
        new("work-focus", "work", "Deep work", "Finish 10 focus blocks", "work.focus", 10),
        new("work-breathe", "work", "Breathe easy", "Breathe for a minute 10 times", "work.breathe", 10),
        new("work-meetings", "work", "Right on time", "Get a heads-up before 20 meetings", "work.meetings", 20),
        new("work-invites", "work", "Coffee club", "Go for coffee, lunch or a walk with co-workers 5 times", "work.invites", 5),
        new("work-notes", "work", "Note to self", "Tick off 10 sticky notes", "work.notes", 10),
        new("work-timers", "work", "Tea's ready", "Let 10 timers ring", "work.timers", 10),

        // 1.8.6 · coding agents and CI

        // 1.8.6 · the programmer's day

        // 1.8.6 · party games

        // 1.8.6 · card games

        // 1.8.6 · word and key games

        // 1.8.6 · Spot the Bug

        // 1.8.6 · arcade
        new("freecell-win", "solitaire", "Free and clear", "Solve a FreeCell deal", "solitaire.freecell", 1),
        new("spider-win", "solitaire", "Eight legs", "Solve a game of Spider", "solitaire.spider", 1),
        new("servers-500", "servers", "Uptime", "Serve 500 requests in Load Balancer", "servers.served", 500),
        new("servers-wave", "servers", "Black Friday", "Reach wave 8 in Load Balancer", "servers.wave", 8),
        new("pipeline-deploys", "pipeline", "Continuous delivery", "Deploy 25 Pipeline levels", "pipeline.deploys", 25),
        new("pipeline-level", "pipeline", "Release train", "Reach level 10 in Pipeline", "pipeline.level", 10),
        new("pipeline-pieces", "pipeline", "Plumber", "Lay 500 pieces of pipe", "pipeline.pieces", 500),
        new("servers-clean", "servers", "Five nines", "Clear 5 waves without losing a request", "servers.clean", 5),


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
        new("pet-fetch", "pet", "Fetch!", "Have your pet bring the ball back 20 times", "pet.fetches", 20),
        new("pet-treats", "pet", "Treat time", "Feed your pet 30 treats", "pet.treats", 30),
        new("pet-visit", "pet", "Playdate", "Meet a co-worker's pet over the LAN", "pet.visits", 1),
        new("pet-grown", "pet", "All grown up", "Raise a pet until it is grown up", "pet.stage", 1),
        new("pet-wise", "pet", "Old and wise", "Raise a pet until it is old and wise", "pet.stage", 2),
        new("pet-post", "pet", "Pet post", "Send 10 gifts to a co-worker's pet", "pet.giftssent", 10),
        new("pet-parcel", "pet", "Special delivery", "Open a parcel from a co-worker", "pet.parcels", 1),

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

        new("toss-10", "toss", "Wastebasket pro", "Score 10 in one run", "toss.run", 10),
        new("toss-swish", "toss", "Clean throw", "Toss 25 swishes into the bin", "toss.swishes", 25),
        new("fishing-50", "fishing", "Angler", "Catch 50 fish", "fishing.caught", 50),
        new("fishing-golden", "fishing", "Golden catch", "Catch a golden trout", "fishing.golden", 1),
        new("bowling-turkey", "bowling", "Turkey", "Bowl three strikes in a row", "bowling.turkeys", 1),
        new("bowling-150", "bowling", "League night", "Score 150 in one game", "bowling.best", 150),

        new("pool-clear", "pool", "Table cleared", "Clear the pool table", "pool.cleared", 1),
        new("pool-triple", "pool", "Hat trick", "Pot 3 balls with one shot", "pool.multi", 3),
        new("pinball-5k", "pinball", "Pinball wizard", "Score 5,000 in one game", "pinball.best", 5000),
        new("pinball-bumpers", "pinball", "Bumper cars", "Hit 500 bumpers", "pinball.bumpers", 500),

        new("checkers-win", "checkers", "Crowned", "Win a game of Checkers", "checkers.wins", 1),
        new("chess-win", "chess", "Checkmate", "Win a game of Chess", "chess.wins", 1),
        new("connect4-win", "connect4", "Four in a row", "Win a game of Connect Four", "connect4.wins", 1),
        new("tictactoe-win", "tictactoe", "Three in a row", "Win a game of Tic-tac-toe", "tictactoe.wins", 1),
        new("reversi-hard", "reversi", "Corner office", "Beat the Reversi CPU on Hard or Expert", "reversi.hardwins", 1),
        new("reversi-landslide", "reversi", "Landslide", "Win Reversi with 40 discs or more, or wipe out every disc", "reversi.landslides", 1),
        new("gomoku-hard", "gomoku", "Five alive", "Beat the Gomoku CPU on Hard or Expert", "gomoku.hardwins", 1),
        new("gomoku-quick", "gomoku", "Quick five", "Win Gomoku with fewer than 15 stones", "gomoku.quickwins", 1),
        new("seabattle-win", "seabattle", "Admiral", "Win a game of Sea Battle", "seabattle.wins", 1),
        new("seabattle-salvo", "seabattle", "Broadside", "Win a game of Sea Battle with salvo rules", "seabattle.salvowins", 1),
        new("durak-win", "durak", "Not the fool", "Get rid of your cards before someone else in Durak", "durak.wins", 1),
        new("durak-ten", "durak", "Card shark", "Escape being the durak 10 times", "durak.wins", 10),
        new("durak-transfer", "durak", "Not me!", "Pass an attack on 10 times in Perevodnoy", "durak.transfers", 10),
        new("memory-win", "memory", "Total recall", "Clear a Memory board", "memory.wins", 1),
        new("memory-pairs", "memory", "Matchmaker", "Find 100 pairs", "memory.pairs", 100),

        new("solitaire-win", "solitaire", "Patience", "Solve a game of Solitaire", "solitaire.wins", 1),
        new("solitaire-10", "solitaire", "Patience of a saint", "Solve 10 games of Solitaire", "solitaire.wins", 10),
        new("solitaire-cards", "solitaire", "Homeward bound", "Send 500 cards home", "solitaire.cards", 500),

        new("lastcard-win", "lastcard", "Out first", "Win a game of Last Card", "lastcard.wins", 1),
        new("lastcard-10", "lastcard", "Empty-handed", "Win 10 games of Last Card", "lastcard.wins", 10),
        new("lastcard-plus4", "lastcard", "No hard feelings", "Play 20 Wild Draw Fours", "lastcard.plusfours", 20),

        new("interns-1", "interns", "Onboarding", "Clear the first level of Interns", "interns.level", 1),
        new("interns-10", "interns", "Head of department", "Clear level 10 of Interns", "interns.level", 10),
        new("interns-100", "interns", "Mentor", "Get 100 interns to the exit", "interns.saved", 100),
        new("interns-perfect", "interns", "Nobody left behind", "Save every intern on a level", "interns.perfect", 1),

        new("blockfall-four", "blockfall", "Four at once", "Clear four lines with one piece", "blockfall.fours", 1),
        new("blockfall-lines", "blockfall", "Line manager", "Clear 200 lines", "blockfall.lines", 200),
        new("blockfall-10k", "blockfall", "Stacked", "Score 10,000 points in one game", "blockfall.best", 10000),
        new("marble-cup", "marble", "In the cup", "Land a marble in the cup in Marble Run", "marble.cups", 1),
        new("marble-bare", "marble", "Look, no ramps", "Land a marble in the cup without placing a piece", "marble.bare", 1),
        new("marble-streak", "marble", "Marble master", "Land 5 marbles in a row at the first release", "marble.streak", 5),

        new("sheep-100", "sheep", "Good dog", "Pen 100 sheep", "sheep.penned", 100),
        new("sheep-round5", "sheep", "Top dog", "Clear round 5 of Sheep Herding", "sheep.round", 5),
        new("sheep-spare", "sheep", "Come by!", "Pen the whole flock with 30 seconds to spare", "sheep.spare", 30),

        new("cannons-hard", "cannons", "Castle breaker", "Beat the computer at Hard or Expert in Cannon Castles", "cannons.hardwins", 1),
        new("cannons-flawless", "cannons", "Untouchable", "Win Cannon Castles without losing a single block", "cannons.flawless", 1),
        new("cannons-duel", "cannons", "Siege of the next desk", "Win a Cannon Castles duel over the LAN", "cannons.duelwins", 1),

        new("typing-40", "typing", "Touch typist", "Finish a race at 40 words per minute", "typing.best", 40),
        new("typing-80", "typing", "Lightning fingers", "Finish a race at 80 words per minute", "typing.best", 80),
        new("typing-perfect", "typing", "Not a single typo", "Finish a race without a mistake", "typing.perfect", 1),
        new("typing-code", "typing", "Ten-finger coder", "Finish 10 races with code", "typing.code", 10),
        new("typing-wins", "typing", "Pole position", "Win 25 typing races", "typing.wins", 25),
        new("rain-250", "rain", "Weather report", "Zap 250 falling words", "rain.words", 250),
        new("rain-level", "rain", "Monsoon", "Reach level 8 in Word Rain", "rain.level", 8),
        new("rain-combo", "rain", "Clear skies", "Zap 25 words in a row without a miss", "rain.combo", 25),
        new("snake-100", "snake", "An apple a day", "Eat 100 apples in Snakes on Windows", "snake.apples", 100),
        new("snake-run", "snake", "Python", "Eat 30 apples in one game of Snakes on Windows", "snake.run", 30),
        new("snake-golden", "snake", "Golden delicious", "Eat 10 golden apples", "snake.golden", 10),
        new("asteroids-200", "asteroids", "Space janitor", "Destroy 200 rocks in Asteroids", "asteroids.rocks", 200),
        new("asteroids-wave", "asteroids", "Deep space", "Reach wave 6 in Asteroids", "asteroids.wave", 6),
        new("asteroids-coop", "asteroids", "Wingman", "Fly a game of Asteroids with a co-worker", "asteroids.coop", 1),
        new("mines-win", "mines", "Sweeper", "Clear a Minesweeper board", "mines.wins", 1),
        new("mines-expert", "mines", "Bomb squad", "Clear an Expert Minesweeper board", "mines.expert", 1),
        new("mines-fast", "mines", "Quick sweep", "Clear a Beginner board in under 20 seconds", "mines.fast", 1),
        new("sudoku-win", "sudoku", "Nine by nine", "Solve a Sudoku", "sudoku.wins", 1),
        new("sudoku-hard", "sudoku", "Logician", "Solve a Hard Sudoku", "sudoku.hard", 1),
        new("sudoku-daily", "sudoku", "Daily puzzler", "Solve 7 daily Sudoku puzzles", "sudoku.daily", 7),
        new("mancala-win", "mancala", "Seed saver", "Win a game of Mancala", "mancala.wins", 1),
        new("mancala-hard", "mancala", "Master sower", "Beat the Mancala CPU on Hard or Expert", "mancala.hardwins", 1),
        new("mancala-capture", "mancala", "Big harvest", "Capture 10 seeds with one move", "mancala.bigcaptures", 1),
        new("blackjack-natural", "blackjack", "Natural", "Get a blackjack", "blackjack.blackjacks", 1),
        new("blackjack-roller", "blackjack", "High roller", "Finish a Blackjack round with 250 chips or more", "blackjack.best", 250),
        new("blackjack-100", "blackjack", "Card sharp", "Win 100 hands of Blackjack", "blackjack.wins", 100),
        new("curling-win", "curling", "Hurry hard!", "Win a game of Curling", "curling.wins", 1),
        new("curling-hard", "curling", "Skipper", "Beat the Curling CPU on Hard or Expert", "curling.hardwins", 1),
        new("curling-big", "curling", "Big end", "Score 3 or more in one end of Curling", "curling.big", 1),
        new("planes-50", "planes", "Paper pilot", "Throw 50 paper planes", "planes.throws", 50),
        new("planes-long", "planes", "Long haul", "Fly a paper plane 120 metres", "planes.best", 120),
        new("planes-strip", "planes", "Touchdown", "Land on the strip 5 times", "planes.perfect", 5),
        new("cups-50", "cups", "Splash", "Sink 50 cups", "cups.sunk", 50),
        new("cups-bounce", "cups", "Off the table", "Sink a bounce shot", "cups.bounce", 1),
        new("cups-clean", "cups", "Clean rack", "Clear a rack of six in six throws", "cups.clean", 1),

        new("bingo-line", "bingo", "Bingo!", "Get three in a line on a Bingo of Work card", "bingo.lines", 1),
        new("bingo-full", "bingo", "Full house", "Dab all nine squares of a Bingo of Work card", "bingo.full", 1),
        new("bingo-10", "bingo", "Office regular", "Get 10 bingos", "bingo.lines", 10),

        new("kite-clouds", "kite", "Head in the clouds", "Catch 100 clouds with the kite", "kite.clouds", 100),
        new("kite-clean", "kite", "Steady hands", "Fly a whole minute without a crash", "kite.clean", 1),
        new("kite-best", "kite", "High flyer", "Score 250 in one kite flight", "kite.best", 250),

        new("backgammon-win", "backgammon", "Borne off", "Win a game of Backgammon", "backgammon.wins", 1),
        new("backgammon-gammon", "backgammon", "Gammon", "Win before the other side bears off a single checker", "backgammon.gammons", 1),
        new("backgammon-hard", "backgammon", "Backgammon master", "Beat the Hard computer at Backgammon", "backgammon.hardwins", 1),

        new("dominoes-out", "dominoes", "Domino!", "Lay your last tile and go out", "dominoes.outs", 1),
        new("dominoes-win", "dominoes", "First to fifty", "Win a match of Dominoes", "dominoes.wins", 1),
        new("dominoes-hard", "dominoes", "Stone cold", "Beat the Hard computer in a Dominoes match", "dominoes.hardwins", 1),

        new("jenga-10", "jenga", "Careful now", "Move 10 blocks in one Window Jenga tower", "jenga.best", 10),
        new("jenga-standing", "jenga", "Still standing", "Keep a Window Jenga tower up until the time runs out", "jenga.standing", 1),
        new("jenga-200", "jenga", "Master builder", "Move 200 blocks in Window Jenga", "jenga.moved", 200),

        new("bridge-50", "bridge", "Safe crossing", "Get 50 interns across the Rope Bridge", "bridge.saved", 50),
        new("bridge-perfect", "bridge", "Nobody fell", "Get all fifteen interns of a round across", "bridge.perfect", 1),
        new("bridge-planks", "bridge", "Chief engineer", "Lay 200 planks on the Rope Bridge", "bridge.planks", 200),
        new("codebreaker-win", "codebreaker", "Code cracked", "Break a code", "codebreaker.wins", 1),
        new("codebreaker-fast", "codebreaker", "Mind reader", "Break a code in 4 guesses or fewer", "codebreaker.fast", 1),
        new("lan-win", "general", "Office rival", "Beat a co-worker over the LAN", "lan.wins", 1),
        new("daily-first", "general", "Daily player", "Finish a daily challenge", "daily.done", 1),
        new("daily-week", "general", "Seven in a row", "Finish the daily challenge 7 days in a row", "daily.streak", 7),
    };
}
