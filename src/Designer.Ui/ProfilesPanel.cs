using System.Text;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using BrutalGame.Ships;
using BrutalGame.Ships.Interior;

namespace BrutalGame.Designer.Ui;

/// <summary>
/// The Profiles tab. The plan's own profiles, hallway outlines, wall sections (half a hallway, for rooms) and brush outlines
/// (swept along strokes): make one from a preset or from half a hallway, duplicate, rename, delete (when nothing uses it)
/// and shape it in the editor; every hallway, room or brush using it follows. The library on disk (<see cref="ProfileLibrary"/>, folders
/// as categories): import a profile into the plan, save a plan profile to it. The plan keeps its copy: a change to the
/// library file shows here as "the library has changed" and reaches the plan only with Reimport.
/// </summary>
public sealed class ProfilesPanel : IDisposable
{
    private readonly ProfileLibrary _library;
    private readonly FileDialogs _dialogs;
    private readonly ProfileEditor _editor = new();
    private readonly byte[] _name = new byte[HullProfile.MaxNameBytes + 1];
    private int _nameFor = -1;
    private int _nameVersion = -1;
    private float _width = (float)InteriorCommands.DefaultWidth;
    private float _height = (float)InteriorCommands.DefaultHeight;
    private float _wallHeight = 2.4f;
    private float _feature = 0.3f;
    private float _brushWidth = 0.5f;
    private float _brushDepth = 0.25f;
    private string _message = "";

    public ProfilesPanel(ProfileLibrary library)
    {
        _library = library;
        _dialogs = new FileDialogs("Hallway profile", ProfileLibrary.Extension, library.Folder);
        _library.Refresh();
    }

    /// <summary>The plan profile shown in the editor (-1: none).</summary>
    public int Selected = -1;

    /// <summary>Brings the tab to the front on the next draw (the Interior panel's "Edit profile").</summary>
    public bool Focus;

    public void Reset() => (Selected, _nameFor) = (-1, -1);

    public void Draw(DesignSession session)
    {
        ImGui.SetNextWindowDockID(new ImGuiID(ShipLayout.InteriorDock), ImGuiCond.FirstUseEver);

        if (Focus)
        {
            ImGui.SetNextWindowFocus();
            Focus = false;
        }

        if (ImGui.Begin("Profiles"u8))
        {
            var interior = session.Doc.Interior;
            DrawPlanProfiles(session, interior);
            Selected = interior is null ? -1 : Math.Min(Selected, interior.Profiles.Count - 1);

            if (Selected >= 0)
                DrawProfile(session, interior!, Selected);

            DrawLibrary(session, interior);

            if (_message.Length > 0)
                ImGui.TextWrapped($"{_message}");
        }

        ImGui.End();
    }

