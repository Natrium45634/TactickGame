using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Roster;
using HexTactics.Core.Scenarios;
using HexTactics.Core.Units;

namespace HexTactics.Simulation;

/// <summary>
/// Goals of a scenario (spec part 7 §3.2, §11.1 p.9): the last system of the tick checks victory and defeat once, from the
/// confirmed state (deaths of this tick included). The first running tick turns «Preparing» into «Running»; the scenario clock
/// counts only running game time (never the pause). Victory is checked before defeat: when the last enemy and the last unit of
/// the player fall in the same tick the player wins. Without scenario rules (free play, tests) it does nothing.
/// </summary>
internal sealed class ObjectiveSystem : ISimulationSystem
{
    private readonly GameState _state;
    private readonly List<SimulationEvent> _events;
    private readonly ScenarioRules? _rules;

    public ObjectiveSystem(GameState state, List<SimulationEvent> events, ScenarioRules? rules)
    {
        _state = state;
        _events = events;
        _rules = rules;
    }

    public string Name => "Objectives";

    public ScenarioPhase Phase { get; private set; } = ScenarioPhase.Preparing;

    public ScenarioEndReason? Reason { get; private set; }

    /// <summary>Game seconds of the running phase.</summary>
    public double Elapsed { get; private set; }

    /// <summary>Enemies of the scenario still alive (the goal «победить всех врагов»).</summary>
    public int EnemiesLeft => _state.Units.Count(u => u.Side == Side.Enemy && u.IsAlive);

    public void Tick(SimulationContext context)
    {
        if (_rules is null || Phase is ScenarioPhase.Victory or ScenarioPhase.Defeat)
        {
            return;
        }

        Phase = ScenarioPhase.Running;
        Elapsed += context.DeltaSeconds;
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
