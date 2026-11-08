using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Model;
using HexTactics.Content.Registry;
using HexTactics.Content.Schema;
using HexTactics.Editor.History;

namespace HexTactics.Editor.Definitions;

/// <summary>
/// Common model of the definition editors (units, skills, tile types; spec part 4 §3, §5–7): a working copy of one
/// library object, every change an undoable snapshot command, values checked against the field schema before they are
/// applied (an invalid value is refused with an explanation and never reaches the object), saving through the library
/// (built-in objects are saved as a copy).
/// </summary>
public class DefinitionEditorModel
{
    public DefinitionEditorModel(ContentLibrary library, ContentItem item)
    {
        Library = library;
        Item = item.Clone();
        History.Changed += () => Changed?.Invoke();
    }

    public ContentLibrary Library { get; }

    public ContentItem Item { get; private set; }

    public CommandHistory History { get; } = new();

    public event Action? Changed;

    public bool IsDirty => History.IsDirty || Item.Revision == 0;

    public IReadOnlyList<ContentIssue> Validate() => Library.Validate(Item);

    public void SetName(string name) => Apply("Название", (_, i) => i.Name = name.Trim());

    public void SetDescription(string text) => Apply("Описание", (_, i) => i.Description = text);

    /// <summary>
    /// Sets a field from user input. Text is parsed for numeric fields (comma or dot); the value is checked by the
    /// field schema first — on failure nothing changes and the issue explains why (spec part 4 §5.1, §12.2 p.11).
    /// </summary>
    public ContentIssue? TrySetField(FieldSchema field, object? input)
    {
        var value = input;
        if (field.Kind is FieldKind.Number or FieldKind.Integer && input is string text)
        {
            if (!double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return ContentIssue.Error(field.Key, $"«{field.Label}»: «{text}» — не число, значение не изменено");
            }

            value = parsed;
        }

        if (value is int i)
        {
            value = (double)i;
        }

        if (field.Check(value, field.Key) is { } issue)
        {
            return issue with { Message = issue.Message + " — значение не изменено" };
        }

        Apply($"«{field.Label}»: {Show(value)}", (d, _) => field.Set!(d, value));
        return null;
    }

    /// <summary>Tells the view to refresh (selection and other view state that is not part of the object).</summary>
    protected void RaiseChanged() => Changed?.Invoke();

    public bool Undo() => History.Undo();

    public bool Redo() => History.Redo();

    /// <summary>Validates and saves; a built-in object is saved as a copy with a new id and editing continues on the copy.</summary>
    public ContentOperationResult Save()
    {
        var target = Item;
        if (Item.IsBuiltIn)
        {
            target = Item.CopyAs(Library.NewId(Item.Kind.Id), ContentOrigin.User, $"{Item.Name} (копия)");
            target.Revision = 0;
        }

        var result = Library.Save(target);
        if (result.Ok)
        {
            Item = result.Item!.Clone();
            History.MarkSaved();
            Library.User?.DeleteDraft(Item.Kind.Folder, Item.Id);
        }

        Changed?.Invoke();
        return result;
    }

    public string Snapshot() => ContentSerializer.Write(Item);

    /// <summary>Replaces the working copy by a restored draft (undoable, unsaved).</summary>
    public void LoadDraft(ContentItem draft)
    {
        var data = draft.Data.Clone();
        Apply("Восстановить черновик", (_, i) =>
        {
            i.Data = data.Clone();
            i.Name = draft.Name;
            i.Description = draft.Description;
        });
    }

    /// <summary>One undoable change of the data (and/or the envelope); no-op changes are not recorded.</summary>
    protected void Apply(string description, Action<IContentData, ContentItem> mutate)
    {
        var before = (Data: Item.Data.Clone(), Item.Name, Item.Description);
        var probe = Item.CopyAs(Item.Id, Item.Origin, Item.Name);
        mutate(probe.Data, probe);
        var after = (Data: probe.Data.Clone(), probe.Name, probe.Description);
        if (after.Data.ToJson().ToJsonString() == before.Data.ToJson().ToJsonString() && after.Name == before.Name && after.Description == before.Description)
        {
            return;
        }

        History.Execute(new DelegateCommand(description, () => Restore(after), () => Restore(before)));
    }

    /// <summary>
    /// Adds an effect of an operation with its default value to a list of effects of the definition (unit passives, species
    /// and trait effects — spec part 6 §4, §5); one undo step; returns its key or a refusal.
    /// </summary>
    protected (string? Id, string? Refusal) AddEffect(Func<IContentData, List<PassiveData>> list, string operation, string label)
    {
        if (PassiveOperations.Find(operation) is not { } op)
        {
            return (null, $"нет шаблона «{operation}»");
        }

        var current = list(Item.Data);
        if (current.Count >= PassiveData.MaxPerUnit)
        {
            return (null, $"не больше {PassiveData.MaxPerUnit} эффектов");
        }

        var n = 1;
        while (current.Any(p => p.Id == $"p{n}"))
        {
            n++;
        }

        var passive = new PassiveData
        {
            Id = $"p{n}",
            Name = char.ToUpper(op.Template[0], CultureInfo.CurrentCulture) + op.Template[1..],
            Operation = operation,
            Value = op.DefaultValue,
            Rule = op.UsesRule ? Library.Items(ContentTypeRegistry.DefenseRule).FirstOrDefault(r => !r.HasErrors)?.Id ?? string.Empty : string.Empty,
        };
        Apply($"{label} «{passive.Name}»", (d, _) => list(d).Add(passive.Clone()));
        return (passive.Id, null);
    }

