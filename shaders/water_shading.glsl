// Water shading for the ocean surface (ocean.frag): the body by the water fog (water_fog.glsl, marched near, closed form
// far), reflection of the scene (water_ssr.glsl) or the sky, sun, foam lace, tonemap and fog.
// Needs a uniform block named frame with cameraPos (w = time), sunDir (w = intensity), skyZenith, skyHorizon (w = fog
// density), waterDeep and waterScatter, WATER_FOG_SET / WATER_FOG_BINDING defined for the fog's bindings, and
// WATER_SSR_SET for the scene's reflection (the sky only without it). Fragment stage only (gl_FragCoord).
#ifndef WATER_SHADING_GLSL
#define WATER_SHADING_GLSL

#include "water_fog.glsl"

#ifndef WATER_NOISE_GLSL
#define WATER_NOISE_GLSL
const float LACE_WRAP = 600.0;  // seconds: foam texture clocks wrap so noise coordinates stay small

float hash12(vec2 p)
{
    vec3 p3 = fract(vec3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}

float valueNoise(vec2 p)
{
    vec2 i = floor(p);
    vec2 f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash12(i), hash12(i + vec2(1.0, 0.0)), u.x), mix(hash12(i + vec2(0.0, 1.0)), hash12(i + 1.0), u.x), u.y);
}
#endif

const float PI = 3.14159265359;
const float ROUGHNESS = 0.08;
const vec3 FOAM_ALBEDO = vec3(0.85);
const vec3 SAND_ALBEDO = vec3(0.62, 0.56, 0.38);         // terrain.frag palette: sand
const vec3 SEABED_ABSORPTION = vec3(0.45, 0.07, 0.04);  // sea water, per metre: red goes first
const vec3 SHALLOW_SCATTER = vec3(0.04, 0.24, 0.22);     // turquoise in-scatter that replaces the seabed with depth
const float WET_SAND = 0.7;                              // sand under water is darker than the dry beach
const float RAIN_ROUGHNESS = 0.3;                        // a sea roughened by rain: rings finer than a pixel ...
const float RAIN_SKY_BLUR = 0.35;                        // ... scatter the sky's reflection toward its average

// 0..1: how much rain roughens this pixel's surface (ocean.frag sets it).
float gRainRough = 0.0;

// 0..1: how much of this pixel's water body the fog march shaded (1 near the camera, 0 by the far blend's end, with the
// fog off, or with the camera under water), for callers that keep a depth rule for the far field.
float gFogNear = 0.0;

// Sky gradient plus sun glow (disk handled by specular on water).
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

// GGX distribution * Smith-Schlick visibility.
float ggxSpecular(vec3 N, vec3 V, vec3 L)
{
    vec3 H = normalize(V + L);
    float NdotH = max(dot(N, H), 0.0);
    float NdotL = max(dot(N, L), 0.0);
    float NdotV = max(dot(N, V), 1e-4);
    float roughness = mix(ROUGHNESS, RAIN_ROUGHNESS, gRainRough);
    float a2 = roughness * roughness * roughness * roughness;
    float d = NdotH * NdotH * (a2 - 1.0) + 1.0;
    float D = a2 / (PI * d * d);
    float k = roughness * 0.5;
    float vis = 1.0 / ((NdotL * (1.0 - k) + k) * (NdotV * (1.0 - k) + k));
    return D * vis * 0.25 * NdotL;
}

vec3 acesFit(vec3 x)
{
    return clamp((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14), 0.0, 1.0);
}

const float SSR_MIN_SHARE = 0.025; // reflection share (Fresnel, less foam) under which the scene isn't traced: 0.02 head on

#include "water_ssr.glsl"

const float LACE_SCALE = 1.4;      // lace cells per metre
const float LACE_CALM = 0.35;      // churn rate of foam left lying on the water ...
const float LACE_ROLLER = 1.6;     // ... and of fresh whitewater in a breaking crest

// Ridged noise, high along a network of veins between cells: the shape of foam lace on broken water. Time t churns it:
// the domain warp and the fine bubbles drift, so the veins wriggle, break and re-form in place instead of sliding.
float lace(vec2 p, float t)
{
    // Each octave on its own rotated grid, so the veins don't line up with the noise lattice into squares.
    const mat2 turnB = mat2(0.6, 0.8, -0.8, 0.6);
    const mat2 turnC = mat2(-0.28, 0.96, -0.96, -0.28);
    vec2 drift = vec2(0.21, -0.17) * t;
    vec2 w = p + 0.5 * vec2(valueNoise(p * 0.5 + 5.2 + drift), valueNoise(p * 0.5 + 9.7 - drift.yx));
    float a = 1.0 - abs(2.0 * valueNoise(w + 0.3 * drift.yx) - 1.0);
    float b = 1.0 - abs(2.0 * valueNoise(turnB * w * 2.7 + 13.1 + 1.7 * drift) - 1.0);
    float c = 1.0 - abs(2.0 * valueNoise(turnC * w * 6.3 + 41.7 + vec2(0.9, 0.6) * t) - 1.0);
    return 0.5 * a * a + 0.3 * b * b + 0.2 * c * c;
}

