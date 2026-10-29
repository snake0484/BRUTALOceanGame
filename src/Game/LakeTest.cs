using System.Numerics;
using Brutal.Logging;
using BrutalGame.Engine.Scene;
using BrutalGame.Terrain;
using BrutalGame.Terrain.Runtime;
using BrutalGame.Terrain.Runtime.Water;
using BrutalGame.Terrain.Water;

namespace BrutalGame;

/// <summary>
/// Scripted scenes for --lake-test (docs/voxel-water.md, "Lakes" and "Dig wakes the lake"), from the crater's west rim
/// once the lake's blocks are in. <c>hole</c>: digs a small hole into the bank right at the water's edge, as a player
/// would; a lake at rest should wake only around the hole (a lake disc frees its water there), pour into it, go back to
/// sleep and rejoin the lake, the disc gone. <c>tunnel</c>: digs a tunnel from the sea up through the rim and breaches the
/// lake last, then logs what the lake gives, how fast, and how deep the water stands along the tunnel. <c>deep</c>: the
/// same tunnel, but it comes up through the lake bed 80 m inside the shore, under about 10 m of water. <c>bank</c>: as a
/// player dug it, from the lake out: a wide mouth well inside the shore and under the water, then a thin tunnel falling
/// gently under the lake and on out through the rim, so the lake pours into the mouth while the tunnel is still being dug (a dome of
/// water rose over both ends). Logs the
/// voxel sim each half second. (The tunnel exits onto the crater's outer slope: the sea is 500 m from the lake.)
/// </summary>
public sealed class LakeTest(TerrainWorld world, VoxelWaterSystem water, FlyCamera camera, string mode)
{
    private static readonly LogCategory Log = LogCategory.Make("LakeTest");

    /// <summary>
    /// The hole: its radius, and its centre's distance inside the lake's shore radius and under its surface, so the hole
    /// takes away the shoreline and the lake pours into it.
    /// </summary>
    private const float HoleRadius = 3f;
    private const float HoleInside = 3.5f;
    private const float HoleDepth = 1.5f;

    /// <summary>
    /// The tunnel: brush radius, one dig every TunnelStep metres, kept at least TunnelCover under the ground. It opens onto
    /// the crater's outer slope where the ground first lies ExitDrop under the lake's surface (the sea is 500 m off), and
    /// the water runs on down the hill from there.
    /// </summary>
    private const float TunnelRadius = 3f;
    private const float TunnelStep = 2f;
    private const float TunnelCover = 4f;
    private const float ExitDrop = 30f;
    private const int MaxTunnelDigs = 256;

    /// <summary>
    /// The bank tunnel: the mouth's brush radius, its centre this far inside the shore and under the lake's surface; then
    /// TunnelRadius digs every TunnelStep falling BankFall a metre west until BankDepth under the surface, then level west
    /// until the outer slope's ground lies under the tunnel's floor.
    /// </summary>
    private const float BankMouth = 7f;
    private const float BankInside = 8f;
    private const float BankUnder = 5f;
    private const float BankFall = 0.25f;
    private const float BankDepth = 20f;

    /// <summary>The deep breach: a tunnel to a point this far inside the shore, this far over the lake bed, where the lake is about 10 m deep.</summary>
    private const float DeepInside = 80f;
    private const float DeepOverBed = 2f;

    private readonly LakeInfo _lake = TerrainGenerator.Lake(world.Params);
    private readonly ushort _body = water.Generated.Length > 0 ? water.Generated[0].Id : WaterBodies.None;

    /// <summary>The lake's shore radius (its surface area's), inside the generator's wider fill radius.</summary>
    private float Shore => water.Generated.Length > 0 ? MathF.Sqrt(water.Generated[0].Area / MathF.PI) : _lake.Radius;
    private readonly Vector3[] _tunnel = new Vector3[MaxTunnelDigs];
    private int _tunnelCount;
    private int _dugCount;
    private double _start = -1.0;
    private double _dug = -1.0;
    private double _nextLog;
    private float _givenAtLog;
    private int _stage;

    private bool Tunnel => mode is "tunnel" or "deep" or "bank";
    private bool Deep => mode == "deep";
    private bool Bank => mode == "bank";

