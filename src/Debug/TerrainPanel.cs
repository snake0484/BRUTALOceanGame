using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using BrutalGame.Engine.Time;
using BrutalGame.Physics;
using BrutalGame.Terrain.Runtime;
using BrutalGame.Terrain.Runtime.Rendering;

namespace BrutalGame.Debug;

/// <summary>Voxel terrain: streaming and level of detail (leaves per LOD as a histogram), edits, memory, collision and timings.</summary>
public sealed class TerrainPanel(TerrainWorld world, TerrainRenderer renderer, PhysicsWorld physics) : DebugPanel("Terrain", GlfwKey.F5, "F5")
{
    private const int Lods = 13;

    private readonly float[] _lods = new float[Lods];

    public readonly TimingRing UpdateMs = new(256);
    public float BrushRadius;

    public void Draw()
    {
        if (!Open)
            return;

        if (ImGui.Begin(Title, ref Open))
        {
            if (DebugUi.Section("Streaming"u8))
                DrawStreaming();

            if (DebugUi.Section("Edits and collision"u8))
                DrawEdits();

            if (DebugUi.Section("Perf"u8) && DebugUi.Timings("##terrain-perf"u8))
            {
                DebugUi.Timing("Main-thread update"u8, UpdateMs);
                DebugUi.Timing("Cull + draw"u8, renderer.DrawMs);
                DebugUi.End();
            }
        }

        ImGui.End();
    }

    private void DrawStreaming()
    {
        if (DebugUi.Props("##streaming"u8))
        {
            DebugUi.Row("Chunks"u8, $"{world.LoadedChunks} / {renderer.ChunkCount} / {renderer.DrawnLastFrame}");
            DebugUi.Tip("loaded / meshed / drawn last frame"u8);
            DebugUi.Row("Leaves"u8, $"{world.DesiredLeaves}  (speed LOD {world.MinLod})");
            DebugUi.Row("Select"u8, $"{world.LastSelectMs:F2} ms (worker)");
            DebugUi.Tip($"{world.LastSelectVisits} nodes visited, {world.LastSelectClassified} bounded");
            DebugUi.Row("Jobs"u8, world.PendingJobs > 0 ? DebugUi.Warn : DebugUi.Good, $"{world.PendingJobs} pending");
            DebugUi.Row("Last batch"u8, $"{world.LastBatchChunks} chunks in {world.LastBatchMs:F1} ms");
            DebugUi.Row("GPU memory"u8, $"{renderer.VertexBytesUsed / (1024f * 1024f):F1} + {renderer.IndexBytesUsed / (1024f * 1024f):F1} MB");
            DebugUi.Tip("vertex + index buffers in use"u8);
            DebugUi.End();
        }

        var leaves = world.LeavesPerLod;
        var max = 1f;

        for (var i = 0; i < Lods; i++)
        {
            _lods[i] = leaves[i];
            max = MathF.Max(max, _lods[i]);
        }

        ImGui.PlotHistogram("##lods"u8, _lods, 0, "leaves per LOD, 0 to 12"u8, 0f, max, new float2(-1f, DebugUi.Ems(3f)));
    }

    private void DrawEdits()
    {
        if (!DebugUi.Props("##edits"u8))
            return;

        DebugUi.Row("Edits"u8, $"{world.EditCount}  dirty chunks {world.DirtyChunks}");
        DebugUi.Row("Brush"u8, $"r {BrushRadius:F1} m");
        DebugUi.Tip("LMB digs, Ctrl+LMB places, [ and ] size the brush"u8);
        DebugUi.Row("Collision"u8, $"{world.CollisionBodies} chunk bodies  {physics.BodyCount} physics bodies");
        DebugUi.Row("Physics step"u8, $"{physics.LastStepMs:F3} ms");
        DebugUi.End();
    }
}
