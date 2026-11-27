using System.Diagnostics;
using Brutal.Numerics;
using BrutalGame.Water.Coast;
using BrutalGame.Water.Ocean;
using Xunit.Abstractions;

namespace BrutalGame.Water.Tests;

/// <summary>
/// The region manager (breaking-waves-brief.md step 4): regions load round the camera where the coast is, unload behind
/// it, cross-fade when the carrier changes, sit against the camera for the GPU, and allocate nothing once warm.
/// </summary>
public sealed class CoastRegionSetTests(ITestOutputHelper output)
{
    private const float Frame = 1f / 60f;
    private const float Hs = 1.5f;

    private static readonly SeaTiles Tiles = CoastBakeTests.Tiles;
    private static readonly CoastWindow Home = new(Tiles, CoastBakeTests.Tile, 0, 0);
    private static readonly CoastCarrier Swell = CoastCarrier.From(0.3f, 0.6f, 1.2f);

    private static CoastRegionSet Beach() => new(Tiles, new PlaneSeabed(Tiles, CoastBakeTests.Tile, (u, _) => CoastBakeTests.BeachDepth(u)), 1);

    /// <summary>A camera <paramref name="height"/> m over the sea at the home tile's plane coordinates (u, v).</summary>
    private static double3 Camera(double u, double v, double height = 2.0) => Home.Direction(u, v) * (Tiles.SeaRadius + height);

    private static int Settle(CoastRegionSet set, double3 camera, CoastCarrier carrier, int limit = 4000)
    {
        for (var frame = 0; frame < limit; frame++)
        {
            set.Update(camera, carrier, Hs, Frame);

            if (set.Settled && !set.Busy)
                return frame;

            if (set.Busy)
                Thread.Sleep(1);
        }

        Assert.Fail("the regions never settled");
        return limit;
    }

    private static int Live(CoastRegionSet set)
    {
        var live = 0;

        foreach (var region in set.Regions)
            live += region.State == CoastRegionState.Live ? 1 : 0;

        return live;
    }

    [Fact]
    public void Regions_LoadWhereTheCoastIs_AndUnloadBehindTheCamera()
    {
        using var set = Beach();
        var frames = Settle(set, Camera(790.0, 512.0), Swell);
        output.WriteLine($"settled in {frames} frames: {set.Bakes} bakes, {set.Probes} probes, last bake {set.LastBakeMs:F1} ms");

        Assert.Equal(CoastRegionSet.MaxDesired, Live(set));
        Assert.Equal(CoastRegionSet.MaxDesired, set.Drawn);

        foreach (var region in set.Regions)
        {
            if (region.State != CoastRegionState.Live)
                continue;

            Assert.Equal(0, region.Key.X);
            Assert.InRange(region.Key.Z, -3, 3);
            Assert.Equal(1f, region.Weight);
            Assert.Equal(Swell, region.Key.Carrier);
        }

        // Along the shore: the windows ahead come in and the ones behind go.
        Settle(set, Camera(790.0, 512.0 + 2700.0), Swell);
        Assert.Equal(CoastRegionSet.MaxDesired, Live(set));

        foreach (var region in set.Regions)
        {
            if (region.State == CoastRegionState.Live)
                Assert.InRange(region.Key.Z, 0, 6);
        }

        // Out to sea past the unload distance, and from altitude: nothing is kept.
        Settle(set, Camera(790.0 - 9000.0, 512.0), Swell);
        Assert.Equal(0, set.Drawn);
        Settle(set, Camera(790.0, 512.0), Swell);
        Assert.Equal(CoastRegionSet.MaxDesired, set.Drawn);
        Settle(set, Camera(790.0, 512.0, 7000.0), Swell);
        Assert.Equal(0, set.Drawn);
        Assert.True(CoastRegionSet.ResidentBytes <= 16 * 1048576);
    }

