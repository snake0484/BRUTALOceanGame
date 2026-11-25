// Shared ocean surface evaluation (ocean.vert): frame block, maps, layer B coastal terms, the shore wash, and oceanAt(),
// the displaced surface at a rest position. Must match the CPU OceanSwell (see each section).
 

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
    vec4 coastRect;    // layer B near field: xy = world origin, z = cell size, w = resolution (0 = none)
    vec4 refEnergy[3]; // swell variance per coastal reference frequency
    vec4 refChop[3];   // variance of the Gerstner trace, sum 1/2 (|c| k)^2, per reference
    vec4 refK0[3];     // deep-water k per reference
    vec4 coastDir;     // xy = direction the phase maps were solved for, z = crossfade weight of the current field,
                       // w = direction (radians) the fading-out field was solved for
    vec4 coastPrev;    // fading-out field: xy = world origin, z = cell size, w = resolution (0 = none)
    vec4 carrier;      // surf coherence (OceanSwell.GpuCarrier): k0, reference coord, phase, peak-band energy sum a^2
    vec4 carrierChop;  // x = choppiness, y = omega, z = set group phase, w = group ratio (a set every 1 / this waves)
    vec4 windSea;      // x = wind speed the cascades' spectrum is raised by (m/s), yzw unused
    vec4 coastFar;     // the current near field's far field (CoastalField.Far): xy = world origin, z = cell size, w = resolution (0 = none)
    vec4 coastFarPrev; // the fading-out near field's far field, the same
    ivec4 counts;      // x = wave count, y..z = coastal reference range in use, w = buffer slots, 3 bits each: current, previous, current's far, previous's far
    Wave waves[512];
} frame;

layout(set = 0, binding = 1) uniform sampler2DArray dispMap;  // (λDx, h, λDz, λDxz)
layout(set = 0, binding = 2) uniform sampler2DArray derivMap; // (hx, hz, λDxx, λDzz)
layout(set = 0, binding = 3) uniform sampler2DArray foamMap;  // r = persistent foam
layout(set = 0, binding = 4) uniform sampler2DArray depthMap; // see depthMapAt

// Layer B coastal maps (CoastalField): 7 vec4 per texel = (depth, active, 0, 0), then (lag, amp, lag, amp) x 6. Six
// buffers shared by all frames (OceanRenderer.Slot); counts.w says which hold the current near field and, while a rebuild
// crossfades in, the one it replaces, and each one's far field.
layout(std430, set = 0, binding = 5) readonly buffer Coastal { vec4 texels[]; } coastal[6];

// Carrier atlas (ocean_carrier_atlas.comp): the surf carrier's (lag, water depth, shoal amplitude, active flag) per
// texel of the current coastal field, mixed to the carrier's reference once per texel when the field or the carrier
// changes. What surfBandAt reads, bilinear by hand from four texels. Read-only except in that compute shader.
#ifdef CARRIER_ATLAS_WRITABLE
#define CARRIER_ATLAS_ACCESS
#else
#define CARRIER_ATLAS_ACCESS readonly
#endif

layout(set = 0, binding = 7, rgba32f) CARRIER_ATLAS_ACCESS uniform image2DArray carrierAtlas;

#define RAIN_COVER_SET 0
#define RAIN_COVER_BINDING 8
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
const float TWO_PI = 6.28318530718;

#ifndef WATER_NOISE_GLSL
#define WATER_NOISE_GLSL
const float FLOW_PERIOD = 1.0;  // s: each copy of the flow map slides for this long, by speed x this, before it fades out and resets
const float LACE_WRAP = 600.0;  // seconds: foam texture clocks wrap so noise coordinates stay small

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
#endif

// Per tap (0 = current field, 1 = fading-out field): buffer slot and cell size of the field it read (the near field, or
// past it the far one), first vec4 of the four bilinear texels, weights.
int cSlot[2];
float cCell[2];
ivec4 cTexel[2];
vec2 cT[2];
bool cHas[2];
float cDepth[2];
vec2 cDir[2];
float cFade;

