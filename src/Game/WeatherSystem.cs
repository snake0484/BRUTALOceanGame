using System.Diagnostics;
using System.Numerics;
using Brutal.Numerics;
using BrutalGame.Engine.Rendering;
using BrutalGame.Water.Rendering;
using BrutalGame.Weather;

namespace BrutalGame;

/// <summary>
/// The client's weather: the sim's <see cref="SphereWeatherField"/> baked into a <see cref="SphereWeatherCache"/> round the
/// camera on ticks, sampled once per frame for the sky. The cache's values do not depend on where it is anchored, so
/// what the client shows agrees with the sim's sea wind (SimWorld.SeaWinds reads the same field) and with every peer.
/// Rain, audio and the cloud map (D80) read the same cache.
/// </summary>
public sealed class WeatherSystem
{
    private static readonly Vector3 OvercastZenith = new(0.34f, 0.37f, 0.41f);
    private static readonly Vector3 OvercastHorizon = new(0.56f, 0.59f, 0.62f);
    private static readonly Vector3 StormZenith = new(0.15f, 0.17f, 0.20f);
    private static readonly Vector3 StormHorizon = new(0.30f, 0.32f, 0.35f);

    public readonly SphereWeatherCache Cache;

    private readonly double3[] _anchor = new double3[1];

    public WeatherSystem(SphereWeatherField field) => Cache = new SphereWeatherCache(field);

    public SphereWeatherField Field => Cache.Field;

    /// <summary>Weather at the camera as of the last <see cref="Update"/>.</summary>
    public WeatherSample Local { get; private set; }

    /// <summary>Milliseconds of the last <see cref="Tick"/>.</summary>
    public float LastTickMs { get; private set; }

    /// <summary>Fixed tick: <paramref name="weatherTick"/> is the sim's weather tick (sim tick plus any debug offset).</summary>
    public void Tick(long weatherTick, in double3 camera)
    {
        var start = Stopwatch.GetTimestamp();
        _anchor[0] = camera;
        Cache.Tick(weatherTick, _anchor);
        LastTickMs = (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    /// <summary>Main thread, once per frame. <paramref name="clear"/> is the sky with no weather.</summary>
    public void Update(in double3 camera, long weatherTick, OceanRenderer ocean, in SceneLighting clear, bool driveSky)
    {
        Local = Cache.Sample(camera, weatherTick);
        ApplySky(ocean, driveSky ? Sky(clear, Local) : clear);
    }

    /// <summary>
    /// Overcast greys the sky and blocks most direct sun; rain and storm cores darken it further and thicken the fog
    /// (fog's 1/e distance about 0.8 km in a downpour against 8 km clear).
    /// </summary>
    private static SceneLighting Sky(in SceneLighting clear, in WeatherSample w)
    {
        var overcast = Smooth(0.25f, 0.95f, w.Cloud);
        var dark = MathF.Max(w.Storm, 0.6f * w.Rain);

        return new SceneLighting
        {
            SunDirection = clear.SunDirection,
            SunIntensity = clear.SunIntensity * (1f - 0.85f * overcast) * (1f - 0.5f * dark),
            SkyZenith = Vector3.Lerp(Vector3.Lerp(clear.SkyZenith, OvercastZenith, overcast), StormZenith, dark),
            SkyHorizon = Vector3.Lerp(Vector3.Lerp(clear.SkyHorizon, OvercastHorizon, overcast), StormHorizon, dark),
            FogDensity = clear.FogDensity * (1f + 1.5f * overcast + 8f * w.Rain),
        };
    }

    private static void ApplySky(OceanRenderer ocean, in SceneLighting sky)
    {
        ocean.SunIntensity = sky.SunIntensity;
        ocean.SkyZenith = sky.SkyZenith;
        ocean.SkyHorizon = sky.SkyHorizon;
        ocean.FogDensity = sky.FogDensity;
    }

    private static float Smooth(float from, float to, float x)
    {
        var t = Math.Clamp((x - from) / (to - from), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