// Surf foam as lace: coverage 0..1 sets how much of the lace field l is white, so a little foam is thin veins with the
// water showing through and a fresh roller is a lumpy mass, thick on the lace's ridges and thin between, never flat
// white. It evens out to its mean where a pixel can't resolve it: footprint is the pixel's size in lace cells.
float laceVeins(float l, float footprint, float coverage)
{
    float edge = 1.0 - 0.9 * coverage;
    float veins = smoothstep(edge - 0.08, edge + 0.08, l) * (0.55 + 0.45 * coverage);
    veins *= mix(1.0, 0.6 + 0.4 * smoothstep(edge, edge + 0.45, l), coverage);
    return mix(veins, 0.4 * coverage, smoothstep(0.3, 1.0, footprint));
}

// Foam lace at xz (callers pass the water's rest position, so it rides the orbital motion), churning in place at the
// churn rate (fresh whitewater, churn 1, faster than foam left lying).
float foamLace(vec2 xz, float coverage, float churn)
{
    float footprint = length(fwidth(xz * LACE_SCALE));
    if (coverage <= 0.01)
        return 0.0;
    float t = mod(frame.cameraPos.w, LACE_WRAP) * mix(LACE_CALM, LACE_ROLLER, churn);
    return laceVeins(lace(xz * LACE_SCALE, t), footprint, coverage);
}

// The water body by depth alone, as before the fog (the fog off): deep tint, scatter through crests, and the sand seen
// through the shallows.
vec3 waterBodyDepth(vec3 worldPos, vec3 N, vec3 V, float depth, float thickness)
{
    vec3 L = frame.sunDir.xyz;
    float intensity = frame.sunDir.w;

    // Body colour: deep tint + subsurface scatter through wave crests + wrap diffuse.
    float sss = pow(max(dot(V, -L), 0.0), 4.0) * clamp((worldPos.y + 1.5) * 0.25, 0.0, 1.0);
    float wrap = max((dot(N, L) + 0.5) / 1.5, 0.0);
    vec3 deepColour = mix(vec3(0.03, 0.20, 0.18), frame.waterDeep.rgb, smoothstep(0.0, 10.0, depth));
    vec3 body = deepColour * (0.2 + 0.3 * wrap * intensity)
              + frame.waterScatter.rgb * sss * intensity;

    // Shallows: the body is the sand seen through the water column, lit and absorbed like terrain.frag, so a long flat
    // beach shelf blends into the beach instead of reading as a dark band.
    // Light goes down to the sand and back up (twice the column), and the water scatters turquoise into the view as it
    // deepens, so shallows read as clear tinted water rather than bare white sand.
    float column = max(thickness, 0.0);
    vec3 seabed = WET_SAND * SAND_ALBEDO * (0.5 * max(L.y, 0.0) * intensity + 0.5 * frame.skyZenith.rgb) * exp(-2.0 * SEABED_ABSORPTION * column);
    vec3 sand = mix(seabed, SHALLOW_SCATTER * (0.3 + 0.4 * wrap * intensity), 1.0 - exp(-0.8 * column));
    return mix(sand, body, smoothstep(0.0, 2.5, thickness));
}

// The water body seen into from the surface at worldPos along -V, by the fog: marched through the volume near the
// camera, the open sea's closed form over the column (then the sand under it) far away, blended between (fog.camera.zw).
// through = the share of what was drawn behind the water (the bed) that reaches the eye, 0 where the body is opaque.
// Two texels of the same water (the same tint; the density may differ, as where a pour's silt has blended in).
bool fogSameWater(vec4 a, vec4 b)
{
    return a.a < FOG_SOLID && b.a < FOG_SOLID && all(lessThan(abs(a.rgb - b.rgb), vec3(2.0 / 255.0)));
}

const float WATER_IOR = 1.333;

