using System.Numerics;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using BrutalGame.Designer.Tools;
using BrutalGame.Ships;

namespace BrutalGame.Designer.Ui;

/// <summary>
/// The 3D viewport's overlay and mouse tool, drawn with ImGui's background draw list over the scene (no depth test):
/// brush wireframes (Add grey, Subtract red, Paint amber, selection bright), a move gizmo on ship axes and resize
/// handles on the primary brush's local axes. Left click picks (Ctrl toggles); dragging a handle previews the brush and
/// commits one undo group on release, so nothing bakes while dragging.
/// </summary>
public sealed class ViewportTool
{
    private const float HandlePixels = 9f;
    private const float GizmoScreenFraction = 0.16f;
    private const int CircleSegments = 24;

    private static readonly ImColor8 AddColour = new(170, 178, 190, 120);
    private static readonly ImColor8 SubtractColour = new(235, 80, 70, 170);
    private static readonly ImColor8 PaintColour = new(240, 190, 60, 170);
    private static readonly ImColor8 SelectedColour = new(255, 255, 255, 255);
    private static readonly ImColor8 PreviewColour = new(120, 220, 255, 255);
    private static readonly ImColor8[] AxisColours = [new(235, 70, 70, 255), new(90, 220, 90, 255), new(80, 140, 255, 255)];

    private readonly double[] _matrix = new double[9];
    private DragKind _drag;
    private int _dragAxis;
    private Vector3d _dragOrigin;
    private Vector3d _dragDirection;
    private double _dragStart;
    private HullBrush _dragBrush;
    private HullBrush _preview;
    private int _hoverAxis = -1;
    private DragKind _hoverKind;

    private enum DragKind { None, Move, Resize }

    public bool Dragging => _drag != DragKind.None;

    public void Update(DesignSession session, OrbitCamera camera, Vector2 viewport, in Matrix4x4 viewProj, bool mouseFree)
    {
        var io = ImGui.GetIO();
        var mouse = new Vector2(io.MousePos.X, io.MousePos.Y);
        var list = ImGui.GetBackgroundDrawList(ImGui.GetMainViewport());
        var primary = session.Selection.Primary;
        var hasPrimary = session.TryGetBrush(primary, out var brush);

        foreach (ref readonly var b in session.Doc.Brushes.AsSpan())
        {
            if (_drag != DragKind.None && b.Id == primary)
                continue;

            var selected = session.Selection.Contains(b.Id);
            var colour = selected ? SelectedColour : b.Op switch
            {
                BrushOp.Subtract => SubtractColour,
                BrushOp.Paint => PaintColour,
                _ => AddColour,
            };
            DrawBrush(list, b, camera, viewProj, viewport, colour, selected ? 2f : 1f);
        }

        if (_drag != DragKind.None)
        {
            UpdateDrag(session, camera, viewport, mouse, io.MouseDown[0]);
            DrawBrush(list, _preview, camera, viewProj, viewport, PreviewColour, 2f);
            DrawHandles(list, _preview, camera, viewProj, viewport, mouse, hover: false);
            return;
        }

        _hoverAxis = -1;

        if (hasPrimary)
            DrawHandles(list, brush, camera, viewProj, viewport, mouse, hover: mouseFree);

        if (!mouseFree || !ImGui.IsMouseClicked(ImGuiMouseButton.Left, false))
            return;

        if (hasPrimary && _hoverAxis >= 0)
        {
            BeginDrag(brush, camera, viewport, mouse);
            return;
        }

        var ray = camera.Ray(mouse, viewport);
        var hit = HullPicking.Pick(session.Doc, ToVector3d(camera.Position), ray, out _);

        if (io.KeyCtrl)
        {
            if (hit != 0)
                session.Selection.Toggle(hit);
        }
        else
        {
            session.Selection.Set(hit);
        }
    }

    private void BeginDrag(in HullBrush brush, OrbitCamera camera, Vector2 viewport, Vector2 mouse)
    {
        _drag = _hoverKind;
        _dragAxis = _hoverAxis;
        _dragBrush = brush;
        _preview = brush;
        _dragOrigin = new Vector3d(brush.PositionX, brush.PositionY, brush.PositionZ);
        _dragDirection = _drag == DragKind.Move ? ShipAxis(_dragAxis) : LocalAxis(brush, _dragAxis);
        var ray = camera.Ray(mouse, viewport);

        if (!HullPicking.ClosestOnAxis(ToVector3d(camera.Position), ray, _dragOrigin, _dragDirection, out _dragStart))
            _drag = DragKind.None;
    }

