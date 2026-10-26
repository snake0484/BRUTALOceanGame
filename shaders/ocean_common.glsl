// Shared ocean surface evaluation (ocean.vert, ocean_lip.vert): frame block, maps, layer B coastal terms, breaker faces,
// and oceanAt(), the displaced surface at a rest position. Must match the CPU OceanSwell (see each section).
 

struct Wave { vec4 a; vec4 b; }; // a = (kx, kz, phase, amplitude), b = (cx, cz, omega, coastal ref coord)

layout(set = 0, binding = 0, std140) uniform Frame {
    mat4 viewProj;
    vec4 cameraPos;    // xyz world, w = time
    vec4 sunDir;       // xyz toward sun, w = intensity
    vec4 skyZenith;
    vec4 skyHorizon;   // w = fog density
    vec4 waterDeep;
    vec4 waterScatter;
    vec4 cascadeL;     // xyz = patch size L per cascade (m), w = cascade count
    vec4 cascadeKLo;   // xyz = band low |k| per cascade, w = persistent foam strength
    vec4 foamParams;   // x = rain 0..1, y = rain time (s, looped), z = debug view (1 = Jacobian/foam), w = sim tick alpha
    vec4 depthRect;    // xy = depth map world origin, z = world size, w = depth outside the map
    vec4 coastRect;    // layer B maps: xy = world origin, z = cell size, w = resolution (0 = none)
    vec4 refEnergy[3]; // swell variance per coastal reference frequency
    vec4 refChop[3];   // variance of the Gerstner trace, sum 1/2 (|c| k)^2, per reference
    vec4 refK0[3];     // deep-water k per reference
    vec4 coastDir;     // xy = direction the phase maps were solved for, z = crossfade weight of the current field,
                       // w = direction (radians) the fading-out field was solved for
    vec4 coastPrev;    // fading-out field: xy = world origin, z = cell size, w = resolution (0 = none)
    vec4 carrier;      // surf coherence (OceanSwell.GpuCarrier): k0, reference coord, phase, peak-band energy sum a^2
    vec4 carrierChop;  // x = choppiness, y = omega, z = set group phase, w = group ratio (a set every 1 / this waves)
    vec4 windSea;      // x = wind speed the cascades' spectrum is raised by (m/s), yzw unused
    ivec4 counts;      // x = wave count, y..z = coastal reference range in use, w = buffer slots (bits 0-1 current, 2-3 previous)
    Wave waves[512];
} frame;

layout(set = 0, binding = 1) uniform sampler2DArray dispMap;  // (λDx, h, λDz, λDxz)
layout(set = 0, binding = 2) uniform sampler2DArray derivMap; // (hx, hz, λDxx, λDzz)
layout(set = 0, binding = 3) uniform sampler2DArray foamMap;  // r = persistent foam
layout(set = 0, binding = 4) uniform sampler2DArray depthMap; // see depthMapAt

// Layer B coastal maps (CoastalField): 7 vec4 per 4 m texel = (depth, active, 0, 0), then (lag, amp, lag, amp) x 6.
// Three buffers shared by all frames (OceanRenderer.Slot); counts.w says which holds the current field and which, while
// a rebuild crossfades in, the one it replaces.
layout(std430, set = 0, binding = 5) readonly buffer Coastal { vec4 texels[]; } coastal[3];

// Carrier atlas (ocean_carrier_atlas.comp): per tap (0 = current field, 1 = fading out), layer 2 tap = (the surf carrier's
// lag, water depth, shoal amplitude, active flag) per coastal texel and layer 2 tap + 1 = (d lag / dx, d lag / dz, by
// central differences a cell apart). What surfCarrierAt, carrierDirection and carrierDepthAt read, bilinear by hand
// from four texels as the CPU does (OceanSwell.SurfCarrier), instead of the 7-vec4 coastal texels: the reference mix,
// the gradient and the four lag lookups are done once per texel when a field or the carrier changes. Read-only except
// in that compute shader.
#ifdef CARRIER_ATLAS_WRITABLE
#define CARRIER_ATLAS_ACCESS
#else
#define CARRIER_ATLAS_ACCESS readonly
#endif

layout(set = 0, binding = 10, rgba32f) CARRIER_ATLAS_ACCESS uniform image2DArray carrierAtlas;

#define RAIN_COVER_SET 0
#define RAIN_COVER_BINDING 11
#include "rain_cover.glsl"

const int TEXEL_VEC4S = 7;
const float BREAKING_GAMMA = 0.55;
const float SHORE_CALM = 0.0; // m of water under the surface (oceanFinish) at which the GPU band's chop is gone ...
const float SHORE_FULL = 1.5; // ... and from which it is at full strength
const float CASCADE_N = 256.0; // FFT resolution, fixed in C#
// Share of each cascade a fully turbulent sea-zone pool shows, wind or not: a churned plunge pool's chop is short and
// steep, so mostly the 3 m and 12 m cascades' ripples, a little over the open sea's in the finest.
const vec3 TURBULENCE_SHARE = vec3(0.2, 0.8, 1.2);
const float EDDY_SPEED = 1.2;  // m/s of swirl in fully turbulent sea-zone water ...
const float EDDY_SCALE = 0.25; // ... in eddies about 4 m across ...
const float EDDY_RATE = 0.15;  // ... turning over as the noise drifts this fast
const float COHERENT_KH = 0.4;   // OceanSwell: fully coherent surf at or below this carrier k h
const float INCOHERENT_KH = 1.2; // ... and none above this
const float BAND_INNER = 0.5;    // peak band: within this many reference steps of the carrier ...
const float BAND_OUTER = 1.0;    // ... fading out by this many
const float MAX_COHERENCE = 0.75; // share of the band the carrier takes at most; the rest keeps random phases
const int CARRIER_SLOTS = 3;      // carrier plus its 2nd and 3rd bound harmonics
const float WAVE_BREAKING_GAMMA = 0.78; // the coherent surf wave breaks at this height / depth
const float GROUP_DEPTH = 0.7;          // sets modulate the surf wave's height by 1 +- this, mean square 1 (OceanSwell.GroupEnvelope)

float groupEnvelope(float spatial)
{
    return (1.0 + GROUP_DEPTH * cos(frame.carrierChop.w * spatial + frame.carrierChop.z)) / sqrt(1.0 + 0.5 * GROUP_DEPTH * GROUP_DEPTH);
}
const float MAX_SHARPNESS = 0.55; // crest peaking s at full ...
const float SHARP_FROM = 0.1;     // ... from carrier H/h this ...
const float SHARP_FULL = 0.45;    // ... to this (OceanSwell.Sharpness)

// Wavenumber for angular frequency omega in water this deep (Jonswap.WaveNumberApprox: Fenton-McKee, within 1.6%).
// Continuous in depth, unlike the bilinear lag map's gradient, which steps at every texel line: the surf carrier's
// wavenumber magnitude comes from here (the eikonal makes |grad phase| = k(h) anyway), only its direction from the map.
float waveNumberApprox(float omega, float depth)
{
    float deep = omega * omega / 9.81;
    float kh = deep * max(depth, 1e-3);
    return kh > 10.0 ? deep : deep / pow(tanh(pow(kh, 0.75)), 2.0 / 3.0);
}

float carrierSharpness(float height, float depth)
{
    return MAX_SHARPNESS * smoothstep(SHARP_FROM, SHARP_FULL, height / max(depth, 0.1));
}
const float MAX_RMS_STEEPNESS = 0.25; // choppiness x rms of the Gerstner trace at most: 4 sigma to a fold (OceanSwell.MaxRmsSteepness)
const float DEEP_DEPTH = 1000.0;
const float SPENT_DEPTH_K = 0.3; // the surf carrier's wavenumber is taken at no shallower than this (BreakerFace)
const float TWO_PI = 6.28318530718;

float hash12(vec2 p)
{
    vec3 p3 = fract(vec3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}

float valueNoise(vec2 p)
{
    vec2 i = floor(p);
    vec2 f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash12(i), hash12(i + vec2(1.0, 0.0)), u.x), mix(hash12(i + vec2(0.0, 1.0)), hash12(i + 1.0), u.x), u.y);
}

// Fold (breakerFace): the fine patch sets gFoldTongue so the tongue's rest strip is drawn as the tongue; the clipmap
// leaves it false and collapses the strip onto the crest line instead. gFoldWeight is 1 on tongue vertices (oceanFinish
// damps the cascades and whitecaps there).
bool gFoldTongue = false;
float gFoldWeight = 0.0;
float gFoldInner = 0.0; // 1 on the tongue's inner skin, the tube's ceiling (ocean.frag darkens it)
float gFoldThrow = 0.0; // the tongue's throw ahead and drop below the crest (world m), from the segment's lip bases (ocean_patch.vert); 0 = no tongue
float gFoldDrop = 0.0;
vec3 gFoldCrest = vec3(0.0); // the crest point the tongue grows from (world), the lip bases' position along the segment
float gFoldTaper = 1.0; // the tongue's reach scaled toward a segment end that holds no lip (ocean_patch.vert): 0 there
vec2 gFoldPour = vec2(0.0); // on the strip, the rest offset that puts this vertex's lace at the surface's density along the skin (ocean.frag)
vec3 gFoldBody = vec3(0.0); // on the strip: x = how far along the tongue's path (0 root .. 1 tip, on either skin), y = the body's thickness there (m), z = how deep in the tube's shadow (ocean.frag)

// Per tap (0 = current field, 1 = fading-out field): buffer slot, first vec4 of the four bilinear texels, weights.
int cSlot[2];
ivec4 cTexel[2];
vec2 cT[2];
bool cHas[2];
float cDepth[2];
vec2 cDir[2];
float cFade;

// Same bilinear as CoastalField.Sample on the CPU. False = this field has nothing active here.
bool coastalTapAt(int tap, vec4 rect, vec2 xz, out float depth)
{
    depth = DEEP_DEPTH;
    int res = int(rect.w);
    if (res <= 0)
        return false;

    vec2 f = (xz - rect.xy) / rect.z - 0.5;
    if (!(f.x >= 0.0 && f.y >= 0.0 && f.x < float(res - 1) && f.y < float(res - 1)))
        return false;

    ivec2 i = ivec2(f);
    cT[tap] = f - vec2(i);
    ivec4 t;
    t.x = (i.x + i.y * res) * TEXEL_VEC4S;
    t.y = t.x + TEXEL_VEC4S;
    t.z = t.x + res * TEXEL_VEC4S;
    t.w = t.z + TEXEL_VEC4S;
    cTexel[tap] = t;

    int s = cSlot[tap];
    vec4 a = coastal[s].texels[t.x];
    vec4 b = coastal[s].texels[t.y];
    vec4 c = coastal[s].texels[t.z];
    vec4 d = coastal[s].texels[t.w];
    if (a.y + b.y + c.y + d.y <= 0.0)
        return false;

    depth = mix(mix(a.x, b.x, cT[tap].x), mix(c.x, d.x, cT[tap].x), cT[tap].y);
    return true;
}

// Current field, crossfaded from the previous one while a rebuild fades in (OceanSwell.SampleCoastal). False = open ocean.
bool coastalSample(vec2 xz)
{
    cSlot[0] = frame.counts.w & 3;
    cSlot[1] = (frame.counts.w >> 2) & 3;
    cFade = frame.coastDir.z; // no previous field (coastPrev.w = 0) fades in from the open ocean

    cDepth[1] = DEEP_DEPTH;
    cHas[0] = coastalTapAt(0, frame.coastRect, xz, cDepth[0]);
    cHas[1] = cFade < 1.0 && coastalTapAt(1, frame.coastPrev, xz, cDepth[1]);
    cDir[0] = frame.coastDir.xy;
    cDir[1] = vec2(cos(frame.coastDir.w), sin(frame.coastDir.w));
    return cHas[0] || cHas[1];
}

// Reference j of the field in buffer s, from the four bilinear texels t with weights w: (lag, dlag/dx, dlag/dz, amp),
// with the analytic bilinear gradient.
vec4 coastalRefAt(int s, ivec4 t, vec2 w, int j, float cell)
{
    int v = 1 + j / 2;
    bool high = (j & 1) != 0;
    vec4 qa = coastal[s].texels[t.x + v];
    vec4 qb = coastal[s].texels[t.y + v];
    vec4 qc = coastal[s].texels[t.z + v];
    vec4 qd = coastal[s].texels[t.w + v];
    vec2 a = high ? qa.zw : qa.xy;
    vec2 b = high ? qb.zw : qb.xy;
    vec2 c = high ? qc.zw : qc.xy;
    vec2 d = high ? qd.zw : qd.xy;

    return vec4(
        mix(mix(a.x, b.x, w.x), mix(c.x, d.x, w.x), w.y),
        mix(b.x - a.x, d.x - c.x, w.y) / cell,
        mix(c.x - a.x, d.x - b.x, w.x) / cell,
        mix(mix(a.y, b.y, w.x), mix(c.y, d.y, w.x), w.y));
}

// One field's reference j at the point coastalSample took. Open ocean if absent.
vec4 coastalTapRef(int tap, int j, float cell)
{
    if (!cHas[tap])
        return vec4(0.0, 0.0, 0.0, 1.0);

    return coastalRefAt(cSlot[tap], cTexel[tap], cT[tap], j, cell);
}

// While a rebuild fades in, the surface is evaluated with each field and the results blended (oceanAt). Fields are never
// mixed term by term: blending two lags sweeps the phase by their difference and the waves run backwards.
vec4 coastalRef(int tap, int j)
{
    return coastalTapRef(tap, j, tap == 0 ? frame.coastRect.z : frame.coastPrev.z);
}

// Over the references in use: breaking cap min(1, gamma h / Hs) and the choppiness scale that keeps the rms of the
// shoaled, shortened Gerstner trace under MAX_RMS_STEEPNESS, as the open sea's clamp does (OceanSwell.CoastalLimits).
vec2 coastalLimits(int tap)
{
    float variance = 0.0;
    float steepness = 0.0;

    for (int j = frame.counts.y; j <= frame.counts.z; ++j)
    {
        vec4 r = coastalRef(tap, j);
        float k0 = frame.refK0[j / 4][j % 4];
        vec2 k = k0 * cDir[tap] + r.yz;
        float amp2 = r.w * r.w;
        variance += frame.refEnergy[j / 4][j % 4] * amp2;
        steepness += frame.refChop[j / 4][j % 4] * amp2 * dot(k, k) / (k0 * k0);
    }

    float hs = 4.0 * sqrt(variance);
    float cap = hs > 1e-4 ? clamp(BREAKING_GAMMA * cDepth[tap] / hs, 0.0, 1.0) : 1.0;
    steepness = sqrt(steepness) * cap;
    return vec2(cap, steepness > MAX_RMS_STEEPNESS ? MAX_RMS_STEEPNESS / steepness : 1.0);
}

// A coastal field for the surf carrier: tap 0 = the current one, 1 = while a rebuild crossfades in, the one it replaces.
// Its rect (origin, cell size, resolution), buffer slot and the direction it was solved for.
void carrierField(int tap, out vec4 rect, out int slot, out vec2 dir)
{
    rect = tap == 0 ? frame.coastRect : frame.coastPrev;
    slot = tap == 0 ? frame.counts.w & 3 : (frame.counts.w >> 2) & 3;
    dir = tap == 0 ? frame.coastDir.xy : vec2(cos(frame.coastDir.w), sin(frame.coastDir.w));
}

