using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Model;
using HexTactics.Content.Registry;
using HexTactics.Core.Combat;
using HexTactics.Core.Effects;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;
using HexTactics.Core.Skills;
using HexTactics.Core.Units;

namespace HexTactics.Content.Runtime;

/// <summary>How a tile type looks (presentation reads it; rules use the tile's <see cref="TileType"/>).</summary>
public sealed record TileVisual(string Id, string Name, string Color, int Level, TileType Type, string Icon = "");

/// <summary>A unit definition turned into runtime values.</summary>
public sealed record UnitTemplate(string Id, string Name, UnitStats Stats, IReadOnlyList<string> Skills, Side Side, string Color, double Scale)
{
    /// <summary>What the simulation needs to create a unit of this definition.</summary>
    public UnitProfile ToProfile() => new(Id, Name, Stats, Skills);
}

/// <summary>A unit already on the map when a scenario starts (spec part 7 §3.1 p.2).</summary>
public sealed record ScenarioUnit(UnitProfile Profile, HexCoord Cell, HexDirection Facing, Side Side, HexTactics.Core.Scenarios.AiProfile? Ai = null, bool Target = false);

/// <summary>What the match needs of a scenario: its goals, the units already on the map and the goals in words.</summary>
public sealed record ScenarioRuntime(string Id, string Name, HexTactics.Core.Scenarios.ScenarioRules Rules, IReadOnlyList<ScenarioUnit> Placed, string GoalsText, bool StartPaused = true);

/// <summary>Everything a match needs, built from content definitions by one factory (spec part 4 §9.2 <c>RuntimeFactory</c>).</summary>
public sealed record GameContent(
    MapDefinition Map,
    IReadOnlyDictionary<string, SkillDefinition> Skills,
    UnitTemplate DefaultUnit,
    IReadOnlyDictionary<string, TileVisual> TileVisuals,
    IReadOnlyList<ContentIssue> Issues)
{
    /// <summary>Every valid unit definition of the library (looks of units on the map, skill buttons).</summary>
    public IReadOnlyDictionary<string, UnitTemplate> Units { get; init; } = new Dictionary<string, UnitTemplate>();

    /// <summary>The roster of the match: operative names and the unit definitions they are made of.</summary>
    public IReadOnlyList<(string Name, UnitProfile Profile)> Squad { get; init; } = Array.Empty<(string, UnitProfile)>();

    /// <summary>
    /// Effects of tile types by type id (spec part 4 §7) for <c>SimulationSettings.TileEffects</c>: map cells refer to a
    /// type, the logic exists once per type.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<TileEffect>> TileEffects { get; init; } = new Dictionary<string, IReadOnlyList<TileEffect>>();

    /// <summary>Damage types of the library by id (spec part 5 §2) for <c>SimulationSettings.DamageTypes</c>.</summary>
    public IReadOnlyDictionary<string, DamageTypeDefinition> DamageTypes { get; init; } = HexTactics.Core.Combat.DamageTypes.BuiltIn;

    /// <summary>The combat rules of the map (spec part 6 §9.5) for <c>SimulationSettings.CombatRules</c>.</summary>
    public CombatRules CombatRules { get; init; } = CombatRules.Default;

    /// <summary>Id of the ruleset the rules come from.</summary>
    public string CombatRulesetId { get; init; } = CombatRulesetData.Legacy;

    /// <summary>The map allows automatic base attacks (spec part 7 §8, D-170) for <c>SimulationSettings.AutoAttacks</c>.</summary>
    public bool AutoAttacks { get; init; }

    /// <summary>Blocking acts (spec part 7 §7.3); a scenario may switch it off.</summary>
    public bool Blocking { get; init; } = true;

    /// <summary>The scenario of the match (spec part 7 §2.1); null — free play or a test of one object.</summary>
    public ScenarioRuntime? Scenario { get; init; }

    /// <summary>Names of the five roster operatives (spec part 2 §1).</summary>
    public static IReadOnlyList<string> OperativeNames { get; } = new[] { "Альфа", "Браво", "Чарли", "Дельта", "Эхо" };
}

/// <summary>
/// Turns library definitions into the runtime model of Core (spec part 4 §8.4: a definition is not the live object).
/// The same factory serves the «Играть» mode and test runs from the editors, so both use one core and one set of rules.
/// </summary>
public static class RuntimeFactory
{
    public const string DefaultMapId = "core.map.prototype";
    public const string DefaultUnitId = "core.unit.operative";

    /// <summary>
    /// Hexes of the prototype map that a test run of the tile editor turns into the tested type: a 2 × 3 block right in
    /// front of the player base, crossed by every route out of it.
    /// </summary>
    public static IReadOnlyList<HexCoord> TileTestCells { get; } = new[]
    {
        HexCoord.FromOffset(2, 4), HexCoord.FromOffset(2, 5), HexCoord.FromOffset(2, 6),
        HexCoord.FromOffset(3, 4), HexCoord.FromOffset(3, 5), HexCoord.FromOffset(3, 6),
    };

    public static TileType TypeOf(TileData tile)
    {
        if (!tile.Passable || tile.Category == TileData.Obstacle)
        {
            return TileType.Blocked;
        }

        return tile.Category switch
        {
            TileData.PlayerBase => TileType.PlayerBase,
            TileData.EnemyBase => TileType.EnemyBase,
            TileData.Deploy => TileType.DeployZone,
            TileData.Spawn => TileType.SpawnZone,
            _ => TileType.Walkable,
        };
    }