    [Fact]
    public void LoadedRegion_StaysUntilTheUnloadDistance()
    {
        using var set = Beach();
        Settle(set, Camera(790.0, 512.0), Swell);

        // 4.5 km out to sea: past the load distance, inside the unload distance.
        Settle(set, Camera(790.0 - 4500.0, 512.0), Swell);
        Assert.True(set.Drawn > 0, "a loaded region was dropped inside the unload distance");

        using var fresh = Beach();
        Settle(fresh, Camera(790.0 - 4500.0, 512.0), Swell);
        Assert.Equal(0, fresh.Drawn);
    }

    [Fact]
    public void OpenWaterAndCalm_LoadNothing()
    {
        using var open = new CoastRegionSet(Tiles, new PlaneSeabed(Tiles, CoastBakeTests.Tile, (_, _) => 80f), 1);
        Settle(open, Camera(500.0, 500.0), Swell);
        Assert.Equal(0, open.Drawn);
        Assert.Equal(0, open.Bakes);
        Assert.True(open.Probes > 0);

        using var calm = Beach();
        Settle(calm, Camera(790.0, 512.0), default);
        Assert.Equal(0, calm.Drawn);
        Assert.Equal(0, calm.Bakes);
    }

    /// <summary>
    /// A new carrier bakes each window again and cross-fades: through it the weights of a window's maps sum to 1, and
    /// the face at a fixed point of the break line never jumps, nor does it when the regions first fade in.
    /// </summary>
    [Fact]
    public void LoadAndRebake_FadeWithoutAPop()
    {
        using var set = Beach();
        var point = Home.Direction(778.0, 512.0);
        var camera = Camera(790.0, 512.0);
        var turned = CoastCarrier.From(0.3f + 0.26f, 0.6f, 1.2f);
        var worst = 0f;
        var peak = 0f;
        var least = 1f;
        var tick = 0L;
        var stats = default(BreakerStability);
        set.Sample(point, 0.0, out var before);
        var first = before;

        void Step(CoastCarrier carrier)
        {
            set.Update(camera, carrier, Hs, Frame);
            set.Sample(point, ++tick * (double)Frame, out var face);
            worst = MathF.Max(worst, MathF.Abs(face.Height - before.Height));
            peak = MathF.Max(peak, MathF.Abs(face.Height));
            stats.Step(before, face);

            if (tick > 1)
                stats.Kick(first, before, face);

            (first, before) = (before, face);

            if (set.Busy)
                Thread.Sleep(1);
        }

        for (var i = 0; i < 2000 && !(set.Settled && !set.Busy && set.Drawn > 0); i++)
            Step(Swell);

        var loadWorst = worst;

        for (var i = 0; i < 6000; i++)
        {
            Step(turned);
            var sum = 0f;
            var maps = 0;

            foreach (var region in set.Regions)
            {
                if (!region.Drawn || region.Key.X != 0 || region.Key.Z != 0)
                    continue;

                sum += region.Weight;
                maps++;
            }

            if (maps > 1)
                least = MathF.Min(least, sum);

            if (set.Settled && !set.Busy)
                break;
        }

        output.WriteLine($"largest height step in a tick: {loadWorst:F3} m through the load, {worst:F3} m through load and rebake; peak height {peak:F2} m; least weight sum {least:F3}");

        foreach (var region in set.Regions)
        {
            if (region.State == CoastRegionState.Live)
                Assert.Equal(turned, region.Key.Carrier);
        }

        Assert.Equal(CoastRegionSet.MaxDesired, Live(set));
        Assert.True(peak > 0.3f, "the point never saw a wave");
        output.WriteLine(stats.ToString());
        Assert.True(worst <= BreakerStability.HeightJump, $"the face jumped {worst:F3} m in a tick");
        Assert.Equal(0, stats.Jumps);
        Assert.Equal(0, stats.Kicks);
        Assert.True(least > 0.999f, $"a window's maps summed to {least:F3} in a cross-fade");
    }

