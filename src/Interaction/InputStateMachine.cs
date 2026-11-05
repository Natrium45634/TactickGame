using System;
using HexTactics.Core.Hex;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;
using HexTactics.Simulation;

namespace HexTactics.Interaction;

/// <summary>Current mode of the input state machine (spec §5.3).</summary>
public enum InputMode
{
    Idle = 0,
    UnitSelected = 1,

    /// <summary>Debug placement mode: the next click on a hex places a unit (spawn method 3).</summary>
    Placement = 2,
}

public sealed record InteractionSettings
{
    /// <summary>Pointer travel (pixels) below which a press-release is a click, not a drag.</summary>
    public double DragThresholdPixels { get; init; } = 8.0;

    public Side PlacementSide { get; init; } = Side.Player;

    public static InteractionSettings Default { get; } = new();
}

/// <summary>
/// Finite state machine for mouse/keyboard interaction, working purely in map coordinates.
/// The client feeds pointer samples; the machine issues commands to <see cref="GameSimulation"/>
/// and reports <see cref="InteractionFeedback"/>. No Godot dependency, fully unit-testable.
/// </summary>
public sealed class InputStateMachine
{
    private readonly GameSimulation _sim;
    private PressInfo? _press;

    public InputStateMachine(GameSimulation simulation, InteractionSettings? settings = null)
    {
        _sim = simulation ?? throw new ArgumentNullException(nameof(simulation));
        Settings = settings ?? InteractionSettings.Default;
    }

    public event Action<InteractionFeedback>? Feedback;

    public InteractionSettings Settings { get; }

    public InputMode Mode { get; private set; } = InputMode.Idle;

    public UnitId? SelectedUnit { get; private set; }

    /// <summary>Last known pointer sample (for previews).</summary>
    public PointerSample? Pointer { get; private set; }

    public bool IsPlacementActive => Mode == InputMode.Placement;

    public void PointerDown(PointerButton button, PointerSample sample)
    {
        Pointer = sample;
        _press = new PressInfo(button, sample);
    }

    public void PointerMove(PointerSample sample) => Pointer = sample;

    public void PointerUp(PointerButton button, PointerSample sample)
    {
        Pointer = sample;
        var press = _press;
        _press = null;
        if (press is null || press.Value.Button != button)
        {
            return;
        }

        var isClick = press.Value.Sample.Screen.DistanceTo(sample.Screen) <= Settings.DragThresholdPixels;
        if (!isClick)
        {
            return; // drags are handled from v0.3.0
        }

        if (button == PointerButton.Secondary)
        {
            if (Mode == InputMode.Placement)
            {
                ExitPlacementMode();
            }

            return;
        }

        OnPrimaryClick(sample);
    }

    /// <summary>Esc: leaves the current transient mode without changing the game state.</summary>
    public void Cancel()
    {
        _press = null;
        if (Mode == InputMode.Placement)
        {
            ExitPlacementMode();
        }
    }

    public void EnterPlacementMode()
    {
        if (Mode == InputMode.Placement)
        {
            return;
        }

        Mode = InputMode.Placement;
        Feedback?.Invoke(new PlacementModeChanged(true));
    }

    public void ExitPlacementMode()
    {
        if (Mode != InputMode.Placement)
        {
            return;
        }

        Mode = SelectedUnit is not null ? InputMode.UnitSelected : InputMode.Idle;
        Feedback?.Invoke(new PlacementModeChanged(false));
    }

    public void TogglePlacementMode()
    {
        if (Mode == InputMode.Placement)
        {
            ExitPlacementMode();
        }
        else
        {
            EnterPlacementMode();
        }
    }

    /// <summary>Selects a unit programmatically (e.g. after spawning from the debug panel).</summary>
    public void Select(UnitId? unit)
    {
        if (unit is { } id && _sim.State.FindUnit(id) is null)
        {
            return;
        }

        if (Mode == InputMode.Placement)
        {
            ExitPlacementMode();
        }

        Mode = unit is null ? InputMode.Idle : InputMode.UnitSelected;
        SetSelection(unit);
    }

    private void OnPrimaryClick(PointerSample sample)
    {
        if (Mode == InputMode.Placement)
        {
            PlaceAt(sample.Hex);
            return;
        }

        var unit = ResolveUnit(sample);
        Select(unit);
    }

    private void PlaceAt(HexCoord? hex)
    {
        // A pointer that misses the plane yields a request without a cell (rejected as MissingCell).
        var request = hex is { } cell
            ? SpawnRequest.AtCell(cell, Settings.PlacementSide)
            : new SpawnRequest(SpawnMethod.DirectPlacement, Settings.PlacementSide);
        var result = _sim.Spawn(request);
        Feedback?.Invoke(new SpawnAttempted(request, result));
        if (result.Success)
        {
            Mode = InputMode.UnitSelected;
            Feedback?.Invoke(new PlacementModeChanged(false));
            SetSelection(result.Unit);
        }
    }

    private UnitId? ResolveUnit(PointerSample sample)
    {
        if (sample.Unit is { } id && _sim.State.FindUnit(id) is not null)
        {
            return id;
        }

        return sample.Hex is { } hex ? _sim.State.UnitAt(hex)?.Id : null;
    }

    private void SetSelection(UnitId? unit)
    {
        if (SelectedUnit == unit)
        {
            return;
        }

        SelectedUnit = unit;
        Feedback?.Invoke(new SelectionChanged(unit));
    }

    private readonly record struct PressInfo(PointerButton Button, PointerSample Sample);
}
