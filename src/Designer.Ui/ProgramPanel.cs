using System.Text;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using BrutalGame.Ships;
using BrutalGame.Ships.Logic;

namespace BrutalGame.Designer.Ui;

/// <summary>
/// A component's programme, in the component editor: the interface it implements (built-in, the player's, or a new one
/// saved to the library); the node graph that answers each call (nodes as boxes, flow as curves from an output to the
/// next node; drag a node's title to move it, drag from an output onto a node to link it, right-click an output to cut
/// it, right-click a node to delete it, right-click empty space to add one, middle-drag to pan); each state's pose (where
/// every joint stands in it, or holds); and the preview (a button per call, "Something in the way", the state it is in,
/// the node running, highlighted).
/// </summary>
public sealed class ProgramPanel
{
    private const float NodeWidth = 190f;
    private const float TitleHeight = 22f;
    private const float ParamHeight = 26f;
    private const float OutHeight = 20f;
    private const float PinRadius = 5f;

    private static readonly ImColor8 Canvas = new(0, 0, 0, 70);
    private static readonly ImColor8 GridLine = new(255, 255, 255, 14);
    private static readonly ImColor8 Body = new(38, 42, 50, 240);
    private static readonly ImColor8 Border = new(255, 255, 255, 50);
    private static readonly ImColor8 Running = new(255, 220, 90, 255);
    private static readonly ImColor8 Pin = new(150, 210, 255, 255);
    private static readonly ImColor8 Link = new(150, 210, 255, 200);
    private static readonly ImColor8 Text = new(235, 235, 235, 255);
    private static readonly ImColor8 Faint = new(200, 200, 200, 160);

    private readonly InterfaceLibrary _library;
    private readonly byte[] _faceName = new byte[64];
    private readonly byte[] _faceCalls = new byte[256];
    private readonly byte[] _faceStates = new byte[256];
    private float2 _pan;
    private ushort _dragNode;
    private float2 _dragDelta;
    private (ushort Node, int Output)? _linkFrom;
    private ushort _menuNode;
    private float2 _addAt;
    private string _message = "";
    private bool _refreshed;
    private readonly Dictionary<ushort, float> _waitDraft = new();

    /// <summary>The preview's requests, read and cleared by the host each frame.</summary>
    public int CallRequest = -1;
    public bool ResetRequest;

    /// <summary>The preview's "Something in the way".</summary>
    public bool Blocked;

    public ProgramPanel(InterfaceLibrary library) => _library = library;

    public static string NodeName(NodeKind kind) => kind switch
    {
        NodeKind.OnCall => "On call",
        NodeKind.SetState => "Set state",
        NodeKind.WaitArrived => "Wait until in place",
        NodeKind.Wait => "Wait",
        NodeKind.IfState => "If state is",
        _ => "If something is in the way",
    };

    private static string[] Outputs(NodeKind kind) => kind switch
    {
        NodeKind.WaitArrived => ["in place", "in the way"],
        NodeKind.IfState or NodeKind.IfBlocked => ["yes", "no"],
        _ => ["then"],
    };

    private static bool HasParam(NodeKind kind) => kind is NodeKind.OnCall or NodeKind.SetState or NodeKind.IfState or NodeKind.Wait;

    private static float Height(NodeKind kind) => TitleHeight + (HasParam(kind) ? ParamHeight : 0) + Outputs(kind).Length * OutHeight + 6;

    public void Draw(DesignSession session, ProgramInstance? run)
    {
        ImGui.SetNextWindowDockID(new ImGuiID(ShipLayout.EditDock), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("Programme"u8))
            DrawBody(session, run);

        ImGui.End();
    }

    private void DrawBody(DesignSession session, ProgramInstance? run)
    {
        if (!_refreshed)
        {
            _library.Refresh();
            _refreshed = true;
        }

        var program = session.Doc.Program;
        ImGui.TextWrapped("A programme makes the component answer its interface's calls the same way as every other component of that kind: anything can open any door with Open."u8);
        DrawInterface(session, program);

        if (program is null)
        {
            if (_message.Length > 0)
                ImGui.TextWrapped($"{_message}");

            return;
        }

        DrawPreview(program, run);
        ImGui.SeparatorText("Sequence"u8);

        if (ImGui.Button("Start from the standard door sequence"u8))
        {
            var ok = true;
            ProgramCommands.Set(session, p => ok = ProgramCommands.StandardDoor(p, session.Doc.Joints?.Joints ?? []));
            _message = ok ? "" : "The standard door sequence needs a door's calls (Open, Close, Lock, Unlock) and states (Closed, Opening, Open, Closing, Locked, Blocked).";
        }

        ImGui.TextDisabled("Drag titles to move, outputs onto a node to link; right-click to add, delete or cut; middle-drag pans."u8);
        DrawGraph(session, program, run);
        DrawPoses(session, program);

        if (_message.Length > 0)
            ImGui.TextWrapped($"{_message}");
    }