    protected void RemoveEffect(Func<IContentData, List<PassiveData>> list, string id, string label)
    {
        var name = list(Item.Data).FirstOrDefault(p => p.Id == id)?.Name ?? id;
        Apply($"Убрать {label} «{name}»", (d, _) => list(d).RemoveAll(p => p.Id == id));
    }

    /// <summary>Changes one field of an effect of the list from input; a refusal leaves it as it was.</summary>
    protected ContentIssue? SetEffect(Func<IContentData, List<PassiveData>> list, string pathRoot, string id, string key, object? input)
    {
        var current = list(Item.Data);
        var index = current.FindIndex(p => p.Id == id);
        if (index < 0)
        {
            return ContentIssue.Error($"{pathRoot}.{id}", "нет такого эффекта");
        }

        var probe = current[index].Clone();
        var path = $"{pathRoot}[{index}].{key}";
        double? Number()
        {
            if (input is double d)
            {
                return d;
            }

            return input is string t && double.TryParse(t.Trim().Replace(',', '.').TrimEnd('%').Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
        }

        switch (key)
        {
            case "name":
                probe.Name = ((input as string) ?? string.Empty).Trim();
                break;
            case "stat":
                probe.Stat = input as string ?? probe.Stat;
                if (HexTactics.Content.Runtime.PassiveEffects.StatChoices(Library).All(c => c.Value != probe.Stat))
                {
                    return ContentIssue.Error(path, $"нет характеристики «{probe.Stat}» — значение не изменено");
                }

                break;
            case "inherit":
                probe.Inherit = input as string ?? PassiveData.InheritReplace;
                break;
            case "countSpecies":
                probe.CountSpecies = input as string ?? string.Empty;
                if (probe.CountSpecies.Length > 0 && Library.Find(ContentTypeRegistry.Species, probe.CountSpecies) is null)
                {
                    return ContentIssue.Error(path, $"нет вида «{probe.CountSpecies}» — значение не изменено");
                }

                break;
            case "countSamePack":
                probe.CountSamePack = input is true or "true";
                break;
            case "countSelf":
                probe.CountSelf = input is true or "true";
                break;
            case "countRadius":
            case "countMax":
                if (Number() is not { } whole || Math.Abs(whole - Math.Round(whole)) > 1e-9)
                {
                    return ContentIssue.Error(path, $"«{input}» — не целое число, значение не изменено");
                }

                if (key == "countRadius")
                {
                    probe.CountRadius = (int)whole;
                }
                else
                {
                    probe.CountMax = (int)whole;
                }

                break;
            case "damageTypeId":
                probe.DamageType = input as string ?? string.Empty;
                if (probe.DamageType.Length > 0 && Library.Find(ContentTypeRegistry.DamageType, probe.DamageType) is null)
                {
                    return ContentIssue.Error(path, $"нет типа урона «{probe.DamageType}» — значение не изменено");
                }

                break;
            case "key":
                probe.Key = input as string ?? UnitData.GlobalResistance;
                if (probe.Key != UnitData.GlobalResistance && Library.Find(ContentTypeRegistry.DamageType, probe.Key) is null)
                {
                    return ContentIssue.Error(path, $"нет типа урона «{probe.Key}» — значение не изменено");
                }

                break;
            case "ruleId":
                probe.Rule = input as string ?? string.Empty;
                if (Library.Find(ContentTypeRegistry.DefenseRule, probe.Rule) is null)
                {
                    return ContentIssue.Error(path, $"нет правила защиты «{probe.Rule}» — значение не изменено");
                }

                break;
            case "value":
                if (Number() is not { } value)
                {
                    return ContentIssue.Error(path, $"«{input}» — не число, значение не изменено");
                }

                probe.Value = Math.Round(value, 2, MidpointRounding.AwayFromZero);
                break;
            case "healthBelowPercent":
                if (Number() is not { } below)
                {
                    return ContentIssue.Error(path, $"«{input}» — не число, значение не изменено");
                }

                probe.HealthBelowPercent = below;
                break;
            case "stackGroup":
                probe.StackGroup = ((input as string) ?? string.Empty).Trim();
                break;
            default:
                return ContentIssue.Error(path, "нет такого поля эффекта");
        }

        var issues = new IssueList();
        probe.Validate(issues, $"{pathRoot}[{index}]");
        if (issues.Items.FirstOrDefault(i => i.IsError) is { } issue)
        {
            return issue with { Message = issue.Message + " — значение не изменено" };
        }

        Apply($"«{probe.Name}»", (d, _) => list(d)[index] = probe.Clone());
        return null;
    }

    /// <summary>The effects of a list as short cards (broken ones marked).</summary>
    protected IReadOnlyList<HexTactics.Editor.Units.PassiveCard> EffectCards(IEnumerable<PassiveData> list) => list
        .Select(p =>
        {
            var issues = new IssueList();
            p.Validate(issues, "p");
            var broken = issues.Items.Any(i => i.IsError) || p.References().Any(r => Library.Find(r.Kind, r.Id) is null);
            return new HexTactics.Editor.Units.PassiveCard(p.Id, p.Name, (broken ? "✗ " : string.Empty) + HexTactics.Content.Runtime.PassiveEffects.Describe(p, Library), broken);
        })
        .ToList();

    private void Restore((IContentData Data, string Name, string Description) state)
    {
        Item.Data = state.Data.Clone();
        Item.Name = state.Name;
        Item.Description = state.Description;
    }

    private static string Show(object? value) => value switch
    {
        double d => d.ToString("0.###", CultureInfo.InvariantCulture),
        IEnumerable<string> list => string.Join(", ", list),
        _ => value?.ToString() ?? "—",
    };
}
