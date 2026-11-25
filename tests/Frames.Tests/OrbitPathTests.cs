using System.Numerics;
using Brutal.Numerics;

namespace BrutalGame.Frames.Tests;

/// <summary>The math of --orbit-test: the float draw path against the exact projection along the 2 m to 1e8 m flight.</summary>
public class OrbitPathTests
{
    private const double Radius = 1.0e6;
    private const double FrameSeconds = 1.0 / 144.0;
    private const double Hold = 1.0;
    private const double Seconds = 30.0;
    private const float FovY = 60f * MathF.PI / 180f;
    private const float Near = 0.1f;
    private static readonly Vector2 Viewport = new(1600f, 900f);
    private static readonly OrbitPath Path = new(Radius, new double3(0.3, 0.5, -0.8));

    private static float Aspect => Viewport.X / Viewport.Y;

    private static double PathFraction(int frame) => Math.Clamp((frame * FrameSeconds - Hold) / Seconds, 0.0, 1.0);

    private static int Frames => (int)((Hold + Seconds) / FrameSeconds) + 1;

    private static (doubleQuat Rotation, Matrix4x4 ViewProj) Look(double s, double3 camera)
    {
        var rotation = CameraProjection.LookRotation(Path.LookAt(s, camera) - camera, double3.Normalize(camera), double3.UnitX);
        return (rotation, CameraProjection.View(rotation) * CameraProjection.Projection(FovY, Aspect, Near));
    }

    private static Vector3 ToFloat(double3 v) => new((float)v.X, (float)v.Y, (float)v.Z);

    [Fact]
    public void Flight_MeetsExitCheck_AtEveryAltitude()
    {
        var camera = Path.Camera(0.0);
        var last = 0.0;
        var lastError = Vector2.Zero;
        var maxJitter = 0f;

        for (var frame = 0; frame < Frames; frame++)
        {
            var s = PathFraction(frame);
            camera += Path.Camera(s) - Path.Camera(last);
            last = s;

            var (rotation, viewProj) = Look(s, camera);
            var sample = Path.Measure(camera, rotation, viewProj, FovY, Aspect, Viewport);
            var altitude = OrbitPath.Altitude(s);
            var jitter = frame > 0 ? Vector2.Distance(sample.Error, lastError) : 0f;
            lastError = sample.Error;
            maxJitter = MathF.Max(maxJitter, jitter);

            Assert.True(sample.Finite, $"NaN/Inf at {altitude:E2} m");
            Assert.True(sample.DepthOk, $"depth {sample.Depth:E3} at {altitude:E2} m");
            Assert.True(jitter < 0.1f, $"jitter {jitter} px at {altitude:E2} m");
            Assert.True(sample.SurfaceError < 1e-4f, $"surface error {sample.SurfaceError:E2} at {altitude:E2} m");
            Assert.True((camera - Path.Camera(s)).Length() < 1e-3, $"camera error {(camera - Path.Camera(s)).Length():E2} m at {altitude:E2} m");
            Assert.InRange(sample.Pixel.X, 0f, Viewport.X);
            Assert.InRange(sample.Pixel.Y, 0f, Viewport.Y);
        }

        Assert.Equal(OrbitPath.EndAltitude, OrbitPath.Altitude(last), 1e-6 * OrbitPath.EndAltitude);
        Assert.True(maxJitter > 0f);
    }

    [Fact]
    public void Altitude_IsExponentialFromStartToEnd()
    {
        Assert.Equal(OrbitPath.StartAltitude, OrbitPath.Altitude(0.0), 12);
        Assert.Equal(OrbitPath.EndAltitude, OrbitPath.Altitude(1.0), 1e-6);
        Assert.Equal(Math.Sqrt(OrbitPath.StartAltitude * OrbitPath.EndAltitude), OrbitPath.Altitude(0.5), 1e-6);
        Assert.Equal(OrbitPath.StartAltitude, (Path.Camera(0.0).Length() - Radius), 1e-6);
    }

    /// <summary>Teeth: positions cast to float before subtracting jitter by pixels a few metres above the surface.</summary>
    [Fact]
    public void FloatPositions_JitterNearTheSurface()
    {
        var maxJitter = 0f;
        var lastError = Vector2.Zero;
        var marker = ToFloat(Path.Marker);

        for (var frame = 0; frame < 2000; frame++)
        {
            var s = frame * 2e-5;
            var camera = Path.Camera(s);
            var (rotation, viewProj) = Look(s, camera);
            var pixel = CameraProjection.ProjectFloat(marker - ToFloat(camera), viewProj, Viewport, out _);
            var exact = CameraProjection.ProjectExact(Path.Marker - camera, rotation, FovY, Aspect, new double2(Viewport.X, Viewport.Y));
            var error = new Vector2((float)(pixel.X - exact.X), (float)(pixel.Y - exact.Y));

            if (frame > 0)
                maxJitter = MathF.Max(maxJitter, Vector2.Distance(error, lastError));

            lastError = error;
        }

        Assert.True(maxJitter > 1f, $"naive float jitter only {maxJitter} px");
    }

    /// <summary>Teeth: the textbook float ray-sphere root (c = |oc|² − R²) is centimetres off 2 m above a 1000 km sphere.</summary>
    [Fact]
    public void TextbookFloatSphere_LosesPrecision_WhereTheStableRootDoesNot()
    {
        var camera = Path.Camera(0.0);
        var relative = Path.Marker - camera;
        var range = relative.Length();
        var direction = Vector3.Normalize(ToFloat(relative));
        var oc = ToFloat(camera);
        var b = Vector3.Dot(oc, direction);
        var c = Vector3.Dot(oc, oc) - (float)Radius * (float)Radius;
        var textbook = -b - MathF.Sqrt(b * b - c);

        var altitude = camera.Length() - Radius;
        var stable = CameraProjection.SphereDistance(-oc, (float)Radius, (float)altitude, (float)(altitude * (2.0 * Radius + altitude)), direction);

        Assert.True(Math.Abs(textbook - range) / range > 1e-3, $"textbook error {Math.Abs(textbook - range) / range:E2}");
        Assert.True(Math.Abs(stable - range) / range < 1e-5, $"stable error {Math.Abs(stable - range) / range:E2}");
    }
}
