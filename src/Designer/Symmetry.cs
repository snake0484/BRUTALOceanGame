using BrutalGame.Plans;
using BrutalGame.Ships;

namespace BrutalGame.Designer;

/// <summary>
/// Mirror modes: symmetry planes through the ship origin (X = 0, Y = 0, Z = 0). With planes on, every hull command also
/// acts on the solid's mirror partners: the solids placed exactly as its reflections (<see cref="SolidMirror"/>). Partners
/// are found by placement, not stored links, so symmetry survives undo, save and load, and a solid on a plane (its own
/// reflection) has no partner. Mirrored solids are ordinary solids in the plan (ship-hulls "mirror planes").
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

    public static HullMesh? MeshOf(PlanDocument doc, in HullSolid solid) =>
        solid.Shape == SolidShape.Mesh && solid.Param < doc.Meshes.Count ? doc.Meshes[solid.Param] : null;

    /// <summary>The distinct reflections of <paramref name="solid"/> that differ from it, with their meshes for mesh solids.</summary>
    public List<(int Mask, HullSolid Solid, HullMesh? Mesh)> Reflections(PlanDocument doc, in HullSolid solid)
    {
        var result = new List<(int, HullSolid, HullMesh?)>();
        var mesh = MeshOf(doc, solid);

        foreach (var mask in Masks())
        {
            if (!SolidMirror.Mirror(solid, mask, mesh, out var mirrored, out var mirroredMesh))
                continue;

            if (SolidMirror.SamePlacement(solid, mesh, mirrored, mirroredMesh ?? mesh))
                continue;

            var duplicate = false;

            foreach (var (_, other, otherMesh) in result)
                duplicate |= SolidMirror.SamePlacement(other, otherMesh, mirrored, mirroredMesh);

            if (!duplicate)
                result.Add((mask, mirrored, mirroredMesh));
        }

        return result;
    }

    /// <summary>Ids of the solids placed as <paramref name="solid"/>'s reflections, by mask (each solid at most once).</summary>
    public List<(int Mask, uint Id)> Partners(PlanDocument doc, in HullSolid solid)
    {
        var partners = new List<(int, uint)>();

        foreach (var (mask, mirrored, mirroredMesh) in Reflections(doc, solid))
        {
            foreach (ref readonly var candidate in doc.Solids.AsSpan())
            {
                var id = candidate.Id;

                if (id == solid.Id || partners.Exists(p => p.Item2 == id))
                    continue;

                if (SolidMirror.SamePlacement(candidate, MeshOf(doc, candidate), mirrored, mirroredMesh))
                {
                    partners.Add((mask, candidate.Id));
                    break;
                }
            }
        }

        return partners;
    }
}
