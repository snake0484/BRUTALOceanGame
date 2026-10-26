using System.Diagnostics;
using System.Numerics;
using BrutalGame.Water.Ocean;
using Xunit.Abstractions;

namespace BrutalGame.Water.Tests;

/// <summary>
/// Diagnostic (docs/breaker-review.md §5): every crest on the bar beach's centre line, its surf state and lip every
/// 0.4 s (cap, break clock, curl, broken, height, lift, reach, crash, throw, drop), so the lip's life can be read against
/// the face it sits on. Run with --logger "console;verbosity=detailed".
/// </summary>
public sealed class LipLifeProbe(ITestOutputHelper output)
{
    private const int N = CoastalField.Resolution;
    private const float Cell = CoastalField.CellSize;
    private const float ProbeZ = 512f;
    private static readonly Vector2 Shoreward = new(1f, 0f);

    private static float BarDepth(float x, float z)
    {
        var shore = 820f + 40f * MathF.Cos(MathF.Tau * z / (N * Cell));
        var depth = 0.02f * (shore - x);
        var along = BreakerFaces.Smooth((90f - MathF.Abs(z - 512f)) / 20f);
        var bar = (x - 640f - 0.35f * (z - 512f)) / 12f;
        var trough = (x - 690f - 0.35f * (z - 512f)) / 15f;
        return depth - MathF.Max(depth - 1.6f, 0f) * MathF.Exp(-bar * bar) * along + 5f * MathF.Exp(-trough * trough) * along;
    }

    private static OceanSwell Build(float windSpeed, float swellHeight, float swellPeriod)
    {
        var depths = new float[N * N];
        for (var z = 0; z < N; z++)
            for (var x = 0; x < N; x++)
                depths[x + z * N] = BarDepth((x + 0.5f) * Cell, (z + 0.5f) * Cell);

        using var builder = new CoastalBuilder();
        builder.TryStart(depths, Vector2.Zero, 0f);
        var timer = Stopwatch.StartNew();
        while (!builder.Poll())
        {
            Assert.True(timer.Elapsed.TotalSeconds < 30, "coastal build timed out");
            Thread.Sleep(1);
        }

        var swell = new OceanSwell(5);
        swell.SetWind(new SwellParams { WindSpeed = windSpeed, WindDirection = 0f, SwellHeight = swellHeight, SwellPeriod = swellPeriod, SwellDirection = 0f });
        swell.Coastal = builder.Current;
        return swell;
    }

    [Theory]
    [InlineData(20f, 1.2f, 13f, 0)]
    [InlineData(14f, 1.2f, 13f, 0)]
    [InlineData(8f, 3f, 12f, 0)]
    [InlineData(8f, 3f, 12f, 1)]
    [InlineData(16f, 0f, 12f, 2)]
    public void CrestsOverTheBar(float windSpeed, float swellHeight, float swellPeriod, int kind)
    {
        var swell = kind == 0 ? Build(windSpeed, swellHeight, swellPeriod) : kind == 1 ? BreakerSetTests.PlungingBeachForProbe(0f) : BreakerSetTests.BeachForProbe(0f);
        var set = new BreakerSet();
        var surface = new OceanSurface(swell, set);
        output.WriteLine($"wind {windSpeed} swell {swellHeight} m {swellPeriod} s; bar crest 1.6 m at x 640, trough 5 m at x 690, shore x 860 (z {ProbeZ})");
        output.WriteLine("bed at x 560..700 step 20: " + string.Join(" ", Enumerable.Range(0, 8).Select(i => $"{BarDepth(560f + 20f * i, ProbeZ):F1}")));
        output.WriteLine("    t      x  depth  unbrk   cap  clock  prog  curl  brokn     H   amp  plng  reach  crash  throw  drop   lip");

        var lastLog = -1.0;
        var rows = 0;
        for (var t = 120; t < 60 * (kind == 0 ? 40 : 80); t++)
        {
            var time = 100.0 + t / 60.0;
            swell.SetTime(time);
            set.Update(swell, 1f / 60f);
            if (time - lastLog < 0.4 - 1e-6)
                continue;

            lastLog = time;
            for (var i = 0; i < set.Count; i++)
            {
                var b = set[i];
                var best = -1;
                var bestDz = float.MaxValue;
                for (var v = 0; v < b.Count; v++)
                {
                    var dz = MathF.Abs(b.Vertices[v].Position.Y - ProbeZ);
                    if (dz < bestDz)
                    {
                        bestDz = dz;
                        best = v;
                    }
                }

                if (best < 0 || bestDz > 6f)
                    continue;

                var p = b.Vertices[best];
                var x = p.Position.X;
                if (x < 540f || x > 798f)
                    continue;

                swell.SurfCarrier(x, ProbeZ, out var c);
                var k = MathF.Max(MathF.Sqrt(c.Kx * c.Kx + c.Kz * c.Kz), 1e-6f);
                var wavelength = MathF.Tau / k;
                var breakDistance = set.BreakDistance(x, ProbeZ);
                var cap = c.Unbroken > 1e-4f ? Math.Clamp(OceanSwell.WaveBreakingGamma * MathF.Max(c.Depth, 0f) / c.Unbroken, 0f, 1f) : 1f;
                var plunge = BreakerFace.PlungeAt(swell, new Vector2(x, ProbeZ), new Vector2(c.Kx, c.Kz) / k, c.Depth, breakDistance, c.Unbroken);
                var state = BreakerFace.CrestStateAt(swell, set, x, ProbeZ);
                var (reach, crash) = BreakerLip.Life(breakDistance, wavelength, plunge);
                var hasLip = BreakerLip.Sample(surface, set, x, ProbeZ, Shoreward, out var lip);
                var throwDist = hasLip ? (lip.Landing.X - lip.Crest.X) * Shoreward.X + (lip.Landing.Z - lip.Crest.Z) * Shoreward.Y : 0f;
                var drop = hasLip ? lip.Crest.Y - lip.Landing.Y : 0f;
                output.WriteLine($"{time - 100.0,5:F1} {x,6:F0} {c.Depth,6:F2} {c.Unbroken,6:F2} {cap,5:F2} {breakDistance,6:F1} {breakDistance / (0.35f * wavelength),5:F2} {state.Curl,5:F2} {state.Broken,6:F2} {state.Height,5:F2} {state.Amplitude,5:F2} {plunge,5:F2} {reach,6:F2} {crash,6:F2} {throwDist,6:F2} {drop,5:F2}   {(hasLip ? "yes" : "no ")}  L={wavelength:F0} crestY={(hasLip ? lip.Crest.Y : 0f):F2} landY={(hasLip ? lip.Landing.Y : 0f):F2} seaY={surface.SampleHeight(x, ProbeZ):F2} id={b.Crest} n={b.Count} stage={p.Stage}");
                rows++;
            }
        }

        Assert.True(rows > 0, "no crest on the probe line");
    }
}
