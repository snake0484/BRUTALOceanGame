// Shared ocean surface evaluation (ocean.vert): frame block, maps, the depth-limited swell, the shore wash, and
// oceanAt(), the displaced surface at a rest position. Must match the CPU OceanSurface (see each section).
// Camera-relative, in the sea frame (SeaFrame: a tangent frame on the sea sphere near the camera, x east, z south):
// x, z are relative to the camera and y is the height above sea level; seaDrop() bends the frame's plane to the sphere.
// Everything with a world phase (wave and carrier phases, cascade anchors, depth rects) is rebased to the camera in
// double by OceanRenderer.Prepare.

struct Wave { vec4 a; vec4 b; }; // a = (ku, kv, unused, amplitude), b = (cu, cv, omega, unused), in the wave plane's axes

const int MAX_WAVES = 512;
const int MAX_SLOTS = 4;
const int MAX_COAST = 8;

// A coast region against the camera (CoastPlacement, CoastAtlasGpu): its cell coordinates are dot(cell.xy, x0) + cell.z.
struct CoastRegion
{
    vec4 cellX; // w = the sea tile slot of its tile
    vec4 cellZ; // w = its layer of the atlas textures
    vec4 wave;  // x = the map's phase at the frame's time, y = the carrier's omega, z = its deep-water height, w = the region's fade
};

layout(set = 0, binding = 0, std140) uniform Frame {
    mat4 viewProj;
    vec4 cameraPos;      // (0, camera height above the sea frame's plane, 0), w = time, wrapped at 3000 s (OceanRenderer.TimeWrap)
    vec4 sunDir;         // xyz toward sun (sea frame axes), w = intensity
    vec4 skyZenith;      // w = slope variance of the waves a pixel can't resolve (far glint width)
    vec4 skyHorizon;     // w = fog density
    vec4 waterDeep;      // w = the surface's opacity: from altitude it fades out into the terrain's far sea (terrain.frag)
    vec4 waterScatter;
    vec4 cascadeL;       // xyz = patch size L per cascade (m), w = cascade count
    vec4 cascadeKLo;     // xyz = band low |k| per cascade, w = persistent foam strength
    vec4 cascadeAnchorX; // xyz = frac(camera x / L) per cascade (cascadeUv), w = camera x mod the lace period (foamLace)
    vec4 cascadeAnchorZ; // xyz = frac(camera z / L) per cascade, w = camera z mod the lace period
    vec4 depthRect;      // near depth map: xy = window corner relative to the camera, z = world size (0 = none), w = depth outside both maps
    vec4 depthFar;       // far depth map: xy = corner relative to the camera, z = world size (0 = none)
    vec4 depthWrap;      // xy = the near window's corner cell mod the map's size (it is stored toroidally), z = the grid spacing the camera's height calls for (m, continuous: OceanRenderer.FaceSpacing), w unused
    vec4 depthValid;     // the part of the near window that holds its depth: xy = min, zw = max, relative to the camera (empty: min > max)
    vec4 carrier;        // surf carrier (OceanSwell.GpuCarrier): x = omega, y = phase, z = set group ratio (a set every 1 / this waves), w = set group phase
    vec4 shore;          // xy = carrier travel direction, z = its breaker height (0 = no shore wash), w = open swell Hs
    vec4 planet;         // xy = camera x, z in the sea frame, z unused, w = sea radius
    vec4 bandRow;        // tile blend coordinate across the row edge = dot(xy, x0) + z (SeaSlots)
    vec4 bandCell[2];    // ... across the cell edge of the row south of it, and of the row north of it
    vec4 slotToTile[MAX_SLOTS];   // per slot: (u by x, u by z, v by x, v by z), sea frame plane to tile plane at the camera
    vec4 slotFromTile[MAX_SLOTS]; // per slot: the inverse, (x by u, x by v, z by u, z by v)
    vec4 slotSide[MAX_SLOTS];     // per slot: x = side of the row edge, y = side of its row's cell edge (-1, +1, 0 = whole), z = its row (0 south, 1 north)
    CoastRegion coast[MAX_COAST]; // the coast regions drawn (ocean_breaker.glsl)
    int waveCount;
    int slotCount;
    float debugView;     // 1 = Jacobian/foam
    int coastCount;
    Wave waves[MAX_WAVES];
    vec4 phases[MAX_WAVES]; // per wave, its phase at the camera in each slot's tile plane
} frame;

