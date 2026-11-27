using System.Numerics;
using Brutal.ImGuiApi;
using BrutalGame.Ships;

namespace BrutalGame.Designer.Ui;

/// <summary>
/// Draws brush strokes onto the hull and edits them in the viewport. Draw mode: each left click on the hull adds a point
/// (on the surface, with its normal); Enter or a double click finishes, Esc cancels. With a stroke selected its control
/// points show as handles: drag one along the hull to move it (one undo step on release). Every stroke's line is drawn
/// where the last bake stuck it to the hull.
/// </summary>
public sealed class StrokeTool
{
    private const float HandlePixels = 10f;
    private const double HandleHalf = 0.08;
    private const double LiftMetres = 0.02;

    private static readonly uint AddLine = OverlayRenderer.Rgba(90, 230, 200, 255);
    private static readonly uint DrawLine = OverlayRenderer.Rgba(255, 196, 40, 255);
    private static readonly uint HandleColour = OverlayRenderer.Rgba(255, 255, 255, 255);
    private static readonly uint HoverHandle = OverlayRenderer.Rgba(255, 196, 40, 255);

    private readonly List<StrokePoint> _pending = new();
    private HullSurface? _surface;
    private int _surfaceVersion = -1;
    private int _dragPoint = -1;
    private StrokePoint _dragValue;
    private bool _dragHit;

    /// <summary>Draw mode: clicks on the hull add points to a new stroke.</summary>
    public bool Drawing;

    /// <summary>Brush index new strokes use.</summary>
    public int Brush = -1;

    /// <summary>Catalog index of the material new strokes use.</summary>
    public int Material;

    /// <summary>The selected stroke's id, or 0.</summary>
    public uint Selected;

    /// <summary>The selected stroke's point last grabbed in the view (its size is edited in the panel), or -1: then the whole stroke has the gizmo.</summary>
    public int Point = -1;

    private readonly TransformGizmo _gizmo = new();
    private uint _lastSelected;

    /// <summary>Result of the last expansion (the host sets it after each bake starts): stuck paths to draw.</summary>
    public ExpandedHull? Expanded;

    public string Message = "";

    public int PendingPoints => _pending.Count;

    public bool Dragging => _dragPoint >= 0;

    /// <summary>Asks the host to finish the pending stroke (it needs the catalog for the material).</summary>
    public bool FinishRequested { get; private set; }

    public IReadOnlyList<StrokePoint> Pending => _pending;

    public void Cancel()
    {
        _pending.Clear();
        Drawing = false;
        FinishRequested = false;
    }

    public void ClearPending()
    {
        _pending.Clear();
        FinishRequested = false;
    }

    /// <summary>Draws strokes and handles input; true when it used the mouse this frame (the solid tools then skip it).</summary>
    public bool Update(DesignSession session, OrbitCamera camera, Vector2 viewport, in Matrix4x4 viewProj, bool mouseFree, OverlayRenderer overlay)
    {
        FinishRequested = false;
        var set = session.Doc.Brushes;

        if (Selected != 0 && (set is null || set.IndexOfStroke(Selected) < 0))
            Selected = 0;

        if (_surfaceVersion != session.Version)
        {
            _surface = new HullSurface(session.Doc.Solids.AsSpan(), session.Doc.Meshes);
            _surfaceVersion = session.Version;
        }

        DrawPaths(set, overlay);
        var io = ImGui.GetIO();
        var mouse = new Vector2(io.MousePos.X, io.MousePos.Y);
        var p = camera.Position;
        var origin = new Vector3d(p.X, p.Y, p.Z);
        var ray = camera.Ray(mouse, viewport);

        if (Drawing)
            return UpdateDrawing(camera, origin, ray, mouseFree, overlay);

        // A stroke picked anew (here or in the tree) starts whole.
        if (Selected != _lastSelected)
            (_lastSelected, Point) = (Selected, -1);

        if (set is null || Expanded is null)
            return false;

        if (Selected != 0 && Point < 0 && UpdateWhole(session, set, camera, viewport, viewProj, mouse, mouseFree, io.MouseDown[0], overlay))
            return true;

        if (Selected != 0 && UpdateHandles(session, set, camera, viewport, viewProj, origin, ray, mouse, mouseFree, io.MouseDown[0], overlay))
            return true;

        // A click on a stroke's line picks the whole stroke.
        if (!mouseFree || !ImGui.IsMouseClicked(ImGuiMouseButton.Left, false))
            return false;

        var picked = 0u;
        var best = WholeTransform.LinePixels;

        for (var s = 0; s < set.Strokes.Count && s < Expanded.Paths.Count; s++)
        {
            var path = Expanded.Paths[s];

            for (var i = 1; i < path.Points.Count; i++)
            {
                if (WholeTransform.ToLine(camera, viewProj, viewport, mouse, path.Points[i - 1], path.Points[i]) is var d && d < best)
                    (best, picked) = (d, set.Strokes[s].Id);
            }
        }

        if (picked == 0)
            return false;

        (Selected, _lastSelected, Point) = (picked, picked, -1);
        return true;
    }

