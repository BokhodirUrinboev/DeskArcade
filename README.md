# Desk Arcade

Mini-games that play **on top of your desktop** in a transparent overlay. Shoot hoops while a build, a
deploy or Claude Code is working, and still see everything underneath.

**Platforms:** Windows 10/11 (x64, ARM64) · Ubuntu 22.04/24.04 (amd64, arm64) · other Linux via AppImage
or Flatpak · macOS 14+ (experimental) &nbsp;·&nbsp; **License:** [MIT](LICENSE) &nbsp;·&nbsp;
**Version:** 1.8.1

---

## Contents

- [Highlights](#highlights)
- [Games](#games)
- [Play with a co-worker](#play-with-a-co-worker)
- [Scoreboard, stats and achievements](#scoreboard-stats-and-achievements)
- [Controls](#controls)
- [Language](#language)
- [Installation](#installation)
- [Updating](#updating)
- [Claude Code integration](#claude-code-integration)
- [Play while a command runs](#play-while-a-command-runs)
- [Building from source](#building-from-source)
- [Project structure](#project-structure)
- [Troubleshooting](#troubleshooting)
- [Code signing policy](#code-signing-policy)
- [Privacy](#privacy)
- [Third-party software](#third-party-software)
- [License](#license)

## Highlights

- **Your screen is a closed box.** Balls, pucks, bubbles, stones and cans bounce off the left, right and
  top edges, and arrows stick into them. Nothing flies off-screen and vanishes. The floor is the top of
  the taskbar or dock.
- **Clicks pass through.** The overlay covers one monitor, but only the game objects (ball, hoop, bow,
  scoreboard) take the mouse. Clicks anywhere else go straight to your windows.
- **Never steals focus.** You can keep typing in the terminal while you play.
- **Window tops are platforms.** Balls, bugs, pets and towers sit on the top edges of your windows and
  ride along when you drag one. You can turn this off in the tray menu.
- **Play while it builds.** `arcade dotnet test` (or `deskarcade --while make`) runs your command as usual
  and shows it on the scoreboard, then chimes when it passes or fails.
- **Idle means idle.** When nothing is moving, rendering stops and CPU use drops to almost zero.
- **Somebody to play against, in every game.** Board games have four CPU levels. Round-based games race a
  computer rival that plays a round alongside yours at the level you pick, marking its scoring on the desktop,
  and the same rounds race a co-worker over the LAN. The scoreboard shows who you are up against, the CPU's
  level and whose turn it is.
- **Boards move.** Chess, checkers, the card tables and the other boards have a grip above them: drag it (or
  right-drag the board) to put the game where you want it, and it stays there.
- **39 games, 102 achievements**, a daily challenge, play-time stats, and English, Uzbek and Russian text.
- **Play with the person at the next desk** over the local network: Air Hockey (best of 3), Pong,
  H-O-R-S-E, Mini Golf, Archery and Cannon Castles duels, board games, Sea Battle and score races in twenty-three games. You see
  what the other player does: their ball, arrow or puck, and a marker wherever they pop, whack or stack.
- **Office leaderboard** (opt-in): today's best scores of everyone on the network who shares theirs.
- **Thirteen themes** (including a seasonal one) that dress the scoreboard, boards, tables, pieces and pet, with light
  decorations drifting over the desktop; a **break reminder**; and twelve desktop pets that fetch, beg for treats and
  visit each other over the LAN.
- **No assets to download.** Every sound is synthesized and all artwork is drawn in code.

## Games

| Game | How to play |
|---|---|
| 🏀 **Hoops** | Grab the ball, flick it and let go. 2 points, 3 from long range, +1 for a swish, 1 for a dunk. Three in a row set the ball on fire (points ×2); at five the hoop moves. Drag the backboard to move the hoop. |
| 🎯 **Archery** | Press on the bow, drag backwards and release. 10 arrows a round. Rings score 2–10, balloons 5, gold balloons 15. From round 2 there is wind and the targets move. Right-drag the bow to move it. |
| ⚽ **Keepy-Uppy** | Click the ball to kick it up; where you click sets the direction. Don't let it touch the ground or a window top. Stars are +3, and gravity grows the longer you juggle. Every run is a race: the computer rival juggles alongside at your CPU difficulty (or a co-worker does, over the LAN) and the higher run wins. |
| ⛳ **Mini Golf** | Drag back from the ball and release to putt. The cup sits on the taskbar or a window top and follows that window. Nine holes, each starting where the last ended, scored against par. |
| 🐞 **Bug Squash** | Click the sleeping bug to start a 30-second round. Squash bugs crawling along the taskbar and window tops. Quick squashes build a combo up to ×4, golden bugs are worth 5, and ladybugs are features: squashing one costs 5. Each round races the computer rival at your CPU difficulty, or a co-worker over the LAN. |
| 🥫 **Can Knockdown** | Throw the ball from behind the dashed line to knock the pyramid off its shelf. 3 balls a stack, the golden can is worth 3. Clearing a stack earns a bonus and a bigger stack; failing ends the game. A game, from the first throw to that game over, races the computer rival at your CPU difficulty, or a co-worker over the LAN. |
| 🧱 **Brick Breaker** | Click the paddle to launch; while the ball is in play the paddle follows your mouse. The ball rebounds off the sides and top; the floor costs one of your 3 balls. Gold bricks are worth 10, steel bricks take two hits. Bricks fall in row by row and the paddle glows when it can launch. A game, from the launch to the last ball lost, races the computer rival at your CPU difficulty, or a co-worker over the LAN. |
| 🫧 **Bubble Pop** | Click the "pop me" bubble to start. Clicking a bubble splits it in two, and the smallest pop. Smaller bubbles score more, quick pops build a combo up to ×3, and leftover seconds become bonus points. |
| 🏒 **Air Hockey** | Drag your blue mallet on the left half and hit the puck into the right-hand goal while the CPU defends. The puck bounces off every other edge. First to 7 wins. The CPU plays at the level from **tray → CPU difficulty** (Easy, Medium, Hard or Expert): two straight match wins move it up a level, two straight losses move it down, and within a session every match it loses makes it a little faster on top. |
| 🏓 **Pong** | Pong on the edges of your screen: drag your paddle up and down the left edge and get the ball past the CPU's paddle on the right. Where the ball hits the paddle sets its angle, and every return speeds it up. First to 7. The CPU plays at the level from **tray → CPU difficulty**: two straight wins move it up, two straight losses move it down, and each win in a session makes it a little sharper on top. |
| 🎯 **Clay Shooting** | Click the trap machine to start 15 pulls. Clays arc across the screen and bounce off the edges; click one to break it. Breaking two with one click is a DOUBLE, golden clays are worth 5, and a clay that lands is a miss. Each round is a race against the computer (**tray → CPU difficulty**) or a co-worker over the LAN. |
| 🔨 **Whack-a-Bug** | Bugs peek out from behind your window tops, the taskbar and the screen edges for a moment. Whack them: 1 point, fast bugs 2, golden 5, combo up to ×3. Ladybugs are features again: −5. |
| 🎲 **Plinko** | Click the strip at the top of the board to drop a disc through the pegs. Slots score 10 to 250, and the gold middle slot is the jackpot. Ten discs a round, raced against the computer or a co-worker over the LAN. Drag the grip above the board (or right-drag its header) to move it; it remembers where you put it. |
| 🗼 **Tower Stack** | Click the sliding block to drop it on the tower. The overhang is cut off, so the tower narrows; a perfect drop keeps the full width, and three in a row widen it. Miss completely and the game ends. |
| 🪨 **Slingshot** | Pull the stone back and let go to knock a tower of blocks off a window top. 3 stones a tower, 10 points a block, 50 for each spare stone. Clear the tower for a bigger one. Each tower is a race against the computer or a co-worker over the LAN. |
| ♟️ **Checkers** | Russian rules (shashki). Click one of your pieces, then the square it should move to (for a multi-jump, the last square; if several routes end there, click each landing in turn). Men move forward but capture backward too; kings fly any distance along a diagonal. Capturing is compulsory, but you choose which capture. A man that reaches the far row in the middle of a capture is crowned and carries on capturing as a king. Play the CPU (it starts on Easy and gets stronger as you win; **tray → CPU difficulty** sets it), or a co-worker over the LAN. Moves slide, captures fade and a crowning pops. Drag the grip above the board or right-drag to move it; the scoreboard shows whose turn it is and the CPU level. |
| ♞ **Chess** | Click a piece, then its square. Full rules: castling, en passant, check, mate, stalemate and the 50-move rule; pawns always promote to a queen. Play the CPU (Easy, Medium, Hard or Expert: it starts on Easy, moves up a level each time you win and back down if you lose twice running) or a co-worker over the LAN. Moves slide, captures fade, a promotion pops and a king in check pulses red. Drag the grip above the board or right-drag to move it; the scoreboard shows whose turn it is and the CPU level. |
| 🔴 **Connect Four** | Click a column to drop a disc; four in a row (across, down or diagonal) wins. Discs fall and bounce into place, and the winning four lights up. Play the CPU or a co-worker. Drag the grip above the board or right-drag to move it; the turn shows on the scoreboard. |
| ❌ **Tic-tac-toe** | Click a square; three in a row wins. The CPU is good but slips now and then. Marks pop in and the winning line lights up. Drag the grip above the board or right-drag to move it; the turn shows on the scoreboard. |
| ⚫ **Reversi** | Othello rules on an 8×8 board. Click one of the dotted squares: your disc must outflank a line of the other colour, and every disc it outflanks turns over, one after another. No move? You pass (the board says so); when neither side can move, the most discs wins. Play the CPU (Easy plays loosely, Medium grabs corners and edges, Hard and Expert look ahead; it starts on Easy and moves up as you win; **tray → CPU difficulty** sets it) or a co-worker over the LAN. Drag the grip above the board or right-drag to move it; the scoreboard shows the discs each side has. |
| ⚪ **Gomoku** | Five in a row on the 15×15 intersections, freestyle (six or more in a row wins too). Click an intersection to put a stone there; the winning line lights up. Play the CPU (Easy extends its own lines, Medium weighs every threat and block, Hard and Expert look a few moves ahead among the best points; **tray → CPU difficulty** sets it) or a co-worker over the LAN. Drag the grip above the board or right-drag to move it. |
| 🚢 **Sea Battle** | Your fleet is on the left, the enemy's waters on the right. Click your grid to shuffle your ships, the enemy grid to start, then fire. A hit shoots again; sink all five ships to win. Shells arc to their square (a splash for a miss, a burst for a hit), a sunk ship darkens square by square and the enemy fleet surfaces when the game is over. The CPU has four levels (**tray → CPU difficulty**): Easy fires at random, Medium hunts and then works along a wounded ship, Hard hunts on a checkerboard, Expert hunts where the most ships could still lie; it moves up a level when you win and down after two losses. Or play a co-worker. Drag the grip above the grids or right-drag to move them; the scoreboard shows whose shot it is. |
| 🃏 **Durak** | The Russian card game (podkidnoy), for 2–4 players: against 1–3 computer players, or co-workers in a room you create (see below). Click a card to attack, throw in or beat a card (click a table card first to pick which one); **Take** gives up the bout, **Done** ends your throwing in. The last player holding cards is the durak. The table has a grip above it (or right-drag it anywhere) and remembers where you put it. Cards are dealt off the deck one by one, fly to the table when played and go to whoever took them; the scoreboard chip says whose move the bout is waiting on (in a room, by name). |
| 🗑️ **Paper Toss** | Grab the crumpled paper in the corner and flick it into the wastebasket on a window top or the far end of the taskbar. A desk fan blows a new wind every throw, stronger the longer your streak. A basket is 1 point, a swish 2, and the bin moves; one miss ends the run. |
| 🎯 **Darts** | 501, double out. Press on the board and hold: the aim wobbles, steady at first, then worse the longer you wait. Let go to throw. Three darts a turn; going below zero, leaving 1 or finishing on anything but a double is a bust. The scoreboard suggests a checkout when you can finish, and your best is the fewest darts. A leg is a race against the computer or a co-worker over the LAN and the fewest darts win; click the score sheet under the board twice to give a hopeless leg up (it counts as 99 darts). Drag the grip (or right-drag the board) to move it. |
| 🎳 **Bowling** | A lane along the taskbar. Drag back from the ball and let go; the angle and length of the drag set the line and the speed. Ten frames with strikes, spares and the 10th-frame bonus balls, scored on the sheet above the lane. |
| 🎱 **Pool** | A table over the screen. Drag back from the cue ball to aim (the line shows where the first ball will go) and let go to shoot. Pot all 15 balls in as few shots as you can; potting the cue ball costs a shot. Drag the grip above the table (or right-drag) to move it. A rack is a round: race a co-worker over the LAN, or the computer, to clear the table in fewer shots. |
| 🪩 **Pinball** | The whole screen is the table and the taskbar is the drain. Click the ball to serve it, then press and hold near a flipper to raise it (right-click flips both). Bumpers in the middle and on your window tops score 100; light all three top lanes to raise the multiplier. Three balls a game. |
| 🎣 **Fishing** | A pond along the taskbar. Drag back from the rod and let go to cast. Wait through the nibbles and click when the bobber goes under. Then hold to reel and let go when the tension bar turns red, or the line snaps. Two minutes a round; perch, carp, pike, catfish and a rare golden trout. |
| 🃏 **Memory** | 24 cards face down. Flip two at a time to find the 12 pairs; your best is the fewest moves. Drag the grip above the cards (or right-drag) to move them. A deal is a round: race a co-worker or the computer to find the pairs in fewer moves. |
| 🟢 **Code Breaker** | Crack a hidden code of four colours (repeats allowed) in ten guesses. Pick a colour and click a hole, or click a hole to cycle it (right-click empties it), then **Check**: a black pin is a right colour in the right place, a white pin a right colour in the wrong place. Each colour also has a symbol for colour-blind play. Drag the grip above the board (or right-drag off the holes) to move it. A code is a round: race a co-worker or the computer to crack it in fewer guesses; a code that gets away counts as 11. |
| 🂡 **Solitaire** | Klondike, draw one. Click the stock to turn a card. Click a card to send it where it fits (home to its foundation first), or drag a card or a face-up run onto the pile you want. **Undo** takes a move back; **New deal** asks once more before it throws the game away. When every card is face up, the rest go home by themselves. Your best is the fewest moves. Drag the grip above the felt (or right-drag) to move the whole layout. A deal is a round: race a co-worker or the computer to send more cards home, counted when the deal is solved or given up with **New deal**. |
| 🟥 **Last Card** | An UNO-style game for 2–4 players. Play a card of the colour on the pile, or the same number or symbol. **Skip**, **Reverse** and **+2** hit the next player; a **Wild** lets you pick the colour, and a **Wild +4** is allowed only when you hold nothing of the colour on the pile. Can't play? Click the pile to draw; a card that fits can go straight down, or **Pass**. Click **Last card!** when you're down to one card (or before, with two), or you draw two as soon as the next player moves. The first to play their last card wins. Every colour also has a shape in the corners (circle, triangle, square, diamond). Against 1–3 computer players, or co-workers in a room. The table has a grip (or right-drag it) and remembers its place. Cards deal from the pile one by one and fly onto the discard when played; a Skip, Reverse or draw card makes a show of itself on the pile, a Wild's colours fan out, and **Last card!** pulses while it applies. The scoreboard chip says whose turn it is (in a room, by name; when it is yours, who is next). |
| 🧑‍💼 **Interns** | Click the hatch and a line of office interns drops out, onto a window top or just above the taskbar, and walks wherever their feet take them. Get enough of them to the **EXIT** door on the taskbar, past open manholes and drops too high to survive. Pick a tool on the toolbar (right-click an intern to switch tools) and click an intern: an **Umbrella** for a safe fall, a **Blocker** who turns the others round (click them again to let them go), or a **Builder** who lays a staircase. Drag a window and everyone standing on it rides along, so a window can be a bridge. Each level has more interns and manholes and fewer spare tools; your best is the highest level cleared. A level is a round: race a co-worker over the LAN, or the computer, to save more interns. **Ctrl+Alt+B** moves the toolbar to the cursor. |
| 🧱 **Blockfall** | Falling blocks, played with the mouse: while the pointer is over the well the falling piece follows its column. Click to turn the piece, hold the button to drop it faster, right-click to drop it at once. Full rows clear (four at once scores the most), and every ten rows is a level with faster pieces. The well stands on a window top when one is wide enough and rides along when you drag that window; otherwise it stands on the taskbar. **Ctrl+Alt+B** moves it to the cursor. |
| 🔮 **Marble Run** | A marble waits in a funnel near the top of the screen; a cup is sunk into the taskbar somewhere else. Drag a **ramp** or a **bumper** out of the tray beside the funnel and drop it on the desktop (three pieces at most): drag a placed piece to move it, drag a ramp's knob to tilt it, right-click a piece to put it back. Click the funnel and the marble falls, rolls along your window tops with the speed it brought and drops off their edges, bounces off your pieces and the screen's sides, and drops into the cup if it rolls over it slowly enough. A sunk marble scores 100, plus 25 for every piece left in the tray, less 25 for each earlier miss; three misses lose the course. Five courses make a round. **Ctrl+Alt+B** brings the tray to the cursor. |
| 🐑 **Sheep Herding** | A flock of sheep grazes on your window tops and along the taskbar, and the cursor is the sheepdog. Sheep run from the dog, and a scared sheep sets its neighbours off, so the flock can be driven: they hop off the ends of window tops and jump up onto low ones as they flee. Drive every sheep through the gate of the pen by the edge of the screen before the clock runs out; a sheep in the pen stays in. **Click to bark**, which sends the sheep nearby running harder (the dog needs a moment between barks), and look out for the stray that bolts. Ten points a sheep, and five for every second left once the whole flock is in. Click the pen to start; every round cleared has a bigger flock and a shorter clock. Drag a window and the sheep on it ride along. **Ctrl+Alt+B** moves the pen to the side of the screen nearer the cursor. |
| 🏰 **Cannon Castles** | Two castles face each other across the desk: yours on the left, the rival's on the right, each on a window top with room above it (or at its end of the taskbar) and riding along when you drag that window. Take turns: drag back from your cannon to set the angle and the power (a short dotted line shows the start of the arc), let go to fire. The windsock between the castles shows the wind, which changes after every shot, and windows in between stop a ball. A ball cracks the block it hits, and a second hit anywhere in a cracked column brings it down with everything above; the flag stands on the keep, so the first keep to lose a block loses the game. Against the computer at four levels (it corrects its aim after each miss, and better at the higher levels), or a co-worker over the LAN. |
| 🐱 **Desktop Pet** | Not a game: a cat (or a dog, duck, bunny, penguin, fox, hamster, turtle, parrot, frog, owl or a small dragon: **tray → Pet**) that walks along your taskbar and window tops, follows the cursor, jumps between windows (the owl and the parrot fly, wings out) and sleeps when left alone. Click to pet it, drag to carry and throw it, **right-click for its trick**. Each animal has its own voices (the cat meows, purrs and chatters at birds; the dog barks, whines and pants; the duck quacks; the bunny squeaks and thumps; the penguin brays; the fox barks "wow-wow" and screams; the hamster squeaks and chitters; the turtle hisses and grunts; the parrot squawks, whistles and says hello; the frog ribbits and croaks; the owl hoots and trills; the dragon rumbles, puffs and roars), its own walk (the bunny and the frog hop, the duck and penguin waddle, the hamster scurries, the turtle crawls, the dragon stomps) and its own trick: the cat stretches, the dog chases its tail, the duck flaps, the bunny does a twisting hop, the penguin belly-slides, the fox pounces, the hamster spins like a wheel, the turtle pops into its shell, the parrot flies a loop and shouts a word, the frog catches a fly with a long tongue, the owl turns its head right round and the dragon breathes a little fire. Left alone it keeps busy the way its animal does: cats groom and stalk the cursor, dogs sniff and wag, hamsters stuff their cheeks, turtles hide and stretch their necks, parrots bob and shriek, frogs puff their throats and catch flies, owls swivel their heads and blink slowly, dragons puff smoke and curl up. Pet it twice and a **ball** appears: throw it and dogs and foxes fetch it back to your cursor, cats sometimes (and may wander off to groom halfway), parrots and owls fly to it, hamsters push it, bunnies and frogs hop after it, and ducks, penguins, turtles and dragons just watch and comment. A **treat jar** stands at the end of the taskbar: click it to toss a treat (a crunch, crumbs, hearts and a burst of energy), and a pet that has gone without for a while comes and begs at the jar. A **thought bubble** shows what is on its mind: a heart, a "z", the ball, a treat or a "!". It is drowsier late at night and sleeps in a nightcap, says good morning with a long stretch, and when your cursor rests for a while it comes to sit on the window nearest you. Over the **LAN** a co-worker's pet visits as a faded ghost with their name above it, and the two say hello when they meet. |

> **Brick Breaker note:** while a ball is in play, a strip along the bottom of the screen takes the
> mouse so the paddle can follow it. It goes away as soon as the ball is lost or you switch games.

## Play with a co-worker

**Play over LAN** in the tray menu: one of you picks **Host a game** and the other picks **Join a game**
(the first game found) or **Find games / join by address…** (a list of everyone hosting, plus a box for an
IP address when the network blocks broadcasts; the host's tray shows its address). No server or account is
involved: everything goes over UDP port 47820 on your local network. The host picks the game: the guest
joins straight into it and follows whenever the host switches.

| Game | Together |
|---|---|
| Air Hockey | Your co-worker's mallet replaces the CPU; each of you defends the left goal on your own screen. Matches form a best-of-3 series |
| Pong | Your co-worker's paddle replaces the CPU's; each of you plays from the left edge |
| Hoops | H-O-R-S-E: make a shot and the other player has to make it from the same spot, or take a letter. Each of you sees the other's ball fly as a faded ghost ball. In every duel the scoreboard chip names the other player and says whose shot it is; your ball, bow or hoop swells once with a soft sound when the turn comes round, and sits dimmed while they play |
| Mini Golf | Match play over 9 holes, taking turns stroke by stroke on your own courses. Their ball shows up as a ghost around your cup; a hole goes to the better score against par |
| Archery | Take turns, one arrow each, ten apiece, in the same wind. Their arrow flies from your bow as a ghost |
| Cannon Castles | Take turns, one ball each, each of you firing from the left of your own screen; the shooter's screen decides what the ball hit and both castles lose the same blocks. The wind for the next shot travels with every shot, and their ball flies from their cannon to yours as a ghost |
| Checkers, Chess, Connect Four, Tic-tac-toe, Reversi, Gomoku | Turns over the network; the guest sees the board from their side and the host's move slide in; the scoreboard chip says whose turn it is (both PCs need 1.8.0 or later) |
| Sea Battle | Each fleet stays on its own PC; only shots and hits cross the network |
| Durak, Last Card | The host's table opens a room and your co-worker's copy joins it by itself; the host starts the game from the table, with or without computer players (the room also takes more co-workers, see below) |
| Bubble Pop, Whack-a-Bug, Tower Stack, Bowling, Fishing, Pinball, Paper Toss, Blockfall, Marble Run, Keepy-Uppy, Bug Squash, Can Knockdown, Brick Breaker, Clay Shooting, Plinko, Slingshot, Darts, Pool, Memory, Code Breaker, Solitaire, Interns, Sheep Herding | Race: start a round and theirs starts too (in Pinball a three-ball game, in Paper Toss a run, in Blockfall a game, in Marble Run five courses, in Keepy-Uppy a run of kicks, in Can Knockdown and Brick Breaker a whole game, in Clay Shooting 15 pulls, in Plinko ten discs, in Slingshot one tower, in Darts a leg of 501, in Pool a rack, in Memory and Solitaire a deal, in Code Breaker a code, in Interns a level, in Sheep Herding a round). You see their live score, a red ring wherever they pop, whack, kick, squash, knock, break, hit, stack, pot, pair, pin, send a card home, save an intern or pen a sheep, and who won; in Darts, Pool, Memory and Code Breaker the fewer darts, shots, moves or guesses win |
| Desktop Pet | Your pets visit each other: the co-worker's pet walks your desktop as a faded ghost with their name above it, and the two say hello when they meet |

**Rooms** for Durak and Last Card are separate from the two-player link, for up to four people: **tray → Play
over LAN → Durak with co-workers…** or **Last Card with co-workers…** (or the button on the game's table). Each
room plays one game, and the list only shows rooms for that game. One player clicks **Create a room** and reads out the
four-letter code; the others pick the room from the list or type the code (plus the host's IP address if
the network blocks broadcasts). The host chooses 2, 3 or 4 seats and starts; computer players fill the empty
seats and take over for anyone who drops out. Several rooms can run on one network. Rooms use UDP port 47822.

**Send** in the same menu pops a quick emote ("gg", "One more?"…) up on the other screen. Windows asks
once whether to allow Desk Arcade on private networks; say yes on both PCs.

## Scoreboard, stats and achievements

The scoreboard is a small pill showing the game icon, score and best, then who you are playing: **CPU ·
Hard**, **vs Alice**, or **Your turn** / **Alice's turn** in a game with turns (the dot breathes while you wait
for the other side). The score pops when it changes. **Click it** to open the full board with a tab for every
game; it shrinks back shortly after the mouse leaves. Drag it anywhere.

The **☰** at the end of the pill (and of the tabs) opens a menu with the essentials of the tray menu: game,
pet, CPU difficulty, theme, sound, accessibility, language, play over LAN, stats, shortcuts, updates, hide, exit. It is the way in on desktops
without a tray (GNOME without the AppIndicator extension, a bare window manager), and it never leaves the overlay.

**Race the computer:** in round-based games (Bubble Pop, Whack-a-Bug, Tower Stack and the others that race
over the LAN) a computer rival plays a round alongside yours when nobody is on the LAN. Its live score sits
under the scoreboard, red rings show where it scores, and when your round ends the higher score wins (the
lower one in games where fewer is better, like Darts). It plays at the game's CPU level (**tray → CPU
difficulty**, or the ☰ menu); two wins in a row move it up a level, two losses move it down. Switch it off
with **Race the computer in solo rounds** in the same menu.

**Daily challenge:** one task a day, the same for everyone ("Make 15 baskets in Hoops"), shown in the tray
with your progress; click it to jump to the game. Finish it on consecutive days to build a streak.

**Stats & achievements** in the tray menu (or `DeskArcade --signal stats`) opens a window with time
played and best score per game, and all 102 achievements with their progress. Stats live in
`stats.json` next to your settings and can be reset from the tray.

**Office leaderboard:** **tray → Office leaderboard** shows today's best hoops streak, baskets, Air Hockey
and Pong wins, bugs squashed, tallest tower, LAN wins and minutes played for everyone on your network who
shares their scores. Sharing is off until you turn it on (in that menu or the leaderboard window): it then
broadcasts your user name and today's scores on UDP port 47821 every 20 seconds, and nothing else.

## Controls

| Shortcut | Action |
|---|---|
| **Ctrl+Alt+G** | Show or hide the overlay |
| **Ctrl+Alt+N** | Next game |
| **Ctrl+Alt+B** | Bring the ball, bow, paddle or pet to the cursor (a penalty stroke in Mini Golf) |
| Click the scoreboard | Open the game tabs |
| Drag the scoreboard | Move it |
| ☰ on the scoreboard | The quick menu: game, pet, CPU difficulty, theme, sound, LAN, stats, hide, exit |
| Drag the grip above a board or table (or right-drag the board) | Move it; the place is remembered (**tray → Reset positions** forgets it) |
| Tray icon, left-click | Show or hide |
| Tray icon menu | Game, pet, CPU difficulty (every game with a computer opponent, and the race toggle), theme, break reminder, office leaderboard, volume, language, Claude Code options, updates, stats, monitor, reset, exit |

On macOS the shortcuts are **Control+Option+G/N/B**. **Tray → Shortcuts…** changes the modifier keys and the
letters (Windows applies them at once; Linux and macOS from the next start).

**Tray → Theme** (also in the ☰ menu) dresses the whole overlay: the scoreboard, the grips above boards and
tables, the chess and checkers boards, the card tables' felt and card backs, the mallets, paddles, puck and
balls, the gold of popups and confetti, and the pet, which wears a scarf in Winter, a pumpkin hat in Halloween,
a flower in Spring and sunglasses in Ocean. Thirteen themes: Classic, Neon, Retro, Halloween, Winter, Ocean,
Forest, Sunset, Candy, Mono (black, white and one red), Spring, Autumn and Midnight, or **Seasonal**, which
follows the calendar: Spring from March to May, Ocean over the summer, Autumn in September and October
(Halloween in its last week), Forest in November, Winter in December and January, Midnight in February.
Picking one shows its name and mood with a burst of confetti. Most themes also drift a few light decorations
over the desktop while a game is moving: snow that settles on window tops and the taskbar for a moment,
leaves, petals, bubbles, fireflies, stars, embers or confetti. They only appear while something is already
moving and fade out within a couple of seconds when the game goes idle, so an idle overlay still costs
nothing; **Theme decorations** in the same menu turns them off, and Reduce motion does too.

**Tray → Break reminder** nudges you after 15 to 60 minutes of play (five minutes away counts as a break),
and can make the "Claude is done" notice say **back to work** when you were playing while Claude worked.

**Tray → Accessibility** has **Reduce motion** (no particle bursts; popups appear in place) and
**Colour-blind friendly colours** (greens become sky blue and reds vermillion, and Connect Four discs are
also marked by shape).

Every action is also available from the command line, which is useful for custom shortcuts and scripts:

```
DeskArcade --signal toggle|next|summon|show|hide|expand|stats|shortcuts|quit|game:durak
DeskArcade --signal lan-host|lan-join|lan-find|lan-leave
DeskArcade --signal durak-rooms|durak-solo:2|durak-host:abcd|durak-join:abcd|durak-start:4|durak-leave
DeskArcade --signal lastcard-rooms|lastcard-solo:2|lastcard-host:abcd|lastcard-join:abcd|lastcard-start:4|lastcard-leave
```

## Language

Desk Arcade speaks **English**, **O'zbekcha** and **Русский**. It follows your system language by
default; pick one in **tray → Language**. The choice applies immediately, menus included.

## Installation

### Windows

Run the installer. It installs for the current user, without an administrator prompt, into
`%LOCALAPPDATA%\Programs\Desk Arcade`.

| File | For |
|---|---|
| `DeskArcade-Setup-<version>.exe` | x64, needs the [.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (setup offers the download page) |
| `DeskArcade-Setup-<version>-standalone.exe` | x64, runtime bundled |
| `DeskArcade-Setup-<version>-arm64.exe` | Windows on ARM, runtime bundled |

The installers are not code-signed yet, so Windows SmartScreen may warn on first run.

Or install with [Scoop](https://scoop.sh) (x64 and ARM64, runtime bundled):

```powershell
scoop bucket add deskarcade https://github.com/BokhodirUrinboev/scoop-bucket
scoop install deskarcade/deskarcade
```

### Ubuntu (22.04 / 24.04)

```bash
sudo apt install ./deskarcade_<version>_amd64.deb   # or _arm64.deb
deskarcade            # or find "Desk Arcade" in the app grid
```

The package bundles its own .NET runtime; apt installs the few X11 libraries it needs. Remove it with
`sudo apt remove deskarcade`.

On Linux the overlay covers the monitor's working area (everything except the top bar and dock) rather
than the whole monitor, because GNOME moves a larger window to another monitor. The games only use the
working area anyway, so nothing changes on screen.

#### Ubuntu notes

| Topic | Xorg session | Wayland session (default) |
|---|---|---|
| Overlay, click-through, sound | ✅ | ✅ through XWayland |
| Always on top, on every workspace, out of the taskbar and Alt+Tab | ✅ EWMH hints, re-applied on every show (GNOME, KDE, XFCE) | ✅ the same hints through XWayland |
| Keyboard focus stays where it was | ✅ the overlay never asks for focus; if a window manager hands it over anyway, it is given straight back to the window that had it | ✅ |
| Stays on the monitor you pick (tray → **Move to next monitor**) | ✅ | ✅ |
| Bouncing on window tops | ✅ all windows | ⚠️ only X11 windows; native Wayland apps are invisible to it |
| Cursor tracking outside the game's own controls | ✅ | ⚠️ over a native Wayland app the position freezes where the pointer left; after 1.5 s the overlay uses its own last pointer event instead, and any movement over an X11 window or the game's controls resumes tracking |
| Global shortcuts Ctrl+Alt+G/N/B | ✅ X11 grabs | ⚠️ through the desktop portal on GNOME 48+ and KDE Plasma 6: the first run asks you to allow the shortcuts. Untested on a real Wayland session; if they don't arrive, add custom shortcuts running `deskarcade --signal toggle`, `… next` and `… summon` |
| Tray menu | ✅ AppIndicator on KDE, XFCE and GNOME with the AppIndicator extension; stock GNOME has no tray, use the ☰ button on the scoreboard | same |

**No tray? Use the ☰ menu.** Stock GNOME ships without a system tray: there is no `org.kde.StatusNotifierWatcher`
on the session bus, so the AppIndicator icon has nowhere to appear. Desk Arcade checks for the watcher when it
starts (a 1.5-second look at the session bus; it assumes a tray whenever it cannot tell) and, if there is none,
shows a one-time notice a few seconds after start-up: the ☰ button on the scoreboard opens the essentials of the
tray menu (game, pet, CPU difficulty, LAN, stats, shortcuts, hide, exit). Installing
`gnome-shell-extension-appindicator` and signing in again brings the tray icon back.

### Other Linux distributions

- **AppImage:** `chmod +x DeskArcade-<version>-x86_64.AppImage` and run it. Ubuntu 22.04+ needs
  `libfuse2` (`sudo apt install libfuse2`), or run with `APPIMAGE_EXTRACT_AND_RUN=1`. "Start when I sign in"
  and the Claude Code hook config record the AppImage file itself, so re-run them after moving the file.
- **Flatpak:** a manifest is in [`packaging/flatpak/`](packaging/flatpak/) for building it yourself. It
  is not on Flathub.

### macOS 14+ (experimental)

Install with [Homebrew](https://brew.sh):

```bash
brew install --cask bokhodirurinboev/tap/deskarcade
```

Or unzip `DeskArcade-<version>-macos-arm64.zip` (or `-x64`) and move `DeskArcade.app` to Applications.
The app is ad-hoc signed and not notarized, so the first launch needs **right-click → Open**.

> **Experimental:** the macOS layer compiles and is packaged by CI, but it has not been run on a Mac.
> Click-through, the window list, hotkeys and sound are unverified there. Reports are welcome.

### winget

Desk Arcade is submitted to the winget repository and waiting for review. Once it is accepted:
`winget install ImperiumGames.DeskArcade`. The manifest templates live in
[`packaging/winget/`](packaging/winget/).

## Updating

Desk Arcade checks GitHub Releases once a day (switch it off in the tray) and tells you when a newer
version exists. **Check for updates** in the tray menu (or the scoreboard's ☰ menu) does it immediately.

On the Windows installer, the Ubuntu `.deb` and the AppImage, the tray and the ☰ menu then offer
**Install version X.Y.Z**: the game downloads the matching file into its updates folder
(`~/.config/DeskArcade/updates`, `%APPDATA%\DeskArcade\updates` on Windows), shows the progress on the
scoreboard, checks the file's SHA-256 against GitHub's digest, and installs it.

| Install | What happens |
|---|---|
| Windows installer | The installer runs silently; the game closes and comes back updated |
| Ubuntu / Debian `.deb` | PolicyKit asks for your password (the same prompt as Software Updater); apt installs the package, which closes the running game, and the new version starts by itself (with the same `--profile`, if one was used). Dismiss the prompt and the `.deb` stays in the updates folder, which opens so you can double-click it or run `sudo apt install ./deskarcade_X.Y.Z_amd64.deb`; on a desktop without `pkexec` the game opens a terminal running `sudo apt-get install` instead |
| AppImage | The new file replaces the running one in place and starts; if its folder is read-only, the new AppImage is saved next to it under its versioned name and the game says where |
| Flatpak | Updates come through `flatpak update` once the app is on Flathub; until then take the `.deb` or the AppImage from the release page |
| A copy you unpacked yourself, macOS | The download page opens |

Settings, high scores and stats are never touched by an update.

## Claude Code integration

Desk Arcade can show whether Claude Code is working, done or waiting for you, chime when it finishes,
and tell you how long you waited.

1. Right-click the tray icon and choose **Copy Claude Code hook config**.
2. Merge the copied JSON into `~/.claude/settings.json`.

| Hook | Command | Scoreboard |
|---|---|---|
| `UserPromptSubmit` | `--signal working` | Amber, "Claude working · 3:12" counting up |
| `Stop` | `--signal done` | Green, "Claude done after 3:12" and a chime |
| `Notification` | `--signal attention` | Red, "Claude needs you" |

In **tray → Claude Code** you can also let the overlay appear by itself when Claude starts working, hide it
when Claude finishes or needs you, or just **pause the game** until you click it. The "done" notice also
sums up the session: "Claude worked 12:03, you played 4:10". Playing while Claude works earns the
"Pair programmer" achievement.

## Play while a command runs

Put `arcade` in front of a build, a test run or a deploy. The command runs in your terminal as usual, with
its output and exit code, while the overlay comes up and the scoreboard shows it running ("dotnet test ·
1:12"). When it ends you get a chime and **Passed** or **Failed** with the exit code, how long it took and
how long you played.

```powershell
arcade dotnet test                           # Windows: the installer's "arcade" command (or Scoop's)
arcade "npm run build && npm test"           # quote a line with && or | to run it as one command
```

```bash
deskarcade --while make -j8                  # Linux and macOS
deskarcade --while "npm run build && npm test"
```

On Windows, **Add the "arcade" command to PATH** is a setup option (on by default); `arcade.cmd` sits next to
`DeskArcade.exe` either way. It is needed because Windows terminals do not wait for a program with a window
like Desk Arcade: `arcade.cmd` waits for it and passes the exit code on. The command's output goes straight
to the console window, so on Windows it cannot be piped (`arcade make | tee log` shows the output but
captures none of it). The Flatpak runs commands inside its sandbox, where your tools are missing: use the
`.deb` or the AppImage for this.

The scoreboard shows the command next to Claude Code's status, so you can use both at once. Playing when a
command finishes earns the "It's compiling" achievement.

## Building from source

**Requirements:** the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). The Windows
installer also needs [Inno Setup 6](https://jrsoftware.org/isinfo.php); the Ubuntu package needs
`dpkg-deb` (Ubuntu/Debian, WSL or a container).

```powershell
dotnet run                                  # run from source
dotnet test tests/DeskArcade.Tests          # unit tests

# Windows
.\build.ps1                                 # dist\DeskArcade.exe
.\build-installer.ps1                       # installer\Output\DeskArcade-Setup-<version>.exe
.\build-installer.ps1 -SelfContained        # ...-standalone.exe
.\build-installer.ps1 -Arch arm64 -SelfContained

# Linux
.\build-linux.ps1                           # from Windows: publishes, then packages inside WSL
packaging/linux/build.sh                    # on Ubuntu itself
packaging/linux/build-appimage.sh <publish-dir> <version> x86_64 dist-linux
```

Releases are built and published by GitHub Actions when a `vX.Y.Z` tag is pushed;
[docs/RELEASING.md](docs/RELEASING.md) covers every artifact, code signing, winget and Flathub.

### Development flags

| Flag | Purpose |
|---|---|
| `--game <id>` | Start on a specific game: `hoops`, `archery`, `juggle`, `golf`, `bugs`, `cans`, `bricks`, `bubbles`, `hockey`, `pong`, `clay`, `whack`, `plinko`, `tower`, `slingshot`, `checkers`, `chess`, `connect4`, `tictactoe`, `reversi`, `gomoku`, `seabattle`, `durak`, `darts`, `toss`, `fishing`, `bowling`, `pool`, `pinball`, `memory`, `codebreaker`, `solitaire`, `lastcard`, `interns`, `blockfall`, `cannons`, `marble`, `sheep`, `pet` |
| `--demo` | The current game plays itself, for smoke tests without touching the mouse |
| `--profile <name>` | Run an isolated copy with its own lock, signal channel and settings, alongside the installed game |

## Project structure

| Path | Contents |
|---|---|
| `src/Engine` | Lightweight game engine: `Vec2`, ball physics, window-top platforms, sprites, effects, themes, the rival's ghost ball, `MiniGame` |
| `src/Games` | One class per game; `BoardGame` is shared by the grid games. Rules and physics without UI (`Draughts`, `ChessRules`, `LineRules`, `ReversiRules`, `GomokuRules`, `SeaBattle`, `HockeyTable`, `PongTable`, `GolfMatch`, `ArcheryMatch`, `DurakRules`, `DartsRules`, `PaperFlight`, `DiscTable`, `BowlingScore`, `PinballTable`, `FishFight`, `MemoryRules`, `CodeBreakerRules`, `SolitaireRules`, `LastCardRules`, `InternsWorld`, `BlockfallRules`, `CannonRules`, `MarbleRules`, `SheepRules`) are unit-tested |
| `src/Net` | `LanLink`: pairing and messages between two copies on the local network; `DuelChannel`: reliable, ordered events for turn-based duels; `OfficeBoard`: the opt-in leaderboard; `RoomLink`: rooms of up to four players by code, for Durak and Last Card |
| `src/RaceMode.cs`, `src/Daily.cs` | Score races over the LAN; the daily challenge |
| `src/Platform` | The OS layer behind `IDesktopPlatform`, with `Windows`, `Linux` and `Mac` implementations |
| `src/Loc.cs`, `src/Strings.*.cs` | Translation lookup and the Uzbek and Russian tables |
| `src/Stats.cs`, `src/Achievements.cs`, `src/StatsWindow.cs` | Counters, achievements and the stats window |
| `src/OverlayWindow.cs` | The transparent, topmost window: frame loop, input, signals |
| `src/TaskRunner.cs`, `packaging/windows/arcade.cmd` | `--while`: runs a command and reports it to the overlay |
| `tests/DeskArcade.Tests`, `tests/smoke.sh` | Unit tests; the start-and-quit check CI runs on real hardware |
| `installer/`, `packaging/` | Inno Setup script, `.deb`, AppImage, Flatpak, winget, Homebrew, Scoop and macOS packaging |
| `.github/workflows` | CI on every pull request, releases on version tags |

The UI is built with [Avalonia](https://avaloniaui.net/), so the games contain no OS-specific code.

### Adding a game

1. Subclass `MiniGame` and implement `Layout`, `Update`, `CollectHitShapes`, `PointerDown`, `Summon`
   and `Hud`, drawing into `Layer`.
2. Keep everything inside `Host.Arena`: it is a closed box, and window tops come from `Host.Platforms`.
3. `CollectHitShapes` decides which areas take the mouse; clicks everywhere else pass through.
4. Return `false` from `Update` when nothing moves, so the overlay can stop rendering.
5. Wrap visible text in `L.T` / `L.F` and add it to both translation tables; report counters through
   `Host.Stats`.
6. Register the game in `OverlayWindow.Start()` and add a hint in `HintFor`.

## Troubleshooting

| Problem | What to try |
|---|---|
| Nothing appears | Press **Ctrl+Alt+G** or left-click the tray icon. On a multi-monitor setup, use **Move to next monitor**. |
| A ball or the pet is out of reach | **Ctrl+Alt+B** brings it to the cursor; **Reset positions** restores the defaults. |
| Shortcuts do nothing on Ubuntu | On Wayland, allow them when the portal asks, or set up custom shortcuts as described above. |
| No sound on Ubuntu | Install `libpulse0`. It works with PulseAudio and PipeWire. |
| The game crashed | Details are in `crash.log` in the settings folder. |

## Code signing policy

Free code signing provided by [SignPath.io](https://signpath.io), certificate by
[SignPath Foundation](https://signpath.org).

Windows releases (`DeskArcade.exe` and the `DeskArcade-Setup-*.exe` installers) are built by GitHub
Actions from this repository and signed only after a maintainer approves each signing request.

| Role | Who |
|---|---|
| Committers and reviewers | [Bokhodir Urinboev](https://github.com/BokhodirUrinboev) |
| Approvers | [Bokhodir Urinboev](https://github.com/BokhodirUrinboev) |

## Privacy

Desk Arcade does not transfer any information to other networked systems unless you ask it to, with one
exception you can switch off:

- **Update check:** once a day it asks GitHub (`api.github.com`) for the latest release of this
  repository. The request carries nothing but the app version. Turn it off with **Check for updates
  automatically** in the tray menu.
- **LAN play:** only when you host or join a game does it talk to other computers on your local network
  (UDP port 47820). It sends your user name and the game moves, and nothing leaves the local network.
- **Durak and Last Card rooms:** only when you create or join a room does it talk to other computers on your local
  network (UDP port 47822). It sends your user name and the game, and nothing leaves the local network.
- **Office leaderboard:** off unless you turn it on. While it is on, it broadcasts your user name and
  today's scores to your local network (UDP port 47821) every 20 seconds, and nothing leaves the local
  network.

There is no account, no telemetry and no analytics. Settings, scores and stats stay on your computer.

## Third-party software

Desk Arcade is built on [Avalonia UI](https://github.com/AvaloniaUI/Avalonia), SkiaSharp, HarfBuzzSharp,
ANGLE, MicroCom.Runtime, Tmds.DBus.Protocol and the .NET runtime, all under permissive licenses. Full
versions, copyright notices and license texts are in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md), which ships with every package.

## License

Desk Arcade is released under the [MIT License](LICENSE).

Copyright © 2026 Imperium Games.
