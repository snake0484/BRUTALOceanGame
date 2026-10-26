using System.Numerics;

namespace BrutalGame.Water.Ocean;

/// <summary>
/// The breaker face on the CPU (docs/breaking-waves.md, rendering step 3), mirroring ocean_common.glsl breakerFace: keep
/// the two in step. A function of the surf carrier alone (<see cref="OceanSwell.SurfCarrier"/>): the nearest crest is
/// where its phase is 0, found by Newton steps along the local wavevector and refined there; the signed distance from it
/// (along <see cref="OceanSwell.CarrierDirection"/>, smooth where the lag map's gradient is not), the wavelength and the
/// crest's surf state come from the carrier there, so the face is continuous in space and time.
/// </summary>
public static class BreakerFace
{
    private const float BackWidth = 0.18f;
    private const float FrontWidth = 0.09f;
    private const float MinFront = 0.8f;
    private const float Peak = 2.5f;
    private const float Pitch = 0.88f;
    private const float PitchSqueezeMax = 0.94f;
    private const float WallWidth = 1f;
    private const float PitchFront = 0.2f;
    private const float PitchRecover = 0.17f;
    private const float PitchReach = 0.4f;
    private const float PitchFade = 0.48f;
    private const float PitchMax = 30f;
    private const float PitchTop = 20f;
    private const float PitchFoot = 4f;
    private const float PitchEnd = 8f;

    /// <summary>The wall only draws back water that is there: it fades out over the last this many metres of depth under each point (PITCH_DRY_DEPTH).</summary>
    private const float PitchDryDepth = 0.6f;

    /// <summary>
    /// Whitewater envelope in metres from the wave's height, apart from the face's wavelength-scaled width (ocean_common.glsl
    /// WHITE_*): the front's Gaussian width ahead of the crest x the height, between <see cref="WhiteFrontMin"/> and
    /// <see cref="WhiteFrontMax"/>; <see cref="WhiteBack"/> x that behind it; and it fades out over the last
    /// <see cref="WhiteDryDepth"/> of water, where the swash's front takes over.
    /// </summary>
    private const float WhiteFront = 1.5f;
    private const float WhiteFrontMin = 0.8f;
    private const float WhiteFrontMax = 4f;
    private const float WhiteBack = 1.6f;
    private const float WhiteDryDepth = 0.15f;

    /// <summary>Whitewater trailing behind a broken crest: its strength and e-folding length x the wave height (m), at least <see cref="RollerTailMin"/> (ocean_common.glsl).</summary>
    private const float RollerTrail = 0.25f;
    private const float RollerTail = 2f;
    private const float RollerTailMin = 1.5f;

    /// <summary>Wavelengths a breaking section travels from the start of its break until its lip lands (ocean_common.glsl).</summary>
    private const float BreakCrash = 0.35f;

    /// <summary>Wavelengths of travel after the clock starts over which the face stands up (curl, lift, pitch); the lip's life and the collapse run on the travel past that.</summary>
    public const float OnsetTravel = 0.15f;

    /// <summary>While the lip is in the air the breaking height is held at its onset value, at most this × the depth (the crest runs on into shallower water as it throws).</summary>
    public const float HoldGamma = 1.5f;

    /// <summary>Wavelengths after the clock starts over which the hold comes on (HOLD_RAMP).</summary>
    public const float HoldRamp = 0.03f;

    /// <summary>Wavelengths past the onset at which the lip has landed and crashed (LANDED_TRAVEL), and the travel past that over which the crest's held height, lift and wall ease away (COLLAPSE_TRAVEL, about a period).</summary>
    public const float LandedTravel = 0.245f;
    public const float CollapseTravel = 1f;

    /// <summary>Wavelengths of travel past the landing over which a landed tongue's throw falls back onto the face (LIP_CLOSE_TRAVEL).</summary>
    public const float LipCloseTravel = 0.3f;

    /// <summary>Below this breaking cap the water has run out under the crest: broken whatever its travel.</summary>
    private const float SpentCap = 0.45f;

