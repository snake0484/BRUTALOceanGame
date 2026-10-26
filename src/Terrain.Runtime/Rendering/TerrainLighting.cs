using System.Numerics;

namespace BrutalGame.Terrain.Runtime.Rendering;

/// <summary>
/// Sun, sky and fog shared with the ocean so terrain lighting and fog match it.
/// </summary>
public struct TerrainLighting
{
    public Vector3 SunDirection;
    public float SunIntensity;
    public Vector3 SkyZenith;
    public Vector3 SkyHorizon;
    public float FogDensity;
}
