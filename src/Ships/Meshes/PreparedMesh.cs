using System.Collections.Concurrent;
using System.Numerics;

namespace BrutalGame.Ships;

/// <summary>
/// A mesh ready for the bake and tools: its subdivided triangles in brush-local metres and an AABB tree over them
/// (median split, ≤ 4 triangles a leaf). Queries: ray crossings (sign rows), first hit on a segment (Hermite data,
/// picking), closest point within a radius (the distance band), triangles in a box. Read-only after build: shared
/// across bake workers. Prepared once per (MeshHash, levels) through <see cref="MeshCache"/>.
/// </summary>
public sealed class PreparedMesh
{
    private const int LeafSize = 4;
    private const double MetresPerUnit = 1.0 / 1024.0;

    public readonly MeshTriangles Source;
    public readonly Vector3[] A, B, C;
    public readonly Vector3[] Normals;

    /// <summary>Area-weighted vertex normals of the subdivided mesh, for smooth shading between facets.</summary>
    public readonly Vector3[] VertexNormals;
    public readonly Vector3 Min, Max;

    private readonly int[] _order;
    private readonly Node[] _nodes;
    private int _nodeCount;

    private struct Node
    {
        public Vector3 Min, Max;
        public int Left, Right;
        public int First, Count;
    }

    public PreparedMesh(MeshTriangles triangles)
    {
        Source = triangles;
        var n = triangles.TriangleCount;
        A = new Vector3[n];
        B = new Vector3[n];
        C = new Vector3[n];
        Normals = new Vector3[n];
        Min = new Vector3(float.MaxValue);
        Max = new Vector3(float.MinValue);

        for (var t = 0; t < n; t++)
        {
            A[t] = Metres(triangles.Vertices[triangles.Indices[3 * t]]);
            B[t] = Metres(triangles.Vertices[triangles.Indices[3 * t + 1]]);
            C[t] = Metres(triangles.Vertices[triangles.Indices[3 * t + 2]]);
            var normal = Vector3.Cross(B[t] - A[t], C[t] - A[t]);
            var length = normal.Length();
            Normals[t] = length > 0f ? normal / length : Vector3.UnitY;
            Min = Vector3.Min(Min, Vector3.Min(A[t], Vector3.Min(B[t], C[t])));
            Max = Vector3.Max(Max, Vector3.Max(A[t], Vector3.Max(B[t], C[t])));
        }

        VertexNormals = new Vector3[triangles.Vertices.Length];

        for (var t = 0; t < n; t++)
        {
            var weighted = Vector3.Cross(B[t] - A[t], C[t] - A[t]);
            VertexNormals[triangles.Indices[3 * t]] += weighted;
            VertexNormals[triangles.Indices[3 * t + 1]] += weighted;
            VertexNormals[triangles.Indices[3 * t + 2]] += weighted;
        }

        for (var v = 0; v < VertexNormals.Length; v++)
        {
            var length = VertexNormals[v].Length();
            VertexNormals[v] = length > 0f ? VertexNormals[v] / length : Vector3.UnitY;
        }

        _order = new int[n];

        for (var i = 0; i < n; i++)
            _order[i] = i;

        _nodes = new Node[Math.Max(1, 2 * n)];

        if (n > 0)
            Build(0, n);
    }

    public int TriangleCount => A.Length;

    /// <summary>The vertex normals interpolated at point <paramref name="p"/> on triangle <paramref name="t"/> (barycentric).</summary>
    public Vector3 SmoothNormal(int t, Vector3 p)
    {
        var v0 = B[t] - A[t];
        var v1 = C[t] - A[t];
        var v2 = p - A[t];
        float d00 = Vector3.Dot(v0, v0), d01 = Vector3.Dot(v0, v1), d11 = Vector3.Dot(v1, v1), d20 = Vector3.Dot(v2, v0), d21 = Vector3.Dot(v2, v1);
        var denom = d00 * d11 - d01 * d01;

        if (MathF.Abs(denom) < 1e-20f)
            return Normals[t];

        var v = Math.Clamp((d11 * d20 - d01 * d21) / denom, 0f, 1f);
        var w = Math.Clamp((d00 * d21 - d01 * d20) / denom, 0f, 1f - v);
        var u = 1f - v - w;
        var idx = Source.Indices;
        var n = VertexNormals[idx[3 * t]] * u + VertexNormals[idx[3 * t + 1]] * v + VertexNormals[idx[3 * t + 2]] * w;
        var length = n.Length();
        return length > 1e-6f ? n / length : Normals[t];
    }

