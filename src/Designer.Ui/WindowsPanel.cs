using System.Numerics;
using Brutal.ImGuiApi;
using BrutalGame.Plans;
using BrutalGame.Ships;
using BrutalGame.Ships.Interior;
using static BrutalGame.Ships.HullSurface;

namespace BrutalGame.Designer.Ui;

/// <summary>
/// Windows: the view tool, the selected window's properties, and the options new ones get (in the Add dialog). A window
/// cuts through the hull to the space behind and its pane follows the hull's shape. Pick one in the object tree or by its
/// centre in the view; edit its shape, size, how deep the opening goes, the pane's thickness and material, turn it, or
/// move it by clicking a new spot.
/// </summary>
public sealed class WindowsPanel
{
    private const float CentrePixels = 12f;

    private static readonly HullWindow Defaults = new();

    private static readonly uint OutlineColour = OverlayRenderer.Rgba(150, 210, 255, 200);
    private static readonly uint SelectedColour = OverlayRenderer.Rgba(255, 230, 120, 255);
    private static readonly uint PreviewColour = OverlayRenderer.Rgba(150, 255, 170, 230);

    private readonly HullMaterialCatalog _catalog;
    private readonly HullWindow _template = new();
    private int _templateMaterial = -1;
    private HullSurface? _surface;
    private int _surfaceVersion = -1;
    private DesignSession? _surfaceSession;
    private string _message = "";

    private enum Placing { None, New, Move }

    private Placing _placing;

    public WindowsPanel(HullMaterialCatalog catalog) => _catalog = catalog;

    public uint Selected;

    public void Reset() => (Selected, _placing, _surface, _surfaceVersion) = (0, Placing.None, null, -1);

    /// <summary>The selected window's properties (in the Properties tab).</summary>
    public void DrawProperties(DesignSession session, Inspector inspector)
    {
        var set = session.Doc.Windows;
        var index = set?.IndexOf(Selected) ?? -1;

        if (index >= 0)
        {
            var window = set!.Windows[index];
            inspector.Begin("Window"u8, $"#{window.Id}");
            DrawWindow(session, inspector, window);
            inspector.End();
        }

        if (_message.Length > 0)
            ImGui.TextWrapped($"{_message}");
    }

    /// <summary>A short name for the object tree.</summary>
    public string Label(DesignSession session, uint id)
    {
        var set = session.Doc.Windows;
        var index = set?.IndexOf(id) ?? -1;

        if (index < 0)
            return $"Window #{id}";

        var w = set!.Windows[index];
        return $"Window, {WindowGeometry.ShapeNames[(int)w.Shape].ToLowerInvariant()} {w.WidthMm / 1000.0:F2} x {w.HeightMm / 1000.0:F2} m  #{w.Id}";
    }

    /// <summary>A window with the new-window options at <paramref name="at"/>, facing <paramref name="normal"/>; selected.</summary>
    public void PlaceAt(DesignSession session, Vector3d at, Vector3d normal)
    {
        var material = _catalog.All[_templateMaterial < 0 ? FirstGlazing() : _templateMaterial].Id;
        Selected = WindowCommands.AddWindow(session, at, normal, material, _template);
        _message = Selected == 0 ? WindowCommands.LastError : "";
    }

    /// <summary>The options new windows get (in the Add dialog).</summary>
    public void DrawAddOptions()
    {
        ImGui.TextWrapped("A window cuts through the hull to the space behind it; its pane takes the hull's shape."u8);
        Shape(ref _template.Shape, out _);
        var width = _template.WidthMm / 1000f;
        var height = _template.HeightMm / 1000f;
        DesignerStyle.ItemWidth(90f);
        ImGui.InputFloat("Width m##tw"u8, ref width, 0f, 0f, "%.2f"u8, ImGuiInputTextFlags.None);
        ImGui.SameLine();
        DesignerStyle.ItemWidth(90f);
        ImGui.InputFloat("Height m##th"u8, ref height, 0f, 0f, "%.2f"u8, ImGuiInputTextFlags.None);
        _template.WidthMm = Mm(width);
        _template.HeightMm = Mm(height);
        var material = _templateMaterial < 0 ? FirstGlazing() : _templateMaterial;

        DesignerStyle.ItemWidth(220f);

        if (MaterialCombo("Pane##tmat"u8, ref material))
            _templateMaterial = material;
    }

