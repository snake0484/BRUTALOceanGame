namespace BrutalGame.Ships;

/// <summary>Ship-local axes: up +Y, front +Z (the designer's front arrow), so +X is the port side seen from behind.</summary>
public static class ShipAxes
{
    public const int UpAxis = 1;
    public const int FrontAxis = 2;
    public const int FrontSign = 1;
}

/// <summary>
/// Reflections of solids across the ship's mirror planes (X = 0, Y = 0, Z = 0; mask bit 0, 1, 2; several bits reflect
/// across each in turn). The reflected solid is S·(R·X + p) = (S·R·S)·(S·X) + S·p with S = diag(±1). Boxes, cylinders and
/// wedges are symmetric across their local X, so S·X is X turned by a rotation F (S itself, or S·diag(-1, 1, 1) when S
/// reflects): R' = S·R·S·F, which is always another 15 degree snap rotation. A mesh solid keeps R' = S·R·S and takes a
/// reflected copy of its mesh (vertices S·v, faces reversed when S reflects).
/// </summary>
public static class SolidMirror
{
    public static (int X, int Y, int Z) Signs(int mask) => ((mask & 1) != 0 ? -1 : 1, (mask & 2) != 0 ? -1 : 1, (mask & 4) != 0 ? -1 : 1);

    public static bool Reflects(int mask) => (((mask & 1) + ((mask >> 1) & 1) + ((mask >> 2) & 1)) & 1) != 0;

    /// <summary>
    /// The solid mirrored by <paramref name="mask"/>, keeping its id. For a mesh solid pass its mesh: the mirrored mesh
    /// comes back in <paramref name="mirroredMesh"/> (the caller stores it and sets Param). False if the rotation is not
    /// a snap rotation (an invalid index).
    /// </summary>
    public static bool Mirror(in HullSolid solid, int mask, HullMesh? mesh, out HullSolid result, out HullMesh? mirroredMesh)
    {
        result = solid;
        mirroredMesh = null;

        if (mask == 0)
            return true;

        var (sx, sy, sz) = Signs(mask);
        result.PosX = solid.PosX * sx;
        result.PosY = solid.PosY * sy;
        result.PosZ = solid.PosZ * sz;

        Span<double> m = stackalloc double[9];
        Span<double> s = [sx, sy, sz];
        SolidRotation.Matrix(solid.Rotation, m);

        // S·R·S: entry (i, j) times s_i·s_j.
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
                m[i * 3 + j] *= s[i] * s[j];

        if (solid.Shape == SolidShape.Mesh)
        {
            if (mesh is not null)
                mirroredMesh = Mirror(mesh, mask);
        }
        else
        {
            // F: S, made a rotation by also flipping local X (a symmetry of every primitive) when S reflects.
            Span<double> f = [Reflects(mask) ? -sx : sx, sy, sz];

            for (var i = 0; i < 3; i++)
                for (var j = 0; j < 3; j++)
                    m[i * 3 + j] *= f[j];
        }

        if (!SolidRotation.FromMatrix(m, out var rotation))
            return false;

        result.Rotation = rotation;
        return true;
    }

    public static HullMesh Mirror(HullMesh mesh, int mask)
    {
        var (sx, sy, sz) = Signs(mask);
        var vertices = new MeshVertex[mesh.Vertices.Length];

        for (var i = 0; i < vertices.Length; i++)
        {
            var v = mesh.Vertices[i];
            vertices[i] = new MeshVertex(v.X * sx, v.Y * sy, v.Z * sz);
        }

        var corners = (ushort[])mesh.Corners.Clone();

        if (Reflects(mask))
        {
            for (var f = 0; f < mesh.FaceCount; f++)
                Array.Reverse(corners, mesh.FaceStarts[f], mesh.FaceSizes[f]);
        }

        return new HullMesh(vertices, (byte[])mesh.FaceSizes.Clone(), corners, (ushort[])mesh.Panels.Clone(), (ulong[])mesh.Creases.Clone(), mesh.SubdivLevels);
    }

    /// <summary>
    /// Same solid placed the same way: shape, op, material, flags, position, half extents, bevel, and the same rotation
    /// up to snap aliases. Mesh solids compare their meshes by content (null meshes: by table index).
    /// </summary>
    public static bool SamePlacement(in HullSolid a, HullMesh? meshA, in HullSolid b, HullMesh? meshB)
    {
        if (a.Shape != b.Shape || a.Op != b.Op || a.Material != b.Material || a.Flags != b.Flags || a.PosX != b.PosX || a.PosY != b.PosY || a.PosZ != b.PosZ
            || a.HalfX != b.HalfX || a.HalfY != b.HalfY || a.Bevel != b.Bevel || SolidRotation.Canonical(a.Rotation) != SolidRotation.Canonical(b.Rotation))
            return false;

        if (a.Shape == SolidShape.Mesh)
            return meshA is not null && meshB is not null ? meshA.ContentEquals(meshB) : a.Param == b.Param;

        return a.Shape == SolidShape.Cylinder || a.HalfZ == b.HalfZ;
    }
}
