using System.Numerics;
using Brutal.Logging;
using BrutalGame.Engine.Scene;
using BrutalGame.Physics;
using BrutalGame.Terrain;
using BrutalGame.Terrain.Runtime;
using BrutalGame.Terrain.Runtime.Water;

namespace BrutalGame;

/// <summary>
/// Scripted check for --seaside-test: dig a hole straddling the waterline so the nearby (now simulated) sea drains into
/// it, frame the camera on it, and log simulated water mass and the water level at the hole over time.
/// </summary>
public sealed class SeasideTest(TerrainWorld world, VoxelWaterSystem water, PhysicsWorld physics, FlyCamera camera)
{
    private static readonly LogCategory Log = LogCategory.Make("SeasideTest");
    private const float HoleRadius = 9f;
    private const float HoleCentreY = -5f;

    private double _start = -1.0;
    private double _nextLog;
    private int _stage;
    private Vector3 _hole;

    public void Update(double time)
    {
        if (_stage == 0 && world.LoadedChunks > 0 && world.PendingJobs == 0)
        {
            _start = time;
            _stage = 1;
        }

        if (_stage == 1 && time - _start > 1.0)
        {
            // Walk inland from the sea along the camera's view to where the seabed rises past the top sea voxel layer
            // (y = -1): the last metre of real water. The hole is centred there, so it cuts into both beach and sea.
            var forward = Vector3.Normalize(new Vector3(camera.Forward.X, 0f, camera.Forward.Z));
            var probe = new Vector3(camera.Position.X, 40f, camera.Position.Z);

            for (var i = 0; i < 400; i++, probe += forward)
            {
                var hit = physics.CastRay(probe, -Vector3.UnitY, 80f);
                if (hit.Hit && hit.Point.Y > -1.5f)
                {
                    _hole = new Vector3(hit.Point.X, HoleCentreY, hit.Point.Z);
                    break;
                }
            }

            world.ApplyBrush(new BrushOp(_hole, HoleRadius, BrushMode.Dig, VoxelMaterial.Air));
            world.ApplyBrush(new BrushOp(_hole + new Vector3(0f, 6f, 0f), HoleRadius, BrushMode.Dig, VoxelMaterial.Air));

            camera.Position = _hole + new Vector3(-26f, 22f, -18f);
            var look = _hole - camera.Position;
            camera.Yaw = MathF.Atan2(look.Z, look.X);
            camera.Pitch = MathF.Atan2(look.Y, new Vector2(look.X, look.Z).Length());

            Log.Info($"hole at waterline {_hole}");
            _nextLog = time;
            _stage = 2;
        }

        if (_stage == 2 && time >= _nextLog)
        {
            Log.Info($"t+{time - _start - 1.0:F1}s zone discs {water.ZoneDiscs} cells {water.Zones.Cells}, volume {water.Zones.Volume:F0} m3, breakers in {water.Zones.InjectedVolume:F0} m3, foam {water.Zones.MeanFoam:F2}, max flow {water.Zones.MaxSpeed:F2} m/s at {water.Zones.MaxSpeedAt - new Vector3(_hole.X, 0f, _hole.Z)}, zone {water.LastZoneMs:F2} ms, voxel step {water.LastStepMs:F2} ms");
            Probe("hole", _hole);
            Probe("sea", _hole + new Vector3(-10f, 0f, 0f));
            _nextLog = time + 1.0;

            if (time - _start > 60.0)
                _stage = 3;
        }
    }

    private void Probe(string name, Vector3 at)
    {
        var grid = world.Bathymetry;
        var gx = (int)MathF.Floor((at.X - grid.Origin.X) / BathymetryGrid.CellSize);
        var gz = (int)MathF.Floor((at.Z - grid.Origin.Y) / BathymetryGrid.CellSize);
        var i = gx + gz * BathymetryGrid.Resolution;
        Log.Info($"  {name}: wet {grid.ZoneWet[i]} surface {grid.ZoneOffset[i]:F2} depth {grid.ZoneDepth[i]:F2} foam {grid.ZoneWhite[i]:F2}");
    }
}