    private void DrawPlanProfiles(DesignSession session, HullInterior? interior)
    {
        ImGui.SeparatorText("In this plan"u8);
        var count = interior?.Profiles.Count ?? 0;

        if (count == 0)
            ImGui.TextDisabled("None yet."u8);

        for (var i = 0; i < count; i++)
        {
            var p = interior!.Profiles[i];
            string note;

            if (p.Kind == ProfileKind.Wall)
            {
                var rooms = interior.RoomWalls.Count(w => w.Profile == i);
                note = $"wall, {rooms} room{(rooms == 1 ? "" : "s")}";
            }
            else if (p.Kind == ProfileKind.Brush)
            {
                var brushes = session.Doc.Brushes?.Brushes.Count(b => b.Kind == BrushKind.Sweep && b.Outline == i) ?? 0;
                note = $"brush, {brushes} brush{(brushes == 1 ? "" : "es")}";
            }
            else
            {
                var users = interior.Hallways.Count(h => h.Profile == i);
                var clear = HallwayGeometry.Measure(p.Points);
                note = $"{(clear.Fits ? $"{clear.People} abreast" : "too small")}, {users} hallway{(users == 1 ? "" : "s")}";
            }

            if (ImGui.Selectable($"{p.Name}  ({note}){(p.Source.Length > 0 ? "  [library]" : "")}##prof{i}", i == Selected, ImGuiSelectableFlags.None, default))
                Selected = i;
        }

        if (ImGui.Button("New hallway"u8))
            Added(InteriorCommands.AddProfile(session, $"Profile {count + 1}", HallwayGeometry.Preset(HallwayPreset.Rounded, _width, _height)));

        ImGui.SameLine();

        if (ImGui.Button("New wall"u8))
            Added(InteriorCommands.AddProfile(session, $"Wall {count + 1}", WallGeometry.Preset(WallPreset.Chamfered, _wallHeight, _feature), kind: ProfileKind.Wall));

        ImGui.SameLine();

        if (ImGui.Button("New brush"u8))
            Added(InteriorCommands.AddProfile(session, $"Brush {count + 1}", BrushCommands.Profile(ProfilePreset.Rib, _brushWidth, _brushDepth), kind: ProfileKind.Brush));

        ImGui.SameLine();
        ImGui.BeginDisabled(Selected < 0 || Selected >= count);

        if (ImGui.Button("Duplicate"u8))
        {
            var source = interior!.Profiles[Selected];
            Added(InteriorCommands.AddProfile(session, $"{source.Name} copy", source.Points, kind: source.Kind));
        }

        ImGui.SameLine();

        if (ImGui.Button("Delete"u8))
        {
            if (InteriorCommands.RemoveProfile(session, Selected))
                Selected = -1;
            else
                _message = InteriorCommands.LastError;
        }

        ImGui.EndDisabled();
    }

    private void Added(int index)
    {
        if (index >= 0)
            Selected = index;

        _message = index < 0 ? InteriorCommands.LastError : "";
    }

    private void DrawProfile(DesignSession session, HullInterior interior, int index)
    {
        var profile = interior.Profiles[index];
        ImGui.SeparatorText("Profile"u8);

        if (_nameFor != index || _nameVersion != session.Version)
        {
            Array.Clear(_name);
            var bytes = Encoding.UTF8.GetBytes(profile.Name);
            Array.Copy(bytes, _name, Math.Min(bytes.Length, HullProfile.MaxNameBytes));
            (_nameFor, _nameVersion) = (index, session.Version);
        }

        DesignerStyle.ItemWidth(220f);
        ImGui.InputText("Name"u8, _name, ImGuiInputTextFlags.None, (ImGuiInputTextCallback?)null, default);

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            var length = Array.IndexOf(_name, (byte)0);
            var name = Encoding.UTF8.GetString(_name, 0, length < 0 ? _name.Length : length).Trim();

            if (name.Length > 0)
                InteriorCommands.SetProfile(session, index, p => p.Name = name);
        }

        if (profile.Kind == ProfileKind.Wall)
            DrawWall(session, index, profile);
        else if (profile.Kind == ProfileKind.Brush)
            DrawBrush(session, index, profile);
        else
            DrawHallway(session, index, profile);

