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
    private Draft _draft;

    private enum Placing { None, New, Move }

    private Placing _placing;

    private struct Draft
    {
        public uint Id;
        public int Version;
        public float Width, Height, Depth;
        public int Thickness;
    }

    public WindowsPanel(HullMaterialCatalog catalog) => _catalog = catalog;

    public uint Selected;

    public void Reset() => (Selected, _placing, _surface, _surfaceVersion) = (0, Placing.None, null, -1);

    /// <summary>The selected window's properties (in the Properties tab).</summary>
    public void DrawProperties(DesignSession session)
    {
        var set = session.Doc.Windows;
        var index = set?.IndexOf(Selected) ?? -1;

        if (index >= 0)
            DrawWindow(session, set!.Windows[index]);

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

        if (MaterialCombo("Pane##tmat"u8, ref material))
            _templateMaterial = material;
    }

    private void DrawWindow(DesignSession session, HullWindow window)
    {
        ImGui.SeparatorText($"Window #{window.Id}");

        if (_draft.Id != window.Id || _draft.Version != session.Version)
        {
            _draft = new Draft
            {
                Id = window.Id, Version = session.Version, Width = window.WidthMm / 1000f, Height = window.HeightMm / 1000f,
                Depth = window.DepthMm / 1000f, Thickness = window.ThicknessMm,
            };
        }

        var shape = window.Shape;

        if (Shape(ref shape, out var changed) && changed)
            WindowCommands.SetWindow(session, window.Id, w => w.Shape = shape);

        DesignerStyle.ItemWidth(90f);
        ImGui.InputFloat("Width m"u8, ref _draft.Width, 0f, 0f, "%.2f"u8, ImGuiInputTextFlags.None);
        var sized = ImGui.IsItemDeactivatedAfterEdit();
        ImGui.SameLine();
        DesignerStyle.ItemWidth(90f);
        ImGui.InputFloat("Height m"u8, ref _draft.Height, 0f, 0f, "%.2f"u8, ImGuiInputTextFlags.None);
        sized |= ImGui.IsItemDeactivatedAfterEdit();
        DesignerStyle.ItemWidth(90f);
        ImGui.InputFloat("Cut depth m"u8, ref _draft.Depth, 0f, 0f, "%.2f"u8, ImGuiInputTextFlags.None);
        sized |= ImGui.IsItemDeactivatedAfterEdit();
        ImGui.SameLine();
        DesignerStyle.ItemWidth(90f);
        ImGui.InputInt("Pane mm"u8, ref _draft.Thickness, 0, 0, ImGuiInputTextFlags.None);
        sized |= ImGui.IsItemDeactivatedAfterEdit();

        if (sized)
        {
            var (w, h) = (Mm(_draft.Width), Mm(_draft.Height));
            var depth = (ushort)Math.Clamp((int)Math.Round(_draft.Depth * 1000), 0, HullWindow.MaxDepthMm);
            var thickness = (ushort)Math.Clamp(_draft.Thickness, HullWindow.MinThicknessMm, 2000);
            WindowCommands.SetWindow(session, window.Id, x => (x.WidthMm, x.HeightMm, x.DepthMm, x.ThicknessMm) = (w, h, depth, thickness));
        }

        ImGui.Text($"Turned {window.Roll * SolidRotation.DegreesPerStep} deg");
        ImGui.SameLine();

        if (ImGui.Button("-##winroll"u8))
            WindowCommands.SetWindow(session, window.Id, x => x.Roll = (byte)((x.Roll + 23) % 24));

        ImGui.SameLine();

        if (ImGui.Button("+##winroll"u8))
            WindowCommands.SetWindow(session, window.Id, x => x.Roll = (byte)((x.Roll + 1) % 24));

        var material = CatalogIndex(session.Doc, window.Material);

        if (MaterialCombo("Pane##winmat"u8, ref material))
            WindowCommands.SetMaterial(session, window.Id, _catalog.All[material].Id);

        if (_catalog.TryGet(_catalog.All[material].Id, out var m))
            ImGui.TextDisabled($"{m.DensityKgM3} kg/m3, strength {m.YieldMpa} MPa{(m.Transparent ? "" : ", not see-through")}");

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
        DesignerStyle.ItemWidth(220f);

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