    // ---- interface ----

    private void DrawInterface(DesignSession session, HullProgram? program)
    {
        ImGui.SeparatorText("Interface"u8);
        var current = program?.Interface;
        DesignerStyle.ItemWidth(220f);

        if (ImGui.BeginCombo("Implements"u8, current is null ? "(none)" : current.Name, ImGuiComboFlags.None))
        {
            foreach (var face in _library.BuiltIn)
            {
                if (ImGui.Selectable($"{face.Name}  (built in)##b{face.Key}", current?.Key == face.Key, ImGuiSelectableFlags.None, default))
                    ProgramCommands.Implement(session, face);
            }

            foreach (var (face, _) in _library.Mine)
            {
                if (ImGui.Selectable($"{face.Name}  (mine)##m{face.Key}", current?.Key == face.Key, ImGuiSelectableFlags.None, default))
                    ProgramCommands.Implement(session, face);
            }

            ImGui.EndCombo();
        }

        if (current is not null)
        {
            ImGui.TextDisabled($"Calls: {string.Join(", ", current.Calls)}");
            ImGui.TextDisabled($"States: {string.Join(", ", current.States)}");
            ImGui.SameLine();

            if (ImGui.SmallButton("Remove programme"u8))
                ProgramCommands.Remove(session);
        }

        if (!ImGui.TreeNodeEx("New interface"u8, ImGuiTreeNodeFlags.None))
            return;

        DesignerStyle.ItemWidth(220f);
        ImGui.InputText("Name##face"u8, _faceName, ImGuiInputTextFlags.None, (ImGuiInputTextCallback?)null, default);
        DesignerStyle.ItemWidth(220f);
        ImGui.InputText("Calls (commas)##face"u8, _faceCalls, ImGuiInputTextFlags.None, (ImGuiInputTextCallback?)null, default);
        DesignerStyle.ItemWidth(220f);
        ImGui.InputText("States (commas)##face"u8, _faceStates, ImGuiInputTextFlags.None, (ImGuiInputTextCallback?)null, default);
        var made = new ComponentInterface { Name = Read(_faceName).Trim(), Calls = Names(_faceCalls), States = Names(_faceStates) };
        var valid = ComponentInterface.IsValid(made);
        ImGui.BeginDisabled(!valid);

        if (ImGui.Button("Save to library"u8))
            _message = _library.Save(made) is { } path ? $"Saved {Path.GetFileName(path)}." : "Not saved.";

        ImGui.SameLine();

        if (ImGui.Button("Implement it"u8))
            ProgramCommands.Implement(session, made);

        ImGui.EndDisabled();

        if (!valid)
            ImGui.TextDisabled($"A name, 1 to {ComponentInterface.MaxNames} calls and states, each named once.");

        ImGui.TreePop();
    }

    private static string Read(byte[] buffer)
    {
        var length = Array.IndexOf(buffer, (byte)0);
        return Encoding.UTF8.GetString(buffer, 0, length < 0 ? buffer.Length : length);
    }

    private static List<string> Names(byte[] buffer) =>
        Read(buffer).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    // ---- preview ----

    private void DrawPreview(HullProgram program, ProgramInstance? run)
    {
        ImGui.SeparatorText("Preview"u8);

        for (var c = 0; c < program.Interface.Calls.Count; c++)
        {
            if (c > 0)
                ImGui.SameLine();

            if (ImGui.Button($"{program.Interface.Calls[c]}##call{c}"))
                CallRequest = c;
        }

        ImGui.SameLine();

        if (ImGui.Button("Reset"u8))
            ResetRequest = true;

        ImGui.Checkbox("Something in the way"u8, ref Blocked);

        if (run is not null && run.State < program.Interface.States.Count)
            ImGui.Text($"State: {program.Interface.States[run.State]}{(run.Held ? " (stopped)" : "")}{(run.Node != 0 ? $", running node {run.Node}" : "")}");
    }

    // ---- the graph ----

