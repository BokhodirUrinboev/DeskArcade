using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using DeskArcade.Engine;
using DeskArcade.Net;

namespace DeskArcade;

/// <summary>
/// The reactions' pictures, drawn in code like the rest of the art, about 40 px across around (0, 0), and their names
/// (for the menus and the line under a reaction that floats in).
/// </summary>
public static class ReactionArt
{
    public static readonly Reaction[] All = System.Enum.GetValues<Reaction>();

    public static string Name(Reaction r) => r switch
    {
        Reaction.Party => L.T("Party!"),
        Reaction.Coffee => L.T("Coffee?"),
        Reaction.Laugh => L.T("Ha ha!"),
        Reaction.Fire => L.T("On fire!"),
        Reaction.Heart => L.T("Love it"),
        Reaction.Star => L.T("Nice!"),
        Reaction.Lunch => L.T("Lunch?"),
        _ => L.T("Thumbs up"),
    };

    /// <summary>The sound a reaction arrives with; a party is a small cheer, which a pet keeping you company dances to.</summary>
    public static (string Clip, double Volume, double Pitch) Sound(Reaction r) => r switch
    {
        Reaction.Party => ("done", 0.35, 1.2),
        Reaction.Laugh => ("chat", 0.45, 1.25),
        Reaction.Heart or Reaction.Star => ("star", 0.5, 1),
        _ => ("chat", 0.45, 1),
    };

