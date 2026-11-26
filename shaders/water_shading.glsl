// Water shading for the ocean surface (ocean.frag): the body by depth, sky reflection, sun, foam lace, tonemap and fog.
// Works in the surface point's own frame (y = the sea sphere's up there): the caller passes the vector to the camera and
// the sun's direction in it. Needs a uniform block named frame with cameraPos (w = time), sunDir (w = intensity),
// skyZenith (w = unresolved slope variance), skyHorizon (w = fog density), waterDeep, waterScatter and
// cascadeAnchorX/Z (w = camera in the sea frame mod LACE_PERIOD / LACE_SCALE). Fragment stage only (fwidth).
#ifndef WATER_SHADING_GLSL
#define WATER_SHADING_GLSL

#ifndef WATER_NOISE_GLSL
#define WATER_NOISE_GLSL
const float LACE_WRAP = 600.0;  // seconds: foam texture clocks wrap so noise coordinates stay small
const float NOISE_CELLS = 128.0; // valueNoise repeats every this many cells (lace: LACE_PERIOD)

float hash12(vec2 p)
{
    vec3 p3 = fract(vec3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}

// Value noise on a lattice wrapped every NOISE_CELLS, so a field built from it can have a period to wrap positions at.
float valueNoise(vec2 p)
{
    vec2 i = mod(floor(p), NOISE_CELLS);
    vec2 j = mod(i + 1.0, NOISE_CELLS);
    vec2 f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash12(i), hash12(vec2(j.x, i.y)), u.x), mix(hash12(vec2(i.x, j.y)), hash12(j), u.x), u.y);
}
#endif

const float PI = 3.14159265359;
const float ROUGHNESS = 0.08;
const float ROUGH_MIRROR = 0.5; // a sea whose waves a pixel can't resolve reflects at most this much of the sky: toward the horizon their slopes show their faces, not the mirror
const vec3 FOAM_ALBEDO = vec3(0.85);
const vec3 SAND_ALBEDO = vec3(0.62, 0.56, 0.38);         // terrain.frag palette: sand
const vec3 SEABED_ABSORPTION = vec3(0.45, 0.07, 0.04);  // sea water, per metre: red goes first
const vec3 SHALLOW_SCATTER = vec3(0.04, 0.24, 0.22);     // turquoise in-scatter that replaces the seabed with depth
const float WET_SAND = 0.7;                              // sand under water is darker than the dry beach

// Sky gradient plus sun glow (disk handled by specular on water).
vec3 sky(vec3 dir, vec3 L)
{
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
float ggxSpecular(vec3 N, vec3 V, vec3 L, float roughness)
{
    vec3 H = normalize(V + L);
    float NdotH = max(dot(N, H), 0.0);
    float NdotL = max(dot(N, L), 0.0);
    float NdotV = max(dot(N, V), 1e-4);
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

const float LACE_SCALE = 1.4;      // lace cells per metre
const float LACE_PERIOD = 2.0 * NOISE_CELLS; // lace cells: lace() repeats over this (OceanRenderer.LacePeriod = this / LACE_SCALE m)
const float LACE_CALM = 0.35;      // churn rate of foam left lying on the water ...
const float LACE_ROLLER = 1.6;     // ... and of fresh whitewater in a breaking crest

// Ridged noise, high along a network of veins between cells: the shape of foam lace on broken water. Time t churns it:
// the domain warp and the fine bubbles drift, so the veins wriggle, break and re-form in place instead of sliding.
// Periodic in p over LACE_PERIOD: the warp's lattice is at half scale and the finer octaves' grids are integer matrices.
float lace(vec2 p, float t)
{
    // Each octave on its own rotated grid (45 and 108 degrees, x2.83 and x6.32), so the veins don't line up with the
    // noise lattice into squares.
    const mat2 turnB = mat2(2.0, 2.0, -2.0, 2.0);
    const mat2 turnC = mat2(-2.0, 6.0, -6.0, -2.0);
    vec2 drift = vec2(0.21, -0.17) * t;
    vec2 w = p + 0.5 * vec2(valueNoise(p * 0.5 + 5.2 + drift), valueNoise(p * 0.5 + 9.7 - drift.yx));
    float a = 1.0 - abs(2.0 * valueNoise(w + 0.3 * drift.yx) - 1.0);
    float b = 1.0 - abs(2.0 * valueNoise(turnB * w + 13.1 + 1.7 * drift) - 1.0);
    float c = 1.0 - abs(2.0 * valueNoise(turnC * w + 41.7 + vec2(0.9, 0.6) * t) - 1.0);
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
    vec2 sea = xz + vec2(frame.cascadeAnchorX.w, frame.cascadeAnchorZ.w);
    return laceVeins(lace(sea * LACE_SCALE, t), footprint, coverage);
}

// The water body by depth: deep tint, scatter through crests (height = the surface above sea level), and the sand seen
// through the shallows.
vec3 waterBodyDepth(float height, vec3 N, vec3 V, vec3 L, float depth, float thickness)
{
    float intensity = frame.sunDir.w;

    // Body colour: deep tint + subsurface scatter through wave crests + wrap diffuse.
    float sss = pow(max(dot(V, -L), 0.0), 4.0) * clamp((height + 1.5) * 0.25, 0.0, 1.0);
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

// Display colour of water whose surface stands height above sea level, with normal N, seen from toCam away and lit
// from L (all in the surface point's frame): body colour (depth = water depth under it, thickness = water above the
// ground there), sky reflection, sun, foam over it, then tonemap and fog. unresolved (0..1) is how much of the waves'
// slope the pixel can't see: their glint widens into the sea's glitter path by the slope variance (frame.skyZenith.w).
vec3 shadeWater(float height, vec3 toCam, vec3 L, vec3 N, float depth, float thickness, float foam, float unresolved)
{
    float dist = length(toCam);
    vec3 V = toCam / max(dist, 1e-4);
    float intensity = frame.sunDir.w;
    float F = fresnelSchlick(max(dot(N, V), 0.0));
    F = mix(F, min(F, ROUGH_MIRROR), unresolved);

    // Sky reflection, kept above the horizon.
    vec3 R = reflect(-V, N);
    R.y = max(R.y, 0.01);
    vec3 reflection = sky(normalize(R), L);
    vec3 colour = mix(waterBodyDepth(height, N, V, L, depth, thickness), reflection, F);

    // Sun specular.
    float roughness = sqrt(ROUGHNESS * ROUGHNESS + unresolved * frame.skyZenith.w);
    colour += vec3(1.0, 0.95, 0.85) * intensity * ggxSpecular(N, V, L, roughness) * F * (1.0 - foam);

    // Foam: rough diffuse white lit by sun + sky ambient, replaces water colour (before tonemap/fog).
    vec3 ambient = 0.5 * (frame.skyZenith.rgb + frame.skyHorizon.rgb);
    vec3 foamColour = FOAM_ALBEDO * (max(dot(N, L), 0.0) * intensity + ambient);
    colour = mix(colour, foamColour, foam);

    // Tonemap + gamma.
    colour = pow(acesFit(colour), vec3(1.0 / 2.2));

    // Fog in display space so the far surface equals the clear colour (skyHorizon.rgb as-is).
    float f = 1.0 - exp(-dist * frame.skyHorizon.w);
    return mix(colour, frame.skyHorizon.rgb, f);
}

#endif
