using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using DeskArcade.Platform;

namespace DeskArcade;

/// <summary>
/// Notification-area icon (Windows) / AppIndicator (Ubuntu): the always-reachable menu,
/// since the overlay never takes keyboard focus. Rebuilt when the language changes.
/// </summary>
public sealed class Tray : IDisposable
{
    static readonly double[] VolumeLevels = { 0.25, 0.5, 0.75, 1.0 };

    readonly OverlayWindow _w;
    readonly TrayIcon _icon;
    readonly List<Action> _refreshers = new();

    public Tray(OverlayWindow w, WindowIcon? icon)
    {
        _w = w;
        _icon = new TrayIcon { Icon = icon, ToolTipText = "Desk Arcade", IsVisible = true };
        _icon.Clicked += (_, _) => _w.ToggleOverlay();
        Rebuild();
        if (Application.Current != null)
            TrayIcon.SetIcons(Application.Current, new TrayIcons { _icon });
    }

    /// <summary>Recreates the whole menu, e.g. after the language changes.</summary>
    public void Rebuild()
    {
        _refreshers.Clear();
        var menu = new NativeMenu();

        var games = new NativeMenu();
        foreach (var g in _w.Games)
        {
            string id = g.Id;
            games.Add(Radio(L.T(g.Title), () => _w.SwitchGame(id), () => _w.Current?.Id == id));
        }
        menu.Add(new NativeMenuItem(L.T("Game")) { Menu = games });
        var pets = new NativeMenu();
        foreach (var (kind, name) in PetChoices())
            pets.Add(Radio(name, () => _w.SetPet(kind), () => _w.Settings.PetKind == kind));
        menu.Add(new NativeMenuItem(L.T("Pet")) { Menu = pets });
        menu.Add(Item(L.T("Next game") + "   (" + Shortcuts.Label(HotkeyAction.NextGame) + ")", () => _w.NextGame()));
        menu.Add(Item(L.T("Bring to cursor") + "   (" + Shortcuts.Label(HotkeyAction.Summon) + ")", () => _w.SummonToCursor()));
        menu.Add(Item(L.T("Stats & achievements…"), () => _w.OpenStats()));
        var board = new NativeMenu();
        board.Add(Item(L.T("Show the leaderboard…"), () => _w.OpenLeaderboard()));
        board.Add(Check(L.T("Share my scores on the local network"), () => _w.SetShareLeaderboard(!_w.Settings.ShareLeaderboard), () => _w.Settings.ShareLeaderboard));
        menu.Add(new NativeMenuItem(L.T("Office leaderboard")) { Menu = board });
        var daily = Item(_w.DailyLine, () => _w.PlayDaily());
        _refreshers.Add(() => daily.Header = _w.DailyLine);
        menu.Add(daily);

        var lan = new NativeMenu();
        var status = new NativeMenuItem(_w.LanStatus) { IsEnabled = false };
        _refreshers.Add(() => status.Header = _w.LanStatus);
        lan.Add(status);
        lan.Add(new NativeMenuItemSeparator());
        lan.Add(Item(L.T("Host a game"), () => _w.HostLan()));
        lan.Add(Item(L.T("Join a game"), () => _w.JoinLan()));
        lan.Add(Item(L.T("Find games / join by address…"), () => _w.OpenLobby()));
        lan.Add(Item(L.T("Durak with co-workers…"), () => _w.OpenDurakRooms()));
        lan.Add(Item(L.T("Last Card with co-workers…"), () => _w.OpenLastCardRooms()));
        var emotes = new NativeMenu();
        for (int i = 0; i < Net.LanLink.Emotes.Length; i++)
        {
            int index = i;
            emotes.Add(Item(L.T(Net.LanLink.Emotes[i]), () => _w.SendEmote(index)));
        }
        var send = new NativeMenuItem(L.T("Send")) { Menu = emotes };
        _refreshers.Add(() => send.IsEnabled = _w.Lan.Connected);
        lan.Add(send);
        lan.Add(Item(L.T("Leave"), () => _w.LeaveLan()));
        menu.Add(new NativeMenuItem(L.T("Play over LAN")) { Menu = lan });
        menu.Add(new NativeMenuItemSeparator());

        menu.Add(Check(L.T("Show overlay") + "   (" + Shortcuts.Label(HotkeyAction.ToggleOverlay) + ")", () => _w.ToggleOverlay(), () => _w.OverlayVisible));
        menu.Add(Check(L.T("Bounce on window tops"), () => Toggle(s => s.Platforms = !s.Platforms), () => _w.Settings.Platforms));
        menu.Add(Check(L.T("Sound"), () => Toggle(s => s.Sound = !s.Sound), () => _w.Settings.Sound));

        var volume = new NativeMenu();
        foreach (double level in VolumeLevels)
            volume.Add(Radio($"{(int)(level * 100)}%", () => _w.SetVolume(level), () => Math.Abs(_w.Settings.Volume - level) < 0.126));
        menu.Add(new NativeMenuItem(L.T("Volume")) { Menu = volume });

        var levels = new NativeMenu();
        levels.Add(Check(L.T("Race the computer in solo rounds"), () => _w.SetCpuRival(!_w.Settings.CpuRival), () => _w.Settings.CpuRival));
        levels.Add(new NativeMenuItemSeparator());
        foreach (var game in _w.Games.Where(g => g.HasCpuLevels))
        {
            var sub = new NativeMenu();
            for (int level = 1; level <= Engine.MiniGame.LevelNames.Length; level++)
            {
                int l = level;
                sub.Add(Radio(L.T(Engine.MiniGame.LevelNames[l - 1]), () => { game.CpuLevel = l; Refresh(); }, () => game.CpuLevel == l));
            }
            levels.Add(new NativeMenuItem(L.T(game.Title)) { Menu = sub });
        }
        menu.Add(new NativeMenuItem(L.T("CPU difficulty")) { Menu = levels });

        var themes = new NativeMenu();
        foreach (var (id, name) in Engine.Themes.Choices)
            themes.Add(Radio(L.T(name), () => _w.SetTheme(id), () => _w.Settings.Theme == id));
        menu.Add(new NativeMenuItem(L.T("Theme")) { Menu = themes });

        var breaks = new NativeMenu();
        foreach (int minutes in new[] { 0, 15, 20, 30, 45, 60 })
            breaks.Add(Radio(minutes == 0 ? L.T("Off") : L.F("After {0} minutes of play", minutes), () => _w.SetBreakMinutes(minutes), () => _w.Settings.BreakMinutes == minutes));
        breaks.Add(new NativeMenuItemSeparator());
        breaks.Add(Check(L.T("Say “back to work” when Claude is done"), () => Toggle(s => s.BackToWork = !s.BackToWork), () => _w.Settings.BackToWork));
        menu.Add(new NativeMenuItem(L.T("Break reminder")) { Menu = breaks });

        var access = new NativeMenu();
        access.Add(Check(L.T("Reduce motion"), () => Toggle(s => s.ReducedMotion = !s.ReducedMotion), () => _w.Settings.ReducedMotion));
        access.Add(Check(L.T("Colour-blind friendly colours"), () => Toggle(s => s.ColorBlind = !s.ColorBlind), () => _w.Settings.ColorBlind));
        menu.Add(new NativeMenuItem(L.T("Accessibility")) { Menu = access });

        var language = new NativeMenu();
        foreach (var (code, name) in L.Languages)
            language.Add(Radio(code == "auto" ? L.T(name) : name, () => _w.SetLanguage(code), () => _w.Settings.Language == code));
        menu.Add(new NativeMenuItem(L.T("Language")) { Menu = language });

        var claude = new NativeMenu();
        claude.Add(Check(L.T("Alerts when Claude finishes"), () => Toggle(s => s.ClaudeNotify = !s.ClaudeNotify), () => _w.Settings.ClaudeNotify));
        claude.Add(Check(L.T("Show the overlay when Claude starts working"), () => Toggle(s => s.ClaudeAutoShow = !s.ClaudeAutoShow), () => _w.Settings.ClaudeAutoShow));
        claude.Add(Check(L.T("Hide the overlay when Claude finishes or needs you"), () => Toggle(s => s.ClaudeAutoHide = !s.ClaudeAutoHide), () => _w.Settings.ClaudeAutoHide));
        claude.Add(Check(L.T("Pause the game when Claude finishes or needs you"), () => Toggle(s => s.ClaudePause = !s.ClaudePause), () => _w.Settings.ClaudePause));
        claude.Add(new NativeMenuItemSeparator());
        claude.Add(Item(L.T("Copy Claude Code hook config"), () => _w.CopyHookConfig()));
        menu.Add(new NativeMenuItem("Claude Code") { Menu = claude });

        menu.Add(Item(L.T("Shortcuts…"), () => _w.OpenShortcuts()));
        menu.Add(Check(L.T("Start when I sign in"), () =>
        {
            _w.AutostartEnabled = !_w.AutostartEnabled;
            Refresh();
        }, () => _w.AutostartEnabled));
        menu.Add(Item(L.T("Move to next monitor"), () => _w.MoveToNextMonitor()));
        menu.Add(new NativeMenuItemSeparator());

        var update = Item(L.T("Check for updates"), () =>
        {
            if (_w.AvailableUpdate != null) _w.InstallUpdate();
            else _w.CheckForUpdates(manual: true);
        });
        _refreshers.Add(() => update.Header = _w.AvailableUpdate is UpdateInfo u
            ? UpdateChecker.CanInstall ? L.F("Install version {0}", u.Version.ToString(3)) : L.F("Download version {0}…", u.Version.ToString(3))
            : L.T("Check for updates"));
        menu.Add(update);
        menu.Add(Check(L.T("Check for updates automatically"), () => Toggle(s => s.CheckForUpdates = !s.CheckForUpdates), () => _w.Settings.CheckForUpdates));
        menu.Add(new NativeMenuItemSeparator());

        menu.Add(Item(L.T("Reset positions"), () => _w.ResetPositions()));
        menu.Add(Item(L.T("Reset high scores"), () => _w.ResetScores()));
        menu.Add(Item(L.T("Reset stats and achievements"), () => _w.ResetStats()));
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item(L.T("Exit"), () => _w.Quit()));

