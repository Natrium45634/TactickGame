using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;
using HexTactics.Core.Scenarios;
using HexTactics.Core.Units;

namespace HexTactics.Simulation;

/// <summary>A planned enemy of a wave waiting for its entrance hex (spec part 7 §4.5: the queue keeps order, count and route).</summary>
public sealed record PendingSpawn(int Wave, int Group, int Index, SpawnGroupDefinition Definition, double DueTime);

/// <summary>
/// Waves of a Tower Defense scenario (spec part 7 §4.5, §11.1 p.2–3): the first system of the tick. Scenario time runs only on
/// running ticks (never on the pause). A wave starts by its own rule; each due enemy joins the queue of its route's first hex in a
/// fixed order (wave, group, index); a queue lets in one enemy per tick when its hex is free and not reserved — never two units in
/// one hex, no free progress while waiting. A wave is resolved when all its enemies were let in and none of them is on the map.
/// </summary>
internal sealed class WaveSystem : ISimulationSystem
{
    private const double Epsilon = 1e-9;

    private readonly GameState _state;
    private readonly List<SimulationEvent> _events;
    private readonly IReadOnlyList<WaveDefinition> _waves;
    private readonly Func<SpawnGroupDefinition, HexCoord, UnitId?> _spawn;
    private readonly double?[] _started;
    private readonly double?[] _resolved;
    private readonly int[][] _nextIndex;
    private readonly HashSet<UnitId>[] _members;
    private readonly List<(HexCoord Cell, Queue<PendingSpawn> Queue)> _queues = new();

    public WaveSystem(GameState state, List<SimulationEvent> events, IReadOnlyList<WaveDefinition> waves, Func<SpawnGroupDefinition, HexCoord, UnitId?> spawn)
    {
        _state = state;
        _events = events;
        _waves = waves;
        _spawn = spawn;
        _started = new double?[waves.Count];
        _resolved = new double?[waves.Count];
        _nextIndex = waves.Select(w => new int[w.Groups.Count]).ToArray();
        _members = waves.Select(_ => new HashSet<UnitId>()).ToArray();
    }

    public string Name => "Waves";

    /// <summary>Game seconds of the running scenario.</summary>
    public double Time { get; private set; }

    public int WaveCount => _waves.Count;

    /// <summary>Waves started so far (the HUD shows «волна 2 из 3»).</summary>
    public int StartedCount => _started.Count(s => s is not null);

    public bool AllResolved => _resolved.All(r => r is not null);

    public bool IsStarted(int wave) => _started[wave] is not null;

    public bool IsResolved(int wave) => _resolved[wave] is not null;

    /// <summary>Enemies waiting for a free entrance hex.</summary>
    public int Queued => _queues.Sum(q => q.Queue.Count);

    /// <summary>Enemies of the waves that have not appeared yet (planned or queued) — the «впереди» number.</summary>
    public int Upcoming
    {
        get
        {
            var future = 0;
            for (var w = 0; w < _waves.Count; w++)
            {
                for (var g = 0; g < _waves[w].Groups.Count; g++)
                {
                    future += _waves[w].Groups[g].Count - _nextIndex[w][g];
                }
            }

            return future + Queued;
        }
    }

    public void Tick(SimulationContext context)
    {
        Time += context.DeltaSeconds;
        for (var w = 0; w < _waves.Count; w++)
        {
            if (_started[w] is null && StartTime(w) is { } start && Time + Epsilon >= start)
            {
                _started[w] = Time;
                _events.Add(new WaveStarted(context.Tick, w, _waves[w].Id, _waves[w].Total));
            }

            if (_started[w] is { } begun)
            {
                Plan(w, begun);
            }
        }

        LetIn(context.Tick);
        for (var w = 0; w < _waves.Count; w++)
        {
            _members[w].RemoveWhere(id => _state.FindUnit(id) is not { IsAlive: true });
            if (_started[w] is not null && _resolved[w] is null && Done(w))
            {
                _resolved[w] = Time;
                _events.Add(new WaveResolved(context.Tick, w, _waves[w].Id));
            }
        }
    }

    /// <summary>When wave <paramref name="w"/> starts by its rule; null — not yet decidable (the previous wave is not done).</summary>
    private double? StartTime(int w)
    {
        var wave = _waves[w];
        switch (wave.Start)
        {
            case WaveStart.AtTime:
                return wave.StartSeconds;
            case WaveStart.AfterPreviousStart:
                return w == 0 ? wave.StartSeconds : _started[w - 1] is { } prev ? prev + wave.StartSeconds : null;
            default:
                return w == 0 ? wave.StartSeconds : _resolved[w - 1] is { } done ? done + wave.StartSeconds : null;
        }
    }

    /// <summary>Every enemy of the wave whose time has come joins the queue of its entrance hex (fixed order).</summary>
    private void Plan(int w, double start)
    {
        var groups = _waves[w].Groups;
        for (var g = 0; g < groups.Count; g++)
        {
            var group = groups[g];
            while (_nextIndex[w][g] < group.Count && Time + Epsilon >= group.SpawnTime(start, _nextIndex[w][g]))
            {
                var k = _nextIndex[w][g]++;
                QueueFor(group.Entrance[0]).Enqueue(new PendingSpawn(w, g, k, group, group.SpawnTime(start, k)));
            }
        }
    }

    private Queue<PendingSpawn> QueueFor(HexCoord cell)
    {
        foreach (var (c, q) in _queues)
        {
            if (c == cell)
            {
                return q;
            }
        }

        var queue = new Queue<PendingSpawn>();
        _queues.Add((cell, queue));
        return queue;
    }

    /// <summary>
    /// Each entrance lets in its waiting enemies in order while a hex of it is free (the route's first visit — one per tick; a
    /// source — its free hexes in row-major order); the others keep their places.
    /// </summary>
    private void LetIn(long tick)
    {
        foreach (var (_, queue) in _queues)
        {
            // Every free hex of the entrance takes the next waiting enemy (a route has one entrance hex; a source may have several).
            while (queue.Count > 0)
            {
                HexCoord? free = null;
                foreach (var cell in queue.Peek().Definition.Entrance)
                {
                    if (_state.UnitAt(cell) is null && _state.ReservedBy(cell) is null)
                    {
                        free = cell;
                        break;
                    }
                }

                if (free is not { } at)
                {
                    break;
                }

                var next = queue.Dequeue();
                if (_spawn(next.Definition, at) is not { } unit)
                {
                    break;
                }

                _members[next.Wave].Add(unit);
                _events.Add(new WaveEnemySpawned(tick, unit, next.Wave, next.Definition.Route?.Id ?? string.Empty));
            }
        }
    }

    private bool Done(int w)
    {
        var groups = _waves[w].Groups;
        for (var g = 0; g < groups.Count; g++)
        {
            if (_nextIndex[w][g] < groups[g].Count)
            {
                return false;
            }
        }

        return _members[w].Count == 0 && !_queues.Any(q => q.Queue.Any(p => p.Wave == w));
    }
}
