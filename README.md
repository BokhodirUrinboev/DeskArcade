# Desk Arcade

Mini-games that play **on top of your desktop**, on Windows and Ubuntu, in a transparent overlay.
You can shoot hoops while Claude Code (or a build, or a deploy) is working, and still see
everything underneath.

- The overlay covers one monitor, but only the game objects (ball, hoop, bow, scoreboard) take the
  mouse. Clicks anywhere else go straight through to your windows.
- It never takes keyboard focus, so you can keep typing in the terminal while playing.
- Balls and arrows **land on the top edges of your open windows** and ride along when you drag a
  window.
- When nothing is moving, it stops rendering and uses almost no CPU.

## Games

| Game | How to play |
|---|---|
| 🏀 **Hoops** | Grab the ball, flick, and let go. 2 pts, 3 pts from far away, +1 for a swish, 1 for a dunk. Make 3 in a row and the ball catches fire (points ×2). At 5 in a row the hoop starts moving. Drag the backboard to move the hoop. |
| 🎯 **Archery** | Press on the bow, drag backwards, and release. Each round has 10 arrows. Rings are worth 2 to 10 points, balloons 5 and gold balloons 15. From round 2 there is wind (shown under the bow) and targets start moving. Right-drag the bow to move it. |
| ⚽ **Keepy-Uppy** | Click the ball to kick it up. Where you click decides the direction. Don't let it touch the ground or a window top. Grab the stars for +3. Gravity gets stronger the longer you juggle. |
| ⛳ **Mini Golf** | Drag back from the ball and let go to putt. The cup sits on the taskbar or on top of one of your windows, and rides along if you move that window. A round is 9 holes; each hole starts where the last one ended. Score is shown against par. |
| 🐞 **Bug Squash** | Click the sleeping bug to start a 30-second round. Bugs crawl along the taskbar and window tops; squash them before they fly away. Quick squashes build a combo up to ×4, and golden bugs are worth 5. Don't squash ladybugs: they're features (−5). |
| 🥫 **Can Knockdown** | Grab the ball and throw it from behind the dashed line to knock the pyramid off its shelf. You get 3 balls per stack, and the golden top can is worth 3. Clearing a stack gives bonus points for spare balls and a bigger stack; failing to clear one ends the game. |

The scoreboard is a small pill showing the game icon, score and your best. **Click it** to open the full board with a tab for every game; it shrinks back shortly after the mouse leaves. You can also switch games from the tray menu or with **Ctrl+Alt+N**.

## Controls

| Shortcut | Action |
|---|---|
| **Ctrl+Alt+G** | Show / hide the overlay |
| **Ctrl+Alt+N** | Next game |
| **Ctrl+Alt+B** | Bring the ball, bow or golf ball to your cursor (costs a penalty stroke in golf) |
| Click the scoreboard | Open the game tabs |
| Drag the scoreboard | Move it |
| Tray icon (left-click) | Show / hide |
| Tray icon menu | Game, sound, window-top bouncing, start at sign-in, monitor, reset, exit |

Every action is also available from a shell, which is useful for keyboard shortcuts and scripts:

```
DeskArcade --signal toggle|next|summon|show|hide|quit
```

## Install

### Windows

Run `DeskArcade-Setup-<version>.exe`. It installs per user, without an admin prompt, into
`%LOCALAPPDATA%\Programs\Desk Arcade`. It needs the .NET 10 Runtime and offers the download page if
it's missing. The `-standalone` installer bundles the runtime instead.

### Ubuntu (22.04 / 24.04, x64)

```bash
sudo apt install ./deskarcade_<version>_amd64.deb
deskarcade            # or find "Desk Arcade" in the app grid
```

The package bundles its own .NET runtime; apt pulls in the few X11 libraries it needs. Remove it
with `sudo apt remove deskarcade`.

**Ubuntu notes**

- Desk Arcade runs as an X11 app. On the default Wayland session it runs through XWayland, and the
  overlay, click-through and sound all work there.
- **Window-top bouncing** can only see X11 windows under Wayland; many native Wayland apps are
  invisible to it. On an "Ubuntu on Xorg" session it sees every window.
- **Global shortcuts** (Ctrl+Alt+G/N/B) work on Xorg. Wayland doesn't let apps grab global keys, so
  add them in *Settings → Keyboard → Custom Shortcuts* with commands like
  `deskarcade --signal toggle`, `deskarcade --signal next` and `deskarcade --signal summon`.
