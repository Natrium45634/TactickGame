using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace HexTactics.Content.Model;

/// <summary>A reference to another content object by <c>kind + id</c> (spec part 4 §8.2) — never by file name or position.</summary>
public readonly record struct ContentRef(string Kind, string Id)
{
    public override string ToString() => $"{Kind}:{Id}";
}

public enum IssueSeverity
{
    Warning = 0,
    Error = 1,
}

/// <summary>A validation finding: where (a field path) and what, in plain Russian for the message panel.</summary>
public sealed record ContentIssue(IssueSeverity Severity, string Path, string Message)
{
    public bool IsError => Severity == IssueSeverity.Error;

    public static ContentIssue Error(string path, string message) => new(IssueSeverity.Error, path, message);

    public static ContentIssue Warning(string path, string message) => new(IssueSeverity.Warning, path, message);

    public override string ToString() => Path.Length == 0 ? Message : $"{Path}: {Message}";
}

/// <summary>Collects issues while reading or validating one object.</summary>
public sealed class IssueList
{
    private readonly List<ContentIssue> _items = new();

    public IReadOnlyList<ContentIssue> Items => _items;

    public bool HasErrors => _items.Exists(i => i.IsError);

    public void Error(string path, string message) => _items.Add(ContentIssue.Error(path, message));

    public void Warning(string path, string message) => _items.Add(ContentIssue.Warning(path, message));

    public void AddRange(IEnumerable<ContentIssue> issues) => _items.AddRange(issues);
}

/// <summary>
/// Stable ids (spec part 4 §8.1–8.2): lowercase latin letters, digits and the separators <c>. _ -</c>; they never change
/// on rename and give safe file names. The <c>core.</c> prefix is reserved for the built-in content of the game.
/// </summary>
public static partial class ContentIds
{
    public const string BuiltInPrefix = "core.";

    public const int MaxLength = 64;

    [GeneratedRegex("^[a-z0-9]+([._-][a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static bool IsValid(string? id) => id is { Length: >= 3 and <= MaxLength } && Pattern().IsMatch(id);

    public static bool IsBuiltIn(string id) => id.StartsWith(BuiltInPrefix, StringComparison.Ordinal);

    /// <summary>A new random id for a user object of the kind, e.g. <c>skill_5f2c9a1b</c>.</summary>
    public static string New(string kind, Func<string, bool>? taken = null)
    {
        while (true)
        {
            var id = $"{kind}_{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}";
            if (taken is null || !taken(id))
            {
                return id;
            }
        }
    }

    /// <summary>File name of an object on disk: always derived from the id, never from the user-visible name.</summary>
    public static string FileName(string id) => IsValid(id) ? id + ".json" : throw new ArgumentException($"Invalid content id '{id}'.", nameof(id));
}
