using System.Numerics;

namespace BrutalGame.Water.Ocean;

/// <summary>The lip at a crest point: where its tongue starts, how far it has got, and where its tip is now.</summary>
public struct LipTip
{
    /// <summary>The crest point the tongue grows from (world), seated on the drawn crest.</summary>
    public Vector3 Crest;

    /// <summary>The landing it falls to: the drawn surface down the wall's rest span (<see cref="BreakerFace.LipRestDistanceAt"/>).</summary>
    public Vector3 Landing;

    /// <summary>The tongue's tip now (world).</summary>
    public Vector3 Tip;

    /// <summary>0 just begun .. 1 at the foot.</summary>
    public float Reach;

    /// <summary>0 while extending .. 1 crashed into whitewater.</summary>
    public float Crash;
    public float Curl;
    public float Plunge;
    public float Height;
}

/// <summary>
/// The breaker lip's geometry on the CPU (ocean_lip_base.comp, ocean_lip.vert, lipPath/lipReach in ocean_common.glsl):
/// keep the two in step. The tongue grows out of the crest top, arcs over the face and falls to its foot, its reach
/// driven by the wave's travel since its break began; for tests that measure it, and later for surf queries.
/// </summary>
public static class BreakerLip
{
    public const float FootShare = 0.95f;
    public const float MaxThrow = 1.2f;
    public const float ThrowSpeed = 0.7f;
    public const float ThrownMax = 1.5f;
    /// <summary>The barrel arc (lipPath): a plunging lip's path is an ellipse, the crest ArcStart (35°) before its top so the lip leaves rising at that angle, the landing ArcEnd (115°) past it so the tip points down and back into the tube.</summary>
    private const float ArcStart = 0.61087f;
    private const float ArcEnd = 2.00713f;
    private const float ArcSin0 = 0.57358f;
    private const float ArcCos0 = 0.81915f;
    private const float ArcSin1 = 0.90631f;
    private const float ArcCos1 = -0.42262f;
    private const float ReachTravel = 0.5f;
    private const float CrashTravel = 0.2f;
    private const float BreakCrash = 0.35f;
    public const float MinReach = 0.03f;

    /// <summary>
    /// The tongue's life from the wave's travel since its break began (ocean_common.glsl lipLife): reach (0 crest top
    /// .. 1 the foot) over <see cref="ReachTravel"/> of the crash distance, then the crash over <see cref="CrashTravel"/>
    /// more. A plunging tongue does this once; a spilling one extends and holds, a steady tumble, falling only as the
    /// water runs out.
    /// </summary>
    public static (float Reach, float Crash) Life(float breakDistance, float wavelength, float plunge)
    {
        var travel = BreakerFace.Travel(breakDistance, wavelength);
        var extend = ReachTravel * BreakCrash * wavelength;
        var crash = CrashTravel * BreakCrash * wavelength;
        var r = Math.Clamp(travel / extend, 0f, 1f);
        return (r * r, plunge * BreakerFaces.Smooth((travel - extend) / crash));
    }

    /// <summary>A spilling tongue lands this far down the wall's rest span: the crest tumbling, not a throw.</summary>
    public const float SpillSpan = 0.5f;

    /// <summary>
    /// Diagnostic, off (the GPU does not do it): seat the root on the drawn surface's own crest rather than the
    /// carrier's, by a secant step on the slope at the carrier crest and <see cref="SeatProbe"/> wavelengths ahead, at
    /// most <see cref="SeatMaxStep"/> wavelengths. Measured (BreakerLipTests.Lip_RootSitsOnTheDrawnCrest) the carrier
    /// crest is already the drawn crest: the face's peak sharpens the surface around it. Kept so the measurement can
    /// be repeated if the sea's residual ever grows.
    /// </summary>
    internal static bool SeatOnDrawnCrest = false;
    private const float SeatProbe = 0.05f;
    private const float SeatMaxStep = 0.12f;

    /// <summary>The rest point of the drawn surface's crest nearest the carrier crest <paramref name="q"/> along <paramref name="n"/>.</summary>
    public static Vector2 SeatOnDrawn(OceanSurface surface, Vector2 q, Vector2 n, float wavelength)
    {
        var delta = SeatProbe * wavelength;
        var p0 = surface.SampleRest(q.X, q.Y);
        var p1 = surface.SampleRest(q.X + n.X * delta, q.Y + n.Y * delta);
        var s0 = p0.SlopeX * n.X + p0.SlopeZ * n.Y;
        var s1 = p1.SlopeX * n.X + p1.SlopeZ * n.Y;
        var curvature = (s1 - s0) / delta;
        if (curvature >= -1e-4f)
            return q;

        var max = SeatMaxStep * wavelength;
        return q + n * Math.Clamp(-s0 / curvature, -max, max);
    }

