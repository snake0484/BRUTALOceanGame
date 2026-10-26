// Rain on water: drop impacts, the rings they spread and, where rings are too fine to resolve, the flash of their crowns.
// Pure functions of rest position and time, world-anchored, shared by ocean.frag, voxel_water.frag and the rain shaders.
// The impact grid is shared with the splash crowns (rain_splash.comp): every crown sits at the centre of its ring.
#ifndef RAIN_RIPPLES_GLSL
#define RAIN_RIPPLES_GLSL

// pcg4d (Jarzynski & Olano 2020): integer hash, stable at any world coordinate.
uvec4 rainPcg4(uvec4 v)
{
    v = v * 1664525u + 1013904223u;
    v.x += v.y * v.w; v.y += v.z * v.x; v.z += v.x * v.y; v.w += v.y * v.z;
    v ^= v >> 16u;
    v.x += v.y * v.w; v.y += v.z * v.x; v.z += v.x * v.y; v.w += v.y * v.z;
    return v;
}

// Four uniforms in [0, 1).
vec4 rainRandom4(uvec3 key)
{
    return vec4(rainPcg4(uvec4(key, 0x9e3779b9u)) >> 8u) * (1.0 / 16777216.0);
}

// Impact grid: one drop per cell per cycle (the cycle's length varies by cell), kept with probability `chance`.
const float IMPACT_CELL = 0.5;    // m
const float IMPACT_PERIOD = 0.68; // s, mean cycle; the shortest (0.75 of it) outlasts the ring
const uint IMPACT_SEED = 0x51u;

// Splash crowns are drawn (rain_splash.comp) out to SPLASH_NEAR from the camera, thinning out by SPLASH_FAR (m, level).
const float SPLASH_NEAR = 10.0;
const float SPLASH_FAR = 16.0;

bool rainEvent(ivec2 cell, float cellSize, uint seed, float period, float time, float chance, out vec2 centre, out float age, out vec4 e)
{
    vec4 h = rainRandom4(uvec3(uvec2(cell), seed));
    float p = period * (0.75 + 0.5 * h.x);
    float cycle = time / p + h.y;
    float k = floor(cycle);
    age = (cycle - k) * p;
    e = rainRandom4(uvec3(uvec2(cell), seed ^ (uint(k) * 2654435761u)));
    centre = (vec2(cell) + e.xy) * cellSize;
    return e.z < chance;
}

// A ring layer (cycles at least `life` long, 0.75 x `period`, so no ring is cut short). Rings spread at `speed` for `life` seconds as a packet of capillary crests (wavelength, gaussian
// width), fading as they go. speed × life + 2 × width stays under half a cell, so a point only sees its 2 × 2 nearest
// cells. Returns the height slope (d/dx, d/dz) and, at a fresh impact, a white dot.
struct RippleLayer
{
    float cell;
    float period;
    uint seed;
    float speed;
    float life;
    float wavelength;
    float width;
    float slope; // peak slope of a fresh ring: amplitude × wavenumber
};

const RippleLayer RIPPLE_LAYERS[3] = RippleLayer[](
    RippleLayer(IMPACT_CELL, IMPACT_PERIOD, IMPACT_SEED, 0.36, 0.5, 0.06, 0.03, 0.55), // one under every crown
    RippleLayer(0.26, 0.54, 0x52u, 0.24, 0.4, 0.03, 0.012, 0.45),                   // small drops
    RippleLayer(0.9, 0.83, 0x53u, 0.5, 0.62, 0.1, 0.045, 0.5)                        // the largest drops' rings, resolved furthest
);

const float RIPPLE_DOT = 0.008;     // m: radius of the white dot at a fresh impact ...
const float RIPPLE_DOT_TIME = 0.04; // s: ... and how long it lasts
const float RIPPLE_DENSITY = 1.4;   // impacts kept per cell at rain 1, before the cap at every cycle

