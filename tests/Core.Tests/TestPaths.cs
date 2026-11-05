using System;
using System.IO;

namespace HexTactics.Core.Tests;

internal static class TestPaths
{
    /// <summary>Repository root (the directory that contains HexTactics.sln).</summary>
    public static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HexTactics.sln")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName ?? throw new InvalidOperationException("HexTactics.sln not found above the test directory.");
        }
    }

    public static string PrototypeMap => Path.Combine(RepoRoot, "src", "GodotClient", "maps", "prototype.hexmap");
}
