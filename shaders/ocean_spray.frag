#version 450
#extension GL_GOOGLE_include_directive : require
// Spray: soft round puffs of white water lit by sun and sky, with the ocean's tonemap and fog.

#include "ocean_common.glsl"

layout(location = 0) in vec3 vWorldPos;
layout(location = 1) in vec2 vCorner;
layout(location = 2) in float vFade;

layout(location = 0) out vec4 outColor;

const float OPACITY = 0.55;

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
    float sun = 0.6 * frame.sunDir.w * max(frame.sunDir.y, 0.0) * rainReaches(vWorldPos);
    vec3 colour = vec3(0.9) * (sun + ambient);
    colour = pow(acesFit(colour), vec3(1.0 / 2.2));

    float dist = length(frame.cameraPos.xyz - vWorldPos);
    colour = mix(colour, frame.skyHorizon.rgb, 1.0 - exp(-dist * frame.skyHorizon.w));

    float alpha = OPACITY * vFade * (1.0 - r2) * (1.0 - r2);
    outColor = vec4(colour, alpha);
}
