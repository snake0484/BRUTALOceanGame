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

    /// <summary>0 on the ground (sun, sky and fog above); 1 from orbit, where the terrain is shaded as the body's impostor is (<see cref="DetailLadder.SpaceLook"/>).</summary>
    public float SpaceLook;

    /// <summary>The star's light in the space look.</summary>
    public float StarIntensity;

    /// <summary>The body's atmosphere for the space look: top and base radius (m from its centre) and scale height; top 0 for none.</summary>
    public float AtmosphereTop;
    public float AtmosphereBase;
    public float AtmosphereScaleHeight;
}