    public static TileVisual Visual(ContentItem tile) =>
        tile.Data is TileData t ? new TileVisual(tile.Id, tile.Name, t.Color, t.Level, TypeOf(t), t.Icon) : throw new ArgumentException("Not a tile type.", nameof(tile));

    /// <summary>The runtime effects of a tile type with their triggers (fields of the tile context, spec part 4 §7.1).</summary>
    public static IReadOnlyList<TileEffect> BuildTileEffects(ContentItem tile, EffectTypeRegistry effects)
    {
        if (tile.Data is not TileData data)
        {
            throw new ArgumentException("Not a tile type.", nameof(tile));
        }

        var list = new List<TileEffect>(data.Effects.Count);
        foreach (var effect in data.Effects)
        {
            var spec = effect.ToSpec(effects, EffectContext.Tile);
            var trigger = spec.Text("trigger", effect.Type == EffectTypes.Periodic ? "stay" : "enter") switch
            {
                "stay" => TileTrigger.Stay,
                "exit" => TileTrigger.Exit,
                _ => TileTrigger.Enter,
            };
            list.Add(new TileEffect(trigger, spec));
        }

        return list;
    }

    /// <summary>
    /// The map: a workspace of <c>columns × rows</c> where only the listed tiles exist (the rest are empty places).
    /// Tiles of a missing type are left empty and reported.
    /// </summary>
    public static MapDefinition BuildMap(ContentItem mapItem, ContentLibrary library, IssueList issues)
    {
        if (mapItem.Data is not MapData map)
        {
            throw new ArgumentException("Not a map.", nameof(mapItem));
        }

        var grid = new HexGrid(Math.Max(1, map.Columns), Math.Max(1, map.Rows));
        foreach (var c in grid.Coords)
        {
            grid.SetTile(c, TileType.Void, null, 0);
        }

        var zoneCells = new SortedDictionary<int, List<HexCoord>>();
        var numbered = new SortedDictionary<(BaseSide, int), List<HexCoord>>();
        var baseCells = new List<HexCoord>();
        var deployCells = new List<HexCoord>();
        foreach (var t in map.Tiles)
        {
            var coord = t.Coord;
            if (!grid.Contains(coord))
            {
                issues.Error("data.tiles", $"гекс ({t.Col},{t.Row}) вне рабочей области — пропущен");
                continue;
            }

            if (library.Find(ContentTypeRegistry.Tile, t.Tile)?.Data is not TileData type)
            {
                issues.Error("data.tiles", $"тип гекса «{t.Tile}» не найден — гекс ({t.Col},{t.Row}) пропущен");
                continue;
            }

            var tileType = TypeOf(type);
            var zone = tileType == TileType.SpawnZone ? t.Zone ?? 1 : 0;
            grid.SetTile(coord, tileType, t.Tile, t.Level ?? type.Level, zone, type.Waypoints);
            switch (tileType)
            {
                case TileType.SpawnZone:
                    if (!zoneCells.TryGetValue(zone, out var list))
                    {
                        list = new List<HexCoord>();
                        zoneCells[zone] = list;
                    }

                    list.Add(coord);
                    break;
                case TileType.PlayerBase:
                    baseCells.Add(coord);
                    AddToBase(numbered, BaseSide.Player, t.Base ?? 1, coord);
                    break;
                case TileType.EnemyBase:
                    AddToBase(numbered, BaseSide.Enemy, t.Base ?? 1, coord);
                    break;
                case TileType.DeployZone:
                    deployCells.Add(coord);
                    break;
            }
        }

        baseCells.Sort();
        deployCells.Sort();
        var zones = new List<SpawnZoneDefinition>();
        foreach (var (id, cells) in zoneCells)
        {
            cells.Sort();
            zones.Add(new SpawnZoneDefinition(id, map.ZoneName(id), cells));
        }

        var playerBase = baseCells.Count > 0 ? new PlayerBaseDefinition(map.BaseName, baseCells) : null;
        var bases = new List<MapBase>();
        foreach (var ((side, number), cells) in numbered)
        {
            cells.Sort();
            bases.Add(new MapBase(side, number, map.BaseTitle(side == BaseSide.Player ? MapBaseNameData.Player : MapBaseNameData.Enemy, number), cells));
        }

        var routes = map.Routes.Select(r => new EnemyRoute(r.Id, r.Name, r.Source, r.Target, r.Coords().ToList(), r.Enabled)).ToList();
        return new MapDefinition(mapItem.Name, grid, zones, playerBase, deployCells) { Bases = bases, Routes = routes };
    }

    private static void AddToBase(SortedDictionary<(BaseSide, int), List<HexCoord>> bases, BaseSide side, int number, HexCoord cell)
    {
        if (!bases.TryGetValue((side, number), out var cells))
        {
            bases[(side, number)] = cells = new List<HexCoord>();
        }

        cells.Add(cell);
    }

    /// <summary>
    /// Problems of the routes of a map against its real tiles (spec part 7 §4.3): a hex removed or closed under a route, a base
    /// gone. They do not stop free play on the map; a scenario that uses such a route cannot start (v0.36.0).
    /// </summary>
    public static IReadOnlyList<(MapRouteData Route, RouteProblem Problem)> CheckRoutes(MapDefinition map, MapData data)
    {
        var problems = new List<(MapRouteData, RouteProblem)>();
        foreach (var r in data.Routes)
        {
            foreach (var p in RouteCheck.Check(map, new EnemyRoute(r.Id, r.Name, r.Source, r.Target, r.Coords().ToList(), r.Enabled)))
            {
                problems.Add((r, p));
            }
        }

        return problems;
    }

