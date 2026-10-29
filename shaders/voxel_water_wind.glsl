// The wind on voxel water, per vertex (voxel_water.vert): how much of the wind reaches the water and its fetch, judged at
// its own level over the cover data. Include after rain_cover.glsl.
#ifndef VOXEL_WATER_WIND_GLSL
#define VOXEL_WATER_WIND_GLSL

// The wind on water at p (docs/voxel-water.md "Wind on voxel water"): a march upwind over the column tops, all judged at
// the water's own level. The cover map's exposure and fetch can't be used: they judge water at sea level, so every lake
// and pond column near the camera read as under cover and lay as glass. A top over the water's own column is a roof:
// the wind reaches under it only from the first open column upwind, dying out over SHELTER_DISTANCE
// (RainCover.ShelterDistance). Past it, every top standing over the water upwind, a bank or a cliff or the hills beyond
// the shore, shelters SHELTER_PER_HEIGHT times its height: the wind separates over high ground and recovers downwind, so
// only ground steeper than 1 in 6 as seen from the water slows it (a crater's 0.3 banks keep about half of it, a cliff's
// lee is calm). The first top at or over the water's level is the shore and ends the fetch; the march carries on past it
// for the shelter, and to the sea (a cell whose top is under sea level, or past the grid): the weather's 10 m wind is a
// sea wind, and over land it slows near the ground (rough ground, LAND_SLOW once adjusted over LAND_ADJUST), then speeds
// up again over the water's smooth surface only slowly (WATER_RECOVER). So an inland lake feels about two thirds of the
// sea's wind, a pit by the shore with the sea upwind all of it. Fine steps over the window's
// 2 m columns for the shelter (rainCoverTop), coarse ones over the 8 m cells' highest tops (cellTop) for the fetch, the
// same world-space data for every block and at any distance, so block seams and near and far water agree. Outside the
// window the shelter steps read the cells too, from COARSE_FROM on, clear of the water's own cell (whose shore would end
// every fetch). A shore found by a coarse step is placed by bisecting back to the step before, so the fetch varies
// smoothly rather than in bands at the step distances. Past the grid, or past the last step, the water counts as open
// (FETCH_REACH). x = the share of the 10 m wind the water feels, 0 sheltered .. 1, y = fetch (m).
const float SHELTER_STEP = 2.0;
const int SHELTER_STEPS = 8;
const int FETCH_STEPS = 10;
const float FETCH_STEP_AT[FETCH_STEPS] = float[](24.0, 32.0, 48.0, 64.0, 96.0, 128.0, 192.0, 256.0, 320.0, 400.0);
const float COARSE_FROM = 12.0;
const float SHELTER_DISTANCE = 10.0;
const float SHELTER_PER_HEIGHT = 6.0;
const float ROOF = 0.05; // m over the surface at which a column's top covers the water
const float SEA_LEVEL = 0.0;      // BathymetryGrid.SeaLevel
const float LAND_SLOW = 0.35;     // the 10 m wind over hilly grassland, adjusted, is 0.65 of the sea's
const float LAND_ADJUST = 100.0;  // m of land over which it adjusts (the rough ground's boundary layer passes 10 m in ~50 m)
const float WATER_RECOVER = 1000.0; // m of water over which it speeds back up (a smooth surface's layer reaches 10 m in ~200 m)
const int SHORE_BISECTIONS = 3;

// Between the last open step at near and the shore at far (m upwind of p), where the shore is.
float bisectShore(vec3 p, vec2 d, float near, float far)
{
    for (int i = 0; i < SHORE_BISECTIONS; i++)
    {
        float mid = 0.5 * (near + far);
        float top = cellTop(p.xz - d * mid);
        if (top != NO_TOP && top - p.y > -0.5)
            far = mid;
        else
            near = mid;
    }

    return 0.5 * (near + far);
}

vec2 windReach(vec3 p, vec2 wind)
{
    float U = length(wind);
    if (U < 1.0)
        return vec2(0.0, FETCH_REACH);

    vec2 d = wind / U;
    float reach = 1.0;
    float own = rainCoverTop(p.xz);
    float open = own == NO_TOP || own - p.y < ROOF ? 0.0 : -1.0;
    float last = 0.0;
    float fetch = -1.0;
    float sea = -1.0;

    for (int i = 0; i < SHELTER_STEPS + FETCH_STEPS; i++)
    {
        float dist = i < SHELTER_STEPS ? SHELTER_STEP * float(i + 1) : FETCH_STEP_AT[i - SHELTER_STEPS];
        vec2 q = p.xz - d * dist;
        float top = i < SHELTER_STEPS ? rainCoverTop(q) : NO_TOP;
        if (top == NO_TOP && dist >= COARSE_FROM)
            top = cellTop(q);

        if (top == NO_TOP)
        {
            open = open < 0.0 ? dist : open;
            sea = fetch >= 0.0 && sea < 0.0 ? dist : sea;
            continue;
        }

        float h = top - p.y;
        if (open < 0.0)
        {
            if (dist >= SHELTER_DISTANCE)
                return vec2(0.0);
            if (h < ROOF)
                open = dist;
            continue;
        }

        if (h > 0.25)
            reach = min(reach, (dist - open) / (SHELTER_PER_HEIGHT * h));

        if (fetch < 0.0 && h > -0.5)
            fetch = (i < SHELTER_STEPS ? dist : bisectShore(p, d, max(last, open), dist)) - open;
        else if (fetch < 0.0)
            last = dist;
        else if (sea < 0.0 && top < SEA_LEVEL)
            sea = dist;
    }

    if (open < 0.0)
        return vec2(0.0);

    if (fetch < 0.0)
        return vec2(reach * (1.0 - min(open / SHELTER_DISTANCE, 1.0)), FETCH_REACH);

    float land = (sea < 0.0 ? FETCH_STEP_AT[FETCH_STEPS - 1] : sea) - open - fetch;
    float slow = 1.0 - LAND_SLOW * (1.0 - exp(-max(land, 0.0) / LAND_ADJUST)) * exp(-fetch / WATER_RECOVER);
    return vec2(reach * slow * (1.0 - min(open / SHELTER_DISTANCE, 1.0)), fetch);
}

#endif
