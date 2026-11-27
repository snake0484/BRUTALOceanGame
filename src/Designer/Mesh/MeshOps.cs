using BrutalGame.Ships;

namespace BrutalGame.Designer.Mesh;

/// <summary>
/// Mesh editor operations (ship-hulls "Mesh editor"), each on a <see cref="MeshBuilder"/> and each one undoable op when
/// committed. Faces stay CCW from outside and the mesh stays closed: every op replaces faces and adds the faces that
/// close the gap, so a valid mesh in gives a valid mesh out unless the geometry folds (validation reports that). New
/// positions round to the 1/1024 m grid. Face indices of faces an op keeps are unchanged, so a face selection survives.
/// Returns false (mesh untouched) with <see cref="Error"/> set when an op does not apply.
/// </summary>
public static class MeshOps
{
    public static string Error { get; private set; } = "";

    private static bool Fail(string message)
    {
        Error = message;
        return false;
    }

    public static (double X, double Y, double Z) Normal(MeshBuilder b, int[] face)
    {
        double nx = 0, ny = 0, nz = 0;

        for (var i = 0; i < face.Length; i++)
        {
            var p = b.Vertices[face[i]];
            var q = b.Vertices[face[(i + 1) % face.Length]];
            nx += (double)(p.Y - q.Y) * (p.Z + q.Z);
            ny += (double)(p.Z - q.Z) * (p.X + q.X);
            nz += (double)(p.X - q.X) * (p.Y + q.Y);
        }

        var length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        return length > 0 ? (nx / length, ny / length, nz / length) : (0, 1, 0);
    }

    public static (double X, double Y, double Z) Centroid(MeshBuilder b, int[] face)
    {
        double x = 0, y = 0, z = 0;

        foreach (var c in face)
        {
            var v = b.Vertices[c];
            (x, y, z) = (x + v.X, y + v.Y, z + v.Z);
        }

        return (x / face.Length, y / face.Length, z / face.Length);
    }

    private static MeshVertex Round(double x, double y, double z) => new((int)Math.Round(x), (int)Math.Round(y), (int)Math.Round(z));

    /// <summary>Moves vertices by a delta in 1/1024 m.</summary>
    public static void Translate(MeshBuilder b, IEnumerable<int> vertices, int dx, int dy, int dz)
    {
        foreach (var v in vertices)
        {
            var p = b.Vertices[v];
            b.Vertices[v] = new MeshVertex(p.X + dx, p.Y + dy, p.Z + dz);
        }
    }

    /// <summary>
    /// Moves vertices of a mesh kept symmetric across local X = 0 (live mirror; run <see cref="MirrorX"/> after). Seam
    /// vertices (X = 0) stay on the plane, moving only in Y and Z, so the halves stay joined; a +X vertex stops at the
    /// plane rather than crossing it; a -X vertex moves its +X twin by the mirrored delta (the -X half is rebuilt from
    /// the +X one). A vertex picked on both sides moves once.
    /// </summary>
    public static void TranslateMirrored(MeshBuilder b, IEnumerable<int> vertices, int dx, int dy, int dz)
    {
        var at = new Dictionary<MeshVertex, int>(b.Vertices.Count);

        for (var i = 0; i < b.Vertices.Count; i++)
            at.TryAdd(b.Vertices[i], i);

        var moved = new HashSet<int>();

        foreach (var v in vertices)
        {
            var p = b.Vertices[v];
            var target = v;
            var sx = 1;

            if (p.X < 0 && at.TryGetValue(new MeshVertex(-p.X, p.Y, p.Z), out var twin))
                (target, sx) = (twin, -1);

            if (!moved.Add(target))
                continue;

            var q = b.Vertices[target];
            var x = q.X == 0 ? 0 : q.X > 0 ? Math.Max(0, q.X + sx * dx) : q.X + sx * dx;
            b.Vertices[target] = new MeshVertex(x, q.Y + dy, q.Z + dz);
        }
    }

