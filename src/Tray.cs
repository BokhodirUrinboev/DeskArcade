using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;

namespace DeskArcade;

/// <summary>
/// Notification-area icon (Windows) / AppIndicator (Ubuntu): the always-reachable menu,
/// since the overlay never takes keyboard focus.
/// </summary>
public sealed class Tray : IDisposable
{
    readonly OverlayWindow _w;
    readonly TrayIcon _icon;
    readonly NativeMenuItem _show, _platforms, _sound, _notify, _autostart;
    readonly Dictionary<NativeMenuItem, string> _gameItems = new();

    public Tray(OverlayWindow w, WindowIcon? icon)
    {
        _w = w;
        var menu = new NativeMenu();

        var games = new NativeMenu();
        foreach (var g in w.Games)
        {
            string id = g.Id;
            var item = Item(g.Title, () => _w.SwitchGame(id));
            item.ToggleType = NativeMenuItemToggleType.Radio;
            _gameItems[item] = id;
            games.Add(item);
        }
        menu.Add(new NativeMenuItem("Game") { Menu = games });
        menu.Add(Item("Next game   (Ctrl+Alt+N)", () => _w.NextGame()));
        menu.Add(Item("Bring to cursor   (Ctrl+Alt+B)", () => _w.SummonToCursor()));
        menu.Add(new NativeMenuItemSeparator());

        _show = Check("Show overlay   (Ctrl+Alt+G)", () => _w.ToggleOverlay());
        _platforms = Check("Bounce on window tops", () =>
        {
            _w.Settings.Platforms = !_w.Settings.Platforms;
            _w.ApplySettings();
        });
        _sound = Check("Sound", () =>
        {
            _w.Settings.Sound = !_w.Settings.Sound;
            _w.ApplySettings();
        });
        _notify = Check("Claude status alerts", () =>
        {
            _w.Settings.ClaudeNotify = !_w.Settings.ClaudeNotify;
            _w.ApplySettings();
        });
        _autostart = Check("Start when I sign in", () =>
        {
            _w.AutostartEnabled = !_w.AutostartEnabled;
            Refresh();
        });
        menu.Add(_show);
        menu.Add(_platforms);
        menu.Add(_sound);
        menu.Add(_notify);
        menu.Add(_autostart);
        menu.Add(Item("Move to next monitor", () => _w.MoveToNextMonitor()));
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item("Copy Claude Code hook config", () => _w.CopyHookConfig()));
        menu.Add(Item("Reset positions", () => _w.ResetPositions()));
        menu.Add(Item("Reset high scores", () => _w.ResetScores()));
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item("Exit", () => _w.Quit()));

        _icon = new TrayIcon { Icon = icon, ToolTipText = "Desk Arcade", Menu = menu, IsVisible = true };
        _icon.Clicked += (_, _) => _w.ToggleOverlay();
        if (Application.Current != null)
            TrayIcon.SetIcons(Application.Current, new TrayIcons { _icon });
        Refresh();
    }

    static NativeMenuItem Item(string header, Action action)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => action();
        return item;
    }

    static NativeMenuItem Check(string header, Action action)
    {
        var item = Item(header, action);
        item.ToggleType = NativeMenuItemToggleType.CheckBox;
        return item;
    }

    public void Refresh()
    {
        _show.IsChecked = _w.OverlayVisible;
        _platforms.IsChecked = _w.Settings.Platforms;
        _sound.IsChecked = _w.Settings.Sound;
        _notify.IsChecked = _w.Settings.ClaudeNotify;
        _autostart.IsChecked = _w.AutostartEnabled;
        foreach (var (item, id) in _gameItems)
            item.IsChecked = id == _w.Current?.Id;
    }

    public void Dispose()
    {
        _icon.IsVisible = false;
        _icon.Dispose();
    }
}