// The four carrier atlas texels around xz in field tap (its lag layer 2 tap, or its gradient layer 2 tap + 1), their
// bilinear weights, and the field's rect and direction: the same bilinear as CoastalField.Sample on the CPU. False
// outside the map; a caller that needs an active texel tests a.w + b.w + c.w + d.w.
bool carrierTexels(int tap, int layer, vec2 xz, out vec4 rect, out vec2 dir, out vec2 w, out vec4 a, out vec4 b, out vec4 c, out vec4 d)
{
    int s;
    carrierField(tap, rect, s, dir);
    int res = int(rect.w);
    vec2 f = (xz - rect.xy) / rect.z - 0.5;
    if (res <= 0 || !(f.x >= 0.0 && f.y >= 0.0 && f.x < float(res - 1) && f.y < float(res - 1)))
        return false;

    ivec2 i = ivec2(f);
    w = f - vec2(i);
    a = imageLoad(carrierAtlas, ivec3(i.x, i.y, layer));
    b = imageLoad(carrierAtlas, ivec3(i.x + 1, i.y, layer));
    c = imageLoad(carrierAtlas, ivec3(i.x, i.y + 1, layer));
    d = imageLoad(carrierAtlas, ivec3(i.x + 1, i.y + 1, layer));
    return true;
}

float bilerp(float a, float b, float c, float d, vec2 w)
{
    return mix(mix(a, b, w.x), mix(c, d, w.x), w.y);
}

// The surf carrier's direction of travel at xz: the atlas's central differences of its lag a cell apart, bilinear (which
// equals the central differences of the bilinear lag): continuous, where the bilinear map's own gradient steps at every
// texel line and, where refraction is strong (off the end of a bar), swings by tens of degrees from one cell to the
// next. fallback where the field runs out.
vec2 carrierDirection(int tap, vec2 xz, vec2 fallback)
{
    vec4 rect;
    vec2 dir, w;
    vec4 a, b, c, d;
    if (!carrierTexels(tap, 2 * tap + 1, xz, rect, dir, w, a, b, c, d))
        return fallback;

    return normalize(frame.carrier.x * dir + vec2(bilerp(a.x, b.x, c.x, d.x, w), bilerp(a.y, b.y, c.y, d.y, w)) + vec2(1e-6, 0.0));
}

// The surf carrier at xz in field tap, from the atlas (coastalSample's per-point state is left alone): x = phase (0 on
// a crest), yz = local wavevector (direction of k0 d + grad lag, magnitude k(h) so it is continuous across texel
// lines), w = unbroken height crest to trough (sets included, as swellPart draws it); depth = water depth there. False
// where the field has nothing (OceanSwell.SurfCarrier).
bool surfCarrierAt(int tap, vec2 xz, out vec4 carrier, out float depth)
{
    carrier = vec4(0.0);
    depth = DEEP_DEPTH;
    vec4 rect;
    vec2 dir, w;
    vec4 a, b, c, d;
    if (frame.carrier.w <= 0.0 || !carrierTexels(tap, 2 * tap, xz, rect, dir, w, a, b, c, d) || a.w + b.w + c.w + d.w <= 0.0)
        return false;

    depth = bilerp(a.y, b.y, c.y, d.y, w);
    float lag = bilerp(a.x, b.x, c.x, d.x, w);
    vec2 grad = vec2(mix(b.x - a.x, d.x - c.x, w.y), mix(c.x - a.x, d.x - b.x, w.x)) / rect.z;
    float shoal = bilerp(a.z, b.z, c.z, d.z, w);
    vec2 k = normalize(frame.carrier.x * dir + grad + vec2(1e-6, 0.0)) * waveNumberApprox(frame.carrierChop.y, max(depth, SPENT_DEPTH_K));
    float coherence = MAX_COHERENCE * (1.0 - smoothstep(COHERENT_KH, INCOHERENT_KH, waveNumberApprox(frame.carrierChop.y, max(depth, 0.0)) * max(depth, 0.0)));
    float spatial = frame.carrier.x * dot(dir, xz) + lag;
    carrier = vec4(spatial + frame.carrier.z, k, 2.0 * sqrt(coherence * frame.carrier.w) * shoal * groupEnvelope(spatial));
    return true;
}

// Water depth at xz in field tap, bilinear as surfCarrierAt reads it (OceanSwell.CarrierDepth); DEEP_DEPTH where the
// field has nothing.
float carrierDepthAt(int tap, vec2 xz)
{
    vec4 rect;
    vec2 dir, w;
    vec4 a, b, c, d;
    if (!carrierTexels(tap, 2 * tap, xz, rect, dir, w, a, b, c, d) || a.w + b.w + c.w + d.w <= 0.0)
        return DEEP_DEPTH;

    return bilerp(a.y, b.y, c.y, d.y, w);
}


// Whether the surf can make foam at x0 now (ocean_surf_foam.comp): inside the current field, in water no deeper than
// the coherent surf wave can break in here (its shoaled height at the top of a set over the breaking ratio, with
// SURF_BAND_MARGIN as BreakerSet's surf mask has, plus SURF_BAND_REACH for the roller's tail behind a crest) and no
// higher up the beach than the swash reaches. One atlas lookup against a breakerFace evaluation per cell outside.
const float SURF_BAND_MARGIN = 1.3;
const float SURF_BAND_REACH = 1.0;
const float SWASH_REACH = 2.0; // SWASH_MAX: no higher up the beach than the swash reaches

bool surfBandAt(vec2 x0)
{
    vec4 rect;
    vec2 dir, w;
    vec4 a, b, c, d;
    if (frame.carrier.w <= 0.0 || !carrierTexels(0, 0, x0, rect, dir, w, a, b, c, d) || a.w + b.w + c.w + d.w <= 0.0)
        return false;

    float depth = bilerp(a.y, b.y, c.y, d.y, w);
    float peak = (1.0 + GROUP_DEPTH) / sqrt(1.0 + 0.5 * GROUP_DEPTH * GROUP_DEPTH);
    float unbroken = 2.0 * sqrt(MAX_COHERENCE * frame.carrier.w) * bilerp(a.z, b.z, c.z, d.z, w) * peak;
    return depth >= -SWASH_REACH && depth <= SURF_BAND_MARGIN * unbroken / WAVE_BREAKING_GAMMA + SURF_BAND_REACH;
}

// Breaker crests (BreakerFaces) the lips are swept along, and the break distance grid (BreakerSet.BreakDistance, half
// floats in pairs on the coastal grid). grid = (break grid origin x, z, its cell size, render - tick seconds: segments
// advance by their celerity), info = (segment count, break grid resolution (0 = none), 0, 1 while faces are drawn).
struct BreakerSegment
{
    vec4 ends;    // x0, z0, x1, z1
    vec4 slots;   // lip base slot of end 0, of end 1 (lipBases.ends; shared with the neighbouring segment), 0, 0
    vec4 dir;     // shoreward normal x, z, celerity, wavelength
    vec4 normals; // shoreward normal at end 0 (xy), at end 1 (zw): the carrier's travel there, shared with the neighbouring segment
    vec4 heights; // CPU wave height H at 0, at 1 (unused here), taper toward a chain's ends at 0, at 1 (1 for raw crossings)
};

layout(std430, set = 0, binding = 6) readonly buffer Breakers
{
    vec4 grid;
    ivec4 info;
    ivec4 slots; // x = lip base slots in use (BreakerFaces.SlotCount)
    BreakerSegment segments[4096];
    uint patchList[4096]; // segments within the fine patch's reach (OceanRenderer.WriteBreakers): the patch's instances
    uint slotRef[8192];   // per lip base slot: the segment << 1 | end whose point it is (BreakerFaces.SlotRefs)
    uint breakDistance[];
} breakers;

const float BACK_WIDTH = 0.18;  // wavelengths: gentle back of the breaker
const float FRONT_WIDTH = 0.09; // wavelengths: the face at full curl (0.05 drew the crest lift as a spike: a pyramid crest)
const float MIN_FRONT = 0.8;    // metres
const float PEAK = 2.5;         // at full curl, height above the trough (as a share of the carrier's span) is raised to this power:
                                // a peaked crest over flat troughs, the shape of a wave about to break (3 was a pyramid)
const float PITCH = 0.88;       // at full curl, 1 - the upper face's Jacobian: how near vertical the wall stands, at least ...
const float PITCH_SQUEEZE_MAX = 0.94; // ... and at most, when WALL_WIDTH asks for more
const float WALL_WIDTH = 1.0;   // the wall's world width, x the wave height: the squeeze is chosen per wave so a small wave on a long
                                // swell gets a wall as steep as a big one's (a fixed squeeze of PITCH_FRONT L made it a 10-30 degree slope)
const float PITCH_FRONT = 0.2;  // wavelengths of face (rest) the wall is squeezed from: the peaked crest's upper part
const float PITCH_RECOVER = 0.17; // wavelengths of trough ahead that gives back what the wall drew in, done before ...
const float PITCH_REACH = 0.4;  // wavelengths: the face at full strength within this of the crest, gone by ...
const float PITCH_FADE = 0.48;  // ... this, before the trough where the phase hands over to the next crest ...
const float PITCH_MAX = 30.0;   // ... and by this many metres
const float PITCH_TOP = 20.0;   // ramp sharpness at the crest top, which stays sharp (rounds over 1 / this of a ramp) ...
const float PITCH_FOOT = 4.0;   // ... at the wall's foot, which rounds off into the trough ahead ...
const float PITCH_END = 8.0;    // ... and where the trough ahead has given everything back
const float PITCH_DRY_DEPTH = 0.6; // the wall only draws back water that is there: it fades out over the last this many metres of depth under each point (pulled across the beach, the swash folded into dark shards)
// Whitewater envelope (breakerFace): metres, set by the wave's height, apart from the face's wavelength-scaled width.
// On a bore in the last metre or two of water the local wavelength is still 20-30 m, so a whitewater band drawn at
// BACK_WIDTH of it was 10-20 m wide and, as the bore reached the beach, whitened the whole strip to the waterline at once
// (and the surf foam map stamped it, so the whole shore lit up and faded together). A real bore is a roller a few times
// its height long: a white front with a decaying wake.
const float WHITE_FRONT = 1.5;      // the front's Gaussian width ahead of the crest, x the wave height (m) ...
const float WHITE_FRONT_MIN = 0.8;  // ... at least this (m): a bore in the last of the water
const float WHITE_FRONT_MAX = 4.0;  // ... and at most this (m)
const float WHITE_BACK = 1.6;       // behind the crest the width is this x the front's: the crest top and the back
const float WHITE_DRY_DEPTH = 0.15; // whitewater fades out over the last of the water: at the waterline the swash's own front takes over
const float ROLLER_TRAIL = 0.25;    // whitewater trailing behind a broken crest: its strength ...
const float ROLLER_TAIL = 2.0;      // ... and e-folding length, x the wave height (m) ...
const float ROLLER_TAIL_MIN = 1.5;  // ... at least this (m)
const float STAND_FROM = 0.45;   // shoaling stands the wave up before it breaks: from H / h this ...
const float STAND_AMOUNT = 0.55; // ... to breaking (WAVE_BREAKING_GAMMA) the face steepens to this, which a broken wave keeps as its rolling front (0.65 stood unbroken faces near vertical with nothing tipping over them)
const float BREAK_STEEP = 0.85;  // a breaking crest's face stands at least this steep under its lip, spilling or plunging (a spiller's 0.55 was a 25 degree slope: the tongue lay on it as a shelf)
const float PLUNGE_FROM = 0.35;  // breaker type from the surf similarity number xi = tan(beta) / sqrt(H / L0): spilling below this ...
const float PLUNGE_FULL = 0.65;  // ... (whitewater down a steep front, no lip), plunging by this (the lip is thrown)
const float SPILL_TIP = 0.5;     // a spilling crest keeps tipping a lip this share of a plunging one's, which tumbles straight down the face
const float SLOPE_REACH = 2.0;   // depths either side of the onset point the bed slope is taken over ...
const float SLOPE_MIN_SPAN = 4.0;  // ... at least a texel ...
const float SLOPE_MAX_SPAN = 20.0; // ... and at most this (m)
const float ONSET_BACK_MAX = 60.0; // m: how far back along its travel a wave's break onset is looked for (BreakerFace.Plunge)

// A crest's surf state from its breaking cap (BreakerFaces on the CPU: Curl, Broken, Amplitude).
const float CREST_AMPLITUDE = 0.2; // crest lift over the heightfield, x the capped height
const float SPENT_DEPTH = 0.3;     // water (m) below which the wave has run out
const float BORE_LIFT = 0.35;      // a broken wave's lift over still water, x its breaking height gamma h ...
const float RUNUP_DEPTH = 0.6;     // ... kept down to this depth ...
const float BORE_FADE_DEPTH = 0.4; // ... and fading out over the last of the water, where the swash takes over
const float BREAK_CRASH = 0.35;    // wavelengths a breaking section travels, past its onset, until its lip has landed and the crest collapsed
const float SPENT_CAP = 0.45;      // below this breaking cap the water has run out under the crest: broken whatever its travel
const float CLOCK_START_CAP = 0.92; // the break clock starts at this cap (BreakerSet.ClockStartCap): the face is ready
const float ONSET_TRAVEL = 0.15;   // wavelengths of travel after the clock starts over which the face stands up (curl, lift, pitch); the lip and the collapse run on the travel past that
const float HOLD_GAMMA = 1.5;      // while the lip is in the air the breaking height is held at its onset value, at most this x the depth
const float HOLD_RAMP = 0.03;      // wavelengths after the clock starts over which the hold comes on (ramped with the onset, H dipped as the cap fell under it)
const float LANDED_TRAVEL = 0.245; // wavelengths past the onset at which the lip has landed and crashed: (LIP_REACH_TRAVEL + LIP_CRASH_TRAVEL) x BREAK_CRASH
const float COLLAPSE_TRAVEL = 1.0; // wavelengths of travel past that over which the crest's held height, lift and wall ease away: about a period (easing them over the second half of BREAK_CRASH, 0.175, dropped the crest the moment the lip landed)
const float LIP_CLOSE_TRAVEL = 0.3; // wavelengths of travel past the landing over which a landed tongue's throw falls back onto the face (lipClose): the pour weakens as the crest falls and the tongue becomes the roller's front (kept thrown through the whole collapse, it was a flat white slab along the crest for a period)

