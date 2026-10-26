#version 450
#extension GL_GOOGLE_include_directive : require
// Breaker lip (docs/breaking-waves.md, rendering step 4): one instance per crest segment, swept along the segment. Its
// cross-section is the tongue of water going over (lipPath): out along the outer surface from the crest top, over the
// face and down to its foot, and back along the inner surface, the tube's ceiling, a body's thickness inside it. How
// far along that path it has got is its reach, driven by the wave's travel since its break began (ocean_lip_base.comp),
// so it grows out of the crest, arcs over and falls; a plunging lip then lands and is gone, a spilling one holds at the
// foot as a cascade.
// Its base is the drawn surface on the crest (oceanAt, same swell, coastal terms, face and FFT), so it grows out of it,
// and its landing is the drawn surface at the foot. Ends use per-vertex normals shared with the neighbouring segment,
// so the ribbons join without seams.

#include "ocean_common.glsl"

const int PROFILE = 12;           // points along the tongue, each surface (OceanRenderer.LipProfile)

layout(location = 0) out vec3 vWorldPos;
layout(location = 1) out vec3 vNormal;
layout(location = 2) out float vAlong;     // 0 at the crest .. 1 at the tongue's tip
layout(location = 3) out float vCurl;
layout(location = 4) out float vThickness; // metres of water, for the subsurface glow
layout(location = 5) out float vBaseFoam;  // the face's whitewater at the root
layout(location = 6) out float vCollapse;  // 0 while throwing .. 1 once it has fallen into the crash
layout(location = 7) out vec2 vLipUV;      // texture position (m): the crest's rest point, out along the tongue
layout(location = 8) out vec2 vHook;       // the tongue's direction in texture space (the crest's shoreward normal)
layout(location = 9) out vec3 vRootNormal; // the surface normal at the crest, blended in at the root
layout(location = 10) out float vReach;    // how far along its path the tongue has got
layout(location = 11) out float vPlunge;   // 0 spilling .. 1 plunging
layout(location = 12) out float vFlowRoot; // the water's forward speed at the crest top (lipStateAt flowRoot), m/s: the pour starts from it

// Corner of quad vertex (end 0/1 along the segment, profile step 0/1).
const ivec2 CORNERS[6] = ivec2[](ivec2(0, 0), ivec2(1, 0), ivec2(0, 1), ivec2(1, 0), ivec2(1, 1), ivec2(0, 1));

void main()
{
    BreakerSegment s = breakers.segments[gl_InstanceIndex];
    ivec2 corner = CORNERS[gl_VertexIndex % 6];
    int end = corner.x;
    int point = gl_VertexIndex / 6 + corner.y; // 0 .. 2 PROFILE - 1: out along the outer surface, back along the inner
    bool inner = point >= PROFILE;
    float u = float(inner ? 2 * PROFILE - 1 - point : point) / float(PROFILE - 1);

    // The drawn surface, the crest's surf state and the landing at each end, evaluated once per lip base slot by
    // ocean_lip_base.comp. A lip throws while the crest curls; once a plunging crest has landed (collapse past 0.5) it
    // turns to foam and fades into the crash; a spilling crest's lip stays until the water runs out.
    LipBase b0 = lipBaseAt(s, 0);
    LipBase b1 = lipBaseAt(s, 1);
    LipBase base = end == 0 ? b0 : b1;
    float height = base.state.z;
    float curl = base.state.x;
    float reach = base.landing.w;
    float plunge = base.normal.w;
    vec2 collapse = vec2(b0.state.y, b1.state.y); // the crash (lipLife)

    // An end holding no lip has no point to sweep from; a segment whose two ends disagree on the shoreward normal (a
    // chain doubling back on itself) would draw a bow-tie.
    if (lipBaseEmpty(b0) || lipBaseEmpty(b1) || max(b0.state.x, b1.state.x) < LIP_MIN_CURL || min(collapse.x, collapse.y) >= 1.0 || max(b0.state.z, b1.state.z) < 0.05
        || max(b0.landing.w, b1.landing.w) < LIP_MIN_REACH || dot(s.normals.xy, s.normals.zw) < 0.3 || !ribbonInRange(s))
    {
        gl_Position = vec4(2.0, 2.0, 2.0, 1.0); // outside the clip volume: the whole instance is culled
        return;
    }

    vec2 n = end == 0 ? s.normals.xy : s.normals.zw;
    float zone = base.position.w;

    // The path in the crest frame (x shoreward, y up): from the crest top to the landing the base compute sampled, its
    // throw ahead and however far down the drawn face is there. The tongue is the first `reach` of it.
    float throwDist = base.surface.x;
    float drop = max(base.position.y - base.landing.y, 0.02 * height);
    float t = u * reach;
    vec4 c = lipPath(t, throwDist, drop, plunge);
    vec2 along = normalize(c.zw + vec2(1e-5, 0.0));
    vec2 inside = vec2(along.y, -along.x); // toward the tube under the tongue

    // A tongue: full thickness out to LIP_TAPER of its length, thinning to a feathered edge that the spray leaves from.
    // Its body is sized from the face it falls down (the drawn surface, carrier and random sea together), not the
    // carrier's capped height alone.
    float size = curl * max(height, LIP_FACE_SIZE * drop) * (1.0 - zone);
    float thickness = size * (LIP_THICKNESS * (1.0 - smoothstep(LIP_TAPER, 1.0, u)) + 0.03);
    vec2 profile = inner ? c.xy + inside * thickness : c.xy;
    vec2 normal = inner ? inside : -inside;

    vec3 forward = vec3(n.x, 0.0, n.y);
    vWorldPos = base.position.xyz + forward * profile.x + vec3(0.0, profile.y, 0.0);
    vNormal = normalize(forward * normal.x + vec3(0.0, normal.y, 0.0));
    vRootNormal = base.normal.xyz;

    // The lip's own texture position: the crest's rest point (where the surface it grows from samples its own detail,
    // so at the root the two match), moved out along the crest's normal by how far along the tongue it is. It travels
    // with the crest, so the lip's foam and ripples go with the water pouring over it rather than staying pinned to the
    // world while the lip moves through them; the lace churns in place.
    vLipUV = base.surface.zw + n * (t * (throwDist + drop));
    vHook = n;

    vAlong = u;
    vCurl = curl;
    vCollapse = end == 0 ? collapse.x : collapse.y;
    vThickness = thickness;
    vBaseFoam = base.surface.y;
    vReach = reach;
    vPlunge = plunge;
    vFlowRoot = base.state.w;
    gl_Position = frame.viewProj * vec4(vWorldPos, 1.0);
}
