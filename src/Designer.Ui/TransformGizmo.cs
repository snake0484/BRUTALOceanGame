using System.Numerics;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using BrutalGame.Designer.Tools;
using BrutalGame.Ships;
using static BrutalGame.Ships.HullSurface;

namespace BrutalGame.Designer.Ui;

/// <summary>What the viewport gizmo does: move along ship axes, turn on the rotation rings, or resize faces (W, E, R).</summary>
public enum GizmoMode { Move, Rotate, Scale }

/// <summary>
/// The rotation rings. A rotation is a 15 degree snap index (yaw, then pitch, then roll), so each ring turns about the
/// axis whose steps stay on that grid: yaw about ship up, pitch about the yawed sideways axis, roll about the thing's own
/// front axis. Turning on a ring adds whole steps to that one angle.
/// </summary>
public static class RotationRings
{
    public const int Segments = 64;
    public const double Step = SolidRotation.DegreesPerStep * Math.PI / 180.0;

    /// <summary>Ring colours by ring (yaw, pitch, roll), matching the axis they turn about (Y green, X red, Z blue).</summary>
    public static readonly uint[] Colours = [OverlayRenderer.Rgba(90, 220, 90), OverlayRenderer.Rgba(235, 70, 70), OverlayRenderer.Rgba(80, 140, 255)];

    private static readonly uint Active = OverlayRenderer.Rgba(255, 230, 120);
    private static readonly ImColor8 LabelColour = new(255, 230, 120, 255);

    /// <summary>The ship-frame axis ring <paramref name="ring"/> (0 yaw, 1 pitch, 2 roll) turns about, for <paramref name="rotation"/>.</summary>
    public static Vector3d Axis(uint rotation, int ring)
    {
        if (ring == 0)
            return new Vector3d(0, 1, 0);

        Span<double> m = stackalloc double[9];

        if (ring == 1)
        {
            SolidRotation.Matrix(SolidRotation.FromSteps(SolidRotation.ToSteps(rotation).Yaw, 0, 0), m);
            return new Vector3d(m[0], m[3], m[6]);
        }

        SolidRotation.Matrix(rotation, m);
        return new Vector3d(m[2], m[5], m[8]);
    }

    /// <summary>Draws the three rings; returns the ring under the mouse (within a few pixels) or -1.</summary>
    public static int Draw(OverlayRenderer overlay, Vector3d centre, double radius, uint rotation, int active, int hovered, OrbitCamera camera,
        in Matrix4x4 viewProj, Vector2 viewport, Vector2 mouse, bool pick, float pixels)
    {
        var best = pixels;
        var hit = -1;

        for (var ring = 0; ring < 3; ring++)
        {
            var n = Axis(rotation, ring);
            Basis(n, out var u, out var v);
            var lit = ring == active || (active < 0 && ring == hovered);
            var colour = lit ? Active : Colours[ring];
            var width = radius * (lit ? 0.03 : 0.018);
            var previous = Add(centre, Scale(u, radius));
            var hasPrevious = camera.Project(previous, viewProj, viewport, out var previousPixel);

            for (var s = 1; s <= Segments; s++)
            {
                var a = s * Math.Tau / Segments;
                var point = Add(centre, Add(Scale(u, radius * Math.Cos(a)), Scale(v, radius * Math.Sin(a))));
                overlay.Ribbon(previous, point, width, colour);
                var hasPoint = camera.Project(point, viewProj, viewport, out var pixel);

                if (pick && hasPoint && hasPrevious)
                {
                    var d = SegmentDistance(mouse, previousPixel, pixel);

                    if (d < best)
                    {
                        best = d;
                        hit = ring;
                    }
                }

                (previous, previousPixel, hasPrevious) = (point, pixel, hasPoint);
            }
        }

        return hit;
    }

