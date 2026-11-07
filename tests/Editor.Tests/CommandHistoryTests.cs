using System;
using System.Collections.Generic;
using HexTactics.Editor.History;
using Xunit;

namespace HexTactics.Editor.Tests;

/// <summary>Undo/Redo of the editors (spec part 4 §3.3).</summary>
public class CommandHistoryTests
{
    private readonly List<int> _values = new();
    private readonly CommandHistory _history = new();

    private IEditorCommand Add(int v) => new DelegateCommand($"+{v}", () => _values.Add(v), () => _values.Remove(v));

    [Fact]
    public void Undo_and_redo_walk_the_history_and_a_new_change_clears_redo()
    {
        _history.Execute(Add(1));
        _history.Execute(Add(2));
        Assert.Equal(new[] { 1, 2 }, _values);
        Assert.True(_history.Undo());
        Assert.Equal(new[] { 1 }, _values);
        Assert.Equal("+2", _history.RedoDescription);
        Assert.True(_history.Redo());
        Assert.Equal(new[] { 1, 2 }, _values);
        _history.Undo();
        _history.Execute(Add(3));
        Assert.False(_history.CanRedo);
        Assert.Equal(new[] { 1, 3 }, _values);
        _history.Undo();
        _history.Undo();
        Assert.Empty(_values);
        Assert.False(_history.Undo());
    }

    [Fact]
    public void A_transaction_is_one_undo_step_and_a_rollback_reverts_everything()
    {
        _history.BeginTransaction("пакет");
        _history.Execute(Add(1));
        _history.Execute(Add(2));
        Assert.False(_history.CanUndo); // not inside an open transaction
        _history.Commit();
        Assert.Equal(1, _history.UndoCount);
        Assert.Equal("пакет", _history.UndoDescription);
        _history.Undo();
        Assert.Empty(_values);
        _history.Redo();
        Assert.Equal(new[] { 1, 2 }, _values);

        _history.BeginTransaction("отменяемый");
        _history.Execute(Add(3));
        _history.Rollback();
        Assert.Equal(new[] { 1, 2 }, _values);
        Assert.Equal(1, _history.UndoCount);
    }

    [Fact]
    public void A_failing_part_of_a_composite_reverts_the_parts_already_done()
    {
        var failing = new DelegateCommand("сбой", () => throw new EditorCommandException("нельзя"), () => { });
        var composite = new CompositeCommand("всё или ничего", new[] { Add(1), Add(2), failing });
        Assert.Throws<EditorCommandException>(() => _history.Execute(composite));
        Assert.Empty(_values);
        Assert.False(_history.CanUndo);
    }

    [Fact]
    public void Dirty_follows_the_saved_state_through_undo_and_redo()
    {
        Assert.False(_history.IsDirty);
        _history.Execute(Add(1));
        Assert.True(_history.IsDirty);
        _history.MarkSaved();
        Assert.False(_history.IsDirty);
        _history.Execute(Add(2));
        Assert.True(_history.IsDirty);
        _history.Undo();
        Assert.False(_history.IsDirty); // back at the saved state
        _history.Undo();
        Assert.True(_history.IsDirty);
        _history.Redo();
        Assert.False(_history.IsDirty);
    }

    [Fact]
    public void A_command_whose_undo_is_refused_stays_on_the_undo_stack()
    {
        var refuse = true;
        _history.Execute(new DelegateCommand("x", () => { }, () =>
        {
            if (refuse)
            {
                throw new EditorCommandException("зависимость");
            }
        }));
        Assert.Throws<EditorCommandException>(() => _history.Undo());
        Assert.True(_history.CanUndo);
        refuse = false;
        Assert.True(_history.Undo());
    }

    [Fact]
    public void The_history_is_limited()
    {
        var history = new CommandHistory(limit: 3);
        for (var i = 0; i < 5; i++)
        {
            history.Execute(Add(i));
        }

        Assert.Equal(3, history.UndoCount);
        var changed = 0;
        history.Changed += () => changed++;
        history.Undo();
        Assert.Equal(1, changed);
        Assert.Throws<InvalidOperationException>(() => history.Commit());
    }
}
