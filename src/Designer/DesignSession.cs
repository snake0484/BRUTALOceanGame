using BrutalGame.DesignKit;
using BrutalGame.Plans;
using BrutalGame.Ships;

namespace BrutalGame.Designer;

[Flags]
public enum Dirty : byte { None = 0, Geometry = 1, Function = 2, Buildability = 4, Cost = 8 }

/// <summary>Selected solid ids; the last one picked is primary (the properties panel and gizmo act on it).</summary>
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
    public void Prune(PlanDocument doc) => Ids.RemoveAll(id => doc.Solids.IndexOf(id) < 0);
}

/// <summary>
/// One open plan with its undo log, selection and snap settings. Every change goes through <see cref="Apply"/> inside a
/// group (<see cref="Edit"/>), which records the op and marks dirty bits; <see cref="Version"/> moves on each committed
/// change so the host re-bakes and re-validates on commit, never per frame.
/// </summary>
public sealed class DesignSession : EditSession<PlanOp, PlanDocument>
{
    public readonly Selection Selection = new();
    public readonly Snap Snap = new();
    public readonly Symmetry Symmetry = new();
    public Dirty Dirty;

    public DesignSession(PlanDocument? doc = null)
        : this(doc ?? new PlanDocument(), new UndoStack())
    {
    }

    private DesignSession(PlanDocument doc, UndoStack undo)
        : base(doc, undo)
    {
        Undo = undo;
    }

    public UndoStack Undo { get; }

    /// <summary>Runs <paramref name="action"/> as one undo group; nothing recorded means no change.</summary>
    public void Edit(Action<DesignSession> action) => Edit(() => action(this));

    protected override void OnOpened() => Selection.Clear();

    protected override void OnHistoryMoved() => Selection.Prune(Doc);

    protected override void OnChanged() => Dirty |= Dirty.Geometry;

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

    public bool TryGetSolid(uint id, out HullSolid solid)
    {
        var index = Doc.Solids.IndexOf(id);
        solid = index >= 0 ? Doc.Solids[index] : default;
        return index >= 0;
    }
}
