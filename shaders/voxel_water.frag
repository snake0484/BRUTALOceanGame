#version 450
#extension GL_GOOGLE_include_directive : require
// Voxel water surface fragment shader: shaded as the ocean is (water_shading.glsl: seabed body, scatter, sky, sun, foam
// lace, tonemap, fog), with wind ripples where the wind reaches, stream ripples and foam lace that stream with the
// water's own flow, and rain.

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

#include "water_shading.glsl"
#include "rain_ripples.glsl"

#define RAIN_COVER_SET 0
#define RAIN_COVER_BINDING 1
#include "rain_cover.glsl"

layout(location = 0) in vec3 vWorldPos;
layout(location = 1) in vec3 vNormal;
layout(location = 2) in float vThickness;
layout(location = 3) in vec2 vFlow;  // the surface water's speed (m/s, world x, z)
layout(location = 4) in float vFoam; // 0..1: how churned the water is (a pour landing, fast shallow flow)

layout(location = 0) out vec4 outColor;

const float GRAVITY = 9.81;
const float TWO_PI_W = 6.28318530718;
const float STREAM_SCALE = 2.2;   // stream ripple cells per metre
const float STREAM_SLOPE = 0.35;  // their slope at full speed
const float STREAM_FULL = 2.0;    // m/s at which stream ripples are at full strength
const float POUR_FOAM = 0.75;     // lace coverage over fully churned water

// How far the wind reaches water at p, 0 sheltered .. 1 open (docs/voxel-water.md "Wind on voxel water"): a march
// upwind over the cover map's column tops (rain_cover.glsl, the same world-space data for every block, so block seams
// agree). A top standing over the water is a wall that shelters SHELTER_PER_HEIGHT times its height; the first top at
// or over the water's level ends the fetch, which raises full ripples over FULL_FETCH of open water. Past the window
// the wind is taken as open.
const float SHELTER_STEP = 2.0;
const int SHELTER_STEPS = 8;
const float SHELTER_PER_HEIGHT = 6.0;
const float MAX_WALL = 6.0;
const float FULL_FETCH = 6.0;

float windReach(vec3 p, vec2 wind)
{
    float U = length(wind);
    if (U < 1.0)
        return 0.0;

    vec2 d = wind / U;
    float reach = 1.0;
    float shore = SHELTER_STEP * float(SHELTER_STEPS);

    for (int i = 1; i <= SHELTER_STEPS; i++)
    {
        float dist = SHELTER_STEP * float(i);
        float top = rainCoverTop(p.xz - d * dist);
        if (top == NO_TOP)
            break;

        float h = top - p.y;
        if (h > 0.25)
        {
            reach = min(reach, dist / (SHELTER_PER_HEIGHT * min(h, MAX_WALL)));
            shore = min(shore, dist);
        }
        else if (h > -0.5)
            shore = min(shore, dist);

        if (reach <= 0.0)
            break;
    }

    return reach * min(shore / FULL_FETCH, 1.0);
}

// A wave a pixel can't resolve fades out rather than aliasing into moire bands across a lake seen at a grazing angle:
// its weight by the pixel's footprint (m) against its wavelength, as the ocean fades its cascades.
float resolved(float lambda, float footprint)
{
    return 1.0 - smoothstep(lambda * 0.12, lambda * 0.35, footprint);
}

// One wind-sea component: a deep-water wave of wavelength lambda running along the wind turned by ang, at steepness
// steep (its slope amplitude) once the wind is over u1 m/s, none under u0. Returns its height-field slope.
vec2 ripple(vec2 p, float t, vec2 d, vec2 n, float ang, float lambda, float steep, float U, float u0, float u1, float footprint)
{
    vec2 k = d * cos(ang) + n * sin(ang);
    float kk = TWO_PI_W / lambda;
    float w = sqrt(GRAVITY * kk);
    return k * (steep * smoothstep(u0, u1, U) * resolved(lambda, footprint)) * cos(dot(k, p) * kk - w * t);
}

// Wind ripples: four short waves spread about the wind, raised by its speed and scaled by how far the wind reaches
// this water. Sheltered water (e = 0) has none and lies as glass.
vec2 windRippleSlope(vec2 p, float t, vec2 wind, float e, float footprint)
{
    float U = length(wind);
    if (e <= 0.001 || U < 1.0)
        return vec2(0.0);

    vec2 d = wind / U;
    vec2 n = vec2(-d.y, d.x);
    vec2 s = ripple(p, t, d, n, -0.45, 0.45, 0.10, U, 1.5, 4.0, footprint);
    s += ripple(p, t, d, n, 0.20, 0.8, 0.09, U, 2.0, 5.0, footprint);
    s += ripple(p, t, d, n, -0.10, 1.4, 0.07, U, 3.0, 7.0, footprint);
    s += ripple(p, t, d, n, 0.50, 2.4, 0.05, U, 4.0, 10.0, footprint);
    return s * e;
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
    vec3 toCam = frame.cameraPos.xyz - vWorldPos;
    float dist = length(toCam);
    vec3 V = toCam / max(dist, 1e-4);
    float time = frame.cameraPos.w;
    float speed = length(vFlow);
    vec2 flowDir = speed > 0.01 ? vFlow / speed : vec2(1.0, 0.0);

    // Ripples where the wind reaches (the walls and shore upwind, windReach; roofs and caves, the cover map's exposure)
    // and where the water runs; both fade with distance so they don't alias.
    vec3 N = normalize(vNormal);
    float footprint = length(fwidth(vWorldPos.xz));
    float rippleFade = 1.0 - smoothstep(60.0, 200.0, dist);
    float exposure = rippleFade > 0.0 ? windReach(vWorldPos, frame.wind.xy) * windCover(vWorldPos.xz).x : 0.0;
    vec2 slope = (windRippleSlope(vWorldPos.xz, time, frame.wind.xy, exposure, footprint) + streamSlope(vWorldPos.xz, flowDir, speed, footprint)) * rippleFade;
    N = normalize(N + vec3(-slope.x, 0.0, -slope.y));

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

    // Foam lace over churned water (a pour's foot, fast shallow flow), streaming with the flow, churning as fast as
    // the water is churned; and the rain's flashes.
    float foam = flowingLace(vWorldPos.xz, flowDir, speed, POUR_FOAM * vFoam, clamp(0.3 + vFoam, 0.0, 1.0));
    foam = clamp(max(foam, 0.8 * rainWhite), 0.0, 1.0);

    float thickness = max(vThickness, 0.0);
    vec3 colour = shadeWater(vWorldPos, N, thickness, thickness, 0.0, foam);

    // Thin water is clear, thick water opaque; grazing views see mostly reflection; foam keeps thin sheets visible but
    // fades out with them so films clinging to walls don't read as white shards.
    float F = fresnelSchlick(max(dot(N, V), 0.0));
    float alpha = mix(mix(0.35, 0.9, clamp(thickness / 3.0, 0.0, 1.0)), 1.0, F);
    float f = 1.0 - exp(-dist * frame.skyHorizon.w);
    alpha = mix(alpha, 1.0, f);
    alpha = max(alpha, foam * smoothstep(0.08, 0.35, thickness));

    outColor = vec4(colour, alpha);
}
