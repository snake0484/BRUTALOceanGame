using System.Numerics;
using System.Runtime.CompilerServices;

namespace BrutalGame.Terrain;

/// <summary>
/// Surface nets over a padded chunk. Works in voxel units; output positions are chunk-local metres (× chunk VoxelScale).
/// Skirts (docs/terrain-lod.md): a border vertex is one whose cell index is -1 or 31 on some axis (the cell layers
/// shared with a neighbour). An edge used by exactly one triangle whose two vertices share a border face is open;
/// each open edge gets a quad extruded 1 voxel against the edge's average normal, wound like its triangle.
/// </summary>
public sealed class SurfaceNetsMesher
{
    private const int P = ChunkConstants.Padded;
    private const int PP = ChunkConstants.PaddedArea;
    private const int Last = ChunkConstants.Size - 1;
    private const int MaxIndicesPerCell = 18;
    private const float Quantise = 256f;
    private const float InvQuantise = 1f / Quantise;
    private const int InitialEdgeCapacity = 4096;

    private static ReadOnlySpan<byte> EdgeA => [0, 2, 4, 6, 0, 1, 4, 5, 0, 1, 2, 3];
    private static ReadOnlySpan<byte> EdgeB => [1, 3, 5, 7, 2, 3, 6, 7, 4, 5, 6, 7];

    private struct BorderEdge
    {
        public uint Lo;
        public uint Hi;
        public uint From;
        public int Count;
    }

    private readonly int[] _cellVertex = new int[ChunkConstants.PaddedVolume];
    private byte[] _vertexBorder = new byte[4096];
    private BorderEdge[] _edges = new BorderEdge[InitialEdgeCapacity];
    private int _edgeCount;

    public int LastSkirtTriangles { get; private set; }

    [SkipLocalsInit]
    public unsafe void Mesh(VoxelChunk chunk, ChunkMesh output, bool skirts = true)
    {
        output.Clear();
        LastSkirtTriangles = 0;

        if (!chunk.HasSurface)
            return;

        var c = stackalloc float[8];
        var mats = stackalloc byte[8];
        var edgeA = EdgeA;
        var edgeB = EdgeB;
        var vertices = output.Vertices;
        var indices = output.Indices;
        var vertexCount = 0;
        var indexCount = 0;
        var scale = chunk.Coord.VoxelScale;

        if (_vertexBorder.Length < vertices.Length)
            _vertexBorder = new byte[vertices.Length];

        var border = _vertexBorder;

        fixed (sbyte* d = chunk.Density)
        fixed (byte* m = chunk.Material)
        fixed (int* cv = _cellVertex)
        {
            for (var z = -1; z <= Last; z++)
            {
                var bz = z < 0 ? 16 : z == Last ? 32 : 0;

                for (var y = -1; y <= Last; y++)
                {
                    var i = ChunkConstants.Index(-1, y, z);
                    var byz = bz | (y < 0 ? 4 : y == Last ? 8 : 0);

                    for (var x = -1; x <= Last; x++, i++)
                    {
                        int d0 = d[i], d1 = d[i + 1], d2 = d[i + P], d3 = d[i + 1 + P];
                        int d4 = d[i + PP], d5 = d[i + 1 + PP], d6 = d[i + P + PP], d7 = d[i + 1 + P + PP];
                        var mask = (int)(((uint)d0 >> 31) | (((uint)d1 >> 31) << 1) | (((uint)d2 >> 31) << 2) | (((uint)d3 >> 31) << 3)
                            | (((uint)d4 >> 31) << 4) | (((uint)d5 >> 31) << 5) | (((uint)d6 >> 31) << 6) | (((uint)d7 >> 31) << 7));

                        if (mask == 0 || mask == 255)
                            continue;

                        c[0] = d0; c[1] = d1; c[2] = d2; c[3] = d3;
                        c[4] = d4; c[5] = d5; c[6] = d6; c[7] = d7;

                        if (vertexCount == vertices.Length || indexCount + MaxIndicesPerCell > indices.Length)
                        {
                            output.EnsureCapacity(vertexCount + 1, indexCount + MaxIndicesPerCell);
                            vertices = output.Vertices;
                            indices = output.Indices;

                            if (border.Length < vertices.Length)
                            {
                                Array.Resize(ref _vertexBorder, vertices.Length);
                                border = _vertexBorder;
                            }
                        }

                        ref var v = ref vertices[vertexCount];
                        v.Position = CellVertex(c, mask, edgeA, edgeB, x, y, z, scale, out var f);
                        v.Normal = Gradient(c, f);
                        v.Material = DominantMaterial(m, mats, i, mask);
                        border[vertexCount] = (byte)(byz | (x < 0 ? 1 : x == Last ? 2 : 0));
                        cv[i] = vertexCount++;

                        if (x < 0 || y < 0 || z < 0)
                            continue;

                        var solid = (mask & 1) != 0;

                        if (((mask ^ (mask >> 1)) & 1) != 0)
                            indexCount = Quad(indices, indexCount, cv[i - P - PP], cv[i - PP], cv[i], cv[i - P], solid);

                        if (((mask ^ (mask >> 2)) & 1) != 0)
                            indexCount = Quad(indices, indexCount, cv[i - 1 - PP], cv[i - 1], cv[i], cv[i - PP], solid);

                        if (((mask ^ (mask >> 4)) & 1) != 0)
                            indexCount = Quad(indices, indexCount, cv[i - 1 - P], cv[i - P], cv[i], cv[i - 1], solid);
                    }
                }
            }
        }

        output.VertexCount = vertexCount;
        output.IndexCount = indexCount;

        if (skirts)
            AddSkirts(output, scale);
    }