    /// <summary>
    /// Region extrude: the selected faces move out by <paramref name="distance"/> (1/1024 m) along their vertices' mean
    /// face normal; a quad wall joins every boundary edge of the region to its copy.
    /// </summary>
    public static bool ExtrudeFaces(MeshBuilder b, IReadOnlyCollection<int> faces, int distance)
    {
        if (faces.Count == 0)
            return Fail("Select faces to extrude");

        var region = new HashSet<int>(faces);
        var directed = b.DirectedEdges();
        var copies = new Dictionary<int, int>();
        var normals = new Dictionary<int, (double X, double Y, double Z)>();

        foreach (var f in region)
        {
            var n = Normal(b, b.Faces[f]);

            foreach (var v in b.Faces[f])
            {
                var s = normals.GetValueOrDefault(v);
                normals[v] = (s.X + n.X, s.Y + n.Y, s.Z + n.Z);
            }
        }

        var walls = new List<(int A, int B)>();

        foreach (var f in region)
        {
            var face = b.Faces[f];

            for (var i = 0; i < face.Length; i++)
            {
                int a = face[i], c = face[(i + 1) % face.Length];

                if (!directed.TryGetValue((c, a), out var twin) || !region.Contains(twin.Face))
                    walls.Add((a, c));
            }
        }

        foreach (var (v, n) in normals)
        {
            var length = Math.Sqrt(n.X * n.X + n.Y * n.Y + n.Z * n.Z);
            var p = b.Vertices[v];
            var k = length > 0 ? distance / length : 0;
            copies[v] = b.AddVertex(Round(p.X + n.X * k, p.Y + n.Y * k, p.Z + n.Z * k));
        }

        foreach (var f in region)
        {
            var face = b.Faces[f];

            for (var i = 0; i < face.Length; i++)
                face[i] = copies[face[i]];
        }

        foreach (var (a, c) in walls)
            b.AddFace([a, c, copies[c], copies[a]], b.Panels[0]);

        return true;
    }

    /// <summary>Individual inset: each face shrinks toward its centroid by <paramref name="width"/> (1/1024 m), ringed by quads.</summary>
    public static bool InsetFaces(MeshBuilder b, IReadOnlyCollection<int> faces, int width)
    {
        if (faces.Count == 0)
            return Fail("Select faces to inset");

        foreach (var f in faces)
        {
            var face = b.Faces[f];
            var c = Centroid(b, face);
            var inner = new int[face.Length];

            for (var i = 0; i < face.Length; i++)
            {
                var p = b.Vertices[face[i]];
                double dx = c.X - p.X, dy = c.Y - p.Y, dz = c.Z - p.Z;
                var length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                var k = length > 0 ? Math.Min(width / length, 0.9) : 0;
                inner[i] = b.AddVertex(Round(p.X + dx * k, p.Y + dy * k, p.Z + dz * k));
            }

            for (var i = 0; i < face.Length; i++)
            {
                var j = (i + 1) % face.Length;
                b.AddFace([face[i], face[j], inner[j], inner[i]], b.Panels[f]);
            }

            b.Faces[f] = inner;
        }

        return true;
    }

    /// <summary>
    /// Loop cut through the quad ring across <paramref name="edge"/>: every ring edge splits at <paramref name="factor"/>
    /// (measured from the same side all round) and every ring quad splits in two. The ring stops at non-quads, whose
    /// edge just gains the new vertex.
    /// </summary>
    public static bool LoopCut(MeshBuilder b, (int A, int B) edge, double factor)
    {
        var directed = b.DirectedEdges();

        if (!directed.ContainsKey(edge))
            edge = (edge.B, edge.A);

        if (!directed.ContainsKey(edge))
            return Fail("Pick an edge of the mesh");

        // Walk the ring both ways; each entry is a ring edge with its "from" side, and the quad crossed after it.
        var ringEdges = new List<(int From, int To)> { edge };
        var ringFaces = new List<int>();
        var seenFaces = new HashSet<int>();

        foreach (var start in new[] { edge, (edge.B, edge.A) })
        {
            var current = start;

            while (true)
            {
                var (face, corner) = directed[current];
                var f = b.Faces[face];

                if (f.Length != 4 || !seenFaces.Add(face))
                    break;

                ringFaces.Add(face);

                // In quad (current.From, current.To, c, d): the opposite edge runs c → d; From pairs with d, To with c.
                var c = f[(corner + 2) % 4];
                var d = f[(corner + 3) % 4];
                var next = (d, c);

                if (next == edge || next == (edge.B, edge.A))
                    break;

                if (start == edge)
                    ringEdges.Add(next);
                else
                    ringEdges.Insert(0, (next.Item2, next.Item1));

                current = next;
            }
        }

        // Split every ring edge once.
        var mids = new Dictionary<(int, int), int>();

        foreach (var (from, to) in ringEdges)
        {
            var key = MeshBuilder.Edge(from, to);

            if (mids.ContainsKey(key))
                continue;

            var p = b.Vertices[from];
            var q = b.Vertices[to];
            mids[key] = b.AddVertex(Round(p.X + (q.X - p.X) * factor, p.Y + (q.Y - p.Y) * factor, p.Z + (q.Z - p.Z) * factor));
        }

        InsertMidpoints(b, mids);

        // Ring quads are now hexagons a m1 b c m2 d: split into a m1 m2 d and m1 b c m2.
        foreach (var face in ringFaces)
        {
            var f = b.Faces[face];
            var cut = new List<int>();

            for (var i = 0; i < f.Length; i++)
            {
                if (mids.ContainsValue(f[i]))
                    cut.Add(i);
            }

            if (cut.Count != 2)
                continue;

            int i0 = cut[0], i1 = cut[1];
            var first = new List<int>();
            var second = new List<int>();

            for (var k = i0; k != i1; k = (k + 1) % f.Length)
                first.Add(f[k]);

            first.Add(f[i1]);

            for (var k = i1; k != i0; k = (k + 1) % f.Length)
                second.Add(f[k]);

            second.Add(f[i0]);
            b.Faces[face] = first.ToArray();
            b.AddFace(second.ToArray(), b.Panels[face]);
        }

        return true;
    }

