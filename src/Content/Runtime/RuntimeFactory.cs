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
        return new MapDefinition(mapItem.Name, grid, zones, playerBase, deployCells);
    }

    /// <summary>The runtime skill: cells (offsets for a caster looking North) with their effective effects.</summary>
    public static SkillDefinition BuildSkill(ContentItem skillItem, EffectTypeRegistry effects)
    {
        if (skillItem.Data is not SkillData skill)
        {
            throw new ArgumentException("Not a skill.", nameof(skillItem));
        }

        var cells = new List<SkillCell>(skill.Cells.Count);
        foreach (var cell in skill.Cells)
        {
            var specs = new List<EffectSpec>();
            foreach (var effect in skill.EffectsOf(cell))
            {
                specs.Add(effect.ToSpec(effects));
            }

            cells.Add(new SkillCell(cell.Offset, specs));
        }

        return new SkillDefinition { Id = skillItem.Id, Name = skillItem.Name, Icon = skill.Icon, Cells = cells };
    }

    public static UnitTemplate BuildUnit(ContentItem unitItem)
    {
        if (unitItem.Data is not UnitData unit)
        {
            throw new ArgumentException("Not a unit.", nameof(unitItem));
        }

        var stats = new UnitStats
        {
            MaxHealth = (int)Math.Round(unit.MaxHealth),
            MoveSpeedHexPerSecond = unit.MoveSpeed,
            AttackRatePerSecond = unit.AttackRate,
            AttackPower = unit.AttackPower,
            NormalAttackRangeHex = unit.AttackRange,
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

        var unit = BuildUnit(unitItem);
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

            var template = BuildUnit(item);
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

        return new GameContent(map, skills, defaultUnit, tiles, issues.Items) { Units = units, Squad = roster, TileEffects = tileEffects };
    }

    private static UnitTemplate SkillFirst(UnitTemplate template, string skillId) =>
        template with { Skills = new[] { skillId }.Concat(template.Skills.Where(s => s != skillId)).ToArray() };
}
