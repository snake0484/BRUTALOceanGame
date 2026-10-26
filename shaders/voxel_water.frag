#version 450
#extension GL_GOOGLE_include_directive : require
// Voxel water surface fragment shader: ripple normals, Fresnel sky reflection, sun highlight,
// thickness tint and alpha, ocean-matched tonemap and fog.

#include "rain_ripples.glsl"

#define RAIN_COVER_SET 0
#define RAIN_COVER_BINDING 1
#include "rain_cover.glsl"

layout(set = 0, binding = 0, std140) uniform Frame {
    mat4 viewProj;
    vec4 cameraPos;   // xyz world, w = time
    vec4 sunDir;      // xyz toward sun, w = intensity
    vec4 skyZenith;
    vec4 skyHorizon;  // w = fog density
    vec4 rain;        // x = intensity 0..1, y = its time (s, looped); as the ocean's foamParams.xy
} frame;

layout(location = 0) in vec3 vWorldPos;
layout(location = 1) in vec3 vNormal;
layout(location = 2) in float vThickness;

layout(location = 0) out vec4 outColor;

const float SHININESS = 900.0;
const vec3 SHALLOW = vec3(0.10, 0.42, 0.40);
const vec3 DEEP = vec3(0.02, 0.12, 0.13);

// Same sky as the ocean: gradient plus sun glow.
vec3 sky(vec3 dir)
{
    vec3 L = frame.sunDir.xyz;
    float t = pow(clamp(dir.y, 0.0, 1.0), 0.45);
    vec3 c = mix(frame.skyHorizon.rgb, frame.skyZenith.rgb, t);
    float s = max(dot(dir, L), 0.0);
    c += vec3(1.0, 0.9, 0.7) * frame.sunDir.w * (pow(s, 8.0) * 0.08 + pow(s, 64.0) * 0.25);
    return c;
}

float fresnelSchlick(float cosTheta)
{
    const float F0 = 0.02;
    return F0 + (1.0 - F0) * pow(1.0 - cosTheta, 5.0);
}

vec3 acesFit(vec3 x)
{
    return clamp((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14), 0.0, 1.0);
}

// Height-field slope (d/dx, d/dz) of three scrolling sine waves.
vec2 rippleSlope(vec2 p, float t)
{
    const vec2 D0 = vec2(0.8, 0.6);
    const vec2 D1 = vec2(-0.5, 0.87);
    const vec2 D2 = vec2(0.97, -0.24);
    vec2 s = D0 * (0.020 * 2.1) * cos(dot(D0, p) * 2.1 + t * 1.7);
    s += D1 * (0.014 * 3.7) * cos(dot(D1, p) * 3.7 + t * 2.3);
    s += D2 * (0.008 * 6.3) * cos(dot(D2, p) * 6.3 + t * 3.1);
    return s;
}

void main()
{
    vec3 toCam = frame.cameraPos.xyz - vWorldPos;
    float dist = length(toCam);
    vec3 V = toCam / max(dist, 1e-4);
    vec3 L = frame.sunDir.xyz;
    float intensity = frame.sunDir.w;
    float time = frame.cameraPos.w;

    // Ripples fade with distance so they don't alias.
    vec3 N = normalize(vNormal);
    float rippleFade = 1.0 - smoothstep(30.0, 120.0, dist);
    vec2 slope = rippleSlope(vWorldPos.xz, time) * rippleFade;
    N = normalize(N + vec3(-slope.x, 0.0, -slope.y));

    // Rain: the ocean's rings and flashes (rain_ripples.glsl), none under cover (a cave pool). No crowns are drawn on
    // voxel water, so flashes throughout.
    float rainWhite = 0.0;
    if (frame.rain.x > 0.0)
    {
        vec3 rain = rainOnWater(vWorldPos.xz, dFdx(vWorldPos.xz), dFdy(vWorldPos.xz), frame.rain.y, frame.rain.x * rainReaches(vWorldPos), 1.0);
        N = normalize(N - vec3(rain.x, 0.0, rain.y));
        rainWhite = rain.z;
    }

    if (dot(N, V) < 0.0)
        N = -N;

    float NdotV = max(dot(N, V), 0.0);
    float F = fresnelSchlick(NdotV);

    vec3 R = reflect(-V, N);
    R.y = max(R.y, 0.01);
    vec3 reflection = sky(normalize(R));

    // Body: shallow turquoise to deep blue-green with thickness, lit by wrap diffuse.
    float depthT = clamp(vThickness / 3.0, 0.0, 1.0);
    float wrap = max((dot(N, L) + 0.5) / 1.5, 0.0);
    vec3 body = mix(SHALLOW, DEEP, depthT) * (0.2 + 0.3 * wrap * intensity);

    vec3 colour = mix(body, reflection, F);

    vec3 H = normalize(V + L);
    float spec = pow(max(dot(N, H), 0.0), SHININESS) * (SHININESS + 8.0) / 25.13;
    colour += vec3(1.0, 0.95, 0.85) * intensity * spec * F * max(dot(N, L), 0.0);

    // Thin water is clear, thick water opaque; grazing views see mostly reflection.
    float alpha = mix(0.35, 0.9, depthT);
    alpha = mix(alpha, 1.0, F);

    vec3 splash = vec3(0.85) * (max(L.y, 0.0) * intensity * 0.5 + 0.5 * (frame.skyZenith.rgb + frame.skyHorizon.rgb));
    colour = mix(colour, splash, 0.8 * rainWhite);
    alpha = max(alpha, 0.8 * rainWhite);

    colour = pow(acesFit(colour), vec3(1.0 / 2.2));

    // Display-space fog; alpha goes to 1 so far water resolves to the fog colour, not to what's behind it.
    float f = 1.0 - exp(-dist * frame.skyHorizon.w);
    colour = mix(colour, frame.skyHorizon.rgb, f);
    alpha = mix(alpha, 1.0, f);

    outColor = vec4(colour, alpha);
}