    /// <summary>In the cells two windows share the two maps are drawn as one wave: the blended face there moves as smoothly as one map's.</summary>
    [Fact]
    public void Overlap_MovesAsOneWave()
    {
        using var set = Beach();
        Settle(set, Camera(790.0, 960.0), Swell);
        var stats = default(BreakerStability);
        var maps = 0;

        foreach (var region in set.Regions)
        {
            region.Window.ToLocal(Home.Direction(770.0, 960.0), out var x, out var z);
            maps += region.Drawn && CoastWindow.Weight((float)x / CoastWindow.CellSize, (float)z / CoastWindow.CellSize) > 0.3f ? 1 : 0;
        }

        Assert.Equal(2, maps);

        for (var u = 690.0; u <= 800.0; u += 2.3)
        {
            for (var v = 930.0; v <= 990.0; v += 30.0)
            {
                var point = Home.Direction(u, v);
                set.Sample(point, 0.0, out var first);
                set.Sample(point, Frame, out var before);
                stats.Step(first, before);

                for (var tick = 2; tick <= 600; tick++)
                {
                    set.Sample(point, tick * (double)Frame, out var face);
                    stats.Step(before, face);
                    stats.Kick(first, before, face);
                    (first, before) = (before, face);
                }
            }
        }

        output.WriteLine(stats.ToString());
        Assert.Equal(0, stats.Jumps);
        Assert.Equal(0, stats.Kicks);
        Assert.True(stats.MaxHeight > 0.5f && stats.MaxFoam > 0.5f, "the wave never broke in the shared cells");
    }

    /// <summary>Inside the tolerance a new carrier keeps the maps and only the drawn height follows; past it they are baked again.</summary>
    [Fact]
    public void CarrierInsideTheTolerance_KeepsTheMaps()
    {
        using var set = Beach();
        var camera = Camera(790.0, 512.0);
        Settle(set, camera, Swell);
        var bakes = set.Bakes;
        var near = CoastCarrier.From(Swell.Direction + 0.008f, Swell.Omega * 1.005f, Swell.Height * 1.03f);
        Assert.True(CoastRegionSet.Keeps(Swell, near));
        var point = Home.Direction(778.0, 512.0);
        set.Sample(point, 0.0, out var before);
        var worst = 0f;

        for (var i = 1; i <= 600; i++)
        {
            set.Update(camera, near, Hs, Frame);
            set.Sample(point, i * (double)Frame, out var face);
            worst = MathF.Max(worst, MathF.Abs(face.Height - before.Height));
            before = face;
        }

        Assert.Equal(bakes, set.Bakes);
        Assert.Equal(Swell, set.BakedFor(CoastBakeTests.Tile));
        Assert.True(worst <= 0.03f, $"the face moved {worst:F3} m in a tick as the height followed");

        for (var slot = 0; slot < CoastRegionSet.Capacity; slot++)
        {
            if (set.Regions[slot].Drawn)
                Assert.Equal(near.Height, set.Atlas(slot).Height, 0.002f);
        }

        var far = CoastCarrier.From(Swell.Direction + 0.05f, Swell.Omega, Swell.Height);
        Assert.False(CoastRegionSet.Keeps(Swell, far));
        Assert.False(CoastRegionSet.Keeps(Swell, CoastCarrier.From(Swell.Direction, Swell.Omega * 1.02f, Swell.Height)));
        Assert.False(CoastRegionSet.Keeps(Swell, CoastCarrier.From(Swell.Direction, Swell.Omega, Swell.Height * 1.08f)));
        Assert.False(CoastRegionSet.Keeps(Swell, default));
        Settle(set, camera, far);
        Assert.Equal(far, set.BakedFor(CoastBakeTests.Tile));
        Assert.Equal(bakes + CoastRegionSet.MaxDesired, set.Bakes);
    }

