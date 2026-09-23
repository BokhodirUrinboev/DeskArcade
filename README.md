# Desk Arcade

Mini-games that play **on top of your desktop** in a transparent overlay. Shoot hoops while a build, a
deploy or Claude Code is working, and still see everything underneath.

**Platforms:** Windows 10/11 (x64, ARM64) · Ubuntu 22.04/24.04 (amd64, arm64) · other Linux via AppImage
or Flatpak · macOS 14+ (experimental) &nbsp;·&nbsp; **License:** [MIT](LICENSE) &nbsp;·&nbsp;
**Version:** 1.7.1

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
- **33 games, 82 achievements**, a daily challenge, play-time stats, and English, Uzbek and Russian text.
- **Play with the person at the next desk** over the local network: Air Hockey (best of 3), Pong,
  H-O-R-S-E, Mini Golf and Archery duels, board games, Sea Battle and score races. You see what the other
  player does: their ball, arrow or puck, and a marker wherever they pop, whack or stack.
- **Office leaderboard** (opt-in): today's best scores of everyone on the network who shares theirs.
- **Themes** (including seasonal ones), a **break reminder**, and six desktop pets.
- **No assets to download.** Every sound is synthesized and all artwork is drawn in code.

## Games

| Game | How to play |
|---|---|
| 🏀 **Hoops** | Grab the ball, flick it and let go. 2 points, 3 from long range, +1 for a swish, 1 for a dunk. Three in a row set the ball on fire (points ×2); at five the hoop moves. Drag the backboard to move the hoop. |
| 🎯 **Archery** | Press on the bow, drag backwards and release. 10 arrows a round. Rings score 2–10, balloons 5, gold balloons 15. From round 2 there is wind and the targets move. Right-drag the bow to move it. |
| ⚽ **Keepy-Uppy** | Click the ball to kick it up; where you click sets the direction. Don't let it touch the ground or a window top. Stars are +3, and gravity grows the longer you juggle. |
| ⛳ **Mini Golf** | Drag back from the ball and release to putt. The cup sits on the taskbar or a window top and follows that window. Nine holes, each starting where the last ended, scored against par. |
| 🐞 **Bug Squash** | Click the sleeping bug to start a 30-second round. Squash bugs crawling along the taskbar and window tops. Quick squashes build a combo up to ×4, golden bugs are worth 5, and ladybugs are features: squashing one costs 5. |
| 🥫 **Can Knockdown** | Throw the ball from behind the dashed line to knock the pyramid off its shelf. 3 balls a stack, the golden can is worth 3. Clearing a stack earns a bonus and a bigger stack; failing ends the game. |
| 🧱 **Brick Breaker** | Click the paddle to launch; while the ball is in play the paddle follows your mouse. The ball rebounds off the sides and top; the floor costs one of your 3 balls. Gold bricks are worth 10, steel bricks take two hits. |
| 🫧 **Bubble Pop** | Click the "pop me" bubble to start. Clicking a bubble splits it in two, and the smallest pop. Smaller bubbles score more, quick pops build a combo up to ×3, and leftover seconds become bonus points. |
| 🏒 **Air Hockey** | Drag your blue mallet on the left half and hit the puck into the right-hand goal while the CPU defends. The puck bounces off every other edge. First to 7 wins, and each win makes the CPU faster. |
| 🏓 **Pong** | Pong on the edges of your screen: drag your paddle up and down the left edge and get the ball past the CPU's paddle on the right. Where the ball hits the paddle sets its angle, and every return speeds it up. First to 7; each win makes the CPU sharper. |
| 🎯 **Clay Shooting** | Click the trap machine to start 15 pulls. Clays arc across the screen and bounce off the edges; click one to break it. Breaking two with one click is a DOUBLE, golden clays are worth 5, and a clay that lands is a miss. |
| 🔨 **Whack-a-Bug** | Bugs peek out from behind your window tops, the taskbar and the screen edges for a moment. Whack them: 1 point, fast bugs 2, golden 5, combo up to ×3. Ladybugs are features again: −5. |
| 🎲 **Plinko** | Click the strip at the top of the board to drop a disc through the pegs. Slots score 10 to 250, and the gold middle slot is the jackpot. Ten discs a round. Right-drag the board's header to move it. |
| 🗼 **Tower Stack** | Click the sliding block to drop it on the tower. The overhang is cut off, so the tower narrows; a perfect drop keeps the full width, and three in a row widen it. Miss completely and the game ends. |
| 🪨 **Slingshot** | Pull the stone back and let go to knock a tower of blocks off a window top. 3 stones a tower, 10 points a block, 50 for each spare stone. Clear the tower for a bigger one. |
| ♟️ **Checkers** | Russian rules (shashki). Click one of your pieces, then the square it should move to (for a multi-jump, the last square; if several routes end there, click each landing in turn). Men move forward but capture backward too; kings fly any distance along a diagonal. Capturing is compulsory, but you choose which capture. A man that reaches the far row in the middle of a capture is crowned and carries on capturing as a king. Play the CPU (it starts on Easy and gets stronger as you win; **tray → CPU difficulty** sets it), or a co-worker over the LAN. Right-drag the board to move it. |
| ♞ **Chess** | Click a piece, then its square. Full rules: castling, en passant, check, mate, stalemate and the 50-move rule; pawns always promote to a queen. Play the CPU (Easy, Medium, Hard or Expert: it starts on Easy, moves up a level each time you win and back down if you lose twice running) or a co-worker over the LAN. Right-drag the board to move it. |
| 🔴 **Connect Four** | Click a column to drop a disc; four in a row (across, down or diagonal) wins. Play the CPU or a co-worker. |
| ❌ **Tic-tac-toe** | Click a square; three in a row wins. The CPU is good but slips now and then. |
| 🚢 **Sea Battle** | Your fleet is on the left, the enemy's waters on the right. Click your grid to shuffle your ships, the enemy grid to start, then fire. A hit shoots again; sink all five ships to win. Against the CPU or a co-worker. |
| 🃏 **Durak** | The Russian card game (podkidnoy), for 2–4 players: against 1–3 computer players, or co-workers in a room you create (see below). Click a card to attack, throw in or beat a card (click a table card first to pick which one); **Take** gives up the bout, **Done** ends your throwing in. The last player holding cards is the durak. |
| 🗑️ **Paper Toss** | Grab the crumpled paper in the corner and flick it into the wastebasket on a window top or the far end of the taskbar. A desk fan blows a new wind every throw, stronger the longer your streak. A basket is 1 point, a swish 2, and the bin moves; one miss ends the run. |
| 🎯 **Darts** | 501, double out. Press on the board and hold: the aim wobbles, steady at first, then worse the longer you wait. Let go to throw. Three darts a turn; going below zero, leaving 1 or finishing on anything but a double is a bust. The scoreboard suggests a checkout when you can finish, and your best is the fewest darts. |
| 🎳 **Bowling** | A lane along the taskbar. Drag back from the ball and let go; the angle and length of the drag set the line and the speed. Ten frames with strikes, spares and the 10th-frame bonus balls, scored on the sheet above the lane. |
| 🎱 **Pool** | A table over the screen. Drag back from the cue ball to aim (the line shows where the first ball will go) and let go to shoot. Pot all 15 balls in as few shots as you can; potting the cue ball costs a shot. |
| 🪩 **Pinball** | The whole screen is the table and the taskbar is the drain. Click the ball to serve it, then press and hold near a flipper to raise it (right-click flips both). Bumpers in the middle and on your window tops score 100; light all three top lanes to raise the multiplier. Three balls a game. |
| 🎣 **Fishing** | A pond along the taskbar. Drag back from the rod and let go to cast. Wait through the nibbles and click when the bobber goes under. Then hold to reel and let go when the tension bar turns red, or the line snaps. Two minutes a round; perch, carp, pike, catfish and a rare golden trout. |
| 🃏 **Memory** | 24 cards face down. Flip two at a time to find the 12 pairs; your best is the fewest moves. |
| 🟢 **Code Breaker** | Crack a hidden code of four colours (repeats allowed) in ten guesses. Pick a colour and click a hole, or click a hole to cycle it, then **Check**: a black pin is a right colour in the right place, a white pin a right colour in the wrong place. Each colour also has a symbol for colour-blind play. |
| 🂡 **Solitaire** | Klondike, draw one. Click the stock to turn a card. Click a card to send it where it fits (home to its foundation first), or drag a card or a face-up run onto the pile you want. **Undo** takes a move back; **New deal** asks once more before it throws the game away. When every card is face up, the rest go home by themselves. Your best is the fewest moves. |
| 🟥 **Last Card** | An UNO-style game for 2–4 players. Play a card of the colour on the pile, or the same number or symbol. **Skip**, **Reverse** and **+2** hit the next player; a **Wild** lets you pick the colour, and a **Wild +4** is allowed only when you hold nothing of the colour on the pile. Can't play? Click the pile to draw; a card that fits can go straight down, or **Pass**. Click **Last card!** when you're down to one card (or before, with two), or you draw two as soon as the next player moves. The first to play their last card wins. Every colour also has a shape in the corners (circle, triangle, square, diamond). Against 1–3 computer players, or co-workers in a room. |
| 🧑‍💼 **Interns** | Click the hatch and a line of office interns drops out, onto a window top or just above the taskbar, and walks wherever their feet take them. Get enough of them to the **EXIT** door on the taskbar, past open manholes and drops too high to survive. Pick a tool on the toolbar (right-click an intern to switch tools) and click an intern: an **Umbrella** for a safe fall, a **Blocker** who turns the others round (click them again to let them go), or a **Builder** who lays a staircase. Drag a window and everyone standing on it rides along, so a window can be a bridge. Each level has more interns and manholes and fewer spare tools; your best is the highest level cleared. **Ctrl+Alt+B** moves the toolbar to the cursor. |
| 🐱 **Desktop Pet** | Not a game: a cat (or a dog, duck, bunny, penguin or fox: **tray → Pet**) that walks along your taskbar and window tops, follows the cursor, jumps between windows and sleeps when left alone. Click to pet it, drag to carry and throw it, **right-click for its trick**. Each animal has its own voices (the cat meows, trills, purrs, hisses and chatters at birds; the dog barks, woofs, whines and pants; the duck quacks; the bunny squeaks, grunts and thumps; the penguin brays; the fox barks "wow-wow", gekkers and screams), its own walk (the bunny hops, the duck and penguin waddle) and its own trick: the cat stretches, the dog chases its tail, the duck flaps, the bunny does a twisting hop, the penguin belly-slides and the fox pounces. Left alone it keeps busy the way its animal does: cats groom, knead and stalk the cursor, dogs sniff, scratch and wag, ducks preen and follow you, bunnies flop over, penguins throw back their heads and bray. Its mood changes over time: lots of play gives it the zoomies, ignoring it makes it call for you, and it yawns, naps and snores when tired. |

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
| Hoops | H-O-R-S-E: make a shot and the other player has to make it from the same spot, or take a letter. Each of you sees the other's ball fly as a faded ghost ball |
| Mini Golf | Match play over 9 holes, taking turns stroke by stroke on your own courses. Their ball shows up as a ghost around your cup; a hole goes to the better score against par |
| Archery | Take turns, one arrow each, ten apiece, in the same wind. Their arrow flies from your bow as a ghost |
| Checkers, Chess, Connect Four, Tic-tac-toe | Turns over the network; the guest sees the board from their side |
| Sea Battle | Each fleet stays on its own PC; only shots and hits cross the network |
| Durak, Last Card | The host's table opens a room and your co-worker's copy joins it by itself; the host starts the game from the table, with or without computer players (the room also takes more co-workers, see below) |
| Bubble Pop, Whack-a-Bug, Tower Stack, Bowling, Fishing, Pinball, Paper Toss | Race: start a round (in Pinball a three-ball game, in Paper Toss a run) and theirs starts too. You see their live score, a red ring wherever they pop, whack or stack, and who won |

