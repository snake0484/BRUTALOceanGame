#version 450
#extension GL_GOOGLE_include_directive : require
// Ocean surface vertex shader: CDLOD clipmap grid; the surface itself comes from ocean_common.glsl (matches CPU OceanSwell).

#include "ocean_common.glsl"

layout(push_constant) uniform Level {
    vec4 level; // (centreX, centreZ relative to the camera, spacing, N)
    vec4 morph; // (morphStart, morphEnd, levelIndex, 0)
} pc;

layout(location = 0) out vec3 vWorldPos;     // final displaced position (CPU swell + GPU cascades)
layout(location = 1) out vec2 vRestXZ;       // x0 after morph (cascade sample coordinate)
layout(location = 2) out vec4 vCpuPartials;  // CPU band (hx, hz, λDxx, λDzz)
layout(location = 3) out float vCpuDxz;      // CPU band λDxz
layout(location = 4) out float vShoreDamp;   // share of the FFT slopes drawn: 0 on the beach .. 1 on open water
layout(location = 5) out float vSurfFoam;
layout(location = 6) out float vSwashEdge;   // 1 at the edge of a swash sheet, 0 half a metre behind it (ocean.frag's edge line)

void main()
{
    // Grid vertex from index.
    int n = int(pc.level.w);
    int side = 2 * n + 1;
    ivec2 g = ivec2(gl_VertexIndex % side, gl_VertexIndex / side) - n;
    float spacing = pc.level.z;
    vec2 x0 = pc.level.xy + vec2(g) * spacing;

    // CDLOD morph: collapse odd vertices onto even ones toward the outer edge.
    vec2 toCam = abs(x0);
    float d = max(toCam.x, toCam.y) / (float(n) * spacing);
    float m = clamp((d - pc.morph.x) / (pc.morph.y - pc.morph.x), 0.0, 1.0);
    vec2 odd = mod(vec2(g), 2.0);
    x0 -= odd * spacing * m;

    // Anti-alias filter: fade waves shorter than ~2-4 cells of effective spacing.
    OceanPoint p = oceanAt(x0, spacing * (1.0 + m));

    vWorldPos = p.position;
    vRestXZ = x0;
    vCpuPartials = p.cpuPartials;
    vCpuDxz = p.cpuDxz;
    vShoreDamp = p.shore;
    vSurfFoam = p.surfFoam;
    vSwashEdge = p.swashEdge;

    gl_Position = frame.viewProj * vec4(vWorldPos, 1.0);
}