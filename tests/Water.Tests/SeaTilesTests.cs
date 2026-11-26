using System.Numerics;
using Brutal.Numerics;
using BrutalGame.Water.Ocean;
using Xunit.Abstractions;

namespace BrutalGame.Water.Tests;

/// <summary>
/// The sea's global tiling (review C5): weights that keep the sea's energy and never jump, across row edges, cell edges,
/// the seam of longitude and the polar caps, and the GPU's view of it (SeaSlots, ocean_common.glsl swellAt) agreeing
/// with the CPU's.
/// </summary>
public sealed unsafe class SeaTilesTests(ITestOutputHelper output)
{
    private const double SeaRadius = 1_000_150.0;
    private const double TickSeconds = 1.0 / 60.0;
    private const long Tick = 1800;
    private const double Metre = 1.0 / SeaRadius;

    private static readonly double3 Spin = double3.Normalize(new double3(0.2, 1.0, -0.1));
    private static readonly SeaTiles Tiles = new(SeaRadius, Spin);

    /// <summary>The direction <paramref name="north"/> m north of the edge under <paramref name="row"/> and <paramref name="east"/> m east of that row's cell edge <paramref name="cell"/>.</summary>
    internal static double3 Corner(SeaTiles tiles, int row, int cell, double north = 0.0, double east = 0.0)
    {
        var latitude = tiles.RowEdge(row) + north / tiles.SeaRadius;
        var longitude = cell * Math.Tau / tiles.Cells(row) + east / (tiles.SeaRadius * Math.Cos(latitude));
        return tiles.Direction(latitude, longitude);
    }

    private static Dictionary<int, float> Weights(double3 dir)
    {
        Tiles.Locate(dir, out var blend);
        var weights = new Dictionary<int, float>();

        for (var k = 0; k < blend.Count; k++)
            weights.Add(blend.Tile[k], blend.Weight[k]);

        return weights;
    }

    [Fact]
    public void Tiles_AreAbout64Km()
    {
        var height = Math.PI * SeaRadius / Tiles.Rows;
        var widest = 0.0;
        var narrowest = double.MaxValue;

        for (var row = 1; row < Tiles.Rows - 1; row++)
        {
            var width = Math.Tau * SeaRadius * Math.Cos((row + 0.5) * Math.PI / Tiles.Rows - 0.5 * Math.PI) / Tiles.Cells(row);
            widest = Math.Max(widest, width);
            narrowest = Math.Min(narrowest, width);
        }

        output.WriteLine($"{Tiles.Rows} rows of {height / 1000.0:F1} km, {Tiles.Count} tiles, cells {narrowest / 1000.0:F1}..{widest / 1000.0:F1} km wide at their row's middle");
        Assert.InRange(height, 60_000.0, 68_000.0);
        Assert.InRange(narrowest, 50_000.0, 64_000.0);
        Assert.InRange(widest, 64_000.0, 80_000.0);
        Assert.Equal(1, Tiles.Cells(0));
        Assert.Equal(1, Tiles.Cells(Tiles.Rows - 1));
    }

    [Fact]
    public void Weights_KeepTheEnergyEverywhere()
    {
        var random = new Random(11);
        var counts = new int[SeaTiles.MaxBlend + 1];

        for (var i = 0; i < 200_000; i++)
        {
            var d = double3.Normalize(new double3(random.NextDouble() - 0.5, random.NextDouble() - 0.5, random.NextDouble() - 0.5));
            Tiles.Locate(d, out var blend);
            var sum = 0.0;

            for (var k = 0; k < blend.Count; k++)
            {
                Assert.InRange(blend.Tile[k], 0, Tiles.Count - 1);
                sum += blend.Weight[k] * (double)blend.Weight[k];
            }

            Assert.Equal(1.0, sum, 1e-5);
            Assert.True(blend.Weight[0] * blend.Weight[0] >= 0.2499f, "the tile the direction is in comes first");
            counts[blend.Count]++;
        }

        output.WriteLine($"directions by tiles blended: 1: {counts[1]}, 2: {counts[2]}, 3: {counts[3]}, 4: {counts[4]} of 200000");
        Assert.True(counts[1] > 150_000, "most of the sea is one tile");
        Assert.True(counts[2] > 0 && counts[3] > 0 && counts[4] > 0);
    }

