namespace HexTactics.Core.Scenarios;

/// <summary>
/// The simple enemy AI presets of spec part 7 §5.2 — and only these (no behaviour trees, no threat maps, no strategy).
/// </summary>
public enum AiPreset
{
    /// <summary>No AI: the unit stands (or walks its route) and hits with its automatic base attack.</summary>
    None = 0,

    /// <summary>Destroy the player's units: pick a reachable one, come into attack range, attack; re-evaluate when it is lost.</summary>
    AttackUnits = 1,

    /// <summary>Reach the defended object and attack it; a blocker on the way may distract it.</summary>
    AttackObjective = 2,

    /// <summary>Units first; the object only when no unit can be reached.</summary>
    UnitsThenObjective = 3,

    /// <summary>The object first; units when there is no object or no way to it.</summary>
    ObjectiveThenUnits = 4,

    /// <summary>Guard a point: attack only inside the radius around it, then come back (spec part 7 §3.3).</summary>
    Guard = 5,
}

/// <summary>How the AI ranks equally allowed targets.</summary>
public enum AiPriority
{
    Nearest = 0,
    LowestHealth = 1,
}

/// <summary>
/// An AI profile (spec part 7 §5.2): the preset and its few editable numbers. Defaults work without tuning (PROTOTYPE_DEFAULT).
/// </summary>
public sealed record AiProfile(
    AiPreset Preset,
    int DetectRange = 0,
    AiPriority Priority = AiPriority.Nearest,
    double ReevaluateSeconds = 1,
    int GuardRadius = 3,
    bool SwitchToBlocker = true)
{
    /// <summary>0 — every target on the map can be noticed.</summary>
    public bool Sees(int distance) => DetectRange <= 0 || distance <= DetectRange;
}

/// <summary>A defended object of a mission (spec part 7 §5.1 <c>DefenseObjective</c>): a hex, its health and whether its loss is a defeat.</summary>
public sealed record MissionObject(string Id, string Name, Hex.HexCoord Cell, double Health, bool Critical = true);

/// <summary>When the defended objects end the battle.</summary>
public enum ObjectsPolicy
{
    /// <summary>Every critical object must survive: the loss of one is the defeat.</summary>
    AllMustSurvive = 0,

    /// <summary>At least one critical object must survive: the loss of the last one is the defeat.</summary>
    AnyMustSurvive = 1,
}
