// Volumetric water fog (docs/water-fog.md): a march through the fog volume around the camera (WaterFogVolume, one RGBA8
// texel per metre: rgb the water's scatter tint, a its density, 255 solid) that integrates single scattering along a ray,
// and the open sea's closed form for the water beyond the window and the far field. Needs a uniform block named frame
// with sunDir (w = intensity) and skyZenith, and WATER_FOG_SET / WATER_FOG_BINDING defined before including: the block
// takes that binding, the volume the next.
#ifndef WATER_FOG_GLSL
#define WATER_FOG_GLSL

layout(std140, set = WATER_FOG_SET, binding = WATER_FOG_BINDING) uniform WaterFog {
    vec4 window;  // xyz = world origin of the window (m, the lowest block's corner), w = enabled
    vec4 size;    // xyz = texels per side, w = sea level
    vec4 params;  // x = extinction (1/m) at density 1, y = stop transmittance (the hard limit), z = step (m), w = max steps
    vec4 sea;     // rgb = the open sea's tint, a = its density (the fog outside the blocks and beyond the window)
    vec4 camera;  // x = camera under water, y = the surface over it (world y), z = far blend start (m), w = far blend end
} fog;

layout(set = WATER_FOG_SET, binding = WATER_FOG_BINDING + 1) uniform sampler3D fogVolume;

const float FOG_SOLID = 0.99;                            // a texel this dense is solid: the bed, which stops a march
const vec3 FOG_SUN_ABSORB = vec3(0.30, 0.09, 0.06);     // per metre of depth under the surface, the light that reaches the fog (terrain.frag ABSORPTION)
const float FOG_GAIN_DOWN = 0.35;                        // in-scatter per unit extinction of the light coming down: deep water stays the dark blue it was
const float FOG_GAIN_BED = 4.0;                          // ... and of the light bounced up off the sand: shallows glow the turquoise they did by depth
const vec3 FOG_BED_ABSORB = vec3(0.6, 0.3, 0.25);        // per metre down to the sand and back: the sand's light is gone by a few metres, as the depth body went dark by 2.5 m
const vec3 FOG_BED_ALBEDO = vec3(0.7 * 0.62, 0.7 * 0.56, 0.7 * 0.38); // wet sand (water_shading.glsl WET_SAND * SAND_ALBEDO)
const vec3 FOG_DEEP = vec3(0.0032, 0.021, 0.044);        // what deep water glows with however deep (OceanRenderer.WaterDeep lit as the depth body was)
const float FOG_FAR = 1.0e9;                             // a march with no end but the water's; a bed that deep is no bed

bool fogEnabled()
{
    return fog.window.w > 0.5;
}

// The texel of the cell at world p (a cell's sample point is its integer position, so the cell is round(p)); the volume is
// toroidal, so the world cell mod the size is the texel, which repeat addressing does.
vec4 fogAt(vec3 p)
{
    return texture(fogVolume, (p + 0.5) / fog.size.xyz);
}

bool fogInside(vec3 p)
{
    vec3 r = p - fog.window.xyz;
    return all(greaterThanEqual(r, vec3(-0.5))) && all(lessThan(r, fog.size.xyz - 0.5));
}

// Light coming down through the surface, before the water takes its share (water_shading.glsl lights the seabed by it).
vec3 fogDown()
{
    return vec3(0.5 * max(frame.sunDir.y, 0.0) * frame.sunDir.w) + 0.5 * frame.skyZenith.rgb;
}

// What a texel's water glows with at depth d under the surface, over a bed bedDepth under it (FOG_FAR for none): its tint
// by the light coming down, and by the light that reached the sand and bounced back up to it (the bed taken as flat under
// the ray), plus the deep glow.
vec3 fogGlow(vec3 tint, float d, float bedDepth)
{
    d = max(d, 0.0);
    vec3 down = fogDown();
    vec3 lit = down * exp(-FOG_SUN_ABSORB * d) * FOG_GAIN_DOWN;
    if (bedDepth < FOG_FAR)
        lit += FOG_BED_ALBEDO * down * exp(-FOG_BED_ABSORB * (bedDepth + max(bedDepth - d, 0.0))) * FOG_GAIN_BED;
    return tint * lit + FOG_DEEP;
}

// Per-pixel sample offset of the march (0..1 of a step), fixed per pixel so it never flickers: without it a 1 m step
// through a sloping bed draws contour bands.
float fogJitter(vec2 fragCoord)
{
    return fract(sin(dot(fragCoord, vec2(12.9898, 78.233))) * 43758.5453);
}

