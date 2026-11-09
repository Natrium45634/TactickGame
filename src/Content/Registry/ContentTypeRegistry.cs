using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using HexTactics.Content.Kinds;
using HexTactics.Content.Model;
using HexTactics.Content.Schema;

namespace HexTactics.Content.Registry;

/// <summary>
/// A content category (spec part 4 §2.2, §9.2 <c>ContentTypeRegistry</c>): how its objects are named, stored, read,
/// created and migrated. The developer catalog lists the registered categories — a new category appears there by
/// registering it, without touching the existing editors.
/// </summary>
public sealed class ContentKind
{
    /// <summary>Value of the <c>kind</c> field, e.g. <c>map</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Catalog title, e.g. «Карты».</summary>
    public required string Title { get; init; }

    /// <summary>One object, e.g. «карта».</summary>
    public required string Singular { get; init; }

    /// <summary>Folder under <c>content/</c>, e.g. <c>maps</c>.</summary>
    public required string Folder { get; init; }

    public string Description { get; init; } = string.Empty;

    /// <summary>Catalog order.</summary>
    public int Order { get; init; }

    /// <summary>
    /// Catalog group (spec part 5 §6.1: «Механики» holds damage types, defense rules and resistance sets as one entry with its
    /// own first screen); null — a top-level category.
    /// </summary>
    public string? Group { get; init; }

    /// <summary>Current version of the serialization schema of the <c>data</c> part.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Shown in the catalog (spec part 4 §2.2: categories appear only once their mechanics are supported).</summary>
    public bool Available { get; init; } = true;

    public required Func<JsonObject, IssueList, IContentData> Read { get; init; }

    public required Func<IContentData> CreateDefault { get; init; }

    /// <summary>Keys of <c>data</c> the kind reads but writes only when set (not «unknown» fields, spec part 6 §2.4).</summary>
    public IReadOnlyCollection<string> OptionalDataKeys { get; init; } = Array.Empty<string>();

    /// <summary>Fields of the definition for generic property panels and validation.</summary>
    public IReadOnlyList<FieldSchema> Fields { get; init; } = Array.Empty<FieldSchema>();

    /// <summary>
    /// Upgrades the <c>data</c> of an older schema version by one step (from → from + 1); null = no migration exists.
    /// </summary>
    public Func<int, JsonObject, IssueList, JsonObject?>? Migrate { get; init; }

    public override string ToString() => Id;
}

/// <summary>Registry of content categories.</summary>
public sealed class ContentTypeRegistry
{
    public const string Map = "map";
    public const string Unit = "unit";
    public const string Skill = "skill";
    public const string Tile = "tile";
    public const string DamageType = "damage_type";
    public const string DefenseRule = "defense_rule";
    public const string ResistanceProfile = "resistance_profile";
    public const string CombatRuleset = "combat_ruleset";
    public const string UnitStat = "unit_stat";
    public const string Species = "species";
    public const string Trait = "trait";
    public const string BasicAttack = "basic_attack";
    public const string Scenario = "scenario";

    /// <summary>The «Механики» group of the catalog.</summary>
    public const string MechanicsGroup = "mechanics";

    /// <summary>Catalog group «Происхождение и теги» (spec part 6 §5.2, §9.1): the species tree and the shared traits.</summary>
    public const string OriginGroup = "origin";

    private readonly List<ContentKind> _kinds = new();

    public ContentTypeRegistry(EffectTypeRegistry? effects = null, DefenseActionRegistry? defenseActions = null)
    {
        Effects = effects ?? EffectTypeRegistry.CreateDefault();
        DefenseActions = defenseActions ?? DefenseActionRegistry.Default;
    }

    /// <summary>Effect types usable inside the definitions of these categories.</summary>
    public EffectTypeRegistry Effects { get; }

    /// <summary>Actions of defense rules (spec part 5 §9.4 <c>DefenseRuleRegistry</c>).</summary>
    public DefenseActionRegistry DefenseActions { get; }