// Lip (ocean_lip.vert; spray leaves from its tip and landing, ocean_spray_spawn.comp). The crest going over: a tongue
// of water that grows out of the crest top as the wave breaks, arcs over the face and falls to the face's foot (the
// drawn surface there, sampled by ocean_lip_base.comp), the tube open under it. How far along that path it has got is
// its reach, driven by the wave's travel since its break began (lipReach), so it visibly throws and falls rather than
// riding the crest as a fixed cap; a plunging lip then lands and is gone, a spilling one holds at the foot as a cascade
// while the face stands. Thick at the root, thinning to a feathered edge.
const float LIP_REACH_TRAVEL = 0.5; // share of BREAK_CRASH travel over which the tongue reaches the foot, where it crashes ...
const float LIP_CRASH_TRAVEL = 0.2; // ... over this share more; a spilling tongue then throws again (its cycle is the two together)
const float LIP_MIN_REACH = 0.03;   // no tongue until the break is travelling: a lip that sat on the crest at reach 0 tipped for ever
const float LIP_THICKNESS = 0.25; // body thickness at the root, x lip size: a mass of water going over, not a skin (0.45 was a metre of body on a 2.5 m wave, lit through to grey) ...
const float LIP_TAPER = 0.3;      // ... kept out to this far along the tongue, then thinning to the feathered edge
// Flow (docs/breaking-waves.md, twenty-first pass): the water's forward speed relative to the rest grid over a breaking
// wave, by which the surface's lace and ripples and the lip's pour are flow-mapped (ocean_shading.glsl flowPhases), so
// the back, the crest and the tongue stream as one. The rest grid already carries the orbital motion; this is what
// breaking adds: nothing at the trough behind, FLOW_CREST x celerity at the crest top of a face at full steepness
// (water moves with the wave as it breaks), the roller on the front with the crest, the tongue faster still. Texture
// only: the CPU face has no need of it.
const float FLOW_CREST = 1.0;   // x celerity, at the crest top at full steepness: in rest space that pins the crest's lace to the crest, as breaking water moves with the wave
const float FLOW_BACK = 0.5;    // wavelengths behind the crest the flow rises over, from the trough
const float FLOW_FRONT = 0.37;  // wavelengths ahead the roller's flow fades over (PITCH_FRONT + PITCH_RECOVER)
const float FLOW_TONGUE = 0.6;  // the tongue's tip streams this much x celerity faster than its root: the pour over the lip
const float FLOW_PERIOD = 1.0;  // s: each copy of the flow map slides for this long, by speed x this, before it fades out and resets
const float LIP_MIN_CURL = 0.05;  // below this no lip is drawn
const float LIP_SLOT_CURL = 0.5 * LIP_MIN_CURL;   // a lip base is evaluated from half the lip's thresholds (ocean_lip_base.comp), so a ...
const float LIP_SLOT_REACH = 0.5 * LIP_MIN_REACH; // ... segment's last end, where the ribbon tapers to nothing, still has its point
const float LIP_SLOT_HEIGHT = 0.025;
const float LIP_UNDERSIDE = 0.45; // light left on the tongue's underside and the tube ceiling: the dark mouth of the tube
const float LACE_WRAP = 600.0;    // seconds: foam and lip texture clocks wrap so noise coordinates stay small
const float LIP_DISTANCE = 250.0; // lips (and their spray) further than this from the camera aren't built or drawn: a 1 m lip is 3 px tall there at 900 px (400 m cost 0.006 ms more in lip bases)
const float SURF_SPACING_FULL = 4.0; // breaker faces and swash at full strength up to this grid spacing (m) ...
const float SURF_SPACING_NONE = 8.0; // ... and gone by this one

// How far (m) the wave at xz has travelled since it began to break (BreakerSet.BreakDistance), bilinear; 0 outside.
float breakDistanceAt(vec2 xz)
{
    int res = breakers.info.y;
    vec2 f = (xz - breakers.grid.xy) / breakers.grid.z - 0.5;
    if (res <= 0 || !(f.x >= 0.0 && f.y >= 0.0 && f.x < float(res - 1) && f.y < float(res - 1)))
        return 0.0;

    ivec2 i = ivec2(f);
    vec2 w = f - vec2(i);
    int c = i.x + i.y * res;
    vec2 low = vec2(unpackHalf2x16(breakers.breakDistance[c >> 1])[c & 1], unpackHalf2x16(breakers.breakDistance[(c + 1) >> 1])[(c + 1) & 1]);
    int d = c + res;
    vec2 high = vec2(unpackHalf2x16(breakers.breakDistance[d >> 1])[d & 1], unpackHalf2x16(breakers.breakDistance[(d + 1) >> 1])[(d + 1) & 1]);
    return mix(mix(low.x, low.y, w.x), mix(high.x, high.y, w.x), w.y);
}

// Breaker type for the wave at a crest point travelling along n (unit), from the surf similarity number
// xi = tan(beta) / sqrt(H / L0) where it began to break (breakDistance back along n: the type is set at onset and kept,
// as the lip it threw is), with the bed slope over SLOPE_REACH depths either side, so a bar's face sets the type and
// its flat top keeps it (BreakerFace.Plunge on the CPU): 0 spilling .. 1 plunging.
float breakerPlunge(int tap, vec2 crest, vec2 n, float depth, float breakDistance, float unbroken)
{
    vec2 onset = crest - n * min(breakDistance, ONSET_BACK_MAX);
    float span = clamp(SLOPE_REACH * max(depth, 0.0), SLOPE_MIN_SPAN, SLOPE_MAX_SPAN);
    float deep = carrierDepthAt(tap, onset - n * span);
    float shallow = carrierDepthAt(tap, onset + n * span);
    float tanBeta = max(deep - shallow, 0.0) / (2.0 * span);
    float omega = frame.carrierChop.y;
    float l0 = TWO_PI * 9.81 / max(omega * omega, 1e-6);
    float xi = tanBeta / sqrt(max(unbroken, 0.05) / l0);
    return smoothstep(PLUNGE_FROM, PLUNGE_FULL, xi);
}

// The break's clock (BreakerSet.CarryBreakDistance, breakDistanceAt): once the wave is past its breaking limit by
// CLOCK_START_CAP the clock runs with its travel, and everything the break does runs on it, so the sequence has the
// same shape on any bed: the face stands up over the first ONSET_TRAVEL wavelengths (breakOnset), then the lip throws,
// reaches the foot and lands and the crest collapses into the roller over BREAK_CRASH more (breakTravel, breakLanded).
// (With the face on the cap and the clock on the travel, a 1:50 shelf, where the cap falls 0.03 per 1.5 m, had the lip
// thrown and the crest collapsed while the face was still standing up: a small tip, then foam.)
float breakOnset(float breakDistance, float wavelength)
{
    return smoothstep(0.0, ONSET_TRAVEL * wavelength, breakDistance);
}

float breakTravel(float breakDistance, float wavelength)
{
    return max(breakDistance - ONSET_TRAVEL * wavelength, 0.0);
}

float breakLanded(float breakDistance, float wavelength)
{
    return smoothstep(0.5, 1.0, breakTravel(breakDistance, wavelength) / (BREAK_CRASH * wavelength));
}

// The breaking cap with the break's memory (BreakerFace.HeldCap): once the face has stood up the height is held at its
// onset value (the cap the clock starts from, at most HOLD_GAMMA x the depth) until the lip has landed, then falls to
// the depth's own cap, so the tube keeps its size while the lip is in the air and the wave decays after it lands,
// rather than shrinking with the bed under it as it throws. The drawn carrier holds the same way (swellShare).
// The crest's collapse: once the lip has landed (LANDED_TRAVEL past the onset) the held height, the lift and the wall
// ease away over COLLAPSE_TRAVEL, about a period, as a broken wave's crest decays; the whitewater (breakLanded, the
// roller) is there from the landing.
float breakCollapse(float breakDistance, float wavelength)
{
    return smoothstep(LANDED_TRAVEL, LANDED_TRAVEL + COLLAPSE_TRAVEL, breakTravel(breakDistance, wavelength) / wavelength);
}

// A landed tongue closes onto the face over LIP_CLOSE_TRAVEL: its throw (lipThrowLanding, the lip path's arc) falls
// back to the wall and the crash whitens it over the same travel (BreakerLip.Close on the CPU).
float lipClose(float breakDistance, float wavelength)
{
    return smoothstep(LANDED_TRAVEL, LANDED_TRAVEL + LIP_CLOSE_TRAVEL, breakTravel(breakDistance, wavelength) / wavelength);
}

float breakHold(float breakDistance, float wavelength)
{
    return smoothstep(0.0, HOLD_RAMP * wavelength, breakDistance) * (1.0 - breakCollapse(breakDistance, wavelength));
}

float heldCap(float cap, float depth, float unbroken, float hold)
{
    float held = min(CLOCK_START_CAP, HOLD_GAMMA * max(depth, 0.0) / max(unbroken, 1e-4));
    return max(cap, held * hold);
}

// Over a wavelength (u = 0 on the crest behind .. 1 on the crest ahead), how much of the drawn carrier's hold is the
// crest ahead's: the hand-over sits around the trough (BreakerFace.HoldBlend).
float holdBlend(float u)
{
    return smoothstep(0.4, 0.6, u);
}

// A crest's surf state from the carrier's unbroken height and the depth there, how far it has travelled since it began
// to break and its breaker type (BreakerFace.CrestState on the CPU): x = curl, y = broken (0 standing .. 1 collapsed
// into a roller), z = wave height H (held while the lip is in the air, fading as the water runs out), w = crest lift
// over the heightfield. Curl rises with the onset, only where it plunges (lips, throwing); by BREAK_CRASH wavelengths
// past the onset the lip has landed (broken) and the curl eased to a quarter, however the depth runs after, so only a
// pocket where the break begins is curling.
vec4 crestState(float unbroken, float depth, float breakDistance, float wavelength, float plunge)
{
    float cap = unbroken > 1e-4 ? clamp(WAVE_BREAKING_GAMMA * max(depth, 0.0) / unbroken, 0.0, 1.0) : 1.0;
    float onset = breakOnset(breakDistance, wavelength);
    float landed = breakLanded(breakDistance, wavelength);
    float collapse = breakCollapse(breakDistance, wavelength);
    float progress = breakTravel(breakDistance, wavelength) / (BREAK_CRASH * wavelength);
    float height = unbroken * heldCap(cap, depth, unbroken, breakHold(breakDistance, wavelength));
    // The cap only says when the water has run out under the crest (SPENT_CAP): a bore however it got there. A plunging
    // lip is thrown once and lands; a spilling crest keeps tipping a small lip that tumbles down its face for as long
    // as the face stands, until the water runs out under it.
    float spent = smoothstep(0.0, 1.0, (SPENT_CAP - cap) / 0.2);
    float curl = onset * mix(SPILL_TIP * (1.0 - spent), 1.0 - 0.75 * max(spent, smoothstep(0.6, 1.0, progress)), plunge);
    float broken = max(spent, landed);
    float water = smoothstep(0.0, 1.0, (depth - SPENT_DEPTH) / SPENT_DEPTH);
    float bore = clamp((cap - 0.3) / 0.3, 0.4, 1.0);
    float swash = BORE_LIFT * BREAKING_GAMMA * max(depth, RUNUP_DEPTH) * broken * smoothstep(0.0, 1.0, depth / BORE_FADE_DEPTH);
    // The crest lift eases away after the lip has landed (breakCollapse): the crest comes down onto its landing over a
    // period and the tube closes.
    return vec4(curl, broken, height * water, max(CREST_AMPLITUDE * height * onset * (1.0 - collapse) * bore * water, swash));
}

// What drives the face (BreakerFace.CrestState: Steep, Spill): x = steepness of the peak and pitch, the curl while it
// throws, the pre-break stand-up from H / h and the rolling front a broken wave keeps; y = a spilling crest's whitewater
// once breaking has begun.
vec2 faceDrive(float unbroken, float depth, float plunge, vec4 state, float breakDistance, float wavelength)
{
    float cap = unbroken > 1e-4 ? clamp(WAVE_BREAKING_GAMMA * max(depth, 0.0) / unbroken, 0.0, 1.0) : 1.0;
    float stand = smoothstep(STAND_FROM, WAVE_BREAKING_GAMMA, unbroken / max(depth, 0.1));
    float onset = breakOnset(breakDistance, wavelength);
    // The wall eases with the crest's collapse (or as the water runs out), not with the whitewater: it stands under the
    // held crest until the lip has landed.
    float ease = max(smoothstep(0.0, 1.0, (SPENT_CAP - cap) / 0.2), breakCollapse(breakDistance, wavelength));
    return vec2(max(max(state.x, BREAK_STEEP * onset) * (1.0 - 0.5 * ease), STAND_AMOUNT * stand), (1.0 - plunge) * onset);
}

// The point on the carrier's crest nearest xz in the current field, where its phase is 0: two Newton steps along the
// wavevector, as breakerFace finds its crest. xz where there is no carrier.
vec2 seatOnCrest(vec2 xz)
{
    for (int i = 0; i < 2; ++i)
    {
        vec4 carrier;
        float depth;
        if (!surfCarrierAt(0, xz, carrier, depth))
            return xz;

        xz -= carrier.yz * ((carrier.x - TWO_PI * floor(carrier.x / TWO_PI + 0.5)) / dot(carrier.yz, carrier.yz));
    }

    return xz;
}

// The surf state of the crest at xz in the current field (crestState), zero where there is no carrier.
vec4 crestStateAt(vec2 xz)
{
    vec4 carrier;
    float depth;
    if (!surfCarrierAt(0, xz, carrier, depth))
        return vec4(0.0);

    float k = max(length(carrier.yz), 1e-6);
    float breakDistance = breakDistanceAt(xz);
    float plunge = breakerPlunge(0, xz, carrier.yz / k, depth, breakDistance, carrier.w);
    vec4 state = crestState(carrier.w, depth, breakDistance, TWO_PI / k, plunge);

    // For the lip, y is its fall: a plunging lip falls with the collapse, a spilling one only as the water runs out.
    float cap = carrier.w > 1e-4 ? clamp(WAVE_BREAKING_GAMMA * max(depth, 0.0) / carrier.w, 0.0, 1.0) : 1.0;
    state.y = mix(smoothstep(0.0, 1.0, (SPENT_CAP - cap) / 0.2), state.y, plunge);
    return state;
}

