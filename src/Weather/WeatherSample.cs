using System.Numerics;

namespace BrutalGame.Weather;

/// <summary>
/// Weather at one point. Fields are linear so samples blend by plain lerp (wind as a vector).
/// </summary>
public struct WeatherSample
{
    /// <summary>10 m wind (m/s) in XZ, the way it blows toward.</summary>
    public Vector2 Wind;

    /// <summary>Cloud cover, 0..1.</summary>
    public float Cloud;

    /// <summary>Rain intensity, 0..1 (1 is a downpour).</summary>
    public float Rain;

    /// <summary>Convective intensity, 0..1: storm cores, where lightning and squalls belong.</summary>
    public float Storm;

    /// <summary>Surface pressure anomaly (hPa) against the background; negative in lows.</summary>
    public float Pressure;

    /// <summary>Gust factor: peak gust / mean wind − 1.</summary>
    public float Gust;

    public readonly float WindSpeed => Wind.Length();

    /// <summary>Radians the wind blows toward, 0 = +X, π/2 = +Z.</summary>
    public readonly float WindDirection => MathF.Atan2(Wind.Y, Wind.X);

    public static WeatherSample Lerp(in WeatherSample a, in WeatherSample b, float t) => new()
    {
        Wind = Vector2.Lerp(a.Wind, b.Wind, t),
        Cloud = float.Lerp(a.Cloud, b.Cloud, t),
        Rain = float.Lerp(a.Rain, b.Rain, t),
        Storm = float.Lerp(a.Storm, b.Storm, t),
        Pressure = float.Lerp(a.Pressure, b.Pressure, t),
        Gust = float.Lerp(a.Gust, b.Gust, t),
    };
}
