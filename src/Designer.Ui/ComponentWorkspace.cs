using System.Numerics;
using Brutal.ImGuiApi;
using BrutalGame.Chemistry;
using BrutalGame.Plans;
using BrutalGame.Ships;
using BrutalGame.Ships.Components;
using static BrutalGame.Ships.HullSurface;

namespace BrutalGame.Designer.Ui;

/// <summary>
/// The component editor: its own plan (a component level document: housing solids, parts, pipes), the Parts panel
/// (add parts, edit their numbers, pipe from a free port, edit pipe nodes), the Test bench (feed the open inlets, spark,
/// watch thrust, pressures and pipe ratings), and the viewport tool (pick parts and pipe nodes, drag them with arrows;
/// parts snap port to port).
/// </summary>
public sealed class ComponentWorkspace
{
    public const double Grid = 0.01;
    private const int History = 240;
    private const float HandlePixels = 10f;
    private const double PortHalf = 0.012;

    private static readonly uint FreePort = OverlayRenderer.Rgba(255, 150, 40);
    private static readonly uint JoinedPort = OverlayRenderer.Rgba(90, 220, 120);
    private static readonly uint SparkPort = OverlayRenderer.Rgba(120, 170, 255);
    private static readonly uint NodeColour = OverlayRenderer.Rgba(235, 235, 235);
    private static readonly uint SelectedColour = OverlayRenderer.Rgba(255, 230, 120);
    private static readonly string[] Feeds = ["Fuel", "Oxidiser"];
    private static readonly string[] Axes = ["Yaw", "Pitch", "Roll"];

    private readonly HullMaterialCatalog _catalog;
    private readonly ChemistryData _chemistry;
    private readonly TransformGizmo _gizmo = new();
    private readonly List<SurfacePolygon> _surface = new();
    private readonly Dictionary<uint, List<SurfacePolygon>> _partSurfaces = new();
    private int _surfaceVersion = -1;
    private DesignSession? _surfaceSession;

    private ComponentModel? _model;
    private bool _running;
    private bool _seaLevel;
    private int _modelVersion = -1;
    private readonly List<Supply> _supplies = new();
    private readonly float[] _thrust = new float[History];
    private readonly float[] _pressure = new float[History];
    private int _samples;
    private string _bench = "";
    private Draft _draft;

    private struct Draft
    {
        public uint Part;
        public int Version;
        public float X, Y, Z;
        public int P0, P1, P2, P3, P4, P5;
        public int Outer, Inner;
        public float NodeX, NodeY, NodeZ;
    }

    public ComponentWorkspace(HullMaterialCatalog catalog, ChemistryData chemistry)
    {
        _catalog = catalog;
        _chemistry = chemistry;
    }

    public uint SelectedPart;
    public uint SelectedPipe;

    /// <summary>The run of the selected pipe whose size the panel edits.</summary>
    public int SelectedRun = -1;
    public int SelectedNode = -1;

    /// <summary>The parts' and pipes' surfaces (component metres) for the exact-surface build; rebuilt when the plan changes.</summary>
    public List<SurfacePolygon> Surfaces(DesignSession session)
    {
        if (_surfaceVersion == session.Version && ReferenceEquals(_surfaceSession, session))
            return _surface;

        _surfaceVersion = session.Version;
        _surfaceSession = session;
        _surface.Clear();
        _partSurfaces.Clear();
        var data = session.Doc.Components;

        if (data is null)
            return _surface;

        foreach (var p in data.Parts)
        {
            var list = new List<SurfacePolygon>();
            PartGeometry.Surface(p, list);
            _partSurfaces[p.Id] = list;
            _surface.AddRange(list);
        }

        foreach (var pipe in data.Pipes)
            PipeGeometry.Surface(pipe, _surface);

        return _surface;
    }

    /// <summary>The Delete key: removes the selected part, or the selected pipe node (the whole pipe when it would drop below two). False when nothing of the component is selected.</summary>
    public bool DeleteSelected(DesignSession session)
    {
        var data = session.Doc.Components;

        if (data is null)
            return false;

        if (data.IndexOfPart(SelectedPart) >= 0)
        {
            ComponentCommands.RemovePart(session, SelectedPart);
            SelectedPart = 0;
            return true;
        }

        var index = data.IndexOfPipe(SelectedPipe);

        if (index < 0)
            return false;

        var pipe = data.Pipes[index];
        var node = SelectedNode;

        if (node >= 0 && node < pipe.Nodes.Count && pipe.Nodes.Count > 2)
        {
            ComponentCommands.RemovePipeNode(session, pipe.Id, node);
            SelectedNode = Math.Max(0, node - 1);
        }
        else
        {
            ComponentCommands.RemovePipe(session, pipe.Id);
            (SelectedPipe, SelectedNode) = (0, -1);
        }

        return true;
    }

