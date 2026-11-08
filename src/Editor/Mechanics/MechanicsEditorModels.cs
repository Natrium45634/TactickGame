using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Model;
using HexTactics.Content.Registry;
using HexTactics.Content.Schema;
using HexTactics.Editor.Definitions;
using HexTactics.Editor.Units;

namespace HexTactics.Editor.Mechanics;

/// <summary>
/// Model of the damage type constructor (spec part 5 §6.2): a new type starts «на основе физического / магического / чистого /
/// нового настраиваемого семейства»; name, colour and icon are the short path, family, strategy and tags are «Подробнее».
/// A subtype always has its family's strategy (never guessed from the name); a family chooses it explicitly.
/// </summary>
public sealed class DamageTypeEditorModel : DefinitionEditorModel
{
    public const string BasePhysical = "physical";
    public const string BaseMagical = "magical";
    public const string BaseTrue = "true";
    public const string BaseFamily = "family";

    private readonly string _savedStrategy;

    public DamageTypeEditorModel(ContentLibrary library, ContentItem item)
        : base(library, item)
    {
        if (item.Data is not DamageTypeData d)
        {
            throw new ArgumentException("Not a damage type.", nameof(item));
        }

        _savedStrategy = item.Revision == 0 ? d.Strategy : ((DamageTypeData)(library.Find(item.Ref)?.Data ?? d)).Strategy;
    }

    public DamageTypeData Type => (DamageTypeData)Item.Data;

    public static IReadOnlyList<FieldOption> Bases { get; } = new[]
    {
        new FieldOption(BasePhysical, "на основе физического (подтип)"),
        new FieldOption(BaseMagical, "на основе магического (подтип)"),
        new FieldOption(BaseTrue, "на основе чистого (обходит R)"),
        new FieldOption(BaseFamily, "новое настраиваемое семейство"),
    };

    /// <summary>A new, unsaved type of the chosen base.</summary>
    public static ContentItem CreateFrom(ContentLibrary library, string baseId, string name)
    {
        var item = library.Create(ContentTypeRegistry.DamageType, name);
        var d = (DamageTypeData)item.Data;
        (d.Family, d.Strategy) = baseId switch
        {
            BaseMagical => ("core.damage.magical", DamageTypeData.ResistanceAffected),
            BaseTrue => ("core.damage.true", DamageTypeData.TrueLike),
            BaseFamily => (string.Empty, DamageTypeData.ResistanceAffected),
            _ => ("core.damage.physical", DamageTypeData.ResistanceAffected),
        };
        d.Color = baseId switch { BaseMagical => "#9C8CE0", BaseTrue => "#F0F0F0", BaseFamily => "#70B0A0", _ => "#B8B8B8" };
        return item;
    }

    /// <summary>Families a subtype can belong to (not itself, not legacy types).</summary>
    public IReadOnlyList<FieldOption> Families()
    {
        var list = new List<FieldOption> { new(string.Empty, "— самостоятельное семейство —") };
        list.AddRange(Library.Items(ContentTypeRegistry.DamageType)
            .Where(t => t.Id != Item.Id && t.Data is DamageTypeData { Family.Length: 0, Legacy: false })
            .OrderBy(t => t.Id.StartsWith("core.", StringComparison.Ordinal) ? 0 : 1).ThenBy(t => t.Name, StringComparer.CurrentCulture)
            .Select(t => new FieldOption(t.Id, t.Name)));
        return list;
    }

    /// <summary>Moves the type into a family (taking its strategy) or makes it a family of its own.</summary>
    public string? SetFamily(string familyId)
    {
        if (familyId.Length > 0)
        {
            if (Library.Find(ContentTypeRegistry.DamageType, familyId)?.Data is not DamageTypeData { Family.Length: 0 } family || familyId == Item.Id)
            {
                return "семейством может быть только тип-семейство";
            }

            if (Library.Items(ContentTypeRegistry.DamageType).Any(t => t.Data is DamageTypeData d && d.Family == Item.Id))
            {
                return "у этого типа есть свои подтипы — он должен остаться семейством";
            }

            Apply($"Семейство: {Library.Find(ContentTypeRegistry.DamageType, familyId)!.Name}", (d, _) =>
            {
                ((DamageTypeData)d).Family = familyId;
                ((DamageTypeData)d).Strategy = family.Strategy;
            });
            return null;
        }

        Apply("Самостоятельное семейство", (d, _) => ((DamageTypeData)d).Family = string.Empty);
        return null;
    }

