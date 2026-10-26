using System.Numerics;
using BrutalGame.Engine.Rendering;
using BrutalGame.Water.Ocean;
using BrutalGame.Water.Rendering;
using BrutalGame.Weather;

namespace BrutalGame;

/// <summary>
/// Feeds the ocean wind and the sky from the weather where the camera is. The map bakes on ticks; this runs once per
/// frame. A new swell wind is queued only when the sea wind has moved past a threshold, and the swell blends to it
/// over its own <see cref="OceanSwell.BlendSeconds"/>, so a sea builds as a storm arrives rather than snapping.
/// </summary>
public sealed class WeatherSystem
{
    private const float SpeedThreshold = 1f;
    private const float DirectionThreshold = 10f * MathF.PI / 180f;
    private const float CalmSpeed = 0.5f;

    /// <summary>
    /// The waves at a point were raised over the water around it, so the swell takes the mean wind speed over a 3 × 3
    /// grid at this spacing (4 km across) along the mean wind's direction. That also hides a storm's calm eye.
    /// </summary>
    private const float SeaWindSpacing = 2000f;

    private static readonly Vector3 OvercastZenith = new(0.34f, 0.37f, 0.41f);
    private static readonly Vector3 OvercastHorizon = new(0.56f, 0.59f, 0.62f);
    private static readonly Vector3 StormZenith = new(0.15f, 0.17f, 0.20f);
    private static readonly Vector3 StormHorizon = new(0.30f, 0.32f, 0.35f);

    private Vector2 _sentWind = new(float.NaN);

    public readonly WeatherMap Map;

    /// <param name="time">Weather time (s) at launch.</param>
    public WeatherSystem(in WeatherClimate climate, double time, Vector2 camera)
    {
        Map = new WeatherMap(new WeatherField(climate));
        Map.Tick(time, camera);
        Local = Map.Sample(camera.X, camera.Y);
        SeaWind = AverageWind(camera);
    }

    /// <summary>Weather at the camera as of the last <see cref="Update"/>.</summary>
    public WeatherSample Local { get; private set; }

    /// <summary>Wind the swell is driven by (m/s, blowing toward), as of the last <see cref="Update"/> that drove it.</summary>
    public Vector2 SeaWind { get; private set; }

    /// <summary>Fixed tick: <paramref name="time"/> is weather time (sim time plus any debug offset).</summary>
    public void Tick(double time, Vector2 camera) => Map.Tick(time, camera);

    /// <summary>Main thread, once per frame. <paramref name="clear"/> is the sky with no weather.</summary>
    public void Update(Vector2 camera, OceanSwell swell, OceanRenderer ocean, in SceneLighting clear, bool driveOcean, bool driveSky)
    {
        Local = Map.Sample(camera.X, camera.Y);

        if (driveOcean)
        {
            SeaWind = AverageWind(camera);

            if (Moved(SeaWind))
                swell.SetWind(WithWind(swell.Wind));
        }
        else
            _sentWind = new Vector2(float.NaN);

        ApplySky(ocean, driveSky ? Sky(clear, Local) : clear);
    }

    /// <summary><paramref name="p"/> with the sea wind's speed and direction; remembered as the wind last sent.</summary>
    public SwellParams WithWind(SwellParams p)
    {
        var wind = SeaWind;
        p.WindSpeed = wind.Length();

        if (p.WindSpeed > CalmSpeed)
            p.WindDirection = MathF.Atan2(wind.Y, wind.X);

        _sentWind = wind;
        return p;
    }

    private Vector2 AverageWind(Vector2 camera)
    {
        var sum = Vector2.Zero;
        var speed = 0f;

        for (var j = -1; j <= 1; j++)
        {
            for (var i = -1; i <= 1; i++)
            {
                var wind = Map.Sample(camera.X + i * SeaWindSpacing, camera.Y + j * SeaWindSpacing).Wind;
                sum += wind;
                speed += wind.Length();
            }
        }

        var length = sum.Length();
        return length > 1e-3f ? sum * (speed / 9f / length) : Vector2.Zero;
    }

    private bool Moved(Vector2 wind)
    {
        if (float.IsNaN(_sentWind.X))
            return true;

        var speed = wind.Length();
        var sent = _sentWind.Length();

        if (MathF.Abs(speed - sent) > SpeedThreshold)
            return true;

        if (speed < SpeedThreshold || sent < SpeedThreshold)
            return false;

        var turn = MathF.IEEERemainder(MathF.Atan2(wind.Y, wind.X) - MathF.Atan2(_sentWind.Y, _sentWind.X), MathF.Tau);
        return MathF.Abs(turn) > DirectionThreshold;
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
