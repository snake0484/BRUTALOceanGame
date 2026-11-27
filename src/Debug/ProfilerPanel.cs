using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using BrutalGame.DesignKit.Ui;
using BrutalGame.Engine.Rendering;
using BrutalGame.Engine.Time;
using BrutalGame.Terrain.Runtime.Rendering;
using BrutalGame.Water.Rendering;

namespace BrutalGame.Debug;

/// <summary>
/// The frame against its 60 Hz budget (a bar of the CPU's parts), every timed system in one sortable table (CPU frame
/// parts, systems the game registers with <see cref="Add"/>, GPU scopes) with a history line each, managed allocations,
/// and geometry with pipeline statistics. Strings go through the ImString arena, so no GC allocation per frame.
/// </summary>
public sealed class ProfilerPanel : DebugPanel
{
    private const float BudgetMs = 1000f / 60f;
    private const int MaxRows = 64;

    private static readonly float4 GpuText = new(0.55f, 0.75f, 1f, 1f);
    private static readonly ImColor8 Track = new(255, 255, 255, 18);
    private static readonly ImColor8 BudgetLine = new(255, 255, 255, 200);
    private static readonly ImColor8[] PartColours =
    [
        new(255, 158, 51, 255),
        new(90, 160, 255, 255),
        new(120, 210, 140, 255),
        new(200, 130, 255, 255),
        new(110, 118, 130, 255),
        new(70, 76, 86, 255),
    ];

    private enum Kind : byte { Cpu, System, Gpu }

    private struct Entry
    {
        public string Name;
        public TimingRing Ring;
        public Kind Kind;
        public float Average;
        public float Max;
    }

    private readonly FrameStats _stats;
    private readonly Renderer _renderer;
    private readonly TerrainRenderer _terrain;
    private readonly OceanRenderer _ocean;
    private readonly Entry[] _fixed = new Entry[MaxRows];
    private readonly Entry[] _rows = new Entry[MaxRows];
    private readonly int[] _order = new int[MaxRows];
    private readonly float[] _parts = new float[6];
    private int _fixedCount;
    private int _sortColumn = -1;
    private bool _sortDescending;

    /// <summary>Pipeline statistics queries are recorded only while this is on and the panel is open.</summary>
    public bool PipelineStatsOn = true;

    public ProfilerPanel(FrameStats stats, Renderer renderer, TerrainRenderer terrain, OceanRenderer ocean)
        : base("Profiler", GlfwKey.F3, "F3")
    {
        _stats = stats;
        _renderer = renderer;
        _terrain = terrain;
        _ocean = ocean;
        Add("Sim tick", stats.Tick, Kind.Cpu);
        Add("UI build", stats.Ui, Kind.Cpu);
        Add("GPU fence wait", stats.GpuWait, Kind.Cpu);
        Add("Record", stats.Record, Kind.Cpu);
        Add("Submit + present", stats.Present, Kind.Cpu);
        Add("Terrain cull + draw", terrain.DrawMs, Kind.System);
    }

    /// <summary>Once, at setup: a system's timing ring listed in the table.</summary>
    public void Add(string name, TimingRing ring) => Add(name, ring, Kind.System);

    public void Draw()
    {
        if (!Open)
            return;

        if (ImGui.Begin(Title, ref Open))
        {
            DrawHeader();
            DrawBudget();

            if (DebugUi.Section("Timings"u8))
                DrawTimings();

            if (DebugUi.Section("Managed memory"u8))
                DrawMemory();

            if (DebugUi.Section("Geometry"u8))
                DrawGeometry();
        }

        ImGui.End();
    }

