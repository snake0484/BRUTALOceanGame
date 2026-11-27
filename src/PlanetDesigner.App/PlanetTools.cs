using System.Numerics;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using BrutalGame.DesignKit.Ui;
using BrutalGame.Terrain;

namespace BrutalGame.PlanetDesigner.App;

public enum PlanetTool : byte
{
    Look,
    Paint,
    Site,
}

/// <summary>
/// The planet designer's tools and panel (planet-designer-brief.md §6): the height brush (raise, lower, level, erase with
/// radius, height and strength), sites (Shift + click places, drag moves, core, blend and height in the panel) and the
/// selected site's marker. Rings draw in the kit's 3D overlay, labels in ImGui. A stroke or drag is one undo step; the
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
    private readonly ImInputString _markerName = new(48, "New settlement"u8);
    private int _brush;
    private float _radiusKm = 8f;
    private float _metres = 200f;
    private float _strength = 0.5f;
    private double3 _lastDab;
    private string? _selected;
    private bool _dragging;
    private double3 _dragTo;
    private int _markerType;
    private string? _draftFor;
    private float _core, _blend, _height;

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
            _ => (AuthoredBrush.Erase, 0f),
        };

        _session.Dab(d, _radiusKm * 1000.0, metres, brush, _strength);
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

    public void DrawPanel(string body, float refreshMs, (float Ms, int Leaves) replace, int pendingJobs)
    {
        if (!ImGui.Begin("Planet"u8))
        {
            ImGui.End();
            return;
        }

        var layer = _session.Layer;
        ImGui.Text($"Body {body}  kind {layer.Kind}");
        ImGui.Text($"Version {layer.Version}{(_session.Modified ? " (unsaved)" : "")}  {layer.Sites.Count} sites, {layer.Markers.Count} markers");
        ImGui.Text($"Refresh {refreshMs:F0} ms + bounds {replace.Ms:F0} ms, {replace.Leaves} leaves; {pendingJobs} jobs");

        if (ImGui.Button("Save (Ctrl+S)"u8))
            _session.Save();

        ImGui.SameLine();

        if (ImGui.Button("Undo"u8))
            _session.Undo();

        ImGui.SameLine();

        if (ImGui.Button("Redo"u8))
            _session.Redo();

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

        ImGui.End();
    }

    private void BrushPanel()
    {
        for (var i = 0; i < BrushNames.Length; i++)
        {
            if (ImGui.RadioButton(BrushNames[i], _brush == i))
                _brush = i;

            if (i < BrushNames.Length - 1)
                ImGui.SameLine();
        }

        ImGui.SliderFloat("Radius km"u8, ref _radiusKm, 2f, 200f, "%.1f"u8, ImGuiSliderFlags.Logarithmic);
        ImGui.SliderFloat(_brush == 2 ? "Level to m"u8 : "Height m"u8, ref _metres, _brush == 2 ? -1500f : 1f, 1500f, "%.0f"u8);
        ImGui.SliderFloat("Strength"u8, ref _strength, 0.05f, 1f, "%.2f"u8);
        ImGui.TextWrapped("Left drag paints. Right drag orbits, middle drag pans, wheel zooms. 1 2 3 pick the tool."u8);
    }

    private void SitePanel()
    {
        ImGui.TextWrapped("Shift + click places a site; click a site to select it, drag to move it."u8);

        if (_selected is null || Site(_selected) is not { } s)
            return;

        ImGui.Separator();
        ImGui.Text($"{s.Id} (since v{s.Since})");

        // The widgets edit a draft that commits when one is released, so a drag is one undo step.
        if (_draftFor != s.Id || !ImGui.IsAnyItemActive())
            (_draftFor, _core, _blend, _height) = (s.Id, s.CoreM, s.BlendM, s.HeightM);

        ImGui.SliderFloat("Core m"u8, ref _core, 20f, 3000f, "%.0f"u8, ImGuiSliderFlags.Logarithmic);
        var commit = ImGui.IsItemDeactivatedAfterEdit();
        ImGui.SliderFloat("Blend m"u8, ref _blend, 10f, 3000f, "%.0f"u8, ImGuiSliderFlags.Logarithmic);
        commit |= ImGui.IsItemDeactivatedAfterEdit();
        ImGui.InputFloat("Height m"u8, ref _height, 0.5f, 5f, "%.1f"u8, ImGuiInputTextFlags.None);
        commit |= ImGui.IsItemDeactivatedAfterEdit();
        var d = double3.Normalize(s.Position);

        if (commit)
            _session.SetSite(s.Id, d, _core, _blend, _height);

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
