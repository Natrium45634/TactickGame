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
        var library = new ContentLibrary(ContentTypeRegistry.CreateDefault(), repository, LoadLimits(repository.Root));
        library.LoadBuiltIn(ReadBuiltIns());
        foreach (var problem in library.Problems)
        {
            GD.PushWarning($"[content] not loaded {problem.Path}: {string.Join("; ", problem.Issues)}");
        }

        GD.Print($"[content] built-in + user objects loaded, user folder: {repository.Root}");
        return library;
    }

    /// <summary>
    /// Safety limits (spec part 4 §8.3): the defaults, lowered by an optional <c>limits.json</c> in the user folder
    /// (never raised past the hard caps).
    /// </summary>
    private static HexTactics.Content.Kinds.ContentLimits LoadLimits(string root)
    {
        var path = System.IO.Path.Combine(root, "limits.json");
        if (!System.IO.File.Exists(path))
        {
            return HexTactics.Content.Kinds.ContentLimits.Default;
        }

        var issues = new HexTactics.Content.Model.IssueList();
        var limits = HexTactics.Content.Kinds.ContentLimits.FromJson(System.IO.File.ReadAllText(path), issues);
        foreach (var issue in issues.Items)
        {
            GD.PushWarning($"[content] {issue.Message}");
        }

        GD.Print($"[content] limits from {path}");
        return limits;
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
