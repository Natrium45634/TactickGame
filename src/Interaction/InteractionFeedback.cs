using HexTactics.Core.Hex;
using HexTactics.Core.Orders;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;
using HexTactics.Simulation;

namespace HexTactics.Interaction;

/// <summary>Structured feedback for the UI (texts are produced by the client).</summary>
public abstract record InteractionFeedback;

public sealed record SelectionChanged(UnitId? Unit) : InteractionFeedback;

public sealed record PlacementModeChanged(bool Active) : InteractionFeedback;

public sealed record SpawnAttempted(SpawnRequest Request, SpawnResult Result) : InteractionFeedback;

public sealed record WaypointSelected(UnitId Unit, Waypoint Waypoint) : InteractionFeedback;

public sealed record DragStarted(UnitId Unit, OrderMode Mode, HexCoord Source) : InteractionFeedback;

/// <summary>A waypoint was created (TД appended or the queue replaced).</summary>
public sealed record OrderCommitted(UnitId Unit, OrderMode Mode, OrderResult Result) : InteractionFeedback;

/// <summary>The drag was released on a hex where no waypoint can be created.</summary>
public sealed record OrderNotCreated(UnitId Unit, DragTargetStatus Status, HexCoord? Target) : InteractionFeedback;

public sealed record RotatedInPlace(UnitId Unit, HexDirection Facing) : InteractionFeedback;

/// <summary>The drag ended without any change (Esc/RMB, or released on the source hex without a gesture).</summary>
public sealed record DragCancelled(UnitId Unit, bool ByUser) : InteractionFeedback;

/// <summary>Chains can only be continued from the last waypoint.</summary>
public sealed record ChainContinueRefused(UnitId Unit, Waypoint Pressed, Waypoint Last) : InteractionFeedback;

public sealed record ObstacleEditModeChanged(bool Active) : InteractionFeedback;

public sealed record ObstacleToggled(HexCoord Cell, bool Blocked, CommandResult Result) : InteractionFeedback;