    /// <summary>A window dropped and wanted again before its map has faded out fades back in: no bake, and its weight never steps.</summary>
    [Fact]
    public void RetiringWindow_WantedAgain_FadesBackIn()
    {
        using var set = Beach();
        var camera = Camera(790.0, 512.0);
        Settle(set, camera, Swell);
        var bakes = set.Bakes;
        var away = Camera(790.0, 512.0, 7000.0);
        var before = Weights(set);
        var worst = 0f;

        void Step(double3 at)
        {
            set.Update(at, Swell, Hs, Frame);
            var now = Weights(set);
            worst = MathF.Max(worst, MathF.Abs(now - before));
            before = now;
        }

        for (var i = 0; i < 40; i++)
            Step(away);

        Assert.InRange(before, 0.1f * CoastRegionSet.MaxDesired, 0.9f * CoastRegionSet.MaxDesired);

        for (var i = 0; i < 240; i++)
            Step(camera);

        Assert.Equal(bakes, set.Bakes);
        Assert.Equal(CoastRegionSet.MaxDesired, Live(set));
        Assert.Equal((float)CoastRegionSet.MaxDesired, before, 0.001f);
        Assert.True(worst < 0.15f, $"the weights stepped {worst:F3} in a frame");
    }

    private static unsafe float Share(int tile, in double3 direction)
    {
        Tiles.Hand(direction, out var hand);

        for (var k = 0; k < hand.Count; k++)
        {
            if (hand.Tile[k] == tile)
                return hand.Weight[k];
        }

        return 0f;
    }

    /// <summary>
    /// Where a coast crosses a sea tile's edge the carrier is handed from one tile to the next over a narrow band: both
    /// tiles' regions load on their own sides, their weights sum to 1 across it, their carriers agree in phase at the
    /// sea point under the camera, and the face does not jump as the camera crosses at a run. At 40° of latitude, where
    /// the tiles' frames are turned 3° from each other.
    /// </summary>
    private const double EdgeShore = 800.0;

    /// <summary>A tile at 40° of latitude, a window of its lattice, and where along the window's u the tile's share of the carrier is one half (m); the shore runs along v = <see cref="EdgeShore"/>.</summary>
    private static unsafe (int Tile, CoastWindow Plane, double Edge) TileEdge()
    {
        var axis = double3.Normalize(new double3(0.2, 1.0, -0.1));
        var (sin, cos) = Math.SinCos(40.0 * Math.PI / 180.0);
        Tiles.Locate(double3.Normalize(double3.Normalize(double3.Cross(axis, double3.UnitX)) * cos + axis * sin), out var blend);
        var tile = blend.Tile[0];
        var plane = new CoastWindow(Tiles, tile, 0, 0);
        var (lo, hi) = (0.0, 40_000.0);
        Assert.Equal(1f, Share(tile, plane.Direction(lo, EdgeShore)));
        Assert.Equal(0f, Share(tile, plane.Direction(hi, EdgeShore)));

        for (var i = 0; i < 40; i++)
        {
            var mid = 0.5 * (lo + hi);
            (lo, hi) = Share(tile, plane.Direction(mid, EdgeShore)) > 0.5f ? (mid, hi) : (lo, mid);
        }

        return (tile, plane, lo);
    }

    private static CoastRegionSet EdgeBeach(int tile) => new(Tiles, new PlaneSeabed(Tiles, tile, (_, v) => (float)(0.04 * (EdgeShore - v))), 1);

