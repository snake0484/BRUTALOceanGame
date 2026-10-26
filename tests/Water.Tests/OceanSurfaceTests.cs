using System.Diagnostics;
using System.Numerics;
using BrutalGame.Water.Ocean;
using Xunit.Abstractions;

namespace BrutalGame.Water.Tests;

/// <summary>
/// The CPU surface with the breaker faces on it (OceanSurface, BreakerFace mirroring the shader's breakerFace): probed
/// across crests that curl, where the pitch squeezes the face into a wall, and tick to tick.
/// </summary>
public sealed class OceanSurfaceTests(ITestOutputHelper output)
{
    private const int Ticks = 480;
    private const float ShorelineDepth = 0.6f;

    /// <summary>
    /// The plunging beach (groundswell on 1:12, ξ ≈ 0.8–1) run until a crest curls along its length (the plane beach
    /// breaks in pulses), with the crest segments built on the last tick to find where. The 1:20 wind-sea beach the
    /// breaker tests use spills (ξ ≈ 0.3): a steep foaming front and no lip, which the curl tests can't probe.
    /// </summary>
    private static OceanSurface Surf(out BreakerFaces faces)
    {
        var swell = BreakerSetTests.PlungingBeachForProbe(0f);
        var set = new BreakerSet();
        faces = new BreakerFaces();

        for (var t = 0; t < Ticks; t++)
        {
            swell.SetTime(100.0 + t / 60.0);
            set.Update(swell, 1f / 60f);
            faces.Build(set, swell);
        }

        // The curl is a pocket that lasts a few seconds of each 12 s wave: run on until a crest is curling.
        for (var t = Ticks; t < 4 * Ticks && !AnyCurling(swell, faces); t++)
        {
            swell.SetTime(100.0 + t / 60.0);
            set.Update(swell, 1f / 60f);
            faces.Build(set, swell);
        }

        return new OceanSurface(swell, set);
    }

    private static bool AnyCurling(OceanSwell swell, BreakerFaces faces)
    {
        for (var i = 0; i < faces.SegmentCount; i++)
            if (Curling(swell, faces, faces.Segments[i]))
                return true;

        return false;
    }

    /// <summary>A segment whose crest curls at its middle (the carrier's state there, break distance included).</summary>
    private static bool Curling(OceanSwell swell, BreakerFaces faces, BreakerSegment s)
    {
        var middle = 0.5f * (new Vector2(s.Ends.X, s.Ends.Y) + new Vector2(s.Ends.Z, s.Ends.W));
        var state = BreakerFace.CrestStateAt(swell, faces.Breaking, middle.X, middle.Y);
        return state.Curl >= 0.5f && state.Height >= 0.5f;
    }

    /// <summary>Rest points across each curling segment's middle, from 0.4 L behind the crest to 0.4 L ahead.</summary>
    private static List<(Vector2 Point, Vector2 Normal)> AcrossCurls(OceanSwell swell, BreakerFaces faces)
    {
        var points = new List<(Vector2, Vector2)>();

        for (var i = 0; i < faces.SegmentCount; i++)
        {
            var s = faces.Segments[i];
            if (!Curling(swell, faces, s))
                continue;

            var middle = 0.5f * (new Vector2(s.Ends.X, s.Ends.Y) + new Vector2(s.Ends.Z, s.Ends.W));
            var normal = new Vector2(s.Direction.X, s.Direction.Y);
            for (var o = -0.4f; o <= 0.4f; o += 0.025f)
                points.Add((middle + normal * (o * s.Direction.W), normal));
        }

        return points;
    }

