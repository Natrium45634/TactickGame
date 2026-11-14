using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Map;
using HexTactics.Core.Roster;
using HexTactics.Core.Scenarios;
using HexTactics.Core.Units;

namespace HexTactics.Simulation;

/// <summary>
/// Goals of a scenario (spec part 7 §3.2, §4.6, §11.1 p.8–9): the last system of the tick settles leaks into the player bases and
/// then checks victory and defeat once, from the confirmed state (deaths of this tick included). The first running tick turns
/// «Preparing» into «Running»; the scenario clock counts only running game time (never the pause).
/// <list type="bullet">
/// <item>Ordinary battle: victory before defeat — when the last enemy and the last unit of the player fall in the same tick the
/// player wins.</item>
/// <item>Tower Defense: an enemy whose route ends on its player base leaks once — when it reached the last visit, or stands before
/// it while a unit holds that base hex (the last border is enough); it leaves the map and only that base loses a life. Defeat by
/// the base lives is checked before victory (all waves resolved, no enemy left): bases must survive.</item>
/// <item>Exploration: a unit standing on an exit hex has reached the exit — remembered once, with <see cref="ExitReached"/>. Defeat
/// first (the key unit or a required member died, or the squad is lost), then victory when the exit rule is met by living units and
/// the extra task is done; killing every enemy is not a goal by itself (M7-34).</item>
/// </list>
/// Without scenario rules (free play, tests) it does nothing.
/// </summary>
internal sealed class ObjectiveSystem : ISimulationSystem
{
    private readonly GameState _state;
    private readonly List<SimulationEvent> _events;
    private readonly ScenarioRules? _rules;
    private readonly WaveSystem? _waves;
    private readonly SortedDictionary<int, int> _lives = new();
    private readonly List<(MissionObject Object, UnitId Unit)> _objects = new();
    private int _sharedLives;
    private readonly HashSet<UnitId> _arrived = new();
    private readonly List<UnitId> _marked = new();
    private readonly HashSet<HexTactics.Core.Hex.HexCoord> _exit = new();

    public ObjectiveSystem(GameState state, List<SimulationEvent> events, ScenarioRules? rules, WaveSystem? waves = null)
    {
        _state = state;
        _events = events;
        _rules = rules;
        _waves = waves;
        foreach (var b in rules?.Bases ?? System.Array.Empty<BaseLives>())
        {
            _lives[b.Base] = b.Lives;
            _sharedLives += b.Lives;
        }
    }

    public string Name => "Objectives";

    public ScenarioPhase Phase { get; private set; } = ScenarioPhase.Preparing;

    public ScenarioEndReason? Reason { get; private set; }

    /// <summary>Game seconds of the running phase.</summary>
    public double Elapsed { get; private set; }

    /// <summary>Enemies of the scenario still alive (the goal «победить всех врагов»).</summary>
    public int EnemiesLeft => _state.Units.Count(u => u.Side == Side.Enemy && u.IsAlive);

    /// <summary>Lives left per player base number (Tower Defense).</summary>
    public IReadOnlyDictionary<int, int> BaseLives => _lives;

    /// <summary>The shared pool of lives (meaningful with <see cref="DefeatPolicy.SharedLives"/>).</summary>
    public int SharedLives => _sharedLives;

    /// <summary>The defended objects of the mission and their units.</summary>
    public IReadOnlyList<(MissionObject Object, UnitId Unit)> Objects => _objects;

    public void RegisterObject(MissionObject obj, UnitId unit) => _objects.Add((obj, unit));

    /// <summary>An enemy of the extra task «defeat the marked enemies» (spec part 7 §6.1).</summary>
    public void MarkTarget(UnitId unit) => _marked.Add(unit);

    /// <summary>Exploration: the units that have reached the exit (remembered even if they leave it).</summary>
    public IReadOnlyCollection<UnitId> Arrived => _arrived;

    /// <summary>Exploration: what is done and what is left (null outside an exploration).</summary>
    public ExplorationProgress? Progress => _rules is { Exploration: true } ? new ExplorationProgress(ExitDone(), Waiting(), MarkedLeft, EnemiesLeft, ExtraDone()) : null;

    private int MarkedLeft => _marked.Count(id => _state.FindUnit(id) is { IsAlive: true });

