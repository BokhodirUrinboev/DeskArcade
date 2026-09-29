using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using DeskArcade.Net;
using DeskArcade.Office;

namespace DeskArcade;

/// <summary>
/// One entry of the At work menu, drawn both in the tray (a native menu, refreshed in place) and on the scoreboard's ☰
/// menu (built fresh each time it opens): a header, what a click does, a check or radio state, and a submenu.
/// </summary>
public sealed class MenuNode
{
    public required Func<string> Header;
    public Action? Click;
    public Func<bool>? Checked;
    public bool Radio;
    public Func<bool>? Enabled;
    public List<MenuNode>? Children;
    public bool Separator;

    public static MenuNode Line() => new() { Header = () => "", Separator = true };
}

/// <summary>The At work menu (tray or ☰ → At work).</summary>
public static class OfficeMenu
{
    static readonly int[] FocusLengths = { 25, 50, 90 }, BreakLengths = { 5, 10, 15 };
    static readonly int[] TimerLengths = { 5, 10, 15, 25, 30, 45, 60 };
    static readonly int[] StretchChoices = { 0, 30, 45, 60, 90 }, WaterChoices = { 0, 60, 90, 120 }, WarnChoices = { 0, 1, 2, 5, 10, 15 };

    public static MenuNode Build(OverlayWindow w)
    {
        var d = w.Office;
        var s = w.Settings;
        var root = new List<MenuNode>
        {
            new() { Header = () => d.StatusLine, Enabled = () => false },
            MenuNode.Line(),
        };

        // focus
        var focus = new List<MenuNode>
        {
            new() { Header = () => L.F("Start a {0}-minute focus block", s.FocusMinutes), Click = () => d.StartFocus(null), Enabled = () => d.FocusPhase != FocusPhase.Focus },
            new() { Header = () => L.T("End the focus block"), Click = d.StopFocus, Enabled = () => d.FocusPhase != FocusPhase.Off },
            MenuNode.Line(),
        };
        foreach (int m in FocusLengths)
            focus.Add(new() { Header = () => L.F("Focus for {0} minutes", m), Click = () => d.SetFocusMinutes(m), Checked = () => s.FocusMinutes == m, Radio = true });
        focus.Add(MenuNode.Line());
        foreach (int m in BreakLengths)
            focus.Add(new() { Header = () => L.F("{0}-minute breaks", m), Click = () => d.SetFocusBreakMinutes(m), Checked = () => s.FocusBreakMinutes == m, Radio = true });
        focus.Add(MenuNode.Line());
        focus.Add(new() { Header = () => L.T("Start the next block after the break"), Click = () => d.SetFocusAuto(!s.FocusAuto), Checked = () => s.FocusAuto });
        focus.Add(new() { Header = () => L.T("Hold chat and invites while focusing"), Click = () => d.SetFocusQuiet(!s.FocusQuiet), Checked = () => s.FocusQuiet });
        root.Add(new() { Header = () => L.T("Focus"), Children = focus });

        // timers
        var timers = TimerLengths.Select(m => new MenuNode { Header = () => L.F("{0} minutes", m), Click = () => d.AddTimer(TimeSpan.FromMinutes(m), "") }).ToList();
        timers.Add(MenuNode.Line());
        timers.Add(new() { Header = () => L.T("Cancel the timers"), Click = d.CancelTimers, Enabled = () => d.Timers.Count > 0 });
        root.Add(new() { Header = () => L.T("Timer"), Children = timers });

        root.Add(new() { Header = () => L.T("New sticky note…"), Click = d.NewNote });
        root.Add(new() { Header = () => L.T("Breathe for a minute"), Click = d.Breathe });
        root.Add(new() { Header = () => L.T("Stretch now"), Click = d.StretchNow });
        root.Add(new() { Header = () => L.T("I drank a glass of water"), Click = d.DrankWater });
        root.Add(MenuNode.Line());

        // breaks
        var breaks = new List<MenuNode>
        {
            new() { Header = () => L.T("Look away every 20 minutes (20-20-20)"), Click = () => d.SetEyeBreaks(!s.EyeBreaks), Checked = () => s.EyeBreaks },
            MenuNode.Line(),
        };
        foreach (int m in StretchChoices)
            breaks.Add(new() { Header = () => m == 0 ? L.T("No stretch breaks") : L.F("Stretch after {0} minutes at the computer", m), Click = () => d.SetStretchMinutes(m), Checked = () => s.StretchMinutes == m, Radio = true });
        breaks.Add(MenuNode.Line());
        foreach (int m in WaterChoices)
            breaks.Add(new() { Header = () => m == 0 ? L.T("No water reminders") : L.F("Water every {0} minutes", m), Click = () => d.SetWaterMinutes(m), Checked = () => s.WaterMinutes == m, Radio = true });
        breaks.Add(MenuNode.Line());
        var play = new List<MenuNode>();
        foreach (int m in new[] { 0, 15, 20, 30, 45, 60 })
            play.Add(new() { Header = () => m == 0 ? L.T("Off") : L.F("After {0} minutes of play", m), Click = () => w.SetBreakMinutes(m), Checked = () => s.BreakMinutes == m, Radio = true });
        play.Add(MenuNode.Line());
        play.Add(new() { Header = () => L.T("Say “back to work” when Claude is done"), Click = () => { s.BackToWork = !s.BackToWork; w.ApplySettings(); }, Checked = () => s.BackToWork });
        breaks.Add(new() { Header = () => L.T("Break reminder"), Children = play });
        root.Add(new() { Header = () => L.T("Breaks"), Children = breaks });

        // meetings
        var meetings = new List<MenuNode>
        {
            new() { Header = () => s.CalendarUrl == null ? L.T("Connect a calendar…") : L.T("Calendar…"), Click = d.OpenWindow },
            MenuNode.Line(),
        };
        foreach (int m in WarnChoices)
            meetings.Add(new() { Header = () => m == 0 ? L.T("Only a minute before") : L.F("{0} minutes before", m), Click = () => d.SetMeetingWarn(m), Checked = () => s.MeetingWarnMinutes == m, Radio = true });
        meetings.Add(MenuNode.Line());
        meetings.Add(new() { Header = () => L.T("Pause the game a minute before"), Click = () => d.SetMeetingPause(!s.MeetingPause), Checked = () => s.MeetingPause });
        meetings.Add(new() { Header = () => L.T("Hide the overlay during meetings"), Click = () => d.SetMeetingHide(!s.MeetingHide), Checked = () => s.MeetingHide });
        root.Add(new() { Header = () => L.T("Meetings"), Children = meetings });

        // invites
        var invites = new List<MenuNode>();
        foreach (var kind in new[] { InviteKind.Coffee, InviteKind.Lunch, InviteKind.Walk })
        {
            var when = OfficeInvites.Minutes.Select(m => new MenuNode
            {
                Header = () => m == 0 ? L.T("Now") : L.F("In {0} minutes", m), Click = () => d.SendInvite(kind, m),
            }).ToList();
            invites.Add(new() { Header = () => OfficeDesk.KindName(kind), Children = when });
        }
        invites.Add(new() { Header = () => d.LastInviteLine ?? L.T("No invite out"), Enabled = () => false });
        invites.Add(MenuNode.Line());
        invites.Add(new() { Header = () => L.T("Invites from co-workers"), Click = () => d.SetInvites(!s.OfficeInvites), Checked = () => s.OfficeInvites });
        root.Add(new() { Header = () => L.T("Invite co-workers"), Children = invites });

        // the end of the day
        var end = new List<MenuNode> { new() { Header = () => L.T("Off"), Click = () => d.SetWorkEnd(null), Checked = () => s.WorkEnd == null, Radio = true } };
        foreach (string time in OfficeDesk.EndTimes)
            end.Add(new() { Header = () => time, Click = () => d.SetWorkEnd(time), Checked = () => s.WorkEnd == time, Radio = true });
        root.Add(new() { Header = () => L.T("End of the day"), Children = end });

        root.Add(new() { Header = () => L.T("Tell me when downloads finish"), Click = () => d.SetWatchDownloads(!s.WatchDownloads), Checked = () => s.WatchDownloads });
        root.Add(new() { Header = () => L.T("Hide while presenting or in full screen"), Click = () => d.SetHideWhenFullScreen(!s.HideWhenFullScreen), Checked = () => s.HideWhenFullScreen });
        root.Add(MenuNode.Line());
        root.Add(new() { Header = () => L.T("Today at work…"), Click = d.OpenWindow });

        return new MenuNode { Header = () => L.T("At work"), Children = root };
    }

