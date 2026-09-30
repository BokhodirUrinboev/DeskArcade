using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DeskArcade.Engine;

namespace DeskArcade;

/// <summary>A button on an <see cref="InfoCard"/>: its label, whether it is the main one (in the theme's accent), and what it does.</summary>
public sealed record InfoAction(string Label, bool Main, Action Click);

/// <summary>
/// A card in the middle of the overlay that says something and waits for a button: the first-run tour and the year at
/// the desk. A small picture on the left (drawn in code, optional), a step line, a title, a few lines of text and the
/// buttons. It takes its own clicks; the game under it waits. It stays until a button closes it.
/// </summary>
public sealed class InfoCard
{
    const double CardWidth = 440, ArtSize = 96;

    public Border Root { get; }
    public Rect Area { get; private set; }

    /// <param name="art">A picture about <see cref="ArtSize"/> square, or null.</param>
    public InfoCard(Rect arena, string step, string title, IReadOnlyList<string> lines, IReadOnlyList<InfoAction> actions, Control? art = null)
    {
        var t = Themes.Current;
        var words = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        if (step.Length > 0) words.Children.Add(Text(step, 11, FontWeight.SemiBold, Art.Blend(t.HudFront, t.HudBack, 0.4)));
        words.Children.Add(Text(title, 20, FontWeight.Bold, Colors.White));
        foreach (string line in lines) words.Children.Add(Text(line, 13, FontWeight.Normal, Color.Parse("#DDE3EA")));

        var row = new DockPanel();
        if (art != null)
        {
            var host = new Panel { Width = ArtSize, Height = ArtSize, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 16, 0) };
            host.Children.Add(art);
            DockPanel.SetDock(host, Dock.Left);
            row.Children.Add(host);
        }
        row.Children.Add(words);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var a in actions) buttons.Children.Add(OfficeDesk.CardButton(a.Label, a.Main ? t.Accent : Color.FromRgb(90, 98, 112), a.Click));
        var body = new StackPanel { Spacing = 4, Children = { row, buttons } };

        Root = new Border
        {
            Width = CardWidth, Padding = new Thickness(20, 16), CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(1.5),
            Background = Art.Brush(Color.FromArgb(246, t.Ink.R, t.Ink.G, t.Ink.B)), BorderBrush = Art.Brush(Color.FromArgb(170, t.Accent.R, t.Accent.G, t.Accent.B)),
            BoxShadow = BoxShadows.Parse("0 8 30 0 #66000000"), Child = body,
        };
        Root.PointerPressed += (_, e) => e.Handled = true; // the card takes its clicks; the game under it waits
        Place(arena);
    }

    /// <summary>Centres the card a little above the middle of the arena.</summary>
    public void Place(Rect arena)
    {
        Root.Measure(Size.Infinity);
        double h = Root.DesiredSize.Height;
        double x = arena.Center.X - CardWidth / 2;
        double y = Math.Clamp(arena.Top + arena.Height * 0.45 - h / 2, arena.Top + 8, Math.Max(arena.Top + 8, arena.Bottom - h - 12));
        Canvas.SetLeft(Root, x);
        Canvas.SetTop(Root, y);
        Area = new Rect(x, y, CardWidth, h);
    }

    static TextBlock Text(string text, double size, FontWeight weight, Color color) => new()
    {
        Text = text, FontFamily = Fx.Font, FontSize = size, FontWeight = weight, Foreground = Art.Brush(color), TextWrapping = TextWrapping.Wrap,
    };
}