    /// <summary>Add menu: a part of <paramref name="kind"/>, laid out in a row beside the others, selected.</summary>
    public void AddPart(DesignSession session, PartKind kind)
    {
        var count = session.Doc.Components?.Parts.Count ?? 0;
        SelectedPart = ComponentCommands.AddPart(session, kind, DefaultMaterial(kind), new Vector3d(0.6 * (count % 4), 0, 0.6 * (count / 4)));
        (SelectedPipe, SelectedNode) = (0, -1);
    }

    /// <summary>Add menu: an open two-node pipe at <paramref name="at"/> (drag a free end onto a port to attach it).</summary>
    public void AddPipe(DesignSession session, Vector3d at)
    {
        SelectedPipe = ComponentCommands.AddPipe(session, PipeEnd.Open, PipeEnd.Open, [at, Add(at, new Vector3d(0, 0, 0.3))], 40, 32, DefaultMaterial(PartKind.Injector));
        (SelectedPart, SelectedNode) = (0, 1);
    }

    // ---- panels ----

    public void DrawPanels(DesignSession session)
    {
        DrawParts(session);
        DrawBench(session);
    }

    private void DrawParts(DesignSession session)
    {
        if (ImGui.Begin("Parts"u8))
        {
            var data = session.Doc.Components;
            ImGui.SeparatorText("Parts"u8);

            if (data is null || data.Parts.Count == 0)
                ImGui.TextDisabled("No parts yet (right-click empty space in the view > a part). Parts snap together port to port when moved close."u8);

            if (data is not null)
            {
                foreach (var p in data.Parts)
                {
                    var label = p.Kind == PartKind.Injector ? $"#{p.Id}  {Feeds[Math.Clamp(p.P3, 0, 1)]} injector" : $"#{p.Id}  {PartGeometry.KindNames[(int)p.Kind]}";

                    if (ImGui.Selectable($"{label}##part{p.Id}", p.Id == SelectedPart, ImGuiSelectableFlags.None, default))
                        (SelectedPart, SelectedPipe, SelectedNode) = (p.Id, 0, -1);
                }

                DrawSelectedPart(session, data);
                ImGui.SeparatorText("Pipes"u8);

                foreach (var pipe in data.Pipes)
                {
                    if (ImGui.Selectable($"#{pipe.Id}  pipe {pipe.OuterMm}/{pipe.InnerMm} mm, {pipe.Length:F2} m##pipe{pipe.Id}", pipe.Id == SelectedPipe, ImGuiSelectableFlags.None, default))
                        (SelectedPipe, SelectedPart, SelectedNode) = (pipe.Id, 0, -1);
                }

                DrawSelectedPipe(session, data);
            }
        }

        ImGui.End();
    }

    private void DrawSelectedPart(DesignSession session, ComponentData data)
    {
        var index = data.IndexOfPart(SelectedPart);

        if (index < 0)
            return;

        var part = data.Parts[index];

        if (_draft.Part != part.Id || _draft.Version != session.Version)
        {
            var pos = part.Position;
            _draft = new Draft
            {
                Part = part.Id, Version = session.Version, X = (float)pos.X, Y = (float)pos.Y, Z = (float)pos.Z,
                P0 = part.P0, P1 = part.P1, P2 = part.P2, P3 = part.P3, P4 = part.P4, P5 = part.P5,
            };
        }

        ImGui.SeparatorText($"{PartGeometry.KindNames[(int)part.Kind]} #{part.Id}");
        var material = CatalogIndex(session.Doc, part.Material);

        if (MaterialCombo("Material##part"u8, ref material))
            ComponentCommands.SetPartMaterial(session, part.Id, _catalog.All[material].Id);

        var moved = Coord("X##p"u8, ref _draft.X) | Coord("Y##p"u8, ref _draft.Y) | Coord("Z##p"u8, ref _draft.Z);

        if (moved)
        {
            var after = part;
            (after.PosX, after.PosY, after.PosZ) = ComponentCommands.Quantise(new Vector3d(_draft.X, _draft.Y, _draft.Z));
            ComponentCommands.SetPart(session, after);
        }

        var (yaw, pitch, roll) = SolidRotation.ToSteps(part.Rotation);
        int[] steps = [yaw, pitch, roll];

        for (var a = 0; a < 3; a++)
        {
            ImGui.Text($"{Axes[a]} {steps[a] * SolidRotation.DegreesPerStep} deg");
            ImGui.SameLine();

            if (ImGui.Button($"-##rot{a}"))
                Rotate(session, part, a, -1);

            ImGui.SameLine();

            if (ImGui.Button($"+##rot{a}"))
                Rotate(session, part, a, 1);
        }

        var names = PartGeometry.ParameterNames(part.Kind);
        var changed = false;

        for (var i = 0; i < ComponentPart.ParamCount; i++)
        {
            if (names[i].Length == 0)
                continue;

            ref var value = ref Param(ref _draft, i);
            DesignerStyle.ItemWidth(110f);
            ImGui.InputInt($"{names[i]}##param{i}", ref value, 0, 0, ImGuiInputTextFlags.None);
            changed |= ImGui.IsItemDeactivatedAfterEdit();
        }

        if (part.Kind == PartKind.Injector)
        {
            var feed = Math.Clamp(_draft.P3, 0, 1);
            DesignerStyle.ItemWidth(110f);

            if (Combo("Feeds"u8, ref feed, Feeds))
            {
                _draft.P3 = feed;
                changed = true;
            }
        }

        if (changed)
        {
            var after = part;

            for (var i = 0; i < ComponentPart.ParamCount; i++)
                after[i] = Math.Max(names[i].Length == 0 ? part[i] : Param(ref _draft, i), part.Kind == PartKind.Injector && i == 3 ? 0 : 1);

            ComponentCommands.SetPart(session, after);
        }

        if (part.Kind == PartKind.Nozzle)
        {
            var nozzle = PartGeometry.Nozzle(part);
            ImGui.TextDisabled($"expansion {nozzle.ExpansionRatio:F1}, length {nozzle.Length * 1000:F0} mm{(nozzle.IsValid ? "" : "  (bell does not fit: change the angles or length)")}");
        }

        ImGui.Text("Ports"u8);
        var links = ComponentModel.Links(data);
        var ports = PartGeometry.Ports(part);

        for (var i = 0; i < ports.Length; i++)
        {
            var piped = data.Pipes.Any(pp => pp.NodeOn(part.Id, i) >= 0);
            var joined = ComponentModel.Partner(links, part.Id, i, out var other, out var otherPort);
            var state = joined ? $"on #{other} {PartGeometry.Ports(data.Parts[data.IndexOfPart(other)])[otherPort].Name}" : piped ? "piped" : "free";
            ImGui.BulletText($"{ports[i].Name}: {state}");

            if (!joined && !piped && ports[i].Kind == PortKind.Flow)
            {
                ImGui.SameLine();

                if (ImGui.Button($"Pipe from here##port{i}"))
                {
                    var (p, d) = PartGeometry.PortAt(part, i);
                    var end = Add(p, Scale(d, 0.3));
                    SelectedPipe = ComponentCommands.AddPipe(session, new PipeEnd(part.Id, (byte)i), PipeEnd.Open, [p, end], 40, 32, DefaultMaterial(PartKind.Injector));
                    (SelectedPart, SelectedNode) = (0, 1);
                }
            }
        }

    }

