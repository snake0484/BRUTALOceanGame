using System.Diagnostics;
using System.Runtime.InteropServices;
using Brutal.Numerics;
using BrutalGame.Water.Coast;
using BrutalGame.Water.Ocean;
using Xunit.Abstractions;

namespace BrutalGame.Water.Tests;

/// <summary>A seabed given in a sea tile's plane (m), so neighbouring coast windows see the same ground.</summary>
internal sealed class PlaneSeabed(SeaTiles tiles, int tile, Func<double, double, float> depth) : ICoastDepth
{
    public float GeneratedDepth(in double3 direction)
    {
        tiles.Plane(tile, direction, out var u, out var v);
        return depth(u, v);
    }
}

/// <summary>
/// The carrier bake (breaking-waves-brief.md step 2; the old CoastalFieldTests): refraction and dispersion of the phase,
/// shoaling of the amplitude, open water left alone, the break clock, and maps that are a pure function of their key.
/// </summary>
public sealed class CoastBakeTests(ITestOutputHelper output)
{
    internal const double SeaRadius = 1_000_150.0;
    internal const int Tile = 40;
    private const float Cell = CoastWindow.CellSize;
    private const double Gravity = 9.81;

    internal static readonly SeaTiles Tiles = new(SeaRadius, double3.Normalize(new double3(0.2, 1.0, -0.1)));

    /// <summary>Plane beach across the window at lattice (0, 0): depth falls 1 in 25 along +u to the shoreline at u = 800, clear of the window's blend.</summary>
    internal static float BeachDepth(double u) => (float)(0.04 * (800.0 - u));

    internal static CoastAtlas Bake(Func<double, double, float> depth, CoastCarrier carrier, int x = 0, int z = 0, CoastBake? bake = null)
    {
        var key = new CoastRegionKey(Tile, (short)x, (short)z, carrier);
        var atlas = new CoastAtlas();
        bake ??= new CoastBake();
        bake.Prepare(new PlaneSeabed(Tiles, Tile, depth), new CoastWindow(Tiles, key), key, atlas, float.PositiveInfinity);
        bake.Run();
        Assert.True(bake.Baked);
        return atlas;
    }

    private static (float X, float Z) Wavevector(CoastAtlas atlas, float x, float z)
    {
        atlas.Sample(x + Cell, z, out var east);
        atlas.Sample(x - Cell, z, out var west);
        atlas.Sample(x, z + Cell, out var south);
        atlas.Sample(x, z - Cell, out var north);
        return ((east.Phase - west.Phase) / (2f * Cell), (south.Phase - north.Phase) / (2f * Cell));
    }

    [Fact]
    public void Refraction_FollowsSnellAndDispersion()
    {
        const float theta0 = MathF.PI / 6f;
        var carrier = CoastCarrier.From(theta0, 0.456f, 1f);
        var atlas = Bake((u, _) => BeachDepth(u), carrier);
        var omega = (double)carrier.Omega;
        var k0 = omega * omega / Gravity;
        var along = k0 * Math.Sin(carrier.Direction);
        var worst = 0.0;
        var worstDirection = 0.0;

        for (var x = 100f; x <= 700f; x += 100f)
        {
            var (kx, kz) = Wavevector(atlas, x, 512f);
            var k = Math.Sqrt(kx * kx + kz * kz);
            var expected = Jonswap.WaveNumber(omega, BeachDepth(x));
            atlas.Sample(x, 512f, out var s);
            var angle = Math.Atan2(s.DirZ, s.DirX);
            var snell = Math.Asin(along / expected);
            worst = Math.Max(worst, Math.Max(Math.Abs(k / expected - 1.0), Math.Abs(kz / along - 1.0)));
            worstDirection = Math.Max(worstDirection, Math.Abs(angle - snell));
            output.WriteLine($"x {x} h {BeachDepth(x):F1}: |k| {k:F4} vs {expected:F4}, k_along {kz:F4} vs {along:F4}, direction {angle * 180 / Math.PI:F1} vs {snell * 180 / Math.PI:F1} deg");
        }

        Assert.True(worst < 0.03, $"worst relative error {worst:P1}");
        Assert.True(worstDirection < 0.03, $"worst direction error {worstDirection:F3} rad");
    }

