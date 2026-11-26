using Brutal.Numerics;
using BrutalGame.Terrain;
using BrutalGame.Water.Ocean;

namespace BrutalGame.Sim;

/// <summary>The ocean's depth source on a planet: <see cref="PlanetShore"/> (generated ground plus binned edits), with no camera or bubble in it (review C7).</summary>
internal sealed class PlanetShoreDepth(PlanetShore shore) : IShoreDepth
{
    public float Depth(in double3 direction) => shore.Depth(direction.X, direction.Y, direction.Z);
}
