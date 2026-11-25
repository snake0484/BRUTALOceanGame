using System.Numerics;
using System.Runtime.CompilerServices;
using BrutalGame.Terrain;

namespace BrutalGame.Ships;

/// <summary>
/// Hermite data of one sign-change edge: where the surface crosses it (0..1 from the lower sample), its exact normal toward
/// air (places the vertex) and a shading normal (smoothed across a subdivided mesh's facets; the same for primitives).
/// </summary>
public interface IHermiteField
{
    void Edge(int x, int y, int z, int axis, int densityA, int densityB, out float t, out Vector3 normal, out Vector3 shade);
}

/// <summary>
/// Dual contouring over a padded 34³ density (ship-hulls: same cell and quad topology as <see cref="SurfaceNetsMesher"/>,
/// only the vertex placement differs). Each mixed cell takes its sign-change edges' Hermite data (computed once per
/// edge and cached) and places its vertex at the QEF minimiser Σ (n·(x − p))², solved about the mass point with small
/// eigenvalues truncated, clamped to the cell: corners and creases land where the brushes put them. A sibling of the
/// terrain mesher so terrain output stays untouched. Positions snap to 1/256 cell like surface nets; the vertex normal
/// is the mean Hermite normal (hull.frag sharpens creases with the face normal).
/// </summary>
public sealed class HullDualContour
{
    private const int P = ChunkConstants.Padded;
    private const int PP = ChunkConstants.PaddedArea;
    private const int Last = ChunkConstants.Size - 1;
    private const float Quantise = 256f;
    private const float InvQuantise = 1f / Quantise;
    private const float Truncate = 0.05f;
    private const float Margin = 0.25f;
    private const float FarMargin = 1f;

    private static ReadOnlySpan<byte> EdgeA => [0, 2, 4, 6, 0, 1, 4, 5, 0, 1, 2, 3];
    private static ReadOnlySpan<byte> EdgeB => [1, 3, 5, 7, 2, 3, 6, 7, 4, 5, 6, 7];

    private readonly int[] _cellVertex = new int[ChunkConstants.PaddedVolume];
    private readonly float[] _edgeT = new float[ChunkConstants.PaddedVolume * 3];
    private readonly Vector3[] _edgeN = new Vector3[ChunkConstants.PaddedVolume * 3];
    private readonly Vector3[] _edgeS = new Vector3[ChunkConstants.PaddedVolume * 3];
    private readonly int[] _edgeStamp = new int[ChunkConstants.PaddedVolume * 3];
    private int _stamp;

    public void Mesh(VoxelChunk chunk, ChunkMesh output, float cellScale, IHermiteField field)
    {
        output.Clear();

        if (!chunk.HasSurface)
            return;

        _stamp++;
        var d = chunk.Density;
        var m = chunk.Material;
        var cv = _cellVertex;
        var vertexCount = 0;
        var indexCount = 0;
        Span<int> c = stackalloc int[8];

        for (var z = -1; z <= Last; z++)
        {
            for (var y = -1; y <= Last; y++)
            {
                var i = ChunkConstants.Index(-1, y, z);

                for (var x = -1; x <= Last; x++, i++)
                {
                    c[0] = d[i]; c[1] = d[i + 1]; c[2] = d[i + P]; c[3] = d[i + 1 + P];
                    c[4] = d[i + PP]; c[5] = d[i + 1 + PP]; c[6] = d[i + P + PP]; c[7] = d[i + 1 + P + PP];
                    var mask = 0;

                    for (var k = 0; k < 8; k++)
                        mask |= (int)((uint)c[k] >> 31) << k;

                    if (mask == 0 || mask == 255)
                        continue;

                    if (vertexCount == output.Vertices.Length)
                    {
                        Array.Resize(ref output.Vertices, output.Vertices.Length * 2);
                        Array.Resize(ref output.Packed, output.Vertices.Length);
                    }

                    if (indexCount + 18 > output.Indices.Length)
                        Array.Resize(ref output.Indices, output.Indices.Length * 2);

                    ref var v = ref output.Vertices[vertexCount];
                    v.Position = (new Vector3(x, y, z) + CellVertex(x, y, z, i, c, mask, field, out v.Normal)) * cellScale;
                    v.Material = DominantMaterial(m, i, mask);
                    cv[i] = vertexCount++;

                    if (x < 0 || y < 0 || z < 0)
                        continue;

                    var solid = (mask & 1) != 0;
                    var indices = output.Indices;

                    if (((mask ^ (mask >> 1)) & 1) != 0)
                        indexCount = Quad(indices, indexCount, output.Vertices, cv[i - P - PP], cv[i - PP], cv[i], cv[i - P], solid);

                    if (((mask ^ (mask >> 2)) & 1) != 0)
                        indexCount = Quad(indices, indexCount, output.Vertices, cv[i - 1 - PP], cv[i - 1], cv[i], cv[i - PP], solid);

                    if (((mask ^ (mask >> 4)) & 1) != 0)
                        indexCount = Quad(indices, indexCount, output.Vertices, cv[i - 1 - P], cv[i - P], cv[i], cv[i - 1], solid);
                }
            }
        }

        output.VertexCount = vertexCount;
        output.IndexCount = indexCount;
        output.Pack(cellScale);
    }

