using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using HexTactics.Content.Model;
using HexTactics.Content.Schema;

namespace HexTactics.Content.Kinds;

/// <summary>
/// An operation a passive can perform (spec part 6 §4.2): a registered handler with its fields, range and template label.
/// New operations are added here together with their handler in <c>RuntimeFactory</c> and a test (spec part 6 §2.5).
/// </summary>
public sealed record PassiveOperation(string Id, string Template, string Hint, bool UsesStat, bool UsesDamageType, bool UsesKey, bool UsesRule,
    bool UsesCondition, double Min, double Max, string Unit, double DefaultValue);

/// <summary>The registry of passive operations (spec part 6 §4.2; crits come with v0.30.0, pack counting with v0.29.0).</summary>
public static class PassiveOperations
{
    public const string StatAddFlat = "stat_add_flat";
    public const string StatAddPercent = "stat_add_percent";
    public const string OutgoingDamagePercent = "outgoing_damage_percent";
    public const string IncomingDamagePercent = "incoming_damage_percent";
    public const string ResistanceDeltaPP = "resistance_delta_pp";
    public const string DefenseDeltaFlat = "defense_delta_flat";
    public const string DefenseDeltaPercent = "defense_delta_percent";
    public const string GrantDefenseRule = "grant_defense_rule";

    public static IReadOnlyList<PassiveOperation> All { get; } = new[]
    {
        new PassiveOperation(OutgoingDamagePercent, "усилить исходящий урон", "Урон, который наносит юнит, до защиты цели: +50 % — удар 20 становится 30. Обычная атака и навыки; не отражения и не поверхности.",
            false, true, false, false, true, -100, 1000, "%", 20),
        new PassiveOperation(IncomingDamagePercent, "снизить получаемый урон", "Множитель остатка после сопротивления R, защиты A и плоского снижения: −25 % — остаток 40 становится 30. Это не R: R меняет само сопротивление.",
            false, true, false, false, true, -100, 1000, "%", -25),
        new PassiveOperation(StatAddPercent, "изменить характеристику, %", "Базовое значение × (1 + %): +10 % силы атаки 10 → 11; −25 % здоровья 100 → 75.",
            true, false, false, false, false, -100, 1000, "%", 10),
        new PassiveOperation(StatAddFlat, "изменить характеристику на число", "Прибавляется после процентов: +5 силы атаки.",
            true, false, false, false, false, -10000, 10000, string.Empty, 5),
        new PassiveOperation(ResistanceDeltaPP, "сопротивление ± п.п.", "Меняет само сопротивление R на столько процентных пунктов (итог R ограничен −500…+500).",
            false, false, true, false, false, -500, 500, "п.п.", 25),
        new PassiveOperation(DefenseDeltaFlat, "защита A ± очки", "Прибавляется к защите A слоя после процентов (итог A не ниже 0).",
            false, false, true, false, false, -1_000_000, 1_000_000, "очк.", 30),
        new PassiveOperation(DefenseDeltaPercent, "защита A ± %", "Меняет накопленную базовую защиту A слоя: +20 % — 100 → 120; затем прибавляются очки.",
            false, false, true, false, false, -100, 1000, "%", 20),
        new PassiveOperation(GrantDefenseRule, "правило защиты", "Подключает сохранённое правило защиты (иммунитет, отражение, щит…) как постоянную особенность юнита.",
            false, false, false, true, false, 0, 0, string.Empty, 0),
    };

    public static PassiveOperation? Find(string id) => All.FirstOrDefault(o => o.Id == id);

    /// <summary>Characteristics a passive can change (only existing ones; the stat registry arrives with v0.29.0).</summary>
    public static IReadOnlyList<FieldOption> Stats { get; } = new[]
    {
        new FieldOption("attackPower", "сила атаки (Attack Power)"),
        new FieldOption("maxHealth", "максимум здоровья"),
        new FieldOption("moveSpeed", "скорость движения"),
        new FieldOption("attackRate", "скорость атак"),
    };
}

/// <summary>
/// A personal permanent passive of one unit (spec part 6 §4.1): an operation with its parameters, always active while the
/// unit lives (or under its explicit health condition). Not a skill: no slot, no activation, never executed as code.
/// </summary>
public sealed class PassiveData
{
    public const int MaxPerUnit = 16;

    /// <summary>Id inside the unit (stable for undo and «откуда взялось»).</summary>
    public string Id { get; set; } = "p1";

    public string Name { get; set; } = "Пассив";

    public string Operation { get; set; } = PassiveOperations.OutgoingDamagePercent;

    /// <summary>Characteristic of the stat operations.</summary>
    public string Stat { get; set; } = "attackPower";

    /// <summary>Damage type filter of the damage operations: empty — every type (incoming: every resistance-affected one).</summary>
    public string DamageType { get; set; } = string.Empty;

    /// <summary>Layer of the R/A operations: <c>global</c>, a family or a type id.</summary>
    public string Key { get; set; } = UnitData.GlobalResistance;

    public double Value { get; set; }

    /// <summary>Referenced defense rule of <see cref="PassiveOperations.GrantDefenseRule"/>.</summary>
    public string Rule { get; set; } = string.Empty;

    /// <summary>Acts only while health is below this percent (0 — always), spec part 6 §4.2 «ConditionalEffect».</summary>
    public double HealthBelowPercent { get; set; }