    private void DrawSelectedPipe(DesignSession session, ComponentData data)
    {
        var index = data.IndexOfPipe(SelectedPipe);

        if (index < 0)
            return;

        var pipe = data.Pipes[index];

        if (SelectedNode >= 0 && SelectedNode < pipe.Nodes.Count && pipe.RunAt(SelectedNode) is var at and >= 0 && (SelectedRun < 0 || SelectedRun >= pipe.Runs.Count))
            SelectedRun = at;

        SelectedRun = Math.Min(SelectedRun, pipe.Runs.Count - 1);
        var run = SelectedRun >= 0 ? pipe.Runs[SelectedRun] : default;

        if (_draft.Part != pipe.Id + 0x80000000u || _draft.Version != session.Version)
        {
            _draft = new Draft { Part = pipe.Id + 0x80000000u, Version = session.Version, Outer = run.OuterMm, Inner = run.InnerMm };

            if (SelectedNode >= 0 && SelectedNode < pipe.Nodes.Count)
            {
                var n = pipe.Node(SelectedNode);
                (_draft.NodeX, _draft.NodeY, _draft.NodeZ) = ((float)n.X, (float)n.Y, (float)n.Z);
            }
        }

        var junctions = Enumerable.Range(0, pipe.Nodes.Count).Count(n => pipe.Degree(n) >= 3);
        ImGui.SeparatorText($"Pipe #{pipe.Id}: {pipe.Runs.Count} run{(pipe.Runs.Count == 1 ? "" : "s")}{(junctions > 0 ? $", {junctions} junction{(junctions == 1 ? "" : "s")}" : "")}");
        var material = CatalogIndex(session.Doc, pipe.Material);

        if (MaterialCombo("Material##pipe"u8, ref material))
            ComponentCommands.SetPipeMaterial(session, pipe.Id, _catalog.All[material].Id);

        var yield = Yield(session.Doc, pipe.Material);
        ImGui.Text($"Yield {yield} MPa: the pipe bursts above {pipe.Rating(yield) / 1e5:F0} bar (its weakest run)");

        // Runs: pick one to size it.
        for (var i = 0; i < pipe.Runs.Count; i++)
        {
            var r = pipe.Runs[i];

            if (ImGui.Selectable($"run {r.A}-{r.B}: {r.OuterMm}/{r.InnerMm} mm, {pipe.RunLength(r):F2} m, {r.Rating(yield) / 1e5:F0} bar##run{i}", i == SelectedRun, ImGuiSelectableFlags.None, default))
            {
                SelectedRun = i;
                _draft.Version = -1;
            }
        }

        if (SelectedRun >= 0)
        {
            DesignerStyle.ItemWidth(110f);
            ImGui.InputInt("Outer diameter (mm)"u8, ref _draft.Outer, 0, 0, ImGuiInputTextFlags.None);
            var sized = ImGui.IsItemDeactivatedAfterEdit();
            DesignerStyle.ItemWidth(110f);
            ImGui.InputInt("Inner diameter (mm)"u8, ref _draft.Inner, 0, 0, ImGuiInputTextFlags.None);
            sized |= ImGui.IsItemDeactivatedAfterEdit();

            if (sized)
            {
                var outer = (ushort)Math.Clamp(_draft.Outer, 2, 2000);
                var inner = (ushort)Math.Clamp(_draft.Inner, 1, outer - 1);
                ComponentCommands.SetPipeRun(session, pipe.Id, SelectedRun, outer, inner);
            }

            ImGui.TextDisabled($"Wall {run.WallMetres * 1000:F1} mm");
        }

        // Nodes: what each sits on.
        ImGui.Text("Nodes (click one in the view; the bar beside it adds or deletes)"u8);

        for (var i = 0; i < pipe.Nodes.Count; i++)
        {
            var n = pipe.Node(i);
            var end = pipe.EndAt(i);
            var what = !end.IsOpen ? End(data, end) : pipe.Degree(i) == 1 ? "open inlet" : pipe.Degree(i) >= 3 ? "junction" : "";

            if (ImGui.Selectable($"{i}: {n.X:F3}, {n.Y:F3}, {n.Z:F3}  {what}##node{i}", i == SelectedNode, ImGuiSelectableFlags.None, default))
            {
                SelectedNode = i;
                SelectedRun = pipe.RunAt(i);
                _draft.Version = -1;
            }
        }

        if (SelectedNode >= 0 && SelectedNode < pipe.Nodes.Count)
        {
            if (pipe.EndAt(SelectedNode).IsOpen)
            {
                var moved = Coord("X##n"u8, ref _draft.NodeX) | Coord("Y##n"u8, ref _draft.NodeY) | Coord("Z##n"u8, ref _draft.NodeZ);

                if (moved)
                {
                    var node = SelectedNode;
                    var to = ComponentCommands.Quantise(new Vector3d(_draft.NodeX, _draft.NodeY, _draft.NodeZ));
                    ComponentCommands.SetPipe(session, pipe.Id, p => p.Nodes[node] = to);
                }
            }
            else
            {
                ImGui.TextDisabled("This node sits on a port and moves with it."u8);
            }
        }
    }