    private void DrawGraph(DesignSession session, HullProgram program, ProgramInstance? run)
    {
        float2? size = new float2(0f, 380f);

        if (!ImGui.BeginChild("##graph"u8, size, ImGuiChildFlags.Borders, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoMove))
        {
            ImGui.EndChild();
            return;
        }

        var io = ImGui.GetIO();
        var corner = ImGui.GetCursorScreenPos();
        var avail = ImGui.GetContentRegionAvail();
        var list = ImGui.GetWindowDrawList();
        var hovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.None);
        list.AddRectFilled(corner, new float2(corner.X + avail.X, corner.Y + avail.Y), Canvas);

        if (hovered && ImGui.IsMouseDown(ImGuiMouseButton.Middle))
            _pan = new float2(_pan.X + io.MouseDelta.X, _pan.Y + io.MouseDelta.Y);

        var origin = new float2(corner.X + _pan.X, corner.Y + _pan.Y);

        for (var x = (_pan.X % 40f + 40f) % 40f; x < avail.X; x += 40f)
            list.AddLine(new float2(corner.X + x, corner.Y), new float2(corner.X + x, corner.Y + avail.Y), GridLine);

        for (var y = (_pan.Y % 40f + 40f) % 40f; y < avail.Y; y += 40f)
            list.AddLine(new float2(corner.X, corner.Y + y), new float2(corner.X + avail.X, corner.Y + y), GridLine);

        // Links under the nodes.
        foreach (var n in program.Nodes)
        {
            for (var k = 0; k < Outputs(n.Kind).Length; k++)
            {
                var to = k == 0 ? n.Out0 : n.Out1;
                var target = program.IndexOf(to);

                if (to != 0 && target >= 0)
                    Curve(list, OutPin(origin, n, k), InPin(origin, program.Nodes[target]), Link);
            }
        }

        var face = program.Interface;

        foreach (var node in program.Nodes)
            DrawNode(session, program, node, origin, list, run?.Node == node.Id);

