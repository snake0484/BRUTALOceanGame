// Water shading shared by the ocean surface (ocean.frag) and breaker lips (ocean_lip.frag), so a lip is coloured and lit
// exactly like the water it grows out of: same seabed body, scatter, reflection, sun, foam, tonemap and fog.
// Include after ocean_common.glsl.

const float PI = 3.14159265359;
const float ROUGHNESS = 0.08;
const vec3 FOAM_ALBEDO = vec3(0.85);
const vec3 SAND_ALBEDO = vec3(0.62, 0.56, 0.38);         // terrain.frag palette: sand
const vec3 SEABED_ABSORPTION = vec3(0.45, 0.07, 0.04);  // sea water, per metre: red goes first
const vec3 SHALLOW_SCATTER = vec3(0.04, 0.24, 0.22);     // turquoise in-scatter that replaces the seabed with depth
const float WET_SAND = 0.7;                              // sand under water is darker than the dry beach
const float RAIN_ROUGHNESS = 0.3;                        // a sea roughened by rain: rings finer than a pixel ...
const float RAIN_SKY_BLUR = 0.35;                        // ... scatter the sky's reflection toward its average

// 0..1: how much rain roughens this pixel's surface (ocean.frag sets it; the lips leave it 0).
float gRainRough = 0.0;

// Full bathymetry sample: r = depth (m), g = sim level offset, b = sea-zone flag, a = flow whitewater.
vec4 waterMap(vec2 xz)
{
    vec2 uv = (xz - frame.depthRect.xy) / frame.depthRect.z;
    if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0))))
        return vec4(frame.depthRect.w, 0.0, 0.0, 0.0);
    return depthMapAt(uv);
}

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

// FFT cascade partials (hx, hz, λDxx, λDzz) and λDxz at rest position restXZ, faded where the pixel footprint cannot
// resolve a cascade, and per cascade over the beach and to the wind sea there (shoreDamp, OceanPoint.shore);
// persistent foam from the same cascades, faded alike.
vec4 cascadePartials(vec2 restXZ, float footprint, vec3 shoreDamp, out float pxz, out float persistent)
{
    vec4 p = vec4(0.0);
    pxz = 0.0;
    persistent = 0.0;
    int cascades = min(int(frame.cascadeL.w), 3);
    for (int i = 0; i < cascades; ++i)
    {
        float Li = frame.cascadeL[i];
        float wf = clamp(1.0 - (footprint / (Li / CASCADE_N) - 1.0) / 3.0, 0.0, 1.0);
        if (wf <= 0.0)
            continue;
        vec3 uv = vec3(restXZ / Li, float(i));
        float w = wf * shoreDamp[i];
        p += w * texture(derivMap, uv);
        pxz += w * texture(dispMap, uv).w;
        persistent += w * texture(foamMap, uv).r;
    }

    return p;
}

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

// Flow mapping (ocean.frag, ocean_lip.frag): the water's forward speed relative to the rest grid over a breaking wave
// (breakerFace's flow, FLOW_*) slides whatever is drawn on it, the lace and the ripple detail, along dir: two copies
// on one global clock half a period (FLOW_PERIOD) apart, each displaced by speed x its phase and fading out as it
// resets, so the water visibly streams at its speed. The clock is global and only the displacement scales with the
// speed: a clock that ran at the speed (time x speed / cycle) wrapped a different number of times per metre wherever the
// speed varied, and after half a minute crushed whole cycles of texture into bands centimetres wide. With the global
// clock the stretch along a speed gradient is at most half a period x the gradient, whatever the time. The same clock
// and speed on the surface and on the tongue, so the back, the crest and the lip stream as one. xy = the two phases
// (−0.5 .. 0.5 of a period), z = the weight of the first.
vec3 flowPhases()
{
    float t = fract(mod(frame.cameraPos.w, LACE_WRAP) / FLOW_PERIOD);
    vec2 phase = vec2(t, fract(t + 0.5)) - 0.5;
    return vec3(phase, 1.0 - abs(2.0 * t - 1.0));
}

const float LACE_MEAN = 0.47; // mean of lace() over the plane (sampled), the pivot of the flow blend below

// The lace field under the foam at xz (callers pass the water's rest position, so it rides the orbital motion),
// churning in place at the churn rate (fresh whitewater, churn 1, faster than foam left lying). Where the water flows
// over the rest grid (speed > 0) it is the flow map's two copies blended by their weight. The blend is of the noise,
// not of the lace thresholded from it, and it is rescaled about the mean so the field keeps its roughness (a plain mix
// of two independent fields is 0.7 x as rough half-way through the period): one set of veins at every phase, morphing
// from one copy's to the other's, instead of two thresholded sets fading through each other twice a second.
float laceField(vec2 xz, vec2 dir, float speed, float churn)
{
    float t = mod(frame.cameraPos.w, LACE_WRAP) * mix(LACE_CALM, LACE_ROLLER, churn);
    if (speed < 0.01)
        return lace(xz * LACE_SCALE, t);

    vec3 ph = flowPhases();
    vec2 slide = dir * (speed * FLOW_PERIOD);
    float l0 = lace((xz - slide * ph.x) * LACE_SCALE, t);
    float l1 = lace((xz - slide * ph.y) * LACE_SCALE, t);
    float w = ph.z;
    return LACE_MEAN + (mix(l1, l0, w) - LACE_MEAN) * inversesqrt(w * w + (1.0 - w) * (1.0 - w));
}