    /// <summary>The direction from <paramref name="centre"/> to where the mouse ray meets the ring's plane; false when the plane is edge on or behind.</summary>
    public static bool Hit(Vector3d eye, Vector3d ray, Vector3d centre, Vector3d normal, out Vector3d direction)
    {
        direction = default;
        var denominator = Dot(ray, normal);

        if (Math.Abs(denominator) < 1e-3)
            return false;

        var t = Dot(Sub(centre, eye), normal) / denominator;

        if (t <= 0)
            return false;

        var offset = Sub(Add(eye, Scale(ray, t)), centre);
        var length = offset.Length();

        if (length < 1e-9)
            return false;

        direction = Scale(offset, 1.0 / length);
        return true;
    }

    /// <summary>Signed angle from <paramref name="a"/> to <paramref name="b"/>, right handed about <paramref name="normal"/>.</summary>
    public static double Angle(Vector3d normal, Vector3d a, Vector3d b) => Math.Atan2(Dot(normal, Cross(a, b)), Dot(a, b));

    /// <summary>Whole snap steps in an accumulated angle.</summary>
    public static int Steps(double radians) => (int)Math.Round(radians / Step);

    /// <summary>"+45 deg" beside the mouse while a ring is turned.</summary>
    public static void Label(Vector2 mouse, int steps)
    {
        var text = $"{(steps > 0 ? "+" : "")}{steps * SolidRotation.DegreesPerStep} deg";
        ImGui.GetForegroundDrawList(ImGui.GetMainViewport()).AddText(new float2(mouse.X + 18f, mouse.Y + 12f), LabelColour, text);
    }

    private static void Basis(Vector3d n, out Vector3d u, out Vector3d v)
    {
        var helper = Math.Abs(n.Y) < 0.9 ? new Vector3d(0, 1, 0) : new Vector3d(1, 0, 0);
        u = Normalise(Cross(helper, n), new Vector3d(1, 0, 0));
        v = Cross(n, u);
    }

    internal static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var t = Math.Clamp(Vector2.Dot(p - a, ab) / Math.Max(ab.LengthSquared(), 1e-6f), 0f, 1f);
        return Vector2.Distance(p, a + ab * t);
    }
}

/// <summary>
/// The gizmo for things that move and turn but have no size handles (component parts, pipe nodes, placed components):
/// three axis arrows, or in rotate mode the three rotation rings. Hover a handle, press and drag. The caller commits when
/// <see cref="Update"/> reports the drag ended, so a drag is one undo step.
/// </summary>
public sealed class TransformGizmo
{
    private const float HandlePixels = 10f;
    private const double ScreenFraction = 0.12;

    private static readonly uint[] Colours = [OverlayRenderer.Rgba(235, 70, 70), OverlayRenderer.Rgba(90, 220, 90), OverlayRenderer.Rgba(80, 140, 255)];
    private static readonly uint Active = OverlayRenderer.Rgba(255, 230, 120);

    private int _axis = -1;
    private int _hover = -1;
    private bool _rotating;
    private Vector3d _origin;
    private double _start;
    private uint _startRotation;
    private Vector3d _last;
    private double _angle;

    public bool Dragging => _axis >= 0;

    public bool Hovered => _hover >= 0;

    /// <summary>The current move's offset (metres).</summary>
    public Vector3d Offset { get; private set; }

    /// <summary>The current turn's rotation (snap index).</summary>
    public uint Rotation { get; private set; }

    /// <summary>True when the drag that just ended was a turn (use <see cref="Rotation"/>), false for a move (use <see cref="Offset"/>).</summary>
    public bool Turned { get; private set; }

    /// <summary>
    /// Draws the handles at <paramref name="centre"/> and runs the drag. True on the frame a drag ends having changed
    /// something. <paramref name="rotation"/> is null for things that cannot turn (pipe nodes): rotate mode shows arrows.
    /// </summary>
    public bool Update(Vector3d centre, uint? rotation, GizmoMode mode, OrbitCamera camera, in Matrix4x4 viewProj, Vector2 viewport, Vector2 mouse,
        bool mouseFree, bool down, bool clicked, double step, OverlayRenderer overlay)
    {
        overlay.OnTop = true;
        var changed = Run(centre, rotation, mode, camera, viewProj, viewport, mouse, mouseFree, down, clicked, step, overlay);
        overlay.OnTop = false;
        return changed;
    }

