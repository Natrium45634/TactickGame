using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using HexTactics.Content.Model;
using HexTactics.Content.Schema;

namespace HexTactics.Content.Kinds;

/// <summary>
/// A unit definition of the library (spec part 4 §5, <c>UnitDefinition</c>): characteristics, skills (references to skill
/// definitions) and appearance. Not a unit on a map: live units keep their own current health, path and facing.
/// Defaults are the prototype values of spec part 3 (attack power 10 is PROTOTYPE_DEFAULT).
/// </summary>
public sealed class UnitData : IContentData
{
    public double MaxHealth { get; set; } = 100;

    public double MoveSpeed { get; set; } = 1.0;

    public double AttackRate { get; set; } = 3.0;

    /// <summary>PROTOTYPE_DEFAULT: strength of the normal attack and the base of «% от AttackPower» skill effects.</summary>
    public double AttackPower { get; set; } = 10;

    public int AttackRange { get; set; } = 1;

    public List<string> Skills { get; } = new();

    /// <summary>Damage type of the normal attack (spec part 5 §8.3); new units hit physically.</summary>
    public string AttackDamageType { get; set; } = "core.damage.physical";

    /// <summary>0 — the target only; N — every enemy within N hexes of the target (spec part 5 §2.1).</summary>
    public int AttackAreaRadius { get; set; }

    /// <summary>Area normal attack: split the strength among the targets instead of full strength to each.</summary>
    public bool AttackAreaSplit { get; set; }

    /// <summary>
    /// Numeric resistances in percentage points, −500…+500 (spec part 5 §3, §7): key <c>global</c> or a damage type id
    /// (a family or a subtype). Missing keys are 0. Visible only in the developer mode.
    /// </summary>
    public SortedDictionary<string, double> Resistances { get; } = new(System.StringComparer.Ordinal);

    /// <summary>
    /// Defense A in defense points, 0…1 000 000 (spec part 6 §3.2): key <c>global</c> or a damage type id (a family or a
    /// subtype); the formula multiplies the damage after R by K / (K + A). Missing keys are 0. Only in the developer mode.
    /// </summary>
    public SortedDictionary<string, double> Armor { get; } = new(System.StringComparer.Ordinal);

    public const double MinArmor = 0;
    public const double MaxArmor = 1_000_000;

    /// <summary>Critical chance of the unit's hits in percentage points (spec part 6 §7.1); may exceed 100 (supercrit).</summary>
    public double CritChance { get; set; }

    /// <summary>The automatic base attack (spec part 7 §8.2 <c>basicAttackRef</c>): a <c>basic_attack</c> id.</summary>
    public string BasicAttack { get; set; } = HexTactics.Core.Combat.BasicAttackDefinition.DefaultId;

    /// <summary>Spec part 7 §8.2 <c>autoAttackEnabled</c>: the unit hits enemies in its base attack area on its own (where the map allows).</summary>
    public bool AutoAttackEnabled { get; set; } = true;

    /// <summary>Armor penetration of the normal attack in defense points (spec part 6 §3.4 p.4).</summary>
    public double AttackArmorPenetration { get; set; }

    /// <summary>Percent 0…100 of the remaining defense A the normal attack ignores.</summary>
    public double AttackArmorPenetrationPercent { get; set; }

    /// <summary>
    /// Resistance sets applied to the unit by reference, in order (spec part 5 §6.4 <c>resistanceProfileRefs</c>); a later set
    /// replaces an earlier one per key, <see cref="Resistances"/> are the unit's own values over them.
    /// </summary>
    public List<string> ResistanceProfiles { get; } = new();

    public const int MaxResistanceProfiles = 8;

    /// <summary>The unit's species (spec part 6 §5.2): one node of the tree, its ancestors count too; empty — none.</summary>
    public string Species { get; set; } = string.Empty;

    /// <summary>Shared traits of the unit in order (spec part 6 §5.3); a trait listed twice acts once unless it stacks.</summary>
    public List<string> Traits { get; } = new();

    public const int MaxTraits = 16;

    /// <summary>Pack of the unit (spec part 6 §5.5 <c>packId</c>) for «per matching ally» bonuses; empty — none.</summary>
    public string PackId { get; set; } = string.Empty;