vec2 lipLife(float breakDistance, float wavelength, float plunge);
const float LIP_FOOT_SHARE = 0.95; // the landing is this far down the wall's rest span (just short of the foot's rounding) ...
const float LIP_MAX_THROW = 1.2;   // ... and never further ahead than this x its drop in the world: on a gentler face the tongue lands on the face
const float LIP_THROW_SPEED = 0.7;  // a plunging lip's tip leaves the crest at this share of the celerity and falls its drop under gravity (lipThrowLanding) ...
const float LIP_THROWN_MAX = 1.5;   // ... landing that far ahead, at most this x its drop: the tube's width (2 drew a roof twice as wide as the drop, a flat shelf from above)
const float LIP_FACE_SIZE = 0.6;   // the tongue's body is sized from at least this share of the face it falls down
const float LIP_SPILL_SPAN = 0.5;  // a spilling tongue lands this far down the wall's rest span: the crest tumbling, not a throw
// The fold (breakers.info.z, the default; --ribbon-lip for the ribbon): the lip as a fold of the ocean mesh itself on the
// fine patch. Its throw and drop are the segment's lip bases' (the landing measured on the drawn surface, clamped to the
// wall as the ribbon's is), so a spiller's tongue lies on the face rather than hanging in the air in front of it.
const float LIP_FOLD_SPAN = 1.0;   // rest metres of strip per metre of the tongue's path that each skin is made of (1: texture at the surface's density)
// The tongue is ragged along the crest (foldRaggedness): each column's breaking cap and reach vary by a noise in the
// wave's frame, so the tongue starts, reaches and dies off unevenly along the crest in lobes, as whitewater tumbles
// off a real crest, instead of a straight-edged shelf.
const float FOLD_RAG_SCALE = 0.35;   // lobes per metre along the crest (about 3 m) ...
const float FOLD_RAG_DRIFT = 0.25;   // ... the pattern drifting this many lobes a second, so successive crests differ
const float FOLD_RAG_CAP = 0.06;     // a column's breaking cap shifted by up to +- this: its die-off along the crest (spent) ...
const float FOLD_RAG_TRAVEL = 0.04;  // ... and its break clock by up to +- this many wavelengths: its onset along the crest
const float FOLD_RAG_REACH = 0.5;    // a column's reach shortened by up to this share (less on a plunger, whose lip is one sheet)
const float FOLD_ONSET = 0.05;       // the tongue grows in over this much curl above LIP_MIN_CURL (a step there was a straight cut across the crest)
const float FOLD_GROW = 0.3;         // metres of path over which a young tongue's look fades in (its foam and body): under that it is the plain face (a sliver drew as a hard white line along the shoulder)
const float FOLD_POUR_STRETCH = 0.4; // ocean.frag: lace cells along the skin per cell on the surface, so the tongue's lace tears into strands down the pour
// The tongue is a sheet of water, lit through (ocean.frag): light reaches the eye through its body, the more the thinner
// it is, so the thrown edge glows and the root stays the wave's own colour; the tube's ceiling is dark only where the
// body over it is thick. (With the ribbon's shading alone a thrown lip read as a dark flag: a glassy outer skin and a
// flat-darkened underside.)
const float FOLD_ABSORPTION = 10.0; // per metre of body: the root (LIP_THICKNESS x the body size, about 0.15 m) lets a fifth through, the feathered edge nearly all
const float FOLD_FEATHER = 0.5;     // a plunging lip at full curl feathers into foam over this last share of its path (0.2 before) ...
const float FOLD_TIP_FADE = 0.7;    // ... and its last stretch fades out between the strands: spray, not a cut edge
const float FOLD_CEILING = 0.5;     // share of the inner skin's rows that are the lip's underside, the tube's ceiling; the rest run down the back wall to the foot
// The tube's inside (ocean.frag): the ceiling and the back wall are lit by what comes through the lip and the wall, not
// by the sky the ceiling would reflect, so they are a dark turquoise, brighter and greener where the body over them is
// thin. (Shaded as water with the sky in its Fresnel, then darkened flat, the inside read as grey.)
const vec3 TUBE_COLOUR = vec3(0.02, 0.14, 0.13); // linear, before the tonemap (not scaled by the sun: x 3 it was pale mint)
const float TUBE_SHADE = 0.4;       // the inside's light in the lip's shadow ...
const float TUBE_THROUGH = 0.6;     // ... plus this much where the body over it is thin (x the light through it)
// How deep in the tube's shadow the inside is (gFoldBody.z): only where a tube is enclosed (a thrown lip, until the
// crest's collapse closes it; a spiller has none), full under the lip's root, lit from the mouth toward the tip and
// down the back wall toward the foot. (Painting the whole wall from crest to foot as the inside, whatever the lip did,
// put a dark zone on every breaking face, far bigger than the lip once the wall relaxed in the collapse or on a spiller.)
const float TUBE_MOUTH = 0.4;       // the ceiling's shade at the tip, where the mouth lights it
const float TUBE_BACK_LIT = 0.3;    // the back wall is in full shade this far down from the root, lit from there to the foot
const float FOLD_LACE_FINE = 2.5;   // the lip's lace is this many times denser by the tip than the surface's (continuous at the root): at close range the surface's cells were blotches on a lip
const float FOLD_CRASH_TIP = 0.35;  // the crash whitens the lip from the tip back: this share of the path as the lip lands, the rest over the crest's collapse (all at once, a landed lip under a held crest was a white sail for a period)
// The lip's body (ocean.frag): between its lace the outer skin is a thin sheet of water lit through, a bright turquoise,
// brighter where it is thin and toward the sun, mixed over the water shading (whose Fresnel showed the sky at the
// grazing angles a low camera sees a lip at, as pale as the veins, so the lip read as a smooth white).
const vec3 LIP_COLOUR = vec3(0.04, 0.22, 0.2); // linear, before the tonemap
const float LIP_SHADE = 0.5;        // the body's light on its own ...
const float LIP_THROUGH = 0.8;      // ... plus this much where it is thin (x the light through it) ...
const float LIP_SUN = 0.3;          // ... and this much more of that toward the sun
const float LIP_BODY_MIX = 0.75;    // how much of the outer skin's colour is the body's; the rest keeps the water's gloss
// Strands (ocean.frag): water pouring over the lip runs down it in strands, ridges along the pour with thin sheet
// between, drawn as a noise stretched along the pour on the lip's rest coordinates (so they slide with its lace): they
// groove the skin's normal, thin the body between them (lighter streaks where the light comes through) and gather the
// foam, so the sheet is not clean.
const float STRAND_ACROSS = 1.8;    // strands per metre across the pour ...
const float STRAND_ALONG = 0.35;    // ... and cells per metre along it: stretched streaks
const float STRAND_SLOPE = 0.25;    // how much they groove the normal
const float STRAND_THIN = 0.6;      // the body between strands is thinner by up to this share
const float STRAND_FOAM = 0.5;      // the lip's lace coverage is raised by up to this x the fold's on the strands
const float LIP_MIN_BODY = 0.6;     // the body is sized from at least this (m) so a small wave's lip is not a sliver
const float THROW_FOAM_HEIGHT = 0.6; // the whitewater where a throwing lip lands is full from this wave height (gating it on the crest lift left small waves with none)

// The lip body's thickness at u along its path (ocean_lip.vert's): LIP_THICKNESS x its size at the root, kept out to
// LIP_TAPER, thinning to the feathered edge.
float lipBodyThickness(float size, float u)
{
    return max(size, LIP_MIN_BODY) * (LIP_THICKNESS * (1.0 - smoothstep(LIP_TAPER, 1.0, u)) + 0.03);
}
const float PATCH_DISTANCE = 80.0; // the fold is drawn by the fine patch (ocean_patch.vert) on crests within this of the camera (OceanRenderer.PatchDistance) ...
const float PATCH_FADE = 10.0;     // ... fading out over the last this many metres; beyond, the face's whitewater marks the crest (a 1 m tongue is a few pixels there)

// The rest distance ahead of the crest at which the lip's landing is sampled (BreakerFace.LipRestDistanceAt): down the
// wall's full rest span (PITCH_FRONT of a wavelength, squeezed by the pitch to 1 - PITCH x amount of that in the world).
// Rest, not world: the pitch squeezes rest space under the crest, and a landing taken at a world-sized rest distance sat
// on the crest's rounded top, 0.14 H down (the tongue was a shelf). The landing is then pulled back along the chord to
// the crest where the face is gentle (lipClampLanding), so the tongue never reaches further ahead than LIP_MAX_THROW x
// the drop it falls: the drawn face's own height, not the carrier's capped height (which is about half of it: the
// drawn surface is the carrier plus the random sea, and a tongue sized from the carrier spanned 46% of its wall).
float lipRestDistance(float wavelength, float amount, float height)
{
    return LIP_FOOT_SHARE * PITCH_FRONT * wavelength;
}

// The landing pulled back along its chord to the crest until it is no further ahead than LIP_MAX_THROW x its drop
// (BreakerLip.Sample on the CPU).
vec3 lipClampLanding(vec3 crest, vec3 foot, vec2 n)
{
    float throwW = max(dot(foot.xz - crest.xz, n), 0.05);
    float limit = LIP_MAX_THROW * max(crest.y - foot.y, 0.05);
    return throwW > limit ? mix(crest, foot, limit / throwW) : foot;
}

// A plunging lip is thrown, not dropped: its tip leaves the crest at LIP_THROW_SPEED x the celerity and falls its drop
// under gravity, so it lands that far ahead however close under the crest the pitched wall's foot is (the landing
// measured on the wall put every tube's lip against its face, barely leaving the wave); a spilling lip (plunge 0)
// tumbles down the face to the wall's foot as measured. The landing keeps the foot's height (BreakerLip.Throw on the
// CPU).
vec3 lipThrowLanding(vec3 crest, vec3 landing, vec2 n, float plunge, float celerity)
{
    float drop = max(crest.y - landing.y, 0.05);
    float thrown = min(LIP_THROW_SPEED * celerity * sqrt(2.0 * drop / 9.81), LIP_THROWN_MAX * drop) * plunge;
    float throwW = dot(landing.xz - crest.xz, n);
    return throwW < thrown ? vec3(crest.x + n.x * thrown, landing.y, crest.z + n.y * thrown) : landing;
}

// As crestStateAt, with the lip's life (reach, crash), the breaker type, the rest distance ahead of the crest its
// landing is sampled at and how far it has closed since landing (ocean_lip_base.comp). y is the crash: the tongue's fall.
vec4 lipStateAt(vec2 xz, out float reach, out float plunge, out float restDist, out float flowRoot, out float close)
{
    reach = 0.0;
    plunge = 0.0;
    restDist = 0.0;
    flowRoot = 0.0;
    close = 0.0;
    vec4 carrier;
    float depth;
    if (!surfCarrierAt(0, xz, carrier, depth))
        return vec4(0.0);

    float k = max(length(carrier.yz), 1e-6);
    float wavelength = TWO_PI / k;
    float breakDistance = breakDistanceAt(xz);
    plunge = breakerPlunge(0, xz, carrier.yz / k, depth, breakDistance, carrier.w);
    vec2 life = lipLife(breakDistance, wavelength, plunge);
    reach = life.x;
    close = lipClose(breakDistance, wavelength);
    vec4 state = crestState(carrier.w, depth, breakDistance, wavelength, plunge);
    // A spilling tongue tumbles down the upper face only (LIP_SPILL_SPAN of the wall), a plunging one is thrown to the
    // foot; a spiller falls as the water runs out, a plunger as it crashes.
    float steep = faceDrive(carrier.w, depth, plunge, state, breakDistance, wavelength).x;
    restDist = lipRestDistance(wavelength, steep, state.z) * mix(LIP_SPILL_SPAN, 1.0, plunge);

    // The water's forward speed at the crest top, where the tongue's pour starts: breakerFace's flow at sn = 0.
    flowRoot = FLOW_CREST * (frame.carrierChop.y / k) * max(steep, state.y);
    float cap = carrier.w > 1e-4 ? clamp(WAVE_BREAKING_GAMMA * max(depth, 0.0) / carrier.w, 0.0, 1.0) : 1.0;
    state.y = mix(smoothstep(0.0, 1.0, (SPENT_CAP - cap) / 0.2), life.y, plunge);
    return state;
}

// Within LIP_DISTANCE of the camera: lip bases and spray are built for this segment.
bool lipInRange(BreakerSegment s)
{
    return distance(frame.cameraPos.xz, 0.5 * (s.ends.xy + s.ends.zw)) < LIP_DISTANCE;
}

// The ribbon draws only with the fold off (--ribbon-lip): the fold is the tongue by default, within the patch's reach.
bool ribbonInRange(BreakerSegment s)
{
    return breakers.info.z == 0 && lipInRange(s);
}

const float LIP_BASE_SPACING = 0.25;    // finest clipmap spacing: the lip's root sits on the full-detail surface ...
const float LIP_LANDING_SPACING = 2.0;  // ... but its landing is measured with the random sea's short components filtered out (swellShare fades 2-4 x this), so the throw and drop, which the tongue's shape is built from, do not shimmy with the chop frame to frame (on a 1 m wave the chop was a third of the drop)

// The tongue's life from the wave's travel since its break began (BreakerLip.Life on the CPU): x = reach, how far
// along its path (0 crest top .. 1 the foot) it has got, growing over LIP_REACH_TRAVEL of the crash distance; y = crash,
// 0 while it extends, rising to 1 over LIP_CRASH_TRAVEL more as it lands and goes to whitewater. A plunging tongue does
// this once. A spilling one extends and then holds: a steady tumble of whitewater off the crest for as long as the face
// stands (the pour carries the motion), falling only as the water runs out (lipStateAt). Cycling it (throw, crash,
// throw again) made every point of the crest a sawtooth: the tongue snapped back to the crest every couple of seconds.
vec2 lipLife(float breakDistance, float wavelength, float plunge)
{
    float travel = breakTravel(breakDistance, wavelength);
    float extend = LIP_REACH_TRAVEL * BREAK_CRASH * wavelength;
    float crash = LIP_CRASH_TRAVEL * BREAK_CRASH * wavelength;
    float r = clamp(travel / extend, 0.0, 1.0);
    return vec2(r * r, plunge * smoothstep(extend, extend + crash, travel)); // r^2: the tip falls under gravity, so its reach accelerates
}
float lipReach(float breakDistance, float wavelength) { return lipLife(breakDistance, wavelength, 1.0).x; }

// The lip's path at t (0 on the crest top .. 1 at the landing), in the crest frame (x shoreward, y up): a cubic Bezier
// from the crest top (carrying on from the back of the wave, so it is the wave's own surface going over) to the landing
// (throwDist ahead, drop below). A plunging lip (plunge 1) follows the barrel's arc: an ellipse from the crest top (its
// top, the lip leaving rising LIP_ARC_RISE) round to the landing LIP_ARC_END past the top, so it goes out past its
// widest point and comes down to land with the tip pointing down and back into the tube; the Bezier is fitted to the
// arc (handle 4/3 tan(E / 4)), its axes from the throw and the drop (a_x = throw / sin E, a_y = drop / (1 - cos E)).
// (Control points at 1.2 x the throw and -0.1 x the drop drew a flat ceiling and a corner: an angular notch.) A
// spilling one (0) falls down the face it hugs. xy = point, zw = d/dt (BreakerLip.Path on the CPU).
const float LIP_ARC_START = 0.61087; // 35 deg: the crest sits this far before the barrel's top, so the lip leaves it rising at that angle (a Bezier fitted from the top read as coming out flat) ...
const float LIP_ARC_END = 2.00713;   // ... and the tip lands 115 deg past the top, pointing down and back into the tube
const float LIP_ARC_SIN0 = 0.57358;  // sin and cos of LIP_ARC_START ...
const float LIP_ARC_COS0 = 0.81915;
const float LIP_ARC_SIN1 = 0.90631;  // ... and of LIP_ARC_END
const float LIP_ARC_COS1 = -0.42262;
vec4 lipPath(float t, float throwDist, float drop, float plunge)
{
    // A spiller: a cubic off the crest top and down the face it hugs.
    vec2 p1 = vec2(0.3 * throwDist, 0.08 * drop);
    vec2 p2 = vec2(0.75 * throwDist, -0.45 * drop);
    vec2 p3 = vec2(throwDist, -drop);
    float v = 1.0 - t;
    vec2 p = 3.0 * v * v * t * p1 + 3.0 * v * t * t * p2 + t * t * t * p3;
    vec2 d = 3.0 * v * v * p1 + 6.0 * v * t * (p2 - p1) + 3.0 * t * t * (p3 - p2);

    // A plunger: the ellipse itself, its angle linear in t from the crest to the landing, so the tip leaves the crest
    // rising LIP_ARC_START and the arc is exact over its 150 degrees (one cubic fitted to it was not).
    float ax = throwDist / (LIP_ARC_SIN0 + LIP_ARC_SIN1);
    float ay = drop / (LIP_ARC_COS0 - LIP_ARC_COS1);
    float theta = mix(-LIP_ARC_START, LIP_ARC_END, t);
    float dtheta = LIP_ARC_START + LIP_ARC_END;
    vec2 arc = vec2(ax * (LIP_ARC_SIN0 + sin(theta)), ay * (cos(theta) - LIP_ARC_COS0));
    vec2 darc = vec2(ax * cos(theta), -ay * sin(theta)) * dtheta;
    return mix(vec4(p, d), vec4(arc, darc), plunge);
}

