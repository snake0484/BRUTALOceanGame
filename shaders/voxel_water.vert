#version 450
#extension GL_GOOGLE_include_directive : require
// Voxel water surface vertex shader: chunk-local position + push-constant origin, the surface flow and foam through, the
// wind on the water (voxel_water_wind.glsl): a march of ~20 cover reads that varies over metres, so it runs per 1 m
// vertex rather than per pixel (0.065 ms per pixel over the crater lake), and the wind sea that wind raises over its
// fetch (wind_sea.glsl), as weights on the wind sea bands, at least as strong as the water's own turbulence.

layout(set = 0, binding = 0, std140) uniform Frame {
    mat4 viewProj;
    vec4 cameraPos;   // xyz world, w = time
    vec4 sunDir;      // xyz toward sun, w = intensity
    vec4 skyZenith;
    vec4 skyHorizon;  // w = fog density
    vec4 rain;        // x = intensity 0..1, y = its time (s, looped)
    vec4 wind;        // xy = 10 m wind (m/s, the way it blows toward)
    vec4 waterDeep;
    vec4 waterScatter;
} frame;

#define RAIN_COVER_SET 0
#define RAIN_COVER_BINDING 1
#include "rain_cover.glsl"
#include "voxel_water_wind.glsl"
#include "wind_sea.glsl"

layout(push_constant) uniform Chunk {
    vec4 origin; // xyz chunk world origin
    vec4 clip;   // a body's far plane: xy centre (world x, z), z radius; z = 0 for a block's mesh
} pc;

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in float inThickness;
layout(location = 3) in vec2 inFlow;
layout(location = 4) in float inFoam;

layout(location = 0) out vec3 vWorldPos;
layout(location = 1) out vec3 vNormal;
layout(location = 2) out float vThickness;
layout(location = 3) out vec2 vFlow;
layout(location = 4) out float vFoam;
layout(location = 5) out vec2 vReach;
layout(location = 6) out vec4 vBands0; // the slope variance of wind sea bands 0..3
layout(location = 7) out vec4 vBands1; // 4..7
layout(location = 8) out vec4 vBands2; // 8, 9, the variance past band 9, the wind the water feels (m/s)

// Turbulence (docs/voxel-water.md "Turbulence"): how agitated the water is, 0..1, besides the wind. Churned water (its
// froth: a pour's foot and what it sends downstream, fast shallow flow), running water, and rain falling on it where it is
// open to the sky (rainReaches) each stir it. The wind sea bands are drawn at least as strong as the chop of a short-fetch
// wind sea at TURBULENT_WIND x the turbulence, per band, so the pattern grows with the turbulence: water with no wind,
// no flow, no froth and no rain on it is flat.
const float TURBULENT_WIND = 10.0;  // m/s of wind whose chop fully turbulent water carries
const float TURBULENT_FETCH = 6.0;  // m: its chop is short, as an eddy's
const float FLOW_TURBULENCE = 0.6;  // turbulence of a stream at STREAM_FULL
const float FLOW_STILL = 0.1;       // m/s under which the flow stirs nothing
const float STREAM_FULL = 2.0;      // m/s (voxel_water.frag)
const float RAIN_TURBULENCE = 0.15; // turbulence of the heaviest rain on open water: a 1.5 m/s wind's ripples

void main()
{
    vec3 world = pc.origin.xyz + inPosition;
    vWorldPos = world;
    vNormal = inNormal;
    vThickness = inThickness;
    vFlow = inFlow;
    vFoam = inFoam;
    vReach = windReach(world, frame.wind.xy);
    float U = length(frame.wind.xy) * vReach.x;
    vec2 fine;
    float tail;
    windSeaBands(U, vReach.y, vBands0, vBands1, fine, tail);

    float turbulence = max(inFoam, FLOW_TURBULENCE * smoothstep(FLOW_STILL, STREAM_FULL, length(inFlow)));
    if (frame.rain.x > 0.0)
        turbulence = max(turbulence, RAIN_TURBULENCE * sqrt(frame.rain.x) * rainReaches(world));

    if (turbulence > 0.01)
    {
        vec4 c0, c1;
        vec2 cFine;
        float cTail;
        windSeaBands(TURBULENT_WIND * clamp(turbulence, 0.0, 1.0), TURBULENT_FETCH, c0, c1, cFine, cTail);
        vBands0 = max(vBands0, c0);
        vBands1 = max(vBands1, c1);
        fine = max(fine, cFine);
        tail = max(tail, cTail);
    }

    vBands2 = vec4(fine, tail, U);
    gl_Position = frame.viewProj * vec4(world, 1.0);
}
