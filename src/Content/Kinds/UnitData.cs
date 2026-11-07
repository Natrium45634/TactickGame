using System.Collections.Generic;
using System.Globalization;
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

        return data;
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

        foreach (var key in Resistances.Keys)
        {
            if (key != GlobalResistance && key != AttackDamageType)
            {
                yield return new ContentRef("damage_type", key);
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

        foreach (var key in new List<string>(Resistances.Keys))
        {
            if (newIds.TryGetValue(new ContentRef("damage_type", key), out var renamed))
            {
                var value = Resistances[key];
                Resistances.Remove(key);
                Resistances[renamed] = value;
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
        };
        copy.Skills.AddRange(Skills);
        foreach (var (key, value) in Resistances)
        {
            copy.Resistances[key] = value;
        }

        return copy;
    }
}
