using System.Text.Json.Nodes;
using HexTactics.Content.Model;

namespace HexTactics.Content.Kinds;

/// <summary>
/// Schema migrations of the content kinds (spec part 4 §8.4, part 5 §9.5). Each step turns data of schema N into N + 1; the
/// library keeps a backup of the original file. Nothing is guessed: old values get the documented legacy meaning.
/// </summary>
public static class Migrations
{
    /// <summary>Legacy neutral type of old skill/attack/status damage (resistance-affected, zero resistances = old numbers).</summary>
    public const string Untyped = "core.damage.untyped";

    /// <summary>Legacy «direct HP effect» of old tile surfaces (bypasses resistances: the same HP per second for every target).</summary>
    public const string Direct = "core.damage.direct";

    /// <summary>
    /// Resistance set 1 → 2 (spec part 6 §3.2, §9.1 «Профили защиты и сопротивлений»): the set gets an empty defense A table.
    /// </summary>
    public static JsonObject? ResistanceProfile(int from, JsonObject data, IssueList issues)
    {
        if (from != 1)
        {
            return null;
        }

        var copy = (JsonObject)data.DeepClone();
        copy["armor"] ??= new JsonObject();
        return copy;
    }

    /// <summary>Skill 1 → 2: damage and damaging periodic effects without a damage type get the legacy neutral type.</summary>
    public static JsonObject? Skill(int from, JsonObject data, IssueList issues)
    {
        if (from != 1)
        {
            return null;
        }

        var copy = (JsonObject)data.DeepClone();
        TypeEffects(copy["baseEffects"] as JsonArray, Untyped);
        if (copy["cells"] is JsonArray cells)
        {
            foreach (var cell in cells)
            {
                if (cell is JsonObject c)
                {
                    TypeEffects(c["effects"] as JsonArray, Untyped);
                }
            }
        }

        return copy;
    }

    /// <summary>Tile 1 → 2: damage of old tile effects keeps its exact HP as the legacy direct type.</summary>
    public static JsonObject? Tile(int from, JsonObject data, IssueList issues)
    {
        if (from != 1)
        {
            return null;
        }

        var copy = (JsonObject)data.DeepClone();
        TypeEffects(copy["effects"] as JsonArray, Direct);
        return copy;
    }

    /// <summary>
    /// Unit 1 → 2: the normal attack hits with the legacy neutral type; no resistances, no area (no hidden bonuses).
    /// Unit 2 → 3 (spec part 5 §9.5): no defense rules and no shields — nothing is invented for old units.
    /// Unit 3 → 4: no resistance sets; the unit's resistances become its own values (same numbers).
    /// Unit 4 → 5 (spec part 6 §3.5, §10.3): defense A = 0 and no armor penetration — K / (K + 0) = 1, old hits stay the same.
    /// </summary>
    public static JsonObject? Unit(int from, JsonObject data, IssueList issues)
    {
        var copy = (JsonObject)data.DeepClone();
        switch (from)
        {
            case 1:
                copy["attackDamageType"] ??= Untyped;
                return copy;
            case 2:
                copy["defenseRules"] ??= new JsonArray();
                copy["shields"] ??= new JsonArray();
                return copy;
            case 3:
                copy["resistanceProfiles"] ??= new JsonArray(); // spec part 5 §6.4: own values stay, no set is applied
                return copy;
            case 4:
                copy["armor"] ??= new JsonObject(); // spec part 6 §3.5: no hidden armor for old units
                copy["attackArmorPenetration"] ??= 0.0;
                copy["attackArmorPenetrationPercent"] ??= 0.0;
                return copy;
            default:
                return null;
        }
    }

    private static void TypeEffects(JsonArray? effects, string legacyType)
    {
        if (effects is null)
        {
            return;
        }

        foreach (var node in effects)
        {
            if (node is not JsonObject e || e["damageType"] is not null)
            {
                continue;
            }

            var type = e["type"]?.GetValue<string>();
            var damaging = type == "damage" || (type == "periodic" && (e["kind"]?.GetValue<string>() ?? "damage") == "damage");
            if (damaging)
            {
                e["damageType"] = legacyType;
            }
        }
    }
}
