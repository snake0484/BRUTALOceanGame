using System.Numerics;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using BrutalGame.Terrain;

namespace BrutalGame.PlanetDesigner.App;

public enum PlanetTool : byte
{
    Look,
    Paint,
    Site,
}

/// <summary>
/// The planet designer's tools and panels (planet-designer-brief.md §6): the height brush (raise, lower, level, erase
/// with radius, height and strength), sites (click to place, drag to move, core, blend and height in the panel) and the
/// selected site's marker. Overlays are ImGui draw-list lines projected from the body frame. A drag or stroke is one
/// undo step; the terrain refreshes when it ends.
/// </summary>
public sealed class PlanetTools
{
    private const float DabSpacing = 0.25f;
    private const int RingPoints = 64;

    private static readonly ImColor8 BrushColour = new(255, 170, 60, 230);
    private static readonly ImColor8 CoreColour = new(90, 230, 200, 230);
    private static readonly ImColor8 BlendColour = new(90, 230, 200, 110);
    private static readonly ImColor8 SelectedColour = new(255, 255, 255, 255);
    private static readonly ImColor8 LabelColour = new(255, 255, 255, 230);

    private static readonly string[] ToolNames = ["Look", "Paint height", "Sites"];
    private static readonly string[] BrushNames = ["Raise", "Lower", "Level", "Erase"];

    private readonly PlanetDesignSession _session;
    private readonly ImInputString _markerName = new(48, "New settlement"u8);
    private int _brush;
    private float _radiusKm = 8f;
    private float _metres = 200f;
    private float _strength = 0.5f;
    private bool _stroking;
    private double3 _lastDab;
    private string? _selected;
    private bool _dragging;
    private double3 _dragTo;
    private int _markerType;
    private string? _draftFor;
    private float _core, _blend, _height;

    private static readonly string[] MarkerTypes = ["city", "outpost"];

    public PlanetTools(PlanetDesignSession session) => _session = session;

    public PlanetTool Tool;

    /// <summary>The surface point under the cursor this frame, if any.</summary>
    public double3? Hover;