        // The library copy: save to it, or reimport from it on purpose.
        DrawSource(session, index, profile);
    }

    private void DrawHallway(DesignSession session, int index, HullProfile profile)
    {
        // Presets reshape it.
        DesignerStyle.ItemWidth(90f);
        ImGui.InputFloat("Width m"u8, ref _width, 0f, 0f, "%.2f"u8, ImGuiInputTextFlags.None);
        ImGui.SameLine();
        DesignerStyle.ItemWidth(90f);
        ImGui.InputFloat("Height m"u8, ref _height, 0f, 0f, "%.2f"u8, ImGuiInputTextFlags.None);
        _width = Math.Clamp(_width, 0.2f, 16f);
        _height = Math.Clamp(_height, 0.2f, 16f);

        for (var p = 0; p < HallwayGeometry.PresetNames.Length; p++)
        {
            if (p > 0)
                ImGui.SameLine();

            if (ImGui.Button($"{HallwayGeometry.PresetNames[p]}##preset{p}"))
                Commit(session, index, HallwayGeometry.Preset((HallwayPreset)p, _width, _height));
        }

        var shown = _editor.Shown(index, profile.Points);
        var clear = HallwayGeometry.Measure(shown);

        if (clear.Fits)
            ImGui.Text($"Clear {clear.Width:F2} x {Math.Min(clear.Height, 9.99):F2} m: {clear.People} {(clear.People == 1 ? "person" : "people")} abreast");
        else
            ImGui.TextColored(new float4(1f, 0.45f, 0.35f, 1f), $"Too small: clear {clear.Width:F2} x {clear.Height:F2} m, needs {HallwayGeometry.MinClearWidth:F1} x {HallwayGeometry.MinClearHeight:F1} m");

        if (_editor.Draw(index, profile.Points, clear) is { } edited)
            Commit(session, index, edited);

        ImGui.TextDisabled("Drag points; double-click an edge to add one, right-click a point to remove it."u8);

        if (ImGui.Button("Make a wall from half of it"u8))
        {
            if (WallGeometry.FromHallway(profile.Points) is { } half)
                Added(InteriorCommands.AddProfile(session, $"{profile.Name} wall", half, kind: ProfileKind.Wall));
            else
                _message = "This outline has no right side to take a wall from";
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("A wall profile for rooms: this outline's right half, from the floor up to its middle"u8);
    }

    private void DrawWall(DesignSession session, int index, HullProfile profile)
    {
        DesignerStyle.ItemWidth(90f);
        ImGui.InputFloat("Height m##wall"u8, ref _wallHeight, 0f, 0f, "%.2f"u8, ImGuiInputTextFlags.None);
        ImGui.SameLine();
        DesignerStyle.ItemWidth(90f);
        ImGui.InputFloat("Detail m"u8, ref _feature, 0f, 0f, "%.2f"u8, ImGuiInputTextFlags.None);
        _wallHeight = Math.Clamp(_wallHeight, 0.2f, 16f);
        _feature = Math.Clamp(_feature, 0.05f, 4f);

        for (var p = 0; p < WallGeometry.PresetNames.Length; p++)
        {
            if (p > 0)
                ImGui.SameLine();

            if (ImGui.Button($"{WallGeometry.PresetNames[p]}##wallpreset{p}"))
                Commit(session, index, WallGeometry.Preset((WallPreset)p, _wallHeight, _feature));
        }

        var shown = _editor.Shown(index, profile.Points);
        var tall = shown.Count > 0 ? shown[^1].Y / (double)HullSolid.PositionUnitsPerMetre : 0;
        var deepest = shown.Count > 0 ? shown.Max(p => p.X) / (double)HullSolid.PositionUnitsPerMetre : 0;
        ImGui.Text($"{tall:F2} m tall, stands in up to {deepest:F2} m from the room's side");

        if (_editor.Draw(index, profile.Points, default, ProfileKind.Wall) is { } edited)
            Commit(session, index, edited);

        ImGui.TextWrapped("The room's side is on the right. The lower half keeps to the floor and the upper half to the ceiling, so taller rooms stretch the middle. Drag points; double-click a segment to add one, right-click a point to remove it."u8);
    }

    private void DrawBrush(DesignSession session, int index, HullProfile profile)
    {
        DesignerStyle.ItemWidth(90f);
        ImGui.InputFloat("Width m##brush"u8, ref _brushWidth, 0f, 0f, "%.2f"u8, ImGuiInputTextFlags.None);
        ImGui.SameLine();
        DesignerStyle.ItemWidth(90f);
        ImGui.InputFloat("Depth m##brush"u8, ref _brushDepth, 0f, 0f, "%.2f"u8, ImGuiInputTextFlags.None);
        _brushWidth = Math.Clamp(_brushWidth, 0.02f, 8f);
        _brushDepth = Math.Clamp(_brushDepth, 0.02f, 8f);

        for (var p = 0; p < BrushCommands.PresetNames.Length; p++)
        {
            if (p > 0)
                ImGui.SameLine();

            if (ImGui.Button($"{BrushCommands.PresetNames[p]}##brushpreset{p}"))
                Commit(session, index, BrushCommands.Profile((ProfilePreset)p, _brushWidth, _brushDepth));
        }

        if (_editor.Draw(index, profile.Points, default, ProfileKind.Brush) is { } edited)
            Commit(session, index, edited);

        ImGui.TextWrapped("An outline swept along brush strokes, X across, Y out of the hull (the bright line is Y = 0). Each brush using it sets where the stroke runs through it and its size. Up to 16 points: drag them, double-click an edge to add one, right-click to remove one."u8);
    }

    private void DrawSource(DesignSession session, int index, HullProfile profile)
    {
        if (profile.Source.Length > 0)
        {
            var current = _library.Current(profile.Source);

            if (current is null)
                ImGui.TextDisabled($"From the library: {profile.Source} (no longer there)");
            else if (current.SameShape(profile))
                ImGui.TextDisabled($"From the library: {profile.Source} (the same)");
            else
                ImGui.TextColored(new float4(1f, 0.8f, 0.4f, 1f), $"From the library: {profile.Source} (the library has changed)");

            ImGui.BeginDisabled(current is null || current.SameShape(profile));

            if (ImGui.Button("Reimport from the library"u8) && current is not null && InteriorCommands.SetProfilePoints(session, index, current.Points))
                _message = $"Reimported {profile.Source}: every hallway using {profile.Name} follows.";

            ImGui.EndDisabled();
            ImGui.SameLine();
        }

        if (ImGui.Button("Save to the library..."u8))
            Save(session, index, profile);
    }

    private void Save(DesignSession session, int index, HullProfile profile)
    {
        var start = profile.Source.Length > 0 ? Path.Combine(_library.Folder, profile.Source) : Path.Combine(_library.Folder, Safe(profile.Name) + "." + ProfileLibrary.Extension);
        var path = _dialogs.PickSave(start);

        if (path is null)
        {
            _message = _dialogs.HasDialogs ? "" : "File dialogs are unavailable on this system (nfd.dll missing)";
            return;
        }

        try
        {
            var relative = _library.Save(profile, path);
            InteriorCommands.SetProfile(session, index, p => p.Source = relative);
            _library.Refresh();
            _message = $"Saved {relative}";
        }
        catch (IOException e)
        {
            _message = $"Save failed: {e.Message}";
        }
    }

    private static string Safe(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return safe.Length > 0 ? safe : "profile";
    }

    private void DrawLibrary(DesignSession session, HullInterior? interior)
    {
        ImGui.SeparatorText("Library"u8);
        ImGui.TextDisabled($"{_library.Folder}");

        if (ImGui.Button("Refresh library"u8))
            _library.Refresh();

        if (_library.Root.Children.Count == 0)
            ImGui.TextDisabled("Empty: save a profile to start it (subfolders become categories)."u8);

        foreach (var node in _library.Root.Children)
            Tree(session, interior, node);
    }

    private void Tree(DesignSession session, HullInterior? interior, ProfileNode node)
    {
        if (node.IsFolder)
        {
            if (ImGui.TreeNodeEx($"{node.Name}##{node.GetHashCode()}", ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.SpanAvailWidth))
            {
                foreach (var child in node.Children)
                    Tree(session, interior, child);

                ImGui.TreePop();
            }

            return;
        }

        var inPlan = interior?.Profiles.FindIndex(p => p.Source == node.Relative) ?? -1;
        ImGui.TreeNodeEx($"{node.Name}{(node.Profile!.Kind == ProfileKind.Hallway ? "" : $"  [{node.Profile.Kind.ToString().ToLowerInvariant()}]")}{(inPlan >= 0 ? "  (in the plan)" : "")}##{node.Relative}", ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.SpanAvailWidth);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Double-click to import a copy into the plan"u8);

        if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        {
            var added = InteriorCommands.AddProfile(session, node.Profile!.Name, node.Profile.Points, node.Relative, node.Profile.Kind);
            Added(added);

            if (added >= 0)
                _message = $"Imported {node.Relative}. Later changes to the file reach the plan only through Reimport.";
        }
    }

    private void Commit(DesignSession session, int index, IReadOnlyList<ProfilePoint> points)
    {
        if (!InteriorCommands.SetProfilePoints(session, index, points))
            _message = InteriorCommands.LastError;
    }

    public void Dispose() => _dialogs.Dispose();
}
