// The breaking face (docs/pivot/briefs/breaking-waves-brief.md): the surf carrier drawn as a wave over the coast regions
// round the camera, and on its crests the lift, peak, pitch and whitewater of a breaking wave. Mirrors the CPU's
// SurfCarrier and BreakerFace (src/Water/Ocean): keep them in step, constants included. Each region is a baked atlas
// (CoastAtlas) in its sea tile's plane; frame.coast places it against the camera. Included by ocean_common.glsl.

layout(set = 0, binding = 7) uniform sampler2DArray coastPhase;  // r = the carrier's spatial phase (rad), nearest: read with textureGather
layout(set = 0, binding = 8) uniform sampler2DArray coastFields; // (depth, shoaling amplitude, break distance, plunge)
layout(set = 0, binding = 9) uniform sampler2DArray coastDir;    // rg = direction of travel in the region's plane

const float COAST_CELLS = 256.0;          // CoastWindow.Cells
const float COAST_CELL = 4.0;             // CoastWindow.CellSize
const float COAST_BLEND = 32.0;           // CoastWindow.Blend
const float COAST_MAX_SPACING = 32.0;     // grid spacing (m) past which no carrier can be drawn ...
const float COAST_FADE_SPACING = 16.0;    // ... and from which the face fades out toward it, whatever its wavelength
const float SURF_GAMMA = 0.78;            // SurfCarrier.BreakingGamma
const float COHERENT_KH = 0.4;
const float INCOHERENT_KH = 1.2;
const float FORM_DEPTH = 4.0;             // SurfCarrier.FormDepth
const float FADE_DEPTH = 8.0;             // SurfCarrier.FadeDepth
const float SPENT_DEPTH_K = 0.3;
const float MAX_SHARPNESS = 0.55;
const float SHARP_FROM = 0.1;
const float SHARP_FULL = 0.45;
const float CLOCK_START_CAP = 0.92;       // CoastBake.ClockStartCap

const float FACE_BACK_WIDTH = 0.18;       // BreakerFace
const float FACE_FRONT_WIDTH = 0.09;
const float FACE_MIN_FRONT = 0.8;
const float FACE_PEAK = 2.5;
const float FACE_PITCH = 0.88;
const float FACE_PITCH_SQUEEZE_MAX = 0.94;
const float FACE_WALL_WIDTH = 1.0;
const float FACE_PITCH_FRONT = 0.2;
const float FACE_PITCH_RECOVER = 0.17;
const float FACE_PITCH_REACH = 0.4;
const float FACE_PITCH_FADE = 0.48;
const float FACE_PITCH_MAX = 30.0;
const float FACE_PITCH_TOP = 20.0;
const float FACE_PITCH_FOOT = 4.0;
const float FACE_PITCH_END = 8.0;
const float FACE_PITCH_DRY_DEPTH = 0.6;
const float FACE_LIFT_DRY_DEPTH = 0.3;
const float FACE_PEAK_FROM = 0.05;
const float FACE_PEAK_FULL = 0.15;
const float WHITE_FRONT = 1.5;
const float WHITE_FRONT_MIN = 0.8;
const float WHITE_FRONT_MAX = 4.0;
const float WHITE_BACK = 1.6;
const float WHITE_DRY_DEPTH = 0.15;
const float ROLLER_TRAIL = 0.25;
const float ROLLER_TAIL = 2.0;
const float ROLLER_TAIL_MIN = 1.5;
const float BREAK_CRASH = 0.35;
const float ONSET_TRAVEL = 0.15;
const float HOLD_GAMMA = 1.5;
const float HOLD_RAMP = 0.03;
const float LANDED_TRAVEL = 0.245;
const float COLLAPSE_TRAVEL = 1.0;
const float SPENT_CAP = 0.45;
const float STAND_FROM = 0.45;
const float STAND_AMOUNT = 0.55;
const float BREAK_STEEP = 0.85;
const float THROW_FOAM_HEIGHT = 0.6;
const float SPILL_TIP = 0.5;
const float CREST_AMPLITUDE = 0.2;
const float BORE_LIFT = 0.35;
const float BORE_GAMMA = 0.55;
const float RUNUP_DEPTH = 0.6;
const float BORE_FADE_DEPTH = 0.4;
const float FACE_SPENT_DEPTH = 0.3;
const float FACE_MIN_HEIGHT = 1e-3;
const float FACE_MIN_CELLS = 1.5;         // the crest's lift and whitewater are at least this many grid cells wide (GPU only)

