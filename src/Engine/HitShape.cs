using Avalonia;

namespace DeskArcade.Engine;

/// <summary>
/// An area of the overlay that should receive the mouse. Everything outside all hit shapes
/// passes clicks through to the windows underneath.
/// </summary>
public readonly record struct HitShape(Rect Bounds, bool Round)
{
    public static HitShape Circle(Vec2 center, double radius) =>
        new(new Rect(center.X - radius, center.Y - radius, radius * 2, radius * 2), true);

    public static HitShape Box(Rect rect) => new(rect, false);

    public bool Contains(Vec2 p)
    {
        if (!Round) return Bounds.Contains(p.ToPoint());
        var c = Bounds.Center;
        double r = Bounds.Width / 2, dx = p.X - c.X, dy = p.Y - c.Y;
        return dx * dx + dy * dy <= r * r;
    }
}
