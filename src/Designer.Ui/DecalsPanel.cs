using System.Numerics;
using Brutal.ImGuiApi;
using BrutalGame.Plans;
using BrutalGame.Ships;
using static BrutalGame.Ships.HullSurface;

namespace BrutalGame.Designer.Ui;

/// <summary>
/// The Decals panel and view tool. A decal is a game image, or a shape (rectangle, ellipse, triangle, diamond) filled with
/// one of the ship's paint slots, projected onto the hull and the brushes on it. Place one by clicking the hull (Add >
/// Decal); pick it in the list or by its centre in the view; choose its image or shape and paint, size, how deep it
/// reaches, its turn and opacity, or move it by clicking a new spot.
/// </summary>
public sealed class DecalsPanel
{
    private const float CentrePixels = 12f;

    private static readonly string[] ShapeNames = ["Image", "Rectangle", "Ellipse", "Triangle", "Diamond"];

    private static readonly uint OutlineColour = OverlayRenderer.Rgba(255, 190, 120, 200);
    private static readonly uint SelectedColour = OverlayRenderer.Rgba(255, 230, 120, 255);
    private static readonly uint PreviewColour = OverlayRenderer.Rgba(150, 255, 170, 230);

    private readonly DecalCatalog _catalog;
    private readonly float[] _aspect;
    private readonly HullDecal _template = new();
    private int _templateAsset;
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
        public float Width, Height, Depth, Opacity;
    }

    /// <param name="aspect">Each catalog image's width over height (the decal texture layers').</param>
    public DecalsPanel(DecalCatalog catalog, float[] aspect)
    {
        _catalog = catalog;
        _aspect = aspect;
    }

    public uint Selected;

    public void Reset() => (Selected, _placing, _surface, _surfaceVersion) = (0, Placing.None, null, -1);

    public void Draw(DesignSession session)
    {
        ImGui.SetNextWindowDockID(new ImGuiID(ShipLayout.EditDock), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("Decals"u8))
        {
            ImGui.TextWrapped("A decal projects a game image, or a shape in one of the ship's paints, onto the hull and the brushes on it."u8);

            if (_placing == Placing.New)
                ImGui.TextColored(new Brutal.Numerics.float4(0.6f, 1f, 0.7f, 1f), "Click the hull to place a decal (Esc stops)."u8);
            else
                ImGui.TextDisabled("Add > Decal, then click the hull."u8);

            DrawList(session);
            var set = session.Doc.Decals;
            var index = set?.IndexOf(Selected) ?? -1;

            if (index >= 0)
                DrawDecal(session, set!.Decals[index]);
            else
                DrawTemplate(session);

            if (_message.Length > 0)
                ImGui.TextWrapped($"{_message}");
        }

        ImGui.End();
    }

    private string Name(uint asset) => _catalog.IndexOf(new DefId(asset)) is var i and >= 0 ? _catalog.All[i].Name : "missing image";

    private string Name(HullDecal d, HullPaintSet? paint) => d.Shape == DecalShape.Image
        ? Name(d.Asset)
        : $"{ShapeNames[(int)d.Shape]} in {(paint is not null && d.Paint < paint.Slots.Count ? paint.Slots[d.Paint].Name : "a missing paint")}";

    /// <summary>The shape (image or a paint shape) and what fills it: an image, or a paint slot.</summary>
    private bool Kind(DesignSession session, bool template, ref DecalShape shape, ref int asset, ref int slot)
    {
        var changed = false;
        var index = (int)shape;
        DesignerStyle.ItemWidth(200f);

        if (ImGui.BeginCombo(template ? "Shape##t"u8 : "Shape"u8, $"{ShapeNames[index]}", ImGuiComboFlags.None))
        {
            for (var i = 0; i < ShapeNames.Length; i++)
            {
                if (ImGui.Selectable($"{ShapeNames[i]}##shape{i}", i == index, ImGuiSelectableFlags.None, default) && i != index)
                    (shape, changed) = ((DecalShape)i, true);
            }

            ImGui.EndCombo();
        }

        if (shape == DecalShape.Image)
        {
            if (_catalog.All.Count == 0)
                ImGui.TextColored(new Brutal.Numerics.float4(1f, 0.6f, 0.4f, 1f), "No decal images in the game data (data/decals)."u8);
            else
                changed |= AssetCombo(template ? "Image##t"u8 : "Image"u8, ref asset);
        }
        else
        {
            DesignerStyle.ItemWidth(200f);
            changed |= PaintPanel.SlotCombo(template ? "Paint##t"u8 : "Paint"u8, session.Doc.Paint, ref slot, "Pick a paint slot");
        }

        return changed;
    }

    private void DrawList(DesignSession session)
    {
        ImGui.SeparatorText("Decals"u8);
        var set = session.Doc.Decals;

        if (set is null || set.IsEmpty)
        {
            ImGui.TextDisabled("None yet."u8);
            return;
        }

        foreach (var d in set.Decals)
        {
            if (ImGui.Selectable($"#{d.Id}  {Name(d, session.Doc.Paint)} {d.WidthMm / 1000.0:F2} x {d.HeightMm / 1000.0:F2} m##decal{d.Id}", d.Id == Selected, ImGuiSelectableFlags.None, default))
                Selected = d.Id == Selected ? 0 : d.Id;
        }
    }

    /// <summary>The image or shape and size new decals get, while none is selected.</summary>
    private void DrawTemplate(DesignSession session)
    {
        ImGui.SeparatorText("New decals"u8);
        var shape = _template.Shape;
        var slot = (int)_template.Paint;

        if (Kind(session, true, ref shape, ref _templateAsset, ref slot))
        {
            (_template.Shape, _template.Paint) = (shape, (byte)Math.Max(slot, 0));

            if (shape == DecalShape.Image)
                _template.HeightMm = Mm(_template.WidthMm / 1000f / Aspect(_templateAsset));
        }

        var width = _template.WidthMm / 1000f;
        var height = _template.HeightMm / 1000f;
        DesignerStyle.ItemWidth(90f);

        if (ImGui.InputFloat("Width m##tdw"u8, ref width, 0f, 0f, "%.2f"u8, ImGuiInputTextFlags.None))
            (_template.WidthMm, _template.HeightMm) = (Mm(width), shape == DecalShape.Image ? Mm(width / Aspect(_templateAsset)) : _template.HeightMm);

        if (shape != DecalShape.Image)
        {
            ImGui.SameLine();
            DesignerStyle.ItemWidth(90f);

            if (ImGui.InputFloat("Height m##tdh"u8, ref height, 0f, 0f, "%.2f"u8, ImGuiInputTextFlags.None))
                _template.HeightMm = Mm(height);
        }
    }

    private float Aspect(int index) => index >= 0 && index < _aspect.Length && _aspect[index] > 0 ? _aspect[index] : 1f;

    private void DrawDecal(DesignSession session, HullDecal decal)
    {
        ImGui.SeparatorText($"Decal #{decal.Id}");

        if (_draft.Id != decal.Id || _draft.Version != session.Version)
        {
            _draft = new Draft
            {
                Id = decal.Id, Version = session.Version, Width = decal.WidthMm / 1000f, Height = decal.HeightMm / 1000f,
                Depth = decal.DepthMm / 1000f, Opacity = decal.Opacity / 255f,
            };
        }

        var asset = _catalog.IndexOf(new DefId(decal.Asset));
        var shape = decal.Shape;
        var slot = decal.Shape == DecalShape.Image ? -1 : (int)decal.Paint;

        if (Kind(session, false, ref shape, ref asset, ref slot))
        {
            var id = asset >= 0 && asset < _catalog.All.Count ? _catalog.All[asset].Id.Value : decal.Asset;
            var height = shape == DecalShape.Image && asset >= 0 ? Mm(decal.WidthMm / 1000f / Aspect(asset)) : decal.HeightMm;
            var paint = (byte)Math.Max(slot, 0);
            DecalCommands.SetDecal(session, decal.Id, d => (d.Shape, d.Asset, d.Paint, d.HeightMm) = (shape, id, paint, height));
        }

        DesignerStyle.ItemWidth(90f);
        ImGui.InputFloat("Width m"u8, ref _draft.Width, 0f, 0f, "%.2f"u8, ImGuiInputTextFlags.None);
        var sized = ImGui.IsItemDeactivatedAfterEdit();
        ImGui.SameLine();
        DesignerStyle.ItemWidth(90f);
        ImGui.InputFloat("Height m"u8, ref _draft.Height, 0f, 0f, "%.2f"u8, ImGuiInputTextFlags.None);
        sized |= ImGui.IsItemDeactivatedAfterEdit();
        DesignerStyle.ItemWidth(90f);
        ImGui.InputFloat("Depth m"u8, ref _draft.Depth, 0f, 0f, "%.2f"u8, ImGuiInputTextFlags.None);
        sized |= ImGui.IsItemDeactivatedAfterEdit();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How far the projection reaches, half out of the hull and half in"u8);

        if (sized)
        {
            var (w, h, depth) = (Mm(_draft.Width), Mm(_draft.Height), Mm(_draft.Depth));
            DecalCommands.SetDecal(session, decal.Id, d => (d.WidthMm, d.HeightMm, d.DepthMm) = (w, h, depth));
        }

        if (decal.Shape == DecalShape.Image && ImGui.Button("Fit height to image"u8) && asset >= 0)
        {
            var height = Mm(decal.WidthMm / 1000f / Aspect(asset));
            DecalCommands.SetDecal(session, decal.Id, d => d.HeightMm = height);
        }

        DesignerStyle.ItemWidth(140f);
        ImGui.SliderFloat("Opacity"u8, ref _draft.Opacity, 0f, 1f, "%.2f"u8, ImGuiSliderFlags.None);

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            var opacity = (byte)Math.Round(Math.Clamp(_draft.Opacity, 0f, 1f) * 255);
            DecalCommands.SetDecal(session, decal.Id, d => d.Opacity = opacity);
        }

        ImGui.Text($"Turned {decal.Roll * SolidRotation.DegreesPerStep} deg");
        ImGui.SameLine();

        if (ImGui.Button("-##decalroll"u8))
            DecalCommands.SetDecal(session, decal.Id, d => d.Roll = (byte)((d.Roll + 23) % 24));

        ImGui.SameLine();

        if (ImGui.Button("+##decalroll"u8))
            DecalCommands.SetDecal(session, decal.Id, d => d.Roll = (byte)((d.Roll + 1) % 24));
    }

    private bool AssetCombo(ReadOnlySpan<byte> label, ref int index)
    {
        var all = _catalog.All;
        var changed = false;
        DesignerStyle.ItemWidth(200f);

        if (ImGui.BeginCombo(label, $"{(index >= 0 && index < all.Count ? all[index].Name : "missing image")}", ImGuiComboFlags.None))
        {
            for (var i = 0; i < all.Count; i++)
            {
                if (ImGui.Selectable($"{all[i].Name}##asset{i}", i == index, ImGuiSelectableFlags.None, default) && i != index)
                {
                    index = i;
                    changed = true;
                }
            }

            ImGui.EndCombo();
        }

        return changed;
    }

    /// <summary>Add menu: the next clicks on the hull place decals.</summary>
    public void StartPlacing() => _placing = Placing.New;

    /// <summary>The menu for the selected decal in the view: move it (click the hull), delete.</summary>
    public void DrawContext(DesignSession session, OrbitCamera camera, Vector2 viewport)
    {
        var set = session.Doc.Decals;
        var index = set?.IndexOf(Selected) ?? -1;

        if (index < 0 || _placing != Placing.None)
            return;

        var decal = set!.Decals[index];

        if (!ContextBar.Begin("decal", camera, decal.Position, viewport))
            return;

        if (ContextBar.Button("Move", "Click the hull to move the decal there"))
            _placing = Placing.Move;

        if (ContextBar.Button("Delete", "Remove this decal"))
        {
            DecalCommands.RemoveDecal(session, decal.Id);
            Selected = 0;
        }

        ContextBar.End();
    }

    // ---- view ----

    /// <summary>Draws every decal's frame, places and moves decals by clicking the hull, picks them by their centre. True when it used the mouse.</summary>
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
        var set = session.Doc.Decals;
        var hover = 0u;
        var best = CentrePixels;

        if (set is not null)
        {
            foreach (var d in set.Decals)
            {
                if (d.Id == Selected)
                    Frame(overlay, d, SelectedColour);

                if (camera.Project(d.Position, viewProj, viewport, out var pixel) && Vector2.Distance(pixel, mouse) < best)
                    (best, hover) = (Vector2.Distance(pixel, mouse), d.Id);
            }

            if (hover != 0 && hover != Selected && set.IndexOf(hover) is var h and >= 0)
                Frame(overlay, set.Decals[h], OutlineColour);
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
                var preview = (set is not null && _placing == Placing.Move && set.IndexOf(Selected) is var i and >= 0 ? set.Decals[i] : _template).Clone();
                (preview.X, preview.Y, preview.Z) = ComponentCommands.Quantise(at);
                preview.SetNormal(normal);
                Frame(overlay, preview, PreviewColour);

                if (clicked)
                {
                    if (_placing == Placing.Move)
                    {
                        var q = ComponentCommands.Quantise(at);
                        var facing = normal;
                        DecalCommands.SetDecal(session, Selected, d =>
                        {
                            (d.X, d.Y, d.Z) = q;
                            d.SetNormal(facing);
                        });
                        _placing = Placing.None;
                    }
                    else
                    {
                        var image = _catalog.All.Count > 0 ? _catalog.All[Math.Clamp(_templateAsset, 0, _catalog.All.Count - 1)].Id : default;
                        Selected = DecalCommands.AddDecal(session, at, normal, image, _template);
                        _message = Selected == 0 ? DecalCommands.LastError : "Placed. Click again to place another, or Esc to stop.";
                    }

                    return true;
                }
            }

            ImGui.SetTooltip(_placing == Placing.Move ? "Click the hull to move the decal there"u8 : "Click the hull to place a decal"u8);
            return clicked;
        }

        if (mouseFree && clicked && hover != 0)
        {
            Selected = hover;
            return true;
        }

        return false;
    }

    /// <summary>The decal's frame on its plane, lifted off the hull, with an arrow for its top.</summary>
    private static void Frame(OverlayRenderer overlay, HullDecal decal, uint colour)
    {
        var (right, up) = decal.Axes();
        var lift = Scale(decal.Normal, 0.02);
        var centre = Add(decal.Position, lift);
        var (hw, hh) = (decal.WidthMm * 0.0005, decal.HeightMm * 0.0005);
        Span<Vector3d> corners =
        [
            Add(centre, Add(Scale(right, -hw), Scale(up, -hh))), Add(centre, Add(Scale(right, hw), Scale(up, -hh))),
            Add(centre, Add(Scale(right, hw), Scale(up, hh))), Add(centre, Add(Scale(right, -hw), Scale(up, hh))),
        ];

        for (var j = 0; j < 4; j++)
            overlay.Ribbon(corners[j], corners[(j + 1) % 4], 0.015, colour);

        overlay.Ribbon(centre, Add(centre, Scale(up, hh * 0.5)), 0.015, colour);
        overlay.Cube(centre, Math.Max(0.02, Math.Min(hw, hh) * 0.04), colour);
    }

    private static ushort Mm(float metres) => (ushort)Math.Clamp((int)Math.Round(metres * 1000), HullDecal.MinSizeMm, HullDecal.MaxSizeMm);
}
