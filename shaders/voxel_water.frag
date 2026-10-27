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
    vec4 wind;        // xy = 10 m wind (m/s, the way it blows toward)
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

const float GRAVITY = 9.81;
const float TWO_PI = 6.2831853;

// One wind-sea component: a deep-water wave of wavelength lambda running along the wind turned by ang, at steepness
// steep (its slope amplitude) once the wind is over u1 m/s, none under u0. Returns its height-field slope.
vec2 ripple(vec2 p, float t, vec2 d, vec2 n, float ang, float lambda, float steep, float U, float u0, float u1)
{
    vec2 k = d * cos(ang) + n * sin(ang);
    float kk = TWO_PI / lambda;
    float w = sqrt(GRAVITY * kk);
    return k * (steep * smoothstep(u0, u1, U)) * cos(dot(k, p) * kk - w * t);
}

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

// Wind ripples: four short waves spread about the wind, raised by its speed and scaled by how far the wind reaches
// this water. Sheltered water (e = 0) has none and lies as glass.
vec2 windRippleSlope(vec2 p, float t, vec2 wind, float e)
{
    float U = length(wind);
    if (e <= 0.001 || U < 1.0)
        return vec2(0.0);

    vec2 d = wind / U;
    vec2 n = vec2(-d.y, d.x);
    vec2 s = ripple(p, t, d, n, -0.45, 0.45, 0.10, U, 1.5, 4.0);
    s += ripple(p, t, d, n, 0.20, 0.8, 0.09, U, 2.0, 5.0);
    s += ripple(p, t, d, n, -0.10, 1.4, 0.07, U, 3.0, 7.0);
    s += ripple(p, t, d, n, 0.50, 2.4, 0.05, U, 4.0, 10.0);
    return s * e;
}

void main()
{
    vec3 toCam = frame.cameraPos.xyz - vWorldPos;
    float dist = length(toCam);
    vec3 V = toCam / max(dist, 1e-4);
    vec3 L = frame.sunDir.xyz;
    float intensity = frame.sunDir.w;
    float time = frame.cameraPos.w;

    // Wind ripples where the wind reaches: the walls and shore upwind (windReach) and the cover map's exposure (roofs
    // and caves, rain_cover.glsl). They fade with distance so they don't alias.
    vec3 N = normalize(vNormal);
    float rippleFade = 1.0 - smoothstep(30.0, 120.0, dist);
    float exposure = rippleFade > 0.0 ? windReach(vWorldPos, frame.wind.xy) * windCover(vWorldPos.xz).x : 0.0;
    vec2 slope = windRippleSlope(vWorldPos.xz, time, frame.wind.xy, exposure) * rippleFade;
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