    /// <summary>
    /// Shoaling stands the wave up before it breaks: from H / h = <see cref="StandFrom"/> to breaking (γ) the face
    /// steepens (peak and pitch) to <see cref="StandAmount"/>, and a broken wave keeps that as its rolling front.
    /// </summary>
    private const float StandFrom = 0.45f;
    private const float StandAmount = 0.55f;

    /// <summary>A breaking crest's face stands at least this steep under its lip, spilling or plunging (a spiller's 0.55 was a 25° slope: the tongue lay on it as a shelf).</summary>
    private const float BreakSteep = 0.85f;

    /// <summary>The whitewater where a throwing lip lands is full from this wave height (THROW_FOAM_HEIGHT).</summary>
    private const float ThrowFoamHeight = 0.6f;

    /// <summary>
    /// Breaker type from the surf similarity number ξ = tan β / √(H / L₀) where the wave began to break
    /// (docs/surf-research.md): spilling below <see cref="PlungeFrom"/> (whitewater tumbles down a steep front, no lip),
    /// plunging by <see cref="PlungeFull"/> (the lip is thrown). The bed slope is taken <see cref="SlopeReach"/> depths
    /// either side of the onset point (at least a texel, at most <see cref="SlopeMaxSpan"/>), so a bar's face sets the
    /// type and its flat top keeps it; the onset is looked for at most <see cref="OnsetBackMax"/> back along the travel.
    /// </summary>
    private const float PlungeFrom = 0.35f;
    private const float PlungeFull = 0.65f;

    /// <summary>A spilling crest still tips a lip this share of a plunging one's, which tumbles straight down the face.</summary>
    private const float SpillTip = 0.5f;
    private const float SlopeReach = 2f;
    private const float SlopeMinSpan = 4f;
    private const float SlopeMaxSpan = 20f;
    private const float OnsetBackMax = 60f;

