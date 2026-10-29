#version 450
#extension GL_GOOGLE_include_directive : require
// Voxel water surface fragment shader: shaded as the ocean is (water_shading.glsl: seabed body, scatter, sky, sun, foam
// lace, tonemap, fog), with the wind sea its own wind raises over its own fetch (wind_sea.glsl: the octave bands weighted
// per vertex), gusting, with whitecaps, stream ripples and foam lace that stream with the water's own flow, and rain.

layout(set = 0, binding = 0, std140) uniform Frame {
    mat4 viewProj;
    vec4 cameraPos;   // xyz world, w = time
    vec4 sunDir;      // xyz toward sun, w = intensity
    vec4 skyZenith;
    vec4 skyHorizon;  // w = fog density
    vec4 rain;        // x = intensity 0..1, y = its time (s, looped); as the ocean's foamParams.xy
    vec4 wind;        // xy = 10 m wind (m/s, the way it blows toward)
    vec4 waterDeep;   // the ocean's deep water colour
    vec4 waterScatter; // the ocean's subsurface scatter colour
} frame;

#define WATER_FOG_SET 0
#define WATER_FOG_BINDING 2
#define WATER_SSR_SET 1
#include "water_shading.glsl"
#include "rain_ripples.glsl"

#define RAIN_COVER_SET 0
#define RAIN_COVER_BINDING 1
#include "rain_cover.glsl"

#include "wind_sea.glsl"

layout(set = 0, binding = 4) uniform sampler2DArray bandMap; // the wind sea bands (WindSeaBands): per layer (hx, hz) of two

layout(push_constant) uniform Chunk {
    vec4 origin; // xyz chunk world origin
    vec4 clip;   // a body's far plane: xy centre (world x, z), z radius; z = 0 for a block's mesh
} pc;

layout(location = 0) in vec3 vWorldPos;
layout(location = 1) in vec3 vNormal;
layout(location = 2) in float vThickness;
layout(location = 3) in vec2 vFlow;  // the surface water's speed (m/s, world x, z)
layout(location = 4) in float vFoam; // 0..1: how churned the water is (a pour landing, fast shallow flow)
layout(location = 5) in vec2 vReach; // the wind on this water (voxel_water_wind.glsl): x = share of the wind it feels, y = fetch (m)
layout(location = 6) in vec4 vBands0; // the slope variance of wind sea bands 0..3 (wind_sea.glsl)
layout(location = 7) in vec4 vBands1; // 4..7
layout(location = 8) in vec4 vBands2; // 8, 9, the variance past band 9, the wind the water feels (m/s)

layout(location = 0) out vec4 outColor;

const float STREAM_SCALE = 2.2;   // stream ripple cells per metre
const float STREAM_SLOPE = 0.35;  // their slope at full speed
const float STREAM_FULL = 2.0;    // m/s at which stream ripples are at full strength
const float POUR_FOAM = 0.75;     // lace coverage over fully churned water
const vec2 BOIL = vec2(0.55, 1.0); // froth over which a pour's foot boils solid white (WaterBlock.Froth)

// A wave a pixel can't resolve fades out rather than aliasing into moire bands across a lake seen at a grazing angle:
// its weight by the pixel's footprint (m) against its wavelength, as the ocean fades its cascades.
float resolved(float lambda, float footprint)
{
    return 1.0 - smoothstep(lambda * 0.12, lambda * 0.35, footprint);
}

// Gusts: the wind over water is never steady, and short waves answer a gust within seconds, so patches of rougher
// water (cat's paws) run across a lake downwind at the wind's speed, between smoother lulls. Noise carried with the
// wind, 0..1 about a mean of 0.5; per band the response grows with how quickly its waves answer (none of 8 m waves and
// longer, all of a metre and shorter). It raises no long waves the fetch has not.
const float GUST_SCALE = 28.0;     // m, patch size
const float GUST_DEPTH = 0.6;      // the shortest waves' slope variance swings by +- this

float gust(vec2 xz, vec2 wind, float t)
{
    vec2 p = (xz - wind * t) / GUST_SCALE;
    return 0.65 * valueNoise(p) + 0.35 * valueNoise(p * 2.7 + 17.3);
}