    public void Update(double time)
    {
        if (_stage == 0 && world.LoadedChunks > 0 && world.PendingJobs == 0)
        {
            // On the west rim, looking east across the shore point and the lake beyond.
            var shore = new Vector3(_lake.Centre.X - Shore, _lake.Top + 0.5f, _lake.Centre.Y);
            camera.Position = shore + new Vector3(-22f, 9f, -26f);
            var look = shore + new Vector3(25f, -2f, 10f) - camera.Position;
            camera.Yaw = MathF.Atan2(look.Z, look.X);
            camera.Pitch = MathF.Atan2(look.Y, new Vector2(look.X, look.Z).Length());
            Log.Info($"{mode}: lake {_lake}, shore point {shore}, model {(water.Pipes ? "pipes" : "automaton")}");

            if (Tunnel)
                PlanTunnel();

            water.Audit = true;

            // The bank tunnel's exit, seen from up the outer slope's side: where the water left the tunnel.
            if (Bank)
            {
                var exit = _tunnel[0];
                camera.Position = exit + new Vector3(-35f, 30f, -35f);
                var at = exit - camera.Position;
                camera.Yaw = MathF.Atan2(at.Z, at.X);
                camera.Pitch = MathF.Atan2(at.Y, new Vector2(at.X, at.Z).Length());
            }

            _start = time;
            _nextLog = time;
            _stage = 1;
        }

        if (_stage == 1 && time - _start > 8.0 && water.Activity.Queued == 0 && water.Activity.Pending == 0)
        {
            if (!Tunnel)
            {
                world.ApplyBrush(new BrushOp(Hole, HoleRadius, BrushMode.Dig, VoxelMaterial.Air));
                Log.Info($"t+{time - _start:F1}s dug {HoleRadius} m at {Hole}, {water.Blocks} blocks, {water.AwakeBlocks} awake");
                _dug = time;
                _stage = 2;
            }
            else if (_dugCount < _tunnelCount)
            {
                // One dig a frame, from the sea up to the lake, so the breach is the last and the sim never hitches (the bank
                // tunnel the other way, from the lake out).
                var dig = Bank ? _tunnel[_tunnelCount - 1 - _dugCount++] : _tunnel[_dugCount++];
                var radius = Bank && _dugCount == 1 ? BankMouth : TunnelRadius;
                world.ApplyBrush(new BrushOp(dig, radius, BrushMode.Dig, VoxelMaterial.Air));

                if (_dugCount == _tunnelCount)
                {
                    Log.Info($"t+{time - _start:F1}s {(Bank ? "dug out to the slope" : "breached the lake")} at {_tunnel[_tunnelCount - 1]} after {_tunnelCount} digs, {water.Blocks} blocks, {water.AwakeBlocks} awake, discs {water.Discs}");
                    _dug = time;
                    _givenAtLog = Given;
                    _stage = 2;
                }
            }
        }

        if (_stage >= 1 && _stage < 3 && time >= _nextLog)
        {
            var since = _dug < 0.0 ? "before the dig" : $"+{time - _dug:F1}s after the dig";
            Log.Info($"t+{time - _start:F1}s ({since}) voxel {(water.Pipes ? "pipes" : "automaton")}: blocks {water.Blocks}, awake {water.AwakeBlocks}, step {water.LastStepMs:F3} ms, awake mass {water.Mass:F0} m3, lake surface {Level:F3} m, discs {water.Discs} ({water.Outlets} outlets), bodies {water.Bodies.Count - 2}, lake gave {Given:F1} m3 ({(Given - _givenAtLog) / 0.5:F2} m3/s); activity {water.Activity}");
            _givenAtLog = Given;

            if (Tunnel && _dug >= 0.0)
            {
                var (free, maxFill, cells, top) = water.FreeWater();
                var (held, outside, floating, floatTop) = water.HeldCells(_body, _lake.Centre, _lake.Radius, Level - 2f, out var high);
                Log.Info($"   audit: made {water.AuditMade:F1} m3 over {water.AuditSteps} steps, worst {water.AuditWorst}; free water by event: activations {water.AuditActivations:F0}, refreshes {water.AuditRefreshes:F0}, evictions {water.AuditEvictions:F0}, settles {water.AuditSettles:F0} m3");
                Log.Info($"   bodies:{BodyList()} lake held cells {held}, {outside} outside its fill radius, {floating} floating (highest y {floatTop:F0}); free water outside the fill radius within 2 m of the level or over it {high:F1} m3");
                Log.Info($"   free water {free:F0} m3 in {cells} cells, largest fill {maxFill:F2}, highest free surface {top - Level:+0.00;-0.00} m over the lake; in the tunnel (m) from the breach to the exit, every 5th dig, then 5 and 10 m down the slope:" + Profile());
            }

            _nextLog = time + 0.5;

            if (_dug >= 0.0 && time - _dug > (Bank ? 600.0 : 90.0))
                _stage = 3;
        }
    }

    private Vector3 Hole => Deep ? DeepHole : new(_lake.Centre.X - Shore + HoleInside, _lake.Top - HoleDepth, _lake.Centre.Y);

    /// <summary>The breach at the lake bed: the tunnel comes up into the lake from below, under 10 m of water.</summary>
    private Vector3 DeepHole
    {
        get
        {
            var x = _lake.Centre.X - Shore + DeepInside;
            var bed = TerrainGenerator.SurfaceHeight(x, _lake.Centre.Y, world.Params);
            return new Vector3(x, bed + DeepOverBed, _lake.Centre.Y);
        }
    }

    private float Level => _body == WaterBodies.None ? float.NaN : water.Bodies[_body].Level;

    private float Given => _body == WaterBodies.None ? 0f : water.Bodies[_body].Given;

