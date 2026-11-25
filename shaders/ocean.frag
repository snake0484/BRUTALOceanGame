#version 450
#extension GL_GOOGLE_include_directive : require
// Ocean surface fragment shader: normal and foam from the CPU band plus FFT detail, shaded by ocean_shading.glsl.

#define WATER_FOG_SET 0
#define WATER_FOG_BINDING 10
#define WATER_SSR_SET 1
#include "ocean_common.glsl"
#include "ocean_shading.glsl"
#include "rain_ripples.glsl"

layout(location = 0) in vec3 vWorldPos;
layout(location = 1) in vec2 vRestXZ;
layout(location = 2) in vec4 vCpuPartials; // CPU band (hx, hz, λDxx, λDzz)
layout(location = 3) in float vCpuDxz;     // CPU band λDxz
layout(location = 4) in vec3 vShoreDamp;  // per cascade (OceanPoint.shore)
layout(location = 5) in float vSurfFoam;   // whitewater left behind by the surf
layout(location = 6) in vec2 vFlow;        // the water's forward speed (m/s): in sea zones the sim's current
layout(location = 7) in float vSwashEdge;  // 1 at the edge of a swash sheet, 0 half a metre behind it
layout(location = 8) in float vTurbulence; // sea-zone water churned by a pour, a crash or a wall, 0..1

// Thin water is see-through (docs/breaking-waves.md, thirty-fourth pass): the terrain shows through it, darkened as wet
// sand is, under the sky's reflection, and the water's own colour takes over as it deepens, so the shallows read as depth
// and a swash sheet as a glassy film with an edge rather than paint.
const float SHEET_OPAQUE = 0.6;    // m of water by which the body colour has taken over from the terrain seen through it
const float SWASH_EDGE_FOAM = 0.7; // lace coverage along the water's edge on the beach (vSwashEdge)
const float TURBULENT_FOAM = 0.45; // lace coverage over fully turbulent sea-zone water (vTurbulence)

layout(location = 0) out vec4 outColor;