    [Fact]
    public void NormalIncidence_ShoalsLikeGreensLaw_AndLagGrowsIntoTheShallows()
    {
        var carrier = CoastCarrier.From(0f, 0.38f, 1f);
        var atlas = Bake((u, _) => BeachDepth(u), carrier);
        var omega = (double)carrier.Omega;
        var k0 = (float)(omega * omega / Gravity);
        var cg0 = 0.5 * Gravity / omega;
        var worst = 0.0;
        var lagBefore = float.NegativeInfinity;

        for (var x = 100f; x <= 700f; x += 100f)
        {
            double h = BeachDepth(x);
            var k = Jonswap.WaveNumber(omega, h);
            var kh = k * h;
            var cg = 0.5 * (1.0 + 2.0 * kh / Math.Sinh(2.0 * kh)) * omega / k;
            var ks = Math.Sqrt(cg0 / cg);
            atlas.Sample(x, 512f, out var s);
            var lag = s.Phase - k0 * x;
            worst = Math.Max(worst, Math.Abs(s.Amplitude / ks - 1.0));
            output.WriteLine($"x {x} h {h:F1}: amp {s.Amplitude:F3} vs Ks {ks:F3}, lag {lag:F2} rad, depth {s.Depth:F2}");

            Assert.True(lag > lagBefore, "the lag against deep water grows toward the shore");
            Assert.Equal(h, s.Depth, 0.05);
            lagBefore = lag;
        }

        Assert.True(worst < 0.05, $"worst relative error {worst:P1}");
    }

    [Fact]
    public void DeepWater_GivesNoLagAndUnitAmplitude()
    {
        var carrier = CoastCarrier.From(0.7f, 0.9f, 1.5f);
        var atlas = Bake((_, _) => 800f, carrier);
        var k0 = carrier.Omega * carrier.Omega / (float)Gravity;
        var (sin, cos) = MathF.SinCos(carrier.Direction);

        for (var x = 30f; x < 1000f; x += 97f)
        {
            atlas.Sample(x, 431f, out var s);
            Assert.Equal(k0 * (cos * x + sin * 431f), s.Phase, 0.02f);
            Assert.Equal(1f, s.Amplitude, 0.01f);
            Assert.Equal(0f, s.BreakDistance);
            Assert.Equal(cos, s.DirX, 0.02f);
            Assert.Equal(sin, s.DirZ, 0.02f);
        }
    }

    [Fact]
    public void BreakClock_StartsWhereTheWaveReachesItsLimit_AndRunsToTheShore()
    {
        var carrier = CoastCarrier.From(0f, 0.6f, 1.2f);
        var atlas = Bake((u, _) => BeachDepth(u), carrier);
        var started = float.NaN;
        var before = 0f;

        for (var x = 300f; x < 790f; x += 2f)
        {
            atlas.Sample(x, 512f, out var s);
            var raw = SurfCarrier.RawCap(SurfCarrier.Unbroken(carrier.Height, carrier.Omega, s.Depth, s.Amplitude), s.Depth);

            if (float.IsNaN(started) && s.BreakDistance > 0.5f)
            {
                started = x;
                output.WriteLine($"clock starts at x {x}, depth {s.Depth:F2}, cap {raw:F3}");
                Assert.InRange(raw, CoastBake.ClockStartCap - 0.12f, CoastBake.ClockStartCap + 0.02f);
            }

            if (!float.IsNaN(started) && s.Depth > 0.4f)
                Assert.True(s.BreakDistance >= before - 0.01f, $"clock runs backwards at x {x}");

            before = s.BreakDistance;
        }

        atlas.Sample(770f, 512f, out var shore);
        Assert.False(float.IsNaN(started), "the wave never broke");
        Assert.Equal(770f - started, shore.BreakDistance, 8f);
    }

