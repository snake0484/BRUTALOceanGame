using System.Numerics;
using BrutalGame.Water.Ocean;
using Xunit.Abstractions;

namespace BrutalGame.Water.Tests;

/// <summary>
/// The lip's tongue measured, not eyeballed (docs/breaking-waves.md, thirteenth pass): over a wave period, every crest
/// seated along a beach line through the surf zone. Where a crest's break begins its reach is 0 and the tip sits on the
/// crest; by LIP_REACH_TRAVEL of the crash distance the reach is 1 and the tip is on the drawn surface at the face's
/// foot, the full face below the crest; in between the tip is in the air above the surface.
/// </summary>
public sealed class BreakerLipTests(ITestOutputHelper output)
{
    private static readonly Vector2 Shoreward = Vector2.UnitX;
    private const int Warmup = 600;
    private const int Ticks = 720;
    private const int Every = 6;

    private readonly record struct Lip(float X, float Travel, LipTip Tip, float Under);

    private List<Lip> Scan(OceanSwell swell, float from, float minHeight = 0f, int ticks = Ticks)
    {
        var set = new BreakerSet();
        var surface = new OceanSurface(swell, set);
        var lips = new List<Lip>();

        for (var t = 0; t < Warmup + ticks; t++)
        {
            swell.SetTime(100.0 + t / 60.0);
            set.Update(swell, 1f / 60f);
            if (t < Warmup || t % Every != 0)
                continue;

            for (var x = from; x < 798f; x += 1f)
            {
                if (!BreakerLip.Sample(surface, set, x, 500f, Shoreward, out var lip) || lip.Height < minHeight)
                    continue;

                lips.Add(new Lip(x, set.BreakDistance(lip.Crest.X, lip.Crest.Z), lip, surface.SampleHeight(lip.Tip.X, lip.Tip.Z)));
            }
        }

        return lips;
    }

    [Fact]
    public void Tongue_GrowsFromTheCrestAndFallsToTheFoot()
    {
        // The plunging beach's lips with room to break are its set waves, one every 64 s: a whole set, waves over 1.5 m.
        var lips = Scan(BreakerSetTests.PlungingBeachForProbe(0f), 700f, 1.5f, 6 * Ticks);
        var reaching = lips.Where(l => l.Tip.Reach is > 0.1f and < 0.9f).ToList();
        var landed = lips.Where(l => l.Tip.Reach >= 0.999f && l.Tip.Crash < 0.5f).ToList();
        var begun = lips.Where(l => l.Tip.Reach < 0.1f).ToList();
        var crashed = lips.Count(l => l.Tip.Crash >= 0.999f);

        for (var bin = 0; bin < 10; bin++)
        {
            var inBin = lips.Where(l => (int)MathF.Min(l.Tip.Reach * 10f, 9f) == bin).ToList();
            if (inBin.Count == 0)
                continue;

            output.WriteLine($"reach {bin / 10f:F1}-{(bin + 1) / 10f:F1}: {inBin.Count} samples, travel {inBin.Average(l => l.Travel):F1} m, plunge {inBin.Average(l => l.Tip.Plunge):F2}, H {inBin.Average(l => l.Tip.Height):F2} m, tip {inBin.Average(l => l.Tip.Crest.Y - l.Tip.Tip.Y):F2} m below the crest, {inBin.Average(l => l.Tip.Tip.Y - l.Under):F2} m above the surface under it, {inBin.Average(l => l.Tip.Tip.X - l.Tip.Crest.X):F2} m ahead");
        }

        var minTravelLanded = landed.Count > 0 ? landed.Min(l => l.Travel) : float.NaN;
        var worstMiss = landed.Count > 0 ? landed.Max(l => MathF.Abs(l.Tip.Tip.Y - l.Tip.Landing.Y)) : float.NaN;
        var drop = landed.Count > 0 ? landed.Average(l => (l.Tip.Crest.Y - l.Tip.Tip.Y) / MathF.Max(l.Tip.Height, 0.1f)) : float.NaN;
        var lowestClearance = reaching.Count > 0 ? reaching.Min(l => l.Tip.Tip.Y - l.Under) : float.NaN;
        output.WriteLine($"{lips.Count} lips over {Ticks / 60f:F0} s: {begun.Count} just begun, {reaching.Count} reaching, {landed.Count} at the foot, {crashed} crashed; the foot is reached after {minTravelLanded:F1} m of travel at least; landed tips miss the foot by at most {worstMiss:F3} m and sit {drop:F2} H below the crest; reaching tips clear the surface by at least {lowestClearance:F2} m");

        Assert.True(lips.Count > 100, "too few lips along the beach line");
        Assert.True(begun.Count > 0, "no lip is ever just beginning: the tongue does not grow");
        Assert.True(crashed > 0 && landed.Count > 0, $"{crashed} crashed tongues, {landed.Count} landed and not yet crashed: they should land, then crash");
        Assert.True(reaching.Count > 20, "the tongue is never caught reaching: it jumps to the foot");
        Assert.True(landed.Count > 20, "the tongue never reaches the foot");
        Assert.True(minTravelLanded > 2f, "the tongue reached the foot the moment the break began: it does not travel");
        Assert.True(lowestClearance > 0f, $"a reaching tip is under the surface ({lowestClearance} m)");
        Assert.True(worstMiss < 0.01f, $"a landed tip is {worstMiss} m off the face's foot");
        Assert.True(drop > 0.15f, $"the tongue only falls {drop} H");
    }

