using Brutal.Numerics;

namespace BrutalGame.Water.Ocean;

/// <summary>
/// Radial water depth below the sea sphere (m, negative on land) along a unit direction of the planet's frame, from the
/// world's analytic source (generated ground plus edits; review C7). A pure function of the direction between edits, the
/// same on every peer; thread-safe, allocation-free.
/// </summary>
public interface IShoreDepth
{
    float Depth(in double3 direction);
}
