using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using HexTactics.Content.Library;
using HexTactics.Content.Registry;
using HexTactics.Content.Storage;
using HexTactics.Tests.Shared;

namespace HexTactics.Tests.Support;

/// <summary>A unique temporary folder removed after the test.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hextactics-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>The built-in content of the game as shipped in <c>src/GodotClient/data/content</c>.</summary>
internal static class ContentTestKit
{
    public static string BuiltInRoot => System.IO.Path.Combine(TestPaths.RepoRoot, "src", "GodotClient", "data", "content");

    /// <summary>The files listed in <c>index.json</c> (what the client loads).</summary>
    public static IReadOnlyList<(string Path, string Text)> BuiltInFiles()
    {
        var index = JsonNode.Parse(File.ReadAllText(System.IO.Path.Combine(BuiltInRoot, "index.json")))!["files"]!.AsArray();
        var files = new List<(string, string)>();
        foreach (var f in index)
        {
            var rel = f!.GetValue<string>();
            files.Add((rel, File.ReadAllText(System.IO.Path.Combine(BuiltInRoot, rel))));
        }

        return files;
    }

    public static DateTimeOffset Clock { get; set; } = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A library with the built-in content and a user repository in <paramref name="userRoot"/>.</summary>
    public static ContentLibrary Library(string? userRoot, ContentTypeRegistry? registry = null)
    {
        var tick = 0;
        var repository = userRoot is null ? null : new ContentRepository(userRoot, () => Clock.AddSeconds(tick++));
        repository?.EnsureLayout();
        var library = new ContentLibrary(registry ?? ContentTypeRegistry.CreateDefault(), repository, clock: () => Clock.AddMinutes(tick++));
        library.LoadBuiltIn(BuiltInFiles());
        return library;
    }
}