// What the face adds at a rest position: height and its gradient, the horizontal shift and its partials
// (xx, zz, xz), and the whitewater weight. In a region's plane from breakerFace, in the sea frame's from coastFace.
struct Face
{
    float height;
    vec2 slope;
    vec2 shift;
    vec3 jacobian;
    float foam;
};

struct SurfPoint
{
    float phase;   // wrapped to [-pi, pi): 0 on a crest
    vec2 dir;
    float k;
    float unbroken;
    float depth;
    float breakDistance;
    float plunge;
};

struct Crest
{
    float curl;
    float broken;
    float height;
    float amplitude;
    float steep;
    float spill;
};

float wrapPhase(float phase)
{
    return phase - TWO_PI * floor(phase / TWO_PI + 0.5);
}

float surfWaveNumber(float omega, float depth)
{
    float deep = omega * omega / 9.81;
    float kh = deep * max(depth, 1e-3);
    return kh > 10.0 ? deep : deep / pow(tanh(pow(kh, 0.75)), 2.0 / 3.0);
}

float surfUnbroken(float deepHeight, float omega, float depth, float shoaling)
{
    float h = max(depth, 0.0);
    float coherence = 1.0 - smooth01((surfWaveNumber(omega, h) * h - COHERENT_KH) / (INCOHERENT_KH - COHERENT_KH));
    float shallows = 1.0 - smooth01((h - FORM_DEPTH * deepHeight) / ((FADE_DEPTH - FORM_DEPTH) * max(deepHeight, 1e-4)));
    return deepHeight * sqrt(coherence * shallows) * shoaling;
}

float surfSharpness(float height, float depth)
{
    return MAX_SHARPNESS * smooth01((height / max(depth, 0.1) - SHARP_FROM) / (SHARP_FULL - SHARP_FROM));
}

// The carrier at cell coordinates c of region r (SurfCarrier.At): the phase bilinear by hand (the filter's 8-bit weights
// would step it), the rest through the samplers; all clamped to the window.
SurfPoint surfCarrierAt(int r, vec2 c)
{
    vec4 wave = frame.coast[r].wave;
    float layer = frame.coast[r].cellZ.w;
    vec2 f = clamp(c - 0.5, vec2(0.0), vec2(COAST_CELLS - 1.001));
    vec2 i = floor(f);
    vec2 t = f - i;
    vec4 g = textureGather(coastPhase, vec3((i + 1.0) / COAST_CELLS, layer), 0);
    vec3 uv = vec3((f + 0.5) / COAST_CELLS, layer);
    vec4 fields = textureLod(coastFields, uv, 0.0);
    vec2 dir = textureLod(coastDir, uv, 0.0).rg;
    float depth = max(fields.r, 0.0);

    SurfPoint p;
    p.phase = wrapPhase(mix(mix(g.w, g.z, t.x), mix(g.x, g.y, t.x), t.y) + wave.x);
    p.dir = dir / max(length(dir), 1e-6);
    p.k = surfWaveNumber(wave.y, max(depth, SPENT_DEPTH_K));
    p.unbroken = surfUnbroken(wave.z, wave.y, depth, fields.g);
    p.depth = fields.r;
    p.breakDistance = fields.b;
    p.plunge = fields.a;
    return p;
}

float breakOnset(float distance, float wavelength)
{
    return smoothstep(0.0, ONSET_TRAVEL * wavelength, distance);
}

float breakTravel(float distance, float wavelength)
{
    return max(distance - ONSET_TRAVEL * wavelength, 0.0);
}

float breakLanded(float distance, float wavelength)
{
    return smoothstep(0.5, 1.0, breakTravel(distance, wavelength) / (BREAK_CRASH * wavelength));
}

float breakCollapse(float distance, float wavelength)
{
    return smoothstep(LANDED_TRAVEL, LANDED_TRAVEL + COLLAPSE_TRAVEL, breakTravel(distance, wavelength) / wavelength);
}

