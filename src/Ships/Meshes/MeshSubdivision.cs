namespace BrutalGame.Ships;

/// <summary>Triangles of a mesh after subdivision: integer vertices (1/1024 m) and index triples, CCW from outside.</summary>
public sealed class MeshTriangles
{
    public MeshVertex[] Vertices = [];
    public int[] Indices = [];

    public int TriangleCount => Indices.Length / 3;
}

/// <summary>
/// Integer Catmull-Clark (ship-hulls "Integer subdivision"): every new point is an exact rational of integer inputs,
/// rounded once to the 1/1024 m grid (half away from zero), so every peer gets the same triangles. Crease edges keep
/// their midpoint and pass the crease to both halves; a vertex on two creases uses the crease rule (6v + a + b) / 8, on
/// three or more it stays put. Level 0 n-gons triangulate by ear clipping from corner 0 in the face's Newell plane.
/// </summary>
public static class MeshSubdivision
{
    public static MeshTriangles Triangulate(HullMesh mesh) => Triangulate(mesh, mesh.SubdivLevels);

    public static MeshTriangles Triangulate(HullMesh mesh, int levels)
    {
        var vertices = new List<(long X, long Y, long Z)>(mesh.Vertices.Length);

        foreach (var v in mesh.Vertices)
            vertices.Add((v.X, v.Y, v.Z));

        var faces = new List<int[]>(mesh.FaceCount);

        for (var f = 0; f < mesh.FaceCount; f++)
            faces.Add(mesh.Face(f).ToArray().Select(c => (int)c).ToArray());

        var creases = new HashSet<long>();

        foreach (var key in mesh.Creases)
            creases.Add(Key((int)(key >> 16), (int)(key & 0xFFFF)));

        for (var level = 0; level < levels; level++)
            (vertices, faces, creases) = Step(vertices, faces, creases);

        var result = new MeshTriangles { Vertices = vertices.Select(v => new MeshVertex((int)v.X, (int)v.Y, (int)v.Z)).ToArray() };
        var indices = new List<int>(faces.Count * 6);

        foreach (var face in faces)
            Triangulate(result.Vertices, face, indices);

        result.Indices = indices.ToArray();
        return result;
    }

