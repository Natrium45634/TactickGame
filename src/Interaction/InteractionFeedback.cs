using HexTactics.Core.Spawning;
using HexTactics.Core.Units;
using HexTactics.Simulation;

namespace HexTactics.Interaction;

/// <summary>Structured feedback for the UI (texts are produced by the client).</summary>
public abstract record InteractionFeedback;

public sealed record SelectionChanged(UnitId? Unit) : InteractionFeedback;

public sealed record PlacementModeChanged(bool Active) : InteractionFeedback;

public sealed record SpawnAttempted(SpawnRequest Request, SpawnResult Result) : InteractionFeedback;