    [Fact]
    public void HeightInvertsSurfacePoint_AcrossPitchedWalls()
    {
        var surface = Surf(out var faces);
        var points = AcrossCurls(surface.Swell, faces);
        var maxErr = 0f;
        var walls = 0;
        var leastJ = float.MaxValue;

        foreach (var (rest, n) in points)
        {
            var r = surface.SampleRest(rest.X, rest.Y);
            var alongN = 1f + r.DxX * n.X * n.X + 2f * r.DxZ * n.X * n.Y + r.DzZ * n.Y * n.Y;
            leastJ = MathF.Min(leastJ, alongN);
            if (alongN < 0.5f)
                walls++;

            var p = surface.SurfacePoint(rest.X, rest.Y);
            maxErr = MathF.Max(maxErr, MathF.Abs(surface.SampleHeight(p.X, p.Z) - p.Y));
        }

        output.WriteLine($"{points.Count} points, {walls} on squeezed walls (least Jacobian across the crest {leastJ:F3}), max inversion error {maxErr:F5} m");
        Assert.True(walls >= 10, $"only {walls} points on pitched walls");
        Assert.True(leastJ > 0f, $"the face folds: Jacobian {leastJ}");
        Assert.True(maxErr < 0.02f, $"max inversion error {maxErr} m");
    }

    /// <summary>
    /// The face's analytic slope and Jacobian terms (the formulas the shader draws its normals and whitecap test with)
    /// against finite differences across the crest, where peak and pitch change fastest. The face goes on a plain sine
    /// rather than the swell, so only the face's own derivatives are under test (the coastal swell's amplitude also varies
    /// with depth, which its analytic slope leaves out). Within 0.35 L of the crest: past that the face fades out on the
    /// carrier's phase, whose gradient steps at the lag map's texel lines, so there its slope is only approximate. So is
    /// the last <see cref="ShorelineDepth"/> of water, where the carrier's wavenumber is clamped (OceanSwell.SpentDepthK)
    /// and the crest search steps between texels: a bore's steep front (0.65 since the tenth pass) shows it there.
    /// </summary>
    [Fact]
    public void FacePartials_MatchFiniteDifferences()
    {
        var swell = Surf(out var faces).Swell;
        var breaking = faces.Breaking;
        const float eps = 0.01f;
        var slopeErrors = new List<float>();
        var stretchErrors = new List<float>();

        for (var i = 0; i < faces.SegmentCount; i++)
        {
            var s = faces.Segments[i];
            if (!Curling(swell, faces, s))
                continue;

            var middle = 0.5f * (new Vector2(s.Ends.X, s.Ends.Y) + new Vector2(s.Ends.Z, s.Ends.W));
            var n = new Vector2(s.Direction.X, s.Direction.Y);
            var a = 0.25f * (s.Heights.X + s.Heights.Y);

            for (var o = -0.35f; o <= 0.35f; o += 0.025f)
            {
                var rest = middle + n * (o * s.Direction.W);
                if (swell.CarrierDepth(rest.X, rest.Y) < ShorelineDepth)
                    continue;

                var p = Faced(swell, breaking, rest, middle, n, s.Direction.W, a);
                var ahead = Faced(swell, breaking, rest + n * eps, middle, n, s.Direction.W, a);
                var behind = Faced(swell, breaking, rest - n * eps, middle, n, s.Direction.W, a);

                var slope = (ahead.Height - behind.Height) / (2f * eps);
                var stretch = ((ahead.Dx - behind.Dx) * n.X + (ahead.Dz - behind.Dz) * n.Y) / (2f * eps);
                var slopeA = p.SlopeX * n.X + p.SlopeZ * n.Y;
                var stretchA = p.DxX * n.X * n.X + 2f * p.DxZ * n.X * n.Y + p.DzZ * n.Y * n.Y;

                slopeErrors.Add(MathF.Abs(slope - slopeA) / MathF.Max(1f, MathF.Abs(slopeA)));
                stretchErrors.Add(MathF.Abs(stretch - stretchA));
            }
        }

        Assert.NotEmpty(slopeErrors);
        slopeErrors.Sort();
        stretchErrors.Sort();
        var p99 = (int)(0.99f * (slopeErrors.Count - 1));
        output.WriteLine($"{slopeErrors.Count} points: slope error p99 {slopeErrors[p99]:F4} max {slopeErrors[^1]:F4}; d(shift)/ds error p99 {stretchErrors[p99]:F4} max {stretchErrors[^1]:F4}");
        Assert.True(slopeErrors[p99] < 0.02f, $"slope error p99 {slopeErrors[p99]}");
        Assert.True(stretchErrors[p99] < 0.02f, $"d(shift)/ds error p99 {stretchErrors[p99]}");
    }

