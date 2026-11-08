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
    public IReadOnlyDictionary<string, ResistanceValue> Layered => LayeredOf(armor: false);

    /// <summary>The parent's defense A under the set's own A (spec part 6 §3.2): the same layering as R.</summary>
    public IReadOnlyDictionary<string, ResistanceValue> LayeredArmor => LayeredOf(armor: true);

    private IReadOnlyDictionary<string, ResistanceValue> LayeredOf(bool armor)
    {
        var result = new SortedDictionary<string, ResistanceValue>(StringComparer.Ordinal);
        if (Profile.Parent.Length > 0)
        {
            foreach (var (key, value) in ResistanceLayers.Of(Library, Profile.Parent, armor))
            {
                result[key] = new ResistanceValue(value, Profile.Parent, false);
            }
        }

        foreach (var (key, value) in Profile.Table(armor))
        {
            result[key] = new ResistanceValue(value, null, true);
        }

        return result;
    }

    public IReadOnlyList<ResistanceRow> Rows() => ResistanceTable.Rows(Library, Layered, LayeredArmor);

    public double Total(string typeId) => ResistanceTable.Total(Library, Layered, typeId);

    /// <summary>A_raw of the set against a type (global + family + subtype, spec part 6 §3.2).</summary>
    public double ArmorTotal(string typeId) => ResistanceTable.ArmorTotal(Library, LayeredArmor, typeId);

    public ContentIssue? SetArmor(string key, object? input)
    {
        if (ResistanceTable.ParseArmor(Library, key, input, $"armor.{key}", out var value) is { } issue)
        {
            return issue;
        }

        var inherited = Profile.Parent.Length > 0 && ResistanceLayers.Of(Library, Profile.Parent, armor: true).ContainsKey(key);
        Apply($"Защита A «{ResistanceTable.KeyName(Library, key)}»: {UnitEditorModel.ExplainArmor(value)}", (d, _) =>
        {
            var p = (ResistanceProfileData)d;
            if (value == 0 && !inherited)
            {
                p.Armor.Remove(key);
            }
            else
            {
                p.Armor[key] = value;
            }
        });
        return null;
    }

    public void ResetArmor(string key) => Apply($"Сбросить защиту A «{ResistanceTable.KeyName(Library, key)}»", (d, _) => ((ResistanceProfileData)d).Armor.Remove(key));

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

        var armor = Rows().Where(r => r.Armor != 0).Select(r => $"{(r.Key == UnitData.GlobalResistance ? "все типы" : r.Label.ToLowerInvariant())} {r.Armor.ToString("0.##", CultureInfo.InvariantCulture)}").ToList();
        lines.Add(strong.Count == 0 && weak.Count == 0
            ? armor.Count == 0 ? "Набор ничего не меняет." : "Сопротивлений нет."
            : $"Сильные стороны: {(strong.Count == 0 ? "нет" : string.Join(", ", strong))}; слабости: {(weak.Count == 0 ? "нет" : string.Join(", ", weak))}.");
        if (armor.Count > 0)
        {
            lines.Add($"Защита A: {string.Join(", ", armor)} — после сопротивления урон × K / (K + A), при K = 100 и A = 100 вдвое меньше.");
        }

        lines.Add("Набор — только данные: юнит применяет его ссылкой, свои значения юнита его перекрывают; изменения действуют на всех после сохранения.");
        var users = Users();
        lines.Add(users.Count == 0 ? "Пока не применён." : $"Применяют: {string.Join(", ", users.Select(u => $"{u.Kind.Singular} «{u.Name}»"))}.");
        return lines;
    }
}

/// <summary>
/// The combat ruleset constructor (spec part 6 §3.2, §9.5): K of the defense formula and optional K per family, with a
/// table of what A means for the damage and the maps that play with the set.
/// </summary>
public sealed class CombatRulesetEditorModel : DefinitionEditorModel
{
    /// <summary>Defense values shown in the preview table.</summary>
    public static readonly double[] PreviewArmor = { 0, 25, 50, 100, 200, 400 };