    private void DrawHeader()
    {
        var pipelines = _renderer.Context.Pipelines;

        if (DebugUi.Props("##device"u8))
        {
            DebugUi.Row("Device"u8, $"{_renderer.Context.DeviceName}");
            DebugUi.Row("Sim"u8, $"t {_stats.SimTime:F2} s  tick {_stats.TickIndex}  {_stats.TicksThisFrame} this frame");
            DebugUi.Row("Pipelines"u8, $"{pipelines.Created}  cache hits {pipelines.Hits}  driver {pipelines.DriverMs:F1} ms");
            DebugUi.Row("Output"u8, $"{_renderer.PresentMode}  {_renderer.OutputExtent.Width}x{_renderer.OutputExtent.Height}");
            DebugUi.Row("Scene"u8, $"{_renderer.Extent.Width}x{_renderer.Extent.Height}");
            DebugUi.End();
        }

        var vsync = _renderer.VSync;

        if (ImGui.Checkbox("VSync"u8, ref vsync))
            _renderer.VSync = vsync;

        ImGui.SameLine();
        DesignerStyle.ItemWidth(160f);
        var scale = _renderer.RenderScale;

        if (ImGui.SliderFloat("Render scale"u8, ref scale, Renderer.MinRenderScale, Renderer.MaxRenderScale, "%.2f"u8))
            _renderer.RenderScale = scale;
    }

    /// <summary>
    /// The frame's CPU parts laid end to end against the 60 Hz budget (the white line); the bar's scale stretches when a
    /// frame runs over. Hover a part for its time. GPU time is the "Frame" GPU scope when there is one.
    /// </summary>
    private void DrawBudget()
    {
        var frame = _stats.Frame.Average;
        _parts[0] = _stats.Tick.Average;
        _parts[1] = _stats.Ui.Average;
        _parts[2] = _stats.Record.Average;
        _parts[3] = _stats.Present.Average;
        _parts[4] = _stats.GpuWait.Average;
        _parts[5] = MathF.Max(0f, frame - _parts[0] - _parts[1] - _parts[2] - _parts[3] - _parts[4]);

        var at = ImGui.GetCursorScreenPos();
        var size = new float2(ImGui.GetContentRegionAvail().X, ImGui.GetFrameHeight());
        ImGui.Dummy(size);
        var hovered = ImGui.IsItemHovered();
        var mouse = ImGui.GetMousePos().X;
        var list = ImGui.GetWindowDrawList();
        var scale = size.X / MathF.Max(BudgetMs, frame);
        list.AddRectFilled(at, at + size, Track);

        var x = at.X;
        var hit = -1;

        for (var i = 0; i < _parts.Length; i++)
        {
            var w = _parts[i] * scale;
            list.AddRectFilled(new float2(x, at.Y), new float2(x + w, at.Y + size.Y), PartColours[i]);

            if (hovered && mouse >= x && mouse < x + w)
                hit = i;

            x += w;
        }

        var budgetX = at.X + BudgetMs * scale;
        list.AddLine(new float2(budgetX, at.Y - 2f), new float2(budgetX, at.Y + size.Y + 2f), BudgetLine, 2f);
        list.AddText(new float2(at.X + DebugUi.Ems(0.4f), at.Y + (size.Y - ImGui.GetTextLineHeight()) * 0.5f), new ImColor8(255, 255, 255, 255), $"CPU {frame:F2} / {BudgetMs:F1} ms");

        if (hit >= 0)
            DebugUi.Tooltip($"{PartName(hit)}: {_parts[hit]:F3} ms ({100f * _parts[hit] / BudgetMs:F0}% of budget)");

        var gpu = GpuFrameMs();

        if (gpu >= 0f)
            DebugUi.Text(DebugUi.Grade(gpu / BudgetMs, 0.8f, 1f), $"GPU {gpu:F2} ms ({100f * gpu / BudgetMs:F0}% of budget)");
        else
            ImGui.TextDisabled("GPU timestamps unsupported on this queue"u8);
    }

    private static string PartName(int part) => part switch
    {
        0 => "Sim tick",
        1 => "UI build",
        2 => "Record",
        3 => "Submit + present",
        4 => "GPU fence wait",
        _ => "Other (poll, input, frame glue)",
    };

    private float GpuFrameMs()
    {
        var gpu = _renderer.GpuTimer;

        if (!gpu.Supported)
            return -1f;

        for (var i = 0; i < gpu.ScopeCount; i++)
        {
            if (gpu.Name(i) == "Frame")
                return gpu.Ring(i).Average;
        }

        return -1f;
    }