    /// <summary>The runtime skill: cells (offsets for a caster looking North) with their effective effects.</summary>
    public static SkillDefinition BuildSkill(ContentItem skillItem, EffectTypeRegistry effects)
    {
        if (skillItem.Data is not SkillData skill)
        {
            throw new ArgumentException("Not a skill.", nameof(skillItem));
        }

        var cells = new List<SkillCell>(skill.Cells.Count);
        for (var c = 0; c < skill.Cells.Count; c++)
        {
            var cell = skill.Cells[c];
            var specs = new List<EffectSpec>();
            var own = skill.EffectsOf(cell);
            for (var e = 0; e < own.Count; e++)
            {
                // «group»: one effect of the skill over all its cells — base effect i, or a cell's own effect (spec part 5
                // §2.3: an area effect set to «split» shares its strength among the targets of its group in one hit).
                var spec = own[e].ToSpec(effects);
                var values = new Dictionary<string, object>(spec.Params, StringComparer.Ordinal) { ["group"] = cell.Inherits ? $"base.{e}" : $"cell.{c}.{e}" };
                specs.Add(new EffectSpec(spec.Type, values));
            }

            cells.Add(new SkillCell(cell.Offset, specs));
        }

        return new SkillDefinition { Id = skillItem.Id, Name = skillItem.Name, Icon = skill.Icon, Cells = cells };
    }

    /// <summary>The damage type catalog of the library: a type without a family is a family itself (spec part 5 §2.2).</summary>
    public static IReadOnlyDictionary<string, DamageTypeDefinition> BuildDamageTypes(ContentLibrary library)
    {
        var catalog = new Dictionary<string, DamageTypeDefinition>(StringComparer.Ordinal);
        foreach (var (id, builtIn) in HexTactics.Core.Combat.DamageTypes.BuiltIn)
        {
            catalog[id] = builtIn; // the fundamental built-ins exist even if a library misses a file
        }

        foreach (var item in library.Items(ContentTypeRegistry.DamageType))
        {
            if (item.Data is not DamageTypeData d || (item.HasErrors && !item.IsBuiltIn))
            {
                continue;
            }

            catalog[item.Id] = DamageTypeOf(item);
        }

        return catalog;
    }

    /// <summary>The runtime definition of one damage type object (also an unsaved one, for previews).</summary>
    public static DamageTypeDefinition DamageTypeOf(ContentItem item)
    {
        var d = (DamageTypeData)item.Data;
        return new DamageTypeDefinition(
            item.Id,
            item.Name,
            d.Family.Length == 0 ? item.Id : d.Family,
            d.Strategy == DamageTypeData.TrueLike ? DamageStrategy.TrueLike : DamageStrategy.ResistanceAffected,
            d.Tags.ToArray());
    }

    /// <summary>
    /// The runtime unit template. Defense rules are resolved by reference in <paramref name="library"/> (a rule that is
    /// missing or has errors is left out and reported in <paramref name="issues"/>); shields start full.
    /// </summary>
    public static UnitTemplate BuildUnit(ContentItem unitItem, ContentLibrary? library = null, IssueList? issues = null)
    {
        if (unitItem.Data is not UnitData unit)
        {
            throw new ArgumentException("Not a unit.", nameof(unitItem));
        }

        var rules = new List<DefenseRule>();
        foreach (var id in unit.DefenseRules.Distinct(StringComparer.Ordinal).Concat(PassiveEffects.GrantedRules(unit, library)))
        {
            if (library?.Find(ContentTypeRegistry.DefenseRule, id) is not { Data: DefenseRuleData rule } ruleItem)
            {
                issues?.Error(unitItem.Id, $"у юнита «{unitItem.Name}» правило защиты «{id}» не найдено");
                continue;
            }

            if (ruleItem.HasErrors)
            {
                issues?.Warning(ruleItem.Id, $"правило защиты «{ruleItem.Name}» с ошибками — не действует");
                continue;
            }

            rules.Add(rule.ToRule(ruleItem.Id, ruleItem.Name, library.Registry.DefenseActions));
        }

        var shields = unit.Shields
            .Select(s => new ShieldDefinition(s.Name, s.Capacity, s.DamageType == ShieldData.AnyType ? null : s.DamageType, s.Order))
            .ToArray();

        // Spec part 7 §8.2: the base attack by its reference; a missing or broken one means no automatic attack (never a guess).
        BasicAttackDefinition? basic = null;
        if (unit.BasicAttack.Length > 0)
        {
            if (library?.Find(ContentTypeRegistry.BasicAttack, unit.BasicAttack) is { Data: BasicAttackData basicData } basicItem && !basicItem.HasErrors)
            {
                basic = basicData.ToDefinition(basicItem.Id, basicItem.Name);
            }
            else if (library is null && unit.BasicAttack == BasicAttackDefinition.DefaultId)
            {
                basic = BasicAttackDefinition.Front;
            }
            else
            {
                issues?.Warning(unitItem.Id, $"у юнита «{unitItem.Name}» базовая атака «{unit.BasicAttack}» не найдена или с ошибками — автоатаки нет");
            }
        }

        // Spec part 6 §4: the passives change the characteristics, R and A here, once — the battle sees only the results.
        var stats = new UnitStats
        {
            MaxHealth = (int)Math.Round(PassiveEffects.Stat(unit, library, "maxHealth").Final),
            MoveSpeedHexPerSecond = PassiveEffects.Stat(unit, library, "moveSpeed").Final,
            AttackRatePerSecond = PassiveEffects.Stat(unit, library, "attackRate").Final,
            AttackPower = PassiveEffects.Stat(unit, library, "attackPower").Final,
            NormalAttackRangeHex = unit.AttackRange,
            AttackDamageTypeId = unit.AttackDamageType,
            AttackAreaRadius = unit.AttackAreaRadius,
            AttackAreaSplit = unit.AttackAreaSplit,
            Resistances = new ResistanceSet(PassiveEffects.Resistances(unit, library)),
            Armor = new ArmorSet(PassiveEffects.Armor(unit, library)),
            DamageModifiers = PassiveEffects.DamageModifiers(unit, library),
            CountModifiers = PassiveEffects.CountModifiers(unit, library),
            CritChance = PassiveEffects.CritChance(unit, library),
            CritImmune = PassiveEffects.Active(unit, library).Any(x => x.Passive.Operation == PassiveOperations.CritImmunity),
            SurpriseImmune = PassiveEffects.Active(unit, library).Any(x => x.Passive.Operation == PassiveOperations.SurpriseImmunity),
            SurpriseChanceBonus = PassiveEffects.SurpriseChanceBonus(unit, library),
            BasicAttack = basic,
            AutoAttackEnabled = unit.AutoAttackEnabled && basic is not null,
            // Spec part 7 §7.1: block is the characteristic «Блок» of the registry with the unit's own value, passives and tags.
            BlockCapacity = (int)Math.Max(0, Math.Floor(PassiveEffects.Stat(unit, library, UnitStatData.Block).Final + 1e-9)),
            Species = PassiveEffects.SpeciesChain(library, unit.Species).Select(i => i.Id).ToArray(),
            PackId = unit.PackId,
            AttackArmorPenetration = unit.AttackArmorPenetration,
            AttackArmorPenetrationPercent = unit.AttackArmorPenetrationPercent,
            DefenseRules = rules,
            Shields = shields,
        };
        return new UnitTemplate(unitItem.Id, unitItem.Name, stats, unit.Skills.ToArray(), unit.Side == "enemy" ? Side.Enemy : Side.Player, unit.Color, unit.Scale);
    }

