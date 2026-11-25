using System.Numerics;
using Brutal.Numerics;

namespace BrutalGame.Terrain;

/// <summary>Octree node key. LOD L has voxel size 2^L m and covers 32·2^L m per side (docs/terrain-lod.md).</summary>
public readonly record struct ChunkCoord(int X, int Y, int Z, int Lod = 0)
{
    private const float ChunkMetres = ChunkConstants.Size * ChunkConstants.VoxelSize;

    public float VoxelScale => ChunkConstants.VoxelSize * (1 << Lod);

    public float SizeMetres => ChunkMetres * (1 << Lod);

    public Vector3 WorldOrigin => new Vector3(X, Y, Z) * SizeMetres;

    public ChunkCoord Parent => new(X >> 1, Y >> 1, Z >> 1, Lod + 1);

    /// <summary>Voxel size in metres as a double: exact, 2^Lod.</summary>
    public double VoxelSize => 1 << Lod;

    /// <summary>Planet-frame origin: integer × size, exact in double.</summary>
    public double3 Origin => new double3(X, Y, Z) * (ChunkMetres * (double)(1 << Lod));

    /// <summary>Storage and network key: 3 × 18-bit signed coords and a 4-bit LOD (planets.md "Numbers").</summary>
    public ulong Key => Pack18(X) | Pack18(Y) << 18 | Pack18(Z) << 36 | (ulong)Lod << 54;

    public static ChunkCoord FromPlanet(double x, double y, double z, int lod)
    {
        var size = ChunkMetres * (double)(1 << lod);
        return new ChunkCoord((int)Math.Floor(x / size), (int)Math.Floor(y / size), (int)Math.Floor(z / size), lod);
    }

    private static ulong Pack18(int v) => (ulong)(uint)v & 0x3FFFF;

    public static ChunkCoord FromWorld(Vector3 p, int lod = 0)
    {
        var size = ChunkMetres * (1 << lod);
        return new ChunkCoord((int)MathF.Floor(p.X / size), (int)MathF.Floor(p.Y / size), (int)MathF.Floor(p.Z / size), lod);
    }

    // The record's generated ToString prints every property, and Parent is a ChunkCoord too: infinite recursion.
    public override string ToString() => $"({X}, {Y}, {Z}) L{Lod}";
}
