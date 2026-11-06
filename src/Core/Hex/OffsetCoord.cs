namespace HexTactics.Core.Hex;

/// <summary>
/// Odd-q offset coordinate for flat-top hexes: <c>Col</c> is the horizontal index (0..11 on the
/// prototype map), <c>Row</c> the vertical index from the top (0..9). Odd columns are shifted down by
/// half a hex height. Used for map storage and display only; rules work in axial/cube coordinates.
/// </summary>
public readonly record struct OffsetCoord(int Col, int Row)
{
    public override string ToString() => $"({Col},{Row})";
}