// The fold at a crest point (breakerFace, ocean_patch.vert): w = the tongue's rest strip is 2 w wide (w per skin),
// 0 where there is no fold; reach along the path, size, throw, drop and fall as the ribbon has them, the reach faded
// out toward PATCH_DISTANCE where the ribbon takes over.
struct Fold
{
    float w;
    float reach;
    float size;
    float throwDist;
    float drop;
    float fall;
};

Fold noFold()
{
    return Fold(0.0, 0.0, 0.0, 0.0, 0.0, 0.0);
}

// The tongue's raggedness along the crest at a crest point: value noise over the point's offset across the carrier's
// travel (the wave's frame, so the pattern rides with the crest rather than lying on the sea floor), drifting in time.
// x = the cap's shift, in lobes of about 3 m; y = the reach's, with a finer octave on it.
vec2 foldRaggedness(vec2 crestXZ, vec2 n)
{
    float across = dot(crestXZ, vec2(-n.y, n.x)) * FOLD_RAG_SCALE;
    float t = mod(frame.cameraPos.w, LACE_WRAP) * FOLD_RAG_DRIFT;
    float coarse = valueNoise(vec2(across + 3.7, t + 1.3));
    float fine = valueNoise(vec2(across * 1.6 + 11.3, t * 1.6 + 5.1));
    return vec2(coarse, 0.7 * coarse + 0.3 * fine);
}

Fold foldFrom(float unbroken, float depth, float wavelength, float breakDistance, float plunge, vec4 state, vec2 crestXZ, vec2 n)
{
    if (state.x < 0.5 * LIP_MIN_CURL || state.z < 0.05 || gFoldThrow <= 0.0 || gFoldTaper <= 0.0)
        return noFold();

    // This column's curl and fall as they would be with its break clock and cap shifted by the raggedness: the curl
    // rises with the onset (crestState), so scaling it by the shifted onset over the unshifted one moves the column's
    // onset along the crest.
    vec2 rag = foldRaggedness(crestXZ, n);
    float cap = unbroken > 1e-4 ? clamp(WAVE_BREAKING_GAMMA * max(depth, 0.0) / unbroken, 0.0, 1.0) : 1.0;
    float capRag = clamp(cap + FOLD_RAG_CAP * (2.0 * rag.x - 1.0), 0.0, 1.0);
    float on = breakOnset(breakDistance, wavelength);
    float onRag = breakOnset(breakDistance + FOLD_RAG_TRAVEL * wavelength * (2.0 * rag.x - 1.0), wavelength);
    float curl = on > 1e-3 ? state.x * onRag / on : 0.0;
    float onset = smoothstep(LIP_MIN_CURL, LIP_MIN_CURL + FOLD_ONSET, curl);
    if (onset <= 0.0)
        return noFold();

    vec2 life = lipLife(breakDistance, wavelength, plunge);
    Fold f;
    f.fall = mix(smoothstep(0.0, 1.0, (SPENT_CAP - capRag) / 0.2), life.y, plunge);
    f.drop = max(gFoldDrop, 0.02 * state.z);
    f.throwDist = max(gFoldThrow, 0.05);
    f.size = curl * max(state.z, LIP_FACE_SIZE * f.drop);
    float fade = 1.0 - smoothstep(PATCH_DISTANCE - PATCH_FADE, PATCH_DISTANCE, distance(frame.cameraPos.xz, crestXZ));
    // The landed tongue stays: the tube closes by the crest dropping onto the landing (crestState's lift falls away as
    // the lip lands) and the wall easing, the tongue becoming the roller's front. (Fading the reach with the fall drew
    // the tongue back up to the crest.)
    float ragged = 1.0 - FOLD_RAG_REACH * (1.0 - 0.7 * plunge) * rag.y;
    f.reach = life.x * ragged * fade * onset * gFoldTaper;

    // The strip spans the rest distance the lip bases sampled the landing at (the wall's rest span, a spiller's half of
    // it), as far as the tongue has reached: the outer skin over its first half, the inner skin over the second, whose
    // last row is the wall point that far down in rest, the landing itself once the tongue has reached the foot.
    float rest = lipRestDistance(wavelength, faceDrive(unbroken, depth, plunge, state, breakDistance, wavelength).x, state.z) * mix(LIP_SPILL_SPAN, 1.0, plunge);
    f.w = LIP_FOLD_SPAN * 0.5 * rest * f.reach;
    return f;
}

// The fold's half strip width at a crest point xz in the current field (ocean_patch.vert sizes its rows from it).
float foldWidthAt(vec2 xz)
{
    vec4 carrier;
    float depth;
    if (breakers.info.z == 0 || !surfCarrierAt(0, xz, carrier, depth))
        return 0.0;

    float k = max(length(carrier.yz), 1e-6);
    float wavelength = TWO_PI / k;
    vec2 n = carrierDirection(0, xz, carrier.yz / k);
    float breakDistance = breakDistanceAt(xz);
    float plunge = breakerPlunge(0, xz, n, depth, breakDistance, carrier.w);
    vec4 state = crestState(carrier.w, depth, breakDistance, wavelength, plunge);
    return foldFrom(carrier.w, depth, wavelength, breakDistance, plunge, state, xz, n).w;
}

// Spray particles: ring buffer written by ocean_spray_*.comp, drawn by ocean_spray.vert.
const uint SPRAY_MAX = 16384u;
struct SprayParticle
{
    vec4 positionLife; // xyz world, w = seconds left
    vec4 velocitySize; // xyz m/s, w = radius (m)
    vec4 motion;       // x = air drag (1/s), y = share of gravity: spray falls, mist hangs in the air;
                       // z = growth of its radius (m/s) as it disperses; w = opacity
};

const vec4 SPRAY_MOTION = vec4(1.2, 1.0, 0.6, 1.0);  // droplets: slowed quickly in air, falling, misting out
const vec4 MIST_MOTION = vec4(3.0, 0.1, 0.25, 0.35); // mist: fine droplets, drifting and settling slowly, thin

// Read-only except in the spray compute shaders (they define SPRAY_WRITABLE): vertex stages may not see writable buffers.
#ifdef SPRAY_WRITABLE
#define SPRAY_ACCESS
#else
#define SPRAY_ACCESS readonly
#endif

// Surf foam left behind by breakers (ocean_surf_foam.comp): coverage per cell of the depth map's grid, decaying.
// Read-only except in that compute shader, which defines SURF_FOAM_WRITABLE.
#ifdef SURF_FOAM_WRITABLE
#define SURF_FOAM_ACCESS
#else
#define SURF_FOAM_ACCESS readonly
#endif

const int SURF_FOAM_RES = 512;

layout(std430, set = 0, binding = 8) SURF_FOAM_ACCESS buffer SurfFoam
{
    float values[];
} surfFoam;

// Lip bases (ocean_lip_base.comp): per lip base slot (a crossed edge of the crest's marching squares, shared by the two
// segments meeting there: BreakerSegment.slots), the drawn surface at the segment end seated on the crest: (position,
// zone weight), (the lip's throw ahead in world metres, face whitewater, rest xz), the crest's surf state there
// (crestStateAt: curl, the lip's fall, H, and in place of the lift the water's flow speed at the root, lipStateAt's
// flowRoot), the landing (the drawn surface's position at the lip's throw ahead of
// the crest, w = reach) and the surface normal at the crest (w = plunge). Written every frame for every slot in use;
// where the state is under the lip thresholds the slot holds no lip: position.w = 1 and the rest zero, and a segment
// with such an end never draws.
#ifdef LIP_BASES_WRITABLE
#define LIP_BASES_ACCESS
#else
#define LIP_BASES_ACCESS readonly
#endif

struct LipBase
{
    vec4 position;
    vec4 surface;
    vec4 state;
    vec4 landing;
    vec4 normal;
};

layout(std430, set = 0, binding = 9) LIP_BASES_ACCESS buffer LipBases
{
    LipBase ends[];
} lipBases;

// A segment's lip base at one end. Every slot in use is written every frame (a stale or zeroed slot once drew the lip as
// a slab across the scene); one holding no lip has position.w = 1.
LipBase lipBaseAt(BreakerSegment s, int end)
{
    return lipBases.ends[int(end == 0 ? s.slots.x : s.slots.y)];
}

bool lipBaseEmpty(LipBase b)
{
    return b.position.w >= 1.0;
}

layout(std430, set = 0, binding = 7) SPRAY_ACCESS buffer Spray
{
    uint head;
    uint pad0;
    uint pad1;
    uint pad2;
    SprayParticle particles[SPRAY_MAX];
} spray;

// Splashes (OceanRenderer.Prepare, from ZoneFlowSim.Agitate): where sea-zone water slammed into a wall it can only climb,
// or landed from a pour, this tick. ocean_spray_splash.comp throws spray there.
const float SPLASH_WALL = 0.0;
const float SPLASH_POUR = 1.0;

struct WaterSplash
{
    vec4 positionSpeed; // xyz = a wall's foot on the water surface, or where a pour lands; w = impact speed (m/s)
    vec4 direction;     // xy = unit direction (world x, z) into the wall or along the pour; z = depth (m) at a wall,
                        // the pour's discharge per metre of lip (m²/s); w = kind (SPLASH_*)
};

layout(std430, set = 0, binding = 12) readonly buffer WaterSplashes
{
    uint count;
    uint pad0;
    uint pad1;
    uint pad2;
    WaterSplash items[];
} waterSplashes;

// Zone flow (OceanRenderer.WriteZoneFlow, from ZoneFlowSim.PackFlow): the sea-zone sim's own 1 m cells, per cell
// half (velocity x, z) and half (turbulence, 0). Empty (size 0) when there is no sim or it is too big.
layout(std430, set = 0, binding = 13) readonly buffer ZoneFlow
{
    vec4 rect;   // xy = world corner of the grid, z = cell size (m)
    ivec4 size;  // x = cells across, y = cells down
    uvec2 cells[];
} zoneFlow;

vec3 zoneFlowCell(int i)
{
    uvec2 c = zoneFlow.cells[i];
    return vec3(unpackHalf2x16(c.x), unpackHalf2x16(c.y).x);
}

// The sim's flow at xz, bilinear between cell centres: xy = velocity (m/s), z = turbulence (0..1). Still outside it.
vec3 zoneFlowAt(vec2 xz)
{
    ivec2 n = zoneFlow.size.xy;
    vec2 f = (xz - zoneFlow.rect.xy) / max(zoneFlow.rect.z, 1e-3) - 0.5;
    ivec2 c = ivec2(floor(f));
    if (any(lessThan(c, ivec2(0))) || any(greaterThanEqual(c, n - 1)))
        return vec3(0.0);

    vec2 t = f - vec2(c);
    int i = c.y * n.x + c.x;
    vec3 a = mix(zoneFlowCell(i), zoneFlowCell(i + 1), t.x);
    vec3 b = mix(zoneFlowCell(i + n.x), zoneFlowCell(i + n.x + 1), t.x);
    return mix(a, b, t.y);
}

#ifdef SPRAY_WRITABLE
// Random numbers for spawning spray: a hash chain seeded per invocation.
uint hash(uint x)
{
    x ^= x >> 16;
    x *= 0x7feb352du;
    x ^= x >> 15;
    x *= 0x846ca68bu;
    x ^= x >> 16;
    return x;
}

float random(inout uint state)
{
    state = hash(state);
    return float(state) * (1.0 / 4294967295.0);
}
#endif

float softPlus(float x)
{
    return max(x, 0.0) + log(1.0 + exp(-abs(x)));
}

// 0 below x = 0, x up to 1, 1 above, its corner at 0 rounded over about 1 / k0 and at 1 over 1 / k1; d = its slope.
float softRamp(float x, float k0, float k1, out float d)
{
    d = 1.0 / (1.0 + exp(-k0 * x)) - 1.0 / (1.0 + exp(-k1 * (x - 1.0)));
    return softPlus(k0 * x) / k0 - softPlus(k1 * (x - 1.0)) / k1;
}