    public CombatRulesetEditorModel(ContentLibrary library, ContentItem item)
        : base(library, item)
    {
        if (item.Data is not CombatRulesetData)
        {
            throw new ArgumentException("Not a combat ruleset.", nameof(item));
        }
    }

    public CombatRulesetData Ruleset => (CombatRulesetData)Item.Data;

    /// <summary>The rules this set gives the damage pipeline (unsaved edits included).</summary>
    public HexTactics.Core.Combat.CombatRules Rules => HexTactics.Content.Runtime.RuntimeFactory.BuildCombatRules(Library, Item.Id, Item);

    /// <summary>Damage families that can get an own K: resistance-affected families without one yet.</summary>
    public IReadOnlyList<FieldOption> FamilyChoices() => Library.Items(ContentTypeRegistry.DamageType)
        .Where(t => t.Data is DamageTypeData { Family.Length: 0, Strategy: DamageTypeData.ResistanceAffected, Legacy: false } && !t.Archived && !Ruleset.DefenseKByFamily.ContainsKey(t.Id))
        .Select(t => new FieldOption(t.Id, t.Name))
        .ToList();

    public ContentIssue? SetDefenseK(object? input) => TrySetField(CombatRulesetData.Fields.Single(f => f.Key == "defenseK"), input);

    /// <summary>Gives a family its own K (from input), replacing the common one for its types.</summary>
    public ContentIssue? SetFamilyK(string familyId, object? input)
    {
        if (Library.Find(ContentTypeRegistry.DamageType, familyId)?.Data is not DamageTypeData { Family.Length: 0 } family)
        {
            return ContentIssue.Error($"defenseKByFamily.{familyId}", $"«{familyId}» — не семейство типов урона");
        }

        if (family.Strategy != DamageTypeData.ResistanceAffected)
        {
            return ContentIssue.Error($"defenseKByFamily.{familyId}", "это семейство обходит защиту A — свой K не действовал бы");
        }

        var text = input switch { double d => d.ToString(CultureInfo.InvariantCulture), string s => s, _ => string.Empty };
        if (!double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var k) || !double.IsFinite(k) ||
            k < HexTactics.Core.Combat.CombatRules.MinDefenseK || k > HexTactics.Core.Combat.CombatRules.MaxDefenseK)
        {
            return ContentIssue.Error($"defenseKByFamily.{familyId}", $"K: больше 0 и не больше 1 000 000, а не «{input}» — значение не изменено");
        }

        var name = Library.Find(ContentTypeRegistry.DamageType, familyId)!.Name;
        Apply($"K для «{name}» = {k.ToString("0.##", CultureInfo.InvariantCulture)}", (d, _) => ((CombatRulesetData)d).DefenseKByFamily[familyId] = k);
        return null;
    }

    // ------------------------------------------------------------------ surprise and crits (spec part 6 §6, §7, §9.5)

    public void SetSurpriseEnabled(bool on) => Apply(on ? "Внезапность включена" : "Внезапность выключена", (d, _) => ((CombatRulesetData)d).SurpriseEnabled = on);

    public void SetCritEnabled(bool on) => Apply(on ? "Криты включены" : "Криты выключены", (d, _) => ((CombatRulesetData)d).CritEnabled = on);

    /// <summary>
    /// Sets the surprise chance of a side (0…5 clockwise from the front); <paramref name="symmetric"/> sets the mirrored side
    /// too (front right ↔ front left, rear right ↔ rear left), as the symmetric preset requires (spec part 6 §6.1).
    /// </summary>
    public ContentIssue? SetSurpriseChance(int sector, object? input, bool symmetric)
    {
        if (sector is < 0 or > 5 || !TryNumber(input, 0, 100, out var v))
        {
            return ContentIssue.Error("surpriseChances", $"шанс внезапности — число от 0 до 100 %, а не «{input}»");
        }

        Apply($"Внезапность: {HexTactics.Core.Combat.SurpriseMath.SectorNames[sector]} {v.ToString("0.##", CultureInfo.InvariantCulture)} %", (d, _) =>
        {
            var chances = (double[])((CombatRulesetData)d).SurpriseChances.Clone();
            if (chances.Length != 6)
            {
                chances = new double[6];
            }

            chances[sector] = v;
            if (symmetric && sector is not (0 or 3))
            {
                chances[6 - sector] = v;
            }

            ((CombatRulesetData)d).SurpriseChances = chances;
        });
        return null;
    }

