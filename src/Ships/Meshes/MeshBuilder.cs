namespace BrutalGame.Ships;

/// <summary>
/// Editable polygon mesh: vertex list, faces as corner lists, a panel per face, crease edges. Editor ops work on it;
/// <see cref="Build"/> drops unused vertices and remaps everything into an immutable <see cref="HullMesh"/>.
/// </summary>
public sealed class MeshBuilder
{
    public readonly List<MeshVertex> Vertices = new();
    public readonly List<int[]> Faces = new();
    public readonly List<ushort> Panels = new();
    public readonly HashSet<(int, int)> Creases = new();
    public byte Levels;

    public MeshBuilder()
    {
    }

    public MeshBuilder(HullMesh mesh)
    {
        Vertices.AddRange(mesh.Vertices);
        Levels = mesh.SubdivLevels;

        for (var f = 0; f < mesh.FaceCount; f++)
        {
            var face = mesh.Face(f);
            var corners = new int[face.Length];

            for (var i = 0; i < corners.Length; i++)
                corners[i] = face[i];

            Faces.Add(corners);
            Panels.Add(mesh.Panels[f]);
        }

        foreach (var key in mesh.Creases)
            Creases.Add(Edge((int)(key >> 16), (int)(key & 0xFFFF)));
    }

    public static (int, int) Edge(int a, int b) => a < b ? (a, b) : (b, a);

    public int AddVertex(MeshVertex v)
    {
        Vertices.Add(v);
        return Vertices.Count - 1;
    }

    public void AddFace(int[] corners, ushort panel = 0)
    {
        Faces.Add(corners);
        Panels.Add(panel);
    }

    public bool IsCrease(int a, int b) => Creases.Contains(Edge(a, b));

    /// <summary>Directed edge (a → b) to its face and corner index.</summary>
    public Dictionary<(int, int), (int Face, int Corner)> DirectedEdges()
    {
        var map = new Dictionary<(int, int), (int, int)>();

        for (var f = 0; f < Faces.Count; f++)
        {
            var face = Faces[f];

            for (var i = 0; i < face.Length; i++)
                map.TryAdd((face[i], face[(i + 1) % face.Length]), (f, i));
        }

        return map;
    }

    /// <summary>Compacts unused vertices and builds the immutable mesh. Creases on edges no longer in any face are dropped.</summary>
    public HullMesh Build()
    {
        var remap = new int[Vertices.Count];
        Array.Fill(remap, -1);
        var vertices = new List<MeshVertex>();
        var sizes = new byte[Faces.Count];
        var corners = new List<ushort>();
        var edges = new HashSet<(int, int)>();

        for (var f = 0; f < Faces.Count; f++)
        {
            var face = Faces[f];
            sizes[f] = (byte)face.Length;

            for (var i = 0; i < face.Length; i++)
            {
                var v = face[i];

                if (remap[v] < 0)
                {
                    remap[v] = vertices.Count;
                    vertices.Add(Vertices[v]);
                }

                corners.Add((ushort)remap[v]);
                edges.Add(Edge(face[i], face[(i + 1) % face.Length]));
            }
        }

        var creases = new List<ulong>();

        foreach (var (a, b) in Creases)
        {
            if (edges.Contains((a, b)) && remap[a] >= 0 && remap[b] >= 0)
                creases.Add(HullMesh.EdgeKey(remap[a], remap[b]));
        }

        creases.Sort();
        return new HullMesh(vertices.ToArray(), sizes, corners.ToArray(), Panels.ToArray(), creases.Distinct().ToArray(), Levels);
    }
}

/// <summary>Primitive brushes as control meshes (Convert to mesh): the same solid, centred on the brush origin.</summary>
public static class MeshPrimitives
{
    public const int CylinderSides = 16;

    public static HullMesh FromBrush(in HullBrush brush)
    {
        var b = new MeshBuilder();
        int hx = brush.HalfX * 4, hy = brush.HalfY * 4, hz = brush.HalfZ * 4;

        switch (brush.Shape)
        {
            case BrushShape.Cylinder:
                Cylinder(b, hx, hy);
                break;
            case BrushShape.Wedge:
                Wedge(b, hx, hy, hz);
                break;
            default:
                Box(b, hx, hy, hz);
                break;
        }

        return b.Build();
    }

    /// <summary>Corners: bit 0 = +X, bit 1 = +Y, bit 2 = +Z. Faces CCW from outside, all edges creased.</summary>
    public static void Box(MeshBuilder b, int hx, int hy, int hz)
    {
        for (var i = 0; i < 8; i++)
            b.AddVertex(new MeshVertex((i & 1) != 0 ? hx : -hx, (i & 2) != 0 ? hy : -hy, (i & 4) != 0 ? hz : -hz));

        b.AddFace([1, 3, 7, 5], 0);
        b.AddFace([0, 4, 6, 2], 1);
        b.AddFace([2, 6, 7, 3], 2);
        b.AddFace([0, 1, 5, 4], 3);
        b.AddFace([4, 5, 7, 6], 4);
        b.AddFace([0, 2, 3, 1], 5);
        CreaseAll(b);
    }

    /// <summary>The wedge brush: full bottom, vertical back face (-Z), slope from the bottom front edge to the top back edge.</summary>
    public static void Wedge(MeshBuilder b, int hx, int hy, int hz)
    {
        b.AddVertex(new MeshVertex(-hx, -hy, -hz));
        b.AddVertex(new MeshVertex(hx, -hy, -hz));
        b.AddVertex(new MeshVertex(-hx, -hy, hz));
        b.AddVertex(new MeshVertex(hx, -hy, hz));
        b.AddVertex(new MeshVertex(-hx, hy, -hz));
        b.AddVertex(new MeshVertex(hx, hy, -hz));
        b.AddFace([0, 1, 3, 2], 0);
        b.AddFace([0, 4, 5, 1], 1);
        b.AddFace([2, 3, 5, 4], 2);
        b.AddFace([0, 2, 4], 3);
        b.AddFace([1, 5, 3], 4);
        CreaseAll(b);
    }

    /// <summary>A prism of <see cref="CylinderSides"/> sides along Y, vertices on the radius; cap edges creased.</summary>
    public static void Cylinder(MeshBuilder b, int radius, int hy)
    {
        for (var i = 0; i < CylinderSides; i++)
        {
            var a = i * Math.Tau / CylinderSides;
            int x = (int)Math.Round(Math.Cos(a) * radius), z = (int)Math.Round(Math.Sin(a) * radius);
            b.AddVertex(new MeshVertex(x, -hy, z));
            b.AddVertex(new MeshVertex(x, hy, z));
        }

        var top = new int[CylinderSides];
        var bottom = new int[CylinderSides];

        for (var i = 0; i < CylinderSides; i++)
        {
            var j = (i + 1) % CylinderSides;
            b.AddFace([2 * i, 2 * i + 1, 2 * j + 1, 2 * j], 0);
            top[i] = 2 * (CylinderSides - 1 - i) + 1;
            bottom[i] = 2 * i;
            b.Creases.Add(MeshBuilder.Edge(2 * i, 2 * j));
            b.Creases.Add(MeshBuilder.Edge(2 * i + 1, 2 * j + 1));
        }

        b.AddFace(top, 1);
        b.AddFace(bottom, 2);
    }

    private static void CreaseAll(MeshBuilder b)
    {
        foreach (var face in b.Faces)
        {
            for (var i = 0; i < face.Length; i++)
                b.Creases.Add(MeshBuilder.Edge(face[i], face[(i + 1) % face.Length]));
        }
    }
}