// Same bilinear as CoastalField.Sample on the CPU, of the field in buffer slot. False = it has nothing active here.
bool coastalTapAt(int tap, vec4 rect, int slot, vec2 xz, out float depth)
{
    depth = DEEP_DEPTH;
    int res = int(rect.w);
    if (res <= 0)
        return false;

    cSlot[tap] = slot;
    cCell[tap] = rect.z;

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

// Current field, crossfaded from the previous one while a rebuild fades in (OceanSwell.SampleCoastal); each past its
// edge, or where it has nothing, its far field (CoastalField.SampleWithFar). False = open ocean.
bool coastalSample(vec2 xz)
{
    int slots = frame.counts.w;
    cFade = frame.coastDir.z; // no previous field (coastPrev.w = 0) fades in from the open ocean

    cDepth[1] = DEEP_DEPTH;
    cHas[0] = coastalTapAt(0, frame.coastRect, slots & 7, xz, cDepth[0])
        || coastalTapAt(0, frame.coastFar, (slots >> 6) & 7, xz, cDepth[0]);
    cHas[1] = cFade < 1.0 && (coastalTapAt(1, frame.coastPrev, (slots >> 3) & 7, xz, cDepth[1])
        || coastalTapAt(1, frame.coastFarPrev, (slots >> 9) & 7, xz, cDepth[1]));
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
    return coastalTapRef(tap, j, cCell[tap]);
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

// The four carrier atlas texels around xz in the current field and their bilinear weights: the same bilinear as
// CoastalField.Sample on the CPU. False outside the map; a caller that needs an active texel tests a.w + b.w + c.w + d.w.
bool carrierTexels(vec2 xz, out vec2 w, out vec4 a, out vec4 b, out vec4 c, out vec4 d)
{
    vec4 rect = frame.coastRect;
    int res = int(rect.w);
    vec2 f = (xz - rect.xy) / rect.z - 0.5;
    if (res <= 0 || !(f.x >= 0.0 && f.y >= 0.0 && f.x < float(res - 1) && f.y < float(res - 1)))
        return false;

    ivec2 i = ivec2(f);
    w = f - vec2(i);
    a = imageLoad(carrierAtlas, ivec3(i.x, i.y, 0));
    b = imageLoad(carrierAtlas, ivec3(i.x + 1, i.y, 0));
    c = imageLoad(carrierAtlas, ivec3(i.x, i.y + 1, 0));
    d = imageLoad(carrierAtlas, ivec3(i.x + 1, i.y + 1, 0));
    return true;
}

float bilerp(float a, float b, float c, float d, vec2 w)
{
    return mix(mix(a, b, w.x), mix(c, d, w.x), w.y);
}

// Whether the surf can make foam at x0 now (ocean_surf_foam.comp): inside the current field, in water no deeper than
// the coherent surf wave can break in here (its shoaled height at the top of a set over the breaking ratio, with
// SURF_BAND_MARGIN, plus SURF_BAND_REACH behind a crest) and no higher up the beach than the swash reaches. One atlas
// lookup against a swash evaluation per cell outside.
const float SURF_BAND_MARGIN = 1.3;
const float SURF_BAND_REACH = 1.0;
const float SWASH_REACH = 2.0; // SWASH_MAX: no higher up the beach than the swash reaches

bool surfBandAt(vec2 x0)
{
    vec2 w;
    vec4 a, b, c, d;
    if (frame.carrier.w <= 0.0 || !carrierTexels(x0, w, a, b, c, d) || a.w + b.w + c.w + d.w <= 0.0)
        return false;

    float depth = bilerp(a.y, b.y, c.y, d.y, w);
    float peak = (1.0 + GROUP_DEPTH) / sqrt(1.0 + 0.5 * GROUP_DEPTH * GROUP_DEPTH);
    float unbroken = 2.0 * sqrt(MAX_COHERENCE * frame.carrier.w) * bilerp(a.z, b.z, c.z, d.z, w) * peak;
    return depth >= -SWASH_REACH && depth <= SURF_BAND_MARGIN * unbroken / WAVE_BREAKING_GAMMA + SURF_BAND_REACH;
}

const float SURF_SPACING_FULL = 4.0; // swash at full strength up to this grid spacing (m) ...
const float SURF_SPACING_NONE = 8.0; // ... and gone by this one

// Surf foam left behind by the swash (ocean_surf_foam.comp): coverage per cell of the depth map's grid, decaying.
// Read-only except in that compute shader, which defines SURF_FOAM_WRITABLE.
#ifdef SURF_FOAM_WRITABLE
#define SURF_FOAM_ACCESS
#else
#define SURF_FOAM_ACCESS readonly
#endif

const int SURF_FOAM_RES = 512;

layout(std430, set = 0, binding = 6) SURF_FOAM_ACCESS buffer SurfFoam
{
    float values[];
} surfFoam;

// Zone flow (OceanRenderer.WriteZoneFlow, from ZoneFlowSim.PackFlow): the sea-zone sim's own 1 m cells, per cell
// half (velocity x, z) and half (turbulence, 0). Empty (size 0) when there is no sim or it is too big.
layout(std430, set = 0, binding = 9) readonly buffer ZoneFlow
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
// began (surfWidth: the distance from where it broke to the shore), so the small waves between sets barely move
// the edge and a set wave runs far up, and along the beach the edge is a wavy line. The sheet behind an edge is the
// Shen-Meyer lens, (x_edge - x)^2 / (9 g t^2), thick just behind a fresh edge and thinning as the sheet ages, over a
// minimum film. GPU only.
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
const float SWASH_SEA = 0.5;         // water depth (m) over which the swash fades into the sea surface
const float SWASH_SLOPE_SPAN = 6.0;  // m either side the beach slope is taken over: the foreshore's, not one 2 m depth cell's, whose slope varies by a third cell to cell and stepped the edge
const float SWASH_FOAM_FRONT = 2.0;  // m of sheet behind an up-rushing edge that is fresh foam (ocean_surf_foam.comp) ...
const float SWASH_FOAM_U0 = 3.0;     // ... at full strength from this edge speed (m/s)

// The surf zone's width (m) for a wave of breaking height H on a beach of this slope: from where it breaks, in water
// H / WAVE_BREAKING_GAMMA deep, to the shoreline.
float surfWidth(float breakerHeight, float slope)
{
    return breakerHeight / WAVE_BREAKING_GAMMA / slope;
}

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
    // break's dissipation over the surf zone it has crossed.
    float breaker = 2.0 * sqrt(MAX_COHERENCE * frame.carrier.w) * ref.w;
    float unbroken = breaker * exp(-surfWidth(breaker, slope) / SWASH_DISSIPATE);
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
    float surfFoam;    // whitewater left behind by the surf (ocean_surf_foam.comp)
    float zone;        // 0..1 sea-zone weight (the sim owns the surface where 1)
    float swashEdge;   // 1 at the edge of the swash sheet drawn here, 0 half a metre behind it or where the sea is the surface (ocean.frag's edge line)
    vec2 flow;         // the water's forward speed (m/s): in sea zones the sim's current
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
// cache warm but lost the warp's coalesced reads of the wave array.
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
    // the wave the swash times its run-up from, by the carrier's local k h (its eikonal wavenumber |k0 d + grad lag| x depth).
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
        // Its own breaking limit (OceanSwell.CarrierBreaking): it grows as it shoals until H = WAVE_BREAKING_GAMMA x depth.
        float kl = max(length(carrierK), 1e-6);
        float unbroken = 2.0 * sqrt(coherence * frame.carrier.w) * carrierRef.w
            * groupEnvelope(frame.carrier.x * dot(cDir[tap], x0) + carrierRef.x);
        float depthC = max(cDepth[tap], 0.0);
        float capC = unbroken > 1e-4 ? clamp(WAVE_BREAKING_GAMMA * depthC / unbroken, 0.0, 1.0) : 1.0;
        float theta = frame.carrier.x * dot(cDir[tap], x0) + carrierRef.x + frame.carrier.z;
        float A = 0.5 * unbroken * capC;
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

// Everything on top of the swell: swash, the GPU cascades, sea zones.
OceanPoint oceanFinish(vec2 x0, float se, Swell sw)
{
    float height = sw.height;
    float hx = sw.hx, hz = sw.hz;
    float Dx = sw.Dx, Dz = sw.Dz;
    float jxx = sw.jxx, jzz = sw.jzz, jxz = sw.jxz;

    // Swash is metres wide: a grid this coarse can't show it, so it fades out with the spacing (smoothly, so no seam
    // where one clipmap level hands over to the next) and far vertices skip the work.
    float near = 1.0 - smoothstep(SURF_SPACING_FULL, SURF_SPACING_NONE, se);
    float swashEdge = 0.0;

    if (near > 0.0)
    {
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
    float bed = waterDepth(x0);
    float column = max(mix(bed + height, bed, zone), 0.0);
    float shore = smoothstep(SHORE_CALM, SHORE_FULL, column);
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
    p.surfFoam = surfFoamAt(x0) * (1.0 - zone);
    p.zone = zone;
    p.swashEdge = swashEdge * (1.0 - zone);
    // Eddies in churned water (EDDY_*): a slowly turning swirl as strong as the water is turbulent, so foam over it boils
    // about rather than lying in place.
    float swirl = TWO_PI * valueNoise(x0 * EDDY_SCALE + frame.cameraPos.w * EDDY_RATE);
    vec2 eddy = vec2(cos(swirl), sin(swirl)) * (EDDY_SPEED * simFlow.z);
    p.flow = (simFlow.xy + eddy) * zone;
    p.turbulence = simFlow.z * zone;
    return p;
}