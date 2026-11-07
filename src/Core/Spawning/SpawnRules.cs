using System;
using System.Collections.Generic;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;
using HexTactics.Core.Units;

namespace HexTactics.Core.Spawning;

/// <summary>The three independent ways a unit can appear on the map (spec §4).</summary>
public enum SpawnMethod
{
    /// <summary>On a cell of the player base defined by the map data.</summary>
    PlayerBase = 0,

    /// <summary>In a spawn zone on the outer edge of the map.</summary>
    SpawnZone = 1,

    /// <summary>On a hex chosen directly by the player in placement mode.</summary>
    DirectPlacement = 2,
}

public enum SpawnError
{
    None = 0,
    OutOfBounds,
    NotWalkable,
    Occupied,
    NoPlayerBase,
    BaseFull,
    UnknownZone,
    ZoneFull,
    CellNotInZone,
    MissingCell,
}

/// <summary>
/// A request to create a unit. For base/zone requests <see cref="Cell"/> is optional: without it
/// the first free cell of the zone (in its fixed row-major order) is used; with it, that exact
/// cell must belong to the zone. Direct placement always needs <see cref="Cell"/>.
/// </summary>
public sealed record SpawnRequest(SpawnMethod Method, Side Side, int ZoneId = 0, HexCoord? Cell = null, HexDirection? Facing = null)
{
    /// <summary>The unit definition to create (null = the simulation's default unit).</summary>
    public HexTactics.Core.Units.UnitProfile? Profile { get; init; }

    public static SpawnRequest AtBase(Side side = Side.Player, HexCoord? cell = null) => new(SpawnMethod.PlayerBase, side, Cell: cell);

    public static SpawnRequest AtZone(int zoneId, Side side = Side.Player, HexCoord? cell = null) => new(SpawnMethod.SpawnZone, side, zoneId, cell);

    public static SpawnRequest AtCell(HexCoord cell, Side side = Side.Player, HexDirection? facing = null) => new(SpawnMethod.DirectPlacement, side, Cell: cell, Facing: facing);
}

/// <summary>Outcome of resolving a request: the chosen cell, or the reason it is impossible.</summary>
public readonly record struct SpawnResolution(HexCoord? Cell, SpawnError Error)
{
    public bool IsValid => Error == SpawnError.None && Cell is not null;
}

/// <summary>
/// Pure placement rules shared by all three spawn methods: the target must be inside the map,
/// walkable and free. Rules never move or remove existing units.
/// </summary>
public static class SpawnRules
{
    private static readonly HexLayout UnitLayout = new(1.0);

    /// <summary>Checks the common conditions for any spawn cell.</summary>
    public static SpawnError ValidateCell(GameState state, HexCoord cell)
    {
        if (!state.Grid.Contains(cell))
        {
            return SpawnError.OutOfBounds;
        }

        if (!state.Grid[cell].IsWalkable)
        {
            return SpawnError.NotWalkable;
        }

        // A hex a moving unit is stepping into/out of counts as occupied.
        return state.IsOccupied(cell) || state.IsReserved(cell) ? SpawnError.Occupied : SpawnError.None;
    }

    public static SpawnResolution Resolve(GameState state, SpawnRequest request)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(request);

        switch (request.Method)
        {
            case SpawnMethod.DirectPlacement:
                {
                    if (request.Cell is not { } cell)
                    {
                        return new SpawnResolution(null, SpawnError.MissingCell);
                    }

                    var error = ValidateCell(state, cell);
                    return new SpawnResolution(error == SpawnError.None ? cell : null, error);
                }

            case SpawnMethod.PlayerBase:
                {
                    var playerBase = state.Map.PlayerBase;
                    if (playerBase is null || playerBase.Cells.Count == 0)
                    {
                        return new SpawnResolution(null, SpawnError.NoPlayerBase);
                    }

                    return ResolveInZone(state, playerBase.Cells, request.Cell, SpawnError.BaseFull);
                }

            case SpawnMethod.SpawnZone:
                {
                    var zone = state.Map.FindZone(request.ZoneId);
                    if (zone is null || zone.Cells.Count == 0)
                    {
                        return new SpawnResolution(null, SpawnError.UnknownZone);
                    }

                    return ResolveInZone(state, zone.Cells, request.Cell, SpawnError.ZoneFull);
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(request), $"Unknown spawn method {request.Method}.");
        }
    }

    /// <summary>
    /// Initial facing of a new unit: towards the centre of the map, quantised to six directions.
    /// </summary>
    public static HexDirection DefaultFacing(HexGrid grid, HexCoord cell)
    {
        var (min, max) = grid.PlaneBounds(UnitLayout);
        var center = new PlaneVector((min.X + max.X) / 2.0, (min.Z + max.Z) / 2.0);
        var d = center - UnitLayout.HexToPlane(cell);
        return HexDirections.FromPlaneVector(d.X, d.Z, 0.25) ?? HexDirection.North;
    }

    private static SpawnResolution ResolveInZone(GameState state, IReadOnlyList<HexCoord> zoneCells, HexCoord? requested, SpawnError fullError)
    {
        if (requested is { } exact)
        {
            if (!Contains(zoneCells, exact))
            {
                return new SpawnResolution(null, state.Grid.Contains(exact) ? SpawnError.CellNotInZone : SpawnError.OutOfBounds);
            }

            var error = ValidateCell(state, exact);
            return new SpawnResolution(error == SpawnError.None ? exact : null, error);
        }

        foreach (var cell in zoneCells)
        {
            if (ValidateCell(state, cell) == SpawnError.None)
            {
                return new SpawnResolution(cell, SpawnError.None);
            }
        }

        return new SpawnResolution(null, fullError);
    }

    private static bool Contains(IReadOnlyList<HexCoord> cells, HexCoord cell)
    {
        foreach (var c in cells)
        {
            if (c == cell)
            {
                return true;
            }
        }

        return false;
    }
}