    public static Vector3 Metres(MeshVertex v) => new((float)(v.X * MetresPerUnit), (float)(v.Y * MetresPerUnit), (float)(v.Z * MetresPerUnit));

    private int Build(int first, int count)
    {
        var index = _nodeCount++;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var cmin = new Vector3(float.MaxValue);
        var cmax = new Vector3(float.MinValue);

        for (var i = first; i < first + count; i++)
        {
            var t = _order[i];
            min = Vector3.Min(min, Vector3.Min(A[t], Vector3.Min(B[t], C[t])));
            max = Vector3.Max(max, Vector3.Max(A[t], Vector3.Max(B[t], C[t])));
            var c = (A[t] + B[t] + C[t]) / 3f;
            cmin = Vector3.Min(cmin, c);
            cmax = Vector3.Max(cmax, c);
        }

        _nodes[index] = new Node { Min = min, Max = max, Left = -1, Right = -1, First = first, Count = count };

        if (count <= LeafSize)
            return index;

        var extent = cmax - cmin;
        var axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
        var keys = new float[count];

        for (var i = 0; i < count; i++)
        {
            var t = _order[first + i];
            var c = A[t] + B[t] + C[t];
            keys[i] = axis == 0 ? c.X : axis == 1 ? c.Y : c.Z;
        }

        var slice = _order.AsSpan(first, count);
        keys.AsSpan().Sort(slice);
        var half = count / 2;
        var left = Build(first, half);
        var right = Build(first + half, count - half);
        _nodes[index].Left = left;
        _nodes[index].Right = right;
        _nodes[index].Count = 0;
        return index;
    }

    /// <summary>
    /// Every crossing of the line <paramref name="origin"/> + t·<paramref name="direction"/> (t ≥ 0) with a triangle, as t,
    /// unsorted, into <paramref name="hits"/>. Watertight edge test in double so a crossing through a shared edge counts once.
    /// </summary>
    public void Crossings(Vector3d origin, Vector3d direction, List<double> hits)
    {
        if (TriangleCount == 0)
            return;

        Span<int> stack = stackalloc int[64];
        var top = 0;
        stack[top++] = 0;
        var inv = new Vector3d(1.0 / direction.X, 1.0 / direction.Y, 1.0 / direction.Z);

        while (top > 0)
        {
            ref readonly var node = ref _nodes[stack[--top]];

            if (!RayBox(origin, inv, node.Min, node.Max, double.MaxValue))
                continue;

            if (node.Count > 0)
            {
                for (var i = node.First; i < node.First + node.Count; i++)
                {
                    var t = _order[i];

                    if (Watertight(origin, direction, A[t], B[t], C[t], out var hit) && hit >= 0.0)
                        hits.Add(hit);
                }

                continue;
            }

            stack[top++] = node.Left;
            stack[top++] = node.Right;
        }
    }

    /// <summary>Nearest crossing on the segment from <paramref name="origin"/> along <paramref name="delta"/> (t in [0, 1]).</summary>
    public bool FirstHit(Vector3d origin, Vector3d delta, out double t, out int triangle)
    {
        t = double.MaxValue;
        triangle = -1;

        if (TriangleCount == 0)
            return false;

        Span<int> stack = stackalloc int[64];
        var top = 0;
        stack[top++] = 0;
        var inv = new Vector3d(1.0 / delta.X, 1.0 / delta.Y, 1.0 / delta.Z);

        while (top > 0)
        {
            ref readonly var node = ref _nodes[stack[--top]];

            if (!RayBox(origin, inv, node.Min, node.Max, Math.Min(t, 1.0)))
                continue;

            if (node.Count > 0)
            {
                for (var i = node.First; i < node.First + node.Count; i++)
                {
                    var tri = _order[i];

                    if (Watertight(origin, delta, A[tri], B[tri], C[tri], out var hit) && hit >= 0.0 && hit <= 1.0 && hit < t)
                    {
                        t = hit;
                        triangle = tri;
                    }
                }

                continue;
            }

            stack[top++] = node.Left;
            stack[top++] = node.Right;
        }

        return triangle >= 0;
    }

