using System;
using System.Collections.Generic;
using System.Globalization;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Model;
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
