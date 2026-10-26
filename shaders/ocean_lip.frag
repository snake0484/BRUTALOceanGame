#version 450
#extension GL_GOOGLE_include_directive : require
// Breaker lip shading: the ocean surface's own model (ocean_shading.glsl) with the same FFT detail on its normal and the
// water depth under it, so the lip is the wave's water rather than a separate sheet. At the root it takes the surface's
// own normal, so it shades as the crest it grows from; its underside and the tube ceiling are in the tube's shadow; a
// thin sheet lets a little more sun through, and the thrown edge turns to foam as the lip throws and breaks.

#include "ocean_common.glsl"
#include "ocean_shading.glsl"

layout(location = 0) in vec3 vWorldPos;
layout(location = 1) in vec3 vNormal;
layout(location = 2) in float vAlong;
layout(location = 3) in float vCurl;
layout(location = 4) in float vThickness;
layout(location = 5) in float vBaseFoam;   // the face's whitewater at the lip's root
layout(location = 6) in float vCollapse;   // 0 while throwing .. 1 once it has fallen into the crash
layout(location = 7) in vec2 vLipUV;       // texture position (m): the crest point under it, out along the tongue
layout(location = 8) in vec2 vHook;        // the tongue's direction in texture space
layout(location = 9) in vec3 vRootNormal;  // the surface normal at the crest
layout(location = 10) in float vReach;
layout(location = 11) in float vPlunge;    // 0 spilling .. 1 plunging
layout(location = 12) in float vFlowRoot;  // the water's forward speed at the crest top, m/s (breakerFace's flow there)

layout(location = 0) out vec4 outColor;

const float ABSORPTION = 0.9; // per metre of sheet
const float ROOT_BLEND = 0.25; // share of the tongue over which its normal and colour blend from the surface's
const float LIP_DETAIL = 0.3;  // share of the surface's ripple slopes bent onto the tongue
const float STREAK_ACROSS = 0.9; // pour lines down the tongue: cells per metre across it (2.5 ribbed the sheet) ...
const float STREAK_ALONG = 0.3;  // ... and along it (stretched streaks)
const float STREAK_SLOPE = 0.1;  // how much they groove the surface
const float SPILL_WHITE = 0.55;  // a spilling tongue's foam coverage at its root, rising toward the tip: tumbling whitewater, not glass
const float STREAK_POUR = 4.0;   // m/s: the streaks slide toward the tip at this speed in the lip's frame

// Water pours over the lip and down the tongue, faster the further it has fallen: everything drawn on it, the foam lace
// and the ripple detail, streams out along the tongue by the shared flow mapping (ocean_shading.glsl flowingLace,
// flowingPartials), starting at the speed the surface's water has at the crest top (vFlowRoot: breakerFace's flow
// there, which the surface's own lace and ripples stream by), so the back, the crest and the tongue stream as one, and
// FLOW_TONGUE faster by the tip. (A pattern that only rode with the crest read as frozen; an outward stream with a plain
// clock crushed the foam into strings within a minute.)
float pourSpeed()
{
    return vFlowRoot * (1.0 + FLOW_TONGUE * vAlong);
}

