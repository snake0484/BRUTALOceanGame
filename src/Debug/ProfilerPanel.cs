using Brutal.ImGuiApi;
using Brutal.Numerics;
using BrutalGame.Engine.Rendering;
using BrutalGame.Engine.Time;

namespace BrutalGame.Debug;

/// <summary>
/// CPU frame breakdown. Strings go through the ImString arena, so no GC allocation per frame.
/// </summary>
public sealed class ProfilerPanel(FrameStats stats, Renderer renderer, RainCover cover)
{
    private static readonly float2 GraphSize = new(0f, 48f);

    public bool Open = true;

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

            var vsync = renderer.VSync;
            if (ImGui.Checkbox("VSync"u8, ref vsync))
                renderer.VSync = vsync;

            ImGui.SameLine();
            ImGui.Text($"{renderer.PresentMode}  {renderer.Swapchain.Extent.Width}x{renderer.Swapchain.Extent.Height}");

            ImGui.SeparatorText("Frame"u8);
            Plot("##frame"u8, stats.Frame);

            ImGui.SeparatorText("CPU breakdown (avg ms)"u8);
            Row("Sim tick"u8, stats.Tick);
            Row("UI build"u8, stats.Ui);
            Row("GPU fence wait"u8, stats.GpuWait);
            Row("Record"u8, stats.Record);
            Row("Submit + present"u8, stats.Present);
            Row("Cover rebuild"u8, cover.BuildMs);

            var gpu = renderer.GpuTimer;
            ImGui.SeparatorText("GPU (avg ms)"u8);

            if (!gpu.Supported)
                ImGui.Text("timestamps unsupported on this queue"u8);

            for (var i = 0; i < gpu.ScopeCount; i++)
                Row(gpu.Name(i), gpu.Ring(i));
        }

        ImGui.End();
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
