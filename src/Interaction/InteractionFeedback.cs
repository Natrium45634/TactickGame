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

public sealed record RosterDragStarted(int RosterEntry) : InteractionFeedback;

/// <summary>The card was dropped on a forbidden hex / outside the map or the drag was cancelled: nothing was created.</summary>
public sealed record RosterCardReturned(int RosterEntry, PlacementError Error, HexCoord? Cell) : InteractionFeedback;

/// <summary>The unit appeared on the map; a draft (arrow) was opened for it immediately.</summary>
public sealed record RosterUnitPlaced(int RosterEntry, UnitId Unit, HexCoord Cell) : InteractionFeedback;

/// <summary>The command mode ("Команда" button) was switched on or off.</summary>
public sealed record GroupModeChanged(bool Active) : InteractionFeedback;

/// <summary>A unit joined the team being collected (the first one is the captain).</summary>
public sealed record GroupMemberAdded(UnitId Unit, bool IsCaptain, int Count) : InteractionFeedback;

/// <summary>A click on a unit did not change the team (already a member, team full, or not an ally).</summary>
public sealed record GroupMemberRefused(UnitId Unit, GroupTargetStatus Reason) : InteractionFeedback;

/// <summary>A click on the ground before any member was chosen: the captain has to be clicked first.</summary>
public sealed record GroupNeedsCaptain : InteractionFeedback;

public sealed record GroupWaypointAdded(int Number, HexCoord Cell, GroupStagePlan Stage) : InteractionFeedback;

/// <summary>RMB on a draft КТД removed it and all following ones (with no КТД left the team is being collected again).</summary>
public sealed record GroupDraftTruncated(int RemovedFromNumber, int Remaining) : InteractionFeedback;

/// <summary>The command draft was discarded (RMB, Esc, an invalid КТД click, a lost member); nothing was applied.</summary>
public sealed record GroupCancelled(DraftCancelReason Reason, GroupTargetStatus Status, HexCoord? Cell) : InteractionFeedback;

/// <summary>The captain's facing was confirmed and the command order was applied to all members.</summary>
public sealed record GroupCommitted(GroupOrder Order, int GroupId) : InteractionFeedback;

/// <summary>Applying the command order failed because the game changed meanwhile; the draft stays open.</summary>
public sealed record GroupCommitFailed(GroupOrderResult Result) : InteractionFeedback;
