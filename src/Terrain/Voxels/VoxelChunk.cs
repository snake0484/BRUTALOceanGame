namespace BrutalGame.Terrain;

public sealed class VoxelChunk
{
    public ChunkCoord Coord;
    public readonly sbyte[] Density = new sbyte[ChunkConstants.PaddedVolume];
    public readonly byte[] Material = new byte[ChunkConstants.PaddedVolume];
    public bool HasSurface;
    public int Version;

    public void Fill(sbyte density, VoxelMaterial material)
    {
        Density.AsSpan().Fill(density);
        Material.AsSpan().Fill((byte)material);
        HasSurface = false;
        Version++;
    }

    public void RecomputeHasSurface()
    {
        var solid = false;
        var air = false;

        foreach (var d in Density)
        {
            if (d < 0)
                solid = true;
            else
                air = true;

            if (solid && air)
                break;
        }

        HasSurface = solid && air;
    }
}
