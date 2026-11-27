using System.Numerics;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using BrutalGame.Designer.Planets;
using BrutalGame.DesignKit.Ui;
using BrutalGame.Terrain;

namespace BrutalGame.Designer.App.Planets;

public enum PlanetTool : byte
{
    Look,
    Paint,
    Site,
}

/// <summary>
/// A planet tab's tools and its Planet panel (planet-designer-brief.md §6): the height brush (raise, lower, level, erase
/// with radius, height and strength), sites (Shift + click places, drag moves, core, blend and height in the kit's
/// inspector) and the selected site's marker. Rings draw in the kit's 3D overlay, labels in ImGui. A stroke or drag is one undo step; the
/// terrain refreshes when it ends.
/// </summary>
public sealed class PlanetTools
{
    private const float DabSpacing = 0.25f;
    private const int RingPoints = 96;
    private const double RingLift = 3.0;

    private static readonly uint BrushColour = OverlayRenderer.Rgba(255, 170, 60);
    private static readonly uint CoreColour = OverlayRenderer.Rgba(90, 230, 200);
    private static readonly uint BlendColour = OverlayRenderer.Rgba(90, 230, 200, 120);
    private static readonly uint SelectedColour = OverlayRenderer.Rgba(255, 255, 255);
    private static readonly ImColor8 LabelColour = new(255, 255, 255, 230);

    private static readonly string[] ToolNames = ["Look", "Paint height", "Sites"];
    private static readonly string[] BrushNames = ["Raise", "Lower", "Level", "Erase"];
    private static readonly string[] MarkerTypes = ["city", "outpost"];

    private readonly PlanetDesignSession _session;
    private readonly Inspector _inspector = new();
    private readonly ImInputString _markerName = new(48, "New settlement"u8);
    private int _brush;
    private double _radiusKm = 8.0;
    private double _metres = 200.0;
    private double _strength = 0.5;
    private double3 _lastDab;
    private string? _selected;
    private bool _dragging;
    private double3 _dragTo;
    private int _markerType;

    public PlanetTools(PlanetDesignSession session) => _session = session;

    public PlanetTool Tool;

    /// <summary>The surface point under the cursor this frame, if any.</summary>
    public double3? Hover;

    /// <summary>A stroke or a site drag owns the left mouse.</summary>
    public bool Busy => _session.Stroking || _dragging;

    /// <summary>Per frame with the cursor's surface point (null off the body) and whether the mouse is over the 3D view.</summary>
    public void Update(double radius, double3? hit, bool mouseFree)
    {
        Hover = hit;

        if (Tool == PlanetTool.Paint)
            Paint(hit, mouseFree, radius);
        else if (Tool == PlanetTool.Site)
            Sites(hit, mouseFree, radius);
    }

    private void Paint(double3? hit, bool mouseFree, double radius)
    {
        if (_session.Stroking && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            _session.EndStroke();
            return;
        }

        if (hit is not { } h)
            return;

        var d = double3.Normalize(h);

        if (!_session.Stroking)
        {
            if (!mouseFree || !ImGui.IsMouseClicked(ImGuiMouseButton.Left, false))
                return;

            _session.BeginStroke();
            Dab(d);
            return;
        }

        if ((d - _lastDab).Length() * radius >= DabSpacing * _radiusKm * 1000f)
            Dab(d);
    }

    private void Dab(double3 d)
    {
        var (brush, metres) = _brush switch
        {
            0 => (AuthoredBrush.Raise, _metres),
            1 => (AuthoredBrush.Raise, -_metres),
            2 => (AuthoredBrush.Level, _metres),
            _ => (AuthoredBrush.Erase, 0.0),
        };

        _session.Dab(d, _radiusKm * 1000.0, (float)metres, brush, (float)_strength);
        _lastDab = d;
    }

    private void Sites(double3? hit, bool mouseFree, double radius)
    {
        if (_dragging)
        {
            if (hit is { } h)
                _dragTo = double3.Normalize(h);

            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                _dragging = false;

                if (Site(_selected!) is { } site && double3.Dot(_dragTo, double3.Normalize(site.Position)) < 1.0 - 1e-14)
                    _session.SetSite(site.Id, _dragTo, site.CoreM, site.BlendM, _session.SiteHeight(_dragTo));
            }

            return;
        }

