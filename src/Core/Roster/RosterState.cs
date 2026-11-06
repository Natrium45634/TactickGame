using System;
using System.Collections.Generic;
using HexTactics.Core.Units;

namespace HexTactics.Core.Roster;

/// <summary>Placement lifecycle of a roster card / operative (spec part 2 §5.7).</summary>
public enum RosterEntryState
{
    /// <summary>In the bottom panel, can be dragged onto the map.</summary>
    AvailableInRoster = 0,

    /// <summary>The card is being dragged over the map (nothing exists on the map yet).</summary>
    PlacementPreview = 1,

    /// <summary>The unit stands on the map but has no facing yet; it is not active and does not act.</summary>
    PlacedPendingFacing = 2,

    /// <summary>The unit is on the map with a valid facing and executes orders.</summary>
    Active = 3,

    /// <summary>Cannot be placed now (reserved for future resource/count limits).</summary>
    Unavailable = 4,
}

/// <summary>One operative of the roster.</summary>
public sealed class RosterEntry
{
    internal RosterEntry(int id, string name)
    {
        Id = id;
        Name = name;
    }

    public int Id { get; }

    public string Name { get; }

    public RosterEntryState State { get; internal set; } = RosterEntryState.AvailableInRoster;

    /// <summary>The unit created from this entry (once placed).</summary>
    public UnitId? Unit { get; internal set; }
}

/// <summary>The player's roster (bottom panel). Concrete limits (resources, counts) are a future spec item.</summary>
public sealed class RosterState
{
    private readonly List<RosterEntry> _entries = new();

    public RosterState(IEnumerable<string> names)
    {
        var id = 1;
        foreach (var name in names)
        {
            _entries.Add(new RosterEntry(id++, name));
        }
    }

    /// <summary>The prototype roster: five operatives (spec part 2 §1: planning for five units).</summary>
    public static RosterState Prototype() => new(new[] { "Альфа", "Браво", "Чарли", "Дельта", "Эхо" });

    public static RosterState Empty() => new(Array.Empty<string>());

    public IReadOnlyList<RosterEntry> Entries => _entries;

    /// <summary>Incremented on every state change (for UI refresh).</summary>
    public int Version { get; private set; }

    public RosterEntry? Find(int id) => _entries.Find(e => e.Id == id);

    public RosterEntry? FindByUnit(UnitId unit) => _entries.Find(e => e.Unit == unit);

    internal void Set(RosterEntry entry, RosterEntryState state, UnitId? unit = null)
    {
        entry.State = state;
        if (unit is not null)
        {
            entry.Unit = unit;
        }

        Version++;
    }
}