layout(set = 0, binding = 1) uniform sampler2DArray dispMap;  // (λDx, h, λDz, λDxz)
layout(set = 0, binding = 2) uniform sampler2DArray derivMap; // (hx, hz, λDxx, λDzz)
layout(set = 0, binding = 3) uniform sampler2DArray foamMap;  // r = persistent foam
layout(set = 0, binding = 4) uniform sampler2D depthMap;    // near signed depth (ShoreDepthTexture), see waterDepth
layout(set = 0, binding = 5) uniform sampler2D depthFarMap; // far signed depth

const float BREAKING_GAMMA = 0.55; // OceanSwell.DepthCapGamma
const float SHORE_CALM = 0.0; // m of water under the surface (oceanAt) at which the GPU band's chop is gone ...
const float SHORE_FULL = 1.5; // ... and from which it is at full strength
const float CASCADE_N = 256.0; // FFT resolution, fixed in C#
const float WAVE_BREAKING_GAMMA = 0.78; // the surf wave breaks at this height / depth
const float GROUP_DEPTH = 0.7;          // sets modulate the surf wave's height by 1 +- this, mean square 1
const float TWO_PI = 6.28318530718;

// Set envelope at a point whose carrier spatial phase is spatial: (1 + m cos(ratio spatial + group phase)) / sqrt(1 + m^2/2).
float groupEnvelope(float spatial)
{
    return (1.0 + GROUP_DEPTH * cos(frame.carrier.z * spatial + frame.carrier.w)) / sqrt(1.0 + 0.5 * GROUP_DEPTH * GROUP_DEPTH);
}

// Cascade i's texture coordinate at camera-relative x plus the camera's place in the tile, so it stays small anywhere
// in the world.
vec3 cascadeUv(vec2 x, int i)
{
    return vec3(x / frame.cascadeL[i] + vec2(frame.cascadeAnchorX[i], frame.cascadeAnchorZ[i]), float(i));
}

const float SURF_SPACING_FULL = 4.0; // swash at full strength up to this grid spacing (m) ...
const float SURF_SPACING_NONE = 8.0; // ... and gone by this one

// Surf foam left behind by the swash (ocean_surf_foam.comp): coverage per cell of the depth map's grid, decaying.
// Read-only except in that compute shader, which defines SURF_FOAM_WRITABLE.
#ifdef SURF_FOAM_WRITABLE
#define SURF_FOAM_ACCESS
#else
#define SURF_FOAM_ACCESS readonly
#endif

const int SURF_FOAM_RES = 512;

layout(std430, set = 0, binding = 6) SURF_FOAM_ACCESS buffer SurfFoam
{
    float values[];
} surfFoam;

// How far the sea lies below the sea frame's plane at fromOrigin (x, z from the frame's origin): exact on the sphere,
// (x² + z²) / 2R near the origin (SeaFrame.Drop).
float seaDrop(vec2 fromOrigin)
{
    float R = frame.planet.w;
    float r2 = min(dot(fromOrigin, fromOrigin), R * R);
    return r2 / (R + sqrt(R * R - r2));
}

// Radial water depth (m): sea level - ground, negative on land (PlanetShore: the generated ground plus placed fill; an
// inland pit dug below sea level stays land, so the sea never fills it). From the near map where its window holds its
// depth (half a cell in from the edge, so the filter reads only such cells), the far map round it, deep outside both.
float waterDepth(vec2 xz)
{
    float halfCell = 0.5 * frame.depthRect.z / float(SURF_FOAM_RES);
    if (all(greaterThan(xz, frame.depthValid.xy + halfCell)) && all(lessThan(xz, frame.depthValid.zw - halfCell)))
    {
        vec2 uv = (xz - frame.depthRect.xy) / frame.depthRect.z;
        return textureLod(depthMap, uv + frame.depthWrap.xy / float(SURF_FOAM_RES), 0.0).r;
    }

    if (frame.depthFar.z > 0.0)
    {
        vec2 uv = (xz - frame.depthFar.xy) / frame.depthFar.z;
        if (all(greaterThan(uv, vec2(0.0))) && all(lessThan(uv, vec2(1.0))))
            return textureLod(depthFarMap, uv, 0.0).r;
    }

    return frame.depthRect.w;
}

