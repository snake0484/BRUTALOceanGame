// Where rain and wind can't reach (RainCover), per 2 m column around the camera. Rain below the highest solid surface
// is under a roof, an overhang or in a cave: no drops, no crowns, no rings. The wind sea is limited by its fetch, the
// open water upwind (an effective fetch over the wind's +-45 degrees, mapped over the whole bathymetry grid), and dies
// out with distance under cover. Define RAIN_COVER_SET and RAIN_COVER_BINDING before including.
#ifndef RAIN_COVER_GLSL
#define RAIN_COVER_GLSL

struct CoverColumn
{
    float top;      // highest surface (m), NO_TOP where unknown
    float exposure; // 0 deep under cover .. 1 open to the sky (RainCover.ShelterDistance)
    float fetch;    // open water upwind (m), the fetch map's at open columns; FETCH_REACH and over is open sea
};

const int FETCH_SIDE = 128; // RainCover.FetchSide

layout(std430, set = RAIN_COVER_SET, binding = RAIN_COVER_BINDING) readonly buffer RainCover {
    vec4 rect;             // xy = world corner of the window, z = cell size (m), w = cells per side
    vec4 fetchRect;        // fetch map over the bathymetry grid: xy = world corner, z = cell size (m), w = cells per side (0 = none yet)
    float fetchMap[FETCH_SIDE * FETCH_SIDE];
    CoverColumn columns[];
} rainCover;

const float NO_TOP = -1.0e6;
const float FETCH_REACH = 400.0; // RainCover.FetchReach

// The fetch map at xz, bilinear between cell centres and clamped to the outer ones (RainCover.FetchAt); open sea
// outside the grid.
float gridFetch(vec2 xz)
{
    int n = int(rainCover.fetchRect.w);
    vec2 f = (xz - rainCover.fetchRect.xy) / rainCover.fetchRect.z;
    if (n <= 0 || any(lessThan(f, vec2(0.0))) || any(greaterThanEqual(f, vec2(float(n)))))
        return FETCH_REACH;

    f = clamp(f - 0.5, vec2(0.0), vec2(float(n - 1)));
    ivec2 c = min(ivec2(f), ivec2(n - 2));
    vec2 t = f - vec2(c);
    int i = c.y * n + c.x;
    return mix(mix(rainCover.fetchMap[i], rainCover.fetchMap[i + 1], t.x), mix(rainCover.fetchMap[i + n], rainCover.fetchMap[i + n + 1], t.x), t.y);
}

float rainCoverTop(vec2 xz)
{
    int n = int(rainCover.rect.w);
    ivec2 c = ivec2(floor((xz - rainCover.rect.xy) / rainCover.rect.z));
    if (any(lessThan(c, ivec2(0))) || any(greaterThanEqual(c, ivec2(n))))
        return NO_TOP;
    return rainCover.columns[c.y * n + c.x].top;
}

// 1 where rain reaches a surface at p, 0 under cover. A column's top is its highest surface, which on water is the
// seabed or the beach under it, so open water is always reached.
float rainReaches(vec3 p)
{
    return 1.0 - smoothstep(-0.05, 0.05, rainCoverTop(p.xz) - p.y);
}

vec2 windColumn(int i)
{
    return vec2(rainCover.columns[i].exposure, rainCover.columns[i].fetch);
}

// The wind on water at xz, bilinear between column centres: x = exposure (0 deep in a sea cave or under a roof, 1 open
// to the sky), y = fetch (m). Outside the window, open to the sky with the fetch map's fetch, which the window's open
// columns carry too, so near and far water take the same wind sea.
vec2 windCover(vec2 xz)
{
    int n = int(rainCover.rect.w);
    vec2 f = (xz - rainCover.rect.xy) / rainCover.rect.z - 0.5;
    ivec2 c = ivec2(floor(f));
    if (any(lessThan(c, ivec2(0))) || any(greaterThanEqual(c, ivec2(n - 1))))
        return vec2(1.0, gridFetch(xz));

    vec2 t = f - vec2(c);
    int i = c.y * n + c.x;
    vec2 a = mix(windColumn(i), windColumn(i + 1), t.x);
    vec2 b = mix(windColumn(i + n), windColumn(i + n + 1), t.x);
    return mix(a, b, t.y);
}

#endif
