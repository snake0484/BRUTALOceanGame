using System.Numerics;
using Brutal.Logging;
using BrutalGame.Engine.Scene;
using BrutalGame.Physics;
using BrutalGame.Terrain;
using BrutalGame.Terrain.Runtime;
using BrutalGame.Terrain.Runtime.Water;

namespace BrutalGame;

/// <summary>
/// Scripted scene for --pond-test (docs/voxel-water.md, "Flow simulation v2"): water that is the voxels' own, away from
/// the sea zones, so the voxel model (the automaton, or --water-pipes) is all that moves it. Once terrain has loaded it
/// digs a bowl into the hillside above the west beach, fills it short of its lowest rim, lets it settle, then cuts the
/// downhill rim below the water line so the pond pours out and runs down the slope toward the sea. Frames the camera on
/// it and logs the voxel sim each half second.
/// </summary>
public sealed class PondTest(TerrainWorld world, VoxelWaterSystem water, PhysicsWorld physics, FlyCamera camera)
{
    private static readonly LogCategory Log = LogCategory.Make("PondTest");

    /// <summary>Lowest ground (m) the pond is dug at, how much higher it may be, and the steepest ground (rise per metre across the bowl) it accepts.</summary>
    private const float SiteHeight = 7f;
    private const float SiteBand = 8f;
    private const float MaxSlope = 0.35f;
    private const float BowlRadius = 7f;

    /// <summary>The bowl's centre sits this far over the ground (under it when negative), so it is about BowlRadius minus this deep.</summary>
    private const float BowlLift = -1f;

    /// <summary>Water stands this far under the bowl's lowest rim, and the breach's channel cuts this far under the water ...</summary>
    private const float Freeboard = 0.8f;
    private const float BreachDepth = 1.5f;
    private const float BreachRadius = 2f;

    /// <summary>... its bed falling this much per metre, one cut every ChannelStep metres, at most ChannelReach past the rim.</summary>
    private const float ChannelFall = 0.05f;
    private const float ChannelStep = 1.5f;
    private const float ChannelReach = 40f;
    private const int MaxCuts = 32;

    private static readonly float[] Lanes = [0f, 25f, -25f, 50f, -50f, 75f];

    private double _start = -1.0;
    private double _nextLog;
    private int _stage;
    private Vector3 _site;
    private Vector3 _downhill;
    private float _level;