    /// <summary>The QEF vertex in cell-local units (0..1 per axis).</summary>
    private Vector3 CellVertex(int x, int y, int z, int i, ReadOnlySpan<int> c, int mask, IHermiteField field, out Vector3 normal)
    {
        // Symmetric AᵀA (xx, xy, xz, yy, yz, zz), Aᵀb, mass point.
        float a00 = 0, a01 = 0, a02 = 0, a11 = 0, a12 = 0, a22 = 0;
        var atb = Vector3.Zero;
        var mass = Vector3.Zero;
        var nsum = Vector3.Zero;
        var count = 0;
        var edgeA = EdgeA;
        var edgeB = EdgeB;

        for (var e = 0; e < 12; e++)
        {
            int a = edgeA[e], b = edgeB[e];

            if ((((mask >> a) ^ (mask >> b)) & 1) == 0)
                continue;

            var axis = e >> 2;
            int ax = a & 1, ay = (a >> 1) & 1, az = a >> 2;
            var sample = i + ax + ay * P + az * PP;
            var key = sample * 3 + axis;

            if (_edgeStamp[key] != _stamp)
            {
                field.Edge(x + ax, y + ay, z + az, axis, c[a], c[b], out _edgeT[key], out _edgeN[key], out _edgeS[key]);
                _edgeStamp[key] = _stamp;
            }

            var t = _edgeT[key];
            var n = _edgeN[key];
            var p = new Vector3(ax, ay, az);

            if (axis == 0)
                p.X += t;
            else if (axis == 1)
                p.Y += t;
            else
                p.Z += t;

            a00 += n.X * n.X; a01 += n.X * n.Y; a02 += n.X * n.Z;
            a11 += n.Y * n.Y; a12 += n.Y * n.Z; a22 += n.Z * n.Z;
            atb += n * Vector3.Dot(n, p);
            mass += p;
            nsum += _edgeS[key];
            count++;
        }

        mass /= count;
        var len = nsum.Length();
        normal = len > 1e-6f ? nsum / len : Vector3.UnitY;

        // x = mass + pinv(AᵀA) (Aᵀb − AᵀA mass)
        var r = atb - new Vector3(a00 * mass.X + a01 * mass.Y + a02 * mass.Z, a01 * mass.X + a11 * mass.Y + a12 * mass.Z, a02 * mass.X + a12 * mass.Y + a22 * mass.Z);
        var f = mass + SolvePseudoInverse(a00, a01, a02, a11, a12, a22, r);

        // A minimiser far outside the cell is a near-degenerate system: fall back to the mass point. One a little
        // outside is a real feature passing beside the cell (a curved crease): keep it, clamped to a margin.
        if (!float.IsFinite(f.X) || !float.IsFinite(f.Y) || !float.IsFinite(f.Z) || Vector3.Max(Vector3.Abs(f - new Vector3(0.5f)), Vector3.Zero) is var off && MathF.Max(off.X, MathF.Max(off.Y, off.Z)) > 0.5f + FarMargin)
            f = mass;

        f = Vector3.Clamp(f, new Vector3(-Margin), new Vector3(1f + Margin));
        return new Vector3(MathF.Round(f.X * Quantise), MathF.Round(f.Y * Quantise), MathF.Round(f.Z * Quantise)) * InvQuantise;
    }