    /// <summary>Own base values of registry characteristics (spec part 6 §5.7), e.g. Agility; missing — the stat's base.</summary>
    public SortedDictionary<string, double> CustomStats { get; } = new(System.StringComparer.Ordinal);

    /// <summary>Personal permanent passives of the unit (spec part 6 §4), in order; not shared with other units.</summary>
    public List<PassiveData> Passives { get; } = new();

    /// <summary>References to defense rules of the library (spec part 5 §6.4, §8.3 <c>defenseRuleRefs</c>), no copies.</summary>
    public List<string> DefenseRules { get; } = new();

    /// <summary>Shields of the unit (spec part 5 §4.5 <c>ShieldPool</c>); a new unit in battle starts with them full.</summary>
    public List<ShieldData> Shields { get; } = new();

    public const int MaxDefenseRules = 16;
    public const int MaxShields = 4;

    public const string GlobalResistance = "global";
    public const double MinResistance = -500;
    public const double MaxResistance = 500;

    public string Side { get; set; } = "player";

    public string Color { get; set; } = "#4D80CC";

    public double Scale { get; set; } = 1.0;

    public static IReadOnlyList<FieldSchema> Fields { get; } = new FieldSchema[]
    {
        new()
        {
            Key = "maxHealth", Label = "Максимум здоровья", Kind = FieldKind.Integer, Default = 100.0, Min = 1, Max = 100000, Unit = "HP",
            Hint = "Здоровье нового юнита; текущее здоровье в бою хранится отдельно.",
            Get = o => ((UnitData)o).MaxHealth, Set = (o, v) => ((UnitData)o).MaxHealth = (double)v!,
        },
        new()
        {
            Key = "moveSpeed", Label = "Скорость движения", Kind = FieldKind.Number, Default = 1.0, Min = 0.1, Max = 10, Unit = "гекс/с",
            Get = o => ((UnitData)o).MoveSpeed, Set = (o, v) => ((UnitData)o).MoveSpeed = (double)v!,
        },
        new()
        {
            Key = "attackRate", Label = "Скорость атак", Kind = FieldKind.Number, Default = 3.0, Min = 0.1, Max = 20, Unit = "удар/с",
            Hint = "Сколько обычных ударов в секунду — не сила удара.",
            Get = o => ((UnitData)o).AttackRate, Set = (o, v) => ((UnitData)o).AttackRate = (double)v!,
        },
        new()
        {
            Key = "attackPower", Label = "Сила атаки (Attack Power)", Kind = FieldKind.Number, Default = 10.0, Min = 0, Max = 10000,
            Hint = "Урон обычного удара и база для навыков «% от Attack Power». 10 — временное значение прототипа.",
            Get = o => ((UnitData)o).AttackPower, Set = (o, v) => ((UnitData)o).AttackPower = (double)v!,
        },
        new()
        {
            Key = "attackRange", Label = "Дальность обычной атаки", Kind = FieldKind.Integer, Default = 1.0, Min = 1, Max = 6, Unit = "гекс",
            Get = o => (double)((UnitData)o).AttackRange, Set = (o, v) => ((UnitData)o).AttackRange = (int)System.Math.Round((double)v!),
        },
        new()
        {
            Key = "attackDamageType", Label = "Тип урона обычной атаки", Kind = FieldKind.Reference, RefKind = "damage_type",
            Default = "core.damage.physical", Hint = "Природа обычного удара: от неё зависят сопротивления цели.",
            Get = o => ((UnitData)o).AttackDamageType, Set = (o, v) => ((UnitData)o).AttackDamageType = (string)v!,
        },
        new()
        {
            Key = "attackAreaRadius", Label = "Область обычной атаки", Kind = FieldKind.Integer, Default = 0.0, Min = 0, Max = 3, Unit = "гекс",
            Hint = "0 — только цель; 1–3 — каждый враг в этом радиусе от гекса цели получает удар (цель тоже).",
            Get = o => (double)((UnitData)o).AttackAreaRadius, Set = (o, v) => ((UnitData)o).AttackAreaRadius = (int)System.Math.Round((double)v!),
        },
        new()
        {
            Key = "attackAreaSplit", Label = "Разделить силу удара между целями области", Kind = FieldKind.Flag, Default = false,
            Hint = "Если снято — каждая цель области получает полную силу.",
            Get = o => ((UnitData)o).AttackAreaSplit, Set = (o, v) => ((UnitData)o).AttackAreaSplit = (bool)v!,
        },
        new()
        {
            Key = "autoAttackEnabled", Label = "Автоатака включена", Kind = FieldKind.Flag, Default = true,
            Hint = "Юнит сам бьёт врагов в области своей базовой атаки (на картах с включённой автоатакой), не поворачиваясь и не преследуя их.",
            Get = o => ((UnitData)o).AutoAttackEnabled, Set = (o, v) => ((UnitData)o).AutoAttackEnabled = (bool)v!,
        },
        new()
        {
            Key = "basicAttack", Label = "Базовая атака", Kind = FieldKind.Reference, RefKind = "basic_attack",
            Default = HexTactics.Core.Combat.BasicAttackDefinition.DefaultId,
            Hint = "Область и выбор цели автоматической атаки; не зависит от навыков. Своя — «Механики» → «Базовые атаки».",
            Get = o => ((UnitData)o).BasicAttack, Set = (o, v) => ((UnitData)o).BasicAttack = (string)v!,
        },
        new()
        {
            Key = "critChance", Label = "Шанс крита", Kind = FieldKind.Number, Default = 0.0, Min = 0, Max = 1000, Unit = "п.п.",
            Hint = "Только если правила боя карты включают криты: 35 — 35 % крита, 100 — крит всегда, 125 — ещё 25 % сверхкрита, 200 — сверхкрит всегда.",
            Get = o => ((UnitData)o).CritChance, Set = (o, v) => ((UnitData)o).CritChance = (double)v!,
        },
        new()
        {
            Key = "attackArmorPenetration", Label = "Пробитие защиты A", Kind = FieldKind.Number, Default = 0.0, Min = 0, Max = 1_000_000, Unit = "очк.",
            Advanced = true, Hint = "Обычный удар вычитает столько очков из защиты A цели (не ниже 0); сопротивление R не меняет.",
            Get = o => ((UnitData)o).AttackArmorPenetration, Set = (o, v) => ((UnitData)o).AttackArmorPenetration = (double)v!,
        },
        new()
        {
            Key = "attackArmorPenetrationPercent", Label = "Пробитие защиты A, %", Kind = FieldKind.Number, Default = 0.0, Min = 0, Max = 100, Unit = "%",
            Advanced = true, Hint = "Доля оставшейся защиты A цели, которую обычный удар не учитывает.",
            Get = o => ((UnitData)o).AttackArmorPenetrationPercent, Set = (o, v) => ((UnitData)o).AttackArmorPenetrationPercent = (double)v!,
        },
        new()
        {
            Key = "skills", Label = "Навыки", Kind = FieldKind.ReferenceList, RefKind = "skill", Section = FieldSections.Effects,
            Hint = "Ссылки на сохранённые навыки (без копирования их определений).",
            Get = o => ((UnitData)o).Skills,
            Set = (o, v) =>
            {
                var u = (UnitData)o;
                u.Skills.Clear();
                u.Skills.AddRange((IEnumerable<string>)v!);
            },
        },
        new()
        {
            Key = "side", Label = "Сторона", Kind = FieldKind.Choice, Default = "player",
            Options = new[] { new FieldOption("player", "игрок"), new FieldOption("enemy", "враг") },
            Get = o => ((UnitData)o).Side, Set = (o, v) => ((UnitData)o).Side = (string)v!,
        },
        new()
        {
            Key = "color", Label = "Оттенок", Kind = FieldKind.Color, Default = "#4D80CC", Section = FieldSections.Appearance,
            Get = o => ((UnitData)o).Color, Set = (o, v) => ((UnitData)o).Color = (string)v!,
        },
        new()
        {
            Key = "scale", Label = "Размер модели", Kind = FieldKind.Number, Default = 1.0, Min = 0.6, Max = 1.4, Percent = true,
            Section = FieldSections.Appearance, Hint = "Только вид: клетку юнит занимает одну.",
            Get = o => ((UnitData)o).Scale, Set = (o, v) => ((UnitData)o).Scale = (double)v!,
        },
    };