    /// <summary>
    /// Where the face stands steep (4 m wind-sea faces at 20 m/s on the island's 1:50 beach) the tongue overhangs: it
    /// lands within LIP_MAX_THROW x H ahead of the crest and well down the face.
    /// </summary>
    [Fact]
    public void SteepFace_TongueFallsDownTheFace()
    {
        var lips = Scan(BreakerSetTests.SteepBeachForProbe(0f), 600f);
        var landed = lips.Where(l => l.Tip.Reach >= 0.999f && l.Tip.Height > 1f && l.Tip.Crash < 0.5f).ToList();
        var drop = landed.Count > 0 ? landed.Average(l => (l.Tip.Crest.Y - l.Tip.Tip.Y) / l.Tip.Height) : float.NaN;
        var ahead = landed.Count > 0 ? landed.Average(l => (l.Tip.Tip.X - l.Tip.Crest.X) / l.Tip.Height) : float.NaN;
        var height = landed.Count > 0 ? landed.Average(l => l.Tip.Height) : float.NaN;

        output.WriteLine($"{lips.Count} lips on the steep beach, {landed.Count} landed on waves over 1 m (H {height:F2} m): tips {drop:F2} H below the crest, {ahead:F2} H ahead");
        Assert.True(landed.Count > 20, "no landed lips on waves over 1 m");
        // The landing is capped at LIP_MAX_THROW (1.2) H ahead through the wall's mean Jacobian; the drawn wall's own varies a little.
        Assert.True(ahead <= 1.4f, $"the tongue reaches {ahead} H ahead");
        Assert.True(drop > 0.5f, $"the tongue only falls {drop} H down a steep face");
    }

