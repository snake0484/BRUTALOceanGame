using BrutalGame.Water.Coast;
using BrutalGame.Water.Ocean;
using Xunit.Abstractions;

namespace BrutalGame.Water.Tests;

/// <summary>
/// The surf carrier and the breaker face on the CPU (breaking-waves-brief.md step 3; the old BreakerSetTests' assertions
/// against a baked map instead of crest polylines): head-on crests run along the shore and break in the shallows, crests
/// move shoreward, an oblique swell peels, and nothing jumps from tick to tick.
/// </summary>
public sealed class BreakerFaceTests(ITestOutputHelper output)
{
    private const double TickSeconds = 1.0 / 60.0;
    private const float Omega = 0.6f;
    private const float DeepHeight = 1.2f;

    private static readonly CoastAtlas HeadOn = CoastBakeTests.Bake((u, _) => CoastBakeTests.BeachDepth(u), CoastCarrier.From(0f, Omega, DeepHeight));
    private static readonly CoastAtlas Oblique = CoastBakeTests.Bake((u, _) => CoastBakeTests.BeachDepth(u), CoastCarrier.From(0.6f, Omega, DeepHeight));

    /// <summary>The crests' x along the row at z between x0 and x1: where the carrier's phase crosses 0 going down.</summary>
    private static List<float> Crests(CoastAtlas atlas, float z, float phase0, float x0 = 400f, float x1 = 790f)
    {
        var crests = new List<float>();
        var before = SurfCarrier.At(atlas, x0, z, phase0).Phase;

        for (var x = x0 + 0.25f; x <= x1; x += 0.25f)
        {
            var phase = SurfCarrier.At(atlas, x, z, phase0).Phase;

            if (before < 0f && phase >= 0f && phase - before < 1f)
                crests.Add(x - 0.25f * phase / (phase - before));

            before = phase;
        }

        return crests;
    }

    [Fact]
    public void HeadOnSwell_GivesShoreParallelCrestsThatBreakInTheShallows()
    {
        var phase0 = HeadOn.PhaseAt(12.0);
        var reference = Crests(HeadOn, 300f, phase0);
        Assert.True(reference.Count >= 3, $"{reference.Count} crests");

        for (var z = 200f; z <= 800f; z += 100f)
        {
            var crests = Crests(HeadOn, z, phase0);
            Assert.Equal(reference.Count, crests.Count);

            for (var i = 0; i < crests.Count; i++)
                Assert.Equal(reference[i], crests[i], 0.5f);
        }

        var broke = 0;

        foreach (var x in reference)
        {
            var crest = SurfCarrier.At(HeadOn, x, 512f, phase0);
            var cap = SurfCarrier.RawCap(crest.Unbroken, crest.Depth);
            BreakerFace.Apply(HeadOn, x + 1f, 512f, phase0, out var face);
            var state = BreakerFace.CrestState(crest, MathF.Tau / crest.K);
            output.WriteLine($"crest x {x:F1} depth {crest.Depth:F2} H {crest.Unbroken:F2} cap {cap:F2} clock {crest.BreakDistance:F1} m: curl {state.Curl:F2} broken {state.Broken:F2} lift {state.Amplitude:F2} foam {face.Foam:F2} height {face.Height:F2}");

            if (face.Foam > 0.05f)
            {
                broke++;
                Assert.True(cap < 1f, $"whitewater on a crest that is not breaking (cap {cap:F2})");
            }

            if (cap > 1.15f)
                Assert.True(face.Foam < 0.01f, $"whitewater in deep water (cap {cap:F2}, foam {face.Foam:F2})");
        }

        Assert.True(broke > 0, "no crest broke");
    }

    [Fact]
    public void Offshore_TheFaceIsNothing()
    {
        var deep = CoastBakeTests.Bake((_, _) => 300f, CoastCarrier.From(0.3f, Omega, DeepHeight));

        for (var t = 0; t < 20; t++)
        {
            BreakerFace.Apply(deep, 500f, 400f, deep.PhaseAt(t * 0.37), out var face);
            Assert.Equal(0f, face.Height);
            Assert.Equal(0f, face.Foam);
            Assert.Equal(0f, face.Dx);
        }
    }

    [Fact]
    public void Crests_KeepTheirNumberAndMoveShoreward()
    {
        var before = Crests(HeadOn, 512f, HeadOn.PhaseAt(30.0), 500f, 760f);
        var after = Crests(HeadOn, 512f, HeadOn.PhaseAt(30.0 + TickSeconds), 500f, 760f);
        Assert.Equal(before.Count, after.Count);
        Assert.True(before.Count >= 2);

        for (var i = 0; i < before.Count; i++)
        {
            var speed = (after[i] - before[i]) / TickSeconds;
            var depth = CoastBakeTests.BeachDepth(before[i]);
            output.WriteLine($"crest x {before[i]:F1} depth {depth:F1}: {speed:F2} m/s, sqrt(g h) {Math.Sqrt(9.81 * depth):F2}");

            Assert.True(speed > 0.0, "crest should travel toward the shore (+x)");
            Assert.True(speed < 10.0, "shallow-water speed is a few m/s");
        }
    }