    /// <summary>The strategy of a family (spec part 5 §6.2: always explicit); a subtype inherits it.</summary>
    public string? SetStrategy(string strategy)
    {
        if (Type.Family.Length > 0)
        {
            return "стратегию подтипа задаёт его семейство";
        }

        if (strategy is not (DamageTypeData.ResistanceAffected or DamageTypeData.TrueLike))
        {
            return $"неизвестная стратегия «{strategy}»";
        }

        if (strategy != Type.Strategy && Library.Items(ContentTypeRegistry.DamageType).Any(t => t.Data is DamageTypeData d && d.Family == Item.Id))
        {
            return "у семейства есть подтипы — их расчёт не меняется молча; создайте новое семейство";
        }

        Apply("Стратегия расчёта", (d, _) => ((DamageTypeData)d).Strategy = strategy);
        return null;
    }

    public string? SetColor(string color)
    {
        if (!FieldSchema.IsColor(color))
        {
            return "ожидается цвет #RRGGBB";
        }

        Apply("Цвет", (d, _) => ((DamageTypeData)d).Color = color);
        return null;
    }

    public string? SetIcon(string icon)
    {
        icon = icon.Trim();
        if (new StringInfo(icon).LengthInTextElements > 4)
        {
            return "значок — до 4 символов";
        }

        Apply("Значок", (d, _) => ((DamageTypeData)d).Icon = icon);
        return null;
    }

    /// <summary>The «Элемент» category of a user element (spec part 6 §2.2): grouping and labels only, never the calculation.</summary>
    public void SetElement(bool on) => Apply(on ? "Категория: элемент" : "Без категории «элемент»", (d, _) =>
    {
        var c = ((DamageTypeData)d).Classification;
        c.Remove(DamageTypeData.Element);
        if (on)
        {
            c.Insert(0, DamageTypeData.Element);
        }
    });

    /// <summary>Tags from a comma separated text («fire, plasma»).</summary>
    public string? SetTags(string text)
    {
        var tags = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();
        if (tags.Count > 16 || tags.Any(t => t.Length > 32))
        {
            return "не больше 16 тегов до 32 символов";
        }

        Apply("Теги", (d, _) =>
        {
            ((DamageTypeData)d).Tags.Clear();
            ((DamageTypeData)d).Tags.AddRange(tags);
        });
        return null;
    }

    /// <summary>Library objects that refer to this type (units, skills, tiles, rules, sets, subtypes).</summary>
    public IReadOnlyList<ContentItem> Dependents() => Library.UsedBy(Item.Ref)
        .OrderBy(i => i.Kind.Order).ThenBy(i => i.Name, StringComparer.CurrentCulture)
        .ToList();

    public IReadOnlyList<string> Preview()
    {
        var lines = new List<string>();
        var family = Type.Family.Length == 0 ? null : Library.Find(ContentTypeRegistry.DamageType, Type.Family);
        lines.Add(family is null ? "Самостоятельное семейство." : $"Подтип семейства «{family.Name}».");
        lines.Add(Type.Strategy == DamageTypeData.TrueLike
            ? "Расчёт: численные сопротивления и плоское снижение не действуют; явный иммунитет и щиты — действуют."
            : $"Расчёт: урон × max(0, 1 − R/100), R = все типы + «{family?.Name ?? Item.Name}»" + (family is null ? string.Empty : $" + «{Item.Name}»") + " (+ временные), от −500 до +500 %.");
        if (Type.Tags.Count > 0)
        {
            lines.Add("Теги: " + string.Join(", ", Type.Tags) + " — по ним можно фильтровать правила защиты.");
        }

        var users = Dependents();
        lines.Add(users.Count == 0
            ? "Пока нигде не используется."
            : $"Используют: {users.Count} — " + string.Join(", ", users.Take(8).Select(u => $"{u.Kind.Singular} «{u.Name}»")) + (users.Count > 8 ? " …" : string.Empty) + ".");
        if (Type.Strategy != _savedStrategy && users.Count > 0)
        {
            lines.Add($"⚠ Смена стратегии изменит расчёт у {users.Count} объектов — после сохранения (новая ревизия) проверьте их «Проверить на ударе».");
        }

        lines.Add("Тип сам появится в списках типов урона юнитов, навыков, гексов, правил защиты и наборов сопротивлений.");
        return lines;
    }
}

/// <summary>
/// Model of the resistance set editor (spec part 5 §6.1, §6.4): own values over an optional parent set; the table shows what is
/// inherited and what is the set's own; parents that would form a cycle are not offered and are refused.
/// </summary>
public sealed class ResistanceProfileEditorModel : DefinitionEditorModel
{
    public ResistanceProfileEditorModel(ContentLibrary library, ContentItem item)
        : base(library, item)
    {
        if (item.Data is not ResistanceProfileData)
        {
            throw new ArgumentException("Not a resistance set.", nameof(item));
        }
    }

    public ResistanceProfileData Profile => (ResistanceProfileData)Item.Data;