        _icon.Menu = menu;
        Refresh();
    }

    /// <summary>The pets to choose from, as setting values and display names (shared with the scoreboard's menu).</summary>
    public static IEnumerable<(string Kind, string Name)> PetChoices() => new[]
    {
        ("cat", L.T("Cat")), ("dog", L.T("Dog")), ("duck", L.T("Duck")), ("bunny", L.T("Bunny")), ("penguin", L.T("Penguin")), ("fox", L.T("Fox")),
        ("hamster", L.T("Hamster")), ("turtle", L.T("Turtle")), ("parrot", L.T("Parrot")), ("frog", L.T("Frog")), ("owl", L.T("Owl")), ("dragon", L.T("Dragon")),
    };

    void Toggle(Action<Settings> change)
    {
        change(_w.Settings);
        _w.ApplySettings();
    }

    static NativeMenuItem Item(string header, Action action)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => action();
        return item;
    }

    NativeMenuItem Check(string header, Action action, Func<bool> isChecked) =>
        Stateful(header, action, isChecked, NativeMenuItemToggleType.CheckBox);

    NativeMenuItem Radio(string header, Action action, Func<bool> isChecked) =>
        Stateful(header, action, isChecked, NativeMenuItemToggleType.Radio);

    NativeMenuItem Stateful(string header, Action action, Func<bool> isChecked, NativeMenuItemToggleType type)
    {
        var item = Item(header, action);
        item.ToggleType = type;
        _refreshers.Add(() => item.IsChecked = isChecked());
        return item;
    }

    public void Refresh()
    {
        foreach (var refresh in _refreshers) refresh();
    }

    public void Dispose()
    {
        _icon.IsVisible = false;
        _icon.Dispose();
    }
}
