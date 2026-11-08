using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Registry;
using HexTactics.Content.Schema;

namespace HexTactics.Editor.Library;

/// <summary>One entry of a reference selector: the value (an id), its label, the group it is shown under and its colour.</summary>
public sealed record ReferenceOption(string Value, string Label, string Group, string? Color = null)
{
    public bool IsCreate => Value == ReferenceCatalog.CreateValue;
}

/// <summary>
/// The options of every reference selector of the editors, from the one library (spec part 6 §2.1–§2.3): typed filters
/// (a family field offers families, a resistance field only resistance-affected types), grouped by family, legacy and archived
/// objects hidden unless already chosen, names shown, ids only in details. A newly saved object appears in every compatible
/// selector at the next rebuild — no list is copied into skills, units or tiles.
/// </summary>
public static class ReferenceCatalog
{
    /// <summary>The value of «＋ Создать…»: never stored, the editor opens a constructor instead.</summary>
    public const string CreateValue = "__create__";

    /// <summary>Kinds whose objects can be created right from a selector (spec part 6 §5.3).</summary>
    public static bool CanCreate(string refKind) => refKind == ContentTypeRegistry.DamageType;

    public static IReadOnlyList<ReferenceOption> Options(ContentLibrary library, string refKind, ReferenceFilter filter, string? current, bool allowCreate = false)
    {
        var list = new List<ReferenceOption>();
        var items = library.Items(refKind).Where(i => Accepts(library, i, filter) && ((!i.Archived && i.Data is not DamageTypeData { Legacy: true }) || i.Id == current)).ToList();
        if (refKind == ContentTypeRegistry.DamageType)
        {
            // Families in a stable order (physical, magical, custom, true-like last), each followed by its subtypes.
            int Order(ContentItem f) => f.Data is DamageTypeData { Strategy: DamageTypeData.TrueLike } ? 3 : f.Id == "core.damage.physical" ? 0 : f.Id == "core.damage.magical" ? 1 : 2;
            var all = library.Items(ContentTypeRegistry.DamageType);
            var families = all.Where(t => t.Data is DamageTypeData { Family.Length: 0 }).OrderBy(Order).ThenBy(t => t.Name, StringComparer.CurrentCulture).ToList();
            foreach (var family in families)
            {
                var group = family.Name;
                foreach (var t in items.Where(i => i.Id == family.Id || ((DamageTypeData)i.Data).Family == family.Id)
                             .OrderBy(i => i.Id == family.Id ? 0 : 1).ThenBy(i => i.Name, StringComparer.CurrentCulture))
                {
                    list.Add(new ReferenceOption(t.Id, Label(t), group, ((DamageTypeData)t.Data).Color));
                }
            }

            foreach (var orphan in items.Where(i => list.All(o => o.Value != i.Id)))
            {
                list.Add(new ReferenceOption(orphan.Id, Label(orphan), "Без семейства", ((DamageTypeData)orphan.Data).Color));
            }
        }
        else
        {
            var kind = library.Registry.Find(refKind);
            list.AddRange(items.OrderBy(i => i.IsBuiltIn ? 0 : 1).ThenBy(i => i.Name, StringComparer.CurrentCulture)
                .Select(i => new ReferenceOption(i.Id, Label(i), kind?.Title ?? refKind)));
        }

        if (allowCreate && CanCreate(refKind))
        {
            list.Add(new ReferenceOption(CreateValue, "＋ Новый тип урона…", "Создать"));
        }

        return list;
    }

    private static string Label(ContentItem item)
    {
        var text = item.Name;
        if (item.Data is DamageTypeData d)
        {
            if (d.Classification.Count > 0)
            {
                text += " · " + string.Join(", ", d.Classification.Select(DamageTypeData.ClassificationLabel));
            }
        }

        if (!item.IsBuiltIn)
        {
            text += " (свой)";
        }

        return item.Archived ? text + " — в архиве" : text;
    }

    /// <summary>Whether an object passes the typed filter of a field.</summary>
    public static bool Accepts(ContentLibrary library, ContentItem item, ReferenceFilter filter) => filter switch
    {
        ReferenceFilter.Family => item.Data is DamageTypeData { Family.Length: 0 },
        ReferenceFilter.Subtype => item.Data is DamageTypeData { Family.Length: > 0 },
        ReferenceFilter.ResistanceAffected => item.Data is DamageTypeData { Strategy: DamageTypeData.ResistanceAffected },
        _ => true,
    };
}
