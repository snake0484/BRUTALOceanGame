using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace BrutalGame.Ships;

/// <summary>Solid-local vertex in 1/1024 m; |coord| ≤ 2^18 (256 m), so integer predicates on it are exact.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct MeshVertex(int X, int Y, int Z);

/// <summary>
/// A closed polygon control mesh for <see cref="SolidShape.Mesh"/> solids (ship-hulls "Mesh solids", D11): n-gons of 3-16
/// corners wound CCW seen from outside, sharp crease edges, a panel per face and 0-2 levels of integer Catmull-Clark.
/// Several solids may share one entry (mirror, duplicate, hollow). Immutable once built: edits make a new mesh, so undo
/// keeps snapshots and caches key on <see cref="MeshHash"/>.
/// </summary>
public sealed class HullMesh
{
    public const int MaxVertices = 4096;
    public const int MaxFaces = 4096;
    public const int MinCorners = 3;
    public const int MaxCorners = 16;
    public const int MaxLevels = 2;
    public const int MaxCoordinate = 1 << 18;

    public HullMesh(MeshVertex[] vertices, byte[] faceSizes, ushort[] corners, ushort[] panels, ulong[] creases, byte levels)
    {
        Vertices = vertices;
        FaceSizes = faceSizes;
        Corners = corners;
        Panels = panels;
        Creases = creases;
        SubdivLevels = levels;
        FaceStarts = new int[faceSizes.Length + 1];

        for (var f = 0; f < faceSizes.Length; f++)
            FaceStarts[f + 1] = FaceStarts[f] + faceSizes[f];

        MeshHash = XxHash64.Hash(CanonicalBytes());
    }

    public readonly MeshVertex[] Vertices;
    public readonly byte[] FaceSizes;
    public readonly ushort[] Corners;
    public readonly ushort[] Panels;

    /// <summary>Sharp edges as sorted keys (min vertex &lt;&lt; 16 | max vertex), kept by subdivision.</summary>
    public readonly ulong[] Creases;

    public readonly byte SubdivLevels;

    /// <summary>Corner offset of each face; one past the last at <c>FaceCount</c>.</summary>
    public readonly int[] FaceStarts;

    /// <summary>xxHash64 of <see cref="CanonicalBytes"/>: cache key and the server's "already validated" key.</summary>
    public readonly ulong MeshHash;

    public int FaceCount => FaceSizes.Length;

    public ReadOnlySpan<ushort> Face(int f) => Corners.AsSpan(FaceStarts[f], FaceSizes[f]);

    public static ulong EdgeKey(int a, int b) => a < b ? ((ulong)a << 16) | (uint)b : ((ulong)b << 16) | (uint)a;

    public bool IsCrease(int a, int b) => Array.BinarySearch(Creases, EdgeKey(a, b)) >= 0;

    /// <summary>
    /// levels u8, 0 u8, vertex count u16, face count u16, crease count u16, corner count u32, vertices (i32 x, y, z),
    /// face sizes (u8), corners (u16), panels (u16), creases (u16 a, u16 b). Little endian; the hash input and the plan's
    /// on-disk form.
    /// </summary>
    public byte[] CanonicalBytes()
    {
        var bytes = new byte[HeaderBytes + Vertices.Length * 12 + FaceSizes.Length * 3 + Corners.Length * 2 + Creases.Length * 4];
        Write(bytes);
        return bytes;
    }

    public const int HeaderBytes = 12;

    public int ByteSize => HeaderBytes + Vertices.Length * 12 + FaceSizes.Length * 3 + Corners.Length * 2 + Creases.Length * 4;

    public void Write(Span<byte> s)
    {
        s[0] = SubdivLevels;
        s[1] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(s[2..], (ushort)Vertices.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(s[4..], (ushort)FaceSizes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(s[6..], (ushort)Creases.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(s[8..], (uint)Corners.Length);
        var o = HeaderBytes;

        foreach (var v in Vertices)
        {
            BinaryPrimitives.WriteInt32LittleEndian(s[o..], v.X);
            BinaryPrimitives.WriteInt32LittleEndian(s[(o + 4)..], v.Y);
            BinaryPrimitives.WriteInt32LittleEndian(s[(o + 8)..], v.Z);
            o += 12;
        }

        FaceSizes.CopyTo(s[o..]);
        o += FaceSizes.Length;

        foreach (var c in Corners)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(s[o..], c);
            o += 2;
        }

        foreach (var p in Panels)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(s[o..], p);
            o += 2;
        }

        foreach (var e in Creases)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(s[o..], (ushort)(e >> 16));
            BinaryPrimitives.WriteUInt16LittleEndian(s[(o + 2)..], (ushort)e);
            o += 4;
        }
    }