    [Fact]
    public unsafe void TileEdge_HandsTheCarrierOver()
    {
        const double shore = EdgeShore;
        const double pace = 0.2;
        var (tile, plane, edge) = TileEdge();
        var carrier = CoastCarrier.From(1.3f, 0.6f, 1.2f);
        using var set = EdgeBeach(tile);
        double3 Camera(double u) => plane.Direction(u, shore - 40.0) * (Tiles.SeaRadius + 2.0);
        Settle(set, Camera(edge), carrier);
        var home = 0;
        var other = 0;

        foreach (var region in set.Regions)
        {
            if (region.State != CoastRegionState.Live)
                continue;

            home += region.Key.Tile == tile ? 1 : 0;
            other += region.Key.Tile == tile ? 0 : 1;
        }

        output.WriteLine($"edge at u {edge:F0} m: {home} regions of the tile, {other} of its neighbour");
        Assert.True(home > 0 && other > 0, "both tiles' regions load at their edge");
        var band = 0;

        for (var u = edge - 600.0; u <= edge + 600.0; u += 10.0)
        {
            var share = Share(tile, plane.Direction(u, shore - 60.0));
            band += share is > 0f and < 1f ? 1 : 0;
            Assert.Equal(1f, set.Coverage(plane.Direction(u, shore - 60.0)), 0.002f);
        }

        Assert.InRange(band * 10.0, 300.0, 900.0);

        // At the sea point under the camera the two tiles' carriers are the same wave, to the lag between two solves
        // whose directions differ as the tiles' frames do; unmatched they would be anywhere up to π apart.
        for (var along = -150.0; along <= 150.0; along += 75.0)
        {
            set.Update(Camera(edge + along), carrier, Hs, Frame);
            var under = plane.Direction(edge + along, shore - 40.0);
            var phaseHome = float.NaN;
            var phaseOther = float.NaN;

            for (var slot = 0; slot < CoastRegionSet.Capacity; slot++)
            {
                var region = set.Regions[slot];
                region.Window.ToLocal(under, out var x, out var z);

                if (!region.Drawn || CoastWindow.Weight((float)x / CoastWindow.CellSize, (float)z / CoastWindow.CellSize) < 0.5f)
                    continue;

                var phase = SurfCarrier.At(set.Atlas(slot), (float)x, (float)z, set.PhaseAt(slot, 7.0)).Phase;

                if (region.Key.Tile == tile)
                    phaseHome = phase;
                else
                    phaseOther = phase;
            }

            var apart = MathF.Abs(SurfCarrier.Wrap(phaseHome - phaseOther));
            output.WriteLine($"carriers under the camera {along:F0} m along the edge: {apart:F3} rad apart");
            Assert.True(apart < 0.5f, $"the tiles' carriers are {apart:F2} rad apart under the camera");
        }

        // Crossing the hand-over at 12 m/s: the face at fixed points in it moves as a wave does.
        var stats = default(BreakerStability);
        var watch = new double3[3];
        var last = new FacePoint[3];
        var tick = 0L;
        set.Update(Camera(edge - 300.0), carrier, Hs, Frame);

        for (var i = 0; i < watch.Length; i++)
        {
            watch[i] = plane.Direction(edge - 120.0 + 120.0 * i, shore - 45.0);
            set.Sample(watch[i], 0.0, out last[i]);
        }

        for (var u = edge - 300.0; u <= edge + 300.0; u += pace)
        {
            set.Update(Camera(u), carrier, Hs, Frame);
            tick++;

            for (var i = 0; i < watch.Length; i++)
            {
                set.Sample(watch[i], tick * (double)Frame, out var face);
                stats.Step(last[i], face);
                last[i] = face;
            }

            if (set.Busy)
                Thread.Sleep(1);
        }

        output.WriteLine($"crossing the hand-over: {stats}");
        Assert.True(stats.MaxHeight > 0.3f, "the points never saw a wave");
        Assert.True(stats.MaxHeightStep < 0.04f, $"the face moved {stats.MaxHeightStep:F3} m in a frame");
        Assert.Equal(0, stats.HeightJumps + stats.FoamJumps + stats.SlopeJumps);
    }

    /// <summary>Its own carrier on one tile, another on every other: a tile edge between two swells.</summary>
    private sealed class TwoCarriers(int home, float hs) : ICoastCarriers
    {
        public CoastCarrier Home;
        public CoastCarrier Other;

        public CoastCarrier At(int tile, out float significantHeight)
        {
            significantHeight = hs;
            return tile == home ? Home : Other;
        }
    }