**Rooms** for Durak and Last Card are separate from the two-player link, for up to four people: **tray → Play
over LAN → Durak with co-workers…** or **Last Card with co-workers…** (or the button on the game's table). Each
room plays one game, and the list only shows rooms for that game. One player clicks **Create a room** and reads out the
four-letter code; the others pick the room from the list or type the code (plus the host's IP address if
the network blocks broadcasts). The host chooses 2, 3 or 4 seats and starts; computer players fill the empty
seats and take over for anyone who drops out. Several rooms can run on one network. Rooms use UDP port 47822.

**Send** in the same menu pops a quick emote ("gg", "One more?"…) up on the other screen. Windows asks
once whether to allow Desk Arcade on private networks; say yes on both PCs.

## Scoreboard, stats and achievements

The scoreboard is a small pill showing the game icon, score and best. **Click it** to open the full
board with a tab for every game; it shrinks back shortly after the mouse leaves. Drag it anywhere.

**Daily challenge:** one task a day, the same for everyone ("Make 15 baskets in Hoops"), shown in the tray
with your progress; click it to jump to the game. Finish it on consecutive days to build a streak.

**Stats & achievements** in the tray menu (or `DeskArcade --signal stats`) opens a window with time
played and best score per game, and all 82 achievements with their progress. Stats live in
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
| Tray icon, left-click | Show or hide |
| Tray icon menu | Game, pet, CPU difficulty, theme, break reminder, office leaderboard, volume, language, Claude Code options, updates, stats, monitor, reset, exit |

On macOS the shortcuts are **Control+Option+G/N/B**. **Tray → Shortcuts…** changes the modifier keys and the
letters (Windows applies them at once; Linux and macOS from the next start).

**Tray → Theme** recolours the mallets, paddles, puck and balls: Classic, Neon, Retro, Halloween, Winter,
or **Seasonal** (Halloween in October, Winter in December and January, Classic the rest of the year).

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
| Stays on the monitor you pick (tray → **Move to next monitor**) | ✅ | ✅ |
| Bouncing on window tops | ✅ all windows | ⚠️ only X11 windows; native Wayland apps are invisible to it |
| Global shortcuts Ctrl+Alt+G/N/B | ✅ X11 grabs | ⚠️ through the desktop portal on GNOME 48+ and KDE Plasma 6: the first run asks you to allow the shortcuts. Untested on a real Wayland session; if they don't arrive, add custom shortcuts running `deskarcade --signal toggle`, `… next` and `… summon` |
| Tray menu | ✅ AppIndicator | ✅ AppIndicator |

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
version exists. **Check for updates** in the tray menu does it immediately. On Windows installs the tray
then offers **Install version X.Y.Z**: it downloads the matching installer, checks its SHA-256 against
GitHub's, and runs it silently; the game closes and comes back updated. Elsewhere it opens the download
page.

| Platform | How | What happens |
|---|---|---|
| Windows | Run the newer setup | Closes the running game, replaces the files in the same folder, keeps your shortcut and autostart choices, and restarts the game after silent updates. Downgrades are refused. |
| Ubuntu | `sudo apt install ./deskarcade_x.y.z_amd64.deb` | apt replaces the older version and closes the running game first. Start it again afterwards. |
| AppImage | Replace the file | Nothing else to do. |

Settings, high scores and stats are never touched by an update. They live in `%APPDATA%\DeskArcade` on
Windows and `~/.config/DeskArcade` on Linux and macOS.

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
| `--game <id>` | Start on a specific game: `hoops`, `archery`, `juggle`, `golf`, `bugs`, `cans`, `bricks`, `bubbles`, `hockey`, `pong`, `clay`, `whack`, `plinko`, `tower`, `slingshot`, `checkers`, `chess`, `connect4`, `tictactoe`, `seabattle`, `durak`, `darts`, `toss`, `fishing`, `bowling`, `pool`, `pinball`, `memory`, `codebreaker`, `solitaire`, `lastcard`, `interns`, `pet` |
| `--demo` | The current game plays itself, for smoke tests without touching the mouse |
| `--profile <name>` | Run an isolated copy with its own lock, signal channel and settings, alongside the installed game |

## Project structure

| Path | Contents |
|---|---|
| `src/Engine` | Lightweight game engine: `Vec2`, ball physics, window-top platforms, sprites, effects, themes, the rival's ghost ball, `MiniGame` |
| `src/Games` | One class per game; `BoardGame` is shared by the grid games. Rules and physics without UI (`Draughts`, `ChessRules`, `LineRules`, `SeaBattle`, `HockeyTable`, `PongTable`, `GolfMatch`, `ArcheryMatch`, `DurakRules`, `DartsRules`, `PaperFlight`, `DiscTable`, `BowlingScore`, `PinballTable`, `FishFight`, `MemoryRules`, `CodeBreakerRules`, `SolitaireRules`, `LastCardRules`, `InternsWorld`) are unit-tested |
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
