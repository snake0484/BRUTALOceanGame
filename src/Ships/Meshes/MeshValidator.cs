namespace BrutalGame.Ships;

public enum MeshIssue : byte
{
    None,
    Limits,
    RepeatedCorner,
    Open,
    NonManifold,
    DuplicateFace,
    InsideOut,
    TooManyTriangles,
    Degenerate,
    ShortEdge,
    SelfIntersection,
}

/// <summary>
/// ship-hulls "Mesh solids" validation, run by the designer on commit and by the server on save and import: limits,
/// closed (every edge in exactly two faces), oriented (each directed edge once, its reverse present), manifold (one fan
/// per vertex, no duplicate faces), positive volume, no degenerate triangle or edge under 1/64 m after subdivision, and
/// no self-intersection between non-adjacent subdivided triangles. Predicates are exact: vertices are integers, so
/// orientation determinants run in Int128. Stops at the first issue; Value says where (a face or triangle index).
/// </summary>
public static class MeshValidator
{
    public const int MaxTriangles = 65536;
    public const int MinEdgeUnits = 16;
    private const int MaxCached = 256;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, (byte[] Bytes, MeshIssue Issue, int Value)> Validated = new();

    /// <summary>
    /// <see cref="Validate"/> once per mesh content: results are cached by MeshHash, and a hit compares the canonical bytes,
    /// so a crafted xxHash64 collision cannot pass an invalid mesh.
    /// </summary>
    public static MeshIssue ValidateCached(HullMesh mesh, out int value)
    {
        var bytes = mesh.CanonicalBytes();

        if (Validated.TryGetValue(mesh.MeshHash, out var hit) && hit.Bytes.AsSpan().SequenceEqual(bytes))
        {
            value = hit.Value;
            return hit.Issue;
        }

        var issue = Validate(mesh, out value);

        if (Validated.Count >= MaxCached)
            Validated.Clear();

        Validated[mesh.MeshHash] = (bytes, issue, value);
        return issue;
    }

    public static MeshIssue Validate(HullMesh mesh, out int value)
    {
        value = 0;

        if (mesh.Vertices.Length > HullMesh.MaxVertices || mesh.FaceCount > HullMesh.MaxFaces || mesh.SubdivLevels > HullMesh.MaxLevels || mesh.FaceCount < 4)
            return MeshIssue.Limits;

        foreach (var v in mesh.Vertices)
        {
            if (Math.Abs(v.X) > HullMesh.MaxCoordinate || Math.Abs(v.Y) > HullMesh.MaxCoordinate || Math.Abs(v.Z) > HullMesh.MaxCoordinate)
                return MeshIssue.Limits;
        }

        var issue = Topology(mesh, out value);

        if (issue != MeshIssue.None)
            return issue;

        var tris = MeshSubdivision.Triangulate(mesh);

        if (tris.TriangleCount > MaxTriangles)
        {
            value = tris.TriangleCount;
            return MeshIssue.TooManyTriangles;
        }

        return Geometry(tris, out value);
    }

    private static MeshIssue Topology(HullMesh mesh, out int value)
    {
        value = 0;
        var directed = new Dictionary<(int, int), int>(mesh.Corners.Length);
        var faces = new HashSet<string>();

        for (var f = 0; f < mesh.FaceCount; f++)
        {
            var face = mesh.Face(f);
            value = f;

            for (var i = 0; i < face.Length; i++)
            {
                for (var j = i + 1; j < face.Length; j++)
                {
                    if (face[i] == face[j])
                        return MeshIssue.RepeatedCorner;
                }

                if (!directed.TryAdd((face[i], face[(i + 1) % face.Length]), f))
                    return MeshIssue.NonManifold;
            }

            var sorted = face.ToArray();
            Array.Sort(sorted);

            if (!faces.Add(string.Join(',', sorted)))
                return MeshIssue.DuplicateFace;
        }

        foreach (var ((a, b), f) in directed)
        {
            if (!directed.ContainsKey((b, a)))
            {
                value = f;
                return MeshIssue.Open;
            }
        }

        // One fan per vertex: walking from any outgoing edge around the vertex must visit all its outgoing edges.
        var next = new Dictionary<(int, int), int>(directed.Count);
        var outgoing = new int[mesh.Vertices.Length];

        for (var f = 0; f < mesh.FaceCount; f++)
        {
            var face = mesh.Face(f);

            for (var i = 0; i < face.Length; i++)
            {
                int prev = face[(i + face.Length - 1) % face.Length], cur = face[i];
                next[(cur, face[(i + 1) % face.Length])] = prev;
                outgoing[cur]++;
            }
        }

        var seen = new bool[mesh.Vertices.Length];

        foreach (var (a, b) in directed.Keys)
        {
            if (seen[a])
                continue;

            seen[a] = true;
            var count = 0;
            var to = b;

            do
            {
                // In the face holding a → to, the corner before a is p (edge p → a); its twin a → p is the next spoke.
                to = next[(a, to)];
                count++;
            }
            while (to != b && count <= outgoing[a]);

            if (count != outgoing[a])
            {
                value = a;
                return MeshIssue.NonManifold;
            }
        }

        return MeshIssue.None;
    }