    /// <summary>The face applied to a sine of amplitude a whose crest runs through <paramref name="crest"/> along the segment.</summary>
    private static RestPoint Faced(OceanSwell swell, BreakerSet? breaking, Vector2 x, Vector2 crest, Vector2 n, float wavelength, float a)
    {
        var k = 2f * MathF.PI / wavelength;
        var theta = k * Vector2.Dot(x - crest, n);
        var slope = -a * k * MathF.Sin(theta);
        var p = new RestPoint { Height = a * MathF.Cos(theta), SlopeX = slope * n.X, SlopeZ = slope * n.Y };
        BreakerFace.Apply(swell, breaking, x.X, x.Y, ref p);
        return p;
    }

    /// <summary>
    /// The face moves with its wave: at fixed rest points its shear changes tick to tick only as fast as the crest carries
    /// it past (celerity x its slope across the crest, about 0.15 m per tick here), never in jumps. Crest state carried on
    /// polylines that split, merge and resample between ticks used to jump by metres.
    /// </summary>
    [Fact]
    public void Face_IsContinuousTickToTick()
    {
        var swell = BreakerSetTests.BeachForProbe(0f);
        var set = new BreakerSet();
        const int nx = 60;
        const int nz = 8;
        var before = new Vector2[nx * nz];
        var worst = 0f;
        var moving = 0;

        for (var t = 0; t < 900; t++)
        {
            swell.SetTime(100.0 + t / 60.0);
            set.Update(swell, 1f / 60f);

            for (var i = 0; i < nx * nz; i++)
            {
                // Across the surf zone, where the crests break (shoreline at x = 800 m), along a stretch of beach.
                var p = default(RestPoint);
                BreakerFace.Apply(swell, set, 700f + 1.6f * (i % nx), 400f + 23f * (i / nx), ref p);
                var shift = new Vector2(p.Dx, p.Dz);

                if (t > 0)
                {
                    var jump = Vector2.Distance(shift, before[i]);
                    worst = MathF.Max(worst, jump);
                    if (jump > 1e-3f)
                        moving++;
                }

                before[i] = shift;
            }
        }

        output.WriteLine($"largest tick-to-tick change in the face's shear {worst:F3} m ({moving} point-ticks moving)");
        Assert.True(moving > 1000, $"the face hardly moved ({moving} point-ticks): the probe missed the surf zone");
        Assert.True(worst < 0.3f, $"the face jumped {worst} m in one tick");
    }

    /// <summary>
    /// The whitewater (the face's foam weight, OceanSurface's Whitewater) moves with the bores: a Gaussian front metres
    /// wide travelling a few centimetres a tick, so no point's weight jumps tick to tick; and near the beach, where the
    /// local wavelength is still 20-30 m, a bore is a white front a few metres long, not a band the width of the face
    /// (a 10-20 m band at BACK_WIDTH of the wavelength whitened the whole strip to the waterline at once).
    /// </summary>
    [Fact]
    public void Whitewater_IsContinuousTickToTick_AndNarrowNearTheBeach()
    {
        var swell = BreakerSetTests.BeachForProbe(0f);
        var set = new BreakerSet();
        const int nx = 60;
        const int nz = 8;
        const float spacing = 0.8f;
        var before = new float[nx * nz];
        var now = new float[nx * nz];
        var worst = 0f;
        var white = 0;
        var longest = 0;

        for (var t = 0; t < 900; t++)
        {
            swell.SetTime(100.0 + t / 60.0);
            set.Update(swell, 1f / 60f);

            for (var i = 0; i < nx * nz; i++)
            {
                // The last 48 m of water before the shoreline at x = 800 m, along a stretch of beach.
                var p = default(RestPoint);
                now[i] = BreakerFace.Apply(swell, set, 752f + spacing * (i % nx), 400f + 23f * (i / nx), ref p);

                if (t > 0)
                    worst = MathF.Max(worst, MathF.Abs(now[i] - before[i]));

                if (now[i] > 0.15f)
                    white++;
            }

            // The longest run of whitewater over 0.15 along any row: the front and its wake.
            for (var row = 0; row < nz; row++)
            {
                var run = 0;
                for (var i = 0; i < nx; i++)
                {
                    run = now[row * nx + i] > 0.15f ? run + 1 : 0;
                    longest = Math.Max(longest, run);
                }
            }

            (before, now) = (now, before);
        }

        output.WriteLine($"largest tick-to-tick change in whitewater {worst:F3} ({white} white point-ticks), longest white run {longest * spacing:F1} m");
        Assert.True(white > 1000, $"hardly any whitewater ({white} point-ticks): the probe missed the bores");
        Assert.True(worst < 0.2f, $"the whitewater jumped {worst} in one tick");
        Assert.True(longest * spacing <= 14f, $"a bore near the beach whitened {longest * spacing} m at once");
    }