// Swash (docs/shore-wash.md): run-up events per shoreline point. Each bore that reaches the shoreline below a beach
// point collapses into a sheet whose edge runs up the beach ballistically (Shen and Meyer: x = u0 t - a t^2 / 2 along the
// beach, a = g tan(beta) x (1 + SWASH_FRICTION) on the way up, SWASH_BACK x g tan(beta) back down, so it drains slower
// than it rushed up) and the surface above a point is the highest of the last SWASH_EVENTS bores' sheets, so a bore
// arriving in a backwash pushes the edge out again rather than one half sine a period. Timing is the open carrier's
// phase at the shoreline point below (found along the depth gradient): along a straight shore Snell's law keeps k0 sin
// of the angle, so the arrival phase is the open wave's there. The amplitude is that bore's: the carrier's breaker height
// (frame.shore.z) for the crest that arrived (the set envelope it carried), less what the break has dissipated since it
// began (surfWidth: the distance from where it broke to the shore), so the small waves between sets barely move the edge
// and a set wave runs far up, and lee shores facing away from the carrier get none. The sheet behind an edge is the
// Shen-Meyer lens, (x_edge - x)^2 / (9 g t^2), thick just behind a fresh edge and thinning as the sheet ages, over a
// minimum film. GPU only.
const int SWASH_EVENTS = 4;          // bores remembered per point; the oldest fades out over its last period
const float SWASH_U0 = 1.2;          // the edge's speed as the bore collapses at the shoreline, x sqrt(g x the bore's energy height) (Shen and Meyer: 2, friction-free)
const float SWASH_FRICTION = 0.3;    // the up-rush decelerates at (1 + this) x g tan(beta) ...
const float SWASH_DECEL_MIN = 0.6;   // ... and at least this (m/s^2): bed friction on the thin tip, which on a gentle beach otherwise ran up for tens of seconds and the edge barely moved per wave
const float SWASH_BACK = 0.7;        // the backwash accelerates at this x the up-rush's deceleration
const float SWASH_EDGE_WIDTH = 0.5;  // m of sheet behind the edge marked as the water's edge (OceanPoint.swashEdge, ocean.frag's edge line)
const float SWASH_DISSIPATE = 100.0; // m of broken travel over which a bore's energy height falls by 1/e
const float SWASH_MAX = 2.0;         // vertical run-up at most (m)
const float SWASH_MIN_SHEET = 0.03;  // m: the film left as a sheet drains ...
const float SWASH_SHEET = 0.25;      // ... plus this x the height of the edge above the point: a film thinner than the voxel terrain's facets is cut by them
const float SWASH_LENS_MAX = 0.25;   // m: the sheet at most this thick
const float SWASH_LENS_TIME = 1.5;   // s: the lens's age is taken as at least this (its thickness goes as 1 / age^2)
const float SWASH_SEA = 0.5;         // water depth (m) over which the swash fades into the sea surface
const float SWASH_SLOPE_SPAN = 6.0;  // m either side the beach slope is taken over: the foreshore's, not one 2 m depth cell's, whose slope varies by a third cell to cell and stepped the edge
const float SWASH_FOAM_FRONT = 2.0;  // m of sheet behind an up-rushing edge that is fresh foam (ocean_surf_foam.comp) ...
const float SWASH_FOAM_U0 = 3.0;     // ... at full strength from this edge speed (m/s)

// The surf zone's width (m) for a wave of breaking height H on a beach of this slope: from where it breaks, in water
// H / WAVE_BREAKING_GAMMA deep, to the shoreline.
float surfWidth(float breakerHeight, float slope)
{
    return breakerHeight / WAVE_BREAKING_GAMMA / slope;
}

// A beach point for the swash: its depth, the beach's slope, its distance up the beach from the shoreline below it
// (negative in the water), the open carrier's spatial phase at that shoreline point, the bore's energy height there
// and the edge's decelerations and top speed.
struct SwashBeach
{
    float d;
    float slope;
    float s0;
    float spatial;
    float unbroken;
    float up;
    float back;
    float u0Max;
};

