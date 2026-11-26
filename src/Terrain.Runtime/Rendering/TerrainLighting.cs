using System.Numerics;

namespace BrutalGame.Terrain.Runtime.Rendering;

/// <summary>
/// Sun, sky and fog shared with the ocean so terrain lighting and fog match it, and where the planet is: everything in
/// the camera frame's axes, camera-relative.
/// </summary>
public struct TerrainLighting
{
    public Vector3 SunDirection;
    public float SunIntensity;
    public Vector3 SkyZenith;
    public Vector3 SkyHorizon;
    public float FogDensity;

    /// <summary>The planet's centre relative to the camera: up and height above sea come from it per fragment.</summary>
    public Vector3 PlanetCentre;

    /// <summary>Sea sphere radius (m); 0 for no sea (no water absorption).</summary>
    public float SeaRadius;

    /// <summary>Colour seabeds fade to with depth where no ocean surface is drawn over them.</summary>
    public Vector3 WaterColour;

    /// <summary>Distance from the camera (m) past which seabeds get <see cref="WaterColour"/>: the ocean surface's reach, 0 with no surface drawn.</summary>
    public float SeaFillStart;
}
