using System;
using Avalonia.Controls;
using Avalonia.Media;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>Checkers with Russian rules (shashki, see <see cref="Draughts"/>) against the CPU or over the LAN.</summary>
public sealed class CheckersGame : BoardGame
{
    static readonly Color White = Color.FromRgb(240, 240, 235), Red = Color.FromRgb(214, 64, 69);

    public CheckersGame(IGameHost host) : base(host, Color.FromRgb(232, 221, 200), Color.FromRgb(120, 84, 60)) { }

    public override string Id => "checkers";
    public override string Title => "Checkers";

    protected override int[] LevelDepths => new[] { 1, 2, 4, 5 };

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        s.Rotor.Children.Add(Art.Circle(-3, 2, 7, Art.Brush(Red), Art.Brush("#6B1E22"), 1));
        s.Rotor.Children.Add(Art.Circle(3, -3, 7, Art.Brush(White), Art.Brush("#555555"), 1));
        return s;
    }

    protected override IBoardRules NewRules() => Draughts.New();

    protected override IBoardRules? Decode(string text) => Draughts.Decode(text);

    protected override string DrawReason(IBoardRules game) => L.T("15 moves without a capture or a man moving");

    protected override void DrawPiece(Canvas into, sbyte piece, Vec2 c, double cell)
    {
        double r = cell * 0.38;
        var color = piece > 0 ? White : Red;
        into.Children.Add(Art.Circle(c.X + 2, c.Y + 3, r, Art.Brush(70, 0, 0, 0)));
        into.Children.Add(Art.Circle(c.X, c.Y, r, Art.Brush(color), Art.Brush(Art.Blend(color, Colors.Black, 0.45)), 2));
        into.Children.Add(Art.Circle(c.X, c.Y, r * 0.62, null, Art.Brush(Art.Blend(color, Colors.Black, 0.25)), 1.5));
        if (Math.Abs(piece) == 2) into.Children.Add(Art.Circle(c.X, c.Y, r * 0.3, Art.Brush(Gold)));
    }
}