// The body seen through the surface at worldPos with normal N (facing the viewer) along the eye's ray refracted into the
// water: the path to the bed shortens and the fog reads with the ripples. (The bed itself stays where the framebuffer
// has it: there is no colour buffer to look up at the refracted point.)
// The bed is the one this pixel shows, where the eye's ray meets the opaque scene (its depth, copied before the water),
// not the ground straight under the fragment: on a slope the two differ by metres, so looking up a beach the water was
// fogged as deep as the column under the surface point and looking out over a drop-off as shallow. The fragment's
// thickness stands in where the scene has nothing behind it.
vec3 waterBodyFog(vec3 worldPos, vec3 N, vec3 V, float dist, float thickness, out float through)
{
    float farW = smoothstep(fog.camera.z, fog.camera.w, dist);
    gFogNear = 1.0 - farW;
    vec3 dir = refract(-V, N, 1.0 / WATER_IOR);
    dir = dot(dir, dir) > 0.0 ? normalize(dir) : -V;
    float column = max(thickness, 0.0);
    float bedDepth = thickness < 1000.0 ? column : FOG_FAR;
    vec3 behind;
    if (waterSceneBehind(worldPos, behind) && behind.y < worldPos.y)
    {
        column = worldPos.y - behind.y;
        bedDepth = column;
    }
    vec3 near = vec3(0.0);
    float tNear = 1.0;
    if (farW < 1.0)
    {
        // A crest stands over the sea-level layer the volume's sea columns stop at (no sim block there, so the texel is
        // air): its water down to that layer is the sea's closed form first, no longer than the column is thick.
        // The march ends at the bed the fragment knows (its thickness, continuous from the depth map or the mesh): the
        // volume's solid texels are a metre coarse and stopped it a column at a time, which drew the shallows as 1 m
        // squares. They stay as the stop for what the thickness can't know (a cave roof, a dug pit's wall).
        vec3 start = worldPos;
        float toBed = bedDepth < FOG_FAR ? column / max(-dir.y, 0.05) : FOG_FAR;
        // The volume only says something the fragment doesn't know where the water changes along the ray: a pour's silt
        // in a pond, stirred water in a pit. Water of one colour as far as the ray goes (the texel at the fragment, or
        // under it for a crest over the volume's top cell, against one a couple of metres in; the sea's where there is
        // none: open sea past the mask's reach, the swash up a gentle beach) takes the closed form in that water's own
        // tint to the bed, continuous and exact, so a lake is its own colour near and far; marching uniform water at a
        // metre a step met the closed form along a seam and read its density a cell at a time. A crest takes the closed
        // form down to the cell under it first.
        // Solid under it (the swash over a beach's top cell) is no water either: taken as water, the bed's texel (black,
        // density 1) drew a dark strip of 1 m steps along the shore.
        vec4 here = fogAt(worldPos);
        if (here.a == 0.0)
        {
            vec4 below = fogAt(worldPos - vec3(0.0, 1.0, 0.0));
            if (below.a == 0.0 || below.a >= FOG_SOLID)
                here = fog.sea;
            else
            {
                float top = floor(worldPos.y + 0.5) - 0.5;
                float d = min((worldPos.y - top) / max(-dir.y, 0.2), toBed);
                vec4 under = fogAt(worldPos - vec3(0.0, 2.0, 0.0));
                fogUniform(vec4(below.rgb, under.a < FOG_SOLID ? max(below.a, under.a) : below.a), worldPos, dir, d, worldPos.y, bedDepth, near, tNear);
                start += dir * d;
                toBed -= d;
                here = below;
            }
        }

        if (toBed > 0.0)
        {
            // Only another water a couple of metres in means the water changes; the bed there, or air (a shallow bank,
            // the sample past the bed the thickness places), is this water to the bed. Deciding by the sample's cell
            // drew the mesh's triangles as a sawtooth of marched against closed-form water along a lake's shore.
            vec4 deeper = fogAt(start + dir * min(2.0, 0.5 * toBed));
            if (here.a >= FOG_SOLID)
                here = fog.sea;

            bool deeperWater = deeper.a > 0.0 && deeper.a < FOG_SOLID;
            if (!deeperWater || fogSameWater(here, deeper))
            {
                fogUniform(vec4(here.rgb, deeperWater ? max(here.a, deeper.a) : here.a), start, dir, toBed, worldPos.y, bedDepth, near, tNear);
                toBed = 0.0;
            }
        }

        if (toBed > 0.0)
            waterFogMarch(start, dir, toBed, worldPos.y, bedDepth, fogJitter(gl_FragCoord.xy), near, tNear);

        // The sand under the column as the far field estimates it, so the colour is whole (the blend is done after the
        // tonemap, where a colour missing its bed and divided by its alpha came out inflated); the caller fades it by
        // through and adds the sand the framebuffer really holds.
        if (bedDepth < FOG_FAR)
            near += tNear * WET_SAND * SAND_ALBEDO * fogDown() * exp(-FOG_SUN_ABSORB * column);
    }

    // The far field in the water's own tint where the volume still holds it (inside the window), else the sea's.
    vec3 far = vec3(0.0);
    float tFar = 1.0;
    if (farW > 0.0)
    {
        vec4 own = fogInside(worldPos) ? fogAt(worldPos - vec3(0.0, 1.0, 0.0)) : vec4(0.0);
        fogUniform(own.a > 0.0 && own.a < FOG_SOLID ? own : fog.sea, worldPos, dir, column / max(-dir.y, 0.1), worldPos.y, bedDepth, far, tFar);
        far += tFar * WET_SAND * SAND_ALBEDO * fogDown() * exp(-FOG_SUN_ABSORB * column);
    }

    through = tNear * (1.0 - farW);
    return mix(near, far, farW);
}

