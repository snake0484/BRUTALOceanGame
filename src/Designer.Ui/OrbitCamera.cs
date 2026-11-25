using System.Numerics;
using Brutal.Numerics;
using BrutalGame.Frames;
using BrutalGame.Ships;

namespace BrutalGame.Designer.Ui;

/// <summary>
/// Orbit camera about a target in ship metres (Y up): right drag orbits, middle drag (or Shift + right) pans, the wheel
/// zooms. The view has no translation; draws go at (point − <see cref="Position"/>) in double. Infinite reverse-Z.
/// </summary>
public sealed class OrbitCamera
{
    private const double MaxPitch = 89.0 * Math.PI / 180.0;
    private const double OrbitPerPixel = 0.006;
    private const double ZoomPerNotch = 0.88;
    private const double MinDistance = 1.0;
    private const double MaxDistance = 2000.0;

    public double3 Target = new(0.0, 0.0, 0.0);
    public double Yaw = 0.8;
    public double Pitch = 0.45;
    public double Distance = 30.0;
    public float FovY = 50f * MathF.PI / 180f;
    public float Near = 0.05f;

    /// <summary>Unit vector from the target to the camera.</summary>
    public double3 Back => new(Math.Cos(Pitch) * Math.Sin(Yaw), Math.Sin(Pitch), Math.Cos(Pitch) * Math.Cos(Yaw));

    public double3 Position => Target + Back * Distance;

    public doubleQuat Rotation => CameraProjection.LookRotation(-Back, double3.UnitY, new double3(Math.Cos(Yaw), 0.0, -Math.Sin(Yaw)));

    public Matrix4x4 ViewProjection(float aspect) => CameraProjection.View(Rotation) * CameraProjection.Projection(FovY, aspect, Near);

    public void Orbit(double dx, double dy)
    {
        Yaw -= dx * OrbitPerPixel;
        Pitch = Math.Clamp(Pitch + dy * OrbitPerPixel, -MaxPitch, MaxPitch);
    }

    /// <summary>Moves the target across the view by a drag in pixels, scaled so the point under the cursor follows it.</summary>
    public void Pan(double dx, double dy, double viewportHeight)
    {
        var rotation = Rotation;
        var right = rotation * double3.UnitX;
        var up = rotation * double3.UnitY;
        var metresPerPixel = 2.0 * Distance * Math.Tan(FovY * 0.5) / Math.Max(1.0, viewportHeight);
        Target += (-right * dx + up * dy) * metresPerPixel;
    }

    public void Zoom(double notches) => Distance = Math.Clamp(Distance * Math.Pow(ZoomPerNotch, notches), MinDistance, MaxDistance);

    /// <summary>Looks at <paramref name="centre"/> from far enough to see a sphere of <paramref name="radius"/>.</summary>
    public void Frame(double3 centre, double radius)
    {
        Target = centre;
        Distance = Math.Clamp(radius / Math.Sin(FovY * 0.5) * 1.1, MinDistance, MaxDistance);
    }

    /// <summary>Unit ray through a pixel (top-left origin) in ship axes, from <see cref="Position"/>.</summary>
    public Vector3d Ray(Vector2 pixel, Vector2 viewport)
    {
        var aspect = viewport.X / Math.Max(1f, viewport.Y);
        var t = Math.Tan(FovY * 0.5);
        var x = (pixel.X / viewport.X * 2.0 - 1.0) * t * aspect;
        var y = (1.0 - pixel.Y / viewport.Y * 2.0) * t;
        var rotation = Rotation;
        var d = rotation * new double3(x, y, -1.0);
        d = double3.Normalize(d);
        return new Vector3d(d.X, d.Y, d.Z);
    }

    /// <summary>Pixel of a ship-space point; false behind the near plane.</summary>
    public bool Project(Vector3d point, in Matrix4x4 viewProj, Vector2 viewport, out Vector2 pixel)
    {
        var p = Position;
        var relative = new Vector3((float)(point.X - p.X), (float)(point.Y - p.Y), (float)(point.Z - p.Z));
        var clip = Vector4.Transform(new Vector4(relative, 1f), viewProj);
        pixel = default;

        if (clip.W <= Near)
            return false;

        pixel = new Vector2((clip.X / clip.W * 0.5f + 0.5f) * viewport.X, (clip.Y / clip.W * 0.5f + 0.5f) * viewport.Y);
        return true;
    }

    /// <summary>View-space depth (distance along the view axis) of a ship-space point.</summary>
    public double Depth(Vector3d point)
    {
        var back = Back;
        var p = Position;
        return -((point.X - p.X) * back.X + (point.Y - p.Y) * back.Y + (point.Z - p.Z) * back.Z);
    }
}