    /// <summary>The bar beside the selected part or pipe node: add a node (extend from an end, branch from the middle), delete.</summary>
    public void DrawContext(DesignSession session, OrbitCamera camera, Vector2 viewport)
    {
        var data = session.Doc.Components;

        if (data is null || _gizmo.Dragging)
            return;

        var part = data.IndexOfPart(SelectedPart);

        if (part >= 0)
        {
            if (!ContextBar.Begin("part", camera, data.Parts[part].Position, viewport))
                return;

            if (ContextBar.Button("Delete part", "Remove this part (pipes on it come loose)"))
            {
                ComponentCommands.RemovePart(session, SelectedPart);
                SelectedPart = 0;
            }

            ContextBar.End();
            return;
        }

        var index = data.IndexOfPipe(SelectedPipe);

        if (index < 0 || SelectedNode < 0 || SelectedNode >= data.Pipes[index].Nodes.Count)
            return;

        var pipe = data.Pipes[index];

        if (!ContextBar.Begin("pipe", camera, pipe.Node(SelectedNode), viewport))
            return;

        var end = pipe.Degree(SelectedNode) <= 1;

        if (ContextBar.Button(end ? "+ Node" : "+ Branch", end ? "Extend the pipe from this node" : "Branch from this node (a tee)"))
        {
            var node = ComponentCommands.AddPipeNode(session, pipe.Id, SelectedNode, NewNodeAt(pipe, SelectedNode, 0.3));
            (SelectedNode, SelectedRun) = node >= 0 ? (node, -1) : (SelectedNode, SelectedRun);
        }

        if (ContextBar.Button("Delete node", "Remove this node (a node between two runs joins them)"))
        {
            var count = pipe.Nodes.Count;
            ComponentCommands.RemovePipeNode(session, pipe.Id, SelectedNode);
            (SelectedNode, SelectedRun) = (count <= 2 ? -1 : Math.Max(0, SelectedNode - 1), -1);

            if (count <= 2)
                SelectedPipe = 0;
        }

        if (ContextBar.Button("Delete pipe", "Remove the whole pipe"))
        {
            ComponentCommands.RemovePipe(session, pipe.Id);
            (SelectedPipe, SelectedNode, SelectedRun) = (0, -1, -1);
        }

        ContextBar.End();
    }

