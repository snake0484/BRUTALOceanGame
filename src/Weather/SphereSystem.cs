using Brutal.Numerics;

namespace BrutalGame.Weather;

/// <summary>
/// One weather system on a sphere at one instant: where it is (a unit direction in the planet frame), how far it reaches,
/// and its peaks at full strength (<see cref="Strength"/> scales them where they apply), with its circulation sense and equatorial fade folded into <see cref="Spin"/>.
/// </summary>
public struct SphereSystem
{
    /// <summary>Unit direction of the centre, planet frame.</summary>
    public double3 Centre;

    /// <summary>Planet radius / system radius, so q = great-circle angle × this.</summary>
    public double InvAngularRadius;

    /// <summary>Great-circle radius (m).</summary>
    public float Radius;

    public PressureKind Kind;

    /// <summary>Life envelope, 0..1.</summary>
    public float Strength;

    /// <summary>Circulation about local up, −1..1: positive is counter-clockwise from above. Fades to 0 at the equator.</summary>
    public float Spin;

    /// <summary>Pressure anomaly at the centre (hPa).</summary>
    public float Pressure;

    /// <summary>Strongest wind of the system's own circulation (m/s), on a ring at 0.45 × radius.</summary>
    public float PeakWind;

    /// <summary>Cloud it brings (lows) or clears (highs) at the centre, 0..1.</summary>
    public float Cloud;
    public float Rain;
    public float Convection;

    /// <summary>Share of the prevailing wind a high stills at its centre.</summary>
    public float Calm;
}