    private void DrawWindow(DesignSession session, Inspector inspector, HullWindow window)
    {
        var id = window.Id;

        if (inspector.Section("Shape"u8))
        {
            var shape = (int)window.Shape;

            if (inspector.Choice("Outline"u8, ref shape, WindowGeometry.ShapeNames, (int)Defaults.Shape, "The opening's outline"u8))
                WindowCommands.SetWindow(session, id, w => w.Shape = (WindowShape)shape);

            var width = window.WidthMm / 1000.0;

            if (inspector.Float("Width"u8, ref width, Defaults.WidthMm / 1000.0, 0.01f, HullWindow.MinSizeMm / 1000.0, HullWindow.MaxSizeMm / 1000.0, "%.2f m"u8, "Across the window"u8))
                WindowCommands.SetWindow(session, id, w => w.WidthMm = Mm((float)width));

            var height = window.HeightMm / 1000.0;

            if (inspector.Float("Height"u8, ref height, Defaults.HeightMm / 1000.0, 0.01f, HullWindow.MinSizeMm / 1000.0, HullWindow.MaxSizeMm / 1000.0, "%.2f m"u8, "Up the window"u8))
                WindowCommands.SetWindow(session, id, w => w.HeightMm = Mm((float)height));

            var turn = (double)window.Roll * SolidRotation.DegreesPerStep;

            if (inspector.Float("Turn"u8, ref turn, 0, 1f, 0, 360 - SolidRotation.DegreesPerStep, "%.0f deg"u8, "Turned about the hull's normal, in 15 degree steps"u8))
            {
                var roll = (byte)((int)Math.Round(turn / SolidRotation.DegreesPerStep) % 24);
                WindowCommands.SetWindow(session, id, w => w.Roll = roll);
            }
        }

        if (inspector.Section("Cut"u8))
        {
            var depth = window.DepthMm / 1000.0;

            if (inspector.Float("Depth"u8, ref depth, Defaults.DepthMm / 1000.0, 0.01f, 0, HullWindow.MaxDepthMm / 1000.0, "%.2f m"u8, "How far into the hull the opening cuts"u8))
            {
                var mm = (ushort)Math.Clamp((int)Math.Round(depth * 1000), 0, HullWindow.MaxDepthMm);
                WindowCommands.SetWindow(session, id, w => w.DepthMm = mm);
            }
        }

        if (inspector.Section("Pane"u8))
        {
            var thickness = (int)window.ThicknessMm;

            if (inspector.Int("Thickness"u8, ref thickness, Defaults.ThicknessMm, 1f, HullWindow.MinThicknessMm, 2000, "%d mm"u8, "The pane's thickness"u8))
            {
                var mm = (ushort)thickness;
                WindowCommands.SetWindow(session, id, w => w.ThicknessMm = mm);
            }

            var material = CatalogIndex(session.Doc, window.Material);

            if (inspector.Row("Material"u8, "What the pane is made of: glazings first, they are see-through"u8))
            {
                var glazing = FirstGlazing();
                ImGui.SetNextItemWidth(inspector.EditorWidth);
                var picked = MaterialCombo("##winmat"u8, ref material);

                if (inspector.Revert(material != glazing))
                    (material, picked) = (glazing, true);

                if (picked)
                    WindowCommands.SetMaterial(session, id, _catalog.All[material].Id);
            }

            if (_catalog.All.Count > 0 && _catalog.TryGet(_catalog.All[material].Id, out var m))
            {
                inspector.Info("Density"u8, $"{m.DensityKgM3} kg/m3");
                inspector.Info("Strength"u8, $"{m.YieldMpa} MPa");
                inspector.Info("See-through"u8, m.Transparent ? "Yes"u8 : "No"u8);
            }
        }
    }

