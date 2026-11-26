using System.Numerics;
using BrutalGame.Water.Ocean;
using BrutalGame.Weather;

namespace BrutalGame.Sim;

/// <summary>
/// The swell's wind from the weather, camera-independent so a headless host reproduces it. The client's WeatherMap is
/// baked around the camera, so this samples the analytic <see cref="WeatherField"/> directly, about a fixed reference
/// point (the sea frame's origin at the spawn). A new wind is queued only when the sea wind has moved past a threshold,
/// and the swell blends to it over its own <see cref="OceanSwell.BlendSeconds"/>, so a sea builds as a storm arrives.
/// </summary>
internal sealed class SwellWind(WeatherField field)
{
    private const float SpeedThreshold = 1f;
    private const float DirectionThreshold = 10f * MathF.PI / 180f;
    private const float CalmSpeed = 0.5f;

    /// <summary>
    /// The waves at a point were raised over the water around it, so the swell takes the mean wind speed over a 3 × 3
    /// grid at this spacing (4 km across) along the mean wind's direction. That also hides a storm's calm eye.
    /// </summary>
    private const float Spacing = 2000f;

    private Vector2 _sent = new(float.NaN);

    /// <summary>Sea wind (m/s, blowing toward) as of the last <see cref="Update"/>.</summary>
    public Vector2 Wind { get; private set; }

    /// <summary>Samples the sea wind at weather time <paramref name="time"/> and queues it on the swell if it moved.</summary>
    public void Update(OceanSwell swell, double time)
    {
        Wind = Average(time);

        if (Moved(Wind))
            swell.SetWind(With(swell.Wind));
    }

    /// <summary>Forget the last wind sent, so the next <see cref="Update"/> sends.</summary>
    public void Reset() => _sent = new Vector2(float.NaN);

    /// <summary><paramref name="p"/> with the sea wind's speed and direction; remembered as the wind last sent.</summary>
    public SwellParams With(SwellParams p)
    {
        p.WindSpeed = Wind.Length();

        if (p.WindSpeed > CalmSpeed)
            p.WindDirection = MathF.Atan2(Wind.Y, Wind.X);

        _sent = Wind;
        return p;
    }

    public void Sample(double time) => Wind = Average(time);

    private Vector2 Average(double time)
    {
        var sum = Vector2.Zero;
        var speed = 0f;

        for (var j = -1; j <= 1; j++)
        {
            for (var i = -1; i <= 1; i++)
            {
                var wind = field.Sample(i * Spacing, j * Spacing, time).Wind;
                sum += wind;
                speed += wind.Length();
            }
        }

        var length = sum.Length();
        return length > 1e-3f ? sum * (speed / 9f / length) : Vector2.Zero;
    }

    private bool Moved(Vector2 wind)
    {
        if (float.IsNaN(_sent.X))
            return true;

        var speed = wind.Length();
        var sent = _sent.Length();

        if (MathF.Abs(speed - sent) > SpeedThreshold)
            return true;

        if (speed < SpeedThreshold || sent < SpeedThreshold)
            return false;

        var turn = MathF.IEEERemainder(MathF.Atan2(wind.Y, wind.X) - MathF.Atan2(_sent.Y, _sent.X), MathF.Tau);
        return MathF.Abs(turn) > DirectionThreshold;
    }
}