    /// <summary>Distance to the nearest triangle if closer than <paramref name="radius"/>, else <paramref name="radius"/>.</summary>
    public float Distance(Vector3 p, float radius)
    {
        if (TriangleCount == 0)
            return radius;

        Span<int> stack = stackalloc int[64];
        var top = 0;
        stack[top++] = 0;
        var best = radius * radius;

        while (top > 0)
        {
            ref readonly var node = ref _nodes[stack[--top]];
            var d = Vector3.Max(Vector3.Max(node.Min - p, p - node.Max), Vector3.Zero);

            if (d.LengthSquared() >= best)
                continue;

            if (node.Count > 0)
            {
                for (var i = node.First; i < node.First + node.Count; i++)
                {
                    var t = _order[i];
                    var q = ClosestPoint(p, A[t], B[t], C[t]);
                    best = MathF.Min(best, Vector3.DistanceSquared(p, q));
                }

                continue;
            }

            stack[top++] = node.Left;
            stack[top++] = node.Right;
        }

        return MathF.Sqrt(best);
    }

    /// <summary>Triangles whose bounds overlap the box, into <paramref name="output"/>.</summary>
    public void Overlapping(Vector3 min, Vector3 max, List<int> output)
    {
        if (TriangleCount == 0)
            return;

        Span<int> stack = stackalloc int[64];
        var top = 0;
        stack[top++] = 0;

        while (top > 0)
        {
            ref readonly var node = ref _nodes[stack[--top]];

            if (node.Min.X > max.X || node.Min.Y > max.Y || node.Min.Z > max.Z || node.Max.X < min.X || node.Max.Y < min.Y || node.Max.Z < min.Z)
                continue;

            if (node.Count > 0)
            {
                for (var i = node.First; i < node.First + node.Count; i++)
                    output.Add(_order[i]);

                continue;
            }

            stack[top++] = node.Left;
            stack[top++] = node.Right;
        }
    }

    private static bool RayBox(Vector3d o, Vector3d inv, Vector3 min, Vector3 max, double tMax)
    {
        double t0 = 0.0, t1 = tMax;
        Slab(o.X, inv.X, min.X, max.X, ref t0, ref t1);
        Slab(o.Y, inv.Y, min.Y, max.Y, ref t0, ref t1);
        Slab(o.Z, inv.Z, min.Z, max.Z, ref t0, ref t1);
        return t0 <= t1;
    }

    private static void Slab(double o, double inv, double min, double max, ref double t0, ref double t1)
    {
        var a = (min - o) * inv;
        var b = (max - o) * inv;

        if (double.IsNaN(a) || double.IsNaN(b))
        {
            if (o < min || o > max)
                t0 = double.MaxValue;

            return;
        }

        if (a > b)
            (a, b) = (b, a);

        t0 = Math.Max(t0, a - 1e-9);
        t1 = Math.Min(t1, b + 1e-9);
    }

