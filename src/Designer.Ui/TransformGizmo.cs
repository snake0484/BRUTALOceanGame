using System.Numerics;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using BrutalGame.Designer.Tools;
using BrutalGame.Ships;
using static BrutalGame.Ships.HullSurface;

namespace BrutalGame.Designer.Ui;

/// <summary>
/// The rotation rings. A rotation is a 15 degree snap index (yaw, then pitch, then roll), so each ring turns about the
/// axis whose steps stay on that grid: yaw about ship up, pitch about the yawed sideways axis, roll about the thing's own
/// front axis. Turning on a ring adds whole steps to that one angle.
/// </summary>
public static class RotationRings
{
    public const int Segments = 64;
    public const double Step = SolidRotation.DegreesPerStep * Math.PI / 180.0;

    /// <summary>Ring radius over the move arrows' length: the rings sit just outside the arrow tips.</summary>
    public const double Outside = 1.3;

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

    /// <summary>Draws the three rings; returns the ring under the mouse nearer than <paramref name="best"/> pixels (lowering it) or -1.</summary>
    public static int Draw(OverlayRenderer overlay, Vector3d centre, double radius, uint rotation, int active, int hovered, OrbitCamera camera,
        in Matrix4x4 viewProj, Vector2 viewport, Vector2 mouse, bool pick, ref float best)
    {
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

    internal static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b) => GizmoMath.SegmentDistance(p, a, b);
}

/// <summary>
/// The gizmo for things that move and maybe turn but have no size handles (component parts, pipe and hallway nodes, placed
/// components): the kit's <see cref="MoveGizmo"/> arrows, and the three rotation rings just outside them for things that
/// turn. Hover a handle (arrows win over rings), press and drag; while dragging only that handle shows. The caller commits
/// when <see cref="Update"/> reports the drag ended, so a drag is one undo step.
/// </summary>
public sealed class TransformGizmo
{
    private const float HandlePixels = 10f;

    private readonly MoveGizmo _move = new();
    private int _ring = -1;
    private int _hover = -1;
    private Vector3d _origin;
    private uint _startRotation;
    private Vector3d _last;
    private double _angle;

    public bool Dragging => _ring >= 0 || _move.Dragging;

    public bool Hovered => _hover >= 0 || _move.Hovered;

    /// <summary>The current move's offset (metres).</summary>
    public Vector3d Offset { get; private set; }

    /// <summary>The current turn's rotation (snap index).</summary>
    public uint Rotation { get; private set; }

    /// <summary>True when the drag that just ended was a turn (use <see cref="Rotation"/>), false for a move (use <see cref="Offset"/>).</summary>
    public bool Turned { get; private set; }

    /// <summary>
    /// Draws the handles at <paramref name="centre"/> and runs the drag. True on the frame a drag ends having changed
    /// something. <paramref name="rotation"/> is null for things that cannot turn (pipe nodes): no rings.
    /// </summary>
    public bool Update(Vector3d centre, uint? rotation, OrbitCamera camera, in Matrix4x4 viewProj, Vector2 viewport, Vector2 mouse,
        bool mouseFree, bool down, bool clicked, double step, OverlayRenderer overlay)
    {
        if (_ring >= 0)
            return Rings(centre, _startRotation, camera, viewProj, viewport, mouse, false, down, false, overlay);

        var moved = _move.Update(centre, camera, viewProj, viewport, mouse, mouseFree, down, clicked, step, overlay);
        Offset = _move.Offset;

        if (moved)
        {
            Turned = false;
            return true;
        }

        if (_move.Dragging || rotation is null)
        {
            _hover = -1;
            return false;
        }

        var free = mouseFree && !_move.Hovered;
        Rings(centre, rotation.Value, camera, viewProj, viewport, mouse, free, down, clicked && free, overlay);
        return false;
    }

    private bool Rings(Vector3d centre, uint rotation, OrbitCamera camera, in Matrix4x4 viewProj, Vector2 viewport, Vector2 mouse,
        bool mouseFree, bool down, bool clicked, OverlayRenderer overlay)
    {
        overlay.OnTop = true;
        var changed = Turn(centre, rotation, camera, viewProj, viewport, mouse, mouseFree, down, clicked, overlay);
        overlay.OnTop = false;
        return changed;
    }

    private bool Turn(Vector3d centre, uint rotation, OrbitCamera camera, in Matrix4x4 viewProj, Vector2 viewport, Vector2 mouse,
        bool mouseFree, bool down, bool clicked, OverlayRenderer overlay)
    {
        Vector3d eye = camera.Position;
        Vector3d ray = camera.Ray(mouse, viewport);
        var length = Math.Max(_move.MinLength, camera.Depth(centre) * _move.ScreenFraction) * RotationRings.Outside;
        var best = HandlePixels;

        if (_ring >= 0)
        {
            var n = RotationRings.Axis(_startRotation, _ring);

            if (RotationRings.Hit(eye, ray, _origin, n, out var direction))
            {
                _angle += RotationRings.Angle(n, _last, direction);
                _last = direction;
            }

            var steps = RotationRings.Steps(_angle);
            Rotation = SolidRotation.AddSteps(_startRotation, _ring, steps);
            RotationRings.Draw(overlay, centre, length, Rotation, _ring, -1, camera, viewProj, viewport, mouse, pick: false, ref best);
            RotationRings.Label(mouse, steps);

            if (down)
                return false;

            _ring = -1;
            Turned = true;
            return steps != 0;
        }

        Offset = default;
        var lastHover = _hover;
        _hover = RotationRings.Draw(overlay, centre, length, rotation, -1, lastHover, camera, viewProj, viewport, mouse, mouseFree, ref best);

        if (!mouseFree || !clicked || _hover < 0)
            return false;

        var axis = RotationRings.Axis(rotation, _hover);

        if (RotationRings.Hit(eye, ray, centre, axis, out _last))
        {
            (_ring, _origin, _startRotation, _angle) = (_hover, centre, rotation, 0);
            Rotation = rotation;
        }

        return false;
    }
}
