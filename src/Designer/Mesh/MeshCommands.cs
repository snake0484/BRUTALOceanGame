using BrutalGame.Plans;
using BrutalGame.Ships;

namespace BrutalGame.Designer.Mesh;

public enum MeshSelectMode : byte { Vertex, Edge, Face }

/// <summary>Elements picked on the mesh being edited: vertices, edges (sorted vertex pairs) or faces, by the active mode.</summary>
public sealed class MeshSelection
{
    public MeshSelectMode Mode = MeshSelectMode.Face;
    public readonly HashSet<int> Vertices = new();
    public readonly HashSet<(int A, int B)> Edges = new();
    public readonly HashSet<int> Faces = new();

    public bool IsEmpty => Mode switch
    {
        MeshSelectMode.Vertex => Vertices.Count == 0,
        MeshSelectMode.Edge => Edges.Count == 0,
        _ => Faces.Count == 0,
    };

    public void Clear()
    {
        Vertices.Clear();
        Edges.Clear();
        Faces.Clear();
    }

    /// <summary>Every vertex the selection moves: the vertices, the edges' ends or the faces' corners.</summary>
    public HashSet<int> MovedVertices(HullMesh mesh)
    {
        var result = new HashSet<int>();

        switch (Mode)
        {
            case MeshSelectMode.Vertex:
                result.UnionWith(Vertices);
                break;
            case MeshSelectMode.Edge:
                foreach (var (a, b) in Edges)
                {
                    result.Add(a);
                    result.Add(b);
                }

                break;
            default:
                foreach (var f in Faces)
                {
                    if (f < mesh.FaceCount)
                    {
                        foreach (var c in mesh.Face(f))
                            result.Add(c);
                    }
                }

                break;
        }

        result.RemoveWhere(v => v >= mesh.Vertices.Length);
        return result;
    }

    /// <summary>Drops elements past the mesh (after an op that removed some).</summary>
    public void Prune(HullMesh mesh)
    {
        Vertices.RemoveWhere(v => v >= mesh.Vertices.Length);
        Faces.RemoveWhere(f => f >= mesh.FaceCount);
        Edges.RemoveWhere(e => e.B >= mesh.Vertices.Length);
    }
}

/// <summary>
/// Mesh brush commands, each one undo group: convert a primitive to a mesh brush (same solid, same op, so a cut stays a
/// cut), and apply an editor op to a brush's mesh. Editing a mesh shared by several brushes changes all of them, and every
/// brush using it gets the new half extents. Live mirror symmetrises after the op.
/// </summary>
public static class MeshCommands
{
    public static string LastError { get; private set; } = "";

    public static bool ConvertToMesh(DesignSession session, uint id)
    {
        var index = session.Doc.Brushes.IndexOf(id);

        if (index < 0)
            return false;

        var before = session.Doc.Brushes[index];

        if (!BrushGeometry.IsSupported(before.Shape))
            return false;

        var mesh = MeshPrimitives.FromBrush(before);

        session.Edit(s =>
        {
            var slot = s.Doc.Meshes.Count;
            s.Apply(PlanOp.AddMesh(slot, mesh));
            var after = before;
            after.Shape = BrushShape.Mesh;
            after.Param = (ushort)slot;
            (after.HalfX, after.HalfY, after.HalfZ) = mesh.HalfUnits();
            s.Apply(PlanOp.Modify(index, before, after));
        });

        return true;
    }

    public static HullMesh? MeshOf(DesignSession session, uint id) =>
        session.TryGetBrush(id, out var brush) && brush.Shape == BrushShape.Mesh && brush.Param < session.Doc.Meshes.Count ? session.Doc.Meshes[brush.Param] : null;

    /// <summary>Runs <paramref name="op"/> on a builder of the brush's mesh and commits the result. False if the op refused.</summary>
    public static bool Apply(DesignSession session, uint id, Func<MeshBuilder, bool> op, bool mirror = false)
    {
        var mesh = MeshOf(session, id);

        if (mesh is null)
        {
            LastError = "Select a mesh brush";
            return false;
        }

        var builder = new MeshBuilder(mesh);

        if (!op(builder))
        {
            LastError = MeshOps.Error;
            return false;
        }

        if (mirror && !MeshOps.MirrorX(builder))
        {
            LastError = MeshOps.Error;
            return false;
        }

        LastError = "";
        return Replace(session, id, builder.Build());
    }

    /// <summary>Replaces the mesh of brush <paramref name="id"/> and refits every brush sharing it.</summary>
    public static bool Replace(DesignSession session, uint id, HullMesh after)
    {
        if (!session.TryGetBrush(id, out var brush) || brush.Shape != BrushShape.Mesh || brush.Param >= session.Doc.Meshes.Count)
            return false;

        var slot = brush.Param;
        var before = session.Doc.Meshes[slot];

        if (before.ContentEquals(after))
            return false;

        session.Edit(s =>
        {
            s.Apply(PlanOp.SetMesh(slot, before, after));
            var half = after.HalfUnits();
            var brushes = s.Doc.Brushes;

            for (var i = 0; i < brushes.Count; i++)
            {
                var b = brushes[i];

                if (b.Shape != BrushShape.Mesh || b.Param != slot)
                    continue;

                var refit = b;
                (refit.HalfX, refit.HalfY, refit.HalfZ) = half;

                if (refit != b)
                    s.Apply(PlanOp.Modify(i, b, refit));
            }
        });

        return true;
    }

    public static bool SetLevels(DesignSession session, uint id, int levels, bool mirror = false) =>
        Apply(session, id, b =>
        {
            b.Levels = (byte)Math.Clamp(levels, 0, HullMesh.MaxLevels);
            return true;
        }, mirror);
}
