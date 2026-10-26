using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using Brutal.JoltApi;
using Brutal.Numerics;

namespace BrutalGame.Physics;

/// <summary>
/// Pre-built static triangle mesh collision shape. <see cref="Create"/> is thread-safe and meant to run on worker
/// threads. The Jolt shape is reference counted: bodies hold their own reference, so disposing this right after
/// <see cref="PhysicsWorld.AddStatic"/> is fine, and one shape may back many bodies.
/// <para>
/// Uses a Jolt MeshShape when the native library supports it, otherwise a StaticCompoundShape of TriangleShapes
/// (see <see cref="JoltRuntime"/>). A managed ray cast BVH is kept only when native ray casts are unavailable.
/// </para>
/// </summary>
public sealed class StaticMeshShape : IDisposable
{
    const float MinEdgeLengthSq = 1e-8f;
    const float MinAreaSq = 1e-14f;

    Shape _shape;

    StaticMeshShape(Shape shape, TriangleBvh? bvh, int triangleCount)
    {
        _shape = shape;
        Bvh = bvh;
        TriangleCount = triangleCount;
    }

    internal TriangleBvh? Bvh { get; }
    internal Shape JoltShape => _shape.IsNull() ? throw new ObjectDisposedException(nameof(StaticMeshShape)) : _shape;
    public int TriangleCount { get; }

    /// <summary>Builds the shape. Vertices are body-local. Degenerate triangles are dropped; returns null if none remain.</summary>
    public static StaticMeshShape? Create(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<uint> indices)
    {
        if (indices.Length % 3 != 0)
            throw new ArgumentException("Index count must be a multiple of 3.", nameof(indices));

        var kept = new uint[indices.Length];
        int keptCount = 0;

        for (int i = 0; i < indices.Length; i += 3)
        {
            uint i0 = indices[i], i1 = indices[i + 1], i2 = indices[i + 2];

            if (i0 >= (uint)vertices.Length || i1 >= (uint)vertices.Length || i2 >= (uint)vertices.Length)
                throw new ArgumentOutOfRangeException(nameof(indices), "Index out of vertex range.");

            if (IsDegenerate(vertices[(int)i0], vertices[(int)i1], vertices[(int)i2]))
                continue;

            kept[keptCount++] = i0;
            kept[keptCount++] = i1;
            kept[keptCount++] = i2;
        }

        if (keptCount == 0)
            return null;

        JoltRuntime.EnsureInitialized();
        var keptSpan = kept.AsSpan(0, keptCount);
        var shape = JoltRuntime.NativeMeshShape ? CreateMeshShape(vertices, keptSpan) : CreateTriangleCompound(vertices, keptSpan);
        var bvh = JoltRuntime.NativeRayCast ? null : new TriangleBvh(vertices, keptSpan);
        return new StaticMeshShape(shape, bvh, keptCount / 3);
    }

    static bool IsDegenerate(Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a;
        var ac = c - a;

        if (ab.LengthSquared() < MinEdgeLengthSq || ac.LengthSquared() < MinEdgeLengthSq || (c - b).LengthSquared() < MinEdgeLengthSq)
            return true;

        return Vector3.Cross(ab, ac).LengthSquared() < MinAreaSq;
    }

    static Shape CreateMeshShape(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<uint> indices)
    {
        int triCount = indices.Length / 3;
        var triangles = ArrayPool<IndexedTriangle>.Shared.Rent(triCount);

        try
        {
            for (int i = 0; i < triCount; i++)
            {
                ref var t = ref triangles[i];
                t.Idx[0] = indices[i * 3];
                t.Idx[1] = indices[i * 3 + 1];
                t.Idx[2] = indices[i * 3 + 2];
                t.MaterialIndex = 0;
                t.UserData = 0;
            }

            var joltVertices = System.Runtime.InteropServices.MemoryMarshal.Cast<Vector3, float3>(vertices);
            using var settings = MeshShapeSettings.Create(joltVertices, triangles.AsSpan(0, triCount), ReadOnlySpan<PhysicsMaterial>.Empty);
            return settings.CreateShape();
        }
        finally
        {
            ArrayPool<IndexedTriangle>.Shared.Return(triangles);
        }
    }

    static Shape CreateTriangleCompound(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<uint> indices)
    {
        using var compound = StaticCompoundShapeSettings.Create();

        for (int i = 0; i < indices.Length; i += 3)
        {
            using var triangle = TriangleShapeSettings.Create(ToJolt(vertices[(int)indices[i]]), ToJolt(vertices[(int)indices[i + 1]]), ToJolt(vertices[(int)indices[i + 2]]));
            compound.AddShape(default, floatQuat.Identity, triangle);
        }

        return compound.CreateShape();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float3 ToJolt(Vector3 v) => Unsafe.BitCast<Vector3, float3>(v);

    /// <summary>Drops this wrapper's Jolt reference. Bodies already using the shape keep it alive.</summary>
    public void Dispose()
    {
        if (_shape.IsNull())
            return;

        _shape.Dispose();
        _shape = default;
    }
}