float breakHold(float distance, float wavelength)
{
    return smoothstep(0.0, HOLD_RAMP * wavelength, distance) * (1.0 - breakCollapse(distance, wavelength));
}

float heldCap(float cap, float depth, float unbroken, float hold)
{
    return max(cap, min(CLOCK_START_CAP, HOLD_GAMMA * max(depth, 0.0) / max(unbroken, 1e-4)) * hold);
}

float faceWater(float depth)
{
    return smooth01((depth - FACE_SPENT_DEPTH) / FACE_SPENT_DEPTH);
}

// BreakerFace.CrestState.
Crest crestState(SurfPoint c, float wavelength)
{
    float depth = max(c.depth, 0.0);
    float cap = c.unbroken > 1e-4 ? clamp(SURF_GAMMA * depth / c.unbroken, 0.0, 1.0) : 1.0;
    float onset = breakOnset(c.breakDistance, wavelength);
    float landed = breakLanded(c.breakDistance, wavelength);
    float collapse = breakCollapse(c.breakDistance, wavelength);
    float progress = breakTravel(c.breakDistance, wavelength) / (BREAK_CRASH * wavelength);
    float height = c.unbroken * heldCap(cap, depth, c.unbroken, breakHold(c.breakDistance, wavelength));
    float spent = smooth01((SPENT_CAP - cap) / 0.2);
    float bore = clamp((cap - 0.3) / 0.3, 0.4, 1.0);
    float curl = onset * mix(SPILL_TIP * (1.0 - spent), 1.0 - 0.75 * max(spent, smoothstep(0.6, 1.0, progress)), c.plunge);
    float broken = max(spent, landed);
    float afloat = smooth01(c.depth / FACE_SPENT_DEPTH + 1.0);

    Crest s;
    s.curl = curl * afloat;
    s.broken = broken * afloat;
    s.height = height * faceWater(c.depth);
    s.amplitude = max(CREST_AMPLITUDE * height * onset * (1.0 - collapse) * bore * faceWater(c.depth),
                      BORE_LIFT * BORE_GAMMA * max(depth, RUNUP_DEPTH) * broken * smooth01(depth / BORE_FADE_DEPTH));
    s.steep = max(max(curl, BREAK_STEEP * onset) * (1.0 - 0.5 * max(spent, collapse)),
                  STAND_AMOUNT * smoothstep(STAND_FROM, SURF_GAMMA, c.unbroken / max(depth, 0.1))) * afloat;
    s.spill = (1.0 - c.plunge) * onset * afloat;
    return s;
}

float softPlus(float x)
{
    return max(x, 0.0) + log(1.0 + exp(-abs(x)));
}

float softRamp(float x, float k0, float k1, out float slope)
{
    slope = 1.0 / (1.0 + exp(-k0 * x)) - 1.0 / (1.0 + exp(-k1 * (x - 1.0)));
    return softPlus(k0 * x) / k0 - softPlus(k1 * (x - 1.0)) / k1;
}