    private void AddSkirts(ChunkMesh output, float scale)
    {
        Array.Clear(_edges);
        _edgeCount = 0;

        var border = _vertexBorder;
        var indices = output.Indices;
        var indexCount = output.IndexCount;

        for (var t = 0; t < indexCount; t += 3)
        {
            uint a = indices[t], b = indices[t + 1], c = indices[t + 2];
            int ba = border[a], bb = border[b], bc = border[c];

            if ((ba | bb | bc) == 0)
                continue;

            if ((ba & bb) != 0)
                AddEdge(a, b);

            if ((bb & bc) != 0)
                AddEdge(b, c);

            if ((bc & ba) != 0)
                AddEdge(c, a);
        }

        var open = 0;

        foreach (ref readonly var e in _edges.AsSpan())
        {
            if (e.Count == 1)
                open++;
        }

        if (open == 0)
            return;

        var vertexCount = output.VertexCount;
        output.EnsureCapacity(vertexCount + open * 2, indexCount + open * 6);
        var vertices = output.Vertices;
        indices = output.Indices;

        foreach (ref readonly var e in _edges.AsSpan())
        {
            if (e.Count != 1)
                continue;

            var a = e.From;
            var b = e.From == e.Lo ? e.Hi : e.Lo;
            ref readonly var va = ref vertices[a];
            ref readonly var vb = ref vertices[b];
            var n = va.Normal + vb.Normal;
            var len2 = n.LengthSquared();
            n = len2 > 1e-12f ? n * (scale / MathF.Sqrt(len2)) : new Vector3(0f, scale, 0f);

            var a2 = (uint)vertexCount;
            var b2 = a2 + 1;
            vertices[a2] = va with { Position = va.Position - n };
            vertices[b2] = vb with { Position = vb.Position - n };
            vertexCount += 2;

            indices[indexCount] = b;
            indices[indexCount + 1] = a;
            indices[indexCount + 2] = a2;
            indices[indexCount + 3] = b;
            indices[indexCount + 4] = a2;
            indices[indexCount + 5] = b2;
            indexCount += 6;
        }

        output.VertexCount = vertexCount;
        output.IndexCount = indexCount;
        LastSkirtTriangles = open * 2;
    }

    private void AddEdge(uint from, uint to)
    {
        if ((_edgeCount + 1) * 2 > _edges.Length)
            GrowEdges();

        var lo = Math.Min(from, to);
        var hi = Math.Max(from, to);
        var mask = _edges.Length - 1;
        var slot = (int)(((lo * 0x9E3779B1u) ^ (hi * 0x85EBCA77u)) & (uint)mask);

        while (true)
        {
            ref var e = ref _edges[slot];

            if (e.Count == 0)
            {
                e = new BorderEdge { Lo = lo, Hi = hi, From = from, Count = 1 };
                _edgeCount++;
                return;
            }

            if (e.Lo == lo && e.Hi == hi)
            {
                e.Count++;
                return;
            }

            slot = (slot + 1) & mask;
        }
    }