    /// <summary>Sets «surpriseMultiplier», «baseCritChance», «critMultiplier» or «superCritMultiplier» from input.</summary>
    public ContentIssue? SetValue(string key, object? input)
    {
        var (min, max) = key == "baseCritChance" ? (-1000.0, 1000.0) : (0.0, 100.0);
        if (!TryNumber(input, min, max, out var v))
        {
            return ContentIssue.Error(key, $"значение от {min.ToString(CultureInfo.InvariantCulture)} до {max.ToString(CultureInfo.InvariantCulture)}, а не «{input}» — не изменено");
        }

        Apply($"Правила боя: {key}", (d, _) =>
        {
            var r = (CombatRulesetData)d;
            switch (key)
            {
                case "surpriseMultiplier": r.SurpriseMultiplier = v; break;
                case "baseCritChance": r.BaseCritChance = v; break;
                case "critMultiplier": r.CritMultiplier = v; break;
                case "superCritMultiplier": r.SuperCritMultiplier = v; break;
            }
        });
        return null;
    }

    private static bool TryNumber(object? input, double min, double max, out double value)
    {
        value = 0;
        var text = input switch { double d => d.ToString(CultureInfo.InvariantCulture), string s => s, _ => string.Empty };
        return double.TryParse(text.Trim().Replace(',', '.').TrimEnd('%').Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value) && value >= min && value <= max;
    }

    /// <summary>The probabilities at the reference chances (spec part 6 §7.1 table, §9.5): «C 125 %: обычный 0 %, крит 75 %, сверхкрит 25 %».</summary>
    public static IReadOnlyList<string> CritTable() => new[] { 0.0, 35, 75, 100, 125, 175, 200, 250 }.Select(c =>
    {
        string P(double p) => (p * 100).ToString("0.##", CultureInfo.InvariantCulture) + " %";
        var super = HexTactics.Core.Combat.CritMath.SuperChance(c);
        var crit = HexTactics.Core.Combat.CritMath.CritOnlyChance(c);
        return $"C {c.ToString("0", CultureInfo.InvariantCulture)} %: обычный {P(1 - crit - super)}, крит {P(crit)}, сверхкрит {P(super)}" + (c > 200 ? " (избыток сверх 200 % сохраняется, но не усиливает)" : string.Empty);
    }).ToList();

    public void RemoveFamilyK(string familyId) =>
        Apply($"Общий K для «{Library.Find(ContentTypeRegistry.DamageType, familyId)?.Name ?? familyId}»", (d, _) => ((CombatRulesetData)d).DefenseKByFamily.Remove(familyId));

    /// <summary>Maps that play with this set.</summary>
    public IReadOnlyList<ContentItem> Users() => Library.UsedBy(Item.Ref);

    public IReadOnlyList<string> Preview()
    {
        string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        var k = Ruleset.DefenseK;
        var lines = new List<string>
        {
            $"Урон после сопротивления × K / (K + A), K = {N(k)}.",
            "При K = " + N(k) + ": " + string.Join(", ", PreviewArmor.Select(a => $"A {N(a)} → ×{N(HexTactics.Core.Combat.ArmorMath.Multiplier(a, k))}")) + ".",
        };
        foreach (var (family, fk) in Ruleset.DefenseKByFamily)
        {
            lines.Add($"{Library.Find(ContentTypeRegistry.DamageType, family)?.Name ?? family}: свой K = {N(fk)} (A 100 → ×{N(HexTactics.Core.Combat.ArmorMath.Multiplier(100, fk))}).");
        }

        lines.Add("Чистый урон защиту A не учитывает. У юнитов без защиты A (старый контент) урон не меняется при любом K.");
        var r = Ruleset;
        lines.Add(r.SurpriseEnabled
            ? "Внезапность: " + string.Join(", ", r.SurpriseChances.Select((c, i) => $"{HexTactics.Core.Combat.SurpriseMath.SectorNames[i]} {N(c)} %")) + $"; удача — ×{N(r.SurpriseMultiplier)}. Сторона — относительно логического взгляда цели."
            : "Внезапность выключена.");
        lines.Add(r.CritEnabled
            ? $"Криты: шанс атакующего + {N(r.BaseCritChance)} п.п.; крит ×{N(r.CritMultiplier)}, сверхкрит ×{N(r.SuperCritMultiplier)}. Внезапность и крит независимы, множители перемножаются."
            : "Криты выключены.");
        var users = Users();
        lines.Add(users.Count == 0
            ? Item.Id == CombatRulesetData.Legacy ? "Используют все карты без своих правил боя (созданные до ТЗ ч.6)." : "Пока не назначены ни одной карте."
            : $"Используют: {string.Join(", ", users.Select(u => $"{u.Kind.Singular} «{u.Name}»"))}.");
        return lines;
    }
}