// Breaker face at rest position x0 (docs/breaking-waves.md, rendering step 3), a function of the surf carrier alone:
// the nearest crest is where its phase is 0, found from x0 by Newton steps along the local wavevector (two: the second
// takes the first's error, from the lag map's wavenumber stepping against k(h), well under a millimetre, so sn moves one
// for one with x0 as the derivatives assume) and refined once there; sn is the distance from it along the crest's
// direction of travel (carrierDirection, smooth where the map's own gradient is not) (+ shoreward), measured along its normal, and the wavelength and the crest's
// surf state come from the carrier there. Measuring a distance rather than dividing the phase keeps sn smooth: the lag
// map is bilinear, so the phase has kinks at its texel lines. The face is continuous in space and time; the crest
// polylines (BreakerFaces) only carry the lips. Around the crest: an asymmetric
// lift, steep in front as it curls, the crest peaked, and the whole crest pitched shoreward so the upper face stands as
// a near-vertical wall, with the matching slope and Jacobian terms. The heightfield cannot overhang; the lip mesh (step
// 4) throws the curl from the top of the wall. Returns the foam weight. BreakerFace.Apply mirrors this on the CPU
// (OceanSurface), so physics feels the wave that is drawn: keep the two in step. tap = the coastal field (carrierField).
float breakerFace(int tap, vec2 x0, inout float height, inout float Dx, inout float Dz, inout float hx, inout float hz,
    inout float jxx, inout float jzz, inout float jxz, inout vec2 flow)
{
    vec4 here;
    float depthHere;
    if (breakers.info.w == 0 || !surfCarrierAt(tap, x0, here, depthHere))
        return 0.0;

    float phase = here.x - TWO_PI * floor(here.x / TWO_PI + 0.5);
    vec4 crest;
    float depth;
    vec2 guess = x0 - here.yz * (phase / dot(here.yz, here.yz));
    if (!surfCarrierAt(tap, guess, crest, depth))
        return 0.0;

    guess -= crest.yz * ((crest.x - TWO_PI * floor(crest.x / TWO_PI + 0.5)) / dot(crest.yz, crest.yz));
    if (!surfCarrierAt(tap, guess, crest, depth))
        return 0.0;

    float k = max(length(crest.yz), 1e-6);
    vec2 n = carrierDirection(tap, guess, crest.yz / k);
    float sn = dot(x0 - guess, n) + (crest.x - TWO_PI * floor(crest.x / TWO_PI + 0.5)) / k;
    float wavelength = TWO_PI / k;
    float fadeEnd = min(PITCH_FADE * wavelength, PITCH_MAX);

    // The window runs on the phase at x0 (as a distance at the crest's wavenumber), not on sn: the phase hands over to
    // the next crest at exactly +-pi, where this reaches half a wavelength, past fadeEnd, whatever shoaling does to the
    // spacing ahead; sn there can still be short of fadeEnd.
    float reach = abs(phase) / k;
    if (reach >= fadeEnd)
        return 0.0;

    float breakDistance = breakDistanceAt(guess);
    float plunge = breakerPlunge(tap, guess, n, depth, breakDistance, crest.w);
    vec4 state = crestState(crest.w, depth, breakDistance, wavelength, plunge);
    vec2 drive = faceDrive(crest.w, depth, plunge, state, breakDistance, wavelength);
    float curl = state.x;
    float broken = state.y;
    float amplitude = state.w;
    float amount = drive.x;
    if (amount <= 0.0 && amplitude < 1e-3)
        return 0.0;

    // The fold (--fold-lip), on the fine patch only (ocean_patch.vert, gFoldTongue): the patch's rest strip just ahead
    // of the crest, 2 foldW wide, is the tongue, the first foldW its outer skin from the crest top to the tip, the
    // second its inner skin back to the root. The clipmap never folds: it draws the plain wall under the tongue, which
    // is the tube's back wall, and the patch's rows beyond its strip draw that same wall, so the two meshes agree
    // wherever they overlap. (Collapsing the clipmap's strip onto the crest left a sliver there, the random sea's
    // height varying across the strip; shifting the face beyond the strip put a seam at the patch's edge.) The strip
    // grows with the lip's reach (lipLife) and shrinks away as a plunging lip crashes, so the tongue never jumps.
    Fold f = gFoldTongue && breakers.info.z != 0 ? foldFrom(crest.w, depth, wavelength, breakDistance, plunge, state, guess, n) : noFold();
    bool fold = f.w > 0.02;
    float foldW = f.w;

    // Everything fades out before the trough, where the phase hands over to the next crest (window, dwindow = its slope).
    float fadeStart = PITCH_REACH / PITCH_FADE * fadeEnd;
    float t = clamp((reach - fadeStart) / (fadeEnd - fadeStart), 0.0, 1.0);
    float window = 1.0 - t * t * (3.0 - 2.0 * t);
    float dwindow = -sign(phase) * 6.0 * t * (1.0 - t) / (fadeEnd - fadeStart) * length(here.yz) / k; // reach moves at k(h) here / k

    float width = sn < 0.0 ? BACK_WIDTH * wavelength : max(mix(BACK_WIDTH, FRONT_WIDTH, amount) * wavelength, MIN_FRONT);
    float e = exp(-(sn * sn) / (width * width));
    float h = amplitude * e * window;
    float dh = amplitude * e * (dwindow - 2.0 * sn / (width * width) * window);

    // Peak: as it stands up and curls the wave gathers into a narrow crest over flat troughs. Height above the trough,
    // as a share q of the carrier's own crest-to-trough span, goes to q^PEAK (linear past the crest, unchanged below the
    // trough). The span is the carrier's: its bound harmonics put its crest 1 + s + s^2 and its trough 1 - s + s^2 (over
    // the norm) from the mean (swellShare), not +-H / 2; with +-H / 2 the harmonic crest sat above the reference and the
    // linear continuation tripled the excess (a 2.7 m wave drew a 5 m crest once its height was held past gamma h).
    float a = 0.5 * state.z;
    if (a > 0.05 && amount > 0.0)
    {
        float w = amount * window;
        float dw = amount * dwindow;
        float gamma = mix(1.0, PEAK, w);
        float s = carrierSharpness(state.z, depth);
        float norm = sqrt(1.0 + s * s * (1.0 + s * s));
        float crestAmp = a * (1.0 + s + s * s) / norm;
        float troughAmp = a * (1.0 - s + s * s) / norm;
        float span = crestAmp + troughAmp;
        float q = (height + troughAmp) / span;
        float peaked = q <= 0.0 ? q : q < 1.0 ? pow(q, gamma) : 1.0 + gamma * (q - 1.0);
        float dq = q <= 0.0 ? 1.0 : q < 1.0 ? gamma * pow(q, gamma - 1.0) : gamma;
        float dgamma = q <= 0.0 ? 0.0 : q < 1.0 ? peaked * log(q) : q - 1.0; // d peaked / d gamma, as the window fades
        float across = span * dgamma * (PEAK - 1.0) * dw;
        height += span * (peaked - q);
        hx = hx * dq + across * n.x;
        hz = hz * dq + across * n.y;
    }

    // Pitch: the breaking wave's face is drawn back under its crest. The crest stays where the swell has it; from it to
    // the foot the peaked crest's upper face is squeezed to 1 - PITCH of its width, a near-vertical wall for the lip to
    // throw from, over a concave foot, and the trough ahead gives back what the wall drew in. As a wave passes, the water
    // ahead of it draws back toward it, runs up the wall into the crest, and behind the crest is left as the swell had
    // it. (Leaning the crest forward over its back instead dragged the back of every breaker seaward at 0.6 c.)
    float front = PITCH_FRONT * wavelength;
    float recover = PITCH_RECOVER * wavelength;
    float dWall, dRecover;
    float wall = softRamp(sn / front, PITCH_TOP, PITCH_FOOT, dWall);
    float back = softRamp((sn - front) / recover, PITCH_FOOT, PITCH_END, dRecover);
    float pitch = clamp(1.0 - WALL_WIDTH * state.z / front, PITCH, PITCH_SQUEEZE_MAX);
    float lean = pitch * front * amount * smoothstep(0.0, PITCH_DRY_DEPTH, depthHere);
    float tent = -lean * (wall - back);
    float shift = tent * window;
    float dshift = -lean * (dWall / front - dRecover / recover) * window + tent * dwindow;

    height += h;
    hx += dh * n.x;
    hz += dh * n.y;
    Dx += shift * n.x;
    Dz += shift * n.y;
    jxx -= dshift * n.x * n.x;
    jzz -= dshift * n.y * n.y;
    jxz -= dshift * n.x * n.y;

    // The fold: a patch vertex in the strip is placed on the lip path (lipPath, the ribbon's) from the crest point the
    // segment's lip bases measured (gFoldCrest), the inner skin a body's thickness inside the outer. Its normal is the
    // skin's, written as the heightfield slope the fragment rebuilds it from (the inner skin's, facing down, is stored
    // facing up and flipped toward the eye there). The strip's last row, the inner skin's root at the crest, joins the
    // next row down the wall: the tube's back.
    float foldFoam = 0.0;
    bool onStrip = fold && sn >= 0.0 && sn < 2.0 * foldW;
    vec2 stripFlow = vec2(0.0);
    if (onStrip)
    {
        // The barrel: the outer skin runs from the crest point the segment's lip bases measured (gFoldCrest) out along
        // the lip path to the tip; the inner skin is the tube's inside, a C from the tip back along the lip's underside
        // (the ceiling: the path a body's thickness inside the outer skin, FOLD_CEILING of its rows) to the root inside
        // the wall under the crest, then down the back wall to the foot (the wall at each vertex's own rest distance, its
        // unfolded position, which the evaluation above already gave), so the strip's last row meets the wall exactly
        // where the landing was sampled; the trough between the foot and the landing, the heightfield, is the floor.
        // (A straight inner skin from the tip to the foot closed the tube with a flat facet: a dark angular notch.)
        // Once landed, the tongue closes (lipClose): the roof sinks onto the wall under it, each row to its own rest
        // point (unfolded, the plain wall the clipmap draws), so by the end the strip is that wall and nothing else,
        // the throw shortening with it (the lip bases) and the tube's shadow going; the whitewater of the crash stays on
        // the wall as the roller's front. (Kept thrown through the collapse it was a flat white slab for a period;
        // morphed to the spiller's tumble instead, a white sheet with a sawtooth edge lay on the face.)
        bool outer = sn < foldW;
        float shade = 0.0;
        float close = lipClose(breakDistance, wavelength);
        float tube = plunge * f.reach * (1.0 - close);
        vec3 fwd = vec3(n.x, 0.0, n.y);
        vec4 tipPath = lipPath(f.reach, f.throwDist, f.drop, plunge);
        vec3 tip = gFoldCrest + fwd * tipPath.x + vec3(0.0, tipPath.y, 0.0);
        vec3 unfolded = vec3(x0.x + Dx, height, x0.y + Dz);
        float u;
        vec3 pos;
        vec2 along;
        if (outer)
        {
            u = sn / foldW;
            vec4 c = lipPath(u * f.reach, f.throwDist, f.drop, plunge);
            pos = gFoldCrest + fwd * c.x + vec3(0.0, c.y, 0.0);
            along = normalize(c.zw + vec2(1e-5, 0.0));
        }
        else
        {
            float v = (sn - foldW) / foldW;
            if (v < FOLD_CEILING)
            {
                u = 1.0 - v / FOLD_CEILING;
                vec4 c = lipPath(u * f.reach, f.throwDist, f.drop, plunge);
                vec2 tangent = normalize(c.zw + vec2(1e-5, 0.0));
                float thick = lipBodyThickness(f.size, u) * (1.0 - smoothstep(0.92, 1.0, u));
                vec2 q = c.xy + vec2(tangent.y, -tangent.x) * thick;
                pos = gFoldCrest + fwd * q.x + vec3(0.0, q.y, 0.0);
                along = -tangent;
                shade = tube * mix(1.0, TUBE_MOUTH, u);
            }
            else
            {
                // The back: from the ceiling's root down the wall to the foot, through a fillet (a quadratic that carries
                // the ceiling's tangent on past the root for a body thickness before turning down), so the inside is one
                // curve from the tip to the foot, not a ceiling meeting a wall at a corner; the bend lies inside the wall.
                u = 0.0;
                vec4 c0 = lipPath(0.0, f.throwDist, f.drop, plunge);
                vec2 t0 = normalize(c0.zw + vec2(1e-5, 0.0));
                float rootThick = lipBodyThickness(f.size, 0.0);
                vec2 q0 = c0.xy + vec2(t0.y, -t0.x) * rootThick;
                vec3 root = gFoldCrest + fwd * q0.x + vec3(0.0, q0.y, 0.0);
                vec3 handle = root - (fwd * t0.x + vec3(0.0, t0.y, 0.0)) * rootThick;
                float w = (v - FOLD_CEILING) / (1.0 - FOLD_CEILING);
                float ww = 1.0 - w;
                pos = ww * ww * root + 2.0 * ww * w * handle + w * w * unfolded;
                vec3 d = 2.0 * ww * (handle - root) + 2.0 * w * (unfolded - handle);
                along = normalize(vec2(dot(d.xz, n), d.y) + vec2(1e-5, 0.0));
                shade = tube * (1.0 - smoothstep(TUBE_BACK_LIT, 1.0, w));
            }
        }

        float wallHx = hx, wallHz = hz, wallJxx = jxx, wallJzz = jzz, wallJxz = jxz;
        pos = mix(pos, unfolded, close);
        Dx = pos.x - x0.x;
        Dz = pos.z - x0.y;
        height = pos.y;

        // The skin's normal, perpendicular to its path in the crest frame: away from the tube on the outer skin, into it
        // on the ceiling (whose tangent runs back toward the wall), stored facing up and flipped toward the eye there.
        vec2 nrm = vec2(-along.y, along.x);
        vec3 N = vec3(n.x * nrm.x, nrm.y, n.y * nrm.x);
        if (N.y < 0.0)
            N = -N;

        N.y = max(N.y, 0.08);
        hx = mix(-N.x / N.y, wallHx, close);
        hz = mix(-N.z / N.y, wallHz, close);
        jxx = wallJxx * close;
        jzz = wallJzz * close;
        jxz = wallJxz * close;
        float grown = smoothstep(0.0, FOLD_GROW, f.reach * (f.throwDist + f.drop));
        gFoldWeight = grown * (1.0 - close);
        gFoldInner = outer ? 0.0 : 1.0;
        gFoldBody = vec3(u, lipBodyThickness(f.size, u), shade);

        // The lace on the tongue (ocean.frag) is drawn at the surface's density along the skin, not the strip's rest
        // density: the strip is as wide in rest as the landing's rest distance, folded onto a path a fraction of that,
        // so in rest the lace was squeezed past what a pixel resolves and the floor fell back to flat white. The rest
        // offset here puts this vertex at its arc length along the skin (the outer skin's path walked in four steps,
        // the inner skin's chord from the tip), stretched by FOLD_POUR_STRETCH and FOLD_LACE_FINE times denser by the
        // tip; zero at the root, so the lace carries on from the surface's. The density is applied here, to the arc
        // length alone: applied in the fragment to the whole lace position, hundreds of metres of world coordinate
        // were scaled by a factor that varied across the tongue, and the position's derivative was metres a pixel,
        // so the lace fell back to its flat coverage: a tongue anywhere away from the origin was flat white.
        float arc = 0.0;
        vec2 prev = vec2(0.0);
        float end = outer ? u * f.reach : f.reach;
        for (int i = 1; i <= 4; ++i)
        {
            vec2 q = lipPath(end * float(i) / 4.0, f.throwDist, f.drop, plunge).xy;
            arc += distance(q, prev);
            prev = q;
        }

        if (!outer)
            arc += distance(pos, tip);

        gFoldPour = n * (sn - FOLD_POUR_STRETCH * arc * mix(1.0, FOLD_LACE_FINE, u));
        // The crash whitens the lip from the tip back toward the root: FOLD_CRASH_TIP of the path as the lip lands (its
        // fall), the rest as the tongue closes, so a landed lip keeps a glassy body and whitens from the impact.
        float whiteFrom = 1.0 - FOLD_CRASH_TIP * f.fall - (1.0 - FOLD_CRASH_TIP) * close;
        float crashed = smoothstep(whiteFrom - 0.1, whiteFrom + 0.1, u) * (1.0 - smoothstep(1.2 * foldW, 2.0 * foldW, sn)); // fading down the inside's last rows to the wall's own foam at the foot
        foldFoam = max(mix(0.55 + 0.45 * u, smoothstep(mix(0.9, 1.0 - FOLD_FEATHER, curl), 1.0, u), plunge), crashed) * window * grown;

        // The tongue's water pours from the root to the tip: the crest's own flow at the root (continuous with the
        // heightfield there), FLOW_TONGUE x celerity faster by the tip, and on at the tip's speed along the ceiling to
        // the wall; forward in rest space on both skins, since both run toward the wall's foot.
        stripFlow = (frame.carrierChop.y / k) * (FLOW_CREST * max(amount, broken) + FLOW_TONGUE * (outer ? u : 1.0)) * window * n;
    }

    // Whitewater: ahead of a throwing crest where its lip lands (the crest itself stays glassy under the lip); a spilling
    // crest's whitewater tumbling down its front; then, once it has collapsed, a roller on the bore front trailing foam
    // behind it, so the crash reads as the wave itself turning white rather than a patch laid over it. Its envelope is
    // metres from the wave's height (WHITE_*), not the face's wavelength-scaled width, and it fades out over the last of
    // the water (WHITE_DRY_DEPTH): at the waterline the swash's front carries the foam on up the beach.
    float wet = smoothstep(0.0, WHITE_DRY_DEPTH, depthHere);
    float whiteFront = clamp(WHITE_FRONT * state.z, WHITE_FRONT_MIN, WHITE_FRONT_MAX);
    float whiteWidth = sn < 0.0 ? WHITE_BACK * whiteFront : whiteFront;
    float ew = exp(-(sn * sn) / (whiteWidth * whiteWidth));
    float throwing = smoothstep(0.6, 1.0, curl) * (sn > 0.0 ? 0.6 * ew : 0.15 * ew * ew * ew) * smoothstep(0.0, THROW_FOAM_HEIGHT, state.z);
    float spilling = drive.y * (sn > 0.0 ? ew : ew * ew);
    float roller = broken * (sn > 0.0 ? ew : max(ew, ROLLER_TRAIL * exp(sn / max(ROLLER_TAIL * state.z, ROLLER_TAIL_MIN))));

    // Flow (FLOW_*): the water's forward speed relative to the rest grid, along n: rising up the back as the face stands,
    // with the crest at the top (lipStateAt's flowRoot, where the tongue's pour starts), with the crest down the front
    // as the roller, gone before the trough where the phase hands over.
    float strength = max(amount, broken);
    float rise = smoothstep(-FLOW_BACK * wavelength, 0.0, sn);
    float along = sn < 0.0 ? rise * rise : 1.0 - smoothstep(0.0, FLOW_FRONT * wavelength, sn);
    vec2 wallFlow = FLOW_CREST * (frame.carrierChop.y / k) * strength * along * window * n;
    flow += onStrip ? mix(stripFlow, wallFlow, lipClose(breakDistance, wavelength)) : wallFlow;

    return max(max(max(throwing, spilling), roller) * window * wet, foldFoam);
}

