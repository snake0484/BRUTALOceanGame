using System.Numerics;

namespace BrutalGame.Engine.Rendering;

/// <summary>
/// Sun, sky and fog shared by scene renderers so lighting and fog match the ocean.
/// </summary>
public struct SceneLighting
{
    public Vector3 SunDirection;
    public float SunIntensity;
    public Vector3 SkyZenith;
    public Vector3 SkyHorizon;
    public float FogDensity;
}