    private void GrowEdges()
    {
        var old = _edges;
        _edges = new BorderEdge[old.Length * 2];
        var mask = _edges.Length - 1;

        foreach (var e in old)
        {
            if (e.Count == 0)
                continue;

            var slot = (int)(((e.Lo * 0x9E3779B1u) ^ (e.Hi * 0x85EBCA77u)) & (uint)mask);

            while (_edges[slot].Count != 0)
                slot = (slot + 1) & mask;

            _edges[slot] = e;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe Vector3 CellVertex(float* c, int mask, ReadOnlySpan<byte> edgeA, ReadOnlySpan<byte> edgeB, int x, int y, int z, float scale, out Vector3 f)
    {
        var sum = Vector3.Zero;
        var n = 0;

        for (var e = 0; e < 12; e++)
        {
            int a = edgeA[e], b = edgeB[e];

            if ((((mask >> a) ^ (mask >> b)) & 1) == 0)
                continue;

            var t = c[a] / (c[a] - c[b]);
            var pa = new Vector3(a & 1, (a >> 1) & 1, a >> 2);
            var pb = new Vector3(b & 1, (b >> 1) & 1, b >> 2);
            sum += pa + (pb - pa) * t;
            n++;
        }

        f = sum / n;
        f = new Vector3(MathF.Round(f.X * Quantise), MathF.Round(f.Y * Quantise), MathF.Round(f.Z * Quantise)) * InvQuantise;
        return (new Vector3(x, y, z) + f) * scale;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe Vector3 Gradient(float* c, Vector3 f)
    {
        float fx = f.X, fy = f.Y, fz = f.Z;
        float gx0 = 1f - fx, gy0 = 1f - fy, gz0 = 1f - fz;
        var gx = gy0 * gz0 * (c[1] - c[0]) + fy * gz0 * (c[3] - c[2]) + gy0 * fz * (c[5] - c[4]) + fy * fz * (c[7] - c[6]);
        var gy = gx0 * gz0 * (c[2] - c[0]) + fx * gz0 * (c[3] - c[1]) + gx0 * fz * (c[6] - c[4]) + fx * fz * (c[7] - c[5]);
        var gz = gx0 * gy0 * (c[4] - c[0]) + fx * gy0 * (c[5] - c[1]) + gx0 * fy * (c[6] - c[2]) + fx * fy * (c[7] - c[3]);
        var g = new Vector3(gx, gy, gz);
        var len2 = g.LengthSquared();

        if (len2 < 1e-12f)
        {
            g = new Vector3(c[1] + c[3] + c[5] + c[7] - c[0] - c[2] - c[4] - c[6],
                c[2] + c[3] + c[6] + c[7] - c[0] - c[1] - c[4] - c[5],
                c[4] + c[5] + c[6] + c[7] - c[0] - c[1] - c[2] - c[3]);
            len2 = g.LengthSquared();

            if (len2 < 1e-12f)
                return Vector3.UnitY;
        }

        return g / MathF.Sqrt(len2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe uint DominantMaterial(byte* m, byte* mats, int i, int mask)
    {
        mats[0] = m[i]; mats[1] = m[i + 1]; mats[2] = m[i + P]; mats[3] = m[i + 1 + P];
        mats[4] = m[i + PP]; mats[5] = m[i + 1 + PP]; mats[6] = m[i + P + PP]; mats[7] = m[i + 1 + P + PP];
        uint best = 0;
        var bestCount = 0;

        for (var a = 0; a < 8; a++)
        {
            if ((mask & (1 << a)) == 0)
                continue;

            var count = 0;

            for (var b = a; b < 8; b++)
            {
                if ((mask & (1 << b)) != 0 && mats[b] == mats[a])
                    count++;
            }

            if (count > bestCount)
            {
                bestCount = count;
                best = mats[a];
            }
        }

        return best;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Quad(uint[] indices, int n, int a, int b, int c, int d, bool solidBase)
    {
        indices[n] = (uint)a;

        if (solidBase)
        {
            indices[n + 1] = (uint)b;
            indices[n + 2] = (uint)c;
            indices[n + 3] = (uint)a;
            indices[n + 4] = (uint)c;
            indices[n + 5] = (uint)d;
        }
        else
        {
            indices[n + 1] = (uint)c;
            indices[n + 2] = (uint)b;
            indices[n + 3] = (uint)a;
            indices[n + 4] = (uint)d;
            indices[n + 5] = (uint)c;
        }

        return n + 6;
    }
}
