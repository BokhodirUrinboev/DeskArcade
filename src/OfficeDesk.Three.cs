using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DeskArcade.Engine;

namespace DeskArcade;

/// <summary>
/// Today's three under the scoreboard: a small list that a click ticks, drawn on the at-work layer so the scoreboard
/// itself stays as it is. It follows the scoreboard when that is dragged or grows, sits above it when there is no
/// room below, and stays out of sight while the overlay only peeks out for a card.
/// </summary>
public sealed partial class OfficeDesk
{
    const double ThreeWidth = 236;

    Border? _threeView;
    Rect _threeRect, _threeHud;
    bool _threeShown;

    /// <summary>(Re)builds the list from the settings; nothing to show, no list.</summary>
    void BuildThree()
    {
        if (_threeView != null)
        {
            _w.OfficeLayer.Children.Remove(_threeView);
            _threeView = null;
        }
        var items = S.Three.Items;
        if (items.Count == 0)
        {
            SetThreeShown(false, default);
            return;
        }
        var t = Themes.Current;
        var panel = new StackPanel { Spacing = 1 };
        int done = items.Count(i => i.Done);
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
        var count = new TextBlock { Text = $"{done}/{items.Count}", FontFamily = Fx.Font, FontSize = 10.5, FontWeight = FontWeight.Bold, Foreground = Art.Brush(done == items.Count ? t.Gold : Color.Parse("#8D97A5")) };
        DockPanel.SetDock(count, Dock.Right);
        head.Children.Add(count);
        head.Children.Add(new TextBlock { Text = L.T("Today's three").ToUpperInvariant(), FontFamily = Fx.Font, FontSize = 10.5, FontWeight = FontWeight.Bold, Foreground = Art.Brush(Color.Parse("#8D97A5")) });
        panel.Children.Add(head);
        for (int i = 0; i < items.Count; i++)
        {
            int index = i;
            var row = CoachCard.ChecklistRow(items[i], t.Accent);
            row.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                ToggleThree(index);
            };
            panel.Children.Add(row);
        }
        _threeView = new Border
        {
            Width = ThreeWidth, Padding = new Thickness(10, 7, 10, 6), CornerRadius = new CornerRadius(10), Child = panel,
            Background = Art.Brush(Color.FromArgb(215, t.Ink.R, t.Ink.G, t.Ink.B)),
            BorderBrush = Art.Brush(Color.FromArgb(90, t.Accent.R, t.Accent.G, t.Accent.B)), BorderThickness = new Thickness(1),
        };
        _threeView.PointerPressed += (_, e) => e.Handled = true; // a click between the rows stays on the list
        _w.OfficeLayer.Children.Add(_threeView);
        _threeHud = default;
        _threeShown = false;
        PlaceThree();
    }

    /// <summary>Puts the list under the scoreboard (above it when there is no room below), or out of sight.</summary>
    void PlaceThree()
    {
        if (_threeView is not Border view) return;
        var hud = _w.HudBounds;
        bool show = _w.OverlayVisible && !_w.IsPeeking && !_fullScreen && hud.Width > 0 && S.Three.Items.Count > 0;
        view.IsVisible = show;
        if (!show)
        {
            SetThreeShown(false, default);
            return;
        }
        _threeHud = hud;
        view.Measure(Size.Infinity);
        double w = ThreeWidth, h = view.DesiredSize.Height;
        var a = _w.Arena;
        double x = hud.Center.X > a.Center.X ? hud.Right - w : hud.Left;
        x = Math.Clamp(x, a.Left + 4, Math.Max(a.Left + 4, a.Right - w - 4));
        double y = hud.Bottom + 6 + h < a.Bottom - 4 ? hud.Bottom + 6 : Math.Max(a.Top + 4, hud.Top - h - 6);
        Canvas.SetLeft(view, x);
        Canvas.SetTop(view, y);
        SetThreeShown(true, new Rect(x, y, w, h));
    }

    void SetThreeShown(bool shown, Rect rect)
    {
        if (shown == _threeShown && rect == _threeRect) return;
        _threeShown = shown;
        _threeRect = rect;
        _w.HitShapesChanged();
    }

    /// <summary>A frame or a tick: the scoreboard moved or changed size, so the list goes with it.</summary>
    void FollowScoreboard()
    {
        if (_threeView != null && (_w.HudBounds != _threeHud || _threeView.IsVisible != (_w.OverlayVisible && !_w.IsPeeking && !_fullScreen))) PlaceThree();
    }

    void CollectThreeShapes(List<HitShape> into)
    {
        if (_threeShown) into.Add(HitShape.Box(_threeRect));
    }

    // ------------------------------------------------------------------ ticking, writing

    /// <summary>Ticks or unticks one of today's three; ticking the last one counts toward "Three for three".</summary>
    public void ToggleThree(int index)
    {
        var items = S.Three.Items;
        if (index < 0 || index >= items.Count) return;
        bool finished = S.Three.Toggle(index, DateOnly.FromDateTime(DateTime.Now));
        _w.SaveSettings();
        _w.Sound.Play(items[index].Done ? "pop" : "whoosh", 0.4);
        if (finished)
        {
            _w.Stats.Add("work.three");
            Say(L.T("All of today's three done!"), L.T("a good day's work"), Green, "best", 0.7);
        }
        BuildThree();
    }

    /// <summary>Today's three as typed in the window (or "deskarcade --signal three:a|b|c").</summary>
    public void SetThree(IEnumerable<string?> texts)
    {
        S.Three.Set(texts, DateOnly.FromDateTime(DateTime.Now));
        _w.SaveSettings();
        BuildThree();
        if (S.Three.Items.Count > 0 && _card == null)
            Say(L.T("Today's three"), L.T("under the scoreboard · click one when it's done"), Gold, "pop", 0.5);
        _w.RefreshTray();
    }

    /// <summary>At work → My day → Today's three…: the small window to type them in.</summary>
    public void OpenThree() => ThreeWindow.ShowFor(_w, this);

    public Office.TodaysThree Three => S.Three;
}
