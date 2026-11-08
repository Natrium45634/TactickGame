using System;
using System.Collections.Generic;
using Godot;
using HexTactics.Core.Units;
using HexTactics.Simulation;

namespace HexTactics.Client.Presentation;

/// <summary>
/// Health bars above every unit (spec part 3 §3), drawn in screen space so they stay crisp and never cover the order
/// overlay with geometry. Purely visual: values come from the simulation.
/// </summary>
public partial class HealthBarsView : Control
{
    private readonly List<(Vector2 Position, float Fraction, bool Enemy, bool Selected)> _bars = new();

    public float UiScale { get; set; } = 1f;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
    }

    public void Sync(GameState state, Camera3D camera, Func<UnitState, Vector3> positionOf, UnitId? selected)
    {
        _bars.Clear();
        foreach (var unit in state.Units)
        {
            var world = positionOf(unit) + new Vector3(0, UnitView.BodyHeight + 0.42f, 0);
            if (camera.IsPositionBehind(world))
            {
                continue;
            }

            var fraction = unit.MaxHealth > 0 ? (float)unit.CurrentHealth / unit.MaxHealth : 0f;
            _bars.Add((camera.UnprojectPosition(world), fraction, unit.Side != HexTactics.Core.Units.Side.Player, unit.Id == selected));
        }

        QueueRedraw();
    }

    public override void _Draw()
    {
        var w = 38f * UiScale;
        var h = 5f * UiScale;
        foreach (var (p, fraction, enemy, selected) in _bars)
        {
            var origin = p - new Vector2(w * 0.5f, h * 0.5f);
            DrawRect(new Rect2(origin - new Vector2(1, 1), new Vector2(w + 2, h + 2)), new Color(0.06f, 0.07f, 0.09f, 0.85f));
            var fill = enemy ? new Color(0.92f, 0.33f, 0.28f) : new Color(0.36f, 0.82f, 0.42f);
            if (!enemy && fraction < 0.35f)
            {
                fill = new Color(0.95f, 0.72f, 0.25f);
            }

            DrawRect(new Rect2(origin, new Vector2(w * Mathf.Clamp(fraction, 0f, 1f), h)), fill);
            if (selected)
            {
                DrawRect(new Rect2(origin - new Vector2(2, 2), new Vector2(w + 4, h + 4)), new Color(1f, 0.9f, 0.55f, 0.9f), false, 1f);
            }
        }
    }
}

/// <summary>Short-lived combat effects: floating damage numbers and a fading mark where a unit died (visual only).</summary>
public partial class CombatFxView : Node3D
{
    private readonly List<(Node3D Node, double Age, double Life, Vector3 Start)> _items = new();

    // Continuous effects (periodic statuses, tile surfaces) change health every tick by fractions; they are summed per
    // unit and shown once per game second (or when they stop), not as 30 tiny numbers a second.
    private readonly Dictionary<(UnitId Unit, bool Heal), (double Amount, long FirstTick, double Idle, Vector3 At)> _continuous = new();

    private void Accumulate(UnitId unit, bool heal, double amount, long tick, Vector3 at)
    {
        var key = (unit, heal);
        if (_continuous.TryGetValue(key, out var acc))
        {
            acc = (acc.Amount + amount, acc.FirstTick, 0, at);
            _continuous[key] = acc;
            if (tick - acc.FirstTick >= 29)
            {
                Flush(key);
            }
        }
        else
        {
            _continuous[key] = (amount, tick, 0, at);
        }
    }

    private void Flush((UnitId Unit, bool Heal) key)
    {
        if (_continuous.Remove(key, out var acc) && acc.Amount >= 0.05)
        {
            var text = (key.Heal ? "+" : "−") + HexTactics.Client.UI.UiText.Hp(acc.Amount);
            Float(acc.At, text, key.Heal ? new Color(0.45f, 0.95f, 0.55f) : new Color(1f, 0.62f, 0.3f), 44, 1.0);
        }
    }

    public void Damage(Vector3 at, double amount, bool skill) =>
        Float(at, $"−{HexTactics.Client.UI.UiText.Hp(amount)}", skill ? new Color(1f, 0.45f, 0.35f) : new Color(1f, 0.95f, 0.85f), 52, 0.9);