    private bool Run(Vector3d centre, uint? rotation, GizmoMode mode, OrbitCamera camera, in Matrix4x4 viewProj, Vector2 viewport, Vector2 mouse,
        bool mouseFree, bool down, bool clicked, double step, OverlayRenderer overlay)
    {
        var p = camera.Position;
        var eye = new Vector3d(p.X, p.Y, p.Z);
        var ray = camera.Ray(mouse, viewport);
        var length = Math.Max(0.15, camera.Depth(centre) * ScreenFraction);

        if (_axis >= 0)
        {
            if (_rotating)
            {
                var n = RotationRings.Axis(_startRotation, _axis);

                if (RotationRings.Hit(eye, ray, _origin, n, out var direction))
                {
                    _angle += RotationRings.Angle(n, _last, direction);
                    _last = direction;
                }

                var steps = RotationRings.Steps(_angle);
                Rotation = SolidRotation.AddSteps(_startRotation, _axis, steps);
                RotationRings.Draw(overlay, centre, length, Rotation, _axis, -1, camera, viewProj, viewport, mouse, pick: false, HandlePixels);
                RotationRings.Label(mouse, steps);

                if (down)
                    return false;

                _axis = -1;
                Turned = true;
                return steps != 0;
            }

            var axis = Axis(_axis);

            if (HullPicking.ClosestOnAxis(eye, ray, _origin, axis, out var along))
            {
                var d = along - _start;

                if (step > 0)
                    d = Math.Round(d / step) * step;

                Offset = Scale(axis, d);
            }

            DrawArrows(Add(centre, Offset), length, camera, viewProj, viewport, mouse, hover: false, overlay);

            if (down)
                return false;

            _axis = -1;
            Turned = false;
            return Offset.Length() > 1e-9;
        }

        Offset = default;
        var lastHover = _hover;
        _hover = -1;
        var turning = mode == GizmoMode.Rotate && rotation is not null;

        if (turning)
            _hover = RotationRings.Draw(overlay, centre, length, rotation!.Value, -1, lastHover, camera, viewProj, viewport, mouse, mouseFree, HandlePixels);
        else
            DrawArrows(centre, length, camera, viewProj, viewport, mouse, mouseFree, overlay);

        if (!mouseFree || !clicked || _hover < 0)
            return false;

        if (turning)
        {
            var n = RotationRings.Axis(rotation!.Value, _hover);

            if (RotationRings.Hit(eye, ray, centre, n, out _last))
            {
                (_axis, _rotating, _origin, _startRotation, _angle) = (_hover, true, centre, rotation.Value, 0);
                Rotation = rotation.Value;
            }
        }
        else if (HullPicking.ClosestOnAxis(eye, ray, centre, Axis(_hover), out _start))
        {
            (_axis, _rotating, _origin) = (_hover, false, centre);
        }

        return false;
    }

    private void DrawArrows(Vector3d centre, double length, OrbitCamera camera, in Matrix4x4 viewProj, Vector2 viewport, Vector2 mouse, bool hover, OverlayRenderer overlay)
    {
        var best = HandlePixels;
        var hasCentre = camera.Project(centre, viewProj, viewport, out var c);

        for (var a = 0; a < 3; a++)
        {
            var d = Axis(a);
            var tip = Add(centre, Scale(d, length));
            var neck = Add(centre, Scale(d, length * 0.8));
            var active = _axis == a || _hover == a;
            var colour = active ? Active : Colours[a];
            overlay.Ribbon(centre, neck, length * (active ? 0.035 : 0.025), colour);
            overlay.Cone(neck, tip, length * (active ? 0.08 : 0.06), colour);

            if (hover && hasCentre && camera.Project(tip, viewProj, viewport, out var t))
            {
                var distance = RotationRings.SegmentDistance(mouse, Vector2.Lerp(c, t, 0.2f), t);

                if (distance < best)
                {
                    best = distance;
                    _hover = a;
                }
            }
        }
    }

    private static Vector3d Axis(int a) => a == 0 ? new Vector3d(1, 0, 0) : a == 1 ? new Vector3d(0, 1, 0) : new Vector3d(0, 0, 1);
}