// Surf foam as lace: coverage 0..1 sets how much of the lace field l is white, so a little foam is thin veins with the
// water showing through and a fresh roller is a lumpy mass, thick on the lace's ridges and thin between, never flat
// white. It evens out to its mean where a pixel can't resolve it: footprint is the pixel's size in lace cells (foamLace
// measures it; a caller in non-uniform control flow, where derivatives are undefined, passes its own).
float laceVeins(float l, float footprint, float coverage)
{
    float edge = 1.0 - 0.9 * coverage;
    float veins = smoothstep(edge - 0.08, edge + 0.08, l) * (0.55 + 0.45 * coverage);
    veins *= mix(1.0, 0.6 + 0.4 * smoothstep(edge, edge + 0.45, l), coverage);
    return mix(veins, 0.4 * coverage, smoothstep(0.3, 1.0, footprint));
}

float foamLaceAt(vec2 xz, float footprint, float coverage, float churn)
{
    if (coverage <= 0.01)
        return 0.0;
    return laceVeins(laceField(xz, vec2(1.0, 0.0), 0.0, churn), footprint, coverage);
}

float foamLace(vec2 xz, float coverage, float churn)
{
    return foamLaceAt(xz, length(fwidth(xz * LACE_SCALE)), coverage, churn);
}

float flowingLaceAt(vec2 xz, float footprint, vec2 dir, float speed, float coverage, float churn)
{
    if (coverage <= 0.01)
        return 0.0;
    return laceVeins(laceField(xz, dir, speed, churn), footprint, coverage);
}

float flowingLace(vec2 xz, vec2 dir, float speed, float coverage, float churn)
{
    return flowingLaceAt(xz, length(fwidth(xz * LACE_SCALE)), dir, speed, coverage, churn);
}

vec4 flowingPartials(vec2 restXZ, vec2 dir, float speed, float footprint, vec3 shoreDamp, out float pxz, out float persistent)
{
    if (speed < 0.01)
        return cascadePartials(restXZ, footprint, shoreDamp, pxz, persistent);

    vec3 ph = flowPhases();
    vec2 slide = dir * (speed * FLOW_PERIOD);
    float pxz0, pxz1, per0, per1;
    vec4 p0 = cascadePartials(restXZ - slide * ph.x, footprint, shoreDamp, pxz0, per0);
    vec4 p1 = cascadePartials(restXZ - slide * ph.y, footprint, shoreDamp, pxz1, per1);
    pxz = mix(pxz1, pxz0, ph.z);
    persistent = mix(per1, per0, ph.z);
    return mix(p1, p0, ph.z);
}

// Display colour of water at worldPos with normal N: body colour (depth = water depth under it, thickness = water above
// the ground there, zone = sea-zone weight), sky reflection, sun, foam over it, then tonemap and fog.
vec3 shadeWater(vec3 worldPos, vec3 N, float depth, float thickness, float zone, float foam)
{
    vec3 toCam = frame.cameraPos.xyz - worldPos;
    float dist = length(toCam);
    vec3 V = toCam / max(dist, 1e-4);
    vec3 L = frame.sunDir.xyz;
    float intensity = frame.sunDir.w;

    float NdotV = max(dot(N, V), 0.0);
    float F = fresnelSchlick(NdotV);

    // Reflection, kept above the horizon.
    vec3 R = reflect(-V, N);
    R.y = max(R.y, 0.01);
    vec3 reflection = sky(normalize(R));
    reflection = mix(reflection, 0.5 * (frame.skyZenith.rgb + frame.skyHorizon.rgb), RAIN_SKY_BLUR * gRainRough);

    // Body colour: deep tint + subsurface scatter through wave crests + wrap diffuse.
    float sss = pow(max(dot(V, -L), 0.0), 4.0) * clamp((worldPos.y + 1.5) * 0.25, 0.0, 1.0);
    float wrap = max((dot(N, L) + 0.5) / 1.5, 0.0);
    // Pits filled from the sea are stirred-up water, not deep ocean: cap the depth tint and add sediment turbidity.
    float tintDepth = mix(depth, min(depth, 2.0), zone);
    vec3 deepColour = mix(vec3(0.03, 0.20, 0.18), frame.waterDeep.rgb, smoothstep(0.0, 10.0, tintDepth));
    deepColour = mix(deepColour, vec3(0.10, 0.22, 0.19), 0.45 * zone);
    vec3 body = deepColour * (0.2 + 0.3 * wrap * intensity)
              + frame.waterScatter.rgb * sss * intensity;

    // Shallows: the body is the sand seen through the water column, lit and absorbed like terrain.frag, so a long flat
    // beach shelf blends into the beach instead of reading as a dark band.
    // Light goes down to the sand and back up (twice the column), and the water scatters turquoise into the view as it
    // deepens, so shallows read as clear tinted water rather than bare white sand.
    float column = max(thickness, 0.0);
    vec3 seabed = WET_SAND * SAND_ALBEDO * (0.5 * max(L.y, 0.0) * intensity + 0.5 * frame.skyZenith.rgb) * exp(-2.0 * SEABED_ABSORPTION * column);
    vec3 sand = mix(seabed, SHALLOW_SCATTER * (0.3 + 0.4 * wrap * intensity), 1.0 - exp(-0.8 * column));
    body = mix(sand, body, smoothstep(0.0, 2.5, thickness) * (1.0 - zone));

    vec3 colour = mix(body, reflection, F);

    // Sun specular.
    colour += vec3(1.0, 0.95, 0.85) * intensity * ggxSpecular(N, V, L) * F * (1.0 - foam);

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
