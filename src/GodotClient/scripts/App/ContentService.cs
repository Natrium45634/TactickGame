using System.Collections.Generic;
using System.Text.Json.Nodes;
using Godot;
using HexTactics.Content.Library;
using HexTactics.Content.Registry;
using HexTactics.Content.Storage;

namespace HexTactics.Client.App;

/// <summary>
/// Loads the content library (spec part 4 §8.1): the built-in objects shipped inside the game (<c>res://data/content</c>,
/// read-only) and the user's objects from the user data folder (<c>user://</c>, or <c>--hx-user-root=…</c> for tests).
/// The user folder is never part of a release and survives updates of the game.
/// </summary>
public static class ContentService
{
    public const string BuiltInIndex = "res://data/content/index.json";

    public static ContentLibrary Load(string? userRootOverride)
    {
        var root = string.IsNullOrEmpty(userRootOverride) ? ProjectSettings.GlobalizePath("user://") : userRootOverride;
        var repository = new ContentRepository(root);
        repository.EnsureLayout();
        var library = new ContentLibrary(ContentTypeRegistry.CreateDefault(), repository);
        library.LoadBuiltIn(ReadBuiltIns());
        foreach (var problem in library.Problems)
        {
            GD.PushWarning($"[content] not loaded {problem.Path}: {string.Join("; ", problem.Issues)}");
        }

        GD.Print($"[content] built-in + user objects loaded, user folder: {repository.Root}");
        return library;
    }

    private static IEnumerable<(string Path, string Text)> ReadBuiltIns()
    {
        var index = JsonNode.Parse(FileAccess.GetFileAsString(BuiltInIndex))!["files"]!.AsArray();
        foreach (var file in index)
        {
            var path = "res://data/content/" + file!.GetValue<string>();
            yield return (path, FileAccess.GetFileAsString(path));
        }
    }
}
