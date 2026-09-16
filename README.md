# Desk Arcade

Mini-games that play **on top of your desktop** in a transparent overlay, on Windows and Ubuntu.
Shoot hoops while a build, a deploy or Claude Code is working, and still see everything underneath.

**Platforms:** Windows 10/11 (x64) · Ubuntu 22.04/24.04 (x64) &nbsp;·&nbsp; **License:** [MIT](LICENSE)
&nbsp;·&nbsp; **Version:** 1.3.0

---

## Contents

- [Highlights](#highlights)
- [Games](#games)
- [Controls](#controls)
- [Installation](#installation)
- [Updating](#updating)
- [Claude Code integration](#claude-code-integration)
- [Building from source](#building-from-source)
- [Project structure](#project-structure)
- [Troubleshooting](#troubleshooting)
- [Third-party software](#third-party-software)
- [License](#license)

## Highlights

- **Your monitor is a closed box.** Balls, pucks, bubbles and cans bounce off the left, right and
  top edges of the screen, and arrows stick into them. Nothing flies off-screen and vanishes. The floor
  is the top of the taskbar (Windows) or dock (Ubuntu).
- **Clicks pass through.** The overlay covers one monitor, but only the game objects (ball, hoop,
  bow, scoreboard) take the mouse. Clicks anywhere else go straight to your windows.
- **Never steals focus.** You can keep typing in the terminal while you play.
- **Window tops are platforms.** Balls, bubbles and arrows land on the top edges of open windows and
  ride along when you drag a window. You can turn this off in the tray menu.
- **Idle means idle.** When nothing is moving, rendering stops and CPU use drops to almost zero.
- **No assets to download.** Every sound is synthesized and all artwork is drawn in code.

## Games

| Game | How to play |
|---|---|
| 🏀 **Hoops** | Grab the ball, flick it and let go. Scoring is 2 points, 3 from long range, +1 for a swish and 1 for a dunk. Three baskets in a row set the ball on fire (points ×2); at five the hoop starts moving. Drag the backboard to move the hoop. |
| 🎯 **Archery** | Press on the bow, drag backwards and release. Each round has 10 arrows. Rings score 2–10, balloons 5 and gold balloons 15. From round 2 there is wind (shown under the bow) and targets move. Right-drag the bow to reposition it. |
| ⚽ **Keepy-Uppy** | Click the ball to kick it up; where you click sets the direction. Don't let it touch the ground or a window top. Stars are worth +3. Gravity grows the longer you juggle. |
| ⛳ **Mini Golf** | Drag back from the ball and release to putt. The cup sits on the taskbar or on a window top and follows that window. A round is 9 holes, each starting where the last one ended, scored against par. |
| 🐞 **Bug Squash** | Click the sleeping bug to start a 30-second round. Squash bugs crawling along the taskbar and window tops before they fly off. Quick squashes build a combo up to ×4 and golden bugs are worth 5. Ladybugs are features: squashing one costs 5. |
| 🥫 **Can Knockdown** | Throw the ball from behind the dashed line to knock the pyramid off its shelf. You get 3 balls per stack and the golden top can is worth 3. Clearing a stack earns a bonus for spare balls and a bigger stack; failing ends the game. |
| 🧱 **Brick Breaker** | Click the paddle to launch the ball. While it is in play the paddle follows your mouse. The ball rebounds off the sides and top of the screen; if it touches the floor you lose one of your 3 balls. Gold bricks are worth 10, and steel bricks (from level 2) take two hits. Clearing a wall earns a bonus and a taller wall. |
| 🫧 **Bubble Pop** | Click the "pop me" bubble to start. Clicking a bubble splits it into two smaller ones, and the smallest pop. Smaller bubbles score more (1, 2, 3, 5) and quick pops build a combo up to ×3. Clear every bubble before the timer runs out; leftover seconds become bonus points. |
| 🏒 **Air Hockey** | Drag your blue mallet on the left half of the screen and hit the puck into the goal on the right edge while the CPU defends it. The puck bounces off every other edge. First to 7 wins, and each win makes the next CPU opponent faster. |

The scoreboard is a small pill showing the game icon, score and best. **Click it** to open the full
board with a tab for every game; it shrinks back shortly after the mouse leaves.

> **Brick Breaker note:** while a ball is in play, a strip along the bottom of the screen takes the
> mouse so the paddle can follow it on every desktop session. The strip goes away as soon as the ball
> is lost or you switch games.

## Controls

| Shortcut | Action |
|---|---|
| **Ctrl+Alt+G** | Show or hide the overlay |
| **Ctrl+Alt+N** | Next game |
| **Ctrl+Alt+B** | Bring the ball, bow, paddle or mallet to the cursor (a penalty stroke in Mini Golf) |
| Click the scoreboard | Open the game tabs |
| Drag the scoreboard | Move it |
| Tray icon, left-click | Show or hide |
| Tray icon menu | Game, sound, window-top bouncing, start at sign-in, monitor, reset, exit |

Every action is also available from the command line, which is useful for custom shortcuts and
scripts:

```
DeskArcade --signal toggle|next|summon|show|hide|quit
```

## Installation

### Windows

Run `DeskArcade-Setup-<version>.exe`. It installs for the current user, without an administrator
prompt, into `%LOCALAPPDATA%\Programs\Desk Arcade`.

- The regular installer needs the [.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
  and offers to open the download page if it is missing.
- The `-standalone` installer bundles the runtime and has no prerequisites.

### Ubuntu (22.04 / 24.04, x64)

```bash
sudo apt install ./deskarcade_<version>_amd64.deb
deskarcade            # or find "Desk Arcade" in the app grid
```

The package bundles its own .NET runtime; apt installs the few X11 libraries it needs. To remove it,
run `sudo apt remove deskarcade`.

On Ubuntu the overlay window covers the monitor's working area (everything except the top bar and
dock) rather than the whole monitor. GNOME moves a larger window to another monitor, so this keeps the
overlay where you put it. The games only use the working area on every platform, so nothing changes
on screen.

#### Ubuntu notes

| Topic | Xorg session | Wayland session (default) |
|---|---|---|
| Overlay, click-through, sound | ✅ | ✅ through XWayland |
| Multi-monitor: stays on the monitor you pick (tray → **Move to next monitor**) | ✅ | ✅ |
| Bouncing on window tops | ✅ all windows | ⚠️ only X11 windows; native Wayland apps are invisible to it |
| Global shortcuts Ctrl+Alt+G/N/B | ✅ | ❌ Wayland keeps global keys to the compositor, see below |
| Tray menu | ✅ AppIndicator | ✅ AppIndicator |

On Wayland, add the shortcuts in **Settings → Keyboard → View and Customize Shortcuts → Custom
Shortcuts**, using commands such as `deskarcade --signal toggle`, `deskarcade --signal next` and
`deskarcade --signal summon`. The tray icon uses AppIndicator, which Ubuntu enables by default.

## Updating

Both packages upgrade in place.

| Platform | How | What happens |
|---|---|---|
| Windows | Run the newer `DeskArcade-Setup-x.y.z.exe` | Closes the running game, replaces the files in the same folder, keeps your shortcut and autostart choices, and restarts the game after silent updates. Downgrades are refused. |
| Ubuntu | `sudo apt install ./deskarcade_x.y.z_amd64.deb` | apt replaces the older version and closes the running game first. Start it again afterwards. |

Settings and high scores are never touched by an update. They are stored in `%APPDATA%\DeskArcade`
on Windows and `~/.config/DeskArcade` on Ubuntu.

## Claude Code integration

Desk Arcade can show on the scoreboard whether Claude Code is working, done or waiting for you, and
chime when it finishes. This is optional.

1. Right-click the tray icon and choose **Copy Claude Code hook config**.
2. Merge the copied JSON into `~/.claude/settings.json`.

This registers three hooks:

| Hook | Command | Scoreboard |
|---|---|---|
| `UserPromptSubmit` | `--signal working` | Amber, "Claude working" |
| `Stop` | `--signal done` | Green, "Claude done" and a chime |
| `Notification` | `--signal attention` | Red, "Claude needs you" |

The copied commands point at the installed executable (`/usr/bin/deskarcade` on Ubuntu). If the game
is not running, the hook exits immediately and does nothing.

## Building from source

**Requirements:** the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). The Windows
installer additionally needs [Inno Setup 6](https://jrsoftware.org/isinfo.php); the Ubuntu package
needs `dpkg-deb` (Ubuntu/Debian, WSL, or an `ubuntu` Docker container).

```powershell
dotnet run                                  # run from source (Windows or Linux)

# Windows
.\build.ps1                                 # dist\DeskArcade.exe
.\build-installer.ps1                       # installer\Output\DeskArcade-Setup-<version>.exe
.\build-installer.ps1 -SelfContained        # ...-standalone.exe with the runtime bundled

# Ubuntu package
.\build-linux.ps1                           # from Windows: publishes, then packages inside WSL
packaging/linux/build.sh                    # on Ubuntu itself
```

### Development flags

| Flag | Purpose |
|---|---|
| `--game <id>` | Start on a specific game: `hoops`, `archery`, `juggle`, `golf`, `bugs`, `cans`, `bricks`, `bubbles` or `hockey` |
| `--demo` | The current game plays itself, for smoke tests without touching the mouse |
| `--profile <name>` | Run an isolated copy with its own lock, signal channel and settings, alongside the installed game. Combine it with `--signal`, e.g. `DeskArcade --profile test --signal quit` |

### Releasing a new version

1. Bump `<Version>` in `DeskArcade.csproj` (for example `1.2.0` → `1.3.0`) and merge it.
2. Tag the commit and push the tag: `git tag v1.3.0 && git push origin v1.3.0`.

The [Release workflow](.github/workflows/release.yml) then builds both Windows installers and the
Ubuntu package and publishes them as a GitHub Release with generated notes. It refuses a tag that
does not match the csproj version. To build the packages without publishing, run the workflow by
hand from the Actions tab (or locally with `.\build-installer.ps1` and `.\build-linux.ps1`).

Two identifiers let upgrades recognise the previous version. **Never change them:**

- the installer `AppId` (`MyAppGuid` in `installer/DeskArcade.iss`)
- the Debian package name `deskarcade` (`packaging/linux/control.in`)

If a later Windows version stops shipping a file, add it to `[InstallDelete]` in the `.iss` script.

## Project structure

| Path | Contents |
|---|---|
| `src/Engine` | Lightweight game engine: `Vec2`, ball physics, window-top platforms, sprites, effects and the `MiniGame` base class |
| `src/Games` | One class per game |
| `src/Platform` | The OS layer behind `IDesktopPlatform`: click-through, window list, hotkeys, audio, autostart |
| `src/Platform/Windows` | Layered window with `WS_EX_TRANSPARENT` toggled under the cursor, Win32 hotkeys, waveOut audio |
| `src/Platform/Linux` | XShape input regions, `_NET_CLIENT_LIST_STACKING`, `XGrabKey`, PulseAudio |
| `src/OverlayWindow.cs` | The transparent, topmost Avalonia window: frame loop, input and signals |
| `installer/` | Inno Setup script (Windows) |
| `packaging/linux/` | `.deb` builder, desktop entry and maintainer scripts |

The UI is built with [Avalonia](https://avaloniaui.net/), so the games contain no OS-specific code.

### Adding a game

1. Subclass `MiniGame` and implement `Layout`, `Update`, `CollectHitShapes`, `PointerDown`, `Summon`
   and `Hud`, drawing into `Layer`.
2. Keep every object inside `Host.Arena`. It is a closed box: bounce off `Left`, `Right` and `Top`,
   and land on `Bottom` or a window top from `Host.Platforms`. `BallBody` already does this.
3. `CollectHitShapes` decides which areas take the mouse; clicks everywhere else pass through.
4. Register the game in `OverlayWindow.Start()`, add a hint in `HintFor`, and store its best score in
   `Settings`.

## Troubleshooting

| Problem | What to try |
|---|---|
| Nothing appears | Press **Ctrl+Alt+G** or left-click the tray icon; the overlay may be hidden. On a multi-monitor setup, use **Move to next monitor** in the tray menu. |
| A ball or bow is out of reach | **Ctrl+Alt+B** brings it to the cursor. **Reset positions** in the tray menu restores the defaults. |
| Shortcuts do nothing on Ubuntu | You are probably on Wayland; set up custom shortcuts as described in [Ubuntu notes](#ubuntu-notes). |
| No sound on Ubuntu | Make sure `libpulse0` is installed (`sudo apt install libpulse0`). It works with PulseAudio and PipeWire. |
| The game crashed | Details are written to `crash.log` in the settings folder (`%APPDATA%\DeskArcade` or `~/.config/DeskArcade`). |

## Third-party software

Desk Arcade is built on these open-source components, all under permissive licenses:

| Component | License |
|---|---|
| [Avalonia UI](https://github.com/AvaloniaUI/Avalonia) 11.3.9 | MIT |
| [SkiaSharp](https://github.com/mono/SkiaSharp) 2.88.9 and the native [Skia](https://skia.org) library | MIT / BSD-3-Clause |
| [HarfBuzzSharp](https://github.com/mono/SkiaSharp) 8.3.1.1 and the native [HarfBuzz](https://github.com/harfbuzz/harfbuzz) library | MIT / Old MIT |
| [ANGLE](https://github.com/AvaloniaUI/angle) (Windows only, via Avalonia.Angle.Windows.Natives) | BSD-3-Clause |
| [MicroCom.Runtime](https://github.com/kekekeks/MicroCom) 0.11.0 | MIT |
| [Tmds.DBus.Protocol](https://github.com/tmds/Tmds.DBus) 0.21.2 | MIT |
| [.NET Runtime](https://github.com/dotnet/runtime) 10 (bundled in the `.deb` and standalone builds) | MIT |

Full copyright notices and license texts are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md),
which is also installed alongside the application.

## License

Desk Arcade is released under the [MIT License](LICENSE).

Copyright © 2026 Imperium Games.
