using System.Collections.Generic;
using HexTactics.Core.Effects;
using HexTactics.Core.Hex;
using HexTactics.Core.Units;

namespace HexTactics.Simulation;

/// <summary>
/// Effects of tile types (spec part 4 §7): the truth is the unit's <b>logical</b> hex (<see cref="UnitState.Cell"/>, which
/// switches when the body crosses the edge, part 3 §2), never the drawn mesh. Every tick each living unit belongs to
/// exactly one hex, so a smoothed path that cuts a corner cannot count a tile twice.
/// <list type="bullet">
/// <item>enter — once on the tick the logical hex becomes the tile (also when the unit appears on it);</item>
/// <item>exit — once on the tick the logical hex changes away from it (not on death);</item>
/// <item>stay — every tick on the tile for the tick's game time: «5 HP/s» for 2 s = exactly 10 HP; no ticks while paused.</item>
/// </list>
/// Runs after attacks, before statuses; effects are executed by the shared <see cref="EffectResolver"/> only.
/// </summary>
internal sealed class TileEffectSystem : ISimulationSystem
{
    private readonly GameState _state;
    private readonly SimulationSettings _settings;
    private readonly EffectResolver _resolver;
    private readonly Dictionary<UnitId, HexCoord> _cells = new();

    public TileEffectSystem(GameState state, SimulationSettings settings, EffectResolver resolver)
    {
        _state = state;
        _settings = settings;
        _resolver = resolver;
    }

    public string Name => "Tiles";

    public void Tick(SimulationContext context)
    {
        if (_settings.TileEffects.Count == 0)
        {
            return;
        }

        var seen = new HashSet<UnitId>();
        foreach (var unit in new List<UnitState>(_state.Units))
        {
            if (!unit.IsAlive)
            {
                continue;
            }

            seen.Add(unit.Id);
            var cell = unit.Cell;
            var known = _cells.TryGetValue(unit.Id, out var previous);
            if (!known || previous != cell)
            {
                _cells[unit.Id] = cell;
                if (known)
                {
                    Fire(previous, TileTrigger.Exit, unit, context);
                }

                Fire(cell, TileTrigger.Enter, unit, context);
            }

            Fire(cell, TileTrigger.Stay, unit, context);
        }

        if (seen.Count != _cells.Count)
        {
            foreach (var id in new List<UnitId>(_cells.Keys))
            {
                if (!seen.Contains(id))
                {
                    _cells.Remove(id); // dead or removed: no exit trigger
                }
            }
        }
    }

    private void Fire(HexCoord cell, TileTrigger trigger, UnitState unit, SimulationContext context)
    {
        if (!_state.Grid.Contains(cell) || _state.Grid[cell].TileId is not { } tileId || !_settings.TileEffects.TryGetValue(tileId, out var effects))
        {
            return;
        }

        foreach (var effect in effects)
        {
            if (effect.Trigger != trigger || !unit.IsAlive)
            {
                continue;
            }

            _resolver.Apply(new EffectInvocation(effect.Effect, null, unit, context.Tick, EffectSource.Tile, tileId)
            {
                Slice = trigger == TileTrigger.Stay ? context.DeltaSeconds : 0,
            });
        }
    }
}