    private void UpdateDrag(DesignSession session, OrbitCamera camera, Vector2 viewport, Vector2 mouse, bool held)
    {
        var ray = camera.Ray(mouse, viewport);

        if (HullPicking.ClosestOnAxis(ToVector3d(camera.Position), ray, _dragOrigin, _dragDirection, out var along))
        {
            var delta = along - _dragStart;
            var brush = _dragBrush;

            if (_drag == DragKind.Move)
            {
                var units = (int)Math.Round(delta * HullBrush.PositionUnitsPerMetre);

                if (_dragAxis == 0)
                    brush.PosX += units;
                else if (_dragAxis == 1)
                    brush.PosY += units;
                else
                    brush.PosZ += units;
            }
            else
            {
                Resize(ref brush, delta);
            }

            _preview = session.Snap.SnapBrush(brush);
        }

        if (held)
            return;

        _drag = DragKind.None;
        HullCommands.SetBrush(session, _dragBrush.Id, _preview);
    }

    /// <summary>Grows the brush along one local axis by <paramref name="delta"/> metres, keeping the opposite face still.</summary>
    private void Resize(ref HullBrush brush, double delta)
    {
        var axis = _dragAxis;
        var cylinderRadius = brush.Shape == BrushShape.Cylinder && axis != 1;
        int half = axis == 0 || cylinderRadius ? brush.HalfX : axis == 1 ? brush.HalfY : brush.HalfZ;
        var newHalf = Math.Clamp(half + (int)Math.Round(delta * 0.5 * HullBrush.HalfUnitsPerMetre), 16, ushort.MaxValue);
        var moved = (newHalf - half) * (HullBrush.PositionUnitsPerMetre / HullBrush.HalfUnitsPerMetre);

        if (cylinderRadius)
        {
            brush.HalfX = (ushort)newHalf;
            brush.HalfZ = (ushort)newHalf;
            return;
        }

        if (axis == 0)
            brush.HalfX = (ushort)newHalf;
        else if (axis == 1)
            brush.HalfY = (ushort)newHalf;
        else
            brush.HalfZ = (ushort)newHalf;

        brush.PosX += (int)Math.Round(_dragDirection.X * moved);
        brush.PosY += (int)Math.Round(_dragDirection.Y * moved);
        brush.PosZ += (int)Math.Round(_dragDirection.Z * moved);
    }

    private void DrawHandles(ImDrawListPtr list, in HullBrush brush, OrbitCamera camera, in Matrix4x4 viewProj, Vector2 viewport, Vector2 mouse, bool hover)
    {
        var centre = new Vector3d(brush.PositionX, brush.PositionY, brush.PositionZ);
        var length = Math.Max(0.5, camera.Depth(centre) * GizmoScreenFraction);
        var best = HandlePixels;

        if (!camera.Project(centre, viewProj, viewport, out var c))
            return;

        for (var axis = 0; axis < 3; axis++)
        {
            var d = ShipAxis(axis);
            var tip = new Vector3d(centre.X + d.X * length, centre.Y + d.Y * length, centre.Z + d.Z * length);

            if (!camera.Project(tip, viewProj, viewport, out var t))
                continue;

            var active = (_drag == DragKind.Move && _dragAxis == axis) || (_hoverKind == DragKind.Move && _hoverAxis == axis);
            list.AddLine(F(c), F(t), AxisColours[axis], active ? 4f : 2.5f);
            list.AddCircleFilled(F(t), active ? 7f : 5f, AxisColours[axis], 12);

            if (hover && Vector2.Distance(mouse, t) < best)
            {
                best = Vector2.Distance(mouse, t);
                (_hoverAxis, _hoverKind) = (axis, DragKind.Move);
            }
        }

        var half = BrushGeometry.Half(brush);

        for (var axis = 0; axis < 3; axis++)
        {
            if (brush.Shape == BrushShape.Cylinder && axis == 2)
                continue;

            var d = LocalAxis(brush, axis);
            var reach = (axis == 0 ? half.X : axis == 1 ? half.Y : half.Z) + 0.02 * length;
            var face = new Vector3d(centre.X + d.X * reach, centre.Y + d.Y * reach, centre.Z + d.Z * reach);

            if (!camera.Project(face, viewProj, viewport, out var f))
                continue;

            var active = (_drag == DragKind.Resize && _dragAxis == axis) || (_hoverKind == DragKind.Resize && _hoverAxis == axis);
            var r = active ? 7f : 5f;
            list.AddRectFilled(F(f - new Vector2(r, r)), F(f + new Vector2(r, r)), AxisColours[axis]);
            list.AddRect(F(f - new Vector2(r, r)), F(f + new Vector2(r, r)), SelectedColour);

            if (hover && Vector2.Distance(mouse, f) < best)
            {
                best = Vector2.Distance(mouse, f);
                (_hoverAxis, _hoverKind) = (axis, DragKind.Resize);
            }
        }
    }

