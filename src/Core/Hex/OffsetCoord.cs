namespace HexTactics.Core.Hex;

/// <summary>
/// Odd-r offset coordinate: <c>Col</c> is the index inside a horizontal row,
/// <c>Row</c> is the row index from the top of the map. Odd rows are shifted
/// right by half a hex width. Used for map storage and display only.
/// </summary>
public readonly record struct OffsetCoord(int Col, int Row)
{
    public override string ToString() => $"({Col},{Row})";
}
