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
/// it, right-click a node to delete it, right-click empty space to add one, middle-drag to pan; a condition node's orange
/// output goes to the "when" input of a Wait until or If); each state's pose (where every joint stands in it, or holds);
/// and the preview (a button per call, "Jam the moving parts" to stop them as something in the way would, power, a test
/// object for its sensors, the state it is in, the node running, highlighted, and every signal live). Its sensors and
/// effectors (D88) are listed and added here too: catalog cores, each on a joint or at a point of the component.
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
    private static readonly ImColor8 ConditionPin = new(255, 170, 70, 255);
    private static readonly ImColor8 ConditionLink = new(255, 170, 70, 200);
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
    private ushort _conditionFrom;
    private ushort _menuNode;
    private float2 _addAt;
    private string _message = "";
    private bool _refreshed;
    private readonly Dictionary<ushort, float> _waitDraft = new();

    /// <summary>The preview's requests, read and cleared by the host each frame.</summary>
    public int CallRequest = -1;
    public bool ResetRequest;

    /// <summary>The preview's "Jam the moving parts": they stop where they are, as if something were in the way.</summary>
    public bool Jammed;

    /// <summary>The preview's power switch, and how far in front of its sensors the test object is (metres; past <see cref="ProbeNone"/>: none).</summary>
    public bool Powered = true;
    public float ProbeDistance = ProbeNone;
    public const float ProbeNone = 15f;

    /// <summary>The catalog's cores (set by the host).</summary>
    public CoreCatalog Catalog = new();

    /// <summary>A tool (D91): the preview shows the holder's controls, read by its grip.</summary>
    public bool IsTool;
    public HolderInput Holder;

    private int _newCore;

    public ProgramPanel(InterfaceLibrary library) => _library = library;

    /// <summary>The nodes the Add menu offers, in its order (older kinds still run but are not offered).</summary>
    private static readonly NodeKind[] Offered =
    [
        NodeKind.OnCall, NodeKind.OnStart, NodeKind.SetState, NodeKind.SetInput, NodeKind.WaitUntil, NodeKind.Wait, NodeKind.If, NodeKind.IfState,
        NodeKind.JointAt, NodeKind.StateIs, NodeKind.InPose, NodeKind.SignalIs,
    ];

    private static readonly string[] CompareNames = ["=", "!=", "<", ">", "<=", ">="];

    public static string NodeName(NodeKind kind) => kind switch
    {
        NodeKind.OnCall => "On call",
        NodeKind.SetState => "Set state",
        NodeKind.WaitArrived => "Wait until in place (old)",
        NodeKind.Wait => "Wait",
        NodeKind.IfState => "If state is",
        NodeKind.IfBlocked => "If jammed (old)",
        NodeKind.WaitUntil => "Wait until",
        NodeKind.If => "If",
        NodeKind.StateIs => "State is",
        NodeKind.JointAt => "Joint is at",
        NodeKind.OnStart => "On start",
        NodeKind.SetInput => "Set input",
        NodeKind.SignalIs => "Signal is",
        _ => "Joints in pose",
    };

    private static string[] Outputs(NodeKind kind) => kind switch
    {
        NodeKind.WaitArrived => ["in place", "stopped"],
        NodeKind.WaitUntil => ["got there", "timed out"],
        NodeKind.IfState or NodeKind.IfBlocked or NodeKind.If => ["yes", "no"],
        NodeKind.StateIs or NodeKind.JointAt or NodeKind.InPose or NodeKind.SignalIs => [],
        _ => ["then"],
    };

    /// <summary>How many rows of settings a node has.</summary>
    private static int ParamRows(NodeKind kind) => kind switch
    {
        NodeKind.JointAt or NodeKind.SetInput or NodeKind.SignalIs => 2,
        NodeKind.OnCall or NodeKind.SetState or NodeKind.IfState or NodeKind.Wait or NodeKind.WaitUntil or NodeKind.StateIs => 1,
        _ => 0,
    };

    private static float Height(NodeKind kind) =>
        TitleHeight + ParamRows(kind) * ParamHeight + Math.Max(Outputs(kind).Length, kind is NodeKind.WaitUntil or NodeKind.If ? 1 : 0) * OutHeight + 6;

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
        DrawCores(session, run);
        ImGui.SeparatorText("Sequence"u8);

        if (ImGui.Button("Start from the standard door sequence"u8))
        {
            var ok = true;
            ProgramCommands.Set(session, p => ok = ProgramCommands.StandardDoor(p, session.Doc.Joints?.Joints ?? [], session.Doc.Cores));
            _message = ok ? "" : "The standard door sequence needs a door's calls (Open, Close, Lock, Unlock) and states (Closed, Opening, Open, Closing, Locked, Blocked).";
        }

        ImGui.TextDisabled("Drag titles to move, outputs onto a node to link (orange: a condition, onto a Wait until or If); right-click to add, delete or cut; middle-drag pans."u8);
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

        ImGui.Checkbox("Jam the moving parts"u8, ref Jammed);
        ImGui.SameLine();
        ImGui.TextDisabled("(they stop where they are, as if something were in the way)"u8);

        if (IsTool)
            DrawHolder();

        ImGui.Checkbox("Power"u8, ref Powered);
        ImGui.SameLine();
        DesignerStyle.ItemWidth(200f);
        ImGui.SliderFloat("Test object"u8, ref ProbeDistance, 0f, ProbeNone, ProbeDistance >= ProbeNone ? "none" : "%.2f m ahead", ImGuiSliderFlags.None);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Something in front of every proximity sensor, this far away (all the way right: nothing)"u8);

        if (run is not null && run.State < program.Interface.States.Count)
            ImGui.Text($"State: {program.Interface.States[run.State]}{(run.Held ? " (stopped)" : "")}{(run.Node != 0 ? $", running node {run.Node}" : "")}");

        if (run?.Cores is { Entries.Count: > 0 } cores)
            DrawSignals(cores, run);
    }

    /// <summary>The holder's controls: holding it, the trigger (held while the button is), the alternate trigger, the mode.</summary>
    private void DrawHolder()
    {
        ImGui.Checkbox("Held"u8, ref Holder.Holding);
        ImGui.SameLine();
        ImGui.Button("Trigger (hold)"u8);
        Holder.Trigger = ImGui.IsItemActive();
        ImGui.SameLine();
        ImGui.Checkbox("Alt"u8, ref Holder.Alt);
        ImGui.SameLine();
        int mode = Holder.Mode;
        DesignerStyle.ItemWidth(90f);
        ImGui.SliderInt("Mode"u8, ref mode, 0, 3, "%d", ImGuiSliderFlags.None);
        Holder.Mode = (byte)mode;
    }

    /// <summary>Every core's signals as they are now: value in its unit, or dead (no power, no reading yet).</summary>
    private static void DrawSignals(CoreLayout cores, ProgramInstance run)
    {
        if (!ImGui.BeginTable("##signals"u8, 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("Core"u8, ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableSetupColumn("Signal"u8, ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Value"u8, ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableHeadersRow();

        foreach (var e in cores.Entries)
        {
            for (var s = 0; s < e.Def.Signals.Count; s++)
            {
                var signal = e.Def.Signals[s];
                ImGui.TableNextRow();
                ImGui.TableNextColumn();

                if (s == 0)
                    ImGui.Text($"{e.Def.Name} #{e.Id}");

                ImGui.TableNextColumn();
                ImGui.TextDisabled($"{signal.Name}{(signal.Input ? " (in)" : "")}");
                ImGui.TableNextColumn();

                if (run.IsLive(e.SignalBase + s))
                    ImGui.Text($"{Show(signal, run.Signals[e.SignalBase + s])}");
                else
                    ImGui.TextDisabled("dead"u8);
            }
        }

        ImGui.EndTable();
        ImGui.TextDisabled($"{cores.PowerW:0.#} W, declared {cores.TickCostNs} ns a tick{(cores.Missing > 0 ? $", {cores.Missing} core(s) missing from the catalog or their joint" : "")}");
    }

    /// <summary>A signal's value in counts, shown in its unit (a 0..1 signal as yes or no).</summary>
    private static string Show(CoreSignal signal, int counts) =>
        signal is { Min: 0, Max: 1, Step: 1 } ? counts != 0 ? "yes" : "no" : $"{counts * signal.Step:0.###} {(signal.Unit == "joint" ? "" : signal.Unit)}";

    // ---- sensors and effectors ----

    private void DrawCores(DesignSession session, ProgramInstance? run)
    {
        ImGui.SeparatorText("Sensors and effectors"u8);
        ImGui.TextDisabled("Catalog cores: sensors measure, effectors act through their inputs. A joint's motor drives it to each state's pose."u8);
        var all = Catalog.All;

        if (all.Count == 0)
        {
            ImGui.TextDisabled("No cores in the catalog (data/sensors, data/effectors)."u8);
            return;
        }

        _newCore = Math.Clamp(_newCore, 0, all.Count - 1);
        DesignerStyle.ItemWidth(220f);

        if (ImGui.BeginCombo("##newcore"u8, $"{all[_newCore].Role}: {all[_newCore].Name}", ImGuiComboFlags.None))
        {
            for (var i = 0; i < all.Count; i++)
            {
                if (ImGui.Selectable($"{all[i].Role}: {all[i].Name}##def{i}", i == _newCore, ImGuiSelectableFlags.None, default))
                    _newCore = i;
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();

        if (ImGui.Button("Add"u8))
        {
            var def = all[_newCore];
            var joint = def.NeedsJoint ? session.Doc.Joints?.Joints.FirstOrDefault()?.Id ?? 0 : 0;

            if (def.NeedsJoint && joint == 0)
                _message = $"{def.Name} reads or drives a joint: add a hinge or slider first.";
            else if (CoreCommands.Add(session, def.Key, joint, default, new Vector3d(0, 0, 1)) == 0)
                _message = $"At most {HullCoreSet.MaxCores} cores.";
        }

        if (session.Doc.Cores is not { } set)
            return;

        foreach (var core in set.Cores)
        {
            ImGui.PushID((int)core.Id);
            var known = Catalog.TryGet(core.Def, out var def);
            ImGui.Text($"{(known ? def.Name : "Unknown core")} #{core.Id}");
            ImGui.SameLine();

            if (ImGui.SmallButton("Remove"u8))
            {
                CoreCommands.Remove(session, core.Id);
                ImGui.PopID();
                break;
            }

            if (known && def.NeedsJoint)
                CoreJoint(session, core, def);
            else if (known)
                CorePlace(session, core);

            ImGui.PopID();
        }
    }

    /// <summary>The joint a core reads or drives, and a limit switch's trip position.</summary>
    private static void CoreJoint(DesignSession session, HullCore core, CoreDef def)
    {
        var joints = session.Doc.Joints?.Joints;
        var joint = joints?.Find(j => j.Id == core.Joint);
        var id = core.Id;
        DesignerStyle.ItemWidth(150f);

        if (ImGui.BeginCombo("Joint"u8, joint is null ? "(pick a joint)" : $"{JointsPanel.KindName(joint.Kind)} #{joint.Id}", ImGuiComboFlags.None))
        {
            foreach (var j in joints ?? [])
            {
                var pick = j.Id;

                if (ImGui.Selectable($"{JointsPanel.KindName(j.Kind)} #{j.Id}", j.Id == core.Joint, ImGuiSelectableFlags.None, default))
                    CoreCommands.Set(session, id, c => c.Joint = pick);
            }

            ImGui.EndCombo();
        }

        if (def.Kind != CoreKind.JointLimit)
            return;

        ImGui.SameLine();
        var unit = joint?.Kind == JointKind.Slider ? 1000f : 10f;
        var at = core.At / unit;
        DesignerStyle.ItemWidth(110f);
        ImGui.InputFloat("Trips at"u8, ref at, 0f, 0f, joint?.Kind == JointKind.Slider ? "%.3f m" : "%.1f deg", ImGuiInputTextFlags.None);

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            var stored = (short)Math.Clamp((int)MathF.Round(at * unit), short.MinValue, short.MaxValue);
            CoreCommands.Set(session, id, c => c.At = stored);
        }
    }

    /// <summary>Where a core without a joint sits (metres, component space); it faces the way it was placed.</summary>
    private static void CorePlace(DesignSession session, HullCore core)
    {
        var p = core.Position;
        var v = new float3((float)p.X, (float)p.Y, (float)p.Z);
        DesignerStyle.ItemWidth(260f);
        ImGui.DragFloat3("At (m)"u8, ref v, 0.01f, -100f, 100f, "%.2f", ImGuiSliderFlags.None);

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            var (x, y, z) = ComponentCommands.Quantise(new Vector3d(v.X, v.Y, v.Z));
            CoreCommands.Set(session, core.Id, c => (c.X, c.Y, c.Z) = (x, y, z));
        }
    }

    // ---- the graph ----

    private void DrawGraph(DesignSession session, HullProgram program, ProgramInstance? run)
    {
        float2? size = new float2(0f, 480f);

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

            if (n.TakesCondition && n.In != 0 && program.IndexOf(n.In) is var c and >= 0)
                Curve(list, ConditionOut(origin, program.Nodes[c]), ConditionIn(origin, n), ConditionLink);
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

                    if (target.HasInput && target.Id != from.Node && Inside(mouse, p, target.Kind))
                    {
                        var (id, output, to) = (from.Node, from.Output, target.Id);
                        ProgramCommands.Set(session, pr => Relink(pr, id, output, to));
                        break;
                    }
                }

                _linkFrom = null;
            }
        }

        // A condition being dragged: drop it on a Wait until or If.
        if (_conditionFrom != 0 && program.IndexOf(_conditionFrom) is var ci and >= 0)
        {
            var mouse = io.MousePos;
            Curve(list, ConditionOut(origin, program.Nodes[ci]), mouse, Running);

            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                foreach (var target in program.Nodes)
                {
                    if (target.TakesCondition && Inside(mouse, NodePos(origin, target), target.Kind))
                    {
                        var (id, condition) = (target.Id, _conditionFrom);
                        ProgramCommands.Set(session, pr => Edit(pr, id, n => n with { In = condition }));
                        break;
                    }
                }

                _conditionFrom = 0;
            }
        }
        else
        {
            _conditionFrom = 0;
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

            foreach (var kind in Offered)
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
                    B = kind switch { NodeKind.Wait => (short)ProgramRunner.TicksPerSecond, NodeKind.WaitUntil => (short)(5 * ProgramRunner.TicksPerSecond), _ => (short)0 },
                    A = kind == NodeKind.JointAt ? ProgramCommands.PositionTolerance : (byte)0,
                    Joint = kind == NodeKind.JointAt ? session.Doc.Joints?.Joints.FirstOrDefault()?.Id ?? 0
                        : kind is NodeKind.SetInput or NodeKind.SignalIs ? session.Doc.Cores?.Cores.FirstOrDefault()?.Id ?? 0 : 0,
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
                        n.In = n.In == id ? (ushort)0 : n.In;
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
        return new float2(p.X + NodeWidth, p.Y + TitleHeight + ParamRows(n.Kind) * ParamHeight + k * OutHeight + OutHeight * 0.5f);
    }

    /// <summary>A condition's output: in its title, on the right.</summary>
    private float2 ConditionOut(float2 origin, in ProgramNode n)
    {
        var p = NodePos(origin, n);
        return new float2(p.X + NodeWidth, p.Y + TitleHeight * 0.5f);
    }

    /// <summary>A Wait until's or If's "when" input: on the left, level with its first output.</summary>
    private float2 ConditionIn(float2 origin, in ProgramNode n)
    {
        var p = NodePos(origin, n);
        return new float2(p.X, p.Y + TitleHeight + ParamRows(n.Kind) * ParamHeight + OutHeight * 0.5f);
    }

    private static bool Inside(float2 mouse, float2 p, NodeKind kind) =>
        mouse.X >= p.X && mouse.X <= p.X + NodeWidth && mouse.Y >= p.Y && mouse.Y <= p.Y + Height(kind);

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

        // A condition's output: drag onto a Wait until or If.
        if (node.IsCondition)
        {
            var pin = ConditionOut(origin, node);
            list.AddCircleFilled(pin, PinRadius, ConditionPin, 12);
            ImGui.SetCursorScreenPos(new float2(pin.X - 8, pin.Y - 8));
            ImGui.InvisibleButton("##cond"u8, new float2(16, 16), ImGuiButtonFlags.MouseButtonLeft);

            if (ImGui.IsItemActive() && _conditionFrom == 0)
                _conditionFrom = node.Id;
        }

        // A Wait until's or If's "when": right-click to cut.
        if (node.TakesCondition)
        {
            var pin = ConditionIn(origin, node);
            list.AddCircleFilled(pin, PinRadius, ConditionPin, 12);
            list.AddText(new float2(pin.X + 9, pin.Y - 8), Faint, node.In == 0 ? "when (link a condition)" : "when");
            ImGui.SetCursorScreenPos(new float2(pin.X - 8, pin.Y - 8));
            ImGui.InvisibleButton("##when"u8, new float2(16, 16), ImGuiButtonFlags.MouseButtonRight);

            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                var id = node.Id;
                ProgramCommands.Set(session, pr => Edit(pr, id, n => n with { In = 0 }));
            }
        }

        // Its settings.
        if (ParamRows(node.Kind) > 0)
            DrawParam(session, face, node, p);

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

    private void DrawParam(DesignSession session, ComponentInterface face, ProgramNode node, float2 p)
    {
        var id = node.Id;
        ImGui.SetCursorScreenPos(new float2(p.X + 6, p.Y + TitleHeight + 2));
        ImGui.SetNextItemWidth(NodeWidth - 12);

        switch (node.Kind)
        {
            case NodeKind.OnCall:
                Pick(session, id, node.A, face.Calls);
                break;
            case NodeKind.SetState or NodeKind.IfState or NodeKind.StateIs:
                Pick(session, id, node.A, face.States);
                break;
            case NodeKind.Wait:
                Seconds(session, node, "%.2f s");
                break;
            case NodeKind.WaitUntil:
                Seconds(session, node, node.B == 0 ? "for ever" : "time out %.2f s");
                break;
            case NodeKind.JointAt:
                JointAt(session, node, p);
                break;
            case NodeKind.SetInput or NodeKind.SignalIs:
                CoreParam(session, node, p);
                break;
        }
    }

    /// <summary>
    /// A Set input or Signal is: which core, then which of its inputs and the value (Set input), or which signal, the
    /// comparison and the value (Signal is); values in the signal's unit, stored as counts of its step.
    /// </summary>
    private void CoreParam(DesignSession session, ProgramNode node, float2 p)
    {
        var id = node.Id;
        var cores = session.Doc.Cores?.Cores;
        var core = cores?.Find(c => c.Id == node.Joint);
        var def = core is not null && Catalog.TryGet(core.Def, out var d) ? d : null;

        if (ImGui.BeginCombo("##core"u8, def is null ? "(pick a core)" : $"{def.Name} #{core!.Id}", ImGuiComboFlags.None))
        {
            foreach (var c in cores ?? [])
            {
                var pick = c.Id;
                var name = Catalog.TryGet(c.Def, out var cd) ? cd.Name : "Unknown core";

                if (ImGui.Selectable($"{name} #{c.Id}", c.Id == node.Joint, ImGuiSelectableFlags.None, default))
                    ProgramCommands.Set(session, pr => Edit(pr, id, n => n with { Joint = pick, A = 0 }));
            }

            ImGui.EndCombo();
        }

        if (def is null)
            return;

        // The signals this node can name: a Set input only the core's inputs (A counts inputs); a Signal is any (A & 15).
        var setting = node.Kind == NodeKind.SetInput;
        var names = new List<(string Name, int Signal)>();

        for (var s = 0; s < def.Signals.Count; s++)
        {
            if (!setting || def.Signals[s].Input)
                names.Add((def.Signals[s].Name, s));
        }

        var current = setting ? node.A : node.A & 15;
        ImGui.SetCursorScreenPos(new float2(p.X + 6, p.Y + TitleHeight + ParamHeight + 2));
        ImGui.SetNextItemWidth((NodeWidth - 24) * (setting ? 0.5f : 0.4f));

        if (ImGui.BeginCombo("##signal"u8, current < names.Count ? names[current].Name : "(none)", ImGuiComboFlags.None))
        {
            for (var i = 0; i < names.Count; i++)
            {
                if (!ImGui.Selectable($"{names[i].Name}##s{i}", i == current, ImGuiSelectableFlags.None, default))
                    continue;

                var pick = (byte)i;
                ProgramCommands.Set(session, pr => Edit(pr, id, n => n with { A = setting ? pick : (byte)((n.A & 0xF0) | pick) }));
            }

            ImGui.EndCombo();
        }

        if (!setting)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth((NodeWidth - 24) * 0.22f);
            var op = node.A >> 4;

            if (ImGui.BeginCombo("##op"u8, op < CompareNames.Length ? CompareNames[op] : "?", ImGuiComboFlags.NoArrowButton))
            {
                for (var i = 0; i < CompareNames.Length; i++)
                {
                    if (!ImGui.Selectable(CompareNames[i], i == op, ImGuiSelectableFlags.None, default))
                        continue;

                    var pick = (byte)(i << 4);
                    ProgramCommands.Set(session, pr => Edit(pr, id, n => n with { A = (byte)((n.A & 15) | pick) }));
                }

                ImGui.EndCombo();
            }
        }

        if (current >= names.Count)
            return;

        var signal = def.Signals[names[current].Signal];
        var value = (float)(node.B * signal.Step);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputFloat("##value"u8, ref value, 0f, 0f, signal.Step >= 1 ? "%.0f" : "%.2f", ImGuiInputTextFlags.None);

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            var counts = (short)Math.Clamp(ProgramRunner.Quantise(value, signal.Step), short.MinValue, short.MaxValue);
            ProgramCommands.Set(session, pr => Edit(pr, id, n => n with { B = counts }));
        }
    }

    /// <summary>A Wait's or Wait until's time, in seconds (stored in ticks).</summary>
    private void Seconds(DesignSession session, ProgramNode node, string format)
    {
        var id = node.Id;

        if (!_waitDraft.TryGetValue(id, out var seconds) || !ImGui.IsAnyItemActive())
            seconds = node.B / (float)ProgramRunner.TicksPerSecond;

        ImGui.InputFloat("##seconds"u8, ref seconds, 0f, 0f, format, ImGuiInputTextFlags.None);
        _waitDraft[id] = seconds;

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            var ticks = (short)Math.Clamp((int)MathF.Round(seconds * ProgramRunner.TicksPerSecond), 0, short.MaxValue);
            ProgramCommands.Set(session, pr => Edit(pr, id, n => n with { B = ticks }));
        }
    }

    /// <summary>A Joint is at: which joint, then where (degrees, or a slider's metres) and how near.</summary>
    private void JointAt(DesignSession session, ProgramNode node, float2 p)
    {
        var id = node.Id;
        var joints = session.Doc.Joints?.Joints;
        var joint = joints?.Find(j => j.Id == node.Joint);

        if (ImGui.BeginCombo("##joint"u8, joint is null ? "(pick a joint)" : $"{JointsPanel.KindName(joint.Kind)} #{joint.Id}", ImGuiComboFlags.None))
        {
            foreach (var j in joints ?? [])
            {
                var pick = j.Id;

                if (ImGui.Selectable($"{JointsPanel.KindName(j.Kind)} #{j.Id}", j.Id == node.Joint, ImGuiSelectableFlags.None, default))
                    ProgramCommands.Set(session, pr => Edit(pr, id, n => n with { Joint = pick }));
            }

            ImGui.EndCombo();
        }

        var unit = joint?.Kind == JointKind.Slider ? 1000f : 10f;
        var at = node.B / unit;
        var near = node.A / unit;
        ImGui.SetCursorScreenPos(new float2(p.X + 6, p.Y + TitleHeight + ParamHeight + 2));
        ImGui.SetNextItemWidth((NodeWidth - 18) * 0.55f);
        ImGui.InputFloat("##at"u8, ref at, 0f, 0f, joint?.Kind == JointKind.Slider ? "%.3f m" : "%.1f deg", ImGuiInputTextFlags.None);

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            var stored = (short)Math.Clamp((int)MathF.Round(at * unit), short.MinValue, short.MaxValue);
            ProgramCommands.Set(session, pr => Edit(pr, id, n => n with { B = stored }));
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth((NodeWidth - 18) * 0.45f);
        ImGui.InputFloat("##near"u8, ref near, 0f, 0f, joint?.Kind == JointKind.Slider ? "+-%.3f" : "+-%.1f", ImGuiInputTextFlags.None);

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            var stored = (byte)Math.Clamp((int)MathF.Round(near * unit), 0, 255);
            ProgramCommands.Set(session, pr => Edit(pr, id, n => n with { A = stored }));
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
        NodeKind.WaitArrived or NodeKind.Wait or NodeKind.WaitUntil => new ImColor8(150, 110, 60, 255),
        NodeKind.StateIs or NodeKind.JointAt or NodeKind.InPose or NodeKind.SignalIs => new ImColor8(170, 100, 40, 255),
        NodeKind.OnStart => new ImColor8(70, 120, 200, 255),
        NodeKind.SetInput => new ImColor8(60, 140, 140, 255),
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
