using Brutal.ImGuiApi;
using Brutal.Numerics;
using BrutalGame.Designer.Mesh;
using BrutalGame.Ships;

namespace BrutalGame.Designer.Ui;

/// <summary>
/// The mesh editor's panel for the primary mesh solid (ship-hulls "Mesh editor"): edit toggle (Tab), selection mode
/// (1 vertex, 2 edge, 3 face), live mirror, subdivision levels and creases, and the modelling ops with their amounts.
/// Each button is one undoable op; the mesh's validation result shows under it.
/// </summary>
public sealed class MeshPanel
{
    private const int Units = HullSolid.PositionUnitsPerMetre;

    private static readonly string[] Axes = ["X", "Y", "Z"];

    private float _extrude = 1f;
    private float _inset = 0.25f;
    private float _bevel = 0.25f;
    private float _loopFactor = 0.5f;
    private int _knifeAxis;
    private float _knifeOffset;
    private string _message = "";
    private ulong _validatedHash;
    private MeshIssue _issue;
    private int _issueAt;

    public void Draw(DesignSession session, MeshEditState edit)
    {
        var id = session.Selection.Primary;
        var mesh = MeshCommands.MeshOf(session, id);

        if (mesh is null)
            return;

        if (edit.FocusPanel)
        {
            ImGui.SetNextWindowFocus();
            edit.FocusPanel = false;
        }

        if (ImGui.Begin("Mesh"u8))
        {
            Validate(mesh);
            var triangles = MeshCache.Get(mesh).TriangleCount;
            ImGui.Text($"{mesh.Vertices.Length} vertices  {mesh.FaceCount} faces  {triangles} triangles");

            if (_issue == MeshIssue.None)
                ImGui.TextColored(new float4(0.5f, 0.9f, 0.5f, 1f), "valid closed mesh"u8);
            else
                ImGui.TextColored(new float4(1f, 0.45f, 0.35f, 1f), $"invalid: {_issue} at {_issueAt}");

            ImGui.Checkbox("Edit mesh (Tab)"u8, ref edit.Enabled);
            ImGui.SameLine();
            ImGui.Checkbox("Live mirror X"u8, ref edit.Mirror);
            ImGui.TextDisabled("Vertex, edge or face select: top left of the view (1, 2, 3)."u8);
            ImGui.Text($"selected: {edit.Selection.Vertices.Count} v  {edit.Selection.Edges.Count} e  {edit.Selection.Faces.Count} f");

            if (ImGui.Button("All faces"u8))
            {
                edit.Selection.Mode = MeshSelectMode.Face;
                edit.Selection.Faces.Clear();

                for (var f = 0; f < mesh.FaceCount; f++)
                    edit.Selection.Faces.Add(f);
            }

            ImGui.SameLine();

            if (ImGui.Button("None"u8))
                edit.Selection.Clear();

            var levels = (int)mesh.SubdivLevels;
            DesignerStyle.ItemWidth(120f);

            if (ImGui.SliderInt("Subdivision"u8, ref levels, 0, HullMesh.MaxLevels, "%d"u8, ImGuiSliderFlags.None))
                Run(MeshCommands.SetLevels(session, id, levels, edit.Mirror));

            ImGui.SeparatorText("Faces"u8);
            Amount("##extrude"u8, ref _extrude);

            if (ImGui.Button("Extrude"u8))
                Op(session, edit, id, b => MeshOps.ExtrudeFaces(b, edit.Selection.Faces.ToArray(), Metres(_extrude)));

            Amount("##inset"u8, ref _inset);

            if (ImGui.Button("Inset"u8))
                Op(session, edit, id, b => MeshOps.InsetFaces(b, edit.Selection.Faces.ToArray(), Metres(_inset)));

            if (ImGui.Button("Bridge two faces"u8))
            {
                var faces = edit.Selection.Faces.ToArray();
                Op(session, edit, id, b => faces.Length == 2 ? MeshOps.BridgeFaces(b, faces[0], faces[1]) : MeshOpsFail("Select exactly two faces"));
                edit.Selection.Faces.Clear();
            }

            ImGui.SeparatorText("Edges"u8);
            Amount("##bevel"u8, ref _bevel);

            if (ImGui.Button("Bevel"u8))
                Op(session, edit, id, b => MeshOps.BevelEdges(b, edit.Selection.Edges.ToArray(), Metres(_bevel)));

            DesignerStyle.ItemWidth(90f);
            ImGui.SliderFloat("##loop"u8, ref _loopFactor, 0.05f, 0.95f, "%.2f"u8, ImGuiSliderFlags.None);
            ImGui.SameLine();

            if (ImGui.Button("Loop cut"u8))
            {
                var edges = edit.Selection.Edges.ToArray();
                Op(session, edit, id, b => edges.Length > 0 ? MeshOps.LoopCut(b, edges[0], _loopFactor) : MeshOpsFail("Select an edge across the ring"));
                edit.Selection.Edges.Clear();
            }

            if (ImGui.Button("Crease"u8))
                Op(session, edit, id, b =>
                {
                    MeshOps.SetCrease(b, edit.Selection.Edges, true);
                    return true;
                });

            ImGui.SameLine();

            if (ImGui.Button("Smooth"u8))
                Op(session, edit, id, b =>
                {
                    MeshOps.SetCrease(b, edit.Selection.Edges, false);
                    return true;
                });

            ImGui.SeparatorText("Mesh"u8);
            DesignerStyle.ItemWidth(50f);

            if (ImGui.BeginCombo("##axis"u8, $"{Axes[_knifeAxis]}", ImGuiComboFlags.None))
            {
                for (var i = 0; i < 3; i++)
                {
                    if (ImGui.Selectable($"{Axes[i]}", i == _knifeAxis, ImGuiSelectableFlags.None, default))
                        _knifeAxis = i;
                }

                ImGui.EndCombo();
            }

            ImGui.SameLine();
            DesignerStyle.ItemWidth(80f);
            ImGui.InputFloat("##knife"u8, ref _knifeOffset, 0.25f, 1f, "%.2f"u8, ImGuiInputTextFlags.None);
            ImGui.SameLine();

            if (ImGui.Button("Knife plane"u8))
            {
                Op(session, edit, id, b => MeshOps.Knife(b, _knifeAxis, Metres(_knifeOffset)));
                edit.Selection.Clear();
            }

            if (ImGui.Button("Mirror X now"u8))
            {
                Op(session, edit, id, MeshOps.MirrorX);
                edit.Selection.Clear();
            }

            if (_message.Length > 0)
                ImGui.TextWrapped($"{_message}");
        }

        ImGui.End();
    }