    public static Canvas Icon(Reaction r)
    {
        var c = new Canvas { IsHitTestVisible = false };
        var ink = Art.Brush("#3A2A12");
        switch (r)
        {
            case Reaction.ThumbsUp:
            {
                var skin = Art.Brush("#FFC83D");
                var edge = Art.Brush("#B07A12");
                c.Children.Add(Art.At(new Rectangle { Width = 22, Height = 18, RadiusX = 6, RadiusY = 6, Fill = skin, Stroke = edge, StrokeThickness = 1.5 }, -10, -1));
                c.Children.Add(Art.PathOf("M-7,0 C-8,-8 -7,-17 -1,-19 C4,-20 5,-15 4,-11 L3,0 Z", skin, edge, 1.5));
                foreach (double y in new[] { 4.0, 8.5, 13.0 }) c.Children.Add(Art.PathOf($"M4,{Art.F(y)} L11,{Art.F(y)}", null, edge, 1.3));
                c.Children.Add(Art.At(new Rectangle { Width = 7, Height = 20, RadiusX = 2, RadiusY = 2, Fill = Art.Brush("#4DA3FF") }, -17, -2));
                break;
            }
            case Reaction.Party:
                c.Children.Add(Art.PathOf("M-15,17 L-3,-6 L8,6 Z", Art.Brush("#8E5BD8"), Art.Brush("#4B2A86"), 1.5));
                c.Children.Add(Art.PathOf("M-11,9 L-2,13 M-7,1 L3,6", null, Art.Brush("#FFD166"), 2));
                c.Children.Add(Art.PathOf("M-3,-7 Q3,-19 13,-16", null, Art.Brush("#FF6FA8"), 2));
                c.Children.Add(Art.PathOf("M2,-3 Q12,-9 17,-1", null, Art.Brush("#4DA3FF"), 2));
                c.Children.Add(Art.Circle(8, -12, 2.5, Art.Brush("#FF5C6C")));
                c.Children.Add(Art.Circle(15, -8, 2, Art.Brush("#FFD166")));
                c.Children.Add(Art.Circle(0, -17, 2, Art.Brush("#3DDC84")));
                c.Children.Add(Art.At(new Rectangle { Width = 4, Height = 4, Fill = Art.Brush("#4DA3FF"), RenderTransform = new RotateTransform(30) }, 13, -18));
                break;
            case Reaction.Coffee:
                c.Children.Add(Art.At(new Ellipse { Width = 34, Height = 7, Fill = Art.Brush("#D9D3C3") }, -17, 13));
                c.Children.Add(Art.PathOf("M12,-1 C21,-1 21,11 10,10", null, Art.Brush("#F4F1E8"), 3.5));
                c.Children.Add(Art.PathOf("M-12,-5 L12,-5 L10,14 C10,17 -10,17 -10,14 Z", Art.Brush("#F4F1E8"), Art.Brush("#B5AE9C"), 1.5));
                c.Children.Add(Art.At(new Ellipse { Width = 22, Height = 5, Fill = Art.Brush("#6B3E1E") }, -11, -7));
                c.Children.Add(Art.PathOf("M-5,-10 C-9,-14 -1,-16 -5,-21 M4,-10 C0,-14 8,-16 4,-21", null, Art.Brush(150, 200, 205, 215), 2));
                break;
            case Reaction.Laugh:
                c.Children.Add(Art.Circle(0, 0, 17, Art.Brush("#FFC83D"), Art.Brush("#B07A12"), 1.5));
                c.Children.Add(Art.PathOf("M-10,-4 Q-6,-10 -2,-4 M2,-4 Q6,-10 10,-4", null, ink, 2.2));
                c.Children.Add(Art.PathOf("M-10,2 Q0,18 10,2 Z", ink));
                c.Children.Add(Art.PathOf("M-5,9 Q0,13 5,9 Q0,6 -5,9 Z", Art.Brush("#FF6F7F")));
                c.Children.Add(Art.PathOf("M-17,-3 C-21,2 -19,6 -16,5 C-14,4 -15,0 -17,-3 Z M17,-3 C21,2 19,6 16,5 C14,4 15,0 17,-3 Z", Art.Brush("#6EC6FF")));
                break;
            case Reaction.Fire:
                c.Children.Add(Art.PathOf("M0,-20 C8,-10 16,-4 12,8 C10,16 4,19 0,19 C-4,19 -12,16 -12,6 C-12,-2 -6,-4 -4,-12 C-2,-6 0,-8 0,-20 Z", Art.Brush("#FF6B1A"), Art.Brush("#C0390B"), 1.2));
                c.Children.Add(Art.PathOf("M0,-6 C5,0 8,4 6,10 C5,15 2,17 0,17 C-3,17 -7,14 -6,8 C-6,3 -2,2 0,-6 Z", Art.Brush("#FFD23F")));
                break;
            case Reaction.Heart:
                c.Children.Add(Art.PathOf("M0,17 C-22,2 -19,-17 -7,-16 C-2,-15 0,-10 0,-7 C0,-10 2,-15 7,-16 C19,-17 22,2 0,17 Z", Art.Brush(Art.Safe(Color.FromRgb(236, 52, 84))), Art.Brush("#8E1B33"), 1.5));
                c.Children.Add(Art.At(new Ellipse { Width = 6, Height = 8, Fill = Art.Brush(120, 255, 255, 255) }, -12, -11));
                break;
            case Reaction.Star:
                c.Children.Add(Art.PathOf(Art.StarPath(0, 2, 19, 8), Art.Brush("#FFD166"), Art.Brush("#B7791F"), 1.5));
                c.Children.Add(Art.PathOf(Art.StarPath(15, -14, 5, 2), Art.Brush("#FFF3C4")));
                c.Children.Add(Art.PathOf(Art.StarPath(-16, -10, 3.5, 1.4), Art.Brush("#FFF3C4")));
                break;
            default: // lunch: a slice of pizza
                c.Children.Add(Art.PathOf("M-15,-12 L15,-12 L0,19 Z", Art.Brush("#FFD66B"), Art.Brush("#D99A1E"), 1.5));
                c.Children.Add(Art.PathOf("M-16,-17 L16,-17 L15,-11 L-15,-11 Z", Art.Brush("#C98A45"), Art.Brush("#8A5A2B"), 1.2));
                foreach (var (x, y) in new[] { (-6.0, -5.0), (5.0, -4.0), (0.0, 6.0) }) c.Children.Add(Art.Circle(x, y, 3.2, Art.Brush("#D8413A")));
                break;
        }
        return c;
    }
}