    /// <summary>Where a new node off <paramref name="node"/> goes: straight on from an end, square to the first run from a node with more.</summary>
    private static Vector3d NewNodeAt(PipeRoute pipe, int node, double length)
    {
        var at = pipe.Node(node);
        var runIndex = pipe.RunAt(node);

        if (runIndex < 0)
            return Add(at, new Vector3d(0, 0, length));

        var away = Normalise(Sub(at, pipe.Node(pipe.Runs[runIndex].Other(node))), new Vector3d(0, 0, 1));

        if (pipe.Degree(node) == 1)
            return Add(at, Scale(away, length));

        var side = Normalise(Cross(away, Math.Abs(away.Y) < 0.9 ? new Vector3d(0, 1, 0) : new Vector3d(1, 0, 0)), new Vector3d(1, 0, 0));
        return Add(at, Scale(side, length));
    }

    // ---- test bench ----

    private void DrawBench(DesignSession session)
    {
        if (!ImGui.Begin("Test bench"u8))
        {
            ImGui.End();
            return;
        }

        var data = session.Doc.Components;

        if (data is null || data.Parts.Count == 0)
        {
            ImGui.TextDisabled("Add parts to test the component."u8);
            ImGui.End();
            return;
        }

        if (_running && _modelVersion != session.Version)
            Stop("The design changed: start again to test it.");

        ImGui.SeparatorText("Outside"u8);

        if (ImGui.RadioButton("Vacuum"u8, !_seaLevel))
            _seaLevel = false;

        ImGui.SameLine();

        if (ImGui.RadioButton("Sea level (1.013 bar)"u8, _seaLevel))
            _seaLevel = true;

        ImGui.SeparatorText("Inlets"u8);
        var any = false;

        foreach (var pipe in data.Pipes)
        {
            foreach (var node in pipe.OpenEnds())
            {
                any = true;
                var supply = SupplyFor(data, pipe, node);
                var mixture = Math.Max(0, _chemistry.Mixtures.FindIndex(m => m.Key == supply.Mixture));
                ImGui.PushID((int)(pipe.Id * 64 + node));
                ImGui.Text($"Pipe #{pipe.Id} inlet at node {node}");
                DesignerStyle.ItemWidth(110f);

                if (Combo("##mix"u8, ref mixture, _chemistry.Mixtures.Select(m => m.Name).ToArray()))
                    supply.Mixture = _chemistry.Mixtures[mixture].Key;

                ImGui.SameLine();
                var bar = (float)(supply.Pressure / 1e5);
                DesignerStyle.ItemWidth(80f);

                if (ImGui.InputFloat("bar"u8, ref bar, 0f, 0f, "%.1f"u8, ImGuiInputTextFlags.None))
                    supply.Pressure = Math.Clamp(bar, 0.1f, 1000f) * 1e5;

                ImGui.PopID();
            }
        }

        if (!any)
            ImGui.TextDisabled("No open pipe ends: pipe the injectors' inlets out to feed them."u8);

        ImGui.SeparatorText("Run"u8);

        if (!_running)
        {
            if (ImGui.Button("Open the valves"u8))
                Start(session, data);
        }
        else
        {
            if (ImGui.Button("Spark"u8))
                _bench = _model!.Spark() ? "Spark: lit." : _model.Igniters.Count == 0 ? "No igniter on a chamber." : "Spark: nothing flammable in the chamber.";

            ImGui.SameLine();

            if (ImGui.Button("Close the valves"u8))
                Stop("");
        }

        if (_bench.Length > 0)
            ImGui.TextWrapped($"{_bench}");

        if (_model is not null)
        {
            foreach (var issue in _model.Issues)
                ImGui.TextColored(new Brutal.Numerics.float4(1f, 0.55f, 0.35f, 1f), $"{issue}");

            DrawReadouts(data);
        }

        ImGui.End();
    }

    private void DrawReadouts(ComponentData data)
    {
        var net = _model!.Network;

        foreach (var (id, gas) in _model.ChamberGas)
        {
            var g = net.Gases[gas];
            var lit = string.Join(", ", _chemistry.Reactions.Where(r => g.Lit[r.Index]).Select(r => r.Name));
            ImGui.Text($"Chamber #{id}: {g.Pressure / 1e5:F1} bar, {g.Temperature:F0} K{(lit.Length > 0 ? $", burning ({lit})" : ", not burning")}");
        }

        foreach (var n in net.Nozzles)
        {
            var p = n.Last;
            ImGui.Text($"Thrust {p.Thrust / 1000:F2} kN, Isp {p.Isp:F0} s, {p.MassFlow:F2} kg/s");
            ImGui.Text($"Exit Mach {p.ExitMach:F2}, exit {p.ExitPressure / 1e5:F2} bar, divergence {p.DivergenceEfficiency:F3}{(p.Separated ? $", separated at {p.SeparationFraction * 100:F0} % of the bell" : "")}");
        }

        foreach (var pipe in data.Pipes)
        {
            if (!_model.PipeGas.TryGetValue(pipe.Id, out var gas))
                continue;

            var burst = net.Burst[gas];
            var pressure = net.Gases[gas].Pressure;
            var rating = _model.PipeRating[pipe.Id];
            var text = burst ? $"Pipe #{pipe.Id}: BURST (rated {rating / 1e5:F0} bar)" : $"Pipe #{pipe.Id}: {pressure / 1e5:F1} of {rating / 1e5:F0} bar";

            if (burst)
                ImGui.TextColored(new Brutal.Numerics.float4(1f, 0.35f, 0.3f, 1f), $"{text}");
            else
                ImGui.Text($"{text}");
        }

        var count = Math.Min(_samples, History);

        if (count > 1)
        {
            var start = _samples > History ? _samples % History : 0;
            ImGui.PlotLines("##thrust"u8, _thrust.AsSpan(0, count), start, "thrust"u8, 0f, float.MaxValue, new Brutal.Numerics.float2(Px(300f), Px(60f)), sizeof(float));
            ImGui.PlotLines("##pressure"u8, _pressure.AsSpan(0, count), start, "chamber pressure"u8, 0f, float.MaxValue, new Brutal.Numerics.float2(Px(300f), Px(60f)), sizeof(float));
        }
    }