// The wind sea's slope at xz: each band's unit field scaled by the root of its variance here, faded where the pixel's
// footprint can't resolve it (as the ocean fades its cascades: whole until the footprint is an eighth of the band's
// longest wave, gone at half, about one of its shortest). One fetch per layer reads both its bands. resolvedVar = the
// variance drawn, lost = the variance faded out, which roughens the surface instead.
vec2 windSeaSlope(vec2 xz, float footprint, float variance[WIND_SEA_BANDS], out float resolvedVar, out float lost)
{
    vec2 slope = vec2(0.0);
    resolvedVar = 0.0;
    lost = 0.0;
    for (int i = 0; i < WIND_SEA_BANDS / 2; i++)
    {
        vec2 v = vec2(variance[2 * i], variance[2 * i + 1]);
        vec2 texel = BAND_LONGEST / 8.0 / vec2(float(1 << (2 * i)), float(2 << (2 * i)));
        vec2 wf = clamp(1.0 - (footprint / texel - 1.0) / 3.0, 0.0, 1.0) * step(1e-7, v);
        lost += dot(v, 1.0 - wf * wf);
        if (wf.x + wf.y <= 0.0)
            continue;

        vec4 t = texture(bandMap, vec3(xz * (float(1 << (2 * i)) / LAYER_PATCH), float(i)));
        vec2 a = wf * sqrt(v);
        slope += a.x * t.xy + a.y * t.zw;
        resolvedVar += dot(a, a);
    }

    return slope;
}

// The same slid along the water's flow by the flow map's two phases (water_shading.glsl flowPhases), as the ocean slides
// its cascades over a breaking wave: the chop on a stream runs with the stream.
vec2 flowingWindSea(vec2 xz, vec2 dir, float speed, float footprint, float variance[WIND_SEA_BANDS], out float resolvedVar, out float lost)
{
    if (speed < 0.01)
        return windSeaSlope(xz, footprint, variance, resolvedVar, lost);

    vec3 ph = flowPhases();
    vec2 slide = dir * (speed * FLOW_PERIOD);
    float resolved1, lost1;
    vec2 s0 = windSeaSlope(xz - slide * ph.x, footprint, variance, resolvedVar, lost);
    vec2 s1 = windSeaSlope(xz - slide * ph.y, footprint, variance, resolved1, lost1);
    return mix(s1, s0, ph.z);
}

// Whitecaps: Monahan's share of the surface the wind U whitens, 3.84e-6 U^3.41 (OceanFft.WhitecapCoverage), on the
// steepest of the drawn waves. Their squared slope over its mean is near exponential, so the share W lies over -ln W:
// the foam rides the crests the bands draw, and only where the wind is strong enough to break them.
float whitecaps(vec2 slope, float resolvedVar, float U)
{
    float W = min(3.84e-6 * pow(U, 3.41), 0.6);
    if (W < 1e-4 || resolvedVar <= 0.0)
        return 0.0;

    float t = -log(W);
    return smoothstep(t, t + 0.75, dot(slope, slope) / resolvedVar);
}

// The slope of a noise field slid along the flow (the flow map's two phases, water_shading.glsl flowPhases), so
// running water carries ripples with it: a stream's surface is never still.
vec2 streamSlope(vec2 xz, vec2 dir, float speed, float footprint)
{
    float strength = STREAM_SLOPE * smoothstep(0.05, STREAM_FULL, speed) * resolved(1.0 / STREAM_SCALE, footprint);
    if (strength <= 0.0)
        return vec2(0.0);

    vec3 ph = flowPhases();
    vec2 slide = dir * (speed * FLOW_PERIOD);
    vec2 side = vec2(-dir.y, dir.x);
    // Stretched along the flow: streaks, not blobs.
    mat2 stretch = mat2(dir * 0.5, side) * STREAM_SCALE;
    vec2 p0 = stretch * (xz - slide * ph.x);
    vec2 p1 = stretch * (xz - slide * ph.y);
    const float e = 0.08;
    vec2 g0 = vec2(valueNoise(p0 + vec2(e, 0.0)) - valueNoise(p0 - vec2(e, 0.0)), valueNoise(p0 + vec2(0.0, e)) - valueNoise(p0 - vec2(0.0, e)));
    vec2 g1 = vec2(valueNoise(p1 + vec2(e, 0.0)) - valueNoise(p1 - vec2(e, 0.0)), valueNoise(p1 + vec2(0.0, e)) - valueNoise(p1 - vec2(0.0, e)));
    vec2 g = mix(g1, g0, ph.z) / (2.0 * e);
    // Back to world axes.
    return (g.x * dir * 0.5 + g.y * side) * strength;
}