    /// <summary>The path at t (0 crest top .. 1 foot) in the crest frame (x shoreward, y up); see lipPath.</summary>
    public static Vector2 Path(float t, float throwDist, float drop, float plunge)
    {
        var p1 = new Vector2(0.3f * throwDist, 0.08f * drop);
        var p2 = new Vector2(0.75f * throwDist, -0.45f * drop);
        var p3 = new Vector2(throwDist, -drop);
        var v = 1f - t;
        var spill = 3f * v * v * t * p1 + 3f * v * t * t * p2 + t * t * t * p3;
        var ax = throwDist / (ArcSin0 + ArcSin1);
        var ay = drop / (ArcCos0 - ArcCos1);
        var theta = float.Lerp(-ArcStart, ArcEnd, t);
        var arc = new Vector2(ax * (ArcSin0 + MathF.Sin(theta)), ay * (MathF.Cos(theta) - ArcCos0));
        return Vector2.Lerp(spill, arc, plunge);
    }

    /// <summary>
    /// The lip of the crest nearest rest position (x, z), travelling along <paramref name="n"/> (unit, shoreward). False
    /// where there is no carrier or no lip (curl under 0.05 or no height).
    /// </summary>
    public static bool Sample(OceanSurface surface, BreakerSet breaking, float x, float z, Vector2 n, out LipTip tip)
    {
        tip = default;
        var swell = surface.Swell;
        var q = new Vector2(x, z);

        // Seat on the crest (seatOnCrest): two Newton steps along the wavevector to where the carrier's phase is 0.
        for (var i = 0; i < 2; i++)
        {
            if (!swell.SurfCarrier(q.X, q.Y, out var c))
                return false;

            var phase = c.Phase - MathF.Tau * MathF.Floor(c.Phase / MathF.Tau + 0.5f);
            q -= new Vector2(c.Kx, c.Kz) * (phase / (c.Kx * c.Kx + c.Kz * c.Kz));
        }

        if (!swell.SurfCarrier(q.X, q.Y, out var crest))
            return false;

        var k = MathF.Max(MathF.Sqrt(crest.Kx * crest.Kx + crest.Kz * crest.Kz), 1e-6f);
        var state = BreakerFace.CrestStateAt(swell, breaking, q.X, q.Y);
        if (state.Curl < 0.05f || state.Height < 0.05f)
            return false;

        var breakDistance = breaking.BreakDistance(q.X, q.Y);
        var plunge = BreakerFace.PlungeAt(swell, q, new Vector2(crest.Kx, crest.Kz) / k, crest.Depth, breakDistance, crest.Unbroken);
        var root = SeatOnDrawnCrest ? SeatOnDrawn(surface, q, n, MathF.Tau / k) : q;
        var land = root + n * (BreakerFace.LipRestDistanceAt(swell, breaking, q.X, q.Y) * float.Lerp(SpillSpan, 1f, plunge));
        var crestPoint = surface.SurfacePoint(root.X, root.Y);
        var landing = surface.SurfacePoint(land.X, land.Y);

        // Pulled back along the chord to the crest until it is no further ahead than MaxThrow x its drop (lipClampLanding).
        var throwDist = MathF.Max((landing.X - crestPoint.X) * n.X + (landing.Z - crestPoint.Z) * n.Y, 0.05f);
        var limit = MaxThrow * MathF.Max(crestPoint.Y - landing.Y, 0.05f);
        if (throwDist > limit)
        {
            landing = Brutal.Numerics.float3.Lerp(crestPoint, landing, limit / throwDist);
            throwDist = limit;
        }

        // Thrown (lipThrowLanding): a plunging lip's tip leaves the crest at ThrowSpeed x the celerity and falls its drop
        // under gravity, landing that far ahead (at most ThrownMax x the drop) however close the wall's foot is.
        // Once landed the tongue closes (lipClose): its throw falls back onto the face as the roof sinks onto the wall.
        var fall = MathF.Max(crestPoint.Y - landing.Y, 0.05f);
        var thrown = MathF.Min(ThrowSpeed * (swell.PeakOmega / k) * MathF.Sqrt(2f * fall / 9.81f), ThrownMax * fall) * plunge * (1f - BreakerFace.Close(breakDistance, MathF.Tau / k));
        if (throwDist < thrown)
        {
            landing = new Brutal.Numerics.float3(crestPoint.X + n.X * thrown, landing.Y, crestPoint.Z + n.Y * thrown);
            throwDist = thrown;
        }

        var drop = MathF.Max(crestPoint.Y - landing.Y, 0.02f * state.Height);
        var (reach, crash) = Life(breakDistance, MathF.Tau / k, plunge);
        crash = float.Lerp(state.Broken, crash, plunge);
        if (reach < MinReach)
            return false;

        var p = Path(reach, throwDist, drop, plunge);

        tip = new LipTip
        {
            Crest = new Vector3(crestPoint.X, crestPoint.Y, crestPoint.Z),
            Landing = new Vector3(landing.X, landing.Y, landing.Z),
            Tip = new Vector3(crestPoint.X + n.X * p.X, crestPoint.Y + p.Y, crestPoint.Z + n.Y * p.X),
            Reach = reach,
            Crash = crash,
            Curl = state.Curl,
            Plunge = plunge,
            Height = state.Height,
        };
        return true;
    }
}