// Display colour of water at worldPos with normal N: body colour (depth = water depth under it, thickness = water above
// the ground there), sky reflection, sun, foam over it, then tonemap and fog. through = the
// share of the pixel that is what was drawn behind the water (the bed through the fog's transmittance, less the
// reflection's and the foam's shares), for the caller's alpha; the colour returned holds an estimate of it. Under water the
// surface is a window on the sky, and the water between the camera and it is marched.
vec3 shadeWater(vec3 worldPos, vec3 N, float depth, float thickness, float foam, out float through)
{
    vec3 toCam = frame.cameraPos.xyz - worldPos;
    float dist = length(toCam);
    vec3 V = toCam / max(dist, 1e-4);
    vec3 L = frame.sunDir.xyz;
    float intensity = frame.sunDir.w;

    float NdotV = max(dot(N, V), 0.0);
    float F = fresnelSchlick(NdotV);

    through = 0.0;
    gFogNear = 0.0;
    bool under = fogEnabled() && fog.camera.x > 0.5;
    vec3 body;

    // Reflection, kept above the horizon: the opaque scene where the reflected ray meets it on screen, else the sky. Not
    // traced where the reflection's share is too small to show, and marched coarser the less of it survives the blur.
    vec3 R = reflect(-V, N);
    R.y = max(R.y, 0.01);
    R = normalize(R);
    vec3 reflection = sky(R);
    float share = F * (1.0 - foam);
    float blur = min(RAIN_SKY_BLUR * gRainRough, 0.6);
    if (!under && share > SSR_MIN_SHARE)
    {
        vec3 scene;
        float w = ssrTrace(worldPos, R, share * (1.0 - blur), scene);
        reflection = mix(reflection, scene, w * smoothstep(SSR_MIN_SHARE, 2.0 * SSR_MIN_SHARE, share));
    }
    reflection = mix(reflection, 0.5 * (frame.skyZenith.rgb + frame.skyHorizon.rgb), blur);

    if (under)
    {
        // From below: the sky through the surface (no refraction yet), and the water itself mirrored on the underside.
        // Snell's window: the sky through the surface along the ray refracted out, and past the critical angle the
        // underside mirrors the water itself.
        vec3 exit = refract(-V, N, WATER_IOR);
        reflection = fogGlow(fog.sea.rgb, fog.camera.y - worldPos.y, FOG_FAR);
        bool window = dot(exit, exit) > 0.0;
        body = window ? sky(normalize(vec3(exit.x, max(exit.y, 0.02), exit.z))) : reflection;
        F = window ? F : 1.0;
    }
    else if (fogEnabled())
        body = waterBodyFog(worldPos, N, V, dist, thickness, through);
    else
        body = waterBodyDepth(worldPos, N, V, depth, thickness);

    vec3 colour = mix(body, reflection, F);
    through *= 1.0 - F;

    // Sun specular.
    colour += vec3(1.0, 0.95, 0.85) * intensity * ggxSpecular(N, V, L) * F * (1.0 - foam);

    // Foam: rough diffuse white lit by sun + sky ambient, replaces water colour (before tonemap/fog).
    vec3 ambient = 0.5 * (frame.skyZenith.rgb + frame.skyHorizon.rgb);
    vec3 foamColour = FOAM_ALBEDO * (max(dot(N, L), 0.0) * intensity + ambient);
    colour = mix(colour, foamColour, foam);
    through *= 1.0 - foam;

    // Under water: the fog between the camera and the surface, stopped by the hard limit when it is thick.
    if (under)
    {
        vec3 inscatter = vec3(0.0);
        float t = 1.0;
        waterFogMarch(frame.cameraPos.xyz, -V, dist, fog.camera.y, max(thickness, 0.0), fogJitter(gl_FragCoord.xy), inscatter, t);
        colour = colour * t + inscatter;
    }

    // Tonemap + gamma.
    colour = pow(acesFit(colour), vec3(1.0 / 2.2));

    // Fog in display space so the far surface equals the clear colour (skyHorizon.rgb as-is).
    float f = 1.0 - exp(-dist * frame.skyHorizon.w);
    return mix(colour, frame.skyHorizon.rgb, f);
}

#endif
