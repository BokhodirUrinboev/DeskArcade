using System;
using Avalonia.Controls;
using Avalonia.Media;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>Chess (see <see cref="ChessRules"/>) against the CPU or over the LAN. Pawns always promote to a queen.</summary>
public sealed class ChessGame : BoardGame
{
    // white: the filled glyph in white with the outline glyph on top as its edge; black: the filled glyph alone
    const string Filled = " ♟♞♝♜♛♚", Outline = " ♙♘♗♖♕♔";
    static readonly FontFamily Symbols = new("Segoe UI Symbol, DejaVu Sans, Apple Symbols, Noto Sans Symbols 2");

    public ChessGame(IGameHost host) : base(host, Color.FromRgb(238, 238, 210), Color.FromRgb(118, 150, 86)) { }

    public override string Id => "chess";
    public override string Title => "Chess";

    protected override int[] LevelDepths => new[] { 1, 2, 3, 3 };

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        s.Rotor.Children.Add(Glyph("♚", 20, Brushes.White, -10, -14));
        s.Rotor.Children.Add(Glyph("♔", 20, Brushes.Black, -10, -14));
        return s;
    }

    protected override IBoardRules NewRules() => ChessRules.New();

    protected override IBoardRules? Decode(string text) => ChessRules.Decode(text);

    protected override string DrawReason(IBoardRules game) => ((ChessRules)game).DrawKind switch
    {
        "stalemate" => L.T("Stalemate"),
        "fifty" => L.T("50 moves without a capture or pawn move"),
        _ => L.T("Not enough pieces to mate"),
    };

    protected override void DrawPiece(Canvas into, sbyte piece, Vec2 c, double cell)
    {
        int kind = Math.Abs(piece);
        double size = cell * 0.8, x = c.X - cell / 2, y = c.Y - size * 0.66;
        if (piece > 0)
        {
            into.Children.Add(Glyph(Filled[kind].ToString(), size, Brushes.White, x, y, cell));
            into.Children.Add(Glyph(Outline[kind].ToString(), size, Brushes.Black, x, y, cell));
        }
        else into.Children.Add(Glyph(Filled[kind].ToString(), size, Brushes.Black, x, y, cell)); // solid black, one layer: no blur
    }

    static TextBlock Glyph(string text, double size, IBrush brush, double x, double y, double width = 20)
    {
        var t = new TextBlock
        {
            Text = text, FontFamily = Symbols, FontSize = size, Foreground = brush, Width = width,
            TextAlignment = TextAlignment.Center, IsHitTestVisible = false,
        };
        Canvas.SetLeft(t, x);
        Canvas.SetTop(t, y);
        return t;
    }
}
