using System;
using HexTactics.Client.Debug;
using HexTactics.Content.Library;

namespace HexTactics.Client.DeveloperMode.Editors;

/// <summary>
/// An editor of one content category hosted by the developer mode (spec part 4 §9.2 <c>EditorRegistry</c>: create,
/// edit, preview, validate, test). Editors only display their pure-C# model; saving goes through the content library.
/// </summary>
public interface IContentEditor
{
    /// <summary>Category id (<c>map</c>, <c>unit</c> …).</summary>
    string Kind { get; }

    /// <summary>Name of the edited object.</summary>
    string ObjectName { get; }

    /// <summary>Unsaved changes exist.</summary>
    bool IsDirty { get; }

    /// <summary>The user wants to leave the editor (the host asks about unsaved changes first).</summary>
    event Action? CloseRequested;

    /// <summary>«Тест»: a temporary copy should be played in the game mode.</summary>
    event Action<ContentItem>? TestRequested;

    ContentOperationResult Save();

    /// <summary>Drops unsaved changes (and their draft).</summary>
    void Discard();

    /// <summary>Handles an automation step of the editor; returns <c>-2</c> if the step is not an editor step.</summary>
    int RunAutomationStep(string step, AutomationRunner runner);
}
