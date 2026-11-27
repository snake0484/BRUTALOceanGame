namespace BrutalGame.DesignKit;

/// <summary>
/// One open document being edited: the document, its file path, grouped undo, a <see cref="Version"/> that moves on every
/// committed change (hosts re-validate or rebuild when it moves, never per frame) and <see cref="Modified"/> since the
/// last save or load. Every change goes through <see cref="Edit"/> as one undo group of ops applied with
/// <see cref="Apply"/>. Subclasses add their selection and other editor state through the hooks.
/// </summary>
public abstract class EditSession<TOp, TDoc> where TOp : struct, IUndoOp<TOp, TDoc> where TDoc : class
{
    protected EditSession(TDoc doc, UndoStack<TOp, TDoc> undo)
    {
        Doc = doc;
        UndoLog = undo;
    }

    public TDoc Doc { get; private set; }

    /// <summary>The undo log (subclasses may expose a derived type).</summary>
    public UndoStack<TOp, TDoc> UndoLog { get; }

    /// <summary>Moves on every committed change, open, undo and redo.</summary>
    public int Version { get; private set; }

    /// <summary>Path of the file the document came from or was last saved to; empty for a new one.</summary>
    public string Path = "";

    /// <summary>Changes since the last save or load.</summary>
    public bool Modified { get; private set; }

    /// <summary>Replaces the document (new or loaded): clears undo.</summary>
    public void Open(TDoc doc, string path)
    {
        Doc = doc;
        Path = path;
        UndoLog.Clear();
        Modified = false;
        OnOpened();
        Changed();
    }

    public void MarkSaved(string path)
    {
        Path = path;
        Modified = false;
    }

    /// <summary>Runs <paramref name="action"/> as one undo group (nested calls join the outer group); nothing recorded means no change.</summary>
    public void Edit(Action action)
    {
        var nested = UndoLog.InGroup;

        if (!nested)
            UndoLog.BeginGroup();

        var before = UndoLog.UndoCount;

        try
        {
            action();
        }
        finally
        {
            if (!nested)
            {
                UndoLog.EndGroup();

                if (UndoLog.UndoCount != before)
                {
                    Modified = true;
                    Changed();
                }
            }
        }
    }

    /// <summary>Applies an op to the document and records it (inside <see cref="Edit"/>).</summary>
    public void Apply(in TOp op)
    {
        op.ApplyTo(Doc);
        UndoLog.Record(op);
    }

    public bool UndoLast()
    {
        if (!UndoLog.Undo(Doc))
            return false;

        OnHistoryMoved();
        Modified = true;
        Changed();
        return true;
    }

    public bool RedoNext()
    {
        if (!UndoLog.Redo(Doc))
            return false;

        OnHistoryMoved();
        Modified = true;
        Changed();
        return true;
    }

    /// <summary>A new document was opened (clear selection and the like).</summary>
    protected virtual void OnOpened()
    {
    }

    /// <summary>Undo or redo changed the document under the editor (prune selections of things that are gone).</summary>
    protected virtual void OnHistoryMoved()
    {
    }

    /// <summary>The version moved.</summary>
    protected virtual void OnChanged()
    {
    }

    private void Changed()
    {
        Version++;
        OnChanged();
    }
}