// (e^(b x) - 1) / b, with its limit x at b = 0.
vec3 expm1Over(vec3 b, float x)
{
    vec3 small = vec3(lessThan(abs(b), vec3(1e-4)));
    vec3 safe = mix(b, vec3(1.0), small);
    return mix((exp(safe * x) - 1.0) / safe, vec3(x), small);
}

// Uniform fog of one water (rgb its tint, a its density: the sea's, or a texel's) from q along dir for dist metres
// (FOG_FAR for all of it): the closed form of the march below with fogGlow's light along the ray, so the far field and
// a marched near field agree. Adds the in-scatter, scales t.
void fogUniform(vec4 water, vec3 q, vec3 dir, float dist, float surfaceY, float bedDepth, inout vec3 inscatter, inout float t)
{
    float sigma = fog.params.x * water.a;
    if (sigma <= 0.0 || dist <= 0.0)
        return;

    float d0 = max(surfaceY - q.y, 0.0);
    float slope = max(-dir.y, 0.0);
    float tEnd = dist >= FOG_FAR ? 0.0 : exp(-sigma * dist);
    vec3 down = fogDown();

    // Down-lit: depth grows along the ray, so each channel decays at sigma + k slope.
    vec3 a = vec3(sigma) + FOG_SUN_ABSORB * slope;
    vec3 reach = dist >= FOG_FAR ? vec3(1.0) : 1.0 - exp(-a * dist);
    vec3 sum = water.rgb * down * exp(-FOG_SUN_ABSORB * d0) * FOG_GAIN_DOWN * sigma * reach / a + FOG_DEEP * (1.0 - tEnd);

    // Bed-lit: the way back up from the sand shortens along the ray, so the glow grows at k slope while t falls at sigma,
    // until the ray reaches the bed.
    if (bedDepth < FOG_FAR && bedDepth > d0)
    {
        float toBed = min(dist, slope > 1e-3 ? (bedDepth - d0) / slope : dist);
        vec3 b = FOG_BED_ABSORB * slope - vec3(sigma);
        sum += water.rgb * FOG_BED_ALBEDO * down * exp(-FOG_BED_ABSORB * (2.0 * bedDepth - d0)) * FOG_GAIN_BED * sigma * expm1Over(b, toBed);
    }

    inscatter += t * sum;
    t *= tEnd;
}

// The open sea's fog: the water outside the blocks and beyond the window.
void fogSea(vec3 q, vec3 dir, float dist, float surfaceY, float bedDepth, inout vec3 inscatter, inout float t)
{
    fogUniform(fog.sea, q, dir, dist, surfaceY, bedDepth, inscatter, t);
}

// Marches from p along dir (unit) for at most maxDist through the volume: the in-scattered light is added to inscatter
// and the transmittance t scaled. The march ends at a solid texel (the bed: what was drawn there shows through at t), at
// the hard limit (t under params.y: the rest is fog, t = 0), at maxDist, or on leaving the window, from where the open
// sea's fog carries on below sea level. surfaceY is the water surface the light comes down through, bedDepth the bed
// under it (FOG_FAR for none), for the glow.
void waterFogMarch(vec3 p, vec3 dir, float maxDist, float surfaceY, float bedDepth, float jitter, inout vec3 inscatter, inout float t)
{
    float sigma = fog.params.x;
    float stopT = fog.params.y;
    float step = fog.params.z;
    int maxSteps = int(fog.params.w);
    float travelled = 0.0;

    for (int i = 0; i < maxSteps; i++)
    {
        if (travelled >= maxDist)
            return;

        // Each segment is read at one point inside it, so the path is exact whatever the jitter.
        float ds = min(step, maxDist - travelled);
        vec3 q = p + dir * (travelled + jitter * ds);
        if (!fogInside(q))
            break;

        vec4 f = fogAt(q);
        if (f.a >= FOG_SOLID)
            return;

        if (f.a > 0.0)
        {
            float tn = t * exp(-sigma * f.a * ds);
            vec3 glow = fogGlow(f.rgb, surfaceY - q.y, bedDepth);
            inscatter += glow * (t - tn);
            t = tn;

            if (t < stopT)
            {
                inscatter += glow * t;
                t = 0.0;
                return;
            }
        }

        travelled += ds;
    }

    vec3 q = p + dir * travelled;
    if (q.y < fog.size.w)
        fogSea(q, dir, maxDist >= FOG_FAR ? FOG_FAR : maxDist - travelled, surfaceY, bedDepth, inscatter, t);
}

#endif