// The sheets of the last SWASH_EVENTS bores for a carrier whose phase at the shoreline point is `phase` (0 as a crest
// reaches it) and angular frequency omega: the surface above the beach point, how fresh the up-rushing foam is there
// (front) and how near the edge of the highest sheet it is (edge).
float swashRun(SwashBeach b, float phase, float omega, out float front, out float edge)
{
    float p = fract(-phase / TWO_PI); // periods since the last crest reached the shoreline
    float period = TWO_PI / omega;
    float sheet = -1e3;
    front = 0.0;
    edge = 0.0;

    for (int n = 0; n < SWASH_EVENTS; ++n)
    {
        float since = p + float(n);
        float age = since * period;
        // The set envelope travels with the crests, so the crest that reached the shore `since` periods ago carried the
        // envelope `since` wavelengths back along the travel.
        float u0 = min(SWASH_U0 * sqrt(9.81 * b.unbroken * groupEnvelope(b.spatial + TWO_PI * since)), b.u0Max);
        float tUp = u0 / b.up;
        float dt = age - tUp;
        float xs = age < tUp ? age * (u0 - 0.5 * b.up * age) : 0.5 * u0 * tUp - 0.5 * b.back * dt * dt;
        xs *= 1.0 - smoothstep(float(SWASH_EVENTS - 1), float(SWASH_EVENTS), since);
        float behind = xs - b.s0;
        float t = max(age, SWASH_LENS_TIME);
        // Past the edge the sheet dives under the sand at twice the beach's slope, so the edge falls smoothly between
        // vertices rather than on them.
        float lens = behind > 0.0 ? min(max(behind * behind / (9.0 * 9.81 * t * t), SWASH_SHEET * b.slope * behind), SWASH_LENS_MAX) : 2.0 * b.slope * behind;
        float surface = -b.d + SWASH_MIN_SHEET + lens;
        if (surface > sheet)
        {
            sheet = surface;
            edge = behind > 0.0 ? 1.0 - min(behind / SWASH_EDGE_WIDTH, 1.0) : 0.0;
        }

        if (b.d < 0.0 && age < tUp && behind >= 0.0 && behind < SWASH_FOAM_FRONT)
            front = max(front, (1.0 - behind / SWASH_FOAM_FRONT) * smoothstep(0.0, SWASH_FOAM_U0, u0));
    }

    return sheet;
}

// The swash of the coast regions over a shoreline point, each by its weight there (ocean_breaker.glsl): their carriers
// reach the shore when the breaking faces drawn from them do.
float coastSwash(SwashBeach b, vec2 shore, out float front, out float edge, out float weight);

// The swash sheet's surface above x0 (depth d: negative on land), or -1e3 where there is none. front: how fresh the
// up-rushing foam is here, 1 at the edge of a strong bore's up-rush falling to 0 SWASH_FOAM_FRONT behind it; only on the
// beach, never over the water. gSwashEdge: 1 at the edge of the sheet that is highest here, 0 SWASH_EDGE_WIDTH behind it.
// The bores arrive on the coast regions' carrier where one covers the shoreline point, and on the open carrier's phase
// there elsewhere (along a straight shore Snell's law keeps k0 sin of the angle).
float gSwashEdge = 0.0;

