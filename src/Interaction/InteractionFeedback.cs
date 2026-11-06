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

/// <summary>An order draft was opened (from the unit, or continuing its confirmed route).</summary>
public sealed record DraftStarted(UnitId Unit, bool IsEdit) : InteractionFeedback;

public sealed record DraftWaypointAdded(UnitId Unit, int Number, HexCoord Cell) : InteractionFeedback;

/// <summary>RMB on a draft waypoint removed it and all following ones.</summary>
public sealed record DraftTruncated(UnitId Unit, int RemovedFromNumber, int Remaining) : InteractionFeedback;

/// <summary>The whole draft was discarded; nothing was applied and earlier confirmed orders are untouched.</summary>
public sealed record DraftCancelled(UnitId Unit, DraftCancelReason Reason, DraftTargetStatus Status, HexCoord? Cell) : InteractionFeedback;

/// <summary>The final facing was confirmed and the order was applied as a whole (no waypoints = turn in place).</summary>
public sealed record DraftCommitted(UnitId Unit, IndividualOrder Order) : InteractionFeedback;

/// <summary>Applying the draft failed because the game changed meanwhile; the draft stays open, the old route is kept.</summary>
public sealed record DraftCommitFailed(UnitId Unit, OrderApplyResult Result) : InteractionFeedback;

/// <summary>An enemy unit was clicked as a target: reserved <see cref="OrderKind.AttackTarget"/>, not executed yet.</summary>
public sealed record AttackTargetReserved(UnitId Unit, UnitId Target) : InteractionFeedback;

/// <summary>RMB on a confirmed waypoint of the selected unit removed it and the following ones.</summary>
public sealed record ConfirmedWaypointsRemoved(UnitId Unit, Waypoint From, CommandResult Result) : InteractionFeedback;

/// <summary>Continuing a confirmed chain is only possible from its last waypoint.</summary>
public sealed record ChainContinueRefused(UnitId Unit, Waypoint Pressed, Waypoint Last) : InteractionFeedback;

public sealed record ObstacleEditModeChanged(bool Active) : InteractionFeedback;

public sealed record ObstacleToggled(HexCoord Cell, bool Blocked, CommandResult Result) : InteractionFeedback;
