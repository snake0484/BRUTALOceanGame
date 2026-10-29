// Screen-space reflections for every water surface (ScreenReflections.cs, docs/water-ssr.md): the reflected ray is marched
// through the opaque scene's depth (the terrain, copied before any water draws) and, where it meets it, the scene's colour
// there replaces the sky. Included by water_shading.glsl when WATER_SSR_SET is defined (binding 0 the uniforms, 1 the
// scene colour, 2 the scene depth); without it ssrTrace finds nothing. Needs frame.skyHorizon (w = fog density) and acesFit.
#ifndef WATER_SSR_GLSL
#define WATER_SSR_GLSL

#ifdef WATER_SSR_SET
layout(std140, set = WATER_SSR_SET, binding = 0) uniform Ssr {
    mat4 viewProj;
    vec4 screen;  // xy = size (px), z = px between steps where the whole reflection shows, w = the scene was copied this frame
    vec4 params;  // x = on, y = longest ray (m), z = march steps, w = near plane (m): reverse-Z infinite, view depth = w / depth
} ssr;
layout(set = WATER_SSR_SET, binding = 1) uniform sampler2D sceneColour; // display colour: tonemapped, gamma, fogged
layout(set = WATER_SSR_SET, binding = 2) uniform sampler2D sceneDepth;

const int SSR_REFINE = 5;           // binary steps on the last crossing
const float SSR_THICKNESS = 0.03;   // a crossing counts if the ray was this share of the scene's depth in front a step before
const float SSR_EDGE = 0.06;        // uv margin over which a hit fades out at the screen's edge

// The scene's view depth at uv (m); the cleared far plane is infinitely far.
float ssrSceneDepth(vec2 uv)
{
    float d = textureLod(sceneDepth, uv, 0.0).r;
    return d > 0.0 ? ssr.params.w / d : 1e30;
}

// ACES fit (acesFit) inverted: the root of (c y - a) x² + (d y - b) x + e y = 0 that is 0 at y = 0.
vec3 acesInverse(vec3 y)
{
    y = min(y, vec3(0.98));
    vec3 qa = 2.43 * y - 2.51;
    vec3 qb = 0.59 * y - 0.03;
    vec3 qc = 0.14 * y;
    return (-qb - sqrt(max(qb * qb - 4.0 * qa * qc, 0.0))) / (2.0 * qa);
}

// The scene's display colour at a point dist m from the camera back to linear light: its fog taken off, then gamma and the
// tonemap undone. fogged = how much of it was fog, so a hit lost in fog can hand back to the sky.
vec3 ssrLinear(vec3 display, float dist, out float fogged)
{
    fogged = 1.0 - exp(-dist * frame.skyHorizon.w);
    vec3 clear = (display - frame.skyHorizon.rgb * fogged) / max(1.0 - fogged, 0.05);
    return acesInverse(pow(clamp(clear, 0.0, 1.0), vec3(2.2)));
}
#endif

// The opaque scene behind the water at worldPos along the eye's ray (what this pixel shows through it: the bed, a rock):
// its world position, false where there is none (nothing drawn there, the scene in front, no copy this frame).
bool waterSceneBehind(vec3 worldPos, out vec3 behind)
{
    behind = worldPos;
#ifdef WATER_SSR_SET
    if (ssr.screen.w < 0.5)
        return false;

    float sceneW = ssrSceneDepth(gl_FragCoord.xy / ssr.screen.xy);
    float fragW = (ssr.viewProj * vec4(worldPos, 1.0)).w;
    if (sceneW >= 1e29 || sceneW <= fragW || fragW <= 0.0)
        return false;

    vec3 eye = frame.cameraPos.xyz;
    behind = eye + (worldPos - eye) * (sceneW / fragW);
    return true;
#else
    return false;
#endif
}

