using Brutal.ImGuiApi;
using Brutal.Numerics;
using BrutalGame.Engine.Time;
using BrutalGame.Physics;
using BrutalGame.Terrain.Runtime;
using BrutalGame.Terrain.Runtime.Rendering;

namespace BrutalGame.Debug;

/// <summary>
/// Streaming and GPU memory readouts for voxel terrain.
/// </summary>
public sealed class TerrainPanel(TerrainWorld world, TerrainRenderer renderer, PhysicsWorld physics)
{
    public readonly TimingRing UpdateMs = new(256);
    public bool Open = true;
    public float BrushRadius;

    public void Draw()
    {
        if (!Open)
            return;

        ImGui.SetNextWindowPos(new float2(12f, 420f), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("Terrain"u8, ref Open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text($"chunks loaded {world.LoadedChunks}  meshed {renderer.ChunkCount}  drawn {renderer.DrawnLastFrame}");
            var lods = world.LeavesPerLod;
            ImGui.Text($"leaves {world.DesiredLeaves}  select {world.LastSelectMs:F2} ms (worker, {world.LastSelectVisits} nodes, {world.LastSelectClassified} bounded)  speed lod {world.MinLod}");
            ImGui.Text($"lod 0-6:  {lods[0]} {lods[1]} {lods[2]} {lods[3]} {lods[4]} {lods[5]} {lods[6]}");
            ImGui.Text($"lod 7-12: {lods[7]} {lods[8]} {lods[9]} {lods[10]} {lods[11]} {lods[12]}");
            ImGui.Text($"jobs pending {world.PendingJobs}  last batch {world.LastBatchChunks} in {world.LastBatchMs:F1} ms");
            ImGui.Text($"vertex {renderer.VertexBytesUsed / (1024f * 1024f):F1} MB  index {renderer.IndexBytesUsed / (1024f * 1024f):F1} MB");
            ImGui.Text($"main-thread update {UpdateMs.Average:F3} ms  max {UpdateMs.Max:F3}");
            ImGui.Text($"edits {world.EditCount}  dirty {world.DirtyChunks}  brush r {BrushRadius:F1} m  (LMB dig, Ctrl+LMB place, [ ])");
            ImGui.Text($"collision chunks {world.CollisionBodies}  physics bodies {physics.BodyCount}  step {physics.LastStepMs:F3} ms");
        }

        ImGui.End();
    }
}
