using System;
using Avalonia;

namespace DeskArcade.Engine;

/// <summary>Mutable 2D vector for the simulation (Avalonia's Point/Vector are immutable).</summary>
public struct Vec2 : IEquatable<Vec2>
{
    public double X;
    public double Y;

    public Vec2(double x, double y)
    {
        X = x;
        Y = y;
    }

    public readonly double Length => Math.Sqrt(X * X + Y * Y);
    public readonly double LengthSquared => X * X + Y * Y;

    public readonly Vec2 Normalized()
    {
        double l = Length;
        return l < 1e-12 ? default : new Vec2(X / l, Y / l);
    }

    public static double Dot(Vec2 a, Vec2 b) => a.X * b.X + a.Y * b.Y;

    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator -(Vec2 a) => new(-a.X, -a.Y);
    public static Vec2 operator *(Vec2 a, double k) => new(a.X * k, a.Y * k);
    public static Vec2 operator *(double k, Vec2 a) => new(a.X * k, a.Y * k);
    public static Vec2 operator /(Vec2 a, double k) => new(a.X / k, a.Y / k);

    public readonly Point ToPoint() => new(X, Y);
    public static implicit operator Vec2(Point p) => new(p.X, p.Y);

    public readonly bool Equals(Vec2 other) => X == other.X && Y == other.Y;
    public override readonly bool Equals(object? obj) => obj is Vec2 v && Equals(v);
    public override readonly int GetHashCode() => HashCode.Combine(X, Y);
    public static bool operator ==(Vec2 a, Vec2 b) => a.Equals(b);
    public static bool operator !=(Vec2 a, Vec2 b) => !a.Equals(b);
    public override readonly string ToString() => $"({X:0.#}, {Y:0.#})";
}
