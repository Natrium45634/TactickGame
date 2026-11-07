using System;
using System.Collections.Generic;
using HexTactics.Content.Kinds;
using HexTactics.Content.Model;
using HexTactics.Content.Registry;
using HexTactics.Content.Storage;

namespace HexTactics.Content.Library;

/// <summary>A file that could not be loaded (it is left untouched on disk).</summary>
public sealed record ContentLoadProblem(string Path, ContentOrigin Origin, IReadOnlyList<ContentIssue> Issues);

/// <summary>Result of a library operation.</summary>
public sealed record ContentOperationResult(bool Ok, IReadOnlyList<ContentIssue> Issues, ContentItem? Item = null)
{
    public static ContentOperationResult Fail(string message) => new(false, new[] { ContentIssue.Error(string.Empty, message) });

    public string Message => Issues.Count == 0 ? string.Empty : string.Join("; ", Issues);
}

/// <summary>A deleted object that can be restored (Undo of a library deletion).</summary>
public sealed record DeletedContent(ContentKind Kind, string Id, string TrashPath);

/// <summary>
/// The content library (spec part 4 §2.2, §8, §9.2 <c>ContentRepository</c>): built-in objects of the game (read-only)
/// plus the user's objects from <see cref="ContentRepository"/>; lookup by <c>kind + id</c>, dependencies, validation and
/// the CRUD operations of the developer catalog. Saving only happens after validation; invalid objects are never written.
/// </summary>
public sealed class ContentLibrary
{
    private readonly Dictionary<(string Kind, string Id), ContentItem> _items = new();
    private readonly List<ContentLoadProblem> _problems = new();
    private readonly List<(string Path, string Text)> _builtInFiles = new();
    private readonly Func<DateTimeOffset> _clock;

    public ContentLibrary(ContentTypeRegistry registry, ContentRepository? user = null, ContentLimits? limits = null, Func<DateTimeOffset>? clock = null)
    {
        Registry = registry;
        User = user;
        Limits = limits ?? ContentLimits.Default;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public ContentTypeRegistry Registry { get; }

    public ContentRepository? User { get; }

    public ContentLimits Limits { get; }

    /// <summary>Incremented on every change of the library (for UI refresh).</summary>
    public int Version { get; private set; }

    /// <summary>Files that could not be loaded at the last (re)load.</summary>
    public IReadOnlyList<ContentLoadProblem> Problems => _problems;

    /// <summary>Registers the built-in objects shipped with the game (path is only used in messages).</summary>
    public void LoadBuiltIn(IEnumerable<(string Path, string Text)> files)
    {
        _builtInFiles.AddRange(files);
        Reload();
    }

    /// <summary>Re-reads the built-in objects and every user file.</summary>
    public void Reload()
    {
        _items.Clear();
        _problems.Clear();
        foreach (var (path, text) in _builtInFiles)
        {
            Add(path, text, ContentOrigin.BuiltIn);
        }

        if (User is not null)
        {
            foreach (var kind in Registry.All)
            {
                foreach (var (path, text) in User.ReadAll(kind.Folder))
                {
                    Add(path, text, ContentOrigin.User, kind);
                }
            }
        }

        Version++;
    }

    /// <summary>All objects of a kind, built-in first, then by name.</summary>
    public IReadOnlyList<ContentItem> Items(string kind)
    {
        var list = new List<ContentItem>();
        foreach (var item in _items.Values)
        {
            if (item.Kind.Id == kind)
            {
                list.Add(item);
            }
        }

        list.Sort((a, b) => a.Origin != b.Origin ? a.Origin.CompareTo(b.Origin) : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase) is var c && c != 0 ? c : string.CompareOrdinal(a.Id, b.Id));
        return list;
    }

    public ContentItem? Find(string kind, string id) => _items.TryGetValue((kind, id), out var item) ? item : null;

    public ContentItem? Find(ContentRef reference) => Find(reference.Kind, reference.Id);

    /// <summary>Objects whose data refers to <paramref name="target"/> (spec part 4 §8.3: no silent destruction).</summary>
    public IReadOnlyList<ContentItem> UsedBy(ContentRef target)
    {
        var list = new List<ContentItem>();
        foreach (var item in _items.Values)
        {
            foreach (var r in item.References)
            {
                if (r == target)
                {
                    list.Add(item);
                    break;
                }
            }
        }

        list.Sort((a, b) => string.CompareOrdinal(a.Ref.ToString(), b.Ref.ToString()));
        return list;
    }

