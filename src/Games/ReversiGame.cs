using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>
/// Reversi (see <see cref="ReversiRules"/>) against the CPU or over the LAN. Click one of the dotted squares; the
/// disc pops in and the discs it outflanks turn over one after another. A side with no move passes, and both
/// screens say so. Black (you, or the host) moves first.
/// </summary>
public sealed class ReversiGame : BoardGame
{
    static readonly Color Felt = Color.FromRgb(28, 122, 74);
    static readonly Color Black = Color.FromRgb(30, 32, 36), White = Color.FromRgb(242, 242, 236);
    static readonly IBrush Lines = Art.Brush(150, 10, 40, 24);

    public ReversiGame(IGameHost host) : base(host, Felt, Felt) { }

    public override string Id => "reversi";
    public override string Title => "Reversi";
    protected override double MaxSize => 480;
    protected override bool FlipForGuest => false; // both players see the same board
    protected override bool DotPlacements => true;
    protected override string YourMove => L.T("Your move · click a dotted square · right-drag moves the board");

    /// <summary>Easy: random, leaning to big flips; Medium: greedy with corners; Hard and Expert: a search 4 and 6 moves deep.</summary>
    protected override int[] LevelDepths => new[] { 0, 1, 4, 6 };

    protected override double Blunder(int level) => 0; // the levels themselves play loosely enough

    protected override string? Note => ((ReversiRules)Game).Passed switch
    {
        0 => null,
        int side when side == Me => L.T("You have no move · pass"),
        _ => L.F("{0} has no move · pass", Opponent?.Name ?? L.T("CPU")),
    };

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        s.Rotor.Children.Add(Art.Circle(-4, 2, 6.5, Art.Brush(Black), Art.Brush("#888888"), 1));
        s.Rotor.Children.Add(Art.Circle(4, -3, 6.5, Art.Brush(White), Art.Brush("#555555"), 1));
        return s;
    }

    protected override IBoardRules NewRules() => ReversiRules.New();

    protected override IBoardRules? Decode(string text) => ReversiRules.Decode(text);

    protected override string DrawReason(IBoardRules game) => L.T("Equal discs");

    /// <summary>The flips are no captures: a soft click that grows with the number turned, and a count for the daily task.</summary>
    protected override void MoveFx(int captures, bool byMe)
    {
        Host.Sound.Play("board", 0.45, 1.2);
        if (captures <= 0) return;
        Host.Sound.Play("click", Math.Min(0.8, 0.25 + 0.06 * captures), 1.3);
        if (byMe) Host.Stats.Add("reversi.flips", captures);
    }

    protected override void Won(int cpuLevel)
    {
        if (cpuLevel >= 3) Host.Stats.Add("reversi.hardwins");
        if (Game.Count(Me) >= 40 || Game.Count(-Me) == 0) Host.Stats.Add("reversi.landslides");
    }

    /// <summary>The grid lines and the four marker dots.</summary>
    protected override void DrawSquare(Canvas into, int sq, Vec2 c, double cell)
    {
        double h = cell / 2;
        var box = new Rect(c.X - h, c.Y - h, cell, cell);
        int r = sq / Cols, col = sq % Cols;
        if (col < Cols - 1) into.Children.Add(Stroke(box.TopRight, box.BottomRight));
        if (r < Rows - 1) into.Children.Add(Stroke(box.BottomLeft, box.BottomRight));
        if (r is 1 or 5 && col is 1 or 5) into.Children.Add(Art.Circle(box.Right, box.Bottom, cell * 0.06, Lines));
    }

    /// <summary>A black or a white disc; with colour-blind colours on, the white one carries a ring as well.</summary>
    protected override void DrawPiece(Canvas into, sbyte piece, Vec2 c, double cell)
    {
        double r = cell * 0.4;
        var color = piece > 0 ? Black : White;
        into.Children.Add(Art.Circle(c.X + 1.5, c.Y + 2.5, r, Art.Brush(70, 0, 0, 0)));
        into.Children.Add(Art.Circle(c.X, c.Y, r, Art.Brush(color), Art.Brush(Art.Blend(color, piece > 0 ? Colors.White : Colors.Black, 0.3)), 1.5));
        into.Children.Add(Art.Circle(c.X - r * 0.3, c.Y - r * 0.3, r * 0.3, Art.Brush(piece > 0 ? (byte)40 : (byte)110, 255, 255, 255)));
        if (Art.ColorBlind && piece < 0) into.Children.Add(Art.Circle(c.X, c.Y, r * 0.5, null, Art.Brush("#3A3F4B"), 2)); // a shape cue too
    }

    static Line Stroke(Point a, Point b) => new() { StartPoint = a, EndPoint = b, Stroke = Lines, StrokeThickness = 1.5, IsHitTestVisible = false };
}