    /// <summary>Inserts each split edge's new vertex into both faces using that edge.</summary>
    private static void InsertMidpoints(MeshBuilder b, Dictionary<(int, int), int> mids)
    {
        for (var f = 0; f < b.Faces.Count; f++)
        {
            var face = b.Faces[f];
            List<int>? grown = null;

            for (var i = 0; i < face.Length; i++)
            {
                var key = MeshBuilder.Edge(face[i], face[(i + 1) % face.Length]);
                grown?.Add(face[i]);

                if (!mids.TryGetValue(key, out var mid))
                    continue;

                if (grown is null)
                {
                    grown = new List<int>(face.Length + 2);
                    grown.AddRange(face.AsSpan(0, i + 1).ToArray());
                }

                grown.Add(mid);

                if (b.Creases.Remove(key))
                {
                    b.Creases.Add(MeshBuilder.Edge(face[i], mid));
                    b.Creases.Add(MeshBuilder.Edge(mid, face[(i + 1) % face.Length]));
                }
            }

            if (grown is not null)
                b.Faces[f] = grown.ToArray();
        }
    }

    /// <summary>
    /// One-segment bevel (chamfer) of edges whose ends are 3-valent corners, like a box's: each end splits along its
    /// other two edges by <paramref name="width"/> (1/1024 m) and a quad fills the chamfer. Edges sharing an end are refused.
    /// </summary>
    public static bool BevelEdges(MeshBuilder b, IReadOnlyCollection<(int A, int B)> edges, int width)
    {
        if (edges.Count == 0)
            return Fail("Select edges to bevel");

        var ends = new HashSet<int>();

        foreach (var (a, c) in edges)
        {
            if (!ends.Add(a) || !ends.Add(c))
                return Fail("Bevel edges one at a time where they share a corner");
        }

        foreach (var edge in edges)
        {
            if (!BevelEdge(b, edge, width))
                return false;
        }

        return true;
    }

    private static bool BevelEdge(MeshBuilder b, (int A, int B) edge, int width)
    {
        var directed = b.DirectedEdges();

        if (!directed.ContainsKey(edge))
            edge = (edge.B, edge.A);

        if (!directed.TryGetValue(edge, out var left) || !directed.TryGetValue((edge.B, edge.A), out var right))
            return Fail("Pick an edge of the mesh");

        var (a, c) = edge;

        if (Valence(b, a) != 3 || Valence(b, c) != 3)
            return Fail("Bevel needs both ends to be corners of three edges");

        // Face L holds a → c, face R holds c → a. At a: L's corner before a is pL (edge pL → a), R's corner after a is sR.
        var lf = b.Faces[left.Face];
        var rf = b.Faces[right.Face];
        var pL = lf[(left.Corner + lf.Length - 1) % lf.Length];
        var nL = lf[(left.Corner + 2) % lf.Length];
        var sR = rf[(right.Corner + 2) % rf.Length];
        var pR = rf[(right.Corner + rf.Length - 1) % rf.Length];

        var aL = b.AddVertex(Toward(b, a, pL, width));
        var aR = b.AddVertex(Toward(b, a, sR, width));
        var cL = b.AddVertex(Toward(b, c, nL, width));
        var cR = b.AddVertex(Toward(b, c, pR, width));

        // L: pL → aL → cL → nL; R: pR → cR → aR → sR; third face at a: sR → aR → aL → pL; at c: nL → cL → cR → pR.
        Replace(b, left.Face, a, [aL]);
        Replace(b, left.Face, c, [cL]);
        Replace(b, right.Face, c, [cR]);
        Replace(b, right.Face, a, [aR]);
        Replace(b, FaceWith(b, sR, a), a, [aR, aL]);
        Replace(b, FaceWith(b, nL, c), c, [cL, cR]);
        b.AddFace([aL, aR, cR, cL], b.Panels[left.Face]);
        b.Creases.Remove(MeshBuilder.Edge(a, c));
        return true;
    }