    /// <summary>Floating text over a unit: damage, healing (green «+»), status names (smaller).</summary>
    public void Float(Vector3 at, string text, Color color, int size, double life)
    {
        var label = new Label3D
        {
            Text = text,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = true,
            FontSize = size,
            OutlineSize = 12,
            PixelSize = 0.0055f,
            Modulate = color,
            OutlineModulate = new Color(0.1f, 0.08f, 0.08f),
            RenderPriority = 45,
            OutlineRenderPriority = 44,
        };
        AddChild(label);
        var start = at + new Vector3(0, UnitView.BodyHeight + 0.55f, 0);
        label.Position = start;
        _items.Add((label, 0, life, start));
    }

    public void Death(Vector3 at)
    {
        var mark = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = UnitView.BodyRadius, BottomRadius = UnitView.BodyRadius, Height = 0.06f, RadialSegments = 24 },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.25f, 0.25f, 0.28f, 0.8f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(mark);
        mark.Position = at + new Vector3(0, 0.03f, 0);
        _items.Add((mark, 0, 1.2, mark.Position));
    }

    public override void _Process(double delta)
    {
        foreach (var key in new List<(UnitId, bool)>(_continuous.Keys))
        {
            var acc = _continuous[key];
            acc.Idle += delta;
            _continuous[key] = acc;
            if (acc.Idle > 0.35)
            {
                Flush(key); // the unit left the surface, the status ended or the game is paused
            }
        }

        for (var i = _items.Count - 1; i >= 0; i--)
        {
            var (node, age, life, start) = _items[i];
            age += delta;
            if (age >= life)
            {
                node.QueueFree();
                _items.RemoveAt(i);
                continue;
            }

            var t = (float)(age / life);
            switch (node)
            {
                case Label3D label:
                    label.Position = start + new Vector3(0, 0.45f * t, 0);
                    label.Modulate = label.Modulate with { A = 1f - (t * t) };
                    label.OutlineModulate = label.OutlineModulate with { A = 1f - (t * t) };
                    break;
                case MeshInstance3D mesh when mesh.MaterialOverride is StandardMaterial3D m && m.NoDepthTest:
                    m.AlbedoColor = m.AlbedoColor with { A = VisualConfig.SkillFlash.A * (1f - t) };
                    break;
                case MeshInstance3D mesh when mesh.MaterialOverride is StandardMaterial3D m:
                    m.AlbedoColor = m.AlbedoColor with { A = 0.8f * (1f - t) };
                    mesh.Scale = new Vector3(1f + t, 1f, 1f + t);
                    break;
            }

            _items[i] = (node, age, life, start);
        }
    }

    /// <summary>Brief flash of the hexes a skill hit (spec part 3 §6.4: no lingering "ghost" areas afterwards).</summary>
    public void SkillFlash(IEnumerable<HexTactics.Core.Hex.HexCoord> cells, HexTactics.Core.Hex.HexLayout layout)
    {
        foreach (var cell in cells)
        {
            var mark = new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = 0.92f, BottomRadius = 0.92f, Height = 0.02f, RadialSegments = 6 },
                MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = VisualConfig.SkillFlash,
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    NoDepthTest = true,
                    RenderPriority = 12,
                },
                Rotation = new Vector3(0, Mathf.Pi / 6f, 0),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            AddChild(mark);
            mark.Position = layout.HexToPlane(cell).ToWorld(0.03f);
            _items.Add((mark, 0, 0.6, mark.Position));
        }
    }

    private readonly System.Collections.Generic.Dictionary<(UnitId Unit, string Text), long> _notes = new();

    /// <summary>A grey word over a unit, at most once per game second per unit and word (continuous damage sends 30 events/s).</summary>
    private void Note(UnitId unit, long tick, string text, Vector3 position)
    {
        if (_notes.TryGetValue((unit, text), out var last) && tick - last < 30 && tick >= last)
        {
            return;
        }

        _notes[(unit, text)] = tick;
        Float(position + new Vector3(0, 0.45f, 0), text, text == "щит" ? new Color(0.65f, 0.85f, 1f) : new Color(0.8f, 0.8f, 0.8f), 34, 0.9);
    }

    /// <summary>How many times each hit word was shown (automation: <c>expect:hitword:&lt;word&gt;:yes|no</c>).</summary>
    public System.Collections.Generic.Dictionary<string, int> HitWords { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Spec part 6 §6.3 p.4, P6-51: a surprise or a critical hit is shown by a word over the target — never by its chance,
    /// side or multiplier (those stay in the developer mode).
    /// </summary>
    private void HitWord(DamageApplied d, Vector3 at)
    {
        var word = (d.Crit, d.Surprised) switch
        {
            (HexTactics.Core.Combat.CritTier.SuperCrit, true) => "Внезапный сверхкрит!",
            (HexTactics.Core.Combat.CritTier.SuperCrit, false) => "Сверхкрит!",
            (HexTactics.Core.Combat.CritTier.Crit, true) => "Внезапный крит!",
            (HexTactics.Core.Combat.CritTier.Crit, false) => "Крит!",
            (_, true) => "Внезапно!",
            _ => null,
        };
        if (word is null)
        {
            return;
        }

        HitWords[word] = HitWords.TryGetValue(word, out var n) ? n + 1 : 1;
        Float(at + new Vector3(0, 0.45f, 0), word, d.Crit == HexTactics.Core.Combat.CritTier.SuperCrit ? new Color(1f, 0.55f, 0.2f) : new Color(1f, 0.85f, 0.35f),
            d.Crit == HexTactics.Core.Combat.CritTier.Normal ? 36 : 42, 1.1);
    }

    /// <summary>Spawns effects for the combat events of the last simulation update.</summary>
    public void Handle(SimulationEvent e, Func<UnitId, Vector3?> positionOf, HexTactics.Core.Hex.HexLayout layout)
    {
        if (e is DamageApplied note && positionOf(note.Target) is { } np)
        {
            // Spec part 5 §4.2, §7.4: a non-numeric hint only — never the reason, the rule or a percentage.
            if (note.Immune)
            {
                Note(note.Target, note.Tick, "невосприимчив", np);
            }
            else if (note.ShieldAbsorbed > 0)
            {
                Note(note.Target, note.Tick, "щит", np);
            }
        }

        switch (e)
        {
            case StatusBlocked blocked when positionOf(blocked.Target) is { } bp:
                Note(blocked.Target, blocked.Tick, "невосприимчив", bp);
                break;
            case DamageApplied pd when pd.Amount > 0 && pd.Kind is DamageKind.Periodic or DamageKind.Surface && positionOf(pd.Target) is { } pp:
                Accumulate(pd.Target, false, pd.Amount, pd.Tick, pp);
                break;
            case HealApplied ph when ph.Amount > 0 && ph.Kind is DamageKind.Periodic or DamageKind.Surface && positionOf(ph.Target) is { } php:
                Accumulate(ph.Target, true, ph.Amount, ph.Tick, php);
                break;
            case DamageApplied d when d.Amount > 0 && positionOf(d.Target) is { } p:
                Damage(p, d.Amount, d.Kind == DamageKind.Skill);
                HitWord(d, p);
                break;
            case UnitDied died:
                Death(layout.HexToPlane(died.Cell).ToWorld(0f));
                break;
            case SkillActivated skill:
                SkillFlash(skill.Cells, layout);
                break;
            case SkillHit hit when hit.HitIndex > 0:
                SkillFlash(new[] { hit.Cell }, layout); // later hits of a timed skill flash their own cell
                break;
            case HealApplied h when h.Amount > 0 && positionOf(h.Target) is { } hp:
                Float(hp, $"+{HexTactics.Client.UI.UiText.Hp(h.Amount)}", new Color(0.45f, 0.95f, 0.55f), 52, 0.9);
                break;
            case StatusApplied st when positionOf(st.Target) is { } sp:
                Float(sp + new Vector3(0, 0.25f, 0), HexTactics.Client.UI.UiText.StatusName(st.Kind, st.Stat, st.Key), new Color(0.85f, 0.85f, 1f), 34, 1.1);
                break;
        }
    }
}
