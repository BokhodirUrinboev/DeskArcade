using System;
using System.Collections.Generic;
using System.Linq;
using DeskArcade.Dev;

namespace DeskArcade;

/// <summary>
/// The Coding agents &amp; CI menu (tray or ☰): the alerts for every agent's sessions, the ready hook configs, the
/// status lanes, and the repo folders whose CI and pull requests are followed. Built as <see cref="MenuNode"/>s, so the
/// tray and the ☰ menu share it (see <see cref="OfficeMenu"/>).
/// </summary>
public static class DevMenu
{
    public static MenuNode Build(OverlayWindow w)
    {
        var d = w.Coding;
        var s = w.Settings;
        void Toggle(Action<Settings> change)
        {
            change(s);
            w.ApplySettings();
        }

        var root = new List<MenuNode>
        {
            new() { Header = () => d.StatusLine, Enabled = () => false },
            MenuNode.Line(),
            new() { Header = () => L.T("Alerts when an agent finishes"), Click = () => Toggle(x => x.ClaudeNotify = !x.ClaudeNotify), Checked = () => s.ClaudeNotify },
            new() { Header = () => L.T("Show the overlay when an agent starts working"), Click = () => Toggle(x => x.ClaudeAutoShow = !x.ClaudeAutoShow), Checked = () => s.ClaudeAutoShow },
            new() { Header = () => L.T("Hide the overlay when an agent finishes or needs you"), Click = () => Toggle(x => x.ClaudeAutoHide = !x.ClaudeAutoHide), Checked = () => s.ClaudeAutoHide },
            new() { Header = () => L.T("Pause the game when an agent finishes or needs you"), Click = () => Toggle(x => x.ClaudePause = !x.ClaudePause), Checked = () => s.ClaudePause },
            new() { Header = () => L.T("Show what Claude is doing (two more hooks)"), Click = () => d.SetAgentSteps(!s.AgentSteps), Checked = () => s.AgentSteps },
            MenuNode.Line(),
            new() { Header = () => L.T("Copy Claude Code hook config"), Click = () => d.CopyConfig(AgentConfigs.Claude) },
        };

        var others = AgentConfigs.Others.Select(id => new MenuNode { Header = () => AgentConfigs.MenuHeader(id), Click = () => d.CopyConfig(id) }).ToList();
        root.Add(new() { Header = () => L.T("Copy a config for another agent"), Children = others });
        root.Add(new() { Header = () => L.T("Forget finished sessions"), Click = d.ClearSessions, Enabled = () => d.Sessions.Count > 0 });
        root.Add(new() { Header = () => L.T("Clear the status lanes"), Click = d.ClearLanes, Enabled = () => d.Lanes.All.Any(l => !l.Ci) });
        root.Add(MenuNode.Line());

        // CI and pull requests: a line per repo folder (the tray is rebuilt when one is added or removed)
        var repos = new List<MenuNode>();
        foreach (string folder in s.Repos.ToList())
        {
            string f = folder;
            repos.Add(new()
            {
                Header = () => DevDesk.RepoLine(f, d.Repos.FirstOrDefault(r => r.Folder == f).Last),
                Children = new() { new() { Header = () => L.T("Stop following"), Click = () => d.RemoveRepo(f) } },
            });
        }
        if (repos.Count > 0) repos.Add(MenuNode.Line());
        repos.Add(new() { Header = () => L.T("Add a repo folder…"), Click = d.PickRepo });
        repos.Add(new() { Header = () => DevDesk.HasTool ? L.T("Read through gh or glab every 2 minutes") : L.T("Needs gh or glab, signed in"), Enabled = () => false });
        root.Add(new() { Header = () => L.T("CI and pull requests"), Children = repos });

        return new MenuNode { Header = () => L.T("Coding agents & CI"), Children = root };
    }
}
