using System.Numerics;

namespace BrutalGame.Physics;

/// <summary>
/// Managed BVH over an indexed triangle mesh, used for ray casts. Rebuilt in place by <see cref="Build"/> (its arrays
/// only grow, so a warmed instance builds without allocating); read-only between builds, so safe to query from any
/// thread then. Exists because the shipped Jolt native library does not export any NarrowPhaseQuery / Shape ray cast
/// entry points.
/// </summary>
internal sealed class TriangleBvh
{
    const int LeafSize = 6;
    const int MaxStack = 64;
    const float DirEpsilon = 1e-20f;
    const float DetEpsilon = 1e-12f;

    struct Node
    {
        public Vector3 Min;
        public int Start;
        public Vector3 Max;
        public int Count;
    }

    Vector3[] _vertices = [];
    int[] _indices = [];
    Node[] _nodes = new Node[1];
    Vector3[] _centroids = [];
    int[] _order = [];
    float[] _keys = [];
    int _triangleCount;
    int _nodeCount;

    public Vector3 Min => _nodes[0].Min;
    public Vector3 Max => _nodes[0].Max;
    public int TriangleCount => _triangleCount;
    public long MemoryBytes => _vertices.Length * 12L + _indices.Length * 4L + _nodes.Length * 32L;

    public TriangleBvh()
    {
    }

    public TriangleBvh(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<uint> indices) => Build(vertices, indices);

    public void Build(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<uint> indices)
    {
        int triCount = indices.Length / 3;
        Grow(ref _centroids, triCount);
        Grow(ref _order, triCount);
        Grow(ref _keys, triCount);
        Grow(ref _vertices, vertices.Length);
        Grow(ref _indices, triCount * 3);
        Grow(ref _nodes, 2 * triCount / 3 + 2);

        for (int i = 0; i < triCount; i++)
        {
            _centroids[i] = (vertices[(int)indices[i * 3]] + vertices[(int)indices[i * 3 + 1]] + vertices[(int)indices[i * 3 + 2]]) * (1f / 3f);
            _order[i] = i;
        }

        _triangleCount = triCount;
        _nodeCount = 1;
        BuildNode(0, 0, triCount, vertices, indices);
        vertices.CopyTo(_vertices);

        for (int i = 0; i < triCount; i++)
        {
            int src = _order[i] * 3;
            _indices[i * 3] = (int)indices[src];
            _indices[i * 3 + 1] = (int)indices[src + 1];
            _indices[i * 3 + 2] = (int)indices[src + 2];
        }
    }

    /// <summary>Grows with headroom, so a pooled instance settles after a growth or two.</summary>
    static void Grow<T>(ref T[] array, int length)
    {
        if (array.Length < length)
            array = new T[Math.Max(length + length / 2, array.Length * 2)];
    }

    void BuildNode(int nodeIndex, int start, int count, ReadOnlySpan<Vector3> vertices, ReadOnlySpan<uint> indices)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var cMin = min;
        var cMax = max;

        for (int i = start; i < start + count; i++)
        {
            int t = _order[i] * 3;

            for (int k = 0; k < 3; k++)
            {
                var v = vertices[(int)indices[t + k]];
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }

            cMin = Vector3.Min(cMin, _centroids[_order[i]]);
            cMax = Vector3.Max(cMax, _centroids[_order[i]]);
        }

        if (count <= LeafSize)
        {
            _nodes[nodeIndex] = new Node { Min = min, Max = max, Start = start, Count = count };
            return;
        }

        var extent = cMax - cMin;
        int axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;

        for (int i = start; i < start + count; i++)
            _keys[i] = _centroids[_order[i]][axis];

        Array.Sort(_keys, _order, start, count);

        int left = _nodeCount;
        _nodeCount += 2;

        if (_nodes.Length < _nodeCount)
            Array.Resize(ref _nodes, _nodes.Length * 2);

        _nodes[nodeIndex] = new Node { Min = min, Max = max, Start = left, Count = 0 };

        int half = count / 2;
        BuildNode(left, start, half, vertices, indices);
        BuildNode(left + 1, start + half, count - half, vertices, indices);
    }

    public static Vector3 InverseDirection(Vector3 dir) => new(1f / SafeComponent(dir.X), 1f / SafeComponent(dir.Y), 1f / SafeComponent(dir.Z));

    static float SafeComponent(float d) => MathF.Abs(d) >= DirEpsilon ? d : d < 0f ? -DirEpsilon : DirEpsilon;

    public static bool RayAabb(Vector3 origin, Vector3 invDir, Vector3 min, Vector3 max, float maxT)
    {
        var t0 = (min - origin) * invDir;
        var t1 = (max - origin) * invDir;
        var tNear = Vector3.Min(t0, t1);
        var tFar = Vector3.Max(t0, t1);
        float tEnter = MathF.Max(MathF.Max(tNear.X, tNear.Y), MathF.Max(tNear.Z, 0f));
        float tExit = MathF.Min(MathF.Min(tFar.X, tFar.Y), MathF.Min(tFar.Z, maxT));
        return tEnter <= tExit;
    }

    /// <summary>Closest double-sided hit in local space with t below <paramref name="best"/>. Normal follows triangle winding.</summary>
    public bool CastRay(Vector3 origin, Vector3 dir, Vector3 invDir, ref float best, out Vector3 normal)
    {
        Span<int> stack = stackalloc int[MaxStack];
        int sp = 0;
        int hitTri = -1;
        stack[sp++] = 0;

        while (sp > 0)
        {
            ref readonly var node = ref _nodes[stack[--sp]];

            if (!RayAabb(origin, invDir, node.Min, node.Max, best))
                continue;

            if (node.Count == 0)
            {
                stack[sp++] = node.Start;
                stack[sp++] = node.Start + 1;
                continue;
            }

            for (int i = node.Start; i < node.Start + node.Count; i++)
            {
                if (RayTriangle(origin, dir, i, best, out float t))
                {
                    best = t;
                    hitTri = i;
                }
            }
        }

        if (hitTri < 0)
        {
            normal = default;
            return false;
        }

        var a = _vertices[_indices[hitTri * 3]];
        normal = Vector3.Normalize(Vector3.Cross(_vertices[_indices[hitTri * 3 + 1]] - a, _vertices[_indices[hitTri * 3 + 2]] - a));
        return true;
    }

    bool RayTriangle(Vector3 origin, Vector3 dir, int tri, float maxT, out float t)
    {
        var a = _vertices[_indices[tri * 3]];
        var e1 = _vertices[_indices[tri * 3 + 1]] - a;
        var e2 = _vertices[_indices[tri * 3 + 2]] - a;
        var p = Vector3.Cross(dir, e2);
        float det = Vector3.Dot(e1, p);
        t = 0f;

        if (MathF.Abs(det) < DetEpsilon)
            return false;

        float invDet = 1f / det;
        var s = origin - a;
        float u = Vector3.Dot(s, p) * invDet;

        if (u < 0f || u > 1f)
            return false;

        var q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(dir, q) * invDet;

        if (v < 0f || u + v > 1f)
            return false;

        t = Vector3.Dot(e2, q) * invDet;
        return t >= 0f && t < maxT;
    }
}
