using HexTactics.Core.Hex;
using HexTactics.Core.Map;
using HexTactics.Core.Orders;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;
using HexTactics.Interaction;
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
        HexDirection.North => "север",
        HexDirection.NorthEast => "северо-восток",
        HexDirection.SouthEast => "юго-восток",
        HexDirection.South => "юг",
        HexDirection.SouthWest => "юго-запад",
        HexDirection.NorthWest => "северо-запад",
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

    public static string UnitInfo(UnitState u, int? selectedWaypoint = null, bool paused = false)
    {
        var text = $"Юнит {u.Id} · {SideName(u.Side)}\n" +
                   $"клетка {Cell(u.Cell)} · взгляд: {Direction(u.Facing)} ({u.Facing.ShortName()})\n" +
                   $"скорость {u.SpeedHexesPerSecond:0.#} гекс/с · ТД в очереди: {u.Orders.Count}\n" +
                   $"движение: {Motion(u)}{(paused && u.Motion.Status != MovementStatus.Idle ? " (пауза)" : string.Empty)}";
        if (!u.Orders.IsEmpty)
        {
            var parts = new System.Collections.Generic.List<string>();
            foreach (var wp in u.Orders.Items)
            {
                var mark = wp.Id == selectedWaypoint ? "▸" : string.Empty;
                parts.Add($"{mark}{wp.Sequence}{Cell(wp.Cell)}");
            }

            text += "\nмаршрут: " + string.Join(" → ", parts);
        }

        return text;
    }

    public static string Motion(UnitState u) => u.Motion.Status switch
    {
        MovementStatus.Moving => "идёт",
        MovementStatus.Waiting => "ждёт освобождения клетки",
        MovementStatus.Blocked => u.Motion.BlockReason == BlockReason.TargetOccupied ? "остановлен — ТД занята" : "остановлен — нет пути",
        _ => "стоит",
    };

    public static string Blocked(UnitBlocked b, GameState state)
    {
        var where = $"ТД{b.Waypoint.Sequence} {Cell(b.Waypoint.Cell)}";
        var reason = b.Reason == BlockReason.TargetOccupied
            ? $"{where} занята юнитом {state.UnitAt(b.Waypoint.Cell)?.Id}"
            : $"нет пути к {where}";
        return $"Юнит {b.Unit} остановлен: {reason}. Приказы сохранены, движение продолжится, когда путь появится.";
    }

    public static string Committed(DraftCommitted c)
    {
        var o = c.Order;
        if (o.IsTurnInPlace)
        {
            return $"Юнит {c.Unit}: поворот на месте — {Direction(o.FinalFacing)} ({o.FinalFacing.ShortName()}).";
        }

        var last = o.Waypoints[^1];
        return $"Юнит {c.Unit}: приказ принят — {o.Waypoints.Count} ТД до {Cell(last)}, затем взгляд {Direction(o.FinalFacing)}.";
    }

    public static string Cancelled(DraftCancelled c) => c.Reason switch
    {
        DraftCancelReason.InvalidTarget => $"Черновик отменён: {Status(c.Status, c.Cell)}. Прежние приказы не изменены.",
        DraftCancelReason.UnitLost => "Черновик отменён: юнита больше нет.",
        _ => "Черновик приказа отменён. Прежние приказы не изменены.",
    };

    public static string CommitFailed(DraftCommitFailed f) =>
        $"Приказ не принят: ТД{f.Result.FailedIndex + 1} — {Error(f.Result.Error, null)}. Прежний маршрут сохранён, черновик открыт.";

    public static string Status(DraftTargetStatus status, HexCoord? cell)
    {
        var c = cell is { } h ? Cell(h) : string.Empty;
        return status switch
        {
            DraftTargetStatus.OutOfBounds or DraftTargetStatus.NoTarget => "точка вне поля",
            DraftTargetStatus.NotWalkable => $"клетка {c} непроходима",
            DraftTargetStatus.Occupied => $"клетка {c} занята",
            DraftTargetStatus.Unreachable => $"до клетки {c} нет пути",
            _ => "недопустимая клетка",
        };
    }

    private static string Error(WaypointError error, HexCoord? cell) => error switch
    {
        WaypointError.OutOfBounds => "точка вне поля",
        WaypointError.NotWalkable => $"клетка {(cell is { } c ? Cell(c) : string.Empty)} непроходима",
        WaypointError.Occupied => "клетка занята",
        WaypointError.Unreachable => "нет пути",
        WaypointError.SameAsSource => "совпадает с исходной клеткой",
        _ => error.ToString(),
    };
}
