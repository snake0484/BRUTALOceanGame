using System.Numerics;
using Brutal.Logging;
using BrutalGame.Engine.Rendering;
using BrutalGame.Physics;
using BrutalGame.Terrain;
using BrutalGame.Terrain.Runtime;
using BrutalGame.Terrain.Runtime.Water;

namespace BrutalGame;

/// <summary>
/// Scripted scene for --cave-test (docs/voxel-water.md, "Sea caves and walls"): once terrain has loaded, builds three
/// sea-zone waters on the coast east of the start camera and logs their wind (fetch, exposure), the zone sim and its wall
/// splashes. A sea cave: a rock mound over the waterline with a tunnel dug through it at sea level, its mouth on the sea.
/// A pool: a pit inland, fed by an L-shaped channel that leaves land upwind of it. A cove: a bay dug into the beach.
/// The camera is left alone: frame each with --cam from the logged positions. <paramref name="site"/> (cave, pool, cove)
/// builds only that one: one zone sim spanning all three is 15,000 cells.
/// </summary>
public sealed class CaveTest(TerrainWorld world, VoxelWaterSystem water, PhysicsWorld physics, RainCover cover, string site)
{
    private static readonly LogCategory Log = LogCategory.Make("CaveTest");
    private const float CaveZ = 0f;
    private const float PoolZ = 70f;
    private const float CoveZ = -70f;
    private const float SeaX = -300f;
    private const float Step = 2f;

    private double _start = -1.0;
    private double _nextLog;
    private int _stage;
    private Vector3 _cave;
    private Vector3 _pool;
    private Vector3 _cove;

    public void Update(double time)
    {
        if (_stage == 0 && world.LoadedChunks > 0 && world.PendingJobs == 0)
        {
            _start = time;
            _stage = 1;
        }

        if (_stage == 1 && time - _start > 1.0)
        {
            if (site is "all" or "cave")
                DigCave(Shore(CaveZ));

            if (site is "all" or "pool")
                DigPool(Shore(PoolZ));

            if (site is "all" or "cove")
                DigCove(Shore(CoveZ));

            Log.Info($"cave mouth {_cave}, pool {_pool}, cove {_cove}");
            _nextLog = time;
            _stage = 2;
        }

        if (_stage == 2 && time >= _nextLog)
        {
            var walls = 0;
            var pours = 0;
            var fastest = Vector4.Zero;
            foreach (var splash in water.Zones.Splashes)
            {
                walls += splash.Direction.W == WaterSplash.Wall ? 1 : 0;
                pours += splash.Direction.W == WaterSplash.Pour ? 1 : 0;
                fastest = splash.PositionSpeed.W > fastest.W ? splash.PositionSpeed : fastest;
            }

            Log.Info($"t+{time - _start - 1.0:F0}s zone discs {water.ZoneDiscs} cells {water.Zones.Cells}, {water.LastZoneMs:F2} ms (agitate {water.LastAgitateMs:F3}), max flow {water.Zones.MaxSpeed:F2} m/s; splashes: wall {walls}, pour {pours}, fastest {fastest.W:F2} m/s at {fastest.X:F0},{fastest.Y:F1},{fastest.Z:F0}; turbulence mean {water.Zones.MeanTurbulence:F2} max {water.Zones.MaxTurbulence:F2}, foam {water.Zones.MeanFoam:F2}; cover rebuild {cover.LastBuildMs:F3} ms ({cover.Builds})");
            if (site is "all" or "cave")
            {
                Wind("cave mouth", _cave);
                Wind("cave inside", _cave + new Vector3(16f, 0f, 0f));
                Wind("open sea", _cave - new Vector3(30f, 0f, 0f));
            }

            if (site is "all" or "pool")
            {
                Wind("pool west", _pool - new Vector3(6f, 0f, 0f));
                Wind("pool east", _pool + new Vector3(6f, 0f, 0f));
            }

            if (site is "all" or "cove")
                Wind("cove", _cove + new Vector3(8f, 0f, 0f));

            _nextLog = time + (time - _start < 12.0 ? 1.0 : 5.0);
        }
    }