    [Fact]
    public void BreakerType_FollowsTheBedSlope()
    {
        var carrier = CoastCarrier.From(0f, 0.6f, 1.2f);
        var gentle = Bake((u, _) => (float)(0.01 * (800.0 - u)), carrier);
        var steep = Bake((u, _) => (float)(0.08 * (800.0 - u)), carrier);
        gentle.Sample(740f, 512f, out var g);
        steep.Sample(785f, 512f, out var s);
        output.WriteLine($"plunge: 1 in 100 {g.Plunge:F2} (clock {g.BreakDistance:F0} m), 1 in 12.5 {s.Plunge:F2} (clock {s.BreakDistance:F0} m)");

        Assert.True(g.BreakDistance > 1f && s.BreakDistance > 1f);
        Assert.True(g.Plunge < 0.2f, $"a 1 in 100 beach spills ({g.Plunge:F2})");
        Assert.True(s.Plunge > 0.8f, $"a 1 in 12.5 beach plunges ({s.Plunge:F2})");
    }

    [Fact]
    public void Bake_IsAPureFunctionOfItsKey()
    {
        static float Bay(double u, double v) => (float)(0.03 * (900.0 - u) + 6.0 * Math.Sin(v / 180.0));
        var carrier = CoastCarrier.From(0.4f, 0.5f, 1.4f);
        var shared = new CoastBake();
        var first = Bake(Bay, carrier, bake: shared);
        Bake((u, _) => BeachDepth(u), CoastCarrier.From(-0.3f, 0.8f, 0.7f), bake: shared);
        var again = Bake(Bay, carrier, bake: shared);
        var fresh = Bake(Bay, carrier);

        Assert.True(MemoryMarshal.AsBytes<float>(first.Phase).SequenceEqual(MemoryMarshal.AsBytes<float>(again.Phase)));
        Assert.True(MemoryMarshal.AsBytes<Half>(first.Fields).SequenceEqual(MemoryMarshal.AsBytes<Half>(again.Fields)));
        Assert.True(first.Direction.AsSpan().SequenceEqual(again.Direction));
        Assert.True(MemoryMarshal.AsBytes<float>(first.Phase).SequenceEqual(MemoryMarshal.AsBytes<float>(fresh.Phase)));
        Assert.True(MemoryMarshal.AsBytes<Half>(first.Fields).SequenceEqual(MemoryMarshal.AsBytes<Half>(fresh.Fields)));
        Assert.True(CoastAtlas.Bytes <= 1.5 * 1048576, $"{CoastAtlas.Bytes} bytes a region");
    }

    [Fact]
    public void Probe_BakesOnlyWhereThereIsACoast()
    {
        var key = new CoastRegionKey(Tile, 0, 0, CoastCarrier.From(0f, 0.6f, 1.2f));
        var window = new CoastWindow(Tiles, key);
        var bake = new CoastBake();
        var breakDepth = CoastBake.BreakDepth(1.5f);

        bake.Prepare(new PlaneSeabed(Tiles, Tile, (_, _) => 60f), window, key, new CoastAtlas(), breakDepth);
        bake.Run();
        Assert.False(bake.Baked, "open water");

        bake.Prepare(new PlaneSeabed(Tiles, Tile, (_, _) => -12f), window, key, new CoastAtlas(), breakDepth);
        bake.Run();
        Assert.False(bake.Baked, "land");

        bake.Prepare(new PlaneSeabed(Tiles, Tile, (u, _) => BeachDepth(u)), window, key, new CoastAtlas(), breakDepth);
        bake.Run();
        Assert.True(bake.Baked, "a beach");
        Assert.InRange(bake.ProbeMin, -9f, -7f);
        Assert.InRange(bake.ProbeMax, 30f, 33f);

        bake.Prepare(new PlaneSeabed(Tiles, Tile, (u, _) => BeachDepth(u)), window, key with { Carrier = default }, new CoastAtlas(), breakDepth);
        bake.Run();
        Assert.False(bake.Baked, "calm");
    }

