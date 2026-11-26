using Brutal.Numerics;
using BrutalGame.Engine.Scene;
using BrutalGame.Frames;

namespace BrutalGame.Menu;

/// <summary>
/// The main menu's view: the planet from orbit, right of centre with the star to the upper left, the camera circling
/// the star's axis at a constant phase angle so the lit side and the terminator stay in view while the surface slides by.
/// </summary>
public static class MenuCamera
{
    public const float FovY = 60f * MathF.PI / 180f;

    private const double DistanceRadii = 3.0;
    private const double Phase = 55.0 * Math.PI / 180.0;
    private const double LapSeconds = 1800.0;
    private const double PlanetYaw = 21.0 * Math.PI / 180.0;
    private const double SunTilt = 35.0 * Math.PI / 180.0;

    /// <param name="sun">Toward the star in the camera's frame (the planet's surface frame, its centre at the origin).</param>
    public static void Apply(FlyCamera camera, double planetRadius, double3 sun, double seconds)
    {
        sun = double3.Normalize(sun);
        var u = double3.Normalize(double3.Cross(sun, Math.Abs(sun.Y) < 0.9 ? double3.UnitY : double3.UnitX));
        var v = double3.Cross(sun, u);
        var lap = seconds * (2.0 * Math.PI / LapSeconds);
        var outward = sun * Math.Cos(Phase) + (u * Math.Cos(lap) + v * Math.Sin(lap)) * Math.Sin(Phase);

        var sunOnScreen = double3.Normalize(sun - outward * double3.Dot(sun, outward));
        var across = double3.Cross(outward, sunOnScreen);
        var up = sunOnScreen * Math.Sin(SunTilt) - across * Math.Cos(SunTilt);
        var right = double3.Cross(-outward, up);
        var forward = -outward * Math.Cos(PlanetYaw) - right * Math.Sin(PlanetYaw);

        camera.FovY = FovY;
        camera.Position = outward * (planetRadius * DistanceRadii);
        camera.Rotation = CameraProjection.LookRotation(forward, up, right);
    }
}
