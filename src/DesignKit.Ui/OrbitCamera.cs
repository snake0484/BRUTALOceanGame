using System.Numerics;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using BrutalGame.Frames;

namespace BrutalGame.DesignKit.Ui;

/// <summary>
/// Orbit camera about a target in double precision (<see cref="Up"/>, Y by default; a planet tool sets the local
/// vertical at its target each frame): right drag orbits, middle drag (or Shift + right) pans, the
/// wheel zooms. The view has no translation; draws go at (point − <see cref="Position"/>) in double. Infinite reverse-Z.
/// The distance range is the host's (a ship: 1 m to 2 km; a planet: metres above the ground to several radii).
/// </summary>
public sealed class OrbitCamera
{
    private const double MaxPitch = 89.0 * Math.PI / 180.0;
    private const double OrbitPerPixel = 0.006;
    private const double ZoomPerNotch = 0.88;

    /// <summary>Closest the camera zooms to its target (metres).</summary>
    public double MinDistance = 1.0;

    /// <summary>Farthest the camera zooms out (metres).</summary>
    public double MaxDistance = 2000.0;

    public double3 Target = new(0.0, 0.0, 0.0);
    public double Yaw = 0.8;
    public double Pitch = 0.45;
    public double Distance = 30.0;
    public float FovY = 50f * MathF.PI / 180f;
    public float Near = 0.05f;

    /// <summary>The unit up yaw turns about and pitch rises toward; yaw 0 looks back along the axis nearest +Z across it.</summary>
    public double3 Up = double3.UnitY;

    /// <summary>Unit vector from the target to the camera.</summary>
    public double3 Back
    {
        get
        {
            Basis(out var east, out var north);
            return east * (Math.Cos(Pitch) * Math.Sin(Yaw)) + Up * Math.Sin(Pitch) + north * (Math.Cos(Pitch) * Math.Cos(Yaw));
        }
    }

    public double3 Position => Target + Back * Distance;

    public doubleQuat Rotation
    {
        get
        {
            Basis(out var east, out var north);
            return CameraProjection.LookRotation(-Back, Up, east * Math.Cos(Yaw) - north * Math.Sin(Yaw));
        }
    }

    /// <summary>Y up gives X and Z exactly (the ship designer's axes); any other up, +Z made perpendicular to it (X near the poles).</summary>
    private void Basis(out double3 east, out double3 north)
    {
        if (Up == double3.UnitY)
        {
            (east, north) = (double3.UnitX, double3.UnitZ);
            return;
        }

        var axis = Math.Abs(Up.Z) < 0.99 ? double3.UnitZ : double3.UnitX;
        north = double3.Normalize(axis - Up * double3.Dot(axis, Up));
        east = double3.Cross(Up, north);
    }

    public Matrix4x4 ViewProjection(float aspect) => CameraProjection.View(Rotation) * CameraProjection.Projection(FovY, aspect, Near);

    private double _goalYaw;
    private double _goalPitch;
    private bool _turning;

    /// <summary>Starts a smooth turn to look from yaw and pitch (radians); <see cref="Step"/> runs it.</summary>
    public void TurnTo(double yaw, double pitch)
    {
        (_goalYaw, _goalPitch, _turning) = (yaw, Math.Clamp(pitch, -MaxPitch, MaxPitch), true);
    }

    /// <summary>Each frame: eases a turn started by <see cref="TurnTo"/> (the short way round), about a fifth of a second.</summary>
    public void Step(double dt)
    {
        if (!_turning)
            return;

        var k = 1.0 - Math.Exp(-dt * 16.0);
        var yaw = Math.IEEERemainder(_goalYaw - Yaw, Math.Tau);
        var pitch = _goalPitch - Pitch;

        if (Math.Abs(yaw) < 1e-4 && Math.Abs(pitch) < 1e-4)
        {
            Yaw += yaw;
            Pitch = _goalPitch;
            _turning = false;
            return;
        }

        Yaw += yaw * k;
        Pitch += pitch * k;
    }

    public void Orbit(double dx, double dy)
    {
        _turning = false;
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

    /// <summary>The usual mouse: right drag orbits, middle drag (or Shift + right) pans, the wheel zooms. Call when the UI does not want the mouse.</summary>
    public void Mouse(ImGuiIOPtr io, float viewportHeight)
    {
        var delta = io.MouseDelta;
        var panning = io.MouseDown[2] || (io.MouseDown[1] && io.KeyShift);

        if (panning)
            Pan(delta.X, delta.Y, viewportHeight);
        else if (io.MouseDown[1])
            Orbit(delta.X, delta.Y);

        if (io.MouseWheel != 0f)
            Zoom(io.MouseWheel);
    }

    public void Zoom(double notches) => Distance = Math.Clamp(Distance * Math.Pow(ZoomPerNotch, notches), MinDistance, MaxDistance);

    /// <summary>Looks at <paramref name="centre"/> from far enough to see a sphere of <paramref name="radius"/>.</summary>
    public void Frame(double3 centre, double radius)
    {
        Target = centre;
        Distance = Math.Clamp(radius / Math.Sin(FovY * 0.5) * 1.1, MinDistance, MaxDistance);
    }

    /// <summary>Unit ray through a pixel (top-left origin) in ship axes, from <see cref="Position"/>.</summary>
    public double3 Ray(Vector2 pixel, Vector2 viewport)
    {
        var aspect = viewport.X / Math.Max(1f, viewport.Y);
        var t = Math.Tan(FovY * 0.5);
        var x = (pixel.X / viewport.X * 2.0 - 1.0) * t * aspect;
        var y = (1.0 - pixel.Y / viewport.Y * 2.0) * t;
        var rotation = Rotation;
        var d = rotation * new double3(x, y, -1.0);
        return double3.Normalize(d);
    }

    /// <summary>Pixel of a ship-space point; false behind the near plane.</summary>
    public bool Project(double3 point, in Matrix4x4 viewProj, Vector2 viewport, out Vector2 pixel)
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
    public double Depth(double3 point)
    {
        var back = Back;
        var p = Position;
        return -((point.X - p.X) * back.X + (point.Y - p.Y) * back.Y + (point.Z - p.Z) * back.Z);
    }
}
