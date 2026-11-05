using System.Runtime.CompilerServices;

// The simulation drives movement state (UnitMotion) whose setters are internal to keep the
// public model read-only for the presentation layer.
[assembly: InternalsVisibleTo("HexTactics.Simulation")]
[assembly: InternalsVisibleTo("HexTactics.Simulation.Tests")]
[assembly: InternalsVisibleTo("HexTactics.Core.Tests")]
