# Roadmap: Desk Arcade 1.5.0

1.4.0 shipped on 2026-09-18 and 1.4.1 fixed the Air Hockey corner trap and LAN game sync the same day (their
roadmaps are in the git history). 1.5.0 is about **seeing what the other player does**: every LAN game now
shows the rival's ball, arrow, puck or clicks, not just their score. It also adds Pong, an office leaderboard,
themes, a break reminder and three more pets.

Work happens on `feature/roadmap-1.5`. Each item says how it was verified; the last sections list what this
machine could not check and ideas for later. "Demo" means two copies on one PC (`--profile`) playing by
themselves (`--demo`).

## See the other player

- [x] **Ghost markers in the races.** Bubble Pop, Whack-a-Bug and Tower Stack send each pop, whack and drop
  as a position on the screen (`ga|x|y|points`, handled by `LanLink` beside the game messages). The other
  screen draws a red ring there, with the points. *Verified: loopback unit test; demo run on screen.*
- [x] **Tower Stack race.** Starting a tower starts the rival's; the height is the score.
  *Verified: demo run (both towers passed 20 blocks, the rival's height showed under the scoreboard).*
- [x] **Mini Golf duel.** Match play over 9 holes, stroke by stroke on each player's own course; the ball
  streams relative to the cup and shows as a ghost around the other player's cup. Rules in `GolfMatch`.
  *Verified: unit tests for turns, waiting and scoring; demo run (holes decided, ghost balls on screen).*
- [x] **Archery duel.** One arrow each, ten apiece, same wind (the host picks it); the arrow streams relative
  to the bow and flies from the other player's bow as a ghost. Rules in `ArcheryMatch`.
  *Verified: unit tests; demo run (two full matches, ghost arrows and "+10" popups on screen).*
- [x] **Reliable duel events.** `DuelChannel` numbers events and re-sends them until acknowledged, so both
  screens apply the same strokes and arrows in the same order. *Verified: unit tests with 30% and 60% loss.*

## New games and modes

- [x] **Pong.** Paddles on the left and right screen edges; the hit point sets the angle and every return
  speeds the ball up. Against a CPU that gets sharper with each win, or a co-worker (host-run, like Air
  Hockey). Physics in `PongTable`. *Verified: unit tests (returns, misses, wall prediction, CPU levels);
  solo and LAN demo runs.*
- [x] **Air Hockey best of 3.** LAN matches form a series; the scoreboard shows it and the series winner
  gets a fanfare and an achievement. *Verified: LAN demo run (series shown on both screens).*
- [x] **Air Hockey table without UI.** The physics moved to `HockeyTable`. *Verified: unit tests that the CPU
  never covers a cornered puck and frees it within 4 seconds, a mallet stops at a pinned puck, and goals score.*

## Everyday use

- [x] **Office leaderboard** (opt-in). While sharing is on, each copy broadcasts the user name and today's
  scores on UDP 47821 every 20 seconds; **tray → Office leaderboard** ranks everyone. Stats now keep
  today's counters beside the all-time ones. *Verified: unit tests for the wire format, hostile input,
  ranking and the day rollover; not yet seen with two PCs.*
- [x] **Break reminder.** After 15–60 minutes of play (five minutes away resets it), and an optional
  "Claude is done · back to work". *Verified: builds; not timed on screen.*
- [x] **Themes.** Classic, Neon, Retro, Halloween, Winter and Seasonal recolour mallets, paddles, puck, the
  basketball and the golf ball. *Verified: unit test for the seasonal calendar; the recolouring is not
  checked on screen yet.*
- [x] **More pets.** Bunny, penguin and fox. *Verified: builds; not checked on screen yet.*
- [x] **Six achievements** for the series, Pong, and the golf and archery duels (53 in all).

## Not verified here

| What | Needs |
|---|---|
| Every LAN game between two real PCs, played by two people | Two PCs on one network |
| The office leaderboard with more than one person | Two PCs with sharing on |
| Themes and the new pets on screen, the break reminder firing | A few minutes of play |

## Ideas for more mini games

Games that suit the overlay: quick to start, played with the mouse (the overlay never takes the keyboard),
and using the windows and taskbar as the playing field. LAN notes say how each could work over the network.

| Idea | How it plays | LAN |
|---|---|---|
| **Paper Toss** | Flick a crumpled paper ball into a bin on a window top; a desk fan blows a different wind each throw | Race, or H-O-R-S-E-style turns |
| **Curling** | Slide stones along the taskbar toward a target painted on the floor; knock the rival's stones away | Turns with ghost stones, like the golf duel |
| **Darts** | A board on the screen; the aim wobbles while you hold, 501 counting down to a double | Turns, one dart each |
| **Bowling** | Roll a ball along the taskbar at pins standing on a window top; 10 frames with spares and strikes | Frame by frame, pins shown as ghosts |
| **Pool** | A table drawn over the screen, cue by dragging back from the white ball (Air Hockey's physics, with friction and pockets) | Turns; host runs the balls |
| **Pinball** | Flippers in the bottom corners, bumpers on window tops, the taskbar as the drain | Score race |
| **Window Tetris** | Blocks fall from the top and settle on window tops as well as the taskbar | Race; cleared lines send garbage to the rival |
| **Fishing** | Cast into a pond along the taskbar and reel in with well-timed clicks; rare fish are worth more | Race for the biggest catch |
| **Memory** | Pairs of cards laid over the screen; flip two at a time | Turns, both see every flipped card |
| **Code Breaker** | Guess a hidden four-colour code from black and white pegs (Mastermind) | Each sets a code for the other |
| **Asteroids** | Rocks drift and bounce around the closed box; steer a ship with the mouse and click to fire | Co-op: two ships, one field |
| **Fetch** | Throw a ball for the desktop pet, which runs, jumps between windows and brings it back | — |