    /// <summary>
    /// What the face adds to the swell at rest position (x0, z0), whose height and partials <paramref name="p"/> already
    /// holds: the crest lift, the peak and the pitch, with their slope and Jacobian terms. Returns the whitewater weight.
    /// In the field fading out when <paramref name="previous"/>; <paramref name="breaking"/> gives how far each crest has
    /// travelled since it began to break (none: every crest has just begun). Read-only, so safe from jobs between ticks.
    /// </summary>
    public static float Apply(OceanSwell swell, BreakerSet? breaking, float x0, float z0, ref RestPoint p, bool previous = false)
    {
        if (!swell.SurfCarrier(x0, z0, out var here, previous))
            return 0f;

        var phase = Wrap(here.Phase);

        // Two Newton steps to the crest: the second takes the error of the first (the lag map's wavenumber steps against
        // k(h)) from centimetres to well under a millimetre, so sn moves one for one with x0, as the derivatives assume.
        var step = phase / (here.Kx * here.Kx + here.Kz * here.Kz);
        var guess = new Vector2(x0 - here.Kx * step, z0 - here.Kz * step);
        if (!swell.SurfCarrier(guess.X, guess.Y, out var crest, previous))
            return 0f;

        step = Wrap(crest.Phase) / (crest.Kx * crest.Kx + crest.Kz * crest.Kz);
        guess -= new Vector2(crest.Kx, crest.Kz) * step;
        if (!swell.SurfCarrier(guess.X, guess.Y, out crest, previous))
            return 0f;

        var k = MathF.Max(MathF.Sqrt(crest.Kx * crest.Kx + crest.Kz * crest.Kz), 1e-6f);
        var n = swell.CarrierDirection(guess.X, guess.Y, out var dirX, out var dirZ, previous) ? new Vector2(dirX, dirZ) : new Vector2(crest.Kx, crest.Kz) / k;
        var sn = Vector2.Dot(new Vector2(x0, z0) - guess, n) + Wrap(crest.Phase) / k;
        var wavelength = MathF.Tau / k;
        var fadeEnd = MathF.Min(PitchFade * wavelength, PitchMax);

        // The window runs on the phase at x0: it hands over to the next crest at exactly ±π, past fadeEnd, whatever
        // shoaling does to the spacing ahead.
        var reach = MathF.Abs(phase) / k;
        if (reach >= fadeEnd)
            return 0f;

        var breakDistance = breaking?.BreakDistance(guess.X, guess.Y) ?? 0f;
        var plunge = Plunge(swell, guess, n, crest.Depth, breakDistance, crest.Unbroken, previous);
        var state = CrestState(crest, breakDistance, wavelength, plunge);
        if (state.Steep <= 0f && state.Amplitude < 1e-3f)
            return 0f;

        // Everything fades out before the trough, where the phase hands over to the next crest.
        var fadeStart = PitchReach / PitchFade * fadeEnd;
        var tw = Math.Clamp((reach - fadeStart) / (fadeEnd - fadeStart), 0f, 1f);
        var window = 1f - tw * tw * (3f - 2f * tw);
        var dwindow = -MathF.Sign(phase) * 6f * tw * (1f - tw) / (fadeEnd - fadeStart) * MathF.Sqrt(here.Kx * here.Kx + here.Kz * here.Kz) / k;

        var amount = state.Steep;
        var width = sn < 0f ? BackWidth * wavelength : MathF.Max(float.Lerp(BackWidth, FrontWidth, amount) * wavelength, MinFront);
        var e = MathF.Exp(-(sn * sn) / (width * width));
        var h = state.Amplitude * e * window;
        var dh = state.Amplitude * e * (dwindow - 2f * sn / (width * width) * window);

        // Peak: height above the trough, as a share of the carrier's own crest-to-trough span, raised to gamma (linear
        // past the crest, unchanged below the trough). The span is the carrier's: its bound harmonics put its crest
        // 1 + s + s² and its trough 1 − s + s² (over the norm) from the mean (OceanSwell.CoastalWaves), not ±H / 2; with
        // ±H / 2 the harmonic crest sat above the reference and the linear continuation tripled the excess (a 2.7 m wave
        // drew a 5 m crest once its height was held past γ h).
        var a = 0.5f * state.Height;
        if (a > 0.05f && amount > 0f)
        {
            var w = amount * window;
            var dw = amount * dwindow;
            var gamma = float.Lerp(1f, Peak, w);
            var s = OceanSwell.Sharpness(state.Height, crest.Depth);
            var norm = MathF.Sqrt(1f + s * s * (1f + s * s));
            var crestAmp = a * (1f + s + s * s) / norm;
            var troughAmp = a * (1f - s + s * s) / norm;
            var span = crestAmp + troughAmp;
            var share = (p.Height + troughAmp) / span;
            var peaked = share <= 0f ? share : share < 1f ? MathF.Pow(share, gamma) : 1f + gamma * (share - 1f);
            var dShare = share <= 0f ? 1f : share < 1f ? gamma * MathF.Pow(share, gamma - 1f) : gamma;
            var dGamma = share <= 0f ? 0f : share < 1f ? peaked * MathF.Log(share) : share - 1f;
            var across = span * dGamma * (Peak - 1f) * dw;
            p.Height += span * (peaked - share);
            p.SlopeX = p.SlopeX * dShare + across * n.X;
            p.SlopeZ = p.SlopeZ * dShare + across * n.Y;
        }

        // Pitch: the face is drawn back under the crest, which stays put: the upper face is squeezed into a near-vertical
        // wall and the trough ahead gives back what the wall drew in.
        var front = PitchFront * wavelength;
        var recover = PitchRecover * wavelength;
        var wall = SoftRamp(sn / front, PitchTop, PitchFoot, out var dWall);
        var back = SoftRamp((sn - front) / recover, PitchFoot, PitchEnd, out var dRecover);
        var pitch = Math.Clamp(1f - WallWidth * state.Height / front, Pitch, PitchSqueezeMax);
        var lean = pitch * front * amount * SmoothStep(0f, PitchDryDepth, here.Depth);
        var tent = -lean * (wall - back);
        var shift = tent * window;
        var dshift = -lean * (dWall / front - dRecover / recover) * window + tent * dwindow;

        p.Height += h;
        p.SlopeX += dh * n.X;
        p.SlopeZ += dh * n.Y;
        p.Dx += shift * n.X;
        p.Dz += shift * n.Y;
        p.DxX += dshift * n.X * n.X;
        p.DzZ += dshift * n.Y * n.Y;
        p.DxZ += dshift * n.X * n.Y;

        // Whitewater: ahead of a throwing crest where its lip lands (the crest stays glassy under the lip); a spilling
        // crest's whitewater tumbling down its front; then, once it has collapsed, a roller on the bore front trailing
        // foam behind it. Its envelope is metres from the wave's height (White*), not the face's width, and it fades
        // out over the last of the water.
        var whiteFront = Math.Clamp(WhiteFront * state.Height, WhiteFrontMin, WhiteFrontMax);
        var whiteWidth = sn < 0f ? WhiteBack * whiteFront : whiteFront;
        var ew = MathF.Exp(-(sn * sn) / (whiteWidth * whiteWidth));
        var throwing = SmoothStep(0.6f, 1f, state.Curl) * (sn > 0f ? 0.6f * ew : 0.15f * ew * ew * ew) * SmoothStep(0f, ThrowFoamHeight, state.Height);
        var spilling = state.Spill * (sn > 0f ? ew : ew * ew);
        var roller = state.Broken * (sn > 0f ? ew : MathF.Max(ew, RollerTrail * MathF.Exp(sn / MathF.Max(RollerTail * state.Height, RollerTailMin))));
        return MathF.Max(MathF.Max(throwing, spilling), roller) * window * SmoothStep(0f, WhiteDryDepth, here.Depth);
    }

