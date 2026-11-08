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

    /// <summary>
    /// The editor wants another object edited on top of it (spec part 5 §7.3: a copy of a rule from the unit editor); the
    /// callback gets the saved object (or null) when that editor closes, and this editor comes back with its unsaved state.
    /// </summary>
    event Action<ContentItem, Action<ContentItem?>>? NestedRequested;

    /// <summary>Opens the collapsed part that holds the control <paramref name="automationId"/>; false if there is none.</summary>
    bool Reveal(string automationId);

    ContentOperationResult Save();

    /// <summary>Drops unsaved changes (and their draft).</summary>
    void Discard();

    /// <summary>Handles an automation step of the editor; returns <c>-2</c> if the step is not an editor step.</summary>
    int RunAutomationStep(string step, AutomationRunner runner);
}
