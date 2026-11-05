using System;

namespace HexTactics.Core.Hex;

/// <summary>
/// A point / vector on the map plane in world units. <see cref="X"/> grows to the east
/// (screen right), <see cref="Z"/> grows to the south (towards higher rows / the camera).
/// The Godot client maps it 1:1 to <c>Vector3(X, height, Z)</c>.
/// </summary>
public readonly record struct PlaneVector(double X, double Z)
{
    public static PlaneVector operator +(PlaneVector a, PlaneVector b) => new(a.X + b.X, a.Z + b.Z);

    public static PlaneVector operator -(PlaneVector a, PlaneVector b) => new(a.X - b.X, a.Z - b.Z);

    public static PlaneVector operator *(PlaneVector a, double k) => new(a.X * k, a.Z * k);

    public double Length => Math.Sqrt((X * X) + (Z * Z));

    public double DistanceTo(PlaneVector other) => (this - other).Length;

    public static PlaneVector Lerp(PlaneVector a, PlaneVector b, double t) => new(a.X + ((b.X - a.X) * t), a.Z + ((b.Z - a.Z) * t));

    public override string ToString() => $"({X:0.###}, {Z:0.###})";
}