    /// <summary>
    /// Builds a match from the library: the map, every valid skill, the default unit and the look of every tile type.
    /// Objects with errors are skipped and reported, never half-loaded.
    /// </summary>
    public static GameContent BuildGame(ContentLibrary library, string mapId = DefaultMapId, string unitId = DefaultUnitId, IReadOnlyList<string>? squad = null) =>
        BuildGame(library, library.Find(ContentTypeRegistry.Map, mapId) ?? throw new InvalidOperationException($"Map '{mapId}' is not in the content library."), unitId, squad);

    /// <summary>
    /// A match on a map that need not be saved (test runs from the map editor use a temporary copy). <paramref name="squad"/>
    /// lists the unit definitions of the five roster operatives (missing entries = <paramref name="unitId"/>);
    /// <paramref name="extraUnits"/> are unsaved unit definitions to include (test runs of the unit editor).
    /// </summary>
    public static GameContent BuildGame(ContentLibrary library, ContentItem mapItem, string unitId = DefaultUnitId, IReadOnlyList<string>? squad = null, IReadOnlyList<ContentItem>? extraUnits = null, ContentItem? testSkill = null, ContentItem? testTile = null)
    {
        var issues = new IssueList();
        var unitItem = library.Find(ContentTypeRegistry.Unit, unitId) ?? throw new InvalidOperationException($"Unit '{unitId}' is not in the content library.");

        var skills = new Dictionary<string, SkillDefinition>(StringComparer.Ordinal);
        var skillItems = new List<ContentItem>(library.Items(ContentTypeRegistry.Skill));
        if (testSkill is not null)
        {
            skillItems.RemoveAll(s => s.Id == testSkill.Id);
            skillItems.Add(testSkill);
        }

        foreach (var item in skillItems)
        {
            if (item.HasErrors && !ReferenceEquals(item, testSkill))
            {
                issues.Warning(item.Id, $"навык «{item.Name}» с ошибками — не загружен");
                continue;
            }

            skills[item.Id] = BuildSkill(item, library.Registry.Effects);
        }

        var damageTypes = BuildDamageTypes(library);
        var tiles = new Dictionary<string, TileVisual>(StringComparer.Ordinal);
        var tileEffects = new Dictionary<string, IReadOnlyList<TileEffect>>(StringComparer.Ordinal);
        var tileItems = new List<ContentItem>(library.Items(ContentTypeRegistry.Tile));
        if (testTile is not null)
        {
            tileItems.RemoveAll(t => t.Id == testTile.Id);
            tileItems.Add(testTile);
        }

        foreach (var item in tileItems)
        {
            tiles[item.Id] = Visual(item);
            if (item.HasErrors && !ReferenceEquals(item, testTile))
            {
                issues.Warning(item.Id, $"тип гекса «{item.Name}» с ошибками — его эффекты не действуют");
                continue;
            }

            if (BuildTileEffects(item, library.Registry.Effects) is { Count: > 0 } list)
            {
                tileEffects[item.Id] = list;
            }
        }

        var map = BuildMap(mapItem, library, issues);
        if (testTile?.Data is TileData tested)
        {
            // Tile test run: a block in front of the base becomes the tested type (an unsaved copy is fine).
            foreach (var cell in TileTestCells)
            {
                if (map.Grid.Contains(cell) && map.Grid.Exists(cell) && map.Grid[cell].Type == TileType.Walkable)
                {
                    map.Grid.SetTile(cell, TypeOf(tested) is TileType.Blocked ? TileType.Blocked : TileType.Walkable, testTile.Id, tested.Level, 0, tested.Waypoints);
                }
            }
        }

        var unit = BuildUnit(unitItem, library, issues);
        var known = new List<string>();
        foreach (var s in unit.Skills)
        {
            if (skills.ContainsKey(s))
            {
                known.Add(s);
            }
            else
            {
                issues.Error(unitItem.Id, $"у юнита «{unitItem.Name}» навык «{s}» не найден");
            }
        }

        var units = new Dictionary<string, UnitTemplate>(StringComparer.Ordinal);
        var unitItems = new List<ContentItem>(library.Items(ContentTypeRegistry.Unit));
        if (extraUnits is not null)
        {
            unitItems.RemoveAll(u => extraUnits.Any(e => e.Id == u.Id));
            unitItems.AddRange(extraUnits);
        }

        foreach (var item in unitItems)
        {
            if (item.HasErrors && (extraUnits is null || !extraUnits.Contains(item)))
            {
                continue;
            }

            var template = BuildUnit(item, library);
            units[item.Id] = template with { Skills = template.Skills.Where(skills.ContainsKey).ToArray() };
        }

        var defaultUnit = units.TryGetValue(unitItem.Id, out var known2) ? known2 : unit with { Skills = known };
        if (testSkill is not null)
        {
            // Skill test run: debug spawns (panel buttons, P) carry the tested skill first as well.
            defaultUnit = SkillFirst(defaultUnit, testSkill.Id);
        }

        var roster = new List<(string, UnitProfile)>();
        for (var i = 0; i < GameContent.OperativeNames.Count; i++)
        {
            var id = squad is not null && i < squad.Count ? squad[i] : unitItem.Id;
            var template = units.TryGetValue(id, out var t) ? t : defaultUnit;
            if (testSkill is not null)
            {
                // Skill test run: every operative has the tested skill first (R key, ТД menu), then its own ones.
                template = SkillFirst(template, testSkill.Id);
            }

            roster.Add((GameContent.OperativeNames[i], template.ToProfile()));
        }

        var rulesetId = mapItem.Data is MapData md ? md.EffectiveRuleset : CombatRulesetData.Legacy;
        if (library.Find(ContentTypeRegistry.CombatRuleset, rulesetId) is null)
        {
            issues.Warning(mapItem.Id, $"правила боя «{rulesetId}» не найдены — используются совместимые");
            rulesetId = CombatRulesetData.Legacy;
        }

        return new GameContent(map, skills, defaultUnit, tiles, issues.Items)
        {
            Units = units, Squad = roster, TileEffects = tileEffects, DamageTypes = damageTypes,
            CombatRules = BuildCombatRules(library, rulesetId), CombatRulesetId = rulesetId,
            AutoAttacks = mapItem.Data is MapData { AutoAttacks: true },
        };
    }