void main()
{
    // Each skin of the tongue is shaded from its own side, the body's outside (ocean_lip.vert's normals): the outer skin
    // from in front of the curl, the inner skin (the tube's ceiling) from inside the tube. A fragment whose side faces
    // away from the eye is the far skin seen through the near one, or the lip's inside through an end. Turning its
    // normal to the eye by triangle winding instead (which differs between the skins and with the crest's orientation)
    // sometimes pointed it away, and Fresnel then drew that skin as pure sky reflection.
    vec3 N = normalize(vNormal);
    if (dot(N, frame.cameraPos.xyz - vWorldPos) < 0.0)
        discard;

    // At the root the tongue is the crest: its normal eases from the surface's own over the first part of the tongue.
    float root = smoothstep(0.0, ROOT_BLEND, vAlong);
    N = normalize(mix(vRootNormal, N, root));

    // FFT detail, bent onto the sheet and pouring down it: ripples like those on the surface it leaves.
    vec2 lipUV = vLipUV;
    float speed = pourSpeed();
    // Scaled down: at full slope strength on a sheet seen edge-on the ripples aliased into ribbing.
    float pxz, persistent;
    vec2 detail = LIP_DETAIL * flowingPartials(lipUV, vHook, speed, length(fwidth(lipUV)), vec3(1.0), pxz, persistent).xy;
    N = normalize(N - vec3(detail.x, 0.0, detail.y));

    // Pour lines: water running down the tongue grooves it into streaks that slide toward the tip (a plain scroll: a
    // translation never stretches), stronger toward the tip where the sheet thins and tears.
    vec2 side = vec2(-vHook.y, vHook.x);
    float pour = STREAK_POUR * mod(frame.cameraPos.w, LACE_WRAP);
    vec2 sp = vec2(dot(lipUV, side), dot(lipUV, vHook) - pour);
    float streak = 0.65 * valueNoise(sp * vec2(STREAK_ACROSS, STREAK_ALONG)) + 0.35 * valueNoise(sp * vec2(2.3 * STREAK_ACROSS, 1.7 * STREAK_ALONG) + 7.3);
    N = normalize(N + vec3(side.x, 0.0, side.y) * ((streak - 0.5) * STREAK_SLOPE * (0.3 + 0.7 * vAlong)));

    // Water under the lip: its colour comes from the same seabed and depth as the surface below.
    vec4 map = waterMap(vWorldPos.xz);
    float depth = max(map.r, 0.0);
    float thickness = vWorldPos.y + map.r;

    // A throwing lip is clear water: only its thrown edge feathers into foam (more of it at full curl), and the face's
    // whitewater carries on from the root (a spilling crest's tip is tumbling whitewater, its face foaming under it; a
    // plunging one's is glass). That foam is streaks along the pour, the streak field whitened from its ridges down as
    // the coverage rises: a pouring sheet tears into lines, not lace (the ocean's lace at low coverage drew a net of
    // thin veins over the tongue). Only as a lip crashes does the whole sheet churn to lace.
    float edge = smoothstep(mix(1.0, 0.8, vCurl), 1.0, vAlong);
    float spill = (1.0 - vPlunge) * (SPILL_WHITE + (1.0 - SPILL_WHITE) * vAlong);
    float coverage = max(max(edge, 0.6 * vPlunge * vBaseFoam * (1.0 - 0.6 * vAlong)), spill);
    float streaks = smoothstep(1.0 - 0.9 * coverage, 1.15 - 0.9 * coverage, streak) * (0.5 + 0.5 * coverage);
    float foam = max(streaks, flowingLace(lipUV, vHook, speed, max(vCollapse, spill), 1.0));

    vec3 colour = shadeWater(vWorldPos, N, depth, thickness, map.b, foam);

    // The tube's shadow: the underside of the tongue and its ceiling face down into the tube and get little sky.
    float under = clamp(-N.y, 0.0, 1.0) * (1.0 - 0.5 * foam);
    colour *= mix(1.0, LIP_UNDERSIDE, under);

    // Sun shining through the sheet lights it from behind: the thinner the tongue the more comes through, so the
    // feathered edge glows while the root stays a solid mass.
    float through = exp(-ABSORPTION * vThickness) * pow(max(dot(normalize(frame.cameraPos.xyz - vWorldPos), -frame.sunDir.xyz), 0.0), 4.0);
    colour += frame.waterScatter.rgb * frame.sunDir.w * through * 0.3 * (1.0 - foam);

    // Grows out of the face: transparent at its root on the crest, solid once clear of it, feathering out again at the
    // tip while it is still reaching (the tip is water breaking up into spray, not a cut edge); gone once a plunging lip
    // has landed, the whitewater and spray carrying on from there.
    float rootAlpha = smoothstep(0.0, 0.12, vAlong);
    float tip = (1.0 - smoothstep(0.85, 1.0, vAlong) * (1.0 - vReach) * (1.0 - foam)) * (1.0 - 0.7 * smoothstep(0.92, 1.0, vAlong));
    float landed = 1.0 - smoothstep(0.4, 1.0, vCollapse);
    outColor = vec4(colour, rootAlpha * tip * landed * mix(0.9, 1.0, max(foam, 1.0 - exp(-ABSORPTION * vThickness))));
}
