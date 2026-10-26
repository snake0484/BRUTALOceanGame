namespace BrutalGame.Weather;

/// <summary>
/// World-wide inputs to the weather: the prevailing wind, which also steers every pressure system, how stormy the
/// climate is, and the fair-weather cloud between systems. Directions are where the wind blows toward, radians,
/// 0 = +X, π/2 = +Z (as SwellParams).
/// </summary>
public struct WeatherClimate()
{
    public ulong Seed = 1;
    public float PrevailingSpeed = 6f;
    public float PrevailingDirection = 0f;

    /// <summary>0..1: the share of systems that are storms rather than highs, and how hard storms blow.</summary>
    public float Storminess = 0.5f;

    /// <summary>Fair-weather cloud cover between systems, 0..1.</summary>
    public float BaseCloud = 0.15f;
}
