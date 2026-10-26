using System.Numerics;

namespace BrutalGame.Terrain;

/// <summary>Octree node key. LOD L has voxel size 2^L m and covers 32·2^L m per side (docs/terrain-lod.md).</summary>
public readonly record struct ChunkCoord(int X, int Y, int Z, int Lod = 0)
{
    private const float ChunkMetres = ChunkConstants.Size * ChunkConstants.VoxelSize;

    public float VoxelScale => ChunkConstants.VoxelSize * (1 << Lod);

    public float SizeMetres => ChunkMetres * (1 << Lod);

    public Vector3 WorldOrigin => new Vector3(X, Y, Z) * SizeMetres;

    public ChunkCoord Parent => new(X >> 1, Y >> 1, Z >> 1, Lod + 1);

    public static ChunkCoord FromWorld(Vector3 p, int lod = 0)
    {
        var size = ChunkMetres * (1 << lod);
        return new ChunkCoord((int)MathF.Floor(p.X / size), (int)MathF.Floor(p.Y / size), (int)MathF.Floor(p.Z / size), lod);
    }

    // The record's generated ToString prints every property, and Parent is a ChunkCoord too: infinite recursion.
    public override string ToString() => $"({X}, {Y}, {Z}) L{Lod}";
}
