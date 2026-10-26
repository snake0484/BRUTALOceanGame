// Rain (RainRenderer): set 1 of the rain pipelines, beside the ocean's set 0 (ocean_common.glsl, include it first).
// The frame's rain block and the splash slots rain_splash.comp writes. What occludes the rain is the ocean set's
// RainCover (rain_cover.glsl).
#ifndef RAIN_COMMON_GLSL
#define RAIN_COMMON_GLSL

#include "rain_ripples.glsl"

layout(std430, set = 1, binding = 0) readonly buffer RainFrame {
    vec4 params; // x = intensity 0..1, y = time (s, looped), z = streak exposure (s), w = radians per pixel
    vec4 air;    // xz = how far the air has carried the rain (m, wrapped), y = mean fall speed (m/s), w = 0
    vec4 wind;   // xy = wind (m/s along x, z), zw = 0
} rain;

#ifdef RAIN_SPLASHES_WRITABLE
#define RAIN_SPLASHES_ACCESS
#else
#define RAIN_SPLASHES_ACCESS readonly
#endif

// Two vec4 per slot: (position, life 0..1 or < 0 when empty), (size, seed, 1 on ground / 0 on water, 0).
layout(std430, set = 1, binding = 1) RAIN_SPLASHES_ACCESS buffer Splashes { vec4 slot[]; } splashes;

// Splash crowns are drawn in a window of impact cells around the camera (out to SPLASH_FAR, rain_ripples.glsl).
const int SPLASH_SIDE = 64;

// The ocean is drawn at xz unless the column holds none (ocean.frag's discard: an inland pit, off the zones).
bool oceanDrawnAt(vec2 xz)
{
    vec2 uv = (xz - frame.depthRect.xy) / frame.depthRect.z;
    if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0))))
        return true;
    vec4 map = depthMapAt(uv);
    return !(map.b < 0.5 && map.r < -500.0);
}

vec3 acesFitRain(vec3 x)
{
    return clamp((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14), 0.0, 1.0);
}

// Water in the air lit by the sky and sun, tonemapped like the ocean (display space), then fogged.
vec3 rainLight(vec3 worldPos, float sunShare)
{
    vec3 ambient = 0.5 * (frame.skyZenith.rgb + frame.skyHorizon.rgb);
    vec3 colour = vec3(0.9) * (sunShare * frame.sunDir.w * max(frame.sunDir.y, 0.0) + ambient);
    colour = pow(acesFitRain(colour), vec3(1.0 / 2.2));
    float dist = length(frame.cameraPos.xyz - worldPos);
    return mix(colour, frame.skyHorizon.rgb, 1.0 - exp(-dist * frame.skyHorizon.w));
}

#endif
