using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>The line that won a "get N in a row" game, so the board can light it up. UI-free, so it can be tested.</summary>
public static class LineArt
{
    static readonly (int, int)[] Dirs = { (0, 1), (1, 0), (1, 1), (1, -1) };

    /// <summary>The squares of the first run of <paramref name="need"/> in a row on the board, or null while nobody has one.</summary>
    public static int[]? WinningLine(sbyte[] board, int cols, int rows, int need)
    {
        for (int sq = 0; sq < board.Length; sq++)
        {
            int side = board[sq];
            if (side == 0) continue;
            foreach (var (dr, dc) in Dirs)
            {
                var line = new List<int> { sq };
                int r = sq / cols + dr, c = sq % cols + dc;
                while (line.Count < need && r >= 0 && r < rows && c >= 0 && c < cols && board[r * cols + c] == side)
                {
                    line.Add(r * cols + c);
                    r += dr;
                    c += dc;
                }
                if (line.Count == need) return line.ToArray();
            }
        }
        return null;
    }
}

/// <summary>Tic-tac-toe against the CPU (which slips now and then) or over the LAN. Click a square; the mark pops in.</summary>
public sealed class TicTacToeGame : BoardGame
{
    static readonly Color Paper = Color.FromRgb(246, 243, 235);
    static readonly IBrush Ink = Art.Brush("#2B6CB0"), Ink2 = Art.Brush("#D64545"), Grid = Art.Brush("#3A3F4B");

    public TicTacToeGame(IGameHost host) : base(host, Paper, Paper) { }

    public override string Id => "tictactoe";
    public override string Title => "Tic-tac-toe";
    protected override int Cols => 3;
    protected override int Rows => 3;
    protected override double MaxSize => 330;
    protected override bool FlipForGuest => false;
    protected override string Score => SessionScore;
    protected override string YourMove => L.T("Your move · click a square · right-drag moves the board");
    protected override int[]? WinningLine => LineArt.WinningLine(Game.Board, Cols, Rows, 3);

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        foreach (var shape in Cross(new Vec2(-4, -3), 5, Ink, 2.5)) s.Rotor.Children.Add(shape);
        s.Rotor.Children.Add(Art.Circle(5, 4, 5, null, Ink2, 2.5));
        return s;
    }

    protected override IBoardRules NewRules() => LineRules.TicTacToe();

    protected override IBoardRules? Decode(string text) => LineRules.Decode(text, LineRules.TicTacToe());

    protected override string DrawReason(IBoardRules game) => L.T("The board is full");

    /// <summary>The right and bottom edge of each cell, skipping the board's outer edge.</summary>
    protected override void DrawSquare(Canvas into, int sq, Vec2 c, double cell)
    {
        double h = cell / 2;
        var box = new Rect(c.X - h, c.Y - h, cell, cell);
        if (sq % Cols < Cols - 1) into.Children.Add(Stroke(box.TopRight, box.BottomRight));
        if (sq / Cols < Rows - 1) into.Children.Add(Stroke(box.BottomLeft, box.BottomRight));
    }

    protected override void DrawPiece(Canvas into, sbyte piece, Vec2 c, double cell)
    {
        if (piece > 0)
            foreach (var shape in Cross(c, cell * 0.3, Ink, cell * 0.09)) into.Children.Add(shape);
        else into.Children.Add(Art.Circle(c.X, c.Y, cell * 0.3, null, Ink2, cell * 0.09));
    }

    static Line Stroke(Point a, Point b) => new() { StartPoint = a, EndPoint = b, Stroke = Grid, StrokeThickness = 3, IsHitTestVisible = false };

    static IEnumerable<Line> Cross(Vec2 c, double r, IBrush brush, double width) => new[]
    {
        new Line { StartPoint = new Point(c.X - r, c.Y - r), EndPoint = new Point(c.X + r, c.Y + r), Stroke = brush, StrokeThickness = width, StrokeLineCap = PenLineCap.Round },
        new Line { StartPoint = new Point(c.X + r, c.Y - r), EndPoint = new Point(c.X - r, c.Y + r), Stroke = brush, StrokeThickness = width, StrokeLineCap = PenLineCap.Round },
    };
}

/// <summary>Connect Four against the CPU or over the LAN. Click a column to drop a disc, which falls and bounces into place; four in a row wins.</summary>
public sealed class ConnectFourGame : BoardGame
{
    static readonly Color Frame = Color.FromRgb(38, 84, 196);
    static readonly Color Red = Color.FromRgb(230, 57, 70), Yellow = Color.FromRgb(255, 200, 45);

    public ConnectFourGame(IGameHost host) : base(host, Frame, Frame) { }

    public override string Id => "connect4";
    public override string Title => "Connect Four";
    protected override int Cols => 7;
    protected override int Rows => 6;
    protected override double MaxSize => 480;
    protected override bool FlipForGuest => false; // discs fall down on both screens
    protected override string Score => SessionScore;
    protected override string YourMove => L.T("Your move · click a square · right-drag moves the board");
    protected override int[]? WinningLine => LineArt.WinningLine(Game.Board, Cols, Rows, 4);

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        s.Rotor.Children.Add(new Rectangle { Width = 18, Height = 15, Fill = Art.Brush(Frame), RadiusX = 2, RadiusY = 2, [Canvas.LeftProperty] = -9.0, [Canvas.TopProperty] = -7.0 });
        s.Rotor.Children.Add(Art.Circle(-4, 3, 3.2, Art.Brush(Red)));
        s.Rotor.Children.Add(Art.Circle(4, 3, 3.2, Art.Brush(Yellow)));
        s.Rotor.Children.Add(Art.Circle(4, -3, 3.2, Art.Brush(Red)));
        return s;
    }

    protected override IBoardRules NewRules() => LineRules.ConnectFour();

    protected override IBoardRules? Decode(string text) => LineRules.Decode(text, LineRules.ConnectFour());

    protected override string DrawReason(IBoardRules game) => L.T("The board is full");

    /// <summary>A click anywhere in a column drops a disc into it.</summary>
    protected override int[]? PlacementAt(int sq, List<int[]> moves) => moves.FirstOrDefault(m => m[0] % Cols == sq % Cols);

    /// <summary>The disc falls in from above the frame; the lower the row, the longer the fall.</summary>
    protected override (Vec2 From, double Seconds)? DropFrom(int sq) => (new Vec2(Local(sq).X, -Cell * 0.7), 0.3 + 0.05 * (sq / Cols + 1));

    protected override void DrawSquare(Canvas into, int sq, Vec2 c, double cell) =>
        into.Children.Add(Art.Circle(c.X, c.Y, cell * 0.38, Art.Brush("#141A2A"), Art.Brush(Art.Blend(Frame, Colors.Black, 0.4)), 2));

    protected override void DrawPiece(Canvas into, sbyte piece, Vec2 c, double cell)
    {
        var color = piece > 0 ? Art.Safe(Red) : Yellow;
        into.Children.Add(Art.Circle(c.X, c.Y, cell * 0.38, Art.Brush(color), Art.Brush(Art.Blend(color, Colors.Black, 0.35)), 2));
        into.Children.Add(Art.Circle(c.X, c.Y, cell * 0.25, null, Art.Brush(Art.Blend(color, Colors.Black, 0.2)), 1.5));
        if (Art.ColorBlind && piece < 0) into.Children.Add(Art.Circle(c.X, c.Y, cell * 0.1, Art.Brush("#3A2A00"))); // a shape cue too
    }
}