/// <summary>A registry characteristic (spec part 6 §5.7): unit label, base value, range and rounding, with its users.</summary>
public sealed class UnitStatEditorModel : DefinitionEditorModel
{
    public UnitStatEditorModel(ContentLibrary library, ContentItem item)
        : base(library, item)
    {
        if (item.Data is not UnitStatData)
        {
            throw new ArgumentException("Not a characteristic.", nameof(item));
        }
    }

    public UnitStatData Stat => (UnitStatData)Item.Data;

    /// <summary>Sets «unit», «base», «min», «max» or «decimals» from input; a refusal leaves the value as it was.</summary>
    public ContentIssue? Set(string key, object? input)
    {
        var probe = (UnitStatData)Stat.Clone();
        var text = (input as string ?? Convert.ToString(input, CultureInfo.InvariantCulture) ?? string.Empty).Trim();
        if (key == "unit")
        {
            probe.Unit = text;
        }
        else if (!double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v))
        {
            return ContentIssue.Error(key, $"«{input}» — не число, значение не изменено");
        }
        else
        {
            switch (key)
            {
                case "base": probe.BaseValue = v; break;
                case "min": probe.Min = v; break;
                case "max": probe.Max = v; break;
                case "decimals": probe.Decimals = (int)Math.Round(v); break;
                default: return ContentIssue.Error(key, "нет такого поля");
            }
        }

        var issues = new IssueList();
        probe.Validate(new ValidationContext(issues, Library.Registry.Effects, Library.Limits));
        if (issues.Items.FirstOrDefault(i => i.IsError) is { } issue)
        {
            return issue with { Message = issue.Message + " — значение не изменено" };
        }

        Apply($"Характеристика: {key}", (d, _) =>
        {
            var s = (UnitStatData)d;
            (s.Unit, s.BaseValue, s.Min, s.Max, s.Decimals) = (probe.Unit, probe.BaseValue, probe.Min, probe.Max, probe.Decimals);
        });
        return null;
    }

    public IReadOnlyList<string> Preview()
    {
        string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        var users = Library.UsedBy(Item.Ref);
        return new[]
        {
            $"Каждый юнит имеет {Item.Name.ToLowerInvariant()} {N(Stat.BaseValue)} {Stat.Unit}, если у него нет своего значения; диапазон {N(Stat.Min)}…{N(Stat.Max)}, знаков после запятой: {Stat.Decimals}.",
            "Пассивы и теги меняют её: база × (1 + %) + число. На бой она влияет только через механику, которая её читает; в этой редакции таких нет — значение видно и меняется, но ничего не ускоряет и не даёт уклонения.",
            users.Count == 0 ? "Пока не используется." : $"Используют: {string.Join(", ", users.Select(u => $"{u.Kind.Singular} «{u.Name}»"))}.",
        };
    }
}

/// <summary>A node of the species tree (spec part 6 §5.2): parent (no cycle), effects with inheritance, lineage and users.</summary>
public sealed class SpeciesEditorModel : DefinitionEditorModel
{
    public SpeciesEditorModel(ContentLibrary library, ContentItem item)
        : base(library, item)
    {
        if (item.Data is not SpeciesData)
        {
            throw new ArgumentException("Not a species.", nameof(item));
        }
    }

    public SpeciesData Species => (SpeciesData)Item.Data;