        // A link being dragged: drop it on a node to connect.
        if (_linkFrom is { } from && program.IndexOf(from.Node) is var fi and >= 0)
        {
            var mouse = io.MousePos;
            Curve(list, OutPin(origin, program.Nodes[fi], from.Output), mouse, Running);

            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                foreach (var target in program.Nodes)
                {
                    var p = NodePos(origin, target);

                    if (target.HasInput && target.Id != from.Node && mouse.X >= p.X && mouse.X <= p.X + NodeWidth && mouse.Y >= p.Y && mouse.Y <= p.Y + Height(target.Kind))
                    {
                        var (id, output, to) = (from.Node, from.Output, target.Id);
                        ProgramCommands.Set(session, pr => Relink(pr, id, output, to));
                        break;
                    }
                }

                _linkFrom = null;
            }
        }

        // Right-click on empty canvas: add a node there.
        if (hovered && !ImGui.IsAnyItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right, false))
        {
            _addAt = new float2(io.MousePos.X - origin.X, io.MousePos.Y - origin.Y);
            ImGui.OpenPopup("addnode"u8, ImGuiPopupFlags.None);
        }

        if (ImGui.BeginPopup("addnode"u8, ImGuiWindowFlags.None))
        {
            ImGui.TextDisabled("Add node"u8);

            foreach (var kind in Enum.GetValues<NodeKind>())
            {
                if (!ImGui.MenuItem(NodeName(kind), default, false, true))
                    continue;

                var at = _addAt;

                if (program.Nodes.Count >= HullProgram.MaxNodes)
                {
                    _message = $"At most {HullProgram.MaxNodes} nodes.";
                    continue;
                }

                ProgramCommands.Set(session, p => p.Nodes.Add(new ProgramNode
                {
                    Id = p.NextNodeId(), Kind = kind, X = (short)Math.Clamp(at.X, short.MinValue, short.MaxValue), Y = (short)Math.Clamp(at.Y, short.MinValue, short.MaxValue),
                    B = kind == NodeKind.Wait ? (short)ProgramRunner.TicksPerSecond : (short)0,
                }));
            }

            ImGui.EndPopup();
        }

        if (ImGui.BeginPopup("nodemenu"u8, ImGuiWindowFlags.None))
        {
            if (ImGui.MenuItem("Delete node"u8, default, false, true))
            {
                var id = _menuNode;
                ProgramCommands.Set(session, p =>
                {
                    p.Nodes.RemoveAll(n => n.Id == id);

                    for (var i = 0; i < p.Nodes.Count; i++)
                    {
                        var n = p.Nodes[i];
                        n.Out0 = n.Out0 == id ? (ushort)0 : n.Out0;
                        n.Out1 = n.Out1 == id ? (ushort)0 : n.Out1;
                        p.Nodes[i] = n;
                    }
                });
            }

            ImGui.EndPopup();
        }

        _ = face;
        ImGui.EndChild();
    }

    private float2 NodePos(float2 origin, in ProgramNode n)
    {
        var p = new float2(origin.X + n.X, origin.Y + n.Y);
        return n.Id == _dragNode ? new float2(p.X + _dragDelta.X, p.Y + _dragDelta.Y) : p;
    }

    private float2 InPin(float2 origin, in ProgramNode n)
    {
        var p = NodePos(origin, n);
        return new float2(p.X, p.Y + TitleHeight * 0.5f);
    }

    private float2 OutPin(float2 origin, in ProgramNode n, int k)
    {
        var p = NodePos(origin, n);
        return new float2(p.X + NodeWidth, p.Y + TitleHeight + (HasParam(n.Kind) ? ParamHeight : 0) + k * OutHeight + OutHeight * 0.5f);
    }

    private static void Curve(ImDrawListPtr list, float2 a, float2 b, ImColor8 colour)
    {
        var bend = Math.Max(40f, Math.Abs(b.X - a.X) * 0.5f);
        list.AddBezierCubic(a, new float2(a.X + bend, a.Y), new float2(b.X - bend, b.Y), b, colour, 2f, 0);
    }

    private void DrawNode(DesignSession session, HullProgram program, ProgramNode node, float2 origin, ImDrawListPtr list, bool running)
    {
        var face = program.Interface;
        var p = NodePos(origin, node);
        var outs = Outputs(node.Kind);
        var height = Height(node.Kind);
        ImGui.PushID(node.Id);
        list.AddRectFilled(p, new float2(p.X + NodeWidth, p.Y + height), Body, 4f, ImDrawFlags.None);
        list.AddRectFilled(p, new float2(p.X + NodeWidth, p.Y + TitleHeight), TitleColour(node.Kind), 4f, ImDrawFlags.RoundCornersTop);
        list.AddRect(p, new float2(p.X + NodeWidth, p.Y + height), running ? Running : Border, 4f, ImDrawFlags.None, running ? 2.5f : 1f);
        list.AddText(new float2(p.X + 8, p.Y + 4), Text, NodeName(node.Kind));

        // Title: drag to move (kept on release), right-click for the node menu.
        ImGui.SetCursorScreenPos(p);
        ImGui.InvisibleButton("##title"u8, new float2(NodeWidth, TitleHeight), ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);

        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 0f))
        {
            var d = ImGui.GetIO().MouseDelta;
            _dragNode = node.Id;
            _dragDelta = new float2(_dragDelta.X + d.X, _dragDelta.Y + d.Y);
        }

        if (ImGui.IsItemDeactivated() && _dragNode == node.Id)
        {
            var (id, dx, dy) = (node.Id, (int)MathF.Round(_dragDelta.X), (int)MathF.Round(_dragDelta.Y));
            (_dragNode, _dragDelta) = (0, default);

            if (dx != 0 || dy != 0)
                ProgramCommands.Set(session, pr => Move(pr, id, dx, dy));
        }

        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            _menuNode = node.Id;
            ImGui.OpenPopup("nodemenu"u8, ImGuiPopupFlags.None);
        }

        if (node.HasInput)
            list.AddCircleFilled(InPin(origin, node), PinRadius, Pin, 12);

        // Its setting.
        if (HasParam(node.Kind))
        {
            ImGui.SetCursorScreenPos(new float2(p.X + 6, p.Y + TitleHeight + 2));
            ImGui.SetNextItemWidth(NodeWidth - 12);
            DrawParam(session, face, node);
        }

        // Outputs: drag onto a node to link, right-click to cut.
        for (var k = 0; k < outs.Length; k++)
        {
            var pin = OutPin(origin, node, k);
            var label = outs[k];
            list.AddText(new float2(pin.X - 12 - label.Length * 7f, pin.Y - 8), Faint, label);
            list.AddCircleFilled(pin, PinRadius, Pin, 12);
            ImGui.SetCursorScreenPos(new float2(pin.X - 8, pin.Y - 8));
            ImGui.InvisibleButton($"##out{k}", new float2(16, 16), ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);

            if (ImGui.IsItemActive() && _linkFrom is null)
                _linkFrom = (node.Id, k);

            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                var (id, output) = (node.Id, k);
                ProgramCommands.Set(session, pr => Relink(pr, id, output, 0));
            }
        }

        ImGui.PopID();
    }

    private void DrawParam(DesignSession session, ComponentInterface face, ProgramNode node)
    {
        var id = node.Id;

        switch (node.Kind)
        {
            case NodeKind.OnCall:
                Pick(session, id, node.A, face.Calls);
                break;
            case NodeKind.SetState or NodeKind.IfState:
                Pick(session, id, node.A, face.States);
                break;
            case NodeKind.Wait:
            {
                if (!_waitDraft.TryGetValue(id, out var seconds) || !ImGui.IsAnyItemActive())
                    seconds = node.B / (float)ProgramRunner.TicksPerSecond;

                ImGui.InputFloat("##wait"u8, ref seconds, 0f, 0f, "%.2f s"u8, ImGuiInputTextFlags.None);
                _waitDraft[id] = seconds;

                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    var ticks = (short)Math.Clamp((int)MathF.Round(seconds * ProgramRunner.TicksPerSecond), 0, short.MaxValue);
                    ProgramCommands.Set(session, pr => Edit(pr, id, n => n with { B = ticks }));
                }

                break;
            }
        }
    }

    private static void Pick(DesignSession session, ushort id, byte current, List<string> names)
    {
        if (!ImGui.BeginCombo("##pick"u8, current < names.Count ? names[current] : "(missing)", ImGuiComboFlags.None))
            return;

        for (var i = 0; i < names.Count; i++)
        {
            if (!ImGui.Selectable($"{names[i]}##n{i}", i == current, ImGuiSelectableFlags.None, default))
                continue;

            var a = (byte)i;
            ProgramCommands.Set(session, pr => Edit(pr, id, n => n with { A = a }));
        }

        ImGui.EndCombo();
    }

    private static ImColor8 TitleColour(NodeKind kind) => kind switch
    {
        NodeKind.OnCall => new ImColor8(70, 120, 200, 255),
        NodeKind.SetState => new ImColor8(70, 150, 100, 255),
        NodeKind.WaitArrived or NodeKind.Wait => new ImColor8(150, 110, 60, 255),
        _ => new ImColor8(120, 80, 150, 255),
    };

    private static void Edit(HullProgram program, ushort id, Func<ProgramNode, ProgramNode> change)
    {
        var i = program.IndexOf(id);

        if (i >= 0)
            program.Nodes[i] = change(program.Nodes[i]);
    }

    private static void Move(HullProgram program, ushort id, int dx, int dy) =>
        Edit(program, id, n => n with { X = (short)Math.Clamp(n.X + dx, short.MinValue, short.MaxValue), Y = (short)Math.Clamp(n.Y + dy, short.MinValue, short.MaxValue) });

    private static void Relink(HullProgram program, ushort id, int output, ushort to) =>
        Edit(program, id, n => output == 0 ? n with { Out0 = to } : n with { Out1 = to });

    // ---- poses ----

    private static void DrawPoses(DesignSession session, HullProgram program)
    {
        ImGui.SeparatorText("Poses"u8);
        var joints = session.Doc.Joints?.Joints;

        if (joints is null || joints.Count == 0)
        {
            ImGui.TextDisabled("No joints: add a hinge or slider and put the moving part's solids under it."u8);
            return;
        }

        ImGui.TextDisabled("Where each joint stands in each state (degrees; a slider's metres). Unticked: it stays where it is."u8);

        for (var s = 0; s < program.Interface.States.Count; s++)
        {
            ImGui.Text($"{program.Interface.States[s]}");

            foreach (var joint in joints)
            {
                ImGui.SameLine();
                ImGui.PushID(s * 1000 + (int)(joint.Id % 1000));
                var set = program.PoseOf(s, joint.Id, out var value);
                var unit = joint.Kind == JointKind.Slider ? 1000f : 10f;
                var shown = value / unit;

                if (ImGui.Checkbox("##set"u8, ref set))
                    ProgramCommands.SetPose(session, s, joint.Id, set ? joint.Target : null);

                ImGui.SameLine();
                ImGui.BeginDisabled(!set);
                ImGui.SetNextItemWidth(70f);
                ImGui.InputFloat($"#{joint.Id}", ref shown, 0f, 0f, "%.2f", ImGuiInputTextFlags.None);

                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    var stored = (short)Math.Clamp((int)MathF.Round(shown * unit), joint.Limited ? joint.Min : short.MinValue, joint.Limited ? joint.Max : short.MaxValue);
                    ProgramCommands.SetPose(session, s, joint.Id, stored);
                }

                ImGui.EndDisabled();
                ImGui.PopID();
            }
        }
    }
}