    /// <summary>A fresh id for a new user object of the kind.</summary>
    public string NewId(string kind) => ContentIds.New(kind, id => _items.ContainsKey((kind, id)) || (User?.Exists(Registry.Find(kind)!.Folder, id) ?? false));

    /// <summary>A new, unsaved user object with the default data of its kind.</summary>
    public ContentItem Create(string kind, string name)
    {
        var descriptor = Registry.Find(kind) ?? throw new ArgumentException($"Unknown content kind '{kind}'.", nameof(kind));
        return new ContentItem(descriptor, NewId(kind), descriptor.CreateDefault(), ContentOrigin.User) { Name = name, Revision = 0 };
    }

    /// <summary>Full check of one object: envelope, data values, limits and that every reference resolves.</summary>
    public IReadOnlyList<ContentIssue> Validate(ContentItem item)
    {
        var issues = new IssueList();
        if (!ContentIds.IsValid(item.Id))
        {
            issues.Error("id", $"неверный идентификатор «{item.Id}»");
        }

        if (string.IsNullOrWhiteSpace(item.Name))
        {
            issues.Error("name", "нужно название");
        }
        else if (item.Name.Length > 100)
        {
            issues.Error("name", "название длиннее 100 символов");
        }

        if (item.Description.Length > Limits.MaxTextLength)
        {
            issues.Error("description", "слишком длинное описание");
        }

        var dataIssues = new IssueList();
        item.Data.Validate(new ValidationContext(dataIssues, Registry.Effects, Limits, Registry.DefenseActions));
        foreach (var i in dataIssues.Items)
        {
            issues.AddRange(new[] { i with { Path = i.Path.Length == 0 ? "data" : "data." + i.Path } });
        }

        foreach (var r in item.References)
        {
            var target = Find(r);
            if (target is null)
            {
                var kindTitle = Registry.Find(r.Kind)?.Singular ?? r.Kind;
                issues.Error("references", $"ссылка на отсутствующий объект: {kindTitle} «{r.Id}»");
            }
        }

        if (HasCycle(item))
        {
            issues.Error("references", "циклическая ссылка между объектами");
        }

        if (item.Data is MapData map)
        {
            MapChecks.Validate(map, this, issues);
        }

        if (item.Data is ResistanceProfileData profile)
        {
            foreach (var key in profile.Values.Keys)
            {
                if (key != UnitData.GlobalResistance && Find(ContentTypeRegistry.DamageType, key) is { Data: DamageTypeData { Strategy: DamageTypeData.TrueLike } } bypass)
                {
                    issues.Warning($"data.values.{key}", $"«{bypass.Name}» обходит численные сопротивления — значение не действует");
                }
            }
        }

        if (item.Data is DamageTypeData type && type.Family.Length > 0 && Find(ContentTypeRegistry.DamageType, type.Family) is { Data: DamageTypeData family } familyItem)
        {
            // Spec part 5 §6.2: a subtype cannot change how its family is calculated.
            if (family.Family.Length > 0)
            {
                issues.Error("data.family", $"«{familyItem.Name}» — подтип, а не семейство");
            }
            else if (family.Strategy != type.Strategy)
            {
                issues.Error("data.strategy", $"стратегия подтипа должна совпадать со стратегией семейства «{familyItem.Name}»");
            }
        }

        if (item.Data is UnitData unit)
        {
            var layered = ResistanceLayers.Values(unit, this);
            foreach (var r in unit.Resistances.Keys)
            {
                if (r != UnitData.GlobalResistance && Find(ContentTypeRegistry.DamageType, r) is { Data: DamageTypeData { Strategy: DamageTypeData.TrueLike } } bypass)
                {
                    issues.Warning($"data.resistances.{r}", $"«{bypass.Name}» обходит численные сопротивления — значение не действует");
                }
            }

            foreach (var t in Items(ContentTypeRegistry.DamageType))
            {
                if (t.Data is not DamageTypeData { Strategy: DamageTypeData.ResistanceAffected } d)
                {
                    continue;
                }

                var familyId = d.Family.Length == 0 ? t.Id : d.Family;
                var sum = Get(layered, UnitData.GlobalResistance) + Get(layered, familyId) + (familyId == t.Id ? 0 : Get(layered, t.Id));
                if (sum > UnitData.MaxResistance || sum < UnitData.MinResistance)
                {
                    issues.Warning("data.resistances", $"итог сопротивления «{t.Name}» = {sum:0.##} % вне −500…+500 — в бою будет ограничен");
                }
            }
        }

        return issues.Items;

        static double Get(IReadOnlyDictionary<string, double> values, string key) => values.TryGetValue(key, out var v) ? v : 0;
    }

