using BrutalGame.Plans;
using BrutalGame.Ships;

namespace BrutalGame.Designer;

[Flags]
public enum Dirty : byte { None = 0, Geometry = 1, Function = 2, Buildability = 4, Cost = 8 }

/// <summary>Selected brush ids; the last one picked is primary (the properties panel and gizmo act on it).</summary>
public sealed class Selection
{
    public readonly List<uint> Ids = new();

    public uint Primary => Ids.Count > 0 ? Ids[^1] : 0;

    public bool Contains(uint id) => Ids.Contains(id);

    public void Set(uint id)
    {
        Ids.Clear();

        if (id != 0)
            Ids.Add(id);
    }

    public void Toggle(uint id)
    {
        if (!Ids.Remove(id))
            Ids.Add(id);
    }

    public void Clear() => Ids.Clear();

    /// <summary>Drops ids no longer in <paramref name="doc"/> (after undo, delete or load).</summary>
    public void Prune(PlanDocument doc) => Ids.RemoveAll(id => doc.Brushes.IndexOf(id) < 0);
}

/// <summary>
/// One open plan with its undo log, selection and snap settings. Every change goes through <see cref="Apply"/> inside a
/// group (<see cref="Edit"/>), which records the op and marks dirty bits; <see cref="Version"/> moves on each committed
/// change so the host re-bakes and re-validates on commit, never per frame.
/// </summary>
public sealed class DesignSession
{
    public readonly UndoStack Undo = new();
    public readonly Selection Selection = new();
    public readonly Snap Snap = new();
    public Dirty Dirty;

    public DesignSession(PlanDocument? doc = null)
    {
        Doc = doc ?? new PlanDocument();
    }

    public PlanDocument Doc { get; private set; }

    public int Version { get; private set; }

    /// <summary>Path of the file the plan came from or was last saved to; empty for a new plan.</summary>
    public string Path = "";

    /// <summary>Changes since the last save or load.</summary>
    public bool Modified { get; private set; }

    /// <summary>Replaces the document (new or loaded): clears undo and selection.</summary>
    public void Open(PlanDocument doc, string path)
    {
        Doc = doc;
        Path = path;
        Undo.Clear();
        Selection.Clear();
        Modified = false;
        Changed();
    }

    public void MarkSaved(string path)
    {
        Path = path;
        Modified = false;
    }

    /// <summary>Runs <paramref name="action"/> as one undo group; nothing recorded means no change.</summary>
    public void Edit(Action<DesignSession> action)
    {
        var nested = Undo.InGroup;

        if (!nested)
            Undo.BeginGroup();

        var before = Undo.UndoCount;

        try
        {
            action(this);
        }
        finally
        {
            if (!nested)
            {
                Undo.EndGroup();

                if (Undo.UndoCount != before)
                {
                    Modified = true;
                    Changed();
                }
            }
        }
    }

    public void Apply(in PlanOp op)
    {
        op.ApplyTo(Doc);
        Undo.Record(op);
    }

    public bool UndoLast()
    {
        if (!Undo.Undo(Doc))
            return false;

        Selection.Prune(Doc);
        Modified = true;
        Changed();
        return true;
    }

    public bool RedoNext()
    {
        if (!Undo.Redo(Doc))
            return false;

        Selection.Prune(Doc);
        Modified = true;
        Changed();
        return true;
    }

    /// <summary>Plan material index of <paramref name="material"/>, appending it to the table if new (inside a group).</summary>
    public byte EnsureMaterial(DefId material)
    {
        var index = Doc.MaterialIndex(material);

        if (index >= 0)
            return (byte)index;

        if (Doc.Materials.Count >= PlanLimits.MaxMaterials)
            throw new InvalidOperationException("Material table is full");

        index = Doc.Materials.Count;
        Apply(PlanOp.AddMaterial(index, material));
        return (byte)index;
    }

    public bool TryGetBrush(uint id, out HullBrush brush)
    {
        var index = Doc.Brushes.IndexOf(id);
        brush = index >= 0 ? Doc.Brushes[index] : default;
        return index >= 0;
    }

    private void Changed()
    {
        Dirty |= Dirty.Geometry;
        Version++;
    }
}