    /// <summary>Stacking group of percentage damage modifiers; empty — the standard additive group of the operation.</summary>
    public string StackGroup { get; set; } = string.Empty;

    public PassiveOperation? Definition => PassiveOperations.Find(Operation);

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["id"] = Id, ["name"] = Name, ["operation"] = Operation };
        var def = Definition;
        if (def?.UsesStat == true)
        {
            o["stat"] = Stat;
        }

        if (def?.UsesDamageType == true)
        {
            o["damageTypeId"] = DamageType;
        }

        if (def?.UsesKey == true)
        {
            o["key"] = Key;
        }

        if (def?.UsesRule == true)
        {
            o["ruleId"] = Rule;
        }
        else
        {
            o["value"] = Value;
        }

        if (def?.UsesCondition == true && HealthBelowPercent > 0)
        {
            o["healthBelowPercent"] = HealthBelowPercent;
        }

        if (StackGroup.Length > 0)
        {
            o["stackGroup"] = StackGroup;
        }

        return o;
    }

    public static PassiveData Read(JsonObject obj, IssueList issues, string path) => new()
    {
        Id = JsonFields.Text(obj, "id", "p1", issues, path),
        Name = JsonFields.Text(obj, "name", "Пассив", issues, path),
        Operation = JsonFields.Text(obj, "operation", string.Empty, issues, path),
        Stat = JsonFields.Text(obj, "stat", "attackPower", issues, path),
        DamageType = JsonFields.Text(obj, "damageTypeId", string.Empty, issues, path),
        Key = JsonFields.Text(obj, "key", UnitData.GlobalResistance, issues, path),
        Value = JsonFields.Number(obj, "value", 0, issues, path),
        Rule = JsonFields.Text(obj, "ruleId", string.Empty, issues, path),
        HealthBelowPercent = JsonFields.Number(obj, "healthBelowPercent", 0, issues, path),
        StackGroup = JsonFields.Text(obj, "stackGroup", string.Empty, issues, path),
    };

    /// <summary>Library objects the passive refers to.</summary>
    public IEnumerable<ContentRef> References()
    {
        var def = Definition;
        if (def?.UsesDamageType == true && DamageType.Length > 0)
        {
            yield return new ContentRef("damage_type", DamageType);
        }

        if (def?.UsesKey == true && Key.Length > 0 && Key != UnitData.GlobalResistance)
        {
            yield return new ContentRef("damage_type", Key);
        }

        if (def?.UsesRule == true && Rule.Length > 0)
        {
            yield return new ContentRef("defense_rule", Rule);
        }
    }

    public void RemapReferences(IReadOnlyDictionary<ContentRef, string> newIds)
    {
        if (newIds.TryGetValue(new ContentRef("damage_type", DamageType), out var type))
        {
            DamageType = type;
        }

        if (newIds.TryGetValue(new ContentRef("damage_type", Key), out var key))
        {
            Key = key;
        }

        if (newIds.TryGetValue(new ContentRef("defense_rule", Rule), out var rule))
        {
            Rule = rule;
        }
    }

    public void Validate(IssueList issues, string path)
    {
        if (Definition is not { } def)
        {
            issues.Error($"{path}.operation", $"неизвестная операция пассива «{Operation}» — исполнителя нет, пассив не действует");
            return;
        }

        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 60)
        {
            issues.Error($"{path}.name", "у пассива нужно название до 60 символов");
        }

        if (Id.Length is 0 or > 32 || !Id.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-'))
        {
            issues.Error($"{path}.id", $"неверный ID пассива «{Id}» (1–32 символа a–z, 0–9, _ и -)");
        }

        if (def.UsesStat && PassiveOperations.Stats.All(s => s.Value != Stat))
        {
            issues.Error($"{path}.stat", $"неизвестная характеристика «{Stat}»");
        }

        if (def.UsesDamageType && DamageType.Length > 0 && !ContentIds.IsValid(DamageType))
        {
            issues.Error($"{path}.damageTypeId", $"неверный тип урона «{DamageType}»");
        }

        if (def.UsesKey && Key != UnitData.GlobalResistance && !ContentIds.IsValid(Key))
        {
            issues.Error($"{path}.key", $"неверный слой «{Key}»");
        }

        if (def.UsesRule)
        {
            if (!ContentIds.IsValid(Rule))
            {
                issues.Error($"{path}.ruleId", "выберите правило защиты");
            }
        }
        else if (!double.IsFinite(Value) || Value < def.Min || Value > def.Max)
        {
            issues.Error($"{path}.value", $"значение: от {N(def.Min)} до {N(def.Max)}{(def.Unit.Length > 0 ? " " + def.Unit : string.Empty)}, сейчас {N(Value)}");
        }

        if (!double.IsFinite(HealthBelowPercent) || HealthBelowPercent < 0 || HealthBelowPercent > 100)
        {
            issues.Error($"{path}.healthBelowPercent", "условие «здоровье ниже» — от 0 до 100 %");
        }

        if (StackGroup.Length > 40)
        {
            issues.Error($"{path}.stackGroup", "группа суммирования — до 40 символов");
        }
    }

    public PassiveData Clone() => new()
    {
        Id = Id, Name = Name, Operation = Operation, Stat = Stat, DamageType = DamageType, Key = Key, Value = Value, Rule = Rule,
        HealthBelowPercent = HealthBelowPercent, StackGroup = StackGroup,
    };

    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
