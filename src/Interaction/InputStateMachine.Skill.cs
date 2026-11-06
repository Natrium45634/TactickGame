using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Hex;
using HexTactics.Core.Units;

namespace HexTactics.Interaction;

/// <summary>Aiming an immediate skill use (spec part 3 §5.2).</summary>
/// <param name="Origin">Hex the skill will be used from (the unit's command anchor).</param>
/// <param name="Direction">One of six directions chosen by the pointer, or null in the neutral centre.</param>
/// <param name="Area">Existing hexes hit for <paramref name="Direction"/> (empty without a direction).</param>
public sealed record SkillAimView(UnitId Unit, string SkillId, HexCoord Origin, PlaneVector OriginCenter, HexDirection? Direction, IReadOnlyList<HexCoord> Area, PlaneVector? Pointer);

public sealed record SkillAimStarted(UnitId Unit, string SkillId) : InteractionFeedback;

public sealed record SkillAimCancelled(UnitId Unit) : InteractionFeedback;

/// <summary>The skill use was planned; it fires once when the unit stands on a hex centre (after a pause: on resume).</summary>
public sealed record SkillUsePlanned(UnitId Unit, string SkillId, HexDirection Direction) : InteractionFeedback;

public sealed record SkillUseRefused(UnitId Unit, string Reason) : InteractionFeedback;

/// <summary>
/// Immediate skill use (spec part 3 §5.2, design proposal for the trigger): with a unit selected, the skill button / key
/// starts aiming — the pointer picks one of six directions around the unit's hex (neutral centre: none), the two hit
/// hexes are previewed in grey, LMB confirms, RMB/Esc cancel only the aiming.
/// </summary>
public sealed partial class InputStateMachine
{
    private string? _aimSkill;

    public SkillAimView? SkillAim { get; private set; }

    public bool IsAimingSkill => Mode == InputMode.SkillAim;

    /// <summary>Starts aiming <paramref name="skillId"/> for the selected unit. Returns false if not possible.</summary>
    public bool BeginSkillAim(string skillId)
    {
        if (SelectedUnit is not { } id || _sim.State.FindUnit(id) is not { IsAlive: true, IsActive: true } unit
            || unit.Side != Settings.PlacementSide || !unit.Skills.Contains(skillId))
        {
            if (SelectedUnit is { } refused)
            {
                Feedback?.Invoke(new SkillUseRefused(refused, "not available"));
            }

            return false;
        }

        if (IsAimingSkill)
        {
            CancelSkillAim();
        }

        Cancel(); // drafts, command mode, roster card, debug modes
        Select(id);
        _aimSkill = skillId;
        Mode = InputMode.SkillAim;
        Feedback?.Invoke(new SkillAimStarted(id, skillId));
        UpdateSkillAim(Pointer ?? new PointerSample(new ScreenPoint(0, 0), null, null));
        return true;
    }

    public void CancelSkillAim()
    {
        if (!IsAimingSkill)
        {
            return;
        }

        var unit = SkillAim?.Unit ?? SelectedUnit;
        EndSkillAim();
        if (unit is { } u)
        {
            Feedback?.Invoke(new SkillAimCancelled(u));
        }
    }

    private void EndSkillAim()
    {
        _aimSkill = null;
        SkillAim = null;
        Mode = SelectedUnit is not null ? InputMode.UnitSelected : InputMode.Idle;
    }

    private void UpdateSkillAim(PointerSample sample)
    {
        if (!IsAimingSkill || SelectedUnit is not { } id || _aimSkill is not { } skillId)
        {
            return;
        }

        if (_sim.State.FindUnit(id) is not { IsAlive: true })
        {
            EndSkillAim();
            return;
        }

        var origin = _sim.CommandAnchor(id);
        var centre = _layout.HexToPlane(origin);
        HexDirection? direction = null;
        var point = sample.Ground ?? sample.Plane;
        if (point is { } p && (p - centre).Length >= Settings.NeutralZone * _layout.Size)
        {
            var v = p - centre;
            direction = HexDirections.FromPlaneVector(v.X, v.Z);
        }

        var area = direction is { } d && _sim.Settings.Skills.TryGetValue(skillId, out var skill)
            ? skill.Area(origin, d, _sim.State.Grid)
            : (IReadOnlyList<HexCoord>)System.Array.Empty<HexCoord>();
        SkillAim = new SkillAimView(id, skillId, origin, centre, direction, area, sample.Plane);
    }

    private void SkillAimClick(PointerSample sample)
    {
        UpdateSkillAim(sample);
        if (SkillAim is not { Direction: { } direction } view)
        {
            return; // the neutral centre never confirms
        }

        var result = _sim.UseSkill(view.Unit, view.SkillId, direction);
        EndSkillAim();
        Feedback?.Invoke(result.Success
            ? new SkillUsePlanned(view.Unit, view.SkillId, direction)
            : new SkillUseRefused(view.Unit, result.Reason ?? "refused"));
    }
}
