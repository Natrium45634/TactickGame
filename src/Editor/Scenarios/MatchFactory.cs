using System.Linq;
using HexTactics.Content.Runtime;
using HexTactics.Core.Roster;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;
using HexTactics.Simulation;

namespace HexTactics.Editor.Scenarios;

/// <summary>
/// The start of a match from built content (spec part 7 §2.2 <c>GameSession</c>): the simulation settings, the roster of the
/// squad, the units already on the map and the preparation pause of a scenario. The play mode, «▶ Тест» of the editors and the
/// tests start a match the same way, so a scenario plays as it was checked.
/// </summary>
public static class MatchFactory
{
    public static SimulationSettings Settings(GameContent content) => new()
    {
        DefaultStats = content.DefaultUnit.Stats,
        Skills = content.Skills,
        DefaultSkillSet = content.DefaultUnit.Skills,
        DefaultUnitId = content.DefaultUnit.Id,
        TileEffects = content.TileEffects,
        DamageTypes = content.DamageTypes,
        CombatRules = content.CombatRules,
        AutoAttacks = content.AutoAttacks,
        Blocking = content.Blocking,
        Scenario = content.Scenario?.Rules,
    };

    public static GameSimulation Start(GameContent content)
    {
        var sim = new GameSimulation(
            content.Map,
            Settings(content),
            content.Squad.Count > 0 ? new RosterState(content.Squad.Select(s => (s.Name, (UnitProfile?)s.Profile))) : RosterState.Prototype());
        if (content.Scenario is { } scenario)
        {
            // Spec part 7 §3.1: the enemies of the scenario are on the map before the start; «Подготовка» is the first pause —
            // the player deploys and plans, nothing advances until «Начать бой».
            foreach (var placed in scenario.Placed)
            {
                if (sim.Spawn(SpawnRequest.AtCell(placed.Cell, placed.Side, placed.Facing) with { Profile = placed.Profile }).Unit is { } id)
                {
                    sim.State.SetAi(id, placed.Ai); // spec part 7 §3.3, §5.2: «Охранник» and the other presets
                }
            }

            sim.SetPaused(scenario.StartPaused);
        }

        return sim;
    }
}