    /// <summary>
    /// The checks of a scenario that need its map and units (spec part 7 §2.2, M7-59): the map and every unit exist and are
    /// valid, every enemy stands on an existing walkable hex of the map, inside it and not twice. Errors block the battle start;
    /// the editor shows them with the object to fix.
    /// </summary>
    public static IReadOnlyList<ContentIssue> CheckScenario(ContentLibrary library, ContentItem scenarioItem)
    {
        var issues = new IssueList();
        if (scenarioItem.Data is not ScenarioData s)
        {
            issues.Error(scenarioItem.Id, "не сценарий");
            return issues.Items;
        }

        foreach (var issue in library.Validate(scenarioItem).Where(i => i.IsError))
        {
            issues.Error(issue.Path, issue.Message);
        }

        if (library.Find(ContentTypeRegistry.Map, s.Map) is not { } mapItem || mapItem.HasErrors)
        {
            issues.Error("map", $"карта «{s.Map}» не найдена или с ошибками — откройте «Карты» и исправьте её");
            return issues.Items;
        }

        var built = BuildMap(mapItem, library, new IssueList());
        var grid = built.Grid;
        if (s.IsTowerDefense || s.IsDefense)
        {
            CheckWaves(library, s, built, mapItem.Name, issues);
        }

        if (s.IsDefense)
        {
            // Spec part 7 §5.1: objects stand on real walkable hexes, one per hex, never under an enemy.
            var taken = s.Enemies.Select(e => e.Cell).ToHashSet();
            foreach (var (o, i) in s.Objects.Select((o, i) => (o, i)))
            {
                if (!grid.Exists(o.Cell) || !grid.IsWalkable(o.Cell))
                {
                    issues.Error($"objects[{i}]", $"объект «{o.Name}» на {o.Col},{o.Row} — там нет проходимого гекса карты «{mapItem.Name}»");
                }
                else if (!taken.Add(o.Cell))
                {
                    issues.Error($"objects[{i}]", $"объект «{o.Name}» на {o.Col},{o.Row} — гекс уже занят");
                }
            }
        }

        if (s.IsExploration)
        {
            CheckExploration(s, grid, mapItem.Name, issues);
        }
        else if (!grid.Coords.Any(c => grid.Exists(c) && HexTactics.Core.Spawning.PlacementRules.IsDeployTile(grid[c].Type)))
        {
            issues.Error("map", $"на карте «{mapItem.Name}» нет гексов выставления (база игрока или зона выставления) — отряду некуда выйти");
        }

        for (var i = 0; i < s.Enemies.Count; i++)
        {
            var e = s.Enemies[i];
            if (library.Find(ContentTypeRegistry.Unit, e.Unit) is not { } unit || unit.HasErrors)
            {
                issues.Error($"enemies.{i}", $"враг на {e.Col},{e.Row}: юнит «{e.Unit}» не найден или с ошибками");
            }
            else if (!grid.Exists(e.Cell) || !grid.IsWalkable(e.Cell))
            {
                issues.Error($"enemies.{i}", $"враг «{unit.Name}» стоит на {e.Col},{e.Row} — там нет проходимого гекса карты «{mapItem.Name}»");
            }
        }

        foreach (var id in s.Squad.Distinct(StringComparer.Ordinal))
        {
            if (library.Find(ContentTypeRegistry.Unit, id) is not { } unit || unit.HasErrors)
            {
                issues.Error("squad", $"в отряде юнит «{id}» не найден или с ошибками");
            }
        }

        if (s.Ruleset.Length > 0 && library.Find(ContentTypeRegistry.CombatRuleset, s.Ruleset) is null)
        {
            issues.Error("ruleset", $"правила боя «{s.Ruleset}» не найдены");
        }

        return issues.Items;
    }