    /// <summary>The parent's values (inherited) under the set's own values.</summary>
    public IReadOnlyDictionary<string, ResistanceValue> Layered
    {
        get
        {
            var result = new SortedDictionary<string, ResistanceValue>(StringComparer.Ordinal);
            if (Profile.Parent.Length > 0)
            {
                foreach (var (key, value) in ResistanceLayers.Of(Library, Profile.Parent))
                {
                    result[key] = new ResistanceValue(value, Profile.Parent, false);
                }
            }

            foreach (var (key, value) in Profile.Values)
            {
                result[key] = new ResistanceValue(value, null, true);
            }

            return result;
        }
    }

    public IReadOnlyList<ResistanceRow> Rows() => ResistanceTable.Rows(Library, Layered);

    public double Total(string typeId) => ResistanceTable.Total(Library, Layered, typeId);

    public ContentIssue? SetValue(string key, object? input)
    {
        if (ResistanceTable.Parse(Library, key, input, $"values.{key}", out var rounded) is { } issue)
        {
            return issue;
        }

        var inherited = Profile.Parent.Length > 0 && ResistanceLayers.Of(Library, Profile.Parent).ContainsKey(key);
        Apply($"«{ResistanceTable.KeyName(Library, key)}»: {UnitEditorModel.Explain(rounded)}", (d, _) =>
        {
            var p = (ResistanceProfileData)d;
            if (rounded == 0 && !inherited)
            {
                p.Values.Remove(key);
            }
            else
            {
                p.Values[key] = rounded;
            }
        });
        return null;
    }

    public void Reset(string key) => Apply($"Сбросить «{ResistanceTable.KeyName(Library, key)}»", (d, _) => ((ResistanceProfileData)d).Values.Remove(key));

    /// <summary>Sets that can be the parent: not this one and not any set that already extends this one.</summary>
    public IReadOnlyList<FieldOption> Parents()
    {
        var list = new List<FieldOption> { new(string.Empty, "— без основы —") };
        list.AddRange(Library.Items(ContentTypeRegistry.ResistanceProfile)
            .Where(p => p.Id != Item.Id && !Extends(p.Id, Item.Id))
            .Select(p => new FieldOption(p.Id, p.Name)));
        return list;
    }

    public string? SetParent(string parentId)
    {
        if (parentId.Length > 0 && (parentId == Item.Id || Extends(parentId, Item.Id)))
        {
            return "циклическое наследование наборов запрещено";
        }

        if (parentId.Length > 0 && Library.Find(ContentTypeRegistry.ResistanceProfile, parentId) is null)
        {
            return $"набор «{parentId}» не найден";
        }

        Apply("Основа набора", (d, _) => ((ResistanceProfileData)d).Parent = parentId);
        return null;
    }

    /// <summary>True if <paramref name="profileId"/> extends <paramref name="ancestor"/> through its parents.</summary>
    private bool Extends(string profileId, string ancestor)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var id = profileId;
        while (id.Length > 0 && seen.Add(id) && Library.Find(ContentTypeRegistry.ResistanceProfile, id)?.Data is ResistanceProfileData p)
        {
            if (p.Parent == ancestor)
            {
                return true;
            }

            id = p.Parent;
        }

        return false;
    }

    /// <summary>Units that apply the set and sets built on it.</summary>
    public IReadOnlyList<ContentItem> Users() => Library.UsedBy(Item.Ref);

    public IReadOnlyList<string> Preview()
    {
        var lines = new List<string>();
        var strong = new List<string>();
        var weak = new List<string>();
        foreach (var t in Library.Items(ContentTypeRegistry.DamageType))
        {
            if (t.Data is not DamageTypeData { Legacy: false, Strategy: DamageTypeData.ResistanceAffected })
            {
                continue;
            }

            var r = Total(t.Id);
            var text = $"{t.Name.ToLowerInvariant()} {r.ToString("+0.##;−0.##", CultureInfo.InvariantCulture)} %";
            (r > 0 ? strong : r < 0 ? weak : null)?.Add(text);
        }

        lines.Add(strong.Count == 0 && weak.Count == 0 ? "Набор ничего не меняет." : $"Сильные стороны: {(strong.Count == 0 ? "нет" : string.Join(", ", strong))}; слабости: {(weak.Count == 0 ? "нет" : string.Join(", ", weak))}.");
        lines.Add("Набор — только данные: юнит применяет его ссылкой, свои значения юнита его перекрывают; изменения действуют на всех после сохранения.");
        var users = Users();
        lines.Add(users.Count == 0 ? "Пока не применён." : $"Применяют: {string.Join(", ", users.Select(u => $"{u.Kind.Singular} «{u.Name}»"))}.");
        return lines;
    }
}
