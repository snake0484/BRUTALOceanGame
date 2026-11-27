using BrutalGame.Water.Ocean;

namespace BrutalGame.Water.Coast;

/// <summary>Where the coast maps of each sea tile take their carrier from (<see cref="CoastRegionSet.Update(in Brutal.Numerics.double3, ICoastCarriers?, float)"/>).</summary>
public interface ICoastCarriers
{
    /// <summary>The carrier a tile's maps are baked for and the Hs of its swell; calm where the tile has no swell.</summary>
    CoastCarrier At(int tile, out float significantHeight);
}

/// <summary>One carrier on every tile (tests, tools, a pinned sea).</summary>
public sealed class UniformCarriers : ICoastCarriers
{
    public CoastCarrier Carrier;
    public float SignificantHeight;

    public CoastCarrier At(int tile, out float significantHeight)
    {
        significantHeight = SignificantHeight;
        return Carrier;
    }
}

/// <summary>Each tile's own swell's target carrier (<see cref="SeaSwells.For"/>): it moves in steps, when the tile takes up a new wind.</summary>
public sealed class SwellCarriers(SeaSwells swells) : ICoastCarriers
{
    public CoastCarrier At(int tile, out float significantHeight)
    {
        if (swells.For(tile) is not { } swell)
        {
            significantHeight = 0f;
            return default;
        }

        significantHeight = swell.TargetSignificantHeight;
        return CoastCarrier.From(swell.TargetCarrierDirection, swell.TargetCarrierOmega, swell.TargetCarrierHeight);
    }
}