// The opaque scene seen along R from the water at origin: its linear colour, and returns how much of the reflection it is
// (0 for a miss, fading out at the screen's edge, at the ray's end and into fog, toward the sky the caller has). The ray
// runs in screen space, from the origin to where it leaves the screen, the near plane or its longest length, in
// steps spaced by how much of it shows (shown: its share of the pixel after foam and blur), with a per-pixel offset; a
// step that goes behind the scene's depth, having been in front of it a step before, is the crossing, refined by bisection.
float ssrTrace(vec3 origin, vec3 R, float shown, out vec3 colour)
{
    colour = vec3(0.0);
#ifdef WATER_SSR_SET
    if (ssr.params.x < 0.5)
        return 0.0;

    float len = ssr.params.y;
    vec4 c0 = ssr.viewProj * vec4(origin, 1.0);
    vec4 dc = ssr.viewProj * vec4(R, 0.0);
    float nearW = ssr.params.w * 1.5;
    if (dc.w < 0.0)
        len = min(len, (c0.w - nearW) / -dc.w);
    if (len <= 0.0)
        return 0.0;

    vec4 c1 = c0 + dc * len;
    vec2 p0 = c0.xy / c0.w;
    vec2 p1 = c1.xy / c1.w;
    float k0 = 1.0 / c0.w;
    float k1 = 1.0 / c1.w;

    // Clip the segment to the screen, so no step is spent off it.
    vec2 dp = p1 - p0;
    float tEnd = 1.0;
    if (abs(dp.x) > 1e-6)
        tEnd = min(tEnd, ((dp.x > 0.0 ? 1.0 : -1.0) - p0.x) / dp.x);
    if (abs(dp.y) > 1e-6)
        tEnd = min(tEnd, ((dp.y > 0.0 ? 1.0 : -1.0) - p0.y) / dp.y);
    if (tEnd <= 0.0 || dot(dp * ssr.screen.xy, dp * ssr.screen.xy) * tEnd * tEnd < 4.0)
        return 0.0;

    // A step every ssr.screen.z px where the whole reflection shows, sparser as less of it does (low Fresnel, foam, blur),
    // never more than ssr.params.z: a far ray spans few pixels and a faint one hides what a coarse march misses. Four
    // depths are fetched per pass and tested together, so the steps round up to a multiple of four.
    float pixels = length(dp * 0.5 * ssr.screen.xy) * tEnd;
    float steps = 4.0 * clamp(ceil(pixels * shown / (4.0 * ssr.screen.z)), 1.0, ceil(ssr.params.z / 4.0));
    float dt = tEnd / steps;
    float t0 = dt * fogJitter(gl_FragCoord.xy);
    float prevW = c0.w;
    float t = 0.0;
    bool hit = false;

    for (float i = 0.0; i < steps && !hit; i += 4.0)
    {
        vec4 tc = min(t0 + dt * (i + vec4(1.0, 2.0, 3.0, 4.0)), vec4(tEnd));
        vec4 k = mix(vec4(k0), vec4(k1), tc);
        vec4 rayW = 1.0 / k;
        vec4 sceneW = vec4(ssrSceneDepth(mix(p0, p1, tc.x) * 0.5 + 0.5), ssrSceneDepth(mix(p0, p1, tc.y) * 0.5 + 0.5),
                           ssrSceneDepth(mix(p0, p1, tc.z) * 0.5 + 0.5), ssrSceneDepth(mix(p0, p1, tc.w) * 0.5 + 0.5));
        vec4 before = vec4(prevW, rayW.xyz);
        bvec4 crossed = bvec4(vec4(greaterThanEqual(rayW, sceneW)) * vec4(lessThanEqual(before, sceneW * (1.0 + SSR_THICKNESS))));
        for (int j = 0; j < 4 && any(crossed); j++)
        {
            if (!crossed[j])
                continue;
            vec3 rayPos = origin + R * (len * tc[j] * k1 / k[j]);
            vec3 eye = frame.cameraPos.xyz;
            if (eye.y + (rayPos.y - eye.y) * sceneW[j] / rayW[j] >= origin.y - 0.05)
            {
                hit = true;
                t = tc[j];
                break;
            }
            crossed[j] = false;
        }
        prevW = rayW.w;
    }

    if (!hit)
        return 0.0;

    float lo = max(t - dt, 0.0);
    float hi = t;
    for (int i = 0; i < SSR_REFINE; i++)
    {
        float mid = 0.5 * (lo + hi);
        float rayW = 1.0 / mix(k0, k1, mid);
        if (rayW >= ssrSceneDepth(mix(p0, p1, mid) * 0.5 + 0.5))
            hi = mid;
        else
            lo = mid;
    }

    // World distance along the ray: screen-space t is perspective, s = t k1 / mix(k0, k1, t) is the share of its length.
    vec2 uv = mix(p0, p1, hi) * 0.5 + 0.5;
    float travel = len * hi * k1 / mix(k0, k1, hi);
    vec3 hitPos = origin + R * travel;

    float fogged;
    colour = ssrLinear(textureLod(sceneColour, uv, 0.0).rgb, length(hitPos - frame.cameraPos.xyz), fogged);

    vec2 edge = min(uv, 1.0 - uv);
    float weight = smoothstep(0.0, SSR_EDGE, min(edge.x, edge.y));
    weight *= 1.0 - smoothstep(0.8, 1.0, travel / ssr.params.y);
    weight *= smoothstep(0.02, 0.15, 1.0 - fogged);

    // The reflected light crosses the air between the water and the hit too: fogged toward the sky it would show there.
    return weight * exp(-travel * frame.skyHorizon.w);
#else
    return 0.0;
#endif
}

#endif