    /// <summary>
    /// Two tiles with swells of different periods and directions meet on a coast: each tile's windows are baked for its
    /// own carrier, they cross-fade across the hand-over band, and the face there moves as a wave does with the camera
    /// parked in the band, crossing it at a run, and through the neighbour's rebake to a third carrier (which leaves the
    /// home tile's maps and their phases alone).
    /// </summary>
    [Fact]
    public void TileEdge_TwoSwells_EachTileBakesItsOwn()
    {
        var (tile, plane, edge) = TileEdge();
        var carriers = new TwoCarriers(tile, Hs) { Home = CoastCarrier.From(1.3f, 0.6f, 1.2f), Other = CoastCarrier.From(1.0f, 0.85f, 0.9f) };
        using var set = EdgeBeach(tile);
        double3 Camera(double u) => plane.Direction(u, EdgeShore - 40.0) * (Tiles.SeaRadius + 2.0);
        var tick = 0L;

        void Step(double3 camera)
        {
            set.Update(camera, carriers, Frame);
            tick++;

            if (set.Busy)
                Thread.Sleep(1);
        }

        for (var i = 0; i < 4000 && !(set.Settled && !set.Busy && set.Drawn > 0); i++)
            Step(Camera(edge));

        var home = 0;
        var other = 0;

        foreach (var region in set.Regions)
        {
            if (region.State != CoastRegionState.Live)
                continue;

            Assert.Equal(region.Key.Tile == tile ? carriers.Home : carriers.Other, region.Key.Carrier);
            home += region.Key.Tile == tile ? 1 : 0;
            other += region.Key.Tile == tile ? 0 : 1;
        }

        output.WriteLine($"{home} regions of the tile, {other} of its neighbour");
        Assert.True(home > 0 && other > 0, "both tiles' regions load at their edge");
        Assert.Equal(carriers.Home, set.BakedFor(tile));

        for (var u = edge - 600.0; u <= edge + 600.0; u += 10.0)
            Assert.Equal(1f, set.Coverage(plane.Direction(u, EdgeShore - 60.0)), 0.002f);

        var watch = new double3[7];
        var last = new FacePoint[watch.Length];
        var earlier = new FacePoint[watch.Length];

        for (var i = 0; i < watch.Length; i++)
            watch[i] = plane.Direction(edge - 300.0 + 100.0 * i, EdgeShore - 45.0);

        BreakerStability Follow(Func<int, double3> camera, int ticks)
        {
            var stats = default(BreakerStability);
            Step(camera(0));

            for (var i = 0; i < watch.Length; i++)
            {
                set.Sample(watch[i], (tick - 1) * (double)Frame, out earlier[i]);
                set.Sample(watch[i], tick * (double)Frame, out last[i]);
            }

            for (var n = 1; n <= ticks; n++)
            {
                Step(camera(n));

                for (var i = 0; i < watch.Length; i++)
                {
                    set.Sample(watch[i], tick * (double)Frame, out var face);
                    stats.Step(last[i], face);
                    stats.Kick(earlier[i], last[i], face);
                    (earlier[i], last[i]) = (last[i], face);
                }
            }

            return stats;
        }

        var parked = Follow(_ => Camera(edge), 1200);
        output.WriteLine($"parked in the band: {parked}");
        var crossing = Follow(n => Camera(edge - 300.0 + 0.2 * n), 3000);
        output.WriteLine($"crossing the band at 12 m/s: {crossing}");

        Step(Camera(edge + 60.0));
        var versions = new int[CoastRegionSet.Capacity];
        var phases = new float[CoastRegionSet.Capacity];

        for (var slot = 0; slot < CoastRegionSet.Capacity; slot++)
        {
            var region = set.Regions[slot];
            versions[slot] = region.Key.Tile == tile && region.State == CoastRegionState.Live ? region.Version : -1;
            phases[slot] = versions[slot] >= 0 ? set.PhaseAt(slot, 0.0) : 0f;
        }

        carriers.Other = CoastCarrier.From(0.7f, 1.0f, 1.1f);
        var bakes = set.Bakes;
        var rebake = Follow(_ => Camera(edge + 60.0), 1500);
        output.WriteLine($"through the neighbour's rebake ({set.Bakes - bakes} bakes): {rebake}");
        Assert.True(set.Settled && set.Bakes > bakes, "the neighbour's windows never rebaked");

        for (var slot = 0; slot < CoastRegionSet.Capacity; slot++)
        {
            if (versions[slot] < 0)
                continue;

            Assert.Equal(versions[slot], set.Regions[slot].Version);
            Assert.Equal(phases[slot], set.PhaseAt(slot, 0.0), 1e-4f);
        }

        foreach (var region in set.Regions)
        {
            if (region.Drawn)
                Assert.Equal(region.Key.Tile == tile ? carriers.Home : carriers.Other, region.Key.Carrier);
        }

        foreach (var stats in new[] { parked, crossing, rebake })
        {
            Assert.True(stats.MaxHeight > 0.3f, "the points never saw a wave");
            Assert.Equal(0, stats.Jumps);
            Assert.Equal(0, stats.Kicks);
        }
    }

