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
        return data;
    }

    public IEnumerable<ContentRef> References()
    {
        foreach (var s in Skills)
        {
            yield return new ContentRef("skill", s);
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
    }

    public string Summary() => string.Create(CultureInfo.InvariantCulture,
        $"{(Side == "enemy" ? "враг" : "игрок")}, {MaxHealth:0.#} HP, {MoveSpeed:0.##} гекс/с, {AttackRate:0.##} удар/с, атака {AttackPower:0.##}, навыков: {Skills.Count}");

    public IContentData Clone()
    {
        var copy = new UnitData
        {
            MaxHealth = MaxHealth, MoveSpeed = MoveSpeed, AttackRate = AttackRate, AttackPower = AttackPower,
            AttackRange = AttackRange, Side = Side, Color = Color, Scale = Scale,
        };
        copy.Skills.AddRange(Skills);
        return copy;
    }
}
