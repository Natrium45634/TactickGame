using System;
using System.Collections.Generic;
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
public sealed record TileVisual(string Id, string Name, string Color, int Level, TileType Type);

/// <summary>A unit definition turned into runtime values.</summary>
public sealed record UnitTemplate(string Id, string Name, UnitStats Stats, IReadOnlyList<string> Skills, Side Side, string Color, double Scale);

/// <summary>Everything a match needs, built from content definitions by one factory (spec part 4 §9.2 <c>RuntimeFactory</c>).</summary>
public sealed record GameContent(
    MapDefinition Map,
    IReadOnlyDictionary<string, SkillDefinition> Skills,
    UnitTemplate DefaultUnit,
    IReadOnlyDictionary<string, TileVisual> TileVisuals,
    IReadOnlyList<ContentIssue> Issues);

/// <summary>
/// Turns library definitions into the runtime model of Core (spec part 4 §8.4: a definition is not the live object).
/// The same factory serves the «Играть» mode and test runs from the editors, so both use one core and one set of rules.
/// </summary>
public static class RuntimeFactory
{
    public const string DefaultMapId = "core.map.prototype";
    public const string DefaultUnitId = "core.unit.operative";

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
        tile.Data is TileData t ? new TileVisual(tile.Id, tile.Name, t.Color, t.Level, TypeOf(t)) : throw new ArgumentException("Not a tile type.", nameof(tile));

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
            grid.SetTile(coord, tileType, t.Tile, t.Level ?? type.Level, zone);
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

        return new SkillDefinition { Id = skillItem.Id, Name = skillItem.Name, Cells = cells };
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
            NormalAttackDamage = (int)Math.Round(unit.AttackPower),
            NormalAttackRangeHex = unit.AttackRange,
        };
        return new UnitTemplate(unitItem.Id, unitItem.Name, stats, unit.Skills.ToArray(), unit.Side == "enemy" ? Side.Enemy : Side.Player, unit.Color, unit.Scale);
    }

    /// <summary>
    /// Builds a match from the library: the map, every valid skill, the default unit and the look of every tile type.
    /// Objects with errors are skipped and reported, never half-loaded.
    /// </summary>
    public static GameContent BuildGame(ContentLibrary library, string mapId = DefaultMapId, string unitId = DefaultUnitId)
    {
        var issues = new IssueList();
        var mapItem = library.Find(ContentTypeRegistry.Map, mapId) ?? throw new InvalidOperationException($"Map '{mapId}' is not in the content library.");
        var unitItem = library.Find(ContentTypeRegistry.Unit, unitId) ?? throw new InvalidOperationException($"Unit '{unitId}' is not in the content library.");

        var skills = new Dictionary<string, SkillDefinition>(StringComparer.Ordinal);
        foreach (var item in library.Items(ContentTypeRegistry.Skill))
        {
            if (item.HasErrors)
            {
                issues.Warning(item.Id, $"навык «{item.Name}» с ошибками — не загружен");
                continue;
            }

            skills[item.Id] = BuildSkill(item, library.Registry.Effects);
        }

        var tiles = new Dictionary<string, TileVisual>(StringComparer.Ordinal);
        foreach (var item in library.Items(ContentTypeRegistry.Tile))
        {
            tiles[item.Id] = Visual(item);
        }

        var map = BuildMap(mapItem, library, issues);
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

        return new GameContent(map, skills, unit with { Skills = known }, tiles, issues.Items);
    }
}
