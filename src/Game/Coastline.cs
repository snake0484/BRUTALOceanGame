using BrutalGame.Terrain;

namespace BrutalGame;

/// <summary>
/// Where the main island's west coast meets the sea, so the start camera and the scripted scenes stand at the same place
/// relative to the coast whatever the island's size. They were placed when its waterline at z 0 was at x −210.
/// </summary>
public static class Coastline
{
    /// <summary>World x of the waterline on row z: walking east from far out west of the main island, the first ground above sea level.</summary>
    public static float WestX(in TerrainParams p, float z)
    {
        for (var x = -2.5f * p.IslandRadius; x < 0f; x += 0.5f)
            if (TerrainGenerator.SurfaceHeight(x, z, p) >= p.SeaLevel)
                return x;

        return 0f;
    }
}
