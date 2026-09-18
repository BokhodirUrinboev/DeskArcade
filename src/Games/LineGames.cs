using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>Tic-tac-toe against the CPU (which slips now and then) or over the LAN. Click a square.</summary>
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

    protected override void DrawEmpty(Canvas into, Vec2 c, double cell) => GridLines(into, c, cell);

    protected override void DrawPiece(Canvas into, sbyte piece, Vec2 c, double cell)
    {
        GridLines(into, c, cell);
        if (piece > 0)
            foreach (var shape in Cross(c, cell * 0.3, Ink, cell * 0.09)) into.Children.Add(shape);
        else into.Children.Add(Art.Circle(c.X, c.Y, cell * 0.3, null, Ink2, cell * 0.09));
    }

    /// <summary>The right and bottom edge of each cell, skipping the board's outer edge.</summary>
    void GridLines(Canvas into, Vec2 c, double cell)
    {
        double h = cell / 2;
        var box = new Rect(c.X - h, c.Y - h, cell, cell);
        if (c.X + cell < Center(8).X + h) into.Children.Add(Stroke(box.TopRight, box.BottomRight));
        if (c.Y + cell < Center(8).Y + h) into.Children.Add(Stroke(box.BottomLeft, box.BottomRight));
    }

    static Line Stroke(Point a, Point b) => new() { StartPoint = a, EndPoint = b, Stroke = Grid, StrokeThickness = 3, IsHitTestVisible = false };

    static IEnumerable<Line> Cross(Vec2 c, double r, IBrush brush, double width) => new[]
    {
        new Line { StartPoint = new Point(c.X - r, c.Y - r), EndPoint = new Point(c.X + r, c.Y + r), Stroke = brush, StrokeThickness = width, StrokeLineCap = PenLineCap.Round },
        new Line { StartPoint = new Point(c.X + r, c.Y - r), EndPoint = new Point(c.X - r, c.Y + r), Stroke = brush, StrokeThickness = width, StrokeLineCap = PenLineCap.Round },
    };
}

/// <summary>Connect Four against the CPU or over the LAN. Click a column to drop a disc; four in a row wins.</summary>
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

    protected override void DrawEmpty(Canvas into, Vec2 c, double cell) =>
        into.Children.Add(Art.Circle(c.X, c.Y, cell * 0.38, Art.Brush("#141A2A"), Art.Brush(Art.Blend(Frame, Colors.Black, 0.4)), 2));

    protected override void DrawPiece(Canvas into, sbyte piece, Vec2 c, double cell)
    {
        var color = piece > 0 ? Art.Safe(Red) : Yellow;
        into.Children.Add(Art.Circle(c.X, c.Y, cell * 0.38, Art.Brush(color), Art.Brush(Art.Blend(color, Colors.Black, 0.35)), 2));
        into.Children.Add(Art.Circle(c.X, c.Y, cell * 0.25, null, Art.Brush(Art.Blend(color, Colors.Black, 0.2)), 1.5));
        if (Art.ColorBlind && piece < 0) into.Children.Add(Art.Circle(c.X, c.Y, cell * 0.1, Art.Brush("#3A2A00"))); // a shape cue too
    }
}