    /// <summary>
    /// Neighbouring windows are solved apart, each inside its own coarse domain, and cross-fade over the cells they
    /// share: there the two maps must show the same wave. The beach runs at 30° to the lattice and the swell comes in
    /// 25° off its normal.
    /// </summary>
    [Fact]
    public void NeighbourWindows_ShowTheSameWaveWhereTheyOverlap()
    {
        var (sin, cos) = Math.SinCos(Math.PI / 6.0);
        float Beach(double u, double v) => (float)(0.04 * (1300.0 - (u * cos + v * sin)));
        var carrier = CoastCarrier.From(MathF.PI / 6f + 0.44f, 0.5f, 1.2f);
        var a = Bake(Beach, carrier, 0, 0);
        var b = Bake(Beach, carrier, 1, 0);
        var phaseA = a.PhaseAt(0.0);
        var phaseB = b.PhaseAt(0.0);
        const float shift = (float)CoastWindow.PitchMetres;
        var worstPhase = 0f;
        var worstAmp = 0f;
        var worstBreak = 0f;

        for (var z = 80f; z < 1000f; z += 40f)
        {
            var x = shift + 0.5f * CoastWindow.Blend * Cell;
            a.Sample(x, z, out var sa);
            b.Sample(x - shift, z, out var sb);

            if (sa.Depth < 0.5f || sa.Depth > 12f)
                continue;

            var phase = MathF.Abs(SurfCarrier.Wrap(sa.Phase + phaseA - sb.Phase - phaseB));
            worstPhase = MathF.Max(worstPhase, phase);
            worstAmp = MathF.Max(worstAmp, MathF.Abs(sa.Amplitude - sb.Amplitude));
            worstBreak = MathF.Max(worstBreak, MathF.Abs(sa.BreakDistance - sb.BreakDistance));
            output.WriteLine($"z {z} depth {sa.Depth:F1}: phase apart {phase:F3} rad, amp {sa.Amplitude:F3} / {sb.Amplitude:F3}, clock {sa.BreakDistance:F1} / {sb.BreakDistance:F1} m");
        }

        Assert.True(worstPhase < 0.2f, $"phases {worstPhase:F2} rad apart");
        Assert.True(worstAmp < 0.06f, $"amplitudes {worstAmp:F2} apart");
        Assert.True(worstBreak < 2f, $"break clocks {worstBreak:F1} m apart");
    }

    /// <summary>The same over a seabed that is not a plane (a bar off a curved shore), across both kinds of shared edge.</summary>
    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    public void NeighbourWindows_AgreeOverABarredBay(int stepX, int stepZ)
    {
        static float Seabed(double u, double v) => BreakerFaceTests.BarredBay(0.8 * u + 0.6 * v - 230.0, 0.8 * v - 0.6 * u);
        var carrier = CoastCarrier.From(0.95f, 0.5f, 1.2f);
        var a = Bake(Seabed, carrier, 0, 0);
        var b = Bake(Seabed, carrier, stepX, stepZ);
        var phaseA = a.PhaseAt(0.0);
        var phaseB = b.PhaseAt(0.0);
        const float shift = (float)CoastWindow.PitchMetres;
        const float middle = shift + 0.5f * CoastWindow.Blend * Cell;
        var worstPhase = 0f;
        var worstAmp = 0f;
        var worstBreak = 0f;
        var surf = 0;

        for (var along = 40f; along < 1000f; along += 6f)
        {
            var (x, z) = stepX == 1 ? (middle, along) : (along, middle);
            a.Sample(x, z, out var sa);
            b.Sample(x - shift * stepX, z - shift * stepZ, out var sb);
            Assert.Equal(sa.Depth, sb.Depth, 0.02f);

            if (sa.Depth < 0.5f || sa.Depth > 8f)
                continue;

            surf += sa.BreakDistance > 1f ? 1 : 0;
            worstPhase = MathF.Max(worstPhase, MathF.Abs(SurfCarrier.Wrap(sa.Phase + phaseA - sb.Phase - phaseB)));
            worstAmp = MathF.Max(worstAmp, MathF.Abs(sa.Amplitude - sb.Amplitude));
            worstBreak = MathF.Max(worstBreak, MathF.Abs(sa.BreakDistance - sb.BreakDistance));
        }

        output.WriteLine($"shared edge ({stepX}, {stepZ}): phases {worstPhase:F3} rad apart, amplitudes {worstAmp:F3}, break clocks {worstBreak:F1} m; {surf} samples in the surf");
        Assert.True(surf > 0, "the shared cells miss the surf zone");
        Assert.True(worstPhase < 0.35f, $"phases {worstPhase:F2} rad apart");
        Assert.True(worstAmp < 0.2f, $"amplitudes {worstAmp:F2} apart");
        Assert.True(worstBreak < 4f, $"break clocks {worstBreak:F1} m apart");
    }

