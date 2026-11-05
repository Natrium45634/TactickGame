using Godot;
using HexTactics.Core.Hex;

namespace HexTactics.Client.Presentation;

/// <summary>Conversions between the pure model types and Godot vectors.</summary>
public static class GodotConvert
{
    public static Vector3 ToWorld(this PlaneVector p, float height = 0f) => new((float)p.X, height, (float)p.Z);

    public static PlaneVector ToPlane(this Vector3 v) => new(v.X, v.Z);
}