    private static MeshIssue Geometry(MeshTriangles tris, out int value)
    {
        value = 0;
        var v = tris.Vertices;
        var idx = tris.Indices;
        Int128 volume = 0;

        for (var t = 0; t < tris.TriangleCount; t++)
        {
            value = t;
            MeshVertex a = v[idx[3 * t]], b = v[idx[3 * t + 1]], c = v[idx[3 * t + 2]];
            var (nx, ny, nz) = Cross(Sub(b, a), Sub(c, a));

            if (nx == 0 && ny == 0 && nz == 0)
                return MeshIssue.Degenerate;

            if (Short(a, b) || Short(b, c) || Short(c, a))
                return MeshIssue.ShortEdge;

            volume += (Int128)a.X * ((long)b.Y * c.Z - (long)b.Z * c.Y) - (Int128)a.Y * ((long)b.X * c.Z - (long)b.Z * c.X) + (Int128)a.Z * ((long)b.X * c.Y - (long)b.Y * c.X);
        }

        if (volume <= 0)
            return MeshIssue.InsideOut;

        var prepared = new PreparedMesh(tris);
        var candidates = new List<int>();

        for (var t = 0; t < tris.TriangleCount; t++)
        {
            candidates.Clear();
            var min = System.Numerics.Vector3.Min(prepared.A[t], System.Numerics.Vector3.Min(prepared.B[t], prepared.C[t]));
            var max = System.Numerics.Vector3.Max(prepared.A[t], System.Numerics.Vector3.Max(prepared.B[t], prepared.C[t]));
            prepared.Overlapping(min, max, candidates);

            foreach (var u in candidates)
            {
                if (u <= t || Shares(idx, t, u))
                    continue;

                if (TrianglesIntersect(v, idx, t, u))
                {
                    value = t;
                    return MeshIssue.SelfIntersection;
                }
            }
        }

        return MeshIssue.None;
    }

    private static bool Short(MeshVertex a, MeshVertex b)
    {
        long dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return dx * dx + dy * dy + dz * dz < (long)MinEdgeUnits * MinEdgeUnits;
    }

    private static bool Shares(int[] idx, int t, int u)
    {
        for (var i = 0; i < 3; i++)
        {
            for (var j = 0; j < 3; j++)
            {
                if (idx[3 * t + i] == idx[3 * u + j])
                    return true;
            }
        }

        return false;
    }

    /// <summary>Two triangles with no shared vertex intersect iff an edge of one crosses (or touches) the other.</summary>
    private static bool TrianglesIntersect(MeshVertex[] v, int[] idx, int t, int u)
    {
        MeshVertex a0 = v[idx[3 * t]], a1 = v[idx[3 * t + 1]], a2 = v[idx[3 * t + 2]];
        MeshVertex b0 = v[idx[3 * u]], b1 = v[idx[3 * u + 1]], b2 = v[idx[3 * u + 2]];

        return SegmentTriangle(a0, a1, b0, b1, b2) || SegmentTriangle(a1, a2, b0, b1, b2) || SegmentTriangle(a2, a0, b0, b1, b2)
            || SegmentTriangle(b0, b1, a0, a1, a2) || SegmentTriangle(b1, b2, a0, a1, a2) || SegmentTriangle(b2, b0, a0, a1, a2);
    }

    /// <summary>Exact: segment pq meets triangle abc (touching counts). Coplanar cases use the 2D projection.</summary>
    private static bool SegmentTriangle(MeshVertex p, MeshVertex q, MeshVertex a, MeshVertex b, MeshVertex c)
    {
        var sp = Orient(a, b, c, p);
        var sq = Orient(a, b, c, q);

        if ((sp > 0 && sq > 0) || (sp < 0 && sq < 0))
            return false;

        if (sp == 0 && sq == 0)
            return CoplanarSegmentTriangle(p, q, a, b, c);

        var s1 = Orient(p, q, a, b);
        var s2 = Orient(p, q, b, c);
        var s3 = Orient(p, q, c, a);
        return (s1 >= 0 && s2 >= 0 && s3 >= 0) || (s1 <= 0 && s2 <= 0 && s3 <= 0);
    }