    /// <summary>Water with no way to the open sea carries no wave: a pond behind the beach has none, and a window whose only shallows are a pond is not baked.</summary>
    [Fact]
    public void LandlockedWater_CarriesNoWave()
    {
        static float Pond(double u, double v) => Math.Sqrt((u - 920.0) * (u - 920.0) + (v - 512.0) * (v - 512.0)) < 50.0 ? 2f : BeachDepth(u);
        var carrier = CoastCarrier.From(0f, 0.6f, 1.2f);
        var atlas = Bake(Pond, carrier);
        atlas.Sample(920f, 512f, out var pond);
        atlas.Sample(760f, 512f, out var surf);
        var phase0 = atlas.PhaseAt(3.0);

        Assert.Equal(2f, pond.Depth, 0.05f);
        Assert.Equal(0f, pond.Amplitude);
        Assert.Equal(0f, pond.BreakDistance);
        Assert.Equal(0f, SurfCarrier.At(atlas, 920f, 512f, phase0).Unbroken);
        Assert.True(surf.Amplitude > 1.2f && surf.BreakDistance > 1f, "the beach in front still breaks");

        for (var x = 880f; x <= 960f; x += 4f)
        {
            BreakerFace.Apply(atlas, x, 512f, phase0, out var face);
            Assert.Equal(0f, face.Height);
            Assert.Equal(0f, face.Foam);
        }

        var key = new CoastRegionKey(Tile, 0, 0, carrier);
        var bake = new CoastBake();
        bake.Prepare(new PlaneSeabed(Tiles, Tile, (u, v) => Math.Sqrt((u - 500.0) * (u - 500.0) + (v - 512.0) * (v - 512.0)) < 120.0 ? 2f : -6f), new CoastWindow(Tiles, key), key, new CoastAtlas(), CoastBake.BreakDepth(1.5f));
        bake.Run();
        Assert.True(bake.Solved);
        Assert.False(bake.Baked, "a basin inland is no coast");
        Assert.Equal(float.PositiveInfinity, bake.SeaMin);
    }

    /// <summary>No wave comes through land: a harbour open only to leeward is joined to the sea and nearly still.</summary>
    [Fact]
    public void WaterBehindLand_IsSheltered()
    {
        static float Harbour(double u, double v)
        {
            var wall = u is >= 380.0 and <= 420.0 && v is >= 300.0 and <= 720.0;
            var arms = u is >= 380.0 and <= 640.0 && (v is >= 300.0 and <= 340.0 || v is >= 680.0 and <= 720.0);
            return wall || arms ? -5f : 12f;
        }

        var atlas = Bake(Harbour, CoastCarrier.From(0f, 0.6f, 1.2f));
        atlas.Sample(200f, 512f, out var open);
        atlas.Sample(460f, 512f, out var inside);
        atlas.Sample(560f, 512f, out var mouth);
        atlas.Sample(520f, 150f, out var beside);
        output.WriteLine($"amplitude: open {open.Amplitude:F2}, beside the harbour {beside.Amplitude:F2}, inside at its back {inside.Amplitude:F2}, near its mouth {mouth.Amplitude:F2}");

        Assert.InRange(open.Amplitude, 0.9f, 1.2f);
        Assert.InRange(beside.Amplitude, 0.8f, 1.3f);
        Assert.True(inside.Amplitude < 0.25f * open.Amplitude, $"the harbour's back has {inside.Amplitude:F2} of the open sea's {open.Amplitude:F2}");
        Assert.True(mouth.Amplitude < 0.5f * open.Amplitude, $"the harbour's mouth has {mouth.Amplitude:F2} of the open sea's {open.Amplitude:F2}");
    }