    /// <summary>
    /// One crest followed through its break (the shoulder surf-cam sits at the break point, where every lip is newborn,
    /// so it never shows this): from the lip's first appearance its reach must grow to the foot within a few seconds,
    /// the tip dropping down the face as it does, and then crash.
    /// </summary>
    [Theory]
    [InlineData("steep", 0)]
    [InlineData("plunging", 1)]
    public void Lip_FollowedAlongOneCrest_ThrowsFallsAndCrashes(string beach, int kind)
    {
        var swell = kind == 0 ? BreakerSetTests.SteepBeachForProbe(0f) : BreakerSetTests.PlungingBeachForProbe(0f);
        var set = new BreakerSet();
        var surface = new OceanSurface(swell, set);
        const float dt = 1f / 60f;

        for (var t = 0; t < Warmup; t++)
        {
            swell.SetTime(100.0 + t * dt);
            set.Update(swell, dt);
        }

        // Find a crest with a lip just born, seaward of the surf zone's middle: lips are newborn for a fraction of a
        // second, so look each tick until one is.
        var x = float.NaN;
        var tBorn = float.NaN;
        var tFoot = float.NaN;
        var tCrash = float.NaN;
        var deepest = 0f;
        var youngestEver = float.MaxValue;
        var youngestEverX = float.NaN;
        var youngestEverT = float.NaN;
        var log = new List<string>();

        // Up to 70 s: on the steep beach only a set wave (every 64 s) is born over 0.5 m; lull waves are born at 0.3-0.5 m.
        for (var t = 0; t < 4200 && float.IsNaN(tCrash); t++)
        {
            swell.SetTime(100.0 + (Warmup + t) * dt);
            set.Update(swell, dt);
            var seconds = t * dt;

            if (float.IsNaN(x))
            {
                // The youngest lip on the line this tick, if young enough (a steady spiller is newborn only at its onset).
                var youngest = float.MaxValue;
                var youngestX = float.NaN;
                for (var probe = 600f; probe < 795f; probe += 0.5f)
                    if (BreakerLip.Sample(surface, set, probe, 500f, Shoreward, out var born) && born.Reach < youngest && born.Plunge >= kind * 0.5f && born.Height > (kind == 1 ? 1.5f : 0.4f) && born.Crash < 0.1f)
                        (youngest, x, youngestX) = (born.Reach, born.Crest.X, born.Crest.X);

                if (youngest < youngestEver)
                    (youngestEver, youngestEverX, youngestEverT) = (youngest, youngestX, seconds);

                if (youngest < 0.3f)
                    tBorn = seconds;
                else
                    x = float.NaN;

                continue;
            }

            // Follow the crest: seat on the nearest crest to where it was, nudged ahead by its travel.
            if (!swell.SurfCarrier(x, 500f, out var c))
                break;

            var k = MathF.Sqrt(c.Kx * c.Kx + c.Kz * c.Kz);
            x += swell.PeakOmega / MathF.Max(k, 1e-4f) * dt;
            if (!BreakerLip.Sample(surface, set, x, 500f, Shoreward, out var lip))
            {
                if (!float.IsNaN(tFoot))
                    tCrash = seconds;

                continue;
            }

            x = lip.Crest.X;
            deepest = MathF.Max(deepest, (lip.Crest.Y - lip.Tip.Y) / MathF.Max(lip.Height, 0.1f));
            if (float.IsNaN(tFoot) && lip.Reach >= 0.999f)
                tFoot = seconds;

            if (!float.IsNaN(tFoot) && lip.Crash >= 0.5f)
                tCrash = seconds;

            if (t % 6 == 0)
                log.Add($"  t {seconds:F1} s x {x:F1} H {lip.Height:F2} plunge {lip.Plunge:F2} reach {lip.Reach:F2} crash {lip.Crash:F2} tip {lip.Crest.Y - lip.Tip.Y:F2} m below the crest, {lip.Tip.X - lip.Crest.X:F2} m ahead");
        }

        foreach (var line in log)
            output.WriteLine(line);

        output.WriteLine($"{beach}: born at t {tBorn:F1}, at the foot at t {tFoot:F1}, crashed at t {tCrash:F1}; deepest {deepest:F2} H below the crest");
        Assert.False(float.IsNaN(x), $"no newborn lip on the beach line in 70 s: the youngest seen had reach {youngestEver:F2} at x {youngestEverX:F1}, t {youngestEverT:F1}");
        Assert.False(float.IsNaN(tFoot), "the tongue never reached the foot");
        Assert.InRange(tFoot - tBorn, 0.3f, 4f);

        if (kind == 1)
        {
            Assert.False(float.IsNaN(tCrash), "the plunging tongue never crashed");
            Assert.InRange(tCrash - tFoot, 0f, 2.5f);
            Assert.True(deepest > 0.5f, $"the tongue only fell {deepest} H");
        }
        else
        {
            // A spilling tongue holds as a steady tumble: no crash for at least 3 s after it reaches its span.
            Assert.True(float.IsNaN(tCrash) || tCrash - tFoot > 3f, $"the spilling tongue crashed {tCrash - tFoot:F1} s after reaching its span");
            Assert.True(deepest > 0.25f, $"the tongue only fell {deepest} H");
        }
    }

