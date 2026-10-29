#version 450
#extension GL_GOOGLE_include_directive : require
// Spray: soft puffs and droplet streaks of white water lit by sun and sky, with the ocean's tonemap and fog. Droplets and
// mist scatter sunlight forward (Henyey-Greenstein, g per particle): spray between the eye and the sun glows.

#include "ocean_common.glsl"

layout(location = 0) in vec3 vWorldPos;
layout(location = 1) in vec2 vCorner;
layout(location = 2) in float vFade;
layout(location = 3) in float vPhase;

layout(location = 0) out vec4 outColor;

const float OPACITY = 0.55;
const float MAX_PHASE = 6.0; // so mist straight into the sun glows without blowing out

vec3 acesFit(vec3 x)
{
    return clamp((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14), 0.0, 1.0);
}

void main()
{
    float r2 = dot(vCorner, vCorner);
    if (r2 >= 1.0)
        discard;

    // No sun on spray under cover (rain_cover.glsl): wall spray in a sea cave.
    vec3 ambient = 0.5 * (frame.skyZenith.rgb + frame.skyHorizon.rgb);
    float g = vPhase;
    float c = dot(normalize(vWorldPos - frame.cameraPos.xyz), frame.sunDir.xyz);
    float phase = min((1.0 - g * g) / pow(1.0 + g * g - 2.0 * g * c, 1.5), MAX_PHASE);
    float sun = 0.6 * phase * frame.sunDir.w * max(frame.sunDir.y, 0.0) * rainReaches(vWorldPos);
    vec3 colour = vec3(0.9) * (sun + ambient);
    colour = pow(acesFit(colour), vec3(1.0 / 2.2));

    float dist = length(frame.cameraPos.xyz - vWorldPos);
    colour = mix(colour, frame.skyHorizon.rgb, 1.0 - exp(-dist * frame.skyHorizon.w));

    float alpha = OPACITY * vFade * (1.0 - r2) * (1.0 - r2);
    outColor = vec4(colour, alpha);
}