        if (!mouseFree || hit is not { } point || !ImGui.IsMouseClicked(ImGuiMouseButton.Left, false))
            return;

        var d = double3.Normalize(point);

        foreach (var s in _session.Layer.Sites)
        {
            if ((double3.Normalize(s.Position) - d).Length() * radius < s.CoreM)
            {
                (_selected, _dragging, _dragTo) = (s.Id, true, d);
                return;
            }
        }

        _selected = ImGui.GetIO().KeyShift ? _session.AddSite(d) : null;
    }

    private AuthoredSite? Site(string id)
    {
        foreach (var s in _session.Layer.Sites)
        {
            if (s.Id == id)
                return s;
        }

        return null;
    }

    public void DrawPanel(string body, uint dock, float refreshMs, (float Ms, float MainMs, int Leaves) replace, int pendingJobs)
    {
        ImGui.SetNextWindowDockID(new ImGuiID(dock), ImGuiCond.FirstUseEver);

        if (!ImGui.Begin("Planet"u8))
        {
            ImGui.End();
            return;
        }

        var layer = _session.Layer;
        ImGui.Text($"Body {body}  kind {layer.Kind}");
        ImGui.Text($"Version {layer.Version}{(_session.Modified ? " (unsaved)" : "")}  {layer.Sites.Count} sites, {layer.Markers.Count} markers");
        ImGui.TextDisabled($"Refresh {refreshMs:F0} ms, swap after {replace.Ms:F0} ms ({replace.MainMs:F1} ms main), {replace.Leaves} leaves; {pendingJobs} jobs");

        if (ImGui.Button("Save (Ctrl+S)"u8))
            _session.Save();

        ImGui.SameLine();
        ImGui.BeginDisabled(!_session.CanUndo);

        if (ImGui.Button("Undo"u8))
            _session.Undo();

        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!_session.CanRedo);

        if (ImGui.Button("Redo"u8))
            _session.Redo();

        ImGui.EndDisabled();
        ImGui.Separator();

        for (var i = 0; i < ToolNames.Length; i++)
        {
            if (ImGui.RadioButton(ToolNames[i], (int)Tool == i))
                Tool = (PlanetTool)i;

            if (i < ToolNames.Length - 1)
                ImGui.SameLine();
        }

        if (Tool == PlanetTool.Paint)
            BrushPanel();
        else if (Tool == PlanetTool.Site)
            SitePanel();
        else
            ImGui.TextDisabled("Right drag orbits, middle drag pans, wheel zooms. 1 2 3 pick the tool."u8);

        ImGui.End();
    }

    private void BrushPanel()
    {
        _inspector.Begin("Brush"u8, BrushNames[_brush]);

        if (_inspector.Section("Height brush"u8))
        {
            _inspector.Choice("Mode"u8, ref _brush, BrushNames, 0, "Raise and lower add height; level pulls toward a height; erase takes paint away"u8);
            _inspector.Float("Radius"u8, ref _radiusKm, 8.0, 0.1f, 2.0, 200.0, "%.1f km"u8, "Across the brush's falloff"u8);

            if (_brush != 3)
                _inspector.Float(_brush == 2 ? "Level to"u8 : "Height"u8, ref _metres, 200.0, 5f, _brush == 2 ? -1500.0 : 1.0, 1500.0, "%.0f m"u8, "Height a dab adds at the centre, or the height it levels to"u8);

            _inspector.Float("Strength"u8, ref _strength, 0.5, 0.01f, 0.05, 1.0, "%.2f"u8, "How much of the height one dab applies"u8);
        }

        _inspector.End();
        ImGui.TextDisabled("Left drag paints; one stroke is one undo step."u8);
    }

    private void SitePanel()
    {
        ImGui.TextDisabled("Shift + click places a site; click a site to select it, drag to move it."u8);

        if (_selected is null || Site(_selected) is not { } s)
            return;

        var d = double3.Normalize(s.Position);
        double core = s.CoreM, blend = s.BlendM, height = s.HeightM;
        var commit = false;
        _inspector.Begin("Site"u8, s.Id);

        if (_inspector.Section("Flatten"u8))
        {
            commit |= _inspector.Float("Core"u8, ref core, 250.0, 2f, 20.0, 3000.0, "%.0f m"u8, "Flat at the site's height out to here"u8);
            commit |= _inspector.Float("Blend"u8, ref blend, 200.0, 2f, 10.0, 3000.0, "%.0f m"u8, "Eases back to the ground over this much past the core"u8);
            commit |= _inspector.Float("Height"u8, ref height, s.HeightM, 0.5f, -1500.0, 6000.0, "%.1f m"u8, "The flat's height above the datum"u8);
            _inspector.Info("Since"u8, $"v{s.Since}");
        }

        _inspector.End();

        if (commit)
            _session.SetSite(s.Id, d, (float)core, (float)blend, (float)height);

        if (ImGui.Button("Ground height"u8))
            _session.SetSite(s.Id, d, s.CoreM, s.BlendM, _session.SiteHeight(d));

        ImGui.SameLine();

        if (ImGui.Button("Delete site"u8))
        {
            _session.DeleteSite(s.Id);
            _selected = null;
            return;
        }

        MarkerPanel(s.Id);
    }

    private void MarkerPanel(string site)
    {
        ImGui.Separator();

        foreach (var m in _session.Layer.Markers)
        {
            if (m.Site != site)
                continue;

            ImGui.Text($"Marker: {m.Type} \"{m.Name}\"");

            if (ImGui.Button("Remove marker"u8))
                _session.DeleteMarker(m.Id);

            return;
        }

        ImGui.InputText("Name"u8, _markerName);

        for (var i = 0; i < MarkerTypes.Length; i++)
        {
            if (ImGui.RadioButton(MarkerTypes[i], _markerType == i))
                _markerType = i;

            ImGui.SameLine();
        }

        if (ImGui.Button("Add marker"u8))
            _session.AddMarker(MarkerTypes[_markerType], _markerName.ToString(), site);
    }

    /// <summary>Brush ring and site rings in the 3D overlay (lying on the current ground), marker labels over the view.</summary>
    public void DrawOverlay(OverlayRenderer overlay, OrbitCamera camera, in Matrix4x4 viewProj, Vector2 viewport, PlanetSurface surface)
    {
        var width = camera.Distance * 0.003;

        if (Tool == PlanetTool.Paint && Hover is { } h)
            Ring(overlay, surface, double3.Normalize(h), _radiusKm * 1000.0, width, BrushColour);

        var list = ImGui.GetBackgroundDrawList(ImGui.GetMainViewport());

        foreach (var s in _session.Layer.Sites)
        {
            var d = _dragging && s.Id == _selected ? _dragTo : double3.Normalize(s.Position);
            Ring(overlay, surface, d, s.CoreM, width, s.Id == _selected ? SelectedColour : CoreColour);
            Ring(overlay, surface, d, s.CoreM + s.BlendM, width * 0.6, BlendColour);

            foreach (var m in _session.Layer.Markers)
            {
                if (m.Site == s.Id && camera.Project(d * (surface.Radius + s.HeightM + RingLift), viewProj, viewport, out var at))
                    list.AddText(new float2(at.X + 6f, at.Y - 6f), LabelColour, $"{m.Name} ({m.Type})");
            }
        }
    }

    private static void Ring(OverlayRenderer overlay, PlanetSurface surface, double3 centre, double metres, double width, uint colour)
    {
        var axis = Math.Abs(centre.Y) < 0.99 ? double3.UnitY : double3.UnitX;
        var east = double3.Normalize(double3.Cross(axis, centre));
        var north = double3.Cross(centre, east);
        var angle = metres / surface.Radius;
        var previous = default(double3);

        for (var k = 0; k <= RingPoints; k++)
        {
            var (s, c) = Math.SinCos(2.0 * Math.PI * k / RingPoints);
            var d = double3.Normalize(centre + (east * c + north * s) * angle);
            var p = d * (surface.Radius + Math.Max(surface.Height(d.X, d.Y, d.Z), surface.SeaHeight) + RingLift);

            if (k > 0)
                overlay.Ribbon(previous, p, width, colour);

            previous = p;
        }
    }
}
