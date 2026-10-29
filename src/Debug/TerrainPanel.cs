using Brutal.ImGuiApi;
using Brutal.Numerics;
using BrutalGame.Engine.Rendering;
using BrutalGame.Engine.Time;
using BrutalGame.Physics;
using BrutalGame.Terrain.Runtime;
using BrutalGame.Terrain.Runtime.Rendering;
using BrutalGame.Terrain.Runtime.Water;
using Vector3 = System.Numerics.Vector3;

namespace BrutalGame.Debug;

/// <summary>
/// Streaming and GPU memory readouts for voxel terrain.
/// </summary>
public sealed class TerrainPanel(TerrainWorld world, TerrainRenderer renderer, PhysicsWorld physics, VoxelWaterSystem water, VoxelWaterRenderer waterRenderer, WaterFogVolume fog, ScreenReflections reflections)
{
    public readonly TimingRing UpdateMs = new(256);
    public bool Open = true;
    public float BrushRadius;

    /// <summary>The water probe's point (the ground under the cursor while P is held), or none yet.</summary>
    public Vector3? Probe;

    /// <summary>CPU time and cells of the last depth map upload (whole map on bathymetry changes, else changed zone cells).</summary>
    public float MapUploadMs;
    public int MapUploadCells;

    private string? Snapshot;

    // The probe's text, rebuilt once a second: counting the bodies' held cells scans every loaded block.
    private const double ProbeRefreshSeconds = 1.0;
    private string _bodies = "";
    private string _column = "";
    private long _probedAt;

    public void Draw()
    {
        if (!Open)
            return;

        ImGui.SetNextWindowPos(new float2(12f, 420f), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("Terrain"u8, ref Open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text($"chunks loaded {world.LoadedChunks}  meshed {renderer.ChunkCount}  drawn {renderer.DrawnLastFrame}");
            var lods = world.LeavesPerLod;
            ImGui.Text($"leaves {world.DesiredLeaves}  lod0 {lods[0]} lod1 {lods[1]} lod2 {lods[2]} lod3 {lods[3]} lod4 {lods[4]}  select {world.LastSelectMs:F2} ms");
            ImGui.Text($"bathymetry rebuild {world.Bathymetry.LastRebuildMs:F2} ms  map upload {MapUploadCells} cells in {MapUploadMs:F3} ms");
            ImGui.Text($"jobs pending {world.PendingJobs}  last batch {world.LastBatchChunks} in {world.LastBatchMs:F1} ms");
            ImGui.Text($"vertex {renderer.VertexBytesUsed / (1024f * 1024f):F1} MB  index {renderer.IndexBytesUsed / (1024f * 1024f):F1} MB");
            ImGui.Text($"main-thread update {UpdateMs.Average:F3} ms  max {UpdateMs.Max:F3}");
            ImGui.Text($"edits {world.EditCount}  dirty {world.DirtyChunks}  brush r {BrushRadius:F1} m  (LMB dig, Ctrl+LMB place, [ ])");
            ImGui.Text($"physics bodies {physics.BodyCount}  step {physics.LastStepMs:F3} ms");
            ImGui.Text($"water blocks {water.Blocks}  awake {water.AwakeBlocks}  step {water.LastStepMs:F3} ms  mass {water.Mass:F1} m3");
            ImGui.Text($"water meshes {waterRenderer.MeshesLastFrame}  tris {waterRenderer.TrianglesLastFrame}");
            ImGui.Text($"sea zones {water.ZoneDiscs} discs {water.Zones.Cells} cells  {water.LastZoneMs:F3} ms  max flow {water.Zones.MaxSpeed:F1} m/s  volume {water.Zones.Volume:F0} m3  breakers in {water.Zones.InjectedVolume:F0} m3");

            ImGui.SeparatorText("Water audit"u8);
            ImGui.Checkbox("Audit every step (slow)"u8, ref water.Audit);
            var worst = water.AuditWorst;
            ImGui.Text($"made {water.AuditMade:F1} m3 over {water.AuditSteps} steps; worst step {worst.Step}: {worst.Made:F2} m3, block {worst.Block} ({worst.BlockMade:F2} m3, max fill {worst.MaxFill:F2})");
            ImGui.Text($"free water by event: activations {water.AuditActivations:F0}, refreshes {water.AuditRefreshes:F0}, evictions {water.AuditEvictions:F0}, settles {water.AuditSettles:F0} m3");

            ImGui.SeparatorText("Water probe (hold P over the ground)"u8);

            if (System.Diagnostics.Stopwatch.GetElapsedTime(_probedAt).TotalSeconds >= ProbeRefreshSeconds)
            {
                _bodies = water.DescribeBodiesAndDiscs();
                _column = Probe is { } at ? water.DescribeColumn(at) : "";
                _probedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            }

            ImGui.Text(_bodies);

            if (Probe is { } probe)
            {
                ImGui.Text($"column at {probe.X:F0}, {probe.Z:F0} (ground {probe.Y:F1}):");
                ImGui.Text(_column);

                if (ImGui.Button("Save water snapshot"u8))
                {
                    var path = Path.Combine(AppContext.BaseDirectory, $"water_snapshot_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                    File.WriteAllText(path, $"probe {probe}\n{water.DescribeBodiesAndDiscs()}\ncolumn:\n{water.DescribeColumn(probe)}\naudit: made {water.AuditMade:F1} m3 over {water.AuditSteps} steps, worst {worst}; free water by event: activations {water.AuditActivations:F0}, refreshes {water.AuditRefreshes:F0}, evictions {water.AuditEvictions:F0}, settles {water.AuditSettles:F0} m3\n");
                    Snapshot = path;
                }

                if (Snapshot is not null)
                    ImGui.Text($"saved {Snapshot}");
            }

            ImGui.SeparatorText("Water fog"u8);
            ImGui.Checkbox("Volumetric fog"u8, ref fog.Enabled);
            ImGui.SliderFloat("Extinction /m"u8, ref fog.Extinction, 0.5f, 12f, "%.2f"u8);
            ImGui.SliderFloat("Stop at T"u8, ref fog.StopTransmittance, 0.001f, 0.2f, "%.3f"u8, ImGuiSliderFlags.Logarithmic);
            ImGui.SliderFloat("Step m"u8, ref fog.Step, 0.25f, 2f, "%.2f"u8);
            ImGui.SliderFloat("Far blend m"u8, ref fog.FarStart, 8f, 56f, "%.0f"u8);
            fog.FarEnd = fog.FarStart + 16f;
            ImGui.Text($"fill {fog.FillMs.Average:F3} ms  uploads {fog.UploadsLastFrame}  camera {(fog.CameraInWater ? "in water" : "in air")}  surface {fog.CameraSurface:F2}");

            ImGui.SeparatorText("Water reflections"u8);
            ImGui.Checkbox("Screen-space reflections"u8, ref reflections.Enabled);
            ImGui.SliderInt("Most steps"u8, ref reflections.Steps, 8, 64);
            ImGui.SliderFloat("Px per step"u8, ref reflections.Stride, 1f, 32f, "%.1f"u8, ImGuiSliderFlags.Logarithmic);
            ImGui.SliderFloat("Ray m"u8, ref reflections.MaxDistance, 50f, 4000f, "%.0f"u8, ImGuiSliderFlags.Logarithmic);
        }

        ImGui.End();
    }
}