    /// <summary>
    /// Exploration checks with the map (spec part 7 §6.2; M7-31, M7-35): entry and exit hexes are real walkable hexes, no enemy stands
    /// on the entry, and the exit can be reached from the entry over walkable hexes (units do not close the way — a fight may). A
    /// passage «opened by an event» does not exist yet, so an exit behind obstacles is an error, never a promise.
    /// </summary>
    private static void CheckExploration(ScenarioData s, HexGrid grid, string mapName, IssueList issues)
    {
        foreach (var (area, key, cells) in new[] { ("входа", "entry", s.Entry), ("выхода", "exit", s.Exit) })
        {
            foreach (var c in cells.Where(c => !grid.Exists(c.Cell) || !grid.IsWalkable(c.Cell)))
            {
                issues.Error(key, $"гекс {area} {c} — там нет проходимого гекса карты «{mapName}»");
            }
        }

        foreach (var e in s.Enemies.Where(e => s.Entry.Any(c => c.Cell == e.Cell)))
        {
            issues.Error("entry", $"враг стоит на гексе входа {e.Col},{e.Row} — отряду там не выйти");
        }

        var start = s.Entry.Select(c => c.Cell).Where(grid.IsWalkable).ToList();
        var exits = s.Exit.Select(c => c.Cell).Where(grid.IsWalkable).ToHashSet();
        if (start.Count > 0 && exits.Count > 0 && PathSteps(grid, start, exits) is null)
        {
            issues.Error("exit", $"выход недостижим от входа на карте «{mapName}»: нет пути по проходимым гексам — откройте проход в редакторе карт или перенесите вход/выход");
        }
    }

    /// <summary>Steps of the shortest way over walkable hexes from any start hex to any goal hex (units ignored); null — no way.</summary>
    public static int? PathSteps(HexGrid grid, IReadOnlyCollection<HexCoord> start, IReadOnlySet<HexCoord> goals)
    {
        var seen = new HashSet<HexCoord>(start);
        var frontier = new Queue<(HexCoord Cell, int Steps)>(start.Select(c => (c, 0)));
        while (frontier.Count > 0)
        {
            var (cell, steps) = frontier.Dequeue();
            if (goals.Contains(cell))
            {
                return steps;
            }

            foreach (var next in cell.Neighbors().Where(n => grid.IsWalkable(n) && seen.Add(n)))
            {
                frontier.Enqueue((next, steps + 1));
            }
        }

        return null;
    }

    /// <summary>
    /// Tower Defense checks with the map (spec part 7 §4.3, §4.6, M7-24): every enabled group refers to an existing, enabled route
    /// of the map that passes the route check (the same walkability for every enemy type of the line — the data has no movement
    /// types yet), to a library unit; the map has a player base.
    /// </summary>
    private static void CheckWaves(ContentLibrary library, ScenarioData s, MapDefinition map, string mapName, IssueList issues)
    {
        if (s.IsTowerDefense && !map.Bases.Any(b => b.Side == BaseSide.Player))
        {
            issues.Error("waves", $"на карте «{mapName}» нет базы игрока — врагам некуда идти"); // a defense needs none (spec part 7 §5.1)
        }

        for (var i = 0; i < s.Waves.Count; i++)
        {
            var w = s.Waves[i];
            if (!w.Enabled)
            {
                continue;
            }

            for (var k = 0; k < w.Groups.Count; k++)
            {
                var g = w.Groups[k];
                if (!g.Enabled)
                {
                    continue;
                }

                var at = $"волна «{w.Name}», группа {k + 1}";
                if (g.Route.Length == 0 && s.IsDefense)
                {
                    if (SourceCells(map, g.Source) is not { Count: > 0 } cells || !cells.Any(map.Grid.IsWalkable))
                    {
                        issues.Error($"waves[{i}].groups[{k}].source", $"{at}: источника «{g.Source}» нет на карте «{mapName}» — нужна база врага или зона появления с проходимыми гексами");
                    }
                }
                else if (map.FindRoute(g.Route) is not { } route)
                {
                    issues.Error($"waves[{i}].groups[{k}].route", $"{at}: маршрута «{g.Route}» нет на карте «{mapName}» — выберите другой или нарисуйте его в редакторе карт («Маршруты»)");
                }
                else if (!route.Enabled)
                {
                    issues.Error($"waves[{i}].groups[{k}].route", $"{at}: маршрут «{route.Name}» выключен на карте «{mapName}»");
                }
                else if (RouteCheck.Check(map, route) is { Count: > 0 } problems)
                {
                    issues.Error($"waves[{i}].groups[{k}].route", $"{at}: маршрут «{route.Name}» требует проверки — {problems[0].Message}");
                }

                if (g.Unit.Length > 0 && (library.Find(ContentTypeRegistry.Unit, g.Unit) is not { } unit || unit.HasErrors))
                {
                    issues.Error($"waves[{i}].groups[{k}].unit", $"{at}: юнит «{g.Unit}» не найден или с ошибками");
                }
            }
        }
    }