    /// <summary>
    /// The surf state of the crest at (x, z) from the carrier there (ocean_common.glsl crestStateAt): curl, the lip's
    /// fall (a plunging lip falls with the collapse, a spilling one only as the water runs out), wave height H (capped,
    /// fading as the water runs out) and the crest lift over the heightfield. Zero without a carrier.
    /// </summary>
    public static (float Curl, float Broken, float Height, float Amplitude) CrestStateAt(OceanSwell swell, BreakerSet? breaking, float x, float z)
    {
        if (!swell.SurfCarrier(x, z, out var c))
            return default;

        var k = MathF.Max(MathF.Sqrt(c.Kx * c.Kx + c.Kz * c.Kz), 1e-6f);
        var breakDistance = breaking?.BreakDistance(x, z) ?? 0f;
        var plunge = Plunge(swell, new Vector2(x, z), new Vector2(c.Kx, c.Kz) / k, c.Depth, breakDistance, c.Unbroken, false);
        var state = CrestState(c, breakDistance, MathF.Tau / k, plunge);
        var cap = c.Unbroken > 1e-4f ? Math.Clamp(OceanSwell.WaveBreakingGamma * MathF.Max(c.Depth, 0f) / c.Unbroken, 0f, 1f) : 1f;
        return (state.Curl, float.Lerp(BreakerFaces.Smooth((SpentCap - cap) / 0.2f), state.Broken, plunge), state.Height, state.Amplitude);
    }

    /// <summary>
    /// Breaker type for the wave at a crest point travelling along <paramref name="n"/> (ocean_common.glsl breakerPlunge):
    /// 0 spilling to 1 plunging, from ξ = tan β / √(H / L₀) where it began to break (<paramref name="breakDistance"/> back
    /// along n), with the bed slope over <see cref="SlopeReach"/> depths either side.
    /// </summary>
    /// <summary>
    /// The rest distance ahead of the crest at (x, z) at which the lip's landing is sampled (ocean_common.glsl
    /// lipRestDistance): down the wall's full rest span (<see cref="PitchFront"/> of a wavelength, squeezed by the pitch
    /// to 1 − Pitch × steep of that in the world); <see cref="BreakerLip"/> then pulls it back along the chord where the
    /// face is gentle. Zero without a carrier.
    /// </summary>
    public static float LipRestDistanceAt(OceanSwell swell, BreakerSet? breaking, float x, float z)
    {
        if (!swell.SurfCarrier(x, z, out var c))
            return 0f;

        var k = MathF.Max(MathF.Sqrt(c.Kx * c.Kx + c.Kz * c.Kz), 1e-6f);
        var wavelength = MathF.Tau / k;
        var breakDistance = breaking?.BreakDistance(x, z) ?? 0f;
        var plunge = Plunge(swell, new Vector2(x, z), new Vector2(c.Kx, c.Kz) / k, c.Depth, breakDistance, c.Unbroken, false);
        return BreakerLip.FootShare * PitchFront * wavelength;
    }

