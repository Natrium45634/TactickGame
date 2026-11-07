using System.Collections.Generic;
using HexTactics.Core.Combat;

namespace HexTactics.Core.Units;

/// <summary>
/// What a new unit is made of (spec part 4 §5, §8.4): the unit definition id, its display name, characteristics and
/// skill set. Built from a content <c>UnitDefinition</c>; the live unit keeps its own current health, path and facing.
/// </summary>
public sealed record UnitProfile(string DefinitionId, string Name, UnitStats Stats, IReadOnlyList<string> Skills);
