using System.Numerics;
using System.Runtime.InteropServices;

namespace BrutalGame.Terrain.Water;

[StructLayout(LayoutKind.Sequential)]
public struct WaterVertex
{
    public const int SizeInBytes = 40;

    public Vector3 Position;
    public Vector3 Normal;
    public float Thickness;

    /// <summary>The surface water's speed (m/s, world x and z): the pipe model's face fluxes over the top cell. Ripples and foam stream with it.</summary>
    public Vector2 Flow;

    /// <summary>0..1: how churned the water is here, a pour landing or fast shallow flow; drawn as foam lace.</summary>
    public float Foam;
}

/// <summary>
/// Reusable surface mesh for one block, sized for a quad per column. A waterline clipped through its columns can need
/// more (<see cref="WaterSurfaceMesher.FullDepth"/>); it then grows once and keeps the size.
/// </summary>
public sealed class WaterMesh
{
    public const int MaxVertices = (ChunkConstants.Size + 1) * (ChunkConstants.Size + 1);
    public const int MaxIndices = ChunkConstants.Size * ChunkConstants.Size * 6;

    public WaterVertex[] Vertices = new WaterVertex[MaxVertices];
    public uint[] Indices = new uint[MaxIndices];
    public int VertexCount;
    public int IndexCount;

    public ReadOnlySpan<WaterVertex> VertexSpan => Vertices.AsSpan(0, VertexCount);
    public ReadOnlySpan<uint> IndexSpan => Indices.AsSpan(0, IndexCount);
    public bool IsEmpty => IndexCount == 0;

    public void Clear()
    {
        VertexCount = 0;
        IndexCount = 0;
    }

    public void EnsureCapacity(int vertices, int indices)
    {
        if (vertices > Vertices.Length)
            Array.Resize(ref Vertices, Math.Max(vertices, Vertices.Length * 2));

        if (indices > Indices.Length)
            Array.Resize(ref Indices, Math.Max(indices, Indices.Length * 2));
    }
}
