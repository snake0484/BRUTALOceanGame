using Brutal.Numerics;

namespace BrutalGame.Water.Coast;

/// <summary>
/// Radial water depth below the sea sphere (m, negative on land) along a unit direction of the planet's frame, from the
/// generated ground alone: shores are fixed, so edits are ignored. A pure function of the direction; thread-safe,
/// allocation-free.
/// </summary>
public interface ICoastDepth
{
    float GeneratedDepth(in double3 direction);
}