    /// <summary>Add menu: the next clicks on the hull place windows.</summary>
    public void StartPlacing() => _placing = Placing.New;

    /// <summary>The bar beside the selected window in the view: move it (click the hull), delete.</summary>
    public void DrawContext(DesignSession session, OrbitCamera camera, Vector2 viewport)
    {
        var set = session.Doc.Windows;
        var index = set?.IndexOf(Selected) ?? -1;

        if (index < 0 || _placing != Placing.None || _surface is null)
            return;

        var window = set!.Windows[index];

        if (!ContextBar.Begin("window", camera, WindowGeometry.Centre(window, _surface), viewport))
            return;

        if (ContextBar.Button("Move", "Click the hull to move the window there"))
            _placing = Placing.Move;

        if (ContextBar.Button("Delete", "Remove this window"))
        {
            WindowCommands.RemoveWindow(session, window.Id);
            Selected = 0;
        }

        ContextBar.End();
    }

    private static bool Shape(ref WindowShape shape, out bool changed)
    {
        changed = false;

        for (var s = 0; s < WindowGeometry.ShapeNames.Length; s++)
        {
            if (s > 0)
                ImGui.SameLine();

            if (Toggle($"{WindowGeometry.ShapeNames[s]}##wshape{s}", (int)shape == s) && (int)shape != s)
            {
                shape = (WindowShape)s;
                changed = true;
            }
        }

        return changed;
    }

    // ---- view ----

    /// <summary>Draws every window's outline on the hull, places and moves windows by clicking the hull, picks them by their centre. True when it used the mouse.</summary>
    public bool Update(DesignSession session, OrbitCamera camera, Vector2 viewport, in Matrix4x4 viewProj, bool mouseFree, OverlayRenderer overlay)
    {
        if (_surfaceVersion != session.Version || !ReferenceEquals(_surfaceSession, session))
        {
            _surface = new HullSurface(session.Doc.Solids.AsSpan(), session.Doc.Meshes);
            (_surfaceVersion, _surfaceSession) = (session.Version, session);
        }

        var io = ImGui.GetIO();
        var mouse = new Vector2(io.MousePos.X, io.MousePos.Y);
        var clicked = ImGui.IsMouseClicked(ImGuiMouseButton.Left, false);
        var set = session.Doc.Windows;
        var hover = 0u;
        var best = CentrePixels;

        if (set is not null)
        {
            foreach (var w in set.Windows)
            {
                var centre = WindowGeometry.Centre(w, _surface!);
                Outline(overlay, w, centre, w.Id == Selected ? SelectedColour : OutlineColour, _surface);

                if (camera.Project(centre, viewProj, viewport, out var pixel) && Vector2.Distance(pixel, mouse) < best)
                    (best, hover) = (Vector2.Distance(pixel, mouse), w.Id);
            }
        }

        if (_placing != Placing.None && ImGui.IsKeyPressed(ImGuiKey.Escape, false))
            _placing = Placing.None;

        if (_placing != Placing.None && mouseFree)
        {
            Vector3d eye = camera.Position;
            Vector3d ray = camera.Ray(mouse, viewport);

            if (_surface!.Raycast(eye, ray, 4000, out var t, out var normal))
            {
                var at = Add(eye, Scale(ray, t));
                var preview = (set is not null && _placing == Placing.Move && set.IndexOf(Selected) is var i and >= 0 ? set.Windows[i] : _template).Clone();
                (preview.X, preview.Y, preview.Z) = ComponentCommands.Quantise(at);
                preview.SetNormal(normal);
                Outline(overlay, preview, at, PreviewColour, _surface);

                if (clicked)
                {
                    if (_placing == Placing.Move)
                    {
                        var q = ComponentCommands.Quantise(at);
                        var facing = normal;
                        WindowCommands.SetWindow(session, Selected, w =>
                        {
                            (w.X, w.Y, w.Z) = q;
                            w.SetNormal(facing);
                        });
                        _placing = Placing.None;
                    }
                    else
                    {
                        var material = _catalog.All[_templateMaterial < 0 ? FirstGlazing() : _templateMaterial].Id;
                        Selected = WindowCommands.AddWindow(session, at, normal, material, _template);
                        _message = Selected == 0 ? WindowCommands.LastError : "Placed. Click again to place another, or turn Place off.";
                    }

                    return true;
                }
            }

            ImGui.SetTooltip(_placing == Placing.Move ? "Click the hull to move the window there"u8 : "Click the hull to place a window"u8);
            return clicked;
        }

        if (mouseFree && clicked && hover != 0)
        {
            Selected = hover;
            return true;
        }

        return false;
    }

