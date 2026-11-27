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
        set.Sample(point, 0.0, out var before);

        void Step(CoastCarrier carrier)
        {
            set.Update(camera, carrier, Hs, Frame);
            set.Sample(point, ++tick * (double)Frame, out var face);
            worst = MathF.Max(worst, MathF.Abs(face.Height - before.Height));
            peak = MathF.Max(peak, MathF.Abs(face.Height));
            before = face;

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
        Assert.True(worst <= BreakerStability.HeightJump, $"the face jumped {worst:F3} m in a tick");
        Assert.True(least > 0.999f, $"a window's maps summed to {least:F3} in a cross-fade");
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