vec3 rippleLayer(RippleLayer L, vec2 xz, float time, float chance, float footprint)
{
    // A ring resolves while a pixel is under a third of its wavelength; past half it would alias, so it is gone.
    float visible = 1.0 - smoothstep(0.3 * L.wavelength, 0.5 * L.wavelength, footprint);
    if (visible <= 0.0)
        return vec3(0.0);

    float k = 6.2831853 / L.wavelength;
    float w2 = L.width * L.width;
    ivec2 base = ivec2(floor(xz / L.cell - 0.5));
    vec3 sum = vec3(0.0);

    for (int j = 0; j <= 1; j++)
    {
        for (int i = 0; i <= 1; i++)
        {
            vec2 centre;
            float age;
            vec4 e;
            if (!rainEvent(base + ivec2(i, j), L.cell, L.seed, L.period, time, chance, centre, age, e) || age >= L.life)
                continue;

            vec2 d = xz - centre;
            float dist = length(d);
            float s = dist - L.speed * age;
            float envelope = exp(-s * s / w2);
            float fade = 1.0 - age / L.life;
            float amplitude = L.slope / k * fade * fade * (0.6 + 0.4 * e.w);

            // h(s) = A cos(k s) exp(-s² / w²): two or three crests travelling out.
            float dh = amplitude * envelope * (-k * sin(k * s) - 2.0 * s / w2 * cos(k * s));
            sum.xy += dh * d / max(dist, 1e-4);
            sum.z = max(sum.z, 0.6 * (1.0 - smoothstep(0.5 * RIPPLE_DOT, RIPPLE_DOT, dist)) * (1.0 - smoothstep(0.0, RIPPLE_DOT_TIME, age)));
        }
    }

    return sum * visible;
}

// Crowns too small to draw. Each flashes the one pixel its centre falls in, as bright as the share of that pixel's
// footprint on the water a crown covers (FLASH_AREA), so a flash keeps its real size at any distance: a spark at mid
// range, fading into the rain's roughness far out. (Spots a fixed number of pixels across were metres wide far out,
// and dashes at grazing views, where a pixel's footprint stretches along the view.) World-anchored cells about a pixel
// in size, at the two power-of-two sizes around it blended by the footprint, so the flashes stay put as the view moves.
const float FIZZ_PERIOD = 0.4; // s
const float FIZZ_FLASH = 0.06; // s a flash lasts
const float FIZZ_RATE = 80.0;  // flashes per m² per second in a downpour
const float FLASH_AREA = 0.002; // m²: a crown about 5 cm across

float rainFizzAt(vec2 xz, mat2 toPixels, float pixelArea, float time, float cell, float rate)
{
    vec2 centre;
    float age;
    vec4 e;
    uint seed = 0x60u + floatBitsToUint(cell);
    if (!rainEvent(ivec2(floor(xz / cell)), cell, seed, FIZZ_PERIOD, time, rate * cell * cell * FIZZ_PERIOD, centre, age, e))
        return 0.0;

    vec2 pixels = toPixels * (xz - centre);
    if (max(abs(pixels.x), abs(pixels.y)) > 0.5)
        return 0.0;

    return min(FLASH_AREA / pixelArea, 1.0) * (1.0 - smoothstep(0.0, FIZZ_FLASH, age));
}

// dx, dy: how far the rest position moves per pixel across and down the screen (dFdx, dFdy).
float rainFizz(vec2 xz, vec2 dx, vec2 dy, float time, float footprint, float rate)
{
    mat2 toWorld = mat2(dx, dy);
    float pixelArea = abs(determinant(toWorld));
    if (pixelArea < 1e-10)
        return 0.0;

    mat2 toPixels = inverse(toWorld);
    float lod = log2(max(footprint, 1e-3));
    float l0 = floor(lod);
    return mix(rainFizzAt(xz, toPixels, pixelArea, time, exp2(l0), rate), rainFizzAt(xz, toPixels, pixelArea, time, exp2(l0 + 1.0), rate), lod - l0);
}

// Rain on a water surface at rest position xz, which moves by dx, dy per pixel: (slope d/dx, slope d/dz, white) for
// rain 0..1. `crowns` is how far the drawn splash crowns have faded out here (0 near the camera, 1 past them): the
// flashes stand in for them beyond.
vec3 rainOnWater(vec2 xz, vec2 dx, vec2 dy, float time, float rain, float crowns)
{
    float footprint = length(abs(dx) + abs(dy));
    vec3 sum = vec3(0.0);
    float chance = min(rain * RIPPLE_DENSITY, 1.0);
    for (int l = 0; l < 3; l++)
        sum += rippleLayer(RIPPLE_LAYERS[l], xz, time, chance, footprint);

    sum.z = max(min(sum.z, 1.0), crowns * rainFizz(xz, dx, dy, time, footprint, FIZZ_RATE * rain));
    return sum;
}

#endif
