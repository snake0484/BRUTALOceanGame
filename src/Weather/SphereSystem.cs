using Brutal.Numerics;

namespace BrutalGame.Weather;

/// <summary>
/// One weather system on a sphere at one instant, as <see cref="PressureSystem"/> but placed by a unit direction in the
/// planet frame, with its circulation sense and equatorial fade folded into <see cref="Spin"/>.
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

    public float Pressure;
    public float PeakWind;
    public float Cloud;
    public float Rain;
    public float Convection;
    public float Calm;
}
