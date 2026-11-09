using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Units;

namespace HexTactics.Simulation;

/// <summary>
/// Block (spec part 7 §7.2): a standing unit with free block slots holds hostile units that want to step on from a
/// neighbouring hex — on their own hex (two units never share one), without a hidden extra slot per route. The contact is
/// decided when the moving unit would start its next step (<see cref="HoldsBack"/>); at the start of every tick links that
/// are no longer valid are released (blocker gone, moved or given a move order, enemy gone or stopped, not adjacent, sides,
/// capacity lowered — the newest links first). One blocker per enemy; a block never deals damage on its own.
/// </summary>
internal sealed class BlockSystem : ISimulationSystem
{
    private readonly GameState _state;
    private readonly List<SimulationEvent> _events;

    public BlockSystem(GameState state, List<SimulationEvent> events)
    {
        _state = state;
        _events = events;
    }

    public string Name => "Blocks";

    public void Tick(SimulationContext context) => Release(context.Tick);

    /// <summary>
    /// Whether <paramref name="unit"/> must stay on its hex instead of starting its next step: it already has a valid blocker,
    /// or a standing hostile unit next to it has a free slot (the lowest id first — the same choice every run).
    /// </summary>
    public bool HoldsBack(UnitState unit, long tick)
    {
        if (_state.BlockOf(unit.Id) is { } held)
        {
            if (Reason(held) is not { } reason)
            {
                return true;
            }

            End(held, reason, tick);
        }

        if (unit.Orders.OrderCount == 0)
        {
            return false;
        }

        foreach (var blocker in _state.Units)
        {
            if (CanHold(blocker, unit))
            {
                var link = new BlockLink(blocker.Id, unit.Id, tick);
                _state.AddBlock(link);
                _events.Add(new BlockStarted(tick, blocker.Id, unit.Id));
                return true;
            }
        }

        return false;
    }

    private bool CanHold(UnitState blocker, UnitState enemy) =>
        blocker.Id != enemy.Id && blocker.IsAlive && blocker.IsActive && blocker.Side != enemy.Side && Standing(blocker)
        && blocker.Cell.DistanceTo(enemy.Cell) == 1 && _state.BlockCount(blocker.Id) < BlockMath.Capacity(blocker);

    /// <summary>A blocker holds only while it stands: no step under way and no movement order (spec part 7 §7.2).</summary>
    private static bool Standing(UnitState unit) => !unit.Motion.IsStepping && unit.Orders.OrderCount == 0;

    private void Release(long tick)
    {
        foreach (var link in _state.BlockLinks.ToList())
        {
            if (Reason(link) is { } reason)
            {
                End(link, reason, tick);
            }
        }

        // Fewer slots than links (a debuff): release the newest links first (spec part 7 §7.2, M7-40).
        foreach (var group in _state.BlockLinks.GroupBy(l => l.Blocker).ToList())
        {
            var blocker = _state.FindUnit(group.Key);
            var capacity = blocker is null ? 0 : BlockMath.Capacity(blocker);
            foreach (var extra in group.OrderByDescending(l => l.CreatedAtTick).ThenByDescending(l => l.Enemy.Value).Take(group.Count() - capacity).ToList())
            {
                End(extra, BlockReleaseReason.CapacityReduced, tick);
            }
        }
    }

    private BlockReleaseReason? Reason(BlockLink link)
    {
        var blocker = _state.FindUnit(link.Blocker);
        var enemy = _state.FindUnit(link.Enemy);
        if (blocker is null || !blocker.IsAlive || !blocker.IsActive)
        {
            return BlockReleaseReason.BlockerGone;
        }

        if (enemy is null || !enemy.IsAlive || !enemy.IsActive)
        {
            return BlockReleaseReason.EnemyGone;
        }

        if (blocker.Side == enemy.Side)
        {
            return BlockReleaseReason.SideChanged;
        }

        if (!Standing(blocker))
        {
            return BlockReleaseReason.BlockerMoved;
        }

        if (blocker.Cell.DistanceTo(enemy.Cell) != 1)
        {
            return BlockReleaseReason.NoLongerAdjacent;
        }

        return enemy.Orders.OrderCount == 0 ? BlockReleaseReason.EnemyStopped : null;
    }

    private void End(BlockLink link, BlockReleaseReason reason, long tick)
    {
        if (_state.RemoveBlock(link))
        {
            _events.Add(new BlockReleased(tick, link.Blocker, link.Enemy, reason));
        }
    }
}