    /// <summary>
    /// Reads one mesh at the start of <paramref name="s"/>, structurally checked (counts within limits, face sizes 3-16,
    /// corner and crease indices in range, sorted unique creases); topology is the validator's. Null on any failure.
    /// </summary>
    public static HullMesh? Read(ReadOnlySpan<byte> s, out int consumed)
    {
        consumed = 0;

        if (s.Length < HeaderBytes)
            return null;

        var levels = s[0];
        var vertexCount = BinaryPrimitives.ReadUInt16LittleEndian(s[2..]);
        var faceCount = BinaryPrimitives.ReadUInt16LittleEndian(s[4..]);
        var creaseCount = BinaryPrimitives.ReadUInt16LittleEndian(s[6..]);
        var cornerCount = BinaryPrimitives.ReadUInt32LittleEndian(s[8..]);

        if (s[1] != 0 || levels > MaxLevels || vertexCount > MaxVertices || faceCount > MaxFaces || cornerCount > (uint)faceCount * MaxCorners)
            return null;

        var size = HeaderBytes + vertexCount * 12 + faceCount * 3 + (int)cornerCount * 2 + creaseCount * 4;

        if (s.Length < size)
            return null;

        var o = HeaderBytes;
        var vertices = new MeshVertex[vertexCount];

        for (var i = 0; i < vertexCount; i++, o += 12)
            vertices[i] = new MeshVertex(BinaryPrimitives.ReadInt32LittleEndian(s[o..]), BinaryPrimitives.ReadInt32LittleEndian(s[(o + 4)..]), BinaryPrimitives.ReadInt32LittleEndian(s[(o + 8)..]));

        var sizes = s.Slice(o, faceCount).ToArray();
        o += faceCount;
        long total = 0;

        foreach (var n in sizes)
        {
            if (n is < MinCorners or > MaxCorners)
                return null;

            total += n;
        }

        if (total != cornerCount)
            return null;

        var corners = new ushort[cornerCount];

        for (var i = 0; i < corners.Length; i++, o += 2)
        {
            corners[i] = BinaryPrimitives.ReadUInt16LittleEndian(s[o..]);

            if (corners[i] >= vertexCount)
                return null;
        }

        var panels = new ushort[faceCount];

        for (var i = 0; i < panels.Length; i++, o += 2)
            panels[i] = BinaryPrimitives.ReadUInt16LittleEndian(s[o..]);

        var creases = new ulong[creaseCount];

        for (var i = 0; i < creases.Length; i++, o += 4)
        {
            int a = BinaryPrimitives.ReadUInt16LittleEndian(s[o..]), b = BinaryPrimitives.ReadUInt16LittleEndian(s[(o + 2)..]);

            if (a >= b || b >= vertexCount || (i > 0 && EdgeKey(a, b) <= creases[i - 1]))
                return null;

            creases[i] = EdgeKey(a, b);
        }

        consumed = size;
        return new HullMesh(vertices, sizes, corners, panels, creases, levels);
    }

    /// <summary>Largest |coordinate| per axis in 1/1024 m: the solid's half extent for binning.</summary>
    public (int X, int Y, int Z) Extent()
    {
        int x = 0, y = 0, z = 0;

        foreach (var v in Vertices)
        {
            x = Math.Max(x, Math.Abs(v.X));
            y = Math.Max(y, Math.Abs(v.Y));
            z = Math.Max(z, Math.Abs(v.Z));
        }

        return (x, y, z);
    }

    /// <summary>Half extents in 1/256 m covering the mesh (rounded up, at least 1/16 m): a mesh solid's HalfX..Z.</summary>
    public (ushort X, ushort Y, ushort Z) HalfUnits()
    {
        var (x, y, z) = Extent();
        return (Half(x), Half(y), Half(z));

        static ushort Half(int units) => (ushort)Math.Clamp((units + 3) / 4, 16, ushort.MaxValue);
    }

    public bool ContentEquals(HullMesh other) => MeshHash == other.MeshHash && CanonicalBytes().AsSpan().SequenceEqual(other.CanonicalBytes());
}
