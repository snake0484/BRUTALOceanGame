using System.Numerics;
using BrutalGame.Engine.Rendering;
using BrutalGame.Water.Rendering;
using BrutalGame.Weather;

namespace BrutalGame;

/// <summary>
/// Feeds the sky from the weather where the camera is. The map bakes on ticks; this runs once per frame. The swell's
/// wind is the sim's (SimWorld), sampled from the same field away from the camera.
/// </summary>
public sealed class WeatherSystem
{
    private static readonly Vector3 OvercastZenith = new(0.34f, 0.37f, 0.41f);
    private static readonly Vector3 OvercastHorizon = new(0.56f, 0.59f, 0.62f);
    private static readonly Vector3 StormZenith = new(0.15f, 0.17f, 0.20f);
    private static readonly Vector3 StormHorizon = new(0.30f, 0.32f, 0.35f);

    public readonly WeatherMap Map;

    /// <param name="time">Weather time (s) at launch.</param>
    public WeatherSystem(WeatherField field, double time, Vector2 camera)
    {
        Map = new WeatherMap(field);
        Map.Tick(time, camera);
        Local = Map.Sample(camera.X, camera.Y);
    }

    /// <summary>Weather at the camera as of the last <see cref="Update"/>.</summary>
    public WeatherSample Local { get; private set; }

    /// <summary>Fixed tick: <paramref name="time"/> is weather time (sim time plus any debug offset).</summary>
    public void Tick(double time, Vector2 camera) => Map.Tick(time, camera);

    /// <summary>Main thread, once per frame. <paramref name="clear"/> is the sky with no weather.</summary>
    public void Update(Vector2 camera, OceanRenderer ocean, in SceneLighting clear, bool driveSky)
    {
        Local = Map.Sample(camera.X, camera.Y);
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
