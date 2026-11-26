using Brutal.Numerics;
using BrutalGame.Physics;
using BrutalGame.Terrain;
using BrutalGame.Water.Ocean;

namespace BrutalGame.Sim;

/// <summary>
/// Water depth for the ocean's depth cap from the planet's generated surface, in the physics bubble's tangent frame
/// (the sea frame: x, z in its plane): sea level − H along the radial through (x, 0, z). Edits are ignored: shores and
/// seabeds are unminable (D30), so they could only make the water shallower (placed fill). The binned, edit-aware planet
/// depth and the GPU depth map from the same source are M2b (docs/pivot/README.md carry-overs).
/// </summary>
internal sealed class PlanetShoreDepth(PlanetSurface surface, PhysicsBubble bubble) : IShoreDepth
{
    public float Depth(float x, float z)
    {
        var p = bubble.ToPlanet(new double3(x, 0.0, z));
        var d = double3.Normalize(p);
        return surface.SeaHeight - surface.Height(d.X, d.Y, d.Z);
    }
}
