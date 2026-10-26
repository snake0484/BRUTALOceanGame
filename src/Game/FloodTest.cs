using System.Numerics;
using Brutal.Logging;
using BrutalGame.Engine.Scene;
using BrutalGame.Physics;
using BrutalGame.Terrain;
using BrutalGame.Terrain.Runtime;
using BrutalGame.Terrain.Runtime.Water;

namespace BrutalGame;

/// <summary>
/// Scripted check for --flood-test: dig an inland crater below sea level, then cut a channel from it to the sea,
/// and log simulated water mass so the gradual fill is measurable.
/// </summary>
public sealed class FloodTest(TerrainWorld world, VoxelWaterSystem water, PhysicsWorld physics, TerrainBrush brush, FlyCamera camera)
{
    private static readonly LogCategory Log = LogCategory.Make("FloodTest");
    private const float CraterRadius = 9f;
    private const float ChannelRadius = 3f;
    private const float ChannelDepth = -2f;
    private const float SeaX = -250f;

    private double _start = -1.0;
    private double _nextLog;
    private int _stage;
    private Vector3 _crater;

    public void Update(double time)
    {
        if (_stage == 0 && world.LoadedChunks > 0 && world.PendingJobs == 0)
        {
            _start = time;
            _stage = 1;
        }

        if (_stage == 1 && time - _start > 1.0)
        {
            var hit = physics.CastRay(camera.Position, brush.RayThrough(0f, 0.3f, 16f / 9f), 800f);
            if (!hit.Hit)
                return;

            _crater = new Vector3(hit.Point.X, 0f, hit.Point.Z);
            world.ApplyBrush(new BrushOp(new Vector3(_crater.X, hit.Point.Y, _crater.Z), CraterRadius, BrushMode.Dig, VoxelMaterial.Air));
            world.ApplyBrush(new BrushOp(new Vector3(_crater.X, -4f, _crater.Z), CraterRadius, BrushMode.Dig, VoxelMaterial.Air));
            Log.Info($"crater at {_crater}");
            _stage = 2;
        }

        if (_stage == 2 && time - _start > 3.0)
        {
            for (var x = _crater.X; x >= SeaX; x -= 2f)
            {
                world.ApplyBrush(new BrushOp(new Vector3(x, ChannelDepth, _crater.Z), ChannelRadius, BrushMode.Dig, VoxelMaterial.Air));
                world.ApplyBrush(new BrushOp(new Vector3(x, ChannelDepth + 4f, _crater.Z), ChannelRadius, BrushMode.Dig, VoxelMaterial.Air));
            }

            Log.Info($"channel cut from x={_crater.X:F0} to x={SeaX:F0}");
            _nextLog = time;
            _stage = 3;
        }

        if (_stage == 3 && time >= _nextLog)
        {
            Log.Info($"t+{time - _start - 3.0:F1}s water mass {water.Mass:F1} m3, blocks {water.Blocks}, awake {water.AwakeBlocks}, step {water.LastStepMs:F2} ms");
            _nextLog = time + 1.0;

            if (time - _start > 20.0)
                _stage = 4;
        }
    }
}