// BreakerFace.Apply at cell coordinates c of region r, for a grid of effective spacing se: the carrier fades out where
// the grid cannot resolve its wavelength (as the swell's components do), and what rides its crest (lift, whitewater) is
// no narrower than the grid can show, so a far break line is a line and not beads on the vertices. Lengths are metres;
// the result is in the region's plane.
Face breakerFace(int r, vec2 c, float se)
{
    float lo = 2.0 * se;
    float hi = 4.0 * se;
    float narrowest = FACE_MIN_CELLS * se;
    Face o = Face(0.0, vec2(0.0), vec2(0.0), vec3(0.0), 0.0);
    SurfPoint here = surfCarrierAt(r, c);
    float resolved = smoothstep(lo, hi, TWO_PI / here.k) * (1.0 - smoothstep(COAST_FADE_SPACING, COAST_MAX_SPACING, se));
    if (here.unbroken < FACE_MIN_HEIGHT || resolved <= 0.0)
        return o;

    float depth = max(here.depth, 0.0);
    float cap = clamp(SURF_GAMMA * depth / here.unbroken, 0.0, 1.0);
    float height = here.unbroken * heldCap(cap, depth, here.unbroken, breakHold(here.breakDistance, TWO_PI / here.k));
    float sharp = surfSharpness(height, depth);
    float norm = 0.5 * height / sqrt(1.0 + sharp * sharp * (1.0 + sharp * sharp));
    float sn1 = sin(here.phase);
    float cs1 = cos(here.phase);
    float sn2 = 2.0 * sn1 * cs1;
    float cs2 = 2.0 * cs1 * cs1 - 1.0;
    float sn3 = sn1 * (3.0 - 4.0 * sn1 * sn1);
    float cs3 = cs1 * (4.0 * cs1 * cs1 - 3.0);
    float eta = norm * (cs1 + sharp * (cs2 + sharp * cs3));
    vec2 slope = -norm * here.k * (sn1 + sharp * (2.0 * sn2 + sharp * 3.0 * sn3)) * here.dir;

    // Two Newton steps to the crest.
    vec2 x = c * COAST_CELL;
    vec2 guess = x - here.dir * (here.phase / here.k);
    SurfPoint crest = surfCarrierAt(r, guess / COAST_CELL);
    guess -= crest.dir * (crest.phase / crest.k);
    crest = surfCarrierAt(r, guess / COAST_CELL);

    float sn = dot(x - guess, crest.dir) + crest.phase / crest.k;
    float wavelength = TWO_PI / crest.k;
    float fadeEnd = min(FACE_PITCH_FADE * wavelength, FACE_PITCH_MAX);
    float reach = abs(here.phase) / crest.k;
    Crest state = crestState(crest, wavelength);

    if (reach < fadeEnd && (state.steep > 0.0 || state.amplitude >= 1e-3))
    {
        float fadeStart = FACE_PITCH_REACH / FACE_PITCH_FADE * fadeEnd;
        float tw = clamp((reach - fadeStart) / (fadeEnd - fadeStart), 0.0, 1.0);
        float window = 1.0 - tw * tw * (3.0 - 2.0 * tw);
        float dwindow = -sign(here.phase) * 6.0 * tw * (1.0 - tw) / (fadeEnd - fadeStart) * here.k / crest.k;

        float amount = state.steep;
        float width = max(sn < 0.0 ? FACE_BACK_WIDTH * wavelength : max(mix(FACE_BACK_WIDTH, FACE_FRONT_WIDTH, amount) * wavelength, FACE_MIN_FRONT), narrowest);
        float e = exp(-(sn * sn) / (width * width));
        float wet = smoothstep(0.0, FACE_LIFT_DRY_DEPTH, here.depth);
        float lift = state.amplitude * e * window * wet;
        float dlift = state.amplitude * e * (dwindow - 2.0 * sn / (width * width) * window) * wet;

        float a = 0.5 * height;
        float peaking = amount * smoothstep(FACE_PEAK_FROM, FACE_PEAK_FULL, a);
        if (peaking > 0.0)
        {
            float w = peaking * window;
            float dw = peaking * dwindow;
            float gamma = mix(1.0, FACE_PEAK, w);
            float troughAmp = norm * (1.0 - sharp + sharp * sharp);
            float span = norm * (1.0 + sharp + sharp * sharp) + troughAmp;
            float share = (eta + troughAmp) / span;
            float peaked = share <= 0.0 ? share : share < 1.0 ? pow(share, gamma) : 1.0 + gamma * (share - 1.0);
            float dShare = share <= 0.0 ? 1.0 : share < 1.0 ? gamma * pow(share, gamma - 1.0) : gamma;
            float dGamma = share <= 0.0 ? 0.0 : share < 1.0 ? peaked * log(share) : share - 1.0;
            float across = span * dGamma * (FACE_PEAK - 1.0) * dw;
            eta += span * (peaked - share);
            slope = slope * dShare + across * crest.dir;
        }

        float front = FACE_PITCH_FRONT * wavelength;
        float recover = FACE_PITCH_RECOVER * wavelength;
        float dWall;
        float dRecover;
        float wall = softRamp(sn / front, FACE_PITCH_TOP, FACE_PITCH_FOOT, dWall);
        float back = softRamp((sn - front) / recover, FACE_PITCH_FOOT, FACE_PITCH_END, dRecover);
        float pitch = clamp(1.0 - FACE_WALL_WIDTH * state.height / front, FACE_PITCH, FACE_PITCH_SQUEEZE_MAX);
        float lean = pitch * front * amount * smoothstep(0.0, FACE_PITCH_DRY_DEPTH, here.depth);
        float tent = -lean * (wall - back);
        float shift = tent * window;
        float dshift = -lean * (dWall / front - dRecover / recover) * window + tent * dwindow;

        eta += lift;
        slope += dlift * crest.dir;
        o.shift = shift * crest.dir;
        o.jacobian = dshift * vec3(crest.dir.x * crest.dir.x, crest.dir.y * crest.dir.y, crest.dir.x * crest.dir.y);

        float whiteFront = clamp(WHITE_FRONT * state.height, WHITE_FRONT_MIN, WHITE_FRONT_MAX);
        float whiteWidth = max(sn < 0.0 ? WHITE_BACK * whiteFront : whiteFront, narrowest);
        float ew = exp(-(sn * sn) / (whiteWidth * whiteWidth));
        float throwing = smoothstep(0.6, 1.0, state.curl) * (sn > 0.0 ? 0.6 * ew : 0.15 * ew * ew * ew) * smoothstep(0.0, THROW_FOAM_HEIGHT, state.height);
        float spilling = state.spill * (sn > 0.0 ? ew : ew * ew);
        float roller = state.broken * (sn > 0.0 ? ew : max(ew, ROLLER_TRAIL * exp(sn / max(ROLLER_TAIL * state.height, ROLLER_TAIL_MIN))));
        o.foam = max(max(throwing, spilling), roller) * window * smoothstep(0.0, WHITE_DRY_DEPTH, here.depth);
    }

    o.height = eta * resolved;
    o.slope = slope * resolved;
    o.shift *= resolved;
    o.jacobian *= resolved;
    o.foam *= resolved;
    return o;
}

