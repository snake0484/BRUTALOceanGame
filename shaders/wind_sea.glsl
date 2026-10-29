// The wind sea on voxel water (docs/voxel-water.md "Wind on voxel water"): ten octave bands of wave slope (WindSeaBands,
// wind_sea_h0.comp), 16 m down to 1.6 cm, each of unit mean square slope and spread about the wind. Each water vertex
// weights them by the spectrum its own wind and fetch raise (windSeaBands), so the waves on a lake peak at the wavelength
// its fetch grows and run with the wind, as the sea's do, while a sheltered pond carries only ripples.
#ifndef WIND_SEA_GLSL
#define WIND_SEA_GLSL

const int WIND_SEA_BANDS = 10;
const float LAYER_PATCH = 128.0;   // m, layer 0's patch (bands 0 and 1); layer i's is LAYER_PATCH / 4^i
const float BAND_LONGEST = 16.0;   // m, band 0's longest wave; band b holds BAND_LONGEST / 2^b .. half that
const float WIND_SEA_N = 128.0;    // WindSeaBands.Size
const float SEA_GRAVITY = 9.81;
const float SEA_GAMMA = 3.3;       // JONSWAP peak enhancement
const float OPEN_FETCH = 1600.0;   // m: the fetch taken where the march found no shore (FETCH_REACH and over)
const float CAPILLARY_END = 1250.0; // rad/m, 5 mm: the shortest ripples, past the finest band roughness only
const int BAND_STEPS = 4;          // quadrature steps over each octave

// The slope variance per unit ln k of a fetch-limited JONSWAP sea at wavenumber k (deep water, k = w^2 / g): S(w) k^2
// per unit ln w is alpha exp(-1.25 (wp / w)^4) gamma^r, and ln k runs twice as fast as ln w. Flat above the peak (the
// saturated range), nothing much below it.
float slopeDensity(float k, float alpha, float wp)
{
    float w = sqrt(SEA_GRAVITY * k);
    float sigma = w <= wp ? 0.07 : 0.09;
    float d = (w - wp) / (sigma * wp);
    float ratio = wp / w;
    float ratio2 = ratio * ratio;
    return 0.5 * alpha * exp(-1.25 * ratio2 * ratio2) * pow(SEA_GAMMA, exp(-0.5 * d * d));
}

// Per band, the slope variance the wind U (m/s, the share of the 10 m wind the water feels) raises over the fetch (m),
// and the variance past the finest band (tail): JONSWAP's alpha and wp from U and the fetch, grown no further than a
// fully developed sea (Pierson-Moskowitz). The total is held to Cox and Munk's mean square slope at U, 0.003 + 0.00512
// U: JONSWAP's alpha, a fit to the peak, overstates the short waves of a short fetch. Under about 1 m/s none: no
// capillaries, glass.
void windSeaBands(float U, float fetch, out vec4 v0, out vec4 v1, out vec2 v2, out float tail)
{
    v0 = vec4(0.0);
    v1 = vec4(0.0);
    v2 = vec2(0.0);
    tail = 0.0;
    float calm = smoothstep(0.5, 2.0, U);
    if (calm <= 0.0)
        return;

    float F = fetch + (OPEN_FETCH - FETCH_REACH) * smoothstep(0.9 * FETCH_REACH, FETCH_REACH, fetch);
    F = max(F, 1.0);
    float alpha = max(0.076 * pow(U * U / (F * SEA_GRAVITY), 0.22), 0.0081);
    float wp = max(22.0 * pow(SEA_GRAVITY * SEA_GRAVITY / (U * F), 1.0 / 3.0), 0.877 * SEA_GRAVITY / U);

    float bands[WIND_SEA_BANDS];
    float total = 0.0;
    const float STEP = 0.69314718056 / float(BAND_STEPS);
    for (int b = 0; b < WIND_SEA_BANDS; b++)
    {
        float lnLo = log(6.28318530718 * float(1 << b) / BAND_LONGEST);
        float v = 0.0;
        for (int j = 0; j < BAND_STEPS; j++)
            v += slopeDensity(exp(lnLo + (float(j) + 0.5) * STEP), alpha, wp);

        bands[b] = v * STEP;
        total += bands[b];
    }

    float kFinest = 6.28318530718 * float(1 << WIND_SEA_BANDS) / BAND_LONGEST;
    tail = 0.5 * alpha * log(CAPILLARY_END / kFinest);
    total += tail;

    float scale = calm * min((0.003 + 0.00512 * U) / max(total, 1e-6), 1.0);
    v0 = vec4(bands[0], bands[1], bands[2], bands[3]) * scale;
    v1 = vec4(bands[4], bands[5], bands[6], bands[7]) * scale;
    v2 = vec2(bands[8], bands[9]) * scale;
    tail *= scale;
}

#endif