    public void Tick(SimulationContext context)
    {
        if (_rules is null || Phase is ScenarioPhase.Victory or ScenarioPhase.Defeat)
        {
            return;
        }

        Phase = ScenarioPhase.Running;
        Elapsed += context.DeltaSeconds;
        if (_rules.Exploration)
        {
            Arrivals(context.Tick);
            if (ExplorationLost() is { } lost)
            {
                End(ScenarioPhase.Defeat, lost, context.Tick);
            }
            else if (ExitDone() && ExtraDone())
            {
                End(ScenarioPhase.Victory, ScenarioEndReason.ExitReached, context.Tick);
            }
            else if (_rules.TimeLimitSeconds > 0 && Elapsed + 1e-9 >= _rules.TimeLimitSeconds)
            {
                End(ScenarioPhase.Defeat, ScenarioEndReason.TimeUp, context.Tick);
            }

            return;
        }

        if (_rules.Defense)
        {
            // Spec part 7 §5.3: losses are decisive — a lost object or squad ends the battle even on the second of victory.
            if (ObjectLost())
            {
                End(ScenarioPhase.Defeat, ScenarioEndReason.ObjectLost, context.Tick);
            }
            else if (_rules.DefeatWhenSquadLost && SquadLost())
            {
                End(ScenarioPhase.Defeat, ScenarioEndReason.SquadLost, context.Tick);
            }
            else if (_rules.SurviveSeconds > 0 && Elapsed + 1e-9 >= _rules.SurviveSeconds)
            {
                End(ScenarioPhase.Victory, ScenarioEndReason.Survived, context.Tick);
            }
            else if (_rules.SurviveSeconds <= 0 && (_waves?.AllResolved ?? true) && EnemiesLeft == 0)
            {
                End(ScenarioPhase.Victory, ScenarioEndReason.AllWavesCleared, context.Tick);
            }
            else if (_rules.TimeLimitSeconds > 0 && Elapsed + 1e-9 >= _rules.TimeLimitSeconds)
            {
                End(ScenarioPhase.Defeat, ScenarioEndReason.TimeUp, context.Tick);
            }

            return;
        }

        if (_rules.TowerDefense)
        {
            Leaks(context.Tick);
            if (BaseLost())
            {
                End(ScenarioPhase.Defeat, ScenarioEndReason.BaseLost, context.Tick);
            }
            else if ((_waves?.AllResolved ?? true) && EnemiesLeft == 0)
            {
                End(ScenarioPhase.Victory, ScenarioEndReason.AllWavesCleared, context.Tick);
            }
            else if (_rules.TimeLimitSeconds > 0 && Elapsed + 1e-9 >= _rules.TimeLimitSeconds)
            {
                End(ScenarioPhase.Defeat, ScenarioEndReason.TimeUp, context.Tick);
            }

            return;
        }

        if (_rules.EliminateAllEnemies && EnemiesLeft == 0)
        {
            End(ScenarioPhase.Victory, ScenarioEndReason.AllEnemiesDefeated, context.Tick);
        }
        else if (_rules.DefeatWhenSquadLost && SquadLost())
        {
            End(ScenarioPhase.Defeat, ScenarioEndReason.SquadLost, context.Tick);
        }
        else if (_rules.TimeLimitSeconds > 0 && Elapsed + 1e-9 >= _rules.TimeLimitSeconds)
        {
            End(ScenarioPhase.Defeat, ScenarioEndReason.TimeUp, context.Tick);
        }
    }

    /// <summary>Units of the player on an exit hex reach the exit, in id order, each once (spec part 7 §6.1 <c>ExitReached</c>).</summary>
    private void Arrivals(long tick)
    {
        if (_exit.Count == 0)
        {
            _exit.UnionWith(_rules!.ExitCells);
        }

        foreach (var unit in _state.Units.Where(u => u.Side == Side.Player && u.IsAlive && u.Role == UnitRole.Combatant && _exit.Contains(u.Cell)).OrderBy(u => u.Id.Value))
        {
            if (_arrived.Add(unit.Id))
            {
                _events.Add(new ExitReached(tick, unit.Id, EntryOf(unit.Id)));
            }
        }
    }

    private int? EntryOf(UnitId unit) => _state.Roster.Entries.FirstOrDefault(e => e.Unit == unit)?.Id;

    private bool AliveAndArrived(UnitId? unit) => unit is { } id && _arrived.Contains(id) && _state.FindUnit(id) is { IsAlive: true };

    /// <summary>A roster card whose unit was placed and is no longer alive.</summary>
    private bool EntryLost(int entry) =>
        _state.Roster.Entries.FirstOrDefault(e => e.Id == entry) is { Unit: { } id } && _state.FindUnit(id) is not { IsAlive: true };

    private ScenarioEndReason? ExplorationLost()
    {
        switch (_rules!.ExitRule)
        {
            case ExitRule.KeyUnit when EntryLost(_rules.KeyEntry):
                return ScenarioEndReason.KeyUnitLost;
            case ExitRule.AllRequired when _rules.RequiredEntries.Any(EntryLost):
                return ScenarioEndReason.RequiredLost;
        }

        return SquadLost() ? ScenarioEndReason.SquadLost : null;
    }