    public static float PlungeAt(OceanSwell swell, Vector2 crest, Vector2 n, float depth, float breakDistance, float unbroken) =>
        Plunge(swell, crest, n, depth, breakDistance, unbroken, false);

    private static float Plunge(OceanSwell swell, Vector2 crest, Vector2 n, float depth, float breakDistance, float unbroken, bool previous)
    {
        var onset = crest - n * MathF.Min(breakDistance, OnsetBackMax);
        var span = Math.Clamp(SlopeReach * MathF.Max(depth, 0f), SlopeMinSpan, SlopeMaxSpan);
        var deep = swell.CarrierDepth(onset.X - n.X * span, onset.Y - n.Y * span, previous);
        var shallow = swell.CarrierDepth(onset.X + n.X * span, onset.Y + n.Y * span, previous);
        var tanBeta = MathF.Max(deep - shallow, 0f) / (2f * span);
        var omega = swell.PeakOmega;
        var l0 = MathF.Tau * 9.81f / MathF.Max(omega * omega, 1e-6f);
        var xi = tanBeta / MathF.Sqrt(MathF.Max(unbroken, 0.05f) / l0);
        return SmoothStep(PlungeFrom, PlungeFull, xi);
    }

    /// <summary>
    /// The break has memory: by <see cref="BreakCrash"/> wavelengths of travel since it began, the lip has landed
    /// (broken) and the curl eased to a quarter, however the depth runs after. The collapse is that memory alone (tying
    /// it to the cap collapsed every crest the moment it reached a bar top); the cap only says when the water has run
    /// out under the crest (<see cref="SpentCap"/>). Curl (lips, throwing) only where it plunges; Steep drives the peak
    /// and pitch: the curl while it throws, the pre-break stand-up from H / h, and the rolling front a broken wave keeps.
    /// Spill is a spilling crest's whitewater once breaking has begun.
    /// </summary>
    private static (float Curl, float Broken, float Height, float Amplitude, float Steep, float Spill) CrestState(in SurfCarrierPoint c, float breakDistance, float wavelength, float plunge)
    {
        var depth = MathF.Max(c.Depth, 0f);
        var cap = c.Unbroken > 1e-4f ? Math.Clamp(OceanSwell.WaveBreakingGamma * depth / c.Unbroken, 0f, 1f) : 1f;
        var onset = Onset(breakDistance, wavelength);
        var landed = Landed(breakDistance, wavelength);
        var collapse = Collapse(breakDistance, wavelength);
        var progress = Travel(breakDistance, wavelength) / (BreakCrash * wavelength);
        var height = c.Unbroken * HeldCap(cap, depth, c.Unbroken, BreakHold(breakDistance, wavelength));
        var spent = BreakerFaces.Smooth((SpentCap - cap) / 0.2f);
        var curl = onset * float.Lerp(SpillTip * (1f - spent), 1f - 0.75f * MathF.Max(spent, SmoothStep(0.6f, 1f, progress)), plunge);
        var broken = MathF.Max(spent, landed);
        var stand = SmoothStep(StandFrom, OceanSwell.WaveBreakingGamma, c.Unbroken / MathF.Max(depth, 0.1f));
        var steep = MathF.Max(MathF.Max(curl, BreakSteep * onset) * (1f - 0.5f * MathF.Max(spent, collapse)), StandAmount * stand);
        var spill = (1f - plunge) * onset;
        return (curl, broken, height * BreakerFaces.Water(c.Depth), BreakerFaces.Amplitude(height, cap, onset, collapse, broken, c.Depth), steep, spill);
    }

