using System.Numerics;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using BrutalGame.Engine.Scene;
using BrutalGame.Frames;
using BrutalGame.Terrain;

namespace BrutalGame.PlanetDesigner.App;

/// <summary>
/// Globe camera over one body, from three radii out down to a few metres above the ground: it looks at a target point
/// on the surface from a distance, a heading and a tilt. Right drag pans the globe (target moves under the cursor's
/// speed), middle drag (or Alt + right) orbits, the wheel zooms. Drives a <see cref="FlyCamera"/> in the body's surface
/// frame; reads the mouse from ImGui so scripted input works.
/// </summary>
public sealed class PlanetCameraRig
{
    private const double MinDistance = 5.0;
    private const double MaxTilt = 1.45;
    private const double ZoomStep = 1.15;
    private const double OrbitRate = 0.005;
    private const double PanRate = 0.0018;

    private readonly FlyCamera _camera;
    private readonly double _radius;

    public PlanetCameraRig(FlyCamera camera, double radius)
    {
        _camera = camera;
        _radius = radius;
        Distance = 3.0 * radius;
    }

    public double3 Target = double3.Normalize(new double3(0.3, 0.5, 0.8));
    public double Heading;
    public double Tilt = 0.6;
    public double Distance;

    /// <summary>The ground height under the target (m about the radius), refreshed by the app from the current surface.</summary>
    public double Ground;

    public void Update(FrameId frame, bool mouseFree)
    {
        var io = ImGui.GetIO();

        if (mouseFree)
        {
            var delta = io.MouseDelta;
            var alt = io.KeyAlt;

            if (ImGui.IsMouseDown(ImGuiMouseButton.Middle) || (alt && ImGui.IsMouseDown(ImGuiMouseButton.Right)))
            {
                Heading -= delta.X * OrbitRate;
                Tilt = Math.Clamp(Tilt - delta.Y * OrbitRate, 0.0, MaxTilt);
            }
            else if (ImGui.IsMouseDown(ImGuiMouseButton.Right))
            {
                Basis(out var east, out var north);
                var scale = PanRate * Math.Min(Distance, 2.0 * _radius) / _radius;
                var (s, c) = Math.SinCos(Heading);
                var forward = north * c + east * s;
                var right = east * c - north * s;
                Target = double3.Normalize(Target + (forward * delta.Y - right * delta.X) * scale);
            }

            if (io.MouseWheel != 0f)
                Distance = Math.Clamp(Distance * Math.Pow(ZoomStep, -io.MouseWheel), MinDistance, 4.0 * _radius);
        }

        Place(frame);
    }

    /// <summary>Puts the camera where the rig says, looking at the target.</summary>
    public void Place(FrameId frame)
    {
        Basis(out var east, out var north);
        var (s, c) = Math.SinCos(Heading);
        var horizontal = north * c + east * s;
        var point = Target * (_radius + Ground);
        var (st, ct) = Math.SinCos(Tilt);
        var position = point + (Target * ct - horizontal * st) * Distance;
        var forward = double3.Normalize(point - position);
        _camera.Frame = frame;
        _camera.Position = position;
        _camera.Rotation = CameraProjection.LookRotation(forward, Target, double3.Normalize(double3.Cross(forward, Target) + east * 1e-9));
        _camera.Near = (float)Math.Clamp(Distance * 0.001, 0.05, 50.0);
    }

    /// <summary>The ray under the cursor in the body frame (origin, unit direction).</summary>
    public (double3 Origin, double3 Direction) Ray(Vector2 mouse, Vector2 viewport)
    {
        var aspect = viewport.X / viewport.Y;
        var t = Math.Tan(_camera.FovY * 0.5);
        var x = (2.0 * mouse.X / viewport.X - 1.0) * t * aspect;
        var y = (1.0 - 2.0 * mouse.Y / viewport.Y) * t;
        var d = _camera.Rotation * double3.Normalize(new double3(x, y, -1.0));
        return (_camera.Position, d);
    }

    /// <summary>A body-frame point on screen (pixels); false behind the camera.</summary>
    public bool Project(double3 point, Vector2 viewport, out Vector2 screen)
    {
        var local = doubleQuat.Conjugate(_camera.Rotation) * (point - _camera.Position);
        screen = default;

        if (local.Z > -1e-3)
            return false;

        var t = Math.Tan(_camera.FovY * 0.5);
        var aspect = viewport.X / viewport.Y;
        var x = local.X / (-local.Z * t * aspect);
        var y = local.Y / (-local.Z * t);
        screen = new Vector2((float)((x + 1.0) * 0.5 * viewport.X), (float)((1.0 - y) * 0.5 * viewport.Y));
        return true;
    }

    private void Basis(out double3 east, out double3 north)
    {
        var axis = Math.Abs(Target.Y) < 0.99 ? double3.UnitY : double3.UnitX;
        east = double3.Normalize(double3.Cross(axis, Target));
        north = double3.Cross(Target, east);
    }
}

/// <summary>Ray against the analytic surface (generated + authored): the sphere of the relief top, then march and bisect.</summary>
public static class PlanetPick
{
    public static bool Ray(PlanetSurface surface, double3 origin, double3 direction, out double3 hit)
    {
        hit = default;
        var radius = surface.Radius;
        var top = radius + surface.Params.MaxHeight + 10.0;
        var b = double3.Dot(origin, direction);
        var c = double3.Dot(origin, origin) - top * top;
        var disc = b * b - c;

        if (disc < 0.0)
            return false;

        var t = Math.Max(0.0, -b - Math.Sqrt(disc));
        var far = -b + Math.Sqrt(disc);
        var last = t;

        for (var step = 0; step < 4000 && t < far; step++)
        {
            var p = origin + direction * t;
            var r = p.Length();
            var d = p / r;
            var above = r - radius - surface.Height(d.X, d.Y, d.Z);

            if (above <= 0.0)
            {
                double lo = last, hi = t;

                for (var k = 0; k < 40; k++)
                {
                    var mid = 0.5 * (lo + hi);
                    var q = origin + direction * mid;
                    var qr = q.Length();
                    var qd = q / qr;

                    if (qr - radius - surface.Height(qd.X, qd.Y, qd.Z) > 0.0)
                        lo = mid;
                    else
                        hi = mid;
                }

                hit = origin + direction * hi;
                return true;
            }

            last = t;
            t += Math.Max(0.5, 0.3 * above);
        }

        return false;
    }
}