- The tray icon uses AppIndicator, which Ubuntu enables by default. The menu is also reachable
  through the app grid's right-click actions.

## Updates

Both packages upgrade in place:

| | How | What happens |
|---|---|---|
| Windows | run the newer `DeskArcade-Setup-x.y.z.exe` | It closes the running game, replaces the files in the same folder, keeps your shortcut and autostart choices, and restarts the game after silent updates. Downgrades are refused. |
| Ubuntu | `sudo apt install ./deskarcade_x.y.z_amd64.deb` | apt replaces the older version and closes the running game first. Start it again afterwards. |

Settings and high scores are never touched by an update. They live in `%APPDATA%\DeskArcade` on
Windows and `~/.config/DeskArcade` on Ubuntu.

## Build

Requires the .NET 10 SDK.

```powershell
dotnet run                         # run from source (either OS)

# Windows
.\build.ps1                        # dist\DeskArcade.exe
.\build-installer.ps1              # installer\Output\DeskArcade-Setup-<version>.exe (needs Inno Setup 6)
.\build-installer.ps1 -SelfContained

# Ubuntu package
.\build-linux.ps1                  # from Windows: publishes, then packages inside WSL
packaging/linux/build.sh           # on Ubuntu itself
```

The `.deb` step needs `dpkg-deb`, so run it on Ubuntu/Debian, in WSL, or in an `ubuntu` Docker
container.

### Shipping a new version

1. Bump `<Version>` in `DeskArcade.csproj`, for example `1.1.0` → `1.2.0`.
2. Build both packages: `.\build-installer.ps1` and `.\build-linux.ps1`.
3. Ship `DeskArcade-Setup-1.2.0.exe` and `deskarcade_1.2.0_amd64.deb`.

Two identifiers make upgrades recognise the previous version. **Never change them:**

- the installer `AppId` (`MyAppGuid` in `installer/DeskArcade.iss`)
- the Debian package name `deskarcade` (`packaging/linux/control.in`)

If a later Windows version drops a file, add it to `[InstallDelete]` in the `.iss`.

## Claude Code status on the scoreboard (optional)

Desk Arcade can show whether Claude is working, done, or waiting for you, and chime when it
finishes. Right-click the tray icon, choose **Copy Claude Code hook config**, and merge the copied
JSON into `~/.claude/settings.json`. It wires up these hooks:

- `UserPromptSubmit` → `--signal working`
- `Stop` → `--signal done`
- `Notification` → `--signal attention`

The copied commands point at the running copy (`/usr/bin/deskarcade` on Ubuntu). If the game isn't
running, the hook exits immediately and does nothing.

## Code layout

| Path | What's there |
|---|---|
| `src/Engine` | UI-toolkit-light game engine: `Vec2`, ball physics, window-top platforms, sprites, effects, `MiniGame` |
| `src/Games` | one class per game |
| `src/Platform` | the OS layer behind `IDesktopPlatform`: click-through, window list, hotkeys, audio, autostart |
| `src/Platform/Windows` | layered window with `WS_EX_TRANSPARENT` toggled under the cursor, Win32 hotkeys, waveOut |
| `src/Platform/Linux` | XShape input regions, `_NET_CLIENT_LIST_STACKING`, `XGrabKey`, PulseAudio |
| `src/OverlayWindow.cs` | the transparent topmost Avalonia window, frame loop, input, and signals |
| `installer/` | Inno Setup script (Windows) |
| `packaging/linux/` | `.deb` builder, desktop entry, maintainer scripts |

The UI is [Avalonia](https://avaloniaui.net/), so the games themselves have no OS-specific code.

### Adding a game

Subclass `MiniGame` and implement `Layout`, `Update`, `CollectHitShapes`, `PointerDown`, `Summon`,
and `Hud`. Draw into `Layer`, then register the game in `OverlayWindow.Start()`.
`CollectHitShapes` decides which areas take the mouse; clicks everywhere else pass through.

## Development flags

- `--game hoops|archery|juggle` starts on a specific game.
- `--demo` makes the current game play itself, for smoke tests without touching the mouse.
- `--profile NAME` runs an isolated copy with its own lock, signal channel and settings, so you
  can test a build while the installed game keeps running. Combine it with `--signal`:
  `DeskArcade --profile test --signal quit`.
