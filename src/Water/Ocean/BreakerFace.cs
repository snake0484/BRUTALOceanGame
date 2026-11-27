using BrutalGame.Water.Coast;

namespace BrutalGame.Water.Ocean;

/// <summary>What the breaker face adds to the sea at a rest position, in the coast window's plane (x east, z south).</summary>
public struct FacePoint
{
    public float Height;
    public float SlopeX;
    public float SlopeZ;

    /// <summary>Horizontal shift of the rest position and its partials (∂Dx/∂x, ∂Dz/∂z, ∂Dx/∂z).</summary>
    public float Dx;
    public float Dz;
    public float DxX;
    public float DzZ;
    public float DxZ;

    /// <summary>Whitewater weight, 0..1.</summary>
    public float Foam;
}

/// <summary>The surf state of a crest (<see cref="BreakerFace.CrestState"/>).</summary>
public struct CrestState
{
    public float Curl;
    public float Broken;

    /// <summary>Wave height crest to trough, capped by breaking and fading as the water runs out; and the crest's lift over the heightfield.</summary>
    public float Height;
    public float Amplitude;
    public float Steep;
    public float Spill;
}

/// <summary>
/// The breaking face (breaking-waves-brief.md), mirrored in ocean_breaker.glsl breakerFace: keep the two in step. A pure
/// function of one coast map and its phase at the time: the surf carrier drawn as a wave (its bound harmonics peak it as
/// it shoals, its height held by the break's clock), and on the nearest crest (where the carrier's phase is 0, found by
/// two Newton steps along its direction) the crest lift, the peak and the pitch that draws the face back under the
/// crest, with their slope and Jacobian terms, and the whitewater. The signed distance from the crest, the wavelength
/// and the crest's state come from the map there, so the face is continuous in space and time. Visual only: it is not
/// in <see cref="OceanSurface.Sample"/>.
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

    /// <summary>The wall and the lift only move water that is there: they fade out over the last of the depth under each point.</summary>
    private const float PitchDryDepth = 0.6f;
    private const float LiftDryDepth = 0.3f;

    /// <summary>The peak comes on between these crest amplitudes (m), so a wave too small to show has none.</summary>
    private const float PeakFrom = 0.05f;
    private const float PeakFull = 0.15f;

    /// <summary>
    /// Whitewater envelope in metres from the wave's height: the front's Gaussian width ahead of the crest × the height,
    /// between <see cref="WhiteFrontMin"/> and <see cref="WhiteFrontMax"/>; <see cref="WhiteBack"/> × that behind it; it
    /// fades out over the last <see cref="WhiteDryDepth"/> of water, where the swash's front takes over.
    /// </summary>
    private const float WhiteFront = 1.5f;
    private const float WhiteFrontMin = 0.8f;
    private const float WhiteFrontMax = 4f;
    private const float WhiteBack = 1.6f;
    private const float WhiteDryDepth = 0.15f;

    /// <summary>Whitewater trailing behind a broken crest: its strength and e-folding length × the wave height (m), at least <see cref="RollerTailMin"/>.</summary>
    private const float RollerTrail = 0.25f;
    private const float RollerTail = 2f;
    private const float RollerTailMin = 1.5f;

    /// <summary>Wavelengths a breaking section travels from the start of its break until its crest has crashed.</summary>
    private const float BreakCrash = 0.35f;

    /// <summary>Wavelengths of travel after the clock starts over which the face stands up; the collapse runs on the travel past that.</summary>
    public const float OnsetTravel = 0.15f;

    /// <summary>While the face is up the breaking height is held at its onset value, at most this × the depth.</summary>
    public const float HoldGamma = 1.5f;
    public const float HoldRamp = 0.03f;

    /// <summary>Wavelengths past the onset at which the crest has crashed, and the travel past that over which its held height, lift and wall ease away.</summary>
    public const float LandedTravel = 0.245f;
    public const float CollapseTravel = 1f;

    /// <summary>Below this breaking cap the water has run out under the crest: broken whatever its travel.</summary>
    private const float SpentCap = 0.45f;

    /// <summary>Shoaling stands the wave up before it breaks: from H / h = <see cref="StandFrom"/> to breaking the face steepens to <see cref="StandAmount"/>.</summary>
    private const float StandFrom = 0.45f;
    private const float StandAmount = 0.55f;
    private const float BreakSteep = 0.85f;
    private const float ThrowFoamHeight = 0.6f;

    /// <summary>A throwing crest's whitewater comes on over this share of its width behind the crest: it stepped at the crest line, a pop at every point each crest passed.</summary>
    private const float ThrowBlend = 0.5f;
    private const float SpillTip = 0.5f;

    /// <summary>Extra crest height a breaking wave adds, as a share of its capped height; a broken wave's lift as a share of γ h.</summary>
    private const float CrestAmplitude = 0.2f;
    private const float BoreLift = 0.35f;
    private const float BoreGamma = 0.55f;
    private const float RunupDepth = 0.6f;
    private const float BoreFadeDepth = 0.4f;
    private const float SpentDepth = 0.3f;

    /// <summary>The carrier is drawn from this height (m): below it there is nothing to see.</summary>
    private const float MinHeight = 1e-3f;

    /// <summary>
    /// On a grid of a given spacing (the GPU's clipmap): the face fades out between these spacings (m) whatever its
    /// wavelength, the carrier where its wavelength is under 2 to 4 cells, and what rides the crest is at least
    /// <see cref="MinCells"/> cells wide.
    /// </summary>
    public const float FadeSpacing = 16f;
    public const float MaxSpacing = 32f;
    private const float MinCells = 1.5f;

    /// <summary>
    /// The face at local (x, z) metres of <paramref name="atlas"/>, whose phase at the time is <paramref name="phase0"/>
    /// (<see cref="CoastAtlas.PhaseAt"/>). Unweighted: the caller fades it by <see cref="CoastWindow.Weight"/>.
    /// </summary>
    /// <param name="spacing">The spacing (m) of the grid it is drawn on, as the GPU filters it (<see cref="Resolved"/>); 0 for the face itself.</param>
    public static void Apply(CoastAtlas atlas, float x, float z, float phase0, out FacePoint p, float spacing = 0f)
    {
        p = default;
        var here = SurfCarrier.At(atlas, x, z, phase0);
        var resolved = Resolved(MathF.Tau / here.K, spacing);
        var narrowest = MinCells * spacing;

        if (here.Unbroken < MinHeight || resolved <= 0f)
            return;

        // The carrier at this point: bound harmonics (1, s, s²), all in phase at the crest.
        var depth = MathF.Max(here.Depth, 0f);
        var cap = Math.Clamp(SurfCarrier.BreakingGamma * depth / here.Unbroken, 0f, 1f);
        var height = here.Unbroken * HeldCap(cap, depth, here.Unbroken, BreakHold(here.BreakDistance, MathF.Tau / here.K));
        var sharp = SurfCarrier.Sharpness(height, depth);
        var norm = 0.5f * height / MathF.Sqrt(1f + sharp * sharp * (1f + sharp * sharp));
        var (sin, cos) = MathF.SinCos(here.Phase);
        var sin2 = 2f * sin * cos;
        var cos2 = 2f * cos * cos - 1f;
        var sin3 = sin * (3f - 4f * sin * sin);
        var cos3 = cos * (4f * cos * cos - 3f);
        var eta = norm * (cos + sharp * (cos2 + sharp * cos3));
        var slope = -norm * here.K * (sin + sharp * (2f * sin2 + sharp * 3f * sin3));
        var slopeX = slope * here.DirX;
        var slopeZ = slope * here.DirZ;

        // Two Newton steps to the crest: the second takes the first's error from centimetres to under a millimetre. The
        // phase along the way is taken against this point's crest: wrapped afresh, a step that overshot where the water
        // shoals fast would land on the next crest and the face would jump.
        var step = here.Phase / here.K;
        var gx = x - here.DirX * step;
        var gz = z - here.DirZ * step;
        var crest = SurfCarrier.At(atlas, gx, gz, phase0, here.Turns);
        step = crest.Phase / crest.K;
        gx -= crest.DirX * step;
        gz -= crest.DirZ * step;
        crest = SurfCarrier.At(atlas, gx, gz, phase0, here.Turns);

        var sn = (x - gx) * crest.DirX + (z - gz) * crest.DirZ + crest.Phase / crest.K;
        var wavelength = MathF.Tau / crest.K;
        var fadeEnd = MathF.Min(PitchFade * wavelength, PitchMax);

        // The window runs on the phase here: it hands over to the next crest at exactly ±π, past fadeEnd.
        var reach = MathF.Abs(here.Phase) / crest.K;
        var state = CrestState(crest, wavelength);

        if (reach < fadeEnd && (state.Steep > 0f || state.Amplitude >= 1e-3f))
        {
            var fadeStart = PitchReach / PitchFade * fadeEnd;
            var tw = Math.Clamp((reach - fadeStart) / (fadeEnd - fadeStart), 0f, 1f);
            var window = 1f - tw * tw * (3f - 2f * tw);
            var dwindow = -MathF.Sign(here.Phase) * 6f * tw * (1f - tw) / (fadeEnd - fadeStart) * here.K / crest.K;

            var amount = state.Steep;
            var width = MathF.Max(sn < 0f ? BackWidth * wavelength : MathF.Max(float.Lerp(BackWidth, FrontWidth, amount) * wavelength, MinFront), narrowest);
            var e = MathF.Exp(-(sn * sn) / (width * width));
            var wet = SmoothStep(0f, LiftDryDepth, here.Depth);
            var lift = state.Amplitude * e * window * wet;
            var dlift = state.Amplitude * e * (dwindow - 2f * sn / (width * width) * window) * wet;

            // Peak: the carrier's height here above its trough, as a share of its crest-to-trough span, raised to gamma.
            var a = 0.5f * height;
            var peaking = amount * SmoothStep(PeakFrom, PeakFull, a);

            if (peaking > 0f)
            {
                var w = peaking * window;
                var dw = peaking * dwindow;
                var gamma = float.Lerp(1f, Peak, w);
                var troughAmp = norm * (1f - sharp + sharp * sharp);
                var span = norm * (1f + sharp + sharp * sharp) + troughAmp;
                var share = (eta + troughAmp) / span;
                var peaked = share <= 0f ? share : share < 1f ? MathF.Pow(share, gamma) : 1f + gamma * (share - 1f);
                var dShare = share <= 0f ? 1f : share < 1f ? gamma * MathF.Pow(share, gamma - 1f) : gamma;
                var dGamma = share <= 0f ? 0f : share < 1f ? peaked * MathF.Log(share) : share - 1f;
                var across = span * dGamma * (Peak - 1f) * dw;
                eta += span * (peaked - share);
                slopeX = slopeX * dShare + across * crest.DirX;
                slopeZ = slopeZ * dShare + across * crest.DirZ;
            }

            // Pitch: the face is drawn back under the crest, which stays put: the upper face is squeezed into a wall and
            // the trough ahead gives back what the wall drew in.
            var front = PitchFront * wavelength;
            var recover = PitchRecover * wavelength;
            var wall = SoftRamp(sn / front, PitchTop, PitchFoot, out var dWall);
            var back = SoftRamp((sn - front) / recover, PitchFoot, PitchEnd, out var dRecover);
            var pitch = Math.Clamp(1f - WallWidth * state.Height / front, Pitch, PitchSqueezeMax);
            var lean = pitch * front * amount * SmoothStep(0f, PitchDryDepth, here.Depth);
            var tent = -lean * (wall - back);
            var shift = tent * window;
            var dshift = -lean * (dWall / front - dRecover / recover) * window + tent * dwindow;

            eta += lift;
            slopeX += dlift * crest.DirX;
            slopeZ += dlift * crest.DirZ;
            p.Dx = shift * crest.DirX;
            p.Dz = shift * crest.DirZ;
            p.DxX = dshift * crest.DirX * crest.DirX;
            p.DzZ = dshift * crest.DirZ * crest.DirZ;
            p.DxZ = dshift * crest.DirX * crest.DirZ;

            // Whitewater: ahead of a throwing crest where it lands; a spilling crest's tumbling down its front; then,
            // once it has collapsed, a roller on the front trailing foam behind it.
            var whiteFront = Math.Clamp(WhiteFront * state.Height, WhiteFrontMin, WhiteFrontMax);
            var whiteWidth = MathF.Max(sn < 0f ? WhiteBack * whiteFront : whiteFront, narrowest);
            var ew = MathF.Exp(-(sn * sn) / (whiteWidth * whiteWidth));
            var thrown = float.Lerp(0.15f * ew * ew * ew, 0.6f * ew, SmoothStep(-ThrowBlend * whiteWidth, 0f, sn));
            var throwing = SmoothStep(0.6f, 1f, state.Curl) * thrown * SmoothStep(0f, ThrowFoamHeight, state.Height);
            var spilling = state.Spill * (sn > 0f ? ew : ew * ew);
            var roller = state.Broken * (sn > 0f ? ew : MathF.Max(ew, RollerTrail * MathF.Exp(sn / MathF.Max(RollerTail * state.Height, RollerTailMin))));
            p.Foam = MathF.Max(MathF.Max(throwing, spilling), roller) * window * SmoothStep(0f, WhiteDryDepth, here.Depth);
        }

        p.Height = eta * resolved;
        p.SlopeX = slopeX * resolved;
        p.SlopeZ = slopeZ * resolved;
        p.Dx *= resolved;
        p.Dz *= resolved;
        p.DxX *= resolved;
        p.DzZ *= resolved;
        p.DxZ *= resolved;
        p.Foam *= resolved;
    }

    /// <summary>The share of a face of this wavelength a grid of this spacing draws (ocean_breaker.glsl breakerFace).</summary>
    public static float Resolved(float wavelength, float spacing) =>
        spacing <= 0f ? 1f : SmoothStep(2f * spacing, 4f * spacing, wavelength) * (1f - SmoothStep(FadeSpacing, MaxSpacing, spacing));

    /// <summary>
    /// The surf state of the crest at a carrier point. The break has memory: its clock (the map's break distance) stands
    /// the face up over <see cref="OnsetTravel"/>, crashes the crest by <see cref="BreakCrash"/> and collapses it over
    /// <see cref="CollapseTravel"/>, however the depth runs after; the cap only says when the water has run out
    /// (<see cref="SpentCap"/>). Steep drives the peak and the pitch: the curl while it throws, the stand-up before
    /// the break from H / h, and the rolling front a broken wave keeps.
    /// </summary>
    public static CrestState CrestState(in SurfCarrierPoint c, float wavelength)
    {
        var depth = MathF.Max(c.Depth, 0f);
        var cap = c.Unbroken > 1e-4f ? Math.Clamp(SurfCarrier.BreakingGamma * depth / c.Unbroken, 0f, 1f) : 1f;
        var distance = c.BreakDistance;
        var onset = Onset(distance, wavelength);
        var landed = Landed(distance, wavelength);
        var collapse = Collapse(distance, wavelength);
        var progress = Travel(distance, wavelength) / (BreakCrash * wavelength);
        var height = c.Unbroken * HeldCap(cap, depth, c.Unbroken, BreakHold(distance, wavelength));
        var spent = SurfCarrier.Smooth((SpentCap - cap) / 0.2f);
        var curl = onset * float.Lerp(SpillTip * (1f - spent), 1f - 0.75f * MathF.Max(spent, SmoothStep(0.6f, 1f, progress)), c.Plunge);
        var broken = MathF.Max(spent, landed);
        var stand = SmoothStep(StandFrom, SurfCarrier.BreakingGamma, c.Unbroken / MathF.Max(depth, 0.1f));

        // A crest followed ashore is let go over the first of the beach above the sea.
        var afloat = SurfCarrier.Smooth(c.Depth / SpentDepth + 1f);

        return new CrestState
        {
            Curl = curl * afloat,
            Broken = broken * afloat,
            Height = height * Water(c.Depth),
            Amplitude = Amplitude(height, cap, onset, collapse, broken, c.Depth),
            Steep = MathF.Max(MathF.Max(curl, BreakSteep * onset) * (1f - 0.5f * MathF.Max(spent, collapse)), StandAmount * stand) * afloat,
            Spill = (1f - c.Plunge) * onset * afloat,
        };
    }

    public static float Onset(float breakDistance, float wavelength) => SmoothStep(0f, OnsetTravel * wavelength, breakDistance);

    public static float Travel(float breakDistance, float wavelength) => MathF.Max(breakDistance - OnsetTravel * wavelength, 0f);

    public static float Landed(float breakDistance, float wavelength) => SmoothStep(0.5f, 1f, Travel(breakDistance, wavelength) / (BreakCrash * wavelength));

    public static float Collapse(float breakDistance, float wavelength) => SmoothStep(LandedTravel, LandedTravel + CollapseTravel, Travel(breakDistance, wavelength) / wavelength);

    /// <summary>
    /// The breaking cap with the break's memory: once the face has stood up the height is held at its onset value (the
    /// cap the clock starts from, at most <see cref="HoldGamma"/> × the depth) until the crest has crashed, then falls to
    /// the depth's own cap.
    /// </summary>
    public static float HeldCap(float cap, float depth, float unbroken, float hold)
    {
        var held = MathF.Min(CoastBake.ClockStartCap, HoldGamma * MathF.Max(depth, 0f) / MathF.Max(unbroken, 1e-4f));
        return MathF.Max(cap, held * hold);
    }

    public static float BreakHold(float breakDistance, float wavelength) => SmoothStep(0f, HoldRamp * wavelength, breakDistance) * (1f - Collapse(breakDistance, wavelength));

    private static float Amplitude(float height, float cap, float onset, float collapse, float broken, float depth)
    {
        var bore = Math.Clamp((cap - 0.3f) / 0.3f, 0.4f, 1f);
        var swash = BoreLift * BoreGamma * MathF.Max(depth, RunupDepth) * broken * SurfCarrier.Smooth(depth / BoreFadeDepth);
        return MathF.Max(CrestAmplitude * height * onset * (1f - collapse) * bore * Water(depth), swash);
    }

    private static float Water(float depth) => SurfCarrier.Smooth((depth - SpentDepth) / SpentDepth);

    private static float SoftPlus(float x) => MathF.Max(x, 0f) + MathF.Log(1f + MathF.Exp(-MathF.Abs(x)));

    /// <summary>0 below x = 0, x up to 1, 1 above, its corner at 0 rounded over about 1 / k0 and at 1 over 1 / k1.</summary>
    private static float SoftRamp(float x, float k0, float k1, out float slope)
    {
        slope = 1f / (1f + MathF.Exp(-k0 * x)) - 1f / (1f + MathF.Exp(-k1 * (x - 1f)));
        return SoftPlus(k0 * x) / k0 - SoftPlus(k1 * (x - 1f)) / k1;
    }

    private static float SmoothStep(float edge0, float edge1, float x) => SurfCarrier.Smooth((x - edge0) / (edge1 - edge0));
}