    /// <summary>The whole selected stroke: every point moves, or turns (with its normal) about the middle of its points.</summary>
    private bool UpdateWhole(DesignSession session, HullBrushSet set, OrbitCamera camera, Vector2 viewport, in Matrix4x4 viewProj, Vector2 mouse, bool mouseFree, bool mouseDown, OverlayRenderer overlay)
    {
        var index = set.IndexOfStroke(Selected);

        if (index < 0)
            return false;

        var stroke = set.Strokes[index];
        var points = new Vector3d[stroke.Points.Count];

        for (var i = 0; i < points.Length; i++)
            points[i] = stroke.Points[i].Position;

        var centre = WholeTransform.Centre(points);

        if (WholeTransform.Update(_gizmo, centre, true, camera, viewProj, viewport, mouse, mouseFree, mouseDown, ImGui.IsMouseClicked(ImGuiMouseButton.Left, false), session.Snap.StepMetres, overlay))
        {
            Span<double> m = stackalloc double[9];
            WholeTransform.Turn(_gizmo, m);
            var moved = new StrokePoint[points.Length];

            for (var i = 0; i < points.Length; i++)
            {
                var p = stroke.Points[i];
                moved[i] = StrokePoint.From(WholeTransform.Apply(_gizmo, m, centre, p.Position), WholeTransform.Direction(_gizmo, m, p.Normal)) with { Scale = p.Scale };
            }

            BrushCommands.SetStroke(session, Selected, s =>
            {
                for (var i = 0; i < moved.Length && i < s.Points.Count; i++)
                    s.Points[i] = moved[i];
            });
        }

        return _gizmo.Dragging || _gizmo.Hovered;
    }

    private bool UpdateDrawing(OrbitCamera camera, Vector3d origin, Vector3d ray, bool mouseFree, OverlayRenderer overlay)
    {
        var hit = Hit(origin, ray, out var point, out var normal);

        for (var i = 1; i < _pending.Count; i++)
            overlay.Line(Lift(_pending[i - 1]), Lift(_pending[i]), DrawLine);

        foreach (var q in _pending)
            overlay.Cube(Lift(q), HandleHalf, DrawLine);

        if (hit && mouseFree)
        {
            var at = HullSurface.Add(point, HullSurface.Scale(normal, LiftMetres));
            overlay.Cube(at, HandleHalf, HoverHandle);
            overlay.Line(at, HullSurface.Add(at, HullSurface.Scale(normal, 0.5)), HoverHandle);

            if (_pending.Count > 0)
                overlay.Line(Lift(_pending[^1]), at, HoverHandle);
        }

        if (ImGui.IsKeyPressed(ImGuiKey.Escape, false))
        {
            Cancel();
            return mouseFree;
        }

        if (ImGui.IsKeyPressed(ImGuiKey.Enter, false) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, false))
            FinishRequested = true;

        if (!mouseFree)
            return false;