    private static float Weights(CoastRegionSet set)
    {
        var sum = 0f;

        foreach (var region in set.Regions)
            sum += region.Drawn ? region.Weight : 0f;

        return sum;
    }

    /// <summary>The GPU's map from the camera-relative sea frame to a region's cells lands where the window says, to the linearisation's error.</summary>
    [Fact]
    public unsafe void Placement_PutsTheRegionAgainstTheCamera()
    {
        using var set = Beach();
        var camera = Camera(700.0, 300.0, 3.0);
        Settle(set, camera, Swell);
        var frame = SeaFrame.At(Home.Direction(650.0, 380.0) * Tiles.SeaRadius, double3.Normalize(new double3(0.2, 1.0, -0.1)), Tiles.SeaRadius);
        var local = frame.ToLocal(camera);
        SeaSlots.Build(Tiles, frame, local.X, local.Z, 6000.0, out var slots);
        var placed = 0;

        foreach (var region in set.Regions)
        {
            if (!region.Drawn)
                continue;

            Assert.True(CoastPlacement.Of(region, slots, out var placement));
            Assert.Equal(CoastBakeTests.Tile, slots.Tile[placement.Slot]);
            placed++;

            for (var i = 0; i < 5; i++)
            {
                var x = 100f + 200f * i;
                var z = 900f - 190f * i;
                var at = frame.ToLocal(region.Window.Direction(x, z) * Tiles.SeaRadius);
                var rx = (float)(at.X - local.X);
                var rz = (float)(at.Z - local.Z);
                var cellX = placement.CellX.X * rx + placement.CellX.Y * rz + placement.CellX.Z;
                var cellZ = placement.CellZ.X * rx + placement.CellZ.Y * rz + placement.CellZ.Z;
                var far = MathF.Sqrt(rx * rx + rz * rz);
                var slack = 0.02f + far * far * 4e-8f;

                Assert.Equal(x / CoastWindow.CellSize, cellX, slack);
                Assert.Equal(z / CoastWindow.CellSize, cellZ, slack);
            }
        }

        Assert.Equal(CoastRegionSet.MaxDesired, placed);
    }

    [Fact]
    public void Update_AllocatesNothingOnceWarm()
    {
        using var set = Beach();
        var turned = CoastCarrier.From(0.9f, 0.5f, 1.4f);

        // Warm up every path: load, move, unload, rebake, and the face sample.
        Settle(set, Camera(790.0, 512.0), Swell);
        Settle(set, Camera(790.0, 2500.0), Swell);
        Settle(set, Camera(790.0, 512.0), turned);
        set.Sample(Home.Direction(778.0, 512.0), 1.0, out _);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var frames = 0;

        for (var pass = 0; pass < 3; pass++)
        {
            frames += Settle(set, Camera(790.0, 512.0 + 1800.0 * pass), pass == 1 ? Swell : turned);

            for (var i = 0; i < 300; i++)
            {
                set.Update(Camera(790.0 + i, 512.0 + 1800.0 * pass + 3.0 * i), pass == 1 ? Swell : turned, Hs, Frame);
                set.Sample(Home.Direction(778.0, 512.0 + 1800.0 * pass), i * (double)Frame, out _);
                frames++;
            }
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"{frames} updates, {set.Bakes} bakes, {set.Probes} probes: {allocated} bytes allocated on the calling thread");
        Assert.Equal(0, allocated);
    }