    /// <summary>Depths are sampled every 16 m and again at 8 m and 4 m only where the seabed bends: a plane beach takes the base samples alone, and a cliff's foot and top are found to the cell.</summary>
    [Fact]
    public void Depths_AreSampledFinerWhereTheSeabedBends()
    {
        static float Cliff(double u) => (float)Math.Clamp(2.0 * (703.0 - u), -6.0, 6.0);
        var carrier = CoastCarrier.From(0f, 0.6f, 1.2f);
        var plane = new CoastBake();
        Bake((u, _) => BeachDepth(u), carrier, bake: plane);
        const int nodes = (CoastWindow.Cells + 2 * CoastBake.Halo) / CoastBake.NodeStep + 1;
        const int coarse = CoastBake.CoarseCells / CoastBake.CoarseDepthStep;
        Assert.Equal(nodes * nodes + coarse * coarse, plane.DepthSamples);

        var steep = new CoastBake();
        var atlas = Bake((u, _) => Cliff(u), carrier, bake: steep);
        output.WriteLine($"depth samples: plane beach {plane.DepthSamples}, cliff {steep.DepthSamples}");
        Assert.True(steep.DepthSamples > plane.DepthSamples);
        Assert.True(steep.DepthSamples < 2 * plane.DepthSamples, "only the cliff is sampled finer");

        for (var x = 682f; x <= 722f; x += 4f)
        {
            atlas.Sample(x, 510f, out var s);
            var expected = 0.25f * (Cliff(x - 2.0) * 2f + Cliff(x + 2.0) * 2f);
            Assert.Equal(expected, s.Depth, 0.26f);
        }
    }

    /// <summary>The carrier is drawn only near where it breaks, so a window with no water that shallow has none to start at its edge.</summary>
    [Fact]
    public void Carrier_FormsOnlyNearItsBreak()
    {
        const float height = 1.2f;
        Assert.Equal(0f, SurfCarrier.Unbroken(height, 0.6f, SurfCarrier.FadeDepth * height, 1f));
        Assert.Equal(0f, SurfCarrier.Unbroken(height, 0.6f, 40f, 1f));
        Assert.Equal(height, SurfCarrier.Unbroken(height, 0.6f, 0.5f * SurfCarrier.FormDepth * height, 1f), 0.01f);
        var before = 0f;

        for (var depth = SurfCarrier.FadeDepth * height; depth >= SurfCarrier.FormDepth * height; depth -= 0.05f)
        {
            var unbroken = SurfCarrier.Unbroken(height, 0.6f, depth, 1f);
            Assert.True(unbroken >= before - 1e-4f && unbroken - before < 0.08f, $"the carrier steps at {depth:F2} m");
            before = unbroken;
        }
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void BakeCost()
    {
        static float Bay(double u, double v) => (float)(0.03 * (900.0 - u) + 6.0 * Math.Sin(v / 180.0));
        var bake = new CoastBake();
        var carrier = CoastCarrier.From(0.4f, 0.5f, 1.4f);
        Bake(Bay, carrier, bake: bake);
        var best = double.MaxValue;

        for (var i = 0; i < 10; i++)
        {
            var start = Stopwatch.GetTimestamp();
            Bake(Bay, carrier, bake: bake);
            best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        output.WriteLine($"bake {best:F1} ms a region (analytic seabed: depth sampling {bake.DepthMs:F1} ms of it), scratch {CoastBake.ScratchBytes / 1048576.0:F1} MB, atlas {CoastAtlas.Bytes / 1048576.0:F2} MB");
    }
}