    private static float Px(float v) => MathF.Round(v * ImGui.GetFontSize() / DesignerStyle.BodySize);

    private Supply SupplyFor(ComponentData data, PipeRoute pipe, int node)
    {
        var supply = _supplies.Find(s => s.Pipe == pipe.Id && s.Node == node);

        if (supply is not null)
            return supply;

        // A pipe feeding an oxidiser injector defaults to oxygen, anything else to methane.
        var oxidiser = pipe.Ports.Any(p => data.IndexOfPart(p.End.Part) is var index and >= 0 && data.Parts[index].Kind == PartKind.Injector && data.Parts[index].P3 == (int)InjectorFeed.Oxidiser);
        supply = new Supply { Pipe = pipe.Id, Node = node, Mixture = oxidiser ? "mix.oxygen" : "mix.methane" };
        _supplies.Add(supply);
        return supply;
    }

    private void Start(DesignSession session, ComponentData data)
    {
        foreach (var pipe in data.Pipes)
        {
            foreach (var node in pipe.OpenEnds())
                SupplyFor(data, pipe, node);
        }

        _model = ComponentModel.Compile(data, _chemistry, m => Yield(session.Doc, m), _supplies, _seaLevel ? 101325 : 0);
        _modelVersion = session.Version;
        _running = true;
        _samples = 0;
        _bench = "Valves open: gas flows through the chamber. Spark it to light it.";
    }

    private void Stop(string message)
    {
        _running = false;
        _bench = message;
    }

    /// <summary>Each frame: runs the bench one frame of time.</summary>
    public void Step(double dt)
    {
        if (!_running || _model is null)
            return;

        _model.Network.Step(dt);
        var slot = _samples % History;
        _thrust[slot] = (float)_model.Network.Thrust;
        _pressure[slot] = _model.ChamberGas.Count > 0 ? (float)_model.Network.Gases[_model.ChamberGas.Values.First()].Pressure : 0f;
        _samples++;
    }

    // ---- viewport ----

