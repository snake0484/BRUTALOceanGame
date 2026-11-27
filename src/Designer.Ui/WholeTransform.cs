using System.Numerics;
using BrutalGame.Ships;
using static BrutalGame.Ships.HullSurface;

namespace BrutalGame.Designer.Ui;

/// <summary>
/// Moving or turning a whole node-based thing (a hallway, a brush stroke, a pipe) with one gizmo at the middle of its
/// nodes: a click on one of its lines (not on a node or handle) picks the whole thing; the arrows move every node, the
/// rings turn them all about the middle (and anything that has a direction with them). The gizmo starts unturned, so a
/// finished turn is the turn itself.
/// </summary>
public static class WholeTransform
{
    /// <summary>How near (pixels) a click must be to a line to pick it.</summary>
    public const float LinePixels = 7f;

    public static Vector3d Centre(IReadOnlyList<Vector3d> points)
    {
        Vector3d sum = default;

        foreach (var p in points)
            sum = Add(sum, p);

        return points.Count == 0 ? sum : Scale(sum, 1.0 / points.Count);
    }

    /// <summary>The distance (pixels) from <paramref name="mouse"/> to the line a–b on screen, or infinity when it is not in view.</summary>
    public static float ToLine(OrbitCamera camera, in Matrix4x4 viewProj, Vector2 viewport, Vector2 mouse, Vector3d a, Vector3d b) =>
        camera.Project(a, viewProj, viewport, out var pa) && camera.Project(b, viewProj, viewport, out var pb)
            ? GizmoMath.SegmentDistance(mouse, pa, pb)
            : float.PositiveInfinity;

    /// <summary>Runs the gizmo at <paramref name="centre"/>; true on the frame a drag ends (then <see cref="Apply"/> each node).</summary>
    public static bool Update(TransformGizmo gizmo, Vector3d centre, bool turns, OrbitCamera camera, in Matrix4x4 viewProj, Vector2 viewport, Vector2 mouse,
        bool mouseFree, bool down, bool clicked, double step, OverlayRenderer overlay) =>
        gizmo.Update(centre, turns ? SolidRotation.Identity : null, camera, viewProj, viewport, mouse, mouseFree, down, clicked, step, overlay);

    /// <summary>The finished drag's turn (row-major; identity for a move).</summary>
    public static void Turn(TransformGizmo gizmo, Span<double> m) => SolidRotation.Matrix(gizmo.Turned ? gizmo.Rotation : SolidRotation.Identity, m);

    /// <summary>Point <paramref name="p"/> after the finished drag: moved, or turned about <paramref name="centre"/>.</summary>
    public static Vector3d Apply(TransformGizmo gizmo, ReadOnlySpan<double> m, Vector3d centre, Vector3d p) =>
        gizmo.Turned ? Add(centre, Rotate(m, Sub(p, centre))) : Add(p, gizmo.Offset);

    /// <summary>A direction after the finished drag (turned with it; a move leaves it).</summary>
    public static Vector3d Direction(TransformGizmo gizmo, ReadOnlySpan<double> m, Vector3d v) => gizmo.Turned ? Rotate(m, v) : v;

    /// <summary>A snap rotation after the finished turn (unchanged by a move, or when the result is no snap rotation).</summary>
    public static uint Rotation(TransformGizmo gizmo, ReadOnlySpan<double> m, uint rotation)
    {
        if (!gizmo.Turned)
            return rotation;

        Span<double> r = stackalloc double[9];
        Span<double> product = stackalloc double[9];
        SolidRotation.Matrix(rotation, r);

        for (var i = 0; i < 3; i++)
        {
            for (var j = 0; j < 3; j++)
                product[i * 3 + j] = m[i * 3] * r[j] + m[i * 3 + 1] * r[3 + j] + m[i * 3 + 2] * r[6 + j];
        }

        return SolidRotation.FromMatrix(product, out var turned) ? turned : rotation;
    }

    private static Vector3d Rotate(ReadOnlySpan<double> m, Vector3d v) =>
        new(m[0] * v.X + m[1] * v.Y + m[2] * v.Z, m[3] * v.X + m[4] * v.Y + m[5] * v.Z, m[6] * v.X + m[7] * v.Y + m[8] * v.Z);
}