    /// <summary>Possible parents: every species except this one and its descendants (no cycle).</summary>
    public IReadOnlyList<FieldOption> Parents()
    {
        var list = new List<FieldOption> { new(string.Empty, "— корень дерева —") };
        list.AddRange(Library.Items(ContentTypeRegistry.Species)
            .Where(sp => sp.Id != Item.Id && !HexTactics.Content.Runtime.PassiveEffects.IsA(Library, sp.Id, Item.Id))
            .Select(sp => new FieldOption(sp.Id, sp.Name)));
        return list;
    }

    public string? SetParent(string parentId)
    {
        if (parentId.Length > 0 && (parentId == Item.Id || HexTactics.Content.Runtime.PassiveEffects.IsA(Library, parentId, Item.Id)))
        {
            return "цикл в дереве видов запрещён: родитель не может быть потомком";
        }

        if (parentId.Length > 0 && Library.Find(ContentTypeRegistry.Species, parentId) is null)
        {
            return $"вид «{parentId}» не найден";
        }

        Apply("Родитель вида", (d, _) => ((SpeciesData)d).Parent = parentId);
        return null;
    }

    public void SetIcon(string icon) => Apply("Значок вида", (d, _) => ((SpeciesData)d).Icon = icon.Trim());

    public (string? Id, string? Refusal) AddEffect(string operation) => AddEffect(d => ((SpeciesData)d).Effects, operation, "Эффект");

    public void RemoveEffect(string id) => RemoveEffect(d => ((SpeciesData)d).Effects, id, "эффект");

    public ContentIssue? SetEffect(string id, string key, object? input) => SetEffect(d => ((SpeciesData)d).Effects, "effects", id, key, input);

    public IReadOnlyList<HexTactics.Editor.Units.PassiveCard> EffectCards() => EffectCards(Species.Effects);

    /// <summary>«Лунный волк → Волк → Зверь»: this species (unsaved parent included) and its ancestors.</summary>
    public string Lineage()
    {
        var names = new List<string> { Item.Name };
        names.AddRange(HexTactics.Content.Runtime.PassiveEffects.SpeciesChain(Library, Species.Parent).Select(i => i.Name));
        return string.Join(" → ", names);
    }

    public IReadOnlyList<string> Preview()
    {
        var probe = new UnitData { Species = Item.Id };
        var overlay = Library.Overlay(new[] { Item });
        var active = HexTactics.Content.Runtime.PassiveEffects.Active(probe, overlay);
        var lines = new List<string> { $"Линия: {Lineage()} — юнит этого вида считается каждым из них." };
        lines.Add(active.Count == 0 ? "Эффектов нет." : "Действует на юнитов вида (каждый эффект один раз): " +
            string.Join("; ", active.Select(a => $"{HexTactics.Content.Runtime.PassiveEffects.Describe(a.Passive, overlay)} · {a.Source}")));
        var children = Library.Items(ContentTypeRegistry.Species).Where(sp => sp.Data is SpeciesData d && d.Parent == Item.Id).Select(sp => sp.Name).ToList();
        if (children.Count > 0)
        {
            lines.Add($"Потомки: {string.Join(", ", children)} — изменения действуют и на них после сохранения.");
        }

        var users = Library.UsedBy(Item.Ref).Where(u => u.Kind.Id == ContentTypeRegistry.Unit).ToList();
        lines.Add(users.Count == 0 ? "Юнитов этого вида пока нет." : $"Юниты: {string.Join(", ", users.Select(u => u.Name))}.");
        return lines;
    }
}

/// <summary>A shared trait (spec part 6 §5.3, §5.6): category, effects, allowed species, conflicts, stacking and users.</summary>
public sealed class TraitEditorModel : DefinitionEditorModel
{
    public TraitEditorModel(ContentLibrary library, ContentItem item)
        : base(library, item)
    {
        if (item.Data is not TraitData)
        {
            throw new ArgumentException("Not a trait.", nameof(item));
        }
    }

    public TraitData Trait => (TraitData)Item.Data;

    public void SetCategory(string category) => Apply("Категория тега", (d, _) => ((TraitData)d).Category = category);

