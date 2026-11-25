using Brutal.ImGuiApi;
using Brutal.Numerics;
using BrutalGame.Engine.Rendering;
using BrutalGame.Engine.Time;
using BrutalGame.Terrain.Runtime.Rendering;
using BrutalGame.Water.Rendering;

namespace BrutalGame.Debug;

/// <summary>
/// CPU frame breakdown, managed allocations, GPU timings and pipeline statistics. Strings go through the ImString arena,
/// so no GC allocation per frame.
/// </summary>
public sealed class ProfilerPanel(FrameStats stats, Renderer renderer, RainCover cover, WaterFogVolume fog, TerrainRenderer terrain, OceanRenderer ocean)
{
    private static readonly float2 GraphSize = new(0f, 48f);
    private static readonly float4 AllocColour = new(1f, 0.35f, 0.3f, 1f);

    public bool Open = true;

    /// <summary>Pipeline statistics queries are recorded only while this is on and the panel is open.</summary>
    public bool PipelineStatsOn = true;

    public void Draw()
    {
        if (!Open)
            return;

        ImGui.SetNextWindowPos(new float2(12f, 12f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new float2(380f, 0f), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("Profiler"u8, ref Open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            var frameMs = stats.Frame.Average;
            var fps = frameMs > 0f ? 1000f / frameMs : 0f;

            ImGui.Text($"{renderer.Context.DeviceName}");
            ImGui.Text($"{fps:F0} fps  |  {frameMs:F2} ms avg  |  {stats.Frame.Max:F2} ms max");
            ImGui.Text($"sim t {stats.SimTime:F2}s  tick #{stats.TickIndex}  ticks/frame {stats.TicksThisFrame}");

            var pipelines = renderer.Context.Pipelines;
            ImGui.Text($"pipelines {pipelines.Created}  cache hits {pipelines.Hits}  driver {pipelines.DriverMs:F1} ms");

            var vsync = renderer.VSync;
            if (ImGui.Checkbox("VSync"u8, ref vsync))
                renderer.VSync = vsync;

            ImGui.SameLine();
            ImGui.Text($"{renderer.PresentMode}  {renderer.OutputExtent.Width}x{renderer.OutputExtent.Height}");

            var scale = renderer.RenderScale;
            if (ImGui.SliderFloat("Render scale"u8, ref scale, Renderer.MinRenderScale, Renderer.MaxRenderScale, "%.2f"u8))
                renderer.RenderScale = scale;

            ImGui.SameLine();
            ImGui.Text($"{renderer.Extent.Width}x{renderer.Extent.Height}");

            ImGui.SeparatorText("Frame"u8);
            Plot("##frame"u8, stats.Frame);

            ImGui.SeparatorText("CPU breakdown (avg ms)"u8);
            Row("Sim tick"u8, stats.Tick);
            Row("UI build"u8, stats.Ui);
            Row("GPU fence wait"u8, stats.GpuWait);
            Row("Record"u8, stats.Record);
            Row("Submit + present"u8, stats.Present);
            Row("Cover rebuild"u8, cover.BuildMs);
            Row("Fog fill"u8, fog.FillMs);

            DrawMemory();
            DrawGeometry();

            var gpu = renderer.GpuTimer;
            ImGui.SeparatorText("GPU (avg ms)"u8);

            if (!gpu.Supported)
                ImGui.Text("timestamps unsupported on this queue"u8);

            for (var i = 0; i < gpu.ScopeCount; i++)
                Row(gpu.Name(i), gpu.Ring(i));
        }

        ImGui.End();
    }

    /// <summary>GC collections and managed bytes allocated per frame; any allocating frame shows red.</summary>
    private void DrawMemory()
    {
        ImGui.SeparatorText("Managed memory"u8);
        ImGui.Text($"GC gen0 {GC.CollectionCount(0)}  gen1 {GC.CollectionCount(1)}  gen2 {GC.CollectionCount(2)}  heap {GC.GetTotalMemory(false) / (1024 * 1024)} MB");

        var allocating = 0;
        foreach (var bytes in stats.AllocBytes.Values)
        {
            if (bytes > 0f)
                allocating++;
        }

        if (stats.AllocatedMain > 0)
            ImGui.TextColored(AllocColour, $"main thread {stats.AllocatedMain} B last frame");
        else
            ImGui.Text("main thread 0 B last frame"u8);

        ImGui.SameLine();
        ImGui.Text($" all threads {stats.AllocatedAll / 1024} KB");

        if (allocating > 0)
            ImGui.TextColored(AllocColour, $"{allocating} of {stats.AllocBytes.Values.Length} frames allocated, max {stats.AllocBytes.Max:F0} B");
        else
            ImGui.Text($"0 of {stats.AllocBytes.Values.Length} frames allocated");

        ImGui.PlotHistogram("##alloc"u8, stats.AllocBytes.Values, stats.AllocBytes.Offset, default, 0f, MathF.Max(stats.AllocBytes.Max, 1024f), GraphSize);
    }

    /// <summary>
    /// Triangles each system submitted (counted on the CPU) and, per pipeline statistics scope, what the GPU did with
    /// them: overdraw is fragment shader invocations per screen pixel.
    /// </summary>
    private void DrawGeometry()
    {
        ImGui.SeparatorText("Geometry"u8);
        ImGui.Text($"tris  terrain {terrain.TrianglesLastFrame}  ocean {ocean.TrianglesLastFrame}");

        var pipeline = renderer.PipelineStats;

        if (!pipeline.Supported)
        {
            ImGui.Text("pipeline statistics unsupported"u8);
            return;
        }

        ImGui.Checkbox("Pipeline statistics"u8, ref PipelineStatsOn);

        if (!PipelineStatsOn)
            return;

        // The scene passes draw at the render scale, the UI at the window's size.
        var scene = renderer.Extent;
        var output = renderer.OutputExtent;
        var pixels = MathF.Max(1f, (float)scene.Width * scene.Height);
        var outputPixels = MathF.Max(1f, (float)output.Width * output.Height);

        ImGui.Text("scope           prims       vs clip out        fs overdraw"u8);

        var opaque = default(PipelineCounts);
        var waterPass = default(PipelineCounts);

        for (var i = 0; i < pipeline.ScopeCount; i++)
        {
            ref readonly var counts = ref pipeline.Last(i);
            StatsRow(pipeline.Name(i), counts, pipeline.Pass(i) == PipelinePass.Overlay ? outputPixels : pixels);

            if (pipeline.Pass(i) == PipelinePass.Opaque)
                opaque.Add(counts);
            else if (pipeline.Pass(i) == PipelinePass.Water)
                waterPass.Add(counts);
        }

        ImGui.Text($"overdraw  opaque pass {opaque.FragmentInvocations / pixels:F2}  water pass {waterPass.FragmentInvocations / pixels:F2} (UI excluded)");
    }

    private static void StatsRow(ImString label, in PipelineCounts c, float pixels)
    {
        ImGui.Text($"{label,-12} {c.Primitives,8} {c.VertexInvocations,8} {c.ClippingPrimitives,8} {c.FragmentInvocations,9} {c.FragmentInvocations / pixels,8:F2}");
    }

    private static void Row(ImString label, TimingRing ring)
    {
        ImGui.Text($"{label,-18} {ring.Average,7:F3}  max {ring.Max,7:F3}");
    }

    private static void Plot(ImString label, TimingRing ring)
    {
        ImGui.PlotLines(label, ring.Values, ring.Offset, default, 0f, MathF.Max(ring.Max, 1f), GraphSize);
    }
}