    /// <summary>The waterline on row z: walking east from the sea west of the island, the first ground above the top sea voxel layer.</summary>
    private Vector3 Shore(float z)
    {
        var probe = new Vector3(SeaX, 40f, z);

        for (var i = 0; i < 400; i++, probe.X += 1f)
        {
            var hit = physics.CastRay(probe, -Vector3.UnitY, 80f);
            if (hit.Hit && hit.Point.Y > -1.5f)
                return new Vector3(hit.Point.X, 0f, z);
        }

        return new Vector3(probe.X, 0f, z);
    }

    /// <summary>A mound of rock over the waterline, 15 m high, then a tunnel at sea level from the sea into it; about 5 m of rock closes its end.</summary>
    private void DigCave(Vector3 shore)
    {
        _cave = shore - new Vector3(6f, 0f, 0f);
        world.ApplyBrush(new BrushOp(shore + new Vector3(6f, 3f, 0f), 12f, BrushMode.Place, VoxelMaterial.Rock));
        world.ApplyBrush(new BrushOp(shore + new Vector3(16f, 3f, 0f), 12f, BrushMode.Place, VoxelMaterial.Rock));

        for (var x = -10f; x <= 18f; x += Step)
            world.ApplyBrush(new BrushOp(shore + new Vector3(x, 0f, 0f), 4f, BrushMode.Dig, VoxelMaterial.Air));
    }

    /// <summary>
    /// A pit 28 m inland and a channel to it that leaves the sea 22 m north and turns south into it. The beach west of it
    /// lies a few decimetres under sea level, open water the wind would blow over, so a rock berm closes its west rim: with
    /// a wind blowing east, its fetch is then its own width.
    /// </summary>
    private void DigPool(Vector3 shore)
    {
        _pool = shore + new Vector3(28f, 0f, 0f);

        for (var z = -14f; z <= 14f; z += Step)
            world.ApplyBrush(new BrushOp(_pool + new Vector3(-12f, 1f, z), 3f, BrushMode.Place, VoxelMaterial.Rock));

        world.ApplyBrush(new BrushOp(_pool + new Vector3(0f, -4f, 0f), 9f, BrushMode.Dig, VoxelMaterial.Air));
        world.ApplyBrush(new BrushOp(_pool + new Vector3(0f, 2f, 0f), 9f, BrushMode.Dig, VoxelMaterial.Air));

        for (var x = -12f; x <= 28f; x += Step)
            Channel(shore + new Vector3(x, 0f, 22f));

        for (var z = 22f; z >= 8f; z -= Step)
            Channel(_pool + new Vector3(0f, 0f, z));
    }

    private void Channel(Vector3 at)
    {
        world.ApplyBrush(new BrushOp(at + new Vector3(0f, -2f, 0f), 3f, BrushMode.Dig, VoxelMaterial.Air));
        world.ApplyBrush(new BrushOp(at + new Vector3(0f, 2f, 0f), 3f, BrushMode.Dig, VoxelMaterial.Air));
    }

    /// <summary>A bay about 40 m wide and 24 m deep, open to the sea on the west.</summary>
    private void DigCove(Vector3 shore)
    {
        _cove = shore;

        for (var x = 0f; x <= 16f; x += 8f)
            for (var z = -12f; z <= 12f; z += 12f)
                world.ApplyBrush(new BrushOp(shore + new Vector3(x, -3f, z), 8f, BrushMode.Dig, VoxelMaterial.Air));
    }

    private void Wind(string name, Vector3 at)
    {
        var wind = cover.WindAt(new Vector2(at.X, at.Z));
        Log.Info(wind is { } w ? $"  {name} {at.X:F0},{at.Z:F0}: exposure {w.Exposure:F2}, fetch {w.Fetch:F0} m" : $"  {name} {at.X:F0},{at.Z:F0}: outside the cover window");
    }
}