    private static bool CoplanarSegmentTriangle(MeshVertex p, MeshVertex q, MeshVertex a, MeshVertex b, MeshVertex c)
    {
        var (nx, ny, nz) = Cross(Sub(b, a), Sub(c, a));
        var axis = Abs(nx) >= Abs(ny) && Abs(nx) >= Abs(nz) ? 0 : Abs(ny) >= Abs(nz) ? 1 : 2;
        var (px, py) = P(p, axis);
        var (qx, qy) = P(q, axis);
        var (ax, ay) = P(a, axis);
        var (bx, by) = P(b, axis);
        var (cx, cy) = P(c, axis);

        if (PointInTriangle(px, py, ax, ay, bx, by, cx, cy) || PointInTriangle(qx, qy, ax, ay, bx, by, cx, cy))
            return true;

        return Segments(px, py, qx, qy, ax, ay, bx, by) || Segments(px, py, qx, qy, bx, by, cx, cy) || Segments(px, py, qx, qy, cx, cy, ax, ay);
    }

    private static bool PointInTriangle(long px, long py, long ax, long ay, long bx, long by, long cx, long cy)
    {
        var d1 = Orient2(ax, ay, bx, by, px, py);
        var d2 = Orient2(bx, by, cx, cy, px, py);
        var d3 = Orient2(cx, cy, ax, ay, px, py);
        return (d1 >= 0 && d2 >= 0 && d3 >= 0) || (d1 <= 0 && d2 <= 0 && d3 <= 0);
    }

    private static bool Segments(long px, long py, long qx, long qy, long ax, long ay, long bx, long by)
    {
        var d1 = Orient2(ax, ay, bx, by, px, py);
        var d2 = Orient2(ax, ay, bx, by, qx, qy);
        var d3 = Orient2(px, py, qx, qy, ax, ay);
        var d4 = Orient2(px, py, qx, qy, bx, by);

        // Collinear: they touch only if their extents overlap on the line.
        if (d1 == 0 && d2 == 0 && d3 == 0 && d4 == 0)
            return Math.Max(Math.Min(px, qx), Math.Min(ax, bx)) <= Math.Min(Math.Max(px, qx), Math.Max(ax, bx))
                && Math.Max(Math.Min(py, qy), Math.Min(ay, by)) <= Math.Min(Math.Max(py, qy), Math.Max(ay, by));

        return ((d1 >= 0 && d2 <= 0) || (d1 <= 0 && d2 >= 0)) && ((d3 >= 0 && d4 <= 0) || (d3 <= 0 && d4 >= 0));
    }

    private static int Orient2(long ax, long ay, long bx, long by, long cx, long cy)
    {
        var d = (Int128)(bx - ax) * (cy - ay) - (Int128)(by - ay) * (cx - ax);
        return d > 0 ? 1 : d < 0 ? -1 : 0;
    }

    /// <summary>Sign of det[b − a, c − a, d − a], exact.</summary>
    private static int Orient(MeshVertex a, MeshVertex b, MeshVertex c, MeshVertex d)
    {
        long bx = b.X - a.X, by = b.Y - a.Y, bz = b.Z - a.Z;
        long cx = c.X - a.X, cy = c.Y - a.Y, cz = c.Z - a.Z;
        long dx = d.X - a.X, dy = d.Y - a.Y, dz = d.Z - a.Z;
        var det = (Int128)bx * ((Int128)cy * dz - (Int128)cz * dy) - (Int128)by * ((Int128)cx * dz - (Int128)cz * dx) + (Int128)bz * ((Int128)cx * dy - (Int128)cy * dx);
        return det > 0 ? 1 : det < 0 ? -1 : 0;
    }

    private static (long, long, long) Sub(MeshVertex a, MeshVertex b) => ((long)a.X - b.X, (long)a.Y - b.Y, (long)a.Z - b.Z);

    private static (Int128, Int128, Int128) Cross((long X, long Y, long Z) a, (long X, long Y, long Z) b) =>
        ((Int128)a.Y * b.Z - (Int128)a.Z * b.Y, (Int128)a.Z * b.X - (Int128)a.X * b.Z, (Int128)a.X * b.Y - (Int128)a.Y * b.X);

    private static Int128 Abs(Int128 v) => v < 0 ? -v : v;

    private static (long, long) P(MeshVertex v, int axis) => axis switch
    {
        0 => (v.Y, v.Z),
        1 => (v.Z, v.X),
        _ => (v.X, v.Y),
    };
}