    /// <summary>
    /// Validates and atomically saves a user object: revision + 1, modification time, file named by the id. Built-in
    /// objects and objects with errors are refused (nothing is written).
    /// </summary>
    public ContentOperationResult Save(ContentItem item)
    {
        if (User is null)
        {
            return ContentOperationResult.Fail("нет пользовательского хранилища");
        }

        if (item.IsBuiltIn || ContentIds.IsBuiltIn(item.Id))
        {
            return ContentOperationResult.Fail("встроенный объект нельзя изменить — создайте копию");
        }

        var issues = Validate(item);
        foreach (var i in issues)
        {
            if (i.IsError)
            {
                return new ContentOperationResult(false, issues);
            }
        }

        var saved = item.Clone();
        saved.Revision = (Find(item.Kind.Id, item.Id)?.Revision ?? 0) + 1;
        saved.Modified = _clock();
        saved.SchemaVersion = item.Kind.SchemaVersion;
        var text = ContentSerializer.Write(saved);
        try
        {
            saved.SourcePath = User.SaveAtomic(item.Kind.Folder, item.Id, text, written =>
            {
                var check = ContentSerializer.Read(written, Registry, ContentOrigin.User, Limits);
                return check.Item is not null && !check.HasErrors;
            });
        }
        catch (ContentSaveException e)
        {
            return ContentOperationResult.Fail(e.Message);
        }

        saved.Issues = issues;
        _items[(saved.Kind.Id, saved.Id)] = saved;
        item.Revision = saved.Revision;
        item.Modified = saved.Modified;
        Version++;
        return new ContentOperationResult(true, issues, saved);
    }

    /// <summary>Saves a copy of <paramref name="source"/> under a new id (built-in objects can be duplicated too).</summary>
    public ContentOperationResult Duplicate(ContentItem source, string? name = null)
    {
        var copy = source.CopyAs(NewId(source.Kind.Id), ContentOrigin.User, name ?? $"{source.Name} (копия)");
        copy.Revision = 0;
        return Save(copy);
    }

    /// <summary>Changes the display name only; the id (and every reference to it) stays.</summary>
    public ContentOperationResult Rename(string kind, string id, string newName)
    {
        var item = Find(kind, id);
        if (item is null)
        {
            return ContentOperationResult.Fail("объект не найден");
        }

        var copy = item.Clone();
        copy.Name = newName.Trim();
        return Save(copy);
    }

    /// <summary>
    /// Deletes a user object (moved to the trash folder, restorable). Refused for built-in objects and — unless
    /// <paramref name="force"/> — for objects other objects depend on (they are listed in the result).
    /// </summary>
    public (ContentOperationResult Result, DeletedContent? Deleted) Delete(string kind, string id, bool force = false)
    {
        var item = Find(kind, id);
        if (item is null)
        {
            return (ContentOperationResult.Fail("объект не найден"), null);
        }

        if (item.IsBuiltIn || User is null)
        {
            return (ContentOperationResult.Fail("встроенный объект нельзя удалить"), null);
        }

        var users = UsedBy(item.Ref);
        if (users.Count > 0 && !force)
        {
            var issues = new List<ContentIssue>();
            foreach (var u in users)
            {
                issues.Add(ContentIssue.Error("references", $"используется: {u.Kind.Singular} «{u.Name}» ({u.Id})"));
            }

            return (new ContentOperationResult(false, issues, item), null);
        }

        string trash;
        try
        {
            trash = User.MoveToTrash(item.Kind.Folder, item.Id);
        }
        catch (System.IO.IOException e)
        {
            return (ContentOperationResult.Fail($"не удалось удалить: {e.Message}"), null);
        }

        _items.Remove((kind, id));
        Version++;
        return (new ContentOperationResult(true, Array.Empty<ContentIssue>(), item), new DeletedContent(item.Kind, item.Id, trash));
    }

    /// <summary>Puts a deleted object back (Undo of <see cref="Delete"/>).</summary>
    public ContentOperationResult Restore(DeletedContent deleted)
    {
        if (User is null)
        {
            return ContentOperationResult.Fail("нет пользовательского хранилища");
        }

        try
        {
            User.Restore(deleted.Kind.Folder, deleted.Id, deleted.TrashPath);
        }
        catch (System.IO.IOException e)
        {
            return ContentOperationResult.Fail($"не удалось восстановить: {e.Message}");
        }

        var path = User.PathOf(deleted.Kind.Folder, deleted.Id);
        Add(path, System.IO.File.ReadAllText(path), ContentOrigin.User, deleted.Kind);
        Version++;
        var item = Find(deleted.Kind.Id, deleted.Id);
        return item is null ? ContentOperationResult.Fail("объект не восстановился") : new ContentOperationResult(true, Array.Empty<ContentIssue>(), item);
    }

