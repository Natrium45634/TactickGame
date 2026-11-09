using System.Collections.Generic;
using HexTactics.Core.Hex;
using HexTactics.Core.Scenarios;

namespace HexTactics.Core.Units;

/// <summary>What a unit is on the field: a fighter, or a defended mission object that never moves, attacks or takes orders.</summary>
public enum UnitRole
{
    Combatant = 0,
    Objective = 1,
}

/// <summary>The minimal AI state machine of spec part 7 §5.2: choose a target, come into range, attack, re-evaluate, return.</summary>
public enum AiPhase
{
    ChooseTarget = 0,
    Engage = 1,
    Return = 2,
    Idle = 3,
}

/// <summary>
/// The AI of one unit (spec part 7 §5.2): its profile, home hex (for «Охранник»), current target and when it thinks next.
/// Targets found unreachable are skipped for a while, so the unit takes a fallback instead of insisting.
/// </summary>
public sealed class AiState
{
    public AiState(AiProfile profile, HexCoord home)
    {
        Profile = profile;
        Home = home;
    }

    public AiProfile Profile { get; }

    public HexCoord Home { get; }

    public AiPhase Phase { get; internal set; } = AiPhase.ChooseTarget;

    public UnitId? Target { get; internal set; }

    public long NextThinkTick { get; internal set; }

    /// <summary>Targets the unit could not reach, and until which tick they are skipped.</summary>
    public Dictionary<UnitId, long> Unreachable { get; } = new();
}