    /// <summary>
    /// The pitch draws the face back under a crest that stays put, so behind a curling crest it leaves the water as the
    /// swell has it: leaning the crest forward over its back instead dragged the back of every breaker (and the texture
    /// riding it) seaward at about 0.6 c as the wave went by.
    /// </summary>
    [Fact]
    public void Pitch_LeavesTheBackOfTheWaveAlone()
    {
        var swell = Surf(out var faces).Swell;
        var worstBack = 0f;
        var worstFront = 0f;

        for (var i = 0; i < faces.SegmentCount; i++)
        {
            var s = faces.Segments[i];
            if (!Curling(swell, faces, s))
                continue;

            var middle = 0.5f * (new Vector2(s.Ends.X, s.Ends.Y) + new Vector2(s.Ends.Z, s.Ends.W));
            var n = new Vector2(s.Direction.X, s.Direction.Y);

            for (var o = -0.45f; o <= 0.3f; o += 0.025f)
            {
                var rest = middle + n * (o * s.Direction.W);
                var p = default(RestPoint);
                BreakerFace.Apply(swell, faces.Breaking, rest.X, rest.Y, ref p);
                var shift = MathF.Sqrt(p.Dx * p.Dx + p.Dz * p.Dz);
                if (o < -0.05f)
                    worstBack = MathF.Max(worstBack, shift);
                else if (o > 0.05f)
                    worstFront = MathF.Max(worstFront, shift);
            }
        }

        output.WriteLine($"largest shear behind the crest {worstBack:F3} m, ahead of it {worstFront:F2} m");
        Assert.True(worstFront > 0.5f, $"the face is hardly pitched ({worstFront} m)");
        Assert.True(worstBack < 0.1f, $"the back of the wave is sheared {worstBack} m");
    }

