using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>
/// Gomoku (see <see cref="GomokuRules"/>) against the CPU or over the LAN, on a 15×15 wooden board. Click an
/// intersection to put a stone there; five or more in a row wins and lights up. Black (you, or the host) moves first.
/// </summary>
public sealed class GomokuGame : BoardGame
{
    static readonly Color Wood = Color.FromRgb(224, 186, 122);
    static readonly Color Black = Color.FromRgb(28, 30, 34), White = Color.FromRgb(244, 244, 238);
    static readonly IBrush Lines = Art.Brush(200, 70, 48, 24);

    public GomokuGame(IGameHost host) : base(host, Wood, Wood) { }

    public override string Id => "gomoku";
    public override string Title => "Gomoku";
    protected override int Cols => GomokuRules.N;
    protected override int Rows => GomokuRules.N;
    protected override double MaxSize => 540;
    protected override bool FlipForGuest => false; // both players see the same board
    protected override string Score => SessionScore;
    protected override string YourMove => L.T("Your move · click an intersection · right-drag moves the board");
    protected override int[]? WinningLine => ((GomokuRules)Game).WinLine;

    /// <summary>Easy: its own lines or anywhere near; Medium: the best threat or block; Hard and Expert: that plus a search 3 and 4 moves deep.</summary>
    protected override int[] LevelDepths => new[] { 0, 1, 3, 4 };

    protected override double Blunder(int level) => 0; // a random point anywhere on 225 looks silly; Easy has its own slips

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        for (int i = -1; i <= 1; i++)
        {
            s.Rotor.Children.Add(new Line { StartPoint = new Point(-9, i * 6), EndPoint = new Point(9, i * 6), Stroke = Art.Brush("#C9A46A"), StrokeThickness = 1 });
            s.Rotor.Children.Add(new Line { StartPoint = new Point(i * 6, -9), EndPoint = new Point(i * 6, 9), Stroke = Art.Brush("#C9A46A"), StrokeThickness = 1 });
        }
        s.Rotor.Children.Add(Art.Circle(-6, 6, 4, Art.Brush(Black)));
        s.Rotor.Children.Add(Art.Circle(0, 0, 4, Art.Brush(Black)));
        s.Rotor.Children.Add(Art.Circle(6, -6, 4, Art.Brush(White), Art.Brush("#555555"), 1));
        return s;
    }

    protected override IBoardRules NewRules() => new GomokuRules();

    protected override IBoardRules? Decode(string text) => GomokuRules.Decode(text);

    protected override string DrawReason(IBoardRules game) => L.T("The board is full");

    protected override void Won(int cpuLevel)
    {
        if (cpuLevel >= 3) Host.Stats.Add("gomoku.hardwins");
        if (Game.Count(Me) < 15) Host.Stats.Add("gomoku.quickwins");
    }

    /// <summary>The lines run through the middle of each square, so the stones sit on their crossings; five star points.</summary>
    protected override void DrawSquare(Canvas into, int sq, Vec2 c, double cell)
    {
        int r = sq / Cols, col = sq % Cols;
        if (col < Cols - 1) into.Children.Add(Stroke(new Point(c.X, c.Y), new Point(c.X + cell, c.Y)));
        if (r < Rows - 1) into.Children.Add(Stroke(new Point(c.X, c.Y), new Point(c.X, c.Y + cell)));
        if (r is 3 or 7 or 11 && col is 3 or 7 or 11 && (r == 7) == (col == 7)) into.Children.Add(Art.Circle(c.X, c.Y, cell * 0.1, Lines));
    }

    /// <summary>A black or a white stone; with colour-blind colours on, the white one carries a ring as well.</summary>
    protected override void DrawPiece(Canvas into, sbyte piece, Vec2 c, double cell)
    {
        double r = cell * 0.44;
        var color = piece > 0 ? Black : White;
        into.Children.Add(Art.Circle(c.X + 1.5, c.Y + 2, r, Art.Brush(70, 0, 0, 0)));
        into.Children.Add(Art.Circle(c.X, c.Y, r, Art.Brush(color), Art.Brush(Art.Blend(color, piece > 0 ? Colors.White : Colors.Black, 0.3)), 1));
        into.Children.Add(Art.Circle(c.X - r * 0.3, c.Y - r * 0.3, r * 0.3, Art.Brush(piece > 0 ? (byte)45 : (byte)120, 255, 255, 255)));
        if (Art.ColorBlind && piece < 0) into.Children.Add(Art.Circle(c.X, c.Y, r * 0.45, null, Art.Brush("#3A3F4B"), 1.5)); // a shape cue too
    }

    static Line Stroke(Point a, Point b) => new() { StartPoint = a, EndPoint = b, Stroke = Lines, StrokeThickness = 1, IsHitTestVisible = false };
}
