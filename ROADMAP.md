# Roadmap: Desk Arcade 1.6.0

1.5.0 shipped on 2026-09-18 (its roadmap is in the git history). 1.6.0 brings **Durak for up to four
players**, makes Chess and Checkers **beatable**, plays Checkers by **Russian rules**, and gives every pet
**its own voice and trick**.

Work happens on `feature/roadmap-1.6`. Each item says how it was verified. "Demo" means copies on one PC
(`--profile`) playing by themselves (`--demo`).

## Durak

- [x] **Rules** (`DurakRules`): podkidnoy for 2–6 players; trump from the bottom card, the lowest trump
  leads, throw-ins of ranks on the table up to six (five in the first bout) and never more than the defender
  can answer, take or beat, draw back up to six attacker first, the last one holding cards is the durak.
  *Verified: unit tests, including 100 complete computer games (2, 3, 4 and 6 players) that account for all
  36 cards after every move.*
- [x] **Rooms** (`RoomLink`): the host creates a room with a four-letter code; up to three others join from
  the list or by code (and IP when broadcasts are blocked). Several rooms can share a network. The host
  runs the game and sends each player only their own hand. *Verified: loopback unit tests (three join, a
  fourth is turned away, messages reach the right seat, a leaver is noticed); a demo game with three copies
  and a computer player, played to the end.*
- [x] **Table and setup window.** Cards, opponents with card counts, deck and trump, Take and Done; the
  setup window creates or joins rooms. Computer players fill empty seats and take over for anyone who drops
  out. *Verified: demo runs and screenshots.*

## Board games

- [x] **Russian rules for Checkers**: men capture backwards, flying kings (landing where they can go on
  capturing), crowning mid-capture, free choice of capture, Turkish strike, 15-move draw. When several
  capture routes end on one square, the player clicks each landing. *Verified: unit tests for every rule; a
  demo game played to a win.*
- [x] **CPU levels for Chess and Checkers**: Easy, Medium, Hard, Expert. New players start on Easy, a win
  moves the CPU up, two losses in a row move it down, and **tray → CPU difficulty** sets it. Easy looks one
  move ahead and plays a random move almost half the time. *Verified: builds; the Checkers CPU answers in
  milliseconds even in a king endgame (unit test).*

## Pets

- [x] **Voices**: meow (and a purr), bark, quack, squeak, honk and yip, synthesized like every other sound.
- [x] **Natural voices**: a source-filter voice (a buzzing source with jitter and rasp, shaped by moving
  formants) instead of plain tones, and several calls per animal for greeting, surprise, calling, complaining
  and hunting. *Verified: unit test that every clip is synthesized, finite and unclipped.*
- [x] **Animal behaviour over time**: habits per animal (grooming, kneading, sniffing, scratching, preening,
  flopping, braying), a gait per animal, stalking and pouncing on the cursor, reactions to being thrown, and
  moods: excitement (zoomies), boredom (calls for you) and tiredness (yawns, naps, snores). *Verified: demo
  run; the new behaviours still need a look on screen.*
- [x] **Tricks** on right-click, and now and then by themselves: the cat stretches, the dog chases its
  tail, the duck flaps, the bunny does a binky, the penguin belly-slides, the fox pounces. *Verified: demo
  run; the sounds and tricks still need a listen and a look on screen.*

## Not verified here

| What | Needs |
|---|---|
| Durak rooms across real PCs, played by people | Two to four PCs on one network |
| How the pet voices sound | Speakers |

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
| **More card games** | Fool's cousins on the same room code: Perevodnoy (pass the attack on), Blackjack against the house, Crazy Eights | Rooms, like Durak |
| **Code Breaker** | Guess a hidden four-colour code from black and white pegs (Mastermind) | Each sets a code for the other |
| **Asteroids** | Rocks drift and bounce around the closed box; steer a ship with the mouse and click to fire | Co-op: two ships, one field |
| **Fetch** | Throw a ball for the desktop pet, which runs, jumps between windows and brings it back | — |
