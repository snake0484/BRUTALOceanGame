using System.Numerics;
using System.Runtime.InteropServices;

namespace BrutalGame.Water.Ocean;

/// <summary>One crest segment for the ocean shaders (std430, 80 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct BreakerSegment
{
    /// <summary>(x0, z0, x1, z1) world.</summary>
    public Vector4 Ends;

    /// <summary>
    /// (lip base slot of end 0, of end 1, breaking cap at 0, at 1): where ocean_lip_base.comp writes each end's base, a
    /// slot shared with the neighbouring segment; the caps let the renderer list only breaking segments for the fold's patch.
    /// </summary>
    public Vector4 Slots;

    /// <summary>(shoreward normal x, z, celerity m/s, local wavelength m).</summary>
    public Vector4 Direction;

    /// <summary>
    /// Shoreward normal at each end (n0x, n0z, n1x, n1z): the carrier's direction of travel where the crest crosses the
    /// grid edge, the same for both segments meeting there, so neighbouring lip ribbons share their edge.
    /// </summary>
    public Vector4 Normals;

    /// <summary>
    /// (wave height at 0, at 1, end taper at 0, at 1). Height: capped H, tapered as the water runs out (CPU only). Taper:
    /// 1, or with <see cref="BreakerFaces.UseChains"/> 0 at a chain's ends rising to 1 over its first and last metres.
    /// </summary>
    public Vector4 Heights;
}

/// <summary>
/// The segments the breaker lips are swept along (docs/breaking-waves.md, rendering step 4): <see cref="BreakerSet"/>'s
/// marching-squares crossings of the carrier crest on the coastal grid, within <see cref="LipDistance"/> of the focus.
/// The lips take only their seeds and connectivity from here; each end is seated on the carrier crest and takes its
/// surf state from the carrier on the GPU (ocean_lip_base.comp), as the face does (<see cref="BreakerFace"/>). Raw
/// crossings, not the chained crest polylines: a chain that splits at a hole in the surf mask, is resampled as its
/// length changes or falls off the chain limit changes which segments exist and where its end tapers sit, with no change
/// in the wave; <see cref="UseChains"/> keeps the old seeds for comparison. Each crossed edge gets one lip base slot,
/// shared by the two segments meeting there. Rebuilt each tick; allocation-free.
/// </summary>
public sealed class BreakerFaces
{
    public const int MaxSegments = 4096;
    public const int MaxSlots = 2 * MaxSegments;

    /// <summary>Lips (and their spray) are built and drawn within this of the camera (ocean_common.glsl LIP_DISTANCE).</summary>
    public const float LipDistance = 250f;

    /// <summary>Kept beyond the range: half a segment, the render-time advance and the seat's reach.</summary>
    private const float RangeMargin = 40f;

    /// <summary>Extra crest height a breaking wave adds above the heightfield, as a fraction of the local (capped) height.</summary>
    private const float CrestAmplitude = 0.2f;

    private const float EndTaper = 12f;
    private const float SpentDepth = 0.3f;

    /// <summary>
    /// A broken wave's lift over the still water as a fraction of its breaking height γ h, kept down to
    /// <see cref="RunupDepth"/> and fading out over the last <see cref="BoreFadeDepth"/> of water: at the shoreline the
    /// bore hands over to the swash (ocean_common.glsl), which runs up the beach and back.
    /// </summary>
    private const float BoreLift = 0.35f;
    private const float RunupDepth = 0.6f;
    private const float BoreFadeDepth = 0.4f;

    public readonly BreakerSegment[] Segments = new BreakerSegment[MaxSegments];

    /// <summary>Per lip base slot, the segment and end (segment &lt;&lt; 1 | end) whose point it is, for ocean_lip_base.comp.</summary>
    public readonly uint[] SlotRefs = new uint[MaxSlots];

    private readonly int[] _slotOfEdge = new int[BreakerSet.MaxEdges];
    private readonly int[] _slotStamp = new int[BreakerSet.MaxEdges];
    private int _stamp;

    public int SegmentCount { get; private set; }

    public int SlotCount { get; private set; }

    /// <summary>Simulation time the segments were built for; the shader advances them to render time.</summary>
    public double Time { get; private set; }

    /// <summary>Off: no segments are sent, so no lips are drawn (A/B comparisons; the face has its own switch).</summary>
    public bool Enabled = true;

    /// <summary>Seeds from the chained crest polylines (end tapers, neighbour-averaged normals) instead of the raw crossings: the old seeding, for A/B comparison.</summary>
    public bool UseChains;

    /// <summary>The crests these were built from, whose break distances the shaders also read.</summary>
    public BreakerSet? Breaking { get; private set; }

    /// <summary>Segments within <paramref name="range"/> of <paramref name="focus"/> (the camera); every segment by default.</summary>
    public void Build(BreakerSet breakers, OceanSwell swell, Vector2 focus = default, float range = float.PositiveInfinity)
    {
        SegmentCount = 0;
        SlotCount = 0;
        _stamp++;
        Breaking = breakers;
        Time = swell.Time;

        var omega = swell.PeakOmega;
        if (!Enabled || swell.Coastal is null || omega <= 0f)
            return;

        var reach = range + RangeMargin;

        if (UseChains)
            BuildFromChains(breakers, omega, focus, reach * reach);
        else
            BuildFromCrossings(breakers, omega, focus, reach * reach);
    }