    public void SetIcon(string icon) => Apply("Значок тега", (d, _) => ((TraitData)d).Icon = icon.Trim());

    public string? SetExclusive(string group, string policy, int priority)
    {
        if (group.Trim().Length > 40 || policy is not (TraitData.PolicyError or TraitData.PolicyReplace or TraitData.PolicyHighest) || priority is < -1000 or > 1000)
        {
            return "группа — до 40 символов, политика error / replace / highest_priority, приоритет −1000…1000";
        }

        Apply("Взаимоисключение", (d, _) =>
        {
            var t = (TraitData)d;
            (t.ExclusiveGroup, t.ConflictPolicy, t.Priority) = (group.Trim(), policy, priority);
        });
        return null;
    }

    public string? SetStacking(string policy, int max)
    {
        if (policy is not (TraitData.StackUnique or TraitData.StackStack) || max is < 1 or > 16)
        {
            return "повтор: unique или stack, предел 1…16";
        }

        Apply("Повтор тега", (d, _) => ((TraitData)d).StackPolicy = policy);
        Apply("Предел повтора", (d, _) => ((TraitData)d).MaxStacks = max);
        return null;
    }

    /// <summary>Adds or removes an id in «allowed species», «required» or «excluded» traits.</summary>
    public string? Toggle(string list, string id)
    {
        var kind = list == "allowedSpecies" ? ContentTypeRegistry.Species : ContentTypeRegistry.Trait;
        if (Library.Find(kind, id) is null)
        {
            return $"«{id}» не найден";
        }

        if (kind == ContentTypeRegistry.Trait && id == Item.Id)
        {
            return "тег не может ссылаться на себя";
        }

        Apply("Условия тега", (d, _) =>
        {
            var t = (TraitData)d;
            var target = list switch { "allowedSpecies" => t.AllowedSpecies, "requiredTraits" => t.RequiredTraits, _ => t.ExcludedTraits };
            if (!target.Remove(id))
            {
                target.Add(id);
            }
        });
        return null;
    }

    public (string? Id, string? Refusal) AddEffect(string operation) => AddEffect(d => ((TraitData)d).Effects, operation, "Эффект");

    public void RemoveEffect(string id) => RemoveEffect(d => ((TraitData)d).Effects, id, "эффект");

    public ContentIssue? SetEffect(string id, string key, object? input) => SetEffect(d => ((TraitData)d).Effects, "effects", id, key, input);

    public IReadOnlyList<HexTactics.Editor.Units.PassiveCard> EffectCards() => EffectCards(Trait.Effects);

    public IReadOnlyList<string> Preview()
    {
        var t = Trait;
        var lines = new List<string>
        {
            t.Effects.Count == 0 ? "Без эффектов — только классификация (фильтры «имеет тег»)." :
                "Эффекты: " + string.Join("; ", t.Effects.Select(e => HexTactics.Content.Runtime.PassiveEffects.Describe(e, Library))),
            t.StackPolicy == TraitData.StackStack ? $"Повтор: складывается до {t.MaxStacks} раз." : "Повтор: один раз, даже если указан дважды.",
        };
        if (t.ExclusiveGroup.Length > 0)
        {
            lines.Add($"Группа «{t.ExclusiveGroup}»: " + (t.ConflictPolicy switch
            {
                TraitData.PolicyReplace => "действует последний указанный у юнита тег группы.",
                TraitData.PolicyHighest => $"действует тег с наибольшим приоритетом (этот — {t.Priority}).",
                _ => "два тега группы у одного юнита — ошибка, нужно выбрать один.",
            }));
        }

        if (t.AllowedSpecies.Count > 0)
        {
            lines.Add("Только для видов: " + string.Join(", ", t.AllowedSpecies.Select(s => Library.Find(ContentTypeRegistry.Species, s)?.Name ?? s)) + " (и их потомков).");
        }

        var users = Library.UsedBy(Item.Ref).Where(u => u.Kind.Id == ContentTypeRegistry.Unit).ToList();
        lines.Add(users.Count == 0 ? "Пока ни у одного юнита." : $"У юнитов: {string.Join(", ", users.Select(u => u.Name))} — изменения действуют на всех после сохранения.");
        return lines;
    }
}
