using System.Numerics;
using BrutalGame.Designer.Tools;
using BrutalGame.Ships;

namespace BrutalGame.Designer.Ui;

/// <summary>
/// Three axis arrows at a point: hover one, press and drag along it. The offset snaps to a step. The caller commits the
/// offset when <see cref="Update"/> reports the drag ended, so a drag is one undo step.
/// </summary>
public sealed class MoveGizmo
{
    private const float HandlePixels = 10f;
    private const double ScreenFraction = 0.12;

    private static readonly uint[] Colours = [OverlayRenderer.Rgba(235, 70, 70), OverlayRenderer.Rgba(90, 220, 90), OverlayRenderer.Rgba(80, 140, 255)];
    private static readonly uint Active = OverlayRenderer.Rgba(255, 230, 120);

    private int _axis = -1;
    private int _hover = -1;
    private Vector3d _origin;
    private double _start;

    public bool Dragging => _axis >= 0;

    public bool Hovered => _hover >= 0;

    /// <summary>The current drag's offset (metres).</summary>
    public Vector3d Offset { get; private set; }

    /// <summary>Draws the arrows at <paramref name="centre"/> (plus the live offset) and runs the drag. True on the frame a drag ends with a non-zero offset.</summary>
    public bool Update(Vector3d centre, OrbitCamera camera, in Matrix4x4 viewProj, Vector2 viewport, Vector2 mouse, bool mouseFree, bool down, bool clicked, double step, OverlayRenderer overlay)
    {
        var p = camera.Position;
        var eye = new Vector3d(p.X, p.Y, p.Z);
        var ray = camera.Ray(mouse, viewport);

        if (_axis >= 0)
        {
            var axis = Axis(_axis);

            if (HullPicking.ClosestOnAxis(eye, ray, _origin, axis, out var along))
            {
                var d = along - _start;

                if (step > 0)
                    d = Math.Round(d / step) * step;

                Offset = HullSurface.Scale(axis, d);
            }

            Draw(HullSurface.Add(centre, Offset), camera, viewProj, viewport, mouse, hover: false, overlay);

            if (down)
                return false;

            _axis = -1;
            return Offset.Length() > 1e-9;
        }

        Offset = default;
        _hover = -1;
        Draw(centre, camera, viewProj, viewport, mouse, mouseFree, overlay);

        if (mouseFree && clicked && _hover >= 0 && HullPicking.ClosestOnAxis(eye, ray, centre, Axis(_hover), out _start))
        {
            _axis = _hover;
            _origin = centre;
        }

        return false;
    }

    private void Draw(Vector3d centre, OrbitCamera camera, in Matrix4x4 viewProj, Vector2 viewport, Vector2 mouse, bool hover, OverlayRenderer overlay)
    {
        var length = Math.Max(0.15, camera.Depth(centre) * ScreenFraction);
        var best = HandlePixels;
        var hasCentre = camera.Project(centre, viewProj, viewport, out var c);

        for (var a = 0; a < 3; a++)
        {
            var d = Axis(a);
            var tip = HullSurface.Add(centre, HullSurface.Scale(d, length));
            var neck = HullSurface.Add(centre, HullSurface.Scale(d, length * 0.8));
            var active = _axis == a || _hover == a;
            var colour = active ? Active : Colours[a];
            overlay.Ribbon(centre, neck, length * (active ? 0.035 : 0.025), colour);
            overlay.Cone(neck, tip, length * (active ? 0.08 : 0.06), colour);

            if (hover && hasCentre && camera.Project(tip, viewProj, viewport, out var t))
            {
                var distance = SegmentDistance(mouse, Vector2.Lerp(c, t, 0.2f), t);

                if (distance < best)
                {
                    best = distance;
                    _hover = a;
                }
            }
        }
    }

    private static Vector3d Axis(int a) => a == 0 ? new Vector3d(1, 0, 0) : a == 1 ? new Vector3d(0, 1, 0) : new Vector3d(0, 0, 1);

    private static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var t = Math.Clamp(Vector2.Dot(p - a, ab) / Math.Max(ab.LengthSquared(), 1e-6f), 0f, 1f);
        return Vector2.Distance(p, a + ab * t);
    }
}
