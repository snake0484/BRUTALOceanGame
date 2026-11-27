namespace BrutalGame.DesignKit;

/// <summary>
/// One reversible edit of a <typeparamref name="TDoc"/>: applying it changes the document, its inverse changes it back.
/// A struct, so the undo log is a flat array with no per-op allocation.
/// </summary>
public interface IUndoOp<TOp, in TDoc> where TOp : struct, IUndoOp<TOp, TDoc>
{
    void ApplyTo(TDoc doc);

    TOp Inverse();
}

/// <summary>
/// Grouped undo: ops are recorded inside a group (one user action, e.g. a drag), and undo or redo replays a whole group.
/// The first op of a new group drops the redo tail; an empty group changes nothing and keeps it.
/// </summary>
public class UndoStack<TOp, TDoc> where TOp : struct, IUndoOp<TOp, TDoc>
{
    private TOp[] _ops = new TOp[256];
    private int[] _groupEnds = new int[64];
    private int _opCount;
    private int _groupCount;
    private int _cursor;
    private bool _open;
    private bool _recorded;

    /// <summary>Groups that can be undone.</summary>
    public int UndoCount => _cursor;

    public int RedoCount => _groupCount - _cursor;

    public bool InGroup => _open;

    public void BeginGroup()
    {
        if (_open)
            throw new InvalidOperationException("Undo group already open");

        _open = true;
        _recorded = false;
    }

    /// <summary>The first op of a group drops the redo tail; an empty group keeps it.</summary>
    public void Record(in TOp op)
    {
        if (!_open)
            throw new InvalidOperationException("Ops are recorded inside a group");

        if (!_recorded)
        {
            _recorded = true;
            _opCount = _cursor == 0 ? 0 : _groupEnds[_cursor - 1];
            _groupCount = _cursor;
        }

        if (_opCount == _ops.Length)
            Array.Resize(ref _ops, _ops.Length * 2);

        _ops[_opCount++] = op;
    }

    /// <summary>Closes the group; an empty group is dropped.</summary>
    public void EndGroup()
    {
        if (!_open)
            throw new InvalidOperationException("No undo group open");

        if (_recorded)
        {
            if (_groupCount == _groupEnds.Length)
                Array.Resize(ref _groupEnds, _groupEnds.Length * 2);

            _groupEnds[_groupCount++] = _opCount;
            _cursor = _groupCount;
        }

        _open = false;
    }

    public bool Undo(TDoc doc)
    {
        if (InGroup || _cursor == 0)
            return false;

        var end = _groupEnds[_cursor - 1];
        var start = _cursor > 1 ? _groupEnds[_cursor - 2] : 0;

        for (var i = end - 1; i >= start; i--)
            _ops[i].Inverse().ApplyTo(doc);

        _cursor--;
        return true;
    }

    public bool Redo(TDoc doc)
    {
        if (InGroup || _cursor == _groupCount)
            return false;

        var start = _cursor > 0 ? _groupEnds[_cursor - 1] : 0;
        var end = _groupEnds[_cursor];

        for (var i = start; i < end; i++)
            _ops[i].ApplyTo(doc);

        _cursor++;
        return true;
    }

    public void Clear()
    {
        _opCount = 0;
        _groupCount = 0;
        _cursor = 0;
        _open = false;
    }
}
