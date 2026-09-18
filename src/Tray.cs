using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;

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
        menu.Add(Item(L.T("Next game") + "   (" + Shortcuts.Label('N') + ")", () => _w.NextGame()));
        menu.Add(Item(L.T("Bring to cursor") + "   (" + Shortcuts.Label('B') + ")", () => _w.SummonToCursor()));
        menu.Add(Item(L.T("Stats & achievements…"), () => _w.OpenStats()));

        var lan = new NativeMenu();
        var status = new NativeMenuItem(_w.LanStatus) { IsEnabled = false };
        _refreshers.Add(() => status.Header = _w.LanStatus);
        lan.Add(status);
        lan.Add(new NativeMenuItemSeparator());
        lan.Add(Item(L.T("Host a game"), () => _w.HostLan()));
        lan.Add(Item(L.T("Join a game"), () => _w.JoinLan()));
        lan.Add(Item(L.T("Leave"), () => _w.LeaveLan()));
        menu.Add(new NativeMenuItem(L.T("Play over LAN")) { Menu = lan });
        menu.Add(new NativeMenuItemSeparator());

        menu.Add(Check(L.T("Show overlay") + "   (" + Shortcuts.Label('G') + ")", () => _w.ToggleOverlay(), () => _w.OverlayVisible));
        menu.Add(Check(L.T("Bounce on window tops"), () => Toggle(s => s.Platforms = !s.Platforms), () => _w.Settings.Platforms));
        menu.Add(Check(L.T("Sound"), () => Toggle(s => s.Sound = !s.Sound), () => _w.Settings.Sound));

        var volume = new NativeMenu();
        foreach (double level in VolumeLevels)
            volume.Add(Radio($"{(int)(level * 100)}%", () => _w.SetVolume(level), () => Math.Abs(_w.Settings.Volume - level) < 0.126));
        menu.Add(new NativeMenuItem(L.T("Volume")) { Menu = volume });

        var language = new NativeMenu();
        foreach (var (code, name) in L.Languages)
            language.Add(Radio(code == "auto" ? L.T(name) : name, () => _w.SetLanguage(code), () => _w.Settings.Language == code));
        menu.Add(new NativeMenuItem(L.T("Language")) { Menu = language });

        var claude = new NativeMenu();
        claude.Add(Check(L.T("Alerts when Claude finishes"), () => Toggle(s => s.ClaudeNotify = !s.ClaudeNotify), () => _w.Settings.ClaudeNotify));
        claude.Add(Check(L.T("Show the overlay when Claude starts working"), () => Toggle(s => s.ClaudeAutoShow = !s.ClaudeAutoShow), () => _w.Settings.ClaudeAutoShow));
        claude.Add(Check(L.T("Hide the overlay when Claude finishes or needs you"), () => Toggle(s => s.ClaudeAutoHide = !s.ClaudeAutoHide), () => _w.Settings.ClaudeAutoHide));
        claude.Add(new NativeMenuItemSeparator());
        claude.Add(Item(L.T("Copy Claude Code hook config"), () => _w.CopyHookConfig()));
        menu.Add(new NativeMenuItem("Claude Code") { Menu = claude });

        menu.Add(Check(L.T("Start when I sign in"), () =>
        {
            _w.AutostartEnabled = !_w.AutostartEnabled;
            Refresh();
        }, () => _w.AutostartEnabled));
        menu.Add(Item(L.T("Move to next monitor"), () => _w.MoveToNextMonitor()));
        menu.Add(new NativeMenuItemSeparator());

        var update = Item(L.T("Check for updates"), () =>
        {
            if (_w.AvailableUpdate is UpdateInfo u) UpdateChecker.OpenInBrowser(u.Url);
            else _w.CheckForUpdates(manual: true);
        });
        _refreshers.Add(() => update.Header = _w.AvailableUpdate is UpdateInfo u
            ? L.F("Download version {0}…", u.Version.ToString(3))
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