    private static bool MeshOpsFail(string message)
    {
        _failure = message;
        return false;
    }

    private static string _failure = "";

    /// <summary>Runs an op; vertex and edge indices change with topology, so those selections clear (face indices are kept by every op).</summary>
    private void Op(DesignSession session, MeshEditState edit, uint id, Func<MeshBuilder, bool> op)
    {
        _failure = "";
        var done = MeshCommands.Apply(session, id, op, edit.Mirror);
        _message = done ? "" : _failure.Length > 0 ? _failure : MeshCommands.LastError;

        if (done)
        {
            edit.Selection.Vertices.Clear();
            edit.Selection.Edges.Clear();
        }
    }

    private void Run(bool done) => _message = done ? "" : MeshCommands.LastError;

    private void Validate(HullMesh mesh)
    {
        if (mesh.MeshHash == _validatedHash)
            return;

        _validatedHash = mesh.MeshHash;
        _issue = MeshValidator.ValidateCached(mesh, out _issueAt);
    }

    private static void Amount(ReadOnlySpan<byte> label, ref float metres)
    {
        DesignerStyle.ItemWidth(90f);
        ImGui.InputFloat(label, ref metres, 0.125f, 0.5f, "%.3f m"u8, ImGuiInputTextFlags.None);
        ImGui.SameLine();
    }

    private static int Metres(float metres) => (int)MathF.Round(metres * Units);
}