void main()
{
    // A body's far plane covers whole chunks: past the body's disc the ground may lie under its level.
    if (pc.clip.z > 0.0 && distance(vWorldPos.xz, pc.clip.xy) > pc.clip.z)
        discard;

    vec3 toCam = frame.cameraPos.xyz - vWorldPos;
    float dist = length(toCam);
    vec3 V = toCam / max(dist, 1e-4);
    float time = frame.cameraPos.w;
    float speed = length(vFlow);
    vec2 flowDir = speed > 0.01 ? vFlow / speed : vec2(1.0, 0.0);

    // The wind sea where the wind reaches (roofs, walls and the fetch over the water upwind, per vertex) or the water is
    // turbulent (froth, flow, rain where open to the sky: voxel_water.vert), gusting, and stream ripples where the water
    // runs. Still water out of the wind and rain is flat. What a pixel can't resolve roughens the surface instead (gSlopeVariance), with
    // the capillary ripples under the finest band, so far water reads as wind-roughened rather than as a mirror.
    vec3 N = normalize(vNormal);
    float footprint = length(fwidth(vWorldPos.xz));
    float U = vBands2.w;
    float g = 2.0 * gust(vWorldPos.xz, frame.wind.xy, time) - 1.0;
    float variance[WIND_SEA_BANDS] = float[](vBands0.x, vBands0.y, vBands0.z, vBands0.w, vBands1.x, vBands1.y, vBands1.z, vBands1.w, vBands2.x, vBands2.y);
    for (int b = 0; b < WIND_SEA_BANDS; b++)
        variance[b] *= 1.0 + GUST_DEPTH * clamp(0.25 * float(b - 1), 0.0, 1.0) * g;

    float resolvedVar;
    float lost;
    vec2 sea = flowingWindSea(vWorldPos.xz, flowDir, speed, footprint, variance, resolvedVar, lost);
    vec2 slope = sea + streamSlope(vWorldPos.xz, flowDir, speed, footprint);
    N = normalize(N + vec3(-slope.x, 0.0, -slope.y));
    gSlopeVariance = lost + vBands2.z * (1.0 + GUST_DEPTH * g);

    // Rain: the ocean's rings and flashes (rain_ripples.glsl), none under cover (a cave pool). No crowns are drawn on
    // voxel water, so flashes throughout. Rings finer than a pixel roughen the surface, as on the sea.
    float rainWhite = 0.0;
    if (frame.rain.x > 0.0)
    {
        float wet = frame.rain.x * rainReaches(vWorldPos);
        vec3 rain = rainOnWater(vWorldPos.xz, dFdx(vWorldPos.xz), dFdy(vWorldPos.xz), frame.rain.y, wet, 1.0);
        N = normalize(N - vec3(rain.x, 0.0, rain.y));
        rainWhite = rain.z;
        gRainRough = wet * (0.5 + 0.5 * smoothstep(0.01, 0.04, footprint));
    }

    if (dot(N, V) < 0.0)
        N = -N;

    // Foam lace over churned water (a pour's foot and the froth it sends downstream, fast shallow flow), streaming with
    // the flow, churning as fast as the water is churned, boiling white where a pour lands; the wind sea's whitecaps, as
    // the ocean's (gone where the fetch is too short to raise them, with the waves that make them); and the rain's flashes.
    float foam = flowingLace(vWorldPos.xz, flowDir, speed, POUR_FOAM * vFoam, clamp(0.3 + vFoam, 0.0, 1.0));
    foam = mix(foam, 1.0, 0.85 * smoothstep(BOIL.x, BOIL.y, vFoam));
    float whitecap = whitecaps(sea, resolvedVar, U);
    foam = clamp(max(max(foam, flowingLace(vWorldPos.xz, flowDir, speed, whitecap, 0.5)), 0.8 * rainWhite), 0.0, 1.0);

    float thickness = max(vThickness, 0.0);
    float through;
    vec3 colour = shadeWater(vWorldPos, N, thickness, thickness, 0.0, foam, through);

    // Near the camera the fog march says how much of the bed shows through (through; the colour's own sand estimate is
    // faded by it: straight alpha, the bed darkened to WET_SAND); far away, or with the fog off, thin water is clear and thick water opaque,
    // grazing views see mostly reflection. Foam keeps thin sheets visible but fades out with them so films clinging to
    // walls don't read as white shards.
    float F = fresnelSchlick(max(dot(N, V), 0.0));
    float f = 1.0 - exp(-dist * frame.skyHorizon.w);
    float alphaDepth = mix(mix(mix(0.35, 0.9, clamp(thickness / 3.0, 0.0, 1.0)), 1.0, F), 1.0, f);
    float see = through * (1.0 - f);
    float alphaFog = 1.0 - see * WET_SAND;
    float alpha = mix(alphaDepth, alphaFog, gFogNear);
    colour *= mix(1.0, (1.0 - see) / alphaFog, gFogNear);
    alpha = max(alpha, foam * smoothstep(0.08, 0.35, thickness));

    outColor = vec4(colour, alpha);
}
