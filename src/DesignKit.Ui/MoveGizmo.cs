using System.Numerics;
using Brutal.Numerics;

namespace BrutalGame.DesignKit.Ui;

/// <summary>Screen and ray maths shared by the gizmos.</summary>
public static class GizmoMath
{
    /// <summary>
    /// Where along the line <paramref name="axisOrigin"/> + t·<paramref name="axis"/> the mouse ray passes closest: t, in
    /// units of <paramref name="axis"/>. False when the ray runs parallel to the axis.
    /// </summary>
    public static bool ClosestOnAxis(double3 rayOrigin, double3 rayDirection, double3 axisOrigin, double3 axis, out double along)
    {
        var w = axisOrigin - rayOrigin;
        var a = double3.Dot(axis, axis);
        var b = double3.Dot(axis, rayDirection);
        var c = double3.Dot(rayDirection, rayDirection);
        var d = double3.Dot(axis, w);
        var e = double3.Dot(rayDirection, w);
        var denom = a * c - b * b;
        along = 0.0;

        if (Math.Abs(denom) < 1e-9)
            return false;

        along = (b * e - c * d) / denom;
        return true;
    }

    /// <summary>Pixel distance from <paramref name="p"/> to the segment <paramref name="a"/>–<paramref name="b"/>.</summary>
    public static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var t = Math.Clamp(Vector2.Dot(p - a, ab) / Math.Max(ab.LengthSquared(), 1e-6f), 0f, 1f);
        return Vector2.Distance(p, a + ab * t);
    }

    public static double3 Axis(int a) => a == 0 ? double3.UnitX : a == 1 ? double3.UnitY : double3.UnitZ;
}

/// <summary>
/// Three axis arrows at a point, sized to a fixed share of the screen and drawn on top: hover one, press and drag along
/// it; the offset snaps to a step. <see cref="Update"/> reports the frame the drag ends with a non-zero offset, so the
/// caller commits one undo group per drag.
/// </summary>
public sealed class MoveGizmo
{
    private const float HandlePixels = 10f;

    public static readonly uint[] Colours = [OverlayRenderer.Rgba(235, 70, 70), OverlayRenderer.Rgba(90, 220, 90), OverlayRenderer.Rgba(80, 140, 255)];
    public static readonly uint Active = OverlayRenderer.Rgba(255, 230, 120);

    private int _axis = -1;
    private int _hover = -1;
    private double3 _origin;
    private double _start;

    /// <summary>Arrow length as a share of the distance to the camera.</summary>
    public double ScreenFraction = 0.12;

    /// <summary>Shortest arrow (metres), so it stays grabbable close up.</summary>
    public double MinLength = 0.15;

    public bool Dragging => _axis >= 0;

    public bool Hovered => _hover >= 0;

    /// <summary>The current drag's offset.</summary>
    public double3 Offset { get; private set; }

    /// <summary>
    /// Draws the arrows at <paramref name="centre"/> (plus the live offset) and runs the drag. <paramref name="down"/> is the
    /// left button held, <paramref name="clicked"/> pressed this frame, <paramref name="mouseFree"/> not over UI. True on the
    /// frame a drag ends with a non-zero offset.
    /// </summary>
    public bool Update(double3 centre, OrbitCamera camera, in Matrix4x4 viewProj, Vector2 viewport, Vector2 mouse, bool mouseFree, bool down, bool clicked, double step, OverlayRenderer overlay)
    {
        var eye = camera.Position;
        var ray = camera.Ray(mouse, viewport);
        var length = Math.Max(MinLength, camera.Depth(centre) * ScreenFraction);
        overlay.OnTop = true;

        try
        {
            if (_axis >= 0)
            {
                var axis = GizmoMath.Axis(_axis);

                if (GizmoMath.ClosestOnAxis(eye, ray, _origin, axis, out var along))
                {
                    var d = along - _start;

                    if (step > 0)
                        d = Math.Round(d / step) * step;

                    Offset = axis * d;
                }

                Draw(centre + Offset, length, camera, viewProj, viewport, mouse, hover: false, overlay);

                if (down)
                    return false;

                _axis = -1;
                return Offset.Length() > 1e-9;
            }

            Offset = default;
            _hover = -1;
            Draw(centre, length, camera, viewProj, viewport, mouse, mouseFree, overlay);

            if (mouseFree && clicked && _hover >= 0 && GizmoMath.ClosestOnAxis(eye, ray, centre, GizmoMath.Axis(_hover), out _start))
            {
                _axis = _hover;
                _origin = centre;
            }

            return false;
        }
        finally
        {
            overlay.OnTop = false;
        }
    }

    private void Draw(double3 centre, double length, OrbitCamera camera, in Matrix4x4 viewProj, Vector2 viewport, Vector2 mouse, bool hover, OverlayRenderer overlay)
    {
        var best = HandlePixels;
        var hasCentre = camera.Project(centre, viewProj, viewport, out var c);

        for (var a = 0; a < 3; a++)
        {
            var d = GizmoMath.Axis(a);
            var tip = centre + d * length;
            var neck = centre + d * (length * 0.8);
            var active = _axis == a || _hover == a;
            var colour = active ? Active : Colours[a];
            overlay.Ribbon(centre, neck, length * (active ? 0.035 : 0.025), colour);
            overlay.Cone(neck, tip, length * (active ? 0.08 : 0.06), colour);

            if (hover && hasCentre && camera.Project(tip, viewProj, viewport, out var t))
            {
                var distance = GizmoMath.SegmentDistance(mouse, Vector2.Lerp(c, t, 0.2f), t);

                if (distance < best)
                {
                    best = distance;
                    _hover = a;
                }
            }
        }
    }
}