    /// <summary>Where along a crest the break has begun (the clock has run half the onset), as z; NaN if nowhere on it.</summary>
    private static float BreakPoint(CoastAtlas atlas, float phase0, float crestX)
    {
        // Follow one crest across the rows from where it crosses z = 200.
        var x = crestX;
        var beforeOnset = float.NaN;
        var beforeZ = 0f;

        for (var z = 200f; z <= 860f; z += 1f)
        {
            for (var i = 0; i < 3; i++)
            {
                var c = SurfCarrier.At(atlas, x, z, phase0);
                x -= c.Phase / (c.K * MathF.Max(c.DirX, 0.2f));
            }

            var crest = SurfCarrier.At(atlas, x, z, phase0);
            var onset = BreakerFace.Onset(crest.BreakDistance, MathF.Tau / crest.K);

            if (!float.IsNaN(beforeOnset) && (beforeOnset - 0.5f) * (onset - 0.5f) < 0f)
                return beforeZ + (0.5f - beforeOnset) / (onset - beforeOnset);

            beforeOnset = onset;
            beforeZ = z;
        }

        return float.NaN;
    }

    [Fact]
    public void ObliqueSwell_PeelsAlongTheCrest()
    {
        var peeling = 0;

        for (var t = 20.0; t < 34.0; t += 0.5)
        {
            var phase0 = Oblique.PhaseAt(t);
            var later = Oblique.PhaseAt(t + 0.25);

            foreach (var crestX in Crests(Oblique, 200f, phase0, 680f, 780f))
            {
                var a = BreakPoint(Oblique, phase0, crestX);
                var same = SurfCarrier.At(Oblique, crestX, 200f, later);
                var b = BreakPoint(Oblique, later, crestX - same.Phase / (same.K * same.DirX));

                if (float.IsNaN(a) || float.IsNaN(b))
                    continue;

                var speed = (b - a) / 0.25f;
                output.WriteLine($"t {t:F1}: break point z {a:F1} -> {b:F1}, peel {speed:F1} m/s");
                Assert.InRange(MathF.Abs(speed), 1f, 80f);
                peeling++;
            }
        }

        Assert.True(peeling > 0, "an oblique swell should leave break points that move along the crest");
    }

    [Fact]
    public void HeadOnSwell_BreaksAlongTheWholeCrestAtOnce()
    {
        var phase0 = HeadOn.PhaseAt(12.0);

        foreach (var x in Crests(HeadOn, 300f, phase0, 700f, 785f))
        {
            var first = BreakerFace.Onset(SurfCarrier.At(HeadOn, x, 300f, phase0).BreakDistance, 30f);

            for (var z = 200f; z <= 800f; z += 50f)
                Assert.Equal(first, BreakerFace.Onset(SurfCarrier.At(HeadOn, x, z, phase0).BreakDistance, 30f), 0.15f);
        }
    }

    /// <summary>
    /// Per tick, in motion: at fixed rest points across the surf zone the face's height, shift, slope and whitewater
    /// move by a wave's own travel and never jump. The old face moved 0.038 to 0.076 m a tick.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Face_ChangesSmoothlyTickToTick(bool oblique)
    {
        var atlas = oblique ? Oblique : HeadOn;
        var stats = default(BreakerStability);

        for (var x = 680f; x <= 790f; x += 3.7f)
        {
            for (var z = 300f; z <= 700f; z += 133f)
                stats.Run(atlas, x, z, 0, 600, TickSeconds);
        }

        output.WriteLine(stats.ToString());
        Assert.True(stats.MaxHeightStep <= 0.08f, $"height moved {stats.MaxHeightStep:F3} m in a tick");
        Assert.Equal(0, stats.HeightJumps);
        Assert.Equal(0, stats.FoamJumps);
        Assert.Equal(0, stats.SlopeJumps);
        Assert.True(stats.MaxHeight > 0.5f, $"the face never stood up ({stats.MaxHeight:F2} m)");
        Assert.True(stats.MaxFoam > 0.5f, $"no whitewater ({stats.MaxFoam:F2})");
    }

    [Fact]
    public void Face_IsContinuousAcrossTheSurfZone()
    {
        var phase0 = Oblique.PhaseAt(41.3);
        var worst = 0f;
        var worstShift = 0f;
        BreakerFace.Apply(Oblique, 660f, 512f, phase0, out var before);

        for (var x = 660.05f; x < 795f; x += 0.05f)
        {
            BreakerFace.Apply(Oblique, x, 512f, phase0, out var face);
            worst = MathF.Max(worst, MathF.Abs(face.Height - before.Height));
            worstShift = MathF.Max(worstShift, MathF.Abs(face.Dx - before.Dx));
            before = face;
        }

        output.WriteLine($"largest step over 5 cm: height {worst:F3} m, shift {worstShift:F3} m");
        Assert.True(worst < 0.06f, $"height steps {worst:F3} m over 5 cm");
        Assert.True(worstShift < 0.06f, $"shift steps {worstShift:F3} m over 5 cm");
    }

    [Fact]
    public void Pitch_NeverFoldsTheSurface()
    {
        var least = 1f;

        for (var t = 0; t < 40; t++)
        {
            var phase0 = Oblique.PhaseAt(50.0 + t * 0.3);

            for (var x = 680f; x < 795f; x += 0.5f)
            {
                BreakerFace.Apply(Oblique, x, 512f, phase0, out var face);
                least = MathF.Min(least, (1f + face.DxX) * (1f + face.DzZ) - face.DxZ * face.DxZ);
            }
        }

        output.WriteLine($"least Jacobian {least:F3}");
        Assert.True(least > 0.05f, $"the face folds (Jacobian {least:F3})");
    }
}
