namespace BrutalGame.Terrain;

public struct TerrainParams()
{
    public ulong Seed = 1;
    public float SeaLevel = 0f;
    public float SeabedDepth = -30f;
    public float IslandRadius = 220f;
    public float IslandHeight = 55f;

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