    public JsonObject ToJson()
    {
        var skills = new JsonArray();
        foreach (var s in Skills)
        {
            skills.Add(s);
        }

        return new JsonObject
        {
            ["maxHealth"] = MaxHealth,
            ["moveSpeed"] = MoveSpeed,
            ["attackRate"] = AttackRate,
            ["attackPower"] = AttackPower,
            ["attackRange"] = AttackRange,
            ["skills"] = skills,
            ["attackDamageType"] = AttackDamageType,
            ["attackAreaRadius"] = AttackAreaRadius,
            ["attackAreaSplit"] = AttackAreaSplit,
            ["resistances"] = ResistancesJson(),
            ["armor"] = ValuesJson(Armor),
            ["critChance"] = CritChance,
            ["basicAttack"] = BasicAttack,
            ["autoAttackEnabled"] = AutoAttackEnabled,
            ["attackArmorPenetration"] = AttackArmorPenetration,
            ["attackArmorPenetrationPercent"] = AttackArmorPenetrationPercent,
            ["resistanceProfiles"] = Strings(ResistanceProfiles),
            ["defenseRules"] = Strings(DefenseRules),
            ["shields"] = ShieldsJson(),
            ["passives"] = new JsonArray(Passives.Select(p => (JsonNode)p.ToJson()).ToArray()),
            ["species"] = Species,
            ["traits"] = Strings(Traits),
            ["packId"] = PackId,
            ["stats"] = ValuesJson(CustomStats),
            ["side"] = Side,
            ["color"] = Color,
            ["scale"] = Scale,
        };
    }