        if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        {
            FinishRequested = true;
            return true;
        }

        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left, false))
        {
            if (!hit)
                Message = "Click on the hull";
            else if (_pending.Count >= HullStroke.MaxPoints)
                Message = $"A stroke has at most {HullStroke.MaxPoints} points";
            else
            {
                _pending.Add(StrokePoint.From(point, normal));
                Message = "";
            }
        }

        return true;
    }

    private bool UpdateHandles(DesignSession session, HullBrushSet set, OrbitCamera camera, Vector2 viewport, in Matrix4x4 viewProj,
        Vector3d origin, Vector3d ray, Vector2 mouse, bool mouseFree, bool mouseDown, OverlayRenderer overlay)
    {
        var index = set.IndexOfStroke(Selected);

        if (index >= Expanded!.Paths.Count)
            return false;

        var stroke = set.Strokes[index];
        var path = Expanded.Paths[index];

        if (_dragPoint >= 0)
        {
            if (Hit(origin, ray, out var point, out var normal))
            {
                _dragValue = StrokePoint.From(point, normal) with { Scale = _dragValue.Scale };
                _dragHit = true;
            }

            overlay.Cube(Lift(_dragValue), HandleHalf * 1.4, HoverHandle);

            if (!mouseDown)
            {
                var i = _dragPoint;
                var value = _dragValue;
                _dragPoint = -1;

                if (_dragHit && i < stroke.Points.Count)
                    BrushCommands.SetStroke(session, Selected, s => s.Points[i] = value);
            }

            return true;
        }

        var hover = -1;
        var best = HandlePixels;

        for (var c = 0; c < path.Controls.Count && c < stroke.Points.Count; c++)
        {
            var at = path.Points[path.Controls[c]];

            if (camera.Project(at, viewProj, viewport, out var pixel))
            {
                var d = Vector2.Distance(pixel, mouse);

                if (d < best)
                {
                    best = d;
                    hover = c;
                }
            }
        }

        for (var c = 0; c < path.Controls.Count; c++)
        {
            var at = HullSurface.Add(path.Points[path.Controls[c]], HullSurface.Scale(path.Normals[path.Controls[c]], LiftMetres));
            overlay.Cube(at, HandleHalf, c == hover && mouseFree ? HoverHandle : HandleColour);
        }

        if (!mouseFree || hover < 0)
            return false;

        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left, false))
        {
            _dragPoint = hover;
            _dragValue = stroke.Points[hover];
            _dragHit = false;
            Point = hover;
        }

        return true;
    }

    private void DrawPaths(HullBrushSet? set, OverlayRenderer overlay)
    {
        if (set is null || Expanded is null)
            return;

        for (var s = 0; s < set.Strokes.Count && s < Expanded.Paths.Count; s++)
        {
            var path = Expanded.Paths[s];
            var selected = set.Strokes[s].Id == Selected;
            var colour = AddLine;

            if (!selected)
                colour = OverlayRenderer.WithAlpha(colour, 150);

            for (var i = 1; i < path.Points.Count; i++)
            {
                var a = HullSurface.Add(path.Points[i - 1], HullSurface.Scale(path.Normals[i - 1], LiftMetres));
                var b = HullSurface.Add(path.Points[i], HullSurface.Scale(path.Normals[i], LiftMetres));

                if (selected)
                    overlay.Ribbon(a, b, 0.04, colour);
                else
                    overlay.Line(a, b, colour);
            }
        }
    }

    private bool Hit(Vector3d origin, Vector3d ray, out Vector3d point, out Vector3d normal)
    {
        point = default;
        normal = default;

        if (_surface is null || !_surface.Raycast(origin, ray, Tools.HullPicking.MaxRayMetres, out var t, out normal))
            return false;

        point = HullSurface.Add(origin, HullSurface.Scale(ray, t));
        return true;
    }

    private static Vector3d Lift(StrokePoint p) => HullSurface.Add(p.Position, HullSurface.Scale(p.Normal, LiftMetres));
}