    /// <summary>
    /// Every ring in one table: CPU frame parts (white), systems (grey), GPU scopes (blue). Sorted by a clicked header
    /// (name, average, max; a third click returns to the grouped order). % is the average's share of the 16.7 ms budget.
    /// </summary>
    private void DrawTimings()
    {
        var count = Gather();
        const ImGuiTableFlags flags = ImGuiTableFlags.Sortable | ImGuiTableFlags.SortTristate | ImGuiTableFlags.RowBg |
            ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.BordersInnerV;

        if (!ImGui.BeginTable("##timings"u8, 5, flags))
            return;

        ImGui.TableSetupColumn("System"u8, ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Avg"u8, ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.PreferSortDescending, DebugUi.Ems(2.8f));
        ImGui.TableSetupColumn("Max"u8, ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.PreferSortDescending, DebugUi.Ems(2.8f));
        ImGui.TableSetupColumn("%"u8, ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.PreferSortDescending, DebugUi.Ems(2.2f));
        ImGui.TableSetupColumn("History"u8, ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort, DebugUi.Ems(4.5f));
        ImGui.TableHeadersRow();
        ReadSort();
        Sort(count);

        for (var r = 0; r < count; r++)
        {
            ref readonly var e = ref _rows[_order[r]];
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            DebugUi.Text(KindColour(e.Kind), e.Name);
            DebugUi.Tip($"{KindName(e.Kind)}: {e.Name}, {e.Average:F3} ms average, {e.Max:F3} ms max");
            ImGui.TableNextColumn();
            ImGui.Text($"{e.Average:F3}");
            ImGui.TableNextColumn();
            DebugUi.Text(DebugUi.Grade(e.Max / BudgetMs, 0.25f, 0.5f), $"{e.Max:F3}");
            ImGui.TableNextColumn();
            ImGui.Text($"{100f * e.Average / BudgetMs:F1}");
            ImGui.TableNextColumn();
            DebugUi.Spark(e.Ring);
        }

        ImGui.EndTable();
    }

    private static float4 KindColour(Kind kind) => kind switch
    {
        Kind.Gpu => GpuText,
        Kind.Cpu => DesignerStyle.Text,
        _ => DesignerStyle.TextDim,
    };

    private static string KindName(Kind kind) => kind switch
    {
        Kind.Gpu => "GPU scope",
        Kind.Cpu => "CPU frame part",
        _ => "system (CPU)",
    };

    private int Gather()
    {
        var count = 0;

        for (var i = 0; i < _fixedCount; i++)
            _rows[count++] = _fixed[i];

        var gpu = _renderer.GpuTimer;

        if (gpu.Supported)
        {
            for (var i = 0; i < gpu.ScopeCount && count < MaxRows; i++)
                _rows[count++] = new Entry { Name = gpu.Name(i), Ring = gpu.Ring(i), Kind = Kind.Gpu };
        }

        for (var i = 0; i < count; i++)
        {
            _rows[i].Average = _rows[i].Ring.Average;
            _rows[i].Max = _rows[i].Ring.Max;
            _order[i] = i;
        }

        return count;
    }

    private void ReadSort()
    {
        var specs = ImGui.TableGetSortSpecs();

        if (specs.IsNull() || !specs.SpecsDirty)
            return;

        if (specs.SpecsCount == 0)
            _sortColumn = -1;
        else
        {
            ref var first = ref specs.Specs[0];
            _sortColumn = first.ColumnIndex;
            _sortDescending = first.SortDirection == ImGuiSortDirection.Descending;
        }

        specs.SpecsDirty = false;
    }

    /// <summary>Insertion sort of the row order (a few dozen rows); unsorted keeps the grouped order.</summary>
    private void Sort(int count)
    {
        if (_sortColumn < 0)
            return;

        for (var i = 1; i < count; i++)
        {
            var item = _order[i];
            var j = i - 1;

            while (j >= 0 && Before(item, _order[j]))
            {
                _order[j + 1] = _order[j];
                j--;
            }

            _order[j + 1] = item;
        }
    }

    private bool Before(int a, int b)
    {
        ref readonly var x = ref _rows[a];
        ref readonly var y = ref _rows[b];
        var c = _sortColumn switch
        {
            0 => string.CompareOrdinal(x.Name, y.Name),
            2 => x.Max.CompareTo(y.Max),
            _ => x.Average.CompareTo(y.Average),
        };

        return _sortDescending ? c > 0 : c < 0;
    }

    /// <summary>GC collections and managed bytes allocated per frame; any allocating frame shows red.</summary>
    private void DrawMemory()
    {
        var allocating = 0;

        foreach (var bytes in _stats.AllocBytes.Values)
        {
            if (bytes > 0f)
                allocating++;
        }

        if (DebugUi.Props("##memory"u8))
        {
            DebugUi.Row("GC"u8, $"gen0 {GC.CollectionCount(0)}  gen1 {GC.CollectionCount(1)}  gen2 {GC.CollectionCount(2)}  heap {GC.GetTotalMemory(false) / (1024 * 1024)} MB");
            DebugUi.Row("Main thread"u8, _stats.AllocatedMain > 0 ? DebugUi.Bad : DebugUi.Good, $"{_stats.AllocatedMain} B last frame");
            DebugUi.Row("All threads"u8, $"{_stats.AllocatedAll / 1024} KB last frame");
            DebugUi.Row("Frames"u8, allocating > 0 ? DebugUi.Bad : DebugUi.Good, $"{allocating} of {_stats.AllocBytes.Values.Length} allocated, max {_stats.AllocBytes.Max:F0} B");
            DebugUi.End();
        }

        ImGui.PlotHistogram("##alloc"u8, _stats.AllocBytes.Values, _stats.AllocBytes.Offset, default, 0f, MathF.Max(_stats.AllocBytes.Max, 1024f), new float2(-1f, DebugUi.Ems(2.5f)));
    }

    /// <summary>
    /// Triangles each system submitted (counted on the CPU) and, per pipeline statistics scope, what the GPU did with
    /// them: overdraw is fragment shader invocations per screen pixel.
    /// </summary>
    private void DrawGeometry()
    {
        ImGui.Text($"triangles  terrain {_terrain.TrianglesLastFrame}  ocean {_ocean.TrianglesLastFrame}");

        var pipeline = _renderer.PipelineStats;

        if (!pipeline.Supported)
        {
            ImGui.TextDisabled("pipeline statistics unsupported"u8);
            return;
        }

        ImGui.Checkbox("Pipeline statistics"u8, ref PipelineStatsOn);

        if (!PipelineStatsOn)
            return;

        // The scene passes draw at the render scale, the UI at the window's size.
        var scene = _renderer.Extent;
        var output = _renderer.OutputExtent;
        var pixels = MathF.Max(1f, (float)scene.Width * scene.Height);
        var outputPixels = MathF.Max(1f, (float)output.Width * output.Height);
        var opaque = default(PipelineCounts);
        var water = default(PipelineCounts);

        if (ImGui.BeginTable("##pipeline"u8, 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableSetupColumn("Scope"u8, ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Prims"u8);
            ImGui.TableSetupColumn("VS"u8);
            ImGui.TableSetupColumn("Clip out"u8);
            ImGui.TableSetupColumn("FS"u8);
            ImGui.TableSetupColumn("Overdraw"u8);
            ImGui.TableHeadersRow();

            for (var i = 0; i < pipeline.ScopeCount; i++)
            {
                ref readonly var c = ref pipeline.Last(i);
                var overdraw = c.FragmentInvocations / (pipeline.Pass(i) == PipelinePass.Overlay ? outputPixels : pixels);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.Text(pipeline.Name(i));
                ImGui.TableNextColumn();
                ImGui.Text($"{c.Primitives}");
                ImGui.TableNextColumn();
                ImGui.Text($"{c.VertexInvocations}");
                ImGui.TableNextColumn();
                ImGui.Text($"{c.ClippingPrimitives}");
                ImGui.TableNextColumn();
                ImGui.Text($"{c.FragmentInvocations}");
                ImGui.TableNextColumn();
                DebugUi.Text(DebugUi.Grade(overdraw, 2f, 4f), $"{overdraw:F2}");

                if (pipeline.Pass(i) == PipelinePass.Opaque)
                    opaque.Add(c);
                else if (pipeline.Pass(i) == PipelinePass.Water)
                    water.Add(c);
            }

            ImGui.EndTable();
        }

        ImGui.Text($"overdraw  opaque pass {opaque.FragmentInvocations / pixels:F2}  water pass {water.FragmentInvocations / pixels:F2} (UI excluded)");
    }

    private void Add(string name, TimingRing ring, Kind kind) => _fixed[_fixedCount++] = new Entry { Name = name, Ring = ring, Kind = kind };
}
