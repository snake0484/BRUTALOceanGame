#version 450
#extension GL_GOOGLE_include_directive : require
// Fine patch along breaking crests (docs/breaking-waves.md, the fold): one instance per crest segment within
// PATCH_DISTANCE (listed by the CPU, breakers.patchList), an indexed grid of PATCH_ALONG x PATCH_ACROSS quads over the tongue's rest strip (a margin either
// side), evaluated with the same oceanAt as the clipmap but with gFoldTongue set, so breakerFace draws the strip as the
// tongue rather than collapsing it onto the crest. The clipmap underneath has collapsed the same strip, so the two
// meshes meet at the crest and at the strip's far edge; the patch is pushed PATCH_BIAS toward the camera so it wins
// the margin overlap. Drawn after the clipmap with ocean.frag.

#include "ocean_common.glsl"

const int PATCH_ALONG = 8;       // quads along a 4 m segment (OceanRenderer.PatchAlong): 0.5 m, so the ragged edge's 3 m lobes are curves, not a sawtooth
const int PATCH_ACROSS = 20;     // quads across the strip (OceanRenderer.PatchAcross): 10 per skin, about 0.2 m on a 2 m tongue
const float PATCH_MARGIN = 0.6;  // rest metres either side of the tongue strip
const float PATCH_BIAS = 0.03;   // metres toward the camera
const float PATCH_SPACING = 0.2; // the anti-alias filter's grid spacing (m)

layout(location = 0) out vec3 vWorldPos;
layout(location = 1) out vec2 vRestXZ;
layout(location = 2) out vec4 vCpuPartials;
layout(location = 3) out float vCpuDxz;
layout(location = 4) out vec3 vShoreDamp;
layout(location = 5) out float vBreakerFoam;
layout(location = 6) out float vSurfFoam;
layout(location = 7) out vec2 vFlow;
layout(location = 8) out vec4 vFold; // x = on the tongue, y = on its inner skin, zw = the lace's rest offset along the skin (ocean.frag)
layout(location = 9) out vec3 vTongue; // x = along the tongue's path (0 root .. 1 tip), y = the body's thickness (m), z = how deep in the tube's shadow
layout(location = 10) out float vSwashEdge;
layout(location = 11) out float vTurbulence;

void main()
{
    BreakerSegment s = breakers.segments[breakers.patchList[gl_InstanceIndex]];
    int side = PATCH_ALONG + 1;
    int i = gl_VertexIndex % side;
    int j = gl_VertexIndex / side;
    float t = float(i) / float(PATCH_ALONG);
    float r = float(j) / float(PATCH_ACROSS);

    // Culled only as a whole instance (every vertex makes the same call): out of reach, no fold, no faces, no lip at
    // either end, or a segment whose ends disagree on the shoreward normal (a bow-tie).
    LipBase b0 = lipBaseAt(s, 0);
    LipBase b1 = lipBaseAt(s, 1);
    bool has0 = !lipBaseEmpty(b0);
    bool has1 = !lipBaseEmpty(b1);
    float dist = distance(frame.cameraPos.xz, 0.5 * (s.ends.xy + s.ends.zw));
    if (breakers.info.z == 0 || breakers.info.w == 0 || dist > PATCH_DISTANCE + 8.0 || dot(s.normals.xy, s.normals.zw) < 0.3 || !(has0 || has1))
    {
        gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
        return;
    }

    // The segment advanced to render time and seated on the drawn crest (as the lip bases are), then the strip's rest
    // row along the shoreward normal. A column with no tongue (width 0) collapses its strip onto the crest and draws the
    // plain wall there: culling it alone, while its neighbours drew, stretched slivers from them to the clip volume's
    // corner, right across the scene.
    vec2 e = mix(s.ends.xy, s.ends.zw, t);
    vec2 n = normalize(mix(s.normals.xy, s.normals.zw, t) + vec2(1e-6, 0.0));
    vec2 seat = seatOnCrest(e + n * (s.dir.z * breakers.grid.w));

    // The tongue's throw and drop from the segment's lip bases (ocean_lip_base.comp: the landing measured on the drawn
    // surface and clamped to the wall), between the two ends, and its crest point theirs (an empty slot still carries
    // one). At an end holding no lip the tongue tapers to nothing over the segment (gFoldTaper) with the other end's
    // throw and drop: a segment with one lipless end drew nothing, so the neighbour's full-width tongue ended in a
    // straight cut across the crest.
    bool both = has0 && has1;
    float w1 = both ? t : (has1 ? 1.0 : 0.0);
    gFoldThrow = mix(b0.surface.x, b1.surface.x, w1);
    gFoldDrop = mix(b0.position.y - b0.landing.y, b1.position.y - b1.landing.y, w1);
    gFoldTaper = both ? 1.0 : smoothstep(0.0, 1.0, has0 ? 1.0 - t : t);
    gFoldCrest = mix(b0.position.xyz, b1.position.xyz, t);
    float foldW = max(foldWidthAt(seat), 0.0);

    // Without a tongue the rows collapse onto the crest (no fragments); with one, a margin of plain wall either side.
    float has = smoothstep(0.0, 0.05, foldW);
    float sn = mix(-PATCH_MARGIN * has, 2.0 * foldW + PATCH_MARGIN * has, r);
    vec2 x0 = seat + n * sn;

    gFoldTongue = true;
    OceanPoint p = oceanAt(x0, PATCH_SPACING);

    vec3 toCamera = frame.cameraPos.xyz - p.position;
    vWorldPos = p.position + toCamera / max(length(toCamera), 1e-3) * PATCH_BIAS;
    vRestXZ = x0;
    vCpuPartials = p.cpuPartials;
    vCpuDxz = p.cpuDxz;
    vShoreDamp = p.shore;
    vBreakerFoam = p.breakerFoam;
    vSurfFoam = p.surfFoam;
    vFlow = p.flow;
    vFold = p.fold;
    vTongue = p.tongue;
    vSwashEdge = p.swashEdge;
    vTurbulence = p.turbulence;
    gl_Position = frame.viewProj * vec4(vWorldPos, 1.0);
}
