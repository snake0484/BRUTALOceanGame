using System.Numerics;
using Brutal.Logging;
using BrutalGame.Engine.Scene;
using BrutalGame.Terrain;
using BrutalGame.Terrain.Runtime;
using BrutalGame.Terrain.Runtime.Water;
using BrutalGame.Terrain.Water;

namespace BrutalGame;

/// <summary>
/// Scripted scene for --river-test (docs/voxel-water.md "Flows"): once terrain has loaded, frames the river from its
/// bank a share of the way down (<see cref="StartShare"/>), holds there, then in <c>walk</c> mode flies the camera down
/// the river to the sea at <see cref="WalkSpeed"/>, so reaches are taken over by the 3D sim and let go again as it
/// passes. Logs the voxel sim and the flow each second. <c>hold</c> stays put. A given --cam is kept and nothing flies.
/// A mode ending <c>:m</c> (<c>hold:32</c>) sets <see cref="VoxelWaterSystem.FlowSimRadius"/>; <c>@k</c> before it
/// (<c>hold@262:32</c>) starts at station k.
/// </summary>
public sealed class RiverTest(TerrainWorld world, VoxelWaterSystem water, FlyCamera camera, string mode, bool keepCamera)
{
    private static readonly LogCategory Log = LogCategory.Make("RiverTest");

    private const float StartShare = 0.4f;
    private const double HoldSeconds = 30.0;
    private const float WalkSpeed = 6f;

    /// <summary>The camera stands this far to the river's side and over its surface, looking at it this far ahead.</summary>
    private const float Side = 18f;
    private const float Height = 25f;
    private const float Ahead = 6f;

    private double _start = -1.0;
    private double _nextLog;
    private float _station;
    private float _first;

    public void Update(double time)
    {
        if (water.Flows.Count == 0)
            return;

        var flow = water.Flows[1];
        var path = flow.Path;

        if (_start < 0.0)
        {
            if (world.LoadedChunks == 0 || world.PendingJobs > 0)
                return;

            var colon = mode.IndexOf(':');

            if (colon >= 0)
            {
                water.FlowSimRadius = float.Parse(mode[(colon + 1)..], System.Globalization.CultureInfo.InvariantCulture);
                mode = mode[..colon];
            }

            var at = mode.IndexOf('@');
            _first = StartShare * (path.Count - 1);

            if (at >= 0)
            {
                _first = Math.Clamp(int.Parse(mode[(at + 1)..], System.Globalization.CultureInfo.InvariantCulture), 0, path.Count - 1);
                mode = mode[..at];
            }

            _start = time;
            _station = _first;
            Log.Info($"river: {path.Count} stations, sill {path.Sill:F2}, {flow.Reaches.Length} reaches; starting at station {_station:F0} {path.Points[(int)_station]}, mode {mode}, sim radius {water.FlowSimRadius} m{(keepCamera ? " (camera kept)" : "")}");
        }

        var since = time - _start;

        if (!keepCamera)
        {
            if (mode == "walk" && since > HoldSeconds)
                _station = MathF.Min(_first + (float)(since - HoldSeconds) * WalkSpeed, path.Count - 1);

            Frame(path, (int)_station);
        }

        if (time < _nextLog)
            return;

        _nextLog = time + 1.0;
        var active = new System.Text.StringBuilder();

        for (var r = 0; r < flow.Reaches.Length; r++)
            if (flow.Reaches[r].Active)
                active.Append(active.Length > 0 ? "," : "").Append(r);

        Log.Info($"t+{since:F0}s station {_station:F0}: blocks {water.Blocks}, awake {water.AwakeBlocks}, step {water.LastStepMs:F3} ms (peak tick {water.TakePeakStepMs():F2}), activity {water.Activity}; active reaches [{active}]; {water.Flows.Describe(water.Bodies).TrimEnd()}");

        if ((int)since % 5 == 0)
            LogSeams(flow);
    }

    /// <summary>The water along the centreline (m³ per metre column) for 6 m either side of each seam between a simulated and a routed reach.</summary>
    private void LogSeams(WaterFlows.Flow flow)
    {
        var path = flow.Path;
        var previous = -1;

        for (var k = 0; k < path.Count; k++)
        {
            var (cx, cz) = WaterFlows.Column(path.Points[k].X, path.Points[k].Y);

            if (!water.Flows.ReachAt(cx, cz, out var at, out var reach) || at != flow)
                continue;

            if (previous >= 0 && reach != previous && flow.Reaches[reach].Active != flow.Reaches[previous].Active)
            {
                var line = new System.Text.StringBuilder($"seam at station {k} (reach {previous} {(flow.Reaches[previous].Active ? "sim" : "routed")} -> {reach} {(flow.Reaches[reach].Active ? "sim" : "routed")}):");

                for (var j = Math.Max(0, k - 24); j < Math.Min(path.Count, k + 4); j++)
                    line.Append($" {j}:{water.ColumnWater(path.Points[j].X, path.Points[j].Y, path.Beds[j] - 2f, path.Surfaces[j] + 2f):F2}/{path.Depth(j):F2}");

                Log.Info(line.ToString());
            }

            previous = reach;
        }
    }

    private void Frame(RiverPath path, int station)
    {
        var at = path.Points[station];
        var dir = path.Directions[station];
        var side = new Vector2(-dir.Y, dir.X);
        var target = new Vector3(at.X + dir.X * Ahead, path.Surfaces[station], at.Y + dir.Y * Ahead);
        camera.Position = new Vector3(at.X + side.X * Side, path.Surfaces[station] + Height, at.Y + side.Y * Side);
        var look = target - camera.Position;
        camera.Yaw = MathF.Atan2(look.Z, look.X);
        camera.Pitch = MathF.Atan2(look.Y, new Vector2(look.X, look.Z).Length());
    }
}