    public static UnitData Read(JsonObject obj, IssueList issues)
    {
        var data = new UnitData
        {
            MaxHealth = JsonFields.Number(obj, "maxHealth", 100, issues, string.Empty),
            MoveSpeed = JsonFields.Number(obj, "moveSpeed", 1.0, issues, string.Empty),
            AttackRate = JsonFields.Number(obj, "attackRate", 3.0, issues, string.Empty),
            AttackPower = JsonFields.Number(obj, "attackPower", 10, issues, string.Empty),
            AttackRange = JsonFields.Integer(obj, "attackRange", 1, issues, string.Empty),
            Side = JsonFields.Text(obj, "side", "player", issues, string.Empty),
            Color = JsonFields.Text(obj, "color", "#4D80CC", issues, string.Empty),
            Scale = JsonFields.Number(obj, "scale", 1.0, issues, string.Empty),
        };
        data.Skills.AddRange(JsonFields.TextList(obj, "skills", issues, string.Empty));
        data.AttackDamageType = JsonFields.Text(obj, "attackDamageType", "core.damage.physical", issues, string.Empty);
        data.AttackAreaRadius = JsonFields.Integer(obj, "attackAreaRadius", 0, issues, string.Empty);
        data.AttackAreaSplit = JsonFields.Flag(obj, "attackAreaSplit", false, issues, string.Empty);
        if (obj["resistances"] is JsonObject resistances)
        {
            foreach (var (key, node) in resistances)
            {
                if (node is JsonValue v && v.TryGetValue<double>(out var r))
                {
                    data.Resistances[key] = r;
                }
                else
                {
                    issues.Error($"resistances.{key}", "сопротивление — число в процентных пунктах");
                }
            }
        }
        else if (obj["resistances"] is not null)
        {
            issues.Error("resistances", "ожидается объект «тип урона → процент»");
        }

        ReadValues(obj, "armor", data.Armor, issues, "защита A — число очков");
        data.AttackArmorPenetration = JsonFields.Number(obj, "attackArmorPenetration", 0, issues, string.Empty);
        data.CritChance = JsonFields.Number(obj, "critChance", 0, issues, string.Empty);
        data.BasicAttack = JsonFields.Text(obj, "basicAttack", HexTactics.Core.Combat.BasicAttackDefinition.DefaultId, issues, string.Empty);
        data.AutoAttackEnabled = JsonFields.Flag(obj, "autoAttackEnabled", true, issues, string.Empty);
        data.AttackArmorPenetrationPercent = JsonFields.Number(obj, "attackArmorPenetrationPercent", 0, issues, string.Empty);
        data.ResistanceProfiles.AddRange(JsonFields.TextList(obj, "resistanceProfiles", issues, string.Empty));
        data.DefenseRules.AddRange(JsonFields.TextList(obj, "defenseRules", issues, string.Empty));
        if (JsonFields.Array(obj, "shields", issues, string.Empty) is { } shields)
        {
            for (var i = 0; i < shields.Count; i++)
            {
                if (shields[i] is JsonObject so)
                {
                    data.Shields.Add(ShieldData.Read(so, issues, $"shields[{i}]"));
                }
                else
                {
                    issues.Error($"shields[{i}]", "щит должен быть объектом");
                }
            }
        }

        data.Species = JsonFields.Text(obj, "species", string.Empty, issues, string.Empty);
        data.Traits.AddRange(JsonFields.TextList(obj, "traits", issues, string.Empty));
        data.PackId = JsonFields.Text(obj, "packId", string.Empty, issues, string.Empty);
        ReadValues(obj, "stats", data.CustomStats, issues, "характеристика — число");
        if (JsonFields.Array(obj, "passives", issues, string.Empty) is { } passives)
        {
            for (var i = 0; i < passives.Count; i++)
            {
                if (passives[i] is JsonObject po)
                {
                    data.Passives.Add(PassiveData.Read(po, issues, $"passives[{i}]"));
                }
                else
                {
                    issues.Error($"passives[{i}]", "пассив должен быть объектом");
                }
            }
        }

        return data;
    }