float swashSheet(vec2 x0, float d, out float front)
{
    front = 0.0;
    gSwashEdge = 0.0;
    if (d > SWASH_SEA || d < -SWASH_MAX || frame.shore.z <= 0.0)
        return -1e3;

    // The shoreline below x0: along the depth gradient to where the depth reaches 0; the beach slope is the gradient's.
    vec2 grad = vec2(waterDepth(x0 + vec2(SWASH_SLOPE_SPAN, 0.0)) - waterDepth(x0 - vec2(SWASH_SLOPE_SPAN, 0.0)),
                     waterDepth(x0 + vec2(0.0, SWASH_SLOPE_SPAN)) - waterDepth(x0 - vec2(0.0, SWASH_SLOPE_SPAN))) / (2.0 * SWASH_SLOPE_SPAN);
    float g2 = dot(grad, grad);
    if (g2 < 1e-6)
        return -1e3;

    SwashBeach b;
    b.d = d;
    b.slope = sqrt(g2);
    vec2 shore = x0 - d * grad / g2;
    float omega = max(frame.carrier.x, 1e-3);
    b.spatial = omega * omega / 9.81 * dot(frame.shore.xy, shore);

    // The bore's energy height at the shore, before its set envelope: the breaker height on a shore facing the carrier,
    // less the break's dissipation over the surf zone it has crossed.
    float exposure = sqrt(max(dot(frame.shore.xy, -grad / b.slope), 0.0));
    b.unbroken = frame.shore.z * exposure * exp(-surfWidth(frame.shore.z, b.slope) / SWASH_DISSIPATE);
    b.up = max(9.81 * b.slope * (1.0 + SWASH_FRICTION), SWASH_DECEL_MIN);
    b.back = SWASH_BACK * b.up;
    b.u0Max = sqrt(2.0 * b.up * SWASH_MAX / b.slope);
    b.s0 = -d / b.slope;

    float weight;
    float sheet = coastSwash(b, shore, front, gSwashEdge, weight);
    if (weight < 0.999)
    {
        float openFront;
        float openEdge;
        float open = swashRun(b, b.spatial + frame.carrier.y, omega, openFront, openEdge);
        sheet += (1.0 - weight) * open;
        front += (1.0 - weight) * openFront;
        gSwashEdge += (1.0 - weight) * openEdge;
    }

    return sheet;
}

// The surf foam buffer's index of window cell c (stored toroidally, like the near depth map).
int surfFoamIndex(ivec2 c)
{
    ivec2 t = (c + ivec2(frame.depthWrap.xy)) & (SURF_FOAM_RES - 1);
    return t.x + t.y * SURF_FOAM_RES;
}

// Surf foam left behind at xz, bilinear over the cells; 0 outside the window.
float surfFoamAt(vec2 xz)
{
    if (frame.depthRect.z <= 0.0)
        return 0.0;

    vec2 f = (xz - frame.depthRect.xy) / frame.depthRect.z * float(SURF_FOAM_RES) - 0.5;
    ivec2 i = ivec2(floor(f));
    if (i.x < 0 || i.y < 0 || i.x >= SURF_FOAM_RES - 1 || i.y >= SURF_FOAM_RES - 1)
        return 0.0;

    vec2 t = f - vec2(i);
    return mix(mix(surfFoam.values[surfFoamIndex(i)], surfFoam.values[surfFoamIndex(i + ivec2(1, 0))], t.x),
               mix(surfFoam.values[surfFoamIndex(i + ivec2(0, 1))], surfFoam.values[surfFoamIndex(i + ivec2(1, 1))], t.x), t.y);
}

struct OceanPoint
{
    vec3 position;     // displaced position (camera-relative x, z; height above sea level y)
    vec4 cpuPartials;  // (hx, hz, λDxx, λDzz)
    float cpuDxz;
    float shore;       // share of the FFT slopes drawn: 0 on the beach .. 1 on open water
    float surfFoam;    // whitewater left behind by the surf (ocean_surf_foam.comp)
    float breakFoam;   // whitewater of the breaking face (ocean_breaker.glsl)
    vec2 coast;        // x = the share of the breaking faces this grid draws, y = 1 near a coast region (ocean.frag's far band)
    float swashEdge;   // 1 at the edge of the swash sheet drawn here, 0 half a metre behind it or where the sea is the surface (ocean.frag's edge line)
};

// The analytic swell's height, displacement and their partials at a rest position.
struct Swell
{
    float height;
    float hx, hz;
    float Dx, Dz;
    float jxx, jzz, jxz;
};

float smooth01(float t)
{
    t = clamp(t, 0.0, 1.0);
    return t * t * (3.0 - 2.0 * t);
}

// The weight of each sea tile slot at x0 (SeaTiles.Locate through SeaSlots): squares summing to 1.
void slotWeights(vec2 x0, out float w[MAX_SLOTS])
{
    float row = smooth01(0.5 + 0.5 * (dot(frame.bandRow.xy, x0) + frame.bandRow.z));
    float cell[2];
    cell[0] = smooth01(0.5 + 0.5 * (dot(frame.bandCell[0].xy, x0) + frame.bandCell[0].z));
    cell[1] = smooth01(0.5 + 0.5 * (dot(frame.bandCell[1].xy, x0) + frame.bandCell[1].z));

    for (int s = 0; s < MAX_SLOTS; ++s)
    {
        w[s] = 0.0;
        if (s >= frame.slotCount)
            continue;

        vec3 side = frame.slotSide[s].xyz;
        float c = cell[int(side.z)];
        float wr = side.x == 0.0 ? 1.0 : (side.x > 0.0 ? row : 1.0 - row);
        float wc = side.y == 0.0 ? 1.0 : (side.y > 0.0 ? c : 1.0 - c);
        w[s] = sqrt(wr * wc);
    }
}

