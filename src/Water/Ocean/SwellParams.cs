namespace BrutalGame.Water.Ocean;

/// <summary>
/// Inputs for the CPU swell spectrum. Direction is where waves travel toward, radians, 0 = +X, π/2 = +Z.
/// The local wind makes a wind sea (JONSWAP); a groundswell from a distant storm can be added on top, independent of
/// the local wind (docs/surf-research.md): a narrow band of long-period waves with its own height, period and direction.
/// </summary>
public struct SwellParams()
{
    public float WindSpeed = 10f;
    public float WindDirection = 0f;
    public float Fetch = 100_000f;
    public float Depth = float.PositiveInfinity;
    public float Gamma = 3.3f;
    public float Spread = 8f;
    public float MinWavelength = 8f;
    public float Choppiness = 0.8f;
    public int FrequencyBands = 32;
    public int DirectionsPerBand = 4;

    /// <summary>Groundswell significant wave height (m); 0 = none.</summary>
    public float SwellHeight = 0f;

    /// <summary>Groundswell peak period (s): about 10–20 s from a distant storm.</summary>
    public float SwellPeriod = 13f;

    /// <summary>Direction the groundswell travels toward (radians), independent of the wind.</summary>
    public float SwellDirection = 0f;

    /// <summary>Groundswell cos-2s spreading exponent: far narrower than a wind sea's, the swell having sorted itself on the way.</summary>
    public float SwellSpread = 40f;

    /// <summary>
    /// Groundswell frequency bandwidth, σ_ω / ω_p of a Gaussian: narrower swell beats into longer groups (sets every
    /// about 1 / (2 × this) waves).
    /// </summary>
    public float SwellBandwidth = 0.05f;

    public int SwellBands = 8;
    public int SwellDirections = 3;
}