    /// <summary>Reads a «layer key → number» object (resistances, defense A); a non-number value is an error.</summary>
    internal static void ReadValues(JsonObject obj, string key, IDictionary<string, double> into, IssueList issues, string message)
    {
        if (obj[key] is JsonObject values)
        {
            foreach (var (k, node) in values)
            {
                if (node is JsonValue v && v.TryGetValue<double>(out var x))
                {
                    into[k] = x;
                }
                else
                {
                    issues.Error($"{key}.{k}", message);
                }
            }
        }
        else if (obj[key] is not null)
        {
            issues.Error(key, "ожидается объект «тип урона → число»");
        }
    }

    internal static JsonObject ValuesJson(IEnumerable<KeyValuePair<string, double>> values)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in values)
        {
            obj[key] = value;
        }

        return obj;
    }

    /// <summary>Errors of a defense A table: finite, 0…1 000 000, a valid layer key (spec part 6 §3.2).</summary>
    internal static void ValidateArmor(IReadOnlyDictionary<string, double> armor, IssueList issues, string path)
    {
        foreach (var (key, value) in armor)
        {
            if (!double.IsFinite(value) || value < MinArmor || value > MaxArmor)
            {
                issues.Error($"{path}.{key}", $"защита A «{key}»: от 0 до 1 000 000 очков, сейчас {value.ToString("0.##", CultureInfo.InvariantCulture)}");
            }
            else if (key != GlobalResistance && !ContentIds.IsValid(key))
            {
                issues.Error($"{path}.{key}", $"неверный ключ защиты «{key}»");
            }
        }
    }

    private static JsonArray Strings(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var v in values)
        {
            array.Add(v);
        }

        return array;
    }

    private JsonArray ShieldsJson()
    {
        var array = new JsonArray();
        foreach (var shield in Shields)
        {
            array.Add(shield.ToJson());
        }

        return array;
    }

    private JsonObject ResistancesJson()
    {
        var obj = new JsonObject();
        foreach (var (key, value) in Resistances)
        {
            obj[key] = value;
        }

        return obj;
    }

    public IEnumerable<ContentRef> References()
    {
        foreach (var s in Skills)
        {
            yield return new ContentRef("skill", s);
        }

        if (AttackDamageType.Length > 0)
        {
            yield return new ContentRef("damage_type", AttackDamageType);
        }

        var types = new HashSet<string>(System.StringComparer.Ordinal) { AttackDamageType };
        foreach (var key in Resistances.Keys.Concat(Armor.Keys))
        {
            if (key != GlobalResistance && types.Add(key))
            {
                yield return new ContentRef("damage_type", key);
            }
        }

        foreach (var p in ResistanceProfiles)
        {
            yield return new ContentRef("resistance_profile", p);
        }

        if (BasicAttack.Length > 0)
        {
            yield return new ContentRef("basic_attack", BasicAttack);
        }

        foreach (var r in DefenseRules)
        {
            yield return new ContentRef("defense_rule", r);
        }

        foreach (var shield in Shields)
        {
            if (shield.DamageType != ShieldData.AnyType && types.Add(shield.DamageType))
            {
                yield return new ContentRef("damage_type", shield.DamageType);
            }
        }

        var rules = new HashSet<string>(DefenseRules, System.StringComparer.Ordinal);
        var other = new HashSet<ContentRef>();
        if (Species.Length > 0 && other.Add(new ContentRef("species", Species)))
        {
            yield return new ContentRef("species", Species);
        }

        foreach (var t in Traits.Distinct(System.StringComparer.Ordinal))
        {
            other.Add(new ContentRef("trait", t));
            yield return new ContentRef("trait", t);
        }

        foreach (var s in CustomStats.Keys)
        {
            if (other.Add(new ContentRef("unit_stat", s)))
            {
                yield return new ContentRef("unit_stat", s);
            }
        }

        foreach (var r in Passives.SelectMany(p => p.References()))
        {
            if ((r.Kind == "damage_type" && types.Add(r.Id)) || (r.Kind == "defense_rule" && rules.Add(r.Id)) || (r.Kind is not ("damage_type" or "defense_rule") && other.Add(r)))
            {
                yield return r;
            }
        }
    }

    public void RemapReferences(IReadOnlyDictionary<ContentRef, string> newIds)
    {
        for (var i = 0; i < Skills.Count; i++)
        {
            if (newIds.TryGetValue(new ContentRef("skill", Skills[i]), out var id))
            {
                Skills[i] = id;
            }
        }

        if (newIds.TryGetValue(new ContentRef("damage_type", AttackDamageType), out var attack))
        {
            AttackDamageType = attack;
        }

        RemapKeys(Resistances, newIds);
        RemapKeys(Armor, newIds);

        for (var i = 0; i < ResistanceProfiles.Count; i++)
        {
            if (newIds.TryGetValue(new ContentRef("resistance_profile", ResistanceProfiles[i]), out var profile))
            {
                ResistanceProfiles[i] = profile;
            }
        }

        for (var i = 0; i < DefenseRules.Count; i++)
        {
            if (newIds.TryGetValue(new ContentRef("defense_rule", DefenseRules[i]), out var rule))
            {
                DefenseRules[i] = rule;
            }
        }

        foreach (var shield in Shields)
        {
            if (newIds.TryGetValue(new ContentRef("damage_type", shield.DamageType), out var type))
            {
                shield.DamageType = type;
            }
        }

        foreach (var p in Passives)
        {
            p.RemapReferences(newIds);
        }

        if (newIds.TryGetValue(new ContentRef("basic_attack", BasicAttack), out var basicAttack))
        {
            BasicAttack = basicAttack;
        }

        if (newIds.TryGetValue(new ContentRef("species", Species), out var species))
        {
            Species = species;
        }

        for (var i = 0; i < Traits.Count; i++)
        {
            if (newIds.TryGetValue(new ContentRef("trait", Traits[i]), out var trait))
            {
                Traits[i] = trait;
            }
        }

        foreach (var key in new List<string>(CustomStats.Keys))
        {
            if (newIds.TryGetValue(new ContentRef("unit_stat", key), out var renamed))
            {
                var value = CustomStats[key];
                CustomStats.Remove(key);
                CustomStats[renamed] = value;
            }
        }
    }

    /// <summary>Moves the values of a «damage type → number» table to the new ids of their types.</summary>
    internal static void RemapKeys(IDictionary<string, double> values, IReadOnlyDictionary<ContentRef, string> newIds)
    {
        foreach (var key in new List<string>(values.Keys))
        {
            if (newIds.TryGetValue(new ContentRef("damage_type", key), out var renamed))
            {
                var value = values[key];
                values.Remove(key);
                values[renamed] = value;
            }
        }
    }

    public void Validate(ValidationContext context)
    {
        foreach (var field in Fields)
        {
            if (field.Check(field.Get!(this), field.Key) is { } issue)
            {
                context.Issues.AddRange(new[] { issue });
            }
        }

        if (Skills.Count > context.Limits.MaxSkillsPerUnit)
        {
            context.Issues.Error("skills", $"не больше {context.Limits.MaxSkillsPerUnit} навыков");
        }

        var seen = new HashSet<string>();
        foreach (var s in Skills)
        {
            if (!seen.Add(s))
            {
                context.Issues.Warning("skills", $"навык «{s}» указан дважды");
            }
        }

        // Spec part 5 §3.1: every resistance −500…+500 %, a finite number; out-of-range values are refused, never clamped silently.
        foreach (var (key, value) in Resistances)
        {
            if (!double.IsFinite(value) || value < MinResistance || value > MaxResistance)
            {
                context.Issues.Error($"resistances.{key}", $"сопротивление «{key}»: от −500 до +500 %, сейчас {value.ToString("0.##", CultureInfo.InvariantCulture)}");
            }
            else if (key != GlobalResistance && !ContentIds.IsValid(key))
            {
                context.Issues.Error($"resistances.{key}", $"неверный ключ сопротивления «{key}»");
            }
        }

        ValidateArmor(Armor, context.Issues, "armor");
        if (ResistanceProfiles.Count > MaxResistanceProfiles)
        {
            context.Issues.Error("resistanceProfiles", $"не больше {MaxResistanceProfiles} наборов сопротивлений");
        }

        if (ResistanceProfiles.Distinct(System.StringComparer.Ordinal).Count() != ResistanceProfiles.Count)
        {
            context.Issues.Warning("resistanceProfiles", "набор сопротивлений указан дважды");
        }

        if (DefenseRules.Count > MaxDefenseRules)
        {
            context.Issues.Error("defenseRules", $"не больше {MaxDefenseRules} правил защиты");
        }

        var rules = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (var r in DefenseRules)
        {
            if (!ContentIds.IsValid(r))
            {
                context.Issues.Error("defenseRules", $"неверная ссылка на правило «{r}»");
            }
            else if (!rules.Add(r))
            {
                context.Issues.Warning("defenseRules", $"правило «{r}» указано дважды");
            }
        }

        if (Species.Length > 0 && !ContentIds.IsValid(Species))
        {
            context.Issues.Error("species", $"неверная ссылка на вид «{Species}»");
        }

        if (Traits.Count > MaxTraits)
        {
            context.Issues.Error("traits", $"не больше {MaxTraits} тегов");
        }

        if (PackId.Length > 40)
        {
            context.Issues.Error("packId", "стая — до 40 символов");
        }

        foreach (var (key, value) in CustomStats)
        {
            if (!ContentIds.IsValid(key) || !double.IsFinite(value))
            {
                context.Issues.Error($"stats.{key}", $"характеристика «{key}»: неверный ключ или значение");
            }
        }

        if (Passives.Count > PassiveData.MaxPerUnit)
        {
            context.Issues.Error("passives", $"не больше {PassiveData.MaxPerUnit} пассивов");
        }

        var passiveIds = new HashSet<string>(System.StringComparer.Ordinal);
        for (var i = 0; i < Passives.Count; i++)
        {
            Passives[i].Validate(context.Issues, $"passives[{i}]");
            if (!passiveIds.Add(Passives[i].Id))
            {
                context.Issues.Error($"passives[{i}].id", $"два пассива с ID «{Passives[i].Id}»");
            }
        }

        if (Shields.Count > MaxShields)
        {
            context.Issues.Error("shields", $"не больше {MaxShields} щитов");
        }

        var names = new HashSet<string>(System.StringComparer.Ordinal);
        for (var i = 0; i < Shields.Count; i++)
        {
            Shields[i].Validate(context.Issues, $"shields[{i}]");
            if (!names.Add(Shields[i].Name.Trim()))
            {
                context.Issues.Error($"shields[{i}].name", $"два щита с названием «{Shields[i].Name}»");
            }
        }
    }

    public string Summary() => string.Create(CultureInfo.InvariantCulture,
        $"{(Side == "enemy" ? "враг" : "игрок")}, {MaxHealth:0.#} HP, {MoveSpeed:0.##} гекс/с, {AttackRate:0.##} удар/с, атака {AttackPower:0.##}, навыков: {Skills.Count}");

    public IContentData Clone()
    {
        var copy = new UnitData
        {
            MaxHealth = MaxHealth, MoveSpeed = MoveSpeed, AttackRate = AttackRate, AttackPower = AttackPower,
            AttackRange = AttackRange, Side = Side, Color = Color, Scale = Scale,
            AttackDamageType = AttackDamageType, AttackAreaRadius = AttackAreaRadius, AttackAreaSplit = AttackAreaSplit,
            AttackArmorPenetration = AttackArmorPenetration, AttackArmorPenetrationPercent = AttackArmorPenetrationPercent, CritChance = CritChance,
            BasicAttack = BasicAttack, AutoAttackEnabled = AutoAttackEnabled,
        };
        foreach (var (key, value) in Armor)
        {
            copy.Armor[key] = value;
        }

        copy.Skills.AddRange(Skills);
        foreach (var (key, value) in Resistances)
        {
            copy.Resistances[key] = value;
        }

        copy.ResistanceProfiles.AddRange(ResistanceProfiles);
        copy.DefenseRules.AddRange(DefenseRules);
        foreach (var shield in Shields)
        {
            copy.Shields.Add(shield.Clone());
        }

        copy.Passives.AddRange(Passives.Select(p => p.Clone()));
        copy.Species = Species;
        copy.Traits.AddRange(Traits);
        copy.PackId = PackId;
        foreach (var (key, value) in CustomStats)
        {
            copy.CustomStats[key] = value;
        }

        return copy;
    }
}