    private void Add(string path, string text, ContentOrigin origin, ContentKind? expectedKind = null)
    {
        var result = ContentSerializer.Read(text, Registry, origin, Limits, path);
        if (result.Item is not { } item)
        {
            _problems.Add(new ContentLoadProblem(path, origin, result.Issues));
            return;
        }

        var issues = new List<ContentIssue>(result.Issues);
        if (expectedKind is not null && item.Kind != expectedKind)
        {
            _problems.Add(new ContentLoadProblem(path, origin, new[] { ContentIssue.Error("kind", $"объект вида «{item.Kind.Id}» лежит в папке {expectedKind.Folder}") }));
            return;
        }

        if (origin == ContentOrigin.User && ContentIds.IsBuiltIn(item.Id))
        {
            _problems.Add(new ContentLoadProblem(path, origin, new[] { ContentIssue.Error("id", $"идентификатор «{item.Id}» зарезервирован для встроенного контента") }));
            return;
        }

        if (origin == ContentOrigin.User && System.IO.Path.GetFileNameWithoutExtension(path) != item.Id)
        {
            issues.Add(ContentIssue.Warning("id", $"имя файла не совпадает с идентификатором «{item.Id}»"));
        }

        if (_items.ContainsKey((item.Kind.Id, item.Id)))
        {
            _problems.Add(new ContentLoadProblem(path, origin, new[] { ContentIssue.Error("id", $"конфликт идентификаторов: «{item.Id}» уже загружен") }));
            return;
        }

        if (origin == ContentOrigin.User && result.MigratedFrom is { } from && User is not null)
        {
            // Spec part 4 §8.4, §12.3 p.29: an older file is upgraded with a backup of the original; if writing fails, the
            // original stays as it was and the object is still used from memory.
            try
            {
                var backup = User.Backup(item.Kind.Folder, item.Id, $"migration-schema{from}");
                var upgraded = ContentSerializer.Write(item);
                User.SaveAtomic(item.Kind.Folder, item.Id, upgraded, written => ContentSerializer.Read(written, Registry, ContentOrigin.User, Limits).Item is not null);
                User.Log($"migrated {item.Kind.Folder}/{item.Id}: schema {from} → {item.Kind.SchemaVersion}, original in {backup}");
                issues.Add(ContentIssue.Warning("schemaVersion", $"обновлён со схемы {from} до {item.Kind.SchemaVersion}; исходный файл сохранён в backups"));
            }
            catch (Exception e) when (e is ContentSaveException or System.IO.IOException or UnauthorizedAccessException)
            {
                issues.Add(ContentIssue.Warning("schemaVersion", $"обновлён в памяти, файл не перезаписан: {e.Message}"));
            }
        }

        item.Issues = issues;
        _items[(item.Kind.Id, item.Id)] = item;
    }

    /// <summary>
    /// A read-only view of this library with <paramref name="items"/> added or replacing objects of the same
    /// <c>kind + id</c> — to validate imported objects and their links before anything is written.
    /// </summary>
    public ContentLibrary Overlay(IEnumerable<ContentItem> items)
    {
        var copy = new ContentLibrary(Registry, null, Limits, _clock);
        foreach (var (key, value) in _items)
        {
            copy._items[key] = value;
        }

        foreach (var item in items)
        {
            copy._items[(item.Kind.Id, item.Id)] = item;
        }

        return copy;
    }

    private bool HasCycle(ContentItem start)
    {
        var visiting = new HashSet<ContentRef>();
        return Visit(start.Ref, start);

        bool Visit(ContentRef current, ContentItem? item)
        {
            if (!visiting.Add(current))
            {
                return true;
            }

            item ??= Find(current);
            if (item is not null)
            {
                foreach (var r in item.References)
                {
                    if (r == start.Ref || (Find(r) is { } next && Visit(r, next)))
                    {
                        return true;
                    }
                }
            }

            visiting.Remove(current);
            return false;
        }
    }

    /// <summary>Validation of the whole library (every object): used by the catalog's «Проверить».</summary>
    public void Revalidate()
    {
        foreach (var item in _items.Values)
        {
            item.Issues = Validate(item);
        }

        Version++;
    }
}
