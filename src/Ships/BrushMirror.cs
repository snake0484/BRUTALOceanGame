namespace BrutalGame.Ships;

/// <summary>Ship-local axes: up +Y, front +Z (the designer's front arrow), so +X is the port side seen from behind.</summary>
public static class ShipAxes
{
    public const int UpAxis = 1;
    public const int FrontAxis = 2;
    public const int FrontSign = 1;
}

/// <summary>
/// Reflections of brushes across the ship's mirror planes (X = 0, Y = 0, Z = 0; mask bit 0, 1, 2; several bits reflect
/// across each in turn). The reflected solid is S·(R·X + p) = (S·R·S)·(S·X) + S·p with S = diag(±1). Boxes, cylinders and
/// wedges are symmetric across their local X, so S·X is X turned by a rotation F (S itself, or S·diag(-1, 1, 1) when S
/// reflects): R' = S·R·S·F, which is always another 15 degree snap rotation. A mesh brush keeps R' = S·R·S and takes a
/// reflected copy of its mesh (vertices S·v, faces reversed when S reflects).
/// </summary>
public static class BrushMirror
{
    public static (int X, int Y, int Z) Signs(int mask) => ((mask & 1) != 0 ? -1 : 1, (mask & 2) != 0 ? -1 : 1, (mask & 4) != 0 ? -1 : 1);

    public static bool Reflects(int mask) => (((mask & 1) + ((mask >> 1) & 1) + ((mask >> 2) & 1)) & 1) != 0;

    /// <summary>
    /// The brush mirrored by <paramref name="mask"/>, keeping its id. For a mesh brush pass its mesh: the mirrored mesh
    /// comes back in <paramref name="mirroredMesh"/> (the caller stores it and sets Param). False if the rotation is not
    /// a snap rotation (an invalid index).
    /// </summary>
    public static bool Mirror(in HullBrush brush, int mask, HullMesh? mesh, out HullBrush result, out HullMesh? mirroredMesh)
    {
        result = brush;
        mirroredMesh = null;

        if (mask == 0)
            return true;

        var (sx, sy, sz) = Signs(mask);
        result.PosX = brush.PosX * sx;
        result.PosY = brush.PosY * sy;
        result.PosZ = brush.PosZ * sz;

        Span<double> m = stackalloc double[9];
        Span<double> s = [sx, sy, sz];
        BrushRotation.Matrix(brush.Rotation, m);

        // S·R·S: entry (i, j) times s_i·s_j.
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
                m[i * 3 + j] *= s[i] * s[j];

        if (brush.Shape == BrushShape.Mesh)
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

        if (!BrushRotation.FromMatrix(m, out var rotation))
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
    /// up to snap aliases. Mesh brushes compare their meshes by content (null meshes: by table index).
    /// </summary>
    public static bool SamePlacement(in HullBrush a, HullMesh? meshA, in HullBrush b, HullMesh? meshB)
    {
        if (a.Shape != b.Shape || a.Op != b.Op || a.Material != b.Material || a.Flags != b.Flags || a.PosX != b.PosX || a.PosY != b.PosY || a.PosZ != b.PosZ
            || a.HalfX != b.HalfX || a.HalfY != b.HalfY || a.Bevel != b.Bevel || BrushRotation.Canonical(a.Rotation) != BrushRotation.Canonical(b.Rotation))
            return false;

        if (a.Shape == BrushShape.Mesh)
            return meshA is not null && meshB is not null ? meshA.ContentEquals(meshB) : a.Param == b.Param;

        return a.Shape == BrushShape.Cylinder || a.HalfZ == b.HalfZ;
    }
}