/// <summary>
/// A shield of a unit definition (spec part 5 §4.5): name, capacity, the damage it accepts (a type, a family or any) and
/// its order among the unit's shields. A shield takes calculated damage before health; it is never negative or infinite.
/// </summary>
public sealed class ShieldData
{
    public const string AnyType = "any";
    public const double MaxCapacity = 100000;

    public string Name { get; set; } = "Щит";

    public double Capacity { get; set; } = 20;

    public string DamageType { get; set; } = AnyType;

    public int Order { get; set; }

    public JsonObject ToJson() => new()
    {
        ["name"] = Name,
        ["capacity"] = Capacity,
        ["damageTypeId"] = DamageType,
        ["order"] = Order,
    };

    public static ShieldData Read(JsonObject obj, IssueList issues, string path) => new()
    {
        Name = JsonFields.Text(obj, "name", "Щит", issues, path),
        Capacity = JsonFields.Number(obj, "capacity", 20, issues, path),
        DamageType = JsonFields.Text(obj, "damageTypeId", AnyType, issues, path) is { Length: > 0 } t ? t : AnyType,
        Order = JsonFields.Integer(obj, "order", 0, issues, path),
    };

    public void Validate(IssueList issues, string path)
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 40)
        {
            issues.Error($"{path}.name", "у щита нужно название до 40 символов");
        }

        if (!double.IsFinite(Capacity) || Capacity <= 0 || Capacity > MaxCapacity)
        {
            issues.Error($"{path}.capacity", $"прочность щита — от 0 до {MaxCapacity:0} HP (не отрицательная и не бесконечная)");
        }

        if (DamageType != AnyType && !ContentIds.IsValid(DamageType))
        {
            issues.Error($"{path}.damageTypeId", $"неверный тип урона щита «{DamageType}»");
        }

        if (Order is < 0 or > 100)
        {
            issues.Error($"{path}.order", "порядок щита — от 0 до 100");
        }
    }

    public ShieldData Clone() => new() { Name = Name, Capacity = Capacity, DamageType = DamageType, Order = Order };
}
