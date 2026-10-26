#version 450
#extension GL_GOOGLE_include_directive : require
// Ocean surface fragment shader: normal and foam from the CPU band plus FFT detail, shaded by ocean_shading.glsl.

#include "ocean_common.glsl"
#include "ocean_shading.glsl"
#include "rain_ripples.glsl"

layout(location = 0) in vec3 vWorldPos;
layout(location = 1) in vec2 vRestXZ;
layout(location = 2) in vec4 vCpuPartials; // CPU band (hx, hz, λDxx, λDzz)
layout(location = 3) in float vCpuDxz;     // CPU band λDxz
layout(location = 4) in vec3 vShoreDamp;  // per cascade (OceanPoint.shore)
layout(location = 5) in float vBreakerFoam;
layout(location = 6) in float vSurfFoam;   // whitewater left behind by the surf
layout(location = 7) in vec2 vFlow;        // the water's forward speed over a breaking wave (breakerFace), m/s along its travel
layout(location = 8) in vec4 vFold;        // x = on a folded tongue (the fine patch), y = on its inner skin, the tube's ceiling, zw = the lace's rest offset along the skin
layout(location = 9) in vec3 vTongue;      // on a folded tongue: x = along its path (0 root .. 1 tip), y = the body's thickness (m), z = how deep in the tube's shadow
layout(location = 10) in float vSwashEdge; // 1 at the edge of a swash sheet, 0 half a metre behind it
layout(location = 11) in float vTurbulence; // sea-zone water churned by a pour, a crash or a wall, 0..1

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

    // Sum raw partials: CPU band + footprint-faded GPU cascades, the cascades flow-mapped over a breaking wave so the
    // ripples stream up the back, over the crest and into the lip with the water (ocean_shading.glsl flowingPartials).
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
    float whitecap = clamp(persistent * frame.cascadeKLo.w, 0.0, 1.0) * (1.0 - vFold.x);

    // Surf foam as lace (ocean_shading.glsl): throwing crests and the rollers of broken ones, zone whitewater (including
    // what has drifted out past the zone edge), and a thin line at the very edge of the water where the swash runs out.
    // What the surf left behind (crests that passed or ended, the swash's up-rush) lingers as thinner lace.
    // Coverage stays under 1 so even a fresh roller keeps holes of water in it.
    float edgeFoam = max(0.6 * (1.0 - smoothstep(0.0, 0.04, max(thickness, 0.0))), SWASH_EDGE_FOAM * vSwashEdge);
    // Not on a folded tongue: its rest coordinates are squeezed past what a pixel resolves, so this lace fell back to a
    // flat 0.4 x coverage there, a white mass under the tongue's own lace (below).
    // Churned sea-zone water (vTurbulence: under a pour, after a crash) is aerated too: lace over it, churning as fast
    // as it is turbulent. In zones all of it streams with the sim's current (vFlow).
    float coverage = max(max(max(0.7 * vBreakerFoam, 0.6 * vSurfFoam), max(0.8 * map.a, edgeFoam)), TURBULENT_FOAM * vTurbulence) * (1.0 - vFold.x);
    float surf = flowingLace(vRestXZ, flowDir, speed, coverage, clamp(vBreakerFoam + map.a + vTurbulence, 0.0, 1.0));
    float foam = clamp(max(max(flowingLace(vRestXZ, flowDir, speed, whitecap, 0.5), surf), 0.8 * rainWhite), 0.0, 1.0);

    // A folded tongue's whitewater is the lace at the fold's own coverage (a spiller's tumbling whitewater, a plunger's
    // feathered edge, under 1 as the surface's is so water shows between the veins), drawn at the surface's density
    // along the skin and stretched along the pour (the vertex's rest offset, vFold.zw, zero at the root so the lace
    // carries on from the surface's) so it tears into strands down the tongue; where a pixel can't resolve the lace,
    // the tongue edge-on at range, the flat coverage instead, since the lace alone evens out to a grey there. (A flat
    // floor everywhere made a near spiller a white shelf.) The footprint is taken outside the branch, where
    // derivatives are defined.
    // The density along the skin (FOLD_LACE_FINE by the tip) is in the vertex's offset (breakerFace gFoldPour): scaling
    // the whole position here multiplied hundreds of metres of world coordinate by a factor varying across the tongue,
    // and the derivative that follows was metres a pixel, so every tongue away from the origin fell back to flat white.
    float fine = mix(1.0, FOLD_LACE_FINE, vTongue.x * vFold.x);
    vec2 pour = vRestXZ - vFold.zw;
    float pourFoot = length(fwidth(pour)) * LACE_SCALE;
    float strand = 0.5;
    if (vFold.x > 0.0)
    {
        // Strands down the pour (STRAND_*): a noise on the lip's rest coordinates, fine across the pour and stretched
        // along it, slid with the flow as the lace is, ridged (high on the strands, low in the thin sheet between);
        // the fold's own u (vTongue.x) is the along coordinate so the strands run the path, whatever the crest's bend.
        vec2 side = vec2(-flowDir.y, flowDir.x);
        vec3 ph = flowPhases();
        vec2 slide = flowDir * (speed * FOLD_POUR_STRETCH * FLOW_PERIOD);
        vec2 s0 = vec2(dot(vRestXZ - slide * ph.x, side) * STRAND_ACROSS, vTongue.x * 4.0 * STRAND_ALONG);
        vec2 s1 = vec2(dot(vRestXZ - slide * ph.y, side) * STRAND_ACROSS, vTongue.x * 4.0 * STRAND_ALONG);
        float n0 = 0.65 * valueNoise(s0) + 0.35 * valueNoise(s0 * vec2(2.3, 1.7) + 7.3);
        float n1 = 0.65 * valueNoise(s1) + 0.35 * valueNoise(s1 * vec2(2.3, 1.7) + 7.3);
        strand = mix(n1, n0, ph.z);
        float ridge = 1.0 - abs(2.0 * strand - 1.0);
        N = normalize(N + vec3(side.x, 0.0, side.y) * ((strand - 0.5) * STRAND_SLOPE * (0.3 + 0.7 * vTongue.x)) * (1.0 - vFold.y));

        float far = smoothstep(0.3, 1.0, pourFoot);
        float coverage = vBreakerFoam * mix(0.7, 0.5 + STRAND_FOAM * ridge, 1.0 - vFold.y); // the inside's rows at the surface's coverage: the back wall meets the clipmap at the foot
        float tongue = flowingLaceAt(pour, pourFoot, flowDir, speed * FOLD_POUR_STRETCH * fine, coverage, 1.0);
        foam = max(foam, vFold.x * mix(tongue, vBreakerFoam, far));
    }

    // Debug: red = folding (1 - J), green = J, blue = foam coverage before lace (whitecap or surf).
    if (frame.foamParams.z > 0.5)
    {
        outColor = vec4(clamp(1.0 - J, 0.0, 1.0), clamp(J, 0.0, 1.0) * 0.5, max(whitecap, coverage), 1.0);
        return;
    }

    vec3 colour = shadeWater(vWorldPos, N, max(waterDepth(vRestXZ), 0.0), thickness, zone, foam);

    // A folded tongue is a sheet of water lit through. Between its lace the outer skin is the lip's body (LIP_*): a thin
    // sheet lit from behind, bright turquoise, brighter where it is thin (vTongue.y) and toward the sun, mixed over the
    // water shading, whose Fresnel showed the sky at grazing angles, as pale as the veins. Its inside (the ceiling and
    // the back wall, vFold.y) is the tube's: dark turquoise lit by what comes through the lip and the wall (TUBE_*).
    // Both tonemapped and fogged as shadeWater does. And the last stretch of the path fades out between its strands of
    // foam, spray rather than a cut edge.
    if (vFold.x > 0.0)
    {
        vec3 toCam = frame.cameraPos.xyz - vWorldPos;
        float dist = length(toCam);
        vec3 V = toCam / max(dist, 1e-4);
        float fog = 1.0 - exp(-dist * frame.skyHorizon.w);
        // The body is thinner between the strands: lighter streaks where the light comes through.
        float ridge = 1.0 - abs(2.0 * strand - 1.0);
        float through = exp(-FOLD_ABSORPTION * vTongue.y * (1.0 - STRAND_THIN * (1.0 - ridge) * (1.0 - vFold.y)));
        float sunThrough = pow(max(dot(V, -frame.sunDir.xyz), 0.0), 4.0) * frame.sunDir.w;
        vec3 inside = pow(acesFit(TUBE_COLOUR * (TUBE_SHADE + TUBE_THROUGH * through)), vec3(1.0 / 2.2));
        vec3 body = pow(acesFit(LIP_COLOUR * (LIP_SHADE + through * (LIP_THROUGH + LIP_SUN * sunThrough))), vec3(1.0 / 2.2));
        colour = mix(colour, mix(body, frame.skyHorizon.rgb, fog), LIP_BODY_MIX * (1.0 - foam) * vFold.x * (1.0 - vFold.y));
        colour = mix(colour, mix(inside, frame.skyHorizon.rgb, fog), vTongue.z * (1.0 - foam));
        alpha *= 1.0 - FOLD_TIP_FADE * smoothstep(0.85, 1.0, vTongue.x) * (1.0 - foam) * vFold.x;
    }

    // Thin open water is see-through (SHEET_OPAQUE): the share of the pixel that is neither the sky's reflection, foam nor
    // the water body (which builds up over the first SHEET_OPAQUE of thickness) shows the terrain behind, darkened to
    // WET_SAND as wet sand is; the blend is straight alpha, so that share is left out of the colour and taken off the
    // alpha. Opaque at distance, where the fog has the pixel anyway. Zones keep their own fade.
    if (thickness < SHEET_OPAQUE)
    {
        vec3 toEye = frame.cameraPos.xyz - vWorldPos;
        float eyeDist = length(toEye);
        float Fv = fresnelSchlick(max(dot(N, toEye / max(eyeDist, 1e-4)), 0.0));
        float through = (1.0 - Fv) * (1.0 - smoothstep(0.0, SHEET_OPAQUE, thickness)) * (1.0 - foam) * exp(-eyeDist * frame.skyHorizon.w) * (1.0 - zone) * (1.0 - vFold.x);
        float alphaOpen = 1.0 - through * WET_SAND;
        colour *= (1.0 - through) / alphaOpen;
        alpha = mix(alphaOpen * waterline, alpha, zone);
    }

    // Foam keeps thin sheets visible, but fades out with them so films clinging to walls don't read as white shards.
    outColor = vec4(colour, max(alpha, foam * smoothstep(0.08, 0.35, thickness)));
}
