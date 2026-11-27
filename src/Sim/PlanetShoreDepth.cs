using Brutal.Numerics;
using BrutalGame.Terrain;
using BrutalGame.Water.Coast;
using BrutalGame.Water.Ocean;

namespace BrutalGame.Sim;

/// <summary>
/// The ocean's depth sources on a planet, with no camera or bubble in them (review C7): <see cref="PlanetShore"/>
/// (generated ground plus binned edits) for the surface, and the generated ground alone for the coast maps.
/// </summary>
internal sealed class PlanetShoreDepth(PlanetShore shore) : IShoreDepth, ICoastDepth
{
    public float Depth(in double3 direction) => shore.Depth(direction.X, direction.Y, direction.Z);

    public float GeneratedDepth(in double3 direction) => shore.GeneratedDepth(direction.X, direction.Y, direction.Z, 0);
}
