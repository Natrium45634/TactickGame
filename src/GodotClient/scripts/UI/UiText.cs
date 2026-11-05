using HexTactics.Core.Hex;
using HexTactics.Core.Map;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;
using HexTactics.Simulation;

namespace HexTactics.Client.UI;

/// <summary>Player-facing Russian texts for model results. Keeps wording out of the model.</summary>
public static class UiText
{
    public static string Cell(HexCoord c)
    {
        var o = c.ToOffset();
        return $"({o.Col},{o.Row})";
    }

    public static string Direction(HexDirection d) => d switch
    {
        HexDirection.East => "восток",
        HexDirection.NorthEast => "северо-восток",
        HexDirection.NorthWest => "северо-запад",
        HexDirection.West => "запад",
        HexDirection.SouthWest => "юго-запад",
        HexDirection.SouthEast => "юго-восток",
        _ => d.ToString(),
    };

    public static string SideName(Side side) => side == Side.Player ? "игрок" : "противник";

    public static string Method(SpawnRequest request, MapDefinition map) => request.Method switch
    {
        SpawnMethod.PlayerBase => map.PlayerBase?.Name ?? "база игрока",
        SpawnMethod.SpawnZone => $"зона «{map.FindZone(request.ZoneId)?.Name ?? request.ZoneId.ToString()}»",
        _ => "прямое выставление",
    };

    public static string Spawn(SpawnRequest request, SpawnResult result, MapDefinition map)
    {
        if (result.Success)
        {
            return $"Юнит {result.Unit} появился: {Method(request, map)}, клетка {Cell(result.Cell!.Value)}.";
        }

        var cell = result.Cell is { } c ? Cell(c) : "?";
        return result.Error switch
        {
            SpawnError.OutOfBounds => $"Клетка {cell} вне поля — размещение отклонено.",
            SpawnError.NotWalkable => $"Клетка {cell} непроходима — размещение отклонено.",
            SpawnError.Occupied => $"Клетка {cell} занята юнитом {result.Occupant} — размещение отклонено, никто не перемещён.",
            SpawnError.NoPlayerBase => "На карте нет базы игрока.",
            SpawnError.BaseFull => "Все клетки базы заняты — появление отклонено, никто не перемещён.",
            SpawnError.UnknownZone => $"Зона появления {request.ZoneId} не найдена.",
            SpawnError.ZoneFull => $"{Method(request, map)}: все клетки заняты — появление отклонено.",
            SpawnError.CellNotInZone => $"Клетка {cell} не принадлежит выбранной зоне.",
            SpawnError.MissingCell => "Курсор вне поля — выберите клетку.",
            _ => $"Появление отклонено: {result.Error}.",
        };
    }

    public static string UnitInfo(UnitState u)
    {
        return $"Юнит {u.Id} · {SideName(u.Side)}\n" +
               $"клетка {Cell(u.Cell)} · взгляд: {Direction(u.Facing)} ({u.Facing.ShortName()})\n" +
               $"скорость {u.SpeedHexesPerSecond:0.#} гекс/с · ТД в очереди: {u.Orders.Count}";
    }
}