// Bathymetry map (BathymetryTexture): r = signed depth (m), g = sim level, b = sea-zone flag, a = flow whitewater.
// Layer 0 is the latest sim tick, layer 1 the tick before; sea-zone cells blend between them by the tick alpha
// (foamParams.w), so the 60 Hz sim moves smoothly at any frame rate. Pit sentinels never blend.
vec4 depthMapAt(vec2 uv)
{
    vec4 now = textureLod(depthMap, vec3(uv, 0.0), 0.0);
    if (now.b <= 0.0 && now.a <= 0.0)
        return now;

    vec4 before = textureLod(depthMap, vec3(uv, 1.0), 0.0);
    vec4 m = mix(before, now, frame.foamParams.w);
    m.r = min(now.r, before.r) < -500.0 ? now.r : m.r;
    return m;
}

// Sea-zone sample: g = absolute sim surface height (m), b = zone flag. Zero outside the map.
vec2 zoneSurface(vec2 xz)
{
    vec2 uv = (xz - frame.depthRect.xy) / frame.depthRect.z;
    if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0))))
        return vec2(0.0);
    return depthMapAt(uv).gb;
}

// The wind sea the cascades show at xz, per cascade: returned, the share of its height (displacement); slopes, the share
// of its slopes (normals, whitecaps). Over F m of open water upwind (rain_cover.glsl windCover) the wind U raises waves
// only up to the JONSWAP peak kp = (22 (g² / (U F))^(1/3))² / g: shorter ones are saturated as on the open sea, longer
// ones missing. So a cascade keeps the part of its band at or above kp, by height variance (a k^-3 tail: ∝ k^-2 between
// the edges) and by slope variance (∝ ln k). At 10 m/s a 20 m pond keeps ripples under 0.2 m, a 200 m cove chop under
// 1 m, open water all of it (fully grown by FETCH_REACH, where fetch stops being measured). Under cover there is no
// wind, and both fall with the exposure.
vec3 windSea(vec2 xz, out vec3 slopes)
{
    vec2 wind = windCover(xz);
    float wp = 22.0 * pow(9.81 * 9.81 / (max(frame.windSea.x, 0.5) * max(wind.y, 0.5)), 1.0 / 3.0);
    vec3 kp = vec3(wp * wp / 9.81);
    vec3 lo = frame.cascadeKLo.xyz;
    vec3 hi = vec3(frame.cascadeKLo.yz, 0.5 * TWO_PI * CASCADE_N / frame.cascadeL.z);
    vec3 cut = clamp(kp, lo, hi);
    float grown = smoothstep(0.5 * FETCH_REACH, FETCH_REACH, wind.y);
    vec3 heights = (1.0 / (cut * cut) - 1.0 / (hi * hi)) / (1.0 / (lo * lo) - 1.0 / (hi * hi));
    slopes = wind.x * mix(log(hi / cut) / log(hi / lo), vec3(1.0), grown);
    return wind.x * mix(sqrt(heights), vec3(1.0), grown);
}

// Water depth (m) from the bathymetry map; deep outside it.
float waterDepth(vec2 xz)
{
    vec2 uv = (xz - frame.depthRect.xy) / frame.depthRect.z;
    if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0))))
        return frame.depthRect.w;
    return depthMapAt(uv).r;
}

// Swash (docs/breaking-waves.md): run-up events per shoreline point. Each bore that reaches the shoreline below a beach
// point collapses into a sheet whose edge runs up the beach ballistically (Shen and Meyer: x = u0 t - a t^2 / 2 along the
// beach, a = g tan(beta) x (1 + SWASH_FRICTION) on the way up, SWASH_BACK x g tan(beta) back down, so it drains slower
// than it rushed up) and the surface above a point is the highest of the last SWASH_EVENTS bores' sheets, so a bore
// arriving in a backwash pushes the edge out again rather than one half sine a period. Timing is the carrier's phase at
// the shoreline point below (found along the depth gradient); the amplitude is that bore's: the carrier's shoaled height
// at the shore for the crest that arrived (the set envelope it carried), less what the break has dissipated since it
// began (the break distance where the bore hands over, SWASH_SEA deep), so the small waves between sets barely move
// the edge and a set wave runs far up, and along the beach the edge is a wavy line. The sheet behind an edge is the
// Shen-Meyer lens, (x_edge - x)^2 / (9 g t^2), thick just behind a fresh edge and thinning as the sheet ages, over a
// minimum film. GPU only, like the faces.
const int SWASH_EVENTS = 4;          // bores remembered per point; the oldest fades out over its last period
const float SWASH_U0 = 1.2;          // the edge's speed as the bore collapses at the shoreline, x sqrt(g x the bore's energy height) (Shen and Meyer: 2, friction-free)
const float SWASH_FRICTION = 0.3;    // the up-rush decelerates at (1 + this) x g tan(beta) ...
const float SWASH_DECEL_MIN = 0.6;   // ... and at least this (m/s^2): bed friction on the thin tip, which on a gentle beach otherwise ran up for tens of seconds and the edge barely moved per wave
const float SWASH_BACK = 0.7;        // the backwash accelerates at this x the up-rush's deceleration
const float SWASH_EDGE_WIDTH = 0.5;  // m of sheet behind the edge marked as the water's edge (OceanPoint.swashEdge, ocean.frag's edge line)
const float SWASH_DISSIPATE = 100.0; // m of broken travel over which a bore's energy height falls by 1/e
const float SWASH_MAX = 2.0;         // vertical run-up at most (m)
const float SWASH_MIN_SHEET = 0.03;  // m: the film left as a sheet drains ...
const float SWASH_SHEET = 0.25;      // ... plus this x the height of the edge above the point: a film thinner than the voxel terrain's facets is cut by them
const float SWASH_LENS_MAX = 0.25;   // m: the sheet at most this thick
const float SWASH_LENS_TIME = 1.5;   // s: the lens's age is taken as at least this (its thickness goes as 1 / age^2)
const float SWASH_SEA = 0.5;         // water depth (m) over which the swash fades into the sea surface, and where the bore hands over to it
const float SWASH_SLOPE_SPAN = 6.0;  // m either side the beach slope is taken over: the foreshore's, not one 2 m depth cell's, whose slope varies by a third cell to cell and stepped the edge
const float SWASH_FOAM_FRONT = 2.0;  // m of sheet behind an up-rushing edge that is fresh foam (ocean_surf_foam.comp) ...
const float SWASH_FOAM_U0 = 3.0;     // ... at full strength from this edge speed (m/s)

// The swash sheet's surface above x0 (depth d: negative on land), or -1e3 where there is none. front: how fresh the
// up-rushing foam is here, 1 at the edge of a strong bore's up-rush falling to 0 SWASH_FOAM_FRONT behind it; only on the
// beach, never over the water. gSwashEdge: 1 at the edge of the sheet that is highest here, 0 SWASH_EDGE_WIDTH behind it.
float gSwashEdge = 0.0;

float swashSheet(vec2 x0, float d, out float front)
{
    front = 0.0;
    gSwashEdge = 0.0;
    if (d > SWASH_SEA || d < -SWASH_MAX || frame.carrier.w <= 0.0)
        return -1e3;

    // The shoreline below x0: along the depth gradient to where the depth reaches 0; the beach slope is the gradient's.
    vec2 grad = vec2(waterDepth(x0 + vec2(SWASH_SLOPE_SPAN, 0.0)) - waterDepth(x0 - vec2(SWASH_SLOPE_SPAN, 0.0)),
                     waterDepth(x0 + vec2(0.0, SWASH_SLOPE_SPAN)) - waterDepth(x0 - vec2(0.0, SWASH_SLOPE_SPAN))) / (2.0 * SWASH_SLOPE_SPAN);
    float g2 = dot(grad, grad);
    if (g2 < 1e-6)
        return -1e3;

    float slope = sqrt(g2);
    vec2 seaward = grad / g2;
    vec2 shore = x0 - d * seaward;
    if (!coastalSample(shore) || !cHas[0])
        return -1e3;

    int jc = int(frame.carrier.y);
    vec4 ref = mix(coastalRef(0, jc), coastalRef(0, jc + 1), frame.carrier.y - float(jc));
    float spatial = frame.carrier.x * dot(cDir[0], shore) + ref.x;
    float p = fract(-(spatial + frame.carrier.z) / TWO_PI); // periods since the last crest reached the shoreline
    float period = TWO_PI / max(frame.carrierChop.y, 1e-3);

    // The bore's energy height at the shore, before its set envelope: the carrier's shoaled height there, less the
    // break's dissipation over the distance it has been breaking as it hands over.
    float unbroken = 2.0 * sqrt(MAX_COHERENCE * frame.carrier.w) * ref.w * exp(-breakDistanceAt(shore + SWASH_SEA * seaward) / SWASH_DISSIPATE);
    float up = max(9.81 * slope * (1.0 + SWASH_FRICTION), SWASH_DECEL_MIN);
    float back = SWASH_BACK * up;
    float u0Max = sqrt(2.0 * up * SWASH_MAX / slope);
    float s0 = -d / slope; // metres up the beach from the shoreline (negative in the water)
    float sheet = -1e3;

    for (int n = 0; n < SWASH_EVENTS; ++n)
    {
        float since = p + float(n);
        float age = since * period;
        // The set envelope travels with the crests, so the crest that reached the shore `since` periods ago carried the
        // envelope `since` wavelengths back along the travel.
        float u0 = min(SWASH_U0 * sqrt(9.81 * unbroken * groupEnvelope(spatial + TWO_PI * since)), u0Max);
        float tUp = u0 / up;
        float dt = age - tUp;
        float xs = age < tUp ? age * (u0 - 0.5 * up * age) : 0.5 * u0 * tUp - 0.5 * back * dt * dt;
        xs *= 1.0 - smoothstep(float(SWASH_EVENTS - 1), float(SWASH_EVENTS), since);
        float behind = xs - s0;
        float t = max(age, SWASH_LENS_TIME);
        // Past the edge the sheet dives under the sand at twice the beach's slope, so the edge falls smoothly between
        // vertices rather than on them.
        float lens = behind > 0.0 ? min(max(behind * behind / (9.0 * 9.81 * t * t), SWASH_SHEET * slope * behind), SWASH_LENS_MAX) : 2.0 * slope * behind;
        float surface = -d + SWASH_MIN_SHEET + lens;
        if (surface > sheet)
        {
            sheet = surface;
            gSwashEdge = behind > 0.0 ? 1.0 - min(behind / SWASH_EDGE_WIDTH, 1.0) : 0.0;
        }

        if (d < 0.0 && age < tUp && behind >= 0.0 && behind < SWASH_FOAM_FRONT)
            front = max(front, (1.0 - behind / SWASH_FOAM_FRONT) * smoothstep(0.0, SWASH_FOAM_U0, u0));
    }

    return sheet;
}

// Surf foam left behind at xz, bilinear over the cells; 0 outside the map.
float surfFoamAt(vec2 xz)
{
    vec2 f = (xz - frame.depthRect.xy) / frame.depthRect.z * float(SURF_FOAM_RES) - 0.5;
    ivec2 i = ivec2(floor(f));
    if (i.x < 0 || i.y < 0 || i.x >= SURF_FOAM_RES - 1 || i.y >= SURF_FOAM_RES - 1)
        return 0.0;

    vec2 t = f - vec2(i);
    int k = i.x + i.y * SURF_FOAM_RES;
    return mix(mix(surfFoam.values[k], surfFoam.values[k + 1], t.x),
               mix(surfFoam.values[k + SURF_FOAM_RES], surfFoam.values[k + SURF_FOAM_RES + 1], t.x), t.y);
}

struct OceanPoint
{
    vec3 position;     // displaced world position
    vec4 cpuPartials;  // (hx, hz, λDxx, λDzz), zone-mixed
    float cpuDxz;
    vec3 shore;        // per cascade, the share of its slopes drawn: 0 on the beach, in a pond, deep in a sea cave .. 1 on open deep water
    float breakerFoam;
    float surfFoam;    // whitewater left behind by the surf (ocean_surf_foam.comp)
    float zone;        // 0..1 sea-zone weight (the sim owns the surface where 1)
    float swashEdge;   // 1 at the edge of the swash sheet drawn here, 0 half a metre behind it or where the sea is the surface (ocean.frag's edge line)
    vec2 flow;         // the water's forward speed (m/s): over a breaking wave along the crest's travel (breakerFace, FLOW_*), in sea zones the sim's current
    vec4 fold;         // x = 1 on a folded tongue vertex (the fine patch), y = 1 on its inner skin, the tube's ceiling, zw = the rest offset of its lace along the skin
    vec3 tongue;       // on a folded tongue: x = how far along its path (0 root .. 1 tip), y = the body's thickness there (m), z = how deep in the tube's shadow
    float turbulence;  // sea-zone water churned by a pour, a crash or a wall (ZoneFlowSim.Agitate), 0..1
};

// The analytic swell's height, displacement and their partials at a rest position.
struct Swell
{
    float height;
    float hx, hz;
    float Dx, Dz;
    float jxx, jzz, jxz;
};

Swell mixSwell(Swell a, Swell b, float t)
{
    return Swell(mix(a.height, b.height, t), mix(a.hx, b.hx, t), mix(a.hz, b.hz, t), mix(a.Dx, b.Dx, t), mix(a.Dz, b.Dz, t),
        mix(a.jxx, b.jxx, t), mix(a.jzz, b.jzz, t), mix(a.jxz, b.jxz, t));
}

