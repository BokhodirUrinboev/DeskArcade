using System;
using System.Collections.Generic;
using Avalonia.Controls;
using DeskArcade.Engine;
using DeskArcade.Platform;

namespace DeskArcade;

/// <summary>
/// The ☰ menu on the scoreboard: the essentials of the tray menu, for desktops without a tray (GNOME without the
/// AppIndicator extension, a bare window manager) and for anyone who would rather not leave the overlay. Built fresh
/// each time it opens, so it always shows the current game, pet, level and theme.
/// </summary>
public static class QuickMenu
{
    public static IEnumerable<Control> Build(OverlayWindow w)
    {
        var games = Sub(L.T("Game"));
        foreach (var g in w.Games)
        {
            string id = g.Id;
            games.Items.Add(Radio(L.T(g.Title), w.Current?.Id == id, () => w.SwitchGame(id)));
        }
        yield return games;

        var pets = Sub(L.T("Pet"));
        foreach (var (kind, name) in Tray.PetChoices())
        {
            string k = kind;
            pets.Items.Add(Radio(name, w.Settings.PetKind == k, () => w.SetPet(k)));
        }
        yield return pets;

        var cpu = Sub(L.T("CPU difficulty"));
        if (w.Current is { HasCpuLevels: true } current)
        {
            for (int level = 1; level <= MiniGame.LevelNames.Length; level++)
            {
                int l = level;
                cpu.Items.Add(Radio(L.T(MiniGame.LevelNames[l - 1]), current.CpuLevel == l, () => current.CpuLevel = l));
            }
            cpu.Items.Add(new Separator());
        }
        cpu.Items.Add(Check(L.T("Race the computer in solo rounds"), w.Settings.CpuRival, () => w.SetCpuRival(!w.Settings.CpuRival)));
        yield return cpu;

        var themes = Sub(L.T("Theme"));
        foreach (var (id, name) in Themes.Choices)
        {
            string t = id;
            themes.Items.Add(Radio(L.T(name), w.Settings.Theme == t, () => w.SetTheme(t)));
        }
        themes.Items.Add(new Separator());
        themes.Items.Add(Check(L.T("Theme decorations"), w.Settings.ThemeDecor, () => w.SetThemeDecor(!w.Settings.ThemeDecor)));
        yield return themes;

        var lan = Sub(L.T("Play over LAN"));
        lan.Items.Add(new MenuItem { Header = w.LanStatus, IsEnabled = false });
        lan.Items.Add(new Separator());
        lan.Items.Add(Item(L.T("Host a game"), w.HostLan));
        lan.Items.Add(Item(L.T("Join a game"), () => w.JoinLan()));
        lan.Items.Add(Item(L.T("Find games / join by address…"), w.OpenLobby));
        lan.Items.Add(Item(L.T("Durak with co-workers…"), w.OpenDurakRooms));
        lan.Items.Add(Item(L.T("Last Card with co-workers…"), w.OpenLastCardRooms));
        lan.Items.Add(Item(L.T("Leave"), w.LeaveLan));
        yield return lan;

        yield return Item(L.T("Next game") + "   (" + Shortcuts.Label(HotkeyAction.NextGame) + ")", w.NextGame);
        yield return Item(L.T("Bring to cursor") + "   (" + Shortcuts.Label(HotkeyAction.Summon) + ")", w.SummonToCursor);
        yield return Item(w.DailyLine, w.PlayDaily);

        var sound = Sub(L.T("Sound"));
        sound.Items.Add(Check(L.T("Sound"), w.Settings.Sound, () => Toggle(w, s => s.Sound = !s.Sound)));
        sound.Items.Add(new Separator());
        foreach (double level in new[] { 0.25, 0.5, 0.75, 1.0 })
        {
            double v = level;
            sound.Items.Add(Radio($"{(int)(v * 100)}%", Math.Abs(w.Settings.Volume - v) < 0.126, () => w.SetVolume(v)));
        }
        yield return sound;

        var look = Sub(L.T("Accessibility"));
        look.Items.Add(Check(L.T("Reduce motion"), w.Settings.ReducedMotion, () => Toggle(w, s => s.ReducedMotion = !s.ReducedMotion)));
        look.Items.Add(Check(L.T("Colour-blind friendly colours"), w.Settings.ColorBlind, () => Toggle(w, s => s.ColorBlind = !s.ColorBlind)));
        look.Items.Add(Check(L.T("Bounce on window tops"), w.Settings.Platforms, () => Toggle(w, s => s.Platforms = !s.Platforms)));
        yield return look;

        var language = Sub(L.T("Language"));
        foreach (var (code, name) in L.Languages)
        {
            string c = code;
            language.Items.Add(Radio(c == "auto" ? L.T(name) : name, w.Settings.Language == c, () => w.SetLanguage(c)));
        }
        yield return language;

        var claude = Sub("Claude Code");
        claude.Items.Add(Check(L.T("Alerts when Claude finishes"), w.Settings.ClaudeNotify, () => Toggle(w, s => s.ClaudeNotify = !s.ClaudeNotify)));
        claude.Items.Add(Check(L.T("Show the overlay when Claude starts working"), w.Settings.ClaudeAutoShow, () => Toggle(w, s => s.ClaudeAutoShow = !s.ClaudeAutoShow)));
        claude.Items.Add(Check(L.T("Hide the overlay when Claude finishes or needs you"), w.Settings.ClaudeAutoHide, () => Toggle(w, s => s.ClaudeAutoHide = !s.ClaudeAutoHide)));
        claude.Items.Add(Check(L.T("Pause the game when Claude finishes or needs you"), w.Settings.ClaudePause, () => Toggle(w, s => s.ClaudePause = !s.ClaudePause)));
        claude.Items.Add(new Separator());
        claude.Items.Add(Item(L.T("Copy Claude Code hook config"), w.CopyHookConfig));
        yield return claude;

        var board = Sub(L.T("Office leaderboard"));
        board.Items.Add(Item(L.T("Show the leaderboard…"), w.OpenLeaderboard));
        board.Items.Add(Check(L.T("Share my scores on the local network"), w.Settings.ShareLeaderboard, () => w.SetShareLeaderboard(!w.Settings.ShareLeaderboard)));
        yield return board;

        yield return Item(L.T("Stats & achievements…"), w.OpenStats);
        yield return Item(L.T("Shortcuts…"), w.OpenShortcuts);
        yield return Item(L.T("Move to next monitor"), w.MoveToNextMonitor);
        yield return Item(L.T("Reset positions"), w.ResetPositions);
        if (w.AvailableUpdate is { } update)
            yield return Item(UpdateChecker.CanInstall ? L.F("Install version {0}", update.Version.ToString(3)) : L.F("Download version {0}…", update.Version.ToString(3)), w.InstallUpdate);
        else yield return Item(L.T("Check for updates"), () => w.CheckForUpdates(manual: true));
        yield return new Separator();
        yield return Item(L.T("Hide overlay") + "   (" + Shortcuts.Label(HotkeyAction.ToggleOverlay) + ")", w.HideFromMenu);
        yield return Item(L.T("Exit"), w.Quit);
    }

    static void Toggle(OverlayWindow w, Action<Settings> change)
    {
        change(w.Settings);
        w.ApplySettings();
    }

    static MenuItem Sub(string header) => new() { Header = header };

    static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    static MenuItem Radio(string header, bool on, Action action)
    {
        var item = Item(header, action);
        item.ToggleType = MenuItemToggleType.Radio;
        item.IsChecked = on;
        return item;
    }

    static MenuItem Check(string header, bool on, Action action)
    {
        var item = Item(header, action);
        item.ToggleType = MenuItemToggleType.CheckBox;
        item.IsChecked = on;
        return item;
    }
}