    /// <summary>Per frame with the cursor's surface point (null off the body) and whether the mouse is over the 3D view.</summary>
    public void Update(PlanetSurface surface, double3? hit, bool mouseFree)
    {
        Hover = hit;
        var radius = surface.Radius;

        switch (Tool)
        {
            case PlanetTool.Paint:
                Paint(hit, mouseFree, radius);
                break;
            case PlanetTool.Site:
                Sites(hit, mouseFree, radius);
                break;
        }

        var io = ImGui.GetIO();

        if (!io.WantCaptureKeyboard && io.KeyCtrl && !_stroking && !_dragging)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.Z, false))
                _session.Undo();
            else if (ImGui.IsKeyPressed(ImGuiKey.Y, false))
                _session.Redo();
            else if (ImGui.IsKeyPressed(ImGuiKey.S, false))
                _session.Save();
        }
    }

    private void Paint(double3? hit, bool mouseFree, double radius)
    {
        var down = ImGui.IsMouseDown(ImGuiMouseButton.Left);

        if (_stroking && !down)
        {
            _session.EndStroke();
            _stroking = false;
            return;
        }

        if (hit is not { } h)
            return;

        var d = double3.Normalize(h);

        if (!_stroking)
        {
            if (!mouseFree || !ImGui.IsMouseClicked(ImGuiMouseButton.Left, false))
                return;

            _session.BeginStroke();
            _stroking = true;
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
                var s = Site(_selected!);

                if (s is { } site && double3.Dot(_dragTo, double3.Normalize(site.Position)) < 1.0 - 1e-14)
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
                _selected = s.Id;
                _dragging = true;
                _dragTo = d;
                return;
            }
        }

        if (ImGui.GetIO().KeyShift)
            _selected = _session.AddSite(d);
        else
            _selected = null;
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

    public void DrawPanel(string body, float refreshMs, int pendingJobs)
    {
        if (!ImGui.Begin("Planet designer"u8))
        {
            ImGui.End();
            return;
        }

        var layer = _session.Layer;
        ImGui.Text($"Body {body}  kind {layer.Kind}  seed {layer.Seed}");
        ImGui.Text($"Version {layer.Version}{(_session.Dirty ? " (unsaved)" : "")}  hash {layer.Hash():X16}");
        ImGui.Text($"{layer.Sites.Count} sites, {layer.Markers.Count} markers; refresh {refreshMs:F0} ms, {pendingJobs} jobs");

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
        {
            for (var i = 0; i < BrushNames.Length; i++)
            {
                if (ImGui.RadioButton(BrushNames[i], _brush == i))
                    _brush = i;

                if (i < BrushNames.Length - 1)
                    ImGui.SameLine();
            }

            ImGui.SliderFloat("Radius km"u8, ref _radiusKm, 0.5f, 200f, "%.1f"u8, ImGuiSliderFlags.Logarithmic);
            ImGui.SliderFloat(_brush == 2 ? "Level to m"u8 : "Height m"u8, ref _metres, _brush == 2 ? -1500f : 1f, 1500f, "%.0f"u8);
            ImGui.SliderFloat("Strength"u8, ref _strength, 0.05f, 1f, "%.2f"u8);
            ImGui.TextWrapped("Left drag paints. Right drag pans, middle drag orbits, wheel zooms."u8);
        }

        if (Tool == PlanetTool.Site)
        {
            ImGui.TextWrapped("Shift + click places a site; click a site to select, drag to move."u8);

            if (_selected is not null && Site(_selected) is { } s)
            {
                ImGui.Separator();
                ImGui.Text($"{s.Id} (since v{s.Since})");
                // The widgets edit a draft; it commits when one is released, so a drag is one undo step.
                if (_draftFor != s.Id || !ImGui.IsAnyItemActive())
                    (_draftFor, _core, _blend, _height) = (s.Id, s.CoreM, s.BlendM, s.HeightM);

                ImGui.SliderFloat("Core m"u8, ref _core, 20f, 3000f, "%.0f"u8, ImGuiSliderFlags.Logarithmic);
                var commit = ImGui.IsItemDeactivatedAfterEdit();
                ImGui.SliderFloat("Blend m"u8, ref _blend, 10f, 3000f, "%.0f"u8, ImGuiSliderFlags.Logarithmic);
                commit |= ImGui.IsItemDeactivatedAfterEdit();
                ImGui.InputFloat("Height m"u8, ref _height, 0.5f, 5f, "%.1f"u8, ImGuiInputTextFlags.None);
                commit |= ImGui.IsItemDeactivatedAfterEdit();
                var core = _core;
                var blend = _blend;

                if (commit)
                    _session.SetSite(s.Id, double3.Normalize(s.Position), _core, _blend, _height);

                if (ImGui.Button("Ground height"u8))
                    _session.SetSite(s.Id, double3.Normalize(s.Position), core, blend, _session.SiteHeight(double3.Normalize(s.Position)));

                ImGui.SameLine();

                if (ImGui.Button("Delete site"u8))
                {
                    foreach (var m in _session.Layer.Markers.ToArray())
                    {
                        if (m.Site == s.Id)
                            _session.DeleteMarker(m.Id);
                    }

                    _session.DeleteSite(s.Id);
                    _selected = null;
                }

                MarkerPanel(s.Id);
            }
        }

        ImGui.End();
    }

    private void MarkerPanel(string site)
    {
        ImGui.Separator();
        AuthoredMarker? marker = null;

        foreach (var m in _session.Layer.Markers)
        {
            if (m.Site == site)
                marker = m;
        }

        if (marker is { } existing)
        {
            ImGui.Text($"Marker: {existing.Type} \"{existing.Name}\"");

            if (ImGui.Button("Remove marker"u8))
                _session.DeleteMarker(existing.Id);

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

    /// <summary>Brush ring, site rings and marker labels over the 3D view.</summary>
    public void DrawOverlay(PlanetCameraRig rig, PlanetSurface surface, Vector2 viewport)
    {
        var list = ImGui.GetBackgroundDrawList(ImGui.GetMainViewport());
        var radius = surface.Radius;

        if (Tool == PlanetTool.Paint && Hover is { } h)
            Ring(list, rig, surface, double3.Normalize(h), _radiusKm * 1000.0, BrushColour, viewport);

        foreach (var s in _session.Layer.Sites)
        {
            var d = _dragging && s.Id == _selected ? _dragTo : double3.Normalize(s.Position);
            var selected = s.Id == _selected;
            Ring(list, rig, surface, d, s.CoreM, selected ? SelectedColour : CoreColour, viewport);
            Ring(list, rig, surface, d, s.CoreM + s.BlendM, BlendColour, viewport);

            foreach (var m in _session.Layer.Markers)
            {
                if (m.Site == s.Id && rig.Project(d * (radius + s.HeightM + 20.0), viewport, out var at))
                    list.AddText(new float2(at.X + 6f, at.Y - 6f), LabelColour, $"{m.Name} ({m.Type})");
            }
        }
    }

    private static void Ring(ImDrawListPtr list, PlanetCameraRig rig, PlanetSurface surface, double3 centre, double metres, ImColor8 colour, Vector2 viewport)
    {
        var axis = Math.Abs(centre.Y) < 0.99 ? double3.UnitY : double3.UnitX;
        var east = double3.Normalize(double3.Cross(axis, centre));
        var north = double3.Cross(centre, east);
        var angle = metres / surface.Radius;
        var have = false;
        var previous = default(Vector2);

        for (var k = 0; k <= RingPoints; k++)
        {
            var (s, c) = Math.SinCos(2.0 * Math.PI * k / RingPoints);
            var d = double3.Normalize(centre + (east * c + north * s) * angle);
            var p = d * (surface.Radius + Math.Max(surface.Height(d.X, d.Y, d.Z), surface.SeaHeight) + 2.0);

            if (!rig.Project(p, viewport, out var at))
            {
                have = false;
                continue;
            }

            if (have)
                list.AddLine(new float2(previous.X, previous.Y), new float2(at.X, at.Y), colour, 2f);

            previous = at;
            have = true;
        }
    }
}