    /// <summary>All categories in catalog order.</summary>
    public IReadOnlyList<ContentKind> All
    {
        get
        {
            var list = new List<ContentKind>(_kinds);
            list.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : string.CompareOrdinal(a.Id, b.Id));
            return list;
        }
    }

    /// <summary>Categories shown in the developer catalog.</summary>
    public IReadOnlyList<ContentKind> Visible => ((List<ContentKind>)All).FindAll(k => k.Available);

    public void Register(ContentKind kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        if (Find(kind.Id) is not null)
        {
            throw new InvalidOperationException($"Content kind '{kind.Id}' is already registered.");
        }

        if (!ContentIds.IsValid(kind.Folder) || kind.Folder.Contains('.', StringComparison.Ordinal))
        {
            throw new ArgumentException($"Invalid folder '{kind.Folder}' for content kind '{kind.Id}'.", nameof(kind));
        }

        _kinds.Add(kind);
    }

    public ContentKind? Find(string kind) => _kinds.Find(k => k.Id == kind);

    /// <summary>The four categories of the MVP: maps, units, skills, tile types (spec part 4 §2.2).</summary>
    public static ContentTypeRegistry CreateDefault(EffectTypeRegistry? effects = null)
    {
        var registry = new ContentTypeRegistry(effects);
        registry.Register(new ContentKind
        {
            Id = Map, Title = "Карты", Singular = "карта", Folder = "maps", Order = 10,
            Description = "Поля из гексов: размер, типы местности, базы и зоны.",
            Read = (o, i) => MapData.Read(o, i), CreateDefault = () => new MapData { CombatRuleset = CombatRulesetData.Standard },
            SchemaVersion = 2, Migrate = Migrations.Map,
            OptionalDataKeys = new[] { "testPoints", "combatRuleset", "autoAttacks", "bases", "routes" },
        });
        registry.Register(new ContentKind
        {
            Id = Scenario, Title = "Сценарии", Singular = "сценарий", Folder = "scenarios", Order = 12,
            Description = "Что играть: карта, режим (обычный бой; Tower Defense, оборона и исследование — следующими версиями), отряд игрока, враги на карте и цели.",
            Read = (o, i) => ScenarioData.Read(o, i), CreateDefault = () => new ScenarioData(),
            OptionalDataKeys = new[] { "waves", "bases", "defeat", "objects", "objectsPolicy", "survive", "squadLoss" },
        });
        registry.Register(new ContentKind
        {
            Id = Unit, Title = "Юниты", Singular = "юнит", Folder = "units", Order = 20,
            Description = "Определения юнитов: характеристики, навыки, внешний вид.",
            Read = (o, i) => UnitData.Read(o, i), CreateDefault = () => new UnitData(), Fields = UnitData.Fields,
            SchemaVersion = 9, Migrate = Migrations.Unit,
        });
        registry.Register(new ContentKind
        {
            Id = Skill, Title = "Навыки", Singular = "навык", Folder = "skills", Order = 30,
            Description = "Область действия на гексах и эффекты каждой клетки.",
            Read = (o, i) => SkillData.Read(o, i), CreateDefault = () => new SkillData(),
            SchemaVersion = 2, Migrate = Migrations.Skill,
        });
        registry.Register(new ContentKind
        {
            Id = Tile, Title = "Гексы", Singular = "тип гекса", Folder = "tiles", Order = 40,
            Description = "Типы местности: проходимость, высота, цвет, эффекты.",
            Read = (o, i) => TileData.Read(o, i), CreateDefault = () => new TileData(), Fields = TileData.Fields,
            SchemaVersion = 2, Migrate = Migrations.Tile,
        });
        registry.Register(new ContentKind
        {
            Id = DamageType, Title = "Типы урона", Singular = "тип урона", Folder = "damage_types", Order = 50,
            Description = "Физический (колющий, режущий, дробящий), магический, чистый и свои типы: семейство и стратегия расчёта.",
            Read = (o, i) => DamageTypeData.Read(o, i), CreateDefault = () => new DamageTypeData { Family = "core.damage.physical" },
            OptionalDataKeys = new[] { "legacy" },
            Group = MechanicsGroup,
        });
        registry.Register(new ContentKind
        {
            Id = DefenseRule, Title = "Правила защиты", Singular = "правило защиты", Folder = "defense_rules", Order = 60,
            Description = "Иммунитеты, поглощение, лечение, отражение, щиты и ответные удары: «когда → от чего → при условии → что сделать».",
            Read = (o, i) => DefenseRuleData.Read(o, i), CreateDefault = () => DefenseRuleData.CreateDefault(), Fields = DefenseRuleData.Fields,
            Group = MechanicsGroup,
        });
        registry.Register(new ContentKind
        {
            Id = ResistanceProfile, Title = "Профили защиты и сопротивлений", Singular = "профиль защиты и сопротивлений", Folder = "resistance_profiles", Order = 70,
            Description = "Готовые таблицы сопротивлений R и защиты A («Бронированный», «Слаб к колющему»): применяются к юнитам ссылкой, значения юнита их перекрывают.",
            Read = (o, i) => ResistanceProfileData.Read(o, i), CreateDefault = () => new ResistanceProfileData(),
            SchemaVersion = 2, Migrate = Migrations.ResistanceProfile,
            Group = MechanicsGroup,
        });
        registry.Register(new ContentKind
        {
            Id = CombatRuleset, Title = "Правила боя", Singular = "правила боя", Folder = "combat_rulesets", Order = 80,
            Description = "Общие константы боя проекта: K защиты A (урон × K / (K + A)) и K по семействам; назначаются карте.",
            Read = (o, i) => CombatRulesetData.Read(o, i), CreateDefault = () => new CombatRulesetData(), Fields = CombatRulesetData.Fields,
            SchemaVersion = 2, Migrate = Migrations.CombatRuleset,
            Group = MechanicsGroup,
        });
        registry.Register(new ContentKind
        {
            Id = BasicAttack, Title = "Базовые атаки", Singular = "базовая атака", Folder = "basic_attacks", Order = 85,
            Description = "Автоматическая атака юнита: своя область гексов относительно взгляда, выбор цели, доля силы атаки и тип урона. Не зависит от навыков.",
            Read = (o, i) => BasicAttackData.Read(o, i), CreateDefault = () => new BasicAttackData(),
            Group = MechanicsGroup,
        });
        registry.Register(new ContentKind
        {
            Id = UnitStat, Title = "Характеристики", Singular = "характеристика", Folder = "unit_stats", Order = 90,
            Description = "Числовые характеристики юнитов (ловкость и свои): база, диапазон, округление. Меняются пассивами и тегами; на бой влияют только через механику, которая их читает.",
            Read = (o, i) => UnitStatData.Read(o, i), CreateDefault = () => new UnitStatData { BaseValue = 0 },
            Group = MechanicsGroup,
        });
        registry.Register(new ContentKind
        {
            Id = Species, Title = "Дерево видов", Singular = "вид", Folder = "species", Order = 25,
            Description = "Происхождение юнитов: у вида один родитель, потомок считается всеми предками (Лунный волк — Волк — Зверь) и наследует их эффекты один раз.",
            Read = (o, i) => SpeciesData.Read(o, i), CreateDefault = () => new SpeciesData(),
            Group = OriginGroup,
        });
        registry.Register(new ContentKind
        {
            Id = Trait, Title = "Теги", Singular = "тег", Folder = "traits", Order = 26,
            Description = "Общие переиспользуемые особенности юнитов («Вожак», «Матёрый», «Раненый»): эффекты, допустимые виды, взаимоисключение, повтор.",
            Read = (o, i) => TraitData.Read(o, i), CreateDefault = () => new TraitData(),
            Group = OriginGroup,
        });
        return registry;
    }
}