    private bool ExitDone() => _rules!.ExitRule switch
    {
        ExitRule.AnySurvivor => _arrived.Any(id => AliveAndArrived(id)),
        ExitRule.AllRequired => _rules.RequiredEntries.Count > 0 && _rules.RequiredEntries.All(e => AliveAndArrived(UnitOf(e))),
        _ => AliveAndArrived(UnitOf(_rules.KeyEntry)),
    };

    /// <summary>The roster cards that still have to reach the exit (for «осталось»).</summary>
    private IReadOnlyList<int> Waiting()
    {
        var rule = _rules!.ExitRule;
        if (rule == ExitRule.AnySurvivor)
        {
            return ExitDone() ? System.Array.Empty<int>() : _state.Roster.Entries.Select(e => e.Id).ToList();
        }

        var needed = rule == ExitRule.AllRequired ? _rules.RequiredEntries : new[] { _rules.KeyEntry };
        return needed.Where(e => !AliveAndArrived(UnitOf(e))).ToList();
    }

    private UnitId? UnitOf(int entry) => _state.Roster.Entries.FirstOrDefault(e => e.Id == entry)?.Unit;

    private bool ExtraDone() => _rules!.ExtraGoal switch
    {
        ExtraGoal.KillMarked => MarkedLeft == 0,
        ExtraGoal.ClearMap => EnemiesLeft == 0,
        _ => true,
    };

    /// <summary>Enemies at the end of their routes leak into their own base, in id order, each once.</summary>
    private void Leaks(long tick)
    {
        foreach (var unit in _state.Units.Where(u => u.IsAlive && u.Route is not null).ToList())
        {
            var route = unit.Route!;
            var cells = route.Route.Cells;
            var last = cells[^1];
            if (_state.Map.BaseAt(last) is not { Side: BaseSide.Player } target || target.Number != route.Route.TargetBase)
            {
                continue; // a route that does not end on its player base (debug spawns, broken data) never leaks
            }

            var arrived = route.Finished && unit.Cell == last && !unit.Motion.IsStepping;
            var atBorder = route.Index == cells.Count - 2 && unit.Cell == cells[^2] && !unit.Motion.IsStepping
                && _state.UnitAt(last) is { } holder && holder.Id != unit.Id;
            if (!arrived && !atBorder)
            {
                continue;
            }

            _state.RemoveFromMap(unit.Id);
            var left = 0;
            if (_lives.TryGetValue(target.Number, out var lives))
            {
                left = System.Math.Max(0, lives - 1);
                _lives[target.Number] = left;
            }

            _sharedLives = System.Math.Max(0, _sharedLives - 1);
            _events.Add(new EnemyLeaked(tick, unit.Id, route.Route.Id, target.Number, _rules!.Defeat == DefeatPolicy.SharedLives ? _sharedLives : left));
        }
    }

    private bool BaseLost()
    {
        if (_rules!.Bases.Count == 0)
        {
            return false;
        }

        return _rules.Defeat switch
        {
            DefeatPolicy.AllBases => _lives.Values.All(l => l <= 0),
            DefeatPolicy.SharedLives => _sharedLives <= 0,
            _ => _rules.Bases.Any(b => b.Critical && _lives.TryGetValue(b.Base, out var l) && l <= 0),
        };
    }

    /// <summary>Critical objects lost under the policy: one of them (all must survive) or every one (at least one must).</summary>
    private bool ObjectLost()
    {
        var critical = _objects.Where(o => o.Object.Critical).ToList();
        if (critical.Count == 0)
        {
            return false; // a non-critical object may fall (spec part 7 M7-28)
        }

        bool Lost((MissionObject Object, UnitId Unit) o) => _state.FindUnit(o.Unit) is not { IsAlive: true };
        return _rules!.ObjectsPolicy == ObjectsPolicy.AnyMustSurvive ? critical.All(Lost) : critical.Any(Lost);
    }

    /// <summary>No living fighter of the player on the map and no roster card that can still be deployed (spec part 7 §3.2).</summary>
    private bool SquadLost() =>
        !_state.Units.Any(u => u.Side == Side.Player && u.IsAlive && u.Role == UnitRole.Combatant)
        && !_state.Roster.Entries.Any(e => e.State is RosterEntryState.AvailableInRoster or RosterEntryState.PlacementPreview); // placed ones are units

    private void End(ScenarioPhase phase, ScenarioEndReason reason, long tick)
    {
        Phase = phase;
        Reason = reason;
        _events.Add(new ScenarioEnded(tick, phase, reason));
    }
}