    /// <summary>The carrier the maps are keyed by is the swell's target: it steps when a wind is taken up and holds through the blend, while the drawn swell turns.</summary>
    [Fact]
    public void SwellTargetCarrier_StepsWhenAWindIsTakenUp()
    {
        var swell = new OceanSwell(5);
        swell.SetWind(new SwellParams { WindSpeed = 6f, SwellHeight = 1.5f, SwellPeriod = 12f, SwellDirection = 0.4f });
        swell.SetTime(1.0);
        var first = CoastCarrier.From(swell.TargetCarrierDirection, swell.TargetCarrierOmega, swell.TargetCarrierHeight);
        output.WriteLine($"target carrier: direction {first.Direction:F3}, omega {first.Omega:F3}, height {first.Height:F2} m, Hs {swell.TargetSignificantHeight:F2} m");

        Assert.Equal(MathF.Tau / 12f, first.Omega, 0.03f);
        Assert.Equal(0.4f, first.Direction, 0.002f);
        Assert.InRange(first.Height, 0.4f, 1.5f);
        Assert.Equal(swell.SignificantWaveHeight, swell.TargetSignificantHeight, 0.01f);

        swell.SetWind(new SwellParams { WindSpeed = 6f, SwellHeight = 1.5f, SwellPeriod = 12f, SwellDirection = 0.9f });
        var second = CoastCarrier.From(swell.TargetCarrierDirection, swell.TargetCarrierOmega, swell.TargetCarrierHeight);
        Assert.Equal(0.9f, second.Direction, 0.002f);

        for (var t = 2.0; t < 2.0 + swell.BlendSeconds; t += 1.0)
        {
            swell.SetTime(t);
            Assert.Equal(second, CoastCarrier.From(swell.TargetCarrierDirection, swell.TargetCarrierOmega, swell.TargetCarrierHeight));
        }

        Assert.True(new OceanSwell(1).TargetCarrierOmega == 0f);
    }

    [Fact]
    public void Bake_AllocatesNothingOnItsThread()
    {
        var bake = new CoastBake();
        var atlas = new CoastAtlas();
        var seabed = new PlaneSeabed(Tiles, CoastBakeTests.Tile, (u, _) => CoastBakeTests.BeachDepth(u));
        var key = new CoastRegionKey(CoastBakeTests.Tile, 0, 0, Swell);
        bake.Prepare(seabed, Home, key, atlas, CoastBake.BreakDepth(Hs));
        bake.Run();
        var before = GC.GetAllocatedBytesForCurrentThread();
        bake.Prepare(seabed, Home, key with { Carrier = CoastCarrier.From(1f, 0.7f, 0.9f) }, atlas, CoastBake.BreakDepth(Hs));
        bake.Run();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(bake.Baked);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void UpdateCost()
    {
        using var set = Beach();
        Settle(set, Camera(790.0, 512.0), Swell);
        var still = Stopwatch.GetTimestamp();

        for (var i = 0; i < 10000; i++)
            set.Update(Camera(790.0, 512.0), Swell, Hs, Frame);

        var stillMs = Stopwatch.GetElapsedTime(still).TotalMilliseconds / 10000;
        var times = new double[20000];
        var bakes = set.Bakes;

        for (var i = 0; i < times.Length; i++)
        {
            var start = Stopwatch.GetTimestamp();
            set.Update(Camera(790.0, 512.0 + 0.5 * i), Swell, Hs, Frame);
            times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds * 1000.0;
        }

        var over = times.Count(t => t > 50.0);
        var mean = times.Average();
        Array.Sort(times);
        output.WriteLine($"update: {stillMs * 1000:F2} us a frame at rest; flying 30 m/s over 10 km ({set.Bakes - bakes} bakes started): mean {mean:F2} us, p99 {times[(int)(times.Length * 0.99)]:F1} us, p99.9 {times[(int)(times.Length * 0.999)]:F1} us, worst {times[^1]:F0} us, {over} of {times.Length} frames over 50 us");
    }
}