    private void BuildFromCrossings(BreakerSet breakers, float omega, Vector2 focus, float reach2)
    {
        for (var s = 0; s < breakers.RawSegmentCount && SegmentCount < MaxSegments; s++)
        {
            var (e0, e1) = breakers.RawSegment(s);
            var p = breakers.EdgeVertex(e0);
            var q = breakers.EdgeVertex(e1);

            if (Vector2.DistanceSquared(0.5f * (p.Position + q.Position), focus) > reach2)
                continue;

            Add(p, q, breakers.EdgeNormal(e0), breakers.EdgeNormal(e1), 1f, 1f, breakers.EdgeIndex(e0), breakers.EdgeIndex(e1), omega);
        }
    }

    private void BuildFromChains(BreakerSet breakers, float omega, Vector2 focus, float reach2)
    {
        for (var i = 0; i < breakers.Count; i++)
        {
            var b = breakers[i];
            var along = 0f;
            var total = 0f;

            for (var v = 1; v < b.Count; v++)
                total += Vector2.Distance(b.Vertices[v - 1].Position, b.Vertices[v].Position);

            for (var v = 1; v < b.Count && SegmentCount < MaxSegments; v++)
            {
                ref readonly var p = ref b.Vertices[v - 1];
                ref readonly var q = ref b.Vertices[v];
                var length = Vector2.Distance(p.Position, q.Position);

                if (Vector2.DistanceSquared(0.5f * (p.Position + q.Position), focus) <= reach2)
                    Add(p, q, VertexNormal(b, v - 1), VertexNormal(b, v), Taper(along, total), Taper(along + length, total), breakers.EdgeIndex(b.Edges[v - 1]), breakers.EdgeIndex(b.Edges[v]), omega);

                along += length;
            }
        }
    }

    private void Add(in BreakerVertex p, in BreakerVertex q, Vector2 n0, Vector2 n1, float taper0, float taper1, int edge0, int edge1, float omega)
    {
        var length = Vector2.Distance(p.Position, q.Position);
        if (length < 1e-3f)
            return;

        var tangent = (q.Position - p.Position) / length;
        var normal = new Vector2(-tangent.Y, tangent.X);
        if (Vector2.Dot(normal, p.Travel + q.Travel) < 0f)
            normal = -normal;

        var depth = MathF.Max(0.5f * (p.Depth + q.Depth), SpentDepth);
        var k = Jonswap.WaveNumberApprox(omega, depth);

        Segments[SegmentCount] = new BreakerSegment
        {
            Ends = new Vector4(p.Position, q.Position.X, q.Position.Y),
            Slots = new Vector4(Slot(edge0, 0), Slot(edge1, 1), p.Cap, q.Cap),
            Direction = new Vector4(normal, omega / k, 2f * MathF.PI / k),
            Normals = new Vector4(n0, n1.X, n1.Y),
            Heights = new Vector4(p.Height * Water(p.Depth) * taper0, q.Height * Water(q.Depth) * taper1, taper0, taper1),
        };

        SegmentCount++;
    }

    /// <summary>The lip base slot of a crossed edge this tick, claimed for the segment being added when first met.</summary>
    private int Slot(int edge, int end)
    {
        if (_slotStamp[edge] != _stamp)
        {
            _slotStamp[edge] = _stamp;
            _slotOfEdge[edge] = SlotCount;
            SlotRefs[SlotCount++] = (uint)(SegmentCount << 1 | end);
        }

        return _slotOfEdge[edge];
    }

    /// <summary>
    /// The extra crest a breaker adds on top of the heightfield (capped height H, breaking cap, how far it has collapsed,
    /// water depth), which already carries the unbroken wave: zero while the cap is 1, growing as breaking sets in (full
    /// by cap 0.6), falling away in the bore and as the water runs out. Mirrored in ocean_common.glsl (crestState).
    /// </summary>
    internal static float Amplitude(float height, float cap, float onset, float collapse, float broken, float depth)
    {
        var bore = Math.Clamp((cap - 0.3f) / 0.3f, 0.4f, 1f);
        var swash = BoreLift * CoastalField.BreakingGamma * MathF.Max(depth, RunupDepth) * broken * Smooth(depth / BoreFadeDepth);
        return MathF.Max(CrestAmplitude * height * onset * (1f - collapse) * bore * Water(depth), swash);
    }

    /// <summary>0 while the crest stands, rising to 1 as the cap falls from 0.85 to 0.55: the crest has collapsed into a roller.</summary>
    internal static float Broken(float cap) => Smooth((0.85f - cap) / 0.3f);

    /// <summary>1 in water, fading to 0 as it runs out over the last <see cref="SpentDepth"/>.</summary>
    internal static float Water(float depth) => Smooth((depth - SpentDepth) / SpentDepth);

    /// <summary>Shoreward normal at a chain vertex: across the crest, the way the crest itself travels there.</summary>
    private static Vector2 VertexNormal(Breaker b, int v)
    {
        var tangent = b.Vertices[Math.Min(v + 1, b.Count - 1)].Position - b.Vertices[Math.Max(v - 1, 0)].Position;
        var normal = Vector2.Normalize(new Vector2(-tangent.Y, tangent.X) + new Vector2(1e-6f, 0f));
        return Vector2.Dot(normal, b.Vertices[v].Travel) < 0f ? -normal : normal;
    }

    private static float Taper(float along, float total) => Smooth(along / EndTaper) * Smooth((total - along) / EndTaper);

    internal static float Smooth(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
