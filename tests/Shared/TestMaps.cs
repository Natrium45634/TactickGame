using System.IO;
using HexTactics.Core.Map;

namespace HexTactics.Tests.Shared;

internal static class TestMaps
{
    /// <summary>
    /// 6 x 4 flat-top map: zone 1 = (0,0),(1,0); base = (0,2),(0,3); blocked = (2,1).
    /// </summary>
    public const string Small = """
        @layout flat-top odd-q
        @name Small
        @size 6 4
        @zone 1 North
        @base Home
        1 1 . . . .
        . . # . . .
        B . . . . .
        B . . . . .
        """;

    public static MapDefinition LoadSmall() => MapParser.Parse(Small);

    public static MapDefinition LoadPrototype() => MapParser.Parse(File.ReadAllText(TestPaths.PrototypeMap));

    /// <summary>An open map without obstacles.</summary>
    public static MapDefinition Empty(int columns = 12, int rows = 10)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("@layout flat-top odd-q");
        sb.AppendLine($"@size {columns} {rows}");
        for (var r = 0; r < rows; r++)
        {
            sb.AppendLine(string.Join(' ', System.Linq.Enumerable.Repeat(".", columns)));
        }

        return MapParser.Parse(sb.ToString());
    }
}