    /// <summary>Draws ports and pipe nodes, picks parts and nodes, drags them; true when it used the mouse.</summary>
    public bool Update(DesignSession session, OrbitCamera camera, Vector2 viewport, in Matrix4x4 viewProj, bool mouseFree, OverlayRenderer overlay)
    {
        var data = session.Doc.Components;
        var io = ImGui.GetIO();
        var mouse = new Vector2(io.MousePos.X, io.MousePos.Y);
        var clicked = ImGui.IsMouseClicked(ImGuiMouseButton.Left, false);

        if (data is null)
            return false;

        Surfaces(session);
        var links = ComponentModel.Links(data);

        if (_partSurfaces.TryGetValue(SelectedPart, out var picked))
        {
            var tint = OverlayRenderer.WithAlpha(SelectedColour, 70);

            foreach (var poly in picked)
            {
                for (var i = 1; i + 1 < poly.Points.Length; i++)
                    overlay.Triangle(poly.Points[0], poly.Points[i], poly.Points[i + 1], tint);
            }
        }

        foreach (var part in data.Parts)
        {
            var ports = PartGeometry.Ports(part);

            for (var i = 0; i < ports.Length; i++)
            {
                var (p, _) = PartGeometry.PortAt(part, i);
                var joined = ComponentModel.Partner(links, part.Id, i, out _, out _);
                var colour = ports[i].Kind == PortKind.Spark ? SparkPort : joined ? JoinedPort : FreePort;
                overlay.Cube(p, part.Id == SelectedPart ? PortHalf * 1.6 : PortHalf, colour);
            }
        }

        var hoverPipe = 0u;
        var hoverNode = -1;
        var hoverRun = -1;
        var best = HandlePixels;
        var linePipe = 0u;
        var lineBest = WholeTransform.LinePixels;

        foreach (var pipe in data.Pipes)
        {
            // A click on a run picks the whole pipe.
            foreach (var run in pipe.Runs)
            {
                if (run.A < pipe.Nodes.Count && run.B < pipe.Nodes.Count
                    && WholeTransform.ToLine(camera, viewProj, viewport, mouse, pipe.Node(run.A), pipe.Node(run.B)) is var line && line < lineBest)
                    (lineBest, linePipe) = (line, pipe.Id);
            }

            if (pipe.Id == SelectedPipe)
            {
                // The split handle in the middle of each run of the selected pipe.
                for (var r = 0; r < pipe.Runs.Count; r++)
                {
                    var run = pipe.Runs[r];

                    if (run.A >= pipe.Nodes.Count || run.B >= pipe.Nodes.Count)
                        continue;

                    var mid = Scale(Add(pipe.Node(run.A), pipe.Node(run.B)), 0.5);
                    overlay.Cube(mid, PortHalf * 0.8, OverlayRenderer.WithAlpha(SelectedColour, 170));

                    if (camera.Project(mid, viewProj, viewport, out var pixel) && Vector2.Distance(pixel, mouse) < best)
                    {
                        best = Vector2.Distance(pixel, mouse);
                        (hoverPipe, hoverNode, hoverRun) = (pipe.Id, -1, r);
                    }
                }
            }

            for (var i = 0; i < pipe.Nodes.Count; i++)
            {
                var n = pipe.Node(i);
                var selected = pipe.Id == SelectedPipe && i == SelectedNode;
                overlay.Cube(n, selected ? PortHalf * 1.5 : PortHalf, selected ? SelectedColour : NodeColour);

                if (camera.Project(n, viewProj, viewport, out var pixel) && Vector2.Distance(pixel, mouse) < best)
                {
                    best = Vector2.Distance(pixel, mouse);
                    (hoverPipe, hoverNode, hoverRun) = (pipe.Id, i, -1);
                }
            }
        }

        // The arrows of whatever is selected take the mouse first.
        var used = UpdateGizmo(session, data, camera, viewport, viewProj, mouse, mouseFree, io.MouseDown[0], clicked, overlay);

        if (used || !mouseFree || !clicked)
            return used;

        if (hoverRun >= 0)
        {
            var node = ComponentCommands.SplitPipeRun(session, hoverPipe, hoverRun);
            (SelectedPipe, SelectedNode, SelectedPart, SelectedRun) = (hoverPipe, node, 0, -1);
            return true;
        }

        if (hoverNode >= 0)
        {
            (SelectedPipe, SelectedNode, SelectedPart, SelectedRun) = (hoverPipe, hoverNode, 0, -1);
            return true;
        }

        if (linePipe != 0)
        {
            (SelectedPipe, SelectedNode, SelectedPart, SelectedRun) = (linePipe, -1, 0, -1);
            return true;
        }

        var p0 = camera.Position;
        var hit = Pick(new Vector3d(p0.X, p0.Y, p0.Z), camera.Ray(mouse, viewport));
        (SelectedPart, SelectedPipe, SelectedNode) = (hit, 0, -1);
        return hit != 0;
    }

    private bool UpdateGizmo(DesignSession session, ComponentData data, OrbitCamera camera, Vector2 viewport, in Matrix4x4 viewProj, Vector2 mouse, bool mouseFree, bool down, bool clicked, OverlayRenderer overlay)
    {
        var part = data.IndexOfPart(SelectedPart);
        var pipe = data.IndexOfPipe(SelectedPipe);

        if (part >= 0)
        {
            var p = data.Parts[part];

            if (_gizmo.Update(p.Position, p.Rotation, camera, viewProj, viewport, mouse, mouseFree, down, clicked, Grid, overlay))
            {
                if (_gizmo.Turned)
                    p.Rotation = _gizmo.Rotation;
                else
                    (p.PosX, p.PosY, p.PosZ) = ComponentCommands.Quantise(Add(p.Position, _gizmo.Offset));

                ComponentCommands.SetPart(session, p);
            }

            return _gizmo.Dragging || _gizmo.Hovered;
        }

        if (pipe >= 0 && SelectedNode >= 0 && SelectedNode < data.Pipes[pipe].Nodes.Count)
        {
            var route = data.Pipes[pipe];

            // A node on a port moves with the port.
            if (!route.EndAt(SelectedNode).IsOpen)
                return false;

            var node = SelectedNode;

            if (_gizmo.Update(route.Node(node), null, camera, viewProj, viewport, mouse, mouseFree, down, clicked, Grid, overlay))
            {
                var at = ComponentCommands.Quantise(Add(route.Node(node), _gizmo.Offset));
                ComponentCommands.SetPipe(session, route.Id, r => r.Nodes[node] = at);
            }

            return _gizmo.Dragging || _gizmo.Hovered;
        }

        if (pipe >= 0 && SelectedNode < 0)
        {
            // The whole pipe: its free nodes move or turn about the middle of all its nodes (nodes on a part's port stay with the port).
            var route = data.Pipes[pipe];
            var points = new Vector3d[route.Nodes.Count];

            for (var i = 0; i < points.Length; i++)
                points[i] = route.Node(i);

            var centre = WholeTransform.Centre(points);

            if (WholeTransform.Update(_gizmo, centre, true, camera, viewProj, viewport, mouse, mouseFree, down, clicked, Grid, overlay))
            {
                Span<double> m = stackalloc double[9];
                WholeTransform.Turn(_gizmo, m);
                var moved = new (int X, int Y, int Z)[points.Length];

                for (var i = 0; i < points.Length; i++)
                    moved[i] = route.EndAt(i).IsOpen ? ComponentCommands.Quantise(WholeTransform.Apply(_gizmo, m, centre, points[i])) : route.Nodes[i];

                ComponentCommands.SetPipe(session, route.Id, r =>
                {
                    for (var i = 0; i < moved.Length && i < r.Nodes.Count; i++)
                        r.Nodes[i] = moved[i];
                });
            }

            return _gizmo.Dragging || _gizmo.Hovered;
        }

        return false;
    }

