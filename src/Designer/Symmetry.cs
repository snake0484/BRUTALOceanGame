using BrutalGame.Plans;
using BrutalGame.Ships;

namespace BrutalGame.Designer;

/// <summary>
/// Mirror modes: symmetry planes through the ship origin (X = 0, Y = 0, Z = 0). With planes on, every hull command also
/// acts on the brush's mirror partners: the brushes placed exactly as its reflections (<see cref="BrushMirror"/>). Partners
/// are found by placement, not stored links, so symmetry survives undo, save and load, and a brush on a plane (its own
/// reflection) has no partner. Mirrored brushes are ordinary brushes in the plan (ship-hulls "mirror planes").
/// </summary>
public sealed class Symmetry
{
    public bool X;
    public bool Y;
    public bool Z;

    public bool Any => X || Y || Z;

    /// <summary>Every reflection the enabled planes generate (1 plane: 1, 2 planes: 3, 3 planes: 7), as axis masks.</summary>
    public IEnumerable<int> Masks()
    {
        var enabled = (X ? 1 : 0) | (Y ? 2 : 0) | (Z ? 4 : 0);

        for (var mask = 1; mask < 8; mask++)
        {
            if ((mask & ~enabled) == 0)
                yield return mask;
        }
    }

    public static HullMesh? MeshOf(PlanDocument doc, in HullBrush brush) =>
        brush.Shape == BrushShape.Mesh && brush.Param < doc.Meshes.Count ? doc.Meshes[brush.Param] : null;

    /// <summary>The distinct reflections of <paramref name="brush"/> that differ from it, with their meshes for mesh brushes.</summary>
    public List<(int Mask, HullBrush Brush, HullMesh? Mesh)> Reflections(PlanDocument doc, in HullBrush brush)
    {
        var result = new List<(int, HullBrush, HullMesh?)>();
        var mesh = MeshOf(doc, brush);

        foreach (var mask in Masks())
        {
            if (!BrushMirror.Mirror(brush, mask, mesh, out var mirrored, out var mirroredMesh))
                continue;

            if (BrushMirror.SamePlacement(brush, mesh, mirrored, mirroredMesh ?? mesh))
                continue;

            var duplicate = false;

            foreach (var (_, other, otherMesh) in result)
                duplicate |= BrushMirror.SamePlacement(other, otherMesh, mirrored, mirroredMesh);

            if (!duplicate)
                result.Add((mask, mirrored, mirroredMesh));
        }

        return result;
    }

    /// <summary>Ids of the brushes placed as <paramref name="brush"/>'s reflections, by mask (each brush at most once).</summary>
    public List<(int Mask, uint Id)> Partners(PlanDocument doc, in HullBrush brush)
    {
        var partners = new List<(int, uint)>();

        foreach (var (mask, mirrored, mirroredMesh) in Reflections(doc, brush))
        {
            foreach (ref readonly var candidate in doc.Brushes.AsSpan())
            {
                var id = candidate.Id;

                if (id == brush.Id || partners.Exists(p => p.Item2 == id))
                    continue;

                if (BrushMirror.SamePlacement(candidate, MeshOf(doc, candidate), mirrored, mirroredMesh))
                {
                    partners.Add((mask, candidate.Id));
                    break;
                }
            }
        }

        return partners;
    }
}