    public void Update(double time)
    {
        if (_stage == 0 && world.LoadedChunks > 0 && world.PendingJobs == 0)
        {
            _start = time;
            _stage = 1;
        }

        if (_stage == 1 && time - _start > 1.0)
        {
            if (!FindSite())
            {
                Log.Info($"no hillside site found (ground at x -240 z 0: {Ground(-240f, 0f):F1} m, x -200: {Ground(-200f, 0f):F1} m, x -150: {Ground(-150f, 0f):F1} m)");
                _stage = 5;
                return;
            }

            world.ApplyBrush(new BrushOp(_site + new Vector3(0f, BowlLift, 0f), BowlRadius, BrushMode.Dig, VoxelMaterial.Air));
            // From high over the slope, off to one side, looking back up the run-off path at the breach and into the pond.
            var side = new Vector3(_downhill.Z, 0f, -_downhill.X);
            var target = new Vector3(_site.X, _level - 3f, _site.Z) + _downhill * 12f;
            camera.Position = target + _downhill * 14f + side * 8f + new Vector3(0f, 24f, 0f);
            var look = target - camera.Position;
            camera.Yaw = MathF.Atan2(look.Z, look.X);
            camera.Pitch = MathF.Atan2(look.Y, new Vector2(look.X, look.Z).Length());
            Log.Info($"pond at {_site}, water to y {_level:F2}, downhill {_downhill}, model {(water.Pipes ? "pipes" : "automaton")}");
            _stage = 2;
        }

        if (_stage == 2 && time - _start > 4.0)
        {
            water.Fill(_site + new Vector3(0f, BowlLift, 0f), BowlRadius, _level);
            Log.Info("filled");
            _nextLog = time;
            _stage = 3;
        }

        if (_stage == 3 && time - _start > 10.0)
        {
            // A channel from inside the rim down the slope, its bed under the water line and falling gently, cut until
            // it opens onto ground lower than its bed (the ground along it is read before any of it is dug).
            var count = 0;
            Span<Vector4> cuts = stackalloc Vector4[MaxCuts];
            for (var d = BowlRadius - 2f; d < BowlRadius + ChannelReach && count < MaxCuts; d += ChannelStep)
            {
                var at = _site + _downhill * d;
                var bed = _level - BreachDepth - ChannelFall * (d - BowlRadius + 2f);
                cuts[count++] = new Vector4(at.X, bed + BreachRadius, at.Z, Ground(at.X, at.Z));

                if (d > BowlRadius && cuts[count - 1].W < bed)
                    break;
            }

            foreach (var cut in cuts[..count])
                world.ApplyBrush(new BrushOp(new Vector3(cut.X, cut.Y, cut.Z), BreachRadius, BrushMode.Dig, VoxelMaterial.Air));

            Log.Info($"breached: channel of {count} cuts to {cuts[count - 1]}");
            _stage = 4;
        }

        if (_stage >= 3 && _stage < 5 && time >= _nextLog)
        {
            Log.Info($"t+{time - _start:F1}s voxel {(water.Pipes ? "pipes" : "automaton")}: blocks {water.Blocks}, awake {water.AwakeBlocks}, step {water.LastStepMs:F3} ms, awake mass {water.Mass:F0} m3; zone discs {water.ZoneDiscs}; activity {water.Activity}");
            _nextLog = time + 0.5;

            // Water depth down the run-off path: the pond's centre, then every 4 m toward the sea.
            var profile = "";
            for (var d = 0f; d <= 40f; d += 4f)
            {
                var at = _site + _downhill * d;
                profile += $" {water.ColumnWater(at.X, at.Z, _level - 10f, _level + 3f):F2}";
            }

            Log.Info("   column depth (m) from the pond's centre every 4 m downhill:" + profile);

            if (time - _start > 50.0)
                _stage = 5;
        }
    }

    /// <summary>
    /// Walks inland from the sea along a few lanes to the first ground between <see cref="SiteHeight"/> and
    /// <see cref="SiteHeight"/> + <see cref="SiteBand"/> on a gentle slope, takes downhill from the ground's own slope there,
    /// and sets the water level short of the lowest ground round the bowl (its rim).
    /// </summary>
    private bool FindSite()
    {
        foreach (var z in Lanes)
        {
            for (var x = -300f; x < 0f; x += 1f)
            {
                var here = Ground(x, z);
                if (float.IsNaN(here) || here < SiteHeight)
                    continue;

                if (here > SiteHeight + SiteBand)
                    break;

                var grad = new Vector2(Ground(x + BowlRadius, z) - Ground(x - BowlRadius, z), Ground(x, z + BowlRadius) - Ground(x, z - BowlRadius)) / (2f * BowlRadius);
                if (float.IsNaN(grad.X) || float.IsNaN(grad.Y) || grad.Length() > MaxSlope || grad.Length() < 0.02f)
                    continue;

                _site = new Vector3(x, here, z);
                var down = -Vector2.Normalize(grad);
                _downhill = new Vector3(down.X, 0f, down.Y);

                var rim = float.MaxValue;
                for (var a = 0; a < 16; a++)
                {
                    var angle = a * MathF.Tau / 16f;
                    rim = MathF.Min(rim, Ground(x + MathF.Cos(angle) * (BowlRadius - 0.5f), z + MathF.Sin(angle) * (BowlRadius - 0.5f)));
                }

                _level = rim - Freeboard;
                if (_level > _site.Y + BowlLift - BowlRadius + 1f)
                    return true;

                break;
            }
        }

        return false;
    }

    private float Ground(float x, float z)
    {
        var hit = physics.CastRay(new Vector3(x, 80f, z), -Vector3.UnitY, 160f);
        return hit.Hit ? hit.Point.Y : float.NaN;
    }
}
