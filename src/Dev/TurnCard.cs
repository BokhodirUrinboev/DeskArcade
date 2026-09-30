using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DeskArcade.Engine;

namespace DeskArcade.Dev;

/// <summary>
/// The card a finished turn leaves under the scoreboard when "What Claude is doing" is on: who is done, the usual line
/// (your turn, how long it worked and you played), and what the turn changed and ran. It takes no clicks (the overlay's
/// hit shapes leave it out, so the mouse passes through) and fades after <see cref="Seconds"/>.
/// </summary>
public sealed class TurnCard
{
    public const double Seconds = 9;
    const int MaxFiles = 4, MaxCommands = 3;

    readonly Border _card;
    Canvas? _layer;

    public TurnCard(string title, string sub, Color color, IReadOnlyList<string> lines)
    {
        var theme = Themes.Current;
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(Line(title, 15, FontWeight.Bold, Art.Safe(Themes.Themed(color))));
        stack.Children.Add(Line(sub, 11, FontWeight.Normal, Art.Blend(theme.HudFront, theme.HudBack, 0.15)));
        foreach (string line in lines) stack.Children.Add(Line(line, 11, FontWeight.SemiBold, theme.HudFront));
        _card = new Border
        {
            Child = stack, CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 8, 14, 10), MaxWidth = 380,
            Background = Art.Brush(Color.FromArgb(236, theme.HudBack.R, theme.HudBack.G, theme.HudBack.B)),
            BorderBrush = Art.Brush(Color.FromArgb(150, color.R, color.G, color.B)), BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
            Transitions = new Transitions { new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(400) } },
        };
    }

    static TextBlock Line(string text, double size, FontWeight weight, Color color) => new()
    {
        Text = text, FontFamily = Fx.Font, FontSize = size, FontWeight = weight, Foreground = Art.Brush(color),
        TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 352,
    };

    /// <summary>
    /// What a turn did, a line each: "3 files: Program.cs, Hud.cs, Ipc.cs" (or "Changed Program.cs") and
    /// "5 commands: dotnet test · git status · npm run lint +2" (or "Ran dotnet test"). Empty when it did neither.
    /// </summary>
    public static List<string> Lines(AgentTurn turn)
    {
        var lines = new List<string>();
        var files = turn.Files.Select(f => AgentStep.FileName(f, 28)).ToList();
        if (files.Count == 1) lines.Add(L.F("Changed {0}", files[0]));
        else if (files.Count > 1)
        {
            string list = string.Join(", ", files.Take(MaxFiles));
            if (files.Count > MaxFiles) list += " " + L.F("+{0} more", files.Count - MaxFiles);
            lines.Add(L.F("{0} files: {1}", files.Count, list));
        }
        var commands = turn.Commands.Select(c => AgentStep.Command(c)).Where(c => c.Length > 0).ToList();
        var distinct = commands.Distinct().ToList();
        if (commands.Count == 1) lines.Add(L.F("Ran {0}", commands[0]));
        else if (commands.Count > 1)
        {
            string list = string.Join(" · ", distinct.Take(MaxCommands));
            if (distinct.Count > MaxCommands) list += " " + L.F("+{0} more", distinct.Count - MaxCommands);
            lines.Add(L.F("{0} commands: {1}", commands.Count, list));
        }
        return lines;
    }

    /// <summary>Shows the card under the scoreboard (above it when the scoreboard sits low), then fades it away.</summary>
    public void ShowIn(Canvas layer, Rect hud, Rect arena)
    {
        _layer = layer;
        _card.Measure(Size.Infinity);
        var size = _card.DesiredSize;
        double x = Math.Clamp(hud.Left, arena.Left + 8, Math.Max(arena.Left + 8, arena.Right - size.Width - 8));
        double y = hud.Bottom + 8 + size.Height < arena.Bottom - 8 ? hud.Bottom + 8 : Math.Max(arena.Top + 8, hud.Top - size.Height - 8);
        Canvas.SetLeft(_card, x);
        Canvas.SetTop(_card, y);
        layer.Children.Add(_card);
        DispatcherTimer.RunOnce(() => _card.Opacity = 0, TimeSpan.FromSeconds(Seconds));
        DispatcherTimer.RunOnce(Close, TimeSpan.FromSeconds(Seconds + 0.5));
    }

    public void Close()
    {
        _layer?.Children.Remove(_card);
        _layer = null;
    }
}
