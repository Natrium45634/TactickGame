using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Combat;
using HexTactics.Core.Units;

namespace HexTactics.Simulation;

/// <summary>
/// «Per matching ally» bonuses (spec part 6 §5.5 <c>CountMatchingUnits</c>, D-159): the owner's characteristic gets
/// <c>count × percent</c> points for living allies of its side matching the species filter, of its pack (<c>packId</c>), within
/// the radius, up to the limit. Recounted only when the map's roster changes (a unit appears or dies) — or, for a radius,
/// when units move between hexes — never kept after a death and never written to the library definition.
/// </summary>
internal sealed class PackSystem : ISimulationSystem
{
    private readonly GameState _state;
    private long _roster = -1;
    private long _occupancy = -1;

    public PackSystem(GameState state)
    {
        _state = state;
    }

    public string Name => "Packs";

    public void Tick(SimulationContext context) => Refresh(force: false);

    /// <summary>Recounts if something relevant changed (or always with <paramref name="force"/>).</summary>
    public void Refresh(bool force)
    {
        var owners = _state.Units.Where(u => u.IsAlive && u.BaseStats.CountModifiers.Count > 0).ToList();
        var radius = owners.Any(o => o.BaseStats.CountModifiers.Any(m => m.Radius > 0));
        if (!force && _state.RosterVersion == _roster && (!radius || _state.OccupancyVersion == _occupancy))
        {
            return;
        }

        _roster = _state.RosterVersion;
        _occupancy = _state.OccupancyVersion;
        foreach (var owner in owners.OrderBy(o => o.Id.Value))
        {
            var percent = new SortedDictionary<string, double>(StringComparer.Ordinal);
            foreach (var m in owner.BaseStats.CountModifiers)
            {
                percent[m.Stat] = percent.GetValueOrDefault(m.Stat) + (Count(owner, m) * m.PercentPerUnit);
            }

            if (!SameValues(owner.PackPercent, percent))
            {
                _state.SetPackPercent(owner.Id, percent);
            }
        }
    }

    /// <summary>How many living allies match the modifier (spec part 6 §5.5: other wolves of the same pack by default).</summary>
    public int Count(UnitState owner, CountModifier m)
    {
        var count = 0;
        foreach (var u in _state.Units)
        {
            if (!u.IsAlive || u.Side != owner.Side || (u.Id == owner.Id && !m.IncludeSelf))
            {
                continue;
            }

            if (m.SpeciesId is { } species && !u.BaseStats.IsA(species))
            {
                continue;
            }

            if (m.SamePack && (owner.BaseStats.PackId.Length == 0 || u.BaseStats.PackId != owner.BaseStats.PackId))
            {
                continue;
            }

            if (m.Radius > 0 && u.Cell.DistanceTo(owner.Cell) > m.Radius)
            {
                continue;
            }

            count++;
        }

        return m.MaxCount > 0 ? Math.Min(count, m.MaxCount) : count;
    }

    private static bool SameValues(IReadOnlyDictionary<string, double> a, IReadOnlyDictionary<string, double> b) =>
        a.Count == b.Count && a.All(x => b.TryGetValue(x.Key, out var v) && Math.Abs(v - x.Value) < 1e-12);
}
