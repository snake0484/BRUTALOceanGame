using System.Diagnostics;
using System.Numerics;
using BrutalGame.Water.Ocean;
using Xunit.Abstractions;

namespace BrutalGame.Water.Tests;

public sealed class CoastalFieldTests(ITestOutputHelper output)
{
    private const int N = CoastalField.Resolution;
    private const float Cell = CoastalField.CellSize;
    private const double Gravity = 9.81;

    /// <summary>Plane beach: depth falls linearly along +x from 40 m to the shoreline near x = 1000 m.</summary>
    private static float BeachDepth(float x) => 40f - 0.04f * x;

    private static CoastalField Build(Func<float, float, float> depth, float direction)
    {
        var depths = new float[N * N];
        for (var z = 0; z < N; z++)
            for (var x = 0; x < N; x++)
                depths[x + z * N] = depth((x + 0.5f) * Cell, (z + 0.5f) * Cell);

        using var builder = new CoastalBuilder();
        Assert.True(builder.TryStart(depths, Vector2.Zero, direction));

        var timer = Stopwatch.StartNew();
        while (!builder.Poll())
        {
            Assert.True(timer.Elapsed.TotalSeconds < 30, "coastal build timed out");
            Thread.Sleep(1);
        }

        return builder.Current!;
    }

    [Fact]
    public void Refraction_FollowsSnellAndDispersion()
    {
        const float theta0 = MathF.PI / 6f;
        const int reference = 4;
        var field = Build((x, _) => BeachDepth(x), theta0);
        var omega = CoastalField.RefOmega(reference);
        var k0 = omega * omega / Gravity;
        var sample = new CoastalSample();
        var worst = 0.0;

        for (var x = 200f; x <= 800f; x += 100f)
        {
            Assert.True(field.Sample(x, 512f, ref sample));

            var kx = k0 * Math.Cos(theta0) + sample.LagX[reference];
            var kz = k0 * Math.Sin(theta0) + sample.LagZ[reference];
            var k = Math.Sqrt(kx * kx + kz * kz);
            var expected = Jonswap.WaveNumber(omega, BeachDepth(x));
            var dispersionError = Math.Abs(k / expected - 1.0);
            var snellError = Math.Abs(kz / (k0 * Math.Sin(theta0)) - 1.0);
            worst = Math.Max(worst, Math.Max(dispersionError, snellError));

            output.WriteLine($"x {x} h {BeachDepth(x):F1}: |k| {k:F4} vs {expected:F4}, k_along {kz:F4} vs {k0 * Math.Sin(theta0):F4}, angle {Math.Atan2(kz, kx) * 180 / Math.PI:F1} deg");
        }

        Assert.True(worst < 0.03, $"worst relative error {worst:P1}");
    }

    [Fact]
    public void NormalIncidence_ShoalsLikeGreensLaw()
    {
        const int reference = 3;
        var field = Build((x, _) => BeachDepth(x), 0f);
        var omega = CoastalField.RefOmega(reference);
        var cg0 = 0.5 * Gravity / omega;
        var sample = new CoastalSample();
        var worst = 0.0;

        for (var x = 300f; x <= 900f; x += 100f)
        {
            Assert.True(field.Sample(x, 512f, ref sample));

            double h = BeachDepth(x);
            var k = Jonswap.WaveNumber(omega, h);
            var kh = k * h;
            var cg = 0.5 * (1.0 + 2.0 * kh / Math.Sinh(2.0 * kh)) * omega / k;
            var ks = Math.Sqrt(cg0 / cg);
            var error = Math.Abs(sample.Amp[reference] / ks - 1.0);
            worst = Math.Max(worst, error);

            output.WriteLine($"x {x} h {h:F1}: amp {sample.Amp[reference]:F3} vs Ks {ks:F3}");
        }

        Assert.True(worst < 0.05, $"worst relative error {worst:P1}");
    }

    [Fact]
    public void DeepField_LeavesSwellUnchanged()
    {
        var field = Build((_, _) => 5000f, 0f);
        var deep = new OceanSwell(7);
        var coastal = new OceanSwell(7);
        deep.SetWind(new SwellParams());
        coastal.SetWind(new SwellParams());
        deep.SetTime(42.0);
        coastal.SetTime(42.0);
        coastal.Coastal = field;

        for (var x = 100f; x < 900f; x += 97f)
        {
            Assert.Equal(deep.SampleHeight(x, 431f), coastal.SampleHeight(x, 431f));
        }
    }

    [Fact]
    public void Shallows_CapWaveHeightByDepth()
    {
        // Shoreline at x = 800 m, clear of the map's edge fade.
        var field = Build((x, _) => 40f - 0.05f * x, 0f);
        var swell = new OceanSwell(3);
        swell.SetWind(new SwellParams { WindSpeed = 20f });
        swell.Coastal = field;

        const float x = 770f;
        var depth = 40f - 0.05f * x;
        var max = 0f;

        for (var t = 0; t < 600; t++)
        {
            swell.SetTime(t * 0.1);
            max = MathF.Max(max, MathF.Abs(swell.SampleHeight(x, 512f)));
        }

        output.WriteLine($"depth {depth:F2} m, max |eta| {max:F2} m, deep Hs {swell.SignificantWaveHeight:F2} m");
        Assert.True(max < depth, $"crest {max:F2} m in {depth:F2} m of water");
    }
}
