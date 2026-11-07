using System;
using System.Collections.Generic;

namespace HexTactics.Editor.History;

/// <summary>
/// A reversible editor operation (spec part 4 §3.3: <c>AddTile</c>, <c>DeleteTiles</c>, <c>EditStat</c> …): what it changes
/// before and after. <see cref="Do"/> is also used for Redo.
/// </summary>
public interface IEditorCommand
{
    /// <summary>Short Russian description for the top bar («Удалить 2 гекса»).</summary>
    string Description { get; }

    void Do();

    void Undo();
}

/// <summary>A command that cannot be done or undone now; the history stays unchanged and the message is shown.</summary>
public sealed class EditorCommandException : Exception
{
    public EditorCommandException(string message)
        : base(message)
    {
    }
}

/// <summary>A command from two delegates.</summary>
public sealed class DelegateCommand : IEditorCommand
{
    private readonly Action _do;
    private readonly Action _undo;

    public DelegateCommand(string description, Action @do, Action undo)
    {
        Description = description;
        _do = @do;
        _undo = undo;
    }

    public string Description { get; }

    public void Do() => _do();

    public void Undo() => _undo();
}

/// <summary>Several commands applied and reverted as one (an atomic transaction, e.g. batch deletion).</summary>
public sealed class CompositeCommand : IEditorCommand
{
    private readonly List<IEditorCommand> _commands;

    public CompositeCommand(string description, IEnumerable<IEditorCommand> commands)
    {
        Description = description;
        _commands = new List<IEditorCommand>(commands);
    }

    public string Description { get; }

    public IReadOnlyList<IEditorCommand> Commands => _commands;

    public void Do()
    {
        for (var i = 0; i < _commands.Count; i++)
        {
            try
            {
                _commands[i].Do();
            }
            catch
            {
                for (var j = i - 1; j >= 0; j--)
                {
                    _commands[j].Undo(); // all or nothing
                }

                throw;
            }
        }
    }

    public void Undo()
    {
        for (var i = _commands.Count - 1; i >= 0; i--)
        {
            _commands[i].Undo();
        }
    }
}

/// <summary>
/// Undo/Redo history of one editor (spec part 4 §3.3). Every change goes through <see cref="Execute"/>; a transaction
/// groups several commands into one undo step (and reverts them all if it is rolled back). Saving to disk is separate:
/// <see cref="MarkSaved"/> remembers the saved state, <see cref="IsDirty"/> tells whether the current state differs.
/// </summary>
public sealed class CommandHistory
{
    private readonly List<(IEditorCommand Command, long Stamp)> _undo = new();
    private readonly List<(IEditorCommand Command, long Stamp)> _redo = new();
    private List<IEditorCommand>? _transaction;
    private string _transactionName = string.Empty;
    private long _nextStamp = 1;
    private long _savedStamp;

    public CommandHistory(int limit = 500)
    {
        Limit = Math.Max(1, limit);
    }

    public int Limit { get; }

    /// <summary>Raised after every change of the history (execute, undo, redo, save mark, clear).</summary>
    public event Action? Changed;

    public bool CanUndo => _transaction is null && _undo.Count > 0;

    public bool CanRedo => _transaction is null && _redo.Count > 0;

    public string? UndoDescription => _undo.Count > 0 ? _undo[^1].Command.Description : null;

    public string? RedoDescription => _redo.Count > 0 ? _redo[^1].Command.Description : null;

    public int UndoCount => _undo.Count;

    public int RedoCount => _redo.Count;

    public bool InTransaction => _transaction is not null;

    /// <summary>True when the current state differs from the last saved (or initial) one.</summary>
    public bool IsDirty => CurrentStamp != _savedStamp || _transaction is { Count: > 0 };

    private long CurrentStamp => _undo.Count > 0 ? _undo[^1].Stamp : 0;

    /// <summary>Does the command and records it (inside a transaction it is collected for the transaction).</summary>
    public void Execute(IEditorCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Do();
        if (_transaction is not null)
        {
            _transaction.Add(command);
            return;
        }

        Push(command);
    }

    public void BeginTransaction(string description)
    {
        if (_transaction is not null)
        {
            throw new InvalidOperationException("A transaction is already open.");
        }

        _transaction = new List<IEditorCommand>();
        _transactionName = description;
    }

    /// <summary>Closes the transaction as one undo step (nothing is recorded if it is empty).</summary>
    public void Commit()
    {
        var commands = _transaction ?? throw new InvalidOperationException("No open transaction.");
        _transaction = null;
        if (commands.Count > 0)
        {
            Push(commands.Count == 1 ? commands[0] : new CompositeCommand(_transactionName, commands));
        }
        else
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Reverts every command of the open transaction and closes it.</summary>
    public void Rollback()
    {
        var commands = _transaction ?? throw new InvalidOperationException("No open transaction.");
        _transaction = null;
        for (var i = commands.Count - 1; i >= 0; i--)
        {
            commands[i].Undo();
        }

        Changed?.Invoke();
    }

    public bool Undo()
    {
        if (!CanUndo)
        {
            return false;
        }

        var entry = _undo[^1];
        entry.Command.Undo(); // throws EditorCommandException → history unchanged
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(entry);
        Changed?.Invoke();
        return true;
    }

    public bool Redo()
    {
        if (!CanRedo)
        {
            return false;
        }

        var entry = _redo[^1];
        entry.Command.Do();
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(entry);
        Changed?.Invoke();
        return true;
    }

    /// <summary>The current state is the saved one.</summary>
    public void MarkSaved()
    {
        _savedStamp = CurrentStamp;
        Changed?.Invoke();
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        _transaction = null;
        _savedStamp = 0;
        Changed?.Invoke();
    }

    private void Push(IEditorCommand command)
    {
        _undo.Add((command, _nextStamp++));
        _redo.Clear();
        if (_undo.Count > Limit)
        {
            if (_undo[0].Stamp == _savedStamp)
            {
                _savedStamp = -1; // the saved state can no longer be reached by undo
            }

            _undo.RemoveAt(0);
        }

        Changed?.Invoke();
    }
}