    /// <summary>
    /// A match of a scenario (spec part 7 §2.1, §3): its map, its squad as the roster, its enemies on their hexes, its rules
    /// (the scenario's ruleset over the map's), automatic attacks and blocking as the scenario says, and its goals. Throws when
    /// <see cref="CheckScenario"/> finds an error — a broken scenario never starts.
    /// </summary>
    public static GameContent BuildScenario(ContentLibrary library, ContentItem scenarioItem)
    {
        if (CheckScenario(library, scenarioItem) is { Count: > 0 } errors)
        {
            throw new InvalidOperationException("Сценарий с ошибками: " + string.Join("; ", errors.Select(e => e.Message)));
        }

        var s = (ScenarioData)scenarioItem.Data;
        var units = s.Squad.Concat(s.Enemies.Select(e => e.Unit)).Concat(s.IsTowerDefense || s.IsDefense ? s.Waves.Where(w => w.Enabled).SelectMany(w => w.Groups).Where(g => g.Enabled).Select(g => g.Unit) : Enumerable.Empty<string>())
            .Distinct(StringComparer.Ordinal).Select(id => library.Find(ContentTypeRegistry.Unit, id)!).ToList();
        var content = BuildGame(library, library.Find(ContentTypeRegistry.Map, s.Map)!, squad: s.Squad, extraUnits: units);
        var placed = s.Enemies.Select(e => new ScenarioUnit(content.Units[e.Unit].ToProfile(), e.Cell, e.Facing, Side.Enemy, AiOf(e.Ai), s.IsExploration && s.ExtraGoal == "kill_marked" && e.Target)).ToList();
        var rulesetId = s.Ruleset.Length > 0 ? s.Ruleset : content.CombatRulesetId;
        return content with
        {
            Squad = content.Squad.Take(s.Squad.Count).ToList(), // the roster is the squad as it is: three units, three cards (spec part 7 §3.2)
            AutoAttacks = s.AutoAttacks,
            Blocking = s.Blocking,
            CombatRules = BuildCombatRules(library, rulesetId),
            CombatRulesetId = rulesetId,
            Scenario = new ScenarioRuntime(scenarioItem.Id, scenarioItem.Name, ScenarioRulesOf(s, content), placed, s.GoalsText(), s.StartPaused),
        };
    }