    private static int Valence(MeshBuilder b, int v)
    {
        var n = 0;

        foreach (var face in b.Faces)
        {
            if (Array.IndexOf(face, v) >= 0)
                n++;
        }

        return n;
    }

    private static MeshVertex Toward(MeshBuilder b, int from, int to, int width)
    {
        var p = b.Vertices[from];
        var q = b.Vertices[to];
        double dx = q.X - p.X, dy = q.Y - p.Y, dz = q.Z - p.Z;
        var length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        var k = length > 0 ? Math.Min(width / length, 0.45) : 0;
        return Round(p.X + dx * k, p.Y + dy * k, p.Z + dz * k);
    }

    /// <summary>The face holding directed edge <paramref name="from"/> → <paramref name="to"/>.</summary>
    private static int FaceWith(MeshBuilder b, int from, int to)
    {
        for (var f = 0; f < b.Faces.Count; f++)
        {
            var face = b.Faces[f];

            for (var i = 0; i < face.Length; i++)
            {
                if (face[i] == from && face[(i + 1) % face.Length] == to)
                    return f;
            }
        }

        throw new InvalidOperationException("Edge not in mesh");
    }

    private static void Replace(MeshBuilder b, int face, int vertex, int[] with)
    {
        var f = b.Faces[face];
        var i = Array.IndexOf(f, vertex);
        b.Faces[face] = [.. f.AsSpan(0, i), .. with, .. f.AsSpan(i + 1)];
    }

    /// <summary>
    /// Bridges two faces with the same corner count: both are removed and their rims joined by quads (a tunnel through
    /// the solid, or a handle between two parts), matched with the rotation that minimises the total rung length.
    /// </summary>
    public static bool BridgeFaces(MeshBuilder b, int fa, int fb)
    {
        if (fa == fb || fa < 0 || fb < 0)
            return Fail("Select two faces to bridge");

        var a = b.Faces[fa];
        var c = b.Faces[fb];

        if (a.Length != c.Length)
            return Fail("Bridged faces need the same number of corners");

        var n = a.Length;
        var bestOffset = 0;
        var best = double.MaxValue;

        // Rim A runs a0 a1 ...; rim B is matched reversed: a_i pairs with c[(offset − i) mod n].
        for (var offset = 0; offset < n; offset++)
        {
            double sum = 0;

            for (var i = 0; i < n; i++)
                sum += Distance(b.Vertices[a[i]], b.Vertices[c[((offset - i) % n + n) % n]]);

            if (sum < best)
            {
                best = sum;
                bestOffset = offset;
            }
        }

        var panel = b.Panels[fa];
        var first = Math.Max(fa, fb);
        var second = Math.Min(fa, fb);
        b.Faces.RemoveAt(first);
        b.Panels.RemoveAt(first);
        b.Faces.RemoveAt(second);
        b.Panels.RemoveAt(second);

        for (var i = 0; i < n; i++)
        {
            var j = (i + 1) % n;
            var mi = c[((bestOffset - i) % n + n) % n];
            var mj = c[((bestOffset - j) % n + n) % n];
            b.AddFace([a[i], a[j], mj, mi], panel);
        }

        return true;
    }