float coastRamp(float c)
{
    return smooth01(min(c, COAST_CELLS - c) / COAST_BLEND);
}

// The faces of every region over x0, in the sea frame: each by its window's weight (neighbours cross-fade over the cells
// they share), its fade (load, unload, rebake) and its tile's share of the sea there. The spacing is at least
// frame.depthWrap.z, which does not step where the finest level drawn does.
Face coastFace(vec2 x0, float se)
{
    Face o = Face(0.0, vec2(0.0), vec2(0.0), vec3(0.0), 0.0);
    se = max(se, frame.depthWrap.z);
    if (frame.coastCount == 0 || se >= COAST_MAX_SPACING)
        return o;

    bool weighed = false;
    float tiles[MAX_SLOTS];

    for (int r = 0; r < frame.coastCount; ++r)
    {
        vec4 cx = frame.coast[r].cellX;
        vec4 cz = frame.coast[r].cellZ;
        vec2 c = vec2(dot(cx.xy, x0) + cx.z, dot(cz.xy, x0) + cz.z);
        if (c.x <= 0.0 || c.y <= 0.0 || c.x >= COAST_CELLS || c.y >= COAST_CELLS)
            continue;

        if (!weighed)
        {
            slotWeights(x0, tiles);
            weighed = true;
        }

        int s = int(cx.w);
        float w = coastRamp(c.x) * coastRamp(c.y) * frame.coast[r].wave.w * tiles[s] * tiles[s];
        if (w <= 0.0)
            continue;

        Face f = breakerFace(r, c, se);
        vec4 T = frame.slotToTile[s];
        vec4 F = frame.slotFromTile[s];
        vec3 j = f.jacobian;
        vec2 jx = vec2(F.x * j.x + F.y * j.z, F.x * j.z + F.y * j.y);
        vec2 jz = vec2(F.z * j.x + F.w * j.z, F.z * j.z + F.w * j.y);
        o.height += w * f.height;
        o.slope += w * vec2(T.x * f.slope.x + T.z * f.slope.y, T.y * f.slope.x + T.w * f.slope.y);
        o.shift += w * vec2(dot(F.xy, f.shift), dot(F.zw, f.shift));
        o.jacobian += w * vec3(jx.x * T.x + jx.y * T.z, jz.x * T.y + jz.y * T.w, 0.5 * (jx.x * T.y + jx.y * T.w + jz.x * T.x + jz.y * T.z));
        o.foam += w * f.foam;
    }

    return o;
}
