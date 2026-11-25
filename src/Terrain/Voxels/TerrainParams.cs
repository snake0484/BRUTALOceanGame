namespace BrutalGame.Terrain;

public record struct TerrainParams()
{
    public ulong Seed = 1;
    public float SeaLevel = 0f;
    public float SeabedDepth = -30f;
    public float IslandRadius = 660f;
    public float IslandHeight = 110f;

    /// <summary>
    /// A dry crater in the main island's summit: its radius at the rim's foot, its depth at the centre, and the slope its
    /// banks rise at until they meet the island. Its level stands under the lowest the island's ground can be at that
    /// radius (shape, coast warp and detail all at their worst), so the rim stands all round. A radius of 0 means none.
    /// </summary>
    public float LakeRadius = 170f;
    public float LakeDepth = 14f;
    public float LakeBankSlope = 0.3f;

    public float SeabedUndulation = 6f;
    public float SeabedFrequency = 1f / 320f;
    public float CoastWarp = 0.3f;
    public float CoastFrequency = 1f / 140f;
    public float CliffFrequency = 1f / 260f;
    public float DetailHeight = 9f;
    public float DetailFrequency = 1f / 70f;
    public int DetailOctaves = 4;
    public float BeachHeight = 2f;

    /// <summary>Seabed slope at the waterline (m/m), steepening offshore as 1 + distance / ShelfLength. 1:20 so the water deepens quickly off the beach (3 m at 50 m out, 9 m at 100 m) and the wind sea's set waves plunge (ξ ≈ 1); 1:35 left a wide shallow fringe (ξ ≈ 0.57) and 1:50 spilled (ξ ≈ 0.4).</summary>
    public float BeachSlope = 1f / 20f;
    public float ShelfLength = 200f;
    public float SteepSlope = 0.7f;
    public float OverhangAmplitude = 5f;
    public float OverhangBand = 12f;
    public float OverhangFrequency = 1f / 16f;
}
