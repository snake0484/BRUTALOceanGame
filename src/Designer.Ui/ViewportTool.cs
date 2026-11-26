using System.Numerics;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using BrutalGame.Designer.Mesh;
using BrutalGame.Designer.Tools;
using BrutalGame.Ships;

namespace BrutalGame.Designer.Ui;

/// <summary>State of the mesh editor for the primary brush: on or off, selection mode and elements, live mirror.</summary>
public sealed class MeshEditState
{
    public bool Enabled;
    public bool Mirror;
    public readonly MeshSelection Selection = new();
    public uint BrushId;
    public ulong MeshHash;

    /// <summary>Drops the selection when the edited brush or its mesh changed under it (undo, another op).</summary>
    public void Sync(uint brush, HullMesh? mesh)
    {
        if (brush != BrushId || mesh is null)
        {
            Selection.Clear();
            BrushId = brush;
        }

        if (mesh is not null && mesh.MeshHash != MeshHash)
        {
            Selection.Prune(mesh);
            MeshHash = mesh.MeshHash;
        }
    }
}

/// <summary>
/// The 3D viewport's mouse tool and overlay (drawn by <see cref="OverlayRenderer"/>, depth tested against the hull,
/// faint where hidden): brush outlines (Add grey, Subtract red, Paint amber, selection thick white), a move gizmo on ship
/// axes and resize handles on the primary brush's local axes, and a translucent ghost of the brush being dragged
/// (nothing bakes until release, which commits one undo group). With the mesh editor on, the primary mesh brush's cage
/// is drawn instead, clicks pick vertices, edges or faces, and the gizmo moves the selection.
/// </summary>
public sealed class ViewportTool
{
    private const float HandlePixels = 10f;
    private const float EdgePixels = 7f;
    private const double GizmoScreenFraction = 0.16;
    private const int CircleSegments = 24;
    private const int MeshSnapUnits = 16;

    private static readonly uint AddColour = OverlayRenderer.Rgba(175, 182, 195, 150);
    private static readonly uint SubtractColour = OverlayRenderer.Rgba(240, 80, 70, 210);
    private static readonly uint PaintColour = OverlayRenderer.Rgba(240, 190, 60, 210);
    private static readonly uint SelectedColour = OverlayRenderer.Rgba(255, 255, 255, 255);
    private static readonly uint CageColour = OverlayRenderer.Rgba(120, 220, 255, 255);
    private static readonly uint PickColour = OverlayRenderer.Rgba(255, 150, 40, 255);
    private static readonly uint HoverColour = OverlayRenderer.Rgba(255, 230, 120, 255);
    private static readonly uint[] AxisColours = [OverlayRenderer.Rgba(235, 70, 70), OverlayRenderer.Rgba(90, 220, 90), OverlayRenderer.Rgba(80, 140, 255)];
    private static readonly uint FrontColour = OverlayRenderer.Rgba(255, 196, 40);
    private static readonly ImColor8 FrontLabel = new(255, 196, 40, 255);
    private const double GuideMargin = 2.0;
    private const double MinGuideHalf = 6.0;

    private readonly double[] _matrix = new double[9];
    private readonly List<int> _triangles = new();
    private DragKind _drag;
    private int _dragAxis;
    private Vector3d _dragOrigin;
    private Vector3d _dragDirection;
    private double _dragStart;
    private HullBrush _dragBrush;
    private HullBrush _preview;
    private HullMesh? _previewMesh;
    private HashSet<int>? _dragVertices;
    private int _hoverAxis = -1;
    private DragKind _hoverKind;

    private enum DragKind { None, Move, Resize, MeshMove }

    public bool Dragging => _drag != DragKind.None;