// Layer A through one coastal field (tap 0 = current, 1 = fading out, -1 = open ocean), anti-aliased over [lo, hi]:
// components first, first + stride, ... and, for first = 0, the surf carrier. Several threads can split one sum. They
// take every stride-th component rather than a contiguous block each: the block split kept the coastal reference
// cache warm but lost the warp's coalesced reads of the wave array, and the lip-bases pass got a quarter slower.
Swell swellPart(vec2 x0, float lo, float hi, int tap, int first, int stride)
{
    Swell o = Swell(0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0);

    // Layer B: phase lag (refraction + depth dispersion), amplitude (shoaling), breaking cap, local direction.
    // Components come sorted by frequency, so the two references they interpolate between are cached and advanced.
    bool coast = tap >= 0;
    vec2 limits = coast ? coastalLimits(tap) : vec2(1.0);
    int refJ = -2;
    vec4 refLo = vec4(0.0);
    vec4 refHi = vec4(0.0);

    // Surf-zone coherence (OceanSwell.CoastalWaves): peak-band components hand their energy to one coherent carrier,
    // the wave the breakers contour, by the carrier's local k h (its eikonal wavenumber |k0 d + grad lag| x depth).
    float coherence = 0.0;
    vec4 carrierRef = vec4(0.0);
    vec2 carrierK = vec2(0.0);
    if (coast && frame.carrier.w > 0.0)
    {
        int jc = int(frame.carrier.y);
        carrierRef = mix(coastalRef(tap, jc), coastalRef(tap, jc + 1), frame.carrier.y - float(jc));
        carrierK = frame.carrier.x * cDir[tap] + carrierRef.yz;
        float depthK = max(cDepth[tap], 0.0);
        coherence = MAX_COHERENCE * (1.0 - smoothstep(COHERENT_KH, INCOHERENT_KH, waveNumberApprox(frame.carrierChop.y, depthK) * depthK));
    }

    int count = min(frame.counts.x, 512);
    for (int i = first; i < count; i += stride)
    {
        vec4 a = frame.waves[i].a;
        vec4 b = frame.waves[i].b;

        float w = smoothstep(lo, hi, TWO_PI / max(length(a.xy), 1e-6));
        if (w <= 0.0)
            continue;

        vec2 k = a.xy;
        float phase = a.z;
        float amplitude = a.w;
        vec2 chop = b.xy;

        if (coast)
        {
            int j = int(b.w);
            if (j != refJ)
            {
                refLo = j == refJ + 1 ? refHi : coastalRef(tap, j);
                refHi = coastalRef(tap, j + 1);
                refJ = j;
            }

            vec4 r = mix(refLo, refHi, b.w - float(j));
            k += r.yz;
            phase += r.x;
            float band = 1.0 - smoothstep(BAND_INNER, BAND_OUTER, abs(b.w - frame.carrier.y));
            float amp = r.w * limits.x * sqrt(1.0 - coherence * band);
            chop = length(chop) * amp * limits.y * k / max(length(k), 1e-6);
            amplitude *= amp;
        }

        float A = amplitude * w;
        float cx = chop.x * w;
        float cz = chop.y * w;

        // Deep-water k0 · x0 plus the lag; k (k0 + lag gradient) is the local wavevector for slopes.
        float theta = a.x * x0.x + a.y * x0.y + phase;
        float s = sin(theta);
        float c = cos(theta);

        o.height += A * c;
        o.hx -= A * k.x * s;
        o.hz -= A * k.y * s;
        o.Dx -= cx * s;
        o.Dz -= cz * s;
        o.jxx += cx * k.x * c;
        o.jzz += cz * k.y * c;
        o.jxz += cx * k.y * c;
    }

    if (coherence > 0.0 && first == 0)
    {
        // Its own breaking limit (OceanSwell.CarrierBreaking): it grows as it shoals until H = WAVE_BREAKING_GAMMA x depth,
        // and holds that height while its lip is in the air (heldCap, the break clock).
        float kl = max(length(carrierK), 1e-6);
        float unbroken = 2.0 * sqrt(coherence * frame.carrier.w) * carrierRef.w
            * groupEnvelope(frame.carrier.x * dot(cDir[tap], x0) + carrierRef.x);
        float depthC = max(cDepth[tap], 0.0);
        float capC = unbroken > 1e-4 ? clamp(WAVE_BREAKING_GAMMA * depthC / unbroken, 0.0, 1.0) : 1.0;
        float theta = frame.carrier.x * dot(cDir[tap], x0) + carrierRef.x + frame.carrier.z;

        // The held height is the wave's own, taken at its crest (the break clock is carried per position, and a wave's
        // crest and the trough ahead of it hold different values: taken at x0, or blended across the wavelength, it
        // pulled the drawn crest off the carrier crest): the crest behind's over most of the wavelength, handing over
        // to the crest ahead's around the trough (holdBlend), so the crest keeps its amplitude across its whole peak and
        // only the trough's depth varies (OceanSwell.SampleFields).
        float u = fract(theta / TWO_PI);
        vec2 kHat = carrierK / kl;
        float lambda = TWO_PI / kl;
        float hold = mix(breakHold(breakDistanceAt(x0 - kHat * (u * lambda)), lambda), breakHold(breakDistanceAt(x0 + kHat * ((1.0 - u) * lambda)), lambda), holdBlend(u));
        float A = 0.5 * unbroken * heldCap(capC, depthC, unbroken, hold);
        vec2 chop = A * frame.carrierChop.x * limits.y * carrierK / kl * smoothstep(lo, hi, TWO_PI / kl);

        // Shoaling peaks the carrier: bound harmonics (1, s, s^2), normalised to its energy (OceanSwell.CoastalWaves).
        float sharp = carrierSharpness(2.0 * A, depthC);
        float an = A / sqrt(1.0 + sharp * sharp * (1.0 + sharp * sharp));

        for (int h = 1; h <= CARRIER_SLOTS; ++h, an *= sharp)
        {
            vec2 k = float(h) * carrierK;
            float a = an * smoothstep(lo, hi, TWO_PI / (float(h) * kl));
            float s = sin(float(h) * theta);
            float c = cos(float(h) * theta);

            o.height += a * c;
            o.hx -= a * k.x * s;
            o.hz -= a * k.y * s;

            if (h == 1)
            {
                o.Dx -= chop.x * s;
                o.Dz -= chop.y * s;
                o.jxx += chop.x * carrierK.x * c;
                o.jzz += chop.y * carrierK.y * c;
                o.jxz += chop.x * carrierK.y * c;
            }
        }
    }

    return o;
}

Swell swellAt(vec2 x0, float lo, float hi, int tap)
{
    return swellPart(x0, lo, hi, tap, 0, 1);
}

// The swell at x0 summed by one of stride threads (see swellPart); add the parts, then call oceanFinish. While a coastal
// rebuild fades in, the swell is evaluated through both fields and blended (OceanSwell.SampleFields).
Swell swellShare(vec2 x0, float se, int first, int stride)
{
    bool coast = coastalSample(x0);
    Swell sw = swellPart(x0, 2.0 * se, 4.0 * se, coast ? 0 : -1, first, stride);
    if (coast && cFade < 1.0)
        sw = mixSwell(swellPart(x0, 2.0 * se, 4.0 * se, 1, first, stride), sw, cFade);

    return sw;
}

OceanPoint oceanFinish(vec2 x0, float se, Swell sw);

// The ocean surface at rest position x0, for a grid of effective spacing se (anti-aliasing filter width).
OceanPoint oceanAt(vec2 x0, float se)
{
    return oceanFinish(x0, se, swellShare(x0, se, 0, 1));
}

// Everything on top of the swell: breaker faces, swash, the GPU cascades, sea zones.
OceanPoint oceanFinish(vec2 x0, float se, Swell sw)
{
    float height = sw.height;
    float hx = sw.hx, hz = sw.hz;
    float Dx = sw.Dx, Dz = sw.Dz;
    float jxx = sw.jxx, jzz = sw.jzz, jxz = sw.jxz;

    // Breaker faces and swash are metres wide: a grid this coarse can't show them, so they fade out with the spacing
    // (smoothly, so no seam where one clipmap level hands over to the next) and far vertices skip the work.
    float near = 1.0 - smoothstep(SURF_SPACING_FULL, SURF_SPACING_NONE, se);
    float breakerFoam = 0.0;
    float swashEdge = 0.0;
    vec2 flow = vec2(0.0);
    gFoldWeight = 0.0;
    gFoldInner = 0.0;
    gFoldPour = vec2(0.0);
    gFoldBody = vec3(0.0);

    if (near > 0.0)
    {
        Swell before = Swell(height, hx, hz, Dx, Dz, jxx, jzz, jxz);
        breakerFoam = breakerFace(0, x0, height, Dx, Dz, hx, hz, jxx, jzz, jxz, flow);

        // While a rebuild crossfades in, the faces of both fields are blended as the swell is (swellShare): the field
        // fading out, or the open ocean where it has nothing.
        if (cFade < 1.0)
        {
            Swell old = before;
            vec2 flowOld = vec2(0.0);
            float foamOld = cHas[1] ? breakerFace(1, x0, old.height, old.Dx, old.Dz, old.hx, old.hz, old.jxx, old.jzz, old.jxz, flowOld) : 0.0;
            flow = mix(flowOld, flow, cFade);
            Swell blended = mixSwell(old, Swell(height, hx, hz, Dx, Dz, jxx, jzz, jxz), cFade);
            height = blended.height;
            hx = blended.hx;
            hz = blended.hz;
            Dx = blended.Dx;
            Dz = blended.Dz;
            jxx = blended.jxx;
            jzz = blended.jzz;
            jxz = blended.jxz;
            breakerFoam = mix(foamOld, breakerFoam, cFade);
        }

        breakerFoam *= near;
        flow *= near;
        Swell face = mixSwell(before, Swell(height, hx, hz, Dx, Dz, jxx, jzz, jxz), near);
        height = face.height;
        hx = face.hx;
        hz = face.hz;
        Dx = face.Dx;
        Dz = face.Dz;
        jxx = face.jxx;
        jzz = face.jzz;
        jxz = face.jxz;

        // Swash: the beach holds the sheets the last bores ran up it (swashSheet), one continuous surface that carries on
        // under the sand past each edge, so the water's edge falls smoothly between vertices.
        float depthHere = waterDepth(x0);
        float swashFront;
        float sheet = swashSheet(x0, depthHere, swashFront);
        if (sheet > -1e2)
        {
            float lift = near * (1.0 - smoothstep(0.0, SWASH_SEA, depthHere));
            if (sheet > height)
                swashEdge = gSwashEdge * lift;

            height = max(height, mix(height, sheet, lift));
        }
    }

    // In sea zones the shallow-water sim owns the surface: swell arrives physically through its sea boundary, so the
    // analytic swell is replaced by the sim height and only the FFT detail rides on top.
    vec2 zs = zoneSurface(x0);
    float zone = zs.y; // smooth 0..1 ramp inside disc edges: sim surface blends into the swell

    // The sim's own flow there (zoneFlowAt): the ripples and foam stream with its current, and its turbulence, where
    // water has poured, crashed or slammed a wall, roughens the surface whatever the wind (TURBULENCE_SHARE).
    vec3 simFlow = zone > 0.0 ? zoneFlowAt(x0) : vec3(0.0);
    vec3 rough = simFlow.z * zone * TURBULENCE_SHARE;

    // GPU cascades: displacement only, faded out where the grid cannot resolve the band, and kept to the wind sea the
    // wind raises over the water here (windSea) or the sim's turbulence churns up.
    vec3 slopeShare;
    vec3 heightShare = max(windSea(x0, slopeShare), rough);
    slopeShare = max(slopeShare, rough);
    vec3 cpuBand = vec3(Dx, height, Dz);
    vec3 fft = vec3(0.0);
    int cascades = min(int(frame.cascadeL.w), 3);
    for (int i = 0; i < cascades; ++i)
    {
        float lambdaLo = TWO_PI / max(frame.cascadeKLo[i], 1e-6);
        float wv = (1.0 - smoothstep(0.25 * lambdaLo, 0.5 * lambdaLo, se)) * heightShare[i];
        if (wv <= 0.0)
            continue;
        vec4 dc = textureLod(dispMap, vec3(x0 / frame.cascadeL[i], float(i)), 0.0);
        fft += wv * dc.xyz;
    }

    // The swell shoals and breaks through layer B; the FFT detail calms toward the land over SHORE_CALM-SHORE_FULL of the
    // water column under it: the swell's crest or trough over the bed (a swash sheet's thickness, in sea zones the sim's
    // own depth), so the chop comes in on the crests and settles in the troughs between them, and the thin water at the
    // edge stays glassy. Fading it from 8 m of still depth left the whole surf zone glassy; over 0.2-1.5 m, a glassy
    // band that didn't move with the waves; capped at full strength down to half a metre, chop right up to the sand.
    // On a folded tongue it is damped as the ribbon damps it (LIP_DETAIL), and the whitecaps with it.
    float bed = waterDepth(x0);
    float column = max(mix(bed + height, bed, zone), 0.0);
    float shore = smoothstep(SHORE_CALM, SHORE_FULL, column) * (1.0 - 0.7 * gFoldWeight);
    vec3 fftBand = fft * shore;
    vec3 disp = cpuBand + fftBand;

    // CPU partials in contract form: dxx = 1 + λDxx, so λDxx = -jxx (likewise zz, xz).
    // Swell rides on the simulated water level, so waves carry on into sea zones that are draining or filling.
    OceanPoint p;
    p.position = vec3(x0.x + disp.x * (1.0 - zone), mix(disp.y, zs.x + fftBand.y, zone), x0.y + disp.z * (1.0 - zone));

    // Zone slope from central differences of the sim surface (2 m cells): smooth normals for the pour and the pool.
    const float cell = 2.0;
    float zoneHx = (zoneSurface(x0 + vec2(cell, 0.0)).x - zoneSurface(x0 - vec2(cell, 0.0)).x) / (2.0 * cell);
    float zoneHz = (zoneSurface(x0 + vec2(0.0, cell)).x - zoneSurface(x0 - vec2(0.0, cell)).x) / (2.0 * cell);

    p.cpuPartials = mix(vec4(hx, hz, -jxx, -jzz), vec4(zoneHx, zoneHz, 0.0, 0.0), zone);
    p.cpuDxz = -jxz * (1.0 - zone);
    p.shore = shore * slopeShare;
    p.breakerFoam = breakerFoam * (1.0 - zone);
    p.surfFoam = surfFoamAt(x0) * (1.0 - zone);
    p.zone = zone;
    p.swashEdge = swashEdge * (1.0 - zone);
    // Eddies in churned water (EDDY_*): a slowly turning swirl as strong as the water is turbulent, so foam over it boils
    // about rather than lying in place.
    float swirl = TWO_PI * valueNoise(x0 * EDDY_SCALE + frame.cameraPos.w * EDDY_RATE);
    vec2 eddy = vec2(cos(swirl), sin(swirl)) * (EDDY_SPEED * simFlow.z);
    p.flow = flow * (1.0 - zone) + (simFlow.xy + eddy) * zone;
    p.turbulence = simFlow.z * zone;
    p.fold = vec4(gFoldWeight, gFoldInner, gFoldPour);
    p.tongue = gFoldBody;
    return p;
}