    /// <summary>
    /// Digs from the exit on the outer slope up to the hole point, every TunnelStep along the straight line between them
    /// but never nearer the ground than TunnelCover, so it stays a tunnel through the rim; the first dig opens the slope.
    /// </summary>
    private void PlanTunnel()
    {
        if (Bank)
        {
            PlanBank();
            return;
        }

        var breach = Hole;
        var x = breach.X;
        var ground = TerrainGenerator.SurfaceHeight(x, breach.Z, world.Params);

        while (x > Coastline.WestX(world.Params, breach.Z) && ground > _lake.Top - ExitDrop)
            ground = TerrainGenerator.SurfaceHeight(x -= 1f, breach.Z, world.Params);

        var exit = new Vector3(x - TunnelRadius, ground - 1f, breach.Z);
        var span = breach - exit;
        var length = span.Length();
        var count = Math.Min(MaxTunnelDigs - 1, (int)MathF.Ceiling(length / TunnelStep));
        _tunnelCount = 0;

        for (var i = 0; i <= count; i++)
        {
            var at = exit + span * (i / (float)count);
            // Never under the exit, or the mouth is a sump the water has to fill before it spills.
            var cover = TerrainGenerator.SurfaceHeight(at.X, at.Z, world.Params) - TunnelCover - TunnelRadius;
            at.Y = i == 0 || i == count ? at.Y : MathF.Max(exit.Y, MathF.Min(at.Y, cover));
            _tunnel[_tunnelCount++] = at;
        }

        // The digs must overlap: from the breach back, no dig more than TunnelStep under the next (a 45° climb at most).
        for (var i = _tunnelCount - 1; i > 0; i--)
            _tunnel[i - 1].Y = MathF.Max(_tunnel[i - 1].Y, _tunnel[i].Y - TunnelStep);

        // The deep breach drains down: from the breach the tunnel falls at 45° to the exit's height and runs level from
        // there (the rock is deep enough under the lake bed and the rim), so it is never a siphon over a sill.
        if (Deep)
            for (var i = 0; i < _tunnelCount - 1; i++)
                _tunnel[i].Y = MathF.Max(exit.Y, breach.Y - (_tunnelCount - 1 - i) * TunnelStep);

        Log.Info($"tunnel: {_tunnelCount} digs of {TunnelRadius} m from the exit at {exit} (ground {ground:F1} m) to the breach at {breach}, {length:F0} m");
    }

    /// <summary>
    /// The bank tunnel: the mouth BankInside inside the shore, BankUnder under the surface; then west, falling BankFall a
    /// metre to BankDepth under the surface, then level until the outer slope's ground lies under the tunnel's floor. Dug
    /// from the lake out, so _tunnel is reversed once planned: Profile reads it from the breach (the last entry).
    /// </summary>
    private void PlanBank()
    {
        var surface = _lake.Top + 0.5f;
        var z = _lake.Centre.Y;
        var x = _lake.Centre.X - Shore + BankInside;
        var y = surface - BankUnder;
        _tunnelCount = 0;

        while (_tunnelCount < MaxTunnelDigs && x > Coastline.WestX(world.Params, z))
        {
            _tunnel[_tunnelCount++] = new Vector3(x, y, z);

            if (_tunnelCount > 1 && TerrainGenerator.SurfaceHeight(x, z, world.Params) < y - TunnelRadius)
                break;

            x -= TunnelStep;
            y = MathF.Max(surface - BankDepth, y - BankFall * TunnelStep);
        }

        Log.Info($"bank tunnel: mouth {BankMouth} m at {_tunnel[0]}, then {_tunnelCount - 1} digs of {TunnelRadius} m down to {_tunnel[_tunnelCount - 1]}");
        Array.Reverse(_tunnel, 0, _tunnelCount);
    }

    /// <summary>Every live body but the sea: id, level (m), area (m²), unbooked ledger (m³).</summary>
    private string BodyList()
    {
        var list = "";

        for (var id = WaterBodies.Sea + 1; id < water.Bodies.Count; id++)
        {
            ref var body = ref water.Bodies[(ushort)id];
            if (body.Alive)
                list += $" #{id} level {body.Level:F2} area {body.Area:F0} ledger {body.Ledger:F2};";
        }

        return list;
    }

    private string Profile()
    {
        var profile = "";

        for (var i = _tunnelCount - 1; i >= 0; i -= 5)
        {
            var at = _tunnel[i];
            profile += $" {water.ColumnWater(at.X, at.Z, at.Y - TunnelRadius - 1f, at.Y + TunnelRadius + 1f):F2}";
        }

        var exit = _tunnel[0];
        foreach (var d in new[] { 5f, 10f })
        {
            var ground = TerrainGenerator.SurfaceHeight(exit.X - d, exit.Z, world.Params);
            profile += $" |{water.ColumnWater(exit.X - d, exit.Z, ground - 2f, ground + 3f):F2}";
        }

        return profile;
    }
}