    /// <summary>The window's rim as it lies on the hull (each outline point cast onto the surface), and its centre.</summary>
    private static void Outline(OverlayRenderer overlay, HullWindow window, Vector3d centre, uint colour, HullSurface? surface)
    {
        var (across, up, n) = WindowGeometry.Frame(window);
        var outline = WindowGeometry.Outline(window.Shape, window.WidthMm * 0.001, window.HeightMm * 0.001);
        var lift = Scale(n, 0.01);
        var rim = new Vector3d[outline.Count];

        for (var j = 0; j < outline.Count; j++)
        {
            var q = Add(centre, Add(Scale(across, outline[j].X), Scale(up, outline[j].Y)));
            rim[j] = Add(surface is not null && surface.Project(q, n, WindowGeometry.Reach, out var hit, out _) ? hit : q, lift);
        }

        for (var j = 0; j < rim.Length; j++)
            overlay.Ribbon(rim[j], rim[(j + 1) % rim.Length], 0.015, colour);

        overlay.Cube(Add(centre, lift), Math.Max(0.02, Math.Min(window.WidthMm, window.HeightMm) * 0.00004), colour);
    }

    // ---- helpers ----

    private static ushort Mm(float metres) => (ushort)Math.Clamp((int)Math.Round(metres * 1000), HullWindow.MinSizeMm, HullWindow.MaxSizeMm);

    private int FirstGlazing()
    {
        var all = _catalog.All;

        for (var i = 0; i < all.Count; i++)
        {
            if (all[i].Transparent)
                return i;
        }

        return 0;
    }

    private int CatalogIndex(PlanDocument doc, byte material)
    {
        if (material >= doc.Materials.Count)
            return 0;

        var id = doc.Materials[material];

        for (var i = 0; i < _catalog.All.Count; i++)
        {
            if (_catalog.All[i].Id == id)
                return i;
        }

        return 0;
    }

    /// <summary>Glazings first (see-through), then the rest.</summary>
    private bool MaterialCombo(ReadOnlySpan<byte> label, ref int index)
    {
        var all = _catalog.All;

        if (all.Count == 0)
            return false;

        index = Math.Clamp(index, 0, all.Count - 1);
        var changed = false;

        if (ImGui.BeginCombo(label, $"{all[index].Name}", ImGuiComboFlags.None))
        {
            for (var pass = 0; pass < 2; pass++)
            {
                for (var i = 0; i < all.Count; i++)
                {
                    if (all[i].Transparent != (pass == 0))
                        continue;

                    if (ImGui.Selectable($"{all[i].Name}{(all[i].Transparent ? "" : "  (opaque)")}", i == index, ImGuiSelectableFlags.None, default) && i != index)
                    {
                        index = i;
                        changed = true;
                    }
                }
            }

            ImGui.EndCombo();
        }

        return changed;
    }

    private static bool Toggle(ImString label, bool on)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, on ? DesignerStyle.AccentPress : DesignerStyle.Frame);
        var clicked = ImGui.Button(label);
        ImGui.PopStyleColor(1);
        return clicked;
    }
}