    /// <summary>
    /// A break has memory: the distance a wave has travelled since it began to break grows from 0 where breaking starts,
    /// up to a metre per metre once the curl is up, and once it passes the crash distance (0.35 L) the crest has
    /// collapsed into whitewater, so the curl is a pocket where the break begins rather than a tunnel all the way in.
    /// </summary>
    [Fact]
    public void Break_LandsWithinItsCrashDistance()
    {
        var swell = BreakerSetTests.PlungingBeachForProbe(0f);
        var set = new BreakerSet();

        for (var t = 0; t < 600; t++)
        {
            swell.SetTime(100.0 + t / 60.0);
            set.Update(swell, 1f / 60f);
        }

        var onset = float.NaN;
        var pocket = 0f;
        var landedCurl = 0f;
        var landedBroken = 1f;
        var landed = 0;
        var slope = 0f;

        // Snapshots three seconds apart over a set (64 s) until one catches a set wave far enough up the beach to have
        // crashed (a lull wave never starts the break clock).
        for (var snap = 0; snap < 24; snap++)
        {
        for (var t = 0; t < 180; t++)
        {
            swell.SetTime(100.0 + (600 + snap * 180 + t) / 60.0);
            set.Update(swell, 1f / 60f);
        }

        onset = float.NaN;
        pocket = 0f;
        landedCurl = 0f;
        landedBroken = 1f;
        landed = 0;
        for (var x = 600f; x < 798f; x += 0.5f)
        {
            var distance = set.BreakDistance(x, 500f);
            if (float.IsNaN(onset) && distance > 0.5f)
                onset = x;

            if (!swell.SurfCarrier(x, 500f, out var c))
                continue;

            var wavelength = MathF.Tau / MathF.Sqrt(c.Kx * c.Kx + c.Kz * c.Kz);
            var travel = BreakerFace.Travel(distance, wavelength);
            var state = BreakerFace.CrestStateAt(swell, set, x, 500f);
            if (travel < 0.5f * 0.35f * wavelength)
                pocket = MathF.Max(pocket, state.Curl);
            else if (travel > 1.1f * 0.35f * wavelength && state.Height > 0.2f)
            {
                landedCurl = MathF.Max(landedCurl, state.Curl);
                landedBroken = MathF.Min(landedBroken, state.Broken);
                landed++;
            }
        }

        slope = float.IsNaN(onset) ? 0f : (set.BreakDistance(onset + 20f, 500f) - set.BreakDistance(onset + 10f, 500f)) / 10f;
        if (landed > 5)
            break;
        }

        output.WriteLine($"breaking starts at x {onset:F1}; distance grows {slope:F2} m/m; pocket curl {pocket:F2}; past the crash ({landed} points) curl <= {landedCurl:F2}, broken >= {landedBroken:F2}");
        Assert.False(float.IsNaN(onset), "nothing breaks");
        Assert.InRange(slope, 0.5f, 1.1f);
        Assert.True(pocket > 0.5f, $"no curling pocket ({pocket})");
        Assert.True(landed > 5, "the break never got past its crash distance");
        Assert.True(landedCurl < 0.35f && landedBroken > 0.95f, $"still curling past the crash: curl {landedCurl}, broken {landedBroken}");
    }

    [Fact]
    public void WithoutFaces_MatchesTheSwell()
    {
        var swell = BreakerSetTests.BeachForProbe(0f);
        swell.SetTime(100.0);
        var surface = new OceanSurface(swell) { FacesEnabled = false };
        var maxErr = 0f;
        var minDot = 1f;

        for (var ix = 0; ix < 30; ix++)
        for (var iz = 0; iz < 10; iz++)
        {
            var x = 450f + ix * 11.3f;
            var z = 300f + iz * 13.7f;
            var a = swell.Sample(x, z);
            var b = surface.Sample(x, z);
            maxErr = MathF.Max(maxErr, MathF.Abs(a.Height - b.Height));
            minDot = MathF.Min(minDot, a.Normal.X * b.Normal.X + a.Normal.Y * b.Normal.Y + a.Normal.Z * b.Normal.Z);
        }

        output.WriteLine($"max height difference {maxErr:F5} m, min normal dot {minDot:F6}");
        Assert.True(maxErr < 0.01f, $"max height difference {maxErr} m");
        Assert.True(minDot > 0.999f, $"min normal dot {minDot}");
    }

    [Fact]
    public void Sampling_IsAllocationFree()
    {
        var surface = Surf(out var faces);
        var points = AcrossCurls(surface.Swell, faces);
        Assert.NotEmpty(points);

        // Warm up past tiered JIT, then count.
        for (var pass = 0; pass < 5; pass++)
        {
            foreach (var (p, _) in points)
            {
                surface.Sample(p.X, p.Y);
                surface.Swell.Sample(p.X, p.Y);
            }
        }

        var timer = new Stopwatch();
        var before = GC.GetAllocatedBytesForCurrentThread();
        timer.Start();
        foreach (var (p, _) in points)
            surface.Sample(p.X, p.Y);

        var surfaceNs = timer.Elapsed.TotalNanoseconds / points.Count;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        timer.Restart();
        foreach (var (p, _) in points)
            surface.Swell.Sample(p.X, p.Y);

        var swellNs = timer.Elapsed.TotalNanoseconds / points.Count;
        output.WriteLine($"{points.Count} samples across curling crests: surface {surfaceNs:F0} ns, bare swell {swellNs:F0} ns, {allocated} bytes allocated");
        Assert.Equal(0, allocated);
    }
}