    /// <summary>The menu as a native (tray) menu item; <paramref name="refreshers"/> collects what keeps it current.</summary>
    public static NativeMenuItemBase ToNative(MenuNode node, List<Action> refreshers)
    {
        if (node.Separator) return new NativeMenuItemSeparator();
        var item = new NativeMenuItem(node.Header());
        if (node.Children != null)
        {
            var menu = new NativeMenu();
            foreach (var child in node.Children) menu.Add(ToNative(child, refreshers));
            item.Menu = menu;
        }
        if (node.Click is Action click) item.Click += (_, _) => click();
        if (node.Checked != null) item.ToggleType = node.Radio ? NativeMenuItemToggleType.Radio : NativeMenuItemToggleType.CheckBox;
        refreshers.Add(() =>
        {
            item.Header = node.Header();
            if (node.Checked is Func<bool> on) item.IsChecked = on();
            if (node.Enabled is Func<bool> enabled) item.IsEnabled = enabled();
        });
        return item;
    }

    /// <summary>The menu as the ☰ menu's items; empty lines (an invite line with nothing to say) are left out.</summary>
    public static Control ToMenuItem(MenuNode node)
    {
        if (node.Separator) return new Separator();
        var item = new MenuItem { Header = node.Header() };
        if (node.Children != null)
            foreach (var child in node.Children)
                if (child.Separator || child.Header().Length > 0) item.Items.Add(ToMenuItem(child));
        if (node.Click is Action click) item.Click += (_, _) => click();
        if (node.Checked is Func<bool> on)
        {
            item.ToggleType = node.Radio ? MenuItemToggleType.Radio : MenuItemToggleType.CheckBox;
            item.IsChecked = on();
        }
        if (node.Enabled is Func<bool> enabled) item.IsEnabled = enabled();
        return item;
    }
}
