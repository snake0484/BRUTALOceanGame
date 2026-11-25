using System.Numerics;
using Brutal.Numerics;

namespace BrutalGame.Frames;

/// <summary>One frame of the --orbit-test precision probe: the marker through the GPU's float path against the exact double projection.</summary>
public struct OrbitSample
{
    private const float MinNormal = 1.17549435e-38f;

    public Vector2 Pixel;
    public double2 Exact;

    /// <summary>Reverse-Z depth of the marker, float.</summary>
    public float Depth;

    /// <summary>Float depth still orders a point 1e-5 of the range behind the marker in front of it.</summary>
    public bool DepthResolves;

    /// <summary>|float − exact| / exact for the impostor's ray distance to the surface at the marker.</summary>
    public float SurfaceError;

    public readonly Vector2 Error => new((float)(Pixel.X - Exact.X), (float)(Pixel.Y - Exact.Y));

    public readonly bool Finite =>
        float.IsFinite(Pixel.X) && float.IsFinite(Pixel.Y) && double.IsFinite(Exact.X) && double.IsFinite(Exact.Y) && float.IsFinite(Depth) && float.IsFinite(SurfaceError);

    /// <summary>Depth is a normal float in (0, 1] and resolves 1e-5 of the range.</summary>
    public readonly bool DepthOk => Depth >= MinNormal &&Depth <= 1f && DepthResolves;
}

/// <summary>
/// The --orbit-test flight over a sphere centred on its frame's origin: from <see cref="StartAltitude"/>, 3 m beside a
/// surface marker, straight out to <see cref="EndAltitude"/> with altitude growing exponentially, while the view circles
/// the marker so it moves on screen. Closed form in double, so a camera flown by per-frame deltas can be checked against it.
/// </summary>
public readonly struct OrbitPath
{
    public const double StartAltitude = 2.0;
    public const double EndAltitude = 1e8;

    private const double GroundOffset = 3.0;
    private const double LookRadius = 0.15;
    private const double LookTurns = 3.0;
    private const float DepthStep = 1e-5f;

    public readonly double Radius;
    private readonly double3 _up;
    private readonly double3 _east;
    private readonly double3 _north;

    public OrbitPath(double radius, double3 markerUp)
    {
        Radius = radius;
        _up = double3.Normalize(markerUp);
        var axis = Math.Abs(_up.Y) < 0.9 ? double3.UnitY : double3.UnitX;
        _east = double3.Normalize(double3.Cross(axis, _up));
        _north = double3.Cross(_up, _east);
    }

    public double3 Marker => _up * Radius;

    /// <summary>Altitude at path fraction <paramref name="s"/> in [0, 1].</summary>
    public static double Altitude(double s) => StartAltitude * Math.Pow(EndAltitude / StartAltitude, Math.Clamp(s, 0.0, 1.0));

    public double3 Camera(double s)
    {
        var (sin, cos) = Math.SinCos(GroundOffset / Radius);
        return (_up * cos + _east * sin) * (Radius + Altitude(s));
    }

    /// <summary>Where the camera at <paramref name="camera"/> looks: a circle about the marker, 0.15 of the range across.</summary>
    public double3 LookAt(double s, double3 camera)
    {
        var range = (Marker - camera).Length();
        var (sin, cos) = Math.SinCos(2.0 * Math.PI * LookTurns * Math.Clamp(s, 0.0, 1.0));
        return Marker + (_east * cos + _north * sin) * (LookRadius * range);
    }

    /// <summary>
    /// Probes the marker for a camera at <paramref name="camera"/> (frame coordinates) with <paramref name="rotation"/> and
    /// the float <paramref name="viewProj"/> the draws use: the marker's record origin cast to float as InstanceBuffer
    /// does, then projected in float; the sphere's ray distance as planet.frag computes it.
    /// </summary>
    public OrbitSample Measure(double3 camera, doubleQuat rotation, in Matrix4x4 viewProj, float fovY, float aspect, Vector2 viewport)
    {
        var relative = Marker - camera;
        var relativeF = ToFloat(relative);
        var toCentre = -camera;
        var altitude = toCentre.Length() - Radius;
        var sample = new OrbitSample
        {
            Pixel = CameraProjection.ProjectFloat(relativeF, viewProj, viewport, out var depth),
            Exact = CameraProjection.ProjectExact(relative, rotation, fovY, aspect, new double2(viewport.X, viewport.Y)),
            Depth = depth,
        };

        CameraProjection.ProjectFloat(relativeF * (1f + DepthStep), viewProj, viewport, out var behind);
        sample.DepthResolves = behind < depth;

        var range = relative.Length();
        var t = CameraProjection.SphereDistance(ToFloat(toCentre), (float)Radius, (float)altitude, (float)(altitude * (2.0 * Radius + altitude)), Vector3.Normalize(relativeF));
        sample.SurfaceError = (float)(Math.Abs(t - range) / range);
        return sample;
    }

    private static Vector3 ToFloat(double3 v) => new((float)v.X, (float)v.Y, (float)v.Z);
}
