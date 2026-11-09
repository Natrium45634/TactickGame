using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Map;
using HexTactics.Core.Roster;
using HexTactics.Core.Scenarios;
using HexTactics.Core.Units;

namespace HexTactics.Simulation;

/// <summary>
/// Goals of a scenario (spec part 7 §3.2, §4.6, §11.1 p.8–9): the last system of the tick settles leaks into the player bases and
/// then checks victory and defeat once, from the confirmed state (deaths of this tick included). The first running tick turns
/// «Preparing» into «Running»; the scenario clock counts only running game time (never the pause).
/// <list type="bullet">
/// <item>Ordinary battle: victory before defeat — when the last enemy and the last unit of the player fall in the same tick the
/// player wins.</item>
/// <item>Tower Defense: an enemy whose route ends on its player base leaks once — when it reached the last visit, or stands before
/// it while a unit holds that base hex (the last border is enough); it leaves the map and only that base loses a life. Defeat by
/// the base lives is checked before victory (all waves resolved, no enemy left): bases must survive.</item>
/// </list>
/// Without scenario rules (free play, tests) it does nothing.
/// </summary>
internal sealed class ObjectiveSystem : ISimulationSystem
{
    private readonly GameState _state;
    private readonly List<SimulationEvent> _events;
    private readonly ScenarioRules? _rules;
    private readonly WaveSystem? _waves;
    private readonly SortedDictionary<int, int> _lives = new();
    private int _sharedLives;

    public ObjectiveSystem(GameState state, List<SimulationEvent> events, ScenarioRules? rules, WaveSystem? waves = null)
    {
        _state = state;
        _events = events;
        _rules = rules;
        _waves = waves;
        foreach (var b in rules?.Bases ?? System.Array.Empty<BaseLives>())
        {
            _lives[b.Base] = b.Lives;
            _sharedLives += b.Lives;
        }
    }

    public string Name => "Objectives";

    public ScenarioPhase Phase { get; private set; } = ScenarioPhase.Preparing;

    public ScenarioEndReason? Reason { get; private set; }

    /// <summary>Game seconds of the running phase.</summary>
    public double Elapsed { get; private set; }

    /// <summary>Enemies of the scenario still alive (the goal «победить всех врагов»).</summary>
    public int EnemiesLeft => _state.Units.Count(u => u.Side == Side.Enemy && u.IsAlive);

    /// <summary>Lives left per player base number (Tower Defense).</summary>
    public IReadOnlyDictionary<int, int> BaseLives => _lives;

    /// <summary>The shared pool of lives (meaningful with <see cref="DefeatPolicy.SharedLives"/>).</summary>
    public int SharedLives => _sharedLives;

    public void Tick(SimulationContext context)
    {
        if (_rules is null || Phase is ScenarioPhase.Victory or ScenarioPhase.Defeat)
        {
            return;
        }

        Phase = ScenarioPhase.Running;
        Elapsed += context.DeltaSeconds;
        if (_rules.TowerDefense)
        {
            Leaks(context.Tick);
            if (BaseLost())
            {
                End(ScenarioPhase.Defeat, ScenarioEndReason.BaseLost, context.Tick);
            }
            else if ((_waves?.AllResolved ?? true) && EnemiesLeft == 0)
            {
                End(ScenarioPhase.Victory, ScenarioEndReason.AllWavesCleared, context.Tick);
            }
            else if (_rules.TimeLimitSeconds > 0 && Elapsed + 1e-9 >= _rules.TimeLimitSeconds)
            {
                End(ScenarioPhase.Defeat, ScenarioEndReason.TimeUp, context.Tick);
            }

            return;
        }

        if (_rules.EliminateAllEnemies && EnemiesLeft == 0)
        {
            End(ScenarioPhase.Victory, ScenarioEndReason.AllEnemiesDefeated, context.Tick);
        }
        else if (_rules.DefeatWhenSquadLost && SquadLost())
        {
            End(ScenarioPhase.Defeat, ScenarioEndReason.SquadLost, context.Tick);
        }
        else if (_rules.TimeLimitSeconds > 0 && Elapsed + 1e-9 >= _rules.TimeLimitSeconds)
        {
            End(ScenarioPhase.Defeat, ScenarioEndReason.TimeUp, context.Tick);
        }
    }

    /// <summary>Enemies at the end of their routes leak into their own base, in id order, each once.</summary>
    private void Leaks(long tick)
    {
        foreach (var unit in _state.Units.Where(u => u.IsAlive && u.Route is not null).ToList())
        {
            var route = unit.Route!;
            var cells = route.Route.Cells;
            var last = cells[^1];
            if (_state.Map.BaseAt(last) is not { Side: BaseSide.Player } target || target.Number != route.Route.TargetBase)
            {
                continue; // a route that does not end on its player base (debug spawns, broken data) never leaks
            }

            var arrived = route.Finished && unit.Cell == last && !unit.Motion.IsStepping;
            var atBorder = route.Index == cells.Count - 2 && unit.Cell == cells[^2] && !unit.Motion.IsStepping
                && _state.UnitAt(last) is { } holder && holder.Id != unit.Id;
            if (!arrived && !atBorder)
            {
                continue;
            }

            _state.RemoveFromMap(unit.Id);
            var left = 0;
            if (_lives.TryGetValue(target.Number, out var lives))
            {
                left = System.Math.Max(0, lives - 1);
                _lives[target.Number] = left;
            }

            _sharedLives = System.Math.Max(0, _sharedLives - 1);
            _events.Add(new EnemyLeaked(tick, unit.Id, route.Route.Id, target.Number, _rules!.Defeat == DefeatPolicy.SharedLives ? _sharedLives : left));
        }
    }

    private bool BaseLost()
    {
        if (_rules!.Bases.Count == 0)
        {
            return false;
        }

        return _rules.Defeat switch
        {
            DefeatPolicy.AllBases => _lives.Values.All(l => l <= 0),
            DefeatPolicy.SharedLives => _sharedLives <= 0,
            _ => _rules.Bases.Any(b => b.Critical && _lives.TryGetValue(b.Base, out var l) && l <= 0),
        };
    }

    /// <summary>No living unit of the player on the map and no roster card that can still be deployed (spec part 7 §3.2).</summary>
    private bool SquadLost() =>
        !_state.Units.Any(u => u.Side == Side.Player && u.IsAlive)
        && !_state.Roster.Entries.Any(e => e.State is RosterEntryState.AvailableInRoster or RosterEntryState.PlacementPreview); // placed ones are units

    private void End(ScenarioPhase phase, ScenarioEndReason reason, long tick)
    {
        Phase = phase;
        Reason = reason;
        _events.Add(new ScenarioEnded(tick, phase, reason));
    }
}