    private static long Key(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

    private static (List<(long X, long Y, long Z)>, List<int[]>, HashSet<long>) Step(List<(long X, long Y, long Z)> v, List<int[]> faces, HashSet<long> creases)
    {
        var n = v.Count;
        var facePoints = new (long X, long Y, long Z)[faces.Count];
        var edgeFaces = new Dictionary<long, (int A, int B)>();

        for (var f = 0; f < faces.Count; f++)
        {
            var face = faces[f];
            long sx = 0, sy = 0, sz = 0;

            for (var i = 0; i < face.Length; i++)
            {
                var p = v[face[i]];
                (sx, sy, sz) = (sx + p.X, sy + p.Y, sz + p.Z);
                var key = Key(face[i], face[(i + 1) % face.Length]);
                edgeFaces[key] = edgeFaces.TryGetValue(key, out var e) ? (e.A, f) : (f, -1);
            }

            facePoints[f] = (Div(sx, face.Length), Div(sy, face.Length), Div(sz, face.Length));
        }

        var output = new List<(long X, long Y, long Z)>(n + edgeFaces.Count + faces.Count);
        output.AddRange(new (long, long, long)[n]);
        var edgeIndex = new Dictionary<long, int>(edgeFaces.Count);

        foreach (var (key, (fa, fb)) in edgeFaces)
        {
            var a = v[(int)(key >> 32)];
            var b = v[(int)(key & 0xFFFFFFFF)];
            edgeIndex[key] = output.Count;

            if (fb < 0 || creases.Contains(key))
            {
                output.Add((Div(a.X + b.X, 2), Div(a.Y + b.Y, 2), Div(a.Z + b.Z, 2)));
                continue;
            }

            var p = facePoints[fa];
            var q = facePoints[fb];
            output.Add((Div(a.X + b.X + p.X + q.X, 4), Div(a.Y + b.Y + p.Y + q.Y, 4), Div(a.Z + b.Z + p.Z + q.Z, 4)));
        }

        // Vertex points: valence, face-point sums, edge sums and crease neighbours per original vertex.
        var valence = new int[n];
        var faceCount = new int[n];
        var faceSum = new (long X, long Y, long Z)[n];
        var edgeSum = new (long X, long Y, long Z)[n];
        var creaseCount = new int[n];
        var creaseSum = new (long X, long Y, long Z)[n];

        for (var f = 0; f < faces.Count; f++)
        {
            foreach (var c in faces[f])
            {
                faceCount[c]++;
                faceSum[c] = Add(faceSum[c], facePoints[f]);
            }
        }

        foreach (var (key, (_, fb)) in edgeFaces)
        {
            int a = (int)(key >> 32), b = (int)(key & 0xFFFFFFFF);
            valence[a]++;
            valence[b]++;
            edgeSum[a] = Add(edgeSum[a], Add(v[a], v[b]));
            edgeSum[b] = Add(edgeSum[b], Add(v[a], v[b]));

            if (fb < 0 || creases.Contains(key))
            {
                creaseCount[a]++;
                creaseCount[b]++;
                creaseSum[a] = Add(creaseSum[a], v[b]);
                creaseSum[b] = Add(creaseSum[b], v[a]);
            }
        }

        for (var i = 0; i < n; i++)
        {
            var p = v[i];

            if (creaseCount[i] >= 3 || valence[i] < 3)
            {
                output[i] = p;
            }
            else if (creaseCount[i] == 2)
            {
                var s = creaseSum[i];
                output[i] = (Div(6 * p.X + s.X, 8), Div(6 * p.Y + s.Y, 8), Div(6 * p.Z + s.Z, 8));
            }
            else
            {
                // (F + 2R + (k − 3) V) / k with F, R averages over k = valence: (ΣF + Σ(V + Vi) + k (k − 3) V) / k².
                var k = (long)valence[i];
                var fs = faceSum[i];
                var es = edgeSum[i];
                var d = k * k;
                output[i] = (Div(fs.X + es.X + k * (k - 3) * p.X, d), Div(fs.Y + es.Y + k * (k - 3) * p.Y, d), Div(fs.Z + es.Z + k * (k - 3) * p.Z, d));
            }
        }

        var faceIndex = output.Count;
        output.AddRange(facePoints);
        var quads = new List<int[]>(faces.Count * 4);
        var next = new HashSet<long>();

        for (var f = 0; f < faces.Count; f++)
        {
            var face = faces[f];

            for (var i = 0; i < face.Length; i++)
            {
                var prev = face[(i + face.Length - 1) % face.Length];
                var cur = face[i];
                var nxt = face[(i + 1) % face.Length];
                quads.Add([cur, edgeIndex[Key(cur, nxt)], faceIndex + f, edgeIndex[Key(prev, cur)]]);
            }
        }

        foreach (var key in creases)
        {
            if (!edgeIndex.TryGetValue(key, out var mid))
                continue;

            next.Add(Key((int)(key >> 32), mid));
            next.Add(Key((int)(key & 0xFFFFFFFF), mid));
        }

        return (output, quads, next);
    }

    private static (long, long, long) Add((long X, long Y, long Z) a, (long X, long Y, long Z) b) => (a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    /// <summary>Integer division rounding half away from zero.</summary>
    private static long Div(long num, long den)
    {
        var q = Math.DivRem(Math.Abs(num), den, out var r);

        if (r * 2 >= den)
            q++;

        return num < 0 ? -q : q;
    }

    /// <summary>Quads split on their 0-2 diagonal; larger faces ear-clip in the Newell plane starting at corner 0.</summary>
    public static void Triangulate(MeshVertex[] v, ReadOnlySpan<int> face, List<int> output)
    {
        if (face.Length == 3)
        {
            output.Add(face[0]);
            output.Add(face[1]);
            output.Add(face[2]);
            return;
        }

        if (face.Length == 4)
        {
            output.AddRange([face[0], face[1], face[2], face[0], face[2], face[3]]);
            return;
        }

        // Newell normal, then drop its largest axis to project.
        double nx = 0, ny = 0, nz = 0;

        for (var i = 0; i < face.Length; i++)
        {
            var a = v[face[i]];
            var b = v[face[(i + 1) % face.Length]];
            nx += (double)(a.Y - b.Y) * (a.Z + b.Z);
            ny += (double)(a.Z - b.Z) * (a.X + b.X);
            nz += (double)(a.X - b.X) * (a.Y + b.Y);
        }

        var axis = Math.Abs(nx) >= Math.Abs(ny) && Math.Abs(nx) >= Math.Abs(nz) ? 0 : Math.Abs(ny) >= Math.Abs(nz) ? 1 : 2;
        var flip = (axis == 0 ? nx : axis == 1 ? ny : nz) < 0;
        var ring = new List<int>(face.ToArray());
        var guard = ring.Count * ring.Count;

        while (ring.Count > 3 && guard-- > 0)
        {
            var clipped = false;

            for (var i = 0; i < ring.Count; i++)
            {
                int a = ring[(i + ring.Count - 1) % ring.Count], b = ring[i], c = ring[(i + 1) % ring.Count];

                if (!IsEar(v, ring, a, b, c, axis, flip))
                    continue;

                output.AddRange([a, b, c]);
                ring.RemoveAt(i);
                clipped = true;
                break;
            }

            if (!clipped)
                break;
        }

        // Degenerate leftovers fan from the first corner.
        for (var i = 1; i + 1 < ring.Count; i++)
            output.AddRange([ring[0], ring[i], ring[i + 1]]);
    }

    private static bool IsEar(MeshVertex[] v, List<int> ring, int a, int b, int c, int axis, bool flip)
    {
        var (ax, ay) = Project(v[a], axis);
        var (bx, by) = Project(v[b], axis);
        var (cx, cy) = Project(v[c], axis);
        var cross = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);

        if (flip ? cross >= 0 : cross <= 0)
            return false;

        foreach (var p in ring)
        {
            if (p == a || p == b || p == c)
                continue;

            var (px, py) = Project(v[p], axis);

            if (Inside(px, py, ax, ay, bx, by, cx, cy, flip))
                return false;
        }

        return true;
    }

    private static bool Inside(long px, long py, long ax, long ay, long bx, long by, long cx, long cy, bool flip)
    {
        var s = flip ? -1 : 1;
        var d1 = s * ((bx - ax) * (py - ay) - (by - ay) * (px - ax));
        var d2 = s * ((cx - bx) * (py - by) - (cy - by) * (px - bx));
        var d3 = s * ((ax - cx) * (py - cy) - (ay - cy) * (px - cx));
        return d1 >= 0 && d2 >= 0 && d3 >= 0;
    }

    /// <summary>Drops one axis; the remaining pair is kept right-handed about it (Y, Z for X; Z, X for Y; X, Y for Z).</summary>
    private static (long, long) Project(MeshVertex p, int axis) => axis switch
    {
        0 => (p.Y, p.Z),
        1 => (p.Z, p.X),
        _ => (p.X, p.Y),
    };
}