void main()
{
    // Columns cut off from the sea (inland pits below sea level) hold no ocean water. Inside simulated sea zones, thin
    // water fades out by thickness instead, so wet/dry edges and pouring sheets have soft edges.
    vec4 map = waterMap(vWorldPos.xz);
    float zone = map.b; // smooth ramp at zone edges
    if (zone < 0.5 && map.r < -500.0)
        discard;

    // Water thickness: open water is the surface's height over the ground (map.r = sea level − ground, sea level 0), so
    // the edge sits wherever the moving surface meets the beach; sea zones use the sim's own depth.
    float thickness = mix(vWorldPos.y + map.r, map.r, zone);

    // Open water is drawn right up to the waterline (its final opacity is composed below, SHEET_OPAQUE), the line
    // anti-aliased by the share of the pixel the water covers; sea-zone sheets keep a soft fade for pours and wet/dry edges.
    float waterline = clamp(thickness / max(fwidth(thickness), 1e-4) + 0.5, 0.0, 1.0);
    float alpha = mix(waterline, smoothstep(0.03, 0.5, thickness), zone);
    if (alpha < 0.01)
        discard;

    // Sum raw partials: CPU band + footprint-faded GPU cascades, the cascades flow-mapped so the ripples stream with a sea
    // zone's current (ocean_shading.glsl flowingPartials).
    float speed = length(vFlow);
    vec2 flowDir = speed > 0.01 ? vFlow / speed : vec2(1.0, 0.0);
    float pxz;
    float persistent;
    float footprint = length(fwidth(vRestXZ));
    vec4 p = vCpuPartials + flowingPartials(vRestXZ, flowDir, speed, footprint, vShoreDamp, pxz, persistent);
    pxz += vCpuDxz;

    // Normal and Jacobian of the displaced surface (contract form).
    float dxx = 1.0 + p.z;
    float dzz = 1.0 + p.w;
    float dxz = pxz;
    vec3 N = normalize(vec3(p.y * dxz - dzz * p.x,
                            dzz * dxx - dxz * dxz,
                            dxz * p.x - p.y * dxx));
    float J = dxx * dzz - dxz * dxz;

    // Rain (rain_ripples.glsl, foamParams.x = intensity, y = its time): rings from every drop, in rest coordinates so
    // they ride the waves, and beyond the drawn splash crowns their flashes. Rings finer than a pixel roughen the sea.
    // None under cover (rain_cover.glsl): a roof, an overhang, a sea cave.
    float rainWhite = 0.0;
    if (frame.foamParams.x > 0.0)
    {
        float wet = frame.foamParams.x * rainReaches(vWorldPos);
        float crowns = smoothstep(SPLASH_NEAR, SPLASH_FAR, length(vWorldPos.xz - frame.cameraPos.xz));
        vec3 rain = rainOnWater(vRestXZ, dFdx(vRestXZ), dFdy(vRestXZ), frame.foamParams.y, wet, crowns);
        N = normalize(N - vec3(rain.x, 0.0, rain.y));
        rainWhite = rain.z;
        gRainRough = wet * (0.5 + 0.5 * smoothstep(0.01, 0.04, footprint));
    }

    if (dot(N, frame.cameraPos.xyz - vWorldPos) < 0.0)
        N = -N;

    // Whitecaps: the cascades' foam (ocean_fft.comp), seeded on the measured share of the surface for this wind and
    // left to decay, drawn as lace so a patch reads as foam on water rather than flat white. They fade over the shallows,
    // and where the fetch is too short to raise them, with the waves that make them (persistent is already faded so).
    float whitecap = clamp(persistent * frame.cascadeKLo.w, 0.0, 1.0);

    // Surf foam as lace (ocean_shading.glsl): zone whitewater (including what has drifted out past the zone edge), and a
    // thin line at the very edge of the water where the swash runs out. What the swash's up-rush left behind lingers as
    // thinner lace.
    // Coverage stays under 1 so even a fresh roller keeps holes of water in it.
    float edgeFoam = max(0.6 * (1.0 - smoothstep(0.0, 0.04, max(thickness, 0.0))), SWASH_EDGE_FOAM * vSwashEdge);
    // Churned sea-zone water (vTurbulence: under a pour, after a crash) is aerated too: lace over it, churning as fast
    // as it is turbulent. In zones all of it streams with the sim's current (vFlow).
    float coverage = max(max(0.6 * vSurfFoam, max(0.8 * map.a, edgeFoam)), TURBULENT_FOAM * vTurbulence);
    float surf = flowingLace(vRestXZ, flowDir, speed, coverage, clamp(map.a + vTurbulence, 0.0, 1.0));
    float foam = clamp(max(max(flowingLace(vRestXZ, flowDir, speed, whitecap, 0.5), surf), 0.8 * rainWhite), 0.0, 1.0);

    // Debug: red = folding (1 - J), green = J, blue = foam coverage before lace (whitecap or surf).
    if (frame.foamParams.z > 0.5)
    {
        outColor = vec4(clamp(1.0 - J, 0.0, 1.0), clamp(J, 0.0, 1.0) * 0.5, max(whitecap, coverage), 1.0);
        return;
    }

    float through;
    vec3 colour = shadeWater(vWorldPos, N, max(waterDepth(vRestXZ), 0.0), thickness, zone, foam, through);

    // The terrain shows through the water: near the camera by the fog march's transmittance to the bed (through, already
    // less the reflection's and foam's shares; the colour holds the far field's estimate of the sand), and where the march hands over to the far
    // field (or with the fog off) thin open water is see-through (SHEET_OPAQUE): the share of the pixel that is neither
    // the sky's reflection, foam nor the water body (which builds up over the first SHEET_OPAQUE of thickness). Either
    // way the terrain is darkened to WET_SAND as wet sand is; the blend is straight alpha, so that share is taken off
    // the alpha. Opaque at distance, where the fog has the pixel anyway. Zones keep their fade, less what shows through.
    vec3 toEye = frame.cameraPos.xyz - vWorldPos;
    float eyeDist = length(toEye);
    float aerial = exp(-eyeDist * frame.skyHorizon.w);
    float thin = 0.0;
    if (thickness < SHEET_OPAQUE)
    {
        float Fv = fresnelSchlick(max(dot(N, toEye / max(eyeDist, 1e-4)), 0.0));
        thin = (1.0 - Fv) * (1.0 - smoothstep(0.0, SHEET_OPAQUE, thickness)) * (1.0 - foam) * aerial * (1.0 - zone) * (1.0 - gFogNear);
    }

    float see = max(through * aerial, thin);
    if (see > 0.0)
    {
        float alphaOpen = 1.0 - see * WET_SAND;
        colour *= (1.0 - see) / alphaOpen;
        alpha = mix(alphaOpen * waterline, alpha * alphaOpen, zone);
    }

    // Foam keeps thin sheets visible, but fades out with them so films clinging to walls don't read as white shards.
    outColor = vec4(colour, max(alpha, foam * smoothstep(0.08, 0.35, thickness)));
}
