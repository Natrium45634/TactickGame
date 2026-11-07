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

    /// <summary>Why a dragged roster card went back to the panel (spec part 2 §5.5).</summary>
    public static string RosterReturned(string name, PlacementError error, HexCoord? cell)
    {
        var where = cell is { } c ? $"клетка {Cell(c)}" : "вне поля";
        var reason = error switch
        {
            PlacementError.None => "выставление отменено",
            PlacementError.OutOfBounds => "курсор вне поля",
            PlacementError.NotAllowedHere => $"{where} — не зона выставления (только зелёные клетки)",
            PlacementError.NotWalkable => $"{where} непроходима",
            PlacementError.Occupied => $"{where} занята",
            PlacementError.EntryNotAvailable => "оперативник уже выставлен",
            _ => error.ToString(),
        };
        return $"«{name}» вернулся в ростер: {reason}. Юнит не израсходован.";
    }

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

    /// <summary>
    /// Health shown to the player (spec part 4 §6.4: rounding only for display): one decimal, trailing ",0" dropped —
    /// 77.5 → «77,5», 80 → «80». The simulation keeps the exact value.
    /// </summary>
    public static string Hp(double value) => value.ToString("0.#", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));

    /// <summary>Short name of a status for floating text and the unit panel.</summary>
    public static string StatusName(HexTactics.Core.Effects.StatusKind kind, string stat, string key)
    {
        if (kind == HexTactics.Core.Effects.StatusKind.Periodic)
        {
            return key.EndsWith("|heal", System.StringComparison.Ordinal) ? "лечение/с" : "урон/с";
        }

        var down = key.Contains("|debuff|", System.StringComparison.Ordinal);
        var what = stat switch
        {
            "attackPower" => "атака",
            "attackRate" => "темп атак",
            _ => "скорость",
        };
        return (down ? "↓ " : "↑ ") + what;
    }

    public static string UnitInfo(UnitState u, int? selectedWaypoint = null, bool paused = false, string? name = null)
    {
        var text = $"Юнит {u.Id}{(name is null ? string.Empty : $" «{name}»")} · {SideName(u.Side)}{(u.IsActive ? string.Empty : " · ожидает взгляда (не активен)")}\n" +
                   $"клетка {Cell(u.Cell)} · взгляд: {(u.Facing is { } f ? $"{Direction(f)} ({f.ShortName()})" : "не задан — выберите сектор у ребра")}\n" +
                   $"здоровье {Hp(u.CurrentHealth)}/{u.MaxHealth} · атака {Hp(u.Stats.AttackPower)} · скорость {u.SpeedHexesPerSecond:0.#} гекс/с · ТД: {u.Orders.OrderCount}\n" +
                   $"движение: {Motion(u)}{(paused && u.Motion.Status != MovementStatus.Idle ? " (пауза)" : string.Empty)}";
        if (u.Statuses.Count > 0)
        {
            var parts = new System.Collections.Generic.List<string>();
            foreach (var st in u.Statuses)
            {
                parts.Add($"{StatusName(st.Kind, st.Stat, st.Key)} {st.Remaining:0.0} с");
            }

            text += "\nстатусы: " + string.Join(", ", parts);
        }

        if (u.ActiveSkill is { } running)
        {
            text += $"\nнавык: удары {running.Next}/{running.Hits.Count}";
        }

        if (u.Orders.AttackTarget is { } target)
        {
            text += $"\nатака юнита {target}: " + u.Combat.Status switch
            {
                HexTactics.Core.Combat.AttackStatus.FollowingRoute => "после ТД",
                HexTactics.Core.Combat.AttackStatus.Approaching => "подход на дистанцию удара",
                HexTactics.Core.Combat.AttackStatus.Attacking => $"удары ({u.Combat.HitsDelivered} нанесено)",
                HexTactics.Core.Combat.AttackStatus.TargetUnreachable => "цель недостижима — ждёт",
                _ => "ожидает",
            };
        }
        if (u.Orders.OrderCount > 0)
        {
            var parts = new System.Collections.Generic.List<string>();
            foreach (var wp in u.Orders.Items)
            {
                if (wp.IsDetour)
                {
                    continue;
                }

                var mark = wp.Id == selectedWaypoint ? "▸" : string.Empty;
                var skill = wp.Skill is { Status: HexTactics.Core.Skills.SkillActionStatus.Planned } ? "✦" : string.Empty;
                parts.Add($"{mark}{wp.Sequence}{skill}{Cell(wp.Cell)}");
            }

            text += "\nмаршрут: " + string.Join(" → ", parts);
        }

        return text;
    }

    public static string Motion(UnitState u) => u.Motion.Status switch
    {
        MovementStatus.Moving => "идёт",
        MovementStatus.Waiting => u.Motion.WaitReason switch
        {
            WaitReason.TeamStage => "на месте, ждёт остальных у КТД",
            WaitReason.Teammate => "ждёт, пока свои освободят проход",
            WaitReason.Yielding => "уступил проход своему",
            _ => "ждёт освобождения клетки",
        },
        MovementStatus.Blocked => u.Motion.BlockReason switch
        {
            BlockReason.TargetOccupied => "остановлен — ТД занята",
            BlockReason.BlockedByTeammates => "остановлен — проход закрыт своими",
            _ => "остановлен — нет пути",
        },
        _ => "стоит",
    };

    public static string Blocked(UnitBlocked b, GameState state)
    {
        var where = $"ТД{b.Waypoint.Sequence} {Cell(b.Waypoint.Cell)}";
        var reason = b.Reason switch
        {
            BlockReason.TargetOccupied => $"{where} занята юнитом {state.UnitAt(b.Waypoint.Cell)?.Id}",
            BlockReason.BlockedByTeammates => $"проход к {where} закрыт участниками команды, и им некуда отойти",
            _ => $"нет пути к {where}",
        };
        return $"Юнит {b.Unit} остановлен: {reason}. Приказы сохранены, движение продолжится, когда путь появится.";
    }

    public static string Committed(DraftCommitted c)
    {
        var o = c.Order;
        if (o.AttackTarget is { } target)
        {
            return o.Waypoints.Count == 0
                ? $"Юнит {c.Unit}: атака юнита {target} — взгляд на цель, подход и удары по таймеру."
                : $"Юнит {c.Unit}: {o.Waypoints.Count} ТД, затем атака юнита {target} (взгляд — на цель).";
        }

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

    // ----- command mode (spec part 2 §6)

    public static string GroupCancelled(GroupCancelled c) => c.Reason switch
    {
        DraftCancelReason.InvalidTarget => $"Команда отменена: {GroupStatus(c.Status, c.Cell)}. Прежние приказы не изменены.",
        DraftCancelReason.UnitLost => "Команда отменена: участника больше нет.",
        _ => "Команда отменена. Прежние приказы не изменены.",
    };

    public static string GroupCommitFailed(GroupCommitFailed f) =>
        $"Команда не принята: КТД{f.Result.FailedIndex + 1} — {GroupError(f.Result.Error)}. Черновик открыт.";

    public static string GroupStatus(GroupTargetStatus status, HexCoord? cell)
    {
        var c = cell is { } h ? Cell(h) : string.Empty;
        return status switch
        {
            GroupTargetStatus.OutOfBounds or GroupTargetStatus.NoTarget => "точка вне поля",
            GroupTargetStatus.NotWalkable => $"клетка {c} непроходима",
            GroupTargetStatus.Occupied => $"клетка {c} занята",
            GroupTargetStatus.CaptainUnreachable => $"капитан не может дойти до {c}",
            GroupTargetStatus.NotEnoughSlots => $"вокруг {c} не хватает свободных достижимых клеток для всей команды",
            _ => "недопустимая клетка",
        };
    }

    public static string GroupError(GroupPlanError error) => error switch
    {
        GroupPlanError.CaptainUnreachable => "капитан не может дойти",
        GroupPlanError.NotEnoughSlots => "не хватает мест для команды",
        GroupPlanError.Occupied => "клетка занята",
        GroupPlanError.NotWalkable => "клетка непроходима",
        GroupPlanError.OutOfBounds => "вне поля",
        _ => error.ToString(),
    };

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
