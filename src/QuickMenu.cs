using System;
using System.Collections.Generic;
using Avalonia.Controls;
using DeskArcade.Engine;

namespace DeskArcade;

/// <summary>
/// The ☰ menu on the scoreboard: the essentials of the tray menu, for desktops without a tray (GNOME without the
/// AppIndicator extension, a bare window manager) and for anyone who would rather not leave the overlay. Built fresh
/// each time it opens, so it always shows the current game, pet and level.
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

        yield return Item(L.T("Stats & achievements…"), w.OpenStats);
        yield return Item(L.T("Shortcuts…"), w.OpenShortcuts);
        yield return Item(L.T("Reset positions"), w.ResetPositions);
        yield return new Separator();
        yield return Item(L.T("Hide overlay"), () => w.SetOverlayVisible(false));
        yield return Item(L.T("Exit"), w.Quit);
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