    /// <summary>
    /// Every tile's weight is continuous along paths that cross a row edge, a cell edge, a corner where rows of different
    /// cell counts meet, run along a row edge over both rows' cell edges, cross the seam of longitude, and enter a polar cap.
    /// </summary>
    [Theory]
    [InlineData(20, 5, -3000.0, 20000.0, 3000.0, 21000.0, "row edge, across")]
    [InlineData(20, 5, 20000.0, -3000.0, 21000.0, 3000.0, "cell edge, across")]
    [InlineData(20, 5, -3000.0, -2500.0, 3000.0, 2500.0, "through a corner")]
    [InlineData(20, 5, 300.0, -150000.0, -300.0, 150000.0, "along a row edge")]
    [InlineData(30, 0, -2500.0, -3000.0, 2500.0, 3000.0, "seam of longitude at a row edge")]
    [InlineData(1, 0, -5000.0, 400.0, 5000.0, 900.0, "into the south cap")]
    [InlineData(1, 3, 500.0, -9000.0, 900.0, 9000.0, "along the cap's edge")]
    public void Weights_AreContinuous(int row, int cell, double north0, double east0, double north1, double east1, string what)
    {
        var from = Corner(Tiles, row, cell, north0, east0);
        var to = Corner(Tiles, row, cell, north1, east1);
        var length = Math.Acos(Math.Clamp(double3.Dot(from, to), -1.0, 1.0)) * SeaRadius;
        var steps = (int)(length / 0.5);
        var previous = Weights(from);
        var seen = new HashSet<int>(previous.Keys);
        var worst = 0f;
        var most = 1;

        for (var i = 1; i <= steps; i++)
        {
            var d = double3.Normalize(from + (to - from) * (i / (double)steps));
            var weights = Weights(d);
            most = Math.Max(most, weights.Count);
            seen.UnionWith(weights.Keys);

            foreach (var tile in previous.Keys.Union(weights.Keys))
                worst = MathF.Max(worst, MathF.Abs(previous.GetValueOrDefault(tile) - weights.GetValueOrDefault(tile)));

            previous = weights;
        }

        output.WriteLine($"{what}: {length:F0} m in {steps} steps, largest weight step {worst:F5}, {seen.Count} tiles, up to {most} at once");
        Assert.True(worst < 1.5e-3f, $"{what}: a weight jumps by {worst}");
        Assert.True(seen.Count >= 2);
    }

    /// <summary>The sea's height is continuous round tile corners (the weights are, and each tile's field is).</summary>
    [Fact]
    public void Height_IsContinuousAcrossTiles()
    {
        var swell = new OceanSwell(9);
        swell.SetWind(new SwellParams { WindSpeed = 12f, SwellHeight = 2f, SwellPeriod = 12f });
        swell.SetTime(Tick * TickSeconds);
        var surface = new OceanSurface(swell, Tiles, TickSeconds);
        var worst = 0f;

        foreach (var (row, cell) in new[] { (20, 5), (30, 0), (1, 2), (Tiles.Rows - 1, 0), (12, 17) })
        {
            var centre = Corner(Tiles, row, cell);
            SeaTiles.EastSouth(centre, Spin, out var east, out var south);
            var previous = float.NaN;

            for (var i = 0; i <= 20_000; i++)
            {
                var (sin, cos) = Math.SinCos(i * Math.Tau / 20_000);
                var h = surface.SampleHeight(centre * SeaRadius + (east * cos + south * sin) * 700.0, Tick);

                if (i > 0)
                    worst = MathF.Max(worst, MathF.Abs(h - previous));

                previous = h;
            }
        }

        // 0.22 m steps along the path: the swell's slope (under 0.5) bounds the step where nothing jumps.
        output.WriteLine($"largest height step over 0.22 m of path round tile corners: {worst:F4} m");
        Assert.True(worst < 0.12f, $"height jumps by {worst} m");
    }