    /// <summary>The part whose surface the ray meets first, or 0.</summary>
    private uint Pick(Vector3d origin, Vector3d direction)
    {
        var best = double.MaxValue;
        var hit = 0u;

        foreach (var (id, polygons) in _partSurfaces)
        {
            foreach (var poly in polygons)
            {
                for (var i = 1; i + 1 < poly.Points.Length; i++)
                {
                    if (Triangle(origin, direction, poly.Points[0], poly.Points[i], poly.Points[i + 1], out var t) && t < best)
                    {
                        best = t;
                        hit = id;
                    }
                }
            }
        }

        return hit;
    }

    /// <summary>Möller–Trumbore, both faces.</summary>
    public static bool Triangle(Vector3d o, Vector3d d, Vector3d a, Vector3d b, Vector3d c, out double t)
    {
        t = 0;
        var e1 = Sub(b, a);
        var e2 = Sub(c, a);
        var p = Cross(d, e2);
        var det = Dot(e1, p);

        if (Math.Abs(det) < 1e-15)
            return false;

        var inv = 1.0 / det;
        var s = Sub(o, a);
        var u = Dot(s, p) * inv;

        if (u < 0 || u > 1)
            return false;

        var q = Cross(s, e1);
        var v = Dot(d, q) * inv;

        if (v < 0 || u + v > 1)
            return false;

        t = Dot(e2, q) * inv;
        return t > 0;
    }

    // ---- helpers ----

    private void Rotate(DesignSession session, ComponentPart part, int axis, int steps)
    {
        part.Rotation = SolidRotation.AddSteps(part.Rotation, axis, steps);
        ComponentCommands.SetPart(session, part);
    }

    private static ref int Param(ref Draft d, int i)
    {
        switch (i)
        {
            case 0: return ref d.P0;
            case 1: return ref d.P1;
            case 2: return ref d.P2;
            case 3: return ref d.P3;
            case 4: return ref d.P4;
            default: return ref d.P5;
        }
    }

    private static bool Coord(ReadOnlySpan<byte> label, ref float value)
    {
        DesignerStyle.ItemWidth(90f);
        ImGui.InputFloat(label, ref value, 0f, 0f, "%.3f"u8, ImGuiInputTextFlags.None);
        var done = ImGui.IsItemDeactivatedAfterEdit();

        if (label[0] != (byte)'Z')
            ImGui.SameLine();

        return done;
    }

    private static string End(ComponentData data, PipeEnd end)
    {
        if (end.IsOpen)
            return "open inlet";

        var index = data.IndexOfPart(end.Part);
        return index < 0 ? "missing part" : $"#{end.Part} {PartGeometry.Ports(data.Parts[index])[end.Port].Name}";
    }

    private DefId DefaultMaterial(PartKind kind)
    {
        var key = kind switch { PartKind.Chamber => "mat.part.copper", PartKind.Nozzle => "mat.part.inconel", _ => "mat.part.steel" };
        var id = DefId.FromName(key);
        return _catalog.TryGet(id, out _) ? id : _catalog.All[0].Id;
    }

    private double Yield(PlanDocument doc, byte material) =>
        material < doc.Materials.Count && _catalog.TryGet(doc.Materials[material], out var m) ? m.YieldMpa : 0;

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

    private bool MaterialCombo(ReadOnlySpan<byte> label, ref int index)
    {
        var names = _catalog.All.Select(m => m.Name).ToArray();
        DesignerStyle.ItemWidth(160f);
        return Combo(label, ref index, names);
    }

    private static bool Combo(ReadOnlySpan<byte> label, ref int index, string[] names)
    {
        if (names.Length == 0)
            return false;

        index = Math.Clamp(index, 0, names.Length - 1);
        var changed = false;

        if (ImGui.BeginCombo(label, $"{names[index]}", ImGuiComboFlags.None))
        {
            for (var i = 0; i < names.Length; i++)
            {
                if (ImGui.Selectable($"{names[i]}", i == index, ImGuiSelectableFlags.None, default) && i != index)
                {
                    index = i;
                    changed = true;
                }
            }

            ImGui.EndCombo();
        }

        return changed;
    }
}