    public void Update(DesignSession session, MeshEditState edit, OrbitCamera camera, Vector2 viewport, in Matrix4x4 viewProj, bool mouseFree, OverlayRenderer overlay)
    {
        var io = ImGui.GetIO();
        var mouse = new Vector2(io.MousePos.X, io.MousePos.Y);
        var primary = session.Selection.Primary;
        var hasPrimary = session.TryGetBrush(primary, out var brush);
        var mesh = hasPrimary ? MeshCommands.MeshOf(session, primary) : null;
        edit.Sync(primary, mesh);
        var editing = edit.Enabled && mesh is not null;
        DrawGuides(session, camera, viewport, viewProj, overlay);

        foreach (ref readonly var b in session.Doc.Brushes.AsSpan())
        {
            if ((_drag != DragKind.None || editing) && b.Id == primary)
                continue;

            var selected = session.Selection.Contains(b.Id);
            var colour = selected ? SelectedColour : b.Op switch
            {
                BrushOp.Subtract => SubtractColour,
                BrushOp.Paint => PaintColour,
                _ => AddColour,
            };
            DrawBrush(overlay, session, b, null, camera, colour, selected);
        }

        if (_drag == DragKind.MeshMove)
        {
            UpdateMeshDrag(session, edit, camera, viewport, mouse, io.MouseDown[0]);
            DrawCage(overlay, brush, _previewMesh ?? mesh!, edit, camera, -1, -1, (-1, -1));
            DrawGhost(overlay, brush, _previewMesh ?? mesh, session);
            DrawMoveGizmo(overlay, SelectionCentre(brush, _previewMesh ?? mesh!, _dragVertices!), camera, viewProj, viewport, mouse, hover: false);
            return;
        }

        if (_drag != DragKind.None)
        {
            UpdateDrag(session, camera, viewport, mouse, io.MouseDown[0]);
            DrawGhost(overlay, _preview, MeshCommands.MeshOf(session, _preview.Id), session);
            DrawBrush(overlay, session, _preview, null, camera, SelectedColour, true);
            DrawHandles(overlay, _preview, camera, viewProj, viewport, mouse, hover: false, resize: true);
            return;
        }

        _hoverAxis = -1;

        if (editing)
        {
            UpdateMeshEdit(session, edit, brush, mesh!, camera, viewport, viewProj, mouse, mouseFree, io.KeyCtrl, overlay);
            return;
        }

        if (hasPrimary)
            DrawHandles(overlay, brush, camera, viewProj, viewport, mouse, hover: mouseFree, resize: brush.Shape != BrushShape.Mesh);

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

    // ---- mesh editing ----

    private void UpdateMeshEdit(DesignSession session, MeshEditState edit, in HullBrush brush, HullMesh mesh, OrbitCamera camera, Vector2 viewport, in Matrix4x4 viewProj,
        Vector2 mouse, bool mouseFree, bool ctrl, OverlayRenderer overlay)
    {
        var selection = edit.Selection;
        var moved = selection.MovedVertices(mesh);
        var hasGizmo = moved.Count > 0;
        var centre = hasGizmo ? SelectionCentre(brush, mesh, moved) : default;

        if (hasGizmo)
            DrawMoveGizmo(overlay, centre, camera, viewProj, viewport, mouse, hover: mouseFree);

        var hoverVertex = -1;
        var hoverFace = -1;
        var hoverEdge = (-1, -1);

        if (mouseFree && _hoverAxis < 0)
            Hover(brush, mesh, selection.Mode, camera, viewport, viewProj, mouse, out hoverVertex, out hoverEdge, out hoverFace);

        DrawCage(overlay, brush, mesh, edit, camera, hoverVertex, hoverFace, hoverEdge);
        DrawGhost(overlay, brush, mesh, session);

        if (!mouseFree || !ImGui.IsMouseClicked(ImGuiMouseButton.Left, false))
            return;

        if (hasGizmo && _hoverAxis >= 0)
        {
            _drag = DragKind.MeshMove;
            _dragAxis = _hoverAxis;
            _dragOrigin = centre;
            _dragDirection = ShipAxis(_dragAxis);
            _dragBrush = brush;
            _dragVertices = moved;
            _previewMesh = mesh;

            if (!HullPicking.ClosestOnAxis(ToVector3d(camera.Position), camera.Ray(mouse, viewport), _dragOrigin, _dragDirection, out _dragStart))
                _drag = DragKind.None;

            return;
        }

        if (!ctrl)
            selection.Clear();

        switch (selection.Mode)
        {
            case MeshSelectMode.Vertex when hoverVertex >= 0:
                Toggle(selection.Vertices, hoverVertex);
                break;
            case MeshSelectMode.Edge when hoverEdge.Item1 >= 0:
                Toggle(selection.Edges, hoverEdge);
                break;
            case MeshSelectMode.Face when hoverFace >= 0:
                Toggle(selection.Faces, hoverFace);
                break;
        }
    }

    private static void Toggle<T>(HashSet<T> set, T item)
    {
        if (!set.Remove(item))
            set.Add(item);
    }

    private void UpdateMeshDrag(DesignSession session, MeshEditState edit, OrbitCamera camera, Vector2 viewport, Vector2 mouse, bool held)
    {
        var mesh = MeshCommands.MeshOf(session, _dragBrush.Id);

        if (mesh is null)
        {
            _drag = DragKind.None;
            return;
        }

        if (HullPicking.ClosestOnAxis(ToVector3d(camera.Position), camera.Ray(mouse, viewport), _dragOrigin, _dragDirection, out var along))
        {
            var delta = (along - _dragStart) * HullBrush.PositionUnitsPerMetre;
            BrushRotation.Matrix(_dragBrush.Rotation, _matrix);

            // Ship-axis delta into brush-local units (Rᵀ · d), snapped to the 1/64 m mesh grid.
            var d = _dragDirection;
            var lx = Snap((_matrix[0] * d.X + _matrix[3] * d.Y + _matrix[6] * d.Z) * delta);
            var ly = Snap((_matrix[1] * d.X + _matrix[4] * d.Y + _matrix[7] * d.Z) * delta);
            var lz = Snap((_matrix[2] * d.X + _matrix[5] * d.Y + _matrix[8] * d.Z) * delta);
            var builder = new MeshBuilder(mesh);
            MeshOps.Translate(builder, _dragVertices!, lx, ly, lz);

            if (edit.Mirror)
                MeshOps.MirrorX(builder);

            _previewMesh = builder.Build();
        }

        if (held)
            return;

        _drag = DragKind.None;

        if (_previewMesh is not null && !ReferenceEquals(_previewMesh, mesh))
            MeshCommands.Replace(session, _dragBrush.Id, _previewMesh);

        _previewMesh = null;
    }

    private static int Snap(double units) => (int)Math.Round(units / MeshSnapUnits) * MeshSnapUnits;

    private Vector3d SelectionCentre(in HullBrush brush, HullMesh mesh, HashSet<int> vertices)
    {
        double x = 0, y = 0, z = 0;
        var n = 0;

        foreach (var v in vertices)
        {
            if (v >= mesh.Vertices.Length)
                continue;

            var p = mesh.Vertices[v];
            (x, y, z) = (x + p.X, y + p.Y, z + p.Z);
            n++;
        }

        if (n == 0)
            return new Vector3d(brush.PositionX, brush.PositionY, brush.PositionZ);

        BrushRotation.Matrix(brush.Rotation, _matrix);
        return MeshToShip(brush, x / n, y / n, z / n);
    }

    private Vector3d MeshToShip(in HullBrush brush, double x, double y, double z)
    {
        const double k = HullBrush.MetresPerPositionUnit;
        var m = _matrix;
        x *= k;
        y *= k;
        z *= k;
        return new Vector3d(brush.PositionX + m[0] * x + m[1] * y + m[2] * z, brush.PositionY + m[3] * x + m[4] * y + m[5] * z, brush.PositionZ + m[6] * x + m[7] * y + m[8] * z);
    }

    private Vector3d MeshPoint(in HullBrush brush, MeshVertex v) => MeshToShip(brush, v.X, v.Y, v.Z);

    /// <summary>Element under the cursor: nearest projected vertex or edge within a few pixels, or the nearest face the ray hits.</summary>
    private void Hover(in HullBrush brush, HullMesh mesh, MeshSelectMode mode, OrbitCamera camera, Vector2 viewport, in Matrix4x4 viewProj, Vector2 mouse,
        out int vertex, out (int, int) edge, out int face)
    {
        vertex = -1;
        edge = (-1, -1);
        face = -1;
        BrushRotation.Matrix(brush.Rotation, _matrix);
        var faceDistance = FaceUnder(brush, mesh, camera, viewport, mouse, out face);

        if (mode == MeshSelectMode.Vertex)
        {
            var best = HandlePixels;

            for (var v = 0; v < mesh.Vertices.Length; v++)
            {
                var p = MeshPoint(brush, mesh.Vertices[v]);

                if (!camera.Project(p, viewProj, viewport, out var pixel) || Hidden(camera, p, faceDistance))
                    continue;

                var d = Vector2.Distance(pixel, mouse);

                if (d < best)
                {
                    best = d;
                    vertex = v;
                }
            }

            face = -1;
        }
        else if (mode == MeshSelectMode.Edge)
        {
            var best = EdgePixels;

            for (var f = 0; f < mesh.FaceCount; f++)
            {
                var corners = mesh.Face(f);

                for (var i = 0; i < corners.Length; i++)
                {
                    int a = corners[i], b = corners[(i + 1) % corners.Length];

                    if (a > b)
                        continue;

                    var pa = MeshPoint(brush, mesh.Vertices[a]);
                    var pb = MeshPoint(brush, mesh.Vertices[b]);
                    var mid = new Vector3d((pa.X + pb.X) * 0.5, (pa.Y + pb.Y) * 0.5, (pa.Z + pb.Z) * 0.5);

                    if (!camera.Project(pa, viewProj, viewport, out var sa) || !camera.Project(pb, viewProj, viewport, out var sb) || Hidden(camera, mid, faceDistance))
                        continue;

                    var d = SegmentDistance(mouse, sa, sb);

                    if (d < best)
                    {
                        best = d;
                        edge = (a, b);
                    }
                }
            }

            face = -1;
        }
    }

    /// <summary>Behind the visible cage surface by more than a little: not pickable.</summary>
    private static bool Hidden(OrbitCamera camera, Vector3d p, double faceDistance)
    {
        var c = camera.Position;
        var d = Math.Sqrt((p.X - c.X) * (p.X - c.X) + (p.Y - c.Y) * (p.Y - c.Y) + (p.Z - c.Z) * (p.Z - c.Z));
        return d > faceDistance + 0.05;
    }

    private static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var t = ab.LengthSquared() > 0 ? Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0f, 1f) : 0f;
        return Vector2.Distance(p, a + ab * t);
    }

    /// <summary>Nearest cage face under the cursor (control mesh, level 0) and its distance; MaxValue when none.</summary>
    private double FaceUnder(in HullBrush brush, HullMesh mesh, OrbitCamera camera, Vector2 viewport, Vector2 mouse, out int face)
    {
        face = -1;
        var ray = camera.Ray(mouse, viewport);
        var c = camera.Position;
        var m = _matrix;
        double ox = (c.X - brush.PositionX) * HullBrush.PositionUnitsPerMetre, oy = (c.Y - brush.PositionY) * HullBrush.PositionUnitsPerMetre, oz = (c.Z - brush.PositionZ) * HullBrush.PositionUnitsPerMetre;
        var o = new Vector3d(m[0] * ox + m[3] * oy + m[6] * oz, m[1] * ox + m[4] * oy + m[7] * oz, m[2] * ox + m[5] * oy + m[8] * oz);
        var d = new Vector3d(m[0] * ray.X + m[3] * ray.Y + m[6] * ray.Z, m[1] * ray.X + m[4] * ray.Y + m[7] * ray.Z, m[2] * ray.X + m[5] * ray.Y + m[8] * ray.Z);
        var best = double.MaxValue;

        for (var f = 0; f < mesh.FaceCount; f++)
        {
            _triangles.Clear();
            MeshSubdivision.Triangulate(mesh.Vertices, Ints(mesh.Face(f)), _triangles);

            for (var t = 0; t < _triangles.Count; t += 3)
            {
                var a = ToVector3(mesh.Vertices[_triangles[t]]);
                var b = ToVector3(mesh.Vertices[_triangles[t + 1]]);
                var cc = ToVector3(mesh.Vertices[_triangles[t + 2]]);

                if (PreparedMesh.Watertight(o, d, a, b, cc, out var hit) && hit >= 0 && hit < best)
                {
                    best = hit;
                    face = f;
                }
            }
        }

        return best == double.MaxValue ? double.MaxValue : best / HullBrush.PositionUnitsPerMetre;
    }

    private static int[] Ints(ReadOnlySpan<ushort> corners)
    {
        var result = new int[corners.Length];

        for (var i = 0; i < corners.Length; i++)
            result[i] = corners[i];

        return result;
    }

    private static Vector3 ToVector3(MeshVertex v) => new(v.X, v.Y, v.Z);

    private void DrawCage(OverlayRenderer overlay, in HullBrush brush, HullMesh mesh, MeshEditState edit, OrbitCamera camera, int hoverVertex, int hoverFace, (int, int) hoverEdge)
    {
        BrushRotation.Matrix(brush.Rotation, _matrix);
        var selection = edit.Selection;
        var scale = camera.Distance * 0.0025;

        for (var f = 0; f < mesh.FaceCount; f++)
        {
            var corners = mesh.Face(f);
            var picked = selection.Mode == MeshSelectMode.Face && selection.Faces.Contains(f);

            if (picked || f == hoverFace)
            {
                _triangles.Clear();
                MeshSubdivision.Triangulate(mesh.Vertices, Ints(corners), _triangles);
                var colour = OverlayRenderer.WithAlpha(picked ? PickColour : HoverColour, 110);

                for (var t = 0; t < _triangles.Count; t += 3)
                    overlay.Triangle(MeshPoint(brush, mesh.Vertices[_triangles[t]]), MeshPoint(brush, mesh.Vertices[_triangles[t + 1]]), MeshPoint(brush, mesh.Vertices[_triangles[t + 2]]), colour);
            }

            for (var i = 0; i < corners.Length; i++)
            {
                int a = corners[i], b = corners[(i + 1) % corners.Length];

                if (a > b)
                    continue;

                var pa = MeshPoint(brush, mesh.Vertices[a]);
                var pb = MeshPoint(brush, mesh.Vertices[b]);
                var edge = (a, b);

                if (selection.Mode == MeshSelectMode.Edge && selection.Edges.Contains(edge))
                    overlay.Ribbon(pa, pb, scale * 2.5, PickColour);
                else if (edge == hoverEdge)
                    overlay.Ribbon(pa, pb, scale * 2.5, HoverColour);
                else if (mesh.IsCrease(a, b))
                    overlay.Ribbon(pa, pb, scale, CageColour);
                else
                    overlay.Line(pa, pb, CageColour);
            }
        }

        if (selection.Mode != MeshSelectMode.Vertex)
            return;

        for (var v = 0; v < mesh.Vertices.Length; v++)
        {
            var picked = selection.Vertices.Contains(v);
            overlay.Cube(MeshPoint(brush, mesh.Vertices[v]), scale * (picked || v == hoverVertex ? 2.5 : 1.5), picked ? PickColour : v == hoverVertex ? HoverColour : CageColour);
        }
    }

    // ---- guides: front arrow, mirror planes ----

    /// <summary>
    /// The front arrow (gold, along the ship's front axis, from the origin to past the hull's nose, labelled) and a
    /// translucent quad per enabled mirror plane, sized to the hull's bounds.
    /// </summary>
    private void DrawGuides(DesignSession session, OrbitCamera camera, Vector2 viewport, in Matrix4x4 viewProj, OverlayRenderer overlay)
    {
        var (min, max) = HullBounds(session);
        var front = Math.Max(max.Z, MinGuideHalf) + GuideMargin;
        var length = Math.Max(4.0, front * 0.35);
        var tail = new Vector3d(0, 0, front);
        var neck = new Vector3d(0, 0, front + length * 0.75);
        var tip = new Vector3d(0, 0, front + length);
        var width = Math.Max(0.15, length * 0.05);
        overlay.Ribbon(new Vector3d(0, 0, 0), tail, width * 0.3, OverlayRenderer.WithAlpha(FrontColour, 120));
        overlay.Ribbon(tail, neck, width, FrontColour);
        overlay.Cone(neck, tip, width * 2.5, FrontColour);

        // Label just past the tip along the arrow's screen direction, so the cone never covers it.
        if (camera.Project(tip, viewProj, viewport, out var label) && camera.Project(neck, viewProj, viewport, out var back))
        {
            var along = label - back;
            along = along.LengthSquared() > 1e-6f ? Vector2.Normalize(along) : Vector2.UnitX;
            var at = label + along * 22f - new Vector2(18f, 7f);
            ImGui.GetBackgroundDrawList(ImGui.GetMainViewport()).AddText(new float2(at.X, at.Y), FrontLabel, "FRONT"u8);
        }

        var symmetry = session.Symmetry;

        if (!symmetry.Any)
            return;

        var hx = Math.Max(Math.Max(-min.X, max.X), MinGuideHalf) + GuideMargin;
        var hy = Math.Max(Math.Max(-min.Y, max.Y), MinGuideHalf) + GuideMargin;
        var hz = Math.Max(Math.Max(-min.Z, max.Z), MinGuideHalf) + GuideMargin;

        if (symmetry.X)
            Plane(overlay, new Vector3d(0, -hy, -hz), new Vector3d(0, hy, -hz), new Vector3d(0, hy, hz), new Vector3d(0, -hy, hz), AxisColours[0]);

        if (symmetry.Y)
            Plane(overlay, new Vector3d(-hx, 0, -hz), new Vector3d(-hx, 0, hz), new Vector3d(hx, 0, hz), new Vector3d(hx, 0, -hz), AxisColours[1]);

        if (symmetry.Z)
            Plane(overlay, new Vector3d(-hx, -hy, 0), new Vector3d(hx, -hy, 0), new Vector3d(hx, hy, 0), new Vector3d(-hx, hy, 0), AxisColours[2]);
    }

    private static void Plane(OverlayRenderer overlay, Vector3d a, Vector3d b, Vector3d c, Vector3d d, uint colour)
    {
        var fill = OverlayRenderer.WithAlpha(colour, 28);
        overlay.Triangle(a, b, c, fill);
        overlay.Triangle(a, c, d, fill);
        var edge = OverlayRenderer.WithAlpha(colour, 160);
        overlay.Line(a, b, edge);
        overlay.Line(b, c, edge);
        overlay.Line(c, d, edge);
        overlay.Line(d, a, edge);
    }

    private static (Vector3d Min, Vector3d Max) HullBounds(DesignSession session)
    {
        double x0 = 0, y0 = 0, z0 = 0, x1 = 0, y1 = 0, z1 = 0;

        foreach (ref readonly var b in session.Doc.Brushes.AsSpan())
        {
            BrushGeometry.Bounds(b, out var min, out var max);
            (x0, y0, z0) = (Math.Min(x0, min.X), Math.Min(y0, min.Y), Math.Min(z0, min.Z));
            (x1, y1, z1) = (Math.Max(x1, max.X), Math.Max(y1, max.Y), Math.Max(z1, max.Z));
        }

        return (new Vector3d(x0, y0, z0), new Vector3d(x1, y1, z1));
    }

    // ---- brushes, ghosts, gizmos ----

    /// <summary>The translucent ghost of a brush (primitive or mesh cage): what the drag or edit will commit.</summary>
    private void DrawGhost(OverlayRenderer overlay, in HullBrush brush, HullMesh? mesh, DesignSession session)
    {
        var colour = brush.Op == BrushOp.Subtract ? OverlayRenderer.Rgba(240, 80, 70, 70) : OverlayRenderer.Rgba(120, 200, 255, 60);
        BrushRotation.Matrix(brush.Rotation, _matrix);

        if (brush.Shape == BrushShape.Mesh)
        {
            if (mesh is null)
                return;

            for (var f = 0; f < mesh.FaceCount; f++)
            {
                _triangles.Clear();
                MeshSubdivision.Triangulate(mesh.Vertices, Ints(mesh.Face(f)), _triangles);

                for (var t = 0; t < _triangles.Count; t += 3)
                    overlay.Triangle(MeshPoint(brush, mesh.Vertices[_triangles[t]]), MeshPoint(brush, mesh.Vertices[_triangles[t + 1]]), MeshPoint(brush, mesh.Vertices[_triangles[t + 2]]), colour);
            }

            return;
        }

        var primitive = MeshPrimitives.FromBrush(brush);

        for (var f = 0; f < primitive.FaceCount; f++)
        {
            _triangles.Clear();
            MeshSubdivision.Triangulate(primitive.Vertices, Ints(primitive.Face(f)), _triangles);

            for (var t = 0; t < _triangles.Count; t += 3)
                overlay.Triangle(MeshPoint(brush, primitive.Vertices[_triangles[t]]), MeshPoint(brush, primitive.Vertices[_triangles[t + 1]]), MeshPoint(brush, primitive.Vertices[_triangles[t + 2]]), colour);
        }
    }

    private void DrawMoveGizmo(OverlayRenderer overlay, Vector3d centre, OrbitCamera camera, in Matrix4x4 viewProj, Vector2 viewport, Vector2 mouse, bool hover)
    {
        var length = Math.Max(0.5, camera.Depth(centre) * GizmoScreenFraction);
        var best = HandlePixels;
        var hasCentre = camera.Project(centre, viewProj, viewport, out var c);

        for (var axis = 0; axis < 3; axis++)
        {
            var d = ShipAxis(axis);
            var tip = new Vector3d(centre.X + d.X * length, centre.Y + d.Y * length, centre.Z + d.Z * length);
            var neck = new Vector3d(centre.X + d.X * length * 0.8, centre.Y + d.Y * length * 0.8, centre.Z + d.Z * length * 0.8);
            var active = (_drag is DragKind.Move or DragKind.MeshMove && _dragAxis == axis) || (_hoverKind is DragKind.Move or DragKind.MeshMove && _hoverAxis == axis);
            var colour = active ? HoverColour : AxisColours[axis];
            overlay.Ribbon(centre, neck, length * (active ? 0.035 : 0.025), colour);
            overlay.Cone(neck, tip, length * (active ? 0.08 : 0.06), colour);

            // The whole arrow is a handle: distance to its screen segment, skipping the shared centre.
            if (hover && hasCentre && camera.Project(tip, viewProj, viewport, out var t))
            {
                var distance = SegmentDistance(mouse, Vector2.Lerp(c, t, 0.2f), t);

                if (distance < best)
                {
                    best = distance;
                    (_hoverAxis, _hoverKind) = (axis, DragKind.Move);
                }
            }
        }
    }

    private void DrawHandles(OverlayRenderer overlay, in HullBrush brush, OrbitCamera camera, in Matrix4x4 viewProj, Vector2 viewport, Vector2 mouse, bool hover, bool resize)
    {
        var centre = new Vector3d(brush.PositionX, brush.PositionY, brush.PositionZ);
        DrawMoveGizmo(overlay, centre, camera, viewProj, viewport, mouse, hover);

        if (!resize)
            return;

        var length = Math.Max(0.5, camera.Depth(centre) * GizmoScreenFraction);
        var half = BrushGeometry.Half(brush);
        var best = _hoverAxis >= 0 ? 0f : HandlePixels;

        for (var axis = 0; axis < 3; axis++)
        {
            if (brush.Shape == BrushShape.Cylinder && axis == 2)
                continue;

            var d = LocalAxis(brush, axis);
            var reach = (axis == 0 ? half.X : axis == 1 ? half.Y : half.Z) + 0.03 * length;
            var face = new Vector3d(centre.X + d.X * reach, centre.Y + d.Y * reach, centre.Z + d.Z * reach);
            var active = (_drag == DragKind.Resize && _dragAxis == axis) || (_hoverKind == DragKind.Resize && _hoverAxis == axis);
            overlay.Cube(face, length * (active ? 0.05 : 0.035), active ? HoverColour : AxisColours[axis]);

            if (hover && camera.Project(face, viewProj, viewport, out var f) && Vector2.Distance(mouse, f) < best)
            {
                best = Vector2.Distance(mouse, f);
                (_hoverAxis, _hoverKind) = (axis, DragKind.Resize);
            }
        }
    }

    private void DrawBrush(OverlayRenderer overlay, DesignSession session, in HullBrush brush, HullMesh? meshOverride, OrbitCamera camera, uint colour, bool thick)
    {
        BrushRotation.Matrix(brush.Rotation, _matrix);
        var width = thick ? camera.Distance * 0.0025 : 0.0;
        var mesh = brush.Shape == BrushShape.Mesh ? meshOverride ?? MeshCommands.MeshOf(session, brush.Id) : null;

        if (brush.Shape == BrushShape.Mesh)
        {
            if (mesh is null)
                return;

            for (var f = 0; f < mesh.FaceCount; f++)
            {
                var corners = mesh.Face(f);

                for (var i = 0; i < corners.Length; i++)
                {
                    int a = corners[i], b = corners[(i + 1) % corners.Length];

                    if (a < b)
                        Edge(overlay, MeshPoint(brush, mesh.Vertices[a]), MeshPoint(brush, mesh.Vertices[b]), colour, width);
                }
            }

            return;
        }

        var h = BrushGeometry.Half(brush);
        var c = new Vector3d(brush.PositionX, brush.PositionY, brush.PositionZ);

        if (brush.Shape == BrushShape.Cylinder)
        {
            for (var s = 0; s < CircleSegments; s++)
            {
                var a0 = s * Math.Tau / CircleSegments;
                var a1 = (s + 1) * Math.Tau / CircleSegments;
                double x0 = Math.Cos(a0) * h.X, z0 = Math.Sin(a0) * h.X, x1 = Math.Cos(a1) * h.X, z1 = Math.Sin(a1) * h.X;
                Edge(overlay, Transform(c, x0, h.Y, z0), Transform(c, x1, h.Y, z1), colour, width);
                Edge(overlay, Transform(c, x0, -h.Y, z0), Transform(c, x1, -h.Y, z1), colour, width);

                if (s % 6 == 0)
                    Edge(overlay, Transform(c, x0, h.Y, z0), Transform(c, x0, -h.Y, z0), colour, width);
            }

            return;
        }

        Span<Vector3d> corners8 = stackalloc Vector3d[8];

        for (var i = 0; i < 8; i++)
            corners8[i] = Transform(c, (i & 1) != 0 ? h.X : -h.X, (i & 2) != 0 ? h.Y : -h.Y, (i & 4) != 0 ? h.Z : -h.Z);

        // Bit 0 = +X, bit 1 = +Y, bit 2 = +Z. The wedge's top front corners (+Y, +Z) fold onto the top back ones.
        if (brush.Shape == BrushShape.Wedge)
        {
            corners8[2 | 4] = corners8[2];
            corners8[1 | 2 | 4] = corners8[1 | 2];
        }

        ReadOnlySpan<byte> edges = [0, 1, 2, 3, 4, 5, 6, 7, 0, 2, 1, 3, 4, 6, 5, 7, 0, 4, 1, 5, 2, 6, 3, 7];

        for (var e = 0; e < edges.Length; e += 2)
            Edge(overlay, corners8[edges[e]], corners8[edges[e + 1]], colour, width);
    }

    private static void Edge(OverlayRenderer overlay, Vector3d a, Vector3d b, uint colour, double width)
    {
        if (width > 0)
            overlay.Ribbon(a, b, width, colour);
        else
            overlay.Line(a, b, colour);
    }

    private Vector3d Transform(Vector3d centre, double x, double y, double z)
    {
        var m = _matrix;
        return new Vector3d(centre.X + m[0] * x + m[1] * y + m[2] * z, centre.Y + m[3] * x + m[4] * y + m[5] * z, centre.Z + m[6] * x + m[7] * y + m[8] * z);
    }

    private Vector3d LocalAxis(in HullBrush brush, int axis)
    {
        BrushRotation.Matrix(brush.Rotation, _matrix);
        return new Vector3d(_matrix[axis], _matrix[3 + axis], _matrix[6 + axis]);
    }

    private static Vector3d ShipAxis(int axis) => axis == 0 ? new Vector3d(1, 0, 0) : axis == 1 ? new Vector3d(0, 1, 0) : new Vector3d(0, 0, 1);

    private static Vector3d ToVector3d(double3 v) => new(v.X, v.Y, v.Z);
}