    /// <summary>
    /// The tongue must span the face it sits on: the drawn surface is the carrier plus the random sea, and a landing
    /// sized from the carrier's capped height alone left a sliver on a wall several times taller. For landed lips, the
    /// drop from the crest to the landing against the drawn face's own height (crest to the lowest surface within half a
    /// wavelength ahead).
    /// </summary>
    [Theory]
    [InlineData("steep", 0)]
    [InlineData("plunging", 1)]
    public void Lip_SpansTheDrawnFace(string beach, int kind)
    {
        var swell = kind == 0 ? BreakerSetTests.SteepBeachForProbe(0f) : BreakerSetTests.PlungingBeachForProbe(0f);
        var set = new BreakerSet();
        var surface = new OceanSurface(swell, set);
        var shares = new List<float>();
        var faces = new List<float>();
        var carriers = new List<float>();

        // The plunging beach's lips with room to break are its set waves, one every 64 s: scan a whole set there.
        for (var t = 0; t < Warmup + (kind == 1 ? 6 * Ticks : Ticks); t++)
        {
            swell.SetTime(100.0 + t / 60.0);
            set.Update(swell, 1f / 60f);
            if (t < Warmup || t % 12 != 0)
                continue;

            for (var x = 640f; x < 798f; x += 1f)
            {
                if (!BreakerLip.Sample(surface, set, x, 500f, Shoreward, out var lip) || lip.Reach < 0.999f || lip.Height < (kind == 1 ? 1.5f : 0.4f) || lip.Crash > 0.5f)
                    continue;

                if (!swell.SurfCarrier(lip.Crest.X, lip.Crest.Z, out var c))
                    continue;

                var half = 0.5f * MathF.Tau / MathF.Max(MathF.Sqrt(c.Kx * c.Kx + c.Kz * c.Kz), 1e-4f);
                var trough = lip.Crest.Y;
                for (var d = 0.25f; d <= half; d += 0.25f)
                    trough = MathF.Min(trough, surface.SampleHeight(lip.Crest.X + d, lip.Crest.Z));

                var face = lip.Crest.Y - trough;
                if (face < 0.3f)
                    continue;

                faces.Add(face);
                carriers.Add(lip.Height);
                shares.Add((lip.Crest.Y - lip.Landing.Y) / face);
            }
        }

        Assert.True(shares.Count > 20, "too few landed lips");
        shares.Sort();
        output.WriteLine($"{beach}: {shares.Count} landed lips; drawn face {faces.Average():F2} m on average (carrier H {carriers.Average():F2} m); the tongue spans {shares.Average():P0} of it on average, {shares[shares.Count / 10]:P0} at the 10th percentile");
        // The tongue lands at the foot of the pitched wall, the steep upper part of the face; below it the swell's own
        // gentle slope runs down to the trough, which this denominator includes. A spiller tumbles down the upper wall only.
        var (mean, tenth) = kind == 1 ? (0.35f, 0.15f) : (0.3f, 0.15f);
        Assert.True(shares.Average() > mean, $"the tongue spans only {shares.Average():P0} of the drawn face");
        Assert.True(shares[shares.Count / 10] > tenth, $"a tenth of the tongues span under {shares[shares.Count / 10]:P0} of the face");
    }

    /// <summary>
    /// Where the lip's root sits on the drawn wave. A root is on a flank when, within 3 m along the travel direction,
    /// the drawn surface stands at least a tenth of the face (and 10 cm) higher than it. Measured with the root on the
    /// carrier crest (what the GPU draws) and, for comparison, seated on the drawn surface's own crest
    /// (BreakerLip.SeatOnDrawn): 0% and 0% on the 20 m/s beach and the storm scene, 8% and 6% on the plunging fixture,
    /// so the carrier crest is kept and the seating is a diagnostic.
    /// </summary>
    [Theory]
    [InlineData("steep", 0)]
    [InlineData("plunging", 1)]
    [InlineData("storm", 2)]
    public void Lip_RootSitsOnTheDrawnCrest(string beach, int kind)
    {
        var results = new List<(bool Seated, float Flank, float Rise)>();

        foreach (var seated in new[] { false, true })
        {
            BreakerLip.SeatOnDrawnCrest = seated;
            try
            {
                var swell = kind == 0 ? BreakerSetTests.SteepBeachForProbe(0f) : kind == 1 ? BreakerSetTests.PlungingBeachForProbe(0f) : BreakerSetTests.StormBeachForProbe(0f);
                var set = new BreakerSet();
                var surface = new OceanSurface(swell, set);
                var flank = 0;
                var lips = 0;
                var rises = 0f;

                for (var t = 0; t < Warmup + (kind == 1 ? 6 * Ticks : Ticks); t++)
                {
                    swell.SetTime(100.0 + t / 60.0);
                    set.Update(swell, 1f / 60f);
                    if (t < Warmup || t % 12 != 0)
                        continue;

                    for (var x = 640f; x < 798f; x += 1f)
                    {
                        if (!BreakerLip.Sample(surface, set, x, 500f, Shoreward, out var lip) || lip.Height < (kind == 1 ? 1.5f : 0.4f) || lip.Crash > 0.5f)
                            continue;

                        var highest = lip.Crest.Y;
                        for (var d = -3f; d <= 3f; d += 0.25f)
                            highest = MathF.Max(highest, surface.SampleHeight(lip.Crest.X + d, lip.Crest.Z));

                        var face = MathF.Max(lip.Crest.Y - lip.Landing.Y, 0.3f);
                        var rise = highest - lip.Crest.Y;
                        lips++;
                        rises += rise;
                        if (rise > MathF.Max(0.1f, 0.1f * face))
                            flank++;
                    }
                }

                Assert.True(lips > 50, "too few lips");
                results.Add((seated, flank / (float)lips, rises / lips));
            }
            finally
            {
                BreakerLip.SeatOnDrawnCrest = false;
            }
        }

        foreach (var (seated, flank, rise) in results)
            output.WriteLine($"{beach}, root on the {(seated ? "drawn" : "carrier")} crest: {flank:P0} of lips on a flank, the surface nearby higher by {rise:F3} m on average");

        var drawn = results[0];
        Assert.True(drawn.Flank < 0.15f, $"{drawn.Flank:P0} of lips sit on a flank with the root on the carrier crest");
    }