    /// <summary>
    /// Watertight ray-triangle test (Woop, Benthin, Wald 2013) in double: edge functions of the sheared, projected
    /// triangle, ties broken by the same rule on both triangles sharing an edge.
    /// </summary>
    public static bool Watertight(Vector3d o, Vector3d d, Vector3 a3, Vector3 b3, Vector3 c3, out double t)
    {
        t = 0.0;
        var kz = Math.Abs(d.X) >= Math.Abs(d.Y) && Math.Abs(d.X) >= Math.Abs(d.Z) ? 0 : Math.Abs(d.Y) >= Math.Abs(d.Z) ? 1 : 2;
        var kx = (kz + 1) % 3;
        var ky = (kx + 1) % 3;

        if (Get(d, kz) < 0.0)
            (kx, ky) = (ky, kx);

        var dz = Get(d, kz);
        var sx = Get(d, kx) / dz;
        var sy = Get(d, ky) / dz;
        var sz = 1.0 / dz;

        var a = new Vector3d(a3.X - o.X, a3.Y - o.Y, a3.Z - o.Z);
        var b = new Vector3d(b3.X - o.X, b3.Y - o.Y, b3.Z - o.Z);
        var c = new Vector3d(c3.X - o.X, c3.Y - o.Y, c3.Z - o.Z);
        double ax = Get(a, kx) - sx * Get(a, kz), ay = Get(a, ky) - sy * Get(a, kz);
        double bx = Get(b, kx) - sx * Get(b, kz), by = Get(b, ky) - sy * Get(b, kz);
        double cx = Get(c, kx) - sx * Get(c, kz), cy = Get(c, ky) - sy * Get(c, kz);
        var u = cx * by - cy * bx;
        var v = ax * cy - ay * cx;
        var w = bx * ay - by * ax;
        var det = u + v + w;

        if (det == 0.0)
            return false;

        // Inside when every edge function has det's sign. A zero edge function (the ray through a shared edge) counts
        // only for the triangle whose edge, oriented by det's sign, is top-left: the neighbour traverses the edge the
        // other way, computes the same products negated, and so takes the other side. Parity stays exact.
        var s = det > 0.0 ? 1.0 : -1.0;

        if (u * s < 0.0 || v * s < 0.0 || w * s < 0.0)
            return false;

        if ((u == 0.0 && !TopLeft(s * (cx - bx), s * (cy - by))) || (v == 0.0 && !TopLeft(s * (ax - cx), s * (ay - cy))) || (w == 0.0 && !TopLeft(s * (bx - ax), s * (by - ay))))
            return false;

        var tScaled = u * sz * Get(a, kz) + v * sz * Get(b, kz) + w * sz * Get(c, kz);
        t = tScaled / det;
        return true;
    }

    private static double Get(Vector3d v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    private static bool TopLeft(double ex, double ey) => ey > 0.0 || (ey == 0.0 && ex > 0.0);

    /// <summary>Closest point on a triangle (Ericson, Real-Time Collision Detection 5.1.5).</summary>
    public static Vector3 ClosestPoint(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a;
        var ac = c - a;
        var ap = p - a;
        var d1 = Vector3.Dot(ab, ap);
        var d2 = Vector3.Dot(ac, ap);

        if (d1 <= 0f && d2 <= 0f)
            return a;

        var bp = p - b;
        var d3 = Vector3.Dot(ab, bp);
        var d4 = Vector3.Dot(ac, bp);

        if (d3 >= 0f && d4 <= d3)
            return b;

        var vc = d1 * d4 - d3 * d2;

        if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            return a + ab * (d1 / (d1 - d3));

        var cp = p - c;
        var d5 = Vector3.Dot(ab, cp);
        var d6 = Vector3.Dot(ac, cp);

        if (d6 >= 0f && d5 <= d6)
            return c;

        var vb = d5 * d2 - d1 * d6;

        if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            return a + ac * (d2 / (d2 - d6));

        var va = d3 * d6 - d5 * d4;

        if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f)
            return b + (c - b) * ((d4 - d3) / (d4 - d3 + (d5 - d6)));

        var denom = 1f / (va + vb + vc);
        return a + ab * (vb * denom) + ac * (vc * denom);
    }
}

/// <summary>Prepared meshes by (MeshHash, levels), shared by bake workers, picking and validation. Thread safe.</summary>
public static class MeshCache
{
    private const int MaxEntries = 128;
    private static readonly ConcurrentDictionary<(ulong, int), PreparedMesh> Entries = new();

    public static PreparedMesh Get(HullMesh mesh) => Get(mesh, mesh.SubdivLevels);

    public static PreparedMesh Get(HullMesh mesh, int levels)
    {
        var key = (mesh.MeshHash, levels);

        if (Entries.TryGetValue(key, out var prepared))
            return prepared;

        if (Entries.Count >= MaxEntries)
            Entries.Clear();

        prepared = new PreparedMesh(MeshSubdivision.Triangulate(mesh, levels));
        return Entries.GetOrAdd(key, prepared);
    }
}
