using System;
using Godot;

namespace HexTactics.Client.App;

/// <summary>
/// The only way the application quits. C# wrappers of Godot resources that the scene no longer uses (meshes, materials of
/// freed nodes) are finalized by the .NET garbage collector at an arbitrary moment; if that moment falls into the engine's
/// teardown, the finalizer touches freed native memory and the process crashes on exit (seen under Wine after long editor
/// sessions). Collecting and running the pending finalizers while the engine is still alive removes that window.
/// </summary>
public static class AppExit
{
    public static void Quit(SceneTree tree, int exitCode = 0)
    {
        FlushFinalizers();
        tree.Quit(exitCode);
    }

    /// <summary>Runs the finalizers of every unreachable wrapper now, while the engine can still release them.</summary>
    public static void FlushFinalizers()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