    private static double Distance(MeshVertex p, MeshVertex q)
    {
        double dx = p.X - q.X, dy = p.Y - q.Y, dz = p.Z - q.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>
    /// Knife along the plane coordinate[<paramref name="axis"/>] = <paramref name="offset"/> (solid-local, 1/1024 m): edges
    /// it crosses gain a vertex, and every face it crosses splits in two. Faces it would cut into more than two are left.
    /// </summary>
    public static bool Knife(MeshBuilder b, int axis, int offset)
    {
        double Side(int v) => (axis == 0 ? b.Vertices[v].X : axis == 1 ? b.Vertices[v].Y : b.Vertices[v].Z) - offset;

        var mids = new Dictionary<(int, int), int>();

        foreach (var face in b.Faces)
        {
            for (var i = 0; i < face.Length; i++)
            {
                int p = face[i], q = face[(i + 1) % face.Length];
                double sp = Side(p), sq = Side(q);
                var key = MeshBuilder.Edge(p, q);

                if (sp * sq >= 0 || mids.ContainsKey(key))
                    continue;

                var t = sp / (sp - sq);
                var a = b.Vertices[p];
                var c = b.Vertices[q];
                var mid = Round(a.X + (c.X - a.X) * t, a.Y + (c.Y - a.Y) * t, a.Z + (c.Z - a.Z) * t);
                mid = axis == 0 ? mid with { X = offset } : axis == 1 ? mid with { Y = offset } : mid with { Z = offset };
                mids[key] = b.AddVertex(mid);
            }
        }

        if (mids.Count == 0)
            return Fail("The knife plane misses the mesh");

        var onPlane = new HashSet<int>(mids.Values);

        for (var v = 0; v < b.Vertices.Count; v++)
        {
            if (Side(v) == 0)
                onPlane.Add(v);
        }

        InsertMidpoints(b, mids);
        var count = b.Faces.Count;

        for (var f = 0; f < count; f++)
        {
            var face = b.Faces[f];
            var cut = new List<int>();

            for (var i = 0; i < face.Length; i++)
            {
                if (onPlane.Contains(face[i]))
                    cut.Add(i);
            }

            if (cut.Count != 2 || (cut[1] - cut[0]) == 1 || (cut[0] == 0 && cut[1] == face.Length - 1))
                continue;

            var first = new List<int>();
            var second = new List<int>();

            for (var k = cut[0]; k != cut[1]; k = (k + 1) % face.Length)
                first.Add(face[k]);

            first.Add(face[cut[1]]);

            for (var k = cut[1]; k != cut[0]; k = (k + 1) % face.Length)
                second.Add(face[k]);

            second.Add(face[cut[0]]);
            b.Faces[f] = first.ToArray();
            b.AddFace(second.ToArray(), b.Panels[f]);
        }

        return true;
    }

    /// <summary>
    /// Symmetrise across local X = 0: faces on the +X side (every corner at x ≥ 0, some above) are kept and mirrored with
    /// reversed winding; vertices on the plane are shared. Live mirror runs this after every op.
    /// </summary>
    public static bool MirrorX(MeshBuilder b)
    {
        var keep = new List<(int[] Face, ushort Panel)>();

        for (var f = 0; f < b.Faces.Count; f++)
        {
            var face = b.Faces[f];
            var positive = false;
            var negative = false;

            foreach (var v in face)
            {
                positive |= b.Vertices[v].X > 0;
                negative |= b.Vertices[v].X < 0;
            }

            if (positive && !negative)
                keep.Add((face, b.Panels[f]));
        }

        if (keep.Count == 0)
            return Fail("Mirror needs faces on the +X side, closed at X = 0 (loop cut or knife there first)");

        var mirror = new Dictionary<int, int>();
        var creases = b.Creases.ToArray();
        b.Faces.Clear();
        b.Panels.Clear();

        foreach (var (face, panel) in keep)
        {
            b.AddFace(face, panel);
            var copy = new int[face.Length];

            for (var i = 0; i < face.Length; i++)
            {
                var v = face[face.Length - 1 - i];

                if (b.Vertices[v].X == 0)
                {
                    copy[i] = v;
                    continue;
                }

                if (!mirror.TryGetValue(v, out var m))
                {
                    var p = b.Vertices[v];
                    m = b.AddVertex(new MeshVertex(-p.X, p.Y, p.Z));
                    mirror[v] = m;
                }

                copy[i] = m;
            }

            b.AddFace(copy, panel);
        }

        foreach (var (p, q) in creases)
        {
            if (mirror.TryGetValue(p, out var mp) | mirror.TryGetValue(q, out var mq))
                b.Creases.Add(MeshBuilder.Edge(mirror.ContainsKey(p) ? mp : p, mirror.ContainsKey(q) ? mq : q));
        }

        return true;
    }

    public static void SetCrease(MeshBuilder b, IEnumerable<(int A, int B)> edges, bool sharp)
    {
        foreach (var (p, q) in edges)
        {
            if (sharp)
                b.Creases.Add(MeshBuilder.Edge(p, q));
            else
                b.Creases.Remove(MeshBuilder.Edge(p, q));
        }
    }
}