    /// <summary>Diagnostic: the surf zone of the steep beach along one line at one tick: depth, carrier height, cap, break distance, curl, reach.</summary>
    [Fact]
    public void Diagnostic_SteepBeachProfile()
    {
        var swell = BreakerSetTests.SteepBeachForProbe(0f);
        var set = new BreakerSet();
        var surface = new OceanSurface(swell, set);
        for (var t = 0; t < Warmup; t++)
        {
            swell.SetTime(100.0 + t / 60.0);
            set.Update(swell, 1f / 60f);
        }

        for (var x = 560f; x < 800f; x += 8f)
        {
            if (!swell.SurfCarrier(x, 500f, out var c))
                continue;

            var k = MathF.Sqrt(c.Kx * c.Kx + c.Kz * c.Kz);
            var cap = c.Unbroken > 1e-4f ? Math.Clamp(0.78f * MathF.Max(c.Depth, 0f) / c.Unbroken, 0f, 1f) : 1f;
            var state = BreakerFace.CrestStateAt(swell, set, x, 500f);
            var d = set.BreakDistance(x, 500f);
            var (reach, _) = BreakerLip.Life(d, MathF.Tau / k, 0f);
            output.WriteLine($"x {x:F0} depth {c.Depth:F2} H {c.Unbroken:F2} cap {cap:F3} L {MathF.Tau / k:F1} break {d:F1} m curl {state.Curl:F2} reach {reach:F2} phase {c.Phase % MathF.Tau:F2}");
        }
    }

    /// <summary>On the 1:20 spilling beach the tongue extends and then holds as a steady tumble while the face stands.</summary>
    [Fact]
    public void SpillingLip_HoldsAtTheFoot()
    {
        // The small waves of the wind sea plunge even here (xi grows as H shrinks); the cascade is judged on the spillers.
        // These 2 m waves on 35 m are rounded at the crest, so the cascade is a lick down the face, not a fall.
        var lips = Scan(BreakerSetTests.BeachForProbe(0f), 740f).Where(l => l.Tip.Plunge < 0.3f).ToList();
        var atFoot = lips.Count(l => l.Tip.Reach >= 0.99f);
        var reaching = lips.Count(l => l.Tip.Reach is > 0.1f and < 0.9f);
        var crashed = lips.Count(l => l.Tip.Crash > 0.5f);
        var drop = atFoot > 0 ? lips.Where(l => l.Tip.Reach >= 0.99f).Average(l => (l.Tip.Crest.Y - l.Tip.Tip.Y) / MathF.Max(l.Tip.Height, 0.1f)) : float.NaN;

        output.WriteLine($"{lips.Count} spilling lips on the 1:20 beach: {reaching} reaching, {atFoot} at the foot, {crashed} crashing; at the foot {drop:F2} H below the crest");
        Assert.True(lips.Count > 50, "no spilling lips on the 1:20 beach");
        Assert.True(atFoot > lips.Count / 2, "a spilling tongue should extend and then hold as a steady tumble");
        Assert.True(drop > 0.1f, $"the cascade only falls {drop} H");
    }
}