const float HAND_WIDTH = 0.1; // SeaTiles.HandWidth

float handShare(float squaredWeight)
{
    return smoothstep(0.5 - HAND_WIDTH, 0.5 + HAND_WIDTH, squaredWeight);
}

// Each sea tile slot's share of a coherent wave at x0 (SeaTiles.Hand through SeaSlots), summing to 1: all but a narrow
// band at a tile edge is one tile's.
void slotShares(vec2 x0, out float w[MAX_SLOTS])
{
    float row = handShare(smooth01(0.5 + 0.5 * (dot(frame.bandRow.xy, x0) + frame.bandRow.z)));
    float cell[2];
    cell[0] = handShare(smooth01(0.5 + 0.5 * (dot(frame.bandCell[0].xy, x0) + frame.bandCell[0].z)));
    cell[1] = handShare(smooth01(0.5 + 0.5 * (dot(frame.bandCell[1].xy, x0) + frame.bandCell[1].z)));

    for (int s = 0; s < MAX_SLOTS; ++s)
    {
        w[s] = 0.0;
        if (s >= frame.slotCount)
            continue;

        vec3 side = frame.slotSide[s].xyz;
        float c = cell[int(side.z)];
        float wr = side.x == 0.0 ? 1.0 : (side.x > 0.0 ? row : 1.0 - row);
        float wc = side.y == 0.0 ? 1.0 : (side.y > 0.0 ? c : 1.0 - c);
        w[s] = wr * wc;
    }
}

#include "ocean_breaker.glsl"

// The swell (OceanSurface.Sample) anti-aliased over [lo, hi], every component scaled by cap: each slot's tile field,
// summed in its own plane (u, v) and turned into the sea frame's.
Swell swellAt(vec2 x0, float lo, float hi, float cap)
{
    Swell o = Swell(0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0);
    int count = min(frame.waveCount, MAX_WAVES);
    float weights[MAX_SLOTS];
    slotWeights(x0, weights);

    for (int s = 0; s < frame.slotCount; ++s)
    {
        float ws = weights[s] * cap;
        if (ws <= 0.0)
            continue;

        vec4 T = frame.slotToTile[s];
        vec4 F = frame.slotFromTile[s];
        vec2 uv = vec2(dot(T.xy, x0), dot(T.zw, x0));
        float height = 0.0;
        vec2 grad = vec2(0.0);
        vec2 D = vec2(0.0);
        vec3 j = vec3(0.0); // (uu, vv, uv)

        for (int i = 0; i < count; ++i)
        {
            vec4 a = frame.waves[i].a;
            vec4 b = frame.waves[i].b;

            float w = smoothstep(lo, hi, TWO_PI / max(length(a.xy), 1e-6));
            if (w <= 0.0)
                continue;

            float A = a.w * w;
            vec2 c = b.xy * w;
            float theta = dot(a.xy, uv) + frame.phases[i][s];
            float sn = sin(theta);
            float cs = cos(theta);

            height += A * cs;
            grad -= A * a.xy * sn;
            D -= c * sn;
            j += vec3(c.x * a.x, c.y * a.y, c.x * a.y) * cs;
        }

        // Into the sea frame: positions by F, gradients by T transposed, the chop's Jacobian by F j T.
        vec2 jx = vec2(F.x * j.x + F.y * j.z, F.x * j.z + F.y * j.y);
        vec2 jz = vec2(F.z * j.x + F.w * j.z, F.z * j.z + F.w * j.y);
        o.height += ws * height;
        o.hx += ws * (T.x * grad.x + T.z * grad.y);
        o.hz += ws * (T.y * grad.x + T.w * grad.y);
        o.Dx += ws * dot(F.xy, D);
        o.Dz += ws * dot(F.zw, D);
        o.jxx += ws * (jx.x * T.x + jx.y * T.z);
        o.jzz += ws * (jz.x * T.y + jz.y * T.w);
        o.jxz += ws * 0.5 * (jx.x * T.y + jx.y * T.w + jz.x * T.x + jz.y * T.z);
    }

    return o;
}