    private void DrawBrush(ImDrawListPtr list, in HullBrush brush, OrbitCamera camera, in Matrix4x4 viewProj, Vector2 viewport, ImColor8 colour, float thickness)
    {
        BrushRotation.Matrix(brush.Rotation, _matrix);
        var h = BrushGeometry.Half(brush);
        var c = new Vector3d(brush.PositionX, brush.PositionY, brush.PositionZ);

        if (brush.Shape == BrushShape.Cylinder)
        {
            for (var s = 0; s < CircleSegments; s++)
            {
                var a0 = s * Math.Tau / CircleSegments;
                var a1 = (s + 1) * Math.Tau / CircleSegments;
                var p0 = new Vector3d(Math.Cos(a0) * h.X, 0.0, Math.Sin(a0) * h.X);
                var p1 = new Vector3d(Math.Cos(a1) * h.X, 0.0, Math.Sin(a1) * h.X);
                Edge(list, c, Offset(p0, h.Y), Offset(p1, h.Y), camera, viewProj, viewport, colour, thickness);
                Edge(list, c, Offset(p0, -h.Y), Offset(p1, -h.Y), camera, viewProj, viewport, colour, thickness);

                if (s % 6 == 0)
                    Edge(list, c, Offset(p0, h.Y), Offset(p0, -h.Y), camera, viewProj, viewport, colour, thickness);
            }

            return;
        }

        Span<Vector3d> corners = stackalloc Vector3d[8];

        for (var i = 0; i < 8; i++)
            corners[i] = new Vector3d((i & 1) != 0 ? h.X : -h.X, (i & 2) != 0 ? h.Y : -h.Y, (i & 4) != 0 ? h.Z : -h.Z);

        // Corners: bit 0 = +X, bit 1 = +Y, bit 2 = +Z. The wedge's top front corners (+Y, +Z) fold onto the top back ones.
        if (brush.Shape == BrushShape.Wedge)
        {
            corners[2 | 4] = corners[2];
            corners[1 | 2 | 4] = corners[1 | 2];
        }

        ReadOnlySpan<byte> edges = [0, 1, 2, 3, 4, 5, 6, 7, 0, 2, 1, 3, 4, 6, 5, 7, 0, 4, 1, 5, 2, 6, 3, 7];

        for (var e = 0; e < edges.Length; e += 2)
            Edge(list, c, corners[edges[e]], corners[edges[e + 1]], camera, viewProj, viewport, colour, thickness);
    }

    private static Vector3d Offset(Vector3d p, double y) => p with { Y = y };

    private void Edge(ImDrawListPtr list, Vector3d centre, Vector3d a, Vector3d b, OrbitCamera camera, in Matrix4x4 viewProj, Vector2 viewport, ImColor8 colour, float thickness)
    {
        var wa = Transform(centre, a);
        var wb = Transform(centre, b);
        var near = camera.Near * 4.0;
        var da = camera.Depth(wa);
        var db = camera.Depth(wb);

        if (da < near && db < near)
            return;

        if (da < near || db < near)
        {
            var t = (near - da) / (db - da);
            var cut = new Vector3d(wa.X + (wb.X - wa.X) * t, wa.Y + (wb.Y - wa.Y) * t, wa.Z + (wb.Z - wa.Z) * t);

            if (da < near)
                wa = cut;
            else
                wb = cut;
        }

        if (camera.Project(wa, viewProj, viewport, out var pa) && camera.Project(wb, viewProj, viewport, out var pb))
            list.AddLine(F(pa), F(pb), colour, thickness);
    }

    private Vector3d Transform(Vector3d centre, Vector3d local)
    {
        var m = _matrix;
        return new Vector3d(
            centre.X + m[0] * local.X + m[1] * local.Y + m[2] * local.Z,
            centre.Y + m[3] * local.X + m[4] * local.Y + m[5] * local.Z,
            centre.Z + m[6] * local.X + m[7] * local.Y + m[8] * local.Z);
    }

    private Vector3d LocalAxis(in HullBrush brush, int axis)
    {
        BrushRotation.Matrix(brush.Rotation, _matrix);
        return new Vector3d(_matrix[axis], _matrix[3 + axis], _matrix[6 + axis]);
    }

    private static Vector3d ShipAxis(int axis) => axis == 0 ? new Vector3d(1, 0, 0) : axis == 1 ? new Vector3d(0, 1, 0) : new Vector3d(0, 0, 1);

    private static Vector3d ToVector3d(double3 v) => new(v.X, v.Y, v.Z);

    private static float2 F(Vector2 v) => new(v.X, v.Y);
}
