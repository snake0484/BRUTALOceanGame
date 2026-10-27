using System.Numerics;
using System.Runtime.InteropServices;

namespace BrutalGame.Terrain.Water;

[StructLayout(LayoutKind.Sequential)]
public struct WaterVertex
{
    public const int SizeInBytes = 36;

    public Vector3 Position;
    public Vector3 Normal;
    public float Thickness;

    /// <summary>
    /// How far the wind reaches this water, 0..255 per direction it blows toward (<see cref=WaterSurfaceMesher.Directions/>,
    /// 45° apart from +X), as two R8G8B8A8_UNORM attributes: 0 under a roof or in the lee of a wall, 255 open with fetch.
    /// </summary>
    public uint Shelter0;
    public uint Shelter1;

    public readonly byte Shelter(int direction) => (byte)((direction < 4 ? Shelter0 : Shelter1) >> (8 * (direction & 3)));
}

/// <summary>Reusable surface mesh for one block, sized for the worst case so it never grows.</summary>
public sealed class WaterMesh
{
    public const int MaxVertices = (ChunkConstants.Size + 1) * (ChunkConstants.Size + 1);
    public const int MaxIndices = ChunkConstants.Size * ChunkConstants.Size * 6;

    public readonly WaterVertex[] Vertices = new WaterVertex[MaxVertices];
    public readonly uint[] Indices = new uint[MaxIndices];
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
}