// The ocean surface at rest position x0, for a grid of effective spacing se (anti-aliasing filter width).
OceanPoint oceanAt(vec2 x0, float se)
{
    // Depth-limited swell (OceanSurface): min(1, BREAKING_GAMMA h / Hs), so the open swell cannot run through the beach;
    // 0 on land.
    float depth = waterDepth(x0);
    float cap = frame.shore.w > 1e-4 ? clamp(BREAKING_GAMMA * depth / frame.shore.w, 0.0, 1.0) : 1.0;
    Swell sw = swellAt(x0, 2.0 * se, 4.0 * se, cap);

    // The breaking face over the coast regions: the surf carrier and its breakers, on top of the swell. Drawn only: the
    // CPU's OceanSurface does not have it.
    vec2 coast;
    Face face = coastFace(x0, se, coast.x, coast.y);
    float height = sw.height + face.height;

    // Swash is metres wide: a grid this coarse can't show it, so it fades out with the spacing (smoothly, so no seam
    // where one clipmap level hands over to the next) and far vertices skip the work.
    float near = 1.0 - smoothstep(SURF_SPACING_FULL, SURF_SPACING_NONE, se);
    float swashEdge = 0.0;

    if (near > 0.0)
    {
        // Swash: the beach holds the sheets the last bores ran up it (swashSheet), one continuous surface that carries on
        // under the sand past each edge, so the water's edge falls smoothly between vertices.
        float swashFront;
        float sheet = swashSheet(x0, depth, swashFront);
        if (sheet > -1e2)
        {
            float lift = near * (1.0 - smoothstep(0.0, SWASH_SEA, depth));
            if (sheet > height)
                swashEdge = gSwashEdge * lift;

            height = max(height, mix(height, sheet, lift));
        }
    }

    // GPU cascades: displacement only, faded out where the grid cannot resolve the band; open fetch everywhere.
    vec3 cpuBand = vec3(sw.Dx + face.shift.x, height, sw.Dz + face.shift.y);
    vec3 fft = vec3(0.0);
    int cascades = min(int(frame.cascadeL.w), 3);
    for (int i = 0; i < cascades; ++i)
    {
        float lambdaLo = TWO_PI / max(frame.cascadeKLo[i], 1e-6);
        float wv = 1.0 - smoothstep(0.25 * lambdaLo, 0.5 * lambdaLo, se);
        if (wv <= 0.0)
            continue;
        vec4 dc = textureLod(dispMap, cascadeUv(x0, i), 0.0);
        fft += wv * dc.xyz;
    }

    // The FFT detail calms toward the land over SHORE_CALM-SHORE_FULL of the water column under it: the swell's crest
    // or trough over the bed (a swash sheet's thickness), so the chop comes in on the crests and settles in the troughs
    // between them, and the thin water at the edge stays glassy. Fading it from 8 m of still depth left the whole surf
    // zone glassy; over 0.2-1.5 m, a glassy band that didn't move with the waves; capped at full strength down to half a
    // metre, chop right up to the sand.
    float column = max(depth + height, 0.0);
    float shore = smoothstep(SHORE_CALM, SHORE_FULL, column);
    vec3 disp = cpuBand + fft * shore;

    // CPU partials in contract form: dxx = 1 + λDxx, so λDxx = -jxx (likewise zz, xz).
    OceanPoint p;
    p.position = vec3(x0.x + disp.x, disp.y, x0.y + disp.z);
    p.cpuPartials = vec4(sw.hx + face.slope.x, sw.hz + face.slope.y, face.jacobian.x - sw.jxx, face.jacobian.y - sw.jzz);
    p.cpuDxz = face.jacobian.z - sw.jxz;
    p.shore = shore;
    p.surfFoam = surfFoamAt(x0);
    p.breakFoam = face.foam;
    p.coast = coast;
    p.swashEdge = swashEdge;
    return p;
}
