using BrutalGame.Plans;
using BrutalGame.Ships;

namespace BrutalGame.Designer;

public enum OpKind : byte { InsertBrush, RemoveBrush, ModifyBrush, AddMaterial, RemoveMaterial, AddMesh, RemoveMesh, SetMesh, SetLayering }

/// <summary>
/// One invertible edit, a brush-list diff (ship-hulls "undo = brush list diffs"). Insert and Remove carry the brush and
/// its list index; Modify carries before and after; AddMaterial / RemoveMaterial append or drop the table's last entry
/// (<see cref="Value"/> = DefId). Mesh ops (round 5) carry immutable mesh snapshots: AddMesh / RemoveMesh append or drop
/// the mesh table's last entry, SetMesh replaces entry <see cref="Index"/> (one op per modelling step).
/// </summary>
public struct PlanOp
{
    public OpKind Kind;
    public int Index;
    public uint Value;
    public HullBrush Before;
    public HullBrush After;
    public HullMesh? MeshBefore;
    public HullMesh? MeshAfter;
    public HullLayering? LayersBefore;
    public HullLayering? LayersAfter;

    /// <summary>Replaces the plan's layering (null: a solid brush hull). Snapshots are never mutated after this.</summary>
    public static PlanOp SetLayering(HullLayering? before, HullLayering? after) => new() { Kind = OpKind.SetLayering, LayersBefore = before, LayersAfter = after };

    public static PlanOp AddMesh(int index, HullMesh mesh) => new() { Kind = OpKind.AddMesh, Index = index, MeshAfter = mesh };

    public static PlanOp SetMesh(int index, HullMesh before, HullMesh after) => new() { Kind = OpKind.SetMesh, Index = index, MeshBefore = before, MeshAfter = after };

    public static PlanOp Insert(int index, in HullBrush brush) => new() { Kind = OpKind.InsertBrush, Index = index, After = brush };

    public static PlanOp Remove(int index, in HullBrush brush) => new() { Kind = OpKind.RemoveBrush, Index = index, Before = brush };

    public static PlanOp Modify(int index, in HullBrush before, in HullBrush after) => new() { Kind = OpKind.ModifyBrush, Index = index, Before = before, After = after };

    public static PlanOp AddMaterial(int index, DefId material) => new() { Kind = OpKind.AddMaterial, Index = index, Value = material.Value };

    public readonly PlanOp Inverse() => Kind switch
    {
        OpKind.InsertBrush => Remove(Index, After),
        OpKind.RemoveBrush => Insert(Index, Before),
        OpKind.ModifyBrush => Modify(Index, After, Before),
        OpKind.AddMaterial => this with { Kind = OpKind.RemoveMaterial },
        OpKind.RemoveMaterial => this with { Kind = OpKind.AddMaterial },
        OpKind.AddMesh => new PlanOp { Kind = OpKind.RemoveMesh, Index = Index, MeshBefore = MeshAfter },
        OpKind.RemoveMesh => new PlanOp { Kind = OpKind.AddMesh, Index = Index, MeshAfter = MeshBefore },
        OpKind.SetLayering => SetLayering(LayersAfter, LayersBefore),
        _ => SetMesh(Index, MeshAfter!, MeshBefore!),
    };

    /// <summary>Applies to <paramref name="doc"/>; refuses an op whose recorded state does not match (a corrupt log).</summary>
    public readonly void ApplyTo(PlanDocument doc)
    {
        switch (Kind)
        {
            case OpKind.InsertBrush:
                doc.Brushes.Insert(Index, After);

                if (After.Id >= doc.Meta.NextId)
                    doc.Meta.NextId = After.Id + 1;

                break;
            case OpKind.RemoveBrush:
                Expect(doc.Brushes[Index] == Before);
                doc.Brushes.RemoveAt(Index);
                break;
            case OpKind.ModifyBrush:
                Expect(doc.Brushes[Index] == Before);
                doc.Brushes[Index] = After;
                break;
            case OpKind.AddMaterial:
                Expect(Index == doc.Materials.Count);
                doc.Materials.Add(new DefId(Value));
                break;
            case OpKind.RemoveMaterial:
                Expect(Index == doc.Materials.Count - 1 && doc.Materials[Index].Value == Value);
                doc.Materials.RemoveAt(Index);
                break;
            case OpKind.AddMesh:
                Expect(Index == doc.Meshes.Count);
                doc.Meshes.Add(MeshAfter!);
                break;
            case OpKind.RemoveMesh:
                Expect(Index == doc.Meshes.Count - 1 && ReferenceEquals(doc.Meshes[Index], MeshBefore));
                doc.Meshes.RemoveAt(Index);
                break;
            case OpKind.SetMesh:
                Expect(ReferenceEquals(doc.Meshes[Index], MeshBefore));
                doc.Meshes[Index] = MeshAfter!;
                break;
            case OpKind.SetLayering:
                Expect(ReferenceEquals(doc.Layers, LayersBefore));
                doc.Layers = LayersAfter;
                break;
        }
    }

    private static void Expect(bool condition)
    {
        if (!condition)
            throw new InvalidOperationException("Plan op does not match the document");
    }
}

/// <summary>
/// Op log with group boundaries: one user action (add, hollow, move up) is one group, undone and redone whole. Undo
/// applies a group's inverses in reverse; redo re-applies it. A new group after an undo drops the redo tail.
/// </summary>
public sealed class UndoStack
{
    private PlanOp[] _ops = new PlanOp[256];
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
    public void Record(in PlanOp op)
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

    public bool Undo(PlanDocument doc)
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

    public bool Redo(PlanDocument doc)
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