    /// <summary>pinv(S)·r for symmetric 3×3 S by cyclic Jacobi; eigenvalues under <see cref="Truncate"/> of the largest drop.</summary>
    private static Vector3 SolvePseudoInverse(float s00, float s01, float s02, float s11, float s12, float s22, Vector3 r)
    {
        Span<float> a = [s00, s01, s02, s01, s11, s12, s02, s12, s22];
        Span<float> v = [1, 0, 0, 0, 1, 0, 0, 0, 1];

        for (var sweep = 0; sweep < 6; sweep++)
        {
            Rotate(a, v, 0, 1);
            Rotate(a, v, 0, 2);
            Rotate(a, v, 1, 2);
        }

        var lmax = MathF.Max(MathF.Abs(a[0]), MathF.Max(MathF.Abs(a[4]), MathF.Abs(a[8])));

        if (lmax < 1e-12f)
            return Vector3.Zero;

        var result = Vector3.Zero;

        for (var k = 0; k < 3; k++)
        {
            var l = a[k * 4];

            if (MathF.Abs(l) < Truncate * lmax)
                continue;

            var e = new Vector3(v[k], v[3 + k], v[6 + k]);
            result += e * (Vector3.Dot(e, r) / l);
        }

        return result;
    }

    private static void Rotate(Span<float> a, Span<float> v, int p, int q)
    {
        var apq = a[p * 3 + q];

        if (MathF.Abs(apq) < 1e-12f)
            return;

        var app = a[p * 3 + p];
        var aqq = a[q * 3 + q];
        var theta = (aqq - app) / (2f * apq);
        var t = MathF.Sign(theta) / (MathF.Abs(theta) + MathF.Sqrt(theta * theta + 1f));

        if (theta == 0f)
            t = 1f;

        var cs = 1f / MathF.Sqrt(t * t + 1f);
        var sn = t * cs;

        for (var k = 0; k < 3; k++)
        {
            var akp = a[k * 3 + p];
            var akq = a[k * 3 + q];
            a[k * 3 + p] = cs * akp - sn * akq;
            a[k * 3 + q] = sn * akp + cs * akq;
        }

        for (var k = 0; k < 3; k++)
        {
            var apk = a[p * 3 + k];
            var aqk = a[q * 3 + k];
            a[p * 3 + k] = cs * apk - sn * aqk;
            a[q * 3 + k] = sn * apk + cs * aqk;
        }

        for (var k = 0; k < 3; k++)
        {
            var vkp = v[k * 3 + p];
            var vkq = v[k * 3 + q];
            v[k * 3 + p] = cs * vkp - sn * vkq;
            v[k * 3 + q] = sn * vkp + cs * vkq;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint DominantMaterial(byte[] m, int i, int mask)
    {
        Span<byte> mats = [m[i], m[i + 1], m[i + P], m[i + 1 + P], m[i + PP], m[i + 1 + PP], m[i + P + PP], m[i + 1 + P + PP]];
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

    /// <summary>
    /// Two triangles of a quad, split on the diagonal whose end vertices' normals agree most: a crease running diagonally
    /// through the grid then follows triangle edges instead of zig-zagging across them.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Quad(uint[] indices, int n, TerrainVertex[] v, int a, int b, int c, int d, bool solidBase)
    {
        int p0 = a, p1 = solidBase ? b : d, p2 = c, p3 = solidBase ? d : b;

        if (Vector3.Dot(v[p1].Normal, v[p3].Normal) > Vector3.Dot(v[p0].Normal, v[p2].Normal))
        {
            indices[n] = (uint)p0;
            indices[n + 1] = (uint)p1;
            indices[n + 2] = (uint)p3;
            indices[n + 3] = (uint)p1;
            indices[n + 4] = (uint)p2;
            indices[n + 5] = (uint)p3;
        }
        else
        {
            indices[n] = (uint)p0;
            indices[n + 1] = (uint)p1;
            indices[n + 2] = (uint)p2;
            indices[n + 3] = (uint)p0;
            indices[n + 4] = (uint)p2;
            indices[n + 5] = (uint)p3;
        }

        return n + 6;
    }
}