    /// <summary>The cells of a wave source: «base:N» — enemy base N, «zone:N» — spawn zone N; null when there is none.</summary>
    public static IReadOnlyList<HexCoord>? SourceCells(MapDefinition map, string source)
    {
        var parts = source.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n))
        {
            return null;
        }

        return parts[0] switch
        {
            "base" => map.Bases.FirstOrDefault(b => b.Side == BaseSide.Enemy && b.Number == n)?.Cells,
            "zone" => map.FindZone(n)?.Cells,
            _ => null,
        };
    }

    /// <summary>The runtime AI profile of the data; null for «none».</summary>
    public static HexTactics.Core.Scenarios.AiProfile? AiOf(AiData ai) => ai.IsNone ? null : new HexTactics.Core.Scenarios.AiProfile(
        ai.Preset switch
        {
            "attack_units" => HexTactics.Core.Scenarios.AiPreset.AttackUnits,
            "attack_objective" => HexTactics.Core.Scenarios.AiPreset.AttackObjective,
            "units_then_objective" => HexTactics.Core.Scenarios.AiPreset.UnitsThenObjective,
            "objective_then_units" => HexTactics.Core.Scenarios.AiPreset.ObjectiveThenUnits,
            "guard" => HexTactics.Core.Scenarios.AiPreset.Guard,
            _ => HexTactics.Core.Scenarios.AiPreset.None,
        },
        ai.DetectRange,
        ai.Priority == "lowest_health" ? HexTactics.Core.Scenarios.AiPriority.LowestHealth : HexTactics.Core.Scenarios.AiPriority.Nearest,
        ai.Reevaluate,
        ai.GuardRadius,
        ai.SwitchToBlocker);

    /// <summary>The goals of the mode: the ordinary battle; Tower Defense waves, base lives and the defeat policy; or a defense.</summary>
    private static HexTactics.Core.Scenarios.ScenarioRules ScenarioRulesOf(ScenarioData s, GameContent content)
    {
        if (s.IsExploration)
        {
            return new HexTactics.Core.Scenarios.ScenarioRules
            {
                Exploration = true,
                EliminateAllEnemies = false,
                TimeLimitSeconds = s.TimeLimit,
                EntryCells = s.Entry.Select(c => c.Cell).Distinct().ToList(),
                ExitCells = s.Exit.Select(c => c.Cell).Distinct().ToList(),
                ExitRule = s.ExitRule switch { "any" => HexTactics.Core.Scenarios.ExitRule.AnySurvivor, "all" => HexTactics.Core.Scenarios.ExitRule.AllRequired, _ => HexTactics.Core.Scenarios.ExitRule.KeyUnit },
                KeyEntry = s.KeyUnit,
                RequiredEntries = s.Required.Distinct().ToList(),
                ExtraGoal = s.ExtraGoal switch { "kill_marked" => HexTactics.Core.Scenarios.ExtraGoal.KillMarked, "clear_map" => HexTactics.Core.Scenarios.ExtraGoal.ClearMap, _ => HexTactics.Core.Scenarios.ExtraGoal.None },
            };
        }

        if (!s.IsTowerDefense && !s.IsDefense)
        {
            return new HexTactics.Core.Scenarios.ScenarioRules { TimeLimitSeconds = s.TimeLimit };
        }

        var waves = s.Waves.Where(w => w.Enabled).Select(w => new HexTactics.Core.Scenarios.WaveDefinition(
            w.Id,
            w.Name,
            w.Start switch { WaveData.AtTime => HexTactics.Core.Scenarios.WaveStart.AtTime, WaveData.AfterPreviousStart => HexTactics.Core.Scenarios.WaveStart.AfterPreviousStart, _ => HexTactics.Core.Scenarios.WaveStart.AfterPrevious },
            w.Delay,
            w.Groups.Where(g => g.Enabled && g.Count > 0).Select(g => new HexTactics.Core.Scenarios.SpawnGroupDefinition(
                g.Id, g.Route.Length > 0 ? content.Map.FindRoute(g.Route)! : null, content.Units[g.Unit].ToProfile(), g.Count, g.FirstDelay, g.Interval, g.Offset)
            {
                SourceCells = g.Route.Length > 0 ? Array.Empty<HexCoord>() : SourceCells(content.Map, g.Source) ?? Array.Empty<HexCoord>(),
                Ai = g.Route.Length > 0 ? null : AiOf(g.Ai),
            }).ToList())).ToList();
        if (s.IsDefense)
        {
            return new HexTactics.Core.Scenarios.ScenarioRules
            {
                Defense = true,
                EliminateAllEnemies = false,
                DefeatWhenSquadLost = s.SquadLossDefeat,
                TimeLimitSeconds = s.SurviveSeconds > 0 ? 0 : s.TimeLimit,
                SurviveSeconds = s.SurviveSeconds,
                Waves = waves,
                Objects = s.Objects.Select(o => new HexTactics.Core.Scenarios.MissionObject(o.Id, o.Name, o.Cell, o.Health, o.Critical)).ToList(),
                ObjectsPolicy = s.ObjectsPolicy == "any" ? HexTactics.Core.Scenarios.ObjectsPolicy.AnyMustSurvive : HexTactics.Core.Scenarios.ObjectsPolicy.AllMustSurvive,
            };
        }

        var bases = content.Map.Bases.Where(b => b.Side == BaseSide.Player)
            .Select(b => new HexTactics.Core.Scenarios.BaseLives(b.Number, s.LivesOf(b.Number).Lives, s.LivesOf(b.Number).Critical)).ToList();
        return new HexTactics.Core.Scenarios.ScenarioRules
        {
            TowerDefense = true,
            EliminateAllEnemies = false,
            DefeatWhenSquadLost = false,
            TimeLimitSeconds = s.TimeLimit,
            Waves = waves,
            Bases = bases,
            Defeat = s.Defeat switch { "all_bases" => HexTactics.Core.Scenarios.DefeatPolicy.AllBases, "shared_lives" => HexTactics.Core.Scenarios.DefeatPolicy.SharedLives, _ => HexTactics.Core.Scenarios.DefeatPolicy.AnyCriticalBase },
        };
    }

    /// <summary>
    /// Combat rules of a <c>combat_ruleset</c> (spec part 6 §3.2): K and K per family. A missing or broken ruleset gives the
    /// defaults (K = 100), which leave units without defense A unchanged.
    /// </summary>
    public static CombatRules BuildCombatRules(ContentLibrary library, string rulesetId, ContentItem? draft = null)
    {
        var item = draft is not null && draft.Id == rulesetId ? draft : library.Find(ContentTypeRegistry.CombatRuleset, rulesetId);
        if (item?.Data is not CombatRulesetData data || (item.HasErrors && !ReferenceEquals(item, draft)))
        {
            return CombatRules.Default;
        }

        return new CombatRules
        {
            DefenseK = Math.Clamp(data.DefenseK, CombatRules.MinDefenseK, CombatRules.MaxDefenseK),
            DefenseKByFamily = data.DefenseKByFamily.Where(x => double.IsFinite(x.Value) && x.Value > 0)
                .ToDictionary(x => x.Key, x => Math.Clamp(x.Value, CombatRules.MinDefenseK, CombatRules.MaxDefenseK), StringComparer.Ordinal),
            SurpriseEnabled = data.SurpriseEnabled,
            SurpriseChance = data.SurpriseChances.Length == 6 ? data.SurpriseChances.Select(c => Math.Clamp(c, 0, 100)).ToArray() : new double[6],
            SurpriseMultiplier = Math.Max(0, data.SurpriseMultiplier),
            SurpriseMeleeOnly = data.SurpriseMeleeOnly,
            CritEnabled = data.CritEnabled,
            BaseCritChance = data.BaseCritChance,
            CritMultiplier = Math.Max(0, data.CritMultiplier),
            SuperCritMultiplier = Math.Max(0, data.SuperCritMultiplier),
        };
    }

    private static UnitTemplate SkillFirst(UnitTemplate template, string skillId) =>
        template with { Skills = new[] { skillId }.Concat(template.Skills.Where(s => s != skillId)).ToArray() };
}