    /// <summary>The face standing up over the first <see cref="OnsetTravel"/> wavelengths after the break clock starts (ocean_common.glsl breakOnset).</summary>
    public static float Onset(float breakDistance, float wavelength) => SmoothStep(0f, OnsetTravel * wavelength, breakDistance);

    /// <summary>Travel past the onset: what the lip's life and the collapse run on (breakTravel).</summary>
    public static float Travel(float breakDistance, float wavelength) => MathF.Max(breakDistance - OnsetTravel * wavelength, 0f);

    /// <summary>The lip has landed and the crest collapses into the roller, over the second half of <see cref="BreakCrash"/> (breakLanded).</summary>
    public static float Landed(float breakDistance, float wavelength) => SmoothStep(0.5f, 1f, Travel(breakDistance, wavelength) / (BreakCrash * wavelength));

    /// <summary>The crest's collapse: once the lip has landed, the held height, lift and wall ease away over <see cref="CollapseTravel"/> (breakCollapse).</summary>
    public static float Collapse(float breakDistance, float wavelength) => SmoothStep(LandedTravel, LandedTravel + CollapseTravel, Travel(breakDistance, wavelength) / wavelength);

    /// <summary>A landed tongue closing onto the face over <see cref="LipCloseTravel"/> (lipClose): its throw and arc fall back to the wall.</summary>
    public static float Close(float breakDistance, float wavelength) => SmoothStep(LandedTravel, LandedTravel + LipCloseTravel, Travel(breakDistance, wavelength) / wavelength);

    /// <summary>
    /// The breaking cap with the break's memory (ocean_common.glsl heldCap): once the face has stood up the height is held
    /// at its onset value (the cap the clock starts from, at most <see cref="HoldGamma"/> × the depth) until the lip has
    /// landed, then falls to the depth's own cap, so the tube keeps its size while the lip is in the air and the wave
    /// decays after it lands, rather than shrinking with the bed under it as it throws.
    /// </summary>
    public static float HeldCap(float cap, float depth, float unbroken, float hold)
    {
        var held = MathF.Min(BreakerSet.ClockStartCap, HoldGamma * MathF.Max(depth, 0f) / MathF.Max(unbroken, 1e-4f));
        return MathF.Max(cap, held * hold);
    }

    /// <summary>How much of the breaking height is held at this point of the break clock: once the face is up, until the lip has landed (breakHold).</summary>
    public static float BreakHold(float breakDistance, float wavelength) => SmoothStep(0f, HoldRamp * wavelength, breakDistance) * (1f - Collapse(breakDistance, wavelength));

    /// <summary>Over a wavelength (u = 0 on the crest behind .. 1 on the crest ahead), how much of the drawn carrier's hold is the crest ahead's: the hand-over sits around the trough (holdBlend).</summary>
    public static float HoldBlend(float u) => SmoothStep(0.4f, 0.6f, u);

    /// <summary>Phase wrapped to [-π, π): 0 on the nearest crest.</summary>
    private static float Wrap(float phase) => phase - MathF.Tau * MathF.Floor(phase / MathF.Tau + 0.5f);

    private static float SoftPlus(float x) => MathF.Max(x, 0f) + MathF.Log(1f + MathF.Exp(-MathF.Abs(x)));

    /// <summary>0 below x = 0, x up to 1, 1 above, its corner at 0 rounded over about 1 / k0 and at 1 over 1 / k1.</summary>
    private static float SoftRamp(float x, float k0, float k1, out float slope)
    {
        slope = 1f / (1f + MathF.Exp(-k0 * x)) - 1f / (1f + MathF.Exp(-k1 * (x - 1f)));
        return SoftPlus(k0 * x) / k0 - SoftPlus(k1 * (x - 1f)) / k1;
    }

    private static float SmoothStep(float edge0, float edge1, float x) => BreakerFaces.Smooth((x - edge0) / (edge1 - edge0));
}