    /// <summary>
    /// The GPU's low band (ocean_common.glsl swellAt, ported here) from SeaSlots, the wave list and the per-slot phases
    /// is the CPU's surface: inside a tile, on a row edge, on a cell edge, at a corner, at the seam and by a polar cap,
    /// with the camera off the sea frame's origin.
    /// </summary>
    [Theory]
    [InlineData(20, 5, 30000.0, 30000.0, "inside a tile")]
    [InlineData(20, 5, 300.0, 30000.0, "on a row edge")]
    [InlineData(20, 5, 30000.0, -400.0, "on a cell edge")]
    [InlineData(20, 5, 250.0, 350.0, "at a corner")]
    [InlineData(20, 5, -900.0, 1200.0, "by a corner, the other row")]
    [InlineData(30, 0, 500.0, -300.0, "at the seam of longitude")]
    [InlineData(1, 2, -600.0, 500.0, "in the south cap by its edge")]
    public void Gpu_MirrorsTheCpu(int row, int cell, double north, double east, string what)
    {
        var swell = new OceanSwell(9);
        swell.SetWind(new SwellParams { WindSpeed = 12f, SwellHeight = 2f, SwellPeriod = 12f });
        swell.SetTime(Tick * TickSeconds);
        var surface = new OceanSurface(swell, Tiles, TickSeconds);

        // The sea frame's origin 2.1 km from the camera, as when the camera has strayed from it.
        var here = Corner(Tiles, row, cell, north, east);
        var frame = SeaFrame.At(Offset(here, -1500.0, 1470.0), Spin, SeaRadius);
        var camera = frame.ToLocal(here * SeaRadius);
        SeaSlots.Build(Tiles, frame, camera.X, camera.Z, 8700.0, out var slots);

        var waves = new GpuWave[OceanSwell.MaxComponents];
        var phases = new float[OceanSwell.MaxComponents * SeaTiles.MaxBlend];
        var count = swell.CopyGpuWaves(waves, swell.Time, slots.U[0], slots.V[0]);

        for (var s = 0; s < slots.Count; s++)
            Assert.Equal(count, swell.CopyGpuPhases(phases, s, SeaTiles.MaxBlend, swell.Time, slots.U[s], slots.V[s]));

        var worstHeight = 0.0;
        var worstPlace = 0.0;
        var most = 0;

        for (var i = 0; i < 400; i++)
        {
            // Rest positions within 300 m of the camera, where the short waves are drawn.
            var x0 = new Vector2(i % 20 * 30f - 285f, i / 20 * 30f - 290f);
            GpuSwell(slots, waves, phases, count, x0, out var height, out var shift, out var used);
            most = Math.Max(most, used);

            var rest = frame.SeaPoint(camera.X + x0.X, camera.Z + x0.Y);
            var cpu = surface.SurfacePoint(double3.Normalize(rest), Tick, out _);
            var gpu = frame.SeaPoint(camera.X + x0.X + shift.X, camera.Z + x0.Y + shift.Y);
            worstHeight = Math.Max(worstHeight, Math.Abs(cpu.Length() - SeaRadius - height));
            worstPlace = Math.Max(worstPlace, (cpu * (SeaRadius / cpu.Length()) - gpu * (SeaRadius / gpu.Length())).Length());
        }

        output.WriteLine($"{what}: {slots.Count} slots (up to {most} weighted at a point), height differs by at most {worstHeight * 1000.0:F2} mm, the chop's place by {worstPlace * 1000.0:F2} mm");
        Assert.True(worstHeight < 0.01, $"{what}: GPU height off by {worstHeight} m");
        Assert.True(worstPlace < 0.01, $"{what}: GPU displacement off by {worstPlace} m");
    }

    private static double3 Offset(double3 @base, double x, double z)
    {
        SeaTiles.EastSouth(@base, Spin, out var east, out var south);
        return double3.Normalize(@base * SeaRadius + east * x + south * z) * SeaRadius;
    }

    private static float Smooth(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>ocean_common.glsl slotWeights + swellAt (no anti-alias filter, cap 1): height and displacement at x0 in the sea frame's plane.</summary>
    private static void GpuSwell(in SeaSlots slots, GpuWave[] waves, float[] phases, int count, Vector2 x0, out float height, out Vector2 shift, out int used)
    {
        var row = Smooth(0.5f + 0.5f * (slots.BandRow.X * x0.X + slots.BandRow.Y * x0.Y + slots.BandRow.Z));
        Span<float> cell =
        [
            Smooth(0.5f + 0.5f * (slots.BandSouth.X * x0.X + slots.BandSouth.Y * x0.Y + slots.BandSouth.Z)),
            Smooth(0.5f + 0.5f * (slots.BandNorth.X * x0.X + slots.BandNorth.Y * x0.Y + slots.BandNorth.Z)),
        ];

        height = 0f;
        shift = default;
        used = 0;

        for (var s = 0; s < slots.Count; s++)
        {
            var sideRow = slots.Side[4 * s];
            var sideCell = slots.Side[4 * s + 1];
            var c = cell[(int)slots.Side[4 * s + 2]];
            var wr = sideRow == 0f ? 1f : sideRow > 0f ? row : 1f - row;
            var wc = sideCell == 0f ? 1f : sideCell > 0f ? c : 1f - c;
            var ws = MathF.Sqrt(wr * wc);

            if (ws <= 0f)
                continue;

            used++;
            var u = slots.ToTile[4 * s] * x0.X + slots.ToTile[4 * s + 1] * x0.Y;
            var v = slots.ToTile[4 * s + 2] * x0.X + slots.ToTile[4 * s + 3] * x0.Y;
            var h = 0f;
            var d = Vector2.Zero;

            for (var i = 0; i < count; i++)
            {
                var theta = waves[i].Kx * u + waves[i].Kz * v + phases[i * SeaTiles.MaxBlend + s];
                var (sin, cos) = MathF.SinCos(theta);
                h += waves[i].Amplitude * cos;
                d -= new Vector2(waves[i].Cx, waves[i].Cz) * sin;
            }

            height += ws * h;
            shift += ws * new Vector2(
                slots.FromTile[4 * s] * d.X + slots.FromTile[4 * s + 1] * d.Y,
                slots.FromTile[4 * s + 2] * d.X + slots.FromTile[4 * s + 3] * d.Y);
        }
    }
}